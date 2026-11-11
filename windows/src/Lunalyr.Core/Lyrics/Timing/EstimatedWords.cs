using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>Spreads a line-timed line's words over a window, estimating word timing the source did not supply.</summary>
public delegate IReadOnlyList<LyricWord> WordDistributor(string text, double start, double end);

/// <summary>
/// Estimated word timing for lyrics without any, and the sweep and line-end estimates built on it.
/// </summary>
/// <remarks>
/// <see cref="Distribute"/> is re-implemented from a description of the Mac's behaviour (its lines trace back to the
/// squashed import commit): the window (at least 0.3 s) is trimmed to <see cref="EstimatedWordShare"/> of itself, as a
/// sung line normally finishes a little before the next begins, but never below 0.42 s a word nor beyond the window;
/// each word then takes a share in proportion to its length in characters plus 1.5, so longer words hold longer, laid
/// end to end from the line's start.
/// <para>
/// Open question: the Mac splits words with the system's word breaker (ICU, dictionary-based for Chinese and
/// Japanese). <see cref="Tokenize"/> approximates it (whitespace, one token per Han/kana/Hangul character, punctuation
/// kept with the word before it). The parser workstream should supply the shared word splitter; every API here that
/// spreads words takes a <see cref="WordDistributor"/> so it can be swapped in.
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
    /// A line's words for estimated timing: whitespace-separated, with each Han, kana or Hangul character its own
    /// word, and punctuation kept with the word before it (or the first word). Empty when nothing is sung.
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new TokenList();
        if (string.IsNullOrWhiteSpace(text)) return tokens.Words;
        var run = new StringBuilder();
        foreach (var chunk in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            tokens.StartChunk();
            var enumerator = StringInfo.GetTextElementEnumerator(chunk);
            while (enumerator.MoveNext())
            {
                var element = enumerator.GetTextElement();
                if (TextElements.KeepsSyllables(element))
                {
                    tokens.Emit(run.ToString());
                    run.Clear();
                    tokens.Emit(element);
                }
                else
                {
                    run.Append(element);
                }
            }
            tokens.Emit(run.ToString());
            run.Clear();
        }
        return tokens.Words;
    }

    private sealed class TokenList
    {
        private string? _pending; // punctuation seen before the first word
        private bool _chunkHasWord;

        public List<string> Words { get; } = [];

        public void StartChunk() => _chunkHasWord = false;

        public void Emit(string piece)
        {
            if (piece.Length == 0) return;
            if (!TextElements.HasLettersOrDigits(piece))
            {
                // Punctuation stays with the word before it: joined within a chunk, after a space across chunks.
                if (Words.Count > 0) Words[^1] += (_chunkHasWord ? "" : " ") + piece;
                else _pending = _pending is null ? piece : _pending + " " + piece;
                return;
            }
            if (_pending is not null)
            {
                piece = _pending + " " + piece;
                _pending = null;
            }
            Words.Add(piece);
            _chunkHasWord = true;
        }
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
