// Torque's choreography (the Mac's TorqueLayout.swift, Build 88: torqueSize, torquePose, torqueLive, torqueRotation,
// torqueSwing, torquePlan, torqueRows, TorqueTurn, TorqueFaceLayout and the torque constants; Lunalyr's own code, no
// line from the Verso import, ported as it is). Pure functions of the line and the time; the drawing is TorqueLayer's.
//
// A square column of heavy, extruded type turning about a vertical axis at the stage's centre. Each phrase
// (StagePhrases) is one face of the column: while it is sung its face looks straight out and its words are wrung into
// place on it; when the next phrase starts, the whole column torques a quarter turn to the left and the next face swings
// in from the right (mirrored for a right-to-left line). Within a face the focal word gets the wring (it arrives
// edge-on and unwinds, its thick edge growing in after it), content words rise a little, function words cut in beside
// the word they ride with, and a quick run rises as one group. A held word is wrung tighter for as long as it is sung.
// Nothing moves once it has landed. Every turn is a flat 2D transform (a horizontal squash and a slight shear).

using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>A word's part in Torque: the phrase's focal word, a content word, or a function word riding along.</summary>
public enum TorqueRole { Focal, Content, Function }

/// <summary>
/// A word's pose: how far it is still wrung (degrees), how far below its place it is (a share of its size), how deep its
/// thick edge is (0…1 of its resting depth, more for a held word), how visible it is, and its punch (a focal word lands
/// a little large and settles).
/// </summary>
public readonly record struct TorquePose(double Wring, double Rise, double Depth, double Opacity, double Scale)
{
    /// <summary>Landed: flat, in place, its edge at full depth.</summary>
    public static TorquePose Rest => new(0, 0, 1, 1, 1);
}

/// <summary>A flat affine transform (as the Mac's CGAffineTransform: x' = a·x + c·y + tx, y' = b·x + d·y + ty).</summary>
public readonly record struct TorqueAffine(double A, double B, double C, double D, double Tx, double Ty)
{
    public static TorqueAffine Identity => new(1, 0, 0, 1, 0, 0);
    public static TorqueAffine Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static TorqueAffine Scaling(double x, double y) => new(x, 0, 0, y, 0, 0);

    /// <summary>This, then <paramref name="next"/>.</summary>
    public TorqueAffine Then(TorqueAffine next) => new(
        A * next.A + B * next.C, A * next.B + B * next.D,
        C * next.A + D * next.C, C * next.B + D * next.D,
        Tx * next.A + Ty * next.C + next.Tx, Tx * next.B + Ty * next.D + next.Ty);

    public PointD Apply(PointD p) => new(A * p.X + C * p.Y + Tx, B * p.X + D * p.Y + Ty);

    /// <summary>The bounding box of <paramref name="rect"/> once transformed.</summary>
    public StageRect Apply(StageRect rect)
    {
        Span<PointD> corners = [Apply(new PointD(rect.MinX, rect.MinY)), Apply(new PointD(rect.MaxX, rect.MinY)),
                                Apply(new PointD(rect.MinX, rect.MaxY)), Apply(new PointD(rect.MaxX, rect.MaxY))];
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var c in corners)
        {
            minX = Math.Min(minX, c.X); minY = Math.Min(minY, c.Y);
            maxX = Math.Max(maxX, c.X); maxY = Math.Max(maxY, c.Y);
        }
        return new StageRect(minX, minY, maxX - minX, maxY - minY);
    }
}

/// <summary>One face of the column: a phrase.</summary>
/// <param name="Runs">Its runs (<see cref="StageRun.Id"/>s) in reading order.</param>
/// <param name="Rows">The rows, as run ids.</param>
/// <param name="FlushTrailing">Set flush right (flush left otherwise); neighbouring faces alternate.</param>
/// <param name="Start">Its first onset.</param>
/// <param name="TurnStart">When the column starts turning to it.</param>
public sealed record TorqueFace(int[] Runs, int[][] Rows, int Focal, bool FlushTrailing, double Start, double TurnStart)
{
    /// <summary>The rows as positions in <see cref="Runs"/>, for the layout.</summary>
    public int[][] RowSlots { get; } = [.. Rows.Select(row => row.Select(id => Array.IndexOf(Runs, id)).Where(i => i >= 0).ToArray())];
}

/// <summary>A line's faces, rows, roles and clocks.</summary>
public sealed class TorquePlan
{
    public required TorqueFace[] Faces { get; init; }
    /// <summary>The face each run is on, by run id.</summary>
    public required int[] FaceOfRun { get; init; }
    public required TorqueRole[] Roles { get; init; }
    /// <summary>When each run starts its entrance: its own onset, or the first onset of the quick run it belongs to.</summary>
    public required double[] Clocks { get; init; }
    /// <summary>How long each held word is sung (0 for words that are not held).</summary>
    public required double[] Held { get; init; }
    public bool LineTimed { get; init; }
    public bool RightToLeft { get; init; }

    public static TorquePlan Empty { get; } = new() { Faces = [], FaceOfRun = [], Roles = [], Clocks = [], Held = [] };
}

public static class TorqueGeometry
{
    /// <summary>Torque's base unit (s): arrivals take 2, the column's turn 3, the wring 4.</summary>
    public const double Unit = 0.12;
    /// <summary>How far a focal word is turned as it starts to arrive, in degrees.</summary>
    public const double WringAngle = 84;
    /// <summary>The column starts turning this long before a phrase's first word.</summary>
    public const double TurnLead = 0.08;
    /// <summary>The line's exit (about three quarters of the wring and turn).</summary>
    public const double ExitTime = 0.27;
    /// <summary>Each face is set a little tipped, the other way from its neighbours (degrees).</summary>
    public const double Tip = 4;

    /// <summary>A word's size in units, by its role: size carries the hierarchy.</summary>
    public static double Size(TorqueRole role, double unit) => role switch
    {
        TorqueRole.Focal => unit * 2.4,
        TorqueRole.Content => unit * 1.7,
        _ => unit * 1.1,
    };

    /// <summary>The depth of a word's thick edge (as a share of its size) once it has landed.</summary>
    public static double Depth(TorqueRole role) => role switch
    {
        TorqueRole.Focal => 0.10,
        TorqueRole.Content => 0.075,
        _ => 0.05,
    };

    /// <summary>The copies drawn for a word's thick edge.</summary>
    public static int Steps(TorqueRole role) => role switch
    {
        TorqueRole.Focal => 7,
        TorqueRole.Content => 5,
        _ => 3,
    };

    /// <summary>Torque's one out-ease: easeOutBack with an overshoot of about 8 %, for every arrival and the column's turn.</summary>
    public static double Out(double x)
    {
        var t = double.IsNaN(x) ? 0 : Math.Min(Math.Max(x, 0), 1);
        const double c1 = 1.2, c3 = c1 + 1;
        var u = t - 1;
        return 1 + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>Torque's one in-ease: easeInCubic, for the line leaving.</summary>
    public static double In(double x)
    {
        var t = double.IsNaN(x) ? 0 : Math.Min(Math.Max(x, 0), 1);
        return t * t * t;
    }

    /// <summary>A word's pose at <paramref name="time"/>.</summary>
    public static TorquePose Pose(StageRun run, TorqueRole role, double clock, double held, bool lineTimed, double time,
                                  bool reduceMotion)
    {
        var onset = run.Start - StageMotion.Lead;
        if (!(time >= onset)) return new TorquePose(0, 0, 0, 0, 1);
        // Line-timed lines, and Reduce Motion: the face move is the only move.
        if (reduceMotion || lineTimed) return TorquePose.Rest;
        switch (role)
        {
            case TorqueRole.Focal:
            {
                // The biggest word takes the longest: 4 units.
                var p = (time - onset) / (4 * Unit);
                var left = 1 - Out(p);
                // The thick edge follows the word in, and keeps deepening while it is held.
                var edge = Out((time - onset - Unit) / (2 * Unit));
                var tighter = held > 0 ? Math.Min(1, Math.Max(0, (time - run.Start) / held)) : 0;
                return new TorquePose(WringAngle * left, 0, Math.Min(1, Math.Max(0, edge)) * (1 + 0.6 * tighter),
                                      Math.Min(1, (time - onset) / Unit), 1 + 0.22 * left);
            }
            case TorqueRole.Content:
            {
                // A quick run rises as one group, on its first member's clock.
                var start = Math.Min(clock, run.Start) - StageMotion.Lead;
                var left = 1 - Out((time - start) / (2 * Unit));
                // The same twist, smaller: content words are wrung less than half as far.
                return new TorquePose(WringAngle * 0.45 * left, 0.3 * left,
                                      Math.Min(1, Math.Max(0, Out((time - onset - 0.5 * Unit) / (2 * Unit)))),
                                      Math.Min(1, (time - onset) / (0.5 * Unit)), 1);
            }
            default:
                return new TorquePose(0, 0, Math.Min(1, Math.Max(0, Out((time - onset) / (2 * Unit)))), Math.Min(1, (time - onset) / Unit), 1);
        }
    }

    /// <summary>1 while <paramref name="index"/> is the word being sung, easing to 0 over a short hand-over once the next starts; the last stays lit.</summary>
    public static double Live(IReadOnlyList<StageRun> runs, int index, double time)
    {
        if (!(time >= runs[index].Start - StageMotion.Lead)) return 0;
        if (index + 1 >= runs.Count) return 1;
        var next = runs[index + 1].Start - StageMotion.Lead;
        return 1 - Easing.Smoothstep((time - next) / 0.2);
    }

    /// <summary>
    /// How far the column has turned, in faces: 0 facing the first phrase, 1 the second, and so on (a little past during
    /// the overshoot). Under Reduce Motion it steps to the face of the newest shown word.
    /// </summary>
    public static double Rotation(TorquePlan plan, double time, int shownCount, bool reduceMotion)
    {
        if (reduceMotion)
            return shownCount > 0 && shownCount - 1 < plan.FaceOfRun.Length ? plan.FaceOfRun[shownCount - 1] : 0;
        double rho = 0;
        for (var k = 1; k < plan.Faces.Length; k++)
        {
            var p = (time - plan.Faces[k].TurnStart) / (3 * Unit);
            if (!(p > 0)) break;
            rho += Out(p);
        }
        return rho;
    }

    /// <summary>The whole column's extra turn, in degrees: a new line enters already turning (+45° to 0), and leaves turning on to the left (0 to −60).</summary>
    public static double Swing(TorquePlan plan, double time, double exitStart, bool reduceMotion)
    {
        if (reduceMotion || plan.Faces.Length == 0) return 0;
        var enter = (time - (plan.Faces[0].Start - StageMotion.Lead)) / (2 * Unit);
        var entering = plan.LineTimed || enter < 1 ? 45 * (1 - Out(enter)) : 0;
        return entering - 60 * Leaving(time, exitStart);
    }

    /// <summary>The line leaving, 0…1, on the in-ease.</summary>
    public static double Leaving(double time, double exitStart) =>
        double.IsFinite(exitStart) ? In((time - exitStart) / ExitTime) : 0;

    // ---- The plan (per line) ------------------------------------------------------------------------------

    /// <summary>The line's plan: faces, rows, roles and clocks, from its (cached) phrasing. A layer makes it once per line.</summary>
    public static TorquePlan Plan(IReadOnlyList<StageRun> runs) => MakePlan(runs, StagePhrases.Phrasing(runs));

    public static TorquePlan MakePlan(IReadOnlyList<StageRun> runs, StagePhrasing phrasing)
    {
        if (runs.Count == 0 || phrasing.Words.Count != runs.Count) return TorquePlan.Empty;
        var infos = phrasing.Words;
        var roles = infos.Select(info => info.IsFocal ? TorqueRole.Focal : info.IsFunction ? TorqueRole.Function : TorqueRole.Content).ToArray();
        // A quick run's members share its first onset.
        var clocks = runs.Select(run => run.Start).ToArray();
        for (var index = 0; index < infos.Count; index++)
        {
            if (infos[index].RunGroup is not { } group) continue;
            var earliest = double.PositiveInfinity;
            for (var k = 0; k < infos.Count; k++)
                if (infos[k].RunGroup == group && infos[k].Phrase == infos[index].Phrase) earliest = Math.Min(earliest, runs[k].Start);
            clocks[index] = double.IsFinite(earliest) ? earliest : runs[index].Start;
        }
        var held = infos.Select(info => info.IsHeld ? info.Duration : 0).ToArray();
        bool IsFunction(int id) => id >= 0 && id < infos.Count && infos[id].IsFunction;

        var faces = new List<TorqueFace>();
        var faceOfRun = new int[runs.Count];
        if (phrasing.LineTimed)
        {
            // No word timing: one face, a row per phrase, arriving with the face move.
            var rows = phrasing.Phrases.Select(phrase => phrase.Runs.ToArray()).ToArray();
            var start = runs.Min(run => run.Start);
            var focal = phrasing.Phrases[0].Focal;
            foreach (var phrase in phrasing.Phrases)
                if (TextElements.Count(runs[phrase.Focal].Text) > TextElements.Count(runs[focal].Text)) focal = phrase.Focal;
            faces.Add(new TorqueFace([.. Enumerable.Range(0, runs.Count)], rows, focal, false, start, double.NegativeInfinity));
        }
        else
        {
            foreach (var phrase in phrasing.Phrases)
            {
                var ids = phrase.Runs.ToArray();
                var rows = Rows(ids, phrase.Focal, IsFunction);
                var start = ids.Min(id => runs[id].Start);
                var turnStart = double.NegativeInfinity;
                if (faces.Count > 0)
                {
                    // Facing the new phrase as its first word is sung, but never before the old face's last word has landed.
                    var lastLanded = faces[^1].Runs.Max(id => runs[id].Start);
                    var early = start - StageMotion.Lead - TurnLead;
                    turnStart = Math.Min(start - StageMotion.Lead, Math.Max(early, lastLanded + Unit));
                }
                faces.Add(new TorqueFace(ids, rows, phrase.Focal, faces.Count % 2 != 0, start, turnStart));
                foreach (var id in ids) if (id < faceOfRun.Length) faceOfRun[id] = faces.Count - 1;
            }
        }
        return new TorquePlan
        {
            Faces = [.. faces], FaceOfRun = faceOfRun, Roles = roles, Clocks = clocks, Held = held,
            LineTimed = phrasing.LineTimed, RightToLeft = phrasing.RightToLeft,
        };
    }

    /// <summary>
    /// A face's rows: each content word takes the function words in front of it (a function word left at the end joins
    /// the word before); the focal word's group gets a row of its own, the groups before it share a row and the groups
    /// after it share a row. So the grammar decides where the big word sits: top, middle or foot of the face.
    /// </summary>
    public static int[][] Rows(IReadOnlyList<int> ids, int focal, Func<int, bool> isFunction)
    {
        if (ids.Count == 0) return [];
        var groups = new List<List<int>>();
        var pending = new List<int>();
        foreach (var id in ids)
        {
            pending.Add(id);
            if (!isFunction(id))
            {
                groups.Add(pending);
                pending = [];
            }
        }
        if (pending.Count > 0)
        {
            if (groups.Count == 0) groups.Add(pending);
            else groups[^1].AddRange(pending);
        }
        var focalGroup = groups.FindIndex(group => group.Contains(focal));
        if (focalGroup < 0) return [[.. ids]];
        var rows = new List<int[]>();
        var before = groups.Take(focalGroup).SelectMany(group => group).ToArray();
        var after = groups.Skip(focalGroup + 1).SelectMany(group => group).ToArray();
        if (before.Length > 0) rows.Add(before);
        rows.Add([.. groups[focalGroup]]);
        if (after.Length > 0) rows.Add(after);
        return [.. rows];
    }

    /// <summary>
    /// A turn about a vertical axis, drawn in 2D (the Mac's TorqueTurn): the view is squashed by the cosine of
    /// <paramref name="angle"/>, sheared a little for the keystone and, given the space it is fitted into
    /// (<paramref name="available"/>), scaled to fit it and carried round the column (a face's centre swings by half its
    /// width times the sine). Maps the view's own box (top-left origin) to where it is drawn.
    /// </summary>
    public static TorqueAffine Turn(double angle, SizeD size, SizeD? available)
    {
        var fit = available is { } room ? LyricStageFit.Scale(size, room) : 1;
        var radians = Math.Min(Math.Max(angle, -90), 90) * Math.PI / 180;
        var c = Math.Max(Math.Cos(radians), 0.001);
        var s = Math.Sin(radians);
        var swing = available is null ? 0 : size.Width * fit / 2 * s;
        return TorqueAffine.Translation(-size.Width / 2, -size.Height / 2)
            .Then(TorqueAffine.Scaling(fit, fit))
            .Then(new TorqueAffine(c, -s * 0.1, 0, 1 - 0.08 * Math.Abs(s), swing, 0))
            .Then(TorqueAffine.Translation(size.Width / 2, size.Height / 2));
    }

    /// <summary>
    /// Sets a face: rows of words sharing a baseline, stacked, flush to one side. Each word's box from the face's
    /// top-left corner, and the face's size; a word in no row gets a zero box at the origin.
    /// </summary>
    public static (SizeD Size, StageRect[] Frames) ArrangeFace(IReadOnlyList<SizeD> sizes, IReadOnlyList<double> baselines,
                                                               IReadOnlyList<IReadOnlyList<int>> rows, bool trailing,
                                                               bool rightToLeft, double wordGap, double rowGap)
    {
        var frames = new StageRect[sizes.Count];
        var valid = rows.Select(row => row.Where(i => i >= 0 && i < sizes.Count).ToArray()).Where(row => row.Length > 0).ToArray();
        if (valid.Length == 0) return (SizeD.Zero, frames);
        double Baseline(int index)
        {
            var measured = index < baselines.Count ? baselines[index] : sizes[index].Height * 0.8;
            return Math.Min(sizes[index].Height, Math.Max(0, measured));
        }
        var widths = valid.Select(row => row.Sum(i => sizes[i].Width) + wordGap * (row.Length - 1)).ToArray();
        var widest = widths.Max();
        double top = 0;
        for (var r = 0; r < valid.Length; r++)
        {
            var row = valid[r];
            var ascent = row.Max(Baseline);
            var descent = row.Max(i => sizes[i].Height - Baseline(i));
            var x = trailing ? widest - widths[r] : 0;
            foreach (var index in rightToLeft ? row.Reverse() : row)
            {
                var size = sizes[index];
                frames[index] = new StageRect(x, top + ascent - Baseline(index), size.Width, size.Height);
                x += size.Width + wordGap;
            }
            top += ascent + descent + (r < valid.Length - 1 ? rowGap : 0);
        }
        return (new SizeD(widest, Math.Max(0, top)), frames);
    }
}
