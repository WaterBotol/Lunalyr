using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Backgrounds;

/// <summary>
/// An imported video: decoded by Media Foundation on a worker thread (on this surface's GPU when it can, through
/// DXVA), always muted (only its video stream is ever read), looping (<see cref="LoopingVideo"/>). Its clock advances
/// only while the layer is drawn, so a paused, covered or hidden wallpaper leaves the video where it was, and the worker
/// waits on a full queue at no cost. Under Reduce Motion it holds on its first frame. Each frame shown is copied into a
/// texture of the layer's own and drawn placed as the page says.
/// </summary>
public class VideoLayer : PlacedLayer
{
    private readonly LoopingVideo _video;
    private bool _failed;
    private bool _reduceMotion;

    internal VideoLayer(string path, WallpaperPlacement placement, BackgroundOptions options, Logger log)
        : base(path, placement, options, log)
    {
        _video = new LoopingVideo(What, log.Render);
    }

    /// <summary>The first frame is on screen, or the video can't be shown (so a crossfade into it never waits for nothing).</summary>
    public override bool HasFirstFrame => _video.First || _failed || _video.Error is not null;

    /// <summary>A video (animated cover art too) waits up to <see cref="Pacing.WallpaperTransition.VideoFirstFrameWait"/> (the Mac's a4e4f6a).</summary>
    public override Pacing.TransitionGate Gate => Pacing.TransitionGate.Video;

    /// <summary>Moving, so drawn every frame, unless held still by Reduce Motion or broken.</summary>
    public override bool IsAnimating => base.IsAnimating || (!_failed && _video.Error is null && (!_reduceMotion || !_video.First));

    /// <summary>Where the decoder runs, for the log and the measurements: "GPU" or "memory".</summary>
    public string? DecodedOn => _video.DecodedOn;

    public override void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        ReleaseResources();
        base.CreateResources(dc, dwrite, size, scale);
        _failed = false;
    }

    private protected override void DrawContent(in FrameContext frame)
    {
        if (_failed) return;
        if (_video.Error is { } error)
        {
            _failed = true;
            Log.Render.Warn($"{What} \"{System.IO.Path.GetFileName(Path)}\" can't be shown: {error}");
            return;
        }
        _reduceMotion = frame.Scene.ReduceMotion;
        // The file this surface plays (a subclass may have chosen another rendition before the first frame).
        if (!_video.First) _video.Path = Path;
        // The video's own clock: it moves only while drawn, and not at all under Reduce Motion once a frame shows.
        _video.Advance(frame.Dc, frame.DeltaSeconds, _reduceMotion);
        if (_video.Frame is { } bitmap) DrawFrame(frame, bitmap, _video.Width, _video.Height);
    }

    /// <summary>What the log calls this video ("Imported video").</summary>
    private protected virtual string What => "Imported video";

    /// <summary>Whether the video can't be shown (it failed to open, decode or draw).</summary>
    private protected bool VideoFailed => _failed || _video.Error is not null;

    /// <summary>Whether a frame of the video is ready to draw.</summary>
    private protected bool HasVideoFrame => _video.Frame is not null && !VideoFailed;

    /// <summary>Draws the frame on screen, placed (a subclass may soften it).</summary>
    private protected virtual void DrawFrame(in FrameContext frame, ID2D1Bitmap1 bitmap, int width, int height) =>
        frame.Dc.DrawBitmap(bitmap, Destination(width, height), 1, InterpolationMode.Linear, new Vortice.RawRectF(0, 0, width, height), null);

    /// <inheritdoc cref="LoopingVideo.PassLength"/>
    internal static double PassLength(double duration, double first, double last, double step) =>
        LoopingVideo.PassLength(duration, first, last, step);

    private protected override void Fail(Exception ex)
    {
        _failed = true;
        Log.Render.Warn($"{What}: drawing failed; waiting for new resources.", ex);
    }

    public override void ReleaseResources()
    {
        _video.Release();
        base.ReleaseResources();
    }

    public override void Dispose()
    {
        base.Dispose();
        _video.Dispose();
    }
}
