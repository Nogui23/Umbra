using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Umbra.Core;

namespace Umbra.App;

public partial class SettingsView : UserControl
{
    readonly AppState S = AppState.I;
    bool _loading = true;

    public SettingsView()
    {
        InitializeComponent();
        EpgOffset.SetOptions(Enumerable.Range(-12, 25).Select(h => (h == 0 ? L.T("Sin ajuste") : $"{h:+0;-0} h", h.ToString())));

        var st = S.Settings;
        StreamFormat.Select(st.StreamFormat);
        Caching.Select(st.NetworkCachingMs.ToString(), "1500");
        EpgOffset.Select(st.EpgOffsetHours.ToString(), "0");
        CacheHours.Select(st.CacheHours.ToString(), "24");
        UiZoom.Select(st.UiZoom.ToString(), "100");
        ParallelDownloads.Select(st.MaxParallelDownloads.ToString(), "1");
        DownloadFolderText.Text = DownloadManager.I.Folder;
        ReconnectDownloads.IsChecked = st.ReconnectSlowDownloads;
        NotifyDownloads.IsChecked = st.NotifyDownloads;
        CheckUpdates.IsChecked = st.CheckUpdates;
        ShowUpdate();
        Language.Select(L.Language, L.Spanish);
        HwDecoding.IsChecked = st.HardwareDecoding;
        AutoNext.IsChecked = st.AutoPlayNextEpisode;
        ShowPreferredTracks();
        AutoLast.IsChecked = st.AutoPlayLastChannel;
        StartFullscreen.IsChecked = st.StartFullscreen;
        UserAgent.Text = st.UserAgent;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = L.T("Umbra {0} · Reproductor basado en VLC (libVLC)", version?.ToString(3));

        Language.SelectionChanged += (_, _) =>
        {
            st.Language = Language.SelectedTag;
            S.SaveSettings();
            L.Set(st.Language);
            MainWindow.Instance.ReloadLanguage();
        };
        foreach (var picker in new[] { StreamFormat, Caching, EpgOffset, CacheHours, UiZoom, ParallelDownloads })
            picker.SelectionChanged += (_, _) => Changed(picker, new RoutedEventArgs());
        Loaded += (_, _) => Motion.Stagger(Page.Children.OfType<FrameworkElement>(), 30, 50);
        _loading = false;

        // Opened from the library with no account open: there is no navigation bar to leave with.
        BackButton.Visibility = S.Library is null ? Visibility.Visible : Visibility.Collapsed;
        // Without any playlist added, only what the library uses is shown; the rest would change nothing.
        if (S.Library is null && S.Accounts.Items.Count == 0) HideListSettings(Page);
    }

    static void HideListSettings(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<FrameworkElement>())
        {
            if (child.Tag as string == "list") child.Visibility = Visibility.Collapsed;
            else HideListSettings(child);
        }
    }

    void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.GoBack();

    void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var st = S.Settings;
        var oldOffset = st.EpgOffsetHours;
        var oldZoom = st.UiZoom;
        st.StreamFormat = StreamFormat.SelectedTag == "m3u8" ? "m3u8" : "ts";
        st.NetworkCachingMs = int.TryParse(Caching.SelectedTag, out var c) ? c : 1500;
        st.EpgOffsetHours = int.TryParse(EpgOffset.SelectedTag, out var o) ? o : 0;
        st.CacheHours = int.TryParse(CacheHours.SelectedTag, out var h) ? h : 24;
        st.UiZoom = int.TryParse(UiZoom.SelectedTag, out var z) ? z : 100;
        st.MaxParallelDownloads = int.TryParse(ParallelDownloads.SelectedTag, out var p) ? p : 1;
        st.ReconnectSlowDownloads = ReconnectDownloads.IsChecked == true;
        st.NotifyDownloads = NotifyDownloads.IsChecked == true;
        st.CheckUpdates = CheckUpdates.IsChecked == true;
        st.HardwareDecoding = HwDecoding.IsChecked == true;
        st.AutoPlayNextEpisode = AutoNext.IsChecked == true;
        st.AutoPlayLastChannel = AutoLast.IsChecked == true;
        st.StartFullscreen = StartFullscreen.IsChecked == true;
        S.SaveSettings();
        if (oldZoom != st.UiZoom) Ui.I.Recompute();
        if (oldOffset != st.EpgOffsetHours)
            MainWindow.Instance.Toast("El ajuste se aplicará al volver a cargar la guía de cada canal", "");
    }

    // ----- Updates -----

    void ShowUpdate(string? error = null)
    {
        var st = S.Settings;
        var current = Updates.Current.ToString(3);
        UpdateTitle.Text = L.T("Umbra {0}", current);
        if (UpdateNotice.Newer is { } newer)
        {
            UpdateTitle.Text = L.T("Hay una versión nueva: {0}", newer.ToString(3));
            UpdateNote.Text = L.T("Tienes la {0}. Descárgala desde su página de GitHub.", current);
        }
        else if (error is not null)
        {
            UpdateNote.Text = L.T("No se pudo comprobar: {0}", error);
        }
        else if (st.LastUpdateCheckUtc == default)
        {
            UpdateNote.Text = L.T("Todavía no se ha comprobado si hay versiones nuevas.");
        }
        else
        {
            UpdateNote.Text = st.LatestVersion.Length == 0
                ? L.T("Aún no hay ninguna versión publicada en GitHub. Comprobado el {0:dd/MM/yyyy HH:mm}.", st.LastUpdateCheckUtc.ToLocalTime())
                : L.T("Es la última versión. Comprobado el {0:dd/MM/yyyy HH:mm}.", st.LastUpdateCheckUtc.ToLocalTime());
        }
        UpdatePageButton.Visibility = UpdateNotice.Newer is null ? Visibility.Collapsed : Visibility.Visible;
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckButton.IsEnabled = false;
        UpdateNote.Text = L.T("Comprobando…");
        try
        {
            await UpdateNotice.CheckAsync();
            ShowUpdate();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo comprobar si hay una versión nueva", ex);
            ShowUpdate(Errors.Friendly(ex));
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
        }
    }

    void UpdatePage_Click(object sender, RoutedEventArgs e) => UpdateNotice.OpenPage();

    // ----- Backup -----

    void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Exportar copia de seguridad"),
            Filter = L.T("Copia de Umbra (*.json)|*.json"),
            FileName = L.T("Umbra copia {0:yyyy-MM-dd}.json", DateTime.Now),
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(MainWindow.Instance) != true) return;
        try
        {
            S.ExportBackup(dialog.FileName);
            MainWindow.Instance.Toast("Copia de seguridad guardada", "\uE73E");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo exportar la copia de seguridad", ex);
            Sheet.Warn("No se pudo guardar la copia", Errors.Friendly(ex));
        }
    }

    void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Importar copia de seguridad"),
            Filter = L.T("Copia de Umbra (*.json)|*.json"),
        };
        if (dialog.ShowDialog(MainWindow.Instance) != true) return;
        if (!Sheet.Confirm("¿Importar la copia?",
                "Se añadirán sus favoritos y su progreso a los tuyos, y los ajustes pasarán a ser los de la copia (menos el PIN y la carpeta de descargas).",
                "Importar")) return;
        RestoreBackup(dialog.FileName);
    }

    internal void RestoreBackup(string path)
    {
        Backup.Result result;
        try
        {
            result = S.RestoreBackup(path);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo importar la copia de seguridad", ex);
            Sheet.Warn("No se pudo importar la copia", Errors.Friendly(ex));
            return;
        }
        // The settings may change the language or the size: every screen is built again.
        L.Set(S.Settings.Language);
        Ui.I.Recompute();
        var message = result.Missing.Count == 0
            ? L.T("Se han recuperado los datos de {0} cuentas y los ajustes.", result.Restored)
            : L.T("Se han recuperado los datos de {0} cuentas y los ajustes. No están en este equipo: {1}. Añádelas y vuelve a importar la copia para recuperar las suyas.",
                result.Restored, string.Join(", ", result.Missing));
        MainWindow.Instance.ReloadLanguage();
        if (!App.Quiet) Sheet.Info("Copia importada", message);
        Log.Info("Copia importada: " + message);
    }

    void ShowPreferredTracks()
    {
        var st = S.Profile;
        var audio = st.PreferredAudio.Length > 0 ? TrackLanguage.DisplayName(st.PreferredAudio) : L.T("el que traiga el vídeo");
        var subs = st.PreferredSubtitles switch
        {
            "" => L.T("los que traiga el vídeo"),
            TrackLanguage.Off => L.T("desactivados"),
            var key => TrackLanguage.DisplayName(key),
        };
        PreferredTracksText.Text = L.T("Audio: {0} · Subtítulos: {1}. Se recuerda lo último que eliges en el reproductor.", audio, subs);
        ForgetTracksButton.IsEnabled = st.PreferredAudio.Length > 0 || st.PreferredSubtitles.Length > 0;
    }

    void ForgetTracks_Click(object sender, RoutedEventArgs e)
    {
        S.Profile.PreferredAudio = S.Profile.PreferredSubtitles = "";
        S.Profiles.Save();
        ShowPreferredTracks();
    }

    void UserAgent_LostFocus(object sender, RoutedEventArgs e)
    {
        var ua = UserAgent.Text.Trim();
        if (ua.Length == 0) ua = Settings.DefaultUserAgent;
        UserAgent.Text = ua;
        if (ua == S.Settings.UserAgent) return;
        S.Settings.UserAgent = ua;
        S.SaveSettings();
    }

    void ChangeDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Carpeta de descargas") };
        if (Directory.Exists(DownloadManager.I.Folder)) dialog.InitialDirectory = DownloadManager.I.Folder;
        if (dialog.ShowDialog(MainWindow.Instance) != true) return;
        S.Settings.DownloadFolder = dialog.FolderName;
        S.SaveSettings();
        DownloadFolderText.Text = DownloadManager.I.Folder;
        MainWindow.Instance.Toast("Las próximas descargas se guardarán en la nueva carpeta");
    }

    void ChangePin_Click(object sender, RoutedEventArgs e)
    {
        if (!S.CheckPin("PIN actual", "Introduce el PIN actual para cambiarlo.")) return;
        var first = Sheet.AskPin("Nuevo PIN", "Escribe el nuevo PIN (de 4 a 12 cifras).");
        if (first is null) return;
        if (first.Length is < 4 or > 12 || !first.All(char.IsAsciiDigit))
        {
            Sheet.Warn("PIN no válido", "El PIN debe tener entre 4 y 12 cifras.");
            return;
        }
        var second = Sheet.AskPin("Repite el nuevo PIN");
        if (second is null) return;
        if (second != first)
        {
            Sheet.Warn("Los PIN no coinciden", "Vuelve a intentarlo.");
            return;
        }
        S.Settings.ParentalPin = first;
        S.SaveSettings();
        MainWindow.Instance.Toast("PIN cambiado", "");
    }

    void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(AppPaths.CacheRoot)) Directory.Delete(dir, recursive: true);
            MainWindow.Instance.Toast("Caché borrada: el contenido se descargará de nuevo al entrar");
        }
        catch (Exception ex)
        {
            Sheet.Warn("No se pudo borrar toda la caché", ex.Message);
        }
    }

    void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });
}
