using System.Runtime.InteropServices;

namespace Lunalyr.App.Player;

/// <summary>The Win32, DWM and DirectComposition calls the desktop player needs. Source-generated marshalling only (AOT safe).</summary>
internal static unsafe partial class PlayerNative
{
    public const uint WS_POPUP = 0x80000000, WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint CS_DROPSHADOW = 0x00020000;

    public const uint WM_DESTROY = 0x0002, WM_MOVE = 0x0003, WM_ACTIVATE = 0x0006, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008,
        WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_SETTINGCHANGE = 0x001A, WM_SETCURSOR = 0x0020, WM_MOUSEACTIVATE = 0x0021,
        WM_GETOBJECT = 0x003D, WM_NCHITTEST = 0x0084, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102,
        WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_SYSCHAR = 0x0106, WM_SYSCOMMAND = 0x0112, WM_TIMER = 0x0113,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MOUSELEAVE = 0x02A3, WM_CAPTURECHANGED = 0x0215,
        WM_DPICHANGED = 0x02E0, WM_DISPLAYCHANGE = 0x007E, WM_THEMECHANGED = 0x031A, WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    public const uint MA_ACTIVATE = 1, MA_NOACTIVATE = 3;
    public const int HTCLIENT = 1;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40,
        SWP_HIDEWINDOW = 0x80, SWP_NOOWNERZORDER = 0x200;
    public const uint TME_LEAVE = 0x2;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const uint MONITOR_DEFAULTTONEAREST = 2, MONITOR_DEFAULTTONULL = 0;
    public const uint GA_ROOT = 2;
    public const uint GW_HWNDPREV = 3;
    public const int IDC_ARROW = 32512, IDC_HAND = 32649;
    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_ROUND = 2;
    public const uint SPI_GETHIGHCONTRAST = 0x0042, HCF_HIGHCONTRASTON = 0x1;
    public const uint MAPVK_VK_TO_CHAR = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIGHCONTRASTW { public uint cbSize, dwFlags; public nint lpszDefaultScheme; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [LibraryImport("user32", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(in WNDCLASSEXW wc);

    [LibraryImport("user32", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClass(string className, nint instance);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string? title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(nint hwnd, string text);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32")]
    public static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hwnd, nuint id);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32")]
    public static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32", EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(nint hwnd, char* buffer, int max);

    [LibraryImport("user32")]
    public static partial nint MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, RECT*, nint, int> proc, nint data);

    [LibraryImport("shcore")]
    public static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("user32")]
    public static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32")]
    public static partial short GetKeyState(int key);

    [LibraryImport("user32", EntryPoint = "MapVirtualKeyW")]
    public static partial uint MapVirtualKey(uint code, uint mapType);

    [LibraryImport("user32")]
    public static partial nint SetCapture(nint hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32")]
    public static partial nint GetCapture();

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT track);

    [LibraryImport("user32", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint name);

    [LibraryImport("user32")]
    public static partial nint SetCursor(nint cursor);

    [LibraryImport("user32")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32")]
    public static partial nint SetFocus(nint hwnd);

    [LibraryImport("user32")]
    public static partial nint GetWindow(nint hwnd, uint cmd);

    [LibraryImport("user32", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? className, string? title);

    [LibraryImport("user32")]
    public static partial uint GetDoubleClickTime();

    [LibraryImport("user32", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint param, void* value, uint winIni);

    [LibraryImport("user32")]
    public static partial uint GetSysColor(int index);

    [LibraryImport("dwmapi")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);

    [LibraryImport("shell32")]
    public static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("dcomp")]
    public static partial int DCompositionCreateDevice(nint dxgiDevice, in Guid iid, out nint device);

    [LibraryImport("kernel32")]
    public static partial uint GetCurrentProcessId();

    public static string ClassOf(nint hwnd)
    {
        var buffer = stackalloc char[64];
        var length = GetClassName(hwnd, buffer, 64);
        return length <= 0 ? "" : new string(buffer, 0, length);
    }

    public static bool ClassIs(nint hwnd, ReadOnlySpan<char> name)
    {
        var buffer = stackalloc char[64];
        var length = GetClassName(hwnd, buffer, 64);
        return length > 0 && new ReadOnlySpan<char>(buffer, length).SequenceEqual(name);
    }

    public static bool KeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    public static bool AsyncKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>A mouse button is down now (either main button; swapped buttons don't matter for "held").</summary>
    public static bool ButtonsHeld() => AsyncKeyDown(VK_LBUTTON) || AsyncKeyDown(VK_RBUTTON);

    public static short LoWord(nint value) => (short)((long)value & 0xFFFF);

    public static short HiWord(nint value) => (short)(((long)value >> 16) & 0xFFFF);
}

/// <summary>
/// The three DirectComposition calls the card needs (a target for its window, one visual, the swap chain as its
/// content), made through the COM vtables: no package for so little.
/// </summary>
internal sealed unsafe class Composition : IDisposable
{
    private static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
    private nint _device, _target, _visual;

    public Composition(nint dxgiDevice, nint hwnd, nint swapChain)
    {
        Check(PlayerNative.DCompositionCreateDevice(dxgiDevice, IID_IDCompositionDevice, out _device), "DCompositionCreateDevice");
        nint target, visual;
        // IDCompositionDevice: 3 Commit, 6 CreateTargetForHwnd, 7 CreateVisual.
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)Slot(_device, 6))(_device, hwnd, 1, &target), "CreateTargetForHwnd");
        _target = target;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_device, 7))(_device, &visual), "CreateVisual");
        _visual = visual;
        SetContent(swapChain);
        // IDCompositionTarget: 3 SetRoot.
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_target, 3))(_target, _visual), "SetRoot");
        Commit();
    }

    /// <summary>IDCompositionVisual: 15 SetContent (after the overloaded offset, transform and clip setters).</summary>
    public void SetContent(nint swapChain) =>
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_visual, 15))(_visual, swapChain), "SetContent");

    public void Commit() => Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_device, 3))(_device), "Commit");

    public void Dispose()
    {
        Release(ref _visual);
        Release(ref _target);
        Release(ref _device);
    }

    private static void* Slot(nint com, int index) => (*(void***)com)[index];

    private static void Release(ref nint com)
    {
        if (com == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, 2))(com);
        com = 0;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8}).");
    }
}
