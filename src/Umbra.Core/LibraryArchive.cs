using System.IO.Compression;
using System.Text.Json;

namespace Umbra.Core;

/// <summary>
/// Moves downloaded movies and episodes between computers as one .zip: the videos, their artwork and a list with
/// what the library shows about them. Nothing about the account they came from goes in: no addresses, no user,
/// no identifiers of the provider. Imported items belong to no account and play only from disk.
/// </summary>
public static class LibraryArchive
{
    /// <summary>Account that owns imported items: none. Their progress is kept under this name.</summary>
    public const string LocalAccount = "library";

    const string ManifestName = "umbra-library.json";
    const string FormatName = "umbra-library";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed class Manifest
    {
        public string Format { get; set; } = FormatName;
        public int Version { get; set; } = 1;
        public List<Entry> Items { get; set; } = new();
    }

    /// <summary>One movie or episode. Paths are names inside the .zip.</summary>
    public sealed class Entry
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public bool IsEpisode { get; set; }
        /// <summary>For an episode, its series: the folder it is saved in.</summary>
        public string Series { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Video { get; set; } = "";
        public long Size { get; set; }
        public string Image { get; set; } = "";
        public DownloadInfo? Info { get; set; }
    }

    public sealed record ImportResult(int Added, int Skipped);

    /// <summary>Episodes are titled «Serie · T1 E2»: the series is what comes before the last separator.</summary>
    public static string SeriesOf(DownloadItem item)
    {
        if (!item.IsEpisode) return "";
        var cut = item.Title.LastIndexOf(" · ", StringComparison.Ordinal);
        return cut > 0 ? item.Title[..cut] : item.Title;
    }

    static bool IsLocalFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);

    // ----- Export -----

    /// <summary>Writes the finished downloads in <paramref name="items"/> to a .zip. Runs in the background.</summary>
    public static Task ExportAsync(IReadOnlyList<DownloadItem> items, string zipPath, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var ready = items.Where(i => i.IsCompleted && File.Exists(i.FilePath)).ToList();
            if (ready.Count == 0) throw new InvalidOperationException(L.T("No hay nada que exportar."));
            var total = Math.Max(1, ready.Sum(i => new FileInfo(i.FilePath).Length));
            long done = 0;
            var tmp = zipPath + ".tmp";
            try
            {
                await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    var manifest = new Manifest();
                    var number = 0;
                    foreach (var item in ready)
                    {
                        ct.ThrowIfCancellationRequested();
                        var prefix = $"{++number:000}";
                        // Pictures still on the web are left out: their address could point at the provider.
                        async Task<string> Art(string? path, string name)
                        {
                            if (!IsLocalFile(path)) return "";
                            var entryName = $"art/{prefix}-{name}{Path.GetExtension(path).ToLowerInvariant()}";
                            await using var source = File.OpenRead(path!);
                            await using var target = zip.CreateEntry(entryName, CompressionLevel.NoCompression).Open();
                            await source.CopyToAsync(target, ct);
                            return entryName;
                        }

                        var entry = new Entry
                        {
                            Title = item.Title,
                            Subtitle = item.Subtitle,
                            IsEpisode = item.IsEpisode,
                            Series = SeriesOf(item),
                            FileName = Path.GetFileName(item.FilePath),
                            Video = $"videos/{prefix}{Path.GetExtension(item.FilePath).ToLowerInvariant()}",
                            Image = await Art(item.Image, "image"),
                        };
                        if (item.Info is { } info)
                        {
                            entry.Info = new DownloadInfo
                            {
                                Plot = info.Plot,
                                Genre = info.Genre,
                                Director = info.Director,
                                Cast = info.Cast,
                                ReleaseDate = info.ReleaseDate,
                                Duration = info.Duration,
                                Country = info.Country,
                                Rating = info.Rating,
                                SeriesPlot = info.SeriesPlot,
                                Poster = info.Poster == item.Image && entry.Image.Length > 0 ? entry.Image : await Art(info.Poster, "poster"),
                                Backdrop = await Art(info.Backdrop, "backdrop"),
                            };
                        }

                        // Video is already compressed: storing it as it is is much faster and just as small.
                        await using (var source = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                        await using (var target = zip.CreateEntry(entry.Video, CompressionLevel.NoCompression).Open())
                        {
                            entry.Size = source.Length;
                            done = await CopyAsync(source, target, done, total, progress, ct);
                        }
                        manifest.Items.Add(entry);
                    }
                    await using var list = zip.CreateEntry(ManifestName, CompressionLevel.Optimal).Open();
                    await JsonSerializer.SerializeAsync(list, manifest, Json, ct);
                }
                File.Move(tmp, zipPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception) { }
                throw;
            }
        }, ct);

    static async Task<long> CopyAsync(Stream source, Stream target, long done, long total, IProgress<double>? progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 17];
        var last = Environment.TickCount64;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (Environment.TickCount64 - last < 200) continue;
            last = Environment.TickCount64;
            progress?.Report(Math.Clamp((double)done / total, 0, 1));
        }
        return done;
    }

    // ----- Import -----

    /// <summary>
    /// Adds the contents of a .zip made by <see cref="ExportAsync"/> to the library: videos go to the download folder,
    /// what is already there (same title and size) is skipped.
    /// </summary>
    public static Task<ImportResult> ImportAsync(string zipPath, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var manager = DownloadManager.I;
            using var zip = ZipFile.OpenRead(zipPath);
            var listEntry = zip.GetEntry(ManifestName)
                ?? throw new InvalidDataException(L.T("Este archivo no es una biblioteca exportada desde Umbra."));
            Manifest manifest;
            await using (var stream = listEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, Json, ct) ?? new Manifest();
            if (manifest.Format != FormatName || manifest.Items.Count == 0)
                throw new InvalidDataException(L.T("Este archivo no es una biblioteca exportada desde Umbra."));
            if (manifest.Version > 1)
                throw new InvalidDataException(L.T("Este archivo se exportó con una versión más nueva de Umbra."));

            // Only entries the list names, and only from the two folders the export writes.
            ZipArchiveEntry? Find(string name, string folder) =>
                name.StartsWith(folder + "/", StringComparison.Ordinal) && !name.Contains("..") ? zip.GetEntry(name) : null;

            var wanted = manifest.Items.Select(e => (Entry: e, Video: Find(e.Video, "videos"))).Where(x => x.Video is not null).ToList();
            var total = Math.Max(1, wanted.Sum(x => x.Video!.Length));
            long done = 0;
            int added = 0, skipped = 0;

            foreach (var (entry, video) in wanted)
            {
                ct.ThrowIfCancellationRequested();
                var size = video!.Length;
                var known = await manager.OnUi(() => manager.Items.Any(i =>
                    i.Title == entry.Title && i.Subtitle == entry.Subtitle && i.IsCompleted && i.TotalBytes == size && File.Exists(i.FilePath)));
                if (known)
                {
                    skipped++;
                    done += size;
                    progress?.Report(Math.Clamp((double)done / total, 0, 1));
                    continue;
                }

                var id = Guid.NewGuid().ToString("N");
                var name = Path.GetFileNameWithoutExtension(entry.FileName);
                if (string.IsNullOrWhiteSpace(name)) name = entry.Title;
                var extension = Path.GetExtension(entry.Video).TrimStart('.');
                var path = await manager.OnUi(() => manager.NewFilePath(entry.IsEpisode ? entry.Series : null, name, extension));
                if (DownloadManager.FreeSpace(path) is { } free && free < size)
                    throw new IOException(L.T("No hay espacio suficiente en el disco ({0} libres).", DownloadItem.Size(free)));

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var part = path + ".part";
                var art = new List<string>();
                try
                {
                    await using (var source = video.Open())
                    await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                        done = await CopyAsync(source, target, done, total, progress, ct);
                    File.Move(part, path, overwrite: false);

                    async Task<string> Art(string inZip)
                    {
                        if (string.IsNullOrWhiteSpace(inZip) || Find(inZip, "art") is not { Length: > 0 and <= 12 << 20 } picture) return "";
                        var ext = Path.GetExtension(inZip).ToLowerInvariant();
                        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif")) ext = ".jpg";
                        Directory.CreateDirectory(DownloadManager.ArtDir);
                        var file = Path.Combine(DownloadManager.ArtDir, $"{id}-{Text.ShortHash(inZip)}{ext}");
                        await using var source = picture.Open();
                        await using var target = File.Create(file);
                        await source.CopyToAsync(target, ct);
                        art.Add(file);
                        return file;
                    }

                    var item = new DownloadItem
                    {
                        Id = id,
                        AccountId = LocalAccount,
                        Key = "local:" + id,
                        IsEpisode = entry.IsEpisode,
                        Title = entry.Title,
                        Subtitle = entry.Subtitle,
                        Image = await Art(entry.Image),
                        FilePath = path,
                        AddedUtc = DateTime.UtcNow,
                    };
                    if (entry.Info is { } info)
                    {
                        info.Poster = info.Poster == entry.Image ? item.Image : await Art(info.Poster);
                        info.Backdrop = await Art(info.Backdrop);
                        item.Info = info;
                    }
                    item.SetProgress(size, size, 0);
                    item.State = DownloadState.Completed;
                    await manager.OnUi(() =>
                    {
                        manager.AddImported(item);
                        return true;
                    });
                    added++;
                }
                catch
                {
                    foreach (var file in art.Append(part).Append(path))
                        try { File.Delete(file); } catch (Exception) { }
                    throw;
                }
            }
            return new ImportResult(added, skipped);
        }, ct);
}
