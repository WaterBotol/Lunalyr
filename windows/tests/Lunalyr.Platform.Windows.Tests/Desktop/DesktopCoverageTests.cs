using Lunalyr.Platform.Desktop;

namespace Lunalyr.Platform.Windows.Tests.Desktop;

/// <summary>Coverage is decided from a window list alone; these lists are synthetic.</summary>
public sealed class DesktopCoverageTests
{
    // ---- Geometry: the C vectors from the Mac's desktop_test.c, unchanged ----

    private static readonly Box Visible = new(0, 25, 1512, 945);

    // Ported from desktop_test.c: coverage (exact union area and the "no noticeable exposed square" rule)
    [Fact]
    public void ExactUnionAreaAndNoNoticeableExposedSquare()
    {
        Box[] maximised = [new(0, 25, 1512, 945)];
        Assert.Equal(0, DesktopCoverage.ExposedFraction(Visible, maximised));
        Assert.True(DesktopCoverage.RegionCovered(Visible, maximised, 24, .06));
        Assert.Equal(1, DesktopCoverage.ExposedFraction(Visible, []));
        Assert.False(DesktopCoverage.RegionCovered(Visible, [], 24, .06));
        Box[] half = [new(0, 25, 756, 945)];
        Assert.Equal(.5, DesktopCoverage.ExposedFraction(Visible, half), 1e-9);
        Assert.False(DesktopCoverage.RegionCovered(Visible, half, 24, .06));
        // Overlapping windows are not double counted.
        Box[] overlap = [new(0, 25, 1000, 945), new(500, 25, 1012, 945)];
        Assert.Equal(0, DesktopCoverage.ExposedFraction(Visible, overlap));
        Assert.True(DesktopCoverage.RegionCovered(Visible, overlap, 24, .06));
    }

    // Ported from desktop_test.c: tiled windows with 8 pt margins leave only slivers
    [Fact]
    public void TiledWindowsLeaveOnlySlivers()
    {
        Box[] tiled = [new(8, 33, 744, 929), new(760, 33, 744, 929)];
        double sliver = DesktopCoverage.ExposedFraction(Visible, tiled);
        Assert.InRange(sliver, .02, .04);
        Assert.True(DesktopCoverage.RegionCovered(Visible, tiled, 24, .06));
        Assert.False(DesktopCoverage.RegionCovered(Visible, tiled, 4, .06));
        Assert.False(DesktopCoverage.RegionCovered(Visible, tiled, 24, .01)); // area budget
    }

    // Ported from desktop_test.c: a 100 pt hole in the middle (where lyrics sit) is visible desktop
    [Fact]
    public void AHoleWhereTheLyricsSitIsVisibleDesktop()
    {
        Box[] frame = [new(0, 25, 1512, 422), new(0, 547, 1512, 423), new(0, 447, 706, 100), new(806, 447, 706, 100)];
        Assert.True(DesktopCoverage.ExposedFraction(Visible, frame) > 0);
        Assert.False(DesktopCoverage.RegionCovered(Visible, frame, 24, .06));
    }

    // Ported from desktop_test.c: windows spilling onto another display, negative origins and malformed input
    [Fact]
    public void SpillNegativeOriginsAndMalformedInput()
    {
        Assert.True(DesktopCoverage.RegionCovered(Visible, [new(-400, 0, 2400, 1200)], 24, .06));
        Assert.Equal(1, DesktopCoverage.ExposedFraction(Visible, [new(1600, 0, 800, 600)]));
        var leftDisplay = new Box(-1920, -200, 1920, 1080);
        Assert.True(DesktopCoverage.RegionCovered(leftDisplay, [new(-1925, -205, 1930, 1090)], 24, .06));
        Box[] junk = [new(double.NaN, 0, 100, 100), new(0, 0, -5, 10), new(0, 25, 1512, 945)];
        Assert.True(DesktopCoverage.RegionCovered(Visible, junk, 24, .06));
        Box[] maximised = [new(0, 25, 1512, 945)];
        Assert.False(DesktopCoverage.RegionCovered(new Box(double.NaN, 0, 10, 10), maximised, 24, .06));
        Assert.False(DesktopCoverage.RegionCovered(Visible, maximised, double.NaN, .06));
        Assert.Equal(1, DesktopCoverage.ExposedFraction(new Box(0, 0, 0, 10), maximised));
    }

    // Ported from desktop_test.c: only the first VC_MAX_COVERS covers count
    [Fact]
    public void OnlyTheFirstCoversCountSoALongListOnlyUnderReports()
    {
        var many = new Box[DesktopCoverage.MaxCovers + 8];
        for (int i = 0; i < many.Length; i++) many[i] = new Box(0, 0, 1, 1);
        many[DesktopCoverage.MaxCovers + 2] = new Box(0, 25, 1512, 945);
        Assert.False(DesktopCoverage.RegionCovered(Visible, many, 24, .06));
        many[0] = new Box(0, 25, 1512, 945);
        Assert.True(DesktopCoverage.RegionCovered(Visible, many, 24, .06));
    }

    // ---- Per-monitor decisions (DesktopCoverageTests.swift, on Windows' window model) ----

    // A 1512x982 laptop with a 33 px taskbar-free work area offset, and a 2560x1440 display to its right (the Mac
    // test's layout; on Windows the work area excludes the taskbar instead of the menu bar).
    private static readonly MonitorInfo Laptop = new(1, @"\\.\DISPLAY1", new Box(0, 0, 1512, 982), new Box(0, 33, 1512, 949), 96, 60, true);
    private static readonly MonitorInfo External = new(2, @"\\.\DISPLAY2", new Box(1512, 0, 2560, 1440), new Box(1512, 25, 2560, 1415), 96, 60, false);
    private static readonly MonitorInfo[] Both = [Laptop, External];

    private static WindowRecord Window(nint handle, Box bounds, string cls = "Chrome_WidgetWin_1", double alpha = 1,
        bool minimized = false, bool cloaked = false, bool clickThrough = false, bool own = false, bool visible = true) =>
        new(handle, bounds, cls, visible, minimized, cloaked, alpha, clickThrough, own);

    private static int[] Hidden(IReadOnlyList<MonitorInfo> monitors, params WindowRecord[] windows) =>
        [.. DesktopVisibility.Evaluate(monitors, windows).Select((v, i) => (v, i)).Where(x => x.v.CoveredByWindows).Select(x => x.i + 1)];

    // Ported from DesktopCoverageTests.swift: testMaximisedOpaqueWindowHidesOnlyItsDisplay
    [Fact]
    public void MaximisedOpaqueWindowHidesOnlyItsDisplay()
    {
        Assert.Equal([1], Hidden(Both, Window(500, Laptop.WorkArea)));
        Assert.False(DesktopVisibility.Evaluate(Both, [Window(500, Laptop.WorkArea)])[0].FullScreenApp, "a maximised window leaves the taskbar");
    }

    // Ported from DesktopCoverageTests.swift: testEmptyOrFailedWindowListHidesNothing
    [Fact]
    public void EmptyOrFailedWindowListHidesNothing() => Assert.Empty(Hidden(Both));

    // Ported from DesktopCoverageTests.swift: testVisibleDesktopIsNotHidden
    [Fact]
    public void VisibleDesktopIsNotHidden() =>
        Assert.Empty(Hidden(Both, Window(500, new Box(0, 33, 756, 949)), Window(501, new Box(1700, 200, 900, 700))));

    // Ported from DesktopCoverageTests.swift: testTranslucentFloatingAndDesktopLevelWindowsNeverCover (Windows has no
    // window levels: the desktop-level windows are the shell's, and the non-covering kinds are these)
    [Fact]
    public void TranslucentHiddenShellAndOverlayWindowsNeverCover()
    {
        var frame = Laptop.Bounds;
        Assert.Empty(Hidden([Laptop],
            Window(500, frame, alpha: 0.5), // fading
            Window(501, frame, alpha: double.NaN), // layered per pixel: unknown, so see-through
            Window(502, frame, clickThrough: true), // an overlay
            Window(503, frame, cls: "Progman"), // the desktop and its icons
            Window(504, frame, cls: "WorkerW"),
            Window(505, new Box(0, 934, 1512, 48), cls: "Shell_TrayWnd"),
            Window(506, frame, minimized: true),
            Window(507, frame, cloaked: true), // another virtual desktop, a suspended app
            Window(508, frame, visible: false)));
    }

    // Ported from DesktopCoverageTests.swift: testTiledWindowsWithMarginsCountAsCovered
    [Fact]
    public void TiledWindowsWithMarginsCountAsCovered() =>
        Assert.Equal([1], Hidden([Laptop], Window(500, new Box(8, 41, 744, 933)), Window(501, new Box(760, 41, 744, 933))));

    // Ported from DesktopCoverageTests.swift: testAnUncoveredLyricStageKeepsRendering
    [Fact]
    public void AnUncoveredLyricStageKeepsRendering() =>
        Assert.Empty(Hidden([Laptop],
            Window(500, new Box(0, 33, 1512, 300)), Window(501, new Box(0, 633, 1512, 349)),
            Window(502, new Box(0, 333, 606, 300)), Window(503, new Box(906, 333, 606, 300))));

    // Ported from DesktopCoverageTests.swift: testFullScreenAppWindowCoversWholeDisplay
    [Fact]
    public void FullScreenAppWindowCoversWholeDisplay()
    {
        var result = DesktopVisibility.Evaluate(Both, [Window(500, External.Bounds)]);
        Assert.Equal([2], Hidden(Both, Window(500, External.Bounds)));
        Assert.False(result[0].FullScreenApp);
        Assert.True(result[1].FullScreenApp);
    }

    // Ported from DesktopCoverageTests.swift: testOwnWindowsNeverCoverTheirWallpaper
    [Fact]
    public void OwnWindowsNeverCoverTheirWallpaper()
    {
        var result = DesktopVisibility.Evaluate([Laptop], [Window(11, Laptop.Bounds, own: true)]);
        Assert.Equal(new MonitorVisibility(false, false), result[0]);
    }

    // Ported from DesktopCoverageTests.swift: testManyWindowsAreBoundedAndOnlyUnderReport
    [Fact]
    public void ManyWindowsAreBoundedAndOnlyUnderReport()
    {
        var windows = new List<WindowRecord>();
        // 200 tiny windows, then one that covers everything: the large one must still be considered.
        for (int i = 0; i < 200; i++) windows.Add(Window(1000 + i, new Box(i, 40, 2, 2)));
        windows.Add(Window(5000, Laptop.WorkArea));
        Assert.Equal([1], Hidden([Laptop], [.. windows]));
    }

    // Ported from DesktopCoverageTests.swift: testSeeThroughTerminalsNeverCover (Windows Terminal stands in for the
    // Mac's list)
    [Fact]
    public void SeeThroughTerminalsNeverCover() =>
        Assert.Empty(Hidden([Laptop], Window(500, Laptop.WorkArea, cls: "CASCADIA_HOSTING_WINDOW_CLASS")));

    [Fact]
    public void AMaximisedWindowOverAnAutoHiddenTaskbarCoversButIsNoFullScreenApp()
    {
        // With the taskbar auto-hidden the work area is the whole monitor, and so is a maximised window.
        var autoHide = Laptop with { WorkArea = Laptop.Bounds };
        var result = DesktopVisibility.Evaluate([autoHide], [Window(500, autoHide.Bounds) with { Maximized = true }]);
        Assert.Equal(new MonitorVisibility(FullScreenApp: false, CoveredByWindows: true), result[0]);
    }

    [Fact]
    public void AFullMonitorOverlayDrawnPerPixelIsNoFullScreenApp()
    {
        // Seen live: an Electron app's full-monitor DirectComposition overlay (layered, no layered attributes) was the
        // foreground window. The reader marks such windows' alpha unknown (NaN), so they cover nothing.
        var result = DesktopVisibility.Evaluate([Laptop], [Window(500, Laptop.Bounds, alpha: double.NaN)]);
        Assert.Equal(new MonitorVisibility(false, false), result[0]);
    }

    [Fact]
    public void ClickingTheDesktopOrTaskbarIsNeverAFullScreenApp()
    {
        // Progman spans every monitor and is the foreground window after a click on the desktop.
        var result = DesktopVisibility.Evaluate(Both, [Window(1, new Box(0, 0, 4072, 1440), cls: "Progman")]);
        Assert.All(result, r => Assert.Equal(new MonitorVisibility(false, false), r));
    }

    [Fact]
    public void AnExclusiveDirect3DGameHidesTheForegroundMonitorOnly()
    {
        var result = DesktopVisibility.Evaluate(Both, [], foregroundMonitor: External.Handle, d3dFullScreenOnForegroundMonitor: true);
        Assert.False(result[0].FullScreenApp);
        Assert.True(result[1].FullScreenApp);
        Assert.True(result[1].CoveredByWindows);
    }

    [Fact]
    public void TheNoticeableGapScalesWithDpi()
    {
        // Two windows leaving a 30 px column: noticeable at 100 % (gap 24 px), a sliver at 150 % (gap 36 px).
        var at100 = Laptop with { WorkArea = new Box(0, 0, 1512, 982), Bounds = new Box(0, 0, 1512, 1030) };
        var at150 = at100 with { Dpi = 144 };
        WindowRecord[] windows = [Window(500, new Box(0, 0, 741, 982)), Window(501, new Box(771, 0, 741, 982))];
        Assert.Empty(Hidden([at100], windows));
        Assert.Equal([1], Hidden([at150], windows));
    }
}
