using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Umbra.Core;

namespace Umbra.App;

/// <summary>Live TV: categories, channels and, for the selected channel, its actions and programme guide.</summary>
public partial class LiveView : UserControl, IRefreshable
{
    readonly AppState S = AppState.I;
    readonly DispatcherTimer _filterTimer;
    readonly DispatcherTimer _epgTimer;
    readonly DispatcherTimer _clock;
    List<Channel> _current = new();
    Category? _category;
    CancellationTokenSource? _epgCts;
    bool _reverting;

    public LiveView()
    {
        InitializeComponent();
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilter(); };
        _epgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _epgTimer.Tick += (_, _) => { _epgTimer.Stop(); _ = LoadEpgAsync(); };
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => RefreshProgramStates();
        Filter.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        Loaded += (_, _) =>
        {
            _clock.Start();
            Ui.I.Changed += ApplyLayout;
            ApplyLayout();
        };
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            Ui.I.Changed -= ApplyLayout;
        };
        BuildCategories();
    }

    /// <summary>Narrow canvases get slimmer side columns so the channel list keeps its room.</summary>
    void ApplyLayout()
    {
        var compact = Ui.I.Compact;
        CategoryColumn.Width = new GridLength(compact ? 216 : 270);
        DetailColumn.Width = new GridLength(compact ? 350 : 440);
        Layout.Margin = compact ? new Thickness(26, 10, 26, 26) : new Thickness(42, 10, 42, 34);
        DetailCard.Padding = new Thickness(compact ? 18 : 24);
        Filter.Width = compact ? 220 : 280;
    }

    readonly Category _allCat = new() { Id = "@all", Name = L.T("Todos los canales"), Kind = MediaKind.Live, Icon = "" };
    readonly Category _favCat = new() { Id = "@fav", Name = L.T("Favoritos"), Kind = MediaKind.Live, Icon = "" };
    readonly Category _recentCat = new() { Id = "@recent", Name = L.T("Vistos recientemente"), Kind = MediaKind.Live, Icon = "" };

    void BuildCategories()
    {
        var cats = new List<Category> { _allCat, _favCat, _recentCat };
        cats.AddRange(S.Library!.LiveCategories);
        UpdateVirtualCounts();
        Categories.ItemsSource = cats;
        Categories.SelectedIndex = 0;
    }

    void UpdateVirtualCounts()
    {
        _allCat.Count = ChannelsFor(_allCat).Count();
        _favCat.Count = ChannelsFor(_favCat).Count();
        _recentCat.Count = ChannelsFor(_recentCat).Count();
    }

    public void Refresh()
    {
        UpdateVirtualCounts();
        if (_category?.Id is "@fav" or "@recent") ApplyFilter(keepSelection: true);
        UpdateFavButton();
    }

    IEnumerable<Channel> ChannelsFor(Category category)
    {
        var lib = S.Library!;
        return category.Id switch
        {
            "@all" => lib.Channels.Where(c => !S.IsHidden(MediaKind.Live, c.CategoryId)),
            "@fav" => lib.Channels.Where(c => c.IsFavorite && !S.IsHidden(MediaKind.Live, c.CategoryId)),
            "@recent" => S.Data.RecentChannels.Select(lib.ChannelById).OfType<Channel>()
                .Where(c => !S.IsHidden(MediaKind.Live, c.CategoryId)),
            _ => lib.Channels.Where(c => c.CategoryId == category.Id),
        };
    }

    void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reverting || Categories.SelectedItem is not Category category) return;
        if (!category.IsVirtual && !S.TryUnlock(category))
        {
            _reverting = true;
            Categories.SelectedItem = _category;
            _reverting = false;
            return;
        }
        _category = category;
        ApplyFilter();
        if (IsLoaded) Motion.Enter(Channels, 0, 10, 320);
    }

    void ApplyFilter(bool keepSelection = false)
    {
        if (_category is null) return;
        var selected = Channels.SelectedItem as Channel;
        var query = Filter.Text.Trim();
        var list = ChannelsFor(_category);
        if (query.Length > 0) list = list.Where(c => Text.Contains(c.Name, query));
        _current = list.ToList();
        Channels.ItemsSource = _current;
        CountText.Text = _current.Count == 1 ? L.T("{0} · 1 canal", _category.Name) : L.T("{0} · {1:N0} canales", _category.Name, _current.Count);

        EmptyPanel.Visibility = _current.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = L.T(query.Length > 0 ? "Ningún canal coincide con la búsqueda."
            : _category.Id switch
            {
                "@fav" => "Aún no tienes canales favoritos.\nElige un canal y pulsa la estrella.",
                "@recent" => "Aquí aparecerán los últimos canales que veas.",
                _ => "Esta categoría no tiene canales.",
            });

        if (keepSelection && selected is not null && _current.Contains(selected)) Channels.SelectedItem = selected;
        else if (_current.Count > 0) Channels.SelectedIndex = 0;
        if (Channels.SelectedItem is not null) Channels.ScrollIntoView(Channels.SelectedItem);
    }

    void Channels_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Channels.SelectedItem is not Channel channel)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            NoSelection.Visibility = Visibility.Visible;
            return;
        }
        var first = DetailPanel.Visibility != Visibility.Visible;
        DetailPanel.Visibility = Visibility.Visible;
        NoSelection.Visibility = Visibility.Collapsed;
        DetailName.Text = channel.Name;
        DetailInfo.Text = channel.HasArchive
            ? (channel.ArchiveDays > 0
                ? L.T("Canal {0} · Catch-up {1} días", channel.Num, channel.ArchiveDays)
                : L.T("Canal {0} · Catch-up disponible", channel.Num))
            : L.T("Canal {0}", channel.Num);
        DetailLogo.Source = Images.Load(channel.Logo, 320);
        UpdateFavButton();
        Epg.ItemsSource = null;
        ProgramBox.Visibility = Visibility.Collapsed;
        SetEpgStatus("Cargando guía…", busy: true);
        _epgTimer.Stop();
        _epgTimer.Start();
        if (IsLoaded && !first) Motion.Fade(DetailPanel, 0.55, 0);
        if (IsLoaded) Motion.Fade(DetailPanel, 1, 260);
    }

    void SetEpgStatus(string text, bool busy)
    {
        EpgStatus.Text = L.T(text);
        EpgSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        EpgStatusPanel.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateFavButton()
    {
        if (Channels.SelectedItem is not Channel channel) return;
        FavButton.Content = channel.IsFavorite ? "" : "";
        FavButton.ToolTip = L.T(channel.IsFavorite ? "Quitar de favoritos" : "Añadir a favoritos");
    }

    async Task LoadEpgAsync()
    {
        if (Channels.SelectedItem is not Channel channel || S.Source is null) return;
        _epgCts?.Cancel();
        var cts = _epgCts = new CancellationTokenSource();
        try
        {
            var list = await S.Source.GetEpgAsync(channel, cts.Token);
            if (cts.IsCancellationRequested || Channels.SelectedItem != channel) return;
            var now = DateTime.Now;
            foreach (var p in list)
            {
                p.Refresh(now);
                p.CanCatchup = p.IsPast && (p.HasArchive || channel.HasArchive) && S.Source.GetCatchupUrl(channel, p) is not null;
            }
            Epg.ItemsSource = list;
            SetEpgStatus(list.Count == 0 ? "No hay guía de programación para este canal." : "", busy: false);
            Motion.Enter(Epg, 0, 10, 320);
            var current = list.FirstOrDefault(p => p.IsNow) ?? list.FirstOrDefault(p => !p.IsPast);
            if (current is not null)
            {
                Epg.SelectedItem = current;
                Epg.ScrollIntoView(list[^1]);
                Epg.ScrollIntoView(current);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cargar la guía", ex);
            SetEpgStatus("No se pudo cargar la guía de programación.", busy: false);
        }
    }

    void RefreshProgramStates()
    {
        if (Epg.ItemsSource is not IEnumerable<EpgProgram> list) return;
        var now = DateTime.Now;
        foreach (var p in list) p.Refresh(now);
    }

    void Epg_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Epg.SelectedItem is not EpgProgram p)
        {
            ProgramBox.Visibility = Visibility.Collapsed;
            return;
        }
        ProgramBox.Visibility = Visibility.Visible;
        ProgramTitle.Text = p.Title;
        ProgramTime.Text = $"{p.DayText} · {p.TimeText}" + (p.IsNow ? L.T(" · En emisión") : "");
        ProgramDesc.Text = string.IsNullOrWhiteSpace(p.Description) ? L.T("Sin descripción.") : p.Description;
        CatchupButton.Visibility = p.CanCatchup ? Visibility.Visible : Visibility.Collapsed;
    }

    void Epg_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Epg.SelectedItem is EpgProgram { CanCatchup: true } p) PlayCatchup(p);
        else if (Epg.SelectedItem is EpgProgram { IsNow: true }) Play_Click(sender, e);
    }

    void Catchup_Click(object sender, RoutedEventArgs e)
    {
        if (Epg.SelectedItem is EpgProgram p) PlayCatchup(p);
    }

    void PlayCatchup(EpgProgram program)
    {
        if (Channels.SelectedItem is not Channel channel || S.Source is null) return;
        var url = S.Source.GetCatchupUrl(channel, program);
        if (url is null) return;
        PlayerView.Open(new[]
        {
            new PlayItem
            {
                Title = $"{channel.Name} · {program.Title}",
                Subtitle = $"Catch-up · {program.DayText} {program.TimeText}",
                Url = url,
                IsLive = false,
            },
        }, 0);
    }

    void Channels_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(Channels, d) is ListBoxItem)
            Play_Click(sender, e);
    }

    void Channels_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Play_Click(sender, e); e.Handled = true; }
    }

    /// <summary>The round play button that appears on the row under the mouse.</summary>
    void Channels_ButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button { Tag: "play", DataContext: Channel channel }) PlayChannel(channel);
    }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Channels.SelectedItem is Channel channel) PlayChannel(channel);
    }

    /// <summary>Plays a channel with the current list as the zapping list (Re Pág / Av Pág in the player).</summary>
    public void PlayChannel(Channel channel) =>
        PlayerView.OpenChannels(_current.Contains(channel) ? _current : S.Library!.Channels, channel);

    void Fav_Click(object sender, RoutedEventArgs e)
    {
        if (Channels.SelectedItem is not Channel channel) return;
        S.ToggleFavorite(channel);
        UpdateFavButton();
        Motion.Pop(FavButton, 0.8, 360);
        MainWindow.Instance.Toast(channel.IsFavorite ? "Añadido a favoritos" : "Quitado de favoritos", channel.IsFavorite ? "" : "");
        UpdateVirtualCounts();
        if (_category?.Id == "@fav") ApplyFilter(keepSelection: true);
    }

    void Categories_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = e.OriginalSource is DependencyObject d ? ItemsControl.ContainerFromElement(Categories, d) as ListBoxItem : null;
        if (item?.DataContext is not Category { IsVirtual: false } category)
        {
            e.Handled = true;
            return;
        }
        LockMenuItem.Tag = category;
        LockMenuItem.Header = L.T(category.IsLocked ? "Desbloquear «{0}»" : "Bloquear «{0}» con PIN", category.Name);
    }

    void LockMenu_Click(object sender, RoutedEventArgs e)
    {
        ToggleLock(LockMenuItem.Tag as Category);
        UpdateVirtualCounts();
        ApplyFilter(keepSelection: true);
    }

    internal static void ToggleLock(Category? category)
    {
        if (category is null) return;
        var s = AppState.I;
        if (category.IsLocked)
        {
            if (s.CheckPin(L.T("Desbloquear «{0}»", category.Name), "Introduce el PIN de control parental.")) s.SetLocked(category, false);
        }
        else
        {
            s.SetLocked(category, true);
            MainWindow.Instance.Toast(L.T("«{0}» bloqueada con PIN", category.Name), "");
        }
    }
}
