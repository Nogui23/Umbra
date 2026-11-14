using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Umbra.Core;

namespace Umbra.App;

public partial class AccountView : UserControl, IRefreshable
{
    readonly AppState S = AppState.I;
    bool _infoRequested;

    public AccountView()
    {
        InitializeComponent();
        Loaded += (_, _) => Motion.Stagger(Page.Children.OfType<FrameworkElement>(), 30, 55);
    }

    public void Refresh()
    {
        Render(S.Source?.Info);
        var lib = S.Library;
        if (lib is not null)
        {
            ChannelsFigure.Text = lib.Channels.Count.ToString("N0");
            ChannelsNote.Text = Note("canales", lib.LiveCategories.Count);
            MoviesFigure.Text = lib.Movies.Count.ToString("N0");
            MoviesNote.Text = Note("películas", lib.MovieCategories.Count);
            SeriesFigure.Text = lib.Series.Count.ToString("N0");
            SeriesNote.Text = Note("series", lib.SeriesCategories.Count);
            WarningsText.Text = string.Join("\n", lib.Warnings);
            WarningsText.Visibility = lib.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!_infoRequested && S.Source is XtreamSource) _ = LoadInfoAsync();
    }

    static string Note(string kind, int categories) =>
        categories == 1 ? L.T(kind + " · 1 categoría") : L.T(kind + " · {0} categorías", categories);

    async Task LoadInfoAsync()
    {
        _infoRequested = true;
        SetStatus("Consultando el estado de la cuenta…", busy: true);
        try
        {
            var info = await S.Source!.RefreshInfoAsync(CancellationToken.None);
            Render(info);
            SetStatus("", busy: false);
        }
        catch (Exception ex)
        {
            SetStatus(L.T("No se pudo consultar el estado: {0}", Errors.Friendly(ex)), busy: false);
        }
    }

    void SetStatus(string text, bool busy)
    {
        StatusText.Text = L.T(text);
        StatusText.Margin = new Thickness(busy ? 9 : 0, 0, 0, 0);
        StatusSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Render(AccountInfo? info)
    {
        var a = S.Account;
        if (a is null) return;
        NameText.Text = a.Name;
        InitialText.Text = InitialConverter.Of(a.Name);
        KindText.Text = a.KindText;
        StatusPill.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        if (info is not null)
        {
            StatusPillText.Text = L.T(StatusLabel(info.Status));
            // An inactive account gets an outlined pill instead of the solid one.
            StatusPill.Background = info.IsActive ? Brushes.White : Brushes.Transparent;
            StatusPill.BorderBrush = Brushes.White;
            StatusPill.BorderThickness = new Thickness(info.IsActive ? 0 : 1);
            StatusPillText.Foreground = info.IsActive ? Brushes.Black : Brushes.White;
        }

        InfoRows.Children.Clear();
        if (a.Type == AccountType.Xtream)
        {
            Row("Servidor", a.Server);
            Row("Usuario", a.Username);
        }
        else
        {
            Row("Lista", File.Exists(a.M3uSource) ? a.M3uSource : Account.HostOf(a.M3uSource));
            if (a.EpgUrl.Length > 0) Row("Guía EPG", Account.HostOf(a.EpgUrl));
        }
        if (info is not null)
        {
            Row("Caduca", info.Expires is { } exp ? L.T("{0:dd/MM/yyyy HH:mm} · {1}", exp, DaysLeft(exp)) : L.T("Sin caducidad"));
            Row("Cuenta de prueba", L.T(info.IsTrial ? "Sí" : "No"));
            Row("Conexiones", info.MaxConnections > 0 ? L.T("{0} activas de {1}", info.ActiveConnections, info.MaxConnections) : L.T("Ilimitadas"));
            if (info.Formats.Length > 0) Row("Formatos", info.Formats);
            if (info.Timezone.Length > 0) Row("Zona horaria", info.Timezone);
            if (info.Message.Length > 0) Row("Mensaje", info.Message);
        }
        if (S.Library is { } lib) Row("Contenido actualizado", lib.LoadedAt.ToString("dd/MM/yyyy HH:mm"));
    }

    static string StatusLabel(string status) => status.ToLowerInvariant() switch
    {
        "active" => "Activa",
        "expired" => "Caducada",
        "banned" => "Bloqueada",
        "disabled" => "Desactivada",
        "" => "Estado desconocido",
        _ => status,
    };

    static string DaysLeft(DateTime exp)
    {
        var days = (int)Math.Ceiling((exp - DateTime.Now).TotalDays);
        return days < 0 ? L.T("caducada") : days == 1 ? L.T("queda 1 día") : L.T("quedan {0} días", days);
    }

    void Row(string label, string value)
    {
        if (InfoRows.Children.Count > 0)
            InfoRows.Children.Add(new Rectangle { Style = (Style)FindResource("Divider") });
        var row = new Grid { MinHeight = 50 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var name = new TextBlock { Text = L.T(label), VerticalAlignment = VerticalAlignment.Center, FontSize = 14.5 };
        var text = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 10),
            FontSize = 14.5,
            Foreground = (Brush)FindResource("MutedBrush"),
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(name);
        row.Children.Add(text);
        InfoRows.Children.Add(row);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (S.Account is null || S.Source is null) return;
        PlayerView.Shutdown();
        await MainWindow.Instance.OpenAccountAsync(S.Account, ContentSources.Create(S.Account, S.Settings), force: true);
    }

    void Switch_Click(object sender, RoutedEventArgs e)
    {
        PlayerView.Shutdown();
        S.EndSession(forgetLastAccount: true);
        MainWindow.Instance.ShowLogin();
    }
}
