using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>How a search hit compares with the playing recording (the Mac's <c>ll_recording_match</c> results).</summary>
public enum RecordingCheck
{
    Match = 0,
    TitleMismatch = 1,
    ArtistMismatch = 2,
    MissingIdentity = 3,
    DurationMismatch = 4,
}

/// <summary>
/// The name and length checks the sources use to pick among their own search hits, matching the Mac providers.
/// </summary>
/// <remarks>
/// The lyric ranking code owns the shared versions of these helpers (on <see cref="LyricQuery"/>); this copy keeps the
/// sources self-contained until the two are merged, and must give the same answers. <see cref="Normalize"/>, the
/// word-overlap part of <see cref="Similarity"/> and <see cref="MatchScore"/> are written from a description of the
/// Mac's behaviour (its code for them is Verso-derived); <see cref="Core"/>, <see cref="Check"/> and the containment
/// and symbol rules are ported from Lunalyr's own code.
/// </remarks>
public static partial class SourceMatching
{
    /// <summary>True when the query names an artist (a local file without one still matches on title and length).</summary>
    public static bool HasArtist(LyricQuery query) => !string.IsNullOrWhiteSpace(query.Artist);

    /// <summary>Artist similarity for a source's own pre-filter; 0.5 when the query names no artist.</summary>
    public static double ArtistScore(LyricQuery query, string candidate) =>
        HasArtist(query) ? Similarity(query.Artist, candidate) : 0.5;

    /// <summary>
    /// The name folded for comparison: case and accents dropped, every run of characters that are not letters, marks or
    /// digits turned into one space, trimmed. "Beyoncé — HALO!" becomes "beyonce halo".
    /// </summary>
    public static string Normalize(string text)
    {
        var folded = Fold(text);
        var builder = new StringBuilder(folded.Length);
        var gap = false;
        foreach (var rune in folded.EnumerateRunes())
        {
            if (IsWordRune(rune))
            {
                if (gap && builder.Length > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(rune.ToString());
                gap = false;
            }
            else
            {
                gap = true;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 1 for the same name; 0.95 when only edition or credit qualifiers differ ("(2011 Remaster)", "(feat. X)"); 0.85
    /// when one contains the other as whole words (or as text, in scripts written without spaces); otherwise the share
    /// of words they have in common, out of the longer name's.
    /// </summary>
    public static double Similarity(string lhs, string rhs)
    {
        var a = Normalize(lhs);
        var b = Normalize(rhs);
        if (a.Length == 0 || b.Length == 0)
        {
            return SymbolsMatch(lhs, rhs) ? 1 : 0;
        }
        if (a == b)
        {
            return 1;
        }
        var coreA = Core(lhs);
        var coreB = Core(rhs);
        var x = coreA.Length == 0 ? a : coreA;
        var y = coreB.Length == 0 ? b : coreB;
        if (x == y)
        {
            return 0.95;
        }
        var wordsX = x.Split(' ').ToHashSet(StringComparer.Ordinal);
        var wordsY = y.Split(' ').ToHashSet(StringComparer.Ordinal);
        var shared = wordsX.Count(wordsY.Contains);
        var overlap = (double)shared / Math.Max(wordsX.Count, wordsY.Count);
        return Math.Max(overlap, Contains(x, y) ? 0.85 : 0);
    }

    /// <summary>
    /// The name without edition and credit qualifiers, normalised. Qualifiers are bracketed or follow " - ":
    /// feat./ft./featuring/with credits, remaster(ed), live, (radio) edit, version, mono and stereo.
    /// </summary>
    public static string Core(string text)
    {
        var value = Fold(text);
        foreach (var match in BracketPattern().Matches(value).Reverse())
        {
            if (IsQualifier(match.Groups[1].Value, allowsWith: true))
            {
                value = value.Remove(match.Index, match.Length);
            }
        }
        var dash = DashPattern().Match(value);
        if (dash.Success && IsQualifier(value[(dash.Index + dash.Length)..], allowsWith: false))
        {
            value = value[..dash.Index];
        }
        var credit = CreditPattern().Match(value);
        if (credit.Success)
        {
            value = value[..credit.Index];
        }
        return Normalize(value);
    }

    /// <summary>
    /// How sure a hit is to name the playing recording (the Mac's <c>ll_recording_match</c>): the title must be close,
    /// the artist too when compared, and when both lengths are known they must agree within 3 s or 2%.
    /// </summary>
    public static RecordingCheck Check(double titleConfidence, double artistConfidence, bool compareArtist,
        bool lengthRequired, bool compareLength, double trackDuration, double candidateDuration)
    {
        if (!double.IsFinite(titleConfidence) || titleConfidence < 0.75)
        {
            return RecordingCheck.TitleMismatch;
        }
        if (compareArtist && (!double.IsFinite(artistConfidence) || artistConfidence < 0.65))
        {
            return RecordingCheck.ArtistMismatch;
        }
        var hasLength = double.IsFinite(trackDuration) && double.IsFinite(candidateDuration) && trackDuration > 0 && candidateDuration > 0;
        if (lengthRequired && !hasLength)
        {
            return RecordingCheck.MissingIdentity;
        }
        if (compareLength && hasLength && Math.Abs(trackDuration - candidateDuration) > Math.Max(3, trackDuration * 0.02))
        {
            return RecordingCheck.DurationMismatch;
        }
        return RecordingCheck.Match;
    }

    /// <summary>
    /// Whether a hit names the playing recording, as the lookup's recording check will judge it. A local file whose
    /// artist matches is not held to its length; without an artist the length is all that confirms the recording.
    /// </summary>
    public static bool NamesRecording(LyricSourceId source, string title, string artist, double? duration, LyricQuery query)
    {
        var hasArtist = HasArtist(query);
        var lengthDecides = !(source == LyricSourceId.Lyrimuse && hasArtist);
        return Check(Similarity(query.Title, title), ArtistScore(query, artist), hasArtist, !hasArtist, lengthDecides,
            query.Duration, duration ?? 0) == RecordingCheck.Match;
    }

    /// <summary>
    /// A hit's score for ordering a source's own hits. Negative means "not this recording": a title below 0.62, an artist
    /// below 0.45, or a known length more than 25 s off. Otherwise title counts most, then artist, then how close the
    /// length is (when known), then word or line timing, then a matching album.
    /// </summary>
    public static double MatchScore(double title, double artist, double durationDelta, bool durationKnown,
        bool wordTimed, bool lineTimed, bool albumMatches)
    {
        if (!double.IsFinite(title) || !double.IsFinite(artist) || !double.IsFinite(durationDelta))
        {
            return Rejected;
        }
        if (title < 0.62 || artist < 0.45 || (durationKnown && durationDelta > 25))
        {
            return Rejected;
        }
        var length = durationKnown ? Math.Max(-22, 14 - (2 * durationDelta)) : 0;
        var timing = wordTimed ? 20 : lineTimed ? 7 : 0;
        return (48 * title) + (27 * artist) + length + timing + (albumMatches ? 4 : 0);
    }

    private const double Rejected = -1000;

    /// <summary>The score the Mac's catalogue providers give a line-timed hit (no album compared).</summary>
    public static double HitScore(LyricQuery query, string title, string artist, double? duration) =>
        MatchScore(Similarity(query.Title, title), ArtistScore(query, artist),
            duration is double d ? Math.Abs(d - query.Duration) : 0, duration is not null && query.Duration > 0,
            wordTimed: false, lineTimed: true, albumMatches: false);

    /// <summary>
    /// Hits that name the playing recording first, then by score, so a close name with the wrong length is not fetched
    /// ahead of the right recording. Ties keep the service's order.
    /// </summary>
    public static IReadOnlyList<T> Ordered<T>(IEnumerable<T> hits, LyricSourceId source, LyricQuery query,
        Func<T, (string Title, string Artist, double? Duration, double Score)> fields) =>
        [.. hits.Select(hit => (Hit: hit, Fields: fields(hit)))
            .Select(x => (x.Hit, Passes: NamesRecording(source, x.Fields.Title, x.Fields.Artist, x.Fields.Duration, query), x.Fields.Score))
            .OrderByDescending(x => x.Passes).ThenByDescending(x => x.Score)
            .Select(x => x.Hit)];

    /// <summary>Case and accent folding, as the Mac's diacritic- and case-insensitive folding.</summary>
    internal static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (!IsDiacritic(rune))
            {
                builder.Append(Rune.ToLowerInvariant(rune).ToString());
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsDiacritic(Rune rune) => rune.Value switch
    {
        >= 0x0300 and <= 0x036F => true,
        >= 0x1AB0 and <= 0x1AFF => true,
        >= 0x1DC0 and <= 0x1DFF => true,
        >= 0x20D0 and <= 0x20FF => true,
        >= 0xFE20 and <= 0xFE2F => true,
        0x3099 or 0x309A => true,
        _ => false,
    };

    /// <summary>Letters, marks and numbers, as Foundation's <c>alphanumerics</c> set has them.</summary>
    private static bool IsWordRune(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    private static readonly HashSet<string> QualifierWords = ["remaster", "remastered", "live", "edit", "version", "mono", "stereo"];

    private static bool IsQualifier(string text, bool allowsWith)
    {
        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }
        if (words[0] is "feat" or "ft" or "featuring" || (allowsWith && words[0] == "with"))
        {
            return true;
        }
        return words.Any(QualifierWords.Contains);
    }

    private static bool Contains(string a, string b)
    {
        var (longer, shorter) = a.Length >= b.Length ? (a, b) : (b, a);
        if ((" " + longer + " ").Contains(" " + shorter + " ", StringComparison.Ordinal))
        {
            return true;
        }
        // Scripts written without spaces have no word boundaries to respect.
        return shorter.EnumerateRunes().Any(IsUnspaced) && longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static bool IsUnspaced(Rune rune) => rune.Value switch
    {
        >= 0x0E00 and <= 0x0EFF => true,
        >= 0x1000 and <= 0x109F => true,
        >= 0x1780 and <= 0x17FF => true,
        >= 0x3040 and <= 0x30FF => true,
        >= 0x31F0 and <= 0x31FF => true,
        >= 0x3400 and <= 0x4DBF => true,
        >= 0x4E00 and <= 0x9FFF => true,
        >= 0xF900 and <= 0xFAFF => true,
        >= 0x20000 and <= 0x2FA1F => true,
        _ => false,
    };

    private static bool SymbolsMatch(string lhs, string rhs)
    {
        var a = Fold(lhs.Trim());
        return a.Length > 0 && a == Fold(rhs.Trim());
    }

    [GeneratedRegex(@"[(\[{（【]([^)\]}）】]*)[)\]}）】]")]
    private static partial Regex BracketPattern();

    [GeneratedRegex(@"\s+[-–—]\s+")]
    private static partial Regex DashPattern();

    [GeneratedRegex(@"\s(feat\.?|ft\.|featuring)\s")]
    private static partial Regex CreditPattern();
}
