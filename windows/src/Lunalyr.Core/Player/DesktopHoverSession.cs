// Re-implemented from behaviour, not translated: the Mac's hover step (vc_hover_step / vc_hover_update in
// Sources/Support/DesktopInteraction.c and its use in Sources/Desktop/HoverState.swift) still carries lines from the
// Verso import (a99fd65). What it does, as described for this port: one pointer, one active display; a reveal starts
// only in a display's hot zone on exposed desktop with no button held; a shown player lingers a moment after the
// pointer leaves on the same display, goes at once over a window or on another display, survives a press (so the seek
// bar can be dragged) and is never handed to another display mid-press. The structure, names and the keep zone are
// this port's own.

namespace Lunalyr.Core.Player;

/// <summary>An axis-aligned rectangle in virtual-screen pixels: left and top inclusive, right and bottom exclusive.</summary>
public readonly record struct HoverRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public double CentreX => (Left + Right) / 2;

    public double CentreY => (Top + Bottom) / 2;

    public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public HoverRect Inflated(double dx, double dy) => new(Left - dx, Top - dy, Right + dx, Bottom + dy);

    /// <summary>A <paramref name="width"/> × <paramref name="height"/> rectangle centred on this one.</summary>
    public HoverRect Centred(double width, double height) =>
        new(CentreX - width / 2, CentreY - height / 2, CentreX + width / 2, CentreY + height / 2);
}

/// <summary>
/// One display as the hover logic sees it. <paramref name="HotZone"/> is where a reveal may start;
/// <paramref name="KeepZone"/> is where a shown player stays without lingering. The app makes both from the card
/// (<see cref="HoverZones"/>): the card plus 24 DIPs to reveal, plus 56 to keep.
/// </summary>
public readonly record struct HoverDisplay(long Id, HoverRect Bounds, HoverRect HotZone, HoverRect KeepZone);

/// <summary>
/// Which display shows the player, poll by poll, before the reveal intent and the dismiss guards
/// (<see cref="HoverPolicy.Decide"/>) have their say.
/// </summary>
public sealed class DesktopHoverSession
{
    private double _lastInside = double.NegativeInfinity;

    /// <summary>The display the player is up on, or null.</summary>
    public long? Active { get; set; }

    public void Reset()
    {
        Active = null;
        _lastInside = double.NegativeInfinity;
    }

    /// <summary>
    /// Keeps the player on <paramref name="displayId"/> as of <paramref name="now"/>: a guard held it up (a drag, a
    /// click just made), so its linger runs from here.
    /// </summary>
    public void Hold(long displayId, double now)
    {
        Active = displayId;
        _lastInside = now;
    }

    /// <summary>The display under (<paramref name="x"/>, <paramref name="y"/>), or -1. A shared edge belongs to one display.</summary>
    public static int DisplayAt(IReadOnlyList<HoverDisplay> displays, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return -1;
        for (var i = 0; i < displays.Count; i++)
            if (displays[i].Bounds.Contains(x, y)) return i;
        return -1;
    }

    /// <summary>
    /// One poll. <paramref name="blocked"/>: a window (not the desktop, not Lunalyr's own) is under the pointer.
    /// <paramref name="held"/>: a mouse button is down. Returns the display the player should be on, or null.
    /// </summary>
    public long? Step(IReadOnlyList<HoverDisplay> displays, double x, double y, bool blocked, bool held, double now,
        double linger = HoverPolicy.Linger)
    {
        var index = DisplayAt(displays, x, y);
        if (index < 0 || !double.IsFinite(now) || blocked)
        {
            // Off every display, or a window covers the pointer: gone at once, no linger.
            Reset();
            return null;
        }

        var display = displays[index];
        var inZone = display.HotZone.Contains(x, y);
        if (Active is { } active)
        {
            if (active == display.Id)
            {
                if (inZone || display.KeepZone.Contains(x, y))
                {
                    _lastInside = now;
                    return Active;
                }

                // Left the zone on the same display: the player lingers, then goes (a held button lingers too).
                if (now - _lastInside < linger) return Active;
                Reset();
                return null;
            }

            // Another display: never handed over mid-press; without a press it shows there only from its own hot zone.
            Reset();
            if (held || !inZone) return null;
            Active = display.Id;
            _lastInside = now;
            return Active;
        }

        // Nothing shown: a held button never starts a reveal (rubber-band selection, file drags).
        if (held || !inZone) return null;
        Active = display.Id;
        _lastInside = now;
        return Active;
    }
}
