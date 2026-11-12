// Ported from the Mac app's StageLyricStyles.swift (FableStyle, FableLayout, FableBrackets): Lunalyr's own code, no
// line from the Verso import (git blame -w -M -C at next/integration), ported as it is.

namespace Lunalyr.Core.Compositions;

/// <summary>The shape a Fable line's words are set in.</summary>
public enum FableShape { Staircase, Arch, Zigzag, Wave, Ramp }

/// <summary>The accent that marks the word being sung in a Fable line.</summary>
public enum FableAccent { Underline, Ring, Tile, Star, Brackets }

/// <summary>
/// One Fable line's look: the shape its words are set in and the accent that marks the word being sung. Chosen by the
/// line, so the same line looks the same on every render, and arranged so neighbouring lines never share a shape or an
/// accent; all 25 pairs come round before any repeats.
/// </summary>
public readonly record struct FableStyle(FableShape Shape, FableAccent Accent)
{
    private const int Shapes = 5;
    private const int Accents = 5;

    public static FableStyle ForLine(long seed)
    {
        // Floor division, so negative seeds keep the pattern too.
        var lap = seed >= 0 ? seed / Shapes : -((-seed + Shapes - 1) / Shapes);
        static long Wrap(long value) => (value % Shapes + Shapes) % Shapes;
        // The accent moves on one more than the shape each lap, so the pairs shift against each other.
        return new FableStyle((FableShape)Wrap(seed), (FableAccent)(Wrap(seed + lap) % Accents));
    }

    /// <summary>The word set larger: the longest, the first of equals. None for a line of one or two words.</summary>
    public static int? HeroIndex(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count < 3) return null;
        var best = 0;
        var bestCount = StageShared.Count(texts[0]);
        for (var i = 1; i < texts.Count; i++)
        {
            var count = StageShared.Count(texts[i]);
            if (count > bestCount) { best = i; bestCount = count; }
        }
        return best;
    }

    /// <summary>
    /// A word's own entrance (one of four), as <paramref name="progress"/> runs 0…1 with a little overshoot: it drops
    /// in, pops up, swings up from its foot, or slides in from <paramref name="side"/>.
    /// </summary>
    public static (double OffsetX, double OffsetY, double Scale, double Spin) Entrance(int kind, double progress, double size, double side)
    {
        var left = 1 - progress;
        return kind switch
        {
            0 => (0, -left * size * 0.5, 1, 0),
            1 => (0, 0, 0.55 + 0.45 * progress, 0),
            2 => (0, 0, 1, left * -16 * side),
            _ => (side * left * size * 0.6, 0, 1, 0),
        };
    }
}

public static class FableShapes
{
    /// <summary>How much of the stage's width a row may take before it wraps.</summary>
    public static double Share(this FableShape shape) => shape switch
    {
        FableShape.Staircase => 0.7,
        FableShape.Arch => 0.9,
        FableShape.Zigzag => 0.75,
        FableShape.Wave => 0.95,
        _ => 0.85,
    };

    /// <summary>Most words in a row, so the shape still reads as one.</summary>
    public static int WordsPerRow(this FableShape shape) => shape switch
    {
        FableShape.Staircase or FableShape.Zigzag => 3,
        FableShape.Ramp => 4,
        FableShape.Arch => 5,
        _ => 6,
    };

    /// <summary>The spread of each word's tilt, in degrees.</summary>
    public static double Tilt(this FableShape shape) => shape switch
    {
        FableShape.Arch => 0,
        FableShape.Ramp => 2,
        _ => 5,
    };
}

/// <summary>
/// Lays a Fable line out in its shape: words in reading order, wrapped into rows, each row bent into the shape, every
/// word in a box of its own that nothing else enters. Reports the block's own size and the scale it is shown at.
/// </summary>
public static class FableLayout
{
    /// <param name="Size">The block's own size.</param>
    /// <param name="Frames">Each word's box, from the block's top-left corner.</param>
    /// <param name="Scale">The scale the block is shown at to fit the stage.</param>
    public sealed record Arrangement(SizeD Size, StageRect[] Frames, double Scale);

    private static readonly double[] Guesses = [1, 0.9, 0.8, 0.7, 0.6, 0.5, 0.42, 0.35, 0.28];

    /// <summary>Tries a few row widths and keeps the one that can be shown largest.</summary>
    public static Arrangement Arrange(IReadOnlyList<SizeD> sizes, FableShape shape, bool rightToLeft, SizeD available,
                                      double gap, double rowGap)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        if (sizes.Count == 0 || !(available.Width > 0) || !(available.Height > 0))
            return new Arrangement(SizeD.Zero, new StageRect[sizes.Count], 1);
        Arrangement? best = null;
        foreach (var guess in Guesses)
        {
            var laid = Build(sizes, shape, available.Width * shape.Share() / guess, gap, rowGap);
            laid = laid with { Scale = LyricStageFit.Scale(laid.Size, available) };
            if (best is null || laid.Scale > best.Scale + 0.001) best = laid;
            if (laid.Scale >= guess - 0.001) break;
        }
        var result = best!;
        if (rightToLeft)
        {
            // Right to left: the same shape seen in a mirror, read from the right.
            var width = result.Size.Width;
            result = result with { Frames = [.. result.Frames.Select(f => f with { X = width - f.MaxX })] };
        }
        return result;
    }

    private static Arrangement Build(IReadOnlyList<SizeD> sizes, FableShape shape, double wrap, double gap, double rowGap)
    {
        // Rows, by width and by count; a very long line allows more words a row rather than more than about four rows.
        var cap = Math.Max(shape.WordsPerRow(), (int)Math.Ceiling(sizes.Count / 4.0));
        var rows = new List<List<int>> { new() };
        double width = 0;
        for (var index = 0; index < sizes.Count; index++)
        {
            var row = rows[^1];
            var add = (row.Count == 0 ? 0 : gap) + sizes[index].Width;
            if (row.Count > 0 && (width + add > wrap || row.Count >= cap))
            {
                rows.Add([index]);
                width = sizes[index].Width;
            }
            else
            {
                row.Add(index);
                width += add;
            }
        }
        // No lone word left at the end when the row above can spare one and the two still fit the row width.
        if (rows.Count > 1 && rows[^1].Count == 1 && rows[^2].Count >= 3
            && sizes[rows[^2][^1]].Width + gap + sizes[rows[^1][0]].Width <= wrap)
        {
            var moved = rows[^2][^1];
            rows[^2].RemoveAt(rows[^2].Count - 1);
            rows[^1].Insert(0, moved);
        }
        var rowWidths = rows.Select(row => row.Sum(i => sizes[i].Width) + gap * Math.Max(0, row.Count - 1)).ToArray();
        var widest = rowWidths.Length == 0 ? 0 : rowWidths.Max();
        var frames = new StageRect[sizes.Count];
        double top = 0;
        var order = 0;
        var placed = new List<(int Index, StageRect Rect)>();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var rowHeight = row.Count == 0 ? 0 : row.Max(i => sizes[i].Height);
            var start = shape switch
            {
                FableShape.Staircase => r * rowHeight * 0.9,
                FableShape.Zigzag => r % 2 == 0 ? 0 : Math.Max(widest - rowWidths[r], rowHeight * 0.9),
                _ => (widest - rowWidths[r]) / 2,
            };
            var middle = start + rowWidths[r] / 2;
            var half = Math.Max(rowWidths[r] / 2, 1);
            var x = start;
            placed.Clear();
            for (var j = 0; j < row.Count; j++)
            {
                var index = row[j];
                var size = sizes[index];
                var centre = x + size.Width / 2;
                var u = Math.Min(1, Math.Max(-1, (centre - middle) / half));
                var dy = shape switch
                {
                    FableShape.Staircase => j * rowHeight * 0.24,
                    FableShape.Arch => -rowHeight * 0.45 * (1 - u * u),
                    FableShape.Zigzag => row.Count > 1 ? (j % 2 == 0 ? -1 : 1) * rowHeight * 0.16 : 0,
                    FableShape.Wave => Math.Sin(order * 1.25 + 0.4) * rowHeight * 0.2,
                    _ => -(u + 1) / 2 * rowHeight * 0.8,
                };
                placed.Add((index, new StageRect(x, dy - size.Height / 2, size.Width, size.Height)));
                x += size.Width + gap;
                order++;
            }
            // Each row starts below the lowest point of the one before.
            var rowTop = placed.Count == 0 ? 0 : placed.Min(p => p.Rect.MinY);
            var shift = top - rowTop;
            foreach (var (index, rect) in placed) frames[index] = rect.Offset(0, shift);
            top = (placed.Count == 0 ? 0 : placed.Max(p => p.Rect.MaxY)) + shift + rowGap;
        }
        var box = frames[0];
        for (var i = 1; i < frames.Length; i++) box = box.Union(frames[i]);
        return new Arrangement(box.Size, [.. frames.Select(f => f.Offset(-box.MinX, -box.MinY))], 1);
    }

    /// <summary>The four corner marks round a word: eight segments (start, end) with arms of <paramref name="arm"/>.</summary>
    public static void Brackets(StageRect rect, double arm, Span<(PointD From, PointD To)> segments)
    {
        var a = Math.Min(arm, Math.Min(rect.Width / 2, rect.Height / 2));
        PointD P(double x, double y) => new(x, y);
        segments[0] = (P(rect.MinX, rect.MinY + a), P(rect.MinX, rect.MinY));
        segments[1] = (P(rect.MinX, rect.MinY), P(rect.MinX + a, rect.MinY));
        segments[2] = (P(rect.MaxX - a, rect.MinY), P(rect.MaxX, rect.MinY));
        segments[3] = (P(rect.MaxX, rect.MinY), P(rect.MaxX, rect.MinY + a));
        segments[4] = (P(rect.MaxX, rect.MaxY - a), P(rect.MaxX, rect.MaxY));
        segments[5] = (P(rect.MaxX, rect.MaxY), P(rect.MaxX - a, rect.MaxY));
        segments[6] = (P(rect.MinX + a, rect.MaxY), P(rect.MinX, rect.MaxY));
        segments[7] = (P(rect.MinX, rect.MaxY), P(rect.MinX, rect.MaxY - a));
    }
}
