using Lunalyr.Platform.Desktop;

namespace Lunalyr.Platform.Windows.Tests.Desktop;

public class FloatingWindowStylesTests
{
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000,
        WS_EX_LAYERED = 0x80000, WS_EX_NOREDIRECTIONBITMAP = 0x200000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;

    // Ported from FloatingLyricsTests.swift: testWindowStaysOutOfTheWayOfAppsAndTheSystem. Windows has no window levels
    // or Spaces: above the apps is topmost, out of ⌘-Tab and the Dock is a tool window that is never an app window, and
    // never taking the keyboard is no-activate, in edit mode too.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowStaysOutOfTheWayOfAppsAndTheSystem(bool editing)
    {
        var ex = FloatingWindowStyles.ExStyle(editing);
        Assert.Equal(WS_EX_TOPMOST, ex & WS_EX_TOPMOST); // above app windows, full-screen ones too
        Assert.Equal(WS_EX_NOACTIVATE, ex & WS_EX_NOACTIVATE); // never the foreground or the keyboard
        Assert.Equal(WS_EX_TOOLWINDOW, ex & WS_EX_TOOLWINDOW); // not in Alt+Tab or the taskbar
        Assert.Equal(0u, ex & WS_EX_APPWINDOW);
        Assert.Equal(WS_EX_NOREDIRECTIONBITMAP, ex & WS_EX_NOREDIRECTIONBITMAP); // per-pixel alpha through DirectComposition
        Assert.Equal(WS_EX_LAYERED, ex & WS_EX_LAYERED);
        Assert.Equal(FloatingWindowStyles.Always, ex & FloatingWindowStyles.Always);
        Assert.Equal(WS_POPUP, FloatingWindowStyles.Style);
        Assert.Equal(0u, FloatingWindowStyles.Style & (WS_CAPTION | WS_SYSMENU)); // no title bar, no window menu
    }

    [Fact]
    public void ClicksPassThroughExceptInEditMode()
    {
        Assert.Equal(WS_EX_TRANSPARENT, FloatingWindowStyles.ExStyle(takesPointer: false) & WS_EX_TRANSPARENT);
        Assert.Equal(0u, FloatingWindowStyles.ExStyle(takesPointer: true) & WS_EX_TRANSPARENT);
    }
}
