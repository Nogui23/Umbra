using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Umbra.Core;

/// <summary>
/// Movies and series from a Plex Media Server. The account keeps a token, never the Plex password: signing in with
/// user and password asks plex.tv for a token once. Files are played and downloaded as they are stored.
/// </summary>
public sealed class PlexSource : IContentSource
{
    /// <summary>Where Plex accounts sign in. Only the developer self-test points it somewhere else.</summary>
    public static string PlexTv { get; set; } = "https://plex.tv";

    static readonly string ClientId = Text.ShortHash(Environment.MachineName + "|umbra");

    string _base, _token;

    public PlexSource(Account account, Settings settings)
    {
        Account = account;
        _base = account.Server.Trim().Length == 0 ? "" : XtreamSource.NormalizeServer(account.Server);
        _token = account.Password.Trim();
    }

    public Account Account { get; }
    public AccountInfo? Info => null;
    public Task<AccountInfo?> RefreshInfoAsync(CancellationToken ct) => Task.FromResult<AccountInfo?>(null);

    /// <summary>The server in use and the token it accepts, known after <see cref="ConnectAsync"/>.</summary>
    public string BaseUrl => _base;
    public string Token => _token;

    static string E(string s) => Uri.EscapeDataString(s);

    static HttpRequestMessage Request(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", Http.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", ClientId);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "Umbra");
        request.Headers.TryAddWithoutValidation("X-Plex-Version", "1.0");
        request.Headers.TryAddWithoutValidation("X-Plex-Device-Name", "Umbra");
        if (!string.IsNullOrEmpty(token)) request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        return request;
    }

    /// <summary>
    /// Asks plex.tv for a token with the Plex user and password (plus the code, for accounts with two-step
    /// verification). The password is only sent to plex.tv and is not kept.
    /// </summary>
    public static async Task<string> SignInAsync(string user, string password, string code, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, PlexTv + "/api/v2/users/signin", null);
        var form = new Dictionary<string, string> { ["login"] = user, ["password"] = password };
        if (code.Trim().Length > 0) form["verificationCode"] = code.Trim();
        request.Content = new FormUrlEncodedContent(form);
        using var response = await Http.Client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(body, Json.DocOptions);
                var token = doc.RootElement.Str("authToken");
                if (token.Length > 0) return token;
            }
            catch (JsonException) { }
            throw new SourceException("Plex no devolvió datos válidos.");
        }
        // 1029: the account uses two-step verification and the code is missing or wrong.
        if (body.Contains("1029", StringComparison.Ordinal) || body.Contains("verification", StringComparison.OrdinalIgnoreCase))
            throw new SourceException("Tu cuenta de Plex usa verificación en dos pasos: escribe el código de tu app de autenticación.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity)
            throw new SourceException("Usuario o contraseña de Plex incorrectos.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SourceException("Plex ha bloqueado temporalmente los intentos de inicio de sesión. Espera unos minutos.");
        throw new SourceException(L.T("El servidor respondió con un error {0} ({1}).", (int)response.StatusCode, response.ReasonPhrase));
    }

    async Task<HttpStatusCode> ProbeAsync(string server, string token, int seconds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var request = Request(HttpMethod.Get, server + "/library/sections", token);
        using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        return response.StatusCode;
    }

    /// <summary>
    /// Finds the server and the token it accepts. With a server address it is tried as written; if the token is
    /// one of a Plex account (not of that server), or no address was given, the servers of the account are asked
    /// to plex.tv and the first that answers is used.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_token.Length == 0) throw new SourceException("Falta el token de Plex.");
        if (_base.Length > 0)
        {
            HttpStatusCode status;
            try
            {
                status = await ProbeAsync(_base, _token, 12, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new SourceException("No se pudo conectar con el servidor Plex. Revisa la dirección (por ejemplo, http://192.168.1.10:32400).");
            }
            if (status == HttpStatusCode.OK) return;
            if (status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                throw new SourceException("La dirección no parece ser de un servidor Plex. Revisa la dirección y el puerto (normalmente 32400).");
        }

        // The servers this Plex account can use, each with its own token.
        using var request = Request(HttpMethod.Get, PlexTv + "/api/v2/resources?includeHttps=1&includeRelay=1", _token);
        using var response = await Http.Client.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SourceException("Plex ha rechazado el token. Comprueba que es correcto o inicia sesión con usuario y contraseña.");
        if (!response.IsSuccessStatusCode)
            throw new SourceException(L.T("El servidor respondió con un error {0} ({1}).", (int)response.StatusCode, response.ReasonPhrase));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct), Json.DocOptions);

        var wantedHost = _base.Length > 0 && Uri.TryCreate(_base, UriKind.Absolute, out var wanted) ? wanted.Host : null;
        var candidates = new List<(string Uri, string Token, int Order)>();
        foreach (var server in doc.RootElement.Arr())
        {
            if (!server.Str("provides").Contains("server", StringComparison.OrdinalIgnoreCase)) continue;
            var token = Text.FirstNonEmpty(server.Str("accessToken"), _token);
            var connections = server.Prop("connections").Arr().ToList();
            // The address the user wrote, with the token of the server that owns it.
            if (wantedHost is not null && connections.Any(c => c.Str("address") == wantedHost || c.Str("uri").Contains(wantedHost, StringComparison.OrdinalIgnoreCase)))
                candidates.Add((_base, token, 0));
            foreach (var c in connections)
            {
                var uri = c.Str("uri").TrimEnd('/');
                if (uri.Length == 0) continue;
                // Home network first, then from outside, relays last (slow).
                candidates.Add((uri, token, c.Bool("relay") ? 3 : c.Bool("local") ? 1 : 2));
            }
        }
        if (candidates.Count == 0) throw new SourceException("Esta cuenta de Plex no tiene ningún servidor.");

        foreach (var (uri, token, _) in candidates.OrderBy(c => c.Order))
        {
            try
            {
                if (await ProbeAsync(uri, token, 6, ct) != HttpStatusCode.OK) continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                continue;
            }
            (_base, _token) = (uri, token);
            return;
        }
        throw new SourceException("No se pudo conectar con ningún servidor de tu cuenta de Plex. Comprueba que está encendido y que este equipo llega a él.");
    }

    async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, _base + path, _token);
        using var response = await Http.Client.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SourceException("Plex ha rechazado el token. Comprueba que es correcto o inicia sesión con usuario y contraseña.");
        if (!response.IsSuccessStatusCode)
            throw new SourceException(L.T("El servidor respondió con un error {0} ({1}).", (int)response.StatusCode, response.ReasonPhrase));
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct), Json.DocOptions);
            return doc.RootElement.Obj("MediaContainer").Clone();
        }
        catch (JsonException)
        {
            throw new SourceException("El servidor no devolvió datos válidos.");
        }
    }

    string Picture(string path) => path.Length == 0 ? "" : $"{_base}{path}?X-Plex-Token={E(_token)}";

    /// <summary>The file itself: the first part of the first version the server has.</summary>
    (string Url, string Extension) FileOf(JsonElement item)
    {
        foreach (var media in item.Prop("Media").Arr())
        {
            foreach (var part in media.Prop("Part").Arr())
            {
                var key = part.Str("key");
                if (key.Length == 0) continue;
                var ext = Text.FirstNonEmpty(part.Str("container"), media.Str("container"), Path.GetExtension(key).TrimStart('.'));
                if (ext.Length is 0 or > 5 || !ext.All(char.IsAsciiLetterOrDigit)) ext = "mkv";
                return ($"{_base}{key}?X-Plex-Token={E(_token)}", ext.ToLowerInvariant());
            }
        }
        return ("", "mkv");
    }

    static string Tags(JsonElement item, string name, int max = 8) =>
        string.Join(", ", item.Prop(name).Arr().Select(t => t.Str("tag")).Where(t => t.Length > 0).Take(max));

    static double Rating(JsonElement item) => item.Dbl("audienceRating") is > 0 and var a ? a : item.Dbl("rating");

    static string Date(JsonElement item)
    {
        var raw = item.Str("originallyAvailableAt");
        if (raw.Length >= 10) return raw[..10];
        var year = item.Int("year");
        return year > 0 ? year.ToString(CultureInfo.InvariantCulture) : "";
    }

    public async Task<ContentLibrary> LoadLibraryAsync(bool force, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(L.T("Conectando con el servidor…"));
        await ConnectAsync(ct);
        var lib = new ContentLibrary();
        var sections = await GetAsync("/library/sections", ct);
        foreach (var section in sections.Prop("Directory").Arr())
        {
            var key = section.Str("key");
            var type = section.Str("type");
            if (key.Length == 0 || type is not ("movie" or "show")) continue;
            // Each Plex library («Películas», «Infantil»…) is a category.
            var category = new Category { Id = "s:" + key, Name = section.Str("title"), Kind = type == "movie" ? MediaKind.Movie : MediaKind.Series };
            progress?.Report(L.T("Cargando «{0}»…", category.Name));
            var all = await GetAsync($"/library/sections/{E(key)}/all?type={(type == "movie" ? 1 : 2)}", ct);
            if (type == "movie")
            {
                lib.MovieCategories.Add(category);
                foreach (var item in all.Prop("Metadata").Arr())
                {
                    var (url, ext) = FileOf(item);
                    var id = item.Str("ratingKey");
                    if (id.Length == 0 || url.Length == 0) continue;
                    lib.Movies.Add(new Movie
                    {
                        Id = id,
                        Name = item.Str("title"),
                        Image = Picture(item.Str("thumb")),
                        Rating = Rating(item),
                        Added = item.Long("addedAt"),
                        CategoryId = category.Id,
                        Extension = ext,
                        Url = url,
                    });
                }
            }
            else
            {
                lib.SeriesCategories.Add(category);
                foreach (var item in all.Prop("Metadata").Arr())
                {
                    var id = item.Str("ratingKey");
                    if (id.Length == 0) continue;
                    lib.Series.Add(new SeriesItem
                    {
                        Id = id,
                        Name = item.Str("title"),
                        Image = Picture(item.Str("thumb")),
                        Rating = Rating(item),
                        Added = item.Long("addedAt"),
                        CategoryId = category.Id,
                        Plot = item.Str("summary"),
                        Genre = Tags(item, "Genre", 3),
                        ReleaseDate = Date(item),
                    });
                }
            }
        }
        lib.Finish();
        return lib;
    }

    async Task<JsonElement> ItemAsync(string id, CancellationToken ct)
    {
        if (_base.Length == 0) await ConnectAsync(ct);
        var container = await GetAsync($"/library/metadata/{E(id)}", ct);
        return container.Prop("Metadata").Arr().FirstOrDefault();
    }

    public async Task<MovieDetail> GetMovieDetailAsync(Movie movie, CancellationToken ct)
    {
        var item = await ItemAsync(movie.Id, ct);
        var minutes = item.Long("duration") / 60000;
        return new MovieDetail
        {
            Name = Text.FirstNonEmpty(item.Str("title"), movie.Name),
            Plot = item.Str("summary"),
            Genre = Tags(item, "Genre", 4),
            Director = Tags(item, "Director", 3),
            Cast = Tags(item, "Role"),
            ReleaseDate = Date(item),
            Duration = minutes > 0 ? minutes.ToString(CultureInfo.InvariantCulture) : "",
            Country = Tags(item, "Country", 3),
            Rating = Rating(item),
            Poster = Text.FirstNonEmpty(Picture(item.Str("thumb")), movie.Image),
            Backdrop = Picture(item.Str("art")),
        };
    }

    public async Task<SeriesDetail> GetSeriesDetailAsync(SeriesItem series, CancellationToken ct)
    {
        var show = await ItemAsync(series.Id, ct);
        var detail = new SeriesDetail
        {
            Name = Text.FirstNonEmpty(show.Str("title"), series.Name),
            Plot = Text.FirstNonEmpty(show.Str("summary"), series.Plot),
            Genre = Tags(show, "Genre", 4),
            Director = Tags(show, "Director", 3),
            Cast = Tags(show, "Role"),
            ReleaseDate = Date(show),
            Rating = Rating(show),
            Cover = Text.FirstNonEmpty(Picture(show.Str("thumb")), series.Image),
            Backdrop = Picture(show.Str("art")),
        };

        var leaves = await GetAsync($"/library/metadata/{E(series.Id)}/allLeaves", ct);
        var seasons = new SortedDictionary<int, Season>();
        foreach (var item in leaves.Prop("Metadata").Arr())
        {
            var (url, ext) = FileOf(item);
            var id = item.Str("ratingKey");
            if (id.Length == 0 || url.Length == 0) continue;
            var seasonNo = item.Has("parentIndex") ? item.Int("parentIndex") : 1;
            if (!seasons.TryGetValue(seasonNo, out var season))
                seasons[seasonNo] = season = new Season { Number = seasonNo, Name = seasonNo == 0 ? L.T("Especiales") : "" };
            season.Episodes.Add(new Episode
            {
                Id = id,
                Title = item.Str("title"),
                Season = seasonNo,
                Number = item.Has("index") ? item.Int("index") : season.Episodes.Count + 1,
                Plot = item.Str("summary"),
                Image = Picture(item.Str("thumb")),
                DurationSecs = (int)Math.Min(int.MaxValue, item.Long("duration") / 1000),
                Extension = ext,
                Url = url,
            });
        }
        foreach (var season in seasons.Values) season.Episodes.Sort((a, b) => a.Number.CompareTo(b.Number));
        detail.Seasons.AddRange(seasons.Values.OrderBy(s => s.Number == 0).ThenBy(s => s.Number));
        return detail;
    }

    public Task<IReadOnlyList<EpgProgram>> GetEpgAsync(Channel channel, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EpgProgram>>(Array.Empty<EpgProgram>());

    public string GetLiveUrl(Channel channel) => "";
    public string GetMovieUrl(Movie movie) => movie.Url ?? "";
    public string GetEpisodeUrl(Episode episode) => episode.Url ?? "";
    public string? GetCatchupUrl(Channel channel, EpgProgram program) => null;
}
