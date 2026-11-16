using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using Umbra.Core;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Umbra.App;

public sealed class PlayItem
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Subtitle { get; init; }
    public bool IsLive { get; init; }
    public bool IsEpisode { get; init; }
    /// <summary>Key used to store the resume point and watched state (movies and episodes).</summary>
    public string? ResumeKey { get; init; }
    public long StartMs { get; init; }
    public Channel? Channel { get; init; }
}

/// <summary>
/// Video player based on libVLC (the same engine as VLC media player). It is not a window: the main window mounts it
/// over the current screen and removes it when playback is closed, so everything happens in the same window.
/// </summary>
public partial class PlayerView : UserControl
{
    static LibVLC? _libVlc;
    static PlayerView? _instance;

    static readonly (string? Ratio, string Label)[] Aspects =
    [
        (null, "Automático"), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("fill", "Ajustar a la pantalla"),
    ];

    static readonly Geometry PlayShape = Geometry.Parse("M0,0 L15,9 L0,18 Z");
    static readonly Geometry PauseShape = Geometry.Parse("M0,0 H5 V18 H0 Z M10,0 H15 V18 H10 Z");

    readonly AppState S = AppState.I;
    readonly MainWindow _main = MainWindow.Instance;
    readonly MediaPlayer _player;
    readonly DispatcherTimer _hideTimer;
    readonly DispatcherTimer _toastTimer;
    readonly DispatcherTimer _retryTimer;
    readonly DispatcherTimer _liveTimer;
    IReadOnlyList<PlayItem> _items = Array.Empty<PlayItem>();
    int _index;
    Media? _media;
    Window? _overlayWindow;
    bool _videoReady, _pendingPlay, _seeking, _ended, _closing, _disposed, _menuOpen, _channelsOpen, _controlsShown = true;
    int _retries, _aspect;
    long _length;
    Point _lastMouse;
    CancellationTokenSource? _nowCts;
    EpgProgram? _now;

    static LibVLC Lib => _libVlc ??= new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview");

    public static bool IsOpen => _instance is not null;
    internal static PlayerView? Active => _instance;
    internal bool IsClosing => _closing;
    internal bool ReachedPlaying { get; private set; }
    internal bool HadError { get; private set; }

    /// <summary>Plays <paramref name="items"/> starting at <paramref name="index"/>; reuses the player if it is open.</summary>
    public static void Open(IReadOnlyList<PlayItem> items, int index)
    {
        if (items.Count == 0) return;
        if (_instance is null || _instance._closing)
        {
            _instance = new PlayerView();
            MainWindow.Instance.MountPlayer(_instance);
        }
        _instance.Load(items, index);
    }

    /// <summary>Plays a live channel with <paramref name="list"/> as the zapping list.</summary>
    public static void OpenChannels(IReadOnlyList<Channel> list, Channel channel)
    {
        var source = AppState.I.Source;
        if (source is null) return;
        var index = -1;
        for (var i = 0; i < list.Count && index < 0; i++)
            if (ReferenceEquals(list[i], channel)) index = i;
        if (index < 0)
        {
            list = [channel];
            index = 0;
        }
        var items = list.Select(c => new PlayItem { Title = c.Name, Url = source.GetLiveUrl(c), IsLive = true, Channel = c }).ToList();
        Open(items, index);
    }

    /// <summary>Stops playback synchronously (used when the app closes or the account changes).</summary>
    public static void Shutdown()
    {
        var view = _instance;
        if (view is null) return;
        _instance = null;
        view.SaveProgress();
        view._closing = true;
        MainWindow.Instance.UnmountPlayer(view, animate: false);
        var player = view._player;
        Task.Run(() => player.Stop()).Wait(TimeSpan.FromSeconds(3));
        view.Release();
    }

    PlayerView()
    {
        InitializeComponent();
        _player = new MediaPlayer(Lib) { EnableHardwareDecoding = S.Settings.HardwareDecoding };
        // libVLC ignores the volume until audio starts, so it is applied again in OnPlaying.
        Volume.Value = Math.Clamp(S.Settings.Volume, 0, 150);
        SetPlayGlyph(playing: true);
        UpdateFullscreenButton();
        _main.FullscreenChanged += UpdateFullscreenButton;

        _player.Playing += (_, _) => Ui(OnPlaying);
        _player.Paused += (_, _) => Ui(OnPaused);
        _player.Buffering += (_, e) => Ui(() => OnBuffering(e.Cache));
        _player.EndReached += (_, _) => Ui(OnEnded);
        _player.EncounteredError += (_, _) => Ui(OnError);
        _player.LengthChanged += (_, e) => Ui(() => { _length = e.Length; LengthText.Text = FormatTime(e.Length); });
        _player.TimeChanged += (_, e) => Ui(() => OnTime(e.Time));

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _hideTimer.Tick += (_, _) => HideControls();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Motion.Fade(ToastBorder, 0, 240);
        };
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _retryTimer.Tick += (_, _) => { _retryTimer.Stop(); PlayCurrent(); };
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _liveTimer.Tick += (_, _) => UpdateLiveRow();

        // The overlay lives in its own transparent window owned by the main one; find it before it is first shown.
        Video.ApplyTemplate();
        _overlayWindow = FindOverlayWindow();
        if (_overlayWindow is not null)
        {
            if (App.Quiet) _overlayWindow.ShowActivated = false;
            _overlayWindow.PreviewKeyDown += (_, e) => HandleKey(e);
            _overlayWindow.Closing += OverlayWindow_Closing;
        }

        Video.Loaded += (_, _) =>
        {
            if (_closing) return;
            Video.MediaPlayer = _player;
            _videoReady = true;
            if (_overlayWindow is null && Window.GetWindow(Overlay) is { } w && w != Application.Current.MainWindow)
            {
                _overlayWindow = w;
                w.PreviewKeyDown += (_, e) => HandleKey(e);
                w.Closing += OverlayWindow_Closing;
            }
            if (_pendingPlay)
            {
                _pendingPlay = false;
                PlayCurrent();
            }
        };
    }

    Window? FindOverlayWindow()
    {
        DependencyObject? node = Overlay;
        while (node is not null)
        {
            if (node is Window window) return window;
            node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>Alt+F4 on the overlay must close the app, not just the layer with the controls.</summary>
    void OverlayWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing || _disposed) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(() => Application.Current.MainWindow?.Close());
    }

    void Ui(Action action) => Dispatcher.BeginInvoke(() =>
    {
        if (!_disposed && !_closing) action();
    });

    void Load(IReadOnlyList<PlayItem> items, int index)
    {
        SaveProgress();
        _items = items;
        _index = Math.Clamp(index, 0, items.Count - 1);
        _retries = 0;
        var channels = items.Select(i => i.Channel).OfType<Channel>().ToList();
        ChannelList.ItemsSource = channels.Count == items.Count ? channels : null;
        ChannelsButton.Visibility = ChannelList.ItemsSource is null ? Visibility.Collapsed : Visibility.Visible;
        if (ChannelList.ItemsSource is null) SetChannelPanel(false);
        if (_videoReady) PlayCurrent();
        else _pendingPlay = true;
    }

    PlayItem Current => _items[_index];

    void PlayCurrent()
    {
        if (_items.Count == 0 || _closing) return;
        _retryTimer.Stop();
        var item = Current;
        _ended = false;
        _length = 0;
        TitleText.Text = item.Title;
        SubtitleText.Text = item.Subtitle ?? "";
        SubtitleText.Visibility = string.IsNullOrEmpty(SubtitleText.Text) ? Visibility.Collapsed : Visibility.Visible;
        SeekRow.Visibility = item.IsLive ? Visibility.Collapsed : Visibility.Visible;
        LiveRow.Visibility = item.IsLive ? Visibility.Visible : Visibility.Collapsed;
        BackButton10.Visibility = Forward30.Visibility = item.IsLive ? Visibility.Collapsed : Visibility.Visible;
        PrevButton.Visibility = NextButton.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Seek.Value = 0;
        TimeText.Text = LengthText.Text = "0:00";
        _now = null;
        UpdateLiveRow();
        ShowCenterBadge(false);
        SetPlayGlyph(playing: true);
        ShowStatus("Cargando…", busy: true);

        // A downloaded file plays from disk; a stream needs a connection, which the downloads may be using.
        var local = !item.Url.Contains("://", StringComparison.Ordinal);
        var downloads = DownloadManager.I;
        if (!local && downloads.RunningCount > 0 && S.DownloadsLeaveNoConnection)
        {
            downloads.Suspended = true;
            Toast("Descargas en pausa mientras ves el vídeo");
        }
        else if (local)
        {
            downloads.Suspended = false;
        }
        var media = new Media(Lib, item.Url, local ? FromType.FromPath : FromType.FromLocation);
        media.AddOption($":network-caching={S.Settings.NetworkCachingMs}");
        media.AddOption($":http-user-agent={S.Settings.UserAgent}");
        media.AddOption(":http-reconnect");
        if (item.StartMs > 5000 && _retries == 0)
        {
            media.AddOption($":start-time={(item.StartMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)}");
            Toast(L.T("Continuando desde {0}", FormatTime(item.StartMs)));
        }

        var old = _media;
        _media = media;
        _player.Play(media);
        old?.Dispose();
        Log.Info($"Reproduciendo: {item.Title}");

        if (item.Channel is { } channel)
        {
            S.AddRecentChannel(channel);
            ChannelList.SelectedItem = channel;
            ChannelList.ScrollIntoView(channel);
            _ = LoadNowPlayingAsync(channel);
        }
        if (item.IsLive) _liveTimer.Start();
        else _liveTimer.Stop();
        ShowControls();
    }

    async Task LoadNowPlayingAsync(Channel channel)
    {
        _nowCts?.Cancel();
        var cts = _nowCts = new CancellationTokenSource();
        if (S.Source is null) return;
        try
        {
            var list = await S.Source.GetEpgAsync(channel, cts.Token);
            if (cts.IsCancellationRequested || _closing || Current.Channel != channel) return;
            var now = DateTime.Now;
            var current = list.FirstOrDefault(p => p.Start <= now && now < p.End);
            var next = list.FirstOrDefault(p => p.Start >= now);
            var parts = new List<string>();
            if (current is not null) parts.Add(current.Title);
            if (next is not null) parts.Add(L.T("Después, a las {0:HH:mm}: {1}", next.Start, next.Title));
            SubtitleText.Text = string.Join("   ·   ", parts);
            SubtitleText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _now = current;
            UpdateLiveRow();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cargar el programa actual", ex);
        }
    }

    /// <summary>Live TV shows how far the programme on air is instead of a seek bar.</summary>
    void UpdateLiveRow()
    {
        if (_items.Count == 0 || !Current.IsLive) return;
        var now = DateTime.Now;
        if (_now is { } p && now >= p.End && Current.Channel is { } channel)
        {
            _now = null;
            _ = LoadNowPlayingAsync(channel);
        }
        var has = _now is not null;
        NowStart.Visibility = NowEnd.Visibility = NowProgress.Visibility = has ? Visibility.Visible : Visibility.Hidden;
        if (_now is null) return;
        NowStart.Text = _now.Start.ToString("HH:mm");
        NowEnd.Text = _now.End.ToString("HH:mm");
        var total = (_now.End - _now.Start).TotalSeconds;
        NowProgress.Value = total > 0 ? Math.Clamp((now - _now.Start).TotalSeconds / total, 0, 1) : 0;
    }

    // ----- Player events (marshalled to the UI thread) -----

    void OnPlaying()
    {
        ReachedPlaying = true;
        _player.Volume = (int)Volume.Value;
        HideStatus();
        _retries = 0;
        SetPlayGlyph(playing: true);
        ShowCenterBadge(false);
        ShowControls();
    }

    void OnPaused()
    {
        SetPlayGlyph(playing: false);
        ShowCenterBadge(true);
        ShowControls();
    }

    void OnBuffering(float cache)
    {
        if (cache < 100) ShowStatus(L.T("Cargando… {0:0}%", cache), busy: true);
        else if (_player.IsPlaying) HideStatus();
    }

    void OnTime(long time)
    {
        if (StatusPanel.Visibility == Visibility.Visible && _player.IsPlaying && RetryButton.Visibility != Visibility.Visible) HideStatus();
        if (Current.IsLive || _seeking) return;
        TimeText.Text = FormatTime(time);
        if (_length > 0) Seek.Value = Math.Clamp((double)time / _length, 0, 1);
    }

    void OnEnded()
    {
        if (Current.IsLive)
        {
            // Live streams should not end: reconnect.
            ScheduleRetry("La emisión se ha interrumpido. Reconectando…");
            return;
        }
        _ended = true;
        SaveProgress();
        if (Current.IsEpisode && S.Settings.AutoPlayNextEpisode && _index + 1 < _items.Count)
        {
            _index++;
            PlayCurrent();
            Toast("Siguiente episodio");
            return;
        }
        SetPlayGlyph(playing: false);
        ShowStatus("Fin de la reproducción.", busy: false);
        ShowControls();
    }

    void OnError()
    {
        HadError = true;
        if (Current.IsLive && _retries < 3)
        {
            ScheduleRetry("No se pudo abrir el canal. Reintentando…");
            return;
        }
        ShowStatus(Current.IsLive
            ? "No se pudo reproducir el canal. Comprueba tu conexión o prueba a cambiar el formato (TS/HLS) en Ajustes."
            : "No se pudo reproducir el contenido. Comprueba tu conexión o vuelve a intentarlo.", busy: false);
        RetryButton.Visibility = Visibility.Visible;
        ShowControls();
    }

    void ScheduleRetry(string message)
    {
        _retries++;
        ShowStatus(message, busy: true);
        _retryTimer.Stop();
        _retryTimer.Start();
    }

    void ShowStatus(string text, bool busy)
    {
        StatusText.Text = L.T(text);
        StatusSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Margin = new Thickness(0, busy ? 14 : 0, 0, 0);
        if (StatusPanel.Visibility != Visibility.Visible)
        {
            StatusPanel.Visibility = Visibility.Visible;
            Motion.Fade(StatusPanel, 0, 0);
            Motion.Fade(StatusPanel, 1, 220);
        }
    }

    void HideStatus()
    {
        StatusPanel.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
    }

    void Toast(string text)
    {
        ToastText.Text = L.T(text);
        Motion.Fade(ToastBorder, 1, 130);
        if (Motion.Enabled)
        {
            var pop = Motion.Tween(0.9, 1, 260, 0, Motion.Spring);
            ToastScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            ToastScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        else
        {
            ToastScale.ScaleX = ToastScale.ScaleY = 1;
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void SetPlayGlyph(bool playing)
    {
        PlayGlyph.Data = playing ? PauseShape : PlayShape;
        PlayGlyph.Margin = new Thickness(playing ? 0 : 3, 0, 0, 0);
    }

    void ShowCenterBadge(bool paused)
    {
        if (paused && _items.Count > 0 && !Current.IsLive)
        {
            CenterGlyph.Data = PauseShape;
            Motion.Fade(CenterBadge, 1, 160);
            if (Motion.Enabled)
            {
                var pop = Motion.Tween(0.8, 1, 300, 0, Motion.Spring);
                CenterScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                CenterScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
        }
        else
        {
            Motion.Fade(CenterBadge, 0, 160);
        }
    }

    internal static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ----- Progress -----

    void SaveProgress()
    {
        if (_items.Count == 0) return;
        var item = Current;
        if (item.IsLive || item.ResumeKey is null) return;
        var time = _player.Time;
        var length = _player.Length;
        if (_ended || (length > 0 && time > length * 0.94))
        {
            S.MarkWatched(item.ResumeKey);
        }
        else if (time > 30_000)
        {
            S.SaveResume(item.ResumeKey, time);
        }
    }

    // ----- Controls -----

    void TogglePause()
    {
        if (_ended) { PlayCurrent(); return; }
        if (Current.IsLive && _player.IsPlaying) { _player.SetPause(true); return; }
        _player.Pause();
    }

    void SeekBy(long deltaMs)
    {
        if (Current.IsLive || !_player.IsSeekable) return;
        var target = Math.Clamp(_player.Time + deltaMs, 0, Math.Max(0, _player.Length - 1000));
        _player.Time = target;
        Toast((deltaMs > 0 ? "+" : "−") + $"{Math.Abs(deltaMs) / 1000} s");
    }

    void Step(int delta)
    {
        if (_items.Count < 2) return;
        SaveProgress();
        _index = (_index + delta + _items.Count) % _items.Count;
        _retries = 0;
        PlayCurrent();
    }

    void UpdateFullscreenButton()
    {
        var full = _main.IsFullscreen;
        FullscreenButton.Content = full ? "" : "";
        FullscreenButton.ToolTip = L.T(full ? "Salir de pantalla completa (F)" : "Pantalla completa (F)");
    }

    void CycleAspect()
    {
        _aspect = (_aspect + 1) % Aspects.Length;
        var (ratio, label) = Aspects[_aspect];
        _player.AspectRatio = ratio == "fill"
            ? $"{Math.Max(1, (int)Video.ActualWidth)}:{Math.Max(1, (int)Video.ActualHeight)}"
            : ratio;
        Toast(L.T("Imagen: {0}", L.T(label)));
    }

    void ToggleMute()
    {
        _player.Mute = !_player.Mute;
        MuteButton.Content = _player.Mute ? "" : "";
        Toast(_player.Mute ? "Silenciado" : L.T("Volumen {0}%", _player.Volume));
    }

    void SetVolume(double value)
    {
        Volume.Value = Math.Clamp(value, 0, 150);
        Toast(L.T("Volumen {0}%", (int)Volume.Value));
    }

    void SetChannelPanel(bool open)
    {
        if (open && ChannelList.ItemsSource is null) return;
        if (open == _channelsOpen) return;
        _channelsOpen = open;
        if (open)
        {
            ChannelPanel.Visibility = Visibility.Visible;
            Slide(ChannelMove, TranslateTransform.XProperty, 0, 420);
            Motion.Fade(ChannelPanel, 1, 200);
            ChannelList.SelectedItem = Current.Channel;
            if (Current.Channel is not null) ChannelList.ScrollIntoView(Current.Channel);
            Dispatcher.BeginInvoke(() =>
            {
                ChannelList.Focus();
                if (ChannelList.ItemContainerGenerator.ContainerFromItem(ChannelList.SelectedItem) is ListBoxItem item) item.Focus();
            }, DispatcherPriority.Loaded);
        }
        else
        {
            Slide(ChannelMove, TranslateTransform.XProperty, -450, 320);
            Motion.Fade(ChannelPanel, 0, 220, () =>
            {
                if (!_channelsOpen) ChannelPanel.Visibility = Visibility.Collapsed;
            });
            Overlay.Focus();
        }
        ShowControls();
    }

    static void Slide(Animatable target, DependencyProperty property, double to, int ms)
    {
        if (!Motion.Enabled)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Motion.OutStrong });
    }

    /// <summary>Shows the controls at full opacity with no animation (used by the self-test capture).</summary>
    internal void ShowControlsInstant()
    {
        _hideTimer.Stop();
        ControlsLayer.BeginAnimation(OpacityProperty, null);
        TopMove.BeginAnimation(TranslateTransform.YProperty, null);
        BottomMove.BeginAnimation(TranslateTransform.YProperty, null);
        ControlsLayer.Opacity = 1;
        TopMove.Y = BottomMove.Y = 0;
        ControlsLayer.IsHitTestVisible = true;
        _controlsShown = true;
    }

    internal void ShowControls()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
        Overlay.Cursor = null;
        if (_controlsShown) return;
        _controlsShown = true;
        ControlsLayer.IsHitTestVisible = true;
        Motion.Fade(ControlsLayer, 1, 180);
        Slide(TopMove, TranslateTransform.YProperty, 0, 360);
        Slide(BottomMove, TranslateTransform.YProperty, 0, 360);
    }

    void HideControls()
    {
        _hideTimer.Stop();
        var busy = !_player.IsPlaying || _menuOpen || _seeking || _channelsOpen || BottomBar.IsMouseOver || TopBar.IsMouseOver;
        if (busy)
        {
            _hideTimer.Start();
            return;
        }
        _controlsShown = false;
        ControlsLayer.IsHitTestVisible = false;
        Motion.Fade(ControlsLayer, 0, 420);
        Slide(TopMove, TranslateTransform.YProperty, -14, 420);
        Slide(BottomMove, TranslateTransform.YProperty, 18, 420);
        Overlay.Cursor = Cursors.None;
    }

    /// <summary>Keyboard shortcuts; called for keys pressed on the main window and on the overlay.</summary>
    internal void HandleKey(KeyEventArgs e)
    {
        if (_closing || e.Handled || _items.Count == 0) return;
        var channelListFocused = _channelsOpen && ChannelList.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Space or Key.MediaPlayPause or Key.Play or Key.Pause:
                TogglePause();
                break;
            case Key.F or Key.F11:
                _main.ToggleFullscreen();
                break;
            case Key.Enter when !channelListFocused && Keyboard.Modifiers == ModifierKeys.Alt:
                _main.ToggleFullscreen();
                break;
            case Key.Escape or Key.BrowserBack:
                if (_channelsOpen) SetChannelPanel(false);
                else Close();
                break;
            case Key.Left when !channelListFocused:
                SeekBy(-10_000);
                break;
            case Key.Right when !channelListFocused:
                SeekBy(30_000);
                break;
            case Key.Up when !channelListFocused:
                if (Current.IsLive && _items.Count > 1) Step(-1);
                else SetVolume(Volume.Value + 5);
                break;
            case Key.Down when !channelListFocused:
                if (Current.IsLive && _items.Count > 1) Step(1);
                else SetVolume(Volume.Value - 5);
                break;
            case Key.PageUp or Key.MediaPreviousTrack:
                Step(-1);
                break;
            case Key.PageDown or Key.MediaNextTrack:
                Step(1);
                break;
            case Key.OemPlus or Key.Add or Key.VolumeUp:
                SetVolume(Volume.Value + 5);
                break;
            case Key.OemMinus or Key.Subtract or Key.VolumeDown:
                SetVolume(Volume.Value - 5);
                break;
            case Key.C or Key.L:
                SetChannelPanel(!_channelsOpen);
                break;
            case Key.M or Key.VolumeMute:
                ToggleMute();
                break;
            case Key.A:
                CycleAspect();
                break;
            default:
                return;
        }
        e.Handled = true;
        if (!_closing) ShowControls();
    }

    // ----- UI handlers -----

    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if (Math.Abs(p.X - _lastMouse.X) < 2 && Math.Abs(p.Y - _lastMouse.Y) < 2) return;
        _lastMouse = p;
        ShowControls();
    }

    void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != Overlay) return;
        Overlay.Focus();
        if (_channelsOpen)
        {
            SetChannelPanel(false);
            return;
        }
        if (e.ClickCount == 2)
        {
            // Second click of a double-click: toggle full screen and undo the pause the first click triggered.
            _main.ToggleFullscreen();
            TogglePause();
        }
        else
        {
            TogglePause();
            ShowControls();
        }
    }

    void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ChannelPanel.IsMouseOver) return;
        SetVolume(Volume.Value + (e.Delta > 0 ? 5 : -5));
        ShowControls();
    }

    void Seek_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // While dragging, the clock previews the position under the thumb.
        if (_seeking && _length > 0) TimeText.Text = FormatTime((long)(e.NewValue * _length));
    }

    void Seek_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Let the slider update its value first, then seek.
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing && _player.IsSeekable && _length > 0) _player.Position = (float)Seek.Value;
            _seeking = false;
        }, DispatcherPriority.Input);
    }

    void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is null) return;
        _player.Volume = (int)e.NewValue;
        if (_player.Mute && e.NewValue > 0) ToggleMute();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePause();
    void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);
    void Next_Click(object sender, RoutedEventArgs e) => Step(1);
    void Back10_Click(object sender, RoutedEventArgs e) => SeekBy(-10_000);
    void Forward30_Click(object sender, RoutedEventArgs e) => SeekBy(30_000);
    void Fullscreen_Click(object sender, RoutedEventArgs e) => _main.ToggleFullscreen();
    void Aspect_Click(object sender, RoutedEventArgs e) => CycleAspect();
    void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    void Channels_Click(object sender, RoutedEventArgs e) => SetChannelPanel(!_channelsOpen);

    void Retry_Click(object sender, RoutedEventArgs e)
    {
        _retries = 0;
        PlayCurrent();
    }

    void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ZapToSelected();

    void ChannelList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ZapToSelected();
        e.Handled = true;
    }

    void ZapToSelected()
    {
        if (ChannelList.SelectedItem is not Channel channel) return;
        var index = _items.ToList().FindIndex(i => i.Channel == channel);
        if (index < 0 || index == _index) return;
        _index = index;
        _retries = 0;
        PlayCurrent();
    }

    void Audio_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var current = _player.AudioTrack;
        foreach (var track in _player.AudioTrackDescription)
        {
            var id = track.Id;
            var item = new MenuItem { Header = id < 0 ? L.T("Desactivado") : track.Name, IsChecked = id == current };
            item.Click += (_, _) => _player.SetAudioTrack(id);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("No hay pistas de audio"), IsEnabled = false });
        OpenMenu(menu, (Button)sender);
    }

    void Subs_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var current = _player.Spu;
        foreach (var track in _player.SpuDescription)
        {
            var id = track.Id;
            var item = new MenuItem { Header = id < 0 ? L.T("Desactivados") : track.Name, IsChecked = id == current };
            item.Click += (_, _) => _player.SetSpu(id);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("No hay subtítulos en este contenido"), IsEnabled = false });
        menu.Items.Add(new Separator());
        var load = new MenuItem { Header = L.T("Cargar archivo de subtítulos…") };
        load.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = L.T("Subtítulos (*.srt;*.ass;*.ssa;*.vtt;*.sub)|*.srt;*.ass;*.ssa;*.vtt;*.sub|Todos (*.*)|*.*") };
            if (dialog.ShowDialog(_overlayWindow ?? Application.Current.MainWindow) == true)
                _player.AddSlave(MediaSlaveType.Subtitle, new Uri(dialog.FileName).AbsoluteUri, true);
        };
        menu.Items.Add(load);
        OpenMenu(menu, (Button)sender);
    }

    void OpenMenu(ContextMenu menu, Button target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Top;
        menu.Closed += (_, _) => { _menuOpen = false; ShowControls(); };
        _menuOpen = true;
        menu.IsOpen = true;
    }

    // ----- Closing -----

    /// <summary>Closes the player and returns to the screen underneath.</summary>
    internal void Close()
    {
        if (_closing) return;
        _closing = true;
        SaveProgress();
        if (_instance == this) _instance = null;
        _main.UnmountPlayer(this, animate: true);
        // Stopping a network stream can block; do it off the UI thread and release afterwards.
        var player = _player;
        Task.Run(() => player.Stop()).ContinueWith(_ => Dispatcher.BeginInvoke(Release));
    }

    void Release()
    {
        if (_disposed) return;
        _disposed = true;
        if (_instance is null) DownloadManager.I.Suspended = false; // the stream is stopped: the connection is free again
        _hideTimer.Stop();
        _toastTimer.Stop();
        _retryTimer.Stop();
        _liveTimer.Stop();
        _nowCts?.Cancel();
        _main.FullscreenChanged -= UpdateFullscreenButton;
        S.Settings.Volume = (int)Volume.Value;
        S.SaveSettings();
        var player = _player;
        var media = _media;
        try
        {
            Video.MediaPlayer = null;
            Video.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo liberar la superficie de vídeo", ex);
        }
        Task.Run(() =>
        {
            player.Dispose();
            media?.Dispose();
        });
    }
}
