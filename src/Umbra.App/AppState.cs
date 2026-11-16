using Umbra.Core;

namespace Umbra.App;

/// <summary>Session state shared by all screens: settings, saved accounts and the open account's content.</summary>
public sealed class AppState
{
    public static AppState I { get; } = new();

    readonly HashSet<string> _unlocked = new();
    string? _dataId; // account whose favourites and progress are in Data

    public Settings Settings { get; private set; } = new();
    public AccountStore Accounts { get; } = new();
    public Account? Account { get; private set; }
    public IContentSource? Source { get; private set; }
    public ContentLibrary? Library { get; private set; }
    public UserData Data { get; private set; } = new();

    public event Action? LibraryChanged;

    public void Load(bool loadAccounts = true)
    {
        Settings = JsonStore.Load<Settings>("settings.json");
        if (loadAccounts) Accounts.Load();
        ApplySettings();
    }

    void ApplySettings()
    {
        Http.UserAgent = string.IsNullOrWhiteSpace(Settings.UserAgent) ? Settings.DefaultUserAgent : Settings.UserAgent;
        Downloads.Folder = string.IsNullOrWhiteSpace(Settings.DownloadFolder) ? DownloadManager.DefaultFolder : Settings.DownloadFolder;
        Downloads.MaxParallel = Settings.MaxParallelDownloads;
        Downloads.ReconnectWhenThrottled = Settings.ReconnectSlowDownloads;
    }

    public void SaveSettings()
    {
        ApplySettings();
        try { JsonStore.Save("settings.json", Settings); }
        catch (Exception ex) { Log.Error("No se pudieron guardar los ajustes", ex); }
    }

    public void SetSession(Account account, IContentSource source, ContentLibrary library, bool persist = true)
    {
        var sameAccount = Account?.Id == account.Id;
        Account = account;
        Source = source;
        Library = library;
        if (!sameAccount)
        {
            Data = JsonStore.Load<UserData>(DataFile(account));
            _unlocked.Clear();
        }
        _dataId = account.Id;
        ApplyUserData();
        if (persist)
        {
            account.LastUsedUtc = DateTime.UtcNow;
            Accounts.Upsert(account);
            Accounts.Save();
            Settings.LastAccountId = account.Id;
            SaveSettings();
        }
        LibraryChanged?.Invoke();
        if (Downloads.HasPending) EnsureAccountInfo();
    }

    public void EndSession(bool forgetLastAccount)
    {
        Account = null;
        Source = null;
        Library = null;
        Data = new UserData();
        _dataId = null;
        _unlocked.Clear();
        if (forgetLastAccount)
        {
            Settings.LastAccountId = null;
            SaveSettings();
        }
    }

    static string DataFile(Account account) => DataFile(account.Id);
    static string DataFile(string accountId) => $"userdata_{accountId}.json";

    /// <summary>
    /// With no account open (the library reached from the sign-in screen), loads the progress of the account a
    /// download belongs to, so it resumes where it was left and what is watched now is remembered.
    /// </summary>
    public void UseDataOf(string accountId)
    {
        if (Account is not null || accountId.Length == 0 || _dataId == accountId) return;
        Data = JsonStore.Load<UserData>(DataFile(accountId));
        _dataId = accountId;
    }

    public void SaveData()
    {
        if (_dataId is null) return;
        try { JsonStore.Save(DataFile(_dataId), Data); }
        catch (Exception ex) { Log.Error("No se pudieron guardar los datos de usuario", ex); }
    }

    public static string Key(MediaKind kind, string id) => kind switch
    {
        MediaKind.Live => "live:",
        MediaKind.Movie => "movie:",
        _ => "series:",
    } + id;

    public static string EpisodeKey(string id) => "episode:" + id;

    static string CategoryKey(Category c) => $"{c.Kind}:{c.Id}".ToLowerInvariant();

    void ApplyUserData()
    {
        if (Library is null) return;
        foreach (var c in Library.Channels) c.IsFavorite = Data.Favorites.Contains(Key(MediaKind.Live, c.Id));
        foreach (var m in Library.Movies) m.IsFavorite = Data.Favorites.Contains(Key(MediaKind.Movie, m.Id));
        foreach (var s in Library.Series) s.IsFavorite = Data.Favorites.Contains(Key(MediaKind.Series, s.Id));
        foreach (var c in Library.LiveCategories.Concat(Library.MovieCategories).Concat(Library.SeriesCategories))
            c.IsLocked = Data.LockedCategories.Contains(CategoryKey(c));
    }

    // ----- Favourites -----

    public void ToggleFavorite(Channel channel)
    {
        channel.IsFavorite = !channel.IsFavorite;
        SetFavorite(Key(MediaKind.Live, channel.Id), channel.IsFavorite);
    }

    public void ToggleFavorite(PosterItem item)
    {
        item.IsFavorite = !item.IsFavorite;
        SetFavorite(Key(item.Kind, item.Id), item.IsFavorite);
    }

    void SetFavorite(string key, bool on)
    {
        if (on) Data.Favorites.Add(key);
        else Data.Favorites.Remove(key);
        SaveData();
    }

    // ----- Progress -----

    public long ResumeOf(string key) => Data.Resume.GetValueOrDefault(key);

    public void SaveResume(string key, long ms)
    {
        Data.Resume[key] = ms;
        SaveData();
    }

    public void MarkWatched(string key)
    {
        Data.Resume.Remove(key);
        Data.Watched.Add(key);
        SaveData();
    }

    public void ClearProgress(string key)
    {
        Data.Resume.Remove(key);
        Data.Watched.Remove(key);
        SaveData();
    }

    /// <summary>Number of movies with a saved resume point (the «Seguir viendo» shelf).</summary>
    public int MovieResumeCount => Data.Resume.Keys.Count(k => k.StartsWith("movie:", StringComparison.Ordinal));

    /// <summary>Forgets the resume point of every movie, emptying the «Seguir viendo» shelf.</summary>
    public void ClearMovieResumes()
    {
        foreach (var key in Data.Resume.Keys.Where(k => k.StartsWith("movie:", StringComparison.Ordinal)).ToList())
            Data.Resume.Remove(key);
        SaveData();
    }

    public void AddRecentChannel(Channel channel)
    {
        Data.LastChannelId = channel.Id;
        Data.RecentChannels.Remove(channel.Id);
        Data.RecentChannels.Insert(0, channel.Id);
        if (Data.RecentChannels.Count > 40) Data.RecentChannels.RemoveRange(40, Data.RecentChannels.Count - 40);
        SaveData();
    }

    // ----- Downloads -----

    static DownloadManager Downloads => DownloadManager.I;

    /// <summary>The download of that movie or episode in the open account, if there is one.</summary>
    public DownloadItem? DownloadOf(string key) => Account is null ? null : Downloads.Find(Account.Id, key);

    /// <summary>The downloaded file when there is one, so it plays without using the connection; otherwise the stream.</summary>
    public string PlayUrl(string key, string streamUrl) =>
        Account is null ? streamUrl : Downloads.LocalFile(Account.Id, key) ?? streamUrl;

    /// <summary>Queues a movie or episode for download; false when it is already on the list or cannot be saved.</summary>
    public bool StartDownload(string key, string url, string title, string subtitle, string image,
        string? subfolder, string fileName, string extension, bool isEpisode, bool quiet = false, DownloadInfo? info = null)
    {
        if (Account is null || Downloads.Find(Account.Id, key) is not null) return false;
        if (!DownloadManager.CanDownload(url))
        {
            if (!quiet)
                Sheet.Warn("No se puede descargar", "Este contenido se emite como streaming y no como un archivo, así que no se puede guardar.");
            return false;
        }
        var item = Downloads.Enqueue(new DownloadRequest(Account.Id, key, url, title, subtitle, image, subfolder, fileName, extension, isEpisode));
        if (info is not null) Downloads.SetInfo(item, info);
        EnsureAccountInfo();
        return true;
    }

    /// <summary>True when the downloads can take every connection the account allows, leaving none to play a stream.</summary>
    public bool DownloadsLeaveNoConnection =>
        Source?.Info is { MaxConnections: > 0 } info && info.MaxConnections <= Downloads.MaxParallel;

    /// <summary>The connection limit decides whether downloads must wait while a stream plays; ask for it if unknown.</summary>
    void EnsureAccountInfo()
    {
        var source = Source;
        if (source is null || source.Info is not null) return;
        _ = Task.Run(async () =>
        {
            try { await source.RefreshInfoAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Error("No se pudo consultar el límite de conexiones", ex); }
        });
    }

    // ----- Parental control -----

    /// <summary>True when the category is locked and has not been unlocked with the PIN in this session.</summary>
    public bool IsHidden(Category? category) =>
        category is not null && category.IsLocked && !_unlocked.Contains(CategoryKey(category));

    public bool IsHidden(MediaKind kind, string categoryId) => IsHidden(Library?.CategoryOf(kind, categoryId));

    public bool TryUnlock(Category category)
    {
        if (!IsHidden(category)) return true;
        if (!CheckPin(L.T("«{0}» está bloqueada", category.Name), "Introduce el PIN de control parental para entrar.")) return false;
        _unlocked.Add(CategoryKey(category));
        return true;
    }

    public bool CheckPin(string title, string message = "")
    {
        var pin = Sheet.AskPin(title, message);
        if (pin is null) return false;
        if (pin == Settings.ParentalPin) return true;
        Sheet.Warn("PIN incorrecto", "El PIN que has escrito no es correcto.");
        return false;
    }

    public void SetLocked(Category category, bool locked)
    {
        var key = CategoryKey(category);
        if (locked)
        {
            Data.LockedCategories.Add(key);
            _unlocked.Remove(key);
        }
        else
        {
            Data.LockedCategories.Remove(key);
        }
        category.IsLocked = locked;
        SaveData();
    }
}
