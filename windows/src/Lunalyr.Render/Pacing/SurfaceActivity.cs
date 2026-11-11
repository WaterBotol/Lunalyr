using Lunalyr.Platform.Desktop;

namespace Lunalyr.Render.Pacing;

/// <summary>What a surface's frame clock is doing.</summary>
public enum SurfaceState
{
    /// <summary>Rendering at its frame rate (or idling on a still frame when nothing moves).</summary>
    Drawing,
    /// <summary>Can't be seen right now (covered, full-screen app, display off, locked); may be seen again at any moment.</summary>
    Hidden,
    /// <summary>A wallpaper that "Pause wallpaper" froze, until the user resumes it.</summary>
    Frozen,
}

/// <summary>Why a surface isn't drawing. <see cref="None"/> while it draws.</summary>
public enum PauseReason
{
    None,
    /// <summary>"Pause wallpaper" is on.</summary>
    PausedByUser,
    /// <summary>The displays are off or asleep.</summary>
    DisplayOff,
    /// <summary>The session is locked.</summary>
    Locked,
    /// <summary>The screen saver is running.</summary>
    ScreenSaver,
    /// <summary>The user switched to another session (fast user switching, remote).</summary>
    SessionInactive,
    /// <summary>A full-screen app or game covers this monitor.</summary>
    FullScreenApp,
    /// <summary>Ordinary windows cover the wallpaper and "Pause fully covered wallpapers" is on.</summary>
    Covered,
    /// <summary>A preview window (not a wallpaper) is minimised or hidden.</summary>
    WindowHidden,
}

/// <summary>The rule every surface's frame clock follows.</summary>
public static class SurfaceActivity
{
    /// <summary>
    /// Ported from the Mac's <c>WindowRenderActivity.state</c>: "Pause wallpaper" freezes the desktop wallpapers but
    /// not an ordinary window (<paramref name="followsWindow"/>: a preview), which stops only when it can't be seen.
    /// A wallpaper stops while covered only when "Pause fully covered wallpapers" is on. Nothing renders while the
    /// screens sleep, lock or run the screen saver.
    /// </summary>
    public static SurfaceState State(bool visible, bool screensAsleep, bool followsWindow, bool pauseWhenCovered,
        bool wallpaperPaused)
    {
        if (wallpaperPaused && !followsWindow) return SurfaceState.Frozen;
        if (screensAsleep) return SurfaceState.Hidden;
        return (followsWindow || pauseWhenCovered) && !visible ? SurfaceState.Hidden : SurfaceState.Drawing;
    }

    /// <summary>
    /// The Windows decision for one wallpaper surface, with the reason. On top of the Mac rule, a full-screen app or
    /// game hides the wallpaper on its monitor whatever "Pause fully covered wallpapers" says: on Windows it would
    /// otherwise keep rendering (and sharing the GPU) behind a game.
    /// </summary>
    public static (SurfaceState State, PauseReason Reason) ForWallpaper(SystemVisibility system, MonitorVisibility monitor,
        bool pauseWhenCovered, bool wallpaperPaused)
    {
        var state = State(!monitor.CoveredByWindows, system.ScreensHidden, followsWindow: false, pauseWhenCovered, wallpaperPaused);
        if (state == SurfaceState.Frozen) return (state, PauseReason.PausedByUser);
        if (state == SurfaceState.Hidden) return (state, system.ScreensHidden ? ReasonFor(system) : PauseReason.Covered);
        if (monitor.FullScreenApp) return (SurfaceState.Hidden, PauseReason.FullScreenApp);
        return (SurfaceState.Drawing, PauseReason.None);
    }

    /// <summary>A preview window (the harness's <c>--windowed</c>, later the Settings preview): hidden while minimised.</summary>
    public static (SurfaceState State, PauseReason Reason) ForWindow(SystemVisibility system, bool windowVisible)
    {
        var state = State(windowVisible, system.ScreensHidden, followsWindow: true, pauseWhenCovered: false, wallpaperPaused: false);
        return state == SurfaceState.Drawing ? (state, PauseReason.None)
            : (state, system.ScreensHidden ? ReasonFor(system) : PauseReason.WindowHidden);
    }

    /// <summary>The reason to give while the screens are hidden, most fundamental first.</summary>
    public static PauseReason ReasonFor(SystemVisibility system) =>
        system.DisplayOff ? PauseReason.DisplayOff
        : system.Locked ? PauseReason.Locked
        : system.ScreenSaver ? PauseReason.ScreenSaver
        : system.SessionInactive ? PauseReason.SessionInactive
        : PauseReason.None;
}
