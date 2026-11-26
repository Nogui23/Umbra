using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Umbra.Core;

namespace Umbra.App;

/// <summary>A poster of the library: one downloaded movie, or a series with its downloaded episodes.</summary>
public sealed class LibraryTile : ObservableObject
{
    bool _selected;
    /// <summary>Marked to go into the export.</summary>
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    public required string Name { get; init; }
    public required string Image { get; init; }
    public required string Caption { get; init; }
    public required IReadOnlyList<DownloadItem> Items { get; init; }
    public bool IsSeries { get; init; }
}

/// <summary>
/// What is saved on this computer and ready to watch: movies and, grouped by series, episodes. It plays straight
/// from disk, so it also opens from the sign-in screen, without loading any account.
/// With <c>series</c> it lists the episodes of that one series.
/// </summary>
public partial class LibraryView : UserControl, IRefreshable
{
    static DownloadManager Manager => DownloadManager.I;

    readonly string? _series;
    readonly HashSet<string> _selected = new(); // ids of the downloads chosen for export
    List<LibraryTile> _tiles = new();
    bool _selecting, _busy;

    public LibraryView(string? series = null)
    {
        _series = series;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Manager.ListChanged += Refresh;
            Manager.Finished += OnFinished;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Manager.ListChanged -= Refresh;
            Manager.Finished -= OnFinished;
        };
    }

    void OnFinished(DownloadItem item) => Refresh();

    /// <summary>Episodes are titled «Serie · T1 E2»: the series is what comes before the last separator.</summary>
    static string SeriesOf(DownloadItem item)
    {
        var cut = item.Title.LastIndexOf(" · ", StringComparison.Ordinal);
        return cut > 0 ? item.Title[..cut] : item.Title;
    }

    /// <summary>The downloads that can be watched: finished and still on disk.</summary>
    static List<DownloadItem> Ready() => Manager.Items.Where(i => i.IsCompleted && File.Exists(i.FilePath)).ToList();

    /// <summary>File names carry «S01E02», so their order is the order of the episodes.</summary>
    static List<DownloadItem> EpisodesOf(IEnumerable<DownloadItem> items, string series) => items
        .Where(i => i.IsEpisode && SeriesOf(i) == series)
        .OrderBy(i => Path.GetFileName(i.FilePath), StringComparer.OrdinalIgnoreCase)
        .ToList();

    static string SizeOf(IEnumerable<DownloadItem> items) => DownloadItem.Size(items.Sum(i => i.TotalBytes > 0 ? i.TotalBytes : i.ReceivedBytes));

    static string Count(int count, string one, string many) => count == 1 ? L.T(one) : L.T(many, count);

    public void Refresh()
    {
        var ready = Ready();
        // Outside an account, or inside a series, there is no navigation bar to leave with.
        var noAccount = AppState.I.Library is null;
        BackButton.Visibility = _series is not null || (noAccount && MainWindow.Instance.CanGoBack) ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Visibility = noAccount && _series is null ? Visibility.Visible : Visibility.Collapsed;
        // As the first screen of the app, the way to the accounts is here.
        AccountsButton.Visibility = noAccount && _series is null && !MainWindow.Instance.CanGoBack ? Visibility.Visible : Visibility.Collapsed;

        ImportButton.Visibility = ExportButton.Visibility = _series is null ? Visibility.Visible : Visibility.Collapsed;

        if (_series is not null)
        {
            var episodes = EpisodesOf(ready, _series);
            TitleText.Text = _series;
            var info = episodes.Select(e => e.Info).FirstOrDefault(i => i is not null);
            var meta = new List<string>();
            if (info is not null)
            {
                if (info.ReleaseDate.Length >= 4) meta.Add(info.ReleaseDate[..4]);
                if (info.Genre.Length > 0) meta.Add(info.Genre);
                if (info.Rating > 0) meta.Add("★ " + info.Rating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            }
            SeriesMetaText.Text = string.Join("   ·   ", meta);
            SeriesMetaText.Visibility = meta.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SeriesPlotText.Text = info?.SeriesPlot ?? "";
            SeriesPlotText.Visibility = SeriesPlotText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            SummaryText.Text = episodes.Count == 0 ? "" : Count(episodes.Count, "1 episodio", "{0} episodios") + "   ·   " + SizeOf(episodes);
            EpisodesList.ItemsSource = episodes;
            EpisodesList.Visibility = episodes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyPanel.Visibility = episodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        var movies = ready.Where(i => !i.IsEpisode).OrderByDescending(i => i.AddedUtc)
            .Select(i => new LibraryTile { Name = i.Title, Image = Text.FirstNonEmpty(i.Info?.Poster, i.Image), Caption = SizeOf([i]), Items = [i] })
            .ToList();
        var series = ready.Where(i => i.IsEpisode).GroupBy(SeriesOf)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var episodes = EpisodesOf(g, g.Key);
                return new LibraryTile
                {
                    Name = g.Key,
                    Image = Text.FirstNonEmpty(episodes.Select(e => e.Info?.Poster).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)), episodes[0].Image),
                    Caption = Count(episodes.Count, "1 episodio", "{0} episodios") + " · " + SizeOf(episodes),
                    Items = episodes,
                    IsSeries = true,
                };
            })
            .ToList();

        TitleText.Text = L.T("Biblioteca");
        var parts = new List<string>();
        if (movies.Count > 0) parts.Add(Count(movies.Count, "1 película", "{0:N0} películas"));
        if (series.Count > 0) parts.Add(Count(series.Sum(s => s.Items.Count), "1 episodio", "{0} episodios"));
        if (parts.Count > 0) parts.Add(SizeOf(ready));
        SummaryText.Text = parts.Count > 0 ? string.Join("   ·   ", parts) : L.T("Lo que has descargado, listo para ver sin conexión.");

        _tiles = movies.Concat(series).ToList();
        _selected.IntersectWith(ready.Select(i => i.Id));
        foreach (var tile in _tiles) tile.IsSelected = _selecting && tile.Items.All(i => _selected.Contains(i.Id));
        if (_selecting && ready.Count == 0) _selecting = false;
        ShowSelection();
        MoviesList.ItemsSource = movies;
        SeriesList.ItemsSource = series;
        MoviesSection.Visibility = movies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SeriesSection.Visibility = series.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = ready.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Plays <paramref name="item"/>; with a list, the rest follow as the next episodes.</summary>
    internal static void Play(IReadOnlyList<DownloadItem> list, DownloadItem item, bool fromStart = false)
    {
        if (!File.Exists(item.FilePath))
        {
            Sheet.Warn("No se encuentra el archivo", "Puede que se haya movido o borrado fuera de la app.");
            return;
        }
        // Without an open account the progress is the one of the account the file was downloaded with.
        AppState.I.UseDataOf(item.AccountId);
        var playable = list.Where(i => File.Exists(i.FilePath)).ToList();
        PlayerView.Open(playable.Select(i => new PlayItem
        {
            Title = i.Title,
            Subtitle = i.Subtitle.Length > 0 ? i.Subtitle : null,
            Url = i.FilePath,
            IsEpisode = i.IsEpisode,
            ResumeKey = i.Key,
            StartMs = fromStart && ReferenceEquals(i, item) ? 0 : AppState.I.ResumeOf(i.Key),
        }).ToList(), playable.IndexOf(item));
    }

    static T? TagOf<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.Tag as T ?? (sender as FrameworkElement)?.DataContext as T;

    void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<LibraryTile>(sender) is not { } tile) return;
        if (_selecting)
        {
            tile.IsSelected = !tile.IsSelected;
            foreach (var item in tile.Items)
            {
                if (tile.IsSelected) _selected.Add(item.Id);
                else _selected.Remove(item.Id);
            }
            ShowSelection();
            return;
        }
        if (tile.IsSeries) MainWindow.Instance.Push(new LibraryView(tile.Name));
        else MainWindow.Instance.Push(new LibraryDetailView(tile.Items[0]));
    }

    void TileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<LibraryTile>(sender) is not { } tile) return;
        var message = tile.IsSeries
            ? Count(tile.Items.Count, "Se borrará del disco 1 episodio.", "Se borrarán del disco {0} episodios.")
            : L.T("El archivo se borrará del disco.");
        if (!Sheet.Confirm(L.T("¿Eliminar «{0}»?", tile.Name), message, "Eliminar")) return;
        foreach (var item in tile.Items) Manager.Remove(item);
    }

    void Episode_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<DownloadItem>(sender) is { } item && _series is not null) Play(EpisodesOf(Ready(), _series), item);
    }

    void EpisodeDelete_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<DownloadItem>(sender) is not { } item) return;
        if (!Sheet.Confirm("¿Eliminar esta descarga?", "El archivo se borrará del disco.", "Eliminar")) return;
        Manager.Remove(item);
    }

    void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.GoBack();

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) MainWindow.Instance.Push(new SettingsView());
    }

    void Accounts_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) MainWindow.Instance.ShowLogin(accounts: true);
    }

    // ----- Export / import -----

    void ShowSelection()
    {
        SelectBar.Visibility = _selecting && !_busy ? Visibility.Visible : Visibility.Collapsed;
        SelectText.Text = L.T("Elige lo que quieres exportar");
        ExportSelectedButton.Content = L.T("Exportar ({0})", _selected.Count);
        ExportSelectedButton.IsEnabled = _selected.Count > 0;
    }

    /// <summary>Starts choosing: from here a click on a poster marks it instead of opening it.</summary>
    internal void BeginSelection()
    {
        if (_busy) return;
        if (_tiles.Count == 0)
        {
            MainWindow.Instance.Toast("No hay nada que exportar.", "");
            return;
        }
        _selecting = true;
        _selected.Clear();
        foreach (var tile in _tiles) tile.IsSelected = false;
        ShowSelection();
    }

    void EndSelection()
    {
        _selecting = false;
        _selected.Clear();
        foreach (var tile in _tiles) tile.IsSelected = false;
        ShowSelection();
    }

    internal void SelectAll()
    {
        foreach (var tile in _tiles)
        {
            tile.IsSelected = true;
            foreach (var item in tile.Items) _selected.Add(item.Id);
        }
        ShowSelection();
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selecting) EndSelection();
        else BeginSelection();
    }

    void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();

    void CancelSelect_Click(object sender, RoutedEventArgs e) => EndSelection();

    async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Exportar biblioteca"),
            Filter = L.T("Biblioteca de Umbra (*.zip)|*.zip"),
            FileName = $"Umbra {DateTime.Now:yyyy-MM-dd}.zip",
            DefaultExt = ".zip",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await ExportToAsync(dialog.FileName);
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Importar biblioteca"),
            Filter = L.T("Biblioteca de Umbra (*.zip)|*.zip"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await ImportFromAsync(dialog.FileName);
    }

    void SetBusy(bool busy, string text = "")
    {
        _busy = busy;
        Actions.IsEnabled = !busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = busy ? L.T(text, 0) : "";
        BusyProgress.Value = 0;
        ShowSelection();
    }

    IProgress<double> Reporter(string text) => new Progress<double>(p =>
    {
        if (!_busy) return;
        BusyProgress.Value = p;
        BusyText.Text = L.T(text, (int)(p * 100));
    });

    /// <summary>Writes the chosen downloads to <paramref name="path"/>.</summary>
    internal async Task ExportToAsync(string path)
    {
        if (_busy) return;
        var items = Ready().Where(i => _selected.Contains(i.Id)).ToList();
        SetBusy(true, "Exportando… {0} %");
        try
        {
            await LibraryArchive.ExportAsync(items, path, Reporter("Exportando… {0} %"), CancellationToken.None);
            SetBusy(false);
            EndSelection();
            MainWindow.Instance.Toast("Exportación terminada", "");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo exportar la biblioteca", ex);
            SetBusy(false);
            Sheet.Warn("No se pudo exportar", ex.Message);
        }
    }

    /// <summary>Adds what is in the .zip at <paramref name="path"/> to the library.</summary>
    internal async Task ImportFromAsync(string path)
    {
        if (_busy) return;
        if (_selecting) EndSelection();
        SetBusy(true, "Importando… {0} %");
        try
        {
            var result = await LibraryArchive.ImportAsync(path, Reporter("Importando… {0} %"), CancellationToken.None);
            SetBusy(false);
            MainWindow.Instance.Toast(
                result.Added == 0 ? L.T("Todo lo del archivo ya estaba en tu biblioteca")
                : result.Skipped == 0 ? L.T("Importados: {0}", result.Added)
                : L.T("Importados: {0} · ya estaban: {1}", result.Added, result.Skipped), "");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo importar la biblioteca", ex);
            SetBusy(false);
            Sheet.Warn("No se pudo importar", ex.Message);
        }
        Refresh();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Manager.Folder);
            Process.Start(new ProcessStartInfo(Manager.Folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Sheet.Warn("No se pudo abrir la carpeta", ex.Message);
        }
    }
}
