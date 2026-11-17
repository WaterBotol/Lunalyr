// Line continuity, ported from the Mac's LyricPhraseRuns.swift (ddb1a3c5, e6959bca; Lunalyr's own code): lines sung
// close together form a run, so a layout's stage motion can carry on from one line into the next instead of starting
// again at every line. This is the model the carrying layouts (Torque, Hinge, Portal, Echo) build on; it is worked out
// from the lyrics alone, so a seek lands on exactly the picture playing through would have shown.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Motion;

/// <summary>
/// Lines grouped into runs. Two neighbouring lines share a run when the next one starts singing less than
/// <see cref="BreakGap"/> after the singing so far has stopped. An instrumental line, a line with nothing to sing, a
/// switch between word timing and line timing, or a long pause starts a new run.
/// </summary>
public sealed class LyricPhraseRuns : IEquatable<LyricPhraseRuns>
{
    /// <summary>
    /// The shortest pause, in seconds, that breaks a run: a breath between lines, a held beat or a short fill is under it.
    /// It is the pause that brings up the instrumental-break cue, which dims every line for its dots, so a hand-over
    /// across a longer pause would play half-hidden behind them (the Mac's e6959bca).
    /// </summary>
    public const double BreakGap = 2.4;

    /// <summary>Where one line hands the stage on to the next inside a run.</summary>
    /// <param name="LastOnset">The onset of the last word sung on the earlier line.</param>
    /// <param name="NextOnset">The onset of the first word sung on the later line.</param>
    public readonly record struct Boundary(double LastOnset, double NextOnset);

    /// <summary>One line's place in its run.</summary>
    /// <param name="Run">The run's number in the song, from 0.</param>
    /// <param name="Ordinal">The line's place in its run, from 0.</param>
    /// <param name="RunStart">When the run's first word is sung.</param>
    /// <param name="WordsBefore">The sung words on the run's earlier lines.</param>
    /// <param name="Incoming">The hand-over from the line before, when it is in the same run.</param>
    /// <param name="Outgoing">The hand-over to the line after, when it is in the same run.</param>
    public readonly record struct Line(int Run, int Ordinal, double RunStart, int WordsBefore, Boundary? Incoming = null, Boundary? Outgoing = null)
    {
        /// <summary>A steady seed for the whole run: the same for every line in it, different for the next run.</summary>
        public int Seed => unchecked((int)((LyricPhraseTiming.TimeSeed(RunStart) * 0x9E3779B97F4A7C15UL) >> 1));
    }

    private readonly Line?[] _lines;

    /// <summary>One entry per line; null for a line that is in no run (nothing sung).</summary>
    public IReadOnlyList<Line?> Lines => _lines;

    public LyricPhraseRuns(IReadOnlyList<LyricLine> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _lines = new Line?[source.Count];
        var run = -1;
        Line? current = null;
        var sungUntil = double.NegativeInfinity;
        int? previousRow = null;
        for (var row = 0; row < source.Count; row++)
        {
            var line = source[row];
            if (FirstOnset(line) is not { } onset || line.IsInstrumental)
            {
                current = null;
                previousRow = null;
                continue;
            }
            if (previousRow is { } before && current is { } last && Continues(source[before], line, sungUntil, onset))
            {
                // Inside the run: carried across unless the earlier line is still being sung over this one (a duet part
                // or an ad-lib keeps its own exit).
                var carried = !LyricPhraseTiming.IsHeld(before, source) && before + 1 == row;
                Boundary? boundary = carried ? new Boundary(LastOnset(source[before]), onset) : null;
                if (carried && _lines[before] is { } earlier) _lines[before] = earlier with { Outgoing = boundary };
                current = last with
                {
                    WordsBefore = last.WordsBefore + SungWords(source[before]),
                    Ordinal = last.Ordinal + 1,
                    Incoming = boundary,
                    Outgoing = null,
                };
                sungUntil = Math.Max(sungUntil, SungEnd(line));
            }
            else
            {
                run++;
                current = new Line(run, 0, onset, 0);
                sungUntil = SungEnd(line);
            }
            _lines[row] = current;
            previousRow = row;
        }
    }

    /// <summary>The run record for <paramref name="row"/>, if it is in one.</summary>
    public Line? this[int row] => row >= 0 && row < _lines.Length ? _lines[row] : null;

    /// <summary>Whether <paramref name="next"/> carries on the run that <paramref name="previous"/> is in.</summary>
    public static bool Continues(LyricLine previous, LyricLine next, double sungUntil, double nextOnset) =>
        previous.WordTimed == next.WordTimed && nextOnset - sungUntil < BreakGap;

    /// <summary>When the first word is sung (<see cref="LyricPhraseTiming.FirstWordOnset"/>).</summary>
    public static double? FirstOnset(LyricLine line) => LyricPhraseTiming.FirstWordOnset(line);

    /// <summary>The onset of the last word sung on the line (its start, line-timed).</summary>
    public static double LastOnset(LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.WordTimed) return line.Start;
        for (var i = line.Words.Count - 1; i >= 0; i--)
            if (LyricRuns.IsVisible(line.Words[i].Text)) return line.Words[i].Start;
        return line.Start;
    }

    /// <summary>
    /// When the singing on a line stops: its last word's end when word-timed; for a line-timed line, the end of the time
    /// its estimated words were given, never a line end that only marks where the next line starts.
    /// </summary>
    public static double SungEnd(LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.WordTimed) return line.Words.Count == 0 ? line.End : line.Words.Max(word => word.End);
        return EstimatedWords.EstimatedSweepEnd(line);
    }

    public static int SungWords(LyricLine line) => line.Words.Count(word => LyricRuns.IsVisible(word.Text));

    public bool Equals(LyricPhraseRuns? other) => other is not null && _lines.SequenceEqual(other._lines);

    public override bool Equals(object? obj) => Equals(obj as LyricPhraseRuns);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var line in _lines) hash.Add(line);
        return hash.ToHashCode();
    }
}

/// <summary>
/// What one line's stage carries over from the line before it and on into the line after it, inside a run (the Mac's
/// <c>StageCarry</c>). Null (the first line after a break, a preview, Reduce Motion) draws the line as it always was:
/// arriving from rest and leaving on its own.
/// </summary>
/// <param name="Seed">The run's steady seed.</param>
/// <param name="WordsBefore">The words sung on the run's earlier lines.</param>
/// <param name="Incoming">The hand-over from the line before.</param>
/// <param name="Outgoing">The hand-over to the line after.</param>
public sealed record StageCarry(int Seed, int WordsBefore, StageCarry.Hand? Incoming, StageCarry.Hand? Outgoing)
{
    /// <summary>A hand-over and when the later line is first drawn (its line start).</summary>
    public readonly record struct Hand(LyricPhraseRuns.Boundary Boundary, double LaterStart);

    /// <summary>The carry for <paramref name="row"/>, given which neighbours share its layout.</summary>
    public static StageCarry? Make(int row, LyricPhraseRuns runs, IReadOnlyList<LyricLine> lines, Func<int, bool> sameLayout)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sameLayout);
        if (runs[row] is not { } record) return null;
        Hand? incoming = record.Incoming is { } inBoundary && sameLayout(row - 1) ? new Hand(inBoundary, lines[row].Start) : null;
        Hand? outgoing = record.Outgoing is { } outBoundary && row + 1 < lines.Count && sameLayout(row + 1)
            ? new Hand(outBoundary, lines[row + 1].Start) : null;
        return new StageCarry(record.Seed, record.WordsBefore, incoming, outgoing);
    }
}
