using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Umbra.Core;

namespace Umbra.App;

/// <summary>The movies and episodes saved on this computer, with the ones still downloading first.</summary>
public partial class DownloadsView : UserControl, IRefreshable
{
    static DownloadManager Manager => DownloadManager.I;

    public DownloadsView()
    {
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

    public void Refresh()
    {
        var items = Manager.Items
            .OrderBy(i => i.IsCompleted)
            .ThenByDescending(i => i.IsCompleted ? i.AddedUtc : DateTime.MinValue)
            .ToList();
        List.ItemsSource = items;
        EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var done = items.Where(i => i.IsCompleted).ToList();
        var pending = items.Count - done.Count;
        var parts = new List<string>();
        if (done.Count > 0)
            parts.Add(L.T("{0} en disco · {1}", done.Count, DownloadItem.Size(done.Sum(i => i.TotalBytes))));
        if (pending > 0) parts.Add(L.T("{0} en curso", pending));
        SummaryText.Text = string.Join("   ·   ", parts);
        SummaryText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static DownloadItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as DownloadItem;

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!File.Exists(item.FilePath))
        {
            Sheet.Warn("No se encuentra el archivo", "Puede que se haya movido o borrado fuera de la app.");
            return;
        }
        PlayerView.Open(new[]
        {
            new PlayItem
            {
                Title = item.Title,
                Subtitle = item.Subtitle.Length > 0 ? item.Subtitle : null,
                Url = item.FilePath,
                ResumeKey = item.Key,
                StartMs = AppState.I.ResumeOf(item.Key),
            },
        }, 0);
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Manager.Pause(item);
    }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Manager.Resume(item);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var message = item.IsCompleted ? "El archivo se borrará del disco." : "Se cancelará y se borrará lo que ya se había descargado.";
        if (!Sheet.Confirm("¿Eliminar esta descarga?", message, "Eliminar")) return;
        Manager.Remove(item);
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
