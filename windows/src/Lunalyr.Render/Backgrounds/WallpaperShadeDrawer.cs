using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Render.Scene;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Backgrounds;

/// <summary>
/// What a generated wallpaper lays over itself so white and pastel lyrics stay readable (the Mac's
/// <c>WallpaperShade</c>): a scrim from top to bottom, and the text backing, a soft stage-shaped shade behind the lyrics
/// as strong as the backdrop needs, present only while the stage shows something. Also the grain. Three draw calls at
/// most; nothing is allocated per frame.
/// </summary>
internal sealed class WallpaperShadeDrawer : IDisposable
{
    private const int GrainSide = 128;

    private readonly BackgroundOptions _options;
    private ID2D1GradientStopCollection? _scrimStops;
    private ID2D1LinearGradientBrush? _scrim;
    private ID2D1Bitmap1? _backing;
    private ID2D1Bitmap1? _grainBitmap;
    private ID2D1BitmapBrush1? _grain;
    private Vector2 _size;
    private float _scale = 1;

    /// <summary>The stage's presence (0...1), easing towards whether it shows anything.</summary>
    private double _presence;

    private double _presenceFrom;
    private double _presenceTarget = -1;
    private double _presenceChangedAt;

    public WallpaperShadeDrawer(BackgroundOptions options) => _options = options;

    public void CreateResources(ID2D1DeviceContext dc, Vector2 size, float scale)
    {
        ReleaseResources();
        var strength = Math.Max(0, _options.ScrimStrength);
        var scrim = WallpaperShade.Scrim;
        var stops = new GradientStop[scrim.Count];
        for (var i = 0; i < scrim.Count; i++)
            stops[i] = new GradientStop(i / (float)(scrim.Count - 1), new Color4(0, 0, 0, (float)Math.Min(1, scrim[i] * strength)));
        _scrimStops = dc.CreateGradientStopCollection(stops, Gamma.StandardRgb, ExtendMode.Clamp);
        _scrim = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, new Vector2(0, 1)), _scrimStops);

        var shade = LyricLegibility.ShadeImage(LyricLegibility.ShadeColumns, LyricLegibility.ShadeRows)!;
        _backing = CreateBitmap(dc, LyricLegibility.ShadeColumns, LyricLegibility.ShadeRows, shade);

        // Grain: each pixel faintly white or faintly black (premultiplied), so it lifts and lowers about evenly.
        var grain = new byte[GrainSide * GrainSide * 4];
        var random = new Random(0x4C756E61);
        for (var i = 0; i < GrainSide * GrainSide; i++)
        {
            var alpha = (byte)random.Next(0, 4);
            var white = random.Next(2) == 0;
            var value = white ? alpha : (byte)0;
            grain[i * 4] = value; grain[i * 4 + 1] = value; grain[i * 4 + 2] = value; grain[i * 4 + 3] = alpha;
        }
        _grainBitmap = CreateBitmap(dc, GrainSide, GrainSide, grain);
        _grain = dc.CreateBitmapBrush(_grainBitmap, new BitmapBrushProperties1(ExtendMode.Wrap, ExtendMode.Wrap, InterpolationMode.NearestNeighbor), null);
        Resize(size, scale);
    }

    private static unsafe ID2D1Bitmap1 CreateBitmap(ID2D1DeviceContext dc, int width, int height, byte[] premultipliedBgra)
    {
        fixed (byte* data = premultipliedBgra)
        {
            return dc.CreateBitmap(new SizeI(width, height), (IntPtr)data, (uint)(width * 4),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        }
    }

    public void Resize(Vector2 size, float scale)
    {
        _size = size;
        _scale = scale > 0 && float.IsFinite(scale) ? scale : 1;
        if (_scrim is not null)
        {
            _scrim.StartPoint = Vector2.Zero;
            _scrim.EndPoint = new Vector2(0, size.Y);
        }
        // Grain cells are a point across, so it reads the same at any DPI.
        if (_grain is not null) _grain.Transform = Matrix3x2.CreateScale(MathF.Max(1, MathF.Round(_scale)));
    }

    /// <summary>
    /// Eases the stage presence and draws the scrim, the text backing for a backdrop of
    /// <paramref name="fieldLuminance"/> (the wallpaper before any shade) and the grain.
    /// </summary>
    public void Draw(in FrameContext frame, double fieldLuminance)
    {
        if (_scrim is null || _backing is null) return;
        var dc = frame.Dc;
        var whole = new Vortice.RawRectF(0, 0, _size.X, _size.Y);
        dc.FillRectangle(whole, _scrim);

        var shows = frame.Scene.Title is not null || frame.Scene.Lyrics is not null || frame.Scene.Status is not null;
        var presence = Presence(shows ? 1 : 0, frame.WallSeconds);
        var amount = BackingAmount(frame, fieldLuminance) * presence;
        if (amount > WallpaperShade.MinimumBacking)
            dc.DrawBitmap(_backing, whole, (float)Math.Min(1, amount), InterpolationMode.Linear, null, null);

        if (_options.Grain && _grain is not null) dc.FillRectangle(whole, _grain);
    }

    /// <summary>True while the backing is still fading in or out.</summary>
    public bool IsEasing(double now) => _presenceTarget >= 0 && now - _presenceChangedAt < WallpaperShade.BackingFade;

    private double Presence(double target, double now)
    {
        if (target != _presenceTarget)
        {
            // The first frame takes the stage as it is; later changes ease over the Mac's 0.6 s.
            _presenceFrom = _presenceTarget < 0 ? target : _presence;
            _presenceTarget = target;
            _presenceChangedAt = now;
        }
        var t = Math.Clamp((now - _presenceChangedAt) / WallpaperShade.BackingFade, 0, 1);
        _presence = _presenceFrom + (_presenceTarget - _presenceFrom) * Lunalyr.Core.Motion.Easing.Smoothstep(t);
        return _presence;
    }

    /// <summary>The backing the stage needs over a backdrop of <paramref name="luminance"/> (the Mac's textBackingAmount).</summary>
    private double BackingAmount(in FrameContext frame, double luminance)
    {
        if (_options.ManualBacking is { } manual) return Math.Clamp(manual, 0, 1);
        var playing = frame.Scene.Title is not null || frame.Scene.Lyrics is not null;
        // The lyric size in points follows the screen: the Mac's LyricViewportMetrics.baseSize.
        var points = frame.Size / _scale;
        var baseSize = Math.Min(124, Math.Max(24, Math.Min(points.X, points.Y * 16 / 9) * 0.046));
        var target = playing ? LyricLegibility.PastelBackingTarget(_options.IncreaseContrast) : LyricLegibility.TargetContrast(baseSize);
        var scrim = WallpaperShade.ScrimAtStage(Math.Max(0, _options.ScrimStrength));
        return LyricLegibility.StageBacking(luminance, scrim, target);
    }

    public void ReleaseResources()
    {
        _grain?.Dispose(); _grain = null;
        _grainBitmap?.Dispose(); _grainBitmap = null;
        _backing?.Dispose(); _backing = null;
        _scrim?.Dispose(); _scrim = null;
        _scrimStops?.Dispose(); _scrimStops = null;
    }

    public void Dispose() => ReleaseResources();
}
