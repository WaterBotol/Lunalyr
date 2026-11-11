namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>
/// Splits QQ Music's line-only lyrics where a line's window runs on through an instrumental break.
/// </summary>
/// <remarks>
/// Re-implemented from a description of the Mac's behaviour (its lines trace back to the squashed import commit):
/// <list type="bullet">
/// <item>LRC gives starts, not vocal ends, so the song's pace is taken from its compact lines: the median (upper
/// median) of window ÷ word count over sung, line-timed lines with two words or more and a 1–10 s window, counting only
/// paces of 0.18–1.2 s a word; 0.45 s a word when there are none.</item>
/// <item>Every line's <see cref="LyricLine.EndFromNextLine"/> is cleared: QQ's own pacing has now judged every end, so
/// the generic pause inference (<see cref="LyricGapTiming"/>) leaves these lines alone.</item>
/// <item>A word-timed line keeps its supplied word ends (a genuinely long held note included); only when the line
/// runs on more than 2 s past its last word does it end 0.3 s after that word. A line-timed line ends at
/// <see cref="EstimatedWords.EstimatedLineEnd"/> for its word count at the song's pace, with its estimated words spread
/// again over the shorter window.</item>
/// <item>A line that ends earlier is followed by an instrumental line (no text, no words) filling the rest of its old
/// window. Instrumental and wordless lines pass unchanged.</item>
/// </list>
/// It is explicitly an estimate, and the "Treat long pauses as instrumental" setting turns it off for songs with long
/// sustained vocals.
/// </remarks>
public static class QQGapRepair
{
    public static List<LyricLine> Repair(IReadOnlyList<LyricLine> lines, WordDistributor? distribute = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        distribute ??= EstimatedWords.Distribute;
        var pace = SongPace(lines);
        var repaired = new List<LyricLine>(lines.Count + 4);
        foreach (var original in lines)
        {
            var line = original with { EndFromNextLine = null };
            if (line.IsInstrumental || line.Words.Count == 0)
            {
                repaired.Add(line);
                continue;
            }
            var sungUntil = line.WordTimed ? WordTimedEnd(line) : EstimatedWords.EstimatedLineEnd(line.Start, line.End, line.Words.Count, pace);
            if (!(sungUntil < line.End && sungUntil > line.Start))
            {
                repaired.Add(line);
                continue;
            }
            repaired.Add(line.WordTimed
                ? line with { End = sungUntil }
                : line with { End = sungUntil, Words = distribute(line.Text, line.Start, sungUntil) });
            repaired.Add(new LyricLine { Start = sungUntil, End = line.End, Text = "", IsInstrumental = true });
        }
        return repaired;
    }

    // Seconds a word, from the lines whose windows look like ordinary singing.
    private static double SongPace(IReadOnlyList<LyricLine> lines)
    {
        var paces = new List<double>();
        foreach (var line in lines)
        {
            if (line.IsInstrumental || line.WordTimed || line.Words.Count < 2) continue;
            var window = line.End - line.Start;
            if (!(window >= 1 && window <= 10)) continue;
            var perWord = window / line.Words.Count;
            if (perWord is >= 0.18 and <= 1.2) paces.Add(perWord);
        }
        if (paces.Count == 0) return 0.45;
        paces.Sort();
        return paces[paces.Count / 2];
    }

    // A supplied word end is authoritative; the line is only cut when it runs on well past its last word.
    private static double WordTimedEnd(LyricLine line)
    {
        var lastWord = double.NegativeInfinity;
        foreach (var word in line.Words) lastWord = Math.Max(lastWord, word.End);
        return line.End - lastWord > 2 ? Math.Min(line.End, lastWord + 0.3) : line.End;
    }
}
