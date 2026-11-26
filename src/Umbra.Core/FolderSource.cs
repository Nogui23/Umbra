using System.Globalization;
using System.Text.RegularExpressions;

namespace Umbra.Core;

/// <summary>
/// Videos in a folder: one of this computer or a shared one of a NAS (\\nas\videos). Files named like
/// «Serie S01E02» (or «1x02») become episodes of a series; the rest are movies. Posters are the pictures found next
/// to the videos (poster.jpg, folder.jpg or one named like the video).
/// </summary>
public sealed partial class FolderSource : IContentSource
{
    static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".flv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts"];
    static readonly string[] PictureExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    static readonly string[] PosterNames = ["poster", "folder", "cover", "movie", "show"];

    Dictionary<string, SeriesDetail> _series = new();

    public FolderSource(Account account, Settings settings) => Account = account;

    public Account Account { get; }
    public AccountInfo? Info => null;
    public Task<AccountInfo?> RefreshInfoAsync(CancellationToken ct) => Task.FromResult<AccountInfo?>(null);

    public string Root => Account.Server.Trim().Trim('"');

    [GeneratedRegex(@"^(?<show>.*?)[\s._\-\[\(]*(?:S(?<s>\d{1,2})[\s._\-]*E(?<e>\d{1,4})|(?<![\dx])(?<s>\d{1,2})x(?<e>\d{2,4})(?!\d))(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"^(?:season|temporada|temp|staffel|saison|s)[\s._\-]*\d{1,3}$|^specials$|^especiales$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(@"\b(480p|576p|720p|1080p|2160p|4k|uhd|bluray|blu-ray|bdrip|brrip|webrip|web-dl|webdl|hdtv|dvdrip|hdrip|x264|x265|h264|h265|hevc|xvid|remux)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseTagsRegex();

    /// <summary>«The.Movie.2019.1080p.x264» → «The Movie 2019».</summary>
    internal static string Clean(string name)
    {
        var text = name.Replace('.', ' ').Replace('_', ' ');
        text = ReleaseTagsRegex().Replace(text, "");
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Trim(' ', '-', '[', '(', ']', ')');
    }

    public async Task<ContentLibrary> LoadLibraryAsync(bool force, IProgress<string>? progress, CancellationToken ct)
    {
        var root = Root;
        if (root.Length == 0) throw new SourceException("Falta la carpeta.");
        progress?.Report(L.T("Buscando vídeos en la carpeta…"));
        return await Task.Run(() =>
        {
            if (!Directory.Exists(root))
                throw new SourceException("No se puede abrir la carpeta. Comprueba que existe y, si es de un NAS, que está encendido y tienes acceso desde Windows.");
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            var files = Directory.EnumerateFiles(root, "*", options)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ct.ThrowIfCancellationRequested();
            if (files.Count == 0) throw new SourceException("No se ha encontrado ningún vídeo en la carpeta.");
            return Build(root, files);
        }, ct);
    }

    ContentLibrary Build(string root, List<string> files)
    {
        var lib = new ContentLibrary();
        var cats = new Dictionary<(MediaKind, string), Category>();
        var shows = new Dictionary<string, (SeriesItem Item, SortedDictionary<int, Season> Seasons, SeriesDetail Detail)>(StringComparer.OrdinalIgnoreCase);
        var pictures = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        string CategoryId(MediaKind kind, string name)
        {
            if (!cats.TryGetValue((kind, name), out var c))
            {
                c = new Category { Id = "g:" + name, Name = name, Kind = kind };
                cats[(kind, name)] = c;
                (kind == MediaKind.Movie ? lib.MovieCategories : lib.SeriesCategories).Add(c);
            }
            return c.Id;
        }

        string[] PicturesIn(string dir)
        {
            if (pictures.TryGetValue(dir, out var list)) return list;
            try
            {
                list = Directory.EnumerateFiles(dir)
                    .Where(f => PictureExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToArray();
            }
            catch (Exception)
            {
                list = [];
            }
            return pictures[dir] = list;
        }

        // A picture named like the video, or a poster of the folder.
        string PictureFor(string dir, string? baseName)
        {
            var found = PicturesIn(dir);
            if (baseName is not null)
            {
                var same = found.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(baseName, StringComparison.OrdinalIgnoreCase)
                                                     || Path.GetFileNameWithoutExtension(f).Equals(baseName + "-poster", StringComparison.OrdinalIgnoreCase));
                if (same is not null) return same;
            }
            return found.FirstOrDefault(f => PosterNames.Contains(Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)) ?? "";
        }

        // The first folder under the root groups the content («Películas», «Infantil»…).
        string GroupOf(string file)
        {
            var relative = Path.GetRelativePath(root, file);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return first == Path.GetFileName(file) ? L.T("Sin categoría") : first;
        }

        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file) ?? root;
            var baseName = Path.GetFileNameWithoutExtension(file);
            var id = Text.ShortHash(Path.GetRelativePath(root, file).ToLowerInvariant());
            var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            var match = EpisodeRegex().Match(baseName);

            if (!match.Success)
            {
                var alone = dir.Equals(root, StringComparison.OrdinalIgnoreCase) ? null : dir;
                lib.Movies.Add(new Movie
                {
                    Id = id,
                    Name = Text.FirstNonEmpty(Clean(baseName), baseName),
                    Image = PictureFor(dir, baseName) is { Length: > 0 } p && (alone is not null || Path.GetFileNameWithoutExtension(p).StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) ? p : "",
                    CategoryId = CategoryId(MediaKind.Movie, GroupOf(file)),
                    Added = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds(),
                    Extension = ext,
                    Url = file,
                });
                continue;
            }

            // The series is the folder that holds its seasons; without folders, the name written before «S01E02».
            var showDir = SeasonFolderRegex().IsMatch(Path.GetFileName(dir)) ? Path.GetDirectoryName(dir) ?? dir : dir;
            var named = Clean(match.Groups["show"].Value);
            var inOwnFolder = !showDir.Equals(root, StringComparison.OrdinalIgnoreCase);
            var showName = named.Length > 0 ? named : inOwnFolder ? Clean(Path.GetFileName(showDir)) : baseName;
            var key = (inOwnFolder ? showDir : root) + "|" + showName;
            if (!shows.TryGetValue(key, out var show))
            {
                var cover = inOwnFolder ? PictureFor(showDir, null) : "";
                var item = new SeriesItem
                {
                    Id = Text.ShortHash("series:" + key.ToLowerInvariant()),
                    Name = showName,
                    Image = cover,
                    CategoryId = CategoryId(MediaKind.Series, GroupOf(file)),
                    Added = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds(),
                };
                show = (item, new SortedDictionary<int, Season>(), new SeriesDetail { Name = showName, Cover = cover });
                shows[key] = show;
                lib.Series.Add(item);
            }
            var seasonNo = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
            if (!show.Seasons.TryGetValue(seasonNo, out var season)) show.Seasons[seasonNo] = season = new Season { Number = seasonNo };
            var title = Clean(match.Groups["rest"].Value);
            season.Episodes.Add(new Episode
            {
                Id = id,
                Title = title.Length > 0 ? title : L.T("Episodio {0}", int.Parse(match.Groups["e"].Value, CultureInfo.InvariantCulture)),
                Season = seasonNo,
                Number = int.Parse(match.Groups["e"].Value, CultureInfo.InvariantCulture),
                Image = PictureFor(dir, baseName) is { Length: > 0 } still && Path.GetFileNameWithoutExtension(still).StartsWith(baseName, StringComparison.OrdinalIgnoreCase) ? still : "",
                Extension = ext,
                Url = file,
            });
        }

        _series = new Dictionary<string, SeriesDetail>();
        foreach (var (item, seasons, detail) in shows.Values)
        {
            foreach (var season in seasons.Values) season.Episodes.Sort((a, b) => a.Number.CompareTo(b.Number));
            detail.Seasons.AddRange(seasons.Values);
            _series[item.Id] = detail;
        }
        lib.Finish();
        return lib;
    }

    public Task<MovieDetail> GetMovieDetailAsync(Movie movie, CancellationToken ct) =>
        Task.FromResult(new MovieDetail { Name = movie.Name, Poster = movie.Image });

    public Task<SeriesDetail> GetSeriesDetailAsync(SeriesItem series, CancellationToken ct) =>
        Task.FromResult(_series.TryGetValue(series.Id, out var d) ? d : new SeriesDetail { Name = series.Name, Cover = series.Image });

    public Task<IReadOnlyList<EpgProgram>> GetEpgAsync(Channel channel, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EpgProgram>>(Array.Empty<EpgProgram>());

    public string GetLiveUrl(Channel channel) => "";
    public string GetMovieUrl(Movie movie) => movie.Url ?? "";
    public string GetEpisodeUrl(Episode episode) => episode.Url ?? "";
    public string? GetCatchupUrl(Channel channel, EpgProgram program) => null;
}
