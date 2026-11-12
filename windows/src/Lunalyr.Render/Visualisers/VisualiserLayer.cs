// The Visualisers wallpaper type: the Mac's native visualiser scenes (AudioVisualizerView, with NowPlayingView's
// crossfade between scenes and its scrim and text backing), drawn with Direct3D 11 into a texture at the scene's pixel
// budget and laid onto the surface by Direct2D.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Visualisers;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Scene;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Palette = Lunalyr.Render.Scene.Palette;

namespace Lunalyr.Render.Visualisers;

/// <summary>Creates <see cref="VisualiserLayer"/>s. Every layer of one factory shares its <see cref="VisualiserHost"/>.</summary>
public sealed class VisualiserLayerFactory(VisualiserHost host, BackgroundOptions? options = null, Logger? log = null,
    string id = VisualiserLayerFactory.LayerId) : IRenderLayerFactory
{
    /// <summary>The Visualisers type's layer id (the Mac's <c>backgroundMode</c> value).</summary>
    public const string LayerId = "visualizer";

    public string Id => id;

    public VisualiserHost Host => host;

    public IRenderLayer Create() => new VisualiserLayer(host, options ?? BackgroundOptions.Default, log ?? Logger.Null);
}

/// <summary>
/// One surface's visualiser. A scene change crossfades over <see cref="SceneFade"/> seconds, both scenes drawing
/// meanwhile, as the Mac's wallpaper does. Never allocates per frame once a scene's texture exists.
/// </summary>
public sealed class VisualiserLayer : IRenderLayer
{
    /// <summary>A scene change's crossfade (the Mac's surface fade).</summary>
    public const double SceneFade = 0.8;

    private static int _surfaces;

    private readonly VisualiserHost _host;
    private readonly BackgroundOptions _options;
    private readonly Logger _log;
    private readonly WallpaperShadeDrawer _shade;
    private readonly string _display;
    private VisualiserGpu? _gpu;
    private nint _device;
    private VisualiserScene? _scene, _outgoing;
    private double _fadeStart = double.NegativeInfinity;
    private Vector2 _size;
    private bool _ready, _failed;
    private Palette? _palette;
    private AlbumWallpaperPalette _cover = AlbumWallpaperPalette.Fallback;
    private bool _hasCover;
    private Vector3[] _coverStops = [];
    private double _lastNow;

    internal VisualiserLayer(VisualiserHost host, BackgroundOptions options, Logger log)
    {
        _host = host;
        _options = options;
        _log = log;
        _shade = new WallpaperShadeDrawer(options);
        _display = "surface " + Interlocked.Increment(ref _surfaces);
    }

    /// <summary>Visualisers always move (an idle scene drifts too).</summary>
    public bool IsAnimating => true;

    /// <summary>The scene drawn last (tests and diagnostics).</summary>
    internal VisualiserScene? Current => _scene;

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        ReleaseResources();
        _ready = true;
        _failed = false;
        _size = size;
        _shade.CreateResources(dc, size, scale);
    }

    public void Resize(Vector2 size, float scale)
    {
        _size = size;
        _shade.Resize(size, scale);
    }

    public void Render(in FrameContext frame)
    {
        if (!_ready || _failed) return;
        try
        {
            Draw(frame);
        }
        catch (SharpGenException ex)
        {
            _failed = true;
            _log.Render.Warn("Visualiser: drawing failed; waiting for new resources.", ex);
        }
    }

    private void Draw(in FrameContext frame)
    {
        var dc = frame.Dc;
        var gpu = Gpu(dc);
        var settings = _host.Settings();
        var (speed, vividness) = _host.Adjustments(frame.Scene);
        var sound = _host.Sound;
        var clock = sound?.Now ?? AudioAnalyzer.Now();
        _lastNow = frame.WallSeconds;
        Observe(frame.Scene.Palette);

        if (_scene is null || _scene.Style != settings.Style)
        {
            // The outgoing scene fades out under the new one; a third change mid-fade drops the oldest.
            if (_scene?.Bitmap is not null)
            {
                _outgoing?.Dispose();
                _outgoing = _scene;
                _fadeStart = frame.WallSeconds;
            }
            else
            {
                _scene?.Dispose();
            }
            _scene = new VisualiserScene(_host, settings.Style, _display);
        }

        var economical = _options.PixelBudget is { } budget && double.IsFinite(budget)
            ? Math.Sqrt(Math.Max(0.05, budget / Math.Max(1, _size.X * _size.Y)))
            : _options.Economical ? Math.Sqrt(0.6) : 1;
        var input = new SceneFrame
        {
            Flow = _host.Flow(frame.WallSeconds, _options.Speed * speed),
            Clock = clock,
            Response = new VisualizerResponse(frame.Scene.ReduceMotion || _options.ReduceMotion, settings.DimFlashingLights, settings.Punch),
            Sensitivity = settings.Sensitivity,
            Vividness = _options.Vividness * vividness,
            PaletteKey = _hasCover ? _palette : null,
            CoverStops = _coverStops,
            StarTrailsPalette = settings.StarTrailsAlbumColours && _hasCover ? _cover : null,
            Word = _scene.Style == VisualizerStyle.StarTrails ? Word(frame) : null,
            NativeWidth = _size.X,
            NativeHeight = _size.Y,
            PixelScale = Math.Min(1, economical),
            TargetFps = _host.TargetFps(),
        };
        var audio = sound is null ? new AudioSnapshot() : sound.Latest();
        var fade = Math.Clamp((frame.WallSeconds - _fadeStart) / SceneFade, 0, 1);
        if (_outgoing is not null && fade >= 1)
        {
            _outgoing.Dispose();
            _outgoing = null;
        }
        _outgoing?.Draw(gpu, dc, input, audio);
        _scene.Draw(gpu, dc, input, audio);

        var whole = new Vortice.RawRectF(0, 0, _size.X, _size.Y);
        var transform = dc.Transform;
        dc.Transform = Matrix3x2.Identity;
        if (_outgoing?.Bitmap is { } old) dc.DrawBitmap(old, whole, 1, InterpolationMode.Linear, null, null);
        if (_scene.Bitmap is { } bitmap)
            dc.DrawBitmap(bitmap, whole, _outgoing is null ? 1 : (float)fade, InterpolationMode.Linear, null, null);
        dc.Transform = transform;
        _shade.Draw(frame, LyricLegibility.VisualizerLuminance(_cover));
    }

    /// <summary>The newest sung word, which Star Trails lights stars by.</summary>
    private static StarTrailsLyricWord? Word(in FrameContext frame) =>
        frame.Scene.Lyrics is LyricResult.Synced synced && double.IsFinite(frame.SongSeconds)
            ? StarTrailsLyricWord.Newest(synced.Lines, frame.SongSeconds)
            : null;

    /// <summary>The cover's colours, worked out once per palette.</summary>
    private void Observe(Palette palette)
    {
        if (ReferenceEquals(palette, _palette)) return;
        _palette = palette;
        _hasCover = palette.ArtworkStops.Count == 9;
        if (!_hasCover)
        {
            _cover = AlbumWallpaperPalette.Fallback;
            _coverStops = [];
            return;
        }
        var grid = new Double3[9];
        for (var i = 0; i < 9; i++) grid[i] = AlbumColourGrid.Hsb(Double3.From(palette.ArtworkStops[i]));
        _cover = new AlbumWallpaperPalette(grid);
        _coverStops = [.. _cover.Stops];
    }

    /// <summary>The visualiser GPU objects for the device behind <paramref name="dc"/>, made again when it changes.</summary>
    private VisualiserGpu Gpu(ID2D1DeviceContext dc)
    {
        using var d2d = dc.Device;
        using var d2d2 = d2d.QueryInterface<ID2D1Device2>();
        using var dxgi = d2d2.DxgiDevice;
        var d3d = dxgi.QueryInterface<ID3D11Device>();
        if (_gpu is not null && d3d.NativePointer == _device)
        {
            d3d.Dispose();
            return _gpu;
        }
        ReleaseGpu();
        _device = d3d.NativePointer;
        _gpu = new VisualiserGpu(d3d);
        return _gpu;
    }

    private void ReleaseGpu()
    {
        _scene?.Release();
        _outgoing?.Dispose();
        _outgoing = null;
        if (_gpu is not null)
        {
            var device = _gpu.Device;
            _gpu.Dispose();
            device.Dispose();
        }
        _gpu = null;
        _device = 0;
    }

    public void ReleaseResources()
    {
        ReleaseGpu();
        _shade.ReleaseResources();
        _ready = false;
    }

    public void Dispose()
    {
        ReleaseResources();
        _scene?.Dispose();
        _scene = null;
        _shade.Dispose();
    }
}
