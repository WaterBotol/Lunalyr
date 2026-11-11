using System.Runtime.InteropServices;

namespace LunalyrSpike;

internal static unsafe partial class Native
{
    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000,
        WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_CLIPSIBLINGS = 0x04000000, WS_DISABLED = 0x08000000;
    public const uint WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_TRANSPARENT = 0x20, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_TIMER = 0x0113, WM_DISPLAYCHANGE = 0x007E,
        WM_DPICHANGED = 0x02E0, WM_SETTINGCHANGE = 0x001A, WM_NCHITTEST = 0x0084, WM_ERASEBKGND = 0x0014,
        WM_USER = 0x0400, WM_QUIT = 0x0012, WM_SIZE = 0x0005, WM_MOUSEACTIVATE = 0x0021;
    public const int GWL_EXSTYLE = -20, GWL_STYLE = -16;
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1;
    public const uint LWA_ALPHA = 0x2;
    public const uint SMTO_NORMAL = 0;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize; public uint style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public nint hIconSm;
    }

    public delegate nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam);
    public delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rc, nint data);
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint exStyle, string cls, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] public static extern nint DefWindowProc(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32")] public static extern int GetMessage(out MSG m, nint hwnd, uint min, uint max);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] public static extern nint DispatchMessage(ref MSG m);
    [DllImport("user32")] public static extern void PostQuitMessage(int code);
    [DllImport("user32")] public static extern bool PostMessage(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string? cls, string? title);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);
    [DllImport("user32")] public static extern bool EnumWindows(EnumWindowsProc p, nint l);
    [DllImport("user32")] public static extern nint SetParent(nint child, nint parent);
    [DllImport("user32")] public static extern nint GetParent(nint hwnd);
    [DllImport("user32")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32")] public static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32")] public static extern bool GetClientRect(nint hwnd, out RECT r);
    [DllImport("user32")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int idx);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hwnd, int idx, nint v);
    [DllImport("user32")] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32")] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32")] public static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint fn);
    [DllImport("user32")] public static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint hwnd, char[] buf, int max);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    public static extern nint SendMessageTimeout(nint hwnd, uint msg, nuint w, nint l, uint flags, uint timeout, out nuint result);
    [DllImport("user32")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc p, nint data);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(nint hMon, ref MONITORINFOEX mi);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32")] public static extern nint SetProcessDpiAwarenessContext(nint ctx);
    [DllImport("user32")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("shcore")] public static extern int GetDpiForMonitor(nint hMon, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32")] public static extern nint LoadCursor(nint inst, nint id);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("user32")] public static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[] handles, uint ms, uint wake, uint flags);
    [DllImport("kernel32")] public static extern uint WaitForMultipleObjects(uint count, nint[] handles, bool all, uint ms);
    [DllImport("kernel32")] public static extern uint WaitForSingleObjectEx(nint h, uint ms, bool alertable);
    [DllImport("winmm")] public static extern uint timeBeginPeriod(uint p);

    public static string ClassOf(nint hwnd) { var b = new char[256]; int n = GetClassName(hwnd, b, 256); return new string(b, 0, n); }
}
