// What each composition is called and the traits that steer the shared stage code: re-implemented from the
// description in docs/compositions/shared.md and scattered.md (LyricComposition in LyricComposition.swift traces back
// to the squashed Verso import). The idle float bases are Lunalyr's own (OrganicMotion.idleAmplitude).

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

public static class CompositionTraits
{
    /// <summary>The Mac's raw value, which the factories are registered under.</summary>
    public static string Id(this CompositionKind kind) => kind switch
    {
        CompositionKind.Editorial => "editorial",
        CompositionKind.Spoken => "spoken",
        CompositionKind.Cascade => "cascade",
        CompositionKind.Cutout => "cutout",
        CompositionKind.Hinge => "hinge",
        CompositionKind.Broadside => "broadside",
        CompositionKind.Portal => "portal",
        CompositionKind.Mosaic => "mosaic",
        CompositionKind.Torque => "torque",
        CompositionKind.Fable => "fable",
        CompositionKind.Scattered => "scattered",
        CompositionKind.Constellation => "constellation",
        CompositionKind.ConstellationEditorial => "constellationEditorial",
        CompositionKind.Echo => "echo",
        _ => kind.ToString(),
    };

    public static string Title(this CompositionKind kind) => kind switch
    {
        CompositionKind.Editorial => "Editorial",
        CompositionKind.Spoken => "Spoken",
        CompositionKind.Cascade => "Cascade",
        CompositionKind.Cutout => "Cutout",
        CompositionKind.Hinge => "Hinge",
        CompositionKind.Broadside => "Broadside",
        CompositionKind.Portal => "Portal",
        CompositionKind.Mosaic => "Mosaic",
        CompositionKind.Torque => "Torque",
        CompositionKind.Fable => "Fable",
        CompositionKind.Scattered => "Scattered",
        CompositionKind.Constellation => "Constellation",
        CompositionKind.ConstellationEditorial => "Constellation + Editorial",
        CompositionKind.Echo => "Echo",
        _ => kind.ToString(),
    };

    public static string Detail(this CompositionKind kind) => kind switch
    {
        CompositionKind.Editorial => "Two typefaces in one line, set like a spread.",
        CompositionKind.Spoken => "Each sung word takes focus; recent words settle around it.",
        CompositionKind.Cascade => "A moving column of words, arriving with the voice.",
        CompositionKind.Cutout => "Uneven type and album-colour labels, built word by word.",
        CompositionKind.Hinge => "Heavy words swing in on a hinge and fold into a stack.",
        CompositionKind.Broadside => "One strip of big type that slides along, with little words as labels.",
        CompositionKind.Portal => "The sung word in a pill; earlier words drift away as pale ones.",
        CompositionKind.Mosaic => "A collage: each word set huge, on a colour bar, or letter by letter.",
        CompositionKind.Torque => "Words wrung into place with a thick edge, stacking upwards.",
        CompositionKind.Fable => "A new shape and accent every line; each word lands its own way.",
        CompositionKind.Scattered => "Words set loose across the line.",
        CompositionKind.Constellation => "Fragments linked by hairlines, like a star chart.",
        CompositionKind.ConstellationEditorial => "Connected fragments with Editorial’s contrasting typefaces.",
        CompositionKind.Echo => "The phrase repeated at many sizes, some inverted.",
        _ => "",
    };

    /// <summary>The constellation layouts: words at scattered heights, linked by hairlines.</summary>
    public static bool HasConnections(this CompositionKind kind) =>
        kind is CompositionKind.Constellation or CompositionKind.ConstellationEditorial;

    /// <summary>A second, contrasting typeface and a wider range of sizes.</summary>
    public static bool HasEditorialTypography(this CompositionKind kind) =>
        kind is CompositionKind.Editorial or CompositionKind.ConstellationEditorial;

    /// <summary>The base of the slow float settled words keep (<see cref="OrganicMotion.IdleAmplitude"/>).</summary>
    public static double IdleFloat(this CompositionKind kind) =>
        kind.HasConnections() ? OrganicMotion.ConnectedLayoutFloat
        : kind == CompositionKind.Echo ? OrganicMotion.EchoLayoutFloat
        : OrganicMotion.DefaultLayoutFloat;
}

/// <summary>The constellations' line-level quantities (see docs/compositions/scattered.md).</summary>
public static class ConstellationLayout
{
    /// <summary>The Spread setting's default (the Mac's <c>constellationSpread</c>, 0.4–1.8).</summary>
    public const double DefaultSpread = 1.0;

    /// <summary>The Links setting's default (the Mac's <c>constellationLinkOpacity</c>, 0–0.6).</summary>
    public const double DefaultLinkOpacity = 0.20;

    /// <summary>The clearance links keep from the words they join, points.</summary>
    public const double LinkClearance = 7;

    /// <summary>Hairline width and end-dot radius, points.</summary>
    public const double LinkWidth = 0.75, DotRadius = 1;

    /// <summary>
    /// A word-timed line's base size factor: long lines are set smaller, down to 52%, by the square root of 110 over
    /// their density (characters plus three per word).
    /// </summary>
    public static double DensityScale(IReadOnlyList<Lyrics.LyricWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var density = 0;
        foreach (var word in words) density += Lyrics.Timing.TextElements.Count(word.Text) + 3;
        return Math.Min(1.0, Math.Max(0.52, Math.Sqrt(110.0 / Math.Max(density, 110))));
    }

    /// <summary>The share of the stage a constellation line is fitted into (92% × 86%).</summary>
    public static SizeD FitArea(SizeD stage) => new(stage.Width * 0.92, stage.Height * 0.86);

    /// <summary>Gap between runs, as a share of the base size.</summary>
    public static double Spacing(bool connections) => connections ? 0.06 : 0.02;

    /// <summary>Gap between rows, as a share of the base size: word-timed constellations scatter into more room.</summary>
    public static double LineSpacing(bool connections, bool wordTimed, double fullness) =>
        connections && wordTimed ? 0.34 + 0.96 * fullness : 0.30;

    /// <summary>
    /// A link's alpha: the Links opacity, brighter with the heat of either end (to at most 0.75), times the smaller of
    /// the two words' visibilities.
    /// </summary>
    public static double LinkAlpha(double opacity, double heat, double shown) => Math.Min(opacity * (1 + 0.45 * heat), 0.75) * shown;
}
