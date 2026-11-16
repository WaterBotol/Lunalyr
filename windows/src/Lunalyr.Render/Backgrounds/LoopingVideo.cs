using Lunalyr.Core.Logging;
using Lunalyr.Platform.Media;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Backgrounds;

/// <summary>
/// A video file playing silently on a loop, for whatever draws it (the imported video and animated cover wallpapers,
/// the opening title's animated cover): decoded by Media Foundation on a worker thread (on the drawing device's GPU when
/// it can, through DXVA), always muted (only its video stream is ever read). Its clock advances only as it is
/// <see cref="Advance"/>d, so what isn't drawn leaves the video where it was, and the worker waits on a full queue at no
/// cost. Under Reduce Motion it holds on its first frame. Each frame shown is copied into a texture of its own,
/// <see cref="Frame"/>. Not thread-safe: one thread draws.
/// </summary>
internal sealed class LoopingVideo(string what, CategoryLogger log) : IDisposable
{
    /// <summary>Frames decoded ahead of the one shown.</summary>
    private const int QueueLength = 2;

    private readonly Queue<VideoFrame> _queue = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _space = new(QueueLength, QueueLength);
    private Thread? _worker;
    private volatile bool _stopping;

    private ID3D11Device? _device;
    private ID3D11Texture2D? _texture;
    private ID2D1Bitmap1? _bitmap;
    private int _width, _height;
    private double _clock;
    private volatile string? _error;

    /// <summary>The file played: set before the first <see cref="Advance"/> (a change takes effect after <see cref="Release"/>).</summary>
    public string Path { get; set; } = "";

    /// <summary>The first frame has been shown.</summary>
    public bool First { get; private set; }

    /// <summary>Why it can't be played (it failed to open or decode), or null.</summary>
    public string? Error => _error;

    /// <summary>Where the decoder runs, for the log and the measurements: "GPU" or "memory".</summary>
    public string? DecodedOn { get; private set; }

    /// <summary>The frame to draw (opaque; its alpha is ignored), or null before the first.</summary>
    public ID2D1Bitmap1? Frame => _error is null ? _bitmap : null;

    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Starts decoding on <paramref name="dc"/>'s device if it hasn't, moves the video's clock on by
    /// <paramref name="delta"/> seconds (not at all under <paramref name="reduceMotion"/> once a frame shows) and takes
    /// the newest frame now due into <see cref="Frame"/>.
    /// </summary>
    public void Advance(ID2D1DeviceContext dc, double delta, bool reduceMotion)
    {
        if (_error is not null) return;
        if (_worker is null) Start(dc);
        if (!(reduceMotion && First)) _clock += Math.Max(0, delta);
        if (TakeDue(out var due))
        {
            using (due) Show(dc, due);
        }
    }

    /// <summary>
    /// How far one pass through a video moves its clock: the file's duration when it states one, else from its first
    /// frame's time to its last plus one frame. A fragmented MP4 (Apple's animated covers) states no duration and may
    /// start well after zero (10 s for some), so measuring from zero would hold the last frame that long at each loop.
    /// </summary>
    internal static double PassLength(double duration, double first, double last, double step) =>
        duration > 0 ? duration : (last - first > 0 ? last - first : 0) + (step > 0 && step < 1 ? step : 1.0 / 30);

    /// <summary>The newest decoded frame that is due by the video's clock (older due ones are dropped).</summary>
    private bool TakeDue(out VideoFrame due)
    {
        due = default;
        var found = false;
        lock (_gate)
        {
            // Before the first frame shows, the first decoded one; after, the newest one due.
            while (_queue.Count > 0 && (!First ? !found : _queue.Peek().Time <= _clock))
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
        if (!First)
        {
            First = true;
            _clock = frame.Time;
        }
    }

    private void Start(ID2D1DeviceContext dc)
    {
        // The Direct3D device under the Direct2D one: the video decodes and is copied there.
        using (var d2d = dc.Device)
        using (var d2d2 = d2d.QueryInterfaceOrNull<ID2D1Device2>())
        using (var dxgi = d2d2?.DxgiDevice)
            _device = dxgi?.QueryInterfaceOrNull<ID3D11Device>();
        // The decoder uses the device from its own threads: its lock keeps that apart from drawing (the engine's devices have
        // it on already; an offscreen one may not).
        using (var multithread = _device?.QueryInterfaceOrNull<ID3D11Multithread>()) multithread?.SetMultithreadProtected(true);
        var device = _device?.NativePointer ?? 0;
        var path = Path;
        _stopping = false;
        _worker = new Thread(() => Decode(path, device)) { IsBackground = true, Name = "Lunalyr video", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>The worker: open, then read frames into the queue, looping, until stopped.</summary>
    private void Decode(string path, nint device)
    {
        using var reader = VideoReader.Open(path, device, out var error);
        if (reader is null || _device is null)
        {
            _error = error ?? "Media Foundation couldn't open it.";
            return;
        }
        _width = reader.Width;
        _height = reader.Height;
        DecodedOn = reader.Hardware ? "GPU" : "memory";
        log.Info($"{what}: {_width}x{_height}, {reader.Duration:0.0} s, decoded in {DecodedOn}, no audio stream read.");
        double offset = 0, last = 0, previous = 0, first = double.NaN;
        var empty = 0;
        while (!_stopping)
        {
            switch (reader.Read(out var frame))
            {
                case VideoRead.Frame:
                    empty = 0;
                    if (double.IsNaN(first)) first = frame.Time;
                    previous = last;
                    last = frame.Time;
                    frame.Time += offset;
                    if (!Enqueue(frame)) return;
                    break;
                case VideoRead.EndOfStream:
                    // Loop: the next pass's frames follow on from this one's end.
                    offset += PassLength(reader.Duration, first, last, last - previous);
                    first = double.NaN;
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
                    // The slot goes back: a later start on this instance (after device loss) gets its whole queue.
                    _space.Release();
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
        First = false;
        _clock = 0;
    }

    /// <summary>Stops the decoder and lets go of the device's resources; the next <see cref="Advance"/> starts again (an error stays).</summary>
    public void Release()
    {
        Stop();
        _bitmap?.Dispose();
        _bitmap = null;
        _texture?.Dispose();
        _texture = null;
        _device?.Dispose();
        _device = null;
    }

    /// <summary>Forgets an error, so the next <see cref="Advance"/> tries again (new resources after a failure).</summary>
    public void Retry() => _error = null;

    public void Dispose()
    {
        Release();
        _space.Dispose();
    }
}
