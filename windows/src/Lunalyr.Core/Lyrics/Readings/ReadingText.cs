using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// Unicode questions the reading code asks, answered the way Swift answers them on the Mac: a "character" is a
/// grapheme cluster (a <see cref="Grapheme"/>), and its letter, number and whitespace tests look at its first scalar.
/// .NET's <c>Regex</c> has no script classes (<c>\p{Latin}</c>, <c>\p{Han}</c>), so the scripts are range tables
/// taken from Unicode's Scripts.txt and PropList.txt.
/// </summary>
public static class ReadingText
{
    /// <summary>One grapheme cluster of a string, with its UTF-16 offset and length.</summary>
    public readonly record struct Grapheme(string Text, int Offset, int Length)
    {
        public Rune First => Rune.GetRuneAt(Text, 0);
        public bool IsLetter => IsAlphabetic(First);
        public bool IsNumber => ReadingText.IsNumber(First);
        public bool IsLetterOrNumber => IsLetter || IsNumber;
        public bool IsWhitespace => Rune.IsWhiteSpace(First);
        public override string ToString() => Text;
    }

    /// <summary>The grapheme clusters of <paramref name="text"/> in order.</summary>
    public static List<Grapheme> Graphemes(string text)
    {
        var result = new List<Grapheme>(text.Length);
        var offset = 0;
        while (offset < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text, offset);
            if (length <= 0) length = 1;
            result.Add(new Grapheme(text.Substring(offset, length), offset, length));
            offset += length;
        }
        return result;
    }

    /// <summary>How many grapheme clusters <paramref name="text"/> has (Swift's <c>String.count</c>).</summary>
    public static int GraphemeCount(string text)
    {
        var count = 0;
        for (var offset = 0; offset < text.Length; count++)
        {
            var length = StringInfo.GetNextTextElementLength(text, offset);
            offset += length <= 0 ? 1 : length;
        }
        return count;
    }

    /// <summary>The Unicode scalars of <paramref name="text"/>.</summary>
    public static IEnumerable<Rune> Scalars(string text) => text.EnumerateRunes();

    /// <summary>The first scalar, or null for an empty string.</summary>
    public static Rune? FirstScalar(string text) => text.Length == 0 ? null : Rune.GetRuneAt(text, 0);

    /// <summary>The last scalar, or null for an empty string.</summary>
    public static Rune? LastScalar(string text)
    {
        if (text.Length == 0) return null;
        var index = text.Length - 1;
        if (char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1])) index--;
        return Rune.GetRuneAt(text, index);
    }

    /// <summary>The Unicode Alphabetic property (Swift's <c>Character.isLetter</c> and <c>isAlphabetic</c>):
    /// letters, letter numbers, and the vowel signs and other marks that spell sounds. Combining accents are not.</summary>
    public static bool IsAlphabetic(Rune rune)
    {
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.SpacingCombiningMark:
                return true;
            case UnicodeCategory.NonSpacingMark:
                return !IsGenericCombiningMark(rune.Value);
            case UnicodeCategory.OtherSymbol:
                // Circled letters (ⓐ) are alphabetic.
                return rune.Value is >= 0x24B6 and <= 0x24E9 or >= 0x1F130 and <= 0x1F189;
            default:
                return false;
        }
    }

    private static bool IsGenericCombiningMark(int value) =>
        value is >= 0x0300 and <= 0x036F or >= 0x0483 and <= 0x0489 or >= 0x1AB0 and <= 0x1AFF
            or >= 0x1DC0 and <= 0x1DFF or >= 0x20D0 and <= 0x20FF or >= 0xFE20 and <= 0xFE2F
            or 0x3099 or 0x309A or >= 0x302A and <= 0x302F or 0x094D or 0x09CD or 0x0A4D or 0x0ACD
            or 0x0B4D or 0x0BCD or 0x0C4D or 0x0CCD or 0x0D4D or 0x0DCA or 0x0E3A or 0x0E4E or 0x0F84 or 0x1039
            or 0x103A or 0x17D2 or 0x0E47 or 0x0E48 or 0x0E49 or 0x0E4A or 0x0E4B or 0x0E4C;

    /// <summary>Swift's <c>Character.isNumber</c>: any scalar with a numeric type, which includes the ideographs
    /// used as numerals (一, 十, 万).</summary>
    public static bool IsNumber(Rune rune)
    {
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.DecimalDigitNumber:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.OtherNumber:
                return true;
        }
        return rune.Value < 0x10000 && NumericIdeographs.Contains((char)rune.Value);
    }

    private const string NumericIdeographs = "〇一二三四五六七八九十百千万億兆零壹贰貳叁參弎肆伍陆陸柒捌玖拾佰仟廿卅卌";

    public static bool IsLetterOrNumber(Rune rune) => IsAlphabetic(rune) || IsNumber(rune);

    /// <summary>Letters or digits anywhere in <paramref name="text"/> (by grapheme, as Swift's
    /// <c>contains { $0.isLetter || $0.isNumber }</c>).</summary>
    public static bool HasLetterOrNumber(string text)
    {
        foreach (var grapheme in Graphemes(text))
        {
            if (grapheme.IsLetterOrNumber) return true;
        }
        return false;
    }

    /// <summary>Only the graphemes that are letters or digits (Swift's <c>filter { $0.isLetter || $0.isNumber }</c>).</summary>
    public static string LettersAndNumbers(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var grapheme in Graphemes(text))
        {
            if (grapheme.IsLetterOrNumber) builder.Append(grapheme.Text);
        }
        return builder.ToString();
    }

    /// <summary>Only the graphemes that are not whitespace.</summary>
    public static string WithoutWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var grapheme in Graphemes(text))
        {
            if (!grapheme.IsWhitespace) builder.Append(grapheme.Text);
        }
        return builder.ToString();
    }

    /// <summary>Splits at whitespace, dropping empty parts (Swift's <c>split(whereSeparator: \.isWhitespace)</c>).</summary>
    public static List<string> SplitAtWhitespace(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var grapheme in Graphemes(text))
        {
            if (grapheme.IsWhitespace)
            {
                if (current.Length > 0) parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(grapheme.Text);
            }
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    // MARK: - Scripts

    /// <summary>The Latin script (Scripts.txt), full-width Latin included.</summary>
    public static bool IsLatinScript(Rune rune) => InRanges(rune.Value, LatinRanges);

    /// <summary>The Han script: ideographs, radicals and the iteration and closing marks 々 〆 〇.</summary>
    public static bool IsHanScript(Rune rune) => InRanges(rune.Value, HanRanges);

    /// <summary>The Hiragana script. ー, ・ and the voicing marks are Common and are not included.</summary>
    public static bool IsHiraganaScript(Rune rune) =>
        rune.Value is >= 0x3041 and <= 0x3096 or >= 0x309D and <= 0x309F or >= 0x1B001 and <= 0x1B11F
            or 0x1B132 or >= 0x1B150 and <= 0x1B152 or 0x1F200;

    /// <summary>The Katakana script, half-width forms included; ー and ・ are Common and are not.</summary>
    public static bool IsKatakanaScript(Rune rune) =>
        rune.Value is >= 0x30A1 and <= 0x30FA or >= 0x30FD and <= 0x30FF or >= 0x31F0 and <= 0x31FF
            or >= 0x32D0 and <= 0x32FE or >= 0x3300 and <= 0x3357 or >= 0xFF66 and <= 0xFF6F or >= 0xFF71 and <= 0xFF9D
            or >= 0x1AFF0 and <= 0x1AFFE or 0x1B000 or >= 0x1B120 and <= 0x1B122 or 0x1B155 or >= 0x1B164 and <= 0x1B167;

    /// <summary>The Unicode Ideographic property (Swift's <c>properties.isIdeographic</c>).</summary>
    public static bool IsIdeographic(Rune rune) =>
        rune.Value is 0x3006 or 0x3007 or >= 0x3021 and <= 0x3029 or >= 0x3038 and <= 0x303A
            or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFA6D or >= 0xFA70 and <= 0xFAD9
            or 0x16FE4 or >= 0x17000 and <= 0x187F7 or >= 0x18800 and <= 0x18CD5 or >= 0x18D00 and <= 0x18D08
            or >= 0x1B170 and <= 0x1B2FB or >= 0x20000 and <= 0x2FA1D or >= 0x30000 and <= 0x323AF;

    /// <summary>Kana from either script (Swift's <c>[\p{Hiragana}\p{Katakana}]</c>).</summary>
    public static bool HasKana(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHiraganaScript(rune) || IsKatakanaScript(rune)) return true;
        }
        return false;
    }

    /// <summary>Any Han character (Swift's <c>\p{Han}</c>).</summary>
    public static bool HasHan(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHanScript(rune)) return true;
        }
        return false;
    }

    private static bool InRanges(int value, int[] ranges)
    {
        // Pairs of inclusive bounds, sorted.
        int low = 0, high = ranges.Length / 2 - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (value < ranges[middle * 2]) high = middle - 1;
            else if (value > ranges[middle * 2 + 1]) low = middle + 1;
            else return true;
        }
        return false;
    }

    private static readonly int[] LatinRanges =
    [
        0x0041, 0x005A, 0x0061, 0x007A, 0x00AA, 0x00AA, 0x00BA, 0x00BA, 0x00C0, 0x00D6, 0x00D8, 0x00F6,
        0x00F8, 0x02B8, 0x02E0, 0x02E4, 0x1D00, 0x1D25, 0x1D2C, 0x1D5C, 0x1D62, 0x1D65, 0x1D6B, 0x1D77,
        0x1D79, 0x1DBE, 0x1E00, 0x1EFF, 0x2071, 0x2071, 0x207F, 0x207F, 0x2090, 0x209C, 0x212A, 0x212B,
        0x2132, 0x2132, 0x214E, 0x214E, 0x2160, 0x2188, 0x2C60, 0x2C7F, 0xA722, 0xA787, 0xA78B, 0xA7CA,
        0xA7D0, 0xA7D1, 0xA7D3, 0xA7D3, 0xA7D5, 0xA7D9, 0xA7F2, 0xA7FF, 0xAB30, 0xAB5A, 0xAB5C, 0xAB64,
        0xAB66, 0xAB69, 0xFB00, 0xFB06, 0xFF21, 0xFF3A, 0xFF41, 0xFF5A, 0x10780, 0x10785, 0x10787, 0x107B0,
        0x107B2, 0x107BA, 0x1DF00, 0x1DF1E, 0x1DF25, 0x1DF2A,
    ];

    private static readonly int[] HanRanges =
    [
        0x2E80, 0x2E99, 0x2E9B, 0x2EF3, 0x2F00, 0x2FD5, 0x3005, 0x3005, 0x3007, 0x3007, 0x3021, 0x3029,
        0x3038, 0x303B, 0x3400, 0x4DBF, 0x4E00, 0x9FFF, 0xF900, 0xFA6D, 0xFA70, 0xFAD9, 0x16FE2, 0x16FE3,
        0x16FF0, 0x16FF1, 0x20000, 0x2A6DF, 0x2A700, 0x2EBE0, 0x2EBF0, 0x2EE5D, 0x2F800, 0x2FA1D,
        0x30000, 0x3134A, 0x31350, 0x323AF,
    ];

    // MARK: - Folding

    /// <summary>Accents and other combining marks removed (Apple's <c>stripDiacritics</c> transform: decompose,
    /// drop non-spacing marks, recompose). Not ICU's Latin-ASCII, which also rewrites letters such as ł.</summary>
    public static string StripDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark) builder.Append(rune.ToString());
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Case and accents folded away, as Foundation's <c>folding([.caseInsensitive, .diacriticInsensitive])</c>.</summary>
    public static string FoldCaseAndDiacritics(string text) => StripDiacritics(text).ToLowerInvariant();

    /// <summary>Whether a scalar is one of the ASCII letters.</summary>
    public static bool IsAsciiLetter(Rune rune) => rune.Value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
