using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Motion;

/// <summary>
/// A line's entrance and exit. Line-only lyrics animate as one phrase from these; word-timed lines take their entrance
/// from here and hand the exit to their words, which leave one by one from <see cref="ExitStart"/>. A pure function of
/// playback time; a struct, computed per frame without allocating.
/// </summary>
public readonly record struct LyricPhraseTiming
{
    /// <summary>The exit's settle, seconds.</summary>
    public const double ExitDuration = 0.46;

    /// <summary>From <see cref="ExitStart"/> until the last, most staggered word has gone.</summary>
    public const double ExitSpan = 0.70;

    /// <summary>Hinge folds its words shut one after another over most of a second, longer than the other exits.</summary>
    public const double HingeExitSpan = 1.0;

    /// <summary>The longest a line still being sung keeps the stage once the next line has started.</summary>
    public const double LongestHold = 4.0;

    /// <param name="line">The line.</param>
    /// <param name="nextStart">When it hands over (<see cref="HandOver"/>).</param>
    /// <param name="time">Lyric time.</param>
    public LyricPhraseTiming(LyricLine line, double nextStart, double time)
    {
        ArgumentNullException.ThrowIfNull(line);
        var duration = Math.Max(0, Math.Min(line.End, nextStart) - line.Start);
        var attack = line.WordTimed ? 0.22 : Math.Min(0.32, Math.Max(0.12, duration * 0.18));
        Progress = LyricMotion.Settle(time - line.Start, attack);
        var seed = TimeSeed(line.Start) ^ 0x51ED_C0DEUL;
        Arrival = LyricMotion.SpringStep(time - line.Start,
            new Spring(3.36 / Math.Max(0.14, attack * 1.6), 0.82 + 0.06 * LyricMotion.Jitter(seed, 1)));
        ExitStart = ExitStartOf(line, nextStart);
        Exit = LyricMotion.Settle(time - ExitStart, ExitDuration);
    }

    /// <summary>Monotone 0…1 entrance, for opacity.</summary>
    public double Progress { get; }

    /// <summary>The same entrance as a spring, for movement: it can overshoot a touch and settle.</summary>
    public double Arrival { get; }

    /// <summary>Monotone 0…1 exit of the phrase as a whole.</summary>
    public double Exit { get; }

    /// <summary>When the line starts to leave.</summary>
    public double ExitStart { get; }

    public double Presence => Progress * (1 - Exit);

    /// <summary>
    /// How far through a line-only lyric the sweep is: across the line, or, for the last line or one before a long
    /// break, only across the time its estimated words were given (<see cref="EstimatedWords.EstimatedSweepEnd"/>), not
    /// a window that runs on through a break or the end of the song.
    /// </summary>
    public static double Sweep(LyricLine line, double time)
    {
        ArgumentNullException.ThrowIfNull(line);
        var end = line.WordTimed ? line.End : EstimatedWords.EstimatedSweepEnd(line);
        return Math.Min(1, Math.Max(0, (time - line.Start) / Math.Max(0.001, end - line.Start)));
    }

    /// <summary>
    /// A seed from a time, by the millisecond. Never fails: a time too large for 64 bits (a malformed source's) or not
    /// a number gives 0 instead.
    /// </summary>
    public static ulong TimeSeed(double seconds)
    {
        var milliseconds = Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(milliseconds) || milliseconds < -9.223372036854775808E18 || milliseconds >= 9.223372036854775808E18)
            return 0;
        return unchecked((ulong)(long)milliseconds);
    }

    /// <summary>
    /// A line starts to leave a quarter second before the next one, so the two are not both mostly visible in the
    /// middle of the stage, but never before its own last word has been sung.
    /// </summary>
    public static double ExitStartOf(LyricLine line, double nextStart)
    {
        ArgumentNullException.ThrowIfNull(line);
        var lastWordEnd = double.NegativeInfinity;
        for (var i = 0; i < line.Words.Count; i++) lastWordEnd = Math.Max(lastWordEnd, line.Words[i].End);
        return Math.Max(lastWordEnd + 0.10, Math.Min(line.End + 0.12, nextStart - 0.25));
    }

    /// <summary>
    /// When line <paramref name="row"/> hands over to the next: as the next line starts, unless it is a duet part or an
    /// ad-lib whose words are still being sung then (TTML, Lyrics+ and YRC keep overlapping lines), which stays until
    /// its own last word ends, at most <see cref="LongestHold"/> later. Line-only lyrics, which never overlap, hand over
    /// at the next line's start. The last line hands over 0.8 s after its end.
    /// </summary>
    public static double HandOver(int row, IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (row < 0 || row >= lines.Count) return double.PositiveInfinity;
        if (row + 1 >= lines.Count) return lines[row].End + 0.8;
        var next = lines[row + 1].Start;
        return LyricLineIndex.HeldEnd(row, lines) is { } end ? Math.Min(end, next + LongestHold) : next;
    }

    /// <summary>Whether line <paramref name="row"/> is still on the stage, held, after the next line has started.</summary>
    public static bool IsHeld(int row, IReadOnlyList<LyricLine> lines) =>
        row >= 0 && row + 1 < lines.Count && HandOver(row, lines) > lines[row + 1].Start;

    /// <summary>Whether an earlier line still has words on their way out.</summary>
    public static bool StillLeaving(LyricLine line, double nextStart, double time, double span = ExitSpan) =>
        time < ExitStartOf(line, nextStart) + span;

    /// <summary>
    /// The onset of a line's first sung word (one with a letter or digit): its own start when word-timed, the line's
    /// start otherwise; null for an instrumental line or one with no such word.
    /// </summary>
    public static double? FirstWordOnset(LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.IsInstrumental) return null;
        for (var i = 0; i < line.Words.Count; i++)
        {
            if (TextElements.HasLettersOrDigits(line.Words[i].Text)) return line.WordTimed ? line.Words[i].Start : line.Start;
        }
        return null;
    }

    /// <summary>
    /// Keeps rolling words inside their contiguous run of real word timing. Looks back far enough that words still
    /// fading out (up to about four ranks) are never cut when the active line changes.
    /// </summary>
    public static (int First, int Last) RollingRange(int index, IReadOnlyList<LyricLine> lines)
    {
        int first = index, last = index;
        while (first > Math.Max(0, index - 4) && lines[first - 1].WordTimed) first--;
        if (last + 1 < lines.Count && lines[last + 1].WordTimed) last++;
        return (first, last);
    }

    /// <summary>
    /// How long the stage layouts keep a line once its words are sung: its last word's end (no more than 8 s past the
    /// last onset, and at least 1.5 s after the line starts), plus <see cref="WallpaperMotion.HoldAfterSung"/>.
    /// </summary>
    public static double StageHeldUntil(LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        double sung = line.End, lastOnset = line.Start;
        if (line.Words.Count > 0)
        {
            (sung, lastOnset) = (double.NegativeInfinity, double.NegativeInfinity);
            foreach (var word in line.Words)
            {
                sung = Math.Max(sung, word.End);
                lastOnset = Math.Max(lastOnset, word.Start);
            }
        }
        var sungEnd = Math.Min(sung, lastOnset + 8);
        return Math.Max(line.Start + 1.5, sungEnd) + WallpaperMotion.HoldAfterSung;
    }
}
