using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Umbra.Core;

/// <summary>
/// Movies and series from a Jellyfin or Emby server (both speak the same API). Files are played and downloaded
/// as they are stored, without asking the server to convert them.
/// </summary>
public sealed class JellyfinSource : IContentSource
{
    const string Fields = "Genres,Overview,DateCreated,MediaSources";

    readonly string _user, _pass, _deviceId;
    string _base, _token = "", _userId = "";
    Dictionary<string, SeriesItem> _seriesById = new();

    public JellyfinSource(Account account, Settings settings)
    {
        Account = account;
        _base = XtreamSource.NormalizeServer(account.Server);
        _user = account.Username.Trim();
        _pass = account.Password;
        _deviceId = Text.ShortHash(Environment.MachineName + "|umbra");
    }

    public Account Account { get; }
    public AccountInfo? Info => null;
    public Task<AccountInfo?> RefreshInfoAsync(CancellationToken ct) => Task.FromResult<AccountInfo?>(null);

    static string E(string s) => Uri.EscapeDataString(s);

    HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", Http.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var identity = $"MediaBrowser Client=\"Umbra\", Device=\"PC\", DeviceId=\"{_deviceId}\", Version=\"1.0\"";
        if (_token.Length > 0) identity += $", Token=\"{_token}\"";
        // Jellyfin reads the first, Emby the second.
        request.Headers.TryAddWithoutValidation("Authorization", identity);
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", identity);
        if (_token.Length > 0) request.Headers.TryAddWithoutValidation("X-Emby-Token", _token);
        return request;
    }

    /// <summary>Signs in with the user and password; throws with a readable message when the server says no.</summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        if (_base.Length == 0) throw new SourceException("Falta la dirección del servidor.");
        if (_user.Length == 0) throw new SourceException("Falta el usuario.");
        _token = "";
        // Emby serves its API under /emby; Jellyfin at the root.
        var bases = _base.EndsWith("/emby", StringComparison.OrdinalIgnoreCase) ? new[] { _base } : new[] { _base, _base + "/emby" };
        HttpStatusCode last = 0;
        foreach (var candidate in bases)
        {
            using var request = Request(HttpMethod.Post, candidate + "/Users/AuthenticateByName");
            request.Content = new StringContent(JsonSerializer.Serialize(new { Username = _user, Pw = _pass }), Encoding.UTF8, "application/json");
            using var response = await Http.Client.SendAsync(request, ct);
            last = response.StatusCode;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SourceException("Usuario o contraseña incorrectos.");
            if (!response.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct), Json.DocOptions);
            var token = doc.RootElement.Str("AccessToken");
            var userId = doc.RootElement.Obj("User").Str("Id");
            if (token.Length == 0 || userId.Length == 0) continue;
            (_base, _token, _userId) = (candidate, token, userId);
            return;
        }
        throw new SourceException(last == HttpStatusCode.NotFound || last == 0
            ? "La dirección no parece ser de un servidor Jellyfin o Emby. Revisa la dirección y el puerto."
            : L.T("El servidor respondió con un error {0} ({1}).", (int)last, last));
    }

    async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, _base + path);
        using var response = await Http.Client.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SourceException("El servidor ha rechazado el acceso. Revisa el usuario y la contraseña.");
        if (!response.IsSuccessStatusCode)
            throw new SourceException(L.T("El servidor respondió con un error {0} ({1}).", (int)response.StatusCode, response.ReasonPhrase));
        try
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct), Json.DocOptions);
        }
        catch (JsonException)
        {
            throw new SourceException("El servidor no devolvió datos válidos.");
        }
    }

    string ImageUrl(string itemId, string kind, int width) => $"{_base}/Items/{E(itemId)}/Images/{kind}?maxWidth={width}&quality=90";

    string Poster(JsonElement item) => item.Obj("ImageTags").Has("Primary") ? ImageUrl(item.Str("Id"), "Primary", 600) : "";

    /// <summary>The file itself, as stored on the server.</summary>
    string StreamUrl(string itemId, string extension) =>
        $"{_base}/Videos/{E(itemId)}/stream.{extension}?static=true&api_key={E(_token)}&ApiKey={E(_token)}";

    static string ExtensionOf(JsonElement item)
    {
        var container = item.Str("Container");
        if (container.Length == 0) container = item.Prop("MediaSources").Arr().Select(s => s.Str("Container")).FirstOrDefault(c => c.Length > 0) ?? "";
        var first = container.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return first.Length is > 0 and <= 5 && first.All(char.IsAsciiLetterOrDigit) ? first.ToLowerInvariant() : "mkv";
    }

    static long Added(JsonElement item) =>
        DateTimeOffset.TryParse(item.Str("DateCreated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUnixTimeSeconds() : 0;

    static string Date(JsonElement item)
    {
        var raw = item.Str("PremiereDate");
        if (raw.Length >= 10) return raw[..10];
        var year = item.Int("ProductionYear");
        return year > 0 ? year.ToString(CultureInfo.InvariantCulture) : "";
    }

    static string People(JsonElement item, string type) => string.Join(", ",
        item.Prop("People").Arr().Where(p => p.Str("Type").Equals(type, StringComparison.OrdinalIgnoreCase)).Select(p => p.Str("Name")).Where(n => n.Length > 0).Take(8));

    static string Duration(JsonElement item)
    {
        var minutes = item.Long("RunTimeTicks") / 600_000_000;
        return minutes > 0 ? minutes.ToString(CultureInfo.InvariantCulture) : "";
    }

    public async Task<ContentLibrary> LoadLibraryAsync(bool force, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(L.T("Conectando con el servidor…"));
        await SignInAsync(ct);
        var lib = new ContentLibrary();

        string CategoryOf(JsonElement item, MediaKind kind, List<Category> list)
        {
            var genre = item.Prop("Genres").Arr().Select(Json.AsString).FirstOrDefault(g => g.Length > 0) ?? L.T("Sin categoría");
            var id = "g:" + genre;
            if (list.All(c => c.Id != id)) list.Add(new Category { Id = id, Name = genre, Kind = kind });
            return id;
        }

        progress?.Report(L.T("Cargando películas…"));
        using (var movies = await GetAsync($"/Users/{E(_userId)}/Items?IncludeItemTypes=Movie&Recursive=true&SortBy=SortName&Fields={Fields}", ct))
        {
            foreach (var item in movies.RootElement.Prop("Items").Arr())
            {
                var id = item.Str("Id");
                if (id.Length == 0) continue;
                var ext = ExtensionOf(item);
                lib.Movies.Add(new Movie
                {
                    Id = id,
                    Name = item.Str("Name"),
                    Image = Poster(item),
                    Rating = item.Dbl("CommunityRating"),
                    Added = Added(item),
                    CategoryId = CategoryOf(item, MediaKind.Movie, lib.MovieCategories),
                    Extension = ext,
                    Url = StreamUrl(id, ext),
                });
            }
        }

        progress?.Report(L.T("Cargando series…"));
        _seriesById = new Dictionary<string, SeriesItem>();
        using (var series = await GetAsync($"/Users/{E(_userId)}/Items?IncludeItemTypes=Series&Recursive=true&SortBy=SortName&Fields={Fields}", ct))
        {
            foreach (var item in series.RootElement.Prop("Items").Arr())
            {
                var id = item.Str("Id");
                if (id.Length == 0) continue;
                var show = new SeriesItem
                {
                    Id = id,
                    Name = item.Str("Name"),
                    Image = Poster(item),
                    Rating = item.Dbl("CommunityRating"),
                    Added = Added(item),
                    CategoryId = CategoryOf(item, MediaKind.Series, lib.SeriesCategories),
                    Plot = item.Str("Overview"),
                    Genre = string.Join(", ", item.Prop("Genres").Arr().Select(Json.AsString)),
                    ReleaseDate = Date(item),
                };
                lib.Series.Add(show);
                _seriesById[id] = show;
            }
        }

        lib.MovieCategories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        lib.SeriesCategories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        lib.Finish();
        return lib;
    }

    public async Task<MovieDetail> GetMovieDetailAsync(Movie movie, CancellationToken ct)
    {
        if (_token.Length == 0) await SignInAsync(ct);
        using var doc = await GetAsync($"/Users/{E(_userId)}/Items/{E(movie.Id)}", ct);
        var item = doc.RootElement;
        return new MovieDetail
        {
            Name = Text.FirstNonEmpty(item.Str("Name"), movie.Name),
            Plot = item.Str("Overview"),
            Genre = string.Join(", ", item.Prop("Genres").Arr().Select(Json.AsString)),
            Director = People(item, "Director"),
            Cast = People(item, "Actor"),
            ReleaseDate = Date(item),
            Duration = Duration(item),
            Country = string.Join(", ", item.Prop("ProductionLocations").Arr().Select(Json.AsString)),
            Rating = item.Dbl("CommunityRating"),
            Poster = Text.FirstNonEmpty(Poster(item), movie.Image),
            Backdrop = item.Prop("BackdropImageTags").Arr().Any() ? ImageUrl(movie.Id, "Backdrop", 1600) : "",
            Trailer = item.Prop("RemoteTrailers").Arr().Select(t => t.Str("Url")).FirstOrDefault(u => u.Length > 0) ?? "",
        };
    }

    public async Task<SeriesDetail> GetSeriesDetailAsync(SeriesItem series, CancellationToken ct)
    {
        if (_token.Length == 0) await SignInAsync(ct);
        using var showDoc = await GetAsync($"/Users/{E(_userId)}/Items/{E(series.Id)}", ct);
        var show = showDoc.RootElement;
        var detail = new SeriesDetail
        {
            Name = Text.FirstNonEmpty(show.Str("Name"), series.Name),
            Plot = Text.FirstNonEmpty(show.Str("Overview"), series.Plot),
            Genre = string.Join(", ", show.Prop("Genres").Arr().Select(Json.AsString)),
            Director = People(show, "Director"),
            Cast = People(show, "Actor"),
            ReleaseDate = Date(show),
            Rating = show.Dbl("CommunityRating"),
            Cover = Text.FirstNonEmpty(Poster(show), series.Image),
            Backdrop = show.Prop("BackdropImageTags").Arr().Any() ? ImageUrl(series.Id, "Backdrop", 1600) : "",
            Trailer = show.Prop("RemoteTrailers").Arr().Select(t => t.Str("Url")).FirstOrDefault(u => u.Length > 0) ?? "",
        };

        using var episodesDoc = await GetAsync($"/Shows/{E(series.Id)}/Episodes?UserId={E(_userId)}&Fields=Overview,MediaSources", ct);
        var seasons = new SortedDictionary<int, Season>();
        foreach (var item in episodesDoc.RootElement.Prop("Items").Arr())
        {
            var id = item.Str("Id");
            // Episodes the server only knows about (not on disk) cannot be played.
            if (id.Length == 0 || item.Str("LocationType").Equals("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            var seasonNo = item.Has("ParentIndexNumber") ? item.Int("ParentIndexNumber") : 1;
            if (!seasons.TryGetValue(seasonNo, out var season))
            {
                season = new Season { Number = seasonNo, Name = seasonNo == 0 ? Text.FirstNonEmpty(item.Str("SeasonName"), L.T("Especiales")) : "" };
                seasons[seasonNo] = season;
            }
            var ext = ExtensionOf(item);
            season.Episodes.Add(new Episode
            {
                Id = id,
                Title = item.Str("Name"),
                Season = seasonNo,
                Number = item.Has("IndexNumber") ? item.Int("IndexNumber") : season.Episodes.Count + 1,
                Plot = item.Str("Overview"),
                Image = Poster(item),
                DurationSecs = (int)Math.Min(int.MaxValue, item.Long("RunTimeTicks") / 10_000_000),
                Extension = ext,
                Url = StreamUrl(id, ext),
            });
        }
        foreach (var season in seasons.Values) season.Episodes.Sort((a, b) => a.Number.CompareTo(b.Number));
        // Specials (season 0) go last.
        detail.Seasons.AddRange(seasons.Values.OrderBy(s => s.Number == 0).ThenBy(s => s.Number));
        return detail;
    }

    public Task<IReadOnlyList<EpgProgram>> GetEpgAsync(Channel channel, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EpgProgram>>(Array.Empty<EpgProgram>());

    public string GetLiveUrl(Channel channel) => channel.Url ?? "";
    public string GetMovieUrl(Movie movie) => movie.Url ?? "";
    public string GetEpisodeUrl(Episode episode) => episode.Url ?? "";
    public string? GetCatchupUrl(Channel channel, EpgProgram program) => null;
}
