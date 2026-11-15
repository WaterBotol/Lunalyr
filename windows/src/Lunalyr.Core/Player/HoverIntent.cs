// Ported from the Mac's Sources/Player/HoverIntent.swift (Lunalyr's own code, no Verso lines) and the Lunalyr-written
// rules of Sources/Desktop/HoverState.swift (shouldPoll, pollInterval, decide, showsPlayer; every line of them blames to
// Lunalyr commits, not a99fd65). Display ids are HMONITOR values on Windows, CGDirectDisplayID on the Mac.

namespace Lunalyr.Core.Player;

/// <summary>
/// Whether a pointer in the middle of the desktop means to be there.
/// </summary>
/// <remarks>
/// The desktop is crossed all day on the way to windows and to the other display, so touching the hot zone is not
/// enough. The player comes up once the pointer slows down (hoverIntent's rule: two polls in a row under
/// <see cref="SlowSpeed"/>), or after <see cref="Dwell"/> in the zone at any speed short of a fly-through. A pointer
/// moving faster than <see cref="FlyThrough"/> never reveals and starts the dwell again. Speeds are in DIPs a second,
/// so they mean the same at every display scale.
/// </remarks>
public sealed class HoverIntent
{
    public const double SlowSpeed = 120;
    public const int SlowPolls = 2;
    public const double Dwell = 0.22;
    public const double FlyThrough = 900;

    private double _lastX = double.NaN;
    private double _lastY = double.NaN;
    private double _lastTime = double.NaN;

    public double? EnteredAt { get; private set; }

    public int SlowCount { get; private set; }

    public double Speed { get; private set; }

    /// <summary>Whether an intent is building (the pointer is in the zone, not yet revealed).</summary>
    public bool IsPending => EnteredAt is not null;

    public void Reset()
    {
        EnteredAt = null;
        SlowCount = 0;
    }

    /// <summary>
    /// One poll at (<paramref name="x"/>, <paramref name="y"/>) DIPs. <paramref name="inHotZone"/> is false whenever a
    /// reveal could not start here (outside the zone, over a window, a button held). True when the player should come
    /// up now.
    /// </summary>
    public bool Sample(double x, double y, double time, bool inHotZone)
    {
        try
        {
            if (!double.IsNaN(_lastX) && time - _lastTime > 0.005)
            {
                var distance = Math.Sqrt((x - _lastX) * (x - _lastX) + (y - _lastY) * (y - _lastY));
                Speed = distance / (time - _lastTime);
            }
            else if (double.IsNaN(_lastX))
            {
                Speed = 0;
            }

            if (!inHotZone || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(time))
            {
                Reset();
                return false;
            }

            if (Speed > FlyThrough)
            {
                Reset();
                return false;
            }

            EnteredAt ??= time;
            SlowCount = Speed < SlowSpeed ? SlowCount + 1 : 0;
            return SlowCount >= SlowPolls || time - (EnteredAt ?? time) >= Dwell;
        }
        finally
        {
            _lastX = x;
            _lastY = y;
            _lastTime = time;
        }
    }
}

/// <summary>What keeps a shown player up after the pointer has left it.</summary>
public static class PlayerDismissGuard
{
    /// <summary>After a click on the player, long enough to see what it did.</summary>
    public const double AfterClick = 1.2;

    /// <summary>
    /// Whether the player must stay up although the hover logic would let it go. A drag in flight holds it even over a
    /// window; anything else gives way to a window (the user has clearly moved on). (The Mac's search field holding the
    /// keyboard also holds it; Windows has no player search.)
    /// </summary>
    public static bool Holds(bool dragging, double? lastClick, double now, bool overWindow)
    {
        if (dragging) return true;
        if (overWindow) return false;
        if (lastClick is { } click && now - click >= 0 && now - click < AfterClick) return true;
        return false;
    }
}

/// <summary>
/// What the player's views tell the hover poll: kept apart from the drawing so the poll reads it without redrawing
/// anything. One per app (the Mac's is shared too).
/// </summary>
public sealed class PlayerInteraction
{
    /// <summary>A scrub is in flight (button down on the seek bar).</summary>
    public bool Dragging { get; set; }

    /// <summary>Monotonic seconds of the last click on the player.</summary>
    public double? LastClick { get; set; }

    public void NoteClick(double now) => LastClick = now;
}

/// <summary>The hover poll's timing and its last word on which display shows the player.</summary>
public static class HoverPolicy
{
    /// <summary>The poll while the pointer moves.</summary>
    public const double Interval = 0.09;

    /// <summary>
    /// The poll while the pointer has rested a while and nothing is shown: the first movement is noticed a little
    /// later, then the poll is quick again. Lunalyr runs all day; most of it with the pointer still.
    /// </summary>
    public const double RestingInterval = 0.25;

    public const double RestAfter = 1.5;

    /// <summary>
    /// The poll while the player is up or an intent is building: quick enough to judge the pointer's speed, and for a
    /// click on a control to reach it rather than the desktop underneath.
    /// </summary>
    public const double RevealingInterval = 0.045;

    /// <summary>How long a shown player stays after the pointer leaves it on the same display.</summary>
    public const double Linger = 0.7;

    /// <summary>Whether the poll has any reason to run.</summary>
    public static bool ShouldPoll(bool enabled, bool systemHidden, IReadOnlySet<long> hidden, IReadOnlySet<long> displays)
    {
        if (!enabled || systemHidden) return false;
        return displays.Count == 0 || !displays.IsSubsetOf(hidden);
    }

    /// <summary>How often to poll: quickly while the pointer moves or the controls are up, and for <see cref="RestAfter"/> seconds after.</summary>
    public static double PollInterval(double stillFor, bool active) =>
        !active && stillFor >= RestAfter ? RestingInterval : Interval;

    /// <summary>As above, and quicker still while the player is up or a reveal is pending.</summary>
    public static double PollInterval(double stillFor, bool shown, bool pending) =>
        shown || pending ? RevealingInterval : PollInterval(stillFor, active: false);

    /// <summary>
    /// The display the hover logic settles on this poll, after the reveal intent and the dismiss guards: a new reveal
    /// waits for intent; a shown player stays while something holds it (<see cref="PlayerDismissGuard"/>).
    /// </summary>
    public static long? Decide(long? proposed, long? previous, bool intentReady, bool holds)
    {
        if (proposed is not null && proposed != previous && !intentReady) return holds ? previous : null;
        if (proposed is null && holds) return previous;
        return proposed;
    }
}
