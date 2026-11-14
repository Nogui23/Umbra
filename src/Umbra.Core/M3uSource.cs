using System.Globalization;
using System.Text.RegularExpressions;

namespace Umbra.Core;

public sealed class M3uEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public Dictionary<string, string> Attrs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Attr(string key) => Attrs.TryGetValue(key, out var v) ? v.Trim() : "";
}

public sealed class M3uPlaylist
{
    public Dictionary<string, string> Header { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<M3uEntry> Entries { get; } = new();
}

public static partial class M3uParser
{
    [GeneratedRegex(@"([A-Za-z0-9_\-]+)\s*=\s*""([^""]*)""")]
    private static partial Regex AttrRegex();

    public static M3uPlaylist Parse(TextReader reader)
    {
        var playlist = new M3uPlaylist();
        M3uEntry? pending = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in AttrRegex().Matches(line)) playlist.Header[m.Groups[1].Value] = m.Groups[2].Value;
                continue;
            }
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pending = ParseExtinf(line);
                continue;
            }
            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                if (pending is not null && pending.Attr("group-title").Length == 0) pending.Attrs["group-title"] = line[8..].Trim();
                continue;
            }
            if (line.StartsWith('#')) continue;

            var entry = pending ?? new M3uEntry();
            entry.Url = line;
            if (string.IsNullOrWhiteSpace(entry.Name)) entry.Name = entry.Attr("tvg-name").Length > 0 ? entry.Attr("tvg-name") : line;
            playlist.Entries.Add(entry);
            pending = null;
        }
        return playlist;
    }

    static M3uEntry ParseExtinf(string line)
    {
        var entry = new M3uEntry();
        // The display name follows the first comma that is not inside a quoted attribute value.
        var inQuotes = false;
        var comma = -1;
        for (var i = 8; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes) { comma = i; break; }
        }
        var meta = comma >= 0 ? line[..comma] : line;
        entry.Name = comma >= 0 ? line[(comma + 1)..].Trim() : "";
        foreach (Match m in AttrRegex().Matches(meta)) entry.Attrs[m.Groups[1].Value] = m.Groups[2].Value;
        return entry;
    }
}

/// <summary>Content from an M3U/M3U8 playlist (URL or local file), with optional XMLTV guide.</summary>
public sealed partial class M3uSource : IContentSource
{
    readonly Settings _settings;
    Dictionary<string, SeriesDetail> _series = new();
    XmltvEpg? _xmltv;

    public M3uSource(Account account, Settings settings)
    {
        Account = account;
        _settings = settings;
    }

    public Account Account { get; }
    public AccountInfo? Info => null;

    public Task<AccountInfo?> RefreshInfoAsync(CancellationToken ct) => Task.FromResult<AccountInfo?>(null);

    public async Task<ContentLibrary> LoadLibraryAsync(bool force, IProgress<string>? progress, CancellationToken ct)
    {
        var source = Account.M3uSource.Trim().Trim('"');
        if (source.Length == 0) throw new SourceException("Falta la dirección de la lista M3U.");

        string path;
        if (File.Exists(source))
        {
            path = source;
        }
        else
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new SourceException("La dirección de la lista no es válida. Usa una URL http(s) o elige un archivo .m3u.");
            path = Path.Combine(AppPaths.CacheDir(Account.Id), "playlist.m3u");
            if (force || !AppPaths.IsFresh(path, TimeSpan.FromHours(Math.Max(1, _settings.CacheHours))))
            {
                progress?.Report(L.T("Descargando la lista M3U…"));
                await Http.DownloadToFileAsync(source, path, ct);
            }
        }

        progress?.Report(L.T("Procesando la lista…"));
        var playlist = await Task.Run(() =>
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            return M3uParser.Parse(reader);
        }, ct);

        if (playlist.Entries.Count == 0)
            throw new SourceException("No se ha encontrado ningún canal en la lista. Comprueba que es una lista M3U válida.");

        var lib = Build(playlist);

        var epgUrl = Text.FirstNonEmpty(Account.EpgUrl, playlist.Header.GetValueOrDefault("url-tvg"),
            playlist.Header.GetValueOrDefault("x-tvg-url"));
        epgUrl = epgUrl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        _xmltv = epgUrl.Length > 0 ? new XmltvEpg(epgUrl, AppPaths.CacheDir(Account.Id), _settings) : null;
        if (force) _xmltv?.Invalidate();
        return lib;
    }

    [GeneratedRegex(@"^(?<show>.*?)[\s._\-]*\(?S(?<s>\d{1,2})[\s._\-]*E(?<e>\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();

    static readonly string[] VodExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".flv", ".webm", ".mpg", ".mpeg"];

    static string UrlPath(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;

    static string ExtensionOf(string url)
    {
        var ext = Path.GetExtension(UrlPath(url));
        return string.IsNullOrEmpty(ext) ? "" : ext.TrimStart('.').ToLowerInvariant();
    }

    static MediaKind Classify(M3uEntry e)
    {
        switch (e.Attr("tvg-type").ToLowerInvariant())
        {
            case "movie" or "movies" or "vod": return MediaKind.Movie;
            case "series" or "serie" or "show": return MediaKind.Series;
            case "live" or "tv": return MediaKind.Live;
        }
        var path = UrlPath(e.Url).ToLowerInvariant();
        if (path.Contains("/movie/")) return MediaKind.Movie;
        if (path.Contains("/series/")) return MediaKind.Series;
        if (path.Contains("/live/")) return MediaKind.Live;
        var isVod = VodExtensions.Any(path.EndsWith);
        if (isVod && EpisodeRegex().IsMatch(e.Name)) return MediaKind.Series;
        return isVod ? MediaKind.Movie : MediaKind.Live;
    }

    ContentLibrary Build(M3uPlaylist playlist)
    {
        var lib = new ContentLibrary();
        var cats = new Dictionary<(MediaKind, string), Category>();
        var series = new Dictionary<string, (SeriesItem Item, Dictionary<int, Season> Seasons, SeriesDetail Detail)>();
        var num = 0;

        string CategoryId(MediaKind kind, string group)
        {
            if (!cats.TryGetValue((kind, group), out var c))
            {
                c = new Category { Id = "g:" + group, Name = group, Kind = kind };
                cats[(kind, group)] = c;
                (kind switch { MediaKind.Live => lib.LiveCategories, MediaKind.Movie => lib.MovieCategories, _ => lib.SeriesCategories }).Add(c);
            }
            return c.Id;
        }

        foreach (var e in playlist.Entries)
        {
            var group = e.Attr("group-title");
            if (group.Length == 0) group = L.T("Sin categoría");
            var id = Text.ShortHash(e.Url);
            var logo = e.Attr("tvg-logo");

            switch (Classify(e))
            {
                case MediaKind.Live:
                {
                    num++;
                    var mode = e.Attr("catchup");
                    var days = int.TryParse(Text.FirstNonEmpty(e.Attr("catchup-days"), e.Attr("timeshift")), out var d) ? d : 0;
                    if (mode.Length == 0 && e.Attr("catchup-source").Length > 0) mode = "default";
                    if (mode.Length == 0 && days > 0) mode = "default";
                    lib.Channels.Add(new Channel
                    {
                        Id = id,
                        Num = int.TryParse(e.Attr("tvg-chno"), out var chno) ? chno : num,
                        Name = e.Name,
                        Logo = logo,
                        EpgId = Text.FirstNonEmpty(e.Attr("tvg-id"), e.Attr("tvg-name")),
                        CategoryId = CategoryId(MediaKind.Live, group),
                        Url = e.Url,
                        HasArchive = mode.Length > 0,
                        ArchiveDays = days > 0 ? days : (mode.Length > 0 ? 7 : 0),
                        CatchupMode = mode.Length > 0 ? mode.ToLowerInvariant() : null,
                        CatchupSource = e.Attr("catchup-source") is { Length: > 0 } cs ? cs : null,
                    });
                    break;
                }
                case MediaKind.Movie:
                    lib.Movies.Add(new Movie
                    {
                        Id = id,
                        Name = e.Name,
                        Image = logo,
                        CategoryId = CategoryId(MediaKind.Movie, group),
                        Url = e.Url,
                        Extension = Text.FirstNonEmpty(ExtensionOf(e.Url), "mp4"),
                    });
                    break;
                case MediaKind.Series:
                {
                    var m = EpisodeRegex().Match(e.Name);
                    var show = m.Success ? m.Groups["show"].Value.Trim(' ', '-', '.', '_', '(') : e.Name;
                    if (show.Length == 0) show = e.Name;
                    var seasonNo = m.Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 1;
                    var key = group + "|" + show.ToLowerInvariant();
                    if (!series.TryGetValue(key, out var entry))
                    {
                        var item = new SeriesItem
                        {
                            Id = Text.ShortHash("series:" + key),
                            Name = show,
                            Image = logo,
                            CategoryId = CategoryId(MediaKind.Series, group),
                        };
                        entry = (item, new Dictionary<int, Season>(), new SeriesDetail { Name = show, Cover = logo });
                        series[key] = entry;
                        lib.Series.Add(item);
                    }
                    if (!entry.Seasons.TryGetValue(seasonNo, out var season))
                    {
                        season = new Season { Number = seasonNo };
                        entry.Seasons[seasonNo] = season;
                    }
                    var epNo = m.Success ? int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture) : season.Episodes.Count + 1;
                    season.Episodes.Add(new Episode
                    {
                        Id = id,
                        Title = e.Name,
                        Season = seasonNo,
                        Number = epNo,
                        Image = logo,
                        Url = e.Url,
                        Extension = Text.FirstNonEmpty(ExtensionOf(e.Url), "mp4"),
                    });
                    break;
                }
            }
        }

        _series = new Dictionary<string, SeriesDetail>();
        foreach (var (item, seasons, detail) in series.Values)
        {
            foreach (var s in seasons.Values) s.Episodes.Sort((a, b) => a.Number.CompareTo(b.Number));
            detail.Seasons.AddRange(seasons.Values.OrderBy(s => s.Number));
            _series[item.Id] = detail;
        }

        lib.Finish();
        return lib;
    }

    public Task<MovieDetail> GetMovieDetailAsync(Movie movie, CancellationToken ct) =>
        Task.FromResult(new MovieDetail { Name = movie.Name, Poster = movie.Image });

    public Task<SeriesDetail> GetSeriesDetailAsync(SeriesItem series, CancellationToken ct) =>
        Task.FromResult(_series.TryGetValue(series.Id, out var d) ? d : new SeriesDetail { Name = series.Name, Cover = series.Image });

    public async Task<IReadOnlyList<EpgProgram>> GetEpgAsync(Channel channel, CancellationToken ct)
    {
        if (_xmltv is null) return Array.Empty<EpgProgram>();
        var list = await _xmltv.ForChannelAsync(channel);
        var archiveFrom = DateTime.Now.AddDays(-Math.Max(0, channel.ArchiveDays));
        return list
            .Select(p => channel.HasArchive && p.Start >= archiveFrom
                ? new EpgProgram { Title = p.Title, Description = p.Description, Start = p.Start, End = p.End, HasArchive = true }
                : p)
            .ToList();
    }

    public string GetLiveUrl(Channel channel) => channel.Url ?? "";
    public string GetMovieUrl(Movie movie) => movie.Url ?? "";
    public string GetEpisodeUrl(Episode episode) => episode.Url ?? "";

    /// <summary>Builds a catch-up URL from the playlist's catchup / catchup-source attributes.</summary>
    public string? GetCatchupUrl(Channel channel, EpgProgram program)
    {
        if (!channel.HasArchive || channel.Url is null || program.Start >= DateTime.Now) return null;
        var offset = TimeSpan.FromHours(_settings.EpgOffsetHours);
        var start = new DateTimeOffset(program.Start - offset).ToUniversalTime();
        var end = new DateTimeOffset(program.End - offset).ToUniversalTime();
        var now = DateTimeOffset.UtcNow;
        var duration = (long)(end - start).TotalSeconds;

        string Fill(string template) => template
            .Replace("${start}", start.ToUnixTimeSeconds().ToString()).Replace("{utc}", start.ToUnixTimeSeconds().ToString())
            .Replace("{start}", start.ToUnixTimeSeconds().ToString())
            .Replace("${end}", end.ToUnixTimeSeconds().ToString()).Replace("{utcend}", end.ToUnixTimeSeconds().ToString())
            .Replace("{end}", end.ToUnixTimeSeconds().ToString())
            .Replace("${now}", now.ToUnixTimeSeconds().ToString()).Replace("{lutc}", now.ToUnixTimeSeconds().ToString())
            .Replace("${timestamp}", now.ToUnixTimeSeconds().ToString())
            .Replace("${duration}", duration.ToString()).Replace("{duration}", duration.ToString())
            .Replace("${offset}", ((long)(now - start).TotalSeconds).ToString()).Replace("{offset}", ((long)(now - start).TotalSeconds).ToString())
            .Replace("{Y}", start.ToString("yyyy", CultureInfo.InvariantCulture))
            .Replace("{m}", start.ToString("MM", CultureInfo.InvariantCulture))
            .Replace("{d}", start.ToString("dd", CultureInfo.InvariantCulture))
            .Replace("{H}", start.ToString("HH", CultureInfo.InvariantCulture))
            .Replace("{M}", start.ToString("mm", CultureInfo.InvariantCulture))
            .Replace("{S}", start.ToString("ss", CultureInfo.InvariantCulture));

        var url = channel.Url;
        var mode = channel.CatchupMode ?? "default";
        if (channel.CatchupSource is { } source)
        {
            if (mode == "append" || !source.Contains("://", StringComparison.Ordinal)) return url + Fill(source);
            return Fill(source);
        }

        switch (mode)
        {
            case "shift" or "timeshift":
                return url + (url.Contains('?') ? "&" : "?") + $"utc={start.ToUnixTimeSeconds()}&lutc={now.ToUnixTimeSeconds()}";
            case "flussonic" or "fs" or "flussonic-hls" or "flussonic-ts":
                if (url.Contains("/index.m3u8")) return url.Replace("/index.m3u8", $"/index-{start.ToUnixTimeSeconds()}-{duration}.m3u8");
                if (url.Contains("/video.m3u8")) return url.Replace("/video.m3u8", $"/video-{start.ToUnixTimeSeconds()}-{duration}.m3u8");
                if (url.Contains("/mpegts")) return url.Replace("/mpegts", $"/timeshift_abs-{start.ToUnixTimeSeconds()}.ts");
                return null;
        }

        // Xtream-style live URL inside an M3U: /live/user/pass/id.ext → /timeshift/user/pass/minutes/start/id.ts
        var m = XtreamLiveRegex().Match(url);
        if (m.Success)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(duration / 60.0));
            var local = program.Start - offset;
            return $"{m.Groups["base"].Value}/timeshift/{m.Groups["u"].Value}/{m.Groups["p"].Value}/{minutes}/" +
                   $"{local.ToString("yyyy-MM-dd':'HH'-'mm", CultureInfo.InvariantCulture)}/{m.Groups["id"].Value}.ts";
        }
        return url + (url.Contains('?') ? "&" : "?") + $"utc={start.ToUnixTimeSeconds()}&lutc={now.ToUnixTimeSeconds()}";
    }

    [GeneratedRegex(@"^(?<base>https?://[^/]+(?:/[^/]+)*?)/live/(?<u>[^/]+)/(?<p>[^/]+)/(?<id>\d+)(?:\.\w+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex XtreamLiveRegex();
}
