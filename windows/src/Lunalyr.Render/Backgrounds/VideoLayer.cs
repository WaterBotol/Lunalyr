using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Media;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Backgrounds;

/// <summary>
/// An imported video: decoded by Media Foundation on a worker thread (on this surface's GPU when it can, through
/// DXVA), always muted (only its video stream is ever read), looping. Its clock advances only while the layer is drawn,
/// so a paused, covered or hidden wallpaper leaves the video where it was, and the worker waits on a full queue at no
/// cost. Under Reduce Motion it holds on its first frame. Each frame shown is copied into a texture of the layer's own
/// and drawn placed as the page says.
/// </summary>
public sealed class VideoLayer : PlacedLayer
{
    /// <summary>Frames decoded ahead of the one shown.</summary>
    private const int QueueLength = 2;

    private readonly Queue<VideoFrame> _queue = new();
    private readonly Lock _gate = new();
    private Thread? _worker;
    private volatile bool _stopping;
    private readonly SemaphoreSlim _space = new(QueueLength, QueueLength);

    private ID3D11Device? _device;
    private ID3D11Texture2D? _texture;
    private ID2D1Bitmap1? _bitmap;
    private int _width, _height;
    private double _clock;
    private bool _first, _failed;
    private volatile string? _error;
    private bool _reduceMotion;

    internal VideoLayer(string path, WallpaperPlacement placement, BackgroundOptions options, Logger log)
        : base(path, placement, options, log)
    {
    }

    /// <summary>The first frame is on screen, or the video can't be shown (so a crossfade into it never waits for nothing).</summary>
    public override bool HasFirstFrame => _first || _failed || _error is not null;

    /// <summary>Moving, so drawn every frame, unless held still by Reduce Motion or broken.</summary>
    public override bool IsAnimating => base.IsAnimating || (!_reduceMotion && !_failed && _error is null) || !_first;

    /// <summary>Where the decoder runs, for the log and the measurements: "GPU" or "memory".</summary>
    public string? DecodedOn { get; private set; }

    public override void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        ReleaseResources();
        base.CreateResources(dc, dwrite, size, scale);
        _failed = false;
    }

    private protected override void DrawContent(in FrameContext frame)
    {
        if (_failed) return;
        if (_error is { } error)
        {
            _failed = true;
            Log.Render.Warn($"Imported video \"{System.IO.Path.GetFileName(Path)}\" can't be shown: {error}");
            return;
        }
        _reduceMotion = frame.Scene.ReduceMotion;
        if (_worker is null) Start(frame.Dc);
        // The video's own clock: it moves only while drawn, and not at all under Reduce Motion once a frame shows.
        if (!(_reduceMotion && _first)) _clock += frame.DeltaSeconds;
        if (TakeDue(out var due))
        {
            using (due) Show(frame.Dc, due);
        }
        if (_bitmap is not null)
            frame.Dc.DrawBitmap(_bitmap, Destination(_width, _height), 1, InterpolationMode.Linear,
                new Vortice.RawRectF(0, 0, _width, _height), null);
    }

    /// <summary>The newest decoded frame that is due by the video's clock (older due ones are dropped).</summary>
    private bool TakeDue(out VideoFrame due)
    {
        due = default;
        var found = false;
        lock (_gate)
        {
            // Before the first frame shows, the first decoded one; after, the newest one due.
            while (_queue.Count > 0 && (!_first ? !found : _queue.Peek().Time <= _clock))
            {
                if (found) due.Dispose();
                due = _queue.Dequeue();
                found = true;
                _space.Release();
            }
        }
        return found;
    }

    private void Show(ID2D1DeviceContext dc, VideoFrame frame)
    {
        if (_texture is null || _bitmap is null)
        {
            _texture = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width, Height = (uint)_height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            using var surface = _texture.QueryInterface<IDXGISurface>();
            // Alpha ignored: a video is opaque whatever its decoder writes there.
            _bitmap = dc.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore), 96, 96, BitmapOptions.None));
        }
        // Vortice keeps one wrapper for the immediate context: it is not disposed here.
        var context = _device!.ImmediateContext;
        if (frame.Texture != 0)
        {
            using var source = new ID3D11Texture2D(frame.Texture);
            // The wrapper doesn't own the frame's reference: AddRef so its Dispose balances.
            source.AddRef();
            context.CopySubresourceRegion(_texture, 0, 0, 0, 0, source, frame.Subresource,
                new Vortice.Mathematics.Box(0, 0, 0, _width, _height, 1));
        }
        else
        {
            unsafe
            {
                if (frame.Pixels is not null)
                    context.UpdateSubresource(_texture, 0, null, (IntPtr)frame.Pixels, (uint)frame.Pitch, 0);
            }
        }
        if (!_first)
        {
            _first = true;
            _clock = frame.Time;
        }
    }

    private void Start(ID2D1DeviceContext dc)
    {
        // The Direct3D device under this surface's Direct2D one: the video decodes and is copied there.
        using (var d2d = dc.Device)
        using (var d2d2 = d2d.QueryInterfaceOrNull<ID2D1Device2>())
        using (var dxgi = d2d2?.DxgiDevice)
            _device = dxgi?.QueryInterfaceOrNull<ID3D11Device>();
        // The decoder uses the device from its own threads: its lock keeps that apart from drawing (the engine's devices have
        // it on already; an offscreen one may not).
        using (var multithread = _device?.QueryInterfaceOrNull<ID3D11Multithread>()) multithread?.SetMultithreadProtected(true);
        var device = _device?.NativePointer ?? 0;
        _stopping = false;
        _worker = new Thread(() => Decode(device)) { IsBackground = true, Name = "Lunalyr video", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>The worker: open, then read frames into the queue, looping, until stopped.</summary>
    private void Decode(nint device)
    {
        using var reader = VideoReader.Open(Path, device, out var error);
        if (reader is null || _device is null)
        {
            _error = error ?? "Media Foundation couldn't open it.";
            return;
        }
        _width = reader.Width;
        _height = reader.Height;
        DecodedOn = reader.Hardware ? "GPU" : "memory";
        Log.Render.Info($"Imported video: {_width}x{_height}, {reader.Duration:0.0} s, decoded in {DecodedOn}, no audio stream read.");
        double offset = 0, last = 0;
        var empty = 0;
        while (!_stopping)
        {
            switch (reader.Read(out var frame))
            {
                case VideoRead.Frame:
                    empty = 0;
                    last = frame.Time;
                    frame.Time += offset;
                    if (!Enqueue(frame)) return;
                    break;
                case VideoRead.EndOfStream:
                    // Loop: the next pass's frames follow on from this one's end.

                    offset += reader.Duration > 0 ? reader.Duration : last + 1.0 / 30;
                    if (!reader.Rewind() || ++empty > 3)
                    {
                        _error = "It couldn't be played again from its start.";
                        return;
                    }
                    break;
                case VideoRead.Nothing:
                    if (++empty > 1000) return;
                    break;
                default:
                    _error = "Its video couldn't be decoded.";
                    return;
            }
        }
    }

    private bool Enqueue(VideoFrame frame)
    {
        while (!_stopping)
        {
            if (_space.Wait(200))
            {
                if (_stopping)
                {
                    frame.Dispose();
                    return false;
                }
                lock (_gate) _queue.Enqueue(frame);
                return true;
            }
        }
        frame.Dispose();
        return false;
    }

    private protected override void Fail(Exception ex)
    {
        _failed = true;
        Log.Render.Warn("Imported video: drawing failed; waiting for new resources.", ex);
    }

    private void Stop()
    {
        _stopping = true;
        _worker?.Join(3000);
        _worker = null;
        lock (_gate)
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue().Dispose();
                _space.Release();
            }
        }
        _first = false;
        _clock = 0;
    }

    public override void ReleaseResources()
    {
        Stop();
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
        _space.Dispose();
    }
}
