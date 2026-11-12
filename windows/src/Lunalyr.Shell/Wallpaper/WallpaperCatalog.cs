// The effects and patterns this build can draw, as the settings menus and "Match the music" see them. The layers that
// draw them fill the lists from their own files by implementing the partial methods below (docs/APP-SHELL.md,
// "Wallpaper types: hooks for other layers"); nothing here names an effect or a pattern itself.

using Lunalyr.Core.Compositions;

namespace Lunalyr.Shell.Wallpaper;

/// <summary>One GPU effect or line pattern: its id, what the menus call it and where it sits for "Match the music".</summary>
/// <param name="Id">Stable id, saved in <c>shaderStyle</c> / <c>patternStyle</c>; the layer is registered as
/// <c>shader.&lt;Id&gt;</c> or <c>pattern.&lt;Id&gt;</c>.</param>
/// <param name="Title">The menu's name for it ("Aurora").</param>
/// <param name="Character">Where it sits on energy, density and organic (0...1 each).</param>
/// <param name="Heavy">Expensive per pixel: "Match the music" leaves it out while power is being saved.</param>
/// <param name="Detail">An optional caption for the menu's help text.</param>
public sealed record WallpaperStyle(string Id, string Title, WallpaperCharacter Character, bool Heavy = false, string? Detail = null)
{
    public WallpaperCandidate Candidate => new(Id, Character, Heavy);
}

/// <summary>
/// The GPU effects and line patterns registered in this build. Empty until their layers land: the Wallpaper page then
/// offers only "Match the music" in their menus, and the types draw the album colours.
/// </summary>
public static partial class WallpaperCatalog
{
    private static readonly Lazy<IReadOnlyList<WallpaperStyle>> LazyEffects = new(() => { var list = new List<WallpaperStyle>(); AddEffects(list); return Distinct(list); });
    private static readonly Lazy<IReadOnlyList<WallpaperStyle>> LazyPatterns = new(() => { var list = new List<WallpaperStyle>(); AddPatterns(list); return Distinct(list); });

    /// <summary>Every GPU effect, in menu order (the "GPU effect" menu and the <c>shader</c> type).</summary>
    public static IReadOnlyList<WallpaperStyle> Effects => LazyEffects.Value;

    /// <summary>Every line pattern, in menu order (the "Pattern" menu and the <c>pattern</c> type).</summary>
    public static IReadOnlyList<WallpaperStyle> Patterns => LazyPatterns.Value;

    /// <summary>Whether a visualiser is built in (set by the visualiser's own file; until then the type shows a placeholder).</summary>
    public static bool HasVisualisers
    {
        get
        {
            var has = false;
            ProvidesVisualisers(ref has);
            return has;
        }
    }

    /// <summary>The effect with <paramref name="id"/>, or null.</summary>
    public static WallpaperStyle? Effect(string? id) => Find(Effects, id);

    /// <summary>The pattern with <paramref name="id"/>, or null.</summary>
    public static WallpaperStyle? Pattern(string? id) => Find(Patterns, id);

    private static WallpaperStyle? Find(IReadOnlyList<WallpaperStyle> styles, string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var style in styles)
            if (style.Id == id) return style;
        return null;
    }

    // A duplicate id would make two menu entries save the same value; the first wins.
    private static WallpaperStyle[] Distinct(List<WallpaperStyle> list) => [.. list.DistinctBy(s => s.Id, StringComparer.Ordinal)];

    // ---- Hooks: implemented by the GPU effects / line patterns / visualiser layers in their own files ----

    /// <summary>GPU effects: add one entry per effect (implement in <c>WallpaperCatalog.Effects.cs</c>).</summary>
    static partial void AddEffects(List<WallpaperStyle> into);

    /// <summary>Line patterns: add one entry per pattern (implement in <c>WallpaperCatalog.Patterns.cs</c>).</summary>
    static partial void AddPatterns(List<WallpaperStyle> into);

    /// <summary>Visualisers: set <paramref name="has"/> to true (implement in <c>WallpaperCatalog.Visualisers.cs</c>).</summary>
    static partial void ProvidesVisualisers(ref bool has);

    /// <summary>The background layer the Visualisers type draws for <paramref name="settings"/>: "visualizer" unless the visualiser's hook names another (a scene's own id).</summary>
    public static string VisualiserLayerFor(Settings.AppSettings settings)
    {
        var id = "visualizer";
        VisualiserLayer(settings, ref id);
        return id;
    }

    /// <summary>Visualisers: the layer id for the scene <paramref name="settings"/> choose (implement in <c>WallpaperCatalog.Visualisers.cs</c>).</summary>
    static partial void VisualiserLayer(Settings.AppSettings settings, ref string layerId);
}
