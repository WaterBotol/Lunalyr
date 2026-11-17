// The ambient glow around a music video shown whole (the Mac's MusicVideoAmbientGlow, MusicVideoAmbience.swift, 7ef2908
// and 82f7a84): each sample, already blurred (MusicVideoAmbience.Glow), stretched to cover the surface and cross-faded in
// over the last, under the glow's own darkening. Clear until the first sample, so the still-based backdrop under it shows.
// Drawn over the whole surface, or only in the bands around a see-through picture; it never changes the surface's size
// (82f7a84's SwiftUI fix has nothing to port beyond that).

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

internal sealed class MusicVideoGlowDrawer : IDisposable
{
    private CoverArt? _next;
    private (ID2D1Bitmap1 Bitmap, int Width, int Height)? _current, _previous;
    private ID2D1SolidColorBrush? _black;
    private double _since = double.NaN;

    /// <summary>How many samples have been shown (the Mac's generation), for the checks.</summary>
    public int Generation { get; private set; }

    /// <summary>A glow is up, or about to be.</summary>
    public bool Showing => _current is not null || _next is not null;

    /// <summary>Cross-fades to <paramref name="glow"/>, a sample already blurred (the Mac's present).</summary>
    public void Present(CoverArt glow)
    {
        ArgumentNullException.ThrowIfNull(glow);
        _next = glow;
    }

    /// <summary>A new video: the old one's glow goes.</summary>
    public void Reset()
    {
        _next = null;
        Drop(ref _current);
        Drop(ref _previous);
        _since = double.NaN;
        Generation++;
    }

    /// <summary>Draws the glow over <paramref name="size"/>, or only around <paramref name="picture"/> when given.</summary>
    public void Draw(ID2D1DeviceContext dc, Vector2 size, double now, bool reduceMotion, VideoRect? picture = null)
    {
        if (_next is { } next)
        {
            _next = null;
            Drop(ref _previous);
            _previous = _current;
            _current = Create(dc, next);
            _since = now;
            Generation++;
        }
        if (_current is not { } current) return;
        var through = Math.Clamp((now - _since) / MusicVideoAmbience.FadeFor(reduceMotion), 0, 1);
        if (!double.IsFinite(through)) through = 1;
        if (through >= 1) Drop(ref _previous);
        // Ease in and out, as the Mac's animation.
        var eased = (float)(through * through * (3 - 2 * through));
        _black ??= dc.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
        foreach (var band in MusicVideoBackdropDrawer.Bands(size, picture))
        {
            dc.PushAxisAlignedClip(band, AntialiasMode.Aliased);
            if (_previous is { } previous) Fill(dc, previous, size, 1 - eased);
            Fill(dc, current, size, eased);
            _black.Opacity = (float)MusicVideoAmbience.Darkening;
            dc.FillRectangle(new Vortice.RawRectF(0, 0, size.X, size.Y), _black);
            dc.PopAxisAlignedClip();
        }
    }

    /// <summary>The cross-fade is running (the surface needs its frames).</summary>
    public bool Fading(double now, bool reduceMotion) =>
        _next is not null || (_previous is not null && now - _since < MusicVideoAmbience.FadeFor(reduceMotion));

    private static void Fill(ID2D1DeviceContext dc, (ID2D1Bitmap1 Bitmap, int Width, int Height) glow, Vector2 size, float opacity)
    {
        if (opacity <= 0.002f) return;
        var (x, y, w, h) = WallpaperPlacements.Frame(glow.Width, glow.Height, size.X, size.Y, WallpaperPlacement.Fill);
        dc.DrawBitmap(glow.Bitmap, new Vortice.RawRectF((float)x, (float)y, (float)(x + w), (float)(y + h)), opacity,
            InterpolationMode.Linear, new Vortice.RawRectF(0, 0, glow.Width, glow.Height), null);
    }

    private static unsafe (ID2D1Bitmap1, int, int) Create(ID2D1DeviceContext dc, CoverArt glow)
    {
        fixed (byte* pixels = glow.Pixels)
        {
            var bitmap = dc.CreateBitmap(new SizeI(glow.Width, glow.Height), (IntPtr)pixels, (uint)(glow.Width * 4),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            return (bitmap, glow.Width, glow.Height);
        }
    }

    private static void Drop(ref (ID2D1Bitmap1 Bitmap, int Width, int Height)? glow)
    {
        glow?.Bitmap.Dispose();
        glow = null;
    }

    /// <summary>The device is going: the bitmaps go (the next sample comes in afresh).</summary>
    public void Release()
    {
        Drop(ref _current);
        Drop(ref _previous);
        _black?.Dispose();
        _black = null;
    }

    public void Dispose()
    {
        Release();
        _next = null;
    }
}
