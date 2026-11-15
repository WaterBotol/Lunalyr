// Ported from the Mac app's StageLyricStyles.swift (the shared stage helpers) and StageLayouts/BroadsideLayout.swift
// (Build 88; Star4, SketchyOutline): Lunalyr's own code, no line from the Verso import (git blame -w -M -C at
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
    /// focus), the swipes, and everything under Reduce Motion. The chosen entrances (the cutouts too) move exactly as in
    /// every other layout.
    /// </summary>
    public static EntranceTransform? ChosenEntrance(WordArrival arrival, double progress, double size, double side, bool reduceMotion)
    {
        if (reduceMotion) return null;
        return arrival switch
        {
            WordArrival.Lift or WordArrival.Pop or WordArrival.Orbit or WordArrival.Glide or WordArrival.Unfold
                or WordArrival.CutoutFold or WordArrival.CutoutSlide =>
                EntranceTransform.Make(Math.Min(Math.Max(progress, 0), 1), progress, arrival, size, side, false),
            _ => null,
        };
    }

    /// <summary>Grapheme clusters (Swift's <c>String.count</c>).</summary>
    public static int Count(string text) => TextElements.Count(text);
}

/// <summary>
/// Broadside (the Mac's BroadsideLayout.swift, Build 88; Lunalyr's own code, ported as it is): one strip of big type
/// sliding along, the little words riding on the shoulder of the word before as paper labels. Words that slide toward an
/// edge of the stage fade out whole rather than being cut off mid-letter.
/// </summary>
public static class BroadsideGeometry
{
    /// <summary>The little words ("the", "of", "and"...) Broadside sets as labels and Portal never ornaments.</summary>
    public static readonly HashSet<string> Little =
    [
        "a", "an", "the", "of", "to", "in", "on", "at", "by", "for", "and", "or", "but", "so", "as", "if", "is", "it",
        "my", "me", "we", "i", "i'm", "i'll", "you", "be", "up", "us", "no", "oh", "can", "can't", "do", "did", "don't",
        "under", "over", "with", "from", "into", "out", "off", "all", "are", "was", "im", "ill",
    ];

    /// <summary>Whether <paramref name="text"/>, lowercased and trimmed of punctuation, is one of <see cref="Little"/> (any length).</summary>
    public static bool IsLittleWord(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.ToLowerInvariant().AsSpan();
        while (trimmed.Length > 0 && char.IsPunctuation(trimmed[0])) trimmed = trimmed[1..];
        while (trimmed.Length > 0 && char.IsPunctuation(trimmed[^1])) trimmed = trimmed[..^1];
        return Little.Contains(trimmed.ToString());
    }

    /// <summary>A little word ("the", "of"): one of the list, trimmed of punctuation, and no longer than five characters.</summary>
    public static bool IsLittle(string text) => IsLittleWord(text) && StageShared.Count(text) <= 5;

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

    /// <summary>A word fades out, whole, as its box nears an edge of the stage this far in (a share of the stage's width).</summary>
    public const double Margin = 0.05;
    /// <summary>The distance (in type units) over which a word fades as it nears the margin.</summary>
    public const double FadeSpan = 0.7;

    /// <summary>
    /// Where Broadside puts each word's centre: the big words in one row, a little word as a small paper label riding on
    /// the shoulder of the big word before it (above its letters, flush with its end, never on top of them), and the
    /// strip glided so the line builds up centred until it is wider than the stage; after that the newest big word stays
    /// near the right, the line read back to its left. A label does not move the strip, so the word it rides on stays in
    /// view with it. <paramref name="slots"/> is the room each big word's reading takes under it (0 for none): the word
    /// itself is what sits on the strip's line, its reading below.
    /// </summary>
    public static void Places(ReadOnlySpan<SizeD> measured, ReadOnlySpan<bool> little, ReadOnlySpan<double> slots,
                              ReadOnlySpan<double> moves, double unit, StageRect bounds, Span<PointD> places)
    {
        var n = measured.Length;
        if (n == 0) return;
        static double Slot(int index, ReadOnlySpan<double> s) => index < s.Length ? s[index] : 0;
        static bool IsLabel(int index, ReadOnlySpan<bool> l) => index < l.Length && l[index];
        double cursor = 0;
        var previousBig = -1;
        for (var index = 0; index < n; index++)
        {
            var width = measured[index].Width;
            if (IsLabel(index, little) && previousBig >= 0)
            {
                // On the word's shoulder: just clear of the top of its letters.
                var wordHeight = measured[previousBig].Height - Slot(previousBig, slots);
                places[index] = new PointD(places[previousBig].X + measured[previousBig].Width / 2 - width / 2,
                                           bounds.MidY - wordHeight * 0.36 - unit * 0.06 - measured[index].Height / 2);
                continue;
            }
            places[index] = new PointD(cursor + width / 2, bounds.MidY + Slot(index, slots) / 2);
            cursor += width + unit * 0.45;
            previousBig = index;
        }
        // Where the strip stands once each big word has arrived: the words so far centred while they fit, then the
        // newest word's end held clear of the right edge, where the edge fade starts. A label keeps its word's stop.
        var margin = bounds.Width * Margin + unit * FadeSpan + unit * 0.1;
        var firstLeft = places[0].X - measured[0].Width / 2;
        double camera = 0, previousStop = 0;
        for (var index = 0; index < n; index++)
        {
            double stop;
            if (IsLabel(index, little) && index > 0)
            {
                stop = previousStop;
            }
            else
            {
                var half = measured[index].Width / 2;
                var row = places[index].X + half - firstLeft;
                var shift = Math.Min(Math.Max(0, row / 2 - half), Math.Max(0, bounds.Width / 2 - margin - half));
                stop = places[index].X - shift;
            }
            camera = index == 0 ? stop : camera + (index < moves.Length ? moves[index] : 1) * (stop - previousStop);
            previousStop = stop;
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

    /// <summary>
    /// How visible a word is from where its box sits: 1 well inside the stage, fading to 0 as either end reaches the
    /// margin. <paramref name="lead"/> widens the box to the left: a label passes it the width of the word it rides on
    /// (estimated), so it leaves with that word, not after it.
    /// </summary>
    public static double EdgeFade(StageRect frame, double stageWidth, double unit, double lead = 0)
    {
        var minX = frame.MinX - lead;
        var margin = stageWidth * Margin;
        var span = Math.Max(unit * FadeSpan, 1);
        return Easing.Smoothstep((minX - margin) / span) * Easing.Smoothstep((stageWidth - margin - frame.MaxX) / span);
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
