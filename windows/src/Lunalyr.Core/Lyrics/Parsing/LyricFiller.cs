// Chinese catalogues' filler for songs without words (tester feedback on 0.9.0, 14 Nov 2026): "纯音乐，请欣赏"
// ("instrumental, please enjoy") timed over an intro, before the real lyrics. Such a line is dropped; every other line
// stays, and lyrics that are only filler are no lyrics. The Mac's rule exactly (LyricCredits.isInstrumentalNotice,
// next/tester-fixes, 14 Nov 2026): NFKC; drop separators, controls, format characters, punctuation and symbols; fold the
// traditional characters; at most 24 left; then the whole string must be ^(此歌曲为没有填词的)?纯音乐请您?欣赏$.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Parsing;

public static partial class LyricFiller
{
    private const int MaxLength = 24;

    [GeneratedRegex("^(此歌曲为没有填词的)?纯音乐请您?欣赏$", RegexOptions.CultureInvariant)]
    private static partial Regex Notice();

    /// <summary>Whether <paramref name="text"/> is, as a whole, the filler for a song without words.</summary>
    public static bool IsFiller(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    or UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
                    or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
                    or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation
                    or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol
                    or UnicodeCategory.OtherSymbol:
                    continue;
            }
            var folded = rune.Value switch
            {
                '純' => "纯", '樂' => "乐", '請' => "请", '賞' => "赏", '為' => "为", '沒' => "没", '詞' => "词",
                _ => rune.ToString(),
            };
            kept.Append(folded);
            if (kept.Length > MaxLength) return false;
        }
        return Notice().IsMatch(kept.ToString());
    }

    /// <summary><paramref name="lines"/> without filler lines (the same list when there are none).</summary>
    public static IReadOnlyList<LyricLine> Without(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Any(l => IsFiller(l.Text)) ? [.. lines.Where(l => !IsFiller(l.Text))] : lines;
    }

    /// <summary><paramref name="lines"/> without filler lines (the same list when there are none).</summary>
    public static IReadOnlyList<string> Without(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Any(IsFiller) ? [.. lines.Where(l => !IsFiller(l))] : lines;
    }
}
