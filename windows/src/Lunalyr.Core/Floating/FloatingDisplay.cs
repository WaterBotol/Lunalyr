using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Floating;

/// <summary>Physical-pixel insets on each edge of a display (an auto-hidden taskbar's reserve).</summary>
public readonly record struct EdgeInsets(double Left, double Top, double Right, double Bottom)
{
    public static EdgeInsets None => default;
}

/// <summary>
/// One display as the floating window sees it, and the one place Windows' coordinates (physical pixels, virtual
/// screen, y down) meet <see cref="FloatingFrame"/>'s (points, relative to the display, y up).
/// </summary>
/// <param name="Bounds">The whole monitor, physical pixels.</param>
/// <param name="WorkArea">The monitor less the taskbar and docked app bars, physical pixels.</param>
/// <param name="Scale">Pixels per point (the monitor's DPI / 96).</param>
/// <param name="AutoHide">What an auto-hidden taskbar on the monitor would cover when it slides in, so the presets
/// avoid it too (its work area doesn't leave it out).</param>
public readonly record struct FloatingDisplay(Box Bounds, Box WorkArea, double Scale, EdgeInsets AutoHide = default)
{
    private double S => Scale > 0 && double.IsFinite(Scale) ? Scale : 1;

    /// <summary>The display in points, at the origin.</summary>
    public FloatingRect ScreenFrame => new(0, 0, Bounds.Width / S, Bounds.Height / S);

    /// <summary>The work area (less an auto-hidden taskbar's edge) in points, relative to the display, y up.</summary>
    public FloatingRect VisibleFrame
    {
        get
        {
            double left = WorkArea.X + AutoHide.Left, top = WorkArea.Y + AutoHide.Top;
            double right = WorkArea.X + WorkArea.Width - AutoHide.Right, bottom = WorkArea.Y + WorkArea.Height - AutoHide.Bottom;
            var boundsBottom = Bounds.Y + Bounds.Height;
            return new FloatingRect((left - Bounds.X) / S, (boundsBottom - bottom) / S, Math.Max(0, right - left) / S, Math.Max(0, bottom - top) / S);
        }
    }

    /// <summary>The area a floating window may use (no notch on Windows).</summary>
    public FloatingRect Area => FloatingFrame.UsableArea(ScreenFrame, VisibleFrame, 0);

    /// <summary>A frame in points (relative, y up) as a window rectangle in physical pixels (virtual screen, y down), whole pixels.</summary>
    public Box ToPixels(FloatingRect frame)
    {
        var height = Bounds.Height / S;
        // The size is rounded on its own, so a window keeps one size wherever it goes.
        return new Box(Round(Bounds.X + frame.MinX * S), Round(Bounds.Y + (height - frame.MaxY) * S), Round(frame.Width * S), Round(frame.Height * S));
    }

    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>A window rectangle in physical pixels as a frame in points, relative to the display, y up.</summary>
    public FloatingRect ToPoints(Box window)
    {
        var boundsBottom = Bounds.Y + Bounds.Height;
        return new FloatingRect((window.X - Bounds.X) / S, (boundsBottom - (window.Y + window.Height)) / S, window.Width / S, window.Height / S);
    }

    /// <summary>A pointer movement in physical pixels (y down) in points, y up, for <see cref="FloatingFrame.Dragged"/>.</summary>
    public (double Dx, double Dy) Delta(double dxPixels, double dyPixels) => (dxPixels / S, -dyPixels / S);

    /// <summary>Where <paramref name="placement"/> puts the window on this display, in physical pixels.</summary>
    public Box Place(FloatingPlacement placement, FloatingRect? custom, Compositions.SizeD? savedSize, double textSize) =>
        ToPixels(FloatingFrame.Resolve(placement, custom, savedSize, textSize, ScreenFrame, Area));
}
