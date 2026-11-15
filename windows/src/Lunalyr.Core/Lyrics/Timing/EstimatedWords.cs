using System.Globalization;
using System.Text;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>Spreads a line-timed line's words over a window, estimating word timing the source did not supply.</summary>
public delegate IReadOnlyList<LyricWord> WordDistributor(string text, double start, double end);

/// <summary>
/// Estimated word timing for lyrics without any, and the sweep and line-end estimates built on it. The one place the
/// parsers, the timing code and the readings spread a line's words over its window.
/// </summary>
/// <remarks>
/// <see cref="Spread"/> is re-implemented from a description of the Mac's behaviour (its <c>distributeWords</c> lines
/// trace back to the squashed import commit): the window (at least 0.3 s) is trimmed to <see cref="EstimatedWordShare"/>
/// of itself, as a sung line normally finishes a little before the next begins, but never below 0.42 s a word nor
/// beyond the window; each word then takes a share in proportion to its length in characters plus 1.5, so longer words
/// hold longer, laid end to end from the line's start.
/// <para>
/// The Mac splits words with the system's word breaker (ICU, dictionary-based for Chinese and Japanese). Where a word
/// breaker is supplied (the readings' <see cref="IWordBreaker"/>, ICU on Windows) its word starts are used
/// (<see cref="Distribute(string, double, double, IWordBreaker)"/>, <see cref="Distributor"/>); without one,
/// <see cref="Tokenize"/> approximates it.
/// </para>
/// </remarks>
public static class EstimatedWords
{
    /// <summary>The share of a line's window that its estimated words take.</summary>
    public const double EstimatedWordShare = 0.88;

    /// <summary>The quickest pace the estimate allows, seconds a word.</summary>
    public const double MinimumSecondsPerWord = 0.42;

    /// <summary>The default distributor: <see cref="Tokenize"/>, then <see cref="Spread"/>.</summary>
    public static IReadOnlyList<LyricWord> Distribute(string text, double start, double end)
    {
        var tokens = Tokenize(text);
        return tokens.Count == 0 ? [] : Spread(tokens, start, end);
    }

    /// <summary>Words cut at <paramref name="breaker"/>'s word starts, then <see cref="Spread"/>.</summary>
    public static List<LyricWord> Distribute(string text, double start, double end, IWordBreaker breaker)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(breaker);
        var pieces = Pieces(text, [.. breaker.Words(text).Select(word => word.Start)]);
        return pieces.Count == 0 ? [] : [.. Spread(pieces, start, end)];
    }

    /// <summary>A distributor over <paramref name="breaker"/>, for the parsers and the timing code.</summary>
    public static WordDistributor Distributor(IWordBreaker breaker)
    {
        ArgumentNullException.ThrowIfNull(breaker);
        return (text, start, end) => Distribute(text, start, end, breaker);
    }

    /// <summary>Lays <paramref name="tokens"/> over the window from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public static LyricWord[] Spread(IReadOnlyList<string> tokens, double start, double end)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0) return [];
        var window = Math.Max(end - start, 0.3);
        var usable = Math.Min(window, Math.Max(tokens.Count * MinimumSecondsPerWord, window * EstimatedWordShare));
        double totalWeight = 0;
        foreach (var token in tokens) totalWeight += Weight(token);
        var words = new LyricWord[tokens.Count];
        var cursor = start;
        for (var i = 0; i < words.Length; i++)
        {
            var slice = usable * (Weight(tokens[i]) / totalWeight);
            words[i] = new LyricWord(tokens[i], cursor, cursor + slice);
            cursor += slice;
        }
        return words;
    }

    private static double Weight(string token) => TextElements.Count(token) + 1.5;

    /// <summary>
    /// The words of a line as a reader sees them, without a word breaker: runs of letters and digits (an apostrophe or
    /// a separator inside one, "can't", "1,000", keeps it whole), each Han, kana or Thai character on its own (Hangul
    /// is written with spaces, so its words stay whole), and punctuation kept with the word before it (or the first
    /// word). Empty when nothing is sung.
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var characters = Characters(text);
        var starts = new List<int>();
        var inWord = false;
        for (var index = 0; index < characters.Count; index++)
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
            var mid = inWord && index + 1 < characters.Count && Kind(characters[index + 1]) == CharKind.Word
                && characters[index] is "'" or "’" or "." or ",";
            if (!mid) inWord = false;
        }
        var tokens = new List<string>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var from = i == 0 ? 0 : starts[i];
            var to = i + 1 < starts.Count ? starts[i + 1] : characters.Count;
            var token = string.Concat(characters.Skip(from).Take(to - from)).Trim();
            if (token.Length > 0) tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>The text cut at each word start (UTF-16 offsets), trimmed, empty pieces dropped; anything before the first word goes with it.</summary>
    private static List<string> Pieces(string text, List<int> starts)
    {
        var pieces = new List<string>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var from = index == 0 ? 0 : starts[index];
            var to = index + 1 < starts.Count ? starts[index + 1] : text.Length;
            var piece = text[from..to].Trim();
            if (piece.Length > 0) pieces.Add(piece);
        }
        return pieces;
    }

    private static List<string> Characters(string text)
    {
        var result = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) result.Add(enumerator.GetTextElement());
        return result;
    }

    private enum CharKind { Other, Word, Syllable }

    private static CharKind Kind(string character)
    {
        var rune = Rune.GetRuneAt(character, 0);
        if (!(Rune.IsLetter(rune) || Rune.IsNumber(rune))) return CharKind.Other;
        var v = rune.Value;
        var hangul = v is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7FF;
        return !hangul && LyricScript.KeepsSyllables(v) ? CharKind.Syllable : CharKind.Word;
    }

    /// <summary>
    /// Where the line-level sweep of a lyric without word timing ends: the line's end, unless its estimated words were
    /// spread over less than the window (the last line running on through the outro, or a line before a long break),
    /// in which case the sweep ends with them. Never later than the line's end.
    /// </summary>
    public static double EstimatedSweepEnd(LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.WordTimed || line.Words.Count == 0) return line.End;
        var sung = double.NegativeInfinity;
        for (var i = 0; i < line.Words.Count; i++) sung = Math.Max(sung, line.Words[i].End);
        if (!(sung > line.Start)) return line.End;
        return Math.Min(line.End, line.Start + (sung - line.Start) / EstimatedWordShare);
    }

    /// <summary>
    /// A conservative end for a line-only lyric that spans a long instrumental: where its singing most likely stopped,
    /// or <paramref name="nextStart"/> when the window is not clearly longer than singing it would take.
    /// </summary>
    /// <remarks>
    /// Re-implemented from a description of the Mac's behaviour: the sung span is the word count times the seconds a
    /// word (held to 0.18–1.2 s, 0.45 s when not finite) times 1.35, plus 1.25 s, held to 4–22 s. Only a window longer
    /// than both 12 s and the span plus 6 s is cut, to start + span. Not-finite times, a window that is not positive,
    /// or a word count outside 1–10000 leave the end at <paramref name="nextStart"/>.
    /// </remarks>
    public static double EstimatedLineEnd(double start, double nextStart, int wordCount, double secondsPerWord)
    {
        if (!double.IsFinite(start) || !double.IsFinite(nextStart) || nextStart <= start || wordCount is < 1 or > 10000)
            return nextStart;
        var perWord = double.IsFinite(secondsPerWord) ? Math.Clamp(secondsPerWord, 0.18, 1.2) : 0.45;
        var sung = Math.Clamp(wordCount * perWord * 1.35 + 1.25, 4, 22);
        return nextStart - start > Math.Max(12, sung + 6) ? start + sung : nextStart;
    }
}
