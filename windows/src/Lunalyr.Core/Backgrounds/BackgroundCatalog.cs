// The GPU effects (the Mac's ShaderStyle) and line patterns (the Mac's BackgroundStyle) this build draws. Their ids are
// the Mac's raw values, so a pinned choice means the same effect on both apps; everything else is Windows' own. The
// Mac's titles, cost tiers and "character" table sit on Verso-attributed lines (ShaderBackgroundView.swift,
// FractalBackgroundView.swift), so none of them is carried over: the titles below name what Windows' own renders show,
// the cost tiers are Windows measurements (docs/measurements/gpu-effects) and each character was judged from Windows'
// previews (docs/previews/gpu-effects).

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

    /// <summary>Drawn at the smaller pixel budget; left out of "Custom Mix" while power is being saved.</summary>
    Heavy,
}

/// <summary>
/// Where an effect or pattern sits for "Custom Mix", each 0...1: <paramref name="Energy"/> (still to lively),
/// <paramref name="Density"/> (open to busy) and <paramref name="Organic"/> (geometric to flowing).
/// </summary>
public readonly record struct BackgroundCharacter(double Energy, double Density, double Organic);

/// <summary>One GPU effect: its Mac raw value, Windows' name for it, its measured cost and its character.</summary>
public sealed record GpuEffect(string Id, string Title, GpuEffectCost Cost, BackgroundCharacter Character)
{
    /// <summary>The render layer that draws it, <c>shader.&lt;id&gt;</c>.</summary>
    public string LayerId => GpuEffectCatalog.LayerPrefix + Id;

    public bool Heavy => Cost == GpuEffectCost.Heavy;
}

/// <summary>One line pattern: its Mac raw value (an integer on the Mac), its id (the Mac's case name), Windows' name for it and its character.</summary>
public sealed record LinePattern(int RawValue, string Id, string Title, BackgroundCharacter Character)
{
    /// <summary>The render layer that draws it, <c>pattern.&lt;id&gt;</c>.</summary>
    public string LayerId => LinePatternCatalog.LayerPrefix + Id;
}

/// <summary>The GPU effects, in the Mac's order.</summary>
public static class GpuEffectCatalog
{
    /// <summary>Layer ids are this prefix and the effect's id, e.g. <c>shader.plasma</c> (docs/APP-SHELL.md, "Wallpaper types").</summary>
    public const string LayerPrefix = "shader.";

    /// <summary>Drawn when an effect is missing or broken: the plainest and lightest.</summary>
    public const string Fallback = "plasma";

    private static BackgroundCharacter C(double energy, double density, double organic) => new(energy, density, organic);

    public static IReadOnlyList<GpuEffect> All { get; } =
    [
        new("plasma", "Soft Waves", GpuEffectCost.Light, C(0.10, 0.20, 0.90)),
        new("kaleido", "Mirror Bloom", GpuEffectCost.Light, C(0.40, 0.65, 0.35)),
        new("aurora", "Northern Lights", GpuEffectCost.Medium, C(0.40, 0.55, 0.75)),
        new("fractal", "Julia Set", GpuEffectCost.Light, C(0.50, 0.80, 0.45)),
        new("liquid", "Lava Lamp", GpuEffectCost.Light, C(0.30, 0.35, 0.90)),
        new("warp", "Marbling", GpuEffectCost.Heavy, C(0.35, 0.80, 0.85)),
        new("nebula", "Star Cloud", GpuEffectCost.Heavy, C(0.35, 0.85, 0.90)),
        new("voronoi", "Cells", GpuEffectCost.Heavy, C(0.30, 0.75, 0.25)),
        new("orbs", "Lanterns", GpuEffectCost.Light, C(0.20, 0.20, 0.80)),
        new("silk", "Folds", GpuEffectCost.Medium, C(0.20, 0.30, 0.75)),
        new("waves", "Open Sea", GpuEffectCost.Heavy, C(0.40, 0.40, 0.80)),
        new("helix", "Double Helix", GpuEffectCost.Medium, C(0.45, 0.35, 0.25)),
        new("ripple", "Still Water", GpuEffectCost.Light, C(0.20, 0.30, 0.65)),
        new("mirror", "Polished Floor", GpuEffectCost.Light, C(0.15, 0.25, 0.55)),
        new("wormhole", "Tunnel", GpuEffectCost.Light, C(0.85, 0.60, 0.30)),
        new("rings", "Halos", GpuEffectCost.Light, C(0.55, 0.40, 0.10)),
        new("vortex", "Whirlpool", GpuEffectCost.Light, C(0.60, 0.45, 0.60)),
        new("orbit", "Planets", GpuEffectCost.Light, C(0.35, 0.25, 0.15)),
        new("bubbles", "Glass Bubbles", GpuEffectCost.Medium, C(0.30, 0.35, 0.40)),
        new("galaxy", "Spiral Galaxy", GpuEffectCost.Light, C(0.25, 0.40, 0.60)),
        new("strata", "Ridgelines", GpuEffectCost.Medium, C(0.30, 0.55, 0.70)),
        new("clouds", "Cloud Layers", GpuEffectCost.Heavy, C(0.25, 0.60, 0.95)),
        new("curtains", "Light Curtains", GpuEffectCost.Light, C(0.45, 0.60, 0.70)),
        new("ribbons", "Tubes", GpuEffectCost.Light, C(0.50, 0.55, 0.55)),
        new("prism", "Spectrum", GpuEffectCost.Light, C(0.20, 0.30, 0.30)),
        new("tidal", "Tide Lines", GpuEffectCost.Medium, C(0.35, 0.65, 0.85)),
        new("lightTrails", "Streaks", GpuEffectCost.Medium, C(0.65, 0.15, 0.35)),
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
    public static IEnumerable<GpuEffect> Affordable(bool sparing) => sparing ? All.Where(e => !e.Heavy) : All;
}

/// <summary>The line patterns, in the Mac's order (their raw values are 0...17).</summary>
public static class LinePatternCatalog
{
    /// <summary>Layer ids are this prefix and the pattern's id, e.g. <c>pattern.ripples</c>.</summary>
    public const string LayerPrefix = "pattern.";

    private static BackgroundCharacter C(double energy, double density, double organic) => new(energy, density, organic);

    public static IReadOnlyList<LinePattern> All { get; } =
    [
        new(0, "ripples", "Rings on Water", C(0.35, 0.30, 0.45)),
        new(1, "contours", "Topography", C(0.20, 0.70, 0.70)),
        new(2, "waveBands", "Wave Lines", C(0.30, 0.40, 0.65)),
        new(3, "bubbles", "Rising Circles", C(0.35, 0.40, 0.45)),
        new(4, "orbits", "Ellipses", C(0.30, 0.55, 0.25)),
        new(5, "starfield", "Stars", C(0.40, 0.25, 0.50)),
        new(6, "petals", "Flower", C(0.25, 0.40, 0.75)),
        new(7, "flowField", "Currents", C(0.30, 0.45, 0.85)),
        new(8, "constellation", "Star Map", C(0.20, 0.30, 0.35)),
        new(9, "hexGrid", "Honeycomb", C(0.20, 0.60, 0.10)),
        new(10, "moire", "Interference", C(0.40, 0.90, 0.15)),
        new(11, "perspectiveGrid", "Floor Grid", C(0.55, 0.50, 0.05)),
        new(12, "fractalTree", "Branches", C(0.15, 0.25, 0.90)),
        new(13, "lowPoly", "Facets", C(0.15, 0.30, 0.20)),
        new(14, "spiralArms", "Spirals", C(0.35, 0.35, 0.65)),
        new(15, "diagonalRain", "Rainfall", C(0.75, 0.55, 0.35)),
        new(16, "circuitry", "Circuit Board", C(0.15, 0.55, 0.05)),
        new(17, "crosshatch", "Hatching", C(0.25, 0.50, 0.30)),
    ];

    public static LinePattern? Find(int rawValue) => rawValue >= 0 && rawValue < All.Count ? All[rawValue] : null;

    /// <summary>The pattern with id <paramref name="id"/>; a number is read as the Mac's raw value ("3" is bubbles).</summary>
    public static LinePattern? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var pattern in All) if (pattern.Id == id) return pattern;
        return int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var raw) ? Find(raw) : null;
    }

    public static string LayerId(int rawValue) => Find(rawValue)?.LayerId ?? throw new ArgumentOutOfRangeException(nameof(rawValue));

    public static LinePattern? FromLayerId(string? layerId) =>
        layerId is not null && layerId.StartsWith(LayerPrefix, StringComparison.Ordinal) ? Find(layerId[LayerPrefix.Length..]) : null;
}
