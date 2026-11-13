// The Music video wallpaper's layers (milestone 6). The Mac's MusicVideoBackground (MusicVideoView.swift, Lunalyr's own
// code) plays a library video in AVPlayer and a YouTube one in a WKWebView, dims and blurs them, and shows the album's
// colours (the stand-in) while nothing plays. Windows:
// - "musicVideo": a file the user chose for the song, decoded by Media Foundation on a worker thread (video stream only:
//   nothing is ever heard), on this surface's GPU when it can. The frame shown is the one the song's own clock asks for
//   (MusicVideoTiming.VideoTime: the player's position plus the video offset; never the lyrics' clock), chosen by
//   MusicVideoFrameSync, so it cannot drift: the reader seeks for jumps and decodes forward otherwise. Without a file
//   (the lookup still running) it draws the stand-in, the Mac's musicVideoPending.
// - "musicVideo.youtube": see-through (ISeeThroughLayer). YouTube's player is a WebView2 window of its own beneath the
//   wallpaper surface (Lunalyr.App, YouTubeWallpaperHost); this layer draws over it only the dimming, the scrim and the
//   text backing while the player is on screen (playing or paused, as YouTube draws it, never covered), and the stand-in
//   while there is no player.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Platform.Media;
using Lunalyr.Platform.Wallpaper;
using Lunalyr.Render.Scene;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Backgrounds;

/// <summary>What a music video layer reads every frame (render thread).</summary>
/// <param name="LocalPath">The song's video file while it is the video on screen, else null.</param>
/// <param name="SongTime">The player's own position (no lyric offsets, no scrub preview); NaN with nothing playing.</param>
/// <param name="Offset">How far into the video the song starts (<see cref="MusicVideoController.CurrentOffset"/>).</param>
/// <param name="Dim">"Video dimming", 0…0.85.</param>
/// <param name="Blur">"Video blur", in points, 0…24.</param>
/// <param name="PlayerVisible">YouTube's player is on screen (ready, in any state); false before that, or with no player.</param>
public readonly record struct MusicVideoInputs(string? LocalPath, double SongTime, bool Playing, double Offset, double Dim, double Blur,
    bool PlayerVisible = false);

/// <summary>Where the music video layers get their inputs and tell what they saw. Every member is called on the render thread.</summary>
public interface IMusicVideoFeed
{
    MusicVideoInputs Read();

    /// <summary>The file's length and the frame shown (a few times a second): the intro estimate, Align and Tap to sync use them.</summary>
    void Report(string path, double? duration, double videoTime, bool playing);

    /// <summary>The file could not be shown.</summary>
    void Failed(string path, string reason);
}

/// <summary>The local file's layer, id <c>"musicVideo"</c> (the stand-in while there is no file to show).</summary>
public sealed class MusicVideoLayerFactory(IMusicVideoFeed feed, BackgroundOptions? options = null, Logger? log = null) : IRenderLayerFactory
{
    public const string LayerId = "musicVideo";

    public string Id => LayerId;

    public IRenderLayer Create() => new MusicVideoLayer(feed, options ?? BackgroundOptions.Default, log ?? Logger.Null);
}

/// <summary>The YouTube player's layer, id <c>"musicVideo.youtube"</c>: see-through over the player's own window.</summary>
public sealed class YouTubeVideoLayerFactory(IMusicVideoFeed feed, BackgroundOptions? options = null, Logger? log = null) : IRenderLayerFactory
{
    public const string LayerId = "musicVideo.youtube";

    public string Id => LayerId;

    public IRenderLayer Create() => new YouTubeVideoLayer(feed, options ?? BackgroundOptions.Default, log ?? Logger.Null);
}

/// <summary>The album's colours under a video, as the Mac's stand-in gradient: richest accent at the top left to the base.</summary>
internal sealed class MusicVideoStandIn : IDisposable
{
    private ID2D1GradientStopCollection? _stops;
    private ID2D1LinearGradientBrush? _brush;
    private ID2D1SolidColorBrush? _black;
    private Palette? _palette;

    public void Draw(ID2D1DeviceContext dc, Palette palette, Vector2 size)
    {
        if (!ReferenceEquals(palette, _palette) || _brush is null)
        {
            Release();
            _palette = palette;
            var accent = palette.Accents.Count > 0 ? palette.Accents[0] : palette.Base;
            var second = palette.Accents.Count > 1 ? palette.Accents[1] : accent;
            GradientStop[] stops =
            [
                new(0, new Color4(accent.X, accent.Y, accent.Z, 1)),
                new(0.55f, new Color4(second.X * 0.6f, second.Y * 0.6f, second.Z * 0.6f, 1)),
                new(1, new Color4(palette.Base.X, palette.Base.Y, palette.Base.Z, 1)),
            ];
            _stops = dc.CreateGradientStopCollection(stops, Gamma.StandardRgb, ExtendMode.Clamp);
            _brush = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, Vector2.One), _stops);
        }
        _brush.StartPoint = Vector2.Zero;
        _brush.EndPoint = size;
        dc.FillRectangle(new Vortice.RawRectF(0, 0, size.X, size.Y), _brush);
    }

    /// <summary>"Video dimming": black at <paramref name="dim"/> over everything below it.</summary>
    public void Dim(ID2D1DeviceContext dc, double dim, Vector2 size)
    {
        var amount = (float)Math.Clamp(double.IsFinite(dim) ? dim : 0, 0, 0.85);
        if (amount <= 0) return;
        _black ??= dc.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
        _black.Opacity = amount;
        dc.FillRectangle(new Vortice.RawRectF(0, 0, size.X, size.Y), _black);
    }

    public void Release()
    {
        _brush?.Dispose();
        _brush = null;
        _stops?.Dispose();
        _stops = null;
        _black?.Dispose();
        _black = null;
        _palette = null;
    }

    public void Dispose() => Release();
}

/// <summary>Draws a bitmap filling the surface (cropping the edges), softened by "Video blur" with a little overscan.</summary>
internal sealed class MusicVideoPicture : IDisposable
{
    private Vortice.Direct2D1.Effects.GaussianBlur? _blur;
    private ID2D1Bitmap? _input;

    public void Draw(ID2D1DeviceContext dc, ID2D1Bitmap1 bitmap, int width, int height, Vector2 size, double blurPoints, float scale)
    {
        var (x, y, w, h) = WallpaperPlacements.Frame(width, height, size.X, size.Y, WallpaperPlacement.Fill);
        var blur = double.IsFinite(blurPoints) ? Math.Clamp(blurPoints, 0, 24) : 0;
        if (blur <= 0.01)
        {
            dc.DrawBitmap(bitmap, new Vortice.RawRectF((float)x, (float)y, (float)(x + w), (float)(y + h)), 1, InterpolationMode.Linear,
                new Vortice.RawRectF(0, 0, width, height), null);
            return;
        }
        // Overscan 4% so the blur never shows the frame's edge (the Mac's scaleEffect(1.04)).
        var overscan = 1.04;
        var cx = x + w / 2;
        var cy = y + h / 2;
        w *= overscan;
        h *= overscan;
        _blur ??= new Vortice.Direct2D1.Effects.GaussianBlur(dc) { BorderMode = BorderMode.Hard, Optimization = GaussianBlurOptimization.Balanced };
        if (!ReferenceEquals(_input, bitmap))
        {
            _blur.SetInput(0, bitmap, true);
            _input = bitmap;
        }
        var sx = (float)(w / width);
        var sy = (float)(h / height);
        // The blur is set in the bitmap's own pixels: points × DPI scale, over the bitmap-to-screen scale.
        _blur.StandardDeviation = (float)(blur * scale * 0.5 / Math.Max(0.01, sx));
        var old = dc.Transform;
        dc.Transform = Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation((float)(cx - w / 2), (float)(cy - h / 2)) * old;
        dc.DrawImage(_blur, InterpolationMode.Linear, CompositeMode.SourceOver);
        dc.Transform = old;
    }

    public void Release()
    {
        _blur?.Dispose();
        _blur = null;
        _input = null;
    }

    public void Dispose() => Release();
}

/// <summary>
/// The song's video file, kept on the song's clock frame by frame, over black with the dimming, the scrim and the text
/// backing; the album colours (the stand-in) while there is no file or no frame yet. Held on its frame while the
/// wallpaper is paused or covered (nothing is drawn then), under Reduce Motion, before the video starts and past its end.
/// </summary>
public sealed class MusicVideoLayer : PlacedLayer
{
    private const int QueueLength = 3;

    /// <summary>The frame shown is reported this often (seconds).</summary>
    private const double ReportEvery = 0.25;

    private readonly IMusicVideoFeed _feed;
    private readonly MusicVideoStandIn _standIn = new();
    private readonly MusicVideoPicture _picture = new();
    private readonly object _gate = new();
    private readonly Queue<(VideoFrame Frame, int Generation)> _queue = new();

    private Thread? _worker;
    private volatile bool _stopping;
    private string? _path;
    private ID3D11Device? _device;
    private ID3D11Texture2D? _texture;
    private ID2D1Bitmap1? _bitmap;
    private int _width, _height;
    private double? _shownTime;
    private double? _decodedTo;
    private double? _seekingTo;
    private double? _seekRequest;
    private int _generation;
    private volatile int _videoWidth, _videoHeight;
    private double _duration = double.NaN;
    private volatile string? _error;
    private bool _failed;
    /// <summary>Under Reduce Motion: the video time held (the still's), from its first frame shown until Reduce Motion is off.</summary>
    private double? _stillAt;
    private double _lastReport = double.NegativeInfinity;

    internal MusicVideoLayer(IMusicVideoFeed feed, BackgroundOptions options, Logger log)
        : base("", WallpaperPlacement.Fill, options, log)
    {
        _feed = feed;
    }

    /// <summary>The stand-in draws at once; a file waits for its first frame (or its failure).</summary>
    public override bool HasFirstFrame => _path is null || _bitmap is not null || _failed || _error is not null;

    /// <summary>Drawn every frame while a file shows (its frames follow the song) or one is loading.</summary>
    public override bool IsAnimating => base.IsAnimating || (_path is not null && !_failed);

    /// <summary>The time of the frame on screen, for the checks.</summary>
    public double? ShownTime => _shownTime;

    /// <summary>Where the decoder runs: "GPU" or "memory".</summary>
    public string? DecodedOn { get; private set; }

    private protected override void DrawContent(in FrameContext frame)
    {
        var dc = frame.Dc;
        var inputs = _feed.Read();
        if (inputs.LocalPath != _path) Switch(inputs.LocalPath);
        if (_path is { } path && !_failed && _error is { } error)
        {
            _failed = true;
            Log.Render.Warn($"Music video file \"{System.IO.Path.GetFileName(path)}\" can't be shown: {error}");
            _feed.Failed(path, error);
        }
        if (_path is not null && !_failed)
        {
            if (_worker is null) Start(dc);
            Follow(dc, frame, inputs);
        }
        if (_bitmap is not null && !_failed) _picture.Draw(dc, _bitmap, _width, _height, Size, inputs.Blur, Scale);
        else _standIn.Draw(dc, frame.Scene.Palette, Size);
        _standIn.Dim(dc, inputs.Dim, Size);
    }

    /// <summary>Picks the frame the song's clock asks for, asking the worker to seek when decoding forward won't reach it soon.</summary>
    private void Follow(ID2D1DeviceContext dc, in FrameContext frame, MusicVideoInputs inputs)
    {
        double? duration = double.IsFinite(_duration) ? _duration : null;
        var target = MusicVideoTiming.VideoTime(inputs.SongTime, inputs.Offset, duration);
        // Reduce Motion: a still of the video, held at the video time its first frame was shown for. The time is held, not
        // the frame then on screen: a slow reader can hand over a frame decoded on its way there first (after a seek, the
        // one just before it), and the still then settles on the time's own frame.
        if (!frame.Scene.ReduceMotion) _stillAt = null;
        else if (_stillAt is { } still) target = still;
        double? first;
        double? decodedTo;
        double? seeking;
        lock (_gate)
        {
            first = _queue.Count > 0 ? _queue.Peek().Frame.Time : null;
            decodedTo = _decodedTo;
            seeking = _seekingTo;
        }
        var step = MusicVideoFrameSync.Decide(target, _shownTime, first, decodedTo, seeking);
        switch (step.Kind)
        {
            case MusicVideoFrameSync.StepKind.Seek:
                lock (_gate)
                {
                    Drain();
                    _generation++;
                    _seekRequest = step.To;
                    _seekingTo = step.To;
                    _decodedTo = null;
                    Monitor.PulseAll(_gate);
                }
                break;
            case MusicVideoFrameSync.StepKind.Show when target is { } goal:
                if (TakeUpTo(goal, out var due))
                {
                    using (due) Show(dc, due);
                }
                if (frame.Scene.ReduceMotion && _shownTime is not null) _stillAt ??= goal;
                break;
        }
        if (_shownTime is { } shown && frame.WallSeconds - _lastReport >= ReportEvery)
        {
            _lastReport = frame.WallSeconds;
            _feed.Report(_path!, duration, shown, inputs.Playing && target is not null && _stillAt is null);
        }
    }

    /// <summary>The newest decoded frame at or before <paramref name="target"/> (older ones dropped); the first one when nothing shows yet.</summary>
    private bool TakeUpTo(double target, out VideoFrame due)
    {
        due = default;
        var found = false;
        lock (_gate)
        {
            while (_queue.Count > 0 && (_queue.Peek().Frame.Time <= target + 0.001 || (_shownTime is null && !found)))
            {
                if (found) due.Dispose();
                due = _queue.Dequeue().Frame;
                found = true;
            }
            if (found) Monitor.PulseAll(_gate);
        }
        return found;
    }

    private void Show(ID2D1DeviceContext dc, VideoFrame frame)
    {
        int width = _videoWidth, height = _videoHeight;
        if (_texture is null || _bitmap is null || width != _width || height != _height)
        {
            _bitmap?.Dispose();
            _texture?.Dispose();
            _width = width;
            _height = height;
            _texture = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            using var surface = _texture.QueryInterface<IDXGISurface>();
            _bitmap = dc.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore), 96, 96, BitmapOptions.None));
            _picture.Release();
        }
        var context = _device!.ImmediateContext;
        if (frame.Texture != 0)
        {
            using var source = new ID3D11Texture2D(frame.Texture);
            source.AddRef();
            context.CopySubresourceRegion(_texture, 0, 0, 0, 0, source, frame.Subresource, new Box(0, 0, 0, width, height, 1));
        }
        else
        {
            unsafe
            {
                if (frame.Pixels is not null) context.UpdateSubresource(_texture, 0, null, (IntPtr)frame.Pixels, (uint)frame.Pitch, 0);
            }
        }
        _shownTime = frame.Time;
    }

    /// <summary>Another file (or none): the old worker stops; its last frame stays until the new file's first is ready.</summary>
    private void Switch(string? path)
    {
        StopWorker();
        _path = path;
        _failed = false;
        _error = null;
        _shownTime = null;
        _stillAt = null;
        _duration = double.NaN;
        if (path is null)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _texture?.Dispose();
            _texture = null;
            _picture.Release();
        }
    }

    private void Start(ID2D1DeviceContext dc)
    {
        if (_device is null)
        {
            using var d2d = dc.Device;
            using var d2d2 = d2d.QueryInterfaceOrNull<ID2D1Device2>();
            using var dxgi = d2d2?.DxgiDevice;
            _device = dxgi?.QueryInterfaceOrNull<ID3D11Device>();
            using var multithread = _device?.QueryInterfaceOrNull<ID3D11Multithread>();
            multithread?.SetMultithreadProtected(true);
        }
        if (_device is null)
        {
            _error = "No Direct3D device to decode on.";
            return;
        }
        var device = _device.NativePointer;
        var path = _path!;
        _stopping = false;
        _worker = new Thread(() => Decode(path, device)) { IsBackground = true, Name = "Lunalyr music video", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>The worker: open, then read frames into the queue, seeking when asked; it waits at the video's end.</summary>
    private void Decode(string path, nint device)
    {
        using var reader = VideoReader.Open(path, device, out var error);
        if (reader is null)
        {
            _error = error ?? "Media Foundation couldn't open it.";
            return;
        }
        _videoWidth = reader.Width;
        _videoHeight = reader.Height;
        _duration = reader.Duration > 0 ? reader.Duration : double.NaN;
        DecodedOn = reader.Hardware ? "GPU" : "memory";
        Log.Render.Info($"Music video file: {reader.Width}x{reader.Height}, {reader.Duration:0.0} s, decoded in {DecodedOn}, no audio stream read.");
        var skipBefore = double.NegativeInfinity;
        var atEnd = false;
        var generation = 0;
        var nothing = 0;
        while (!_stopping)
        {
            lock (_gate)
            {
                if (_seekRequest is { } seek)
                {
                    _seekRequest = null;
                    generation = _generation;
                    if (!reader.Seek(seek))
                    {
                        _error = "It couldn't be sought to the song's position.";
                        return;
                    }
                    // A frame lasts a 15th of a second at most here; the one showing at the target is kept.
                    skipBefore = seek - 0.07;
                    atEnd = false;
                }
                else if (atEnd || _queue.Count >= QueueLength)
                {
                    Monitor.Wait(_gate, 200);
                    continue;
                }
                else
                {
                    generation = _generation;
                }
            }
            switch (reader.Read(out var frame))
            {
                case VideoRead.Frame:
                    nothing = 0;
                    if (frame.Time < skipBefore)
                    {
                        frame.Dispose();
                        break;
                    }
                    lock (_gate)
                    {
                        if (generation != _generation || _stopping)
                        {
                            frame.Dispose();
                            break;
                        }
                        _queue.Enqueue((frame, generation));
                        _decodedTo = frame.Time;
                        _seekingTo = null;
                    }
                    break;
                case VideoRead.EndOfStream:
                    atEnd = true;
                    lock (_gate) _seekingTo = null;
                    break;
                case VideoRead.Nothing:
                    if (++nothing > 1000)
                    {
                        _error = "Its video stopped coming.";
                        return;
                    }
                    break;
                default:
                    _error = "Its video couldn't be decoded.";
                    return;
            }
        }
    }

    // Must hold _gate.
    private void Drain()
    {
        while (_queue.Count > 0) _queue.Dequeue().Frame.Dispose();
    }

    private void StopWorker()
    {
        _stopping = true;
        lock (_gate) Monitor.PulseAll(_gate);
        _worker?.Join(3000);
        _worker = null;
        lock (_gate)
        {
            Drain();
            _decodedTo = null;
            _seekingTo = null;
            _seekRequest = null;
            _generation++;
        }
        _stopping = false;
    }

    private protected override void Fail(Exception ex)
    {
        Log.Render.Warn("Music video: drawing failed; waiting for new resources.", ex);
    }

    public override void ReleaseResources()
    {
        StopWorker();
        _shownTime = null;
        _stillAt = null;
        _picture.Release();
        _standIn.Release();
        _bitmap?.Dispose();
        _bitmap = null;
        _texture?.Dispose();
        _texture = null;
        _device?.Dispose();
        _device = null;
        base.ReleaseResources();
    }

    public override void Dispose()
    {
        base.Dispose();
        _picture.Dispose();
        _standIn.Dispose();
    }
}

/// <summary>
/// Over YouTube's player window: see-through while the player is on screen (playing or paused, as YouTube draws it), with
/// the dimming, scrim and text backing on top; the album's colours while there is no player. Nothing is drawn in place of
/// a paused player: YouTube's API policies don't allow covering its interface.
/// </summary>
public sealed class YouTubeVideoLayer : IRenderLayer, ISeeThroughLayer
{
    /// <summary>The automatic backing takes a dark-middling video, as for an imported one.</summary>
    public const double AssumedLuminance = 0.2;

    private readonly IMusicVideoFeed _feed;
    private readonly WallpaperShadeDrawer _shade;
    private readonly MusicVideoStandIn _standIn = new();
    private readonly Logger _log;
    private Vector2 _size;
    private bool _ready, _failed;
    private double _lastNow;

    internal YouTubeVideoLayer(IMusicVideoFeed feed, BackgroundOptions options, Logger log)
    {
        _feed = feed;
        _log = log;
        _shade = new WallpaperShadeDrawer(options with { Grain = false });
    }

    /// <summary>Drawn every frame: the player beneath moves on its own and shows or hides at any time.</summary>
    public bool IsAnimating => true;

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        _size = size;
        _shade.CreateResources(dc, size, scale);
        _ready = true;
        _failed = false;
    }

    public void Resize(Vector2 size, float scale)
    {
        _size = size;
        _shade.Resize(size, scale);
    }

    public void Render(in FrameContext frame)
    {
        if (!_ready || _failed) return;
        _lastNow = frame.WallSeconds;
        var dc = frame.Dc;
        try
        {
            var inputs = _feed.Read();
            // Only the main display's desktop surface has the player's window beneath it (the engine passes AheadWork and
            // IsMainDisplay there): other displays and the offscreen settings preview show the album colours.
            if (!inputs.PlayerVisible || frame.AheadWork is null || !frame.IsMainDisplay) _standIn.Draw(dc, frame.Scene.Palette, _size);
            _standIn.Dim(dc, inputs.Dim, _size);
            _shade.Draw(frame, AssumedLuminance, scrim: true, grain: false);
        }
        catch (SharpGenException ex)
        {
            _failed = true;
            _log.Render.Warn("Music video (YouTube): drawing failed; waiting for new resources.", ex);
        }
    }

    public void ReleaseResources()
    {
        _standIn.Release();
        _shade.ReleaseResources();
        _ready = false;
    }

    public void Dispose()
    {
        ReleaseResources();
        _shade.Dispose();
    }
}
