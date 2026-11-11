using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>Text helpers that count the way the Mac's Swift strings do (user-perceived characters).</summary>
internal static class TextElements
{
    /// <summary>Grapheme clusters in <paramref name="text"/> (Swift's <c>String.count</c>). Allocation-free.</summary>
    public static int Count(ReadOnlySpan<char> text)
    {
        var count = 0;
        while (!text.IsEmpty)
        {
            var length = StringInfo.GetNextTextElementLength(text);
            if (length <= 0) break;
            text = text[length..];
            count++;
        }
        return count;
    }

    /// <summary>True when the text has a letter or a digit (anything sung rather than punctuation).</summary>
    public static bool HasLettersOrDigits(ReadOnlySpan<char> text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) return true;
        return false;
    }

    /// <summary>
    /// Han, kana and Hangul, and the scripts written without spaces between words (Thai, Lao, Myanmar, Khmer): every
    /// timed syllable of these is its own token. (The parsers have the same rule; this copy keeps the timing code free
    /// of a dependency on them.)
    /// </summary>
    public static bool KeepsSyllables(ReadOnlySpan<char> text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (KeepsSyllables(rune.Value)) return true;
        return false;
    }

    public static bool KeepsSyllables(int v) =>
        v is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7FF // Hangul
            or >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F // Kana
            or 0x3005 or 0x3007 or 0x303B or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF // Han
            or >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF; // Thai, Lao, Myanmar, Khmer
}
