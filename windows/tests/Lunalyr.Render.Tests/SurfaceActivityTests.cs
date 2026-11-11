using Lunalyr.Platform.Desktop;
using Lunalyr.Render.Pacing;

namespace Lunalyr.Render.Tests;

public sealed class SurfaceActivityTests
{
    private static SurfaceState State(bool visible, bool asleep = false, bool window = false, bool pause = false, bool paused = false) =>
        SurfaceActivity.State(visible, asleep, window, pause, paused);

    // Ported from AudioCaptureGateTests.swift: testSurfacesDrawByTheRuleTheirFrameClocksFollow (the surface rule;
    // the capture gate half belongs to System audio).
    [Fact]
    public void SurfacesDrawByTheRuleTheirFrameClocksFollow()
    {
        // A covered wallpaper keeps drawing unless covered wallpapers pause.
        Assert.Equal(SurfaceState.Drawing, State(visible: false, pause: false));
        Assert.Equal(SurfaceState.Hidden, State(visible: false, pause: true));
        Assert.Equal(SurfaceState.Drawing, State(visible: true, pause: true));
        // The preview stops whenever it can't be seen.
        Assert.Equal(SurfaceState.Hidden, State(visible: false, window: true, pause: false));
        Assert.Equal(SurfaceState.Drawing, State(visible: true, window: true, pause: true));
        // Nothing draws while the screens sleep, lock or show the screen saver.
        Assert.Equal(SurfaceState.Hidden, State(visible: true, asleep: true, pause: false));
        Assert.Equal(SurfaceState.Hidden, State(visible: true, asleep: true, window: true, pause: false));
        // Pause wallpaper freezes every wallpaper, seen or not, but leaves the preview to its own window.
        Assert.Equal(SurfaceState.Frozen, State(visible: true, pause: false, paused: true));
        Assert.Equal(SurfaceState.Frozen, State(visible: false, asleep: true, pause: true, paused: true));
        Assert.Equal(SurfaceState.Drawing, State(visible: true, window: true, pause: true, paused: true));
        Assert.Equal(SurfaceState.Hidden, State(visible: false, window: true, pause: false, paused: true));
    }

    private static readonly SystemVisibility Awake = default;

    [Fact]
    public void EachSystemConditionHidesEveryWallpaperWithItsOwnReason()
    {
        var visible = new MonitorVisibility(false, false);
        Assert.Equal((SurfaceState.Drawing, PauseReason.None), SurfaceActivity.ForWallpaper(Awake, visible, true, false));
        Assert.Equal((SurfaceState.Hidden, PauseReason.DisplayOff),
            SurfaceActivity.ForWallpaper(new SystemVisibility(DisplayOff: true, false, false, false), visible, false, false));
        Assert.Equal((SurfaceState.Hidden, PauseReason.Locked),
            SurfaceActivity.ForWallpaper(new SystemVisibility(false, Locked: true, false, false), visible, false, false));
        Assert.Equal((SurfaceState.Hidden, PauseReason.ScreenSaver),
            SurfaceActivity.ForWallpaper(new SystemVisibility(false, false, ScreenSaver: true, false), visible, false, false));
        Assert.Equal((SurfaceState.Hidden, PauseReason.SessionInactive),
            SurfaceActivity.ForWallpaper(new SystemVisibility(false, false, false, SessionInactive: true), visible, false, false));
        // Pause wallpaper wins over everything: the wallpaper keeps its frozen frame.
        Assert.Equal((SurfaceState.Frozen, PauseReason.PausedByUser),
            SurfaceActivity.ForWallpaper(new SystemVisibility(true, true, false, false), visible, true, true));
    }

    [Fact]
    public void CoveredWallpapersPauseOnlyWhenAskedButFullScreenAppsAlwaysDo()
    {
        var covered = new MonitorVisibility(FullScreenApp: false, CoveredByWindows: true);
        Assert.Equal((SurfaceState.Drawing, PauseReason.None), SurfaceActivity.ForWallpaper(Awake, covered, pauseWhenCovered: false, false));
        Assert.Equal((SurfaceState.Hidden, PauseReason.Covered), SurfaceActivity.ForWallpaper(Awake, covered, pauseWhenCovered: true, false));

        var game = new MonitorVisibility(FullScreenApp: true, CoveredByWindows: true);
        Assert.Equal((SurfaceState.Hidden, PauseReason.FullScreenApp), SurfaceActivity.ForWallpaper(Awake, game, pauseWhenCovered: false, false));
        Assert.Equal((SurfaceState.Hidden, PauseReason.Covered), SurfaceActivity.ForWallpaper(Awake, game, pauseWhenCovered: true, false));
        // The display being off is the better reason to give.
        Assert.Equal((SurfaceState.Hidden, PauseReason.DisplayOff),
            SurfaceActivity.ForWallpaper(new SystemVisibility(true, false, false, false), game, false, false));
    }

    [Fact]
    public void APreviewWindowStopsOnlyWhenItCannotBeSeen()
    {
        Assert.Equal((SurfaceState.Drawing, PauseReason.None), SurfaceActivity.ForWindow(Awake, windowVisible: true));
        Assert.Equal((SurfaceState.Hidden, PauseReason.WindowHidden), SurfaceActivity.ForWindow(Awake, windowVisible: false));
        Assert.Equal((SurfaceState.Hidden, PauseReason.Locked),
            SurfaceActivity.ForWindow(new SystemVisibility(false, true, false, false), windowVisible: true));
    }
}
