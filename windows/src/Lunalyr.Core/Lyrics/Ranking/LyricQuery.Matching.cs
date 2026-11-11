using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics;

// Name matching for lyric lookups. Saved choices, cached results and per-song offsets are keyed by
// RecordingKey, so Normalize must keep its behaviour or existing entries stop matching.
// Normalize, RecordingKey and the first steps of Similarity were re-implemented from a behaviour description
// (the Mac's lines there trace back to the Verso import); the rest is ported from Lunalyr's own code.
public sealed partial record LyricQuery
{
    /// <summary>
    /// "title|artist|album|seconds", each name normalised and the length rounded to whole seconds. Saved choices,
    /// cached results and per-song offsets are keyed by this. Title aliases never take part.
    /// </summary>
    public string RecordingKey =>
        string.Join('|', Normalize(Title), Normalize(Artist), Normalize(Album ?? string.Empty),
            ((long)Math.Round(Duration, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture));

    /// <summary>A local file without an artist tag can still match on title and length.</summary>
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    /// <summary>
    /// Artist similarity for a provider's own pre-filter. Without an artist to compare, candidates pass on title
    /// and duration, which ranking enforces.
    /// </summary>
    public double ArtistScore(string candidate) => HasArtist ? Similarity(Artist, candidate) : 0.5;

    /// <summary>
    /// The titles a service's match may carry for this recording. Every title comparison in ranking goes through
    /// <see cref="TitleSimilarity"/>, which takes the best of these.
    /// </summary>
    /// <remarks>
    /// Extension point for localised titles: Apple Music can show a Japanese recording under its English title, so a
    /// service's Japanese title then fails the title check. When alternative titles are looked up (the Mac's
    /// <c>next/localized-titles</c> work), return them here after <see cref="Title"/>. They must never reach
    /// <see cref="RecordingKey"/>, and should not become a member of this record (that changes its equality): pass
    /// them through a side table or a wrapper and widen this method.
    /// </remarks>
    public IEnumerable<string> MatchTitles()
    {
        yield return Title;
    }

    /// <summary>How closely a service's title names this recording: the best <see cref="Similarity"/> over <see cref="MatchTitles"/>.</summary>
    public double TitleSimilarity(string candidateTitle)
    {
        var best = 0.0;
        foreach (var title in MatchTitles())
        {
            best = Math.Max(best, Similarity(title, candidateTitle));
        }
        return best;
    }

    /// <summary>
    /// Folds accents and case, then keeps the runs of letters, marks and numbers, joined by single spaces:
    /// "Don’t Stop (2011 Remaster)" gives "don t stop 2011 remaster".
    /// </summary>
    public static string Normalize(string text)
    {
        var folded = LyricTextFolding.Fold(text);
        var builder = new StringBuilder(folded.Length);
        var inWord = false;
        foreach (var rune in folded.EnumerateRunes())
        {
            if (LyricTextFolding.IsAlphanumeric(rune))
            {
                if (!inWord && builder.Length > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(rune.ToString());
                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 1 for the same name, 0.95 when only edition or credit qualifiers differ ("(2011 Remaster)" / "- Remastered
    /// 2011", "(feat. X)" / "(with X)"), 0.85 when one name contains the other as whole words, else word overlap.
    /// </summary>
    public static double Similarity(string lhs, string rhs)
    {
        var a = Normalize(lhs);
        var b = Normalize(rhs);
        // Names made only of symbols or emoji ("!!!", "🦋") have nothing left once normalised; compare them as written.
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
        var wordsX = new HashSet<string>(x.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        var wordsY = new HashSet<string>(y.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        var shared = wordsX.Count(wordsY.Contains);
        var token = (double)shared / Math.Max(wordsX.Count, wordsY.Count);
        return Math.Max(token, ContainsWholeWords(x, y) ? 0.85 : 0);
    }

    /// <summary>
    /// The name without edition and credit qualifiers, normalised. Qualifiers are bracketed or follow " - ":
    /// feat./ft./featuring/with credits, remaster(ed) with or without a year, live, (radio) edit, version, mono and
    /// stereo. Recordings are still told apart by duration.
    /// </summary>
    public static string Core(string text)
    {
        var value = LyricTextFolding.Fold(text);
        // Later matches are removed first, so earlier positions stay valid.
        var brackets = LyricNamePatterns.Bracket().Matches(value);
        for (var i = brackets.Count - 1; i >= 0; i--)
        {
            var match = brackets[i];
            if (IsQualifier(match.Groups[1].Value, allowsWith: true))
            {
                value = value.Remove(match.Index, match.Length);
            }
        }
        var dash = LyricNamePatterns.Dash().Match(value);
        if (dash.Success && IsQualifier(value[(dash.Index + dash.Length)..], allowsWith: false))
        {
            value = value[..dash.Index];
        }
        var credit = LyricNamePatterns.Credit().Match(value);
        if (credit.Success)
        {
            value = value[..credit.Index];
        }
        return Normalize(value);
    }

    /// <summary>
    /// A name a service reported, when it can be checked against the one asked for: it matches, or both are written
    /// in Latin script, so a mismatch means a different name. Null when it may only be the same name in another script
    /// ("米津玄師" for "Kenshi Yonezu").
    /// </summary>
    public static string? Checkable(string? reported, string asked)
    {
        if (reported is null)
        {
            return null;
        }
        return Similarity(asked, reported) >= 0.75 || (IsLatin(asked) && IsLatin(reported)) ? reported : null;

        static bool IsLatin(string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (LyricTextFolding.IsAlphabetic(rune) && rune.Value >= 0x0250 && rune.Value is not (>= 0x1E00 and <= 0x1EFF))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>"3:41", or "1:02:03" past an hour. Lengths beyond ten days (a broken reply) show as ten days.</summary>
    public static string Clock(double seconds)
    {
        var clamped = Math.Min(864000, Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        var total = (long)Math.Round(clamped, MidpointRounding.AwayFromZero);
        long h = total / 3600, m = total / 60 % 60, s = total % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    private static readonly HashSet<string> QualifierWords =
        new(["remaster", "remastered", "live", "edit", "version", "mono", "stereo"], StringComparer.Ordinal);

    private static bool IsQualifier(string text, bool allowsWith)
    {
        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }
        var first = words[0];
        if (first is "feat" or "ft" or "featuring" || (allowsWith && first == "with"))
        {
            return true;
        }
        return words.Any(QualifierWords.Contains);
    }

    private static bool ContainsWholeWords(string a, string b)
    {
        var (longer, shorter) = TextElements.Count(a) >= TextElements.Count(b) ? (a, b) : (b, a);
        if ((" " + longer + " ").Contains(" " + shorter + " ", StringComparison.Ordinal))
        {
            return true;
        }
        // Scripts written without spaces have no word boundaries to respect.
        return shorter.EnumerateRunes().Any(IsUnspaced) && longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static bool IsUnspaced(Rune rune) => rune.Value is
        (>= 0x0E00 and <= 0x0EFF) or (>= 0x1000 and <= 0x109F) or (>= 0x1780 and <= 0x17FF)
        or (>= 0x3040 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0x3400 and <= 0x4DBF)
        or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x2FA1F);

    private static bool SymbolsMatch(string lhs, string rhs)
    {
        var a = LyricTextFolding.Fold(lhs.Trim());
        return a.Length > 0 && a == LyricTextFolding.Fold(rhs.Trim());
    }
}

/// <summary>Patterns for reading qualifiers out of song names.</summary>
internal static partial class LyricNamePatterns
{
    [GeneratedRegex(@"[(\[{（【]([^)\]}）】]*)[)\]}）】]", RegexOptions.CultureInvariant)]
    public static partial Regex Bracket();

    [GeneratedRegex(@"\s+[-–—]\s+", RegexOptions.CultureInvariant)]
    public static partial Regex Dash();

    [GeneratedRegex(@"\s(feat\.?|ft\.|featuring)\s", RegexOptions.CultureInvariant)]
    public static partial Regex Credit();
}
