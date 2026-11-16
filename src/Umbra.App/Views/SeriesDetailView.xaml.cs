using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Umbra.Core;

namespace Umbra.App;

public partial class SeriesDetailView : UserControl, IRefreshable
{
    readonly AppState S = AppState.I;
    readonly SeriesItem _series;
    SeriesDetail? _detail;
    Season? _season;
    bool _entered;

    public SeriesDetailView(SeriesItem series)
    {
        InitializeComponent();
        _series = series;
        TitleText.Text = series.Name;
        Cover.Source = Images.Load(series.Image, 500);
        Images.Ambient(Backdrop, series.Image);
        PlotText.Text = series.Plot;
        UpdateFav();
        Loaded += async (_, _) =>
        {
            Ui.I.Changed += ApplyLayout;
            DownloadManager.I.ListChanged += ApplyDownloads;
            ApplyDownloads();
            ApplyLayout();
            if (!_entered)
            {
                _entered = true;
                Motion.Enter(PosterBox, 60, 26, 560);
                Motion.Stagger(Info.Children.OfType<FrameworkElement>(), 120, 55);
            }
            await LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            Ui.I.Changed -= ApplyLayout;
            DownloadManager.I.ListChanged -= ApplyDownloads;
        };
    }

    void ApplyLayout()
    {
        var compact = Ui.I.Compact;
        PosterColumn.Width = new GridLength(compact ? 190 : 230);
        Info.Margin = new Thickness(compact ? 30 : 44, 6, 0, 0);
        TitleText.FontSize = compact ? 36 : 46;
        TitleText.LineHeight = compact ? 42 : 52;
        Page.Margin = compact ? new Thickness(28, 8, 28, 44) : new Thickness(48, 8, 48, 56);
    }

    async Task LoadAsync()
    {
        if (_detail is not null || S.Source is null) return;
        try
        {
            _detail = await S.Source.GetSeriesDetailAsync(_series, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cargar la serie", ex);
            ShowNote("No se pudo cargar la información de la serie.");
            return;
        }

        var d = _detail;
        TitleText.Text = Text.FirstNonEmpty(d.Name, _series.Name);
        if (!string.IsNullOrWhiteSpace(d.Cover)) Cover.Source = Images.Load(d.Cover, 500);
        if (!string.IsNullOrWhiteSpace(d.Backdrop)) Images.Backdrop(Backdrop, d.Backdrop);
        PlotText.Text = d.Plot;
        CastText.Text = string.Join("\n", new[]
        {
            d.Cast.Length > 0 ? L.T("Reparto: {0}", d.Cast) : "",
            d.Director.Length > 0 ? L.T("Dirección: {0}", d.Director) : "",
        }.Where(s => s.Length > 0));
        var meta = new List<string>();
        if (d.ReleaseDate.Length >= 4) meta.Add(d.ReleaseDate[..4]);
        if (d.Genre.Length > 0) meta.Add(d.Genre);
        if (d.Rating > 0) meta.Add("★ " + d.Rating.ToString("0.0", CultureInfo.InvariantCulture));
        meta.Add(d.Seasons.Count == 1 ? L.T("1 temporada") : L.T("{0} temporadas", d.Seasons.Count));
        MetaText.Text = string.Join("   ·   ", meta);

        if (d.Seasons.Count == 0) ShowNote("Esta serie no tiene episodios disponibles.");
        else LoadingPanel.Visibility = Visibility.Collapsed;
        EpisodesTitle.Visibility = d.Seasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Seasons.Visibility = d.Seasons.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Seasons.Children.Clear();
        foreach (var season in d.Seasons)
        {
            var rb = new RadioButton
            {
                Content = season.Label,
                Style = (Style)FindResource("PillToggle"),
                GroupName = "seasons",
                Tag = season,
            };
            rb.Checked += (_, _) => ShowSeason(season);
            Seasons.Children.Add(rb);
        }
        ApplyProgress();
        ApplyDownloads();
        SeasonDownloadButton.Visibility = d.Seasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SeasonDownloadText.Text = L.T(d.Seasons.Count > 1 ? "Descargar temporada" : "Descargar todo");
        var start = NextEpisode();
        var startSeason = start is null ? d.Seasons.FirstOrDefault() : d.Seasons.First(s => s.Episodes.Contains(start));
        if (startSeason is not null)
            ((RadioButton)Seasons.Children[d.Seasons.IndexOf(startSeason)]).IsChecked = true;
        UpdatePlayButton();
    }

    void ShowNote(string text)
    {
        LoadingSpinner.Visibility = Visibility.Collapsed;
        LoadingText.Margin = new Thickness(0);
        LoadingText.Text = L.T(text);
        LoadingPanel.Visibility = Visibility.Visible;
    }

    void ShowSeason(Season season)
    {
        _season = season;
        Episodes.ItemsSource = season.Episodes;
        if (IsLoaded) Motion.Enter(Episodes, 0, 14, 380);
    }

    void ApplyProgress()
    {
        if (_detail is null) return;
        foreach (var ep in _detail.Seasons.SelectMany(s => s.Episodes))
        {
            var key = AppState.EpisodeKey(ep.Id);
            ep.Watched = S.Data.Watched.Contains(key);
            var resume = S.ResumeOf(key);
            ep.Progress = resume > 0 && ep.DurationSecs > 0 ? Math.Clamp(resume / 1000.0 / ep.DurationSecs, 0.02, 1) : (resume > 0 ? 0.05 : 0);
        }
    }

    /// <summary>Links every episode with its entry in the download list (or none).</summary>
    void ApplyDownloads()
    {
        if (_detail is null) return;
        foreach (var ep in _detail.Seasons.SelectMany(s => s.Episodes))
        {
            ep.Download = S.DownloadOf(AppState.EpisodeKey(ep.Id));
            // Downloads made before the details were saved get them now.
            if (ep.Download is { Info: null } item) DownloadManager.I.SetInfo(item, InfoOf(ep));
        }
    }

    /// <summary>What is kept with an episode's download so the library can show it without the provider.</summary>
    DownloadInfo InfoOf(Episode ep) => new()
    {
        Plot = ep.Plot,
        Duration = ep.DurationText,
        Genre = _detail?.Genre ?? _series.Genre,
        Director = _detail?.Director ?? "",
        Cast = _detail?.Cast ?? "",
        ReleaseDate = Text.FirstNonEmpty(_detail?.ReleaseDate, _series.ReleaseDate),
        Rating = _detail is { Rating: > 0 } d ? d.Rating : _series.Rating,
        Poster = Text.FirstNonEmpty(_detail?.Cover, _series.Image),
        Backdrop = _detail?.Backdrop ?? "",
        SeriesPlot = Text.FirstNonEmpty(_detail?.Plot, _series.Plot),
    };

    bool Download(Episode ep, bool quiet)
    {
        if (S.Source is null) return false;
        var seriesName = TitleText.Text;
        var code = $"S{ep.Season:00}E{ep.Number:00}";
        var fileName = string.IsNullOrWhiteSpace(ep.Title) ? $"{seriesName} - {code}" : $"{seriesName} - {code} - {ep.Title}";
        return S.StartDownload(AppState.EpisodeKey(ep.Id), S.Source.GetEpisodeUrl(ep),
            L.T("{0} · T{1} E{2}", seriesName, ep.Season, ep.Number), ep.Title,
            Text.FirstNonEmpty(ep.Image, _detail?.Cover, _series.Image), seriesName, fileName, ep.Extension, isEpisode: true, quiet, InfoOf(ep));
    }

    void EpisodeDownload_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // the button sits inside the episode row, which would otherwise start playing
        if ((sender as FrameworkElement)?.Tag is not Episode ep) return;
        if (ep.Download is not null)
        {
            MainWindow.Instance.ShowDownloads();
            return;
        }
        if (Download(ep, quiet: false)) MainWindow.Instance.Toast("Añadido a descargas", "");
    }

    void SeasonDownload_Click(object sender, RoutedEventArgs e) => DownloadSeason();

    /// <summary>Queues every episode of the season on screen that is not on the download list yet.</summary>
    internal void DownloadSeason()
    {
        var season = _season ?? _detail?.Seasons.FirstOrDefault();
        if (season is null) return;
        var pending = season.Episodes.Where(ep => ep.Download is null).ToList();
        if (pending.Count == 0)
        {
            MainWindow.Instance.ShowDownloads();
            return;
        }
        var added = pending.Count(ep => Download(ep, quiet: true));
        if (added == 0)
            Sheet.Warn("No se puede descargar", "Este contenido se emite como streaming y no como un archivo, así que no se puede guardar.");
        else
            MainWindow.Instance.Toast(added == 1 ? L.T("1 episodio añadido a descargas") : L.T("{0} episodios añadidos a descargas", added), "");
    }

    /// <summary>The episode to continue: one with a resume point, otherwise the first one not watched after the last watched.</summary>
    Episode? NextEpisode()
    {
        if (_detail is null) return null;
        var all = _detail.Seasons.SelectMany(s => s.Episodes).ToList();
        if (all.Count == 0) return null;
        var resumed = all.FirstOrDefault(e => S.ResumeOf(AppState.EpisodeKey(e.Id)) > 0);
        if (resumed is not null) return resumed;
        var lastWatched = all.FindLastIndex(e => e.Watched);
        return lastWatched >= 0 && lastWatched + 1 < all.Count ? all[lastWatched + 1] : all[0];
    }

    void UpdatePlayButton()
    {
        var next = NextEpisode();
        PlayButton.IsEnabled = next is not null;
        if (next is null) return;
        var resumed = S.ResumeOf(AppState.EpisodeKey(next.Id)) > 0;
        var anyWatched = _detail!.Seasons.SelectMany(s => s.Episodes).Any(e => e.Watched);
        PlayText.Text = L.T(resumed || anyWatched ? "Continuar T{0} E{1}" : "Reproducir T{0} E{1}", next.Season, next.Number);
    }

    public void Refresh()
    {
        UpdateFav();
        if (_detail is null) return;
        ApplyProgress();
        ApplyDownloads();
        UpdatePlayButton();
    }

    void UpdateFav()
    {
        FavIcon.Text = _series.IsFavorite ? "" : "";
        FavText.Text = L.T(_series.IsFavorite ? "En favoritas" : "Favorita");
    }

    void Play(Episode episode)
    {
        if (_detail is null || S.Source is null) return;
        var seriesName = TitleText.Text;
        // The whole series is the playlist so the next episode (also across seasons) plays automatically.
        var all = _detail.Seasons.SelectMany(s => s.Episodes).ToList();
        var items = all.Select(e => new PlayItem
        {
            Title = L.T("{0} · T{1} E{2}", seriesName, e.Season, e.Number),
            Subtitle = e.Title,
            Url = S.PlayUrl(AppState.EpisodeKey(e.Id), S.Source.GetEpisodeUrl(e)),
            ResumeKey = AppState.EpisodeKey(e.Id),
            StartMs = S.ResumeOf(AppState.EpisodeKey(e.Id)),
            IsEpisode = true,
        }).ToList();
        PlayerView.Open(items, all.IndexOf(episode));
    }

    void Episode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Episode ep) Play(ep);
    }

    void PlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (NextEpisode() is { } ep) Play(ep);
    }

    void Fav_Click(object sender, RoutedEventArgs e)
    {
        S.ToggleFavorite(_series);
        UpdateFav();
        Motion.Pop(FavIcon, 0.6, 380);
        MainWindow.Instance.Toast(_series.IsFavorite ? "Añadida a favoritas" : "Quitada de favoritas", _series.IsFavorite ? "" : "");
    }

    void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.GoBack();
}
