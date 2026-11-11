// Ported from the Mac's Sources/Desktop/LyricLegibility.swift (Lunalyr's own code; no Verso lines): the contrast
// maths, the backing amount and its stage-shaped profile, and the backdrop estimates for generated wallpapers. Picture
// measurement (thumbnails, video frames, desktop pictures) and LyricStagePresence belong to other workstreams.

using System.Numerics;

namespace Lunalyr.Core.Colour;

/// <summary>
/// Keeps white lyrics readable over whatever sits behind the lyric stage. The lyrics are white; on a pale cover, a light
/// imported picture or the desktop picture, a fixed scrim of about 12% at the stage left white type under 2:1. This
/// estimates the backdrop's luminance where the lyrics sit and returns just enough extra darkening, applied as a soft
/// shade behind the stage, for WCAG contrast: 4.5:1, or 3:1 when the type is very large, with readings and
/// translations (white at 70%) at 3:1 or better. The shade holds that strength across the whole stage and eases out to
/// nothing at the screen's edges. It is capped so the wallpaper always stays visible.
/// </summary>
public static class LyricLegibility
{
    /// <summary>Never darken the middle of the wallpaper further than this.</summary>
    public const double MaximumBacking = 0.6;

    /// <summary>Used when nothing is known about what is behind the lyrics.</summary>
    public const double UnknownBackdropLuminance = 0.35;

    // Shape

    /// <summary>Half the lyric stage's width and height, as shares of the view (the stage is 80% × 72% of it).</summary>
    public static (double X, double Y) StageHalfExtent { get; } = (0.80 / 2, 0.72 / 2);

    /// <summary>
    /// The share of the backing at a point of the view, given from its centre in view sizes: (0.5, 0) is the middle of
    /// its right edge. Full across the lyric stage, corners included, then easing out to nothing at the view's edges.
    /// </summary>
    public static double BackingStrength(double x, double y) =>
        BackingProfile(Math.Abs(x), StageHalfExtent.X) * BackingProfile(Math.Abs(y), StageHalfExtent.Y);

    /// <summary>Along one axis: 1 out to <paramref name="plateau"/>, easing to 0 at the view's edge.</summary>
    public static double BackingProfile(double offset, double plateau)
    {
        if (!double.IsFinite(offset)) return 0;
        const double edge = 0.5;
        if (!(offset > plateau)) return 1;
        if (!(offset < edge && plateau < edge)) return 0;
        var t = (offset - plateau) / (edge - plateau);
        return 1 - t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The shape as an image: premultiplied black RGBA, as opaque as the backing is strong, that a wallpaper stretches
    /// over itself and fades to the amount needed. Null for an empty size.
    /// </summary>
    public static byte[]? ShadeImage(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0) return null;
        // Premultiplied black: every colour channel stays 0.
        var pixels = new byte[columns * rows * 4];
        for (var row = 0; row < rows; row++)
        {
            var y = (row + 0.5) / rows - 0.5;
            for (var column = 0; column < columns; column++)
            {
                var x = (column + 0.5) / columns - 0.5;
                pixels[(row * columns + column) * 4 + 3] = (byte)SwiftMath.Round(BackingStrength(x, y) * 255);
            }
        }
        return pixels;
    }

    /// <summary>The size the shade image is made at: small, as the shape is a share of the view whatever its size.</summary>
    public const int ShadeColumns = 256, ShadeRows = 144;

    // Contrast

    public static double Linear(double channel)
    {
        var c = Math.Min(1, Math.Max(0, channel));
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>The gamma-encoded grey with this relative luminance.</summary>
    public static double Encoded(double luminance)
    {
        var l = Math.Min(1, Math.Max(0, luminance));
        return l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
    }

    /// <summary>WCAG relative luminance of a gamma-encoded sRGB colour.</summary>
    public static double Luminance(Double3 rgb) => 0.2126 * Linear(rgb.X) + 0.7152 * Linear(rgb.Y) + 0.0722 * Linear(rgb.Z);

    public static double WhiteContrast(double luminance) => 1.05 / (Math.Min(1, Math.Max(0, luminance)) + 0.05);

    /// <summary>4.5:1 for ordinary lyric sizes; 3:1 once the type is very large.</summary>
    public static double TargetContrast(double baseSize) => baseSize >= 96 ? 3 : 4.5;

    /// <summary>A white-based backing target with headroom for lower-luminance pastels to keep their hue.</summary>
    public static double PastelBackingTarget(bool increaseContrast) => increaseContrast ? 8 : 6.5;

    /// <summary>
    /// Luminance after a black layer of <paramref name="darkening"/> opacity. Desktop compositing blends gamma-encoded
    /// values, so the backdrop is treated as the grey of the same luminance and scaled in that space.
    /// </summary>
    public static double Darkened(double luminance, double darkening) =>
        Linear(Encoded(luminance) * (1 - Math.Min(1, Math.Max(0, darkening))));

    /// <summary>Extra darkening, on top of <paramref name="existing"/>, for white text to reach <paramref name="target"/>. 0 when it already does.</summary>
    public static double Backing(double luminance, double existing, double target, double cap = MaximumBacking)
    {
        if (!(double.IsFinite(target) && target > 1)) return 0;
        return BackingToCeiling(luminance, existing, 1.05 / target - 0.05, cap);
    }

    // Readings and translations

    /// <summary>Readings and translations are white at this opacity.</summary>
    public const double SecondaryTextOpacity = 0.7;

    /// <summary>They are held to 3:1, the floor for large text.</summary>
    public const double SecondaryTextTarget = 3.0;

    /// <summary>White text at <paramref name="opacity"/> over <paramref name="luminance"/>, blended in gamma-encoded space.</summary>
    public static double ContrastOfWhite(double opacity, double luminance)
    {
        var alpha = Math.Min(1, Math.Max(0, opacity));
        var text = Linear(alpha + (1 - alpha) * Encoded(luminance));
        return (text + 0.05) / (Math.Min(1, Math.Max(0, luminance)) + 0.05);
    }

    /// <summary>The brightest backdrop over which white text at <paramref name="opacity"/> still reaches <paramref name="target"/>; negative if none does.</summary>
    public static double BrightestBackdrop(double opacity, double target)
    {
        if (!(ContrastOfWhite(opacity, 0) >= target)) return -1;
        double dark = 0, light = 1;
        for (var i = 0; i < 40; i++)
        {
            var middle = (dark + light) / 2;
            if (ContrastOfWhite(opacity, middle) >= target) dark = middle; else light = middle;
        }
        return dark;
    }

    /// <summary>Worked out once: it depends on neither the cover nor the scrim.</summary>
    public static double SecondaryTextCeiling { get; } = BrightestBackdrop(SecondaryTextOpacity, SecondaryTextTarget);

    /// <summary>What the stage needs: white lyrics at <paramref name="target"/>, and the readings beside them at 3:1.</summary>
    public static double StageBacking(double luminance, double existing, double target, double cap = MaximumBacking) =>
        Math.Max(Backing(luminance, existing, target, cap), BackingToCeiling(luminance, existing, SecondaryTextCeiling, cap));

    /// <summary>Extra darkening, on top of <paramref name="existing"/>, to bring a backdrop down to <paramref name="ceiling"/>.</summary>
    public static double BackingToCeiling(double luminance, double existing, double ceiling, double cap = MaximumBacking)
    {
        if (!(double.IsFinite(luminance) && double.IsFinite(existing) && double.IsFinite(ceiling))) return 0;
        var backdrop = Encoded(luminance);
        if (!(backdrop > 0)) return 0;
        // Beyond 21:1 nothing is enough; darken as far as allowed.
        if (!(ceiling > 0)) return Math.Min(Math.Max(0, cap), 1);
        var allowed = Encoded(ceiling) / backdrop;
        var remaining = 1 - Math.Min(0.999, Math.Max(0, existing));
        if (!(allowed < remaining)) return 0;
        return Math.Min(Math.Max(0, cap), Math.Max(0, 1 - allowed / remaining));
    }

    // Backdrop estimates

    /// <summary>
    /// The colour field: large soft blobs of each album stop over a darkened average. Any blob can drift under the
    /// lyrics, so the brightest one decides; a pattern's faint white lines add a little.
    /// </summary>
    public static double ColourFieldLuminance(AlbumWallpaperPalette palette, double? patternOpacity)
    {
        var stops = palette.Stops;
        if (stops.Count == 0) return UnknownBackdropLuminance;
        Double3 brightest = default, sum = default;
        var brightestLuminance = double.NegativeInfinity;
        foreach (var stop in stops)
        {
            var rgb = Double3.From(stop);
            sum += rgb;
            var value = Luminance(rgb);
            if (brightestLuminance < value) { brightest = rgb; brightestLuminance = value; }
        }
        var baseColour = sum / stops.Count * 0.45;
        var colour = brightest * 0.85 + baseColour * 0.15;
        if (patternOpacity is { } opacity) colour += Double3.Repeating(0.03 * Math.Min(1, Math.Max(0, opacity)));
        return Luminance(colour.Clamped(0, 1));
    }

    /// <summary>GPU effects take their light from the album's first and third stops and can glow slightly past them.</summary>
    public static double ShaderLuminance(AlbumWallpaperPalette palette)
    {
        var best = double.NegativeInfinity;
        foreach (var index in (ReadOnlySpan<int>)[0, 2])
        {
            if (index >= palette.Stops.Count) continue;
            best = Math.Max(best, Luminance(Double3.From(palette.Stops[index])));
        }
        return double.IsNegativeInfinity(best) ? UnknownBackdropLuminance : Math.Min(1, best * 1.1);
    }

    /// <summary>Visualisers are mostly dark between their lit ribbons.</summary>
    public static double VisualizerLuminance(AlbumWallpaperPalette palette)
    {
        var best = double.NegativeInfinity;
        foreach (var stop in palette.Stops) best = Math.Max(best, Luminance(Double3.From(stop)));
        return (double.IsNegativeInfinity(best) ? UnknownBackdropLuminance : best) * 0.85;
    }

    /// <summary>An animated cover shows the cover itself; the middle row of the 3×3 grid fills the screen behind the lyrics.</summary>
    public static double CoverLuminance(IReadOnlyList<Double3> hsbGrid)
    {
        var best = double.NegativeInfinity;
        var any = false;
        for (var index = 3; index <= 5 && index < hsbGrid.Count; index++)
        {
            best = Math.Max(best, Luminance(ColourSpaces.Rgb(hsbGrid[index])));
            any = true;
        }
        if (!any)
            foreach (var cell in hsbGrid) { best = Math.Max(best, Luminance(ColourSpaces.Rgb(cell))); any = true; }
        return any ? best : UnknownBackdropLuminance;
    }

    public static Double3 Rgb(Double3 hsb) => ColourSpaces.Rgb(hsb);
}

/// <summary>Colour-space helpers shared by the colour code.</summary>
public static class ColourSpaces
{
    /// <summary>HSB (hue 0...1, wrapping) to sRGB 0...1.</summary>
    public static Double3 Rgb(Double3 hsb)
    {
        var hue = (hsb.X % 1 + 1) % 1 * 6;
        double Channel(double offset)
        {
            var k = (hue + offset) % 6;
            return hsb.Z * (1 - hsb.Y * Math.Max(0, SwiftMath.Min(k, 4 - k, 1)));
        }
        return new Double3(Channel(5), Channel(3), Channel(1));
    }
}

/// <summary>
/// The shade a generated wallpaper lays over itself so white lyrics stay readable: a scrim down the whole screen, and
/// extra shade behind the lyric stage while it is in use (the text backing, whose amount
/// <see cref="LyricLegibility"/> works out). Ported from the Mac's Sources/UI/WallpaperShade.swift.
/// </summary>
public static class WallpaperShade
{
    /// <summary>The scrim's black at the top, the middle and the bottom of the screen (evenly spaced) at Darkening 1.</summary>
    public static IReadOnlyList<double> Scrim { get; } = [0.34, 0.12, 0.50];

    /// <summary>The scrim where the lyrics sit: the middle of the screen, where it is lightest.</summary>
    public static double ScrimAtStage(double strength) => Math.Min(1, Scrim[1] * strength);

    /// <summary>The text backing is drawn only above this amount.</summary>
    public const double MinimumBacking = 0.004;

    /// <summary>How long the text backing takes to come or go when the lyric stage fills or empties at once.</summary>
    public const double BackingFade = 0.6;
}
