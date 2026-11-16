using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace Umbra.Core;

public enum DownloadState { Queued, Downloading, Paused, Completed, Failed }

/// <summary>What the app asks to download: a movie or an episode that the provider serves as a plain file.</summary>
public sealed record DownloadRequest(
    string AccountId,
    string Key,
    string Url,
    string Title,
    string Subtitle,
    string Image,
    string? Subfolder,
    string FileName,
    string Extension,
    bool IsEpisode);

/// <summary>
/// What the catalogue says about a download (plot, cast, artwork…), kept with it so the library can show it
/// without asking the provider again.
/// </summary>
public sealed class DownloadInfo
{
    public string Plot { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Director { get; set; } = "";
    public string Cast { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    /// <summary>Already written for people ("1 h 52 min").</summary>
    public string Duration { get; set; } = "";
    public string Country { get; set; } = "";
    public double Rating { get; set; }
    /// <summary>Poster and backdrop: web addresses at first, files on this computer once they have been saved.</summary>
    public string Poster { get; set; } = "";
    public string Backdrop { get; set; } = "";
    /// <summary>For an episode, the plot of its series.</summary>
    public string SeriesPlot { get; set; } = "";
}

/// <summary>A movie or episode saved (or being saved) to disk.</summary>
public sealed class DownloadItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "";
    /// <summary>The same key used for resume points ("movie:ID", "episode:ID").</summary>
    public string Key { get; set; } = "";
    public bool IsEpisode { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Image { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    /// <summary>Null for downloads made before this was saved, until their page is opened again.</summary>
    public DownloadInfo? Info { get; set; }

    /// <summary>The address carries the account's user and password, so it is stored encrypted like them.</summary>
    public string UrlEnc { get; set; } = "";
    [JsonIgnore] public string Url { get; set; } = "";

    DownloadState _state;
    long _total, _received;
    double _speed;
    string _error = "";

    public DownloadState State { get => _state; set { _state = value; Touch(); } }
    public long TotalBytes { get => _total; set { _total = value; Touch(); } }
    public long ReceivedBytes { get => _received; set { _received = value; Touch(); } }
    public string Error { get => _error; set { _error = value ?? ""; Touch(); } }
    [JsonIgnore] public double BytesPerSecond { get => _speed; set { _speed = value; Touch(); } }

    internal void SetProgress(long received, long total, double speed)
    {
        _received = received;
        _total = total;
        _speed = speed;
        Touch();
    }

    /// <summary>Every text shown for the item derives from these few fields, so all bindings are refreshed together.</summary>
    void Touch() => OnPropertyChanged(string.Empty);

    [JsonIgnore] public bool IsCompleted => _state == DownloadState.Completed;
    [JsonIgnore] public bool IsActive => _state is DownloadState.Queued or DownloadState.Downloading or DownloadState.Paused or DownloadState.Failed;
    [JsonIgnore] public bool CanPause => _state is DownloadState.Queued or DownloadState.Downloading;
    [JsonIgnore] public bool CanResume => _state is DownloadState.Paused or DownloadState.Failed;
    [JsonIgnore] public bool ShowProgress => _state is DownloadState.Downloading or DownloadState.Paused;

    /// <summary>0..1; 0 while the size is unknown.</summary>
    [JsonIgnore] public double Progress => _total > 0 ? Math.Clamp((double)_received / _total, 0, 1) : 0;

    [JsonIgnore] public string PercentText => _total > 0 ? $"{(int)(Progress * 100)} %" : "…";

    /// <summary>Very short state for small buttons.</summary>
    [JsonIgnore]
    public string Badge => _state switch
    {
        DownloadState.Downloading => _total > 0 ? $"{(int)(Progress * 100)}%" : "…",
        DownloadState.Queued => "…",
        DownloadState.Paused => "‖",
        DownloadState.Failed => "!",
        _ => "",
    };

    [JsonIgnore]
    public string StatusText => _state switch
    {
        DownloadState.Queued => L.T("En cola"),
        DownloadState.Downloading when _total > 0 && RemainingText is { Length: > 0 } remaining =>
            L.T("{0} de {1} · {2}/s · {3}", Size(_received), Size(_total), Size((long)_speed), remaining),
        DownloadState.Downloading when _total > 0 =>
            L.T("{0} de {1} · {2}/s", Size(_received), Size(_total), Size((long)_speed)),
        DownloadState.Downloading => L.T("{0} · {1}/s", Size(_received), Size((long)_speed)),
        DownloadState.Paused when _total > 0 => L.T("En pausa · {0} de {1}", Size(_received), Size(_total)),
        DownloadState.Paused => L.T("En pausa"),
        DownloadState.Completed => L.T("Descargada · {0}", Size(_total > 0 ? _total : _received)),
        _ => _error.Length > 0 ? L.T("Error: {0}", _error) : L.T("Error en la descarga"),
    };

    /// <summary>Time left at the current speed ("quedan 12 min"); empty while it cannot be estimated.</summary>
    [JsonIgnore]
    public string RemainingText
    {
        get
        {
            if (_state != DownloadState.Downloading || _total <= 0 || _speed < 1024) return "";
            var seconds = (_total - _received) / _speed;
            if (seconds < 60) return L.T("queda menos de 1 min");
            // Rounded up, so the estimate never promises less than what is left.
            var minutes = (long)Math.Ceiling(seconds / 60);
            if (minutes < 60) return L.T("quedan {0} min", minutes);
            return minutes < 60 * 48 ? L.T("quedan {0} h {1} min", minutes / 60, minutes % 60) : L.T("quedan más de 2 días");
        }
    }

    public static string Size(long bytes)
    {
        if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.00", L.Culture) + " GB";
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", L.Culture) + " MB";
        return (bytes / 1024.0).ToString("0", L.Culture) + " KB";
    }
}

/// <summary>
/// Download queue for movies and episodes. Files are written next to their final name with a ".part" suffix and
/// renamed when complete; an interrupted download continues from the bytes already on disk (HTTP Range).
/// The list and every state change live on the thread that called <see cref="Start"/> (the interface thread);
/// only the transfer itself runs in the background.
/// </summary>
public sealed class DownloadManager
{
    public static DownloadManager I { get; } = new();

    const string StoreFile = "downloads.json";
    const int MaxAttempts = 5;
    // Reconnecting when the server slows a download down (see ReconnectWhenThrottled).
    const double ThrottleMinBurst = 1.5 * 1024 * 1024;   // a burst slower than this is just a slow line
    const double ThrottleRatio = 0.3;                     // "slowed down" = under this share of the burst speed
    const long ThrottleMinGain = 16L << 20;               // a connection that brought less than this did not help
    const long ThrottleMinLeft = 32L << 20;               // not worth it for the last few megabytes
    static readonly TimeSpan ThrottleSlowFor = TimeSpan.FromSeconds(8);
    static readonly TimeSpan ThrottleMinAge = TimeSpan.FromSeconds(12);
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    static readonly string[] StreamExtensions = [".m3u8", ".m3u", ".mpd"];

    readonly List<DownloadItem> _items = new();
    readonly Dictionary<string, CancellationTokenSource> _running = new();
    SynchronizationContext? _ui;
    bool _started, _suspended;
    int _maxParallel = 1;

    public IReadOnlyList<DownloadItem> Items => _items;

    /// <summary>An item was added or removed.</summary>
    public event Action? ListChanged;

    /// <summary>A download ended, completed or failed.</summary>
    public event Action<DownloadItem>? Finished;

    public string Folder { get; set; } = DefaultFolder;

    /// <summary>
    /// Some providers send the first part of a file at full speed and then slow the connection down. With this on,
    /// a download that has been slowed down closes its connection and asks for the rest again, which usually brings
    /// the initial speed back. Off by default: it works around a limit the provider set.
    /// </summary>
    public bool ReconnectWhenThrottled { get; set; }

    public static string DefaultFolder
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(videos))
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            return Path.Combine(videos, "Umbra");
        }
    }

    public int MaxParallel
    {
        get => _maxParallel;
        set
        {
            _maxParallel = Math.Clamp(value, 1, 3);
            Pump();
        }
    }

    /// <summary>Number of downloads using a connection right now.</summary>
    public int RunningCount => _running.Count;

    public bool HasPending => _items.Any(i => i.State is DownloadState.Queued or DownloadState.Downloading);

    /// <summary>
    /// While true nothing is transferred: running downloads go back to the queue and continue when it is cleared.
    /// Used while a stream is playing on an account without a spare connection.
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            if (value)
            {
                foreach (var item in _items.Where(i => i.State == DownloadState.Downloading).ToList())
                {
                    Cancel(item);
                    item.State = DownloadState.Queued;
                }
            }
            else
            {
                Pump();
            }
        }
    }

    /// <summary>True when the address is a plain file; playlists and live streams cannot be saved this way.</summary>
    public static bool CanDownload(string? url)
    {
        if (IsLocalPath(url)) return true; // a file of a folder or NAS: it is copied
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        var ext = Path.GetExtension(uri.AbsolutePath);
        return !StreamExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    // ----- Lifetime -----

    /// <summary>Loads the saved list and continues what was left unfinished. Call once, from the interface thread.</summary>
    public void Start(SynchronizationContext? ui)
    {
        if (_started) return;
        _ui = ui;
        foreach (var item in JsonStore.Load<List<DownloadItem>>(StoreFile))
        {
            if (!Secret.TryUnprotect(item.UrlEnc, out var url)) continue;
            item.Url = url;
            if (item.FilePath.Length == 0) continue;
            if (item.State == DownloadState.Completed)
            {
                if (!File.Exists(item.FilePath)) continue; // deleted by hand outside the app
            }
            else
            {
                if (item.State == DownloadState.Downloading) item.State = DownloadState.Queued;
                item.ReceivedBytes = PartLength(item);
            }
            _items.Add(item);
        }
        _started = true;
        ListChanged?.Invoke();
        Pump();
        // Pictures that could not be saved when the download was added (no connection then) are tried again.
        foreach (var item in _items) CacheArt(item);
    }

    /// <summary>Stops every transfer and saves the list; unfinished downloads continue the next time the app opens.</summary>
    public void Shutdown()
    {
        if (!_started) return;
        _started = false;
        foreach (var item in _items.Where(i => i.State == DownloadState.Downloading).ToList())
        {
            Cancel(item);
            item.State = DownloadState.Queued;
        }
        Save();
    }

    // ----- Queries -----

    public DownloadItem? Find(string accountId, string key) =>
        _items.FirstOrDefault(i => i.AccountId == accountId && i.Key == key);

    /// <summary>Path of the finished file for that content, null when it is not downloaded.</summary>
    public string? LocalFile(string accountId, string key) =>
        Find(accountId, key) is { IsCompleted: true } item && File.Exists(item.FilePath) ? item.FilePath : null;

    // ----- Commands -----

    public DownloadItem Enqueue(DownloadRequest request)
    {
        if (Find(request.AccountId, request.Key) is { } existing) return existing;
        var item = new DownloadItem
        {
            AccountId = request.AccountId,
            Key = request.Key,
            IsEpisode = request.IsEpisode,
            Title = request.Title,
            Subtitle = request.Subtitle,
            Image = request.Image,
            Url = request.Url,
            UrlEnc = Secret.Protect(request.Url),
            FilePath = NewPath(request),
            AddedUtc = DateTime.UtcNow,
            State = DownloadState.Queued,
        };
        _items.Add(item);
        Save();
        ListChanged?.Invoke();
        Pump();
        CacheArt(item);
        return item;
    }

    /// <summary>Keeps the catalogue details with the download and saves its artwork to disk.</summary>
    public void SetInfo(DownloadItem item, DownloadInfo info)
    {
        if (!_items.Contains(item)) return;
        item.Info = info;
        Save();
        CacheArt(item);
    }

    internal static string ArtDir => Path.Combine(AppPaths.DataDir, "art");

    /// <summary>A file path (this computer or a shared folder), as opposed to a web address.</summary>
    static bool IsLocalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Contains("://", StringComparison.Ordinal) && Path.IsPathFullyQualified(path);

    /// <summary>Runs <paramref name="work"/> on the thread that owns the list and hands its result back.</summary>
    internal Task<T> OnUi<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    /// <summary>A free path in the download folder for a file brought from outside (an imported library).</summary>
    internal string NewFilePath(string? subfolder, string fileName, string extension) =>
        NewPath(new DownloadRequest("", "", "", "", "", "", subfolder, fileName, extension, false));

    /// <summary>Adds a finished item whose file is already in place.</summary>
    internal void AddImported(DownloadItem item)
    {
        _items.Add(item);
        Save();
        ListChanged?.Invoke();
    }

    static bool IsWeb(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Copies the pictures of the item to disk and points it at the copies, so they show without connection.</summary>
    void CacheArt(DownloadItem item)
    {
        // Pictures on the web, and also those of a folder or NAS, which may not be reachable later.
        bool Outside(string? u) => IsWeb(u) || (IsLocalPath(u) && !u!.StartsWith(ArtDir, StringComparison.OrdinalIgnoreCase));
        var urls = new[] { item.Image, item.Info?.Poster, item.Info?.Backdrop }.Where(Outside).Select(u => u!).Distinct().ToList();
        if (urls.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var url in urls)
            {
                try
                {
                    var local = IsLocalPath(url);
                    var ext = Path.GetExtension(local ? url : new Uri(url).AbsolutePath).ToLowerInvariant();
                    if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif")) ext = ".jpg";
                    var path = Path.Combine(ArtDir, $"{item.Id}-{Text.ShortHash(url)}{ext}");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    byte[] bytes;
                    if (local)
                    {
                        if (!File.Exists(url) || new FileInfo(url).Length > 12 << 20) continue;
                        bytes = await File.ReadAllBytesAsync(url, cts.Token);
                    }
                    else
                    {
                        using var response = await SendAsync(url, 0, cts.Token);
                        if (!response.IsSuccessStatusCode) continue;
                        if (response.Content.Headers.ContentLength > 12 << 20) continue;
                        bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                    }
                    if (bytes.Length is 0 or > 12 << 20) continue;
                    Directory.CreateDirectory(ArtDir);
                    await File.WriteAllBytesAsync(path, bytes, cts.Token);
                    Post(() =>
                    {
                        if (!_items.Contains(item))
                        {
                            try { File.Delete(path); } catch (Exception) { }
                            return;
                        }
                        if (item.Image == url) item.Image = path;
                        if (item.Info is { } info)
                        {
                            if (info.Poster == url) info.Poster = path;
                            if (info.Backdrop == url) info.Backdrop = path;
                        }
                        Save();
                    });
                }
                catch (Exception ex)
                {
                    Log.Error($"No se pudo guardar una imagen de «{item.Title}»", ex);
                }
            }
        });
    }

    public void Pause(DownloadItem item)
    {
        if (!item.CanPause) return;
        Cancel(item);
        item.State = DownloadState.Paused;
        Save();
        Pump();
    }

    /// <summary>Puts a paused or failed download back in the queue.</summary>
    public void Resume(DownloadItem item)
    {
        if (!item.CanResume) return;
        item.Error = "";
        item.State = DownloadState.Queued;
        Save();
        Pump();
    }

    /// <summary>Takes the item off the list and deletes its file (finished or partial) from disk.</summary>
    public void Remove(DownloadItem item)
    {
        Cancel(item);
        if (!_items.Remove(item)) return;
        Save();
        ListChanged?.Invoke();
        Pump();
        var paths = new List<string> { item.FilePath, item.FilePath + ".part" };
        try
        {
            if (Directory.Exists(ArtDir)) paths.AddRange(Directory.GetFiles(ArtDir, item.Id + "-*"));
        }
        catch (Exception) { }
        // A transfer that was just cancelled may hold the file a moment longer.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    foreach (var path in paths)
                        if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(400);
                }
            }
            Log.Error($"No se pudo borrar el archivo de la descarga: {paths[0]}");
        });
    }

    // ----- Queue -----

    void Pump()
    {
        if (!_started || _suspended) return;
        while (_running.Count < _maxParallel)
        {
            var next = _items.FirstOrDefault(i => i.State == DownloadState.Queued && !_running.ContainsKey(i.Id));
            if (next is null) return;
            var cts = new CancellationTokenSource();
            _running[next.Id] = cts;
            next.State = DownloadState.Downloading;
            _ = Task.Run(() => RunAsync(next, cts));
        }
    }

    void Cancel(DownloadItem item)
    {
        if (_running.Remove(item.Id, out var cts)) cts.Cancel();
        item.BytesPerSecond = 0;
    }

    void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    void Finish(DownloadItem item, CancellationTokenSource cts, DownloadState state, string error)
    {
        // Ignored when the transfer was paused, removed or restarted in the meantime.
        if (!_running.TryGetValue(item.Id, out var current) || !ReferenceEquals(current, cts)) return;
        _running.Remove(item.Id);
        item.BytesPerSecond = 0;
        item.Error = error;
        if (state == DownloadState.Completed)
        {
            var size = new FileInfo(item.FilePath).Length;
            item.SetProgress(size, size, 0);
        }
        item.State = state;
        Save();
        Finished?.Invoke(item);
        Pump();
    }

    void Save()
    {
        try { JsonStore.Save(StoreFile, _items); }
        catch (Exception ex) { Log.Error("No se pudo guardar la lista de descargas", ex); }
    }

    // ----- Transfer -----

    sealed class PermanentException(string message) : Exception(message);

    /// <summary>The server slowed the connection down and the rest is going to be asked for again.</summary>
    sealed class ThrottledException : Exception;

    /// <summary>What one download remembers between its connections.</summary>
    sealed class Run
    {
        /// <summary>False once reconnecting has shown not to help (or to be unsafe) for this file.</summary>
        public bool MayReconnect = true;
        /// <summary>The connection being opened replaces one that was slowed down.</summary>
        public bool AfterThrottle;
        public int Reconnects;
    }

    async Task RunAsync(DownloadItem item, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var attempts = 0;
        var run = new Run();
        while (true)
        {
            var before = PartLength(item);
            try
            {
                await TransferAsync(item, run, ct);
                if (run.Reconnects > 0) Log.Info($"Descarga «{item.Title}»: terminada con {run.Reconnects} reconexiones por freno del servidor.");
                Post(() => Finish(item, cts, DownloadState.Completed, ""));
                return;
            }
            catch (ThrottledException)
            {
                run.AfterThrottle = true;
                run.Reconnects++;
                attempts = 0;
                // A moment for the provider to count the old connection as closed.
                try { await Task.Delay(1500, ct); }
                catch (OperationCanceledException) { return; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // paused, removed or the app is closing: the caller already set the state
            }
            catch (PermanentException ex)
            {
                Log.Error($"Descarga fallida ({item.Title}): {ex.Message}");
                Post(() => Finish(item, cts, DownloadState.Failed, ex.Message));
                return;
            }
            catch (Exception ex)
            {
                // Connection drops are retried from where the file was left; the count starts again after any progress.
                if (PartLength(item) > before) attempts = 0;
                if (++attempts >= MaxAttempts)
                {
                    Log.Error($"Descarga fallida ({item.Title})", ex);
                    var message = ex is OperationCanceledException ? L.T("El servidor dejó de enviar datos.") : Errors.Friendly(ex);
                    Post(() => Finish(item, cts, DownloadState.Failed, message));
                    return;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(3 * attempts), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    static long PartLength(DownloadItem item)
    {
        try
        {
            var part = new FileInfo(item.FilePath + ".part");
            return part.Exists ? part.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    async Task TransferAsync(DownloadItem item, Run run, CancellationToken ct)
    {
        var afterThrottle = run.AfterThrottle;
        run.AfterThrottle = false;
        if (IsLocalPath(item.Url))
        {
            await CopyAsync(item, ct);
            return;
        }
        var part = item.FilePath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(item.FilePath)!);
        var have = PartLength(item);

        using var response = await SendAsync(item.Url, have, ct);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Either the file was already complete, or the partial file no longer matches: start again.
            if (have > 0 && response.Content.Headers.ContentRange?.Length == have)
            {
                File.Move(part, item.FilePath, overwrite: true);
                return;
            }
            File.Delete(part);
            throw new IOException("El archivo parcial no coincide con el del servidor.");
        }
        if (afterThrottle && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Most likely the provider still counts the connection just closed: wait and try again, without reconnecting any more.
            run.MayReconnect = false;
            throw new IOException("El servidor rechazó la reconexión.");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new PermanentException(L.T("El servidor ha rechazado la descarga. Puede que la cuenta no tenga conexiones libres o que el proveedor no permita descargar."));
        if (!response.IsSuccessStatusCode)
        {
            var text = L.T("El servidor respondió con un error {0} ({1}).", (int)response.StatusCode, response.ReasonPhrase);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests) throw new IOException(text);
            throw new PermanentException(text);
        }
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new PermanentException(L.T("El servidor no ha devuelto un archivo de vídeo."));

        var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) have = 0; // the server ignored the range: the whole file comes again
        // Reconnecting only pays off when the server continues from where the file was left.
        var resumable = append || response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);
        if (afterThrottle && !append) run.MayReconnect = false;
        var length = response.Content.Headers.ContentLength;
        var total = append
            ? response.Content.Headers.ContentRange?.Length ?? (length is { } rest ? have + rest : 0)
            : length ?? 0;
        if (total > 0 && FreeSpace(item.FilePath) is { } free && free < total - have)
            throw new PermanentException(L.T("No hay espacio suficiente en el disco ({0} libres).", DownloadItem.Size(free)));

        var startedAt = have;
        Post(() => item.SetProgress(startedAt, total, 0));
        // Time spent waiting for the server and for the disk, to tell which of the two limits the speed.
        var network = new Stopwatch();
        var disk = new Stopwatch();
        var elapsed = Stopwatch.StartNew();
        double peak = 0, burst = 0;
        var slowFor = new Stopwatch();

        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true))
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var buffer = new byte[1 << 17];
                var clock = Stopwatch.StartNew();
                long window = 0;
                double speed = 0;
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    network.Start();
                    var read = await source.ReadAsync(buffer, stall.Token);
                    network.Stop();
                    if (read == 0) break;
                    disk.Start();
                    try
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    catch (IOException ex) when ((ex.HResult & 0xFFFF) is 112 or 39)
                    {
                        throw new PermanentException(L.T("El disco se ha llenado."));
                    }
                    finally
                    {
                        disk.Stop();
                    }
                    have += read;
                    window += read;
                    if (clock.ElapsedMilliseconds < 500) continue;
                    var now = window / clock.Elapsed.TotalSeconds;
                    peak = Math.Max(peak, now);
                    speed = speed <= 0 ? now : speed * 0.7 + now * 0.3;
                    var (received, rate) = (have, speed);
                    Post(() =>
                    {
                        if (item.State == DownloadState.Downloading) item.SetProgress(received, total, rate);
                    });
                    clock.Restart();
                    window = 0;

                    if (!ReconnectWhenThrottled || !run.MayReconnect || !resumable || total <= 0) continue;
                    burst = Math.Max(burst, speed);
                    if (burst < ThrottleMinBurst || speed >= burst * ThrottleRatio)
                    {
                        slowFor.Reset();
                        continue;
                    }
                    if (!slowFor.IsRunning) slowFor.Start();
                    if (slowFor.Elapsed < ThrottleSlowFor || elapsed.Elapsed < ThrottleMinAge || total - have < ThrottleMinLeft) continue;
                    if (have - startedAt < ThrottleMinGain)
                    {
                        // The last connection hardly brought anything at full speed: reconnecting is not helping.
                        run.MayReconnect = false;
                        Log.Info($"Descarga «{item.Title}»: reconectar no recupera la velocidad; sigue con esta conexión.");
                        continue;
                    }
                    Log.Info($"Descarga «{item.Title}»: el servidor ha bajado de {DownloadItem.Size((long)burst)}/s a {DownloadItem.Size((long)speed)}/s; reconectando.");
                    throw new ThrottledException();
                }
            }
        }
        finally
        {
            var seconds = Math.Max(0.001, elapsed.Elapsed.TotalSeconds);
            Log.Info($"Descarga «{item.Title}»: {DownloadItem.Size(have - startedAt)} en {seconds:0} s · media {DownloadItem.Size((long)((have - startedAt) / seconds))}/s · " +
                     $"pico {DownloadItem.Size((long)peak)}/s · esperando al servidor {network.Elapsed.TotalSeconds / seconds:P0} · escribiendo en disco {disk.Elapsed.TotalSeconds / seconds:P0}");
        }
        ct.ThrowIfCancellationRequested();
        if (total > 0 && have < total) throw new IOException("La conexión se cortó antes de terminar.");
        File.Move(part, item.FilePath, overwrite: true);
    }

    /// <summary>
    /// «Downloading» from a folder or a NAS is copying the file, continuing where an interrupted copy was left.
    /// </summary>
    async Task CopyAsync(DownloadItem item, CancellationToken ct)
    {
        if (!File.Exists(item.Url)) throw new PermanentException(L.T("No se encuentra el archivo de origen."));
        var part = item.FilePath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(item.FilePath)!);
        await using (var source = new FileStream(item.Url, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        {
            var total = source.Length;
            var have = PartLength(item);
            if (have > total) have = 0;
            if (FreeSpace(item.FilePath) is { } free && free < total - have)
                throw new PermanentException(L.T("No hay espacio suficiente en el disco ({0} libres).", DownloadItem.Size(free)));
            source.Position = have;
            Post(() => item.SetProgress(have, total, 0));
            await using var target = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 20];
            var clock = Stopwatch.StartNew();
            long window = 0;
            double speed = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                try
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) is 112 or 39)
                {
                    throw new PermanentException(L.T("El disco se ha llenado."));
                }
                have += read;
                window += read;
                if (clock.ElapsedMilliseconds < 500) continue;
                var now = window / clock.Elapsed.TotalSeconds;
                speed = speed <= 0 ? now : speed * 0.7 + now * 0.3;
                var (received, rate) = (have, speed);
                Post(() =>
                {
                    if (item.State == DownloadState.Downloading) item.SetProgress(received, total, rate);
                });
                clock.Restart();
                window = 0;
            }
        }
        ct.ThrowIfCancellationRequested();
        File.Move(part, item.FilePath, overwrite: true);
    }

    /// <summary>
    /// Asks for the file from byte <paramref name="from"/> on. Providers usually answer with a redirect to the server
    /// that holds the file, often from https to plain http, which HttpClient refuses to follow by itself; those are
    /// followed here, as the player does.
    /// </summary>
    static async Task<HttpResponseMessage> SendAsync(string url, long from, CancellationToken ct)
    {
        var target = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var request = Http.Get(target.AbsoluteUri);
            if (from > 0) request.Headers.Range = new RangeHeaderValue(from, null);
            var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var redirect = response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            var location = response.Headers.Location;
            if (!redirect || location is null || hop >= 10) return response;
            response.Dispose();
            target = location.IsAbsoluteUri ? location : new Uri(target, location);
            if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
                throw new PermanentException(L.T("El servidor no ha devuelto un archivo de vídeo."));
        }
    }

    internal static long? FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null; // network shares and the like: let the write itself fail if there is no room
        }
    }

    // ----- File names -----

    string NewPath(DownloadRequest request)
    {
        var dir = string.IsNullOrWhiteSpace(request.Subfolder) ? Folder : Path.Combine(Folder, SafeName(request.Subfolder));
        var name = SafeName(request.FileName);
        var ext = ExtensionFor(request);
        var path = Path.Combine(dir, $"{name}.{ext}");
        for (var n = 2; IsTaken(path); n++) path = Path.Combine(dir, $"{name} ({n}).{ext}");
        return path;
    }

    bool IsTaken(string path) =>
        File.Exists(path) || File.Exists(path + ".part") ||
        _items.Any(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));

    static string ExtensionFor(DownloadRequest request)
    {
        static bool Valid(string e) => e.Length is > 0 and <= 5 && e.All(char.IsAsciiLetterOrDigit);
        var fromUrl = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ? Path.GetExtension(uri.AbsolutePath).TrimStart('.') : "";
        if (Valid(fromUrl)) return fromUrl.ToLowerInvariant();
        var given = (request.Extension ?? "").Trim().TrimStart('.');
        return Valid(given) ? given.ToLowerInvariant() : "mp4";
    }

    /// <summary>A name that Windows accepts as a file or folder name.</summary>
    public static string SafeName(string? text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (text ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray();
        var name = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (name.Length > 120) name = name[..120].Trim('.', ' ');
        return name.Length == 0 ? "video" : name;
    }
}
