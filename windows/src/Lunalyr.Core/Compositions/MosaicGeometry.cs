// Mosaic's choreography (the Mac's MosaicLayout.swift, Test Build 90: mosaicBeats, mosaicLeaves, mosaicMotion,
// mosaicAppear, mosaicBarSpread, mosaicCentre, mosaicLetterReveal, mosaicHeld, mosaicHome and their constants; Lunalyr's own code, no line from the Verso import,
// ported as it is). Pure functions of the line and the time; the drawing is MosaicLayer's.
//
// A collage: each word is set a different way as it arrives -- huge, near the stage's width; white on a colour bar; or
// letter by letter, each letter rising onto one baseline -- and then shrinks into a small, flush-left stack above, read
// top to bottom, the oldest words fading out at its top. Nothing else is drawn: no ornaments, no panels.
//
// A quick run of words (each under QuickGap after the one before) is one "beat": set together in one treatment, each
// word appearing in its place as it is sung, so a fast line never stacks big words on top of each other. A beat leaves
// for the stack just before the next one arrives (the times are known), so the two never share the spot. Build 90 moves
// it on the original Mosaic's curves (the shared hand-off spring, its eases and wipe), so nothing jumps at an arrival.

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

    /// <summary>How far ahead of the next beat's arrival a beat starts to leave, at an ordinary pace.</summary>
    public const double LeaveAhead = 0.22;

    /// <summary>
    /// When each beat starts to leave for the stack: a little ahead of the next beat's arrival (or of the line's exit,
    /// for the last), so the two never share the spot, but only once its own last word is in. Null: it stays (the last
    /// beat of a line that does not leave).
    /// </summary>
    public static void Leaves(IReadOnlyList<MosaicBeat> beats, IReadOnlyList<StageRun> runs, double speed, double exitStart,
                              Span<double> leaves)
    {
        var pace = Math.Min(3, Math.Max(1, speed));
        for (var g = 0; g < beats.Count; g++)
        {
            double next;
            if (g + 1 < beats.Count) next = runs[beats[g + 1].From].Start - Lead;
            else if (double.IsFinite(exitStart)) next = exitStart;
            else { leaves[g] = double.NaN; continue; }
            // Ahead by about half the hand-off, which the pace shortens as it shortens the hand-off itself; never before
            // the last word is in.
            leaves[g] = Math.Max(next - LeaveAhead / pace, Held(beats[g], runs, speed));
        }
    }

    /// <summary>
    /// Each beat's journey into the stack (<paramref name="moves"/>, 0…1) and its row there (<paramref name="ranks"/>),
    /// with the original Mosaic's curves: each leave is the shared hand-off spring (its cadence shortened only by the
    /// line's pace), a beat's rank is the sum of its own and every later beat's hand-off, and its move is
    /// <c>glide(min(rank, 1.2) / 1.2)</c>. Under Reduce Motion each hand-off is a step once the next beat has
    /// <paramref name="arrived"/>. <paramref name="leaves"/> is scratch, one per beat.
    /// </summary>
    public static void Motion(IReadOnlyList<MosaicBeat> beats, IReadOnlyList<StageRun> runs, double time, double speed,
                              double exitStart, long seed, bool reduceMotion, int arrived, Span<double> leaves, Span<double> moves,
                              Span<double> ranks)
    {
        Leaves(beats, runs, speed, exitStart, leaves);
        double after = 0;
        for (var g = beats.Count - 1; g >= 0; g--)
        {
            var leave = leaves[g];
            double handoff = 0;
            if (!double.IsNaN(leave))
            {
                if (reduceMotion)
                {
                    var followed = g + 1 < beats.Count;
                    handoff = (followed ? beats[g + 1].From < arrived : time >= leave) ? 1 : 0;
                }
                else
                {
                    var id = g + 1 < beats.Count ? beats[g + 1].From : runs.Count;
                    handoff = StageMotion.Handoff(leave + StageMotion.Lead, time, StageMotion.RunSeed(seed, id), speed);
                }
            }
            after += handoff;
            ranks[g] = after;
        }
        for (var g = 0; g < beats.Count; g++) moves[g] = StageShared.Glide(Math.Min(ranks[g], 1.2) / 1.2);
    }

    public static (double[] Moves, double[] Ranks) Motion(IReadOnlyList<MosaicBeat> beats, IReadOnlyList<StageRun> runs, double time,
                                                         double speed, double exitStart, long seed, bool reduceMotion, int arrived)
    {
        var leaves = new double[beats.Count];
        var moves = new double[beats.Count];
        var ranks = new double[beats.Count];
        Motion(beats, runs, time, speed, exitStart, seed, reduceMotion, arrived, leaves, moves, ranks);
        return (moves, ranks);
    }

    /// <summary>How much of a word is in, 0…1 (it may overshoot a little): fully in by its onset.</summary>
    public static double Appear(StageRun run, double time, double speed, bool reduceMotion, int arrived) =>
        reduceMotion ? (run.Id < arrived ? 1 : 0) : StageMotion.Arrival(run.Start - (Lead - StageMotion.Lead), time, speed);

    /// <summary>
    /// How far the bar (with its word) has spread to its full width: the original wipe's ease and length (0.32 s,
    /// shortened by the line's pace, but never under 0.2 s), from the word's first appearance.
    /// </summary>
    public static double BarSpread(StageRun run, double time, double speed) =>
        StageShared.Glide((time - (run.Start - Lead)) / Math.Max(0.2, 0.32 / Math.Min(3, Math.Max(1, speed))));

    /// <summary>Beats land a little left and right of each other in turn, so one just leaving is never quite where the next lands.</summary>
    public static double Sway(int beat) => beat % 2 == 0 ? -0.035 : 0.035;

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
    /// When a letter of a word rises in: the original Mosaic's rise (0.25 s, letters 0.05 s apart), from its syllable's
    /// own onset less the lead. A quick syllable (the next under 0.35 s away) rises a little quicker, never under 0.2 s;
    /// a long word's letters follow each other closer when the next onset is near, so the word is in by then.
    /// <paramref name="next"/> is the onset after the run.
    /// </summary>
    public static double LetterReveal(int part, int letter, StageRun run, double? next, double time)
    {
        if (LetterTiming(part, run, next) is not { } timing) return 1;
        var onset = run.Parts[part].Start;
        return StageShared.Glide((time - (onset - Lead + letter * timing.Stagger)) / timing.Rise);
    }

    /// <summary>The rise of each letter of a syllable and the delay between letters.</summary>
    public static (double Rise, double Stagger)? LetterTiming(int part, StageRun run, double? next)
    {
        if (part < 0 || part >= run.Parts.Count) return null;
        var onset = run.Parts[part].Start;
        var following = part + 1 < run.Parts.Count ? run.Parts[part + 1].Start : next ?? onset + 1;
        var window = Math.Max(0, following - onset);
        var count = Math.Max(1, TextElements.Count(run.Parts[part].Text));
        var rise = window < 0.35 ? Math.Max(0.2, Math.Min(0.25, window + 0.05)) : 0.25;
        var stagger = count > 1 ? Math.Min(0.05, Math.Max(0.015, (window + Lead - rise) / (count - 1))) : 0;
        return (rise, stagger);
    }

    /// <summary>
    /// When a beat's last word is far enough in for the beat to start leaving: its arrival risen, or its letters well on
    /// their way up (the last ones finish as the hand-off spring gathers pace).
    /// </summary>
    public static double Held(MosaicBeat beat, IReadOnlyList<StageRun> runs, double speed)
    {
        var pace = Math.Min(3, Math.Max(1, speed));
        var held = double.NegativeInfinity;
        for (var index = beat.From; index < beat.To; index++)
        {
            var run = runs[index];
            held = Math.Max(held, run.Start - Lead + StageMotion.ArrivalRise / pace);
            if (beat.Kind != MosaicKind.Letters) continue;
            double? next = index + 1 < runs.Count ? runs[index + 1].Start : null;
            for (var part = 0; part < run.Parts.Count; part++)
            {
                if (LetterTiming(part, run, next) is not { } timing) continue;
                var span = timing.Rise + Math.Max(0, TextElements.Count(run.Parts[part].Text) - 1) * timing.Stagger;
                // A quick syllable's letters finish on the way (the next word is close).
                var share = timing.Rise < 0.25 ? 0.25 : 0.4;
                held = Math.Max(held, run.Parts[part].Start - Lead + span * share);
            }
        }
        return held;
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
