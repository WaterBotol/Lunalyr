using Lunalyr.Core.Colour;

namespace Lunalyr.Render.Backgrounds;

/// <summary>
/// The automatic text backing's shape on Windows: a rounded, widely feathered shade instead of the Mac's stage
/// rectangle (<see cref="LyricLegibility.BackingStrength"/>). The Mac's holds full strength out to the stage's edges
/// and corners and fades to nothing over the last tenth of the screen, which over a bright, contrasty cover leaves a
/// visible dark rectangle with lighter edges. This one holds full strength where lyrics are set, then fades over a
/// much wider band along a rounded-rectangle (superellipse) distance, so no edge or corner can be seen.
/// </summary>
public static class FeatheredBacking
{
    /// <summary>
    /// Full strength out to this share of the stage's half-extent (the lines, their readings and backing vocals sit
    /// inside it).
    /// </summary>
    public const double Plateau = 0.85;

    /// <summary>Where the shade reaches nothing, in stage half-extents (past the screen's sides, near its corners).</summary>
    public const double Reach = 1.6;

    /// <summary>The superellipse exponent: 2 is an ellipse, larger is squarer. Rounded enough to hide any corner.</summary>
    public const double Roundness = 4;

    /// <summary>
    /// The share of the backing at a point of the view, given from its centre in view sizes ((0.5, 0) is the middle of
    /// its right edge): 1 across the lyrics, easing smoothly to 0.
    /// </summary>
    public static double Strength(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return 0;
        var (halfX, halfY) = LyricLegibility.StageHalfExtent;
        var u = Math.Abs(x) / halfX;
        var v = Math.Abs(y) / halfY;
        var distance = Math.Pow(Math.Pow(u, Roundness) + Math.Pow(v, Roundness), 1 / Roundness);
        if (distance <= Plateau) return 1;
        if (distance >= Reach) return 0;
        var t = (distance - Plateau) / (Reach - Plateau);
        // Smootherstep: no kink where the plateau ends or where the shade runs out.
        return 1 - t * t * t * (t * (t * 6 - 15) + 10);
    }

    /// <summary>The shape as premultiplied black BGRA, as opaque as the backing is strong (laid out as <see cref="LyricLegibility.ShadeImage"/>).</summary>
    public static byte[]? Image(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0) return null;
        var pixels = new byte[columns * rows * 4];
        for (var row = 0; row < rows; row++)
        {
            var y = (row + 0.5) / rows - 0.5;
            for (var column = 0; column < columns; column++)
            {
                var x = (column + 0.5) / columns - 0.5;
                pixels[(row * columns + column) * 4 + 3] = (byte)Math.Round(Strength(x, y) * 255);
            }
        }
        return pixels;
    }
}
