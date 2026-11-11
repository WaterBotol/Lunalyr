namespace Lunalyr.Render.Pacing;

/// <summary>
/// When a surface's frame clock can stop (ported from the Mac's <c>FrameClockPolicy</c>). With a still background
/// and no lyric advancing (nothing playing, paused, plain or missing lyrics) every frame would draw the same picture,
/// so the clock stops once the last fade has finished, and starts again the moment any input changes.
/// Times are seconds on the engine's monotonic clock.
/// </summary>
public static class FrameClockPolicy
{
    /// <param name="BackgroundAnimates">A visible layer redraws from the clock (gradient flow, effects).</param>
    /// <param name="LyricsAdvance">Timed lyrics are being sung.</param>
    /// <param name="TransitionsEnd">When each fade still on screen finishes.</param>
    public readonly record struct Inputs(bool BackgroundAnimates, bool LyricsAdvance, IReadOnlyList<double> TransitionsEnd);

    /// <summary>Null while the clock must keep running; otherwise the moment after which it can stop (maybe already past).</summary>
    public static double? QuietAfter(in Inputs inputs)
    {
        if (inputs.BackgroundAnimates || inputs.LyricsAdvance) return null;
        double latest = double.NegativeInfinity;
        foreach (double end in inputs.TransitionsEnd) latest = Math.Max(latest, end);
        return latest;
    }

    public static bool IsIdle(double? quietAfter, double now) => quietAfter is { } quiet && now >= quiet;
}
