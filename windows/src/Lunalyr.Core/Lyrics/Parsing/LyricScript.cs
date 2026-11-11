// Ported from the Mac app's LyricScript.swift (Lunalyr's own code): script rules shared by the TTML, YRC, LRC and
// Lyrics+ parsers. Reading-versus-translation detection differs on Windows; see IsReading.

using System.Text;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// One timed piece of a line exactly as a source supplies it. <see cref="Glued"/> means no whitespace separated it
/// from the previous piece in the source text.
/// </summary>
public readonly record struct TimedToken(string Text, double Start, double End, bool Glued);

/// <summary>
/// Reads text aloud in Latin letters, or returns null when it cannot read all of it. The romanisation workstream can
/// supply a full reader; the built-in one covers kana, Hangul, Cyrillic and Greek (<see cref="BasicLatinReading"/>).
/// </summary>
public delegate string? LatinReader(string text);

public static class LyricScript
{
    /// <summary>
    /// Han, kana and Hangul, and the scripts written without spaces between words (Thai, Lao, Myanmar, Khmer), keep
    /// every timed syllable as its own token: joining them would lose karaoke progress or swallow a phrase.
    /// </summary>
    public static bool KeepsSyllables(string text) => KeepsSyllables(text.AsSpan());

    /// <summary><see cref="KeepsSyllables(string)"/> without allocating (per-frame layout code calls it).</summary>
    public static bool KeepsSyllables(ReadOnlySpan<char> text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (KeepsSyllables(rune.Value)) return true;
        }
        return false;
    }

    /// <summary>Whether one code point is a Hangul, kana, Han, Thai, Lao, Myanmar or Khmer syllable.</summary>
    public static bool KeepsSyllables(int v) =>
        v is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7FF // Hangul
            or >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F // Kana
            or 0x3005 or 0x3007 or 0x303B or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x323AF // Han
            or >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF; // Thai, Lao, Myanmar, Khmer

    /// <summary>Letters or digits, i.e. anything sung rather than punctuation.</summary>
    public static bool HasLetters(string text) => TextRules.HasLetterOrNumber(text);

    /// <summary>
    /// Builds display words from timed pieces. Glued syllables of spaced scripts ("beau" "ti" "ful") form one word
    /// from the first start to the last end; glued punctuation joins its neighbour; CJK pieces stay separate. The
    /// source text, not this list, carries the original spacing.
    /// </summary>
    public static List<LyricWord> Words(IReadOnlyList<TimedToken> tokens)
    {
        var pieces = new List<TimedToken>(tokens.Count);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var joinsNext = index + 1 < tokens.Count && tokens[index + 1].Glued;
            if (token.Glued || joinsNext) pieces.AddRange(SplitAtSpaces(token));
            else pieces.Add(token);
        }
        var words = new List<LyricWord>(pieces.Count);
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
                words[^1] = new LyricWord(last.Text + token.Text, last.Start, Math.Max(last.End, token.End));
            }
            else
            {
                words.Add(new LyricWord(token.Text, token.Start, token.End));
            }
        }
        return words;
    }

    /// <summary>
    /// A syllable piece that runs on into the next word ("lie" "ve you'll") is divided at its spaces, sharing its
    /// time out by length, so each part joins the right word. Punctuation set apart by a space stays attached.
    /// </summary>
    private static IEnumerable<TimedToken> SplitAtSpaces(TimedToken token)
    {
        var parts = token.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var span = token.End - token.Start;
        // A piece with no real end (an open last stamp) cannot be shared out.
        if (parts.Length <= 1 || !(span > 0) || !(span < 3600)) return [token];
        double total = parts.Sum(TextRules.CharacterCount);
        var result = new TimedToken[parts.Length];
        double cursor = token.Start;
        var consumed = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            var start = cursor;
            consumed += TextRules.CharacterCount(parts[index]);
            cursor = index == parts.Length - 1 ? token.End : token.Start + span * (consumed / total);
            result[index] = new TimedToken(parts[index], start, cursor, index == 0 ? token.Glued : !HasLetters(parts[index]));
        }
        return result;
    }

    /// <summary>Letters from any script other than Latin: kana, Hangul, Cyrillic...</summary>
    public static bool HasNonLatinLetters(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) && !IsLatinScript(rune.Value)) return true;
        }
        return false;
    }

    /// <summary>Text that has letters, all of them Latin (romaji and pinyin included).</summary>
    public static bool IsLatin(string text)
    {
        var latin = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            if (!IsLatinScript(rune.Value)) return false;
            latin = true;
        }
        return latin;
    }

    /// <summary>The Unicode Latin script's letter ranges.</summary>
    internal static bool IsLatinScript(int v) =>
        v is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or 0xAA or 0xBA or >= 0xC0 and <= 0xD6 or >= 0xD8 and <= 0xF6
            or >= 0xF8 and <= 0x2B8 or >= 0x2E0 and <= 0x2E4 or >= 0x1D00 and <= 0x1D25 or >= 0x1D2C and <= 0x1D5C
            or >= 0x1D62 and <= 0x1D65 or >= 0x1D6B and <= 0x1D77 or >= 0x1D79 and <= 0x1DBE or >= 0x1E00 and <= 0x1EFF
            or 0x2071 or 0x207F or >= 0x2090 and <= 0x209C or >= 0x212A and <= 0x212B or 0x2132 or 0x214E
            or >= 0x2160 and <= 0x2188 or >= 0x2C60 and <= 0x2C7F or >= 0xA722 and <= 0xA787 or >= 0xA78B and <= 0xA7FF
            or >= 0xAB30 and <= 0xAB64 or >= 0xFB00 and <= 0xFB06 or >= 0xFF21 and <= 0xFF3A or >= 0xFF41 and <= 0xFF5A
            or >= 0x10780 and <= 0x107BA or >= 0x1DF00 and <= 0x1DF2A;

    /// <summary>
    /// Whether a Latin-script line printed beside a non-Latin <paramref name="lyric"/> reads it aloud (romaji, pinyin,
    /// Revised Romanization, transliterated Cyrillic) rather than translating it. Numbered tones (Jyutping, pinyin
    /// with digits) always count as a reading. Otherwise the line, ignoring words both share, is compared with a
    /// reading of the lyric's non-Latin part (the Mac uses the system's transliteration; here
    /// <see cref="ReadingCache"/>'s reader). Where nothing can read the lyric (Han characters without a full reader),
    /// the line counts as a reading when every word of it is made of romaji or pinyin syllables, in a number that
    /// fits the lyric's length. Pass one <paramref name="cache"/> for all the rows of a file.
    /// </summary>
    public static bool IsReading(string candidate, string lyric, ReadingCache? cache = null)
    {
        cache ??= new ReadingCache();
        if (!HasNonLatinLetters(lyric) || !IsLatin(candidate)) return false;
        var syllables = SplitOnNonAlphanumerics(candidate);
        var numbered = syllables.Count(IsNumberedSyllable);
        if (numbered >= 2 && numbered * 2 >= syllables.Count) return true;
        var shared = new HashSet<string>(AsciiWords(lyric), StringComparer.Ordinal);
        var targetWords = AsciiWords(candidate).Where(word => !shared.Contains(word)).ToList();
        var target = string.Concat(targetWords);
        if (target.Length < 2) return false;
        var foreign = ReplaceLatinRuns(lyric);
        var readings = cache.Read(foreign);
        if (readings.Any(reading => Similarity(reading, target) >= 0.4)) return true;
        // Han may be read as either language (kanji read as pinyin by a transliterator that knows no Japanese), so a
        // line of romaji or pinyin syllables still counts there even when no reader agreed.
        var han = foreign.EnumerateRunes().Any(rune => rune.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF);
        return (readings.Count == 0 || han) && LatinSyllables.LooksLikeReading(targetWords, foreign);
    }

    /// <summary>
    /// Readings already made, kept while a file's rows are compared, so a repeated chorus costs nothing. Several
    /// readers may be given (the readings layer's Japanese tokenizer and the system transliteration: kanji may be read
    /// either way); the line is a reading when it matches any of theirs. The built-in reader when none is given.
    /// </summary>
    public sealed class ReadingCache
    {
        private readonly LatinReader[] _readers;
        private readonly Dictionary<string, List<string>> _known = new(StringComparer.Ordinal);

        public ReadingCache(LatinReader? reader = null) => _readers = [reader ?? BasicLatinReading.Read];

        public ReadingCache(IEnumerable<LatinReader>? readers)
        {
            _readers = [.. readers ?? []];
            if (_readers.Length == 0) _readers = [BasicLatinReading.Read];
        }

        /// <summary>The text's readings as plain ASCII letters, one per reader that could read it.</summary>
        internal List<string> Read(string text)
        {
            if (_known.TryGetValue(text, out var known)) return known;
            var readings = new List<string>(_readers.Length);
            foreach (var reader in _readers)
            {
                if (reader(text) is { } reading && string.Concat(AsciiWords(reading)) is { Length: > 0 } letters) readings.Add(letters);
            }
            if (_known.Count > 4096) _known.Clear();
            _known[text] = readings;
            return readings;
        }
    }

    private static List<string> SplitOnNonAlphanumerics(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) current.Append(rune.ToString());
            else if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary><c>(?i)^[a-z]+[1-6]$</c>: a toned syllable such as "ngo5".</summary>
    private static bool IsNumberedSyllable(string text)
    {
        if (text.Length < 2 || text[^1] is < '1' or > '6') return false;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (!char.IsAsciiLetter(text[i])) return false;
        }
        return true;
    }

    /// <summary>The text with each run of Latin letters replaced by a space.</summary>
    private static string ReplaceLatinRuns(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inRun = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) && IsLatinScript(rune.Value))
            {
                if (!inRun) builder.Append(' ');
                inRun = true;
                continue;
            }
            inRun = false;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>Folded to plain lower-case ASCII, cut at anything that is not an ASCII letter.</summary>
    internal static List<string> AsciiWords(string text)
    {
        var folded = TextRules.FoldCaseAndDiacritics(text);
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var c in folded)
        {
            if (char.IsAsciiLetter(c)) current.Append(c);
            else if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Dice coefficient over letter pairs: 1 for identical spellings, near 0 for unrelated text, and tolerant of
    /// differing romanisation schemes.
    /// </summary>
    private static double Similarity(string a, string b)
    {
        static Dictionary<int, int> Pairs(string letters)
        {
            var result = new Dictionary<int, int>();
            for (var index = 1; index < letters.Length; index++)
            {
                var key = (letters[index - 1] & 0xFF) << 8 | (letters[index] & 0xFF);
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
