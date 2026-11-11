using System.Text;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>Script rules shared by the parsers and the readings. Ported from the Mac's LyricScript.swift.</summary>
public static class LyricScript
{
    /// <summary>One timed piece of a line exactly as a source supplies it. <paramref name="Glued"/> means no
    /// whitespace separated it from the previous piece in the source text.</summary>
    public readonly record struct TimedToken(string Text, double Start, double End, bool Glued);

    /// <summary>Han, kana and Hangul, and the scripts written without spaces between words (Thai, Lao, Myanmar,
    /// Khmer), keep every timed syllable as its own token: joining them would lose karaoke progress or swallow a
    /// phrase.</summary>
    public static bool KeepsSyllables(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F
                    or >= 0xAC00 and <= 0xD7FF // Hangul
                or >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F // Kana
                or 0x3005 or 0x3007 or 0x303B or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
                    or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF // Han
                or >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF) // Thai, Lao, Myanmar, Khmer
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Builds display words from timed pieces. Glued syllables of spaced scripts ("beau" "ti" "ful") form
    /// one word from the first start to the last end; glued punctuation joins its neighbour; CJK pieces stay
    /// separate. The source text, not this list, carries the original spacing.</summary>
    public static List<LyricWord> Words(IReadOnlyList<TimedToken> tokens)
    {
        var pieces = new List<TimedToken>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var joinsNext = index + 1 < tokens.Count && tokens[index + 1].Glued;
            if (token.Glued || joinsNext) pieces.AddRange(SplitAtSpaces(token));
            else pieces.Add(token);
        }
        var words = new List<LyricWord>();
        foreach (var token in pieces)
        {
            if (!token.Glued || words.Count == 0)
            {
                words.Add(new LyricWord(token.Text, token.Start, token.End));
                continue;
            }
            var last = words[^1];
            if (!HasLetters(token.Text))
            {
                // Trailing punctuation has no meaningful timing of its own.
                words[^1] = last with { Text = last.Text + token.Text };
            }
            else if (!HasLetters(last.Text) || (!KeepsSyllables(last.Text) && !KeepsSyllables(token.Text)))
            {
                words[^1] = last with { Text = last.Text + token.Text, End = Math.Max(last.End, token.End) };
            }
            else
            {
                words.Add(new LyricWord(token.Text, token.Start, token.End));
            }
        }
        return words;
    }

    /// <summary>A syllable piece that runs on into the next word ("lie" "ve you'll") is divided at its spaces,
    /// sharing its time out by length, so each part joins the right word. Punctuation set apart by a space stays
    /// attached.</summary>
    private static List<TimedToken> SplitAtSpaces(TimedToken token)
    {
        var parts = ReadingText.SplitAtWhitespace(token.Text);
        var span = token.End - token.Start;
        // A piece with no real end (an open last stamp) cannot be shared out.
        if (parts.Count <= 1 || !(span > 0) || !(span < 3600)) return [token];
        double total = parts.Sum(ReadingText.GraphemeCount);
        var cursor = token.Start;
        var consumed = 0;
        var result = new List<TimedToken>(parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var start = cursor;
            consumed += ReadingText.GraphemeCount(part);
            cursor = index == parts.Count - 1 ? token.End : token.Start + span * (consumed / total);
            result.Add(new TimedToken(part, start, cursor, index == 0 ? token.Glued : !HasLetters(part)));
        }
        return result;
    }

    /// <summary>Letters or digits, i.e. anything sung rather than punctuation.</summary>
    public static bool HasLetters(string text) => ReadingText.HasLetterOrNumber(text);

    /// <summary>Letters from any script other than Latin: kana, Hangul, Cyrillic...</summary>
    public static bool HasNonLatinLetters(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsLetterCategory(rune) && !ReadingText.IsLatinScript(rune)) return true;
        }
        return false;
    }

    /// <summary>Text that has letters, all of them Latin (romaji and pinyin included).</summary>
    public static bool IsLatin(string text)
    {
        var latin = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (ReadingText.IsLatinScript(rune)) latin = true;
            else if (IsLetterCategory(rune)) return false;
        }
        return latin;
    }

    /// <summary>The regex class <c>\p{L}</c>: the letter categories only.</summary>
    internal static bool IsLetterCategory(System.Text.Rune rune) => System.Text.Rune.IsLetter(rune);

    /// <summary>Whether a Latin-script line printed beside a non-Latin <paramref name="lyric"/> reads it aloud
    /// (romaji, pinyin, Revised Romanization, transliterated Cyrillic) rather than translating it. The line is
    /// compared with the system's own transliteration of the lyric's non-Latin part, ignoring words both share;
    /// numbered tones (Jyutping, pinyin with digits) always count as a reading. Pass one <paramref name="cache"/>
    /// for all the rows of a file.</summary>
    public static bool IsReading(string candidate, string lyric, ReadingCache cache)
    {
        if (!HasNonLatinLetters(lyric) || !IsLatin(candidate)) return false;
        var syllables = SplitSyllables(candidate);
        var numbered = syllables.Count(IsNumberedSyllable);
        if (numbered >= 2 && numbered * 2 >= syllables.Count) return true;
        var shared = new HashSet<string>(AsciiWords(lyric));
        var target = string.Concat(AsciiWords(candidate).Where(word => !shared.Contains(word)));
        if (target.Length < 2) return false;
        var foreign = ReplaceLatinRuns(lyric);
        // Kana is best read by the Japanese tokenizer, everything else by the system transliteration first; kanji
        // alone may be either language.
        var kana = foreign.EnumerateRunes().Any(rune => rune.Value is >= 0x3040 and <= 0x30FF);
        var han = foreign.EnumerateRunes().Any(rune => rune.Value is >= 0x4E00 and <= 0x9FFF);
        var readers = new List<Func<string, string>> { cache.Latin };
        if (kana || han) readers.Insert(kana ? 0 : 1, cache.Japanese);
        return readers.Any(reader => Similarity(reader(foreign), target) >= 0.4);
    }

    /// <summary>Transliterations already made, and one Japanese tokenizer, kept while a file's rows are compared: a
    /// bilingual file then costs a tokenizer once instead of once per row, and a repeated chorus costs nothing.</summary>
    public sealed class ReadingCache(ReadingServices services)
    {
        private readonly Dictionary<string, string> _latin = [];
        private readonly Dictionary<string, string> _japanese = [];
        private IJapaneseTokenizer? _tokenizer;

        /// <summary>The system's own transliteration, as ASCII letters.</summary>
        internal string Latin(string text)
        {
            if (_latin.TryGetValue(text, out var known)) return known;
            var reading = string.Concat(AsciiWords(services.ToLatin(text) ?? ""));
            _latin[text] = reading;
            return reading;
        }

        /// <summary>The Japanese tokenizer's reading, so kanji are read as Japanese.</summary>
        internal string Japanese(string text)
        {
            if (_japanese.TryGetValue(text, out var known)) return known;
            _tokenizer ??= services.Tokenizer;
            var parts = new List<string>();
            foreach (var token in _tokenizer.Tokenize(text, 0, text.Length))
            {
                if (parts.Count >= 400) break;
                if (token.Latin is { } latin) parts.Add(latin);
            }
            var reading = string.Concat(AsciiWords(string.Join(" ", parts)));
            _japanese[text] = reading;
            return reading;
        }
    }

    private static List<string> SplitSyllables(string candidate)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var grapheme in ReadingText.Graphemes(candidate))
        {
            if (grapheme.IsLetterOrNumber)
            {
                current.Append(grapheme.Text);
            }
            else if (current.Length > 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary><c>(?i)^[a-z]+[1-6]$</c>.</summary>
    private static bool IsNumberedSyllable(string text)
    {
        if (text.Length < 2 || text[^1] is < '1' or > '6') return false;
        for (var index = 0; index < text.Length - 1; index++)
        {
            if (text[index] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z')) return false;
        }
        return true;
    }

    /// <summary>Runs of Latin letters replaced by a space (the regex <c>\p{Latin}+</c>).</summary>
    private static string ReplaceLatinRuns(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inRun = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (ReadingText.IsLatinScript(rune))
            {
                if (!inRun) builder.Append(' ');
                inRun = true;
            }
            else
            {
                builder.Append(rune.ToString());
                inRun = false;
            }
        }
        return builder.ToString();
    }

    internal static List<string> AsciiWords(string text)
    {
        var folded = ReadingText.FoldCaseAndDiacritics(text);
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var character in folded)
        {
            if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                current.Append(character);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// <summary>Dice coefficient over letter pairs: 1 for identical spellings, near 0 for unrelated text, and
    /// tolerant of differing romanisation schemes.</summary>
    private static double Similarity(string a, string b)
    {
        static Dictionary<int, int> Pairs(string letters)
        {
            var result = new Dictionary<int, int>();
            for (var index = 1; index < letters.Length; index++)
            {
                var key = (letters[index - 1] << 8) | letters[index];
                result[key] = result.GetValueOrDefault(key) + 1;
            }
            return result;
        }
        var left = Pairs(a);
        var right = Pairs(b);
        var total = left.Values.Sum() + right.Values.Sum();
        if (total == 0) return 0;
        var shared = left.Sum(pair => Math.Min(pair.Value, right.GetValueOrDefault(pair.Key)));
        return 2.0 * shared / total;
    }
}
