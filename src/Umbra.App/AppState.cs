using Umbra.Core;

namespace Umbra.App;

/// <summary>Session state shared by all screens: settings, saved accounts and the open account's content.</summary>
public sealed class AppState
{
    public static AppState I { get; } = new();

    readonly HashSet<string> _unlocked = new();

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
        Http.UserAgent = string.IsNullOrWhiteSpace(Settings.UserAgent) ? Settings.DefaultUserAgent : Settings.UserAgent;
    }

    public void SaveSettings()
    {
        Http.UserAgent = string.IsNullOrWhiteSpace(Settings.UserAgent) ? Settings.DefaultUserAgent : Settings.UserAgent;
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
    }

    public void EndSession(bool forgetLastAccount)
    {
        Account = null;
        Source = null;
        Library = null;
        Data = new UserData();
        _unlocked.Clear();
        if (forgetLastAccount)
        {
            Settings.LastAccountId = null;
            SaveSettings();
        }
    }

    static string DataFile(Account account) => $"userdata_{account.Id}.json";

    public void SaveData()
    {
        if (Account is null) return;
        try { JsonStore.Save(DataFile(Account), Data); }
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

    public void AddRecentChannel(Channel channel)
    {
        Data.LastChannelId = channel.Id;
        Data.RecentChannels.Remove(channel.Id);
        Data.RecentChannels.Insert(0, channel.Id);
        if (Data.RecentChannels.Count > 40) Data.RecentChannels.RemoveRange(40, Data.RecentChannels.Count - 40);
        SaveData();
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
