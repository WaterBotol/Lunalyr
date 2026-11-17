namespace Lunalyr.Platform.Desktop;

/// <summary>What the whole session looks like: while any of these is set, no wallpaper can be seen anywhere.</summary>
public readonly record struct SystemVisibility(bool DisplayOff, bool Locked, bool ScreenSaver, bool SessionInactive)
{
    public bool ScreensHidden => DisplayOff || Locked || ScreenSaver || SessionInactive;
}

/// <summary>A wallpaper (or preview) window the host created for one monitor.</summary>
/// <param name="Hwnd">The window; owned by the host thread. Valid until the next <see cref="IDesktopSurfaceSink.ReleaseSurfaces"/>.</param>
/// <param name="Width">Client width in physical pixels.</param>
/// <param name="Inset">A floating window's see-through margin round its box, in pixels on every side (0 for a wallpaper).</param>
public sealed record SurfaceWindow(nint Hwnd, MonitorInfo Monitor, int Width, int Height, AttachMode Mode, int Inset = 0);

/// <summary>Everything the renderer needs to decide whether each surface draws.</summary>
/// <param name="Generation">Which <see cref="IDesktopSurfaceSink.SetSurfaces"/> call <paramref name="Monitors"/> lines up with.</param>
/// <param name="Monitors">Per surface, in the order they were handed over.</param>
/// <param name="WindowVisible">The preview window isn't minimised (windowed mode only).</param>
public sealed record DesktopState(int Generation, SystemVisibility System, IReadOnlyList<MonitorVisibility> Monitors,
    bool OnBattery, bool EnergySaverOn, bool WindowVisible = true);

/// <summary>The renderer's side of the desktop host. Every call comes from the host thread.</summary>
public interface IDesktopSurfaceSink
{
    /// <summary>Drop every swap chain on the current windows; the returned handle is set once none is left, so the
    /// host can destroy the windows. The host pumps messages while it waits.</summary>
    WaitHandle ReleaseSurfaces();

    /// <summary>New windows, one per monitor (or the one preview window). <paramref name="generation"/> increases with each call.</summary>
    void SetSurfaces(IReadOnlyList<SurfaceWindow> windows, int generation);

    /// <summary>The latest visibility and power state. Cheap: called whenever anything changes.</summary>
    void UpdateState(DesktopState state);
}
