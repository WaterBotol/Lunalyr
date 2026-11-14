// The thinking orb: a soft glowing orb of three drifting lobes in place of the three interlude dots during an
// instrumental break (the Mac's ff727bb, 15 Nov 2026, ported exactly). Five radial gradients, no shader: a halo, three
// lobes and a core. Timing is the interlude cue's (VocalGaps.InterludeCue), unchanged.

using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Motion;

/// <summary>One lobe: its centre offset and radius, both in units of the orb's radius R.</summary>
public readonly record struct OrbLobe(double X, double Y, double Radius);

/// <summary>The orb on one frame: its scale, glow and alphas, and where its lobes are.</summary>
public readonly record struct OrbPose(double Opacity, double Scale, double Glow, double HaloAlpha, double CoreAlpha,
                                      double LobeAlpha, double LobeEdge, OrbLobe Lobe0, OrbLobe Lobe1, OrbLobe Lobe2);

/// <summary>The orb's colours (OKLab): its primary, two neighbours for the lobes, the halo and the core.</summary>
public readonly record struct OrbColours(OkLab Primary, OkLab Neighbour1, OkLab Neighbour2, OkLab Halo, OkLab Core);

public static class ThinkingOrb
{
    /// <summary>Redrawn this often while a break shows.</summary>
    public const double FramesPerSecond = 24;

    private static readonly double[] Periods = [7.0, 8.6, 9.8];
    private static readonly double[] BaseRadii = [0.80, 0.66, 0.58];

    /// <summary>The orb's diameter D in points: 0.8 of the base size, 30–104.</summary>
    public static double Diameter(double baseSizePoints) => Math.Clamp(baseSizePoints * 0.8, 30, 104);

    /// <summary>
    /// The orb at the cue's moment. <paramref name="energy"/> is the live music level (0 when nothing is captured);
    /// Reduce Motion holds it still (only its glow follows the break).
    /// </summary>
    public static OrbPose? Pose(InstrumentalCue cue, bool reduceMotion, bool increaseContrast, double energy = 0)
    {
        if (!(cue.Opacity > 0)) return null;
        var m = reduceMotion ? 0.0 : 1.0;
        var tau = cue.Elapsed * m;
        var e = Math.Clamp(double.IsFinite(energy) ? energy : 0, 0, 1) * m;
        var entering = Easing.Smoothstep((cue.Elapsed - WallpaperMotion.InterludeInDelay) / WallpaperMotion.InterludeIn);
        var leaving = 1 - Math.Clamp(cue.FadeOut, 0, 1);
        OrbLobe Lobe(int i)
        {
            var phase = 2 * Math.PI * (i / 3.0 + 0.5 * ((cue.Seed >> (16 * i)) & 0xFFFF) / 65536.0);
            var theta = phase + 2 * Math.PI * tau / Periods[i];
            var orbit = 0.32 * (1 + 0.25 * Math.Sin(2 * Math.PI * tau / (1.3 * Periods[i]) + phase));
            var radius = BaseRadii[i] * (1 + 0.10 * Math.Sin(1.5 * theta));
            return new OrbLobe(orbit * Math.Cos(theta), orbit * Math.Sin(theta), radius);
        }
        var breath = 1 + m * (0.045 + 0.055 * e) * Math.Sin(2 * Math.PI * tau / 2.6);
        var scale = reduceMotion ? 1 : breath * (0.55 + 0.45 * entering) * (1 - 0.70 * leaving);
        var glow = cue.Progress is { } progress ? Easing.Smoothstep((progress - 0.5) / 0.5) : 0;
        var halo = Math.Min(1, 0.28 + 0.32 * glow + 0.12 * e) * (increaseContrast ? 1.25 : 1);
        var core = Math.Min(1, 0.30 + 0.40 * glow + 0.10 * e);
        return new OrbPose(cue.Opacity, scale, glow, Math.Min(1, halo), core, increaseContrast ? 0.95 : 0.80,
            increaseContrast ? 0.65 : 0.45, Lobe(0), Lobe(1), Lobe(2));
    }

    /// <summary>The orb's colours from the lyric ink: the old dot colour and two neighbours, all readable and toned for dark lyrics.</summary>
    public static OrbColours Colours(LyricInk ink, OkLab? albumAccent)
    {
        OkLab Toned(OkLab source)
        {
            var light = Perceptual.Readable(source, ink.Backdrop, ink.Contrast);
            return ink.Dark > 0.0005 ? Perceptual.Mix(light, Perceptual.Deep(source, ink.DeepBackdrop, ink.Contrast), Math.Min(1, ink.Dark)) : light;
        }
        // The primary is the old dots' colour exactly (the director's toned, readable highlight).
        var primary = ink.Dot is { } dot ? Perceptual.ToOkLab(new Double3(dot.R, dot.G, dot.B))
            : Toned(ink.Mode == LyricColourMode.SungSweep ? ink.Stops.S0 : ink.Gradient(0.5));
        OkLab n1, n2;
        var stops = Math.Max(1, ink.StopCount);
        switch (ink.Mode)
        {
            case LyricColourMode.Standard:
                (n1, n2) = HueSteps(primary, albumAccent, Toned);
                break;
            case LyricColourMode.SungSweep:
                if (stops > 1)
                {
                    n1 = Toned(ink.Stops.S1);
                    n2 = Toned(ink.Stops.S2 == ink.Stops.S0 ? ink.Stops.S1 : ink.Stops.S2);
                }
                else (n1, n2) = HueSteps(primary, albumAccent, Toned);
                break;
            default:
                if (stops > 1 || ink.Mode == LyricColourMode.MusicReactive)
                {
                    n1 = Toned(ink.Gradient(0.15));
                    n2 = Toned(ink.Gradient(0.85));
                }
                else (n1, n2) = HueSteps(primary, albumAccent, Toned);
                break;
        }
        var dark = Math.Clamp(ink.Dark, 0, 1);
        var halo = Perceptual.Mix(Perceptual.Mix(primary, OkLab.White, 0.35),
            OkLab.FromLch(0.97, Math.Min(0.02, primary.Chroma), primary.Hue), dark);
        var core = Perceptual.Mix(primary, OkLab.White, 0.6 - 0.35 * dark);
        return new OrbColours(primary, n1, n2, halo, core);
    }

    /// <summary>A hue step either side of the primary; a near-grey primary with an album colour takes the accent and the accent turned (chroma at least 0.06); otherwise a faint tint (chroma at least 0.06) either side, as on the Mac.</summary>
    private static (OkLab, OkLab) HueSteps(OkLab primary, OkLab? accent, Func<OkLab, OkLab> toned)
    {
        if (primary.Chroma <= 0.03 && accent is { } a)
        {
            var accentChroma = Math.Max(a.Chroma, 0.06);
            return (toned(OkLab.FromLch(a.L, accentChroma, a.Hue)), toned(OkLab.FromLch(a.L, accentChroma, a.Hue + 0.4)));
        }
        var chroma = Math.Max(primary.Chroma, 0.06);
        return (toned(OkLab.FromLch(primary.L, chroma, primary.Hue - 0.4)), toned(OkLab.FromLch(primary.L, chroma, primary.Hue + 0.4)));
    }
}
