using System.Runtime.InteropServices;

namespace Lunalyr.App.Interop;

/// <summary>Lunalyr's icon (the exe's icon resource, <c>Assets/Lunalyr.ico</c>) on a WinUI window's title bar and taskbar button.</summary>
internal static partial class AppIcon
{
    private static nint _icon;

    public static void Apply(Microsoft.UI.Windowing.AppWindow window)
    {
        if (_icon == 0) _icon = LoadImageW(Win32.GetModuleHandle(null), 32512 /* the SDK's ApplicationIcon id */, 1 /* IMAGE_ICON */, 0, 0, 0x40 /* LR_DEFAULTSIZE */);
        if (_icon == 0) return; // A build without the icon resource keeps Windows' default.
        window.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(_icon));
    }

    [LibraryImport("user32.dll")]
    private static partial nint LoadImageW(nint instance, nint name, uint type, int cx, int cy, uint flags);
}
