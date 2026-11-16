using System.Net;
using System.Reflection;
using System.Text.Json;

namespace Umbra.Core;

/// <summary>
/// Asks GitHub which is the latest published version of Umbra. Nothing is downloaded or installed: the app only
/// says that there is a new one and opens its page.
/// </summary>
public static class Updates
{
    /// <summary>Latest release of the repository. Only the developer self-test points it somewhere else.</summary>
    public static string LatestReleaseApi { get; set; } = "https://api.github.com/repos/Nogui23/Umbra/releases/latest";

    public sealed record Release(Version Version, string Tag, string Url, string Notes);

    /// <summary>The version of this copy of the app (major.minor.patch).</summary>
    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    static Version Normalize(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>«v1.2.0», «1.2», «Umbra 1.2.0-beta» → 1.2.0; null when there is no number.</summary>
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var start = tag.IndexOfAny("0123456789".ToCharArray());
        if (start < 0) return null;
        var end = start;
        while (end < tag.Length && (char.IsAsciiDigit(tag[end]) || tag[end] == '.')) end++;
        var text = tag[start..end].Trim('.');
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var v) ? Normalize(v) : null;
    }

    /// <summary>The latest published version, or null when none is published yet.</summary>
    public static async Task<Release?> LatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        // GitHub asks every client for a name; only the app's, nothing about the user.
        request.Headers.TryAddWithoutValidation("User-Agent", "Umbra/" + Current.ToString(3));
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Http.Client.SendAsync(request, cts.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new SourceException(L.T("El servidor respondió con un error {0} ({1}).", (int)response.StatusCode, response.ReasonPhrase));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token), Json.DocOptions);
        var root = doc.RootElement;
        if (root.Bool("draft") || root.Bool("prerelease")) return null;
        var tag = root.Str("tag_name");
        var version = Parse(tag) ?? Parse(root.Str("name"));
        if (version is null) return null;
        var url = root.Str("html_url");
        // Only a page of GitHub is ever opened.
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = "https://github.com/Nogui23/Umbra/releases";
        return new Release(version, tag, url, root.Str("body"));
    }
}
