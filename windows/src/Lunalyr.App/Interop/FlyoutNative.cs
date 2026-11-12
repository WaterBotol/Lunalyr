using System.Runtime.InteropServices;
using Lunalyr.Shell.Flyout;

namespace Lunalyr.App.Interop;

/// <summary>The Win32 and DWM calls the tray flyout needs: monitors, their scale, and the window's corners and theme.</summary>
internal static unsafe partial class FlyoutNative
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public Win32.RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(in Win32.RECT rect, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);

    /// <summary>The monitor nearest <paramref name="anchor"/>: its rectangle, work area and effective DPI.</summary>
    public static (PixelRect Monitor, PixelRect Work, uint Dpi) MonitorAt(PixelRect anchor)
    {
        var rect = new Win32.RECT { Left = anchor.Left, Top = anchor.Top, Right = Math.Max(anchor.Right, anchor.Left + 1), Bottom = Math.Max(anchor.Bottom, anchor.Top + 1) };
        var monitor = MonitorFromRect(rect, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            var fallback = new PixelRect(0, 0, 1920, 1080);
            return (fallback, fallback, 96);
        }

        var dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var x, out _) == 0 && x > 0 ? x : 96u;
        return (ToPixelRect(info.rcMonitor), ToPixelRect(info.rcWork), dpi);
    }

    /// <summary>Asks for Windows 11's rounded corners and a border that matches the flyout's theme.</summary>
    public static void StyleWindow(nint hwnd, bool dark)
    {
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0, sizeof(int));
    }

    /// <summary>The pointer, as an empty rectangle (a fallback anchor when the icon's position isn't known).</summary>
    public static PixelRect Cursor() => Win32.GetCursorPos(out var p) ? new PixelRect(p.X, p.Y, p.X, p.Y) : new PixelRect(0, 0, 0, 0);

    public static PixelRect ToPixelRect(Win32.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
