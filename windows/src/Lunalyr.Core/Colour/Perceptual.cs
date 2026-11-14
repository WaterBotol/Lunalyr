// Ported from the Mac's Sources/UI/LyricColour.swift ("Perceptual colour"): Lunalyr's own code, translated faithfully.

namespace Lunalyr.Core.Colour;

/// <summary>
/// A colour in OKLab (BjÃ¶rn Ottosson, 2020): lightness <see cref="L"/> 0...1 and the opponent axes <see cref="A"/>
/// (green-red) and <see cref="B"/> (blue-yellow). Straight lines here are perceptually even, so blends and drifts never
/// sag through grey or flare in brightness the way sRGB blends do.
/// </summary>
public readonly record struct OkLab(double L, double A, double B)
{
    /// <summary>OKLCH: lightness, chroma and hue angle in radians.</summary>
    public static OkLab FromLch(double l, double chroma, double hue) => new(l, chroma * Math.Cos(hue), chroma * Math.Sin(hue));

    public double Chroma => Math.Sqrt(A * A + B * B);

    public double Hue => Math.Atan2(B, A);

    public static OkLab White { get; } = new(1, 0, 0);
}

/// <summary>A gamma-encoded sRGB colour with opacity, as a drawing layer takes it (the Mac's <c>Color(.sRGB, â€¦)</c>).</summary>
public readonly record struct SrgbColour(double R, double G, double B, double A = 1)
{
    public static SrgbColour White { get; } = new(1, 1, 1);

    public Double3 Rgb => new(R, G, B);

    public SrgbColour WithOpacity(double opacity) => this with { A = opacity };
}

public static class Perceptual
{
    public static double Linear(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    public static double Gamma(double channel) =>
        channel <= 0.0031308 ? 12.92 * channel : 1.055 * Math.Pow(channel, 1 / 2.4) - 0.055;

    public static OkLab ToOkLab(Double3 rgb)
    {
        double r = Linear(rgb.X), g = Linear(rgb.Y), b = Linear(rgb.Z);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new OkLab(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                         1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                         0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>Linear-light sRGB; components may fall outside 0...1 out of gamut.</summary>
    public static Double3 LinearRgb(OkLab lab)
    {
        var l = lab.L + 0.3963377774 * lab.A + 0.2158037573 * lab.B;
        var m = lab.L - 0.1055613458 * lab.A - 0.0638541728 * lab.B;
        var s = lab.L - 0.0894841775 * lab.A - 1.2914855480 * lab.B;
        double l3 = l * l * l, m3 = m * m * m, s3 = s * s * s;
        return new Double3(4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3,
                           -1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3,
                           -0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3);
    }

    public static Double3 Srgb(OkLab lab)
    {
        var linear = LinearRgb(lab);
        return new Double3(Gamma(Math.Min(1, Math.Max(0, linear.X))), Gamma(Math.Min(1, Math.Max(0, linear.Y))),
                           Gamma(Math.Min(1, Math.Max(0, linear.Z))));
    }

    public static bool InGamut(OkLab lab)
    {
        var rgb = LinearRgb(lab);
        const double tolerance = 1e-7;
        return rgb.X >= -tolerance && rgb.Y >= -tolerance && rgb.Z >= -tolerance
            && rgb.X <= 1 + tolerance && rgb.Y <= 1 + tolerance && rgb.Z <= 1 + tolerance;
    }

    /// <summary>
    /// Brings a colour into the sRGB gamut by lowering chroma only, keeping its lightness and hue: the perceptual way
    /// to fit a vivid colour.
    /// </summary>
    public static OkLab Clipped(OkLab lab)
    {
        var l = Math.Min(1, Math.Max(0, lab.L));
        var fitted = new OkLab(l, lab.A, lab.B);
        if (InGamut(fitted)) return fitted;
        double low = 0, high = 1;
        for (var i = 0; i < 14; i++)
        {
            var mid = (low + high) / 2;
            if (InGamut(new OkLab(l, lab.A * mid, lab.B * mid))) low = mid; else high = mid;
        }
        return new OkLab(l, lab.A * low, lab.B * low);
    }

    /// <summary>WCAG relative luminance of an in-gamut colour.</summary>
    public static double Luminance(OkLab lab)
    {
        var rgb = LinearRgb(Clipped(lab));
        var value = 0.2126 * rgb.X + 0.7152 * rgb.Y + 0.0722 * rgb.Z;
        return Math.Min(1, Math.Max(0, value));
    }

    public static double Luminance(Double3 rgb) =>
        Math.Min(1, Math.Max(0, 0.2126 * Linear(rgb.X) + 0.7152 * Linear(rgb.Y) + 0.0722 * Linear(rgb.Z)));

    /// <summary>WCAG relative luminance of a drawn colour (its opacity ignored).</summary>
    public static double Luminance(SrgbColour colour) => Luminance(colour.Rgb);

    /// <summary>WCAG contrast ratio between two relative luminances.</summary>
    public static double Contrast(double first, double second) =>
        (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);

    public static OkLab Mix(OkLab from, OkLab to, double amount)
    {
        var t = Math.Min(1, Math.Max(0, amount));
        return new OkLab(from.L + (to.L - from.L) * t, from.A + (to.A - from.A) * t, from.B + (to.B - from.B) * t);
    }

    /// <summary>
    /// Lightens a colour, along its own hue, until it reaches <paramref name="ratio"/> against
    /// <paramref name="background"/>. Where even white cannot reach it, the lightest version of the colour is
    /// returned: the text keeps its dark shadow for the rest.
    /// </summary>
    public static OkLab Readable(OkLab lab, double background, double ratio = LyricInk.MinimumContrast,
                                 bool keepingColour = false)
    {
        var colour = Clipped(lab);
        var target = ratio * (background + 0.05) - 0.05;
        if (Luminance(colour) >= target) return colour;
        OkLab lifted;
        if (target >= 1)
        {
            lifted = Clipped(new OkLab(1, colour.A * 0.3, colour.B * 0.3));
        }
        else
        {
            double low = colour.L, high = 1.0;
            for (var i = 0; i < 16; i++)
            {
                var mid = (low + high) / 2;
                if (Luminance(new OkLab(mid, colour.A, colour.B)) >= target) high = mid; else low = mid;
            }
            lifted = Clipped(new OkLab(high, colour.A, colour.B));
        }
        // Increase Contrast keeps its stricter ratio, colour or not.
        if (!keepingColour || ratio > LyricInk.MinimumContrast) return lifted;
        return Tinted(lifted, colour, background);
    }

    /// <summary>
    /// The least chroma a lifted lyric colour may lose before it reads as white. Lifting a pastel to reach 3:1 on a
    /// bright wallpaper used to fade every colour to white.
    /// </summary>
    public const double MinimumLyricChroma = 0.05;

    /// <summary>The contrast a lyric colour may give up to keep that tint; the dark backing and the text shadow carry the rest.</summary>
    public const double TintedContrast = 2.5;

    /// <summary>
    /// <paramref name="lifted"/>, or if lifting washed the colour out, the lightest version of
    /// <paramref name="original"/>'s hue that keeps <see cref="MinimumLyricChroma"/> (or the original's own chroma, if
    /// it had less) at <see cref="TintedContrast"/> or better. Where no such colour exists, the most colourful one that
    /// does.
    /// </summary>
    public static OkLab Tinted(OkLab lifted, OkLab original, double background)
    {
        var floor = Math.Min(original.Chroma, MinimumLyricChroma);
        if (lifted.Chroma >= floor * 0.95) return lifted;
        var target = TintedContrast * (background + 0.05) - 0.05;
        if (target >= 1) return lifted;
        var hue = original.Chroma > 0.001 ? original.Hue : lifted.Hue;
        OkLab best = lifted;
        var bestChroma = lifted.Chroma;
        var lightness = lifted.L;
        while (lightness >= original.L - 0.0001)
        {
            var candidate = Clipped(OkLab.FromLch(lightness, Math.Max(floor, original.Chroma), hue));
            if (Luminance(candidate) < target) break;
            if (candidate.Chroma > bestChroma) { best = candidate; bestChroma = candidate.Chroma; }
            if (candidate.Chroma >= floor * 0.95) return candidate;
            lightness -= 0.01;
        }
        return best;
    }

    /// <summary>
    /// A deep version of <paramref name="lab"/> for light wallpapers ("dark lyrics", the Mac's Perceptual.deep,
    /// next/tester-fixes 8a52096): its hue and chroma at a lightness low enough to reach <paramref name="ratio"/> under
    /// <paramref name="background"/> with a 15% margin. The lightest such colour from L ≤ 0.5 down; black where none is.
    /// </summary>
    public static OkLab Deep(OkLab lab, double background, double ratio = LyricInk.MinimumContrast)
    {
        var target = (Math.Clamp(background, 0, 1) + 0.05) / (ratio * 1.15) - 0.05;
        if (target <= 0) return new OkLab(0, 0, 0);
        double chroma = lab.Chroma, hue = lab.Hue;
        var start = Clipped(OkLab.FromLch(Math.Min(lab.L, 0.5), chroma, hue));
        if (Luminance(start) <= target) return start;
        double low = 0, high = start.L;
        for (var i = 0; i < 18; i++)
        {
            var mid = (low + high) / 2;
            if (Luminance(Clipped(OkLab.FromLch(mid, chroma, hue))) <= target) low = mid; else high = mid;
        }
        return Clipped(OkLab.FromLch(low, chroma, hue));
    }

    /// <summary>The colour as drawn: clipped into gamut and gamma-encoded.</summary>
    public static SrgbColour ToColour(OkLab lab, double opacity = 1)
    {
        var rgb = Srgb(Clipped(lab));
        return new SrgbColour(rgb.X, rgb.Y, rgb.Z, opacity);
    }
}
