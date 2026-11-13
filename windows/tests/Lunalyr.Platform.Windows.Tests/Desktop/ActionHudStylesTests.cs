using Lunalyr.Platform.Desktop;

namespace Lunalyr.Platform.Windows.Tests.Desktop;

public class ActionHudStylesTests
{
    private const uint WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

    // Ported from ActionHUDTests.swift: testThePanelNeverTakesFocusOrClicks. Windows has no panels, Spaces or window
    // cycle: never becoming key is no-activate (and MA_NOACTIVATE), ignoring the mouse is layered + transparent (and
    // HTTRANSPARENT), every Space and over full-screen apps is topmost, out of the cycle is a tool window, and out of
    // screen sharing is WDA_EXCLUDEFROMCAPTURE.
    [Fact]
    public void TestThePanelNeverTakesFocusOrClicks()
    {
        var ex = ActionHudStyles.ExStyle;
        Assert.Equal(WS_EX_NOACTIVATE, ex & WS_EX_NOACTIVATE); // never the foreground or the keyboard
        Assert.Equal(WS_EX_TRANSPARENT | WS_EX_LAYERED, ex & (WS_EX_TRANSPARENT | WS_EX_LAYERED)); // clicks pass through
        Assert.Equal(WS_EX_TOPMOST, ex & WS_EX_TOPMOST); // shows while another app is in front, full-screen ones too
        Assert.Equal(WS_EX_TOOLWINDOW, ex & WS_EX_TOOLWINDOW); // not in Alt+Tab or the taskbar
        Assert.Equal(0u, ex & WS_EX_APPWINDOW);
        Assert.Equal(WS_POPUP, ActionHudStyles.Style);
        Assert.Equal(0u, ActionHudStyles.Style & (WS_CAPTION | WS_SYSMENU));
        Assert.Equal(0u, ActionHudStyles.Style & WS_VISIBLE); // made hidden; shown only while a note is up
        Assert.Equal(0x11u, ActionHudStyles.DisplayAffinity); // left out of screenshots and screen sharing
        Assert.Equal(3, ActionHudStyles.MouseActivate); // MA_NOACTIVATE
        Assert.Equal(-1, ActionHudStyles.HitTest); // HTTRANSPARENT
    }
}
