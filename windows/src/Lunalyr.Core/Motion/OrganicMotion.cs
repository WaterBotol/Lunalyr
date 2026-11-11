using System.Globalization;
using System.Text;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Motion;

/// <summary>Word motion that follows the voice, from the timing alone.</summary>
public static class OrganicMotion
{
    /// <summary>Time from one onset to the next, which sets how briskly a word moves (at least 0.12 s for the last word).</summary>
    public static double Cadence(IReadOnlyList<LyricWord> words, int index)
    {
        var word = words[index];
        if (index + 1 < words.Count && words[index + 1].Start > word.Start) return words[index + 1].Start - word.Start;
        return Math.Max(word.End - word.Start, 0.12);
    }

    /// <summary>
    /// How much the voice is carrying right now, 0…1: dense, quick delivery and long held notes both read as intensity.
    /// A pure function of time, so a seek lands on the same value.
    /// </summary>
    public static double VocalIntensity(IReadOnlyList<LyricWord> words, double time)
    {
        double onsets = 0, held = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var distance = Math.Abs(word.Start - time);
            if (distance < 2.5) onsets += Math.Exp(-distance * distance / 1.2);
            if (time >= word.Start - 0.1 && time <= word.End + 0.3)
                held = Math.Max(held, Easing.Smoothstep((word.End - word.Start - 0.4) / 1.2));
        }
        return Math.Min(1, Math.Max(0, (onsets - 0.8) / 3.2) * 0.7 + held * 0.45);
    }

    /// <summary>Idle float base for layouts that draw links between words (the constellations).</summary>
    public const double ConnectedLayoutFloat = 1.0;
    /// <summary>Idle float base for the Echo layout.</summary>
    public const double EchoLayoutFloat = 0.8;
    /// <summary>Idle float base for every other layout.</summary>
    public const double DefaultLayoutFloat = 0.6;

    /// <summary>
    /// The slow float settled words keep, so a held line is never frozen: the layout's base
    /// (<see cref="ConnectedLayoutFloat"/>, <see cref="EchoLayoutFloat"/>, <see cref="DefaultLayoutFloat"/>) scaled by the
    /// song's mood intensity (0…1). None under Reduce Motion.
    /// </summary>
    public static double IdleAmplitude(double layoutBase, double moodIntensity, bool reduceMotion) =>
        reduceMotion ? 0 : layoutBase * (0.55 + 0.45 * moodIntensity);
}

/// <summary>
/// Script rules for motion: right-to-left and cursive scripts must be shaped as whole words, since drawing one glyph at
/// a time both reverses their order and breaks joining.
/// </summary>
public static class ScriptDirection
{
    /// <summary>Whether the first letter of the text is from a right-to-left script.</summary>
    public static bool IsRightToLeft(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (IsAlphabetic(rune)) return RightToLeft(rune.Value);
        return false;
    }

    /// <summary>
    /// True when glyphs may be transformed one at a time without breaking the word: no cursive joining, no combining
    /// marks, no complex shaping.
    /// </summary>
    public static bool AllowsGlyphMotion(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark) return false;
            var v = rune.Value;
            if (v < 0x0250) continue;                       // Latin and IPA
            if (v is >= 0x0370 and <= 0x052F) continue;     // Greek, Cyrillic
            if (v is >= 0x0530 and <= 0x058F) continue;     // Armenian
            if (v is >= 0x0590 and <= 0x05FF) continue;     // Hebrew (not joined)
            if (v is >= 0x10A0 and <= 0x10FF) continue;     // Georgian
            if (v is >= 0x1E00 and <= 0x1FFF) continue;     // Latin/Greek extended
            if (v is >= 0x2000 and <= 0x2BFF) continue;     // punctuation, symbols
            if (v is >= 0x3000 and <= 0x30FF) continue;     // CJK punctuation, kana
            if (v is >= 0x3400 and <= 0x9FFF) continue;     // CJK ideographs
            if (v is >= 0xAC00 and <= 0xD7AF) continue;     // Hangul syllables
            if (v is >= 0xF900 and <= 0xFAFF) continue;     // CJK compatibility
            if (v is >= 0xFF00 and <= 0xFFEF) continue;     // full width forms
            return false;
        }
        return true;
    }

    /// <summary>A line reads right to left when most of its letters do.</summary>
    public static bool LineIsRightToLeft(IEnumerable<string> texts)
    {
        int rtl = 0, ltr = 0;
        foreach (var text in texts)
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsAlphabetic(rune)) continue;
            if (RightToLeft(rune.Value)) rtl++;
            else ltr++;
        }
        return rtl > ltr;
    }

    private static bool IsAlphabetic(Rune rune) =>
        Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.LetterNumber;

    private static bool RightToLeft(int v) =>
        v is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF or >= 0x10800 and <= 0x10FFF
            or >= 0x1E800 and <= 0x1EFFF;
}
