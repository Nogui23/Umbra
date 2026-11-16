using System.Text.Json;
using System.Text.Json.Serialization;

namespace Umbra.Core;

/// <summary>
/// A copy of what the user has made in the app (profiles with their photos, favourites, progress, «Seguir viendo»,
/// locked categories and settings) to keep it or take it to another computer. Accounts go in only as a way to find
/// them again (name, server, user): never passwords, tokens or playlist links, nor the parental PIN.
/// </summary>
public static class Backup
{
    const string FormatName = "umbra-backup";
    const int CurrentVersion = 2;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed class File
    {
        /// <summary>Empty unless the file says it: any other JSON must not pass for a backup.</summary>
        public string Format { get; set; } = "";
        public int Version { get; set; } = 1;
        public DateTime CreatedUtc { get; set; }
        public string AppVersion { get; set; } = "";
        public Settings? Settings { get; set; }
        public List<ProfileData> Profiles { get; set; } = new();
        public List<AccountData> Accounts { get; set; } = new();
        /// <summary>Progress of what was imported into the library (which belongs to no account), by profile name.</summary>
        public Dictionary<string, UserData> LibraryByProfile { get; set; } = new();
        /// <summary>Version 1, before profiles: the library progress.</summary>
        public UserData? Library { get; set; }
    }

    public sealed class ProfileData
    {
        public string Name { get; set; } = "";
        public string Color { get; set; } = "";
        public string PreferredAudio { get; set; } = "";
        public string PreferredSubtitles { get; set; } = "";
        /// <summary>The photo as PNG in base64, or empty.</summary>
        public string Photo { get; set; } = "";
    }

    public sealed class AccountData
    {
        public string Name { get; set; } = "";
        public AccountType Type { get; set; }
        /// <summary>Server address, or folder for a folder account. Empty for an M3U playlist (its link is secret).</summary>
        public string Server { get; set; } = "";
        public string Username { get; set; } = "";
        /// <summary>Locked categories: shared by every profile.</summary>
        public HashSet<string> Locks { get; set; } = new();
        /// <summary>What each profile did in this account, by profile name.</summary>
        public Dictionary<string, UserData> ByProfile { get; set; } = new();
        /// <summary>Version 1, before profiles: the data of the account.</summary>
        public UserData? Data { get; set; }
    }

    public sealed record Result(int Restored, IReadOnlyList<string> Missing, int NewProfiles, Settings? Settings);

    /// <summary>The data of a profile in a list that is in memory now (newer than its file).</summary>
    public sealed record Open(string AccountId, string ProfileId, UserData Data);

    public static string LocksFile(string accountId) => $"locks_{accountId}.json";

    /// <summary>Settings that belong to this computer or are private: kept as they are on both sides.</summary>
    static void KeepLocal(Settings target, Settings local)
    {
        target.ParentalPin = local.ParentalPin;
        target.LastAccountId = local.LastAccountId;
        target.LastProfileId = local.LastProfileId;
        target.DownloadFolder = local.DownloadFolder;
        // What GitHub said is only trusted when this computer asked.
        target.LastUpdateCheckUtc = local.LastUpdateCheckUtc;
        target.LatestVersion = local.LatestVersion;
        target.LatestVersionUrl = local.LatestVersionUrl;
    }

    /// <summary>A backup is a few megabytes at most (photos are 256×256); anything far bigger is not one.</summary>
    const long MaxFileBytes = 64L << 20;
    const int MaxPhotoBytes = 2 << 20;

    static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// A folder account may come back from a backup on its own (it has nothing secret), but only for an ordinary
    /// folder or share: never a device path such as «\\?\» or «\\.\».
    /// </summary>
    static bool IsPlainFolder(string path) =>
        path.Length is > 2 and < 260
        && Path.IsPathFullyQualified(path)
        && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
        && !path.StartsWith("//", StringComparison.Ordinal)
        && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
        && !path.Split('\\', '/').Contains("..");

    static Settings Copy(Settings s) => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s, Json), Json) ?? new Settings();

    static UserData Load(string accountId, string profileId, Open? open) =>
        open is not null && open.AccountId == accountId && open.ProfileId == profileId
            ? open.Data
            : JsonStore.Load<UserData>(ProfileStore.DataFile(accountId, profileId));

    public static void Export(string path, Settings settings, IEnumerable<Account> accounts, ProfileStore profiles, Open? open)
    {
        var file = new File { Format = FormatName, Version = CurrentVersion, CreatedUtc = DateTime.UtcNow, AppVersion = Updates.Current.ToString(3) };
        var copy = Copy(settings);
        KeepLocal(copy, new Settings { ParentalPin = "", LastAccountId = null, LastProfileId = null, DownloadFolder = "" });
        copy.PreferredAudio = copy.PreferredSubtitles = "";
        file.Settings = copy;
        foreach (var p in profiles.Items)
        {
            var photo = "";
            try
            {
                if (p.HasPhoto && System.IO.File.Exists(ProfileStore.PhotoPath(p)))
                    photo = Convert.ToBase64String(System.IO.File.ReadAllBytes(ProfileStore.PhotoPath(p)));
            }
            catch (Exception) { }
            file.Profiles.Add(new ProfileData { Name = p.Name, Color = p.Color, PreferredAudio = p.PreferredAudio, PreferredSubtitles = p.PreferredSubtitles, Photo = photo });
            file.LibraryByProfile[p.Name] = Load(LibraryArchive.LocalAccount, p.Id, open);
        }
        foreach (var account in accounts)
        {
            var data = new AccountData
            {
                Name = account.Name,
                Type = account.Type,
                Server = account.Type == AccountType.M3U ? "" : account.Server,
                Username = account.Username,
                Locks = JsonStore.Load<HashSet<string>>(LocksFile(account.Id)),
            };
            foreach (var p in profiles.Items)
            {
                var mine = Load(account.Id, p.Id, open);
                data.Locks.UnionWith(mine.LockedCategories);
                data.ByProfile[p.Name] = mine;
            }
            file.Accounts.Add(data);
        }
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    public static File Read(string path)
    {
        File? file;
        if (new FileInfo(path).Length > MaxFileBytes) throw new SourceException("Este archivo no es una copia de seguridad de Umbra.");
        try
        {
            file = JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            file = null;
        }
        if (file is null || file.Format != FormatName) throw new SourceException("Este archivo no es una copia de seguridad de Umbra.");
        if (file.Version > CurrentVersion) throw new SourceException("Esta copia se hizo con una versión más nueva de Umbra.");
        // Lists that are written with «null» in the file come back as null: treat them as empty.
        file.Profiles ??= new();
        file.Accounts ??= new();
        file.LibraryByProfile ??= new();
        file.Profiles.RemoveAll(p => p is null);
        file.Accounts.RemoveAll(a => a is null);
        foreach (var p in file.Profiles)
        {
            p.Name = (p.Name ?? "").Trim();
            if (p.Name.Length > 40) p.Name = p.Name[..40];
            p.Color ??= "";
            p.PreferredAudio ??= "";
            p.PreferredSubtitles ??= "";
            p.Photo ??= "";
        }
        foreach (var a in file.Accounts)
        {
            a.Name ??= "";
            a.Server ??= "";
            a.Username ??= "";
            a.Locks ??= new();
            a.ByProfile ??= new();
        }
        return file;
    }

    /// <summary>The saved account a copied one belongs to: same kind and same login (or same name for a playlist).</summary>
    public static Account? Match(AccountData copied, IEnumerable<Account> accounts) => accounts.FirstOrDefault(a =>
        a.Type == copied.Type && (a.Type switch
        {
            AccountType.M3U => string.Equals(a.Name, copied.Name, StringComparison.CurrentCultureIgnoreCase),
            AccountType.Folder => string.Equals(a.Server.TrimEnd('\\', '/'), copied.Server.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(a.Server, copied.Server, StringComparison.OrdinalIgnoreCase) && a.Username == copied.Username,
        }));

    /// <summary>Adds what is in <paramref name="from"/> to <paramref name="into"/>; where both have something, the newest wins.</summary>
    public static void Merge(UserData into, UserData? from)
    {
        if (from is null) return;
        // A file edited by hand may say «null» for any list.
        from.Favorites ??= new();
        from.LockedCategories ??= new();
        from.Watched ??= new();
        from.Resume ??= new();
        from.ResumeUtc ??= new();
        from.Series ??= new();
        from.RecentChannels ??= new();
        into.Favorites.UnionWith(from.Favorites.Where(k => k is not null));
        into.LockedCategories.UnionWith(from.LockedCategories.Where(k => k is not null));
        into.Watched.UnionWith(from.Watched.Where(k => k is not null));
        foreach (var (key, ms) in from.Resume)
        {
            if (ms < 0) continue;
            var when = from.ResumeUtc.GetValueOrDefault(key);
            if (into.Resume.ContainsKey(key) && into.ResumeUtc.GetValueOrDefault(key) >= when) continue;
            into.Resume[key] = ms;
            into.ResumeUtc[key] = when;
        }
        foreach (var (key, progress) in from.Series)
        {
            if (progress is null) continue;
            progress.Episode ??= "";
            progress.Label ??= "";
            if (into.Series.TryGetValue(key, out var mine) && mine.Utc >= progress.Utc) continue;
            into.Series[key] = progress;
        }
        foreach (var id in from.RecentChannels)
            if (id is not null && !into.RecentChannels.Contains(id) && into.RecentChannels.Count < 40) into.RecentChannels.Add(id);
        into.LastChannelId ??= from.LastChannelId;
    }

    /// <summary>
    /// Puts the copy back. Profiles are matched by name (the missing ones are created, with their photo); each
    /// account's data goes into the saved account it belongs to (folder accounts that are not there are added, since
    /// they have nothing secret). Returns the settings to apply, with the local ones kept.
    /// <paramref name="mergeOpen"/> receives what belongs to the profile and list in memory now.
    /// </summary>
    public static Result Restore(File file, AccountStore store, ProfileStore profiles, Settings current, Account? openAccount,
        Open? open, Action<UserData> mergeOpen)
    {
        // A copy made before profiles belongs to the profile in use.
        var fallback = profiles.Items.FirstOrDefault(p => p.Id == open?.ProfileId) ?? profiles.Items[0];
        var byName = new Dictionary<string, Profile>(StringComparer.CurrentCultureIgnoreCase);
        var newProfiles = 0;
        foreach (var copied in file.Profiles.Where(p => p.Name.Trim().Length > 0))
        {
            var profile = profiles.Items.FirstOrDefault(p => string.Equals(p.Name, copied.Name, StringComparison.CurrentCultureIgnoreCase));
            if (profile is null)
            {
                profile = profiles.Add(copied.Name.Trim());
                if (ProfileStore.Colors.Contains(copied.Color)) profile.Color = copied.Color;
                profile.PreferredAudio = copied.PreferredAudio;
                profile.PreferredSubtitles = copied.PreferredSubtitles;
                newProfiles++;
                // Only a real PNG of a sensible size: it is shown by the image decoder of Windows.
                if (copied.Photo.Length is > 0 and <= MaxPhotoBytes * 4 / 3 + 4)
                {
                    try
                    {
                        var png = Convert.FromBase64String(copied.Photo);
                        if (png.Length <= MaxPhotoBytes && png.AsSpan().StartsWith(PngSignature)) profiles.SetPhoto(profile, png);
                    }
                    catch (Exception) { }
                }
                profiles.Save();
            }
            byName[copied.Name] = profile;
        }
        Profile ProfileFor(string name) => byName.GetValueOrDefault(name) ?? fallback;

        var known = store.Items.Concat(openAccount is not null && store.Items.All(a => a.Id != openAccount.Id) ? [openAccount] : []).ToList();
        var restored = 0;
        var missing = new List<string>();
        foreach (var copied in file.Accounts)
        {
            var account = Match(copied, known);
            if (account is null && copied.Type == AccountType.Folder && IsPlainFolder(copied.Server))
            {
                account = store.Upsert(new Account { Type = AccountType.Folder, Name = copied.Name, Server = copied.Server });
                store.Save();
            }
            if (account is null)
            {
                missing.Add(copied.Name);
                continue;
            }
            var locks = JsonStore.Load<HashSet<string>>(LocksFile(account.Id));
            locks.UnionWith(copied.Locks);
            if (copied.Data is { } old)
            {
                locks.UnionWith(old.LockedCategories);
                Put(account.Id, fallback, old);
            }
            foreach (var (name, data) in copied.ByProfile) Put(account.Id, ProfileFor(name), data);
            JsonStore.Save(LocksFile(account.Id), locks);
            if (open?.AccountId == account.Id) mergeOpen(new UserData { LockedCategories = locks });
            restored++;
        }
        if (file.Library is { } library) Put(LibraryArchive.LocalAccount, fallback, library);
        foreach (var (name, data) in file.LibraryByProfile) Put(LibraryArchive.LocalAccount, ProfileFor(name), data);

        Settings? settings = null;
        if (file.Settings is { } copiedSettings)
        {
            settings = Copy(copiedSettings);
            KeepLocal(settings, current);
            settings.Normalize();
        }
        return new Result(restored, missing, newProfiles, settings);

        void Put(string accountId, Profile profile, UserData data)
        {
            if (open is not null && accountId == open.AccountId && profile.Id == open.ProfileId)
            {
                mergeOpen(data);
                return;
            }
            var mine = JsonStore.Load<UserData>(ProfileStore.DataFile(accountId, profile.Id));
            Merge(mine, data);
            JsonStore.Save(ProfileStore.DataFile(accountId, profile.Id), mine);
        }
    }
}
