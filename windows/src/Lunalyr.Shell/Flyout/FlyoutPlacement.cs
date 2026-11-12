namespace Lunalyr.Shell.Flyout;

/// <summary>A rectangle in physical pixels (virtual-screen coordinates), right and bottom exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);
}

/// <summary>Which side of its monitor the taskbar is on.</summary>
public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>
/// Where the tray flyout goes: beside the taskbar, next to the icon that opened it, inside the monitor's work area,
/// with Windows 11's 12-pixel (at 100%) gap, for any taskbar edge and any scale. Pure arithmetic in physical pixels,
/// so it is tested without a screen.
/// </summary>
public static class FlyoutPlacement
{
    /// <summary>The gap between the flyout and the taskbar or screen edge, at 100% (Windows 11's flyouts use 12).</summary>
    public const double MarginDip = 12;

    /// <summary>Device-independent pixels to physical ones at <paramref name="dpi"/> (96 = 100%).</summary>
    public static int ToPixels(double dip, uint dpi) => (int)Math.Round(dip * (dpi == 0 ? 96 : dpi) / 96.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The taskbar's edge: the side where the work area stops short of the monitor; with an auto-hidden taskbar (no
    /// difference) the monitor side nearest the icon; bottom when nothing tells.
    /// </summary>
    public static TaskbarEdge EdgeOf(PixelRect monitor, PixelRect work, PixelRect? icon)
    {
        var insets = new (TaskbarEdge Edge, int Inset)[]
        {
            (TaskbarEdge.Bottom, monitor.Bottom - work.Bottom),
            (TaskbarEdge.Top, work.Top - monitor.Top),
            (TaskbarEdge.Left, work.Left - monitor.Left),
            (TaskbarEdge.Right, monitor.Right - work.Right),
        };
        var widest = insets.MaxBy(i => i.Inset);
        if (widest.Inset > 0) return widest.Edge;
        if (icon is not { IsEmpty: false } r) return TaskbarEdge.Bottom;

        // Auto-hide: the icon sits on the taskbar, so the nearest monitor edge is the taskbar's.
        var cx = (r.Left + r.Right) / 2;
        var cy = (r.Top + r.Bottom) / 2;
        var distances = new (TaskbarEdge Edge, int Distance)[]
        {
            (TaskbarEdge.Bottom, monitor.Bottom - cy),
            (TaskbarEdge.Top, cy - monitor.Top),
            (TaskbarEdge.Left, cx - monitor.Left),
            (TaskbarEdge.Right, monitor.Right - cx),
        };
        return distances.MinBy(d => d.Distance).Edge;
    }

    /// <summary>
    /// The flyout's rectangle.
    /// </summary>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its wanted height in pixels; it is cut to fit the work area (the content scrolls).</param>
    /// <param name="anchor">The icon's rectangle (<c>Shell_NotifyIconGetRect</c>), or the click point as an empty one.</param>
    /// <param name="monitor">The monitor the anchor is on.</param>
    /// <param name="work">That monitor's work area (without the taskbar).</param>
    /// <param name="margin">The gap in pixels (<see cref="MarginDip"/> at the monitor's scale).</param>
    public static PixelRect Place(int width, int height, PixelRect anchor, PixelRect monitor, PixelRect work, int margin)
    {
        var icon = anchor.IsEmpty ? (PixelRect?)null : anchor;
        var edge = EdgeOf(monitor, work, icon);
        // An auto-hidden taskbar covers the monitor's edge while it shows: keep clear of the icon instead.
        var autoHide = work == monitor;
        var area = work;
        width = Math.Max(1, Math.Min(width, area.Width - 2 * margin));
        height = Math.Max(1, Math.Min(height, area.Height - 2 * margin));
        var cx = (anchor.Left + anchor.Right) / 2;
        var cy = (anchor.Top + anchor.Bottom) / 2;

        int x, y;
        switch (edge)
        {
            case TaskbarEdge.Top:
                y = (autoHide && icon is { } t ? Math.Max(area.Top, t.Bottom) : area.Top) + margin;
                x = cx - width / 2;
                break;
            case TaskbarEdge.Left:
                x = (autoHide && icon is { } l ? Math.Max(area.Left, l.Right) : area.Left) + margin;
                y = cy - height / 2;
                break;
            case TaskbarEdge.Right:
                x = (autoHide && icon is { } r ? Math.Min(area.Right, r.Left) : area.Right) - margin - width;
                y = cy - height / 2;
                break;
            default:
                y = (autoHide && icon is { } b ? Math.Min(area.Bottom, b.Top) : area.Bottom) - margin - height;
                x = cx - width / 2;
                break;
        }

        // Inside the work area, a margin clear of every side.
        x = Math.Clamp(x, area.Left + margin, Math.Max(area.Left + margin, area.Right - margin - width));
        y = Math.Clamp(y, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - margin - height));
        return PixelRect.FromSize(x, y, width, height);
    }
}
