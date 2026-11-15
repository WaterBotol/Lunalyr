// Direct3D 11 drawing inside a Direct2D layer: the GPU effects and the line patterns run a pixel shader over a texture
// on the same device the surface's Direct2D context uses, then the layer draws that texture with Direct2D like any
// bitmap. Windows' own.

using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Backgrounds.Gpu;

/// <summary>The compiled shaders embedded in this assembly (see Shaders.targets).</summary>
public static class GpuShaderLibrary
{
    private static readonly Dictionary<string, byte[]> Cache = [];
    private static readonly Lock Gate = new();

    /// <summary>The bytecode for <paramref name="name"/> (e.g. <c>Effects.plasma</c>), or null when there is none.</summary>
    public static byte[]? Bytecode(string name)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            using var stream = typeof(GpuShaderLibrary).Assembly.GetManifestResourceStream("Lunalyr.Shaders." + name + ".cso");
            if (stream is null) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            Cache[name] = bytes;
            return bytes;
        }
    }

    /// <summary>The names of every embedded shader.</summary>
    public static IEnumerable<string> Names =>
        typeof(GpuShaderLibrary).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Lunalyr.Shaders.", StringComparison.Ordinal) && n.EndsWith(".cso", StringComparison.Ordinal))
            .Select(n => n["Lunalyr.Shaders.".Length..^".cso".Length]);
}

/// <summary>
/// A texture that one pixel shader fills each frame, readable by Direct2D as a bitmap. Everything is made when the
/// layer gets its resources or the canvas changes size; a frame only updates the constants and draws one triangle, and
/// allocates nothing.
/// </summary>
internal sealed unsafe class GpuCanvas : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VertexShader? _vertex;
    private ID3D11Buffer? _constants;
    private ID3D11Texture2D? _texture;
    private ID3D11RenderTargetView? _target;
    private ID2D1Bitmap1? _bitmap;
    private int _constantBytes;
    private bool _premultiplied;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The texture as Direct2D sees it; null until <see cref="Resize"/>.</summary>
    public ID2D1Bitmap1? Bitmap => _bitmap;

    public ID3D11Device? Device => _device;

    /// <summary>
    /// Takes up the Direct3D device behind <paramref name="dc"/>. Its constants are <paramref name="constantBytes"/>
    /// long (a multiple of 16). <paramref name="premultiplied"/> canvases carry alpha (the line patterns); the others
    /// are opaque.
    /// </summary>
    public void Create(ID2D1DeviceContext dc, int constantBytes, bool premultiplied)
    {
        Release();
        using (var d2d = dc.Device)
        using (var d2d2 = d2d.QueryInterface<ID2D1Device2>())
        using (var dxgi = d2d2.DxgiDevice)
            _device = dxgi.QueryInterface<ID3D11Device>();
        _context = _device.ImmediateContext;
        _vertex = _device.CreateVertexShader(GpuShaderLibrary.Bytecode("FullScreen.vs")
                                             ?? throw new InvalidOperationException("The full-screen vertex shader is missing."));
        _constantBytes = constantBytes;
        _constants = _device.CreateBuffer(new BufferDescription((uint)constantBytes, BindFlags.ConstantBuffer, ResourceUsage.Default));
        _premultiplied = premultiplied;
    }

    /// <summary>A pixel shader on this canvas's device, or null when <paramref name="name"/> is not embedded.</summary>
    public ID3D11PixelShader? PixelShader(string name)
    {
        var bytecode = GpuShaderLibrary.Bytecode(name);
        return bytecode is null || _device is null ? null : _device.CreatePixelShader(bytecode);
    }

    /// <summary>Makes the texture <paramref name="width"/> × <paramref name="height"/> pixels (no-op when it already is).</summary>
    public void Resize(ID2D1DeviceContext dc, int width, int height)
    {
        if (_device is null) return;
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_texture is not null && width == Width && height == Height) return;
        ReleaseTexture();
        _texture = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        _target = _device.CreateRenderTargetView(_texture);
        using var surface = _texture.QueryInterface<IDXGISurface>();
        _bitmap = dc.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, _premultiplied ? AlphaMode.Premultiplied : AlphaMode.Ignore), 96, 96, BitmapOptions.None));
        Width = width;
        Height = height;
    }

    /// <summary>Fills the texture with <paramref name="shader"/> given <paramref name="constants"/>.</summary>
    public void Draw<T>(ID3D11PixelShader shader, in T constants) where T : unmanaged
    {
        if (_context is null || _target is null || _vertex is null || _constants is null) return;
        if (sizeof(T) != _constantBytes) throw new ArgumentException("The constants are not the size the canvas was made for.");
        var context = _context;
        context.UpdateSubresource(in constants, _constants);
        if (_premultiplied) context.ClearRenderTargetView(_target, default);
        context.OMSetRenderTargets(_target, null);
        context.RSSetViewport(0, 0, Width, Height, 0, 1);
        context.RSSetState(null);
        context.OMSetBlendState(null);
        context.OMSetDepthStencilState(null, 0);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertex);
        context.GSSetShader(null);
        context.PSSetShader(shader);
        context.PSSetConstantBuffer(0, _constants);
        context.Draw(3, 0);
        // Let go of the texture as a target so Direct2D can read it.
        context.UnsetRenderTargets();
    }

    private void ReleaseTexture()
    {
        _bitmap?.Dispose(); _bitmap = null;
        _target?.Dispose(); _target = null;
        _texture?.Dispose(); _texture = null;
        Width = Height = 0;
    }

    public void Release()
    {
        ReleaseTexture();
        _constants?.Dispose(); _constants = null;
        _vertex?.Dispose(); _vertex = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose() => Release();
}
