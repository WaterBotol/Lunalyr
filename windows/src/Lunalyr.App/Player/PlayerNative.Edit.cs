using System.Runtime.InteropServices;

namespace Lunalyr.App.Player;

// The Win32 calls behind the search field (a child EDIT control over the card) and its colours.
internal static unsafe partial class PlayerNative
{
    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint ES_AUTOHSCROLL = 0x0080, ES_NOHIDESEL = 0x0100;
    public const uint WM_COMMAND = 0x0111, WM_CTLCOLOREDIT = 0x0133, WM_CTLCOLORSTATIC = 0x0138, WM_SETFONT = 0x0030,
        WM_MOUSEWHEEL = 0x020A, WM_IME_STARTCOMPOSITION = 0x010D, WM_IME_ENDCOMPOSITION = 0x010E, WM_NCDESTROY = 0x0082,
        WM_CONTEXTMENU = 0x007B, WM_SETTEXT = 0x000C;
    public const uint EM_GETSEL = 0x00B0, EM_SETSEL = 0x00B1, EM_REPLACESEL = 0x00C2, EM_SETMARGINS = 0x00D3, EM_LIMITTEXT = 0x00C5;
    public const uint EN_SETFOCUS = 0x0100, EN_KILLFOCUS = 0x0200, EN_CHANGE = 0x0300;
    public const int GWLP_WNDPROC = -4;
    public const uint LWA_ALPHA = 0x2;
    public const uint DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000, DT_LEFT = 0;
    public const int TRANSPARENT = 1;
    public const int SW_SHOW = 5;
    public const int DWMWCP_DONOTROUND = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [LibraryImport("user32", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32", EntryPoint = "CallWindowProcW")]
    public static partial nint CallWindowProc(nint previous, nint hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessage(nint hwnd, uint msg, nuint wParam, string lParam);

    [LibraryImport("user32", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(nint hwnd);

    [LibraryImport("user32", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(nint hwnd, char* buffer, int max);

    [LibraryImport("user32")]
    public static partial nint GetFocus();

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32")]
    public static partial nint BeginPaint(nint hwnd, out PAINTSTRUCT paint);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hwnd, in PAINTSTRUCT paint);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32")]
    public static partial int FillRect(nint hdc, in RECT rect, nint brush);

    [LibraryImport("user32", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(nint hdc, string text, int count, ref RECT rect, uint format);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("gdi32", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [LibraryImport("gdi32")]
    public static partial nint CreateSolidBrush(uint colorRef);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    [LibraryImport("gdi32")]
    public static partial nint SelectObject(nint hdc, nint handle);

    [LibraryImport("gdi32")]
    public static partial uint SetTextColor(nint hdc, uint colorRef);

    [LibraryImport("gdi32")]
    public static partial uint SetBkColor(nint hdc, uint colorRef);

    [LibraryImport("gdi32")]
    public static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("ole32")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}
