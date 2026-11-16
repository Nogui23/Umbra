using System.Windows;
using System.Windows.Threading;
using Umbra.Core;

namespace Umbra.App;

public partial class App : Application
{
    /// <summary>Developer self-test: windows stay off-screen, never take focus and no dialog is shown.</summary>
    internal static bool Quiet { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Error no controlado", a.ExceptionObject as Exception);

        Quiet = e.Args.Length > 0 && e.Args[0].StartsWith("--selftest", StringComparison.Ordinal);
        SmoothScroll.Install();
        LibVLCSharp.Shared.Core.Initialize();
        AppState.I.Load(loadAccounts: !Quiet);
        L.Set(e.Args.Contains("--en") ? L.EnglishCode : e.Args.Contains("--es") ? L.Spanish : AppState.I.Settings.Language);

        var window = new MainWindow(fullscreen: !Quiet && AppState.I.Settings.StartFullscreen);
        MainWindow = window;
        if (Quiet)
        {
            var (width, height) = SelfTest.SizeFrom(e.Args);
            window.ShowActivated = false;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.MinWidth = window.MinHeight = 0;
            window.Left = -32000;
            window.Top = 0;
            window.Width = width;
            window.Height = height;
        }
        window.Show();
        if (!Quiet) DownloadManager.I.Start(new DispatcherSynchronizationContext(Dispatcher));
        UpdateNotice.OnStartup();

        if (SelfTest.TryStart(window, e.Args)) return;

        window.Start();
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Error no controlado en la interfaz", e.Exception);
        e.Handled = true;
        // Image decoding/download failures from remote logos are harmless; everything else is reported.
        var trace = e.Exception.StackTrace ?? "";
        if (trace.Contains("System.Windows.Media.Imaging", StringComparison.Ordinal)) return;
        try
        {
            Sheet.Warn("Algo no ha ido bien", Errors.Friendly(e.Exception));
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo mostrar el aviso de error", ex);
        }
    }
}
