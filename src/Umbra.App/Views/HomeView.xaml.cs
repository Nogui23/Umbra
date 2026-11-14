using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Umbra.Core;

namespace Umbra.App;

/// <summary>Landing screen: greeting, the three sections and shelves with what the user is most likely to want next.</summary>
public partial class HomeView : UserControl, IRefreshable
{
    const int ShelfItems = 16;
    const double PosterCell = 190, PosterShelfHeight = 314;
    const double ChannelCell = 202, ChannelShelfHeight = 156;

    readonly AppState S = AppState.I;
    string _signature = "";

    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => Motion.Stagger(Page.Children.OfType<FrameworkElement>(), 40, 70);
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

        bool Visible(MediaKind kind, string categoryId) => !S.IsHidden(kind, categoryId);
        var channels = lib.Channels.Where(c => Visible(MediaKind.Live, c.CategoryId));
        var movies = lib.Movies.Where(m => Visible(MediaKind.Movie, m.CategoryId)).ToList();
        var series = lib.Series.Where(s => Visible(MediaKind.Series, s.CategoryId)).ToList();

        var resume = movies.Where(m => S.ResumeOf(AppState.Key(MediaKind.Movie, m.Id)) > 0).Take(ShelfItems).ToList<object>();
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
            .Select(list => string.Join(",", list.Select(o => o is Channel c ? c.Id : ((PosterItem)o).Id))));
        if (signature == _signature) return;
        _signature = signature;

        Shelves.Children.Clear();
        AddPosterShelf("Seguir viendo", resume, nameof(MainWindow.NavMovies));
        AddChannelShelf("Vistos recientemente", recent);
        AddChannelShelf("Canales favoritos", favChannels);
        AddChannelShelf("Canales", firstChannels);
        AddPosterShelf("Tus favoritas", favPosters, null);
        AddPosterShelf("Películas recién añadidas", newMovies, nameof(MainWindow.NavMovies));
        AddPosterShelf("Series recién actualizadas", newSeries, nameof(MainWindow.NavSeries));
    }

    static string Plural(int count, string one, string many) => count == 1 ? L.T(one) : L.T(many, count);

    void AddPosterShelf(string title, List<object> items, string? nav) =>
        AddShelf(title, items, "PosterTemplate", PosterCell, PosterShelfHeight, nav, (_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement { DataContext: PosterItem item }) return;
            MainWindow.Instance.Push(item is Movie m ? new MovieDetailView(m) : new SeriesDetailView((SeriesItem)item));
        });

    void AddChannelShelf(string title, List<object> items) =>
        AddShelf(title, items, "ChannelCardTemplate", ChannelCell, ChannelShelfHeight, nameof(MainWindow.NavLive), (_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement { DataContext: Channel channel }) return;
            PlayerView.OpenChannels(items.OfType<Channel>().ToList(), channel);
        });

    /// <summary>A titled single row: as many tiles as fit the width, the rest is reached through «Ver todo».</summary>
    void AddShelf(string title, List<object> items, string template, double cellWidth, double height, string? nav, RoutedEventHandler click)
    {
        if (items.Count == 0) return;

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
        Shelves.Children.Add(shelf);
    }

    void Quick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string nav) MainWindow.Instance.SelectNav(nav);
    }
}
