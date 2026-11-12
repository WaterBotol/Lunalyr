// The animated album-colour background ("gradient"). Behaviour follows the Mac's AnimatedGradientView as described in
// Lunalyr.Core.Colour.ColourField (that view is largely Verso-attributed, so nothing of it is translated): a dark base,
// one soft colour mass per palette stop drifting slowly, the whole drawn small and stretched, then the shared scrim,
// text backing and grain. Written fresh for Direct2D.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Render.Scene;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Backgrounds;

/// <summary>Creates <see cref="GradientLayer"/>s: the album colour field, id <c>"gradient"</c>.</summary>
public sealed class GradientLayerFactory(BackgroundOptions? options = null, Logger? log = null) : IRenderLayerFactory
{
    public string Id => "gradient";

    public IRenderLayer Create() => new GradientLayer(options ?? BackgroundOptions.Default, log ?? Logger.Null);
}

/// <summary>
/// The album colour field. Each frame: one small offscreen pass (a clear and five radial-gradient fills, at most a
/// quarter of a megapixel) stretched over the surface, then the scrim, the text backing and the grain: four draw calls
/// on the surface, one more while a palette change fades. Nothing is allocated per frame; gradient brushes are made
/// again only when the palette changes.
/// </summary>
public sealed class GradientLayer : IRenderLayer
{
    /// <summary>The base, then one colour per mass.</summary>
    private const int ColourCount = 1 + ColourField.MassCount;

    private readonly BackgroundOptions _options;
    private readonly Logger _log;
    private readonly WallpaperShadeDrawer _shade;
    private readonly ColourFade _fade = new(ColourCount);
    private readonly Vector4[] _colours = new Vector4[ColourCount];
    private readonly Vector4[] _scratch = new Vector4[ColourCount];
    private readonly FieldSet _from = new(), _to = new();
    private readonly GradientStop[] _stops = new GradientStop[ColourField.FalloffStops];

    private ID2D1DeviceContext? _fieldDc;
    private Palette? _palette;
    private bool _hasPalette;
    private Vector2 _size;
    private float _scale = 1;
    private (int Width, int Height) _canvas;
    private bool _halfFloat;
    private double _lastNow;
    private bool _failed;
    private AmbientClock _clock;

    internal GradientLayer(BackgroundOptions options, Logger log)
    {
        _options = options;
        _log = log;
        _shade = new WallpaperShadeDrawer(options);
    }

    public bool IsAnimating => _options.Speed > 0 || _fade.IsFading(_lastNow) || _shade.IsEasing(_lastNow);

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        ReleaseResources();
        _size = size;
        _scale = scale;
        using (var device = dc.Device) _fieldDc = device.CreateDeviceContext(DeviceContextOptions.None);
        _shade.CreateResources(dc, size, scale);
        CreateCanvases();
        if (_hasPalette) RebuildBrushes();
        _failed = false;
    }

    public void Resize(Vector2 size, float scale)
    {
        _size = size;
        _scale = scale;
        _shade.Resize(size, scale);
        if (_fieldDc is not null && ColourField.CanvasSize(size.X, size.Y) != _canvas)
        {
            _from.ReleaseBitmap();
            _to.ReleaseBitmap();
            CreateCanvases();
            // Brushes are in canvas pixels, set each frame: they carry over.
        }
    }

    private void CreateCanvases()
    {
        _canvas = CanvasFor(_size, _scale);
        // Half-float keeps the five overlapping gradients free of banding; 8-bit where the device can't.
        _halfFloat = true;
        try
        {
            _from.Bitmap = CreateCanvas(Format.R16G16B16A16_Float);
            _to.Bitmap = CreateCanvas(Format.R16G16B16A16_Float);
        }
        catch (SharpGenException)
        {
            _halfFloat = false;
            _from.ReleaseBitmap();
            _from.Bitmap = CreateCanvas(Format.B8G8R8A8_UNorm);
            _to.Bitmap = CreateCanvas(Format.B8G8R8A8_UNorm);
        }
    }

    private (int Width, int Height) CanvasFor(Vector2 size, float scale) =>
        ColourField.CanvasSize(size.X, size.Y, scale, _options.PixelBudget ?? ColourFieldBudget.Pixels(_options.Economical));

    private ID2D1Bitmap1 CreateCanvas(Format format) =>
        _fieldDc!.CreateBitmap(new SizeI(_canvas.Width, _canvas.Height), IntPtr.Zero, 0,
            new BitmapProperties1(new PixelFormat(format, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));

    public void Render(in FrameContext frame)
    {
        if (_fieldDc is null || _failed) return;
        var now = frame.WallSeconds;
        _lastNow = now;
        try
        {
            Observe(frame.Scene.Palette, now);
            // Reduce Motion (the options' or the scene's) slows the field to 15%; a change of it never jumps the field.
            var clock = _clock.At(now, _options.Speed, _options.ReduceMotion || frame.Scene.ReduceMotion);
            var progress = _fade.Progress(now);
            var whole = new Vortice.RawRectF(0, 0, _size.X, _size.Y);
            if (progress < 1)
            {
                DrawField(_from, clock);
                DrawField(_to, clock);
                frame.Dc.DrawBitmap(_from.Bitmap, whole, 1, InterpolationMode.Linear, null, null);
                // The new field over the old at the fade's progress: an exact blend, as both are opaque.
                frame.Dc.DrawBitmap(_to.Bitmap, whole, (float)progress, InterpolationMode.Linear, null, null);
            }
            else
            {
                DrawField(_to, clock);
                frame.Dc.DrawBitmap(_to.Bitmap, whole, 1, InterpolationMode.Linear, null, null);
            }
            _fade.Current(now, _scratch);
            _shade.Draw(frame, ColourField.Luminance(_scratch.AsSpan(1)));
        }
        catch (SharpGenException ex)
        {
            // A lost device comes back through ReleaseResources/CreateResources; until then draw nothing.
            _failed = true;
            _log.Render.Warn("Gradient background: drawing failed; waiting for new resources.", ex);
        }
    }

    /// <summary>Takes up a new palette: at once the first time, faded after that.</summary>
    private void Observe(Palette palette, double now)
    {
        if (_hasPalette && ReferenceEquals(palette, _palette)) return;
        Collect(palette, _colours);
        _palette = palette;
        if (_hasPalette && _fade.Target.SequenceEqual(_colours)) return;
        if (_hasPalette) _fade.Change(_colours, now); else _fade.Reset(_colours);
        _hasPalette = true;
        RebuildBrushes();
    }

    /// <summary>The base and five mass colours of a palette (accents repeat when there are fewer than five).</summary>
    internal static void Collect(Palette palette, Span<Vector4> into)
    {
        into[0] = palette.Base;
        var accents = palette.Accents;
        for (var i = 1; i < into.Length; i++)
            into[i] = accents.Count == 0 ? palette.Base : accents[(i - 1) % accents.Count];
    }

    private void RebuildBrushes()
    {
        if (_fieldDc is null) return;
        Build(_from, _fade.Source);
        Build(_to, _fade.Target);
    }

    private void Build(FieldSet set, ReadOnlySpan<Vector4> colours)
    {
        set.ReleaseBrushes();
        set.Base = ToColour(Saturated(colours[0]), 1);
        for (var i = 0; i < ColourField.MassCount; i++)
        {
            var colour = Saturated(colours[1 + i]);
            for (var s = 0; s < _stops.Length; s++)
            {
                var position = s / (float)(_stops.Length - 1);
                _stops[s] = new GradientStop(position, ToColour(colour, (float)ColourField.Falloff(position)));
            }
            set.Stops[i] = _fieldDc!.CreateGradientStopCollection(_stops, (uint)_stops.Length, ColorSpace.Srgb, ColorSpace.Srgb,
                _halfFloat ? BufferPrecision.PerChannel16Float : BufferPrecision.PerChannel8Unorm, ExtendMode.Clamp,
                ColorInterpolationMode.Straight);
            set.Brushes[i] = _fieldDc.CreateRadialGradientBrush(
                new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), set.Stops[i]!);
        }
    }

    private Vector4 Saturated(Vector4 colour)
    {
        if (_options.Vividness == 1) return colour;
        var rgb = AlbumWallpaperPalette.Saturated(new Vector3(colour.X, colour.Y, colour.Z), _options.Vividness);
        return new Vector4(rgb, colour.W);
    }

    private static Color4 ToColour(Vector4 colour, float alpha) => new(colour.X, colour.Y, colour.Z, alpha);

    private void DrawField(FieldSet set, double clock)
    {
        var dc = _fieldDc!;
        dc.Target = set.Bitmap;
        dc.BeginDraw();
        dc.Clear(set.Base);
        for (var i = 0; i < ColourField.MassCount; i++)
        {
            var brush = set.Brushes[i];
            if (brush is null) continue;
            var mass = ColourField.At(i, clock, _canvas.Width, _canvas.Height);
            float x = (float)mass.X, y = (float)mass.Y, r = (float)mass.Radius;
            brush.Center = new Vector2(x, y);
            brush.RadiusX = r;
            brush.RadiusY = r;
            dc.FillRectangle(new Vortice.RawRectF(x - r, y - r, x + r, y + r), brush);
        }
        dc.EndDraw();
        // A bitmap still set as a target can't be drawn elsewhere.
        dc.Target = null;
    }

    public void ReleaseResources()
    {
        _from.Release();
        _to.Release();
        _shade.ReleaseResources();
        _fieldDc?.Dispose();
        _fieldDc = null;
    }

    public void Dispose()
    {
        ReleaseResources();
        _shade.Dispose();
    }

    /// <summary>One palette's field: its canvas and brushes.</summary>
    private sealed class FieldSet
    {
        public ID2D1Bitmap1? Bitmap;
        public readonly ID2D1GradientStopCollection?[] Stops = new ID2D1GradientStopCollection?[ColourField.MassCount];
        public readonly ID2D1RadialGradientBrush?[] Brushes = new ID2D1RadialGradientBrush?[ColourField.MassCount];
        public Color4 Base;

        public void ReleaseBrushes()
        {
            for (var i = 0; i < Brushes.Length; i++)
            {
                Brushes[i]?.Dispose(); Brushes[i] = null;
                Stops[i]?.Dispose(); Stops[i] = null;
            }
        }

        public void ReleaseBitmap()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }

        public void Release()
        {
            ReleaseBrushes();
            ReleaseBitmap();
        }
    }
}

/// <summary>
/// The colour field's clock (<see cref="ColourField.Clock"/>), kept continuous when Reduce Motion changes while it runs:
/// the Mac advances its gradient clock at the current rate, so turning Reduce Motion on or off changes how fast the field
/// moves from then on, never where it is. Until the first change it is exactly <see cref="ColourField.Clock"/>.
/// </summary>
internal struct AmbientClock
{
    private bool _started;
    private bool _reduceMotion;
    private double _offset;

    public double At(double wallSeconds, double speed, bool reduceMotion)
    {
        if (!_started)
        {
            _started = true;
            _reduceMotion = reduceMotion;
        }
        else if (reduceMotion != _reduceMotion && double.IsFinite(wallSeconds))
        {
            _offset += ColourField.Clock(wallSeconds, speed, _reduceMotion) - ColourField.Clock(wallSeconds, speed, reduceMotion);
            _reduceMotion = reduceMotion;
        }
        return ColourField.Clock(wallSeconds, speed, reduceMotion) + _offset;
    }
}
