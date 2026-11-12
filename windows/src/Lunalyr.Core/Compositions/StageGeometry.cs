// Ported from the Mac app's StageLyricStyles.swift (StageLyricView's static geometry for Hinge, Broadside, Portal,
// Mosaic and Torque; Star4, SketchyOutline): Lunalyr's own code, no line from the Verso import (git blame -w -M -C at
// next/integration), ported as it is. Units are whatever the caller measures in (pixels on Windows); the Mac's are
// points. The per-frame functions write into spans and allocate nothing.

using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>A rectangle (top-left origin).</summary>
public readonly record struct StageRect(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
    public SizeD Size => new(Width, Height);

    public StageRect Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    public StageRect Union(StageRect other)
    {
        double x = Math.Min(MinX, other.MinX), y = Math.Min(MinY, other.MinY);
        return new StageRect(x, y, Math.Max(MaxX, other.MaxX) - x, Math.Max(MaxY, other.MaxY) - y);
    }

    public StageRect Inset(double dx, double dy) => new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);

    public bool Contains(StageRect other) =>
        other.MinX >= MinX && other.MinY >= MinY && other.MaxX <= MaxX && other.MaxY <= MaxY;

    public bool Intersects(StageRect other) =>
        Width > 0 && Height > 0 && other.Width > 0 && other.Height > 0
        && other.MinX < MaxX && MinX < other.MaxX && other.MinY < MaxY && MinY < other.MaxY;
}

/// <summary>The compositions that set the whole stage themselves (the Mac's <c>fillsStage</c>).</summary>
public static class StageCompositions
{
    /// <summary>Hinge, Broadside, Portal, Mosaic, Torque and Fable: drawn by the stage view, one line at a time.</summary>
    public static bool FillsStage(this CompositionKind kind) => kind is CompositionKind.Hinge or CompositionKind.Broadside
        or CompositionKind.Portal or CompositionKind.Mosaic or CompositionKind.Torque or CompositionKind.Fable;
}

/// <summary>What every stage layout shares: per-line chance, the line's pace, readings' sizes and chosen entrances.</summary>
public static class StageShared
{
    /// <summary>
    /// A steady 0…1 for a line (<paramref name="word"/> null) or one of its words, and a salt: the same value for the
    /// same line every time (the Mac's <c>chance</c>).
    /// </summary>
    public static double Chance(long seed, int? word, int salt)
    {
        var mixed = unchecked(Math.Abs(seed == long.MinValue ? 0 : seed) * 2654435761L + (word ?? 977) * 40503L + salt * 9973L);
        var value = unchecked((ulong)mixed);
        value ^= value >> 13;
        value = unchecked(value * 0x2545F4914F6CDD1DUL);
        value ^= value >> 29;
        return value % 10_000 / 10_000.0;
    }

    /// <summary>
    /// 1 for an ordinary pace, up to 3 when the line is sung fast (the median gap between onsets, over 0.02 s, is under
    /// half a second): every motion is shortened and calmed by it so the layout keeps up with the voice.
    /// </summary>
    public static double LineSpeed(IReadOnlyList<double> starts)
    {
        ArgumentNullException.ThrowIfNull(starts);
        var sorted = starts.Order().ToArray();
        var gaps = new List<double>();
        for (var i = 1; i < sorted.Length; i++)
            if (sorted[i] - sorted[i - 1] > 0.02) gaps.Add(sorted[i] - sorted[i - 1]);
        if (gaps.Count == 0) return 1;
        gaps.Sort();
        var median = gaps[gaps.Count / 2];
        return Math.Min(3, Math.Max(1, 0.5 / Math.Max(median, 0.1)));
    }

    /// <summary>Eases 0…1 with no overshoot (smootherstep): the camera and the soft arrivals use this.</summary>
    public static double Glide(double x)
    {
        var t = Math.Min(Math.Max(x, 0), 1);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    /// <summary>A reading's size against its word's: between Fable's (0.36) and the rolling layouts' (0.56).</summary>
    public const double ReadingRatio = 0.45;

    /// <summary>The type size of the reading under a word set at <paramref name="size"/>: never below 12 pt, unless the word itself is about that small.</summary>
    public static double ReadingSize(double size, double point = 1) => Math.Max(size * ReadingRatio, Math.Min(12 * point, size * 0.9));

    /// <summary>The gap between a word and its reading.</summary>
    public static double ReadingGap(double size) => size * 0.04;

    /// <summary>The room a reading takes under a word set at <paramref name="size"/>: the gap and one line.</summary>
    public static double ReadingSlot(double size, double? gap = null, double point = 1) =>
        (gap ?? ReadingGap(size)) + ReadingSize(size, point) * 1.25;

    /// <summary>
    /// The pose a chosen entrance gives a word, or null when the layout's own motion stays: the layout's default (Soft
    /// focus), and everything under Reduce Motion. The chosen entrances move exactly as in every other layout.
    /// </summary>
    public static EntranceTransform? ChosenEntrance(WordArrival arrival, double progress, double size, double side, bool reduceMotion)
    {
        if (reduceMotion) return null;
        return arrival switch
        {
            WordArrival.Lift or WordArrival.Pop or WordArrival.Orbit or WordArrival.Glide or WordArrival.Unfold =>
                EntranceTransform.Make(Math.Min(Math.Max(progress, 0), 1), progress, arrival, size, side, false),
            _ => null,
        };
    }

    /// <summary>Grapheme clusters (Swift's <c>String.count</c>).</summary>
    public static int Count(string text) => TextElements.Count(text);
}

/// <summary>Hinge: the corner of a cube seen from a slant, a pair, a bar on the left face and a column down the right.</summary>
public static class HingeGeometry
{
    /// <summary>How much more room than its own height a reading takes in Hinge's column and pair.</summary>
    public const double ReadingRoom = 1.4;

    /// <summary>A column word's type size (the column's j-th word, at rest).</summary>
    public static double ColumnFont(int j, double unit) => unit * Math.Min(2.8, 1.2 + 0.24 * j);

    /// <summary>The size the pair sizes its readings from: larger than the pair's own type.</summary>
    public static double PairReadingBase(double unit) => unit * 0.95 * 1.4;

    /// <summary>
    /// How much the whole corner is scaled to fit the stage, and how much its column has shrunk. Every word counts by
    /// its weight, so neither steps as a word is added. <paramref name="readingCounts"/> holds each word's reading's
    /// length (0 for none; shorter than the words when there are none).
    /// </summary>
    public static (double Fit, double Shrink) Fit(ReadOnlySpan<int> textCounts, ReadOnlySpan<double> weights, double settle,
                                                  double unit, SizeD stage, ReadOnlySpan<int> readingCounts = default,
                                                  double point = 1)
    {
        var u = unit;
        var n = Math.Min(textCounts.Length, weights.Length);
        var columnCount = Math.Max(0, n - 3);
        double arrived = 0;
        for (var j = 0; j < columnCount; j++) arrived += weights[j + 3];
        var shrink = StageMotion.HingeColumnShrink(arrived);
        (double Height, double Width) Room(int index, double font, ReadOnlySpan<int> readings)
        {
            if (index >= readings.Length || readings[index] <= 0) return (0, 0);
            return (StageShared.ReadingSlot(font, point: point), readings[index] * StageShared.ReadingSize(font, point) * 0.55);
        }
        double heightSum = 0;
        for (var j = 0; j < columnCount; j++)
        {
            var font = ColumnFont(j, u);
            heightSum += (font * 0.95 + Room(j + 3, font, readingCounts).Height * ReadingRoom) * shrink * weights[j + 3];
        }
        var barReading = textCounts.Length > 2 ? Room(2, u * 1.6, readingCounts) : (0, 0);
        var barWidth = textCounts.Length > 2 ? Math.Max(textCounts[2] * u * 1.7 * 0.66, barReading.Width) : 0;
        double columnWidth = 0;
        for (var j = 0; j < columnCount; j++)
        {
            var font = ColumnFont(j, u);
            var word = textCounts[j + 3] * font * 0.66;
            columnWidth = Math.Max(columnWidth, Math.Max(word, Room(j + 3, font, readingCounts).Width) * shrink * 0.84 * weights[j + 3]);
        }
        var widest = Math.Max(barWidth, columnWidth);
        var structureWidth = Math.Max(2 * widest + u * 0.3, u * 3);
        double pairReadings = 0;
        for (var i = 0; i < Math.Min(2, textCounts.Length); i++)
            pairReadings += Room(i, PairReadingBase(u), readingCounts).Height * ReadingRoom * 0.6;
        var structureHeight = Math.Max(heightSum, u * 2.4 + barReading.Item1) + u * 0.8 + pairReadings;
        var fill = Math.Min(2.0, Math.Min(stage.Width * 0.8 / structureWidth, stage.Height * 0.78 / structureHeight));
        var fitHeight = Math.Min(1, stage.Height * 0.74 / Math.Max(heightSum + u * 0.8 + pairReadings, 1));
        var grow = (Math.Max(fill, 1) - 1) * settle;
        return (fitHeight * (1 + grow), shrink);
    }

    /// <summary><see cref="Fit(ReadOnlySpan{int}, ReadOnlySpan{double}, double, double, SizeD, ReadOnlySpan{int}, double)"/> from the words' texts and readings.</summary>
    public static (double Fit, double Shrink) Fit(IReadOnlyList<string> texts, IReadOnlyList<double> weights, double settle,
                                                  double unit, SizeD stage, IReadOnlyList<string?>? readings = null)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(weights);
        var counts = texts.Select(StageShared.Count).ToArray();
        var reading = (readings ?? []).Select(r => string.IsNullOrEmpty(r) ? 0 : StageShared.Count(r)).ToArray();
        return Fit(counts, weights.ToArray(), settle, unit, stage, reading);
    }
}

/// <summary>Broadside: one strip of big type sliding along, the little words laid across it as paper labels.</summary>
public static class BroadsideGeometry
{
    private static readonly HashSet<string> Little =
    [
        "a", "an", "the", "of", "to", "in", "on", "at", "by", "for", "and", "or", "but", "so", "as", "if", "is", "it",
        "my", "me", "we", "i", "i'm", "i'll", "you", "be", "up", "us", "no", "oh", "can", "can't", "do", "did", "don't",
        "under", "over", "with", "from", "into", "out", "off", "all", "are", "was", "im", "ill",
    ];

    /// <summary>A little word ("the", "of"): one of the list, trimmed of punctuation, and no longer than five characters.</summary>
    public static bool IsLittle(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.ToLowerInvariant().AsSpan();
        while (trimmed.Length > 0 && char.IsPunctuation(trimmed[0])) trimmed = trimmed[1..];
        while (trimmed.Length > 0 && char.IsPunctuation(trimmed[^1])) trimmed = trimmed[..^1];
        return Little.Contains(trimmed.ToString()) && StageShared.Count(text) <= 5;
    }

    /// <summary>Which words become labels: few, well apart; one only after a big word, and not within three words of the last.</summary>
    public static bool[] Labels(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var candidates = texts.Select(IsLittle).ToArray();
        var little = new bool[texts.Count];
        var since = 99;
        for (var i = 0; i < texts.Count; i++)
        {
            if (candidates[i] && i > 0 && !candidates[i - 1] && since >= 3) { little[i] = true; since = 0; }
            else since++;
        }
        return little;
    }

    /// <summary>
    /// How much of each word is kept in the strip: words more than a couple of big words behind drop out, by the
    /// hand-offs (<paramref name="moves"/>) of the big words after their own.
    /// </summary>
    public static void Keep(ReadOnlySpan<bool> little, ReadOnlySpan<double> moves, Span<double> keep)
    {
        var count = Math.Min(little.Length, moves.Length);
        var lastBig = 0;
        for (var index = 0; index < count; index++)
        {
            if (!little[index]) lastBig = index;
            double after = 0;
            for (var k = lastBig + 1; k < count; k++)
                if (!little[k]) after += moves[k];
            keep[index] = Math.Max(0, Math.Min(1, 1 - (after - 1.6) * 0.9));
        }
    }

    /// <summary>
    /// Where Broadside puts each word's centre: big words in a row, the strip glided so the newest big word reaches the
    /// centre; the first little word after a big one lies across it. <paramref name="slots"/> is the room each big
    /// word's reading takes under it (0 for none): the word itself sits on the strip's line, its reading below.
    /// </summary>
    public static void Places(ReadOnlySpan<SizeD> measured, ReadOnlySpan<bool> little, ReadOnlySpan<double> slots,
                              ReadOnlySpan<double> moves, double unit, StageRect bounds, Span<PointD> places)
    {
        var n = measured.Length;
        double Slot(int index, ReadOnlySpan<double> s) => index < s.Length ? s[index] : 0;
        double cursor = 0;
        var previousBig = -1;
        var littlesSinceBig = 0;
        // xs and ys go into places; the camera is summed over the big words in order.
        for (var index = 0; index < n; index++)
        {
            if (little[index] && previousBig >= 0 && littlesSinceBig == 0)
            {
                places[index] = new PointD(places[previousBig].X + measured[previousBig].Width * 0.2,
                                           bounds.MidY + (measured[previousBig].Height - Slot(previousBig, slots)) * 0.2);
                littlesSinceBig++;
                continue;
            }
            var width = measured[index].Width;
            places[index] = new PointD(cursor + width / 2, bounds.MidY + Slot(index, slots) / 2);
            cursor += width + (little[index] ? unit * 0.2 : unit * 0.45);
            if (little[index]) littlesSinceBig++;
            else { previousBig = index; littlesSinceBig = 0; }
        }
        double camera = 0;
        var priorBig = -1;
        for (var index = 0; index < n; index++)
        {
            if (little[index]) continue;
            camera = priorBig < 0 ? places[index].X : camera + moves[index] * (places[index].X - places[priorBig].X);
            priorBig = index;
        }
        for (var index = 0; index < n; index++)
            places[index] = new PointD(places[index].X - camera + bounds.MidX, places[index].Y);
    }

    /// <summary><see cref="Places(ReadOnlySpan{SizeD}, ReadOnlySpan{bool}, ReadOnlySpan{double}, ReadOnlySpan{double}, double, StageRect, Span{PointD})"/> into a new array.</summary>
    public static PointD[] Places(IReadOnlyList<SizeD> measured, IReadOnlyList<bool> little, IReadOnlyList<double> slots,
                                  IReadOnlyList<double> moves, double unit, StageRect bounds)
    {
        ArgumentNullException.ThrowIfNull(measured);
        var places = new PointD[measured.Count];
        Places(measured.ToArray(), little.ToArray(), slots.ToArray(), moves.ToArray(), unit, bounds, places);
        return places;
    }
}

/// <summary>Portal: the sung word in a pill with a badge; earlier words drift up a trail as paler, smaller pills.</summary>
public static class PortalGeometry
{
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

    /// <summary>The badges a line's pills may carry (the Mac's SF Symbols, drawn as shapes here).</summary>
    public enum Badge { ArrowUpRight, ArrowRight, Sparkle, Play, Plus, Bolt }

    /// <summary>One line's trail: which way it runs, where its pills sit, and its badge.</summary>
    public readonly record struct Trail(PointD P0, PointD P1, PointD P2, PointD P3, double ShiftX, double Lift, Badge Badge)
    {
        /// <summary>Where a pill of <paramref name="rank"/> sits along the trail, from the stage's centre.</summary>
        public PointD Along(double rank)
        {
            var capped = Math.Min(Math.Max(rank, 0), 3);
            var low = (int)Math.Floor(capped);
            var high = Math.Min(low + 1, 3);
            var f = StageShared.Glide(capped - low);
            PointD a = Point(low), b = Point(high);
            return new PointD(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
        }

        private PointD Point(int index) => index switch { 0 => P0, 1 => P1, 2 => P2, _ => P3 };
    }

    /// <summary>Line <paramref name="seed"/>'s trail on a stage of <paramref name="stage"/>, at its pace.</summary>
    public static Trail TrailFor(long seed, SizeD stage, double speed)
    {
        var flipX = StageShared.Chance(seed, null, 1) < 0.5 ? 1.0 : -1.0;
        var flipY = StageShared.Chance(seed, null, 2) < 0.7 ? 1.0 : -1.0;
        var reach = (0.8 + 0.5 * StageShared.Chance(seed, null, 3)) / Math.Sqrt(speed);
        var shiftX = (StageShared.Chance(seed, null, 4) - 0.5) * 0.24 * stage.Width;
        var lift = stage.Height * (flipY > 0 ? 0.1 + 0.1 * StageShared.Chance(seed, null, 5) : -0.1 - 0.08 * StageShared.Chance(seed, null, 5));
        var badge = (Badge)((int)(StageShared.Chance(seed, null, 6) * 6) % 6);
        return new Trail(
            new PointD(0, 0),
            new PointD(-0.1 * stage.Width * flipX * reach, -0.2 * stage.Height * flipY * reach),
            new PointD(0.11 * stage.Width * flipX * reach, -0.3 * stage.Height * flipY * reach),
            new PointD(-0.04 * stage.Width * flipX * reach, -0.38 * stage.Height * flipY * reach),
            shiftX, lift, badge);
    }
}

/// <summary>Mosaic: each word set a different way as it arrives, shrinking into a small stack above.</summary>
public static class MosaicGeometry
{
    /// <summary>The size the small stack sets its words at.</summary>
    public static double SmallSize(double unit) => unit * 0.6;

    /// <summary>How far apart the stack sets its rows: a word, and its reading when the line has readings.</summary>
    public static double StackStep(double unit, bool readings, double point = 1) =>
        unit * 0.85 + (readings ? StageShared.ReadingSlot(SmallSize(unit), point: point) : 0);

    /// <summary>One continuous path for word <paramref name="index"/>: where it landed, gliding into the stack by its rank.</summary>
    public static PointD Place(int index, double rank, long seed, double step, StageRect bounds)
    {
        var settle = StageShared.Glide(Math.Min(rank, 1.2) / 1.2);
        var sway = (StageShared.Chance(seed, index, 21) - 0.5) * 0.24 * bounds.Width;
        var home = new PointD(bounds.MidX + sway, bounds.MidY + (StageShared.Chance(seed, index, 22) - 0.5) * 0.16 * bounds.Height);
        var slot = Math.Min(3, Math.Max(rank, 1));
        var stack = new PointD(bounds.MidX - bounds.Width * 0.2 + index % 2 * bounds.Width * 0.04,
                               bounds.MinY + bounds.Height * 0.12 + (slot - 1) * step);
        return new PointD(home.X + (stack.X - home.X) * settle, home.Y + (stack.Y - home.Y) * settle);
    }
}

/// <summary>Torque's state for every shown word at one moment.</summary>
/// <param name="Arrivals">Each word's arrival spring.</param>
/// <param name="Ranks">How far each word has been pushed back.</param>
/// <param name="Scales">Shown size over the resting (newest word's) size.</param>
/// <param name="Weights">The share of its room each word takes in the stack.</param>
/// <param name="Slots">The room each word's reading takes under it, at the resting size (0 for none).</param>
public sealed record TorqueState(double[] Arrivals, double[] Ranks, double[] Scales, double[] Weights)
{
    public double[] Slots { get; set; } = [];
}

/// <summary>Torque: words wrung into place with a thick edge, stacking upwards.</summary>
public static class TorqueGeometry
{
    /// <summary>Torque's words rest at this many units.</summary>
    public static double RestSize(double unit) => unit * 2.0;

    /// <summary>The gap between a word and its reading: clear of the thick edge.</summary>
    public static double ReadingGap(double restSize) => restSize * 0.16;

    /// <summary>The room a reading takes under a Torque word.</summary>
    public static double ReadingSlot(double restSize, double point = 1) =>
        StageShared.ReadingSlot(restSize, ReadingGap(restSize), point);

    /// <summary>A word's shown size over the resting size, from its rank: set at the newest word's size, older words scaled down.</summary>
    public static double ScaleFor(double rank) => Math.Max(0.55, 2.0 - 0.38 * Math.Min(rank, 3.5)) / 2.0;

    /// <summary>The room a word takes: it grows as the word arrives and is given back as the word fades out at the top.</summary>
    public static double WeightFor(double arrival, double rank) => StageMotion.Weight(arrival) * StageMotion.TorqueVisibility(rank);

    /// <summary>Torque's state for the shown runs.</summary>
    public static TorqueState State(IReadOnlyList<StageRun> runs, double time, double speed, long seed, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var n = runs.Count;
        var arrivals = new double[n];
        var ranks = new double[n];
        for (var i = 0; i < n; i++) arrivals[i] = StageMotion.Arrival(runs[i].Start, time, speed, reduceMotion);
        StageMotion.Ranks(runs, n, time, speed, seed, reduceMotion, ranks);
        var scales = ranks.Select(ScaleFor).ToArray();
        var weights = new double[n];
        for (var i = 0; i < n; i++) weights[i] = WeightFor(arrivals[i], ranks[i]);
        return new TorqueState(arrivals, ranks, scales, weights);
    }

    /// <summary>
    /// The room each word takes in the stack. Words overlap their neighbours' leading; a reading takes all its room and
    /// a little more, as the stack sways and turns.
    /// </summary>
    public static void Heights(ReadOnlySpan<SizeD> measured, ReadOnlySpan<double> scales, ReadOnlySpan<double> weights,
                               ReadOnlySpan<double> slots, Span<double> heights)
    {
        for (var i = 0; i < measured.Length; i++)
        {
            var slot = i < slots.Length ? slots[i] : 0;
            heights[i] = ((measured[i].Height - slot) * 0.82 + slot * 1.2) * scales[i] * weights[i];
        }
    }

    public static double[] Heights(IReadOnlyList<SizeD> measured, TorqueState state)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(state);
        var heights = new double[measured.Count];
        Heights(measured.ToArray(), state.Scales, state.Weights, state.Slots, heights);
        return heights;
    }

    /// <summary>The height the stack needs: its words, and room at either end for the overlap and the turn.</summary>
    public static double Height(ReadOnlySpan<double> heights, double restSize)
    {
        double total = 0;
        foreach (var h in heights) total += h;
        return total + restSize * 0.3;
    }

    public static double Height(IReadOnlyList<SizeD> measured, TorqueState state, double restSize) =>
        Height(Heights(measured, state), restSize);

    /// <summary>
    /// Where Torque puts each word's centre. The stack is centred by weight (the newest, biggest word sits lowest), as
    /// far as the stage has room to move it; a stack taller than its bounds keeps its foot in them.
    /// </summary>
    public static void Stack(ReadOnlySpan<double> heights, ReadOnlySpan<double> ranks, long seed, StageRect bounds, Span<PointD> places)
    {
        var n = heights.Length;
        double total = 0;
        for (var i = 0; i < n; i++) total += heights[i];
        var cursor = bounds.MidY + total / 2;
        for (var index = n - 1; index >= 0; index--)
        {
            var height = heights[index];
            var y = cursor - height / 2;
            cursor -= height;
            places[index] = new PointD(bounds.MidX + Math.Sin(index * 2.1 + seed % 5) * bounds.Width * 0.05 * Math.Min(ranks[index], 1), y);
        }
        double weight = 0, centroid = 0;
        for (var i = 0; i < n; i++)
        {
            weight += heights[i] * heights[i];
            centroid += heights[i] * heights[i] * places[i].Y;
        }
        if (total > 0 && weight > 0)
        {
            centroid /= weight;
            var room = (bounds.Height - total) / 2;
            var shift = room >= 0 ? Math.Max(-room, Math.Min(room, bounds.MidY - centroid)) : room;
            for (var i = 0; i < n; i++) places[i] = places[i] with { Y = places[i].Y + shift };
        }
    }

    public static PointD[] Stack(IReadOnlyList<SizeD> measured, TorqueState state, long seed, StageRect bounds)
    {
        var heights = Heights(measured, state);
        var places = new PointD[measured.Count];
        Stack(heights, state.Ranks, seed, bounds, places);
        return places;
    }
}

/// <summary>The stage layouts' shapes: a four-pointed star, a hand-drawn outline and corner brackets.</summary>
public static class StageShapes
{
    /// <summary>The eight corners of a four-pointed star in <paramref name="rect"/> (inner radius 0.28 of the outer).</summary>
    public static void Star4(StageRect rect, Span<PointD> points)
    {
        double cx = rect.MidX, cy = rect.MidY;
        var outer = Math.Min(rect.Width, rect.Height) / 2;
        var inner = outer * 0.28;
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4 - Math.PI / 2;
            var r = i % 2 == 0 ? outer : inner;
            points[i] = new PointD(cx + Math.Cos(angle) * r, cy + Math.Sin(angle) * r);
        }
    }

    /// <summary>The points a hand-drawn outline is drawn through (it is closed with quadratic curves between their midpoints).</summary>
    public const int SketchyPoints = 72;

    /// <summary>
    /// A rounded rectangle drawn the way a hand draws one: <see cref="SketchyPoints"/> points that wander a little off
    /// true, differently each time <paramref name="phase"/> changes. False for a rectangle too small to draw.
    /// </summary>
    public static bool Sketchy(StageRect rect, double corner, double amount, double phase, double salt, Span<PointD> points)
    {
        var radius = Math.Min(corner, Math.Min(rect.Width / 2, rect.Height / 2));
        double across = Math.Max(0, rect.Width - 2 * radius), down = Math.Max(0, rect.Height - 2 * radius);
        var arc = Math.PI / 2 * radius;
        Span<double> lengths = [across, arc, down, arc, across, arc, down, arc];
        double perimeter = 0;
        foreach (var length in lengths) perimeter += length;
        if (!(perimeter > 1)) return false;
        const int count = SketchyPoints;
        for (var i = 0; i < count; i++)
        {
            var along = perimeter * i / count;
            var side = 0;
            while (side < 7 && along > lengths[side]) { along -= lengths[side]; side++; }
            double px, py, nx, ny;
            static void OnArc(double cx, double cy, double start, double along, double radius, out double x, out double y, out double ox, out double oy)
            {
                var angle = start + along / Math.Max(radius, 0.001);
                ox = Math.Cos(angle); oy = Math.Sin(angle);
                x = cx + radius * ox; y = cy + radius * oy;
            }
            switch (side)
            {
                case 0: px = rect.MinX + radius + along; py = rect.MinY; nx = 0; ny = -1; break;
                case 1: OnArc(rect.MaxX - radius, rect.MinY + radius, -Math.PI / 2, along, radius, out px, out py, out nx, out ny); break;
                case 2: px = rect.MaxX; py = rect.MinY + radius + along; nx = 1; ny = 0; break;
                case 3: OnArc(rect.MaxX - radius, rect.MaxY - radius, 0, along, radius, out px, out py, out nx, out ny); break;
                case 4: px = rect.MaxX - radius - along; py = rect.MaxY; nx = 0; ny = 1; break;
                case 5: OnArc(rect.MinX + radius, rect.MaxY - radius, Math.PI / 2, along, radius, out px, out py, out nx, out ny); break;
                case 6: px = rect.MinX; py = rect.MaxY - radius - along; nx = -1; ny = 0; break;
                default: OnArc(rect.MinX + radius, rect.MinY + radius, Math.PI, along, radius, out px, out py, out nx, out ny); break;
            }
            // Low whole-number frequencies, so the loop closes without a seam.
            var t = 2 * Math.PI * i / count;
            var wobble = 0.55 * Math.Sin(3 * t + phase * 1.1 + salt) + 0.3 * Math.Sin(7 * t - phase * 0.8 + salt * 2.1)
                + 0.15 * Math.Sin(11 * t + phase * 1.7);
            var push = amount * wobble;
            points[i] = new PointD(px + nx * push, py + ny * push);
        }
        return true;
    }
}
