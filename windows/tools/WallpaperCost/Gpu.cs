using System.Numerics;
using System.Runtime.InteropServices;
using Lunalyr.Render.Devices;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using AlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Lunalyr.Tools.WallpaperCost;

/// <summary>
/// The wallpaper's device as the app makes it (<c>GpuDevice.Create</c>: video support for hardware decoding, the device
/// lock on, grayscale text), drawing into one offscreen bitmap instead of a swap chain. One frame in flight, as the
/// app's swap chains allow (a frame-latency of 1): a fence stands in for the latency waitable. D3D timestamps around
/// each frame give its GPU time.
/// </summary>
internal sealed partial class Gpu : IDisposable
{
    private readonly ID3D11Device _d3d;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11DeviceContext4? _context4;
    private readonly ID3D11Fence? _fence;
    private readonly nint _fenceEvent;
    private ulong _fenceValue;
    private readonly ID3D11Query _disjoint, _start, _end;
    private bool _queryPending;
    private readonly ID2D1Device _device;
    private readonly ID2D1Bitmap1 _target;

    private Gpu(IDXGIAdapter1 adapter, string name, long luid, int width, int height, float scale)
    {
        AdapterName = name;
        Luid = luid;
        Width = width;
        Height = height;
        Scale = scale;
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        VideoSupport = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels, out ID3D11Device? d3d, out ID3D11DeviceContext? context).Success;
        if (!VideoSupport)
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out d3d, out context).CheckError();
        _d3d = d3d!;
        _context = context!;
        using (var multithread = _d3d.QueryInterfaceOrNull<ID3D11Multithread>()) multithread?.SetMultithreadProtected(true);
        Factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        DWrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        using (var dxgi = _d3d.QueryInterface<IDXGIDevice1>()) _device = Factory.CreateDevice(dxgi);
        Dc = _device.CreateDeviceContext(DeviceContextOptions.None);
        Dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        // As the app's composition swap chains: premultiplied, so a see-through layer clears to transparent.
        _target = Dc.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        Dc.Target = _target;

        using var device5 = _d3d.QueryInterfaceOrNull<ID3D11Device5>();
        _context4 = _context.QueryInterfaceOrNull<ID3D11DeviceContext4>();
        if (device5 is not null && _context4 is not null)
        {
            _fence = device5.CreateFence<ID3D11Fence>(0, FenceFlags.None);
            _fenceEvent = CreateEventW(0, false, false, null);
        }
        _disjoint = _d3d.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        _start = _d3d.CreateQuery(new QueryDescription(QueryType.Timestamp));
        _end = _d3d.CreateQuery(new QueryDescription(QueryType.Timestamp));
    }

    public string AdapterName { get; }
    public long Luid { get; }
    public bool VideoSupport { get; }
    public bool HasFence => _fence is not null;
    public int Width { get; }
    public int Height { get; }
    public float Scale { get; }
    public ID2D1Factory1 Factory { get; }
    public IDWriteFactory DWrite { get; }
    public ID2D1DeviceContext Dc { get; }
    public Vector2 Size => new(Width, Height);

    /// <summary>
    /// The device on the adapter whose name contains <paramref name="adapterName"/>, or else the one the app would pick
    /// for the main display (<see cref="AdapterSelection"/>: the adapter whose output drives it).
    /// </summary>
    public static Gpu Create(string? adapterName, int width, int height, float scale)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var adapters = new List<(AdapterOutputs Info, IDXGIAdapter1 Adapter)>();
        try
        {
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success && adapter is not null; i++)
            {
                var desc = adapter.Description1;
                var monitors = new List<nint>();
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success && output is not null; o++)
                    using (output) monitors.Add(output.Description.Monitor);
                long luid = ((long)desc.Luid.HighPart << 32) | desc.Luid.LowPart;
                adapters.Add((new AdapterOutputs(luid, desc.Description, (desc.Flags & AdapterFlags.Software) != 0, monitors), adapter));
            }
            AdapterOutputs? chosen;
            if (adapterName is not null)
            {
                chosen = adapters.Select(a => a.Info).FirstOrDefault(a => a.Name.Contains(adapterName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No adapter matches \"{adapterName}\" (there are: {string.Join(", ", adapters.Select(a => a.Info.Name))}).");
            }
            else
            {
                chosen = AdapterSelection.For(MonitorFromPoint(default, MonitorDefaultToPrimary), [.. adapters.Select(a => a.Info)])
                    ?? throw new InvalidOperationException("No graphics adapter.");
            }
            var pick = adapters.First(a => a.Info.Luid == chosen.Luid);
            if (pick.Info.Software) throw new InvalidOperationException($"{pick.Info.Name} is the software rasteriser; measure on a GPU.");
            return new Gpu(pick.Adapter, pick.Info.Name, pick.Info.Luid, width, height, scale);
        }
        finally
        {
            foreach (var a in adapters) a.Adapter.Dispose();
        }
    }

    /// <summary>The adapter's LUID as the GPU performance counters name it (<c>luid_0x00000000_0x0000ABCD</c>).</summary>
    public string CounterLuid => $"luid_0x{(uint)(Luid >> 32):X8}_0x{(uint)Luid:X8}";

    public void Prepare(IRenderLayer layer) => layer.CreateResources(Dc, DWrite, Size, Scale);

    /// <summary>
    /// Waits until the GPU has finished the previous frame (the swap chain's frame latency of 1) and returns that
    /// frame's GPU time in milliseconds, or NaN when it couldn't be read.
    /// </summary>
    public double FinishPrevious()
    {
        if (_fence is not null && _fence.CompletedValue < _fenceValue)
        {
            _fence.SetEventOnCompletion(_fenceValue, _fenceEvent);
            WaitForSingleObject(_fenceEvent, 1000);
        }
        if (!_queryPending) return double.NaN;
        _queryPending = false;
        if (!TryRead(_disjoint, out QueryDataTimestampDisjoint disjoint) || disjoint.Disjoint || disjoint.Frequency == 0) return double.NaN;
        if (!TryRead(_start, out ulong start) || !TryRead(_end, out ulong end) || end < start) return double.NaN;
        return (end - start) * 1000.0 / disjoint.Frequency;
    }

    /// <summary>Opens a frame: its GPU timestamp, then Direct2D's BeginDraw.</summary>
    public void BeginFrame()
    {
        _context.Begin(_disjoint);
        _context.End(_start);
    }

    /// <summary>Closes a frame: the closing timestamp, the work submitted (the app's Present does that), the fence.</summary>
    public void EndFrame()
    {
        _context.End(_end);
        _context.End(_disjoint);
        _queryPending = true;
        if (_fence is not null) _context4!.Signal(_fence, ++_fenceValue);
        _context.Flush();
    }

    private unsafe bool TryRead<T>(ID3D11Query query, out T value) where T : unmanaged
    {
        T data;
        // The fence has passed, so the data is there; without one, wait a little.
        for (var i = 0; i < 2000; i++)
        {
            if (_context.GetData(query, (IntPtr)(&data), (uint)sizeof(T), AsyncGetDataFlags.DoNotFlush).Code == 0)
            {
                value = data;
                return true;
            }
            if (_fence is not null && i > 10) break;
            Thread.Sleep(0);
        }
        value = default;
        return false;
    }

    /// <summary>The surface's pixels (premultiplied BGRA), read back once for a snapshot; never per measured frame.</summary>
    public byte[] ReadPixels()
    {
        using var readback = Dc.CreateBitmap(new SizeI(Width, Height), IntPtr.Zero, 0,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96,
                BitmapOptions.CannotDraw | BitmapOptions.CpuRead));
        readback.CopyFromBitmap(_target);
        var map = readback.Map(MapOptions.Read);
        var pixels = new byte[Width * Height * 4];
        try
        {
            for (var y = 0; y < Height; y++)
                Marshal.Copy(map.Bits + (int)(y * map.Pitch), pixels, y * Width * 4, Width * 4);
        }
        finally
        {
            readback.Unmap();
        }
        return pixels;
    }

    /// <summary>Saves BGRA pixels as a PNG.</summary>
    public static unsafe void SavePng(string path, byte[] bgra, int width, int height)
    {
        using var wic = new IWICImagingFactory();
        using var file = File.Create(path);
        using var encoder = wic.CreateEncoder(ContainerFormat.Png, file);
        using var frame = encoder.CreateNewFrame(out var properties);
        frame.Initialize(properties);
        frame.SetSize((uint)width, (uint)height);
        var format = Vortice.WIC.PixelFormat.Format32bppBGRA;
        frame.SetPixelFormat(ref format);
        fixed (byte* data = bgra) frame.WritePixels((uint)height, (uint)(width * 4), (uint)bgra.Length, data);
        frame.Commit();
        encoder.Commit();
        properties?.Dispose();
    }

    public void Dispose()
    {
        Dc.Target = null;
        _target.Dispose();
        Dc.Dispose();
        _device.Dispose();
        DWrite.Dispose();
        Factory.Dispose();
        _disjoint.Dispose();
        _start.Dispose();
        _end.Dispose();
        _fence?.Dispose();
        if (_fenceEvent != 0) CloseHandle(_fenceEvent);
        _context4?.Dispose();
        _context.ClearState();
        _context.Flush();
        _context.Dispose();
        _d3d.Dispose();
    }

    private const uint MonitorDefaultToPrimary = 1;

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(PointInt point, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt
    {
        public int X, Y;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
