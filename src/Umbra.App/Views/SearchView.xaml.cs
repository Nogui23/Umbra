using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Umbra.Core;

namespace Umbra.App;

public partial class SearchView : UserControl
{
    const int MaxPerSection = 60;
    readonly AppState S = AppState.I;
    readonly DispatcherTimer _timer;
    List<Channel> _channels = new();

    public SearchView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _timer.Tick += (_, _) => { _timer.Stop(); Run(); };
        Query.TextChanged += (_, _) => { _timer.Stop(); _timer.Start(); };
        Loaded += (_, _) => Query.FocusBox();
    }

    /// <summary>Sets the query and searches right away (used by the self-test).</summary>
    internal void Search(string text)
    {
        Query.Text = text;
        _timer.Stop();
        Run();
    }

    void Run()
    {
        var q = Query.Text.Trim();
        var lib = S.Library;
        if (lib is null) return;
        if (q.Length < 2)
        {
            Hint("", q.Length == 0 ? "Busca en todo tu catálogo a la vez." : "Escribe al menos 2 letras.");
            Show(LiveHeader, LiveResults, "", Array.Empty<object>(), 0);
            Show(MoviesHeader, MovieResults, "", Array.Empty<object>(), 0);
            Show(SeriesHeader, SeriesResults, "", Array.Empty<object>(), 0);
            return;
        }

        var channels = lib.Channels.Where(c => !S.IsHidden(MediaKind.Live, c.CategoryId) && Text.Contains(c.Name, q)).ToList();
        var movies = lib.Movies.Where(m => !S.IsHidden(MediaKind.Movie, m.CategoryId) && Text.Contains(m.Name, q)).ToList();
        var series = lib.Series.Where(s => !S.IsHidden(MediaKind.Series, s.CategoryId) && Text.Contains(s.Name, q)).ToList();
        _channels = channels;

        Show(LiveHeader, LiveResults, "Canales", channels.Take(MaxPerSection), channels.Count);
        Show(MoviesHeader, MovieResults, "Películas", movies.Take(MaxPerSection), movies.Count);
        Show(SeriesHeader, SeriesResults, "Series", series.Take(MaxPerSection), series.Count);

        var total = channels.Count + movies.Count + series.Count;
        if (total == 0)
        {
            Hint("", L.T("No hay resultados para «{0}».", q));
        }
        else
        {
            HintPanel.Visibility = Visibility.Collapsed;
            Motion.Enter(Results, 0, 12, 340);
        }
    }

    void Hint(string glyph, string text)
    {
        HintGlyph.Text = glyph;
        HintText.Text = L.T(text);
        HintPanel.Visibility = Visibility.Visible;
    }

    static void Show(TextBlock header, ItemsControl list, string title, IEnumerable<object> items, int total)
    {
        var shown = items.ToList();
        list.ItemsSource = shown;
        header.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        header.Text = total > shown.Count ? L.T("{0} · {1:N0} (se muestran {2})", L.T(title), total, shown.Count) : $"{L.T(title)} · {total:N0}";
    }

    void Live_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: Channel channel }) return;
        PlayerView.OpenChannels(_channels, channel);
    }

    void Poster_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: PosterItem item }) return;
        MainWindow.Instance.Push(item is Movie m ? new MovieDetailView(m) : new SeriesDetailView((SeriesItem)item));
    }
}
