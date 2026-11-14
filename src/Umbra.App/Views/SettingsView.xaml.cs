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
        Language.Select(L.Language, L.Spanish);
        HwDecoding.IsChecked = st.HardwareDecoding;
        AutoNext.IsChecked = st.AutoPlayNextEpisode;
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
        foreach (var picker in new[] { StreamFormat, Caching, EpgOffset, CacheHours, UiZoom })
            picker.SelectionChanged += (_, _) => Changed(picker, new RoutedEventArgs());
        Loaded += (_, _) => Motion.Stagger(Page.Children.OfType<FrameworkElement>(), 30, 50);
        _loading = false;
    }

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
        st.HardwareDecoding = HwDecoding.IsChecked == true;
        st.AutoPlayNextEpisode = AutoNext.IsChecked == true;
        st.AutoPlayLastChannel = AutoLast.IsChecked == true;
        st.StartFullscreen = StartFullscreen.IsChecked == true;
        S.SaveSettings();
        if (oldZoom != st.UiZoom) Ui.I.Recompute();
        if (oldOffset != st.EpgOffsetHours)
            MainWindow.Instance.Toast("El ajuste se aplicará al volver a cargar la guía de cada canal", "");
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

    void ChangePin_Click(object sender, RoutedEventArgs e)
    {
        if (!S.CheckPin("PIN actual", "Introduce el PIN actual para cambiarlo.")) return;
        var first = Sheet.AskPin("Nuevo PIN", "Escribe el nuevo PIN (de 4 a 12 cifras).");
        if (first is null) return;
        if (first.Length < 4 || !first.All(char.IsDigit))
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
