namespace Lunalyr.Platform.Desktop;

/// <summary>One monitor as the desktop host sees it.</summary>
/// <param name="Handle">The HMONITOR (valid until the next display change).</param>
/// <param name="DeviceName">GDI device name, e.g. <c>\\.\DISPLAY1</c>; stable across display changes.</param>
/// <param name="Bounds">Whole monitor, virtual-screen pixels.</param>
/// <param name="WorkArea">Bounds minus the taskbar and docked app bars.</param>
/// <param name="Dpi">Effective DPI (96 = 100 %).</param>
/// <param name="RefreshHz">Current refresh rate (whole hertz, as GDI reports it).</param>
public sealed record MonitorInfo(nint Handle, string DeviceName, Box Bounds, Box WorkArea, uint Dpi, int RefreshHz, bool IsPrimary)
{
    public float Scale => Dpi / 96f;
}

/// <summary>Compares two monitor snapshots to decide whether the wallpaper surfaces must be rebuilt.</summary>
public static class MonitorLayout
{
    /// <summary>
    /// What changed between two layouts. A rebuild is needed when monitors were added or removed, or one moved,
    /// resized, changed DPI or refresh rate. A work-area change alone (taskbar moved or auto-hidden) only matters to
    /// coverage, not to the surfaces.
    /// </summary>
    public static LayoutChange Compare(IReadOnlyList<MonitorInfo> before, IReadOnlyList<MonitorInfo> after)
    {
        if (before.Count != after.Count) return LayoutChange.Monitors;
        var change = LayoutChange.None;
        for (int i = 0; i < after.Count; i++)
        {
            var a = after[i];
            MonitorInfo? b = null;
            foreach (var m in before) if (m.DeviceName == a.DeviceName) { b = m; break; }
            if (b is null) return LayoutChange.Monitors;
            if (b.Bounds != a.Bounds || b.Dpi != a.Dpi || b.RefreshHz != a.RefreshHz || b.IsPrimary != a.IsPrimary)
                change |= LayoutChange.Surfaces;
            else if (b.WorkArea != a.WorkArea)
                change |= LayoutChange.WorkArea;
        }
        return change;
    }
}

[Flags]
public enum LayoutChange
{
    None = 0,
    /// <summary>Only a work area changed: re-check coverage.</summary>
    WorkArea = 1,
    /// <summary>A monitor moved, resized or changed DPI/refresh: rebuild its surface.</summary>
    Surfaces = 2,
    /// <summary>Monitors were added or removed.</summary>
    Monitors = 4 | Surfaces,
}
