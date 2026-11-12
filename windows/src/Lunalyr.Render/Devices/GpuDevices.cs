using Lunalyr.Core.Logging;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Lunalyr.Render.Devices;

/// <summary>A D3D11 device and its Direct2D device context on one adapter, shared by every surface that adapter drives.</summary>
internal sealed class GpuDevice : IDisposable
{
    private GpuDevice(long luid, string name, ID3D11Device d3d, ID3D11DeviceContext context, ID2D1Device d2d, ID2D1DeviceContext dc)
    {
        Luid = luid;
        Name = name;
        D3D = d3d;
        D3DContext = context;
        D2D = d2d;
        Dc = dc;
    }

    public long Luid { get; }
    public string Name { get; }
    public ID3D11Device D3D { get; }
    public ID3D11DeviceContext D3DContext { get; }
    public ID2D1Device D2D { get; }
    public ID2D1DeviceContext Dc { get; }

    /// <summary>Surfaces using this device.</summary>
    public int Users { get; set; }

    /// <summary>Set when a call reported device removal or reset; every surface on it is recreated.</summary>
    public bool Lost { get; set; }

    /// <summary>The device was made with video support (hardware video decoding).</summary>
    public bool HasVideoSupport { get; private init; }

    public static GpuDevice Create(IDXGIAdapter1 adapter, long luid, string name, ID2D1Factory1 d2dFactory)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        // Video support lets an imported video decode on this GPU (DXVA through Media Foundation); a device that can't
        // offer it is made without, and such videos decode in memory instead.
        var video = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels, out ID3D11Device? d3d, out ID3D11DeviceContext? context).Success;
        if (!video)
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out d3d, out context).CheckError();
        // Media Foundation's decoder uses the device from its own threads; the device's lock keeps that and the render
        // thread's drawing apart.
        using (var multithread = d3d!.QueryInterfaceOrNull<ID3D11Multithread>()) multithread?.SetMultithreadProtected(true);
        // Only the swap chains get a frame latency of 1. Setting it on the device too made Present block for up to
        // 4.5 ms (p95) in the spike and raised CPU by ~50%.
        using var dxgi = d3d!.QueryInterface<IDXGIDevice1>();
        var d2d = d2dFactory.CreateDevice(dxgi);
        var dc = d2d.CreateDeviceContext(DeviceContextOptions.None);
        dc.TextAntialiasMode = TextAntialiasMode.Grayscale;
        return new GpuDevice(luid, name, d3d, context!, d2d, dc) { HasVideoSupport = video };
    }

    public void Dispose()
    {
        Dc.Target = null;
        Dc.Dispose();
        D2D.Dispose();
        D3DContext.ClearState();
        D3DContext.Flush();
        D3DContext.Dispose();
        D3D.Dispose();
    }
}

/// <summary>
/// The DXGI factory, its adapters and which monitors their outputs drive. Rebuilt whenever the factory stops being
/// current (an adapter or output was added, removed or changed, an HDR switch), which the render thread checks
/// every couple of seconds.
/// </summary>
internal sealed class AdapterCatalog : IDisposable
{
    private readonly CategoryLogger _log;
    private readonly List<(AdapterOutputs Info, IDXGIAdapter1 Adapter)> _adapters = [];
    private readonly Dictionary<nint, bool> _hdr = [];

    public AdapterCatalog(CategoryLogger log)
    {
        _log = log;
        Refresh();
    }

    public IDXGIFactory2 Factory { get; private set; } = null!;

    public IReadOnlyList<AdapterOutputs> Adapters => [.. _adapters.Select(a => a.Info)];

    /// <summary>False once the display configuration changed under this factory.</summary>
    public bool IsCurrent => Factory.IsCurrent;

    public void Refresh()
    {
        ReleaseAdapters();
        Factory?.Dispose();
        Factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        for (uint i = 0; Factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success && adapter is not null; i++)
        {
            var desc = adapter.Description1;
            long luid = ((long)desc.Luid.HighPart << 32) | desc.Luid.LowPart;
            bool software = (desc.Flags & AdapterFlags.Software) != 0;
            var monitors = new List<nint>();
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success && output is not null; o++)
            {
                using (output)
                {
                    nint monitor = output.Description.Monitor;
                    monitors.Add(monitor);
                    using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                    if (output6 is not null) _hdr[monitor] = output6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                }
            }
            _adapters.Add((new AdapterOutputs(luid, desc.Description, software, monitors), adapter));
        }
        _log.Debug("Adapters: " + string.Join("; ", _adapters.Select(a =>
            $"{a.Info.Name}{(a.Info.Software ? " (software)" : "")} drives {a.Info.Monitors.Count} output(s)")));
    }

    /// <summary>Whether the output showing <paramref name="monitor"/> is in HDR (advanced colour).</summary>
    public bool IsHdr(nint monitor) => _hdr.TryGetValue(monitor, out bool hdr) && hdr;

    public IDXGIAdapter1? AdapterFor(long luid)
    {
        foreach (var a in _adapters) if (a.Info.Luid == luid) return a.Adapter;
        return null;
    }

    private void ReleaseAdapters()
    {
        foreach (var a in _adapters) a.Adapter.Dispose();
        _adapters.Clear();
        _hdr.Clear();
    }

    public void Dispose()
    {
        ReleaseAdapters();
        Factory?.Dispose();
    }
}
