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
    /// <summary>Folder for downloaded movies and episodes; empty for the default (Videos\Umbra).</summary>
    public string DownloadFolder { get; set; } = "";
    /// <summary>Downloads running at once (1 to 3). Each one uses a connection of the account.</summary>
    public int MaxParallelDownloads { get; set; } = 1;
    /// <summary>Reconnect a download when the provider slows it down. Off by default: it works around the provider's limit.</summary>
    public bool ReconnectSlowDownloads { get; set; }
    /// <summary>Before profiles existed: preferred audio, now in each <see cref="Profile"/>. Only read to move it there.</summary>
    public string PreferredAudio { get; set; } = "";
    /// <summary>Before profiles existed: preferred subtitles, now in each <see cref="Profile"/>.</summary>
    public string PreferredSubtitles { get; set; } = "";
    /// <summary>Profile in use the last time, chosen again when the app opens with a single profile.</summary>
    public string? LastProfileId { get; set; }
    /// <summary>Ask GitHub once a day, when the app opens, whether there is a newer version.</summary>
    public bool CheckUpdates { get; set; } = true;
    public DateTime LastUpdateCheckUtc { get; set; }
    /// <summary>Latest version seen published (empty when none), to keep showing it between checks.</summary>
    public string LatestVersion { get; set; } = "";
    public string LatestVersionUrl { get; set; } = "";
    /// <summary>Windows notification when a download ends while the app is not in front.</summary>
    public bool NotifyDownloads { get; set; } = true;

    /// <summary>
    /// Puts every value back inside what the app can use. The file may have been edited by hand or come from a
    /// backup: an hour offset of millions would make dates overflow, a line break in the user agent would break the
    /// requests.
    /// </summary>
    public Settings Normalize()
    {
        if (Language != L.Spanish && Language != L.EnglishCode) Language = L.Spanish;
        if (StreamFormat is not ("ts" or "m3u8")) StreamFormat = "ts";
        NetworkCachingMs = Math.Clamp(NetworkCachingMs, 100, 60_000);
        EpgOffsetHours = Math.Clamp(EpgOffsetHours, -24, 24);
        CacheHours = Math.Clamp(CacheHours, 1, 24 * 30);
        Volume = Math.Clamp(Volume, 0, 150);
        UiZoom = Math.Clamp(UiZoom, 70, 160);
        MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 3);
        UserAgent = (UserAgent ?? "").Trim();
        if (UserAgent.Length == 0 || UserAgent.Length > 300 || UserAgent.Any(char.IsControl)) UserAgent = DefaultUserAgent;
        ParentalPin ??= "0000";
        DownloadFolder ??= "";
        PreferredAudio ??= "";
        PreferredSubtitles ??= "";
        LatestVersion ??= "";
        LatestVersionUrl ??= "";
        return this;
    }
}

/// <summary>Where a series was left: the episode to continue with, for the «Seguir viendo» shelf.</summary>
public sealed class SeriesProgress
{
    /// <summary>Key of the episode ("episode:ID").</summary>
    public string Episode { get; set; } = "";
    /// <summary>«T2 E5», as shown on the shelf.</summary>
    public string Label { get; set; } = "";
    public DateTime Utc { get; set; }
}

/// <summary>Per-account state: favourites, resume points, watched episodes, locked categories, recent channels.</summary>
public sealed class UserData
{
    public HashSet<string> Favorites { get; set; } = new();
    public Dictionary<string, long> Resume { get; set; } = new();
    /// <summary>When each resume point was last saved, to put the most recent first in «Seguir viendo».</summary>
    public Dictionary<string, DateTime> ResumeUtc { get; set; } = new();
    /// <summary>Series being watched, by series key ("series:ID").</summary>
    public Dictionary<string, SeriesProgress> Series { get; set; } = new();
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
            // What every profile did in this list, and its shared parental locks.
            foreach (var data in ProfileStore.FilesOfAccount(account.Id)) System.IO.File.Delete(data);
            var locks = Path.Combine(AppPaths.DataDir, $"locks_{account.Id}.json");
            if (System.IO.File.Exists(locks)) System.IO.File.Delete(locks);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron borrar los datos de la cuenta", ex);
        }
    }

    static bool SameLogin(Account a, Account b) =>
        a.Type == b.Type && a.Type switch
        {
            AccountType.Xtream or AccountType.Jellyfin =>
                string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase) && a.Username == b.Username,
            AccountType.Folder => string.Equals(a.Server.TrimEnd('\\', '/'), b.Server.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase),
            AccountType.Plex => string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase)
                                && (a.Username.Length > 0 || b.Username.Length > 0 ? a.Username == b.Username : a.Password == b.Password),
            _ => string.Equals(a.M3uSource, b.M3uSource, StringComparison.OrdinalIgnoreCase),
        };
}
