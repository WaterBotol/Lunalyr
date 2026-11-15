// The GPU side of the visualisers on one D3D11 device: the compiled shaders (embedded at build time, see
// Shaders/Shaders.targets), the constant buffers, and one scene's render targets and photosensitivity limiter
// (the Mac's VisualizerGPU.swift and ExposureProbe; Lunalyr's own lines, written for Direct3D 11).

using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Lunalyr.Render.Visualisers;

/// <summary>The device, its immediate context, the shaders and the shared constant buffers. Render thread only.</summary>
internal sealed unsafe class VisualiserGpu : IDisposable
{
    /// <summary>The audio buffer: 64 bands and 256 waveform samples.</summary>
    public const int AudioFloats = 64 + 256;
    /// <summary>The per-scene buffer: Star Trails' state, the largest user.</summary>
    public const int SceneFloat4s = 205;

    private readonly Dictionary<string, ID3D11PixelShader> _scenes = new(StringComparer.Ordinal);
    private readonly ID3D11Buffer[] _buffers;
    private readonly ID3D11ShaderResourceView[] _views = new ID3D11ShaderResourceView[2];
    private readonly ID3D11RenderTargetView[] _noTargets = [];

    public VisualiserGpu(ID3D11Device device)
    {
        Device = device;
        Context = device.ImmediateContext;
        Vertex = CreateVertex(device, Load("Fullscreen"));
        Measure = CreatePixel(device, Load("ExposureMeasure"));
        Uniforms = DynamicBuffer(device, Core.Visualisers.VizUniforms.ByteSize);
        Audio = DynamicBuffer(device, AudioFloats * 4);
        Album = DynamicBuffer(device, 5 * 16);
        Scene = DynamicBuffer(device, SceneFloat4s * 16);
        Params = DynamicBuffer(device, 16);
        _buffers = [Uniforms, Audio, Album, Scene];
        // The limiter's state while it is off: a unit gain.
        float* unity = stackalloc float[2] { 1, 0 };
        var data = new SubresourceData(unity, 8);
        UnityState = device.CreateTexture2D(new Texture2DDescription(Format.R32G32_Float, 1, 1, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Immutable), [data]);
        UnityView = device.CreateShaderResourceView(UnityState);
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11VertexShader Vertex { get; }
    public ID3D11PixelShader Measure { get; }
    public ID3D11Buffer Uniforms { get; }
    public ID3D11Buffer Audio { get; }
    public ID3D11Buffer Album { get; }
    public ID3D11Buffer Scene { get; }
    public ID3D11Buffer Params { get; }
    public ID3D11Texture2D UnityState { get; }
    public ID3D11ShaderResourceView UnityView { get; }

    /// <summary>GPU objects for the Direct3D device behind a Direct2D context (the layer keeps one per device).</summary>
    public static VisualiserGpu For(Vortice.Direct2D1.ID2D1DeviceContext dc)
    {
        using var d2d = dc.Device;
        using var d2d2 = d2d.QueryInterface<Vortice.Direct2D1.ID2D1Device2>();
        using var dxgi = d2d2.DxgiDevice;
        return new VisualiserGpu(dxgi.QueryInterface<ID3D11Device>());
    }

    /// <summary>The scene's pixel shader (created on first use).</summary>
    public ID3D11PixelShader Shader(string name)
    {
        if (_scenes.TryGetValue(name, out var shader)) return shader;
        shader = CreatePixel(Device, Load(name));
        _scenes[name] = shader;
        return shader;
    }

    /// <summary>Copies <paramref name="data"/> into a dynamic constant buffer.</summary>
    public void Write<T>(ID3D11Buffer buffer, ReadOnlySpan<T> data) where T : unmanaged
    {
        var mapped = Context.Map(buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = MemoryMarshal.AsBytes(data);
            bytes.CopyTo(new Span<byte>((void*)mapped.DataPointer, bytes.Length));
        }
        finally
        {
            Context.Unmap(buffer, 0);
        }
    }

    public void Write<T>(ID3D11Buffer buffer, in T value) where T : unmanaged =>
        Write(buffer, new ReadOnlySpan<T>(in value));

    /// <summary>Binds the shared stage: the fullscreen triangle, its buffers, no blending, depth or culling surprises.</summary>
    public void BindStage()
    {
        var context = Context;
        context.ClearState();
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(Vertex);
        context.PSSetConstantBuffers(0, 4, _buffers);
    }

    public void SetTarget(ID3D11RenderTargetView target, int width, int height)
    {
        Context.OMSetRenderTargets(target);
        Context.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, width, height, 0, 1));
    }

    public void SetResources(ID3D11ShaderResourceView? first, ID3D11ShaderResourceView? second = null)
    {
        _views[0] = first!;
        _views[1] = second!;
        Context.PSSetShaderResources(0, 2, _views);
        _views[0] = _views[1] = null!;
    }

    /// <summary>Unbinds every target and resource, so Direct2D can read the scene's texture.</summary>
    public void Unbind()
    {
        SetResources(null, null);
        Context.OMSetRenderTargets(0, _noTargets, null);
        Context.ClearState();
    }

    public void Draw(ID3D11PixelShader shader)
    {
        Context.PSSetShader(shader);
        Context.Draw(3, 0);
    }

    private static ID3D11Buffer DynamicBuffer(ID3D11Device device, int bytes) =>
        device.CreateBuffer((uint)((bytes + 15) / 16 * 16), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);

    private static ID3D11VertexShader CreateVertex(ID3D11Device device, byte[] code)
    {
        fixed (byte* bytes = code) return device.CreateVertexShader(bytes, (nuint)code.Length, null);
    }

    private static ID3D11PixelShader CreatePixel(ID3D11Device device, byte[] code)
    {
        fixed (byte* bytes = code) return device.CreatePixelShader(bytes, (nuint)code.Length, null);
    }

    /// <summary>A compiled shader embedded in this assembly.</summary>
    public static byte[] Load(string name)
    {
        using var stream = typeof(VisualiserGpu).Assembly.GetManifestResourceStream($"Lunalyr.Render.Visualisers.{name}.cso")
            ?? throw new InvalidOperationException($"The visualiser shader \"{name}\" isn't embedded in this build.");
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        return code;
    }

    /// <summary>Whether a shader is embedded (every scene's should be).</summary>
    public static bool Has(string name) =>
        typeof(VisualiserGpu).Assembly.GetManifestResourceInfo($"Lunalyr.Render.Visualisers.{name}.cso") is not null;

    public void Dispose()
    {
        foreach (var shader in _scenes.Values) shader.Dispose();
        _scenes.Clear();
        UnityView.Dispose();
        UnityState.Dispose();
        Params.Dispose();
        Scene.Dispose();
        Album.Dispose();
        Audio.Dispose();
        Uniforms.Dispose();
        Measure.Dispose();
        Vertex.Dispose();
        Context.Dispose();
    }
}
