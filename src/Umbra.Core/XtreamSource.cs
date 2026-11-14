using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Umbra.Core;

public interface IContentSource
{
    Account Account { get; }
    AccountInfo? Info { get; }
    Task<AccountInfo?> RefreshInfoAsync(CancellationToken ct);
    Task<ContentLibrary> LoadLibraryAsync(bool force, IProgress<string>? progress, CancellationToken ct);
    Task<MovieDetail> GetMovieDetailAsync(Movie movie, CancellationToken ct);
    Task<SeriesDetail> GetSeriesDetailAsync(SeriesItem series, CancellationToken ct);
    Task<IReadOnlyList<EpgProgram>> GetEpgAsync(Channel channel, CancellationToken ct);
    string GetLiveUrl(Channel channel);
    string GetMovieUrl(Movie movie);
    string GetEpisodeUrl(Episode episode);
    string? GetCatchupUrl(Channel channel, EpgProgram program);
}

public static class ContentSources
{
    public static IContentSource Create(Account account, Settings settings) => account.Type switch
    {
        AccountType.Xtream => new XtreamSource(account, settings),
        _ => new M3uSource(account, settings),
    };
}

/// <summary>Client for the Xtream Codes player API (player_api.php) used by most IPTV providers.</summary>
public sealed class XtreamSource : IContentSource
{
    readonly Settings _settings;
    readonly string _user, _pass;
    XmltvEpg? _xmltv;

    public XtreamSource(Account account, Settings settings)
    {
        Account = account;
        _settings = settings;
        BaseUrl = NormalizeServer(account.Server);
        _user = account.Username.Trim();
        _pass = account.Password.Trim();
        if (!string.IsNullOrWhiteSpace(account.EpgUrl))
            _xmltv = new XmltvEpg(account.EpgUrl, AppPaths.CacheDir(account.Id), settings);
    }

    public Account Account { get; }
    public AccountInfo? Info { get; private set; }
    public string BaseUrl { get; }

    public static string NormalizeServer(string server)
    {
        var s = server.Trim();
        if (s.Length == 0) return s;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return s.TrimEnd('/');
        var path = uri.AbsolutePath;
        foreach (var tail in new[] { "player_api.php", "get.php", "xmltv.php", "panel_api.php" })
        {
            if (path.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase))
                path = path[..^(tail.Length + 1)];
        }
        return (uri.GetLeftPart(UriPartial.Authority) + path).TrimEnd('/');
    }

    /// <summary>Recognises Xtream-style playlist links (get.php?username=…&amp;password=…).</summary>
    public static bool TryParseXtreamUrl(string url, out string server, out string user, out string pass)
    {
        server = user = pass = "";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!uri.AbsolutePath.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) return false;
        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("username", out var u) || !query.TryGetValue("password", out var p)) return false;
        if (u.Length == 0 || p.Length == 0) return false;
        server = NormalizeServer(uri.GetLeftPart(UriPartial.Path));
        user = u;
        pass = p;
        return true;
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    static string E(string s) => Uri.EscapeDataString(s);

    string Api(string? action = null, string extra = "") =>
        $"{BaseUrl}/player_api.php?username={E(_user)}&password={E(_pass)}"
        + (action is null ? "" : "&action=" + action) + extra;

    static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var res = await Http.SendAsync(url, ct);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        try
        {
            return await JsonDocument.ParseAsync(stream, Json.DocOptions, ct);
        }
        catch (JsonException)
        {
            throw new SourceException("El servidor no devolvió datos válidos. Comprueba que la URL corresponde a un servidor Xtream Codes.");
        }
    }

    public async Task<AccountInfo?> RefreshInfoAsync(CancellationToken ct)
    {
        if (BaseUrl.Length == 0) throw new SourceException("Falta la dirección del servidor.");
        using var doc = await GetJsonAsync(Api(), ct);
        var root = doc.RootElement;
        var ui = root.Obj("user_info");
        if (ui.ValueKind != JsonValueKind.Object)
            throw new SourceException("La respuesta no es de un servidor Xtream Codes. Revisa la URL del servidor.");
        if (ui.Has("auth") && !ui.Bool("auth"))
            throw new SourceException("Usuario o contraseña incorrectos.");

        var formats = ui.Prop("allowed_output_formats").Arr().Select(Json.AsString).Where(f => f.Length > 0);
        var si = root.Obj("server_info");
        Info = new AccountInfo
        {
            Status = ui.Str("status"),
            Expires = Text.FromUnix(ui.Long("exp_date")),
            Created = Text.FromUnix(ui.Long("created_at")),
            IsTrial = ui.Bool("is_trial"),
            ActiveConnections = ui.Int("active_cons"),
            MaxConnections = ui.Int("max_connections"),
            Formats = string.Join(", ", formats),
            Message = ui.Str("message"),
            Timezone = si.Str("timezone"),
        };
        return Info;
    }

    public async Task<ContentLibrary> LoadLibraryAsync(bool force, IProgress<string>? progress, CancellationToken ct)
    {
        var dir = AppPaths.CacheDir(Account.Id);
        var maxAge = TimeSpan.FromHours(Math.Max(1, _settings.CacheHours));
        var lib = new ContentLibrary();

        async Task<JsonDocument> Fetch(string action, string file, string label)
        {
            var path = Path.Combine(dir, file);
            if (force || !AppPaths.IsFresh(path, maxAge))
            {
                progress?.Report(L.T("Descargando {0}…", L.T(label)));
                await Http.DownloadToFileAsync(Api(action), path, ct);
            }
            else
            {
                progress?.Report(L.T("Cargando {0}…", L.T(label)));
            }
            await using var fs = File.OpenRead(path);
            try
            {
                return await JsonDocument.ParseAsync(fs, Json.DocOptions, ct);
            }
            catch (JsonException)
            {
                fs.Close();
                File.Delete(path);
                throw new SourceException(L.T("El servidor devolvió datos no válidos al cargar {0}.", L.T(label)));
            }
        }

        // Live TV is required; movies and series are optional (some subscriptions have none).
        using (var cats = await Fetch("get_live_categories", "live_categories.json", "las categorías de TV"))
            lib.LiveCategories = ParseCategories(cats.RootElement, MediaKind.Live);
        using (var streams = await Fetch("get_live_streams", "live_streams.json", "los canales de TV en directo"))
            lib.Channels = streams.RootElement.Arr().Select(ParseChannel).Where(c => c.Id.Length > 0).ToList();

        try
        {
            using (var cats = await Fetch("get_vod_categories", "vod_categories.json", "las categorías de películas"))
                lib.MovieCategories = ParseCategories(cats.RootElement, MediaKind.Movie);
            using (var streams = await Fetch("get_vod_streams", "vod_streams.json", "las películas"))
                lib.Movies = streams.RootElement.Arr().Select(ParseMovie).Where(m => m.Id.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("No se pudieron cargar las películas", ex);
            lib.Warnings.Add(L.T("No se pudieron cargar las películas: {0}", Errors.Friendly(ex)));
        }

        try
        {
            using (var cats = await Fetch("get_series_categories", "series_categories.json", "las categorías de series"))
                lib.SeriesCategories = ParseCategories(cats.RootElement, MediaKind.Series);
            using (var series = await Fetch("get_series", "series.json", "las series"))
                lib.Series = series.RootElement.Arr().Select(ParseSeries).Where(s => s.Id.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("No se pudieron cargar las series", ex);
            lib.Warnings.Add(L.T("No se pudieron cargar las series: {0}", Errors.Friendly(ex)));
        }

        lib.Finish();
        return lib;
    }

    static List<Category> ParseCategories(JsonElement root, MediaKind kind) =>
        root.Arr()
            .Select(e => new Category { Id = e.Str("category_id"), Name = e.Str("category_name").Trim(), Kind = kind })
            .Where(c => c.Id.Length > 0)
            .ToList();

    static string CategoryOf(JsonElement e)
    {
        var id = e.Str("category_id");
        if (id.Length > 0) return id;
        // Newer panels send "category_ids": [..] instead.
        return e.Prop("category_ids").Arr().Select(Json.AsString).FirstOrDefault(s => s.Length > 0) ?? "";
    }

    static double RatingOf(JsonElement e)
    {
        var r = e.Dbl("rating");
        if (r <= 0) r = e.Dbl("rating_5based") * 2;
        return r > 10 ? 10 : r;
    }

    static Channel ParseChannel(JsonElement e) => new()
    {
        Id = e.Str("stream_id"),
        Num = e.Int("num"),
        Name = e.Str("name").Trim(),
        Logo = e.Str("stream_icon"),
        EpgId = e.Str("epg_channel_id"),
        CategoryId = CategoryOf(e),
        HasArchive = e.Bool("tv_archive"),
        ArchiveDays = e.Int("tv_archive_duration"),
    };

    static Movie ParseMovie(JsonElement e) => new()
    {
        Id = e.Str("stream_id"),
        Name = e.Str("name").Trim(),
        Image = e.Str("stream_icon"),
        Rating = RatingOf(e),
        CategoryId = CategoryOf(e),
        Added = e.Long("added"),
        Extension = Text.FirstNonEmpty(e.Str("container_extension"), "mp4"),
    };

    static SeriesItem ParseSeries(JsonElement e) => new()
    {
        Id = e.Str("series_id"),
        Name = e.Str("name").Trim(),
        Image = e.Str("cover"),
        Rating = RatingOf(e),
        CategoryId = CategoryOf(e),
        Added = Math.Max(e.Long("last_modified"), e.Long("added")),
        Plot = e.Str("plot"),
        Genre = e.Str("genre"),
        ReleaseDate = Text.FirstNonEmpty(e.Str("releaseDate"), e.Str("release_date")),
    };

    public async Task<MovieDetail> GetMovieDetailAsync(Movie movie, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(Api("get_vod_info", "&vod_id=" + E(movie.Id)), ct);
        var root = doc.RootElement;
        var info = root.Obj("info");
        var data = root.Obj("movie_data");
        var ext = data.Str("container_extension");
        if (ext.Length > 0) movie.Extension = ext;
        return new MovieDetail
        {
            Name = Text.FirstNonEmpty(info.Str("name"), data.Str("name"), movie.Name),
            Plot = Text.FirstNonEmpty(info.Str("plot"), info.Str("description")),
            Genre = info.Str("genre"),
            Director = info.Str("director"),
            Cast = Text.FirstNonEmpty(info.Str("cast"), info.Str("actors")),
            ReleaseDate = Text.FirstNonEmpty(info.Str("releasedate"), info.Str("release_date")),
            Duration = Text.FirstNonEmpty(info.Str("duration"), info.Str("episode_run_time")),
            Country = info.Str("country"),
            Rating = info.Dbl("rating") > 0 ? Math.Min(10, info.Dbl("rating")) : movie.Rating,
            Backdrop = info.FirstStr("backdrop_path"),
            Poster = Text.FirstNonEmpty(info.Str("movie_image"), info.Str("cover_big"), movie.Image),
            Trailer = info.Str("youtube_trailer"),
        };
    }

    public async Task<SeriesDetail> GetSeriesDetailAsync(SeriesItem series, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(Api("get_series_info", "&series_id=" + E(series.Id)), ct);
        var root = doc.RootElement;
        var info = root.Obj("info");

        var seasonMeta = new Dictionary<int, (string Name, string Cover)>();
        foreach (var s in root.Prop("seasons").Arr())
        {
            var n = s.Int("season_number");
            seasonMeta[n] = (s.Str("name"), Text.FirstNonEmpty(s.Str("cover_big"), s.Str("cover")));
        }

        var episodes = new List<Episode>();
        var eps = root.Prop("episodes");
        if (eps.ValueKind == JsonValueKind.Object)
        {
            foreach (var season in eps.EnumerateObject())
            {
                int.TryParse(season.Name, out var sn);
                foreach (var ep in season.Value.Arr()) episodes.Add(ParseEpisode(ep, sn));
            }
        }
        else
        {
            foreach (var item in eps.Arr())
            {
                if (item.ValueKind == JsonValueKind.Array)
                    foreach (var ep in item.EnumerateArray()) episodes.Add(ParseEpisode(ep, 0));
                else if (item.ValueKind == JsonValueKind.Object)
                    episodes.Add(ParseEpisode(item, 0));
            }
        }

        var seasons = episodes
            .Where(e => e.Id.Length > 0)
            .GroupBy(e => e.Season)
            .OrderBy(g => g.Key)
            .Select(g => new Season
            {
                Number = g.Key,
                Name = seasonMeta.TryGetValue(g.Key, out var m) ? m.Name : "",
                Cover = seasonMeta.TryGetValue(g.Key, out var m2) ? m2.Cover : "",
                Episodes = g.OrderBy(e => e.Number).ToList(),
            })
            .ToList();

        return new SeriesDetail
        {
            Name = Text.FirstNonEmpty(info.Str("name"), series.Name),
            Plot = Text.FirstNonEmpty(info.Str("plot"), series.Plot),
            Genre = Text.FirstNonEmpty(info.Str("genre"), series.Genre),
            Director = info.Str("director"),
            Cast = Text.FirstNonEmpty(info.Str("cast"), info.Str("actors")),
            ReleaseDate = Text.FirstNonEmpty(info.Str("releaseDate"), info.Str("release_date"), series.ReleaseDate),
            Rating = info.Dbl("rating") > 0 ? Math.Min(10, info.Dbl("rating")) : series.Rating,
            Backdrop = info.FirstStr("backdrop_path"),
            Cover = Text.FirstNonEmpty(info.Str("cover"), series.Image),
            Trailer = info.Str("youtube_trailer"),
            Seasons = seasons,
        };
    }

    static Episode ParseEpisode(JsonElement e, int seasonFromKey)
    {
        var info = e.Obj("info");
        var season = e.Int("season");
        var secs = info.Int("duration_secs");
        if (secs <= 0 && TimeSpan.TryParse(info.Str("duration"), CultureInfo.InvariantCulture, out var d)) secs = (int)d.TotalSeconds;
        return new Episode
        {
            Id = e.Str("id"),
            Title = Text.FirstNonEmpty(e.Str("title"), info.Str("name"), L.T("Episodio {0}", e.Int("episode_num"))),
            Season = season > 0 ? season : seasonFromKey,
            Number = e.Int("episode_num"),
            Extension = Text.FirstNonEmpty(e.Str("container_extension"), "mp4"),
            Plot = info.Str("plot"),
            Image = Text.FirstNonEmpty(info.Str("movie_image"), info.Str("cover_big")),
            DurationSecs = secs,
        };
    }

    public async Task<IReadOnlyList<EpgProgram>> GetEpgAsync(Channel channel, CancellationToken ct)
    {
        var list = new List<EpgProgram>();
        try
        {
            // The full table includes past programmes with their archive flag (needed for catch-up).
            using var doc = await GetJsonAsync(Api("get_simple_data_table", "&stream_id=" + E(channel.Id)), ct);
            list = ParseListings(doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("EPG (tabla completa) no disponible", ex);
        }

        if (list.Count == 0)
        {
            try
            {
                using var doc = await GetJsonAsync(Api("get_short_epg", "&stream_id=" + E(channel.Id) + "&limit=40"), ct);
                list = ParseListings(doc.RootElement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("EPG (corta) no disponible", ex);
            }
        }

        if (list.Count == 0 && _xmltv is not null)
            list = (await _xmltv.ForChannelAsync(channel)).ToList();

        var now = DateTime.Now;
        var from = now.AddDays(-Math.Max(1, channel.ArchiveDays));
        var to = now.AddDays(2);
        return list
            .Where(p => p.End > from && p.Start < to && p.End > p.Start)
            .GroupBy(p => p.Start)
            .Select(g => g.First())
            .OrderBy(p => p.Start)
            .ToList();
    }

    List<EpgProgram> ParseListings(JsonElement root)
    {
        var offset = TimeSpan.FromHours(_settings.EpgOffsetHours);
        var list = new List<EpgProgram>();
        foreach (var e in root.Prop("epg_listings").Arr())
        {
            DateTime start, end;
            var st = e.Long("start_timestamp");
            var et = e.Long("stop_timestamp");
            if (st > 0 && et > 0)
            {
                start = Text.FromUnix(st)!.Value;
                end = Text.FromUnix(et)!.Value;
            }
            else if (TryParseServerTime(e.Str("start"), out var s1) &&
                     TryParseServerTime(Text.FirstNonEmpty(e.Str("end"), e.Str("stop")), out var e1))
            {
                start = s1;
                end = e1;
            }
            else
            {
                continue;
            }

            list.Add(new EpgProgram
            {
                Title = DecodeBase64(e.Str("title")),
                Description = DecodeBase64(e.Str("description")),
                Start = start + offset,
                End = end + offset,
                HasArchive = e.Bool("has_archive"),
                RawStart = e.Str("start"),
            });
        }
        return list;
    }

    static bool TryParseServerTime(string s, out DateTime value) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    /// <summary>Xtream sends EPG titles and descriptions base64-encoded; plain text is returned unchanged.</summary>
    public static string DecodeBase64(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length % 4 != 0) return s;
        foreach (var ch in s)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=')) return s;
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(s)).Trim();
        }
        catch
        {
            return s;
        }
    }

    static string Ext(string? ext, string fallback) => string.IsNullOrWhiteSpace(ext) ? fallback : ext.Trim().TrimStart('.');

    public string GetLiveUrl(Channel channel) =>
        $"{BaseUrl}/live/{E(_user)}/{E(_pass)}/{channel.Id}.{(_settings.StreamFormat == "m3u8" ? "m3u8" : "ts")}";

    public string GetMovieUrl(Movie movie) =>
        $"{BaseUrl}/movie/{E(_user)}/{E(_pass)}/{movie.Id}.{Ext(movie.Extension, "mp4")}";

    public string GetEpisodeUrl(Episode episode) =>
        $"{BaseUrl}/series/{E(_user)}/{E(_pass)}/{episode.Id}.{Ext(episode.Extension, "mp4")}";

    public string? GetCatchupUrl(Channel channel, EpgProgram program)
    {
        if (!channel.HasArchive || program.Start >= DateTime.Now) return null;
        var minutes = Math.Max(1, (int)Math.Ceiling((program.End - program.Start).TotalMinutes));
        var start = TryParseServerTime(program.RawStart, out var raw)
            ? raw
            : program.Start - TimeSpan.FromHours(_settings.EpgOffsetHours);
        var startText = start.ToString("yyyy-MM-dd':'HH'-'mm", CultureInfo.InvariantCulture);
        return $"{BaseUrl}/timeshift/{E(_user)}/{E(_pass)}/{minutes}/{startText}/{channel.Id}.ts";
    }
}
