using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Core;

namespace Umbra.App;

/// <summary>
/// Developer smoke test, only active with command-line flags:
///   --selftest-xtream &lt;server&gt; &lt;user&gt; &lt;pass&gt; &lt;outDir&gt; [WIDTHxHEIGHT]
///   --selftest-m3u &lt;url-or-file&gt; &lt;outDir&gt; [WIDTHxHEIGHT]
/// It logs in without saving the account (saved accounts are not even loaded), walks every screen at the given
/// window size, plays the first channel and movie, and writes screenshots plus report.txt to outDir, then exits.
/// </summary>
static class SelfTest
{
    /// <summary>Window size for the test: a trailing «1920x1080» argument, 1600×900 by default.</summary>
    public static (double Width, double Height) SizeFrom(string[] args)
    {
        foreach (var arg in args)
        {
            var parts = arg.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 640 && h >= 400)
                return (w, h);
        }
        return (1600, 900);
    }

    public static bool TryStart(MainWindow window, string[] args)
    {
        if (args.Length >= 5 && args[0] == "--selftest-xtream")
        {
            var account = new Account { Type = AccountType.Xtream, Server = XtreamSource.NormalizeServer(args[1]), Username = args[2], Password = args[3], Name = "Prueba Xtream" };
            _ = RunAsync(window, account, args[4]);
            return true;
        }
        if (args.Length >= 3 && args[0] == "--selftest-m3u")
        {
            var account = new Account { Type = AccountType.M3U, M3uSource = args[1], Name = "Prueba M3U" };
            _ = RunAsync(window, account, args[2]);
            return true;
        }
        return false;
    }

    static async Task RunAsync(MainWindow window, Account account, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        void Line(string s) { report.AppendLine(s); File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString()); }
        void Shot(string name) => Capture(window, Path.Combine(outDir, name + ".png"));
        var state = AppState.I;
        var savedVolume = state.Settings.Volume;
        state.Settings.Volume = 0; // silent test playback
        try
        {
            Line($"ventana: {window.ActualWidth:0}x{window.ActualHeight:0} · escala {Ui.I.Scale:0.00} · lienzo {Ui.I.Width:0}x{Ui.I.Height:0} · compacto={Ui.I.Compact}");

            // Login: first the empty form, then the picker with made-up accounts (never saved).
            window.ShowLogin();
            await Task.Delay(1300);
            Shot("01-login-form");
            state.Accounts.Items.Add(new Account { Name = "Salón", Type = AccountType.Xtream, Server = "http://tv.example.com:8080", Username = "demo" });
            state.Accounts.Items.Add(new Account { Name = "Lista de casa", Type = AccountType.M3U, M3uSource = "http://listas.example.com/casa.m3u" });
            window.ShowLogin("No se pudo conectar con el servidor. Comprueba la dirección y tu conexión a Internet.");
            await Task.Delay(1300);
            Shot("01-login-picker");
            state.Accounts.Items.Clear();

            var source = ContentSources.Create(account, state.Settings);
            var info = await source.RefreshInfoAsync(CancellationToken.None);
            Line(info is null ? "info: (sin datos de cuenta)" : $"info: estado={info.Status} caduca={info.Expires} conexiones={info.ActiveConnections}/{info.MaxConnections} formatos={info.Formats}");

            var lib = await source.LoadLibraryAsync(true, null, CancellationToken.None);
            Line($"biblioteca: canales={lib.Channels.Count} catTV={lib.LiveCategories.Count} pelis={lib.Movies.Count} catPelis={lib.MovieCategories.Count} series={lib.Series.Count} catSeries={lib.SeriesCategories.Count} avisos={lib.Warnings.Count}");
            foreach (var w in lib.Warnings) Line("  aviso: " + w);

            // Show the shell without persisting the test account; a few favourites and recents fill the home shelves.
            state.SetSession(account, source, lib, persist: false);
            foreach (var c in lib.Channels.Take(3)) state.AddRecentChannel(c);
            if (lib.Channels.Count > 4) state.ToggleFavorite(lib.Channels[4]);
            if (lib.Movies.Count > 2) state.ToggleFavorite(lib.Movies[2]);
            window.EnterShell(account);
            await Task.Delay(3200);
            Shot("02-home");
            Line("pantalla NavHome: OK");

            foreach (var (nav, file) in new[] { ("NavLive", "03-live"), ("NavMovies", "04-movies"), ("NavSeries", "05-series"), ("NavAccount", "07-account"), ("NavSettings", "08-settings") })
            {
                window.SelectNav(nav);
                await Task.Delay(nav == "NavLive" ? 3500 : 2600);
                Shot(file);
                Line($"pantalla {nav}: OK");
            }

            // Switching language while the app is open: the visible screen must come back in the other language.
            var before = L.Language;
            L.Set(L.IsEnglish ? L.Spanish : L.EnglishCode);
            window.ReloadLanguage();
            await Task.Delay(1800);
            Shot("08b-language-switched");
            Line($"cambio de idioma en caliente: {before} -> {L.Language}, pantalla={window.Host.Content?.GetType().Name}");
            L.Set(before);
            window.ReloadLanguage();
            await Task.Delay(600);

            window.SelectNav("NavSearch");
            await Task.Delay(700);
            if (window.Host.Content is SearchView search) search.Search("prueba");
            await Task.Delay(2400);
            Shot("06-search");
            Line("pantalla NavSearch: OK");

            if (lib.Channels.Count > 0)
            {
                var epg = await source.GetEpgAsync(lib.Channels[0], CancellationToken.None);
                Line($"epg canal 1: {epg.Count} programas; ahora={epg.FirstOrDefault(p => p.Start <= DateTime.Now && DateTime.Now < p.End)?.Title ?? "-"}");
                var past = epg.FirstOrDefault(p => p.End < DateTime.Now);
                if (past is not null) Line("catchup url: " + (source.GetCatchupUrl(lib.Channels[0], past) is { } u ? Redact(u, account) : "(no disponible)"));
            }

            if (lib.Movies.Count > 0)
            {
                var d = await source.GetMovieDetailAsync(lib.Movies[0], CancellationToken.None);
                Line($"ficha película: {d.Name} | {d.Genre} | {d.Duration} | trama={d.Plot.Length} car.");
                window.SelectNav("NavMovies");
                window.Push(new MovieDetailView(lib.Movies[0]));
                await Task.Delay(2800);
                Shot("09-movie-detail");
                window.GoBack();
            }

            if (lib.Series.Count > 0)
            {
                var d = await source.GetSeriesDetailAsync(lib.Series[0], CancellationToken.None);
                Line($"ficha serie: {d.Name} | temporadas={d.Seasons.Count} | episodios={d.Seasons.Sum(s => s.Episodes.Count)}");
                window.SelectNav("NavSeries");
                window.Push(new SeriesDetailView(lib.Series[0]));
                await Task.Delay(2800);
                Shot("10-series-detail");
                window.GoBack();
            }

            if (lib.Channels.Count > 0)
            {
                window.SelectNav("NavLive");
                PlayerView.OpenChannels(lib.Channels, lib.Channels[0]);
                var player = PlayerView.Active!;
                var waited = await WaitForPlayback(player);
                await Task.Delay(4000);
                Line($"reproducción canal 1: {Outcome(player)} tras {waited} ms · en la misma ventana={ReferenceEquals(Window.GetWindow(player), window)}");
                CapturePlayer(window, player, Path.Combine(outDir, "11-player-live.png"));
                player.HandleKey(Key(System.Windows.Input.Key.C));
                await Task.Delay(900);
                CapturePlayer(window, player, Path.Combine(outDir, "12-player-channels.png"));
                player.Close();
                await Task.Delay(1200);
                Shot("13-after-player");
                Line($"reproductor cerrado: activo={PlayerView.IsOpen}");
            }

            if (lib.Movies.Count > 0)
            {
                var m = lib.Movies[0];
                PlayerView.Open(new[] { new PlayItem { Title = m.Name, Url = source.GetMovieUrl(m), ResumeKey = AppState.Key(MediaKind.Movie, m.Id) } }, 0);
                var player = PlayerView.Active!;
                var waited = await WaitForPlayback(player);
                await Task.Delay(3000);
                Line($"reproducción película 1: {Outcome(player)} tras {waited} ms");
                CapturePlayer(window, player, Path.Combine(outDir, "14-player-vod.png"));
                PlayerView.Shutdown();
            }

            Line("RESULTADO: completado");
        }
        catch (Exception ex)
        {
            Line("FALLO: " + ex);
        }
        finally
        {
            PlayerView.Shutdown();
            state.EndSession(forgetLastAccount: false);
            await Task.Delay(500);
            state.Settings.Volume = savedVolume;
            state.SaveSettings();
            try
            {
                // The test account is never saved; remove what the session wrote for it.
                File.Delete(Path.Combine(AppPaths.DataDir, $"userdata_{account.Id}.json"));
                var cache = Path.Combine(AppPaths.CacheRoot, account.Id);
                if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            }
            catch
            {
            }
            Application.Current.Shutdown();
        }
    }

    static async Task<int> WaitForPlayback(PlayerView player)
    {
        var waited = 0;
        while (waited < 20000 && !player.ReachedPlaying && !player.HadError)
        {
            await Task.Delay(250);
            waited += 250;
        }
        return waited;
    }

    static string Outcome(PlayerView player) =>
        player.ReachedPlaying ? "OK (reproduciendo)" : player.HadError ? "ERROR" : "sin respuesta";

    static System.Windows.Input.KeyEventArgs Key(System.Windows.Input.Key key) =>
        new(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(Application.Current.MainWindow!)!, 0, key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
        };

    static string Redact(string url, Account account) =>
        account.Password.Length > 0 ? url.Replace(Uri.EscapeDataString(account.Password), "***").Replace(account.Password, "***") : url;

    /// <summary>Renders the WPF visual tree of the window (works off-screen).</summary>
    static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var w = (int)Math.Max(1, content.ActualWidth);
        var h = (int)Math.Max(1, content.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.Resources["WindowBg"], null, new Rect(0, 0, w, h));
            dc.DrawRectangle(ExactBrush(content), null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        Save(bmp, path);
    }

    static VisualBrush ExactBrush(FrameworkElement element) => new(element)
    {
        ViewboxUnits = BrushMappingMode.Absolute,
        Viewbox = new Rect(0, 0, element.ActualWidth, element.ActualHeight),
        Stretch = Stretch.Fill,
    };

    /// <summary>Video frame via PrintWindow (works off-screen) with the WPF controls overlay drawn on top.</summary>
    static void CapturePlayer(Window window, PlayerView player, string path)
    {
        player.ShowControlsInstant();
        player.UpdateLayout();
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, hbmp);
        PrintWindow(hwnd, mem, 2); // PW_RENDERFULLCONTENT
        SelectObject(mem, old);
        var video = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        DeleteObject(hbmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);

        var overlay = player.Overlay;
        var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var origin = player.Video.PointToScreen(new Point(0, 0));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(video, new Rect(0, 0, w, h));
            dc.DrawRectangle(ExactBrush(overlay), null,
                new Rect(origin.X - r.Left, origin.Y - r.Top, overlay.ActualWidth * dpi, overlay.ActualHeight * dpi));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        Save(bmp, path);
    }

    static void Save(BitmapSource bmp, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
}
