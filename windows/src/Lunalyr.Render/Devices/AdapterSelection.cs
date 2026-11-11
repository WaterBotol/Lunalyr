namespace Lunalyr.Render.Devices;

/// <summary>A DXGI adapter and the monitors its outputs drive.</summary>
/// <param name="Luid">The adapter's locally unique id (stable while the adapter exists).</param>
/// <param name="Software">The Microsoft Basic Render Driver (WARP): never chosen while a hardware adapter exists.</param>
/// <param name="Monitors">HMONITOR of each output.</param>
public sealed record AdapterOutputs(long Luid, string Name, bool Software, IReadOnlyList<nint> Monitors);

/// <summary>Picks the GPU to render each monitor's wallpaper on.</summary>
public static class AdapterSelection
{
    /// <summary>
    /// The adapter whose output drives <paramref name="monitor"/>, so the wallpaper renders where it is displayed:
    /// rendering on the other GPU of a hybrid laptop cost 33 % iGPU load and +250 MB in the spike, and keeps a
    /// discrete GPU awake. When no output matches (a monitor in transition, an indirect display driver), the first
    /// hardware adapter (DXGI lists the one driving the primary display first); null only when there are none.
    /// </summary>
    public static AdapterOutputs? For(nint monitor, IReadOnlyList<AdapterOutputs> adapters)
    {
        foreach (var adapter in adapters)
            if (!adapter.Software && monitor != 0 && adapter.Monitors.Contains(monitor)) return adapter;
        foreach (var adapter in adapters)
            if (!adapter.Software) return adapter;
        return adapters.Count > 0 ? adapters[0] : null;
    }

    /// <summary>Whether <paramref name="chosen"/> actually drives <paramref name="monitor"/> (false means a fallback, which is logged).</summary>
    public static bool Drives(AdapterOutputs chosen, nint monitor) => chosen.Monitors.Contains(monitor);
}
