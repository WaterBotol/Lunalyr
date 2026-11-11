namespace Lunalyr.Render.Pacing;

/// <summary>How a surface paces its frames.</summary>
/// <param name="SyncInterval">Present sync interval (1–4): the frame is shown every Nth refresh.</param>
/// <param name="GatePeriod">
/// Seconds between frames when the cap isn't a whole number of refreshes (or needs more than 4): frames are
/// scheduled on this grid and each is shown at the first refresh after it is due. 0 when the sync interval alone
/// paces the frames.
/// </param>
public readonly record struct PacingPlan(uint SyncInterval, double GatePeriod)
{
    public bool IsGated => GatePeriod > 0;
}

public static class FramePacing
{
    /// <summary>DXGI's largest sync interval.</summary>
    public const uint MaxSyncInterval = 4;

    /// <summary>How close refresh/fps must be to a whole number for the sync interval alone to pace it (59 Hz at 30 fps is).</summary>
    private const double WholeTolerance = 0.05;

    /// <summary>
    /// The plan for <paramref name="cap"/> on a display refreshing at <paramref name="refreshHz"/>. A cap that
    /// divides the refresh rate (30 at 60 Hz, 60 at 240 Hz) uses the sync interval, which keeps frames perfectly
    /// even (as the spike's <c>--fps</c> did). Otherwise (60 at 144 or 280 Hz, 15 at 120 Hz) frames go out on a
    /// time grid at sync interval 1, which averages exactly the cap with at most one refresh of jitter.
    /// A cap at or above the refresh rate is the refresh rate.
    /// </summary>
    public static PacingPlan Plan(double refreshHz, FrameRateCap cap)
    {
        if (cap == FrameRateCap.Display || !(refreshHz > 0) || double.IsInfinity(refreshHz)) return new PacingPlan(1, 0);
        double ratio = refreshHz / (int)cap;
        if (ratio <= 1 + WholeTolerance) return new PacingPlan(1, 0);
        double whole = Math.Round(ratio);
        if (whole <= MaxSyncInterval && Math.Abs(ratio - whole) <= WholeTolerance * whole)
            return new PacingPlan((uint)whole, 0);
        return new PacingPlan(1, 1.0 / (int)cap);
    }

    /// <summary>
    /// The next grid time after a frame due at <paramref name="due"/> was started at <paramref name="now"/>. The
    /// grid carries on from the previous due time so the average rate is exact; a frame that was late by more than
    /// one period (paused, stalled) restarts the grid from now rather than rushing to catch up.
    /// </summary>
    public static double NextDue(double due, double now, double period)
    {
        double next = due + period;
        return next < now ? now + period : next;
    }
}
