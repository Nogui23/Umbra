using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Umbra.Core;

namespace Umbra.App;

/// <summary>Entry screen: a picker with the saved accounts, or the form to add / edit one.</summary>
public partial class LoginView : UserControl
{
    readonly AppState S = AppState.I;
    Account? _editing;

    public LoginView(string? error = null)
    {
        InitializeComponent();
        Tabs.SelectionChanged += (_, _) => ShowTab(animate: true);
        LanguageButton.Content = L.IsEnglish ? "Español" : "English";
        RefreshAccounts();
        var hasAccounts = S.Accounts.Items.Count > 0;
        if (hasAccounts) ShowPicker(animate: false);
        else ShowForm(animate: false);
        if (!string.IsNullOrWhiteSpace(error)) ShowError(error);
        Loaded += (_, _) =>
        {
            Motion.Enter(hasAccounts ? PickerPanel : FormPanel, 40, 22, 520);
            if (!hasAccounts) XServer.Focus();
        };
    }

    void RefreshAccounts()
    {
        AccountsList.ItemsSource = null;
        AccountsList.ItemsSource = S.Accounts.Items.ToList();
    }

    void ShowPicker(bool animate)
    {
        FormPanel.Visibility = Visibility.Collapsed;
        PickerPanel.Visibility = Visibility.Visible;
        if (animate) Motion.Enter(PickerPanel, 0, 16, 420);
    }

    void ShowForm(bool animate)
    {
        PickerPanel.Visibility = Visibility.Collapsed;
        FormPanel.Visibility = Visibility.Visible;
        BackToPicker.Visibility = S.Accounts.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowTab(animate: false);
        if (animate)
        {
            Motion.Enter(FormPanel, 0, 16, 420);
            XServer.Focus();
        }
    }

    void ShowTab(bool animate)
    {
        var xtream = Tabs.SelectedIndex == 0;
        XtreamForm.Visibility = xtream ? Visibility.Visible : Visibility.Collapsed;
        M3uForm.Visibility = xtream ? Visibility.Collapsed : Visibility.Visible;
        if (animate) Motion.Enter(xtream ? XtreamForm : M3uForm, 0, 10, 320);
    }

    void ShowError(string message)
    {
        ErrorText.Text = PickerErrorText.Text = L.T(message);
        ErrorBox.Visibility = PickerError.Visibility = Visibility.Visible;
        Motion.Shake(FormPanel.Visibility == Visibility.Visible ? ErrorBox : PickerError);
    }

    void HideError() => ErrorBox.Visibility = PickerError.Visibility = Visibility.Collapsed;

    void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ConnectText.Opacity = busy ? 0 : 1;
        ConnectButton.IsEnabled = !busy;
        AccountsList.IsEnabled = !busy;
        Tabs.IsEnabled = XtreamForm.IsEnabled = M3uForm.IsEnabled = !busy;
        if (busy) HideError();
    }

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        Account account;
        if (Tabs.SelectedIndex == 0)
        {
            var server = XServer.Text.Trim();
            var user = XUser.Text.Trim();
            var pass = XPass.Password;
            if (server.Length == 0 || user.Length == 0 || pass.Length == 0)
            {
                ShowError("Rellena el servidor, el usuario y la contraseña.");
                return;
            }
            var normalized = XtreamSource.NormalizeServer(server);
            account = new Account
            {
                Type = AccountType.Xtream,
                Server = normalized,
                Username = user,
                Password = pass,
                Name = Text.FirstNonEmpty(XName.Text, $"{user} · {Account.HostOf(normalized)}"),
            };
        }
        else
        {
            var source = MUrl.Text.Trim().Trim('"');
            if (source.Length == 0)
            {
                ShowError("Escribe el enlace de tu lista M3U o elige un archivo.");
                return;
            }
            var name = Text.FirstNonEmpty(MName.Text, File.Exists(source) ? Path.GetFileNameWithoutExtension(source) : Account.HostOf(source));
            if (XtreamSource.TryParseXtreamUrl(source, out var server, out var user, out var pass))
            {
                // Same provider through its Xtream API: gives movie info, series, EPG and catch-up.
                account = new Account { Type = AccountType.Xtream, Server = server, Username = user, Password = pass, Name = name, EpgUrl = MEpg.Text.Trim(), M3uSource = source };
            }
            else
            {
                account = new Account { Type = AccountType.M3U, M3uSource = source, EpgUrl = MEpg.Text.Trim(), Name = name };
            }
        }

        if (_editing is not null) account.Id = _editing.Id;
        await ConnectAsync(account);
    }

    async Task ConnectAsync(Account account)
    {
        SetBusy(true);
        try
        {
            var source = ContentSources.Create(account, S.Settings);
            if (source is XtreamSource xtream)
            {
                AccountInfo? info = null;
                try
                {
                    info = await xtream.RefreshInfoAsync(CancellationToken.None);
                }
                catch (Exception ex) when (account.M3uSource.Length > 0)
                {
                    // An Xtream-looking playlist link whose API does not answer: read it as a plain M3U instead.
                    Log.Error("La API Xtream no respondió; se usará la lista M3U", ex);
                    account.Type = AccountType.M3U;
                    source = ContentSources.Create(account, S.Settings);
                }

                if (info is { IsActive: false })
                {
                    throw new SourceException(info.Status.ToLowerInvariant() switch
                    {
                        "expired" => "Tu cuenta ha caducado. Contacta con tu proveedor.",
                        "banned" => "Tu cuenta está bloqueada por el proveedor.",
                        "disabled" => "Tu cuenta está desactivada.",
                        _ => L.T("La cuenta no está activa (estado: {0}).", info.Status),
                    });
                }
            }

            account = S.Accounts.Upsert(account);
            S.Accounts.Save();
            await MainWindow.Instance.OpenAccountAsync(account, source, force: _editing is not null);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo conectar", ex);
            SetBusy(false);
            ShowError(Errors.Friendly(ex));
        }
    }

    async void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Account account) return;
        SetBusy(true);
        await MainWindow.Instance.OpenAccountAsync(account, ContentSources.Create(account, S.Settings), force: false);
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        ResetForm();
        ShowForm(animate: true);
    }

    void BackToPicker_Click(object sender, RoutedEventArgs e)
    {
        ResetForm();
        ShowPicker(animate: true);
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Account account) return;
        ResetForm();
        _editing = account;
        FormTitle.Text = L.T("Editar cuenta");
        CancelEditButton.Visibility = Visibility.Visible;
        ConnectText.Text = L.T("Guardar y conectar");
        if (account.Type == AccountType.Xtream && account.M3uSource.Length == 0)
        {
            Tabs.SelectedIndex = 0;
            XName.Text = account.Name;
            XServer.Text = account.Server;
            XUser.Text = account.Username;
            XPass.Password = account.Password;
        }
        else
        {
            Tabs.SelectedIndex = 1;
            MName.Text = account.Name;
            MUrl.Text = account.M3uSource;
            MEpg.Text = account.EpgUrl;
        }
        ShowForm(animate: true);
    }

    void ResetForm()
    {
        _editing = null;
        FormTitle.Text = L.T("Conecta tu IPTV");
        CancelEditButton.Visibility = Visibility.Collapsed;
        ConnectText.Text = L.T("Conectar");
        foreach (var tb in new[] { XName, XServer, XUser, MName, MUrl, MEpg }) tb.Clear();
        XPass.Clear();
        HideError();
    }

    void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        ResetForm();
        if (S.Accounts.Items.Count > 0) ShowPicker(animate: true);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Account account) return;
        if (!Sheet.Confirm(L.T("¿Eliminar «{0}»?", account.Name),
                "La cuenta se quitará de este equipo junto con sus favoritos y su progreso.", "Eliminar", glyph: "")) return;
        S.Accounts.Remove(account);
        if (S.Settings.LastAccountId == account.Id)
        {
            S.Settings.LastAccountId = null;
            S.SaveSettings();
        }
        if (_editing?.Id == account.Id) ResetForm();
        RefreshAccounts();
        if (S.Accounts.Items.Count == 0) ShowForm(animate: true);
    }

    void Language_Click(object sender, RoutedEventArgs e)
    {
        S.Settings.Language = L.IsEnglish ? L.Spanish : L.EnglishCode;
        S.SaveSettings();
        L.Set(S.Settings.Language);
        MainWindow.Instance.ReloadLanguage();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Elige tu lista M3U"),
            Filter = L.T("Listas M3U (*.m3u;*.m3u8)|*.m3u;*.m3u8|Todos los archivos (*.*)|*.*"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) MUrl.Text = dialog.FileName;
    }
}
