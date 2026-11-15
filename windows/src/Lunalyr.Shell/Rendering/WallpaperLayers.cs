using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Compositions;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Rendering;

/// <summary>
/// The wallpaper's layers as the app registers them: the backgrounds and every lyric composition. The desktop
/// (<c>AppHost.CreateWallpaper</c>) and the settings preview both take them from here, so they never drift apart.
/// Backgrounds drawn by other parts of the app (GPU effects, line patterns, visualisers) join through the partial
/// methods at the end, implemented in their own files (docs/APP-SHELL.md, "Wallpaper types: hooks for other layers").
/// </summary>
public static partial class WallpaperLayers
{
    /// <summary>Every layer: <see cref="Backgrounds"/> then <see cref="Compositions"/>.</summary>
    public static IReadOnlyList<IRenderLayerFactory> Factories(IWordBreaker breaker, Logger? logger = null,
        BackgroundOptions? options = null) =>
        [.. Backgrounds(options ?? BackgroundOptions.Default, logger), .. Compositions(breaker, logger)];

    /// <summary>
    /// The backgrounds, by layer id: <c>gradient</c> (the album colour field), <c>colour</c> (the album gradient of
    /// Colour only), then whatever the hooks add (<c>shader.&lt;id&gt;</c>, <c>pattern.&lt;id&gt;</c>,
    /// <c>visualizer…</c>). The adjustments a user sets reach every one through <see cref="SceneSnapshot.Adjustments"/>;
    /// <paramref name="options"/> carries what is fixed for the run (power saving's pixel budget, grain).
    /// </summary>
    public static IReadOnlyList<IRenderLayerFactory> Backgrounds(BackgroundOptions options, Logger? logger = null)
    {
        var list = new List<IRenderLayerFactory>
        {
            new GradientLayerFactory(options, logger),
            new ColourLayerFactory(options, logger),
            new DesktopLayerFactory(options, logger),
        };
        AddEffectLayers(list, options, logger);
        AddPatternLayers(list, options, logger);
        AddVisualiserLayers(list, options, logger);
        AddMusicVideoLayers(list, options, logger);
        return list;
    }

    /// <summary>Every lyric composition.</summary>
    public static IReadOnlyList<IRenderLayerFactory> Compositions(IWordBreaker breaker, Logger? logger = null) =>
    [
        new EditorialFactory(breaker, logger),
        new SpokenFactory(breaker, logger),
        new CascadeFactory(breaker, logger),
        new CutoutFactory(breaker, logger),
        .. Lunalyr.Render.Compositions.Stage.StageFactories.All(breaker, logger),
        new ScatteredFactory(breaker, logger),
        new ConstellationFactory(breaker, logger),
        new ConstellationEditorialFactory(breaker, logger),
        new EchoFactory(breaker, logger),
    ];

    // ---- Hooks: implemented by the GPU effects / line patterns / visualiser layers in their own files ----

    /// <summary>GPU effects: one factory per effect, id <c>shader.&lt;id&gt;</c> (implement in <c>WallpaperLayers.Effects.cs</c>).</summary>
    static partial void AddEffectLayers(List<IRenderLayerFactory> into, BackgroundOptions options, Logger? logger);

    /// <summary>Line patterns: one factory per pattern, id <c>pattern.&lt;id&gt;</c> (implement in <c>WallpaperLayers.Patterns.cs</c>).</summary>
    static partial void AddPatternLayers(List<IRenderLayerFactory> into, BackgroundOptions options, Logger? logger);

    /// <summary>Visualisers: their factories, ids starting <c>visualizer</c> (implement in <c>WallpaperLayers.Visualisers.cs</c>).</summary>
    static partial void AddVisualiserLayers(List<IRenderLayerFactory> into, BackgroundOptions options, Logger? logger);

    /// <summary>Music video: the file and YouTube layers, ids <c>musicVideo</c> and <c>musicVideo.youtube</c> (in <c>WallpaperLayers.MusicVideo.cs</c>).</summary>
    static partial void AddMusicVideoLayers(List<IRenderLayerFactory> into, BackgroundOptions options, Logger? logger);
}
