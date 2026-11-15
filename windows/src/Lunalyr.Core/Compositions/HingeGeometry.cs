// Hinge's choreography (the Mac's HingeLayout.swift, Build 88: hingeEntrance, hingePages, hingeSpine, hingePageWidth,
// hingeFocalSize, hingeTurns, hingeInstances, hingeExit, hingeEntry, hingePose and the hinge constants; Lunalyr's own
// code, no line from the Verso import, ported as it is). Pure functions of the line and the time; the drawing is
// HingeLayer's.
//
// An open book seen straight on. The spine is a vertical axis; each phrase (StagePhrases) is one page of heavy caps:
// its focal word set huge and flush to the spine, the words before it a small line of type along the page's head, the
// words after it a small line at its foot. The page being sung is the right-hand page (left, for a right-to-left line).
// When the next phrase starts, the page turns over on the spine and lands, dimmed, on the other side; the new phrase is
// set on the fresh page beneath it, and the page before is covered. One axis, one move at two scales: the focal word
// swings open on the spine as it is sung, the page turns on it at each phrase, and the line leaves by closing the book:
// both pages fold shut onto the spine, accelerating, and the closed book recedes in the reading direction; the next
// line's book arrives still moving that way. The focal word bursts in oversized and blurred as it swings, and settles;
// small content words flip open on the spine with the same spring, a quick run as one group; function words just ink.
// A held focal word grows a little from the spine as it is held.

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>Which side of the spine a page sits on.</summary>
public enum HingeSide { Left, Right }

/// <summary>How a word arrives on its page (from its role in <see cref="StagePhrasing"/>).</summary>
public enum HingeEntrance
{
    /// <summary>The focal word: it swings open on the spine.</summary>
    Swing,
    /// <summary>A content word or a member of a quick run: flips open on the spine (a run as one group).</summary>
    Flip,
    /// <summary>A function word: inks in place.</summary>
    Ink,
}

/// <summary>One drawn copy of a page: on its own side (<see cref="Back"/> false) or turned over.</summary>
/// <param name="Angle">The page's angle about the spine, in degrees (0 flat).</param>
public readonly record struct HingeInstance(int Page, bool Back, double Angle, double Opacity, double Z);

/// <summary>A page's slab: whether its phrase has a head line, a foot line, a reading under the focal word, and under any small word.</summary>
public readonly record struct HingeSlab(bool Head, bool Foot, bool FocalReading, bool SmallReading);

public static class HingeGeometry
{
    /// <summary>A page turning over on the spine.</summary>
    public const double Turn = 0.62;
    /// <summary>The exit: the pages folding shut (accelerating), how far shut (a hair short of edge-on, so the closed
    /// spine is seen), the read page fading as the fold starts, and the closed book receding.</summary>
    public const double Close = 0.4, ShutAngle = 86, ReadGone = 0.15, Recede = 0.15;
    /// <summary>How far (a share of the stage's width) the book travels as it leaves, and as the next arrives, decelerating.</summary>
    public const double ExitTravel = 0.12, Arrive = 0.45;
    /// <summary>A page starts to turn this long before the next page's first word arrives.</summary>
    public const double TurnLead = 0.12;
    /// <summary>The focal word's burst settling (scale and blur), and its swing's pace against the shared arrival spring.</summary>
    public const double Swing = 0.42, SwingPace = 0.75;
    /// <summary>The focal word's starting angle, standing out of the page, and how oversized it bursts in.</summary>
    public const double SwingAngle = 84, Burst = 0.4;
    /// <summary>A small content word's flip, and any small word's ink.</summary>
    public const double FlipAngle = 70, Ink = 0.1;
    /// <summary>How much a held focal word grows over its held time.</summary>
    public const double HeldGrowth = 0.045;
    /// <summary>The two pages of a line-timed spread open this far apart.</summary>
    public const double SpreadStagger = 0.08;
    /// <summary>A page once read (turned over), the 5% safe margin, and the gutter each side of the spine (type units).</summary>
    public const double ReadDim = 0.38, Margin = 0.05, Gutter = 0.18;

    public static HingeEntrance EntranceOf(StageWordInfo info)
    {
        if (info.IsFocal) return HingeEntrance.Swing;
        if (info.IsFunction && info.RunGroup is null) return HingeEntrance.Ink;
        return HingeEntrance.Flip;
    }

    /// <summary>The phrases on each page: one a page for a word-timed line; a line-timed line opens as one spread, its phrases split over two pages.</summary>
    public static List<int[]> Pages(StagePhrasing phrasing)
    {
        var count = phrasing.Phrases.Count;
        if (count == 0) return [];
        if (phrasing.LineTimed && count > 1)
        {
            var half = (count + 1) / 2;
            return [[.. Enumerable.Range(0, half)], [.. Enumerable.Range(half, count - half)]];
        }
        return [.. Enumerable.Range(0, count).Select(k => new[] { k })];
    }

    /// <summary>The spine's x: the stage's centre for a spread, at the left third (right third, right to left) for a single page.</summary>
    public static double Spine(double width, bool single, bool rightToLeft)
    {
        if (!single) return width / 2;
        var margin = width * Margin;
        var x = margin + (width - 2 * margin) * 0.3;
        return rightToLeft ? width - x : x;
    }

    /// <summary>The room a page has between the spine's gutter and the 5% margin.</summary>
    public static double PageWidth(double width, double spine, HingeSide side, double unit)
    {
        var margin = width * Margin;
        var gutter = unit * Gutter;
        var room = side == HingeSide.Right ? width - margin - spine - gutter : spine - margin - gutter;
        return Math.Max(unit * 2, room);
    }

    /// <summary>The small type of a page's head and foot lines.</summary>
    public static double SmallSize(double unit) => unit * 0.62;

    /// <summary>
    /// The focal word's type size on a page: larger for a held focal word, and never so large that the page (its slabs,
    /// their small lines and every reading) is taller than 88% of the stage.
    /// </summary>
    public static double FocalSize(IReadOnlyList<HingeSlab> slabs, bool held, double unit, double stageHeight)
    {
        var small = SmallSize(unit);
        var smallLine = small * 1.2;
        double fixedRoom = 0, perFocal = 0;
        foreach (var slab in slabs)
        {
            var lines = (slab.Head ? 1 : 0) + (slab.Foot ? 1 : 0);
            fixedRoom += lines * (smallLine + (slab.SmallReading ? StageShared.ReadingSlot(small) : 0)) + unit * 0.12;
            // A reading under the focal word is about 0.6 of its size.
            perFocal += 1.18 + (slab.FocalReading ? StageShared.ReadingSlot(100) / 100 : 0);
        }
        fixedRoom += Math.Max(0, slabs.Count - 1) * unit * 0.5;
        var fits = (stageHeight * 0.88 - fixedRoom) / Math.Max(perFocal, 0.1);
        var wanted = unit * (held ? 3.0 : 2.5);
        return Math.Max(unit * 0.9, Math.Min(wanted, fits));
    }

    /// <summary>
    /// How far each page has turned over, 0 (flat on its own side) to 1 (flat on the other side, read). A page turns as
    /// the next page's first word is about to be sung; a line-timed spread opens from the spine at once. The last page
    /// never turns: the line leaves by closing the book (<see cref="Exit"/>). Eased in and out: a page is a heavy thing.
    /// </summary>
    public static void Turns(ReadOnlySpan<double> pageStarts, bool lineTimed, double time, double speed, Span<double> turns)
    {
        var count = pageStarts.Length;
        if (count == 0) return;
        var pace = 1 + (Math.Min(3, Math.Max(1, speed)) - 1) * 0.4;
        var span = Turn / pace;
        if (lineTimed)
        {
            var first = double.PositiveInfinity;
            foreach (var start in pageStarts) first = Math.Min(first, start);
            for (var k = 0; k < count; k++)
            {
                var open = EaseOut((time - (first - StageMotion.Lead) - SpreadStagger * k) / Swing);
                turns[k] = k == count - 1 ? 0.5 - 0.5 * open : 0.5 + 0.5 * open;
            }
            return;
        }
        for (var k = 0; k < count; k++)
            turns[k] = k + 1 < count ? EaseInOut((time - (pageStarts[k + 1] - StageMotion.Lead - TurnLead)) / span) : 0;
    }

    public static double[] Turns(IReadOnlyList<double> pageStarts, bool lineTimed, double time, double speed)
    {
        var turns = new double[pageStarts.Count];
        Turns(pageStarts.ToArray(), lineTimed, time, speed, turns);
        return turns;
    }

    /// <summary>
    /// How the line leaves: the book closes. Both pages fold shut onto the spine, accelerating (Fold, 0…1 of the way
    /// shut), the read page gone in the first moments (ReadFade); the closed book is already moving off in the reading
    /// direction and, once shut, recedes quickly (Travel, 0…1 of <see cref="ExitTravel"/>; Opacity).
    /// </summary>
    public static (double Fold, double ReadFade, double Travel, double Opacity) Exit(double exitStart, double time)
    {
        if (!(time > exitStart)) return (0, 1, 0, 1);
        var age = time - exitStart;
        var fold = EaseIn(age / Close);
        var travel = EaseIn(age / (Close + Recede));
        var opacity = 1 - Easing.Smoothstep((age - Close + 0.03) / Recede);
        var readFade = 1 - Easing.Smoothstep(age / ReadGone);
        return (fold, readFade, travel, opacity);
    }

    /// <summary>How far (0…1 of <see cref="ExitTravel"/>) a line's book is still short of its place as it arrives: it comes in already moving, decelerating.</summary>
    public static double Entry(double firstStart, double time)
    {
        var t = Math.Min(Math.Max((time - (firstStart - StageMotion.Lead)) / Arrive, 0), 1);
        if (double.IsNaN(t)) t = 1;
        return (1 - t) * (1 - t) * (1 - t);
    }

    /// <summary>
    /// The drawn copies of each page at its turn, faded edge-on so no page is seen squashed to a sliver, dimmed once
    /// read, and gone once the next page lands on it. As the line leaves (<paramref name="fold"/>, <paramref name="readFade"/>
    /// from <see cref="Exit"/>) both pages fold shut onto the spine and the read page fades at once. Under Reduce Motion
    /// a turn is a cross-fade. <paramref name="begun"/> says which pages have their first word sung (empty: all have).
    /// Written into <paramref name="into"/> (cleared first), back to front.
    /// </summary>
    public static void Instances(ReadOnlySpan<double> turns, ReadOnlySpan<bool> begun, double fold, double readFade, bool reduceMotion,
                                 List<HingeInstance> into)
    {
        into.Clear();
        if (reduceMotion) fold = 0;
        for (var k = 0; k < turns.Length; k++)
        {
            if (k < begun.Length && !begun[k]) continue;
            var turn = Math.Min(Math.Max(turns[k], 0), 1);
            var next = k + 1 < turns.Length ? Math.Min(Math.Max(turns[k + 1], 0), 1) : 0;
            var covered = Easing.Smoothstep((next - 0.55) / 0.35);
            double BackOpacity(double landed) => (1 - (1 - ReadDim) * landed) * readFade;
            if (reduceMotion)
            {
                // A step at the turn's start, cross-faded.
                var mix = turn <= 0 ? 0 : turn >= 1 ? 1 : Easing.Smoothstep(turn / 0.3);
                var front = (1 - mix) * (1 - covered);
                var back = mix * BackOpacity(mix) * (1 - covered);
                if (front > 0.001) into.Add(new HingeInstance(k, false, 0, front, 1));
                if (back > 0.001) into.Add(new HingeInstance(k, true, 0, back, 0));
                continue;
            }
            var degrees = 180 * turn;
            var edgeOn = Math.Abs(Math.Cos(degrees * Math.PI / 180));
            // Closing, a page stays seen to the end, as the book's thin spine.
            var visible = fold > 0 ? 1 : Easing.Smoothstep((edgeOn - 0.1) / 0.35);
            var shut = ShutAngle * fold;
            var moving = turn > 0 && turn < 1;
            if (turn < 0.5)
            {
                var opacity = visible * (1 - covered);
                if (opacity > 0.001) into.Add(new HingeInstance(k, false, Math.Min(degrees + shut, 90), opacity, moving ? 2 : 1));
            }
            else
            {
                var landed = Easing.Smoothstep((turn - 0.5) / 0.5);
                var opacity = visible * BackOpacity(landed) * (1 - covered);
                if (opacity > 0.001) into.Add(new HingeInstance(k, true, Math.Max(degrees - 180 - shut, -90), opacity, moving ? 2 : 0));
            }
        }
        // Back to front, keeping the order of equals (an insertion sort: no allocation).
        for (var i = 1; i < into.Count; i++)
        {
            var item = into[i];
            var j = i - 1;
            while (j >= 0 && into[j].Z > item.Z) { into[j + 1] = into[j]; j--; }
            into[j + 1] = item;
        }
    }

    public static List<HingeInstance> Instances(IReadOnlyList<double> turns, IReadOnlyList<bool>? begun = null,
                                                (double Fold, double ReadFade)? exit = null, bool reduceMotion = false)
    {
        var into = new List<HingeInstance>();
        Instances(turns.ToArray(), (begun ?? []).ToArray(), exit?.Fold ?? 0, exit?.ReadFade ?? 1, reduceMotion, into);
        return into;
    }

    /// <summary>
    /// A word's pose as it arrives: its ink (opacity), its swing about the spine (degrees, 0 flat; the spring takes it a
    /// little past flat and back), its scale (the focal word bursts in oversized and settles, then grows a little while
    /// held) and its blur (0…1, the burst's speed). Each lands on the word's onset; <paramref name="groupStart"/> is when
    /// the quick run it belongs to swings in as one (its own onset otherwise).
    /// </summary>
    public static (double Ink, double Swing, double Grow, double Blur) Pose(HingeEntrance entrance, double start, double groupStart,
                                                                         double? held, double time, double speed, bool reduceMotion)
    {
        var age = time - (start - StageMotion.Lead);
        if (reduceMotion) return (Easing.Smoothstep(age / WallpaperMotion.ReducedDissolve), 0, 1, 0);
        var pace = 1 + (Math.Min(3, Math.Max(1, speed)) - 1) * 0.5;
        var inked = Easing.Smoothstep(age / (Ink / pace));
        switch (entrance)
        {
            case HingeEntrance.Ink:
                return (inked, 0, 1, 0);
            case HingeEntrance.Flip:
            {
                // The shared arrival spring: a quick flip open with a little overshoot.
                var open = StageMotion.Arrival(groupStart, time, speed);
                return (inked, FlipAngle * (1 - open), 1, 0);
            }
            default:
            {
                var open = Spring(age, StageMotion.ArrivalRise / SwingPace / pace);
                var t = Math.Min(Math.Max(age / (Swing / pace), 0), 1);
                if (double.IsNaN(t)) t = 0;
                var settle = 1 - (1 - t) * (1 - t) * (1 - t);
                var grow = 1 + Burst * (1 - settle);
                if (held is { } h && h > 0) grow *= 1 + HeldGrowth * EaseInOut((time - start) / h);
                return (Math.Min(1, Math.Max(0, open) * 3), SwingAngle * (1 - open), grow, 1 - settle);
            }
        }
    }

    /// <summary>The arrival spring (as <see cref="StageMotion.Arrival"/>: about 8% overshoot, from rest) with its own rise time.</summary>
    public static double Spring(double age, double rise) =>
        LyricMotion.SpringStep(age, new Spring(3.36 / Math.Max(rise, 0.01), StageMotion.ArrivalDamping));

    /// <summary>Ease-in (cubic): the book closing and leaving, accelerating.</summary>
    public static double EaseIn(double x)
    {
        var t = Clamp01(x);
        return t * t * t;
    }

    /// <summary>Ease-out (exponential): arrivals.</summary>
    public static double EaseOut(double x)
    {
        var t = Clamp01(x);
        return t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t);
    }

    /// <summary>Ease-in-out (cubic): turns, and a held word's growth.</summary>
    public static double EaseInOut(double x)
    {
        var t = Clamp01(x);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    private static double Clamp01(double x) => double.IsNaN(x) ? 0 : Math.Min(Math.Max(x, 0), 1);
}
