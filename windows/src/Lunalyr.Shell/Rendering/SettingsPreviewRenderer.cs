using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Compositions;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Rendering;

/// <summary>The wallpaper's layers, as the app registers them (the backgrounds and every lyric composition).</summary>
public static class WallpaperLayers
{
    public static IReadOnlyList<IRenderLayerFactory> Factories(IWordBreaker breaker, Logger? logger = null) =>
    [
        new GradientLayerFactory(BackgroundOptions.Default, logger),
        new ColourLayerFactory(BackgroundOptions.Default, logger),
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
}

/// <summary>
/// The settings window's live preview (the Mac's <c>SettingsLivePreview</c>): the scene on the wallpaper, drawn by the
/// same layers into a small offscreen surface and read back as pixels. The surface is laid out as a 1080-line display
/// shrunk to the preview's size, so type and spacing keep the wallpaper's proportions. Not thread-safe: one thread draws.
/// </summary>
public sealed class SettingsPreviewRenderer : IDisposable
{
    private readonly OffscreenSurface _surface;
    private readonly Dictionary<string, IRenderLayerFactory> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IRenderLayer> _layers = new(StringComparer.Ordinal);
    private readonly IRenderLayer[] _drawn = new IRenderLayer[2];
    private double _lastWall = double.NaN;

    /// <param name="width">Preview width in pixels.</param>
    /// <param name="height">Preview height in pixels.</param>
    /// <param name="factories">The layers it can draw (<see cref="WallpaperLayers.Factories"/>).</param>
    /// <param name="hardware">Draw on the GPU (falls back to the WARP software rasteriser when there is none).</param>
    public SettingsPreviewRenderer(int width, int height, IEnumerable<IRenderLayerFactory> factories, bool hardware = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 16);
        var scale = (float)(height / 1080.0);
        try
        {
            _surface = new OffscreenSurface(width, height, scale, hardware);
        }
        catch (SharpGen.Runtime.SharpGenException) when (hardware)
        {
            _surface = new OffscreenSurface(width, height, scale, hardware: false);
        }
        foreach (var factory in factories) _factories[factory.Id] = factory;
    }

    public int Width => _surface.Width;

    public int Height => _surface.Height;

    /// <summary>
    /// Draws <paramref name="scene"/> with background <paramref name="background"/> and composition
    /// <paramref name="composition"/> (unknown ids fall back to the album colours and Editorial, as on the desktop) and
    /// reads it into <paramref name="pixels"/> (premultiplied BGRA, <see cref="Width"/> × <see cref="Height"/> × 4).
    /// </summary>
    public void Render(SceneSnapshot scene, string background, string composition, double songSeconds, bool playing,
                       double wallSeconds, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var delta = double.IsFinite(_lastWall) ? Math.Clamp(wallSeconds - _lastWall, 0, 0.25) : 1.0 / 15;
        _lastWall = wallSeconds;
        _drawn[0] = Layer(EngineWallpaperRenderer.BackgroundLayerFor(background), "gradient");
        _drawn[1] = Layer(composition, "editorial");
        _surface.Draw(_drawn, scene, songSeconds, playing, wallSeconds, delta);
        _surface.ReadPixels(pixels);
    }

    private IRenderLayer Layer(string id, string fallback)
    {
        if (!_factories.ContainsKey(id)) id = fallback;
        if (_layers.TryGetValue(id, out var layer)) return layer;
        layer = _factories[id].Create();
        _surface.Prepare(layer);
        _layers[id] = layer;
        return layer;
    }

    public void Dispose()
    {
        foreach (var layer in _layers.Values) layer.Dispose();
        _layers.Clear();
        _surface.Dispose();
    }
}
