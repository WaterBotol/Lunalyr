// Line construction, estimated word timing and the timing labels shared by every parser.
//
// Provenance: the Mac's `makeLine`, `distributeWords` and part of `precision` (LyricsFetcher.swift) still trace back
// to the Verso import, so they are re-implemented here from this description, not translated:
//   * A line's text is trimmed of spaces; empty text or text made only of note glyphs and dashes ("♪", "-") is an
//     instrumental break with no text and no words.
//   * Without word timing, a line's window is shared out across its words in proportion to each word's length in
//     characters plus 1.5. The words take max(window, 0.3 s) trimmed to the longer of 0.42 s per word and 88 % of the
//     window, never more than the window, starting at the line's start and following each other without gaps.
//   * Precision: "Word-by-word" when every sung line has valid source word timing, "Mixed timing" when some do,
//     else "Line-synced". `ValidWordTiming` and `EstimatedWordShare` are Lunalyr's own and are ported as written.

using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Parsing;

public static class LyricLines
{
    public const string WordByWord = "Word-by-word";
    public const string MixedTiming = "Mixed timing";
    public const string LineSynced = "Line-synced";
    public const string NotSynced = "Not synced";

    /// <summary>The share of a line's window that its estimated words take: a sung line normally finishes a little before the next begins.</summary>
    public const double EstimatedWordShare = 0.88;

    /// <summary>Empty text, or only note glyphs and dashes: an instrumental break, not a lyric.</summary>
    public static bool IsInstrumentalMark(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && "♪♫•·-–—".IndexOf(c) < 0) return false;
        }
        return true;
    }

    /// <summary>A line timed only by its window: estimated words, or an instrumental break.</summary>
    public static LyricLine MakeLine(string text, double start, double end)
    {
        var cleaned = TextRules.TrimSpaces(text);
        var instrumental = IsInstrumentalMark(cleaned);
        return new LyricLine
        {
            Start = start,
            End = end,
            Text = instrumental ? "" : cleaned,
            Words = instrumental ? [] : DistributeWords(cleaned, start, end),
            IsInstrumental = instrumental,
        };
    }

    /// <summary>
    /// Estimated word timing across a window (see the file header). A caller may pass a window shorter than the
    /// line's own; the line's times stay the caller's.
    /// </summary>
    public static IReadOnlyList<LyricWord> DistributeWords(string text, double start, double end)
    {
        var tokens = WordTokens(text);
        if (tokens.Count == 0) return [];
        var window = Math.Max(end - start, 0.3);
        var usable = Math.Min(window, Math.Max(tokens.Count * 0.42, window * EstimatedWordShare));
        var weights = tokens.Select(token => TextRules.CharacterCount(token) + 1.5).ToArray();
        var totalWeight = weights.Sum();
        var words = new LyricWord[tokens.Count];
        var cursor = start;
        for (var index = 0; index < tokens.Count; index++)
        {
            var share = usable * (weights[index] / totalWeight);
            words[index] = new LyricWord(tokens[index], cursor, cursor + share);
            cursor += share;
        }
        return words;
    }

    /// <summary>
    /// The words of a line as a reader sees them, punctuation attached to the word before it. Each character of a
    /// script written without spaces (Han, kana, Thai...) counts as a word: the Mac's dictionary segmentation is not
    /// available, and estimated timing only needs a fair share per sound.
    /// </summary>
    internal static List<string> WordTokens(string text)
    {
        var characters = TextRules.Characters(text);
        var starts = new List<int>();
        var inWord = false;
        for (var index = 0; index < characters.Length; index++)
        {
            var kind = Kind(characters[index]);
            if (kind == CharKind.Syllable)
            {
                starts.Add(index);
                inWord = false;
                continue;
            }
            if (kind == CharKind.Word)
            {
                if (!inWord) starts.Add(index);
                inWord = true;
                continue;
            }
            // Apostrophes inside a word ("can't") and separators inside a number ("1,000") keep it whole.
            var mid = inWord && index + 1 < characters.Length && Kind(characters[index + 1]) == CharKind.Word
                && characters[index] is "'" or "’" or "." or ",";
            if (!mid) inWord = false;
        }
        var tokens = new List<string>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var from = i == 0 ? 0 : starts[i];
            var to = i + 1 < starts.Count ? starts[i + 1] : characters.Length;
            var token = string.Concat(characters[from..to]).Trim();
            if (token.Length > 0) tokens.Add(token);
        }
        return tokens;
    }

    private enum CharKind { Other, Word, Syllable }

    private static CharKind Kind(string character)
    {
        var rune = Rune.GetRuneAt(character, 0);
        if (!(Rune.IsLetter(rune) || Rune.IsNumber(rune))) return CharKind.Other;
        var v = rune.Value;
        var hangul = v is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7FF;
        return !hangul && LyricScript.KeepsSyllables(character) ? CharKind.Syllable : CharKind.Word;
    }

    /// <summary>Source word timing that covers the line's letters, in order and inside the line.</summary>
    public static bool ValidWordTiming(LyricLine line)
    {
        if (!line.WordTimed || line.Words.Count == 0 || !double.IsFinite(line.Start) || !double.IsFinite(line.End)
            || !(line.End > line.Start)) return false;
        var covered = TextRules.LettersAndNumbers(string.Concat(line.Words.Select(word => word.Text)));
        if (covered != TextRules.LettersAndNumbers(line.Text)) return false;
        for (var i = 0; i < line.Words.Count; i++)
        {
            var word = line.Words[i];
            if (!double.IsFinite(word.Start) || !double.IsFinite(word.End) || !(word.Start >= line.Start - 0.05)
                || !(word.End > word.Start) || !(word.End <= line.End + 0.05)) return false;
            if (i > 0 && !(word.Start >= line.Words[i - 1].Start)) return false;
        }
        return true;
    }

    /// <summary>Every sung line has valid source word timing.</summary>
    public static bool HasWordTiming(IReadOnlyList<LyricLine> lines)
    {
        var any = false;
        foreach (var line in lines)
        {
            if (line.IsInstrumental) continue;
            if (!ValidWordTiming(line)) return false;
            any = true;
        }
        return any;
    }

    public static bool HasWordTiming(LyricResult result) => result is LyricResult.Synced synced && HasWordTiming(synced.Lines);

    /// <summary>"Word-by-word", "Mixed timing" or "Line-synced", for the provenance label.</summary>
    public static string Precision(IReadOnlyList<LyricLine> lines)
    {
        int vocal = 0, valid = 0;
        foreach (var line in lines)
        {
            if (line.IsInstrumental) continue;
            vocal++;
            if (ValidWordTiming(line)) valid++;
        }
        if (vocal > 0 && valid == vocal) return WordByWord;
        return valid > 0 ? MixedTiming : LineSynced;
    }

    internal static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
