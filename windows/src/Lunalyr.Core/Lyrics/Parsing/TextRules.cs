// Small text helpers that mirror how the Mac app's Swift code reads strings, so both apps agree on the same input:
// Swift's `.whitespaces` (no newlines) versus `.whitespacesAndNewlines`, Character counts (grapheme clusters, not
// UTF-16 units), letters and numbers judged per Unicode scalar, and Swift's strict `Double(String)`.

using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Parsing;

internal static class TextRules
{
    /// <summary>Swift's <c>CharacterSet.whitespaces</c>: space separators (Zs) and the tab, not line breaks.</summary>
    public static bool IsSpace(char c) => c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>Swift's <c>whitespacesAndNewlines</c> / <c>Character.isWhitespace</c>.</summary>
    public static bool IsSpaceOrNewline(char c) => char.IsWhiteSpace(c);

    /// <summary>Trims Swift's <c>.whitespaces</c> (spaces and tabs, not line breaks).</summary>
    public static string TrimSpaces(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && IsSpace(text[start])) start++;
        while (end > start && IsSpace(text[end - 1])) end--;
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    /// <summary>Trims Swift's <c>.whitespacesAndNewlines</c>.</summary>
    public static string TrimAll(string text) => text.Trim();

    public static bool AllWhitespace(ReadOnlySpan<char> text)
    {
        foreach (var c in text) if (!char.IsWhiteSpace(c)) return false;
        return true;
    }

    public static bool StartsWithWhitespace(string text) => text.Length > 0 && char.IsWhiteSpace(text[0]);
    public static bool EndsWithWhitespace(string text) => text.Length > 0 && char.IsWhiteSpace(text[^1]);

    /// <summary>The text without any whitespace, for "do these cover the same characters" checks.</summary>
    public static string WithoutWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) if (!char.IsWhiteSpace(c)) builder.Append(c);
        return builder.ToString();
    }

    /// <summary>Only the letters and digits of the text (Swift's <c>isLetter || isNumber</c>), per scalar.</summary>
    public static string LettersAndNumbers(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    public static bool HasLetterOrNumber(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) return true;
        }
        return false;
    }

    /// <summary>Grapheme clusters, as Swift's <c>String.count</c> counts them.</summary>
    public static int CharacterCount(string text) => text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;

    /// <summary>The text split into grapheme clusters (Swift Characters).</summary>
    public static string[] Characters(string text)
    {
        if (text.Length == 0) return [];
        var result = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) result.Add((string)enumerator.Current);
        return [.. result];
    }

    /// <summary>
    /// Swift's <c>Double(String)</c>: an optional sign, digits with an optional fraction and exponent, or
    /// "inf"/"nan". No surrounding whitespace, no thousands separators.
    /// </summary>
    public static bool TryParseDouble(ReadOnlySpan<char> text, out double value)
    {
        value = 0;
        if (text.IsEmpty || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])) return false;
        var body = text[0] is '+' or '-' ? text[1..] : text;
        if (body.Equals("inf", StringComparison.OrdinalIgnoreCase) || body.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            value = text[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }
        if (body.Equals("nan", StringComparison.OrdinalIgnoreCase))
        {
            value = double.NaN;
            return true;
        }
        foreach (var c in body)
        {
            if (!(char.IsAsciiDigit(c) || c is '.' or 'e' or 'E' or '+' or '-')) return false;
        }
        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Case and diacritic folded, then cut into its runs of letters, digits and marks, joined by single spaces. This
    /// is the behaviour of the Mac's <c>LyricQuery.normalize</c>, whose lines trace to the Verso import, re-implemented
    /// from that description. A private copy for the credit heading rules; the ranking code owns the shared one.
    /// </summary>
    public static string Normalize(string text)
    {
        var folded = FoldCaseAndDiacritics(text);
        var builder = new StringBuilder(folded.Length);
        var pendingSpace = false;
        foreach (var rune in folded.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var alphanumeric = Rune.IsLetter(rune) || Rune.IsNumber(rune) || category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            if (!alphanumeric)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>Lower-cased with combining marks removed (Foundation's diacritic- and case-insensitive folding).</summary>
    public static string FoldCaseAndDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
