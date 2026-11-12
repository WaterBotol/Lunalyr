// Echo's backdrop: the phrase repeated behind the live line. The table of repeats and which one leads are
// re-implemented from the description in docs/compositions/scattered.md (EchoBackdrop in LyricComposition.swift traces
// back to the squashed Verso import). Lunalyr's own parts are ported as they are: the drift seeds and wander, the fit
// to the stage, the reveal sweep (LyricSweepMask) and the tint and strength.

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>One repeat of the phrase in Echo's table.</summary>
/// <param name="SizeShare">Type size as a share of the base size.</param>
/// <param name="Across">Rightward from the line's centre, in steps of 3.2 base sizes.</param>
/// <param name="Down">Downward from the line's centre, in steps of 1.5 base sizes.</param>
/// <param name="Tilt">Degrees.</param>
/// <param name="Strength">Opacity, before the line's presence and the Repeats setting.</param>
/// <param name="Mirrored">Drawn upside down (reflected about its horizontal axis).</param>
public readonly record struct EchoRepeat(double SizeShare, double Across, double Down, double Tilt, double Strength, bool Mirrored);

/// <summary>Where a repeat is drawn on one frame, relative to the line's centre (base-size units).</summary>
/// <param name="Repeat">The repeat.</param>
/// <param name="X">Rightward.</param>
/// <param name="Y">Downward.</param>
/// <param name="Angle">Its tilt plus the wander, degrees.</param>
/// <param name="RestX">Where it sits without the wander, rightward.</param>
/// <param name="RestY">Where it sits without the wander, downward.</param>
public readonly record struct EchoRepeatPose(EchoRepeat Repeat, double X, double Y, double Angle, double RestX, double RestY);

public static class EchoBackdrop
{
    /// <summary>The Repeats setting's default (the Mac's <c>echoStrength</c>, 0–1.8).</summary>
    public const double DefaultStrength = 1.0;

    /// <summary>The soft edge of the reveal sweep, as a share of the phrase.</summary>
    public const double Feather = 0.2;

    /// <summary>The five repeats (see docs/compositions/scattered.md for the values).</summary>
    public static IReadOnlyList<EchoRepeat> Repeats { get; } =
    [
        new(0.85, -0.30, -0.62, -2.0, 0.055, true),
        new(0.42, 0.46, -0.44, 1.5, 0.085, false),
        new(0.30, -0.50, 0.48, -1.0, 0.10, false),
        new(0.60, 0.34, 0.66, 2.5, 0.065, true),
        new(0.22, 0.10, -0.78, 0.0, 0.12, false),
    ];

    /// <summary>A seed's magnitude: any value works, the most negative included.</summary>
    public static ulong Magnitude(long seed) => seed < 0 ? unchecked(0UL - (ulong)seed) : (ulong)seed;

    /// <summary>Each repeat's drift seed (wrapping, so it never traps however large or negative the seed).</summary>
    public static ulong WanderSeed(long seed, int slot) => unchecked(Magnitude(seed) * 31 + Magnitude(slot));

    /// <summary>The repeat drawn in <paramref name="slot"/> (0…4): the line's seed shifts the table.</summary>
    public static EchoRepeat RepeatAt(int slot, long seed)
    {
        var shift = (int)(Magnitude(seed) % (ulong)Repeats.Count);
        return Repeats[(slot + shift) % Repeats.Count];
    }

    /// <summary>The repeat in <paramref name="slot"/> at <paramref name="time"/>: its place and a slow seeded wander (none under Reduce Motion).</summary>
    public static EchoRepeatPose Pose(int slot, long seed, double time, double baseSize, bool reduceMotion)
    {
        var repeat = RepeatAt(slot, seed);
        var wander = WanderSeed(seed, slot);
        var drift = reduceMotion || !double.IsFinite(time) ? 0.0 : 1.0;
        var restX = baseSize * repeat.Across * 3.2;
        var restY = baseSize * repeat.Down * 1.5;
        var x = restX + drift * LyricMotion.Noise2(wander, time * 0.09) * baseSize * 0.10;
        var y = restY + drift * LyricMotion.Noise2(wander ^ 0x5EED, time * 0.07) * baseSize * 0.06;
        var angle = repeat.Tilt + drift * LyricMotion.Noise(wander ^ 0xA9, time * 0.11) * 0.9;
        return new EchoRepeatPose(repeat, x, y, angle, restX, restY);
    }

    /// <summary>
    /// How much a repeat of <paramref name="ink"/> size is shrunk (never grown) to stay within two thirds of
    /// <paramref name="stage"/> around its resting place.
    /// </summary>
    public static double Fit(SizeD ink, SizeD stage, in EchoRepeatPose pose)
    {
        var area = new SizeD(stage.Width * 2 / 3, stage.Height * 2 / 3);
        var centre = new PointD(area.Width / 2 + pose.RestX, area.Height / 2 + pose.RestY);
        return WordStageFit.Scale(ink, area, centre, 1.04, pose.Repeat.Tilt, 4);
    }

    /// <summary>A repeat's opacity: its strength times the line's presence and the Repeats setting.</summary>
    public static double Opacity(in EchoRepeat repeat, double presence, double strength = DefaultStrength) =>
        repeat.Strength * presence * strength;
}


/// <summary>
/// A soft-edged reveal that runs in reading direction (the Mac's <c>LyricSweepMask</c>): the feather travels past
/// both ends, so 0 shows nothing and 1 shows everything at full strength.
/// </summary>
public static class LyricSweep
{
    /// <summary>The mask's alpha at <paramref name="along"/> (0…1 in reading order).</summary>
    public static double Alpha(double along, double progress, double feather)
    {
        var (solid, clear) = Edges(progress, feather);
        if (solid >= 1) return 1;
        if (clear <= 0) return 0;
        if (along <= solid) return 1;
        if (along >= clear) return 0;
        return clear > solid ? (clear - along) / (clear - solid) : 0;
    }

    /// <summary>Where the mask stops being solid and where it becomes clear, 0…1 in reading order.</summary>
    public static (double Solid, double Clear) Edges(double progress, double feather)
    {
        var reach = Math.Min(Math.Max(double.IsFinite(progress) ? progress : 1, 0), 1) * (1 + feather);
        return (Math.Min(1, Math.Max(reach - feather, 0)), Math.Min(1, Math.Max(reach, 0)));
    }
}
