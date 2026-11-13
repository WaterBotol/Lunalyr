// "Use animated covers" (the Mac's .motion background): Apple Music's animated album cover, playing silently behind the
// lyrics in Match the music. The Mac draws it in NowPlayingView's .motion case through MotionArtworkView; both carry
// lines from the squashed Verso import (the softening blur, its overscan, the flat shade and the centre vignette), so
// nothing of them is translated. Re-implemented from this description of what the Mac shows:
//
//   The album's own colour gradient lies beneath (it shows beside a "Fit whole frame" cover). The cover's video is
//   placed as Covers › Placement says and softened by a blur of Covers › Softening, only enough that fine detail stops
//   fighting the lyrics. The album colours lie over it faintly, then an even shade that follows Darkening, then a soft
//   round shade darkest in the middle of the screen and gone a little past halfway to the edges, then the text backing.
//
// Windows' own structure: the video is the imported-video layer's (decoded on this surface's GPU, muted, looping, held
// on its first frame under Reduce Motion); the blur is one Direct2D Gaussian blur at the video's own resolution, scaled
// into place, with hard edges instead of the Mac's overscan. Until the video's first frame (and if it can't be played),
// the song's still cover is drawn in its place, so a cover never falls back to black. The faint album tint and the
// backing's sums are Lunalyr's own lines (NowPlayingView.swift, 91b3259 / aa22c91), ported.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Render.Scene;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using Double3 = Lunalyr.Core.Colour.Double3;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Backgrounds;

/// <summary>
/// Creates the animated cover's layer, id <c>"motion"</c>: one rendition file for every display, or a tall one for
/// portrait displays when the album has it.
/// </summary>
public sealed class MotionCoverLayerFactory(string squarePath, string? tallPath, WallpaperPlacement placement,
    BackgroundOptions? options = null, Logger? log = null) : IRenderLayerFactory
{
    public const string LayerId = "motion";

    public string Id => LayerId;

    public string SquarePath { get; } = squarePath;

    public string? TallPath { get; } = tallPath;

    public WallpaperPlacement Placement { get; } = placement;

    public IRenderLayer Create() => new MotionCoverLayer(SquarePath, TallPath, Placement, options ?? BackgroundOptions.Default, log ?? Logger.Null);
}

/// <summary>The animated cover: see the file's description. Nothing is allocated per frame once its brushes exist.</summary>
public sealed class MotionCoverLayer : VideoLayer
{
    /// <summary>The faint album tint over the cover (the Mac's 18%).</summary>
    internal const float TintOpacity = 0.18f;

    /// <summary>The even shade over the cover at Darkening 1.</summary>
    internal const double EvenShade = 0.40;

    /// <summary>The round shade's darkness at the middle of the screen, halfway out, and its reach (share of the longer side).</summary>
    internal const float RoundCentre = 0.5f, RoundHalfway = 0.16f, RoundReach = 0.55f;

    /// <summary>Softening is given in points of blur radius; Direct2D's blur takes a standard deviation of half that.</summary>
    internal const double SigmaPerPoint = 0.5;

    private readonly string _square;
    private readonly string? _tall;
    private Vortice.Direct2D1.Effects.GaussianBlur? _blur;
    private ID2D1Image? _blurred;
    private ID2D1Bitmap1? _blurInput;
    private ID2D1Bitmap1? _still;
    private CoverArt? _stillFor;
    private ID2D1SolidColorBrush? _shade;
    private ID2D1GradientStopCollection? _roundStops;
    private ID2D1RadialGradientBrush? _round;
    private ID2D1GradientStopCollection? _albumStops;
    private ID2D1LinearGradientBrush? _album;
    private Palette? _albumFor;

    internal MotionCoverLayer(string squarePath, string? tallPath, WallpaperPlacement placement, BackgroundOptions options, Logger log)
        : base(squarePath, placement, options, log)
    {
        _square = squarePath;
        _tall = tallPath;
    }

    /// <summary>The file this surface plays: the tall rendition on a portrait display when there is one.</summary>
    public string Playing => Path;

    private protected override string What => "Animated cover";

    // The cover lays its own even shade and round shade; the shared top-to-bottom scrim is left out, as on the Mac.
    private protected override bool DrawsScrim => false;

    /// <summary>
    /// The shade already at the stage before the text backing (the Mac's backing sum for animated covers): the even
    /// shade, then the round shade's middle.
    /// </summary>
    private protected override double? StageShade(SceneSnapshot scene) => StageShadeFor(Darkening(scene));

    internal static double StageShadeFor(double darkening) => 1 - (1 - Math.Min(1, EvenShade * Math.Max(0, darkening))) * (1 - RoundCentre);

    public override void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        // Each surface keeps the rendition it started with: a portrait display takes the tall one.
        Path = _tall is not null && size.Y > size.X ? _tall : _square;
        base.CreateResources(dc, dwrite, size, scale);
        _blur = new Vortice.Direct2D1.Effects.GaussianBlur(dc) { BorderMode = BorderMode.Hard, Optimization = GaussianBlurOptimization.Balanced };
        _blurred = _blur.Output;
        _shade = dc.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
        _roundStops = dc.CreateGradientStopCollection(
        [
            new GradientStop(0, new Color4(0, 0, 0, RoundCentre)),
            new GradientStop(0.5f, new Color4(0, 0, 0, RoundHalfway)),
            new GradientStop(1, new Color4(0, 0, 0, 0)),
        ], Gamma.StandardRgb, ExtendMode.Clamp);
        _round = dc.CreateRadialGradientBrush(new RadialGradientBrushProperties(), _roundStops);
        PlaceRound();
    }

    public override void Resize(Vector2 size, float scale)
    {
        base.Resize(size, scale);
        PlaceRound();
        ReleaseAlbum();
    }

    private void PlaceRound()
    {
        if (_round is null) return;
        var reach = MathF.Max(Size.X, Size.Y) * RoundReach;
        _round.Center = Size / 2;
        _round.GradientOriginOffset = Vector2.Zero;
        _round.RadiusX = reach;
        _round.RadiusY = reach;
    }

    private protected override void DrawContent(in FrameContext frame)
    {
        var dc = frame.Dc;
        var whole = new Vortice.RawRectF(0, 0, Size.X, Size.Y);
        var palette = frame.Scene.Palette;
        Luminance = CoverLuminance(palette);
        var album = AlbumGradient(dc, palette);
        // The album's colours beneath: they show beside a cover fitted whole.
        if (album is not null) dc.FillRectangle(whole, album);
        // The song's still cover until the video's first frame, and in its place if it can't play.
        if (!HasVideoFrame && Still(dc, frame.Scene.Cover) is { } still)
            Soften(frame, still, frame.Scene.Cover!.Width, frame.Scene.Cover.Height);
        base.DrawContent(frame);
        if (album is not null && palette.ArtworkStops.Count > 0)
        {
            album.Opacity = TintOpacity;
            dc.FillRectangle(whole, album);
            album.Opacity = 1;
        }
        var even = (float)Math.Min(1, EvenShade * Darkening(frame.Scene));
        if (even > 0 && _shade is not null)
        {
            _shade.Opacity = even;
            dc.FillRectangle(whole, _shade);
        }
        if (_round is not null) dc.FillRectangle(whole, _round);
    }

    private protected override void DrawFrame(in FrameContext frame, ID2D1Bitmap1 bitmap, int width, int height) =>
        Soften(frame, bitmap, width, height);

    /// <summary>
    /// Draws <paramref name="bitmap"/> placed, blurred by Softening: the blur runs at the picture's own resolution (its
    /// standard deviation divided by the scale it is drawn at), and the result is scaled into place.
    /// </summary>
    private void Soften(in FrameContext frame, ID2D1Bitmap1 bitmap, int width, int height)
    {
        var dc = frame.Dc;
        var place = Destination(width, height);
        var scale = (place.Right - place.Left) / Math.Max(1, width);
        var sigma = (float)(SigmaFor(frame.Scene.Adjustments.Softening, Scale) / Math.Max(1e-3f, scale));
        if (sigma < 0.2f || _blur is null || _blurred is null)
        {
            dc.DrawBitmap(bitmap, place, 1, InterpolationMode.Linear, new Vortice.RawRectF(0, 0, width, height), null);
            return;
        }
        if (!ReferenceEquals(_blurInput, bitmap))
        {
            _blur.SetInput(0, bitmap, true);
            _blurInput = bitmap;
        }
        if (_blur.StandardDeviation != sigma) _blur.StandardDeviation = sigma;
        var before = dc.Transform;
        dc.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(place.Left, place.Top) * before;
        dc.DrawImage(_blurred, InterpolationMode.Linear, CompositeMode.SourceOver);
        dc.Transform = before;
    }

    /// <summary>The blur's standard deviation on screen, in pixels, for a Softening of <paramref name="points"/>.</summary>
    internal static double SigmaFor(double points, float dpiScale) =>
        double.IsFinite(points) ? Math.Clamp(points, 0, 25) * SigmaPerPoint * Math.Max(0.5f, dpiScale) : 0;

    /// <summary>
    /// The backdrop's luminance behind the lyrics for the automatic text backing: the brightest of the cover's middle
    /// row of colours (the Mac's coverLuminance, Lunalyr's own lines), or the usual guess without a cover.
    /// </summary>
    internal static double CoverLuminance(Palette palette)
    {
        var best = double.NegativeInfinity;
        for (var index = 3; index <= 5 && index < palette.ArtworkStops.Count; index++)
            best = Math.Max(best, LyricLegibility.Luminance(Double3.From(palette.ArtworkStops[index])));
        return double.IsNegativeInfinity(best) ? LyricLegibility.UnknownBackdropLuminance : best;
    }

    /// <summary>The album gradient (the Mac's palette gradient: its stops from the top-left corner to the bottom-right).</summary>
    private ID2D1LinearGradientBrush? AlbumGradient(ID2D1DeviceContext dc, Palette palette)
    {
        if (ReferenceEquals(palette, _albumFor) && _album is not null) return _album;
        ReleaseAlbum();
        if (palette.Accents.Count == 0) return null;
        var stops = new GradientStop[Math.Max(2, palette.Accents.Count)];
        for (var i = 0; i < stops.Length; i++)
        {
            var colour = palette.Accents[Math.Min(i, palette.Accents.Count - 1)];
            stops[i] = new GradientStop(i / (float)(stops.Length - 1), new Color4(colour.X, colour.Y, colour.Z, 1));
        }
        _albumStops = dc.CreateGradientStopCollection(stops, Gamma.StandardRgb, ExtendMode.Clamp);
        _album = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, Size), _albumStops);
        _albumFor = palette;
        return _album;
    }

    private void ReleaseAlbum()
    {
        _album?.Dispose();
        _album = null;
        _albumStops?.Dispose();
        _albumStops = null;
        _albumFor = null;
    }

    /// <summary>The still cover as a bitmap, made again only when the scene's cover changes.</summary>
    private unsafe ID2D1Bitmap1? Still(ID2D1DeviceContext dc, CoverArt? cover)
    {
        if (cover is null) return null;
        if (ReferenceEquals(cover, _stillFor) && _still is not null) return _still;
        _still?.Dispose();
        _still = null;
        _stillFor = cover;
        fixed (byte* pixels = cover.Pixels)
        {
            _still = dc.CreateBitmap(new SizeI(cover.Width, cover.Height), (IntPtr)pixels, (uint)(cover.Width * 4),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        }
        return _still;
    }

    public override void ReleaseResources()
    {
        // Stop the decoder (base) before the effect that may hold its frame's bitmap goes.
        base.ReleaseResources();
        _blurred?.Dispose();
        _blurred = null;
        _blur?.Dispose();
        _blur = null;
        _blurInput = null;
        _still?.Dispose();
        _still = null;
        _stillFor = null;
        _shade?.Dispose();
        _shade = null;
        _round?.Dispose();
        _round = null;
        _roundStops?.Dispose();
        _roundStops = null;
        ReleaseAlbum();
    }
}
