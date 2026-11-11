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
}
