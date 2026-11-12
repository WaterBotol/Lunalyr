// Echo's backdrop: the phrase repeated behind the live line. The ghost table and which ghost leads are re-implemented
// from the description in docs/compositions/scattered.md (EchoBackdrop in LyricComposition.swift traces back to the
// squashed Verso import). Lunalyr's own parts are ported as they are: the drift seeds and wander, the fit to the
// stage, the reveal sweep (LyricSweepMask) and the tint and strength.

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>One repeat of the phrase.</summary>
/// <param name="Scale">Its type size, as a share of the base size.</param>
/// <param name="Dx">Across from the line's centre, in units of 3.2 base sizes.</param>
/// <param name="Dy">Down from the line's centre, in units of 1.5 base sizes.</param>
/// <param name="Angle">Its turn, degrees.</param>
/// <param name="Opacity">Its opacity before the line's presence and the Repeats setting.</param>
/// <param name="Flipped">Mirrored top to bottom, like a reflection in glass.</param>
public readonly record struct EchoGhost(double Scale, double Dx, double Dy, double Angle, double Opacity, bool Flipped);

/// <summary>Where a ghost is on one frame, relative to the line's centre.</summary>
/// <param name="Ghost">The ghost.</param>
/// <param name="X">Across, in the base size's units.</param>
/// <param name="Y">Down.</param>
/// <param name="Angle">Its turn with the wander, degrees.</param>
/// <param name="RestX">Its resting place across (without the wander).</param>
/// <param name="RestY">Its resting place down.</param>
public readonly record struct EchoGhostPose(EchoGhost Ghost, double X, double Y, double Angle, double RestX, double RestY);

public static class EchoBackdrop
{
    /// <summary>The Repeats setting's default (the Mac's <c>echoStrength</c>, 0–1.8).</summary>
    public const double DefaultStrength = 1.0;

    /// <summary>The soft edge of the reveal sweep, as a share of the phrase.</summary>
    public const double Feather = 0.2;

    /// <summary>
    /// Five repeats, every one well under the live line in size or strength and away from the centre, so none reads as
    /// the line being sung; two are mirrored.
    /// </summary>
    public static IReadOnlyList<EchoGhost> Ghosts { get; } =
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

    /// <summary>The ghost in <paramref name="slot"/> (0…4): the line's seed rotates which one leads.</summary>
    public static EchoGhost GhostAt(int slot, long seed)
    {
        var offset = (int)(Magnitude(seed) % (ulong)Ghosts.Count);
        return Ghosts[(slot + offset) % Ghosts.Count];
    }

    /// <summary>The ghost in <paramref name="slot"/> at <paramref name="time"/>: its place and a slow seeded wander (none under Reduce Motion).</summary>
    public static EchoGhostPose Pose(int slot, long seed, double time, double baseSize, bool reduceMotion)
    {
        var ghost = GhostAt(slot, seed);
        var wander = WanderSeed(seed, slot);
        var drift = reduceMotion || !double.IsFinite(time) ? 0.0 : 1.0;
        var restX = baseSize * ghost.Dx * 3.2;
        var restY = baseSize * ghost.Dy * 1.5;
        var x = restX + drift * LyricMotion.Noise2(wander, time * 0.09) * baseSize * 0.10;
        var y = restY + drift * LyricMotion.Noise2(wander ^ 0x5EED, time * 0.07) * baseSize * 0.06;
        var angle = ghost.Angle + drift * LyricMotion.Noise(wander ^ 0xA9, time * 0.11) * 0.9;
        return new EchoGhostPose(ghost, x, y, angle, restX, restY);
    }

    /// <summary>
    /// How much a ghost of <paramref name="ink"/> size is shrunk (never grown) to stay within two thirds of
    /// <paramref name="stage"/> around its resting place.
    /// </summary>
    public static double Fit(SizeD ink, SizeD stage, in EchoGhostPose pose)
    {
        var area = new SizeD(stage.Width * 2 / 3, stage.Height * 2 / 3);
        var centre = new PointD(area.Width / 2 + pose.RestX, area.Height / 2 + pose.RestY);
        return WordStageFit.Scale(ink, area, centre, 1.04, pose.Ghost.Angle, 4);
    }

    /// <summary>A ghost's opacity: its own, times the line's presence and the Repeats setting.</summary>
    public static double Opacity(in EchoGhost ghost, double presence, double strength = DefaultStrength) =>
        ghost.Opacity * presence * strength;
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
