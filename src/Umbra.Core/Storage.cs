namespace Umbra.Core;

public sealed class Settings
{
    public const string DefaultUserAgent = "VLC/3.0.21 LibVLC/3.0.21";

    /// <summary>Interface language: "es" (Spanish) or "en" (English).</summary>
    public string Language { get; set; } = L.Spanish;

    /// <summary>"ts" (MPEG-TS) or "m3u8" (HLS) for Xtream live channels.</summary>
    public string StreamFormat { get; set; } = "ts";
    public bool HardwareDecoding { get; set; } = true;
    public int NetworkCachingMs { get; set; } = 1500;
    public bool AutoPlayNextEpisode { get; set; } = true;
    public bool AutoPlayLastChannel { get; set; }
    public int EpgOffsetHours { get; set; }
    public string UserAgent { get; set; } = DefaultUserAgent;
    public int CacheHours { get; set; } = 24;
    public string ParentalPin { get; set; } = "0000";
    public int Volume { get; set; } = 100;
    public string? LastAccountId { get; set; }
    /// <summary>Open the app covering the whole screen (F11 switches to a window).</summary>
    public bool StartFullscreen { get; set; } = true;
    /// <summary>Extra interface zoom in percent, applied on top of the automatic scale for the screen size.</summary>
    public int UiZoom { get; set; } = 100;
}

/// <summary>Per-account state: favourites, resume points, watched episodes, locked categories, recent channels.</summary>
public sealed class UserData
{
    public HashSet<string> Favorites { get; set; } = new();
    public Dictionary<string, long> Resume { get; set; } = new();
    public HashSet<string> Watched { get; set; } = new();
    public HashSet<string> LockedCategories { get; set; } = new();
    public List<string> RecentChannels { get; set; } = new();
    public string? LastChannelId { get; set; }
}

public sealed class AccountStore
{
    const string File = "accounts.json";

    public List<Account> Items { get; private set; } = new();

    public void Load()
    {
        var list = JsonStore.Load<List<Account>>(File);
        foreach (var a in list)
        {
            a.PasswordUnreadable = !Secret.TryUnprotect(a.PasswordEnc, out var password);
            a.M3uSourceUnreadable = !Secret.TryUnprotect(a.M3uSourceEnc, out var m3u);
            a.Password = password;
            a.M3uSource = m3u;
        }
        Items = list.OrderByDescending(a => a.LastUsedUtc).ToList();
    }

    public void Save()
    {
        foreach (var a in Items)
        {
            // A secret that could not be decrypted is kept as stored instead of being overwritten with nothing.
            if (a.Password.Length > 0 || !a.PasswordUnreadable) a.PasswordEnc = Secret.Protect(a.Password);
            if (a.M3uSource.Length > 0 || !a.M3uSourceUnreadable) a.M3uSourceEnc = Secret.Protect(a.M3uSource);
        }
        JsonStore.Save(File, Items);
    }

    /// <summary>Adds the account, or updates an existing one with the same login, and returns the stored instance.</summary>
    public Account Upsert(Account account)
    {
        var existing = Items.FirstOrDefault(a => a.Id == account.Id) ?? Items.FirstOrDefault(a => SameLogin(a, account));
        if (existing is null)
        {
            Items.Insert(0, account);
            return account;
        }
        existing.Name = account.Name;
        existing.Type = account.Type;
        existing.Server = account.Server;
        existing.Username = account.Username;
        existing.Password = account.Password;
        existing.M3uSource = account.M3uSource;
        existing.EpgUrl = account.EpgUrl;
        return existing;
    }

    public void Remove(Account account)
    {
        Items.RemoveAll(a => a.Id == account.Id);
        Save();
        try
        {
            var cache = Path.Combine(AppPaths.CacheRoot, account.Id);
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            var data = Path.Combine(AppPaths.DataDir, $"userdata_{account.Id}.json");
            if (System.IO.File.Exists(data)) System.IO.File.Delete(data);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron borrar los datos de la cuenta", ex);
        }
    }

    static bool SameLogin(Account a, Account b) =>
        a.Type == b.Type && (a.Type == AccountType.Xtream
            ? string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase) && a.Username == b.Username
            : string.Equals(a.M3uSource, b.M3uSource, StringComparison.OrdinalIgnoreCase));
}
