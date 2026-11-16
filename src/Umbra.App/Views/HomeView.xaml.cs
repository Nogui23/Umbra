using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Umbra.Core;

namespace Umbra.App;

/// <summary>A poster of «Seguir viendo»: a movie, or a series with the episode it continues with.</summary>
public sealed class ResumeTile
{
    public required PosterItem Item { get; init; }
    /// <summary>"movie:ID" or "series:ID".</summary>
    public required string Key { get; init; }
    public required string Note { get; init; }
    public DateTime Utc { get; init; }
    public string Name => Item.Name;
    public string Image => Item.Image;
}

/// <summary>Landing screen: greeting, the three sections and shelves with what the user is most likely to want next.</summary>
public partial class HomeView : UserControl, IRefreshable
{
    const int ShelfItems = 16;
    const double PosterCell = 190, PosterShelfHeight = 314;
    const double ChannelCell = 202, ChannelShelfHeight = 156;

    readonly AppState S = AppState.I;
    string _signature = "";
    readonly System.Windows.Threading.DispatcherTimer _connectionsTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    bool _askingConnections;

    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => Motion.Stagger(Page.Children.OfType<FrameworkElement>(), 40, 70);
        // The figure changes outside the app (other devices), so it is asked again while the screen is on view.
        _connectionsTimer.Tick += (_, _) => UpdateConnections();
        IsVisibleChanged += (_, _) => _connectionsTimer.IsEnabled = IsVisible;
    }

    public void Refresh()
    {
        var lib = S.Library;
        if (lib is null) return;
        var hour = DateTime.Now.Hour;
        Greeting.Text = L.T(hour is >= 6 and < 13 ? "Buenos días" : hour is >= 13 and < 21 ? "Buenas tardes" : "Buenas noches");
        SubGreeting.Text = S.Account is { } a ? L.T("{0} · ¿Qué te apetece ver?", a.Name) : L.T("¿Qué te apetece ver?");
        LiveCount.Text = Plural(lib.Channels.Count, "1 canal", "{0:N0} canales");
        MovieCount.Text = Plural(lib.Movies.Count, "1 película", "{0:N0} películas");
        SeriesCount.Text = Plural(lib.Series.Count, "1 serie", "{0:N0} series");
        ShowConnections(S.Source?.Info);
        UpdateConnections();

        bool Visible(MediaKind kind, string categoryId) => !S.IsHidden(kind, categoryId);
        var channels = lib.Channels.Where(c => Visible(MediaKind.Live, c.CategoryId));
        var movies = lib.Movies.Where(m => Visible(MediaKind.Movie, m.CategoryId)).ToList();
        var series = lib.Series.Where(s => Visible(MediaKind.Series, s.CategoryId)).ToList();

        // Movies left halfway and series in progress, the most recent first.
        var resume = movies
            .Select(m => (Movie: m, Key: AppState.Key(MediaKind.Movie, m.Id)))
            .Where(x => S.ResumeOf(x.Key) > 0)
            .Select(x => new ResumeTile
            {
                Item = x.Movie,
                Key = x.Key,
                Note = L.T("Desde {0}", MovieDetailView.FormatTime(S.ResumeOf(x.Key))),
                Utc = S.ResumeTimeOf(x.Key),
            })
            .Concat(series
                .Select(s => (Series: s, Key: AppState.Key(MediaKind.Series, s.Id)))
                .Where(x => S.Data.Series.ContainsKey(x.Key))
                .Select(x => new ResumeTile
                {
                    Item = x.Series,
                    Key = x.Key,
                    Note = S.Data.Series[x.Key].Label,
                    Utc = S.Data.Series[x.Key].Utc,
                }))
            .OrderByDescending(t => t.Utc)
            .Take(ShelfItems)
            .ToList<object>();
        var recent = S.Data.RecentChannels.Select(lib.ChannelById).OfType<Channel>()
            .Where(c => Visible(MediaKind.Live, c.CategoryId)).Take(ShelfItems).ToList<object>();
        var favChannels = channels.Where(c => c.IsFavorite).Take(ShelfItems).ToList<object>();
        var favPosters = movies.Where(m => m.IsFavorite).Cast<PosterItem>().Concat(series.Where(s => s.IsFavorite))
            .Take(ShelfItems).ToList<object>();
        var newMovies = movies.Where(m => m.Added > 0).OrderByDescending(m => m.Added).Take(ShelfItems).ToList<object>();
        var newSeries = series.Where(s => s.Added > 0).OrderByDescending(s => s.Added).Take(ShelfItems).ToList<object>();
        var firstChannels = recent.Count == 0 && favChannels.Count == 0 ? channels.Take(ShelfItems).ToList<object>() : new List<object>();

        // Rebuilding the shelves reloads their pictures, so only do it when their content changed.
        var signature = string.Join("|", new[] { resume, recent, favChannels, favPosters, newMovies, newSeries, firstChannels }
            .Select(list => string.Join(",", list.Select(o => o switch
            {
                Channel c => c.Id,
                ResumeTile t => t.Key + "=" + t.Note,
                _ => ((PosterItem)o).Id,
            }))));
        if (signature == _signature) return;
        _signature = signature;

        Shelves.Children.Clear();
        AddResumeShelf(resume);
        AddChannelShelf("Vistos recientemente", recent);
        AddChannelShelf("Canales favoritos", favChannels);
        AddChannelShelf("Canales", firstChannels);
        AddPosterShelf("Tus favoritas", favPosters, null);
        AddPosterShelf("Películas recién añadidas", newMovies, nameof(MainWindow.NavMovies));
        AddPosterShelf("Series recién actualizadas", newSeries, nameof(MainWindow.NavSeries));
    }

    /// <summary>Asks the provider how many connections are in use and shows how many are left.</summary>
    async void UpdateConnections()
    {
        var source = S.Source;
        if (source is null || _askingConnections || App.Quiet) return;
        _askingConnections = true;
        try
        {
            var info = await Task.Run(() => source.RefreshInfoAsync(CancellationToken.None));
            if (ReferenceEquals(source, S.Source)) ShowConnections(info);
        }
        catch (Exception ex)
        {
            // Keeps the last known figure: a failed query says nothing about the connections.
            Log.Error("No se pudieron consultar las conexiones", ex);
        }
        finally { _askingConnections = false; }
    }

    void ShowConnections(AccountInfo? info)
    {
        // Lists without an account (M3U) and accounts without a limit have nothing to count.
        if (info is not { MaxConnections: > 0 })
        {
            ConnectionsPill.Visibility = Visibility.Collapsed;
            return;
        }
        var free = Math.Max(0, info.MaxConnections - info.ActiveConnections);
        ConnectionsText.Text = info.MaxConnections == 1
            ? L.T(free == 1 ? "Conexión libre" : "Conexión en uso")
            : L.T("{0} de {1} conexiones libres", free, info.MaxConnections);
        ConnectionsDot.Fill = new System.Windows.Media.SolidColorBrush(free > 0
            ? System.Windows.Media.Color.FromRgb(0x30, 0xD1, 0x58)
            : System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x0A));
        ConnectionsPill.ToolTip = L.T("En uso ahora mismo: {0} de {1}. Las descargas de Umbra también ocupan conexión.",
            info.ActiveConnections, info.MaxConnections);
        ConnectionsPill.Visibility = Visibility.Visible;
    }

    void Connections_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.SelectNav(nameof(MainWindow.NavAccount));

    static string Plural(int count, string one, string many) => count == 1 ? L.T(one) : L.T(many, count);

    /// <summary>
    /// «Seguir viendo»: movies and series. Besides opening one, it can be tidied up — right-click a poster to take
    /// it off the shelf, or «Limpiar» to empty it.
    /// </summary>
    void AddResumeShelf(List<object> items)
    {
        if (items.Count == 0) return;
        var (header, row) = AddShelf("Seguir viendo", items, "ResumeTemplate", PosterCell, PosterShelfHeight, null, (_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement { DataContext: ResumeTile tile }) return;
            MainWindow.Instance.Push(tile.Item is Movie m ? new MovieDetailView(m) : new SeriesDetailView((SeriesItem)tile.Item));
        });

        var clear = new Button
        {
            Content = L.T("Limpiar"),
            Style = (Style)FindResource("GhostButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = L.T("Quitar todo de «Seguir viendo»"),
        };
        clear.Click += (_, _) =>
        {
            var count = S.ContinueCount;
            var message = count == 1
                ? L.T("Se olvidará por dónde ibas en 1 película o serie. No se borra ninguna descarga ni favorita.")
                : L.T("Se olvidará por dónde ibas en {0} películas y series. No se borra ninguna descarga ni favorita.", count);
            if (!Sheet.Confirm("¿Vaciar «Seguir viendo»?", message, "Vaciar")) return;
            S.ClearContinueWatching();
            Refresh();
            MainWindow.Instance.Toast("«Seguir viendo» está vacío", "");
        };
        // «Limpiar» sits to the left of «Ver todo».
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in header.Children.OfType<Button>().ToList())
        {
            header.Children.Remove(button);
            actions.Children.Add(button);
        }
        actions.Children.Insert(0, clear);
        header.Children.Add(actions);

        var remove = new MenuItem { Header = L.T("Quitar de «Seguir viendo»") };
        var menu = new ContextMenu();
        menu.Items.Add(remove);
        row.ContextMenu = menu;
        row.ContextMenuOpening += (_, e) =>
        {
            // The menu belongs to a poster: nothing opens on the gaps between them.
            if (e.OriginalSource is FrameworkElement { DataContext: ResumeTile tile }) remove.Tag = tile;
            else e.Handled = true;
        };
        remove.Click += (_, _) =>
        {
            if (remove.Tag is not ResumeTile tile) return;
            // A series leaves the shelf; its episodes keep their progress.
            if (tile.Item is Movie) S.ClearProgress(tile.Key);
            else S.ForgetSeries(tile.Key);
            Refresh();
            MainWindow.Instance.Toast("Quitada de «Seguir viendo»", "");
        };
    }

    (Grid Header, ItemsControl Row) AddPosterShelf(string title, List<object> items, string? nav) =>
        AddShelf(title, items, "PosterTemplate", PosterCell, PosterShelfHeight, nav, (_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement { DataContext: PosterItem item }) return;
            MainWindow.Instance.Push(item is Movie m ? new MovieDetailView(m) : new SeriesDetailView((SeriesItem)item));
        });

    void AddChannelShelf(string title, List<object> items) =>
        _ = AddShelf(title, items, "ChannelCardTemplate", ChannelCell, ChannelShelfHeight, nameof(MainWindow.NavLive), (_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement { DataContext: Channel channel }) return;
            PlayerView.OpenChannels(items.OfType<Channel>().ToList(), channel);
        });

    /// <summary>A titled single row: as many tiles as fit the width, the rest is reached through «Ver todo».</summary>
    (Grid Header, ItemsControl Row) AddShelf(string title, List<object> items, string template, double cellWidth, double height, string? nav, RoutedEventHandler click)
    {

        var header = new Grid { Margin = new Thickness(2, 0, 0, 4) };
        header.Children.Add(new TextBlock { Text = L.T(title), Style = (Style)FindResource("H2"), VerticalAlignment = VerticalAlignment.Center });
        if (nav is not null)
        {
            var all = new Button
            {
                Content = L.T("Ver todo"),
                Style = (Style)FindResource("GhostButton"),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            all.Click += (_, _) => MainWindow.Instance.SelectNav(nav);
            header.Children.Add(all);
        }

        var cell = new Style(typeof(ContentPresenter));
        cell.Setters.Add(new Setter(WidthProperty, cellWidth));
        var panel = new FrameworkElementFactory(typeof(WrapPanel));
        var row = new ItemsControl
        {
            ItemsSource = items,
            ItemTemplate = (DataTemplate)FindResource(template),
            ItemContainerStyle = cell,
            ItemsPanel = new ItemsPanelTemplate(panel),
            Height = height,
            ClipToBounds = true,
            Margin = new Thickness(-7, 0, -7, 0),
            Focusable = false,
        };
        row.AddHandler(ButtonBase.ClickEvent, click);

        var shelf = new StackPanel { Margin = new Thickness(0, 34, 0, 0) };
        shelf.Children.Add(header);
        shelf.Children.Add(row);
        // An empty shelf is built but not shown.
        if (items.Count > 0) Shelves.Children.Add(shelf);
        return (header, row);
    }

    void Quick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string nav) MainWindow.Instance.SelectNav(nav);
    }
}
