using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Umbra.Core;

public sealed class XmltvData
{
    public Dictionary<string, List<EpgProgram>> Programs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> NameIndex { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EpgProgram> Find(string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(id) && Programs.TryGetValue(id.Trim(), out var list)) return list;
        foreach (var candidate in new[] { name, id })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (NameIndex.TryGetValue(Normalize(candidate), out var cid) && Programs.TryGetValue(cid, out list)) return list;
        }
        return Array.Empty<EpgProgram>();
    }

    static readonly string[] QualityTags = ["fhd", "uhd", "hd", "sd", "4k", "hevc", "h265"];

    /// <summary>Lower-case letters and digits only, without trailing quality tags ("La 1 HD" → "la1").</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        var result = sb.ToString();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var tag in QualityTags)
            {
                if (result.Length > tag.Length + 1 && result.EndsWith(tag, StringComparison.Ordinal))
                {
                    result = result[..^tag.Length];
                    changed = true;
                }
            }
        }
        return result;
    }
}

public static class XmltvParser
{
    public static XmltvData ParseFile(string path, DateTime from, DateTime to, TimeSpan offset)
    {
        using var fs = File.OpenRead(path);
        var b1 = fs.ReadByte();
        var b2 = fs.ReadByte();
        fs.Position = 0;
        using Stream input = b1 == 0x1f && b2 == 0x8b ? new GZipStream(fs, CompressionMode.Decompress) : fs;
        return Parse(input, from, to, offset);
    }

    public static XmltvData Parse(Stream input, DateTime from, DateTime to, TimeSpan offset)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            XmlResolver = null,
            CheckCharacters = false,
        };
        var data = new XmltvData();
        using var r = XmlReader.Create(input, settings);
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            if (r.Name == "channel")
            {
                var id = r.GetAttribute("id") ?? "";
                foreach (var (name, value) in ReadChildren(r))
                {
                    if (name == "display-name" && value.Length > 0 && id.Length > 0)
                        data.NameIndex.TryAdd(XmltvData.Normalize(value), id);
                }
            }
            else if (r.Name == "programme")
            {
                var channel = r.GetAttribute("channel");
                var start = ParseTime(r.GetAttribute("start"));
                var stop = ParseTime(r.GetAttribute("stop"));
                string? title = null, desc = null;
                foreach (var (name, value) in ReadChildren(r))
                {
                    if (name == "title") title ??= value;
                    else if (name == "desc") desc ??= value;
                }
                if (channel is null || start is null) continue;
                var s = start.Value + offset;
                var e = (stop ?? start.Value.AddMinutes(30)) + offset;
                if (e < from || s > to) continue;
                if (!data.Programs.TryGetValue(channel, out var list))
                {
                    list = new List<EpgProgram>();
                    data.Programs[channel] = list;
                }
                list.Add(new EpgProgram { Title = title ?? "", Description = desc ?? "", Start = s, End = e });
            }
        }
        foreach (var list in data.Programs.Values) list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return data;
    }

    /// <summary>Reads the direct text children of the current element and leaves the reader on its end tag.</summary>
    static IEnumerable<(string Name, string Value)> ReadChildren(XmlReader r)
    {
        var result = new List<(string, string)>();
        using var sub = r.ReadSubtree();
        sub.Read();
        sub.Read();
        while (!sub.EOF)
        {
            if (sub.NodeType == XmlNodeType.Element && sub.Depth == 1)
            {
                var name = sub.Name;
                if (name is "title" or "desc" or "display-name")
                {
                    try
                    {
                        result.Add((name, sub.ReadElementContentAsString().Trim()));
                    }
                    catch (XmlException)
                    {
                        // Unexpected markup inside the text; ignore this child.
                    }
                }
                else
                {
                    sub.Skip();
                }
                continue;
            }
            sub.Read();
        }
        return result;
    }

    /// <summary>Parses XMLTV times such as "20240101203000 +0100" to local time.</summary>
    public static DateTime? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 12) return null;
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 12) return null;
        var format = digits.Length >= 14 ? "yyyyMMddHHmmss" : "yyyyMMddHHmm";
        if (!DateTime.TryParseExact(digits[..format.Length], format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return null;
        var rest = s[digits.Length..].Trim();
        var offset = TimeSpan.Zero;
        if (rest.Length >= 5 && (rest[0] == '+' || rest[0] == '-'))
        {
            var t = rest[1..].Replace(":", "");
            if (t.Length >= 4 && int.TryParse(t.AsSpan(0, 2), out var hh) && int.TryParse(t.AsSpan(2, 2), out var mm))
            {
                offset = new TimeSpan(hh, mm, 0);
                if (rest[0] == '-') offset = -offset;
            }
        }
        return new DateTimeOffset(dt, offset).LocalDateTime;
    }
}

/// <summary>Downloads (with a 12 h cache) and indexes an XMLTV guide once per session.</summary>
public sealed class XmltvEpg(string url, string cacheDir, Settings settings)
{
    Task<XmltvData?>? _task;
    bool _forceDownload;

    public void Invalidate()
    {
        _task = null;
        _forceDownload = true;
    }

    public async Task<IReadOnlyList<EpgProgram>> ForChannelAsync(Channel channel)
    {
        var data = await (_task ??= LoadAsync());
        return data?.Find(channel.EpgId, channel.Name) ?? Array.Empty<EpgProgram>();
    }

    async Task<XmltvData?> LoadAsync()
    {
        try
        {
            string path;
            if (File.Exists(url))
            {
                path = url;
            }
            else
            {
                path = Path.Combine(cacheDir, "epg.xml");
                if (_forceDownload || !AppPaths.IsFresh(path, TimeSpan.FromHours(12)))
                {
                    await Http.DownloadToFileAsync(url, path, CancellationToken.None);
                    _forceDownload = false;
                }
            }
            var now = DateTime.Now;
            var offset = TimeSpan.FromHours(settings.EpgOffsetHours);
            return await Task.Run(() => XmltvParser.ParseFile(path, now.AddDays(-7), now.AddDays(3), offset));
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cargar la guía XMLTV", ex);
            _task = null;
            return null;
        }
    }
}
