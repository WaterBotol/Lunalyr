using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// The text folding every name comparison uses: accents and other non-spacing marks removed, then case folded.
/// One place, so recording keys, similarity, version tags and syllable counts all fold the same way.
/// </summary>
/// <remarks>
/// The Mac uses Foundation's diacritic- and case-insensitive folding, which cannot run here. This follows its
/// documented behaviour (decompose, drop the marks, recompose, case fold); edge cases that may differ from Foundation
/// (ß, kana voicing marks, Greek final sigma) are pinned by tests and listed in the port report.
/// </remarks>
internal static class LyricTextFolding
{
    /// <summary>Removes non-spacing marks (accents, kana voicing marks) and folds case.</summary>
    public static string Fold(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            AppendCaseFolded(builder, rune);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary><see cref="Fold"/> after mapping full-width and half-width forms to their ordinary width.</summary>
    public static string FoldWidth(string text) => Fold(WidthFold(text));

    private static void AppendCaseFolded(StringBuilder builder, Rune rune)
    {
        switch (rune.Value)
        {
            case 0x00DF: // ß
            case 0x1E9E: // ẞ
                builder.Append("ss");
                return;
            case 0x03C2: // ς, final sigma
                builder.Append('σ');
                return;
        }
        builder.Append(Rune.ToLowerInvariant(rune).ToString());
    }

    private static string WidthFold(string text)
    {
        // Only the Halfwidth and Fullwidth Forms block and the ideographic space change width.
        var needs = false;
        foreach (var c in text)
        {
            if (c is >= '＀' and <= '￯' or '　')
            {
                needs = true;
                break;
            }
        }
        if (!needs)
        {
            return text;
        }
        var builder = new StringBuilder(text.Length);
        var run = new StringBuilder();
        foreach (var c in text)
        {
            if (c is >= '＀' and <= '￯')
            {
                run.Append(c);
                continue;
            }
            Flush();
            builder.Append(c == '　' ? ' ' : c);
        }
        Flush();
        return builder.ToString();

        void Flush()
        {
            if (run.Length > 0)
            {
                // Compatibility composition maps exactly these forms to their ordinary width.
                builder.Append(run.ToString().Normalize(NormalizationForm.FormKC));
                run.Clear();
            }
        }
    }

    /// <summary>Unicode letter, mark or number: what the Mac's alphanumeric character set holds.</summary>
    public static bool IsAlphanumeric(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter => true,
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => true,
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    /// <summary>Letter or number, judged on a grapheme cluster's first scalar.</summary>
    public static bool IsLetterOrNumber(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter => true,
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    /// <summary>Unicode "Alphabetic", close enough for script checks: letters and letter numbers.</summary>
    public static bool IsAlphabetic(Rune rune) =>
        Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.LetterNumber;

    /// <summary>The grapheme clusters of <paramref name="text"/>, as the Mac's Characters.</summary>
    public static IEnumerable<string> Graphemes(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }

    /// <summary>Only the grapheme clusters that start with a letter or number, in canonical composed form.</summary>
    public static string LettersAndNumbers(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var cluster in Graphemes(text))
        {
            if (Rune.DecodeFromUtf16(cluster, out var first, out _) == System.Buffers.OperationStatus.Done && IsLetterOrNumber(first))
            {
                builder.Append(cluster);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
