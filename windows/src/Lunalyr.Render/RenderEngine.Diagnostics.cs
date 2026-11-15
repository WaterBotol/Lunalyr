// Settings › General › Copy diagnostics (tester feedback on 0.9.0): what the renderer knows about the GPUs, outputs and
// surfaces, as plain text a tester can paste. Built on the render thread, which owns the surfaces and the catalogue.

using System.Text;
using Vortice.DXGI;

namespace Lunalyr.Render;

public sealed partial class RenderEngine
{
    /// <summary>The adapters, their outputs and every surface, as text; empty when the renderer isn't running.</summary>
    public Task<string> DescribeAsync()
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycle)
        {
            if (_thread is null) return Task.FromResult("Renderer: not running.");
        }
        Post(() =>
        {
            try
            {
                done.TrySetResult(Describe());
            }
            catch (Exception ex)
            {
                done.TrySetResult($"Renderer: couldn't describe itself ({ex.GetType().Name}: {ex.Message}).");
            }
        });
        return done.Task.WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "Renderer: no answer in 3 s.",
            TaskScheduler.Default);
    }

    // Render thread.
    private string Describe()
    {
        var text = new StringBuilder();
        bool tearing = false;
        try
        {
            using var factory5 = _catalog.Factory.QueryInterfaceOrNull<IDXGIFactory5>();
            tearing = factory5?.PresentAllowTearing == true;
        }
        catch (Exception)
        {
        }
        text.AppendLine($"GPUs (variable refresh / tearing supported: {(tearing ? "yes" : "no")}):");
        for (uint i = 0; _catalog.Factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success && adapter is not null; i++)
        {
            using (adapter)
            {
                var desc = adapter.Description1;
                var software = (desc.Flags & AdapterFlags.Software) != 0;
                string driver;
                try
                {
                    driver = adapter.CheckInterfaceSupport<IDXGIDevice>(out var umd)
                        ? $"{umd >> 48 & 0xFFFF}.{umd >> 32 & 0xFFFF}.{umd >> 16 & 0xFFFF}.{umd & 0xFFFF}" : "unknown";
                }
                catch (Exception)
                {
                    driver = "unknown";
                }
                text.AppendLine($"  {i}: {desc.Description}{(software ? " (software)" : "")}, driver {driver}, "
                    + $"{desc.DedicatedVideoMemory / (1024 * 1024)} MB dedicated");
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success && output is not null; o++)
                {
                    using (output)
                    {
                        var od = output.Description;
                        var line = $"     output {od.DeviceName} {od.DesktopCoordinates.Right - od.DesktopCoordinates.Left}x"
                            + $"{od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top} at {od.DesktopCoordinates.Left},{od.DesktopCoordinates.Top}";
                        using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                        if (output6 is not null)
                        {
                            var d1 = output6.Description1;
                            line += $", {d1.BitsPerColor}-bit, colour space {d1.ColorSpace}, max {d1.MaxLuminance:0} nits"
                                + (d1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020 ? " (HDR on)" : "");
                        }
                        text.AppendLine(line);
                    }
                }
            }
        }
        text.AppendLine($"Surfaces (rebuilds {_rebuilds}, device recoveries {_recoveries}):");
        if (_surfaces.Count == 0) text.AppendLine("  none");
        foreach (var s in _surfaces)
        {
            var broken = s.AllLayers().Where(l => l.Broken).Select(l => l.Id).ToList();
            var layers = string.Join(", ", s.AllLayers().Select(l => l.Id + (l.Broken ? " (broken)" : l.HasResources ? "" : " (no resources)")));
            text.AppendLine($"  {(s.IsFloating ? "floating" : s.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))}: "
                + $"{s.Window.Monitor.DeviceName} {s.Width}x{s.Height} @{s.Window.Monitor.Dpi} dpi {s.Window.Monitor.RefreshHz} Hz, "
                + $"{s.Window.Mode}, GPU {s.Gpu?.Name ?? "NONE"}{(s.Gpu is not null && !s.AdapterDrivesMonitor ? " (not the output's adapter)" : "")}"
                + $"{(s.Hdr ? ", HDR" : "")}, {(s.SeeThrough ? "composition" : "window")} swap chain{(s.Swap is null ? " MISSING" : "")}, "
                + $"{s.State}{(s.Reason != default ? $" ({s.Reason})" : "")}, sync {s.Plan.SyncInterval}{(s.Plan.IsGated ? " gated" : "")}"
                + $"{(s.Dead ? ", DEAD" : "")}{(s.PresentFailures > 0 ? $", present failures {s.PresentFailures}" : "")}"
                + $"{(s.Presented ? "" : ", never presented")}");
            text.AppendLine($"     layers: {(layers.Length == 0 ? "none" : layers)}");
        }
        return text.ToString();
    }
}
