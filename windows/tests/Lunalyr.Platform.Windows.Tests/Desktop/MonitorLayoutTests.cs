using Lunalyr.Platform.Desktop;

namespace Lunalyr.Platform.Windows.Tests.Desktop;

/// <summary>Multi-monitor changes, checked as data (this PC's displays are in Duplicate mode).</summary>
public sealed class MonitorLayoutTests
{
    private static readonly MonitorInfo Panel = new(0x10, @"\\.\DISPLAY1", new Box(0, 0, 2560, 1600), new Box(0, 0, 2560, 1552), 120, 60, true);
    private static readonly MonitorInfo Lenovo = new(0x20, @"\\.\DISPLAY2", new Box(2560, 0, 2560, 1440), new Box(2560, 0, 2560, 1392), 96, 144, false);

    [Fact]
    public void TheSameLayoutNeedsNothing() =>
        Assert.Equal(LayoutChange.None, MonitorLayout.Compare([Panel, Lenovo], [Lenovo with { Handle = 0x99 }, Panel]));

    [Fact]
    public void AddingOrRemovingAMonitorRebuilds()
    {
        Assert.Equal(LayoutChange.Monitors, MonitorLayout.Compare([Panel], [Panel, Lenovo]));
        Assert.Equal(LayoutChange.Monitors, MonitorLayout.Compare([Panel, Lenovo], [Panel]));
        Assert.Equal(LayoutChange.Monitors, MonitorLayout.Compare([Panel, Lenovo], [Panel, Lenovo with { DeviceName = @"\\.\DISPLAY3" }]));
    }

    [Fact]
    public void MovingResizingOrChangingDpiOrRefreshRebuildsTheSurfaces()
    {
        Assert.True(MonitorLayout.Compare([Panel, Lenovo], [Panel, Lenovo with { Bounds = new Box(-2560, 0, 2560, 1440) }]).HasFlag(LayoutChange.Surfaces));
        Assert.True(MonitorLayout.Compare([Panel, Lenovo], [Panel with { Dpi = 144 }, Lenovo]).HasFlag(LayoutChange.Surfaces));
        Assert.True(MonitorLayout.Compare([Panel, Lenovo], [Panel, Lenovo with { RefreshHz = 60 }]).HasFlag(LayoutChange.Surfaces));
        Assert.True(MonitorLayout.Compare([Panel, Lenovo], [Panel with { IsPrimary = false }, Lenovo with { IsPrimary = true }]).HasFlag(LayoutChange.Surfaces));
    }

    [Fact]
    public void ATaskbarChangeOnlyRechecksCoverage() =>
        Assert.Equal(LayoutChange.WorkArea, MonitorLayout.Compare([Panel, Lenovo], [Panel with { WorkArea = Panel.Bounds }, Lenovo]));
}
