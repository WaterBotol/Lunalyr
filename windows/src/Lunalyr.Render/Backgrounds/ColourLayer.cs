// The colour-only background ("colour"): the Mac's "Album gradient" in Colour only mode (NowPlayingView's
// .albumGradient case, Lunalyr's own lines). A straight gradient through the album's stops from the left edge to the
// right, its ends tilting slowly up and down, with the shared scrim, text backing and grain.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Render.Scene;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Double3 = Lunalyr.Core.Colour.Double3;

namespace Lunalyr.Render.Backgrounds;

/// <summary>Creates <see cref="ColourLayer"/>s: the album gradient for the colour-only mode, id <c>"colour"</c>.</summary>
public sealed class ColourLayerFactory(BackgroundOptions? options = null, Logger? log = null) : IRenderLayerFactory
{
    public string Id => "colour";

    public IRenderLayer Create() => new ColourLayer(options ?? BackgroundOptions.Default, log ?? Logger.Null);
}

/// <summary>
/// The album gradient: one linear-gradient fill, a second while a palette change fades, then the scrim, the text
/// backing and the grain. Nothing is allocated per frame.
/// </summary>
public sealed class ColourLayer : IRenderLayer
{
    /// <summary>The gradient's stops: the palette's sampled colours, top-left of the cover to bottom-right.</summary>
    private const int StopCount = ColourField.MassCount;

    private readonly BackgroundOptions _options;
    private readonly Logger _log;
    private readonly WallpaperShadeDrawer _shade;
    private readonly ColourFade _fade = new(1 + StopCount);
    private readonly Vector4[] _colours = new Vector4[1 + StopCount];
    private readonly Vector4[] _scratch = new Vector4[1 + StopCount];
    private readonly GradientStop[] _stops = new GradientStop[StopCount];
    private bool _ready;
    private ID2D1GradientStopCollection? _fromStops, _toStops;
    private ID2D1LinearGradientBrush? _fromBrush, _toBrush;
    private Palette? _palette;
    private bool _hasPalette;
    private Vector2 _size;
    private double _lastNow;
    private bool _failed;

    internal ColourLayer(BackgroundOptions options, Logger log)
    {
        _options = options;
        _log = log;
        _shade = new WallpaperShadeDrawer(options);
    }

    /// <summary>Still under Reduce Motion, as on the Mac, except while a fade runs.</summary>
    public bool IsAnimating => (!_options.ReduceMotion && _options.Speed > 0) || _fade.IsFading(_lastNow) || _shade.IsEasing(_lastNow);

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        ReleaseResources();
        _ready = true;
        _size = size;
        _shade.CreateResources(dc, size, scale);
        if (_hasPalette) RebuildBrushes(dc);
        _failed = false;
    }

    public void Resize(Vector2 size, float scale)
    {
        _size = size;
        _shade.Resize(size, scale);
    }

    /// <summary>
    /// The tilt of the gradient's ends at <paramref name="wallSeconds"/>, as a share of the height: the Mac's
    /// <c>sin(flow × 0.16) × 0.2</c>, its flow clock at 0.70 × Motion speed. None under Reduce Motion.
    /// </summary>
    public static double Turn(double wallSeconds, double speed, bool reduceMotion) =>
        reduceMotion ? 0 : Math.Sin(wallSeconds * 0.70 * Math.Max(0, speed) * 0.16) * 0.2;

    public void Render(in FrameContext frame)
    {
        if (!_ready || _failed) return;
        var now = frame.WallSeconds;
        _lastNow = now;
        try
        {
            Observe(frame.Dc, frame.Scene.Palette, now);
            var turn = (float)Turn(now, _options.Speed, _options.ReduceMotion);
            var start = new Vector2(0, _size.Y * (0.22f + turn));
            var end = new Vector2(_size.X, _size.Y * (0.78f - turn));
            var whole = new Vortice.RawRectF(0, 0, _size.X, _size.Y);
            var progress = _fade.Progress(now);
            if (progress < 1 && _fromBrush is not null)
            {
                _fromBrush.StartPoint = start;
                _fromBrush.EndPoint = end;
                frame.Dc.FillRectangle(whole, _fromBrush);
            }
            if (_toBrush is not null)
            {
                _toBrush.StartPoint = start;
                _toBrush.EndPoint = end;
                _toBrush.Opacity = (float)progress;
                frame.Dc.FillRectangle(whole, _toBrush);
            }
            _fade.Current(now, _scratch);
            // The brightest stop decides: the gradient puts each one somewhere behind the lyrics.
            var brightest = 0.0;
            for (var i = 1; i < _scratch.Length; i++)
            {
                var stop = AlbumWallpaperPalette.Saturated(new Vector3(_scratch[i].X, _scratch[i].Y, _scratch[i].Z), _options.Vividness);
                brightest = Math.Max(brightest, Perceptual.Luminance(Double3.From(stop)));
            }
            _shade.Draw(frame, brightest);
        }
        catch (SharpGenException ex)
        {
            _failed = true;
            _log.Render.Warn("Colour background: drawing failed; waiting for new resources.", ex);
        }
    }

    private void Observe(ID2D1DeviceContext dc, Palette palette, double now)
    {
        if (_hasPalette && ReferenceEquals(palette, _palette)) return;
        GradientLayer.Collect(palette, _colours);
        _palette = palette;
        if (_hasPalette && _fade.Target.SequenceEqual(_colours)) return;
        if (_hasPalette) _fade.Change(_colours, now); else _fade.Reset(_colours);
        _hasPalette = true;
        RebuildBrushes(dc);
    }

    private void RebuildBrushes(ID2D1DeviceContext dc)
    {
        ReleaseBrushes();
        (_fromStops, _fromBrush) = Build(dc, _fade.Source);
        (_toStops, _toBrush) = Build(dc, _fade.Target);
    }

    private (ID2D1GradientStopCollection, ID2D1LinearGradientBrush) Build(ID2D1DeviceContext dc, ReadOnlySpan<Vector4> colours)
    {
        for (var i = 0; i < StopCount; i++)
        {
            var c = colours[1 + i];
            var rgb = AlbumWallpaperPalette.Saturated(new Vector3(c.X, c.Y, c.Z), _options.Vividness);
            _stops[i] = new GradientStop(i / (float)(StopCount - 1), new Color4(rgb.X, rgb.Y, rgb.Z, 1));
        }
        var stops = dc.CreateGradientStopCollection(_stops, Gamma.StandardRgb, ExtendMode.Clamp);
        var brush = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, Vector2.UnitX), stops);
        return (stops, brush);
    }

    private void ReleaseBrushes()
    {
        _fromBrush?.Dispose(); _fromBrush = null;
        _toBrush?.Dispose(); _toBrush = null;
        _fromStops?.Dispose(); _fromStops = null;
        _toStops?.Dispose(); _toStops = null;
    }

    public void ReleaseResources()
    {
        ReleaseBrushes();
        _shade.ReleaseResources();
        _ready = false;
    }

    public void Dispose()
    {
        ReleaseResources();
        _shade.Dispose();
    }
}
