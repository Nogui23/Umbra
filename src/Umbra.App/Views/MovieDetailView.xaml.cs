using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Umbra.Core;

namespace Umbra.App;

public partial class MovieDetailView : UserControl, IRefreshable
{
    readonly AppState S = AppState.I;
    readonly Movie _movie;
    MovieDetail? _detail;
    bool _entered;

    public MovieDetailView(Movie movie)
    {
        InitializeComponent();
        _movie = movie;
        TitleText.Text = movie.Name;
        Poster.Source = Images.Load(movie.Image, 600);
        Images.Ambient(Backdrop, movie.Image);
        MetaText.Text = movie.RatingText is { } r ? $"★ {r}" : "";
        Refresh();
        Loaded += async (_, _) =>
        {
            Ui.I.Changed += ApplyLayout;
            ApplyLayout();
            if (!_entered)
            {
                _entered = true;
                Motion.Enter(PosterBox, 60, 26, 560);
                Motion.Stagger(Info.Children.OfType<FrameworkElement>(), 120, 55);
            }
            await LoadAsync();
        };
        Unloaded += (_, _) => Ui.I.Changed -= ApplyLayout;
    }

    void ApplyLayout()
    {
        var compact = Ui.I.Compact;
        PosterColumn.Width = new GridLength(compact ? 220 : 280);
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
            _detail = await S.Source.GetMovieDetailAsync(_movie, CancellationToken.None);
            Show(_detail);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cargar la ficha de la película", ex);
            LoadingSpinner.Visibility = Visibility.Collapsed;
            LoadingText.Margin = new Thickness(0);
            LoadingText.Text = L.T("No se pudo cargar la información de la película.");
            return;
        }
        LoadingPanel.Visibility = Visibility.Collapsed;
    }

    void Show(MovieDetail d)
    {
        TitleText.Text = Text.FirstNonEmpty(d.Name, _movie.Name);
        if (!string.IsNullOrWhiteSpace(d.Poster)) Poster.Source = Images.Load(d.Poster, 600);
        if (!string.IsNullOrWhiteSpace(d.Backdrop)) Images.Backdrop(Backdrop, d.Backdrop);
        var meta = new List<string>();
        if (d.ReleaseDate.Length >= 4) meta.Add(d.ReleaseDate[..4]);
        if (d.Duration.Length > 0) meta.Add(FormatDuration(d.Duration));
        if (d.Genre.Length > 0) meta.Add(d.Genre);
        if (d.Rating > 0) meta.Add("★ " + d.Rating.ToString("0.0", CultureInfo.InvariantCulture));
        MetaText.Text = string.Join("   ·   ", meta);
        PlotText.Text = d.Plot;
        Facts.Children.Clear();
        AddFact("Dirección", d.Director);
        AddFact("Reparto", d.Cast);
        AddFact("País", d.Country);
        AddFact("Estreno", d.ReleaseDate);
        TrailerButton.Visibility = d.Trailer.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Motion.Enter(PlotText, 0, 10, 360);
        Motion.Enter(Facts, 80, 10, 360);
    }

    static string FormatDuration(string raw)
    {
        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var t) && t.TotalMinutes >= 1)
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min";
        return int.TryParse(raw, out var minutes) ? $"{minutes} min" : raw;
    }

    void AddFact(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var name = new TextBlock { Text = L.T(label), FontSize = 13.5, Foreground = (Brush)FindResource("FaintBrush") };
        var text = new TextBlock { Text = value, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush") };
        Grid.SetColumn(text, 1);
        row.Children.Add(name);
        row.Children.Add(text);
        Facts.Children.Add(row);
    }

    string ResumeKey => AppState.Key(MediaKind.Movie, _movie.Id);

    public void Refresh()
    {
        var resume = S.ResumeOf(ResumeKey);
        ResumeButton.Visibility = resume > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResumeText.Text = L.T("Continuar desde {0}", FormatTime(resume));
        PlayButton.Style = (Style)FindResource(resume > 0 ? "SubtleButton" : "AccentButton");
        PlayButton.Padding = new Thickness(resume > 0 ? 22 : 28, 14, resume > 0 ? 22 : 28, 14);
        PlayText.Text = L.T(resume > 0 ? "Desde el principio" : "Reproducir");
        PlayIcon.Text = resume > 0 ? "" : "";
        PlayIcon.FontSize = resume > 0 ? 14 : 12;
        UpdateFav();
    }

    void UpdateFav()
    {
        FavIcon.Text = _movie.IsFavorite ? "" : "";
        FavText.Text = L.T(_movie.IsFavorite ? "En favoritas" : "Favorita");
    }

    internal static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    void Play(long startMs)
    {
        if (S.Source is null) return;
        PlayerView.Open(new[]
        {
            new PlayItem
            {
                Title = TitleText.Text,
                Url = S.Source.GetMovieUrl(_movie),
                ResumeKey = ResumeKey,
                StartMs = startMs,
            },
        }, 0);
    }

    void Play_Click(object sender, RoutedEventArgs e) => Play(0);

    void Resume_Click(object sender, RoutedEventArgs e) => Play(S.ResumeOf(ResumeKey));

    void Fav_Click(object sender, RoutedEventArgs e)
    {
        S.ToggleFavorite(_movie);
        UpdateFav();
        Motion.Pop(FavIcon, 0.6, 380);
        MainWindow.Instance.Toast(_movie.IsFavorite ? "Añadida a favoritas" : "Quitada de favoritas", _movie.IsFavorite ? "" : "");
    }

    void Trailer_Click(object sender, RoutedEventArgs e)
    {
        var t = _detail?.Trailer ?? "";
        if (t.Length == 0) return;
        var url = t.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? t : "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(t);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.GoBack();
}

static class Images
{
    static readonly UrlToImageConverter Converter = new();

    public static ImageSource? Load(string? url, int width) =>
        Converter.Convert(url, typeof(ImageSource), width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) as ImageSource;

    /// <summary>Real backdrop art, shown sharp behind the page.</summary>
    public static void Backdrop(FadeImage target, string url) => SetMono(target, url, 1600, blur: false);

    /// <summary>No backdrop art yet: the poster, heavily blurred, gives the page a soft texture.</summary>
    public static void Ambient(FadeImage target, string? posterUrl) => SetMono(target, posterUrl, 96, blur: true);

    /// <summary>Backgrounds are converted to grayscale so the interface stays monochrome; only the artwork has colour.</summary>
    static void SetMono(FadeImage target, string? url, int width, bool blur)
    {
        if (Load(url, width) is not BitmapSource bitmap) return;
        target.Tag = bitmap;
        void Apply()
        {
            if (!ReferenceEquals(target.Tag, bitmap)) return;
            try
            {
                var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
                gray.Freeze();
                target.Effect = blur
                    ? new BlurEffect { Radius = 70, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
                    : null;
                target.Source = gray;
                target.BeginAnimation(UIElement.OpacityProperty, Motion.Tween(0, 1, 700));
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo preparar la imagen de fondo", ex);
            }
        }
        if (bitmap.IsDownloading) bitmap.DownloadCompleted += (_, _) => Apply();
        else Apply();
    }
}
