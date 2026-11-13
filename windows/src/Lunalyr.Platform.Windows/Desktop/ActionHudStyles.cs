namespace Lunalyr.Platform.Desktop;

/// <summary>
/// The action HUD window's styles (milestone 7; the Mac's <c>ActionHUD.configure</c>), apart so they are tested without
/// a window. Everything that keeps it out of the way: never activated (no foreground, no keyboard), click-through
/// (layered and transparent), above every app (topmost), out of Alt+Tab and the taskbar (a tool window, never an app
/// window), not visible until a note shows, and left out of screen captures and sharing where Windows honours it
/// (the Mac's <c>sharingType = .none</c>).
/// </summary>
public static class ActionHudStyles
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000,
        WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>A borderless pop-up, created hidden (no <c>WS_VISIBLE</c>).</summary>
    public const uint Style = WS_POPUP;

    public const uint ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;

    /// <summary><c>WDA_EXCLUDEFROMCAPTURE</c> (Windows 10 2004 and later): shown on the screen, left out of captures.</summary>
    public const uint DisplayAffinity = 0x11;

    /// <summary><c>WM_MOUSEACTIVATE</c>'s answer: <c>MA_NOACTIVATE</c>.</summary>
    public const nint MouseActivate = 3;

    /// <summary><c>WM_NCHITTEST</c>'s answer: <c>HTTRANSPARENT</c>, so a click goes to the window beneath.</summary>
    public const nint HitTest = -1;
}
