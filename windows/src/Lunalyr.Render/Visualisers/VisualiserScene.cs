// One scene on one surface: its state (Lunalyr.Core's VisualizerSceneState), its render target at the scene's pixel
// budget, its photosensitivity limiter and its GPU timer. The Mac's AudioVisualizerView.Coordinator (drawFrame,
// encodeFrame, ExposureProbe), Lunalyr's own lines, written for Direct3D 11.

using System.Numerics;
using Lunalyr.Core.Visualisers;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using AlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Visualisers;

/// <summary>What one frame of a scene draws with: the host's inputs resolved for this frame.</summary>
internal readonly ref struct SceneFrame
{
    public required double Flow { get; init; }
    public required double Clock { get; init; }
    public required VisualizerResponse Response { get; init; }
    public required double Sensitivity { get; init; }
    public required double Vividness { get; init; }
    public required object? PaletteKey { get; init; }
    public required ReadOnlySpan<Vector3> CoverStops { get; init; }
    public required Lunalyr.Core.Colour.AlbumWallpaperPalette? StarTrailsPalette { get; init; }
    public required StarTrailsLyricWord? Word { get; init; }
    public required double NativeWidth { get; init; }
    public required double NativeHeight { get; init; }
    public required double PixelScale { get; init; }
    public required double TargetFps { get; init; }
}

internal sealed unsafe class VisualiserScene : IDisposable
{
    public const int ProbeWidth = 64, ProbeHeight = 36;
    private const int TimerFrames = 4;

    private readonly VisualiserHost _host;
    private readonly VisualizerSceneState _state = new();
    private readonly GpuFrameTimer _timer = new();
    private readonly Vector4[] _colours = new Vector4[VisualizerSceneState.ColourStops];
    private readonly WavefieldModeUniforms[] _modes = new WavefieldModeUniforms[WavefieldFrame.MaximumModes];
    private readonly Vector4[] _sceneData = new Vector4[VisualiserGpu.SceneFloat4s];
    private readonly float[] _audio = new float[VisualiserGpu.AudioFloats];
    private readonly string _display;

    private VisualiserGpu? _gpu;
    private ID3D11Texture2D? _target;
    private ID3D11RenderTargetView? _targetView;
    private ID2D1Bitmap1? _bitmap;
    private ID3D11Texture2D? _probe;
    private ID3D11RenderTargetView? _probeTarget;
    private ID3D11ShaderResourceView? _probeView;
    private readonly ID3D11Texture2D?[] _exposure = new ID3D11Texture2D?[2];
    private readonly ID3D11RenderTargetView?[] _exposureTargets = new ID3D11RenderTargetView?[2];
    private readonly ID3D11ShaderResourceView?[] _exposureViews = new ID3D11ShaderResourceView?[2];
    private readonly ID3D11Texture2D?[] _readback = new ID3D11Texture2D?[3];
    private readonly ID3D11Query?[] _disjoint = new ID3D11Query?[TimerFrames];
    private readonly ID3D11Query?[] _begin = new ID3D11Query?[TimerFrames];
    private readonly ID3D11Query?[] _end = new ID3D11Query?[TimerFrames];
    private readonly bool[] _timing = new bool[TimerFrames];
    private int _timerNext, _readbackNext, _readbackPending;
    private int _exposureCurrent;
    private bool _hasDrawn, _limiting, _adoptNext;
    private double? _lastLimited;
    private float _frameCount;

    public VisualiserScene(VisualiserHost host, VisualizerStyle style, string display)
    {
        _host = host;
        Style = style;
        _display = display;
    }

    public VisualizerStyle Style { get; }

    /// <summary>The scene's texture as Direct2D draws it, after the last <see cref="Draw"/>; null before.</summary>
    public ID2D1Bitmap1? Bitmap => _hasDrawn ? _bitmap : null;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The ladder's current scale (for diagnostics and tests).</summary>
    public double LadderScale => _state.Ladder.Scale;

    /// <summary>The limiter's gain applied to the last calm frame, read back a few frames late (diagnostics).</summary>
    public float LastShown { get; private set; }

    /// <summary>
    /// Prepares and renders one frame into the scene's texture. Returns false (and keeps the last frame) when the GPU
    /// couldn't draw it; a calm frame is never drawn without its limiter.
    /// </summary>
    public bool Draw(VisualiserGpu gpu, ID2D1DeviceContext dc, in SceneFrame frame, in AudioSnapshot sound)
    {
        if (!ReferenceEquals(gpu, _gpu)) Attach(gpu, frame.Clock);
        var previousScene = _state.Style;
        if (_state.Prepare(Style, frame.Flow, frame.Response, frame.PaletteKey, sound, frame.Clock, _host.Timelines)
            || previousScene != Style)
            _timer.Reset();

        if (_timer.Drain(30) is { } average) _state.Ladder.Adjust(average, 1 / Math.Clamp(frame.TargetFps, 1, 480));
        var (width, height) = RenderScaleLadder.DrawableSize(frame.NativeWidth, frame.NativeHeight, Style, _host.Preview,
            _state.Ladder.Scale * frame.PixelScale);
        if (width != Width || height != Height || _bitmap is null) CreateTarget(gpu, dc, width, height);

        var u = _state.Inputs(width, height, frame.Sensitivity, frame.Vividness, frame.Clock, frame.CoverStops, _colours, _modes);
        u.Frame = _frameCount = (_frameCount + 1) % 16_777_216;
        ref readonly var prepared = ref _state.PreparedSound;
        ((ReadOnlySpan<float>)prepared.Bands).CopyTo(_audio);
        ((ReadOnlySpan<float>)prepared.Waveform).CopyTo(_audio.AsSpan(64));
        gpu.Write(gpu.Uniforms, u);
        gpu.Write<float>(gpu.Audio, _audio);
        gpu.Write<Vector4>(gpu.Album, _colours);
        if (Style == VisualizerStyle.StarTrails)
        {
            var simulation = _host.Timelines.StarTrails.Advance(_state.SceneTime, frame.Clock, prepared, frame.Response,
                frame.Sensitivity, frame.StarTrailsPalette, frame.Word);
            simulation.Pack(width / Math.Max(1.0, height), _sceneData);
            gpu.Write<Vector4>(gpu.Scene, _sceneData);
        }
        else if (Style.IsWavefield())
        {
            gpu.Write<WavefieldModeUniforms>(gpu.Scene, _modes);
        }

        var shader = gpu.Shader(Style.Shader());
        BeginTimer(gpu);
        gpu.BindStage();
        ID3D11ShaderResourceView exposure = gpu.UnityView;
        if (frame.Response.Calm)
        {
            if (!_limiting && _hasDrawn) _adoptNext = true;
            Limit(gpu, shader, frame.Clock);
            exposure = _exposureViews[_exposureCurrent]!;
            _limiting = true;
        }
        else
        {
            _limiting = false;
            _lastLimited = null;
        }
        gpu.BindStage();
        gpu.SetTarget(_targetView!, width, height);
        gpu.SetResources(exposure);
        gpu.Draw(shader);
        gpu.Unbind();
        EndTimer(gpu);
        _hasDrawn = true;
        if (_limiting) RememberShown(gpu, frame.Clock);
        return true;
    }

    /// <summary>The probe pass and the measuring pass: afterwards the current exposure state holds this frame's gain.</summary>
    private void Limit(VisualiserGpu gpu, ID3D11PixelShader shader, double clock)
    {
        gpu.SetTarget(_probeTarget!, ProbeWidth, ProbeHeight);
        gpu.SetResources(gpu.UnityView);
        gpu.Draw(shader);
        gpu.SetResources(null, null);

        var elapsed = _lastLimited is { } last ? Math.Max(0, clock - last) : 0;
        _lastLimited = clock;
        var parameters = new Vector4(ExposureLimiter.MaxRisePerSecond, (float)Math.Min(ExposureLimiter.MaxStep, elapsed),
            _adoptNext ? 1 : 0, 0);
        _adoptNext = false;
        gpu.Write(gpu.Params, parameters);
        gpu.Context.PSSetConstantBuffer(0, gpu.Params);
        var next = 1 - _exposureCurrent;
        gpu.SetTarget(_exposureTargets[next]!, 1, 1);
        gpu.SetResources(_probeView, _exposureViews[_exposureCurrent]);
        gpu.Draw(gpu.Measure);
        gpu.SetResources(null, null);
        _exposureCurrent = next;
    }

    /// <summary>Files the level the limiter shows under this scene, read back without waiting (a frame or two late).</summary>
    private void RememberShown(VisualiserGpu gpu, double clock)
    {
        var slot = _readbackNext;
        gpu.Context.CopyResource(_readback[slot]!, _exposure[_exposureCurrent]!);
        _readbackNext = (slot + 1) % _readback.Length;
        _readbackPending = Math.Min(_readback.Length, _readbackPending + 1);
        if (_readbackPending < _readback.Length) return;
        var oldest = _readbackNext;
        var mapped = new MappedSubresource();
        if (gpu.Context.Map(_readback[oldest]!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out mapped).Failure) return;
        LastShown = ((float*)mapped.DataPointer)[1];
        gpu.Context.Unmap(_readback[oldest]!, 0);
        _host.Exposure.Record(LastShown, Key, clock);
    }

    private ExposureMemory.Key Key => new(Style, _display, _host.Preview);

    private void BeginTimer(VisualiserGpu gpu)
    {
        var slot = _timerNext;
        if (_timing[slot]) Collect(gpu, slot);
        if (_timing[slot]) return; // Still in flight: skip timing this frame.
        _disjoint[slot] ??= gpu.Device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        _begin[slot] ??= gpu.Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        _end[slot] ??= gpu.Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        gpu.Context.Begin(_disjoint[slot]!);
        gpu.Context.End(_begin[slot]!);
        _timing[slot] = true;
        _timingOpen = true;
    }

    private bool _timingOpen;

    private void EndTimer(VisualiserGpu gpu)
    {
        if (!_timingOpen) return;
        _timingOpen = false;
        var slot = _timerNext;
        gpu.Context.End(_end[slot]!);
        gpu.Context.End(_disjoint[slot]!);
        _timerNext = (slot + 1) % TimerFrames;
        for (var i = 0; i < TimerFrames; i++) if (_timing[i] && i != slot) Collect(gpu, i);
    }

    private void Collect(VisualiserGpu gpu, int slot)
    {
        QueryDataTimestampDisjoint disjoint;
        ulong begin, end;
        const AsyncGetDataFlags noFlush = AsyncGetDataFlags.DoNotFlush;
        if (gpu.Context.GetData(_disjoint[slot]!, (nint)(&disjoint), (uint)sizeof(QueryDataTimestampDisjoint), noFlush).Code != 0) return;
        if (gpu.Context.GetData(_begin[slot]!, (nint)(&begin), 8, noFlush).Code != 0) return;
        if (gpu.Context.GetData(_end[slot]!, (nint)(&end), 8, noFlush).Code != 0) return;
        _timing[slot] = false;
        if (disjoint.Disjoint || disjoint.Frequency == 0 || end <= begin) return;
        _timer.Record((end - begin) / (double)disjoint.Frequency);
    }

    /// <summary>A new device (first frame, device loss, another GPU): fresh probes, measured afresh.</summary>
    private void Attach(VisualiserGpu gpu, double clock)
    {
        ReleaseGpu();
        _gpu = gpu;
        _state.Ladder.Reset();
        _timer.Reset();
        var device = gpu.Device;
        _probe = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, ProbeWidth, ProbeHeight, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        _probeTarget = device.CreateRenderTargetView(_probe);
        _probeView = device.CreateShaderResourceView(_probe);
        // The limiter carries on from the brightness on screen: a scene that is on screen (this surface moving to another
        // GPU, or a copy taking it over) starts at the level it is shown at; one coming on screen fades in from black.
        var shown = _limiting ? LastShown : _host.Exposure.Shown(Key, clock) ?? 0;
        for (var i = 0; i < 2; i++)
        {
            _exposure[i] = device.CreateTexture2D(new Texture2DDescription(Format.R32G32_Float, 1, 1, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource));
            _exposureTargets[i] = device.CreateRenderTargetView(_exposure[i]!);
            _exposureViews[i] = device.CreateShaderResourceView(_exposure[i]!);
        }
        Seed(gpu, shown);
        for (var i = 0; i < _readback.Length; i++)
            _readback[i] = device.CreateTexture2D(new Texture2DDescription(Format.R32G32_Float, 1, 1, 1, 1, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Read));
        _readbackPending = 0;
    }

    /// <summary>Starts the limiter at <paramref name="shown"/> (mean linear luminance on screen).</summary>
    private void Seed(VisualiserGpu gpu, float shown)
    {
        var state = new Vector2(1, float.IsFinite(shown) ? Math.Max(0, shown) : 0);
        gpu.Context.UpdateSubresource(state, _exposure[_exposureCurrent]!);
        LastShown = state.Y;
    }

    private void CreateTarget(VisualiserGpu gpu, ID2D1DeviceContext dc, int width, int height)
    {
        ReleaseTarget();
        Width = width;
        Height = height;
        _target = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        _targetView = gpu.Device.CreateRenderTargetView(_target);
        using var surface = _target.QueryInterface<IDXGISurface>();
        _bitmap = dc.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        _hasDrawn = false;
    }

    private void ReleaseTarget()
    {
        _bitmap?.Dispose(); _bitmap = null;
        _targetView?.Dispose(); _targetView = null;
        _target?.Dispose(); _target = null;
        Width = Height = 0;
    }

    private void ReleaseGpu()
    {
        ReleaseTarget();
        _probeView?.Dispose(); _probeView = null;
        _probeTarget?.Dispose(); _probeTarget = null;
        _probe?.Dispose(); _probe = null;
        for (var i = 0; i < 2; i++)
        {
            _exposureViews[i]?.Dispose(); _exposureViews[i] = null;
            _exposureTargets[i]?.Dispose(); _exposureTargets[i] = null;
            _exposure[i]?.Dispose(); _exposure[i] = null;
        }
        for (var i = 0; i < _readback.Length; i++) { _readback[i]?.Dispose(); _readback[i] = null; }
        for (var i = 0; i < TimerFrames; i++)
        {
            _disjoint[i]?.Dispose(); _disjoint[i] = null;
            _begin[i]?.Dispose(); _begin[i] = null;
            _end[i]?.Dispose(); _end[i] = null;
            _timing[i] = false;
        }
        _timingOpen = false;
        _gpu = null;
    }

    /// <summary>Device resources go (device loss); the scene's own state stays.</summary>
    public void Release()
    {
        ReleaseGpu();
    }

    public void Dispose() => ReleaseGpu();
}
