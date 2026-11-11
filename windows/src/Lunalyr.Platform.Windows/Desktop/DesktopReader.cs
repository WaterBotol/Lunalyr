using System.Runtime.InteropServices;
using static Lunalyr.Platform.Desktop.DesktopNative;

namespace Lunalyr.Platform.Desktop;

/// <summary>Reads the monitors and the top-level window list. No window is changed, and no title or content is read.</summary>
public static unsafe class DesktopReader
{
    /// <summary>Every monitor, as GDI lists them (the primary first is not guaranteed).</summary>
    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        var handle = GCHandle.Alloc(list);
        try
        {
            EnumDisplayMonitors(0, 0, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return list;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, RECT* rect, nint data)
    {
        var list = (List<MonitorInfo>)GCHandle.FromIntPtr(data).Target!;
        var mi = new MONITORINFOEXW { cbSize = sizeof(MONITORINFOEXW) };
        if (!GetMonitorInfo(monitor, &mi)) return 1;
        string device = new((char*)mi.szDevice);
        if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpi, out _) != 0) dpi = 96;
        var dm = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
        int hz = EnumDisplaySettings((char*)mi.szDevice, -1 /* ENUM_CURRENT_SETTINGS */, &dm) ? (int)dm.dmDisplayFrequency : 60;
        if (hz <= 1) hz = 60; // "hardware default"
        list.Add(new MonitorInfo(monitor, device, mi.rcMonitor.ToBox(), mi.rcWork.ToBox(), dpi, hz, (mi.dwFlags & 1) != 0));
        return 1;
    }

    /// <summary>
    /// Top-level windows front to back, with what coverage needs. Windows that can't cover anything (hidden,
    /// minimised, empty) skip the DWM queries.
    /// </summary>
    public static List<WindowRecord> Windows(IReadOnlySet<nint> own)
    {
        var state = new WindowScan(own);
        var handle = GCHandle.Alloc(state);
        try
        {
            EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return state.Windows;
    }

    private sealed class WindowScan(IReadOnlySet<nint> own)
    {
        public readonly IReadOnlySet<nint> Own = own;
        public readonly List<WindowRecord> Windows = new(128);
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint hwnd, nint data)
    {
        var scan = (WindowScan)GCHandle.FromIntPtr(data).Target!;
        if (!IsWindowVisible(hwnd)) return 1;
        bool minimized = IsIconic(hwnd);
        if (minimized) return 1;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        RECT r;
        // The extended frame excludes the invisible resize borders, so a maximised window matches its work area.
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)) != 0 && !GetWindowRect(hwnd, out r)) return 1;
        var bounds = r.ToBox();
        if (!bounds.IsUsable) return 1;
        int cloaked = 0;
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int));
        double alpha = 1;
        if ((ex & WS_EX_LAYERED) != 0)
        {
            // A layered window is opaque only with a constant alpha, no colour key, and drawn through its redirection
            // bitmap. One with no attributes draws per pixel (UpdateLayeredWindow), and one without a redirection
            // bitmap draws through DirectComposition, which can be transparent per pixel (Electron's transparent
            // overlays, seen live spanning a whole monitor): unknown, so see-through.
            alpha = (ex & WS_EX_NOREDIRECTIONBITMAP) == 0 && GetLayeredWindowAttributes(hwnd, out _, out byte a, out uint flags)
                && flags == LWA_ALPHA ? a / 255.0 : double.NaN;
        }
        scan.Windows.Add(new WindowRecord(hwnd, bounds, ClassOf(hwnd), true, false, cloaked != 0, alpha,
            (ex & WS_EX_TRANSPARENT) != 0, scan.Own.Contains(hwnd), IsZoomed(hwnd)));
        return 1;
    }

    /// <summary>The monitor holding the foreground window, and whether Windows reports an exclusive-mode Direct3D app.</summary>
    public static (nint Monitor, bool D3DFullScreen) Foreground()
    {
        nint fg = GetForegroundWindow();
        nint monitor = fg != 0 ? MonitorFromWindow(fg, MONITOR_DEFAULTTONULL) : 0;
        bool d3d = SHQueryUserNotificationState(out int state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;
        return (monitor, d3d);
    }

    /// <summary>Whether the screen saver is running.</summary>
    public static bool ScreenSaverRunning()
    {
        int running = 0;
        return SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, &running, 0) && running != 0;
    }

    /// <summary>On battery (or UPS) rather than mains power. False when unknown.</summary>
    public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;
}
