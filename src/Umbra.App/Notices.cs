using System.Diagnostics;
using System.Windows;
using Umbra.Core;

namespace Umbra.App;

/// <summary>Checks, once a day when the app opens, whether a newer version of Umbra has been published.</summary>
static class UpdateNotice
{
    static AppState S => AppState.I;

    /// <summary>The newest version seen published, when it is newer than this copy.</summary>
    public static Version? Newer =>
        Updates.Parse(S.Settings.LatestVersion) is { } latest && latest > Updates.Current ? latest : null;

    /// <summary>Asks GitHub now and remembers the answer. Throws when it cannot be asked.</summary>
    public static async Task<Updates.Release?> CheckAsync()
    {
        var release = await Task.Run(() => Updates.LatestAsync(CancellationToken.None));
        var st = S.Settings;
        st.LastUpdateCheckUtc = DateTime.UtcNow;
        st.LatestVersion = release?.Version.ToString(3) ?? "";
        st.LatestVersionUrl = release?.Url ?? "";
        S.SaveSettings();
        return release;
    }

    /// <summary>On start: checks if it is time, and says so once when a newer version appears.</summary>
    public static async void OnStartup()
    {
        var st = S.Settings;
        if (App.Quiet || !st.CheckUpdates || DateTime.UtcNow - st.LastUpdateCheckUtc < TimeSpan.FromHours(20)) return;
        var known = st.LatestVersion;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6)); // after the start, which has more urgent work
            await CheckAsync();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo comprobar si hay una versión nueva", ex);
            return;
        }
        if (Newer is { } newer && st.LatestVersion != known)
            MainWindow.Instance.Toast(L.T("Hay una versión nueva de Umbra: {0}. Está en Ajustes.", newer.ToString(3)), "");
    }

    public static void OpenPage()
    {
        var url = S.Settings.LatestVersionUrl;
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = "https://github.com/Nogui23/Umbra/releases";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Sheet.Warn("No se pudo abrir la página", ex.Message); }
    }
}

/// <summary>Windows notifications (the ones that appear at the corner of the screen), for when the app is not in front.</summary>
static class DesktopNotice
{
    static System.Windows.Forms.NotifyIcon? _icon;
    static Action? _onClick;

    /// <summary>True when the user would not see a message inside the app.</summary>
    public static bool AppHidden => Application.Current.MainWindow is not { IsActive: true, WindowState: not WindowState.Minimized };

    public static void Show(string title, string text, Action? onClick = null)
    {
        if (App.Quiet) return;
        try
        {
            if (_icon is null)
            {
                _icon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null,
                    Text = "Umbra",
                };
                _icon.BalloonTipClicked += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => _onClick?.Invoke());
            }
            _onClick = onClick;
            _icon.Visible = true;
            _icon.ShowBalloonTip(6000, title, text, System.Windows.Forms.ToolTipIcon.None);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo mostrar el aviso de Windows", ex);
        }
    }

    /// <summary>Removes the icon so it does not stay in the notification area after the app closes.</summary>
    public static void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
