using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// Version qualifiers in a song name ("Live", "Acoustic", "Remix", "Sped Up"…) that name a different recording,
/// whose timing will not fit the playing one.
/// </summary>
/// <remarks>
/// Only qualifier segments count: text in brackets, after " - " ("Song - Live"), or a Japanese "~…~" subtitle.
/// Remasters, mono/stereo, editions and credits name the same recording and are not tags, and a bare "Version" says
/// nothing. Names that only translate the qualifier ("现场" for "Live") fold to the same tag. A language version
/// ("English Version", "Eng ver.", "(English)", "英語ver.") is a version too (<see cref="Languages"/>): the same tune,
/// sung in other words. Ported from the Mac's <c>VersionTags</c> (Lunalyr's own code).
/// </remarks>
public static partial class VersionTags
{
    /// <summary>The tags naming a language version.</summary>
    public static IReadOnlySet<string> Languages { get; } =
        new HashSet<string>(["english", "japanese", "korean", "chinese"], StringComparer.Ordinal);

    /// <summary>The version tags <paramref name="name"/> declares, e.g. {"live"}.</summary>
    public static IReadOnlySet<string> Tags(string name)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in Segments(name))
        {
            found.UnionWith(SegmentTags(segment));
        }
        return found;
    }

    /// <summary>
    /// The tags on which <paramref name="title"/> (a service's name for its match) and the playing song disagree,
    /// empty when they name the same version. A tag the playing song's album carries ("Song (Live)" on "Live at
    /// Wembley") is not held against the match.
    /// </summary>
    public static IReadOnlySet<string> Mismatch(LyricQuery query, string title)
    {
        var playing = Tags(query.Title);
        var offered = Tags(title);
        var album = Tags(query.Album ?? string.Empty);
        var result = new HashSet<string>(offered, StringComparer.Ordinal);
        result.ExceptWith(playing);
        result.ExceptWith(album);
        foreach (var tag in playing)
        {
            if (!offered.Contains(tag))
            {
                result.Add(tag);
            }
        }
        return result;
    }

    /// <summary>"Live", or "Live / Remix", for the source list.</summary>
    public static string Describe(IEnumerable<string> tags) =>
        string.Join(" / ", tags.Order(StringComparer.Ordinal).Select(tag =>
            tag.Length == 0 ? tag : char.ToUpperInvariant(tag[0]) + tag[1..]));

    [GeneratedRegex(@"[(\[{（【〔「『<＜]([^)\]}）】〕」』>＞]*)[)\]}）】〕」』>＞]", RegexOptions.CultureInvariant)]
    private static partial Regex Bracket();

    [GeneratedRegex(@"\s[-–—]\s", RegexOptions.CultureInvariant)]
    private static partial Regex Dash();

    [GeneratedRegex(@"[~～〜]([^~～〜]+)[~～〜]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Tilde();

    private static List<string> Segments(string name)
    {
        var parts = Bracket().Matches(name).Select(match => match.Groups[1].Value).ToList();
        // Everything after the first " - " ("Song - Live", "Song - Radio Edit").
        var dash = Dash().Match(name);
        if (dash.Success)
        {
            parts.Add(name[(dash.Index + dash.Length)..]);
        }
        // Japanese subtitles: "曲名 ~Live ver.~".
        var tilde = Tilde().Match(name);
        if (tilde.Success)
        {
            parts.Add(tilde.Groups[1].Value);
        }
        return parts;
    }

    /// <summary>
    /// Words of a segment, folded. Latin, CJK and digit runs split apart, so "Live版" and "2011Live" still give "live".
    /// </summary>
    public static IReadOnlyList<string> Words(string segment)
    {
        var folded = LyricTextFolding.FoldWidth(segment);
        var words = new List<string>();
        var current = new StringBuilder();
        var kind = 0;
        foreach (var rune in folded.EnumerateRunes())
        {
            int next;
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.DecimalDigitNumber)
            {
                next = 1;
            }
            else if (IsLetterOrMark(category))
            {
                next = rune.Value < 0x2E80 ? 2 : 3;
            }
            else
            {
                next = 0;
            }
            if ((next != kind || next == 0) && current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
            if (next != 0)
            {
                current.Append(rune.ToString());
            }
            kind = next;
        }
        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }
        return words;
    }

    private static bool IsLetterOrMark(UnicodeCategory category) => category is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static readonly Dictionary<string, string> WordTags = new(StringComparer.Ordinal)
    {
        ["live"] = "live", ["unplugged"] = "unplugged", ["acoustic"] = "acoustic", ["acustico"] = "acoustic",
        ["remix"] = "remix", ["remixes"] = "remix", ["remixed"] = "remix", ["rmx"] = "remix",
        ["demo"] = "demo", ["demos"] = "demo",
        ["instrumental"] = "instrumental", ["inst"] = "instrumental", ["karaoke"] = "instrumental", ["offvocal"] = "instrumental",
        ["acapella"] = "a cappella", ["acappella"] = "a cappella",
        ["spedup"] = "sped up", ["nightcore"] = "sped up", ["slowed"] = "slowed",
        ["edit"] = "edit", ["extended"] = "extended", ["orchestral"] = "orchestral", ["symphonic"] = "orchestral",
        ["rehearsal"] = "rehearsal",
    };

    private static readonly Dictionary<string, string> PairTags = new(StringComparer.Ordinal)
    {
        ["off vocal"] = "instrumental", ["a cappella"] = "a cappella", ["a capella"] = "a cappella",
        ["sped up"] = "sped up", ["speed up"] = "sped up", ["piano version"] = "piano", ["piano ver"] = "piano",
        ["dj mix"] = "dj mix", ["continuous mix"] = "dj mix",
    };

    /// <summary>A language named before "Version" / "Ver." ("English Version", "Eng ver."), or alone in a qualifier ("(English)").</summary>
    private static readonly Dictionary<string, string> LanguageWords = new(StringComparer.Ordinal)
    {
        ["english"] = "english", ["eng"] = "english", ["japanese"] = "japanese", ["jpn"] = "japanese", ["jp"] = "japanese",
        ["korean"] = "korean", ["kor"] = "korean", ["chinese"] = "chinese", ["mandarin"] = "chinese",
    };

    private static readonly HashSet<string> VersionWords = new(["version", "ver", "vers", "edition"], StringComparer.Ordinal);

    // Matched inside the segment, for scripts written without spaces. The keys go through the same folding as the
    // names, so kana with voicing marks ("ライブ") match however the folding treats those marks.
    private static readonly (string Span, string Tag)[] SpanTags =
    [
        .. new (string, string)[]
        {
            ("现场", "live"), ("現場", "live"), ("ライブ", "live"), ("라이브", "live"),
            ("不插电", "unplugged"), ("不插電", "unplugged"),
            ("伴奏", "instrumental"), ("纯音乐", "instrumental"), ("純音樂", "instrumental"),
            ("オフボーカル", "instrumental"), ("インスト", "instrumental"),
            ("混音", "remix"), ("リミックス", "remix"), ("리믹스", "remix"),
            ("清唱", "a cappella"), ("아카펠라", "a cappella"),
            ("加速", "sped up"), ("降速", "slowed"), ("慢速", "slowed"),
            ("アコースティック", "acoustic"), ("어쿠스틱", "acoustic"),
            ("英語", "english"), ("英文", "english"), ("英語版", "english"), ("영어", "english"),
            ("日本語", "japanese"), ("日文", "japanese"), ("일본어", "japanese"),
            ("韓国語", "korean"), ("韓國語", "korean"), ("韩语", "korean"), ("한국어", "korean"),
            ("中文版", "chinese"), ("中国語", "chinese"),
        }.Select(pair => (string.Join(' ', Words(pair.Item1)), pair.Item2)),
    ];

    private static readonly HashSet<string> NotAnotherMix = new(["original", "dj", "continuous", "album"], StringComparer.Ordinal);

    private static HashSet<string> SegmentTags(string segment)
    {
        var list = Words(segment);
        var found = new HashSet<string>(StringComparer.Ordinal);
        // Music's DJ Mix albums mark each track "[Mixed]"; "Mixed by X" is a credit.
        if (list is ["mixed"])
        {
            found.Add("dj mix");
        }
        // "(English)" names a language version; "(JP)" or "(Eng)" alone is too short to tell.
        if (list.Count == 1 && list[0].Length > 3 && LanguageWords.TryGetValue(list[0], out var alone))
        {
            found.Add(alone);
        }
        for (var index = 0; index < list.Count; index++)
        {
            var word = list[index];
            if (WordTags.TryGetValue(word, out var tag))
            {
                found.Add(tag);
            }
            if (index + 1 < list.Count && PairTags.TryGetValue(word + " " + list[index + 1], out var pair))
            {
                found.Add(pair);
            }
            if (index + 1 < list.Count && LanguageWords.TryGetValue(word, out var language) && VersionWords.Contains(list[index + 1]))
            {
                found.Add(language);
            }
            // "Club Mix", "Extended Mix": another mix, but "Original Mix" is the original.
            if (word == "mix" && index > 0 && !NotAnotherMix.Contains(list[index - 1]))
            {
                found.Add(list[index - 1] == "extended" ? "extended" : "remix");
            }
        }
        var joined = string.Join(' ', list);
        foreach (var (span, spanTag) in SpanTags)
        {
            if (span.Length > 0 && joined.Contains(span, StringComparison.Ordinal))
            {
                found.Add(spanTag);
            }
        }
        return found;
    }
}
