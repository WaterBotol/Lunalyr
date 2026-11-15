// The thinking orb's size and palette (the Mac's ff727bb): the orb itself is thinking-orbs' dots since v2 (ThinkingOrbs/).
// Its colours come from the lyric ink: the old dot colour and two neighbours, toned for dark lyrics.

using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Motion;

/// <summary>The orb's colours (OKLab): its primary, two neighbours for the lobes, the halo and the core.</summary>
public readonly record struct OrbColours(OkLab Primary, OkLab Neighbour1, OkLab Neighbour2, OkLab Halo, OkLab Core);

public static class ThinkingOrb
{
    /// <summary>The orb's diameter D in points: 0.8 of the base size, 30–104.</summary>
    public static double Diameter(double baseSizePoints) => Math.Clamp(baseSizePoints * 0.8, 30, 104);

    /// <summary>The orb's colours from the lyric ink: the old dot colour and two neighbours, all readable and toned for dark lyrics.</summary>
    public static OrbColours Colours(LyricInk ink, OkLab? albumAccent)
    {
        OkLab Toned(OkLab source) => ToneFor(ink, source);
        // The primary is the old dots' colour exactly (the director's toned, readable highlight).
        var primary = ink.Dot is { } dot ? Perceptual.ToOkLab(new Double3(dot.R, dot.G, dot.B))
            : Toned(ink.Mode == LyricColourMode.SungSweep ? ink.Stops.S0 : ink.Gradient(0.5));
        OkLab n1, n2;
        var stops = Math.Max(1, ink.StopCount);
        switch (ink.Mode)
        {
            case LyricColourMode.Standard:
                (n1, n2) = HueSteps(primary, albumAccent, ink);
                break;
            case LyricColourMode.SungSweep:
                if (stops > 1)
                {
                    n1 = Toned(ink.Stops.S1);
                    n2 = Toned(ink.Stops.S2 == ink.Stops.S0 ? ink.Stops.S1 : ink.Stops.S2);
                }
                else (n1, n2) = HueSteps(primary, albumAccent, ink);
                break;
            default:
                if (stops > 1 || ink.Mode == LyricColourMode.MusicReactive)
                {
                    n1 = Toned(ink.Gradient(0.15));
                    n2 = Toned(ink.Gradient(0.85));
                }
                else (n1, n2) = HueSteps(primary, albumAccent, ink);
                break;
        }
        var dark = Math.Clamp(ink.Dark, 0, 1);
        var halo = Perceptual.Mix(Perceptual.Mix(primary, OkLab.White, 0.35),
            OkLab.FromLch(0.97, Math.Min(0.02, primary.Chroma), primary.Hue), dark);
        var core = Perceptual.Mix(primary, OkLab.White, 0.6 - 0.35 * dark);
        return new OrbColours(primary, n1, n2, halo, core);
    }

    /// <summary>A hue step either side of the primary; a near-grey primary with an album colour takes the accent and the accent turned (chroma at least 0.06); otherwise a faint tint (chroma at least 0.06) either side, as on the Mac.</summary>
    private static OkLab ToneFor(in LyricInk ink, OkLab source)
    {
        var light = Perceptual.Readable(source, ink.Backdrop, ink.Contrast);
        return ink.Dark > 0.0005 ? Perceptual.Mix(light, Perceptual.Deep(source, ink.DeepBackdrop, ink.Contrast), Math.Min(1, ink.Dark)) : light;
    }

    private static (OkLab, OkLab) HueSteps(OkLab primary, OkLab? accent, LyricInk ink)
    {
        OkLab toned(OkLab source) => ToneFor(ink, source);
        if (primary.Chroma <= 0.03 && accent is { } a)
        {
            var accentChroma = Math.Max(a.Chroma, 0.06);
            return (toned(OkLab.FromLch(a.L, accentChroma, a.Hue)), toned(OkLab.FromLch(a.L, accentChroma, a.Hue + 0.4)));
        }
        var chroma = Math.Max(primary.Chroma, 0.06);
        return (toned(OkLab.FromLch(primary.L, chroma, primary.Hue - 0.4)), toned(OkLab.FromLch(primary.L, chroma, primary.Hue + 0.4)));
    }
}
