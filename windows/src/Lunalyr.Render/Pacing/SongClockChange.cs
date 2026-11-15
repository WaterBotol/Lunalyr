namespace Lunalyr.Render.Pacing;

/// <summary>
/// Whether a song-clock reading is new input for idle and paused surfaces: playback started or stopped, a seek while
/// playing, a scrub while paused, or the clock appearing or going away (NaN = nothing playing). A clock that simply
/// runs on, or stays NaN, is not.
/// </summary>
public static class SongClockChange
{
    /// <summary>A playing clock may drift from wall time by this much before it counts as a seek.</summary>
    public const double SeekTolerance = 0.5;

    /// <summary>A paused clock that moves by more than this was scrubbed.</summary>
    public const double ScrubTolerance = 0.05;

    /// <param name="wallElapsed">Wall-clock seconds since <paramref name="lastSeconds"/> was read.</param>
    public static bool IsNewInput(double lastSeconds, bool lastPlaying, double seconds, bool playing, double wallElapsed)
    {
        if (playing != lastPlaying) return true;
        bool wasKnown = !double.IsNaN(lastSeconds), isKnown = !double.IsNaN(seconds);
        if (wasKnown != isKnown) return true;
        if (!isKnown) return false;
        double moved = seconds - lastSeconds;
        return playing ? Math.Abs(moved - wallElapsed) > SeekTolerance : Math.Abs(moved) > ScrubTolerance;
    }
}
