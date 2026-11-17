using System.Numerics;
using Lunalyr.Render.Scene;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using AlphaMode = Vortice.DCommon.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using FactoryType = Vortice.Direct2D1.FactoryType;

namespace Lunalyr.Render.Diagnostics;

/// <summary>
/// A Direct2D surface with no window, for tests and the layer preview tool: a layer drawn here is drawn exactly as on a
/// wallpaper, then read back as pixels or saved as a PNG. Uses the WARP software rasteriser unless asked for hardware.
/// </summary>
public sealed class OffscreenSurface : IDisposable
{
    private readonly ID3D11Device _d3d;
    private readonly ID3D11DeviceContext _d3dContext;
    private readonly ID2D1Device _device;
    private readonly ID2D1Bitmap1 _target;
    private readonly ID2D1Bitmap1 _readback;

    public OffscreenSurface(int width, int height, float scale = 1, bool hardware = false)
        : this(width, height, scale, null, hardware ? DriverType.Hardware : DriverType.Warp)
    {
    }

    /// <summary>A surface on one particular GPU (for measuring a layer on the integrated or the discrete GPU).</summary>
    public OffscreenSurface(int width, int height, float scale, IDXGIAdapter adapter)
        : this(width, height, scale, adapter ?? throw new ArgumentNullException(nameof(adapter)), DriverType.Unknown)
    {
    }

    private OffscreenSurface(int width, int height, float scale, IDXGIAdapter? adapter, DriverType driver)
    {
        Width = width;
        Height = height;
        Scale = scale;
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice(adapter, driver, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? d3d, out ID3D11DeviceContext? context).CheckError();
        _d3d = d3d!;
        _d3dContext = context!;
        Factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
        using (var dxgi = _d3d.QueryInterface<IDXGIDevice>()) _device = Factory.CreateDevice(dxgi);
        Context = _device.CreateDeviceContext(DeviceContextOptions.None);
        DWrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>();
        var format = new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
        _target = Context.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        _readback = Context.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
                                         new BitmapProperties1(format, 96, 96, BitmapOptions.CannotDraw | BitmapOptions.CpuRead));
        Context.Target = _target;
    }

    public int Width { get; }
    public int Height { get; }
    public float Scale { get; }
    public ID2D1Factory1 Factory { get; }
    public ID2D1DeviceContext Context { get; }
    public IDWriteFactory DWrite { get; }
    public Vector2 Size => new(Width, Height);

    /// <summary>Gives <paramref name="layer"/> its resources for this surface.</summary>
    public void Prepare(IRenderLayer layer) => layer.CreateResources(Context, DWrite, Size, Scale);

    /// <summary>Draws one frame of <paramref name="layer"/> over black.</summary>
    public void Draw(IRenderLayer layer, SceneSnapshot scene, double wallSeconds, double deltaSeconds = 1 / 60.0)
    {
        Context.BeginDraw();
        Context.Clear(new Color4(0, 0, 0, 1));
        var frame = new FrameContext
        {
            Dc = Context, DWrite = DWrite, D2DFactory = Factory, Size = Size, Scale = Scale, WallSeconds = wallSeconds,
            SongSeconds = double.NaN, IsPlaying = scene.Title is not null, Scene = scene, DeltaSeconds = deltaSeconds,
        };
        layer.Render(frame);
        Context.EndDraw().CheckError();
    }

    /// <summary>
    /// Draws one frame of <paramref name="layers"/>, bottom first, at song time <paramref name="songSeconds"/> (NaN when
    /// nothing plays), as the wallpaper would: the settings window's live preview.
    /// </summary>
    public void Draw(IReadOnlyList<IRenderLayer> layers, SceneSnapshot scene, double songSeconds, bool playing, double wallSeconds,
                     double deltaSeconds, double songJoined = 0)
    {
        ArgumentNullException.ThrowIfNull(layers);
        Context.BeginDraw();
        Context.Transform = Matrix3x2.Identity;
        Context.Clear(new Color4(0, 0, 0, 1));
        var frame = new FrameContext
        {
            Dc = Context, DWrite = DWrite, D2DFactory = Factory, Size = Size, Scale = Scale, WallSeconds = wallSeconds,
            SongSeconds = songSeconds, IsPlaying = playing, Scene = scene, DeltaSeconds = deltaSeconds, SongJoined = songJoined,
        };
        foreach (var layer in layers)
        {
            Context.Transform = Matrix3x2.Identity;
            layer.Render(frame);
        }
        Context.EndDraw().CheckError();
    }

    private ID2D1Bitmap1? _inset;

    /// <summary>
    /// Draws <paramref name="layer"/> (prepared for <paramref name="width"/> × <paramref name="height"/> at
    /// <paramref name="scale"/>) on its own transparent bitmap, then over the surface at (<paramref name="x"/>,
    /// <paramref name="y"/>): a window inside the picture, such as the floating title card on the Settings picture.
    /// </summary>
    public void DrawInset(IRenderLayer layer, SceneSnapshot scene, int x, int y, int width, int height, float scale,
                          double songSeconds, bool playing, double wallSeconds, double deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (width < 1 || height < 1) return;
        if (_inset is null || _inset.PixelSize.Width != width || _inset.PixelSize.Height != height)
        {
            _inset?.Dispose();
            _inset = Context.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        }
        Context.Target = _inset;
        try
        {
            Context.BeginDraw();
            Context.Transform = Matrix3x2.Identity;
            Context.Clear(new Color4(0, 0, 0, 0));
            var frame = new FrameContext
            {
                Dc = Context, DWrite = DWrite, D2DFactory = Factory, Size = new Vector2(width, height), Scale = scale,
                WallSeconds = wallSeconds, SongSeconds = songSeconds, IsPlaying = playing, Scene = scene, DeltaSeconds = deltaSeconds,
            };
            layer.Render(frame);
            Context.EndDraw().CheckError();
        }
        finally
        {
            Context.Target = _target;
        }
        Context.BeginDraw();
        Context.Transform = Matrix3x2.Identity;
        Context.DrawBitmap(_inset, new Vortice.RawRectF(x, y, x + width, y + height), 1, Vortice.Direct2D1.BitmapInterpolationMode.NearestNeighbor, null);
        Context.EndDraw().CheckError();
    }

    /// <summary>The surface's pixels: premultiplied BGRA, top row first, <see cref="Width"/> × 4 bytes a row.</summary>
    public byte[] ReadPixels()
    {
        var pixels = new byte[Width * Height * 4];
        ReadPixels(pixels);
        return pixels;
    }

    /// <summary>Reads the surface's pixels into <paramref name="pixels"/> (at least <see cref="Width"/> × <see cref="Height"/> × 4 bytes).</summary>
    public void ReadPixels(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length < Width * Height * 4) throw new ArgumentException("The buffer is too small for the surface.", nameof(pixels));
        _readback.CopyFromBitmap(_target);
        var map = _readback.Map(MapOptions.Read);
        try
        {
            for (var y = 0; y < Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(map.Bits + (int)(y * map.Pitch), pixels, y * Width * 4, Width * 4);
        }
        finally
        {
            _readback.Unmap();
        }
    }

    /// <summary>Saves the surface as a PNG.</summary>
    public void SavePng(string path)
    {
        var pixels = ReadPixels();
        using var wic = new IWICImagingFactory();
        using var file = File.Create(path);
        using var encoder = wic.CreateEncoder(ContainerFormat.Png, file);
        using var frame = encoder.CreateNewFrame(out var properties);
        frame.Initialize(properties);
        frame.SetSize((uint)Width, (uint)Height);
        var format = Vortice.WIC.PixelFormat.Format32bppBGRA;
        frame.SetPixelFormat(ref format);
        unsafe
        {
            fixed (byte* data = pixels) frame.WritePixels((uint)Height, (uint)(Width * 4), (uint)pixels.Length, data);
        }
        frame.Commit();
        encoder.Commit();
        properties?.Dispose();
    }

    public void Dispose()
    {
        Context.Target = null;
        _inset?.Dispose();
        _readback.Dispose();
        _target.Dispose();
        Context.Dispose();
        DWrite.Dispose();
        _device.Dispose();
        Factory.Dispose();
        _d3dContext.Dispose();
        _d3d.Dispose();
    }
}
