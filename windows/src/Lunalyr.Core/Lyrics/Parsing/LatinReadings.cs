// Windows stand-ins for the Mac's system transliteration (`applyingTransform(.toLatin)`) and Japanese tokenizer,
// used only to tell a supplied reading from a translation (LyricScript.IsReading). Written for this port: the Mac has
// no equivalent code. Romanisation proper belongs to the romanisation workstream, which can pass a fuller
// LatinReader to LyricScript.ReadingCache.

using System.Text;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// A plain Latin reading of kana (Hepburn), Hangul (Revised Romanization), Cyrillic and Greek. Returns null when the
/// text has letters of any other script (Han characters in particular), so callers know it could not be read.
/// </summary>
public static class BasicLatinReading
{
    // Hiragana from U+3041 to U+3096. "*" marks a small kana that changes the one before it.
    private static readonly string[] Hiragana =
    [
        "*a", "a", "*i", "i", "*u", "u", "*e", "e", "*o", "o", "ka", "ga", "ki", "gi", "ku", "gu", "ke", "ge", "ko", "go",
        "sa", "za", "shi", "ji", "su", "zu", "se", "ze", "so", "zo", "ta", "da", "chi", "ji", "*tsu", "tsu", "zu", "te", "de",
        "to", "do", "na", "ni", "nu", "ne", "no", "ha", "ba", "pa", "hi", "bi", "pi", "fu", "bu", "pu", "he", "be", "pe", "ho",
        "bo", "po", "ma", "mi", "mu", "me", "mo", "*ya", "ya", "*yu", "yu", "*yo", "yo", "ra", "ri", "ru", "re", "ro", "*wa",
        "wa", "i", "e", "o", "n", "vu", "ka", "ke",
    ];

    private static readonly string[] KatakanaV = ["va", "vi", "ve", "vo"];

    private static readonly string[] HangulInitials =
        ["g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h"];
    private static readonly string[] HangulVowels =
    [
        "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu",
        "ui", "i",
    ];
    private static readonly string[] HangulFinals =
    [
        "", "k", "k", "k", "n", "n", "n", "t", "l", "k", "m", "l", "l", "l", "p", "l", "m", "p", "p", "t", "t", "ng", "t",
        "t", "k", "t", "p", "t",
    ];

    // Cyrillic а..я (U+0430..U+044F).
    private static readonly string[] Cyrillic =
    [
        "a", "b", "v", "g", "d", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "kh", "ts",
        "ch", "sh", "shch", "", "y", "", "e", "yu", "ya",
    ];

    // Greek α..ω (U+03B1..U+03C9).
    private static readonly string[] Greek =
    [
        "a", "v", "g", "d", "e", "z", "i", "th", "i", "k", "l", "m", "n", "x", "o", "p", "r", "s", "s", "t", "y", "f", "ch",
        "ps", "o",
    ];

    public static string? Read(string text)
    {
        var builder = new StringBuilder(text.Length * 2);
        var gemination = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x30A1 and <= 0x30F6) v -= 0x60; // katakana as hiragana
            if (v is >= 0x3041 and <= 0x3096)
            {
                var kana = Hiragana[v - 0x3041];
                if (kana == "*tsu") { gemination = true; continue; }
                if (kana[0] == '*') { AttachSmall(builder, kana[1..]); continue; }
                if (gemination && kana.Length > 0 && kana[0] is not ('a' or 'i' or 'u' or 'e' or 'o' or 'n')) builder.Append(kana[0]);
                gemination = false;
                builder.Append(kana);
                continue;
            }
            gemination = false;
            if (v is >= 0x30F7 and <= 0x30FA) { builder.Append(KatakanaV[v - 0x30F7]); continue; }
            if (v == 0x30FC) { RepeatVowel(builder); continue; }
            if (v is >= 0xAC00 and <= 0xD7A3)
            {
                var index = v - 0xAC00;
                builder.Append(HangulInitials[index / 588]).Append(HangulVowels[index % 588 / 28]).Append(HangulFinals[index % 28]);
                continue;
            }
            var lower = Rune.ToLowerInvariant(rune).Value;
            if (lower is >= 0x0430 and <= 0x044F) { builder.Append(Cyrillic[lower - 0x0430]); continue; }
            if (lower is 0x0451) { builder.Append("yo"); continue; }
            if (lower is 0x0456) { builder.Append('i'); continue; }
            if (lower is 0x0457) { builder.Append("yi"); continue; }
            if (lower is 0x0454) { builder.Append("ye"); continue; }
            if (lower is 0x0491) { builder.Append('g'); continue; }
            if (lower is >= 0x0370 and <= 0x03FF or >= 0x1F00 and <= 0x1FFF)
            {
                var plain = rune.ToString().Normalize(NormalizationForm.FormD).ToLowerInvariant()[0];
                if (plain is >= 'α' and <= 'ω') { builder.Append(Greek[plain - 0x03B1]); continue; }
                return null;
            }
            if (Rune.IsLetter(rune))
            {
                if (LyricScript.IsLatinScript(rune.Value)) { builder.Append(rune.ToString()); continue; }
                return null;
            }
            builder.Append(' ');
        }
        return builder.ToString();
    }

    /// <summary>Small ya/yu/yo make a contracted sound (きゃ "kya", しゃ "sha"); small vowels replace the vowel (ふぁ "fa").</summary>
    private static void AttachSmall(StringBuilder builder, string small)
    {
        if (builder.Length == 0) { builder.Append(small); return; }
        if (small[0] == 'y' && builder[^1] == 'i')
        {
            builder.Length--;
            var soft = builder.Length > 0 && (builder[^1] == 'j' || (builder.Length > 1 && builder[^1] == 'h' && builder[^2] is 's' or 'c'));
            builder.Append(soft ? small[1..] : small);
            return;
        }
        if (small is "a" or "i" or "u" or "e" or "o" && builder[^1] is 'a' or 'i' or 'u' or 'e' or 'o')
        {
            builder.Length--;
        }
        builder.Append(small);
    }

    private static void RepeatVowel(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] is 'a' or 'i' or 'u' or 'e' or 'o') builder.Append(builder[^1]);
    }
}

/// <summary>Whether Latin words are spelt in romaji or pinyin syllables, for lyrics nothing can read aloud.</summary>
internal static class LatinSyllables
{
    private static readonly string[] RomajiOnsets =
    [
        "shy", "sh", "ch", "ts", "ky", "gy", "sy", "zy", "ty", "dy", "ny", "hy", "by", "py", "my", "ry", "jy", "cy",
        "k", "g", "s", "z", "t", "d", "n", "h", "b", "p", "m", "y", "r", "w", "f", "j", "v",
    ];

    private static readonly string[] PinyinInitials =
        ["zh", "ch", "sh", "b", "p", "m", "f", "d", "t", "n", "l", "g", "k", "h", "j", "q", "x", "r", "z", "c", "s", "y", "w"];

    private static readonly string[] PinyinFinals =
    [
        "iang", "iong", "uang", "ang", "eng", "ing", "ong", "ian", "iao", "uai", "uan", "ai", "ei", "ao", "ou", "an", "en",
        "in", "un", "ia", "ie", "iu", "ua", "uo", "ui", "ue", "ve", "er", "a", "o", "e", "i", "u", "v",
    ];

    private static readonly string[] PinyinBare = ["ang", "eng", "ai", "ei", "ao", "ou", "an", "en", "er", "a", "o", "e"];

    /// <summary>
    /// Every word is romaji (or, when the lyric has no kana, romaji or pinyin), and the syllables number about as
    /// many as the lyric's kana and Han characters can be read in.
    /// </summary>
    public static bool LooksLikeReading(IReadOnlyList<string> words, string foreign)
    {
        int kana = 0, han = 0;
        foreach (var rune in foreign.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F) kana++;
            else if (v is 0x3005 or 0x3007 or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
                     or >= 0x20000 and <= 0x323AF) han++;
            else if (Rune.IsLetter(rune)) return false;
        }
        var letters = kana + han;
        if (letters == 0 || words.Count == 0) return false;
        var syllables = 0;
        foreach (var word in words)
        {
            var romaji = Romaji(word);
            var pinyin = kana > 0 ? -1 : Pinyin(word);
            if (romaji < 0 && pinyin < 0) return false;
            syllables += romaji < 0 ? pinyin : pinyin < 0 ? romaji : Math.Min(romaji, pinyin);
        }
        return syllables >= Math.Max(1, letters / 2) && syllables <= letters * 4 + 1;
    }

    /// <summary>The fewest morae the word divides into, or -1 when it is not romaji.</summary>
    internal static int Romaji(string word)
    {
        var best = Fill(word.Length);
        best[0] = 0;
        for (var i = 0; i < word.Length; i++)
        {
            if (best[i] < 0) continue;
            var c = word[i];
            if (IsVowel(c)) Relax(best, i + 1, best[i] + 1);
            if (c == 'n') Relax(best, i + 1, best[i] + 1);
            if (c == 'm' && i + 1 < word.Length && word[i + 1] is 'b' or 'p' or 'm') Relax(best, i + 1, best[i] + 1);
            // A doubled consonant (small tsu): "tta", "kka", "tcha".
            if (i + 1 < word.Length && "kgsztdbpcfhjmrw".Contains(c) && (word[i + 1] == c || (c == 't' && word[i + 1] == 'c')))
            {
                Relax(best, i + 1, best[i]);
            }
            foreach (var onset in RomajiOnsets)
            {
                var end = i + onset.Length;
                if (end < word.Length && string.CompareOrdinal(word, i, onset, 0, onset.Length) == 0 && IsVowel(word[end]))
                {
                    Relax(best, end + 1, best[i] + 1);
                }
            }
        }
        return best[word.Length];
    }

    /// <summary>The fewest pinyin syllables the word divides into, or -1 when it is not pinyin.</summary>
    internal static int Pinyin(string word)
    {
        var best = Fill(word.Length);
        best[0] = 0;
        for (var i = 0; i < word.Length; i++)
        {
            if (best[i] < 0) continue;
            foreach (var bare in PinyinBare)
            {
                if (Matches(word, i, bare)) Relax(best, i + bare.Length, best[i] + 1);
            }
            foreach (var initial in PinyinInitials)
            {
                if (!Matches(word, i, initial)) continue;
                foreach (var final in PinyinFinals)
                {
                    if (Matches(word, i + initial.Length, final)) Relax(best, i + initial.Length + final.Length, best[i] + 1);
                }
            }
        }
        return best[word.Length];
    }

    private static bool IsVowel(char c) => c is 'a' or 'i' or 'u' or 'e' or 'o';

    private static bool Matches(string word, int at, string part) =>
        at + part.Length <= word.Length && string.CompareOrdinal(word, at, part, 0, part.Length) == 0;

    private static int[] Fill(int length)
    {
        var values = new int[length + 1];
        Array.Fill(values, -1);
        return values;
    }

    private static void Relax(int[] best, int at, int count)
    {
        if (best[at] < 0 || count < best[at]) best[at] = count;
    }
}
