using System.Text;
using static LunalyrSpike.Native;

namespace LunalyrSpike;

internal sealed record MonitorInfo(string Device, RECT Bounds, uint Dpi, int Hz, bool Primary);

internal static class Monitors
{
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(0, 0, (nint h, nint dc, ref RECT rc, nint d) =>
        {
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(h, ref mi);
            GetDpiForMonitor(h, 0, out uint dpi, out _);
            var dm = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            EnumDisplaySettings(mi.szDevice, -1, ref dm);
            list.Add(new MonitorInfo(mi.szDevice, mi.rcMonitor, dpi, dm.dmDisplayFrequency, (mi.dwFlags & 1) != 0));
            return true;
        }, 0);
        return list;
    }
}

internal enum AttachMode { Windowed, Win24H2, Legacy }

/// <summary>Finds where a wallpaper window goes, behind the desktop icons.</summary>
internal static class DesktopAttach
{
    public sealed record Target(AttachMode Mode, nint Parent, nint InsertAfter, int OriginX, int OriginY);

    public static Target? Find()
    {
        nint progman = FindWindow("Progman", null);
        if (progman == 0) return null;
        nint defView = FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        nint childWorker = FindWindowEx(progman, 0, "WorkerW", null);
        if (childWorker == 0 && defView != 0)
        {
            // Ask Explorer to create the wallpaper WorkerW (harmless and not persistent).
            SendMessageTimeout(progman, 0x052C, 0xD, 1, SMTO_NORMAL, 1000, out _);
            SendMessageTimeout(progman, 0x052C, 0, 0, SMTO_NORMAL, 1000, out _);
            childWorker = FindWindowEx(progman, 0, "WorkerW", null);
        }
        GetWindowRect(progman, out var pr);
        if (childWorker != 0 && defView != 0)
            return new Target(AttachMode.Win24H2, progman, defView, pr.Left, pr.Top);

        // Legacy (pre-24H2): the icons live in a top-level WorkerW; the wallpaper WorkerW follows it.
        nint legacy = 0;
        EnumWindows((h, l) =>
        {
            if (FindWindowEx(h, 0, "SHELLDLL_DefView", null) != 0)
                legacy = FindWindowEx(0, h, "WorkerW", null);
            return true;
        }, 0);
        if (legacy == 0) return null;
        GetWindowRect(legacy, out var lr);
        return new Target(AttachMode.Legacy, legacy, 0, lr.Left, lr.Top);
    }

    public static string DumpTree()
    {
        var sb = new StringBuilder();
        nint progman = FindWindow("Progman", null);
        sb.AppendLine($"Progman {progman:X} ex={GetWindowLongPtr(progman, GWL_EXSTYLE):X}");
        for (nint c = GetWindow(progman, 5 /*GW_CHILD*/); c != 0; c = GetWindow(c, 2 /*GW_HWNDNEXT*/))
        {
            GetWindowRect(c, out var r);
            sb.AppendLine($"  {ClassOf(c),-24} {c:X} vis={IsWindowVisible(c)} ex={GetWindowLongPtr(c, GWL_EXSTYLE):X} rect=({r.Left},{r.Top},{r.Width}x{r.Height})");
        }
        return sb.ToString();
    }
}
