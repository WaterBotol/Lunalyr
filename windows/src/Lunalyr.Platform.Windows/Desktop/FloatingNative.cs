using System.Runtime.InteropServices;

namespace Lunalyr.Platform.Desktop;

/// <summary>The extra Win32 calls the floating lyrics window makes. Blittable signatures only (source-generated), so Native-AOT safe.</summary>
internal static unsafe partial class FloatingNative
{
    public const uint WS_EX_TOPMOST = 0x8, WS_EX_APPWINDOW = 0x40000;
    public const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_CAPTURECHANGED = 0x0215,
        WM_SETCURSOR = 0x0020, WM_NCHITTEST = 0x0084, WM_ACTIVATE = 0x0006, WM_NCACTIVATE = 0x0086;
    public const int HTCLIENT = 1;
    public const nint MA_NOACTIVATE = 3;
    public const int SW_HIDE = 0;
    public const uint SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;
    public static readonly nint HWND_TOPMOST = -1;
    public const nint IDC_ARROW = 32512, IDC_SIZEALL = 32646, IDC_SIZENWSE = 32642, IDC_SIZENESW = 32643, IDC_SIZEWE = 32644,
        IDC_SIZENS = 32645, IDC_HAND = 32649;

    // SHAppBarMessage
    public const uint ABM_GETTASKBARPOS = 5, ABM_GETAUTOHIDEBAREX = 0xB;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage, uEdge;
        public DesktopNative.RECT rc;
        public nint lParam;
    }

    [LibraryImport("shell32.dll")]
    public static partial nuint SHAppBarMessage(uint message, APPBARDATA* data);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    public static partial nint SetCapture(nint hwnd);

    /// <summary>The left mouse button (for <see cref="GetAsyncKeyState"/>: never switch mid-press).</summary>
    public const int VK_LBUTTON = 0x01;

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    public static partial nint GetCapture();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out DesktopNative.POINT point);

    [LibraryImport("user32.dll")]
    public static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, ref DesktopNative.POINT point);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromRect(DesktopNative.RECT* rect, uint flags);

    // ---- Display names and lasting keys (DisplayConfig) ----

    public const uint QDC_ONLY_ACTIVE_PATHS = 2;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling, refreshNumerator, refreshDenominator, scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO { public uint infoType, id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public fixed char viewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        public fixed char monitorFriendlyDeviceName[64];
        public fixed char monitorDevicePath[128];
    }

    [LibraryImport("user32.dll")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [LibraryImport("user32.dll")]
    public static partial int QueryDisplayConfig(uint flags, ref uint paths, DISPLAYCONFIG_PATH_INFO* pathInfo, ref uint modes,
        DISPLAYCONFIG_MODE_INFO* modeInfo, nint topology);

    [LibraryImport("user32.dll")]
    public static partial int DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_HEADER* request);
}
