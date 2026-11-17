// The backdrop around a music video shown whole (the Mac's MusicVideoBackdropView, MusicVideoFraming.swift, Test Build 88):
// the album cover, or the YouTube video's own still once it has loaded, blurred once on a small copy off the render thread
// (MusicVideoBackdrop.Blurred), then drawn stretched to cover the surface and darkened. Clear until one is ready, so the
// stand-in under it shows; never black bars.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.MusicVideo;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Backgrounds;

internal sealed class MusicVideoBackdropDrawer : IDisposable
{
    private CoverArt? _source;
    private double? _aspect;
    private Task<CoverArt?>? _blurring;
    private CoverArt? _blurred;
    private ID2D1Bitmap1? _bitmap;
    private ID2D1SolidColorBrush? _black;
    private double _since = double.NaN;

    /// <summary>The blurred picture is ready (for the checks).</summary>
    public bool Ready => _blurred is not null;

    /// <summary>The blurred picture as last drawn, and its size in pixels; null before.</summary>
    public (ID2D1Bitmap1 Bitmap, int Width, int Height)? Picture =>
        _bitmap is { } bitmap && _blurred is { } blurred ? (bitmap, blurred.Width, blurred.Height) : null;

    /// <summary>The source of <see cref="Picture"/>.</summary>
    public CoverArt? Source => _source;

    /// <summary>
    /// Draws the backdrop of <paramref name="source"/> over the whole of <paramref name="size"/>, or only around
    /// <paramref name="picture"/> (the bands it leaves) when given: there the surface stays as it is (see-through over
    /// YouTube's player).
    /// </summary>
    /// <param name="stillAspect">For a YouTube still: the video's own shape, so only its picture (not YouTube's bars) is blurred.</param>
    public unsafe void Draw(ID2D1DeviceContext dc, CoverArt? source, Vector2 size, double now, VideoRect? picture = null, double? stillAspect = null)
    {
        Follow(source, stillAspect, now);
        if (_blurred is not { } blurred) return;
        if (_bitmap is null)
        {
            fixed (byte* pixels = blurred.Pixels)
            {
                _bitmap = dc.CreateBitmap(new SizeI(blurred.Width, blurred.Height), (IntPtr)pixels, (uint)(blurred.Width * 4),
                    new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            }
        }
        _black ??= dc.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
        var shown = Math.Clamp((now - _since) / YouTubeThumbnail.FadeIn, 0, 1);
        if (!double.IsFinite(shown)) shown = 1;
        // Eased out, as the Mac's opacity transition.
        var opacity = (float)(1 - (1 - shown) * (1 - shown));
        foreach (var band in Bands(size, picture))
        {
            dc.PushAxisAlignedClip(band, AntialiasMode.Aliased);
            var (x, y, w, h) = WallpaperPlacements.Frame(blurred.Width, blurred.Height, size.X, size.Y, WallpaperPlacement.Fill);
            dc.DrawBitmap(_bitmap, new Vortice.RawRectF((float)x, (float)y, (float)(x + w), (float)(y + h)), opacity,
                InterpolationMode.Linear, new Vortice.RawRectF(0, 0, blurred.Width, blurred.Height), null);
            _black.Opacity = (float)(MusicVideoBackdrop.Darkening * opacity);
            dc.FillRectangle(new Vortice.RawRectF(0, 0, size.X, size.Y), _black);
            dc.PopAxisAlignedClip();
        }
    }

    /// <summary>
    /// The parts of the surface outside <paramref name="picture"/> (the whole surface without one). A picture shown whole
    /// spans the width, so these are the bands above and below it; side bands are handled too.
    /// </summary>
    public static IEnumerable<Vortice.RawRectF> Bands(Vector2 size, VideoRect? picture)
    {
        if (picture is not { } p)
        {
            yield return new Vortice.RawRectF(0, 0, size.X, size.Y);
            yield break;
        }
        float left = (float)Math.Max(0, p.X), top = (float)Math.Max(0, p.Y);
        float right = (float)Math.Min(size.X, p.X + p.Width), bottom = (float)Math.Min(size.Y, p.Y + p.Height);
        if (top > 0) yield return new Vortice.RawRectF(0, 0, size.X, top);
        if (bottom < size.Y) yield return new Vortice.RawRectF(0, bottom, size.X, size.Y);
        if (left > 0 && bottom > top) yield return new Vortice.RawRectF(0, top, left, bottom);
        if (right < size.X && bottom > top) yield return new Vortice.RawRectF(right, top, size.X, bottom);
    }

    /// <summary>Starts blurring a new source off the render thread; takes up the result once it is done.</summary>
    private void Follow(CoverArt? source, double? aspect, double now)
    {
        if (!ReferenceEquals(source, _source) || aspect != _aspect)
        {
            _source = source;
            _aspect = aspect;
            _blurring = source is null ? null
                : Task.Run(() => aspect is { } shape ? MusicVideoBackdrop.Blurred(source, shape) : MusicVideoBackdrop.Blurred(source));
            // The old backdrop stays until the new one is ready (the Mac keeps the cover until the still is blurred).
            if (source is null) DropPicture();
        }
        if (_blurring is { IsCompleted: true } task)
        {
            _blurring = null;
            var result = task.IsCompletedSuccessfully ? task.Result : null;
            if (result is not null)
            {
                var first = _blurred is null;
                DropPicture();
                _blurred = result;
                if (first) _since = now;
            }
        }
    }

    private void DropPicture()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _blurred = null;
        _since = double.NaN;
    }

    public void Release()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _black?.Dispose();
        _black = null;
    }

    public void Dispose()
    {
        Release();
        _blurred = null;
        _source = null;
    }
}
