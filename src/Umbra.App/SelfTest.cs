using System.Globalization;
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
        if (args.Length >= 5 && args[0] == "--selftest-jellyfin")
        {
            var account = new Account { Type = AccountType.Jellyfin, Server = XtreamSource.NormalizeServer(args[1]), Username = args[2], Password = args[3], Name = "Prueba Jellyfin" };
            _ = RunAsync(window, account, args[4]);
            return true;
        }
        if (args.Length >= 5 && args[0] == "--selftest-plex")
        {
            // server ("-" to find it through the account), token, address standing in for plex.tv, output folder
            PlexSource.PlexTv = args[3];
            var account = new Account { Type = AccountType.Plex, Server = args[1] == "-" ? "" : XtreamSource.NormalizeServer(args[1]), Password = args[2], Name = "Prueba Plex" };
            _ = StartPlexAsync();
            return true;

            // «user:password» instead of a token signs in first, as the form does.
            async Task StartPlexAsync()
            {
                Directory.CreateDirectory(args[4]);
                var notes = Path.Combine(args[4], "plex.txt");
                try
                {
                    if (args[2].Split(':', 2) is [var user, var password])
                    {
                        string wrong;
                        try { await PlexSource.SignInAsync(user, "mala", "", CancellationToken.None); wrong = "ACEPTADA"; }
                        catch (SourceException ex) { wrong = ex.Message; }
                        account.Username = user;
                        account.Password = await PlexSource.SignInAsync(user, password, "", CancellationToken.None);
                        File.WriteAllText(notes, $"contraseña mala: {wrong}\ninicio de sesión: token de cuenta obtenido\n");
                    }
                    var plex = new PlexSource(account, AppState.I.Settings);
                    await plex.ConnectAsync(CancellationToken.None);
                    File.AppendAllText(notes, $"servidor elegido: {plex.BaseUrl} · token del servidor distinto del de la cuenta: {plex.Token != account.Password}\n");
                    account.Server = plex.BaseUrl;
                    account.Password = plex.Token;
                }
                catch (Exception ex)
                {
                    File.AppendAllText(notes, "FALLO al conectar: " + ex.Message + "\n");
                }
                await RunAsync(window, account, args[4]);
            }
        }
        if (args.Length >= 3 && args[0] == "--selftest-tracks")
        {
            _ = TracksAsync(window, args[1], args[2]);
            return true;
        }
        if (args.Length >= 3 && args[0] == "--selftest-folder")
        {
            var account = new Account { Type = AccountType.Folder, Server = args[1], Name = "Prueba carpeta" };
            _ = RunAsync(window, account, args[2]);
            return true;
        }
        return false;
    }

    /// <summary>Plays a video with several audio and subtitle languages and checks which ones the player picks.</summary>
    static async Task TracksAsync(MainWindow window, string video, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var lines = new List<string>();
        var st = AppState.I.Profile;
        async Task Case(string audio, string subs)
        {
            st.PreferredAudio = audio;
            st.PreferredSubtitles = subs;
            PlayerView.Open(new[] { new PlayItem { Title = "Pistas", Url = video } }, 0);
            var player = PlayerView.Active!;
            await WaitForPlayback(player);
            await Task.Delay(3000);
            lines.Add($"preferido audio='{audio}' subs='{subs}' -> audio={player.AudioKey} subs={player.SubtitleKey} · {Outcome(player)}");
            lines.Add("  pistas: " + player.TrackNames);
            PlayerView.Shutdown();
            await Task.Delay(800);
        }
        try
        {
            window.ShowLogin();
            await Case("", "");
            await Case("en", "en");
            await Case("es", TrackLanguage.Off);
            await Case("en", "es");

            // Speed and sleep timer.
            PlayerView.Open(new[] { new PlayItem { Title = "Velocidad", Url = video } }, 0);
            var player = PlayerView.Active!;
            await WaitForPlayback(player);
            await Task.Delay(1000);
            player.SetSpeed(1.5f);
            await Task.Delay(1200);
            lines.Add($"velocidad 1,5x -> {player.Rate:0.00}");
            CapturePlayer(window, player, Path.Combine(outDir, "speed.png"));
            player.SetSleep(TimeSpan.FromSeconds(3), atEnd: false);
            await Task.Delay(1500);
            lines.Add($"temporizador puesto, sigue reproduciendo: {player.IsPlayingNow}");
            await Task.Delay(3500);
            lines.Add($"temporizador cumplido, reproduciendo: {player.IsPlayingNow}");
            CapturePlayer(window, player, Path.Combine(outDir, "sleep.png"));
            PlayerView.Shutdown();
        }
        catch (Exception ex)
        {
            lines.Add("FALLO: " + ex);
        }
        st.PreferredAudio = st.PreferredSubtitles = "";
        File.WriteAllLines(Path.Combine(outDir, "tracks.txt"), lines);
        Application.Current.Shutdown();
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
            foreach (var movie in lib.Movies.Take(2)) state.SaveResume(AppState.Key(MediaKind.Movie, movie.Id), 600_000);
            if (lib.Series.Count > 0) state.SetSeriesProgress(AppState.Key(MediaKind.Series, lib.Series[0].Id), "episode:x", L.T("T{0} E{1}", 2, 5));
            window.EnterShell(account);
            await Task.Delay(3200);
            Shot("02-home");
            if (state.ContinueCount > 0 && lib.Movies.Count > 0)
            {
                state.ClearProgress(AppState.Key(MediaKind.Movie, lib.Movies[0].Id));
                window.RefreshCurrent();
                await Task.Delay(900);
                Shot("02b-home-one-removed");
                var left = state.ContinueCount;
                state.ClearContinueWatching();
                window.RefreshCurrent();
                await Task.Delay(900);
                Shot("02c-home-cleared");
                Line($"seguir viendo: quedaban {left} (películas y series) tras quitar una, {state.ContinueCount} tras limpiar");
            }
            Line("pantalla NavHome: OK");

            // Profiles: each one has its own favourites; a photo is chosen in the editor.
            if (Environment.GetEnvironmentVariable("UMBRA_HOME") is { Length: > 0 } && lib.Movies.Count > 0)
            {
                var first = state.Profile;
                var movie = lib.Movies[0];
                if (!movie.IsFavorite) state.ToggleFavorite(movie);
                var ana = state.Profiles.Add("Ana");
                var picture = Path.Combine(outDir, "foto-perfil.png");
                File.WriteAllBytes(picture, TestPicture());
                var editor = new ProfileEditView(ana);
                window.Push(editor);
                await Task.Delay(1000);
                editor.UsePhoto(picture);
                await Task.Delay(600);
                Shot("02d-edit-profile");
                editor.Save_Click(editor, new RoutedEventArgs());
                Line($"perfil Ana: foto guardada={ana.HasPhoto && File.Exists(ProfileStore.PhotoPath(ana))}");
                window.SwitchProfile(ana);
                await Task.Delay(2000);
                Line($"perfil Ana: ve la favorita del otro perfil={movie.IsFavorite} (debe ser False)");
                Shot("02e-home-profile-ana");
                window.SwitchProfile(first);
                await Task.Delay(1500);
                Line($"vuelta al primer perfil: favorita={movie.IsFavorite} (debe ser True)");
                window.ShowProfiles(_ => { });
                await Task.Delay(1600);
                Shot("02f-who-is-watching");
                window.Push(new ProfilesView(manage: true, null));
                await Task.Delay(1200);
                Shot("02g-manage-profiles");
                window.EnterShell(account);
                await Task.Delay(1500);
                if (movie.IsFavorite) state.ToggleFavorite(movie);
            }

            foreach (var (nav, file) in new[] { ("NavLive", "03-live"), ("NavMovies", "04-movies"), ("NavSeries", "05-series"), ("NavAccount", "07-account"), ("NavDownloads", "07b-downloads"), ("NavSettings", "08-settings") })
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

            // Backup: a favourite taken away comes back when the copy is imported.
            if (lib.Movies.Count > 0)
            {
                var movie = lib.Movies[0];
                if (!movie.IsFavorite) state.ToggleFavorite(movie);
                var copy = Path.Combine(outDir, "copia.json");
                state.ExportBackup(copy);
                state.ToggleFavorite(movie);
                window.SelectNav("NavSettings");
                await Task.Delay(1500);
                if (window.FindName("Host") is System.Windows.Controls.ContentControl { Content: SettingsView settingsView })
                {
                    settingsView.RestoreBackup(copy);
                    await Task.Delay(1500);
                    var text = File.ReadAllText(copy);
                    Line($"copia de seguridad: favorita recuperada={movie.IsFavorite} · sin contraseñas ni enlaces={!text.Contains(account.Password.Length > 0 ? account.Password : " ") && !text.Contains("lista2.m3u") && !text.Contains("Enc\"")}");
                    Shot("08c-settings-after-restore");
                }
                if (movie.IsFavorite) state.ToggleFavorite(movie);
            }

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
                var resumeKey = AppState.Key(MediaKind.Movie, lib.Movies[0].Id);
                state.SaveResume(resumeKey, 754_000);
                window.Push(new MovieDetailView(lib.Movies[0]));
                await Task.Delay(2800);
                Shot("09-movie-detail");
                state.ClearProgress(resumeKey);
                window.RefreshCurrent();
                await Task.Delay(700);
                Shot("09b-movie-detail-forgotten");
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

                // Watching an episode to the end leaves the series in «Seguir viendo» at the next one.
                var episodes = d.Seasons.SelectMany(x => x.Episodes).Take(2).ToList();
                if (episodes.Count == 2)
                {
                    var seriesKey = AppState.Key(MediaKind.Series, lib.Series[0].Id);
                    PlayerView.Open(episodes.Select(e => new PlayItem
                    {
                        Title = e.Title,
                        Url = source.GetEpisodeUrl(e),
                        ResumeKey = AppState.EpisodeKey(e.Id),
                        IsEpisode = true,
                        SeriesKey = seriesKey,
                        EpisodeLabel = L.T("T{0} E{1}", e.Season, e.Number),
                    }).ToList(), 0);
                    var episodePlayer = PlayerView.Active!;
                    await WaitForPlayback(episodePlayer);
                    await Task.Delay(1500);
                    episodePlayer.HandleKey(Key(System.Windows.Input.Key.Right));
                    await Task.Delay(600);
                    episodePlayer.HandleKey(Key(System.Windows.Input.Key.Right));
                    await Task.Delay(1200);
                    episodePlayer.Close();
                    await Task.Delay(1200);
                    Line($"seguir viendo la serie tras acabar el episodio 1: {(state.Data.Series.TryGetValue(seriesKey, out var sp) ? sp.Label : "NO ESTÁ")}");
                    window.SelectNav("NavHome");
                    await Task.Delay(2500);
                    Shot("10b-home-series-continue");
                    window.SelectNav("NavSeries");
                }
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
                player.HandleKey(Key(System.Windows.Input.Key.Escape)); // closes the channel list
                // Typing a channel number: one of another category, then one that does not exist.
                if ((lib.Channels.Skip(1).FirstOrDefault(c => c.CategoryId != lib.Channels[0].CategoryId) ?? lib.Channels.Skip(1).FirstOrDefault()) is { } target)
                {
                    foreach (var ch in target.Num.ToString(CultureInfo.InvariantCulture))
                        PlayerView.Active!.HandleKey(Key(System.Windows.Input.Key.D0 + (ch - '0')));
                    await Task.Delay(400);
                    CapturePlayer(window, PlayerView.Active!, Path.Combine(outDir, "12b-player-number.png"));
                    await Task.Delay(1800);
                    Line($"número de canal {target.Num}: ahora suena «{PlayerView.Active?.Playing?.Title}» (esperado «{target.Name}»)");
                    foreach (var ch in "9999") PlayerView.Active!.HandleKey(Key(System.Windows.Input.Key.D0 + (ch - '0')));
                    PlayerView.Active!.HandleKey(Key(System.Windows.Input.Key.Enter));
                    await Task.Delay(500);
                    Line($"número de canal 9999 (no existe): sigue «{PlayerView.Active?.Playing?.Title}»");
                }
                player = PlayerView.Active!;
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

            // Downloads: only with an isolated data folder (UMBRA_HOME), so the user's own list is never touched.
            if (Environment.GetEnvironmentVariable("UMBRA_HOME") is { Length: > 0 } && lib.Movies.Count > 0)
            {
                var downloads = DownloadManager.I;
                downloads.Folder = Path.Combine(outDir, "descargas");
                downloads.Start(SynchronizationContext.Current);
                var m = lib.Movies[0];
                var key = AppState.Key(MediaKind.Movie, m.Id);
                state.StartDownload(key, source.GetMovieUrl(m), m.Name, "", m.Image, null, m.Name, m.Extension, isEpisode: false, quiet: true);
                if (lib.Series.Count > 0)
                {
                    window.SelectNav("NavSeries");
                    var seriesView = new SeriesDetailView(lib.Series[0]);
                    window.Push(seriesView);
                    await Task.Delay(1500);
                    seriesView.DownloadSeason();
                }
                for (var i = 0; i < 150 && downloads.HasPending; i++) await Task.Delay(200);
                await Task.Delay(800);
                Shot("15-series-downloaded");
                window.ShowDownloads();
                await Task.Delay(1800);
                Shot("16-downloads");
                window.SelectNav("NavLibrary");
                await Task.Delay(1800);
                Shot("16b-library");
                if (lib.Series.Count > 0)
                {
                    window.Push(new LibraryView(lib.Series[0].Name));
                    await Task.Delay(1500);
                    Shot("16c-library-series");
                    window.GoBack();
                }
                Line("descargas: " + string.Join(", ", downloads.Items.Select(i => $"{i.Title}={i.State}")));
                window.SelectNav("NavMovies");
                window.Push(new MovieDetailView(m));
                await Task.Delay(1500);
                Shot("17-movie-downloaded");
                window.GoBack();
                var local = state.PlayUrl(key, source.GetMovieUrl(m));
                Line("película descargada se reproduce desde: " + (File.Exists(local) ? "el archivo en disco" : "EL STREAM"));
                if (File.Exists(local))
                {
                    PlayerView.Open(new[] { new PlayItem { Title = m.Name, Url = local, ResumeKey = key } }, 0);
                    var player = PlayerView.Active!;
                    var waited = await WaitForPlayback(player);
                    await Task.Delay(1500);
                    Line($"reproducción del archivo descargado: {Outcome(player)} tras {waited} ms");
                    PlayerView.Shutdown();
                }
                window.SelectNav("NavLibrary");
                await Task.Delay(1500);
                if (window.FindName("Host") is System.Windows.Controls.ContentControl { Content: LibraryView libraryView })
                {
                    var zipPath = Path.Combine(outDir, "biblioteca.zip");
                    libraryView.BeginSelection();
                    libraryView.SelectAll();
                    await Task.Delay(700);
                    Shot("16e-library-selecting");
                    await libraryView.ExportToAsync(zipPath);
                    var countBefore = downloads.Items.Count;
                    await libraryView.ImportFromAsync(zipPath);
                    Line($"exportar: zip de {new FileInfo(zipPath).Length / 1024} KB · importar el mismo zip no duplica: {downloads.Items.Count == countBefore}");
                    foreach (var item in downloads.Items.ToList()) downloads.Remove(item);
                    await Task.Delay(1500);
                    await libraryView.ImportFromAsync(zipPath);
                    await Task.Delay(1200);
                    Shot("16f-library-imported");
                    Line("tras borrar e importar: " + string.Join(", ", downloads.Items.Select(i => $"{i.Title}={i.State}/{i.AccountId}/{File.Exists(i.FilePath)}")));
                }
                // The library without any account: from the sign-in screen, playing straight from disk.
                state.EndSession(forgetLastAccount: false);
                window.ShowLogin();
                await Task.Delay(1500);
                Shot("18-start-library");
                window.Push(new SettingsView());
                await Task.Delay(1500);
                Shot("18a-library-settings");
                window.GoBack();
                window.ShowLogin(accounts: true);
                await Task.Delay(1200);
                Shot("18b-login-library-button");
                window.ShowLibraryOffline();
                await Task.Delay(1800);
                Shot("19-library-offline");
                if (downloads.Items.FirstOrDefault(i => i.IsCompleted && !i.IsEpisode) is { } saved)
                {
                    window.Push(new LibraryDetailView(saved));
                    await Task.Delay(1800);
                    Shot("20-library-detail-offline");
                    Line($"ficha guardada con la descarga: {(saved.Info is null ? "NO" : "sí")} · imagen en disco: {File.Exists(saved.Image)}");
                    window.GoBack();
                    state.UseDataOf(saved.AccountId);
                    PlayerView.Open(new[] { new PlayItem { Title = saved.Title, Url = saved.FilePath, ResumeKey = saved.Key } }, 0);
                    var player = PlayerView.Active!;
                    var waited = await WaitForPlayback(player);
                    await Task.Delay(1500);
                    Line($"biblioteca sin cuenta, reproducción: {Outcome(player)} tras {waited} ms");
                    PlayerView.Shutdown();
                }
                foreach (var item in downloads.Items.ToList()) downloads.Remove(item);
                downloads.Shutdown();
                await Task.Delay(800);
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

    /// <summary>A wide picture (to check that the profile photo keeps the centre square).</summary>
    static byte[] TestPicture()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 70, 120)), null, new Rect(0, 0, 600, 400));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(240, 180, 60)), null, new Point(300, 200), 130, 130);
            dc.DrawRectangle(Brushes.White, null, new Rect(250, 170, 100, 60));
        }
        var bmp = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

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
