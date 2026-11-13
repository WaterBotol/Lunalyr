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
    private ID2D1DeviceContext? _dc;
    /// <summary>The darkening the scrim's brush was made for, and whether the backing image is the hand-set shape.</summary>
    private double _scrimBuiltFor = double.NaN;
    private bool? _backingManual;
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

    /// <summary>The darkening the scrim is drawn at: the options' times the scene's "Darkening".</summary>
    public double Darkening(SceneSnapshot scene) => Math.Max(0, _options.ScrimStrength * scene.Adjustments.Darkening);

    /// <summary>The hand-set text backing (the scene's, else the options'), or null for the automatic amount.</summary>
    public double? ManualBacking(SceneSnapshot scene) => scene.Adjustments.ManualBacking ?? _options.ManualBacking;

    public void CreateResources(ID2D1DeviceContext dc, Vector2 size, float scale)
    {
        ReleaseResources();
        _dc = dc;
        BuildScrim(dc, Math.Max(0, _options.ScrimStrength));
        BuildBacking(dc, _options.ManualBacking is not null);

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

    /// <summary>The scrim's brush for a darkening of <paramref name="strength"/> (each stop's alpha is capped at 1).</summary>
    private void BuildScrim(ID2D1DeviceContext dc, double strength)
    {
        _scrim?.Dispose(); _scrim = null;
        _scrimStops?.Dispose(); _scrimStops = null;
        var scrim = WallpaperShade.Scrim;
        var stops = new GradientStop[scrim.Count];
        for (var i = 0; i < scrim.Count; i++)
            stops[i] = new GradientStop(i / (float)(scrim.Count - 1), new Color4(0, 0, 0, (float)Math.Min(1, scrim[i] * strength)));
        _scrimStops = dc.CreateGradientStopCollection(stops, Gamma.StandardRgb, ExtendMode.Clamp);
        _scrim = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, new Vector2(0, Math.Max(1, _size.Y))), _scrimStops);
        _scrimBuiltFor = strength;
    }

    /// <summary>
    /// The backing's shape. The automatic amount covers where the lyrics are set, feathered out widely (Windows' own
    /// shape, see FeatheredBacking); a hand-set amount keeps the Mac's soft ellipse.
    /// </summary>
    private void BuildBacking(ID2D1DeviceContext dc, bool manual)
    {
        _backing?.Dispose(); _backing = null;
        var shade = manual
            ? LyricLegibility.ManualShadeImage(LyricLegibility.ShadeColumns, LyricLegibility.ShadeRows)!
            : FeatheredBacking.Image(LyricLegibility.ShadeColumns, LyricLegibility.ShadeRows)!;
        _backing = CreateBitmap(dc, LyricLegibility.ShadeColumns, LyricLegibility.ShadeRows, shade);
        _backingManual = manual;
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
    /// <paramref name="fieldLuminance"/> (the wallpaper before any shade) and the grain. The scene's adjustments
    /// ("Darkening", a hand-set "Text backing") apply at once: the scrim's brush and the backing's shape are made again
    /// only when they change. <paramref name="scrim"/> and <paramref name="grain"/> off draw the backing alone (over
    /// the user's own wallpaper).
    /// <paramref name="stageShade"/> replaces the scrim's darkness at the stage when a layer lays its own shade there.
    /// </summary>
    public void Draw(in FrameContext frame, double fieldLuminance, bool scrim = true, bool grain = true, double? stageShade = null)
    {
        if (_scrim is null || _backing is null || _dc is null) return;
        var dc = frame.Dc;
        var whole = new Vortice.RawRectF(0, 0, _size.X, _size.Y);
        var strength = Darkening(frame.Scene);
        if (strength != _scrimBuiltFor) BuildScrim(_dc, strength);
        var manual = ManualBacking(frame.Scene);
        if ((manual is not null) != _backingManual) BuildBacking(_dc, manual is not null);
        if (scrim && strength > 0) dc.FillRectangle(whole, _scrim!);

        var presence = Presence(StageShows(frame.Scene, frame.WallSeconds), frame.WallSeconds);
        var amount = BackingAmount(frame, fieldLuminance, scrim ? strength : 0, manual, stageShade) * presence;
        if (amount > WallpaperShade.MinimumBacking)
            dc.DrawBitmap(_backing!, whole, (float)Math.Min(1, amount), InterpolationMode.Linear, null, null);

        if (grain && _options.Grain && _grain is not null) dc.FillRectangle(whole, _grain);
    }

    private string? _noneFoundStatus;
    private double _noneFoundSince;

    /// <summary>
    /// Whether the stage shows anything (1) or not (0). A song without lyrics follows "When none are found"
    /// (<see cref="Core.Motion.LyricStagePresence"/>): the backing stays behind its title and artist, goes with its note
    /// once that has faded, and never comes for an empty stage.
    /// </summary>
    private double StageShows(SceneSnapshot scene, double now)
    {
        if (!scene.NoneFound || scene.Lyrics is not null || scene.Status is null)
        {
            _noneFoundStatus = null;
            return scene.Title is not null || scene.Lyrics is not null || scene.Status is not null ? 1 : 0;
        }
        if (scene.Status != _noneFoundStatus)
        {
            _noneFoundStatus = scene.Status;
            _noneFoundSince = now;
        }
        var inputs = new Core.Motion.LyricStagePresence.Inputs(HasTrack: true, IdleSince: null, IsLoading: false, HasTimedLines: false,
            HasPlainLines: false, NotFound: true, NotFoundAt: _noneFoundSince, scene.Style.NoLyrics);
        return Core.Motion.LyricStagePresence.Presence(inputs, now, Core.Motion.NoticeTiming.Default) > 0.5 ? 1 : 0;
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
    private double BackingAmount(in FrameContext frame, double luminance, double darkening, double? manual, double? stageShade = null)
    {
        if (manual is { } amount) return Math.Clamp(amount, 0, 1);
        var playing = frame.Scene.Title is not null || frame.Scene.Lyrics is not null;
        // The lyric size in points follows the screen: the Mac's LyricViewportMetrics.baseSize.
        var points = frame.Size / _scale;
        var baseSize = Math.Min(124, Math.Max(24, Math.Min(points.X, points.Y * 16 / 9) * 0.046));
        var target = playing ? LyricLegibility.PastelBackingTarget(_options.IncreaseContrast) : LyricLegibility.TargetContrast(baseSize);
        var scrim = stageShade ?? WallpaperShade.ScrimAtStage(darkening);
        return LyricLegibility.StageBacking(luminance, scrim, target);
    }

    public void ReleaseResources()
    {
        _dc = null;
        _scrimBuiltFor = double.NaN;
        _backingManual = null;
        _grain?.Dispose(); _grain = null;
        _grainBitmap?.Dispose(); _grainBitmap = null;
        _backing?.Dispose(); _backing = null;
        _scrim?.Dispose(); _scrim = null;
        _scrimStops?.Dispose(); _scrimStops = null;
    }

    public void Dispose() => ReleaseResources();
}
