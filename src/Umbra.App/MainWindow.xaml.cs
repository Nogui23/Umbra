using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Umbra.Core;

namespace Umbra.App;

/// <summary>Screens that reload their data when the user comes back to them (e.g. after closing the player).</summary>
public interface IRefreshable
{
    void Refresh();
}

/// <summary>
/// The single window of the app: top navigation bar, the current screen and, over them, the player.
/// It opens covering the whole screen; F11 switches between full screen and a normal window.
/// </summary>
public partial class MainWindow : Window
{
    enum Transition { Tab, Push, Pop }

    readonly Stack<UIElement> _back = new();
    readonly DispatcherTimer _toastTimer;
    HomeView? _home;
    LiveView? _live;
    BrowserView? _movies, _series;
    SearchView? _search;
    AccountView? _account;
    DownloadsView? _downloads;
    LibraryView? _library;
    SettingsView? _settings;
    RadioButton? _currentNav;
    bool _fullscreen, _thumbPlaced;

    public static MainWindow Instance => (MainWindow)Application.Current.MainWindow!;

    public bool IsFullscreen => _fullscreen;
    public event Action? FullscreenChanged;

    public MainWindow(bool fullscreen)
    {
        InitializeComponent();
        if (fullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _fullscreen = true;
        }
        UpdateWindowButtons();

        Root.SizeChanged += (_, e) => Ui.I.Resize(e.NewSize.Width, e.NewSize.Height);
        Ui.I.Changed += OnUiChanged;
        NavTabs.SizeChanged += (_, _) => MoveThumb(animate: false);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Motion.Fade(ToastHost, 0, 260);
        };

        PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.XButton1) return;
            if (PlayerView.Active is { } player) player.Close();
            else if (_back.Count > 0) GoBack();
            else return;
            e.Handled = true;
        };
        PreviewKeyDown += OnPreviewKeyDown;

        DownloadManager.I.Finished += item =>
        {
            if (item.State == DownloadState.Completed) Toast(L.T("Descarga completada: {0}", item.Title), "");
            else if (item.State == DownloadState.Failed) Toast(L.T("No se pudo descargar: {0}", item.Title), "");
        };
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While a video is playing every key belongs to the player.
        if (PlayerView.Active is { } player)
        {
            player.HandleKey(e);
            return;
        }
        var inText = Keyboard.FocusedElement is TextBox or PasswordBox;
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Alt:
                ToggleFullscreen();
                break;
            case Key.Escape when !inText && _back.Count > 0:
            case Key.BrowserBack when _back.Count > 0:
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Alt && _back.Count > 0:
                GoBack();
                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control && AppState.I.Library is not null:
            case Key.K when Keyboard.Modifiers == ModifierKeys.Control && AppState.I.Library is not null:
                SelectNav(nameof(NavSearch));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ----- Responsive layout -----

    void OnUiChanged()
    {
        var compact = Ui.I.Compact;
        AccountLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BrandText.Visibility = Ui.I.Width < 1180 ? Visibility.Collapsed : Visibility.Visible;
        Dispatcher.BeginInvoke(() => MoveThumb(animate: false), DispatcherPriority.Loaded);
    }

    // ----- Full screen -----

    public void ToggleFullscreen() => SetFullscreen(!_fullscreen);

    public void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        if (on)
        {
            // Leaving the maximised state first makes Windows cover the taskbar too.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            var area = SystemParameters.WorkArea;
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            if (Left < area.Left || Top < area.Top || Left + Width > area.Right || Top + Height > area.Bottom)
            {
                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + (area.Height - Height) / 2;
            }
        }
        _fullscreen = on;
        UpdateWindowButtons();
        FullscreenChanged?.Invoke();
    }

    void UpdateWindowButtons()
    {
        // In a normal window the system title bar already has minimise and close.
        MinButton.Visibility = CloseButton.Visibility = _fullscreen ? Visibility.Visible : Visibility.Collapsed;
        FullButton.Content = _fullscreen ? "" : "";
        FullButton.ToolTip = L.T(_fullscreen ? "Salir de pantalla completa (F11)" : "Pantalla completa (F11)");
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Full_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ----- Screens -----

    /// <summary>
    /// The screen shown with no account open. When something is downloaded that is the library, ready to watch;
    /// the accounts are one button away. With <paramref name="accounts"/> (or an error to show) it is the accounts.
    /// </summary>
    public void ShowLogin(string? error = null, bool accounts = false)
    {
        NavPill.Visibility = AccountArea.Visibility = Visibility.Collapsed;
        _back.Clear();
        ResetViews();
        var library = error is null && !accounts && DownloadManager.I.Items.Any(i => i.IsCompleted);
        ShowContent(library ? new LibraryView() : new LoginView(error));
    }

    public bool CanGoBack => _back.Count > 0;

    /// <summary>Assigns the host content with a soft transition.</summary>
    void ShowContent(UIElement view, Transition transition = Transition.Tab)
    {
        Host.Content = view;
        if (!Motion.Enabled)
        {
            Host.RenderTransform = Transform.Identity;
            return;
        }
        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        Host.RenderTransform = new TransformGroup { Children = { scale, move } };
        Host.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 280));
        switch (transition)
        {
            case Transition.Tab:
                move.BeginAnimation(TranslateTransform.YProperty, Motion.Tween(16, 0, 420, 0, Motion.OutStrong));
                break;
            case Transition.Push:
                var grow = Motion.Tween(0.965, 1, 420, 0, Motion.OutStrong);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                break;
            case Transition.Pop:
                var shrink = Motion.Tween(1.03, 1, 380, 0, Motion.OutStrong);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
                break;
        }
    }

    /// <summary>Loads (or refreshes) the account's content with a progress screen, then shows the home screen.</summary>
    public async Task OpenAccountAsync(Account account, IContentSource source, bool force)
    {
        NavPill.Visibility = AccountArea.Visibility = Visibility.Collapsed;
        _back.Clear();
        var loading = new LoadingView(account.Name);
        ShowContent(loading);
        try
        {
            var library = await source.LoadLibraryAsync(force, new Progress<string>(loading.SetStatus), CancellationToken.None);
            AppState.I.SetSession(account, source, library);
            EnterShell(account);

            foreach (var w in library.Warnings) Log.Info(w);
            if (library.Channels.Count == 0 && library.Movies.Count == 0 && library.Series.Count == 0)
                Sheet.Info("Sin contenido", "La cuenta no tiene contenido disponible.");

            if (AppState.I.Settings.AutoPlayLastChannel && AppState.I.Data.LastChannelId is { } lastId &&
                library.ChannelById(lastId) is { } lastChannel)
            {
                var siblings = library.Channels.Where(c => c.CategoryId == lastChannel.CategoryId).ToList();
                PlayerView.OpenChannels(siblings, lastChannel);
            }
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cargar la cuenta", ex);
            ShowLogin(Errors.Friendly(ex));
        }
    }

    /// <summary>Shows the navigation for a loaded account and opens the home screen.</summary>
    internal void EnterShell(Account account)
    {
        ResetViews();
        AccountLabel.Text = account.Name;
        AccountInitial.Text = InitialConverter.Of(account.Name);
        NavPill.Visibility = AccountArea.Visibility = Visibility.Visible;
        Motion.Fade(NavPill, 1, 0);
        NavPill.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 420));
        NavHome.IsChecked = true;
    }

    void ResetViews()
    {
        _home = null;
        _live = null;
        _movies = _series = null;
        _search = null;
        _account = null;
        _downloads = null;
        _library = null;
        _settings = null;
        _currentNav = null;
        _thumbPlaced = false;
        foreach (var rb in new[] { NavHome, NavLive, NavMovies, NavSeries, NavLibrary, NavSearch, NavAccount, NavDownloads, NavSettings })
            rb.IsChecked = false;
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        _currentNav = (RadioButton)sender;
        Show(_currentNav);
        Dispatcher.BeginInvoke(() => MoveThumb(animate: true), DispatcherPriority.Loaded);
    }

    void Show(RadioButton nav)
    {
        if (AppState.I.Library is null) return;
        _back.Clear();
        UIElement view = nav.Name switch
        {
            nameof(NavHome) => _home ??= new HomeView(),
            nameof(NavLive) => _live ??= new LiveView(),
            nameof(NavMovies) => _movies ??= new BrowserView(MediaKind.Movie),
            nameof(NavSeries) => _series ??= new BrowserView(MediaKind.Series),
            nameof(NavSearch) => _search ??= new SearchView(),
            nameof(NavAccount) => _account ??= new AccountView(),
            nameof(NavDownloads) => _downloads ??= new DownloadsView(),
            nameof(NavLibrary) => _library ??= new LibraryView(),
            _ => _settings ??= new SettingsView(),
        };
        ShowContent(view);
        (view as IRefreshable)?.Refresh();
    }

    /// <summary>Slides the selection pill of the navigation bar under the current tab.</summary>
    void MoveThumb(bool animate)
    {
        var tab = _currentNav;
        if (tab is null || !ReferenceEquals(tab.Parent, NavTabs) || tab.ActualWidth <= 0 || !NavTabs.IsVisible)
        {
            Motion.Fade(NavThumb, 0, 180);
            _thumbPlaced = false;
            return;
        }
        var x = tab.TranslatePoint(new Point(0, 0), NavTabs).X;
        var width = tab.ActualWidth;
        if (animate && _thumbPlaced && Motion.Enabled)
        {
            NavThumbMove.BeginAnimation(TranslateTransform.XProperty, Motion.Tween(NavThumbMove.X, x, 420, 0, Motion.OutStrong));
            NavThumb.BeginAnimation(WidthProperty, Motion.Tween(NavThumb.ActualWidth, width, 420, 0, Motion.OutStrong));
        }
        else
        {
            NavThumbMove.BeginAnimation(TranslateTransform.XProperty, null);
            NavThumb.BeginAnimation(WidthProperty, null);
            NavThumbMove.X = x;
            NavThumb.Width = width;
        }
        Motion.Fade(NavThumb, 1, 200);
        _thumbPlaced = true;
    }

    public void Push(UIElement view)
    {
        if (Host.Content is UIElement current) _back.Push(current);
        ShowContent(view, Transition.Push);
    }

    public void GoBack()
    {
        if (_back.Count == 0) return;
        var view = _back.Pop();
        ShowContent(view, Transition.Pop);
        (view as IRefreshable)?.Refresh();
    }

    public void SelectNav(string name)
    {
        var rb = (RadioButton)FindName(name);
        if (rb.IsChecked == true) Show(rb);
        else rb.IsChecked = true;
    }

    public void ShowDownloads() => SelectNav(nameof(NavDownloads));

    /// <summary>Opens the downloaded movies and episodes over the sign-in screen, with no account loaded.</summary>
    public void ShowLibraryOffline() => Push(new LibraryView());

    /// <summary>
    /// Applies a language change: texts written in XAML follow by themselves; the screens are rebuilt so the texts
    /// they compose in code are written again in the new language.
    /// </summary>
    public void ReloadLanguage()
    {
        UpdateWindowButtons();
        if (AppState.I.Library is null)
        {
            ShowLogin(accounts: Host.Content is LoginView);
            return;
        }
        _back.Clear();
        _home = null;
        _live = null;
        _movies = _series = null;
        _search = null;
        _account = null;
        _downloads = null;
        _library = null;
        _settings = null;
        if (_currentNav is not null) Show(_currentNav);
    }

    /// <summary>Reloads the visible screen (favourites, progress…) without animating.</summary>
    public void RefreshCurrent() => (Host.Content as IRefreshable)?.Refresh();

    // ----- Toast -----

    /// <summary>Shows a short confirmation at the bottom of the window.</summary>
    public void Toast(string text, string glyph = "")
    {
        ToastText.Text = L.T(text);
        ToastGlyph.Text = glyph;
        Motion.Fade(ToastHost, 1, 180);
        if (Motion.Enabled)
            ToastMove.BeginAnimation(TranslateTransform.YProperty, Motion.Tween(14, 0, 380, 0, Motion.Spring));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ----- Player layer -----

    /// <summary>Puts the player over the shell: the shell dims to black, then the video surface appears.</summary>
    internal void MountPlayer(PlayerView view)
    {
        PlayerLayer.Visibility = Visibility.Visible;
        Shell.IsHitTestVisible = false;
        void Attach()
        {
            if (view.IsClosing || view.Parent is not null) return;
            PlayerLayer.Children.Add(view);
        }
        if (App.Quiet || !Motion.Enabled)
        {
            Motion.Fade(PlayerLayer, 1, 0);
            Attach();
        }
        else
        {
            Motion.Fade(PlayerLayer, 1, 190, Attach);
        }
    }

    /// <summary>Removes the player and brings the shell back.</summary>
    internal void UnmountPlayer(PlayerView view, bool animate)
    {
        PlayerLayer.Children.Remove(view);
        if (PlayerView.Active is not null) return; // another video is already taking its place
        Shell.IsHitTestVisible = true;
        RefreshCurrent();
        void Done()
        {
            if (PlayerView.Active is null && PlayerLayer.Children.Count == 0) PlayerLayer.Visibility = Visibility.Collapsed;
        }
        Motion.Fade(PlayerLayer, 0, animate && !App.Quiet ? 260 : 0, Done);
        if (animate && Motion.Enabled && !App.Quiet)
        {
            var scale = new ScaleTransform(1, 1);
            Shell.RenderTransformOrigin = new Point(0.5, 0.5);
            Shell.RenderTransform = scale;
            var settle = Motion.Tween(0.975, 1, 420, 0, Motion.OutStrong);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        }
        if (!App.Quiet)
        {
            Activate();
            Focus();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        PlayerView.Shutdown();
        DownloadManager.I.Shutdown();
        base.OnClosing(e);
    }
}
