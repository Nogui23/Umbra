using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Umbra.Core;

namespace Umbra.App;

/// <summary>
/// The page of a downloaded movie or episode, built only from what was saved with it (details and artwork),
/// so it works with no account open and no connection.
/// </summary>
public partial class LibraryDetailView : UserControl, IRefreshable
{
    readonly AppState S = AppState.I;
    readonly DownloadItem _item;

    public LibraryDetailView(DownloadItem item)
    {
        InitializeComponent();
        _item = item;
        var info = item.Info;
        TitleText.Text = item.Title;
        SubtitleText.Text = item.Subtitle;
        SubtitleText.Visibility = item.Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var poster = Text.FirstNonEmpty(info?.Poster, item.Image);
        Poster.Source = Images.Load(poster, 600);
        if (!string.IsNullOrWhiteSpace(info?.Backdrop)) Images.Backdrop(Backdrop, info.Backdrop);
        else Images.Ambient(Backdrop, poster);

        var meta = new List<string>();
        if (info is not null)
        {
            if (info.ReleaseDate.Length >= 4) meta.Add(info.ReleaseDate[..4]);
            if (info.Duration.Length > 0) meta.Add(info.Duration);
            if (info.Genre.Length > 0) meta.Add(info.Genre);
            if (info.Rating > 0) meta.Add("★ " + info.Rating.ToString("0.0", CultureInfo.InvariantCulture));
        }
        meta.Add(DownloadItem.Size(item.TotalBytes > 0 ? item.TotalBytes : item.ReceivedBytes));
        MetaText.Text = string.Join("   ·   ", meta);

        PlotText.Text = Text.FirstNonEmpty(info?.Plot, info?.SeriesPlot);
        NoInfoText.Visibility = info is null ? Visibility.Visible : Visibility.Collapsed;
        if (info is not null)
        {
            AddFact("Dirección", info.Director);
            AddFact("Reparto", info.Cast);
            AddFact("País", info.Country);
            AddFact("Estreno", info.ReleaseDate);
        }
        Refresh();
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

    public void Refresh()
    {
        // Without an open account the progress is the one of the account the file was downloaded with.
        S.UseDataOf(_item.AccountId);
        var resume = S.ResumeOf(_item.Key);
        ResumeButton.Visibility = resume > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResumeText.Text = L.T("Continuar desde {0}", MovieDetailView.FormatTime(resume));
        PlayButton.Style = (Style)FindResource(resume > 0 ? "SubtleButton" : "AccentButton");
        PlayButton.Padding = new Thickness(resume > 0 ? 22 : 28, 14, resume > 0 ? 22 : 28, 14);
        PlayText.Text = L.T(resume > 0 ? "Desde el principio" : "Reproducir");
    }

    void Play_Click(object sender, RoutedEventArgs e) => LibraryView.Play([_item], _item, fromStart: true);

    void Resume_Click(object sender, RoutedEventArgs e) => LibraryView.Play([_item], _item);

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!Sheet.Confirm(L.T("¿Eliminar «{0}»?", _item.Title), L.T("El archivo se borrará del disco."), "Eliminar")) return;
        DownloadManager.I.Remove(_item);
        MainWindow.Instance.GoBack();
    }

    void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.GoBack();
}
