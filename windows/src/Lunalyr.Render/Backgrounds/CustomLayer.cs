// "Imported image or video" (the Mac's CustomWallpaperView, Lunalyr's own lines; the video side re-implements the
// behaviour of MotionArtworkView, which carries Verso lines, so nothing of it is translated): the user's picture or
// video, placed as the Wallpaper page says, with the scrim and the text backing over it. Pictures decode off the render
// thread and are drawn once at the surface's size; videos decode on the GPU on a worker thread of their own, silently
// (no audio stream is ever selected), loop, stop with the wallpaper, and hold on a still frame under Reduce Motion.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Wallpaper;
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

/// <summary>Creates the imported picture or video layer, id <c>"custom"</c>, for one file and placement.</summary>
public sealed class CustomLayerFactory(string path, WallpaperPlacement placement, BackgroundOptions? options = null, Logger? log = null)
    : IRenderLayerFactory
{
    public string Id => "custom";

    public string Path { get; } = path;

    public WallpaperPlacement Placement { get; } = placement;

    public IRenderLayer Create() => WallpaperPicture.IsVideo(Path)
        ? new VideoLayer(Path, Placement, options ?? BackgroundOptions.Default, log ?? Logger.Null)
        : new PictureLayer(Path, Placement, options ?? BackgroundOptions.Default, log ?? Logger.Null);
}

/// <summary>What the picture and video layers share: black beneath, the placed frame, then the scrim and backing.</summary>
public abstract class PlacedLayer : IRenderLayer, IFirstFrameLayer
{
    private protected readonly WallpaperPlacement Placement;
    private protected readonly Logger Log;
    private protected readonly string Path;
    private readonly WallpaperShadeDrawer _shade;
    private protected Vector2 Size;
    private protected float Scale = 1;
    private protected bool Ready;
    private double _lastNow;

    private protected PlacedLayer(string path, WallpaperPlacement placement, BackgroundOptions options, Logger log)
    {
        Path = path;
        Placement = placement;
        Log = log;
        // Grain is for the generated gradients' banding; a photo or video has its own texture.
        _shade = new WallpaperShadeDrawer(options with { Grain = false });
    }

    public abstract bool HasFirstFrame { get; }

    public virtual bool IsAnimating => _shade.IsEasing(_lastNow);

    /// <summary>The backdrop luminance behind the stage, for the automatic text backing.</summary>
    private protected double Luminance = 0.2;

    public virtual void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        Size = size;
        Scale = scale;
        _shade.CreateResources(dc, size, scale);
        Ready = true;
    }

    public virtual void Resize(Vector2 size, float scale)
    {
        Size = size;
        Scale = scale;
        _shade.Resize(size, scale);
    }

    public void Render(in FrameContext frame)
    {
        if (!Ready) return;
        _lastNow = frame.WallSeconds;
        var dc = frame.Dc;
        dc.FillRectangle(new Vortice.RawRectF(0, 0, Size.X, Size.Y), Black(dc));
        try
        {
            DrawContent(frame);
        }
        catch (SharpGenException ex)
        {
            Fail(ex);
        }
        _shade.Draw(frame, Luminance, scrim: true, grain: false);
    }

    private protected abstract void DrawContent(in FrameContext frame);

    private protected abstract void Fail(Exception ex);

    /// <summary>Where a <paramref name="width"/> × <paramref name="height"/> frame lands on the surface.</summary>
    private protected Vortice.RawRectF Destination(int width, int height)
    {
        var (x, y, w, h) = WallpaperPlacements.Frame(width, height, Size.X, Size.Y, Placement);
        return new Vortice.RawRectF((float)x, (float)y, (float)(x + w), (float)(y + h));
    }

    private ID2D1SolidColorBrush? _black;

    private ID2D1SolidColorBrush Black(ID2D1DeviceContext dc) => _black ??= dc.CreateSolidColorBrush(new Color4(0, 0, 0, 1));

    public virtual void ReleaseResources()
    {
        _black?.Dispose();
        _black = null;
        _shade.ReleaseResources();
        Ready = false;
    }

    public virtual void Dispose()
    {
        ReleaseResources();
        _shade.Dispose();
    }
}

/// <summary>
/// An imported picture: decoded once per file off the render thread (<see cref="WallpaperPicture"/>), then drawn at the
/// surface's size into a bitmap of its own (high-quality scaling, once), which each frame copies. A picture that can't
/// be decoded leaves black with the backing, and says so in the log once.
/// </summary>
public sealed class PictureLayer : PlacedLayer
{
    private static readonly Lock CacheGate = new();
    private static (string Path, Task<CoverArt?> Task)? s_cache;

    private readonly Task<CoverArt?> _decode;
    private ID2D1Bitmap1? _placed;
    private bool _failed, _logged;

    internal PictureLayer(string path, WallpaperPlacement placement, BackgroundOptions options, Logger log)
        : base(path, placement, options, log)
    {
        // Every display's layer shares one decode of the file.
        lock (CacheGate)
        {
            if (s_cache is not { } cached || cached.Path != path) s_cache = (path, WallpaperPicture.DecodeAsync(path));
            _decode = s_cache.Value.Task;
        }
    }

    public override bool HasFirstFrame => _decode.IsCompleted;

    /// <summary>Drawn again until the picture has decoded, so it shows as soon as it can.</summary>
    public override bool IsAnimating => base.IsAnimating || !_decode.IsCompleted || (_placed is null && !_failed && Ready);

    public override void Resize(Vector2 size, float scale)
    {
        base.Resize(size, scale);
        _placed?.Dispose();
        _placed = null;
    }

    private protected override void DrawContent(in FrameContext frame)
    {
        if (_failed || !_decode.IsCompleted) return;
        var picture = _decode.IsCompletedSuccessfully ? _decode.Result : null;
        if (picture is null)
        {
            _failed = true;
            if (!_logged) Log.Render.Warn($"Imported picture \"{System.IO.Path.GetFileName(Path)}\" couldn't be decoded; showing black.");
            _logged = true;
            return;
        }
        _placed ??= Place(frame.Dc, picture);
        frame.Dc.DrawBitmap(_placed, new Vortice.RawRectF(0, 0, Size.X, Size.Y), 1, InterpolationMode.NearestNeighbor, null, null);
    }

    /// <summary>The picture drawn once where it lands on the surface, scaled smoothly.</summary>
    private unsafe ID2D1Bitmap1 Place(ID2D1DeviceContext dc, CoverArt picture)
    {
        Luminance = PictureLegibility.StageLuminance(picture.Pixels, picture.Width, picture.Height, Size.X, Size.Y, Placement) ?? 0.2;
        int width = Math.Max(1, (int)Size.X), height = Math.Max(1, (int)Size.Y);
        ID2D1Bitmap1 source;
        fixed (byte* pixels = picture.Pixels)
        {
            source = dc.CreateBitmap(new SizeI(picture.Width, picture.Height), (IntPtr)pixels, (uint)(picture.Width * 4),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        }
        using (source)
        {
            var placed = dc.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            // Drawn on a context of its own: the surface's is mid-frame (perhaps inside a crossfade's layer).
            using var device = dc.Device;
            using var bake = device.CreateDeviceContext(DeviceContextOptions.None);
            bake.Target = placed;
            bake.BeginDraw();
            bake.Clear(new Color4(0, 0, 0, 1));
            bake.DrawBitmap(source, Destination(picture.Width, picture.Height), 1, InterpolationMode.HighQualityCubic, null, null);
            bake.EndDraw();
            bake.Target = null;
            return placed;
        }
    }

    private protected override void Fail(Exception ex)
    {
        _failed = true;
        Log.Render.Warn("Imported picture: drawing failed; waiting for new resources.", ex);
    }

    public override void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        ReleaseResources();
        base.CreateResources(dc, dwrite, size, scale);
        _failed = false;
    }

    public override void ReleaseResources()
    {
        _placed?.Dispose();
        _placed = null;
        base.ReleaseResources();
    }
}
