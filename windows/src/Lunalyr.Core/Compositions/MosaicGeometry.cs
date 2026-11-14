// Mosaic's choreography (the Mac's MosaicLayout.swift, Build 88: mosaicBeats, mosaicMoves, mosaicRanks, mosaicAppear,
// mosaicCentre, mosaicLetterReveal, mosaicHome and their constants; Lunalyr's own code, no line from the Verso import,
// ported as it is). Pure functions of the line and the time; the drawing is MosaicLayer's.
//
// A collage: each word is set a different way as it arrives -- huge, near the stage's width; white on a colour bar; or
// letter by letter, each letter rising onto one baseline -- and then shrinks into a small, flush-left stack above, read
// top to bottom, the oldest words fading out at its top. Nothing else is drawn: no ornaments, no panels.
//
// A quick run of words (each under QuickGap after the one before) is one "beat": set together in one treatment, each
// word appearing in its place as it is sung, so a fast line never stacks big words on top of each other. A beat leaves
// for the stack just before the next one arrives (the times are known), so the two never share the spot.

using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>The three ways Mosaic sets a beat.</summary>
public enum MosaicKind
{
    /// <summary>Huge, nearly the stage's width.</summary>
    Huge,
    /// <summary>White on a colour bar (a single word only, printed with its bar).</summary>
    Bar,
    /// <summary>Letter by letter, each rising onto one baseline.</summary>
    Letters,
}

/// <summary>Runs <see cref="From"/> up to (not including) <see cref="To"/>, set together in one treatment.</summary>
public readonly record struct MosaicBeat(int From, int To, MosaicKind Kind)
{
    public int Count => To - From;
    public bool Contains(int run) => run >= From && run < To;
}

public static class MosaicGeometry
{
    /// <summary>Words closer together than this (start to start) share a beat.</summary>
    public const double QuickGap = 0.28;
    /// <summary>A beat holds at most this many words and characters (spaces included).</summary>
    public const int BeatRuns = 3;
    public const int BeatCharacters = 16;
    /// <summary>How far ahead of its onset a word starts to appear, so it is fully in by the time it is sung.</summary>
    public const double Lead = 0.12;
    /// <summary>The colour bar a little less saturated than the ink's tile colour: on a dark stage a full-chroma bar shouts.</summary>
    public const double AccentSaturation = 0.72;
    /// <summary>The bar's reach past its word, in sizes.</summary>
    public const double BarPaddingX = 0.28, BarPaddingY = 0.06;

    /// <summary>The size of a treatment, in units.</summary>
    public static double BigSize(MosaicKind kind) => kind switch
    {
        MosaicKind.Huge => 3.0,
        MosaicKind.Bar => 2.3,
        _ => 2.4,
    };

    /// <summary>The text joining two runs of a beat: a space, or nothing for a script written without spaces.</summary>
    public static string Joiner(StageRun next) => LyricScript.KeepsSyllables(next.Text) ? "" : " ";

    /// <summary>
    /// The beats of a line and how each is set. Runs in <paramref name="alone"/> (a reading under them, or a tile behind
    /// them) always make a beat of their own. The kinds vary by importance and length, never the same twice in a row:
    /// the beat with the longest-held word is huge; a short single word (up to five letters) takes the bar, a long one
    /// (seven or more) the letters; a quick run is huge or letters.
    /// </summary>
    public static List<MosaicBeat> Beats(IReadOnlyList<StageRun> runs, ISet<int>? alone = null, ISet<int>? tiled = null)
    {
        var groups = new List<(int From, int To)>();
        var characters = 0;
        for (var index = 0; index < runs.Count; index++)
        {
            if (groups.Count > 0 && index > 0)
            {
                var last = groups[^1];
                var gap = runs[index].Start - runs[index - 1].Start;
                var added = Joiner(runs[index]).Length + TextElements.Count(runs[index].Text);
                var fits = last.To - last.From < BeatRuns && characters + added <= BeatCharacters;
                var free = !(alone?.Contains(index) ?? false) && !(alone?.Contains(last.From) ?? false);
                if (gap < QuickGap && fits && free)
                {
                    groups[^1] = (last.From, index + 1);
                    characters += added;
                    continue;
                }
            }
            groups.Add((index, index + 1));
            characters = TextElements.Count(runs[index].Text);
        }
        // The longest-held word (ties: the longer word, then the earlier).
        int? held = null;
        (double Held, int Letters) best = (-1, -1);
        for (var index = 0; index < runs.Count; index++)
        {
            var key = (runs[index].End - runs[index].Start, TextElements.Count(runs[index].Text));
            if (key.Item1 > best.Held + 0.001 || (Math.Abs(key.Item1 - best.Held) <= 0.001 && key.Item2 > best.Letters))
            {
                best = key;
                held = index;
            }
        }
        var forced = groups.Select(group =>
        {
            for (var id = group.From; id < group.To; id++)
                if ((tiled?.Contains(id) ?? false) || id == held) return true;
            return false;
        }).ToArray();
        var kinds = new List<MosaicKind>(groups.Count);
        for (var g = 0; g < groups.Count; g++)
        {
            if (forced[g]) { kinds.Add(MosaicKind.Huge); continue; }
            var group = groups[g];
            var letters = 0;
            for (var id = group.From; id < group.To; id++) letters += TextElements.Count(runs[id].Text);
            MosaicKind[] preference = group.To - group.From > 1 ? [MosaicKind.Huge, MosaicKind.Letters]
                : letters <= 5 ? [MosaicKind.Bar, MosaicKind.Letters, MosaicKind.Huge]
                : letters >= 7 ? [MosaicKind.Letters, MosaicKind.Huge, MosaicKind.Bar]
                : [MosaicKind.Huge, MosaicKind.Bar, MosaicKind.Letters];
            MosaicKind? previous = kinds.Count > 0 ? kinds[^1] : null;
            MosaicKind? next = g + 1 < groups.Count && forced[g + 1] ? MosaicKind.Huge : null;
            MosaicKind? choice = null;
            foreach (var kind in preference) if (kind != previous && kind != next) { choice = kind; break; }
            if (choice is null) foreach (var kind in preference) if (kind != previous) { choice = kind; break; }
            kinds.Add(choice ?? preference[0]);
        }
        var beats = new List<MosaicBeat>(groups.Count);
        for (var g = 0; g < groups.Count; g++) beats.Add(new MosaicBeat(groups[g].From, groups[g].To, kinds[g]));
        return beats;
    }

    /// <summary>
    /// How far each beat has gone from where it landed into the stack, 0…1. A beat leaves just ahead of the next beat's
    /// arrival (or of the line's exit, for the last), but not before its last word has had a moment on its own; the move
    /// is quicker on a quick line. <paramref name="arrived"/> beats count under Reduce Motion, where the moves are steps.
    /// </summary>
    public static void Moves(IReadOnlyList<MosaicBeat> beats, IReadOnlyList<StageRun> runs, double time, double speed,
                             double exitStart, bool reduceMotion, int arrived, Span<double> moves)
    {
        for (var g = 0; g < beats.Count; g++)
        {
            var followed = g + 1 < beats.Count;
            if (reduceMotion)
            {
                moves[g] = followed && beats[g + 1].From < arrived ? 1 : 0;
                continue;
            }
            double next;
            if (followed) next = runs[beats[g + 1].From].Start - Lead;
            else if (double.IsFinite(exitStart)) next = exitStart;
            else { moves[g] = 0; continue; }
            var last = double.NegativeInfinity;
            for (var id = beats[g].From; id < beats[g].To; id++) last = Math.Max(last, runs[id].Start);
            if (double.IsNegativeInfinity(last)) last = next;
            var gap = Math.Max(0, next - (last - Lead));
            var duration = Math.Min(0.42 / Math.Min(3, Math.Max(1, speed)) + 0.06, Math.Max(0.14, gap * 0.9));
            var start = Math.Max(last - Lead + Math.Min(0.18, gap * 0.5), next - duration * 0.6);
            moves[g] = StageShared.Glide((time - start) / duration);
        }
    }

    public static double[] Moves(IReadOnlyList<MosaicBeat> beats, IReadOnlyList<StageRun> runs, double time, double speed,
                                 double exitStart, bool reduceMotion, int arrived)
    {
        var moves = new double[beats.Count];
        Moves(beats, runs, time, speed, exitStart, reduceMotion, arrived, moves);
        return moves;
    }

    /// <summary>Each beat's row in the stack: the sum of its own move and the moves of every beat after it.</summary>
    public static void Ranks(ReadOnlySpan<double> moves, Span<double> ranks)
    {
        double after = 0;
        for (var index = moves.Length - 1; index >= 0; index--)
        {
            after += moves[index];
            ranks[index] = after;
        }
    }

    public static double[] Ranks(IReadOnlyList<double> moves)
    {
        var ranks = new double[moves.Count];
        Ranks(moves.ToArray(), ranks);
        return ranks;
    }

    /// <summary>How much of a word is in, 0…1 (it may overshoot a little): fully in by its onset.</summary>
    public static double Appear(StageRun run, double time, double speed, bool reduceMotion, int arrived) =>
        reduceMotion ? (run.Id < arrived ? 1 : 0) : StageMotion.Arrival(run.Start - (Lead - StageMotion.Lead), time, speed);

    /// <summary>The centre of a beat measured <paramref name="size"/>: from where it lands to its row in the stack, flush to the stack's column.</summary>
    public static PointD Centre(SizeD size, double move, double rank, double sway, double step, double overhang, bool rightToLeft,
                                StageRect bounds)
    {
        var home = Home(size, sway, step, overhang, rightToLeft, bounds);
        var column = rightToLeft ? bounds.MaxX - bounds.Width * 0.14 : bounds.MinX + bounds.Width * 0.14;
        var stack = new PointD(column + (rightToLeft ? -1 : 1) * size.Width / 2, RowY(Math.Max(1, rank), step, bounds));
        return new PointD(home.X + (stack.X - home.X) * move, home.Y + (stack.Y - home.Y) * move);
    }

    /// <summary>
    /// When a letter of a word rises in: its syllable's own onset (less the lead), the letters of the syllable a little
    /// apart, all of them in before the next syllable or word is sung. <paramref name="next"/> is the onset after the run.
    /// </summary>
    public static double LetterReveal(int part, int letter, StageRun run, double? next, double time)
    {
        if (part < 0 || part >= run.Parts.Count) return 1;
        var onset = run.Parts[part].Start;
        var following = part + 1 < run.Parts.Count ? run.Parts[part + 1].Start : next ?? onset + 0.5;
        var window = Math.Min(0.6, Math.Max(0.08, following - onset));
        var count = Math.Max(1, TextElements.Count(run.Parts[part].Text));
        var rise = Math.Min(0.2, Math.Max(0.07, window * 0.55));
        var stagger = count > 1 ? Math.Min(0.045, window * 0.45 / (count - 1)) : 0;
        return StageShared.Glide((time - (onset - Lead + letter * stagger)) / rise);
    }

    /// <summary>The size the small stack sets its words at.</summary>
    public static double SmallSize(double unit) => unit * 0.6;

    /// <summary>How far apart the stack sets its rows: a word, and its reading when the line has readings.</summary>
    public static double StackStep(double unit, bool readings, double point = 1) =>
        unit * 0.85 + (readings ? StageShared.ReadingSlot(SmallSize(unit), point: point) : 0);

    /// <summary>The centre of the stack's row for a word <paramref name="rank"/> hand-offs old: the newest lowest, older a row up each (rank 1 to 4).</summary>
    public static double RowY(double rank, double step, StageRect bounds)
    {
        var top = bounds.MinY + Math.Max(bounds.Height * 0.1, step * 0.6);
        return top + (3 - Math.Min(4, Math.Max(rank, 1))) * step;
    }

    /// <summary>
    /// Where a word lands before it joins the stack: on the lower right thirds, below the stack, inside the 5 %
    /// title-safe margin. On a stage too short for both, it moves right of the stack's column instead.
    /// <paramref name="overhang"/> is how far the setting reaches past its own box (the colour bar).
    /// </summary>
    public static PointD Home(SizeD size, double sway, double step, double overhang, bool rightToLeft, StageRect bounds)
    {
        var below = RowY(1, step, bounds) + step * 0.7 + overhang + size.Height / 2;
        var lowest = bounds.MaxY - size.Height / 2 - bounds.Height * 0.05 - overhang;
        var y = Math.Min(lowest, Math.Max(bounds.MinY + bounds.Height * 0.6, below));
        // Its centre towards the right third, never past the safe edge.
        var right = bounds.Width * 0.95 - size.Width / 2;
        var x = bounds.MinX + Math.Max(bounds.Width / 2, Math.Min(right, bounds.Width * (0.6 + sway)));
        if (below > lowest + 1)
        {
            // Clear of the stack's column, as far as the far edge allows.
            var clear = bounds.Width * 0.34 + size.Width / 2;
            var far = bounds.Width * 0.95 - size.Width / 2;
            x = bounds.MinX + Math.Max(x - bounds.MinX, Math.Min(clear, far));
        }
        if (rightToLeft) x = bounds.MaxX - (x - bounds.MinX);
        return new PointD(x, y);
    }

    /// <summary>How much of a stacked word still shows: the newest two clearly, the third faint, gone by the fourth row.</summary>
    public static double StackFade(double rank) => Math.Max(0, Math.Min(1, 1 - (rank - 1.6) * 0.45));
}
