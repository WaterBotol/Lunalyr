using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Compositions;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Rendering;

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
    private IRenderLayer? _card;
    private string? _cardId;
    private (int Width, int Height, float Scale) _cardSize;

    /// <summary>
    /// The floating song title card's window on the picture (the Mac's FloatingScene, b11a1ab3): its box in picture pixels,
    /// the picture pixels to a point of its display, and the floating lyrics' text size.
    /// </summary>
    public sealed record PictureCard(int X, int Y, int Width, int Height, float Scale, double TextSize);

    /// <param name="width">Preview width in pixels.</param>
    /// <param name="height">Preview height in pixels.</param>
    /// <param name="factories">The layers it can draw (<see cref="WallpaperLayers.Factories"/>).</param>
    /// <param name="hardware">Draw on the GPU (falls back to the WARP software rasteriser when there is none).</param>
    /// <param name="layoutHeight">
    /// The display height the picture is laid out as (1080 lines: the wallpaper's proportions). The welcome's layout tiles
    /// use a smaller one, so a tile's line is set larger than a desktop's would be, as the Mac's tiles are.
    /// </param>
    public SettingsPreviewRenderer(int width, int height, IEnumerable<IRenderLayerFactory> factories, bool hardware = true, double layoutHeight = 1080)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(layoutHeight, 16);
        var scale = (float)(height / layoutHeight);
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
    /// <param name="floating">
    /// The lyrics float: the desktop's title card is not drawn (the picture stands for the floating scene), and
    /// <paramref name="card"/>, when given, is drawn settled in its window's box.
    /// </param>
    public void Render(SceneSnapshot scene, string background, string composition, double songSeconds, bool playing,
                       double wallSeconds, byte[] pixels, bool floating = false, PictureCard? card = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var delta = double.IsFinite(_lastWall) ? Math.Clamp(wallSeconds - _lastWall, 0, 0.25) : 1.0 / 15;
        _lastWall = wallSeconds;
        _drawn[0] = Layer(EngineWallpaperRenderer.BackgroundLayerFor(background), "gradient");
        _drawn[1] = Layer(composition, "editorial");
        var shown = floating && scene.Style.OpeningTitle ? scene with { Style = scene.Style with { OpeningTitle = false } } : scene;
        _surface.Draw(_drawn, shown, songSeconds, playing, wallSeconds, delta);
        if (floating && card is { Width: >= 4, Height: >= 4 }) DrawCard(scene, composition, card, songSeconds, playing, wallSeconds, delta);
        _surface.ReadPixels(pixels);
    }

    /// <summary>The title card settled (at its desktop size, scaled to the picture), stacked or as one row, in its box.</summary>
    private void DrawCard(SceneSnapshot scene, string composition, PictureCard card, double songSeconds, bool playing, double wallSeconds,
                          double delta)
    {
        var id = _factories.ContainsKey(composition) ? composition : "editorial";
        var size = (card.Width, card.Height, card.Scale);
        if (_card is null || _cardId != id)
        {
            _card?.Dispose();
            _card = _factories[id].Create();
            _cardId = id;
            _card.CreateResources(_surface.Context, _surface.DWrite, new System.Numerics.Vector2(card.Width, card.Height), card.Scale);
            _cardSize = size;
        }
        else if (_cardSize != size)
        {
            _card.Resize(new System.Numerics.Vector2(card.Width, card.Height), card.Scale);
            _cardSize = size;
        }
        var style = scene.Style with { FloatingTextSize = card.TextSize, FloatingInset = 0, FloatingTitleOnly = true, FloatingTitleSettled = true };
        _surface.DrawInset(_card, scene with { Style = style }, card.X, card.Y, card.Width, card.Height, card.Scale, songSeconds, playing,
                           wallSeconds, delta);
    }

    /// <summary>Uses <paramref name="factory"/> for its id from now on (the imported picture or video), dropping any instance made from another.</summary>
    public void Use(IRenderLayerFactory? factory)
    {
        if (factory is null || (_factories.TryGetValue(factory.Id, out var known) && ReferenceEquals(known, factory))) return;
        _factories[factory.Id] = factory;
        if (_layers.Remove(factory.Id, out var old)) old.Dispose();
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
        _card?.Dispose();
        _card = null;
        _surface.Dispose();
    }
}
