using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Umbra.Core;

/// <summary>An error with a message that can be shown to the user as is.</summary>
public sealed class SourceException(string message) : Exception(L.T(message));

public static class Errors
{
    public static string Friendly(Exception ex) => ex switch
    {
        SourceException s => s.Message,
        HttpRequestException h => L.T("No se pudo conectar con el servidor. Comprueba la dirección y tu conexión a Internet.")
                                  + (string.IsNullOrWhiteSpace(h.Message) ? "" : $"\n({h.Message})"),
        TaskCanceledException => L.T("El servidor tardó demasiado en responder."),
        IOException io => L.T("No se pudo leer el archivo: {0}", io.Message),
        _ => ex.Message,
    };
}

/// <summary>Lenient JSON accessors: Xtream panels mix strings, numbers, nulls and booleans for the same field.</summary>
public static class Json
{
    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static bool Has(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    public static JsonElement Obj(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        return v.ValueKind == JsonValueKind.Object ? v : default;
    }

    public static string Str(this JsonElement e, string name) => AsString(e.Prop(name));

    public static string AsString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        _ => "",
    };

    /// <summary>A string field that may also arrive as an array of strings (first element is used).</summary>
    public static string FirstStr(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in v.EnumerateArray())
            {
                var s = AsString(x);
                if (s.Length > 0) return s;
            }
            return "";
        }
        return AsString(v);
    }

    public static long Long(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : 0;
    }

    public static int Int(this JsonElement e, string name) => (int)Math.Clamp(e.Long(name), int.MinValue, int.MaxValue);

    public static double Dbl(this JsonElement e, string name) =>
        double.TryParse(e.Str(name).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    public static bool Bool(this JsonElement e, string name)
    {
        var s = e.Str(name);
        return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<JsonElement> Arr(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };
}

public static class Text
{
    public static bool Contains(string haystack, string needle) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(haystack, needle,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    public static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    public static string ShortHash(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();

    public static DateTime? FromUnix(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime : null;
}

public static class Http
{
    static HttpClient? _client;

    public static string UserAgent { get; set; } = Settings.DefaultUserAgent;

    public static HttpClient Client => _client ??= new HttpClient(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    public static HttpRequestMessage Get(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        return req;
    }

    public static async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        var req = Get(url);
        var res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            res.Dispose();
            throw new SourceException("El servidor ha rechazado el acceso. Revisa el usuario y la contraseña.");
        }
        if (!res.IsSuccessStatusCode)
        {
            var code = (int)res.StatusCode;
            var reason = res.ReasonPhrase;
            res.Dispose();
            throw new SourceException(L.T("El servidor respondió con un error {0} ({1}).", code, reason));
        }
        return res;
    }

    /// <summary>Downloads to a temporary file and then replaces <paramref name="path"/>, so a failed download keeps the old cache.</summary>
    public static async Task DownloadToFileAsync(string url, string path, CancellationToken ct)
    {
        using var res = await SendAsync(url, ct);
        var tmp = path + ".part";
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            await src.CopyToAsync(dst, 1 << 16, ct);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

public static class AppPaths
{
    const string Name = "Umbra";
    /// <summary>Folder name used before the app was renamed; it is taken over on first run.</summary>
    // Written in two halves on purpose: a find-and-replace of the old name must not change it.
    const string FormerName = "Supa" + "Legacy";

    /// <summary>Optional UMBRA_HOME environment variable: keep data and cache in that folder instead (portable use, tests).</summary>
    static readonly string? Home = Environment.GetEnvironmentVariable("UMBRA_HOME") is { Length: > 0 } home ? home : null;

    public static string DataDir { get; } = Ensure(Home is null ? Adopt(Environment.SpecialFolder.ApplicationData) : Path.Combine(Home, "data"));

    public static string CacheRoot { get; } = Ensure(Path.Combine(Home ?? Adopt(Environment.SpecialFolder.LocalApplicationData), "cache"));

    /// <summary>The app folder under a system folder, taking over the one with the former name if there is no new one yet.</summary>
    static string Adopt(Environment.SpecialFolder root)
    {
        var folder = Path.Combine(Environment.GetFolderPath(root), Name);
        var former = Path.Combine(Environment.GetFolderPath(root), FormerName);
        try
        {
            if (!Directory.Exists(folder) && Directory.Exists(former)) Directory.Move(former, folder);
        }
        catch (Exception)
        {
            // Left where it was (e.g. a file in use): the app starts with an empty folder rather than failing.
        }
        return folder;
    }

    public static string LogFile => Path.Combine(DataDir, "app.log");

    public static string CacheDir(string accountId) => Ensure(Path.Combine(CacheRoot, accountId));

    public static bool IsFresh(string path, TimeSpan maxAge) =>
        File.Exists(path) && new FileInfo(path).Length > 0 && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < maxAge;

    static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

public static class Log
{
    static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                var file = AppPaths.LogFile;
                if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Delete(file);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }
}

public static class JsonStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string file) where T : new()
    {
        var path = Path.Combine(AppPaths.DataDir, file);
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo leer {file}", ex);
        }
        return new T();
    }

    public static void Save<T>(string file, T value)
    {
        var path = Path.Combine(AppPaths.DataDir, file);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

static class Secret
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Umbra.Accounts.v1");

    /// <summary>
    /// Keys of earlier versions. The key is part of the encryption, so data saved before the app was renamed can only
    /// be read with the old one. Never remove an entry: saved passwords would become unreadable.
    /// </summary>
    /// Written in two halves on purpose: a find-and-replace of the old name must not change it.
    static readonly byte[][] FormerEntropies = [Encoding.UTF8.GetBytes("Supa" + "Legacy.Accounts.v1")];

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    /// <summary>Decrypts a stored value; false when it cannot be read (the caller must then keep the stored value as it is).</summary>
    public static bool TryUnprotect(string value, out string result)
    {
        result = "";
        if (string.IsNullOrEmpty(value)) return true;
        Exception? error = null;
        foreach (var entropy in FormerEntropies.Prepend(Entropy))
        {
            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(value), entropy, DataProtectionScope.CurrentUser);
                result = Encoding.UTF8.GetString(data);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }
        Log.Error("No se pudo descifrar un dato guardado", error);
        return false;
    }
}
