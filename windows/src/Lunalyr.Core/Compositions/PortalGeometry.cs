// Portal's choreography (the Mac's PortalLayout.swift, Build 88: portalRuns, PortalMotion, portalClimb, portalIcons,
// portalShapes, portalScale, keepOnStage; Lunalyr's own code, no line from the Verso import, ported as it is). Pure
// functions of the line and the time; the drawing is PortalLayer's.
//
// The word being sung sits in a solid pill, tilted as it arrives and settling flat, with a soft burst swelling off it as
// it lands. The words before it climb above it as a trail of smaller, paler type, spaced by their own heights so none
// overlaps, and hold still there to be read. A word with a picture of its own ("love", "home", "call") carries that
// symbol in its pill; some other words carry a small ornament, never one of the three pills before; function words none.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>A Portal pill's shape: taken in turn so no two pills in a row match.</summary>
public enum PortalShape { Capsule, Rounded, Tag, Speech, Slant }

/// <summary>
/// How each pill moves at one moment, worked out from the words' own times, not the line's pace alone: a quick word gets
/// a shorter entrance, so the next never cuts it off, and a quick run (words under <see cref="PortalGeometry.QuickGap"/>
/// apart) shares one burst. Arrays of one value per run; reused frame to frame.
/// </summary>
public sealed class PortalMotion
{
    public PortalMotion(int count)
    {
        var n = Math.Max(1, count);
        Appear = new double[n]; Settle = new double[n]; Lift = new double[n]; Burst = new double[n]; Handover = new double[n];
        Ranks = new double[n]; Live = new double[n]; Drift = new double[n]; Pace = new double[n];
        Head = new int[n]; RunEnd = new int[n];
        Count = count;
    }

    public int Count { get; }
    /// <summary>0…1: the pill fading in, mostly done by the word's onset.</summary>
    public double[] Appear { get; }
    /// <summary>0…1: the entrance (travel, tilt, outline) easing to rest.</summary>
    public double[] Settle { get; }
    /// <summary>0…1: how far the words before it have climbed out of its way.</summary>
    public double[] Lift { get; }
    /// <summary>0…1: the burst swelling off the pill (its quick run's).</summary>
    public double[] Burst { get; }
    /// <summary>0…1: the word taking the pill over from the one before it (quicker than its settle after a quick word).</summary>
    public double[] Handover { get; }
    /// <summary>How far back each word is: the later words' hand-overs, summed.</summary>
    public double[] Ranks { get; }
    /// <summary>1 while the word is the one sung, easing to 0 as the next takes over.</summary>
    public double[] Live { get; }
    /// <summary>0…1: how far a pill travels in (less for a quick word).</summary>
    public double[] Drift { get; }
    /// <summary>1…3: the pace of a word's own landing spring.</summary>
    public double[] Pace { get; }
    /// <summary>The quick run each word belongs to: its first word's index.</summary>
    internal int[] Head { get; }
    /// <summary>The last word of each word's quick run.</summary>
    internal int[] RunEnd { get; }
}

public static class PortalGeometry
{
    /// <summary>The ornaments a word without a picture of its own may carry (the Mac's SF Symbols; see <see cref="SegoeIcons"/>).</summary>
    public static readonly string[] Ornaments = ["arrow.up.right", "arrow.right", "sparkle", "play.fill", "plus", "bolt.fill", "star.fill"];

    /// <summary>A pill starts to show this long before its word's onset.</summary>
    public const double AppearLead = 0.06;

    /// <summary>Words closer together than this share one burst.</summary>
    public const double QuickGap = 0.16;

    /// <summary>The shortest and longest entrance, in seconds.</summary>
    public const double SettleMin = 0.14, SettleMax = 0.6;

    /// <summary>A word longer than this (in characters) is not one word: it is set as two.</summary>
    public const int LongestWord = 24;

    /// <summary>
    /// Portal's words: tokens glued into one word are one pill, however many syllables (beau·ti·ful, un·for·get·ta·ble,
    /// 기·억·할·게), their syllables revealed inside it as they are sung. A line written without spaces (Japanese,
    /// Chinese, Thai), glued as one run, is parted into its words (<see cref="LyricRuns.SplittingWords"/>), each a pill.
    /// </summary>
    public static List<StageRun> Runs(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, IWordBreaker breaker)
    {
        var padded = new bool[words.Count];
        for (var i = 0; i < padded.Length && i < glue.Count; i++) padded[i] = glue[i];
        var parted = LyricRuns.SplittingWords(words, padded, breaker, LyricRuns.Parting.Words);
        var runs = new List<StageRun>();
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var text = word.Text.Trim();
            if (text.Length == 0) continue;
            var part = new StageRun.Part(text, word.Start, new TimedWordKey(word));
            if (index > 0 && parted[index] && runs.Count > 0
                && TextElements.Count(runs[^1].Text) + TextElements.Count(text) <= LongestWord)
            {
                var last = runs[^1];
                runs[^1] = last with { Text = last.Text + text, End = Math.Max(last.End, word.End), Parts = [.. last.Parts, part] };
            }
            else
            {
                runs.Add(new StageRun(runs.Count, text, word.Start, Math.Max(word.End, word.Start), [part]));
            }
        }
        return runs;
    }

    /// <summary>
    /// The glue Portal's runs are made by: true where a word joins the pill before it (as <see cref="Runs"/> joins them),
    /// so readings pair with the pills the layer draws.
    /// </summary>
    public static bool[] Glue(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, IWordBreaker breaker)
    {
        var padded = new bool[words.Count];
        for (var i = 0; i < padded.Length && i < glue.Count; i++) padded[i] = glue[i];
        var parted = LyricRuns.SplittingWords(words, padded, breaker, LyricRuns.Parting.Words);
        var result = new bool[words.Count];
        string? last = null;
        for (var index = 0; index < words.Count; index++)
        {
            var text = words[index].Text.Trim();
            if (text.Length == 0) { result[index] = index > 0; continue; }
            if (index > 0 && parted[index] && last is not null && TextElements.Count(last) + TextElements.Count(text) <= LongestWord)
            {
                result[index] = true;
                last += text;
            }
            else
            {
                last = text;
            }
        }
        return result;
    }

    /// <summary>How long word <paramref name="index"/>'s entrance takes: most of the time until the next word.</summary>
    public static double SettleTime(IReadOnlyList<StageRun> runs, int index)
    {
        var next = index + 1 < runs.Count ? runs[index + 1].Start - runs[index].Start : 0.8;
        return Math.Min(SettleMax, Math.Max(SettleMin, 0.85 * next));
    }

    /// <summary>Every pill's motion at <paramref name="time"/>, worked out over the whole line.</summary>
    public static PortalMotion Motion(IReadOnlyList<StageRun> runs, double time, bool reduceMotion)
    {
        var motion = new PortalMotion(runs.Count);
        Motion(runs, time, reduceMotion, motion);
        return motion;
    }

    /// <summary><see cref="Motion(IReadOnlyList{StageRun}, double, bool)"/> into <paramref name="motion"/> (allocation-free).</summary>
    public static void Motion(IReadOnlyList<StageRun> runs, double time, bool reduceMotion, PortalMotion motion)
    {
        var count = Math.Min(runs.Count, motion.Count);
        var head = motion.Head;
        var runEnd = motion.RunEnd;
        for (var index = 0; index < count; index++)
            head[index] = index > 0 && runs[index].Start - runs[index - 1].Start < QuickGap ? head[index - 1] : index;
        for (var index = count - 1; index >= 0; index--)
        {
            var joinsNext = index + 1 < count && head[index + 1] == head[index];
            runEnd[index] = joinsNext ? runEnd[index + 1] : index;
        }
        for (var index = 0; index < count; index++)
        {
            var start = runs[index].Start;
            var settleTime = SettleTime(runs, index);
            var previousGap = index > 0 ? start - runs[index - 1].Start : 0.6;
            var liftTime = Math.Min(0.36, Math.Max(0.08, 0.6 * Math.Min(settleTime, previousGap)));
            if (reduceMotion)
            {
                var on = time >= start - StageMotion.Lead ? 1.0 : 0.0;
                motion.Appear[index] = on; motion.Settle[index] = on; motion.Lift[index] = on; motion.Burst[index] = 1;
                motion.Handover[index] = on;
            }
            else
            {
                // Mostly in by the onset (0.84 there), whatever the pace.
                motion.Appear[index] = Easing.Smoothstep((time - (start - AppearLead)) / 0.08);
                motion.Settle[index] = StageShared.Glide((time - (start - 0.05)) / settleTime);
                var handoverTime = Math.Min(settleTime, Math.Max(SettleMin, 2 * previousGap));
                motion.Handover[index] = StageShared.Glide((time - (start - 0.05)) / handoverTime);
                // The trail has climbed out of the way just before the new pill shows, so it never lands on the one before.
                var liftEnd = start - AppearLead + 0.025;
                motion.Lift[index] = StageShared.Glide((time - (liftEnd - liftTime)) / liftTime);
                // One burst for a quick run: from its first word's onset to the end of its last word's entrance.
                var first = runs[head[index]].Start;
                var last = runEnd[index];
                var span = runs[last].Start - first + SettleTime(runs, last);
                motion.Burst[index] = Math.Min(1, Math.Max(0, (time - (first - 0.05)) / span));
            }
            motion.Drift[index] = Math.Min(1, settleTime / 0.5);
            motion.Pace[index] = Math.Min(3, Math.Max(1, 0.6 / settleTime));
        }
        double after = 0;
        for (var index = count - 1; index >= 0; index--)
        {
            motion.Ranks[index] = after;
            after += motion.Handover[index];
        }
        for (var index = 0; index < count; index++) motion.Live[index] = StageMotion.Liveness(motion.Ranks[index]);
    }

    /// <summary>
    /// How far above the live pill's place each pill has climbed: every later pill, as its trail lifts, raises it by
    /// half of each of the two neighbours' shown heights and a gap.
    /// </summary>
    public static void Climb(ReadOnlySpan<double> heights, ReadOnlySpan<double> lifts, double gap, Span<double> climb)
    {
        double above = 0;
        for (var index = heights.Length - 1; index >= 0; index--)
        {
            climb[index] = above;
            if (index > 0) above += lifts[index] * ((heights[index] + heights[index - 1]) / 2 + gap);
        }
    }

    public static double[] Climb(IReadOnlyList<double> heights, IReadOnlyList<double> lifts, double gap)
    {
        var climb = new double[heights.Count];
        Climb(heights.ToArray(), lifts.Take(heights.Count).ToArray(), gap, climb);
        return climb;
    }

    /// <summary>
    /// The symbol in each pill, or null. A word with a picture of its own (<see cref="LyricWordIcons"/>) always shows
    /// it. Other words may carry an ornament, for variety only: about every other one, never a function word, never an
    /// ornament shown on any of the three pills before. <paramref name="pick"/> is a steady 0…1 for a word index (−1 for
    /// the line), so a line looks the same each time it plays.
    /// </summary>
    public static string?[] Icons(IReadOnlyList<string> texts, Func<int, double> pick)
    {
        var icons = new string?[texts.Count];
        var gives = pick(-1) < 0.5;
        Span<int> choices = stackalloc int[Ornaments.Length];
        for (var index = 0; index < texts.Count; index++)
        {
            var text = texts[index];
            if (LyricWordIcons.Symbol(text) is { } symbol)
            {
                icons[index] = symbol;
                continue;
            }
            if (BroadsideGeometry.IsLittleWord(text)) continue;
            var giving = gives;
            gives = !gives;
            if (!giving) continue;
            var count = 0;
            for (var k = 0; k < Ornaments.Length; k++)
            {
                var recent = false;
                for (var back = 1; back <= 3 && index - back >= 0; back++) recent |= icons[index - back] == Ornaments[k];
                if (!recent) choices[count++] = k;
            }
            if (count == 0) continue;
            var slot = (int)(pick(index) * count) % count;
            icons[index] = Ornaments[choices[slot]];
        }
        return icons;
    }

    /// <summary>The pills' shapes, taken in turn so no two pills in a row match.</summary>
    public static PortalShape[] Shapes(int count, Func<int, double> pick)
    {
        const int all = 5;
        var shapes = new PortalShape[Math.Max(0, count)];
        for (var index = 0; index < shapes.Length; index++)
        {
            if (index == 0)
            {
                shapes[index] = (PortalShape)((int)(pick(index) * all) % all);
                continue;
            }
            // Any shape but the last one.
            var step = 1 + (int)(pick(index) * (all - 1)) % (all - 1);
            shapes[index] = (PortalShape)(((int)shapes[index - 1] + step) % all);
        }
        return shapes;
    }

    /// <summary>A pill's shown size against its resting size: older pills are smaller.</summary>
    public static double Scale(double rank) => Math.Max(0.4, 1 - 0.4 * Math.Min(rank, 1.5));

    /// <summary>
    /// <paramref name="centre"/> moved, if it must be, so a pill measured at <paramref name="size"/> and shown at
    /// <paramref name="scale"/> stays inside <paramref name="bounds"/>, with a little room for its squash and tilt. A
    /// pill too big for the stage is centred on it.
    /// </summary>
    public static PointD KeepOnStage(PointD centre, SizeD size, double scale, StageRect bounds)
    {
        double halfWidth = size.Width * scale * 0.54, halfHeight = size.Height * scale * 0.54;
        static double Clamp(double value, double low, double high) => low > high ? (low + high) / 2 : Math.Min(Math.Max(value, low), high);
        return new PointD(Clamp(centre.X, bounds.MinX + halfWidth, bounds.MaxX - halfWidth),
                          Clamp(centre.Y, bounds.MinY + halfHeight, bounds.MaxY - halfHeight));
    }

    /// <summary>The usual corner radius of <paramref name="shape"/> for a word set at <paramref name="size"/> (a capsule's is clamped to half its height).</summary>
    public static double Radius(PortalShape shape, double size) => shape switch
    {
        PortalShape.Capsule or PortalShape.Slant => size * 2,
        PortalShape.Rounded or PortalShape.Speech => size * 0.45,
        _ => size * 0.18,
    };
}
