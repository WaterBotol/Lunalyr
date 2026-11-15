namespace Lunalyr.Platform.Desktop;

/// <summary>
/// One top-level window as the desktop host reads it (front to back from <c>EnumWindows</c>). Only geometry, class
/// name and style bits are read: never titles or contents.
/// </summary>
/// <param name="Alpha">Constant layered alpha 0..1; 1 for a non-layered window; NaN when layered per pixel (unknown).</param>
/// <param name="ClickThrough"><c>WS_EX_TRANSPARENT</c>: an overlay the user can't interact with.</param>
/// <param name="Own">One of Lunalyr's windows.</param>
/// <param name="Maximized">Maximised (<c>WS_MAXIMIZE</c>): an ordinary window, never a full-screen app, even when an
/// auto-hiding taskbar lets it span the whole monitor.</param>
public readonly record struct WindowRecord(nint Handle, Box Bounds, string ClassName, bool Visible, bool Minimized,
    bool Cloaked, double Alpha, bool ClickThrough, bool Own, bool Maximized = false);

/// <summary>Whether each monitor's wallpaper can be seen, decided from a window list alone.</summary>
public static class DesktopVisibility
{
    /// <summary>Windows fading in or out aren't opaque yet.</summary>
    public const double OpaqueAlpha = 0.95;

    /// <summary>The desktop and the taskbars: never an app covering the wallpaper.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>
    /// Terminals whose background transparency leaves the wallpaper showing through a window that still reports full
    /// alpha (the Mac keeps the same kind of list). A wallpaper behind them keeps moving.
    /// </summary>
    private static readonly HashSet<string> SeeThroughClasses = new(StringComparer.Ordinal)
    {
        "CASCADIA_HOSTING_WINDOW_CLASS", // Windows Terminal (acrylic / opacity settings)
    };

    public static bool IsShell(string className) => ShellClasses.Contains(className);

    /// <summary>Whether a window counts towards covering the desktop: visible, on screen, opaque, an app's.</summary>
    public static bool CanCover(in WindowRecord w) =>
        w.Visible && !w.Minimized && !w.Cloaked && !w.Own && !w.ClickThrough
        && w.Alpha >= OpaqueAlpha // NaN (per-pixel layered) fails this too
        && w.Bounds.IsUsable && !IsShell(w.ClassName) && !SeeThroughClasses.Contains(w.ClassName);

    /// <summary>
    /// The visibility of every monitor's wallpaper.
    /// <list type="bullet">
    /// <item>Full-screen app: an opaque app window that isn't maximised covers the monitor's whole bounds (taskbar
    /// included), as a full-screen video, borderless game or presentation does; or Windows reports an exclusive-mode Direct3D app
    /// (<paramref name="d3dFullScreenOnForegroundMonitor"/>) and the foreground window is on this monitor.</item>
    /// <item>Covered by windows: opaque app windows leave nothing noticeable of the work area showing
    /// (<see cref="DesktopCoverage.RegionCovered"/>, the gap scaled by the monitor's DPI).</item>
    /// </list>
    /// An empty or failed window list hides nothing.
    /// </summary>
    public static MonitorVisibility[] Evaluate(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<WindowRecord> windows,
        nint foregroundMonitor = 0, bool d3dFullScreenOnForegroundMonitor = false)
    {
        var result = new MonitorVisibility[monitors.Count];
        var covers = new List<(Box Box, double Area)>();
        var boxes = new Box[DesktopCoverage.MaxCovers];
        for (int m = 0; m < monitors.Count; m++)
        {
            var monitor = monitors[m];
            bool fullScreen = d3dFullScreenOnForegroundMonitor && foregroundMonitor != 0 && foregroundMonitor == monitor.Handle;
            covers.Clear();
            foreach (var w in windows)
            {
                if (!CanCover(w)) continue;
                if (!w.Maximized && w.Bounds.Covers(monitor.Bounds)) fullScreen = true;
                if (monitor.WorkArea.Intersect(w.Bounds) is { } overlap && overlap.Width >= 1 && overlap.Height >= 1)
                    covers.Add((w.Bounds, overlap.Width * overlap.Height));
            }
            // Largest first and bounded: dropping the smallest can only under-report coverage.
            covers.Sort((a, b) => b.Area.CompareTo(a.Area));
            int n = Math.Min(covers.Count, boxes.Length);
            for (int i = 0; i < n; i++) boxes[i] = covers[i].Box;
            bool covered = n > 0 && DesktopCoverage.RegionCovered(monitor.WorkArea, boxes.AsSpan(0, n),
                DesktopCoverage.NoticeableGap * monitor.Scale, DesktopCoverage.MaximumExposedFraction);
            result[m] = new MonitorVisibility(fullScreen, covered || fullScreen);
        }
        return result;
    }

    /// <summary>The window that makes <paramref name="monitor"/> full-screen, if one does (for logs).</summary>
    public static WindowRecord? FullScreenWindow(MonitorInfo monitor, IReadOnlyList<WindowRecord> windows)
    {
        foreach (var w in windows)
            if (CanCover(w) && !w.Maximized && w.Bounds.Covers(monitor.Bounds)) return w;
        return null;
    }
}

/// <summary>What one monitor's wallpaper can see of itself.</summary>
/// <param name="FullScreenApp">A full-screen app or game covers the whole monitor.</param>
/// <param name="CoveredByWindows">Windows leave nothing noticeable of its desktop showing.</param>
public readonly record struct MonitorVisibility(bool FullScreenApp, bool CoveredByWindows);
