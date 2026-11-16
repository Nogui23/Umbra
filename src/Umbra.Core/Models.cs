using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Umbra.Core;

public enum AccountType { Xtream, M3U, Jellyfin, Folder, Plex }

public enum MediaKind { Live, Movie, Series }

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A user's IPTV login: Xtream Codes (server + user + password) or an M3U playlist.</summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public AccountType Type { get; set; }
    public string Server { get; set; } = "";
    public string Username { get; set; } = "";
    public string EpgUrl { get; set; } = "";
    public DateTime LastUsedUtc { get; set; }

    // Secrets are kept in memory in clear text and persisted encrypted with Windows DPAPI.
    public string PasswordEnc { get; set; } = "";
    public string M3uSourceEnc { get; set; } = "";
    [JsonIgnore] public string Password { get; set; } = "";
    [JsonIgnore] public string M3uSource { get; set; } = "";
    /// <summary>The stored secret could not be decrypted on load; saving must not replace it with an empty value.</summary>
    [JsonIgnore] public bool PasswordUnreadable { get; set; }
    [JsonIgnore] public bool M3uSourceUnreadable { get; set; }

    [JsonIgnore]
    public string KindText => Type switch
    {
        AccountType.Xtream => "Xtream Codes",
        AccountType.Jellyfin => "Jellyfin · Emby",
        AccountType.Plex => "Plex",
        AccountType.Folder => L.T("Carpeta · NAS"),
        _ => L.T("Lista M3U"),
    };

    /// <summary>Signs in with a user on a server (as opposed to a playlist or a folder).</summary>
    [JsonIgnore] public bool HasLogin => Type is AccountType.Xtream or AccountType.Jellyfin;

    [JsonIgnore]
    public string DetailText
    {
        get
        {
            if (HasLogin) return $"{Username} · {HostOf(Server)}";
            if (Type == AccountType.Folder) return Server;
            if (Type == AccountType.Plex) return Username.Length > 0 ? $"{Username} · {HostOf(Server)}" : HostOf(Server);
            if (File.Exists(M3uSource)) return Path.GetFileName(M3uSource);
            return HostOf(M3uSource);
        }
    }

    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : url;
}

public sealed class AccountInfo
{
    public string Status { get; set; } = "";
    public DateTime? Expires { get; set; }
    public DateTime? Created { get; set; }
    public bool IsTrial { get; set; }
    public int ActiveConnections { get; set; }
    public int MaxConnections { get; set; }
    public string Formats { get; set; } = "";
    public string Message { get; set; } = "";
    public string Timezone { get; set; } = "";

    public bool IsActive => Status.Length == 0 || Status.Equals("Active", StringComparison.OrdinalIgnoreCase);
}

public sealed class Category : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public MediaKind Kind { get; init; }
    public string Icon { get; init; } = "";
    public bool IsVirtual => Id.StartsWith('@');

    int _count;
    public int Count { get => _count; set => Set(ref _count, value); }

    bool _locked;
    public bool IsLocked { get => _locked; set => Set(ref _locked, value); }
}

public sealed class Channel : ObservableObject
{
    public string Id { get; init; } = "";
    public int Num { get; set; }
    public string Name { get; init; } = "";
    public string Logo { get; init; } = "";
    public string EpgId { get; init; } = "";
    public string CategoryId { get; set; } = "";
    public bool HasArchive { get; init; }
    public int ArchiveDays { get; init; }
    public string? Url { get; init; }
    public string? CatchupMode { get; init; }
    public string? CatchupSource { get; init; }

    bool _fav;
    public bool IsFavorite { get => _fav; set => Set(ref _fav, value); }
}

public abstract class PosterItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
    public double Rating { get; init; }
    public string CategoryId { get; set; } = "";
    public long Added { get; init; }
    public abstract MediaKind Kind { get; }

    public string? RatingText => Rating > 0 ? Rating.ToString("0.0", CultureInfo.InvariantCulture) : null;

    bool _fav;
    public bool IsFavorite { get => _fav; set => Set(ref _fav, value); }
}

public sealed class Movie : PosterItem
{
    public string Extension { get; set; } = "mp4";
    public string? Url { get; init; }
    public override MediaKind Kind => MediaKind.Movie;
}

public sealed class SeriesItem : PosterItem
{
    public string Plot { get; init; } = "";
    public string Genre { get; init; } = "";
    public string ReleaseDate { get; init; } = "";
    public override MediaKind Kind => MediaKind.Series;
}

public sealed class MovieDetail
{
    public string Name { get; init; } = "";
    public string Plot { get; init; } = "";
    public string Genre { get; init; } = "";
    public string Director { get; init; } = "";
    public string Cast { get; init; } = "";
    public string ReleaseDate { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Country { get; init; } = "";
    public double Rating { get; init; }
    public string Backdrop { get; init; } = "";
    public string Poster { get; init; } = "";
    public string Trailer { get; init; } = "";
}

public sealed class SeriesDetail
{
    public string Name { get; init; } = "";
    public string Plot { get; init; } = "";
    public string Genre { get; init; } = "";
    public string Director { get; init; } = "";
    public string Cast { get; init; } = "";
    public string ReleaseDate { get; init; } = "";
    public double Rating { get; init; }
    public string Backdrop { get; init; } = "";
    public string Cover { get; init; } = "";
    public string Trailer { get; init; } = "";
    public List<Season> Seasons { get; init; } = new();
}

public sealed class Season
{
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public string Cover { get; init; } = "";
    public List<Episode> Episodes { get; init; } = new();
    public string Label => string.IsNullOrWhiteSpace(Name) ? L.T("Temporada {0}", Number) : Name;
}

public sealed class Episode : ObservableObject
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int Season { get; init; }
    public int Number { get; init; }
    public string Extension { get; init; } = "mp4";
    public string Plot { get; init; } = "";
    public string Image { get; init; } = "";
    public int DurationSecs { get; init; }
    public string? Url { get; init; }

    public string DisplayTitle => $"E{Number:00} · {Title}";

    public string DurationText => DurationSecs <= 0 ? "" :
        DurationSecs >= 3600 ? $"{DurationSecs / 3600} h {DurationSecs % 3600 / 60} min" : $"{Math.Max(1, DurationSecs / 60)} min";

    bool _watched;
    public bool Watched { get => _watched; set => Set(ref _watched, value); }

    double _progress;
    /// <summary>0..1 position of the saved resume point, 0 when there is none.</summary>
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    DownloadItem? _download;
    /// <summary>The episode's entry in the download list, null when it has not been downloaded.</summary>
    public DownloadItem? Download { get => _download; set => Set(ref _download, value); }
}

public sealed class EpgProgram : ObservableObject
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool HasArchive { get; init; }
    /// <summary>Start time as the server wrote it (server time zone), used for Xtream timeshift URLs.</summary>
    public string RawStart { get; init; } = "";

    public string TimeText => $"{Start:HH:mm} – {End:HH:mm}";

    public string DayText
    {
        get
        {
            var today = DateTime.Today;
            if (Start.Date == today) return L.T("Hoy");
            if (Start.Date == today.AddDays(-1)) return L.T("Ayer");
            if (Start.Date == today.AddDays(1)) return L.T("Mañana");
            return Start.ToString("ddd d MMM", L.Culture);
        }
    }

    bool _isNow, _isPast, _canCatchup;
    double _progress;
    public bool IsNow { get => _isNow; private set => Set(ref _isNow, value); }
    public bool IsPast { get => _isPast; private set => Set(ref _isPast, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool CanCatchup { get => _canCatchup; set => Set(ref _canCatchup, value); }

    public void Refresh(DateTime now)
    {
        IsPast = End <= now;
        IsNow = Start <= now && now < End;
        var total = (End - Start).TotalSeconds;
        Progress = IsNow && total > 0 ? Math.Clamp((now - Start).TotalSeconds / total, 0, 1) : 0;
    }
}

public sealed class ContentLibrary
{
    public List<Category> LiveCategories { get; set; } = new();
    public List<Channel> Channels { get; set; } = new();
    public List<Category> MovieCategories { get; set; } = new();
    public List<Movie> Movies { get; set; } = new();
    public List<Category> SeriesCategories { get; set; } = new();
    public List<SeriesItem> Series { get; set; } = new();
    public List<string> Warnings { get; } = new();
    public DateTime LoadedAt { get; set; } = DateTime.Now;

    Dictionary<string, Channel> _channelById = new();
    readonly Dictionary<MediaKind, Dictionary<string, Category>> _cats = new();

    public Channel? ChannelById(string id) => _channelById.GetValueOrDefault(id);

    public Category? CategoryOf(MediaKind kind, string categoryId) =>
        _cats.TryGetValue(kind, out var map) ? map.GetValueOrDefault(categoryId) : null;

    /// <summary>Counts items per category, moves items with unknown categories to one bucket and drops empty categories.</summary>
    public void Finish()
    {
        LiveCategories = Normalize(LiveCategories, Channels, c => c.CategoryId, (c, id) => c.CategoryId = id, MediaKind.Live);
        MovieCategories = Normalize(MovieCategories, Movies, m => m.CategoryId, (m, id) => m.CategoryId = id, MediaKind.Movie);
        SeriesCategories = Normalize(SeriesCategories, Series, s => s.CategoryId, (s, id) => s.CategoryId = id, MediaKind.Series);
        _channelById = new Dictionary<string, Channel>();
        foreach (var c in Channels) _channelById.TryAdd(c.Id, c);
        _cats[MediaKind.Live] = ToMap(LiveCategories);
        _cats[MediaKind.Movie] = ToMap(MovieCategories);
        _cats[MediaKind.Series] = ToMap(SeriesCategories);
        LoadedAt = DateTime.Now;
    }

    static Dictionary<string, Category> ToMap(List<Category> list)
    {
        var map = new Dictionary<string, Category>();
        foreach (var c in list) map.TryAdd(c.Id, c);
        return map;
    }

    static List<Category> Normalize<T>(List<Category> cats, List<T> items, Func<T, string> getCat, Action<T, string> setCat, MediaKind kind)
    {
        var known = new HashSet<string>();
        foreach (var c in cats) known.Add(c.Id);
        var counts = new Dictionary<string, int>();
        foreach (var item in items)
        {
            var id = getCat(item);
            if (!known.Contains(id)) { id = UncategorizedId; setCat(item, id); }
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        var result = new List<Category>();
        var seen = new HashSet<string>();
        foreach (var c in cats)
        {
            if (!seen.Add(c.Id) || !counts.TryGetValue(c.Id, out var n)) continue;
            c.Count = n;
            result.Add(c);
        }
        if (counts.TryGetValue(UncategorizedId, out var orphans))
            result.Add(new Category { Id = UncategorizedId, Name = L.T("Sin categoría"), Kind = kind, Count = orphans });
        return result;
    }

    public const string UncategorizedId = "_none";
}
