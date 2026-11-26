using System.Globalization;
using System.Text.RegularExpressions;

namespace Umbra.Core;

/// <summary>
/// Turns what a video says about an audio or subtitle track («spa», «es», «Pista 2 - [Español]», «English»)
/// into one key per language, so a preferred language can be found again in the next video.
/// </summary>
public static partial class TrackLanguage
{
    /// <summary>Stored as the subtitle preference when the user turned subtitles off.</summary>
    public const string Off = "off";

    // ISO 639-2/B codes that .NET does not know (it uses the /T ones: «fra», «deu»…).
    static readonly Dictionary<string, string> Bibliographic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fre"] = "fr", ["ger"] = "de", ["dut"] = "nl", ["chi"] = "zh", ["cze"] = "cs", ["gre"] = "el",
        ["per"] = "fa", ["rum"] = "ro", ["slo"] = "sk", ["alb"] = "sq", ["arm"] = "hy", ["baq"] = "eu",
        ["bur"] = "my", ["geo"] = "ka", ["ice"] = "is", ["mac"] = "mk", ["may"] = "ms", ["wel"] = "cy",
        ["tib"] = "bo", ["scc"] = "sr", ["scr"] = "hr",
    };

    static readonly Lazy<CultureInfo[]> Cultures = new(() => CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(c => c.TwoLetterISOLanguageName.Length == 2 && c.TwoLetterISOLanguageName != "iv").ToArray());

    [GeneratedRegex(@"\[([^\]]+)\]")]
    private static partial Regex BracketRegex();

    /// <summary>Two-letter code of a language written as a code or by name, or null when it is not one.</summary>
    static string? CodeOf(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        // «es-ES», «pt_BR»: the language is the first part.
        var head = t.Split('-', '_', ' ')[0];
        if (Bibliographic.TryGetValue(head, out var b)) return b;
        foreach (var c in Cultures.Value)
        {
            if (head == c.TwoLetterISOLanguageName || head == c.ThreeLetterISOLanguageName || head == c.ThreeLetterWindowsLanguageName.ToLowerInvariant())
                return c.TwoLetterISOLanguageName;
        }
        foreach (var c in Cultures.Value)
        {
            if (Same(t, c.EnglishName) || Same(t, c.NativeName) || Same(t, c.DisplayName)) return c.TwoLetterISOLanguageName;
        }
        return null;
    }

    static bool Same(string text, string name)
    {
        // «Spanish», «Español», «español (España)»: compare the part before any parenthesis.
        var plain = name.Split('(')[0].Trim();
        return plain.Length > 0 && string.Equals(text.Split('(')[0].Trim(), plain, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// The key of a track: its language code when the video gives one, otherwise the language written in its name
    /// («[Español]»), otherwise the name itself. Empty when there is nothing to go by.
    /// </summary>
    public static string KeyOf(string? language, string? name)
    {
        if (!string.IsNullOrWhiteSpace(language) && CodeOf(language) is { } code) return code;
        var label = name ?? "";
        foreach (Match m in BracketRegex().Matches(label))
            if (CodeOf(m.Groups[1].Value) is { } fromBracket) return fromBracket;
        foreach (var word in label.Split(' ', '-', ',', '(', ')', '[', ']').Where(w => w.Length >= 4))
            if (CodeOf(word) is { } fromWord) return fromWord;
        var bracket = BracketRegex().Match(label);
        return (bracket.Success ? bracket.Groups[1].Value : "").Trim().ToLowerInvariant();
    }

    /// <summary>How a key is shown in Settings: «Español», «English», or the text as it came.</summary>
    public static string DisplayName(string key)
    {
        if (key.Length == 2)
        {
            try
            {
                var name = CultureInfo.GetCultureInfo(key).NativeName;
                return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..] : key;
            }
            catch (CultureNotFoundException) { }
        }
        return key;
    }
}
