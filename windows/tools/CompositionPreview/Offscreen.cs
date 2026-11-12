// An offscreen Direct2D stage for previews and tests: a D3D11 device (hardware, or WARP when asked or when there is
// none), a Direct2D device context drawing into a bitmap, and a CPU copy to read it back and save it as a PNG.

using System.IO.Compression;
using System.Numerics;
using Lunalyr.Render.Scene;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Lunalyr.Tools.CompositionPreview;

public sealed class Offscreen : IDisposable
{
    private readonly ID3D11Device _d3d;
    private readonly ID3D11DeviceContext _context;
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    private readonly ID2D1Bitmap1 _target;
    private readonly ID2D1Bitmap1 _readback;
    private double _lastWall = double.NaN;

    public Offscreen(int width, int height, bool warp = false)
    {
        Width = width;
        Height = height;
        Vortice.Direct3D.FeatureLevel[] levels = [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1];
        ID3D11Device? d3d = null;
        ID3D11DeviceContext? context = null;
        if (warp || D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out d3d, out context).Failure)
        {
            D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out d3d, out context).CheckError();
            IsWarp = true;
        }
        _d3d = d3d!;
        _context = context!;
        using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
        using (var adapter = dxgi.GetAdapter()) AdapterName = adapter.Description.Description;
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _device = _factory.CreateDevice(dxgi);
        Dc = _device.CreateDeviceContext(DeviceContextOptions.None);
        Dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        DWrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>();
        var format = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        _target = Dc.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        _readback = Dc.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
                                    new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
    }

    public int Width { get; }
    public int Height { get; }
    public bool IsWarp { get; }
    public string AdapterName { get; } = "";
    public ID2D1DeviceContext Dc { get; }
    public IDWriteFactory DWrite { get; }

    /// <summary>Draws one frame of <paramref name="layer"/> over a plain background.</summary>
    public void Render(IRenderLayer layer, SceneSnapshot scene, double songSeconds, double wallSeconds, float scale,
                       Vector4 background, bool playing = true)
    {
        var delta = double.IsFinite(_lastWall) ? Math.Clamp(wallSeconds - _lastWall, 0, 0.25) : 1.0 / 60;
        _lastWall = wallSeconds;
        Dc.Target = _target;
        Dc.BeginDraw();
        Dc.Transform = Matrix3x2.Identity;
        Dc.Clear(new Color4(background.X, background.Y, background.Z, 1));
        var frame = new FrameContext
        {
            Dc = Dc, DWrite = DWrite, D2DFactory = _factory, Size = new Vector2(Width, Height), Scale = scale,
            WallSeconds = wallSeconds, SongSeconds = songSeconds, IsPlaying = playing, Scene = scene, DeltaSeconds = delta,
        };
        layer.Render(frame);
        Dc.EndDraw().CheckError();
        Dc.Target = null;
    }

    /// <summary>
    /// Draws one frame of <paramref name="layers"/>, in order (a background, then the lyrics), as the engine does: on
    /// black, each with the frame's song time and <paramref name="joined"/> (<see cref="FrameContext.SongJoined"/>).
    /// </summary>
    public void Render(IReadOnlyList<IRenderLayer> layers, SceneSnapshot scene, double songSeconds, double wallSeconds, float scale,
                       double joined = 0, bool playing = true)
    {
        var delta = double.IsFinite(_lastWall) ? Math.Clamp(wallSeconds - _lastWall, 0, 0.25) : 1.0 / 60;
        _lastWall = wallSeconds;
        Dc.Target = _target;
        Dc.BeginDraw();
        Dc.Transform = Matrix3x2.Identity;
        Dc.Clear(new Color4(0, 0, 0, 1));
        var frame = new FrameContext
        {
            Dc = Dc, DWrite = DWrite, D2DFactory = _factory, Size = new Vector2(Width, Height), Scale = scale,
            WallSeconds = wallSeconds, SongSeconds = songSeconds, IsPlaying = playing, Scene = scene, DeltaSeconds = delta,
            SongJoined = joined,
        };
        foreach (var layer in layers)
        {
            Dc.Transform = Matrix3x2.Identity;
            layer.Render(frame);
        }
        Dc.EndDraw().CheckError();
        Dc.Target = null;
    }

    /// <summary>The last frame's pixels, BGRA, top row first, alpha opaque.</summary>
    public unsafe byte[] Pixels()
    {
        _readback.CopyFromBitmap(_target).CheckError();
        var mapped = _readback.Map(MapOptions.Read);
        try
        {
            var pixels = new byte[Width * Height * 4];
            for (var y = 0; y < Height; y++)
                new ReadOnlySpan<byte>((byte*)mapped.Bits + (long)y * mapped.Pitch, Width * 4).CopyTo(pixels.AsSpan(y * Width * 4));
            return pixels;
        }
        finally
        {
            _readback.Unmap();
        }
    }

    /// <summary>Saves the last frame as an RGB PNG.</summary>
    public void SavePng(string path) => Png.Save(path, Pixels(), Width, Height);

    public void Dispose()
    {
        _readback.Dispose();
        _target.Dispose();
        DWrite.Dispose();
        Dc.Dispose();
        _device.Dispose();
        _factory.Dispose();
        _context.Dispose();
        _d3d.Dispose();
    }
}

/// <summary>A minimal PNG writer (8-bit RGB) for BGRA pixels.</summary>
public static class Png
{
    public static void Save(string path, byte[] bgra, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var raw = new byte[height * (width * 3 + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 3 + 1);
            for (var x = 0; x < width; x++)
            {
                var source = (y * width + x) * 4;
                raw[row + 1 + x * 3] = bgra[source + 2];
                raw[row + 2 + x * 3] = bgra[source + 1];
                raw[row + 3 + x * 3] = bgra[source];
            }
        }
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BigEndian(header, 0, (uint)width);
        BigEndian(header, 4, (uint)height);
        header[8] = 8;
        header[9] = 2;
        Chunk(file, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true)) z.Write(raw);
            Chunk(file, "IDAT", compressed.ToArray());
        }
        Chunk(file, "IEND", []);
    }

    private static void BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(name);
        stream.Write(data);
        var crc = Crc(name, data);
        var tail = new byte[4];
        BigEndian(tail, 0, crc);
        stream.Write(tail);
    }

    private static uint Crc(byte[] name, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in name) crc = Step(crc, b);
        foreach (var b in data) crc = Step(crc, b);
        return crc ^ 0xFFFFFFFFu;

        static uint Step(uint crc, byte b)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            return crc;
        }
    }
}
