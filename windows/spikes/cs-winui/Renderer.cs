using System.Diagnostics;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using FactoryType = Vortice.Direct2D1.FactoryType;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace LunalyrSpike;

/// <summary>One output surface: a monitor's wallpaper window, or the test window.</summary>
internal sealed class Surface(nint hwnd, int width, int height, float scale, int hz)
{
    public nint Hwnd = hwnd;
    public int Width = width, Height = height, Hz = hz;
    public float Scale = scale;
    public IDXGISwapChain2? Swap;
    public nint Waitable;
    public ID2D1Bitmap1? Target;
    public ID2D1RadialGradientBrush[] Blobs = [];
    public int LineIndex = -1;
    public Word[] Words = [];
    public float FontPx;
    public ID2D1SolidColorBrush? TextBrush;

    public sealed class Word
    {
        public ID2D1Bitmap1 Sharp = null!, Blurred = null!;
        public Vector2 Pos; // top-left of the bitmap (padding included)
    }
}

/// <summary>Renders the spike scene on its own thread to every surface.</summary>
internal sealed class Renderer : IDisposable
{
    readonly Options _opt;
    readonly Stats _stats;
    readonly Thread _thread;
    volatile bool _stop;
    readonly object _gate = new();
    List<Surface> _pending = [];
    bool _swapPending;
    readonly ManualResetEventSlim _released = new(true);

    ID3D11Device _d3d = null!;
    ID3D11DeviceContext _d3dCtx = null!;
    IDXGIFactory2 _dxgiFactory = null!;
    ID2D1Factory1 _d2dFactory = null!;
    ID2D1Device _d2dDevice = null!;
    ID2D1DeviceContext _ctx = null!;
    IDWriteFactory _dw = null!;
    Vortice.Direct2D1.Effects.GaussianBlur _blur = null!;
    Vortice.Direct2D1.Effects.Opacity _opacity = null!;
    List<Surface> _surfaces = [];
    public string AdapterName { get; private set; } = "";
    readonly Stopwatch _clock = Stopwatch.StartNew();
    bool _snapshotDone;

    public Renderer(Options opt, Stats stats)
    {
        _opt = opt; _stats = stats;
        _thread = new Thread(Run) { IsBackground = true, Name = "Render" };
        var ready = new ManualResetEventSlim();
        _thread.Start(ready);
        ready.Wait();
    }

    /// <summary>Hand a new set of windows to the render thread (it releases the old swap chains first).</summary>
    public void SetSurfaces(List<Surface> surfaces)
    {
        lock (_gate) { _pending = surfaces; _swapPending = true; _released.Reset(); }
        _released.Wait(2000);
    }

    /// <summary>Blocks until the render thread has dropped every swap chain (so the windows can be destroyed).</summary>
    public void ReleaseSurfaces() => SetSurfaces([]);

    void Run(object? readyObj)
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        InitDevice();
        ((ManualResetEventSlim)readyObj!).Set();
        var frameWatch = new Stopwatch();
        double lastPresent = -1;
        double lastRendered = -1;
        while (!_stop)
        {
            lock (_gate)
            {
                if (_swapPending)
                {
                    foreach (var s in _surfaces) DisposeSurface(s);
                    _surfaces = _pending;
                    foreach (var s in _surfaces) CreateSwapChain(s);
                    _swapPending = false;
                    _released.Set();
                }
            }
            if (_surfaces.Count == 0) { Thread.Sleep(16); continue; }

            var handles = _surfaces.Select(s => s.Waitable).ToArray();
            Native.WaitForMultipleObjects((uint)handles.Length, handles, true, 1000);

            double now = _clock.Elapsed.TotalMilliseconds;
            lastRendered = now;

            frameWatch.Restart();
            foreach (var s in _surfaces) RenderSurface(s, now);
            frameWatch.Stop();
            double t = _clock.Elapsed.TotalMilliseconds;
            _stats.AddFrame(frameWatch.Elapsed.TotalMilliseconds, lastPresent < 0 ? double.NaN : t - lastPresent);
            lastPresent = t;
        }
        foreach (var s in _surfaces) DisposeSurface(s);
        _surfaces = [];
        _released.Set();
    }

    void InitDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _d3d!, out _d3dCtx!).CheckError();
        using var dxgiDevice = _d3d.QueryInterface<IDXGIDevice1>();
        dxgiDevice.SetMaximumFrameLatency(1);
        using var adapter = dxgiDevice.GetAdapter();
        AdapterName = adapter.Description.Description;
        _dxgiFactory = adapter.GetParent<IDXGIFactory2>();
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _ctx = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _dw = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _blur = new Vortice.Direct2D1.Effects.GaussianBlur(_ctx) { BorderMode = BorderMode.Soft, Optimization = GaussianBlurOptimization.Balanced };
        _opacity = new Vortice.Direct2D1.Effects.Opacity(_ctx);
    }

    void CreateSwapChain(Surface s)
    {
        var desc = new SwapChainDescription1
        {
            Width = (uint)s.Width, Height = (uint)s.Height, Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard, AlphaMode = Vortice.DXGI.AlphaMode.Ignore, Scaling = Scaling.Stretch,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };
        using var sc1 = _dxgiFactory.CreateSwapChainForHwnd(_d3d, s.Hwnd, desc);
        _dxgiFactory.MakeWindowAssociation(s.Hwnd, WindowAssociationFlags.IgnoreAll);
        s.Swap = sc1.QueryInterface<IDXGISwapChain2>();
        s.Swap.MaximumFrameLatency = 1;
        s.Waitable = s.Swap.FrameLatencyWaitableObject;
        using var surface = s.Swap.GetBuffer<IDXGISurface>(0);
        s.Target = _ctx.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore), 96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw));
        s.TextBrush = _ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        float maxDim = Math.Max(s.Width, s.Height);
        s.Blobs = new ID2D1RadialGradientBrush[Scene.Blobs.Length];
        for (int i = 0; i < s.Blobs.Length; i++)
        {
            var (r, g, b) = Scene.Blobs[i];
            using var stops = _ctx.CreateGradientStopCollection(
            [
                new GradientStop(0f, new Color4(r, g, b, 0.85f)),
                new GradientStop(0.45f, new Color4(r, g, b, 0.35f)),
                new GradientStop(1f, new Color4(r, g, b, 0f)),
            ]);
            float rad = maxDim * 0.55f;
            s.Blobs[i] = _ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, rad, rad), stops);
        }
        s.FontPx = s.Height * 0.075f;
        s.LineIndex = -1;
    }

    void DisposeSurface(Surface s)
    {
        DisposeWords(s);
        foreach (var b in s.Blobs) b.Dispose();
        s.Blobs = [];
        s.TextBrush?.Dispose(); s.TextBrush = null;
        _ctx.Target = null;
        s.Target?.Dispose(); s.Target = null;
        s.Swap?.Dispose(); s.Swap = null;
    }

    static void DisposeWords(Surface s)
    {
        foreach (var w in s.Words) { w.Sharp.Dispose(); w.Blurred.Dispose(); }
        s.Words = [];
    }

    void BuildLine(Surface s, int lineIndex)
    {
        DisposeWords(s);
        var line = Scene.Lines[lineIndex];
        string text = line.Text;
        float maxW = s.Width * 0.8f;
        using var format = _dw.CreateTextFormat("Segoe UI Variable Display", null, FontWeight.SemiBold,
            Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, s.FontPx, "en-us");
        using var full = _dw.CreateTextLayout(text, format, maxW, s.Height);
        full.TextAlignment = TextAlignment.Center;
        full.WordWrapping = WordWrapping.Wrap;
        full.ReadingDirection = line.Rtl ? ReadingDirection.RightToLeft : ReadingDirection.LeftToRight;
        var m = full.Metrics;
        float x0 = (s.Width - maxW) / 2, y0 = (s.Height - m.Height) / 2;
        float sigmaMax = 10f / 3f * s.Scale;
        float pad = MathF.Ceiling(sigmaMax * 3 + 2);

        var words = new Surface.Word[line.Words.Length];
        int pos = 0;
        for (int i = 0; i < line.Words.Length; i++)
        {
            string w = line.Words[i];
            var hits = full.HitTestTextRange((uint)pos, (uint)w.Length, 0, 0);
            var h = hits[0];
            pos += w.Length + (line.Spaced ? 1 : 0);

            using var wl = _dw.CreateTextLayout(w, format, h.Width + s.FontPx, h.Height);
            int bw = (int)MathF.Ceiling(h.Width + 2 * pad), bh = (int)MathF.Ceiling(h.Height + 2 * pad);
            var props = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);
            var sharp = _ctx.CreateBitmap(new SizeI(bw, bh), IntPtr.Zero, 0, props);
            _ctx.Target = sharp;
            _ctx.BeginDraw();
            _ctx.Clear(new Color4(0, 0, 0, 0));
            _ctx.DrawTextLayout(new Vector2(pad, pad), wl, s.TextBrush!, DrawTextOptions.EnableColorFont);
            _ctx.EndDraw();

            var blurred = _ctx.CreateBitmap(new SizeI(bw, bh), IntPtr.Zero, 0, props);
            _ctx.Target = blurred;
            _ctx.BeginDraw();
            _ctx.Clear(new Color4(0, 0, 0, 0));
            _blur.SetInput(0, sharp, true);
            _blur.StandardDeviation = sigmaMax;
            _ctx.DrawImage(_blur.Output, Vector2.Zero, InterpolationMode.Linear, CompositeMode.SourceOver);
            _ctx.EndDraw();
            _ctx.Target = null;

            words[i] = new Surface.Word { Sharp = sharp, Blurred = blurred, Pos = new Vector2(x0 + h.Left - pad, y0 + h.Top - pad) };
        }
        s.Words = words;
        s.LineIndex = lineIndex;
    }

    void RenderSurface(Surface s, double nowMs)
    {
        var (li, lineStart) = Scene.LineAt(nowMs, _opt.StartLine);
        if (li != s.LineIndex) BuildLine(s, li);

        _ctx.Target = s.Target;
        _ctx.BeginDraw();
        var (br, bg, bb) = Scene.Base;
        _ctx.Clear(new Color4(br, bg, bb, 1));
        double tS = nowMs / 1000.0;
        for (int i = 0; i < s.Blobs.Length; i++)
        {
            var (cx, cy) = Scene.BlobCentre(i, tS);
            var c = new Vector2(cx * s.Width, cy * s.Height);
            var brush = s.Blobs[i];
            brush.Center = c;
            float r = brush.RadiusX;
            _ctx.FillEllipse(new Ellipse(c, r, r), brush);
        }

        double lineT = nowMs - lineStart;
        float em = s.FontPx;
        for (int i = 0; i < s.Words.Length; i++)
        {
            var w = s.Words[i];
            double p = (lineT - Scene.WordStartMs(i)) / Scene.RevealMs;
            if (p <= 0)
            {
                _ctx.DrawBitmap(w.Blurred, new Rect(w.Pos.X, w.Pos.Y + 0.12f * em, w.Blurred.PixelSize.Width, w.Blurred.PixelSize.Height), 0.3f, BitmapInterpolationMode.Linear, null);
            }
            else if (p >= 1)
            {
                _ctx.DrawBitmap(w.Sharp, new Rect(w.Pos.X, w.Pos.Y, w.Sharp.PixelSize.Width, w.Sharp.PixelSize.Height), 1f, BitmapInterpolationMode.Linear, null);
            }
            else
            {
                double e = Scene.EaseOutCubic(p);
                _blur.SetInput(0, w.Sharp, true);
                _blur.StandardDeviation = (float)(10.0 / 3.0 * s.Scale * (1 - e));
                _opacity.SetInputEffect(0, _blur, true);
                _opacity.Value = (float)(0.3 + 0.7 * e);
                _ctx.DrawImage(_opacity.Output, new Vector2(w.Pos.X, w.Pos.Y + (float)(0.12 * em * (1 - e))), InterpolationMode.Linear, CompositeMode.SourceOver);
            }
        }
        _ctx.EndDraw();
        _ctx.Target = null;

        if (!_snapshotDone && _opt.SnapshotPath != null && nowMs >= _opt.SnapshotAt * 1000 && ReferenceEquals(s, _surfaces[0]))
        {
            _snapshotDone = true;
            try { Snapshot(s, _opt.SnapshotPath); } catch (Exception ex) { _stats.Notes.Add("snapshot failed: " + ex.Message); }
        }
        // A frame-rate cap presents every Nth vblank (sync interval N), which keeps frames evenly paced.
        uint interval = _opt.FpsCap > 0 ? (uint)Math.Clamp(Math.Round((double)s.Hz / _opt.FpsCap), 1, 4) : 1;
        s.Swap!.Present(interval, PresentFlags.None);
    }

    void Snapshot(Surface s, string path)
    {
        using var back = s.Swap!.GetBuffer<ID3D11Texture2D>(0);
        var d = back.Description;
        d.Usage = ResourceUsage.Staging; d.BindFlags = BindFlags.None; d.CPUAccessFlags = CpuAccessFlags.Read; d.MiscFlags = ResourceOptionFlags.None;
        using var staging = _d3d.CreateTexture2D(d);
        _d3dCtx.CopyResource(staging, back);
        var map = _d3dCtx.Map(staging, 0, MapMode.Read);
        try { Png.SaveBgra(path, map.DataPointer, (int)d.Width, (int)d.Height, (int)map.RowPitch); }
        finally { _d3dCtx.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(3000);
    }
}
