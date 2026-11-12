// Draws one visualiser frame from given inputs, without a surface, the engine or smoothing: the Mac's
// Coordinator.bind(_ frame:…) path, used by its render tests. Tests, the preview tool and the cost bench use it.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Visualisers;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Lunalyr.Render.Visualisers;

/// <summary>
/// A visualiser on its own device (WARP unless an adapter is named), drawing frames into an offscreen target and reading
/// them back as BGRA bytes, top row first.
/// </summary>
public sealed unsafe class VisualiserStill : IDisposable
{
    private readonly VisualiserGpu _gpu;
    private readonly Vector4[] _colours = new Vector4[5];
    private readonly Vector4[] _scene = new Vector4[VisualiserGpu.SceneFloat4s];
    private readonly float[] _audio = new float[VisualiserGpu.AudioFloats];
    private ID3D11Texture2D? _target, _staging;
    private ID3D11RenderTargetView? _view;
    private int _width, _height;

    /// <param name="adapter">Part of a hardware adapter's name ("NVIDIA", "Intel"); null draws on WARP.</param>
    public VisualiserStill(string? adapter = null)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        ID3D11Device? device;
        if (adapter is null)
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out ID3D11DeviceContext? context).CheckError();
            context!.Dispose();
            AdapterName = "WARP";
        }
        else
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIAdapter1? chosen = null;
            for (uint i = 0; factory.EnumAdapters1(i, out var candidate).Success && candidate is not null; i++)
            {
                if (chosen is null && candidate.Description1.Description.Contains(adapter, StringComparison.OrdinalIgnoreCase)) chosen = candidate;
                else candidate.Dispose();
            }
            if (chosen is null) throw new InvalidOperationException($"No adapter named like \"{adapter}\".");
            AdapterName = chosen.Description1.Description;
            D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out device, out ID3D11DeviceContext? context).CheckError();
            context!.Dispose();
            chosen.Dispose();
        }
        _gpu = new VisualiserGpu(device!);
    }

    public string AdapterName { get; }

    /// <summary>A PNG of BGRA pixels from <see cref="Render"/>.</summary>
    public static byte[] Png(byte[] bgra, int width, int height) => Engine.PngEncoder.EncodeBgra(bgra, width, height, width * 4);

    /// <summary>
    /// Draws <paramref name="style"/> for <paramref name="sound"/> at scene time <paramref name="time"/> and returns its
    /// pixels. <paramref name="palette"/>: the cover (null keeps the scene's own colours). <paramref name="adjust"/>
    /// changes the uniforms after they are built, as a test may.
    /// </summary>
    public byte[] Render(VisualizerStyle style, in AudioSnapshot sound, int width, int height, double time = 12,
        double sensitivity = 1, AlbumWallpaperPalette? palette = null, double vividness = 1, bool calm = false,
        VizUniformsAdjust? adjust = null)
    {
        Draw(style, sound, width, height, time, sensitivity, palette, vividness, calm, adjust);
        return Read();
    }

    /// <summary>
    /// Star Trails drawn from a given simulation state (<see cref="StarTrailsLayout.Float4Count"/> float4s), as the
    /// Mac's Star Trails render tests bind it: silence, scene time 1.
    /// </summary>
    public byte[] RenderStarTrails(ReadOnlySpan<Vector4> state, int width, int height)
    {
        Ensure(width, height);
        var silence = new AudioSnapshot();
        var u = VisualizerUniforms.Build(VisualizerStyle.StarTrails, width, height, 1, silence, 1, 1, false);
        Array.Clear(_colours);
        Array.Clear(_audio);
        _scene.AsSpan().Clear();
        state.CopyTo(_scene);
        _gpu.Write(_gpu.Uniforms, u);
        _gpu.Write<float>(_gpu.Audio, _audio);
        _gpu.Write<Vector4>(_gpu.Album, _colours);
        _gpu.Write<Vector4>(_gpu.Scene, _scene);
        _gpu.BindStage();
        _gpu.SetTarget(_view!, width, height);
        _gpu.SetResources(_gpu.UnityView);
        _gpu.Draw(_gpu.Shader(VisualizerStyle.StarTrails.Shader()));
        _gpu.Unbind();
        return Read();
    }

    /// <summary>Changes a frame's uniforms before it is drawn.</summary>
    public delegate void VizUniformsAdjust(ref VizUniforms uniforms);

    /// <summary>Draws a frame without reading it back (the cost bench).</summary>
    public void Draw(VisualizerStyle style, in AudioSnapshot sound, int width, int height, double time = 12, double sensitivity = 1,
        AlbumWallpaperPalette? palette = null, double vividness = 1, bool calm = false, VizUniformsAdjust? adjust = null)
    {
        Ensure(width, height);
        var u = VisualizerUniforms.Build(style, width, height, time, sound, sensitivity, 1, calm, vividness);
        if (style.IsWavefield())
        {
            var modes = new WavefieldModes();
            var field = modes.Frame(sound, 0, u.Time, u.Sensitivity, calm);
            field.Apply(u.P);
            var span = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector4, WavefieldModeUniforms>(_scene.AsSpan(0, 36));
            span.Clear();
            field.Modes.CopyTo(span);
        }
        else if (style == VisualizerStyle.StarTrails)
        {
            StarTrailsSimulation.Snapshot(sound, u.Time, u.Sensitivity, calm, width / (double)height, palette).CopyTo(_scene, 0);
        }
        adjust?.Invoke(ref u);
        for (var i = 0; i < 5; i++)
            _colours[i] = palette is not null && style != VisualizerStyle.StarTrails ? new Vector4(palette.Stops[i], u.P[15]) : Vector4.Zero;
        ((ReadOnlySpan<float>)sound.Bands).CopyTo(_audio);
        ((ReadOnlySpan<float>)sound.Waveform).CopyTo(_audio.AsSpan(64));
        _gpu.Write(_gpu.Uniforms, u);
        _gpu.Write<float>(_gpu.Audio, _audio);
        _gpu.Write<Vector4>(_gpu.Album, _colours);
        _gpu.Write<Vector4>(_gpu.Scene, _scene);
        _gpu.BindStage();
        _gpu.SetTarget(_view!, width, height);
        _gpu.SetResources(_gpu.UnityView);
        _gpu.Draw(_gpu.Shader(style.Shader()));
        _gpu.Unbind();
    }

    /// <summary>
    /// GPU milliseconds per frame for <paramref name="frames"/> frames of <paramref name="style"/> at the given size,
    /// measured with timestamp queries (the median frame).
    /// </summary>
    public double MeasureMilliseconds(VisualizerStyle style, in AudioSnapshot sound, int width, int height, int frames = 60)
    {
        var device = _gpu.Device;
        var context = _gpu.Context;
        var samples = new List<double>(frames);
        using var disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        using var begin = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        using var end = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        for (var f = 0; f < frames + 5; f++)
        {
            context.Begin(disjoint);
            context.End(begin);
            Draw(style, sound, width, height, 12 + f / 60.0);
            context.End(end);
            context.End(disjoint);
            QueryDataTimestampDisjoint d;
            ulong b, e;
            while (context.GetData(disjoint, (nint)(&d), (uint)sizeof(QueryDataTimestampDisjoint), AsyncGetDataFlags.None).Code != 0) Thread.Yield();
            while (context.GetData(begin, (nint)(&b), 8, AsyncGetDataFlags.None).Code != 0) Thread.Yield();
            while (context.GetData(end, (nint)(&e), 8, AsyncGetDataFlags.None).Code != 0) Thread.Yield();
            if (f >= 5 && !d.Disjoint && d.Frequency > 0) samples.Add((e - b) * 1000.0 / d.Frequency);
        }
        samples.Sort();
        return samples.Count == 0 ? double.NaN : samples[samples.Count / 2];
    }

    private void Ensure(int width, int height)
    {
        if (width == _width && height == _height && _target is not null) return;
        _view?.Dispose();
        _target?.Dispose();
        _staging?.Dispose();
        _width = width;
        _height = height;
        _target = _gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        _view = _gpu.Device.CreateRenderTargetView(_target);
        _staging = _gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
    }

    private byte[] Read()
    {
        _gpu.Context.CopyResource(_staging!, _target!);
        var pixels = new byte[_width * _height * 4];
        var mapped = _gpu.Context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (var y = 0; y < _height; y++)
                new ReadOnlySpan<byte>((byte*)mapped.DataPointer + y * mapped.RowPitch, _width * 4).CopyTo(pixels.AsSpan(y * _width * 4));
        }
        finally
        {
            _gpu.Context.Unmap(_staging!, 0);
        }
        return pixels;
    }

    public void Dispose()
    {
        _view?.Dispose();
        _target?.Dispose();
        _staging?.Dispose();
        var device = _gpu.Device;
        _gpu.Dispose();
        device.Dispose();
    }
}
