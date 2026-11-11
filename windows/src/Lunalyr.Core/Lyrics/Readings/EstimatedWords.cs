namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// Words with estimated times for text that has none: a line timed only as a line, or a reading set in a line's
/// place. Written for Windows from a description of the Mac's behaviour (its <c>makeLine</c> and
/// <c>distributeWords</c> carry Verso-attributed lines, so they were not translated):
/// <list type="bullet">
/// <item>The text is cut into words at the word breaker's word starts; each word keeps the punctuation that follows
/// it, and anything before the first word goes with the first word. Words are trimmed of whitespace.</item>
/// <item>Each word's share of the time is its length in characters plus 1.5, so long words hold longer.</item>
/// <item>The words fill the window from its start, but not all of it: a sung line usually ends a little before the
/// next one starts, so they take 88% of it, or 0.42 s per word if that is more, never more than the window. A window
/// shorter than 0.3 s counts as 0.3 s.</item>
/// </list>
/// The lyric parsing workstream may port the same behaviour for its parsers; the two should be merged into one.
/// </summary>
public static class EstimatedWords
{
    /// <summary>The share of a line's window its estimated words take.</summary>
    public const double WordShare = 0.88;

    private const double SecondsPerWord = 0.42;
    private const double ShortestWindow = 0.3;
    private const double ExtraWeight = 1.5;

    /// <summary>Words for <paramref name="text"/> spread over <paramref name="start"/>..<paramref name="end"/>.</summary>
    public static List<LyricWord> Distribute(string text, double start, double end, IWordBreaker breaker)
    {
        var pieces = Pieces(text, breaker);
        var result = new List<LyricWord>(pieces.Count);
        if (pieces.Count == 0) return result;
        var window = Math.Max(end - start, ShortestWindow);
        var filled = Math.Min(window, Math.Max(pieces.Count * SecondsPerWord, window * WordShare));
        var weights = pieces.Select(piece => ReadingText.GraphemeCount(piece) + ExtraWeight).ToList();
        var total = weights.Sum();
        var cursor = start;
        for (var index = 0; index < pieces.Count; index++)
        {
            var length = filled * (weights[index] / total);
            result.Add(new LyricWord(pieces[index], cursor, cursor + length));
            cursor += length;
        }
        return result;
    }

    /// <summary>The text cut at each word's start, trimmed, empty pieces dropped.</summary>
    private static List<string> Pieces(string text, IWordBreaker breaker)
    {
        var starts = breaker.Words(text).Select(word => word.Start).ToList();
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

    /// <summary>Empty text, or only note glyphs and dashes: an instrumental break, not a lyric.</summary>
    public static bool IsInstrumentalMark(string text) =>
        text.All(character => "♪♫•·-–—".Contains(character) || char.IsWhiteSpace(character));

    /// <summary>A line timed only by its start and end, with estimated words; a lone note glyph makes an instrumental
    /// break with no text.</summary>
    public static LyricLine LineTimed(string text, double start, double end, IWordBreaker breaker)
    {
        var cleaned = text.Trim(' ', '\t');
        var instrumental = IsInstrumentalMark(cleaned);
        return new LyricLine
        {
            Start = start,
            End = end,
            Text = instrumental ? "" : cleaned,
            Words = instrumental ? [] : Distribute(cleaned, start, end, breaker),
            IsInstrumental = instrumental,
        };
    }

    /// <summary>Where the line-level sweep of a lyric without word timing ends: the line's end, unless its estimated
    /// words were spread over less than the window (the last line running on through the outro, or a line before a
    /// long break), in which case the sweep ends with them. Never later than the line's end.</summary>
    public static double SweepEnd(LyricLine line)
    {
        if (line.WordTimed || line.Words.Count == 0) return line.End;
        var sung = line.Words.Max(word => word.End);
        if (!(sung > line.Start)) return line.End;
        return Math.Min(line.End, line.Start + (sung - line.Start) / WordShare);
    }
}
