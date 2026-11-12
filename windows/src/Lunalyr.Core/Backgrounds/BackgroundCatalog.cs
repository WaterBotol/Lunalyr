// The GPU effects (the Mac's ShaderStyle) and line patterns (the Mac's BackgroundStyle) this build draws, with the
// Mac's raw values as ids so a setting or a per-song choice means the same thing on both. The ids and titles sit on
// Verso-attributed lines on the Mac; they are kept because the settings and Match the music must agree across the two
// apps (docs/PROVENANCE.md, open question). Everything else here is Windows' own: the cost tiers come from Windows
// measurements (docs/milestones/M6.md), not the Mac's table, and nothing of the Mac's matching data is carried over.

namespace Lunalyr.Core.Backgrounds;

/// <summary>
/// How much a GPU effect costs, measured on this port's shaders: the whole frame on the Intel integrated GPU at
/// 2560×1440 with the standard pixel budget (docs/measurements/gpu-effects): Light up to 1.1 ms, Medium up to 1.8 ms,
/// Heavy above.
/// </summary>
public enum GpuEffectCost
{
    /// <summary>Cheap enough to leave running on battery or an integrated GPU.</summary>
    Light,
    Medium,

    /// <summary>Drawn at the smaller pixel budget; swapped for a lighter effect while power is being saved.</summary>
    Heavy,
}

/// <summary>One GPU effect: its Mac raw value, its title and its measured cost.</summary>
public sealed record GpuEffect(string Id, string Title, GpuEffectCost Cost)
{
    /// <summary>The render layer that draws it (registered by the app; see <see cref="GpuEffectCatalog.LayerId"/>).</summary>
    public string LayerId => GpuEffectCatalog.LayerPrefix + Id;
}

/// <summary>One line pattern: its Mac raw value (an integer on the Mac), a name for it and its title.</summary>
public sealed record LinePattern(int RawValue, string Id, string Title)
{
    public string LayerId => LinePatternCatalog.LayerPrefix + Id;
}

/// <summary>The GPU effects, in the Mac's order.</summary>
public static class GpuEffectCatalog
{
    /// <summary>Layer ids are this prefix and the effect's id, e.g. <c>shader:plasma</c>.</summary>
    public const string LayerPrefix = "shader:";

    /// <summary>Drawn when an effect is missing or broken: the plainest and lightest.</summary>
    public const string Fallback = "plasma";

    public static IReadOnlyList<GpuEffect> All { get; } =
    [
        new("plasma", "Plasma", GpuEffectCost.Light),
        new("kaleido", "Kaleidoscope", GpuEffectCost.Light),
        new("aurora", "Aurora", GpuEffectCost.Medium),
        new("fractal", "Fractal", GpuEffectCost.Light),
        new("liquid", "Liquid", GpuEffectCost.Light),
        new("warp", "Marble", GpuEffectCost.Heavy),
        new("nebula", "Nebula", GpuEffectCost.Heavy),
        new("voronoi", "Voronoi", GpuEffectCost.Heavy),
        new("orbs", "Orbs", GpuEffectCost.Light),
        new("silk", "Silk", GpuEffectCost.Medium),
        new("waves", "Waves", GpuEffectCost.Heavy),
        new("helix", "Helix", GpuEffectCost.Medium),
        new("ripple", "Ripple", GpuEffectCost.Light),
        new("mirror", "Mirror", GpuEffectCost.Light),
        new("wormhole", "Wormhole", GpuEffectCost.Light),
        new("rings", "Rings", GpuEffectCost.Light),
        new("vortex", "Vortex", GpuEffectCost.Light),
        new("orbit", "Orbit", GpuEffectCost.Light),
        new("bubbles", "Bubbles", GpuEffectCost.Medium),
        new("galaxy", "Galaxy", GpuEffectCost.Light),
        new("strata", "Strata", GpuEffectCost.Medium),
        new("clouds", "Clouds", GpuEffectCost.Heavy),
        new("curtains", "Curtains", GpuEffectCost.Light),
        new("ribbons", "Ribbons", GpuEffectCost.Light),
        new("prism", "Prism", GpuEffectCost.Light),
        new("tidal", "Tidal", GpuEffectCost.Medium),
        new("lightTrails", "Light Trails", GpuEffectCost.Medium),
    ];

    public static GpuEffect? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var effect in All) if (effect.Id == id) return effect;
        return null;
    }

    /// <summary>The layer id for effect <paramref name="id"/>.</summary>
    public static string LayerId(string id) => LayerPrefix + id;

    /// <summary>The effect a layer id names, or null when it names none.</summary>
    public static GpuEffect? FromLayerId(string? layerId) =>
        layerId is not null && layerId.StartsWith(LayerPrefix, StringComparison.Ordinal) ? Find(layerId[LayerPrefix.Length..]) : null;

    /// <summary>The effects cheap enough to leave running while power is being saved.</summary>
    public static IEnumerable<GpuEffect> Affordable(bool sparing) => sparing ? All.Where(e => e.Cost != GpuEffectCost.Heavy) : All;
}

/// <summary>The line patterns, in the Mac's order (their raw values are 0...17).</summary>
public static class LinePatternCatalog
{
    /// <summary>Layer ids are this prefix and the pattern's id, e.g. <c>pattern:ripples</c>.</summary>
    public const string LayerPrefix = "pattern:";

    public static IReadOnlyList<LinePattern> All { get; } =
    [
        new(0, "ripples", "Ripples"),
        new(1, "contours", "Contours"),
        new(2, "waveBands", "Wave Bands"),
        new(3, "bubbles", "Bubbles"),
        new(4, "orbits", "Orbits"),
        new(5, "starfield", "Starfield"),
        new(6, "petals", "Petals"),
        new(7, "flowField", "Flow Field"),
        new(8, "constellation", "Constellation"),
        new(9, "hexGrid", "Hex Grid"),
        new(10, "moire", "Moiré"),
        new(11, "perspectiveGrid", "Perspective Grid"),
        new(12, "fractalTree", "Fractal Tree"),
        new(13, "lowPoly", "Low Poly"),
        new(14, "spiralArms", "Spiral Arms"),
        new(15, "diagonalRain", "Rain"),
        new(16, "circuitry", "Circuitry"),
        new(17, "crosshatch", "Crosshatch"),
    ];

    public static LinePattern? Find(int rawValue) => rawValue >= 0 && rawValue < All.Count ? All[rawValue] : null;

    public static LinePattern? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var pattern in All) if (pattern.Id == id) return pattern;
        return null;
    }

    public static string LayerId(int rawValue) => Find(rawValue)?.LayerId ?? throw new ArgumentOutOfRangeException(nameof(rawValue));

    public static LinePattern? FromLayerId(string? layerId) =>
        layerId is not null && layerId.StartsWith(LayerPrefix, StringComparison.Ordinal) ? Find(layerId[LayerPrefix.Length..]) : null;
}
