using Lunalyr.Shell.Flyout;

namespace Lunalyr.Shell.Tests;

/// <summary>Where the tray flyout goes, for every taskbar edge, auto-hide, an icon in the overflow and several scales.</summary>
public sealed class FlyoutPlacementTests
{
    // A 1920×1080 monitor at the origin with a 48-pixel taskbar, and a second one to its right at 200% (3840×2160).
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    private static void AssertInside(PixelRect rect, PixelRect area, int margin)
    {
        Assert.True(rect.Left >= area.Left + margin && rect.Right <= area.Right - margin, $"{rect} not inside {area} horizontally");
        Assert.True(rect.Top >= area.Top + margin && rect.Bottom <= area.Bottom - margin, $"{rect} not inside {area} vertically");
    }

    [Fact]
    public void BottomTaskbarPutsItAboveTheIconCorner()
    {
        var work = new PixelRect(0, 0, 1920, 1032);
        var icon = new PixelRect(1860, 1040, 1892, 1072);
        Assert.Equal(TaskbarEdge.Bottom, FlyoutPlacement.EdgeOf(Monitor, work, icon));
        var rect = FlyoutPlacement.Place(364, 600, icon, Monitor, work, 12);
        Assert.Equal(1032 - 12, rect.Bottom); // sits on the taskbar with the gap
        Assert.Equal(1920 - 12, rect.Right); // the icon is near the corner: held inside the work area
        Assert.Equal((364, 600), (rect.Width, rect.Height));
        AssertInside(rect, work, 12);
    }

    [Fact]
    public void AnIconMidTaskbarCentresTheFlyoutOnIt()
    {
        var work = new PixelRect(0, 0, 1920, 1032);
        var icon = new PixelRect(944, 1040, 976, 1072);
        var rect = FlyoutPlacement.Place(364, 600, icon, Monitor, work, 12);
        Assert.Equal(960, (rect.Left + rect.Right) / 2);
    }

    [Fact]
    public void TopTaskbarPutsItBelow()
    {
        var work = new PixelRect(0, 48, 1920, 1080);
        var icon = new PixelRect(1700, 8, 1732, 40);
        Assert.Equal(TaskbarEdge.Top, FlyoutPlacement.EdgeOf(Monitor, work, icon));
        var rect = FlyoutPlacement.Place(364, 600, icon, Monitor, work, 12);
        Assert.Equal(48 + 12, rect.Top);
        AssertInside(rect, work, 12);
    }

    [Fact]
    public void LeftTaskbarPutsItToTheRightNextToTheIcon()
    {
        var work = new PixelRect(62, 0, 1920, 1080);
        var icon = new PixelRect(15, 900, 47, 932);
        Assert.Equal(TaskbarEdge.Left, FlyoutPlacement.EdgeOf(Monitor, work, icon));
        var rect = FlyoutPlacement.Place(364, 600, icon, Monitor, work, 12);
        Assert.Equal(62 + 12, rect.Left);
        Assert.Equal(1080 - 12, rect.Bottom); // centred on the icon, held inside the work area
        AssertInside(rect, work, 12);
    }

    [Fact]
    public void RightTaskbarPutsItToTheLeft()
    {
        var work = new PixelRect(0, 0, 1858, 1080);
        var icon = new PixelRect(1873, 300, 1905, 332);
        Assert.Equal(TaskbarEdge.Right, FlyoutPlacement.EdgeOf(Monitor, work, icon));
        var rect = FlyoutPlacement.Place(364, 400, icon, Monitor, work, 12);
        Assert.Equal(1858 - 12, rect.Right);
        Assert.Equal(316, (rect.Top + rect.Bottom) / 2); // centred on the icon
        AssertInside(rect, work, 12);
    }

    [Fact]
    public void AnAutoHiddenTaskbarIsFoundFromTheIconAndKeptClearOf()
    {
        // The work area is the whole monitor; the icon shows where the taskbar slid in.
        var icon = new PixelRect(1700, 1040, 1732, 1072);
        Assert.Equal(TaskbarEdge.Bottom, FlyoutPlacement.EdgeOf(Monitor, Monitor, icon));
        var rect = FlyoutPlacement.Place(364, 600, icon, Monitor, Monitor, 12);
        Assert.Equal(1040 - 12, rect.Bottom);

        var top = new PixelRect(1700, 4, 1732, 36);
        Assert.Equal(TaskbarEdge.Top, FlyoutPlacement.EdgeOf(Monitor, Monitor, top));
        Assert.Equal(36 + 12, FlyoutPlacement.Place(364, 600, top, Monitor, Monitor, 12).Top);

        var left = new PixelRect(4, 500, 36, 532);
        Assert.Equal(TaskbarEdge.Left, FlyoutPlacement.EdgeOf(Monitor, Monitor, left));
        Assert.Equal(36 + 12, FlyoutPlacement.Place(364, 600, left, Monitor, Monitor, 12).Left);

        // No icon and no taskbar to see: the bottom, Windows' default.
        Assert.Equal(TaskbarEdge.Bottom, FlyoutPlacement.EdgeOf(Monitor, Monitor, null));
    }

    [Fact]
    public void AnIconInTheOverflowStillAnchorsToTheTaskbar()
    {
        // The overflow pops up above the taskbar; the flyout sits on the taskbar under the icon's column instead of
        // floating where the overflow was.
        var work = new PixelRect(0, 0, 1920, 1032);
        var icon = new PixelRect(1600, 960, 1632, 992);
        var rect = FlyoutPlacement.Place(364, 600, icon, Monitor, work, 12);
        Assert.Equal(1032 - 12, rect.Bottom);
        Assert.Equal(1616, (rect.Left + rect.Right) / 2);
    }

    [Fact]
    public void AClickPointStandsInForAnUnknownIcon()
    {
        var work = new PixelRect(0, 0, 1920, 1032);
        var rect = FlyoutPlacement.Place(364, 600, new PixelRect(1500, 1050, 1500, 1050), Monitor, work, 12);
        Assert.Equal(1032 - 12, rect.Bottom);
        Assert.Equal(1500, (rect.Left + rect.Right) / 2);
    }

    [Theory]
    [InlineData(96u, 364, 12)]
    [InlineData(120u, 455, 15)]
    [InlineData(144u, 546, 18)]
    [InlineData(192u, 728, 24)]
    public void SizesFollowTheMonitorsScale(uint dpi, int width, int margin)
    {
        Assert.Equal(width, FlyoutPlacement.ToPixels(364, dpi));
        Assert.Equal(margin, FlyoutPlacement.ToPixels(FlyoutPlacement.MarginDip, dpi));
        Assert.Equal(364, FlyoutPlacement.ToPixels(364, 0)); // an unknown scale reads as 100%
    }

    [Fact]
    public void ASecondMonitorAt200PercentUsesItsOwnCoordinates()
    {
        var monitor = new PixelRect(1920, -200, 1920 + 3840, -200 + 2160);
        var work = new PixelRect(1920, -200, 1920 + 3840, -200 + 2064);
        var icon = new PixelRect(5600, 1880, 5664, 1944);
        var width = FlyoutPlacement.ToPixels(364, 192);
        var rect = FlyoutPlacement.Place(width, FlyoutPlacement.ToPixels(684, 192), icon, monitor, work, FlyoutPlacement.ToPixels(12, 192));
        Assert.Equal(work.Bottom - 24, rect.Bottom);
        Assert.Equal(work.Right - 24, rect.Right);
        AssertInside(rect, work, 24);
    }

    [Fact]
    public void ATallFlyoutIsCutToTheWorkAreaAndScrolls()
    {
        var work = new PixelRect(0, 0, 1366, 720);
        var monitor = new PixelRect(0, 0, 1366, 768);
        var rect = FlyoutPlacement.Place(455, 900, new PixelRect(1300, 730, 1330, 760), monitor, work, 15);
        Assert.Equal(720 - 30, rect.Height);
        AssertInside(rect, work, 15);
    }
}
