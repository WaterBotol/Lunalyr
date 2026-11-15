// Echo's backdrop: the phrase repeated behind the live line. The first table of repeats was re-implemented from the
// description in docs/compositions/scattered.md (EchoBackdrop in LyricComposition.swift traces back to the squashed
// Verso import). Test Build 88's table (repeats stepping off the line's edges, two strengths), the side the seed picks,
// the drift seeds and wander, the fit to the title-safe stage, the reveal sweep (LyricSweepMask) and the tint and
// strength are Lunalyr's own and ported.

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>One repeat of the phrase in Echo's table.</summary>
/// <param name="SizeShare">Type size as a share of the base size.</param>
/// <param name="Across">Sideways from the line's centre, in base sizes (mirrored by the line's seed, <see cref="EchoBackdrop.Side"/>).</param>
/// <param name="Down">From the line's nearer edge, in base sizes: negative rises above the line, positive falls below it.</param>
/// <param name="Tilt">Degrees.</param>
/// <param name="Strength">Opacity, before the line's presence and the Repeats setting.</param>
/// <param name="Mirrored">Drawn upside down (reflected about its horizontal axis), as a reflection in glass.</param>
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

    /// <summary>
    /// The five repeats (the Mac's Test Build 88 table): they recede in steps from the live line, smaller and fainter
    /// with each step away, three rising above it and two mirrored below it as a reflection. Two strengths only: the
    /// repeat next to the line, and those further off.
    /// </summary>
    public static IReadOnlyList<EchoRepeat> Repeats { get; } =
    [
        new(0.62, 0.30, -0.48, 0, 0.18, false),
        new(0.42, 0.62, -1.16, 0, 0.08, false),
        new(0.28, 0.90, -1.66, 0, 0.08, false),
        new(0.78, 0.0, 0.58, 0, 0.18, true),
        new(0.52, -0.30, 1.44, 0, 0.08, true),
    ];

    /// <summary>A seed's magnitude: any value works, the most negative included.</summary>
    public static ulong Magnitude(long seed) => seed < 0 ? unchecked(0UL - (ulong)seed) : (ulong)seed;

    /// <summary>Each repeat's drift seed (wrapping, so it never traps however large or negative the seed).</summary>
    public static ulong WanderSeed(long seed, int slot) => unchecked(Magnitude(seed) * 31 + Magnitude(slot));

    /// <summary>Which way the echo drifts: +1 (rightward) for an even seed, −1 for an odd one, so lines alternate.</summary>
    public static double Side(long seed) => Magnitude(seed) % 2 == 0 ? 1 : -1;

    /// <summary>
    /// The repeat in <paramref name="slot"/> at <paramref name="time"/>: its place, stepped off the line's top or bottom
    /// edge (<paramref name="lineHeight"/> tall), and a slow seeded wander (none under Reduce Motion).
    /// </summary>
    public static EchoRepeatPose Pose(int slot, long seed, double time, double baseSize, bool reduceMotion, double lineHeight = 0)
    {
        var repeat = Repeats[Math.Clamp(slot, 0, Repeats.Count - 1)];
        var wander = WanderSeed(seed, slot);
        var drift = reduceMotion || !double.IsFinite(time) ? 0.0 : 1.0;
        var height = double.IsFinite(lineHeight) ? Math.Max(0, lineHeight) : 0;
        var edge = (repeat.Down < 0 ? -height : height) / 2;
        var restX = baseSize * repeat.Across * Side(seed);
        var restY = edge + baseSize * repeat.Down;
        var x = restX + drift * LyricMotion.Noise2(wander, time * 0.09) * baseSize * 0.08;
        var y = restY + drift * LyricMotion.Noise2(wander ^ 0x5EED, time * 0.07) * baseSize * 0.03;
        var angle = repeat.Tilt + drift * LyricMotion.Noise(wander ^ 0xA9, time * 0.11) * 0.4;
        return new EchoRepeatPose(repeat, x, y, angle, restX, restY);
    }

    /// <summary>
    /// How much a repeat of <paramref name="ink"/> size is shrunk (never grown) to stay within the title-safe area of
    /// <paramref name="stage"/> (5% in from every edge) around its resting place.
    /// </summary>
    public static double Fit(SizeD ink, SizeD stage, in EchoRepeatPose pose)
    {
        var area = new SizeD(stage.Width * 0.90, stage.Height * 0.90);
        var centre = new PointD(area.Width / 2 + pose.RestX, area.Height / 2 + pose.RestY);
        return WordStageFit.Scale(ink, area, centre, 1.02, pose.Repeat.Tilt, 4);
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
