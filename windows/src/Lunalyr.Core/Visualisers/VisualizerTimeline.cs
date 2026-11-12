// Ported from Lunalyr for Mac's VisualizerTimeline.swift (Lunalyr's own lines).

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// The shader clock for one visualiser scene. The wallpaper clock grows for as long as Lunalyr runs, but shaders
/// receive a float: after about two days a float can no longer hold a 60 fps step. This timeline integrates the source
/// clock's steps instead of copying it, so Reduce Motion's slower rate changes speed without jumping the scene, and the
/// origin returns to zero at a natural break (the scene being built, a wake or long pause, music returning after
/// silence, a new album palette) once the scene time has passed <see cref="RebaseThreshold"/>. Continuous playback
/// never jumps.
/// </summary>
public struct VisualizerTimeline
{
    /// <summary>Seconds after which the next natural break restarts the origin.</summary>
    public const double RebaseThreshold = 4_096;
    /// <summary>Last resort for a break-free stream; a float still resolves 8 ms here.</summary>
    public const double ForcedRebase = 65_536;
    /// <summary>A wall-clock gap this long is a pause or a wake, not a frame.</summary>
    public const double PauseGap = 2;
    /// <summary>The longest source step one frame may integrate; a larger one means the scene was off screen.</summary>
    public const double MaximumStep = 0.25;

    private double? _lastSource;
    private double? _lastWallClock;

    public VisualizerTimeline()
    {
    }

    public double Time { get; private set; }

    /// <summary>The value shaders receive.</summary>
    public readonly float ShaderTime => (float)Time;

    /// <summary>
    /// Advances by the source clock's step times <paramref name="rate"/>. <paramref name="wallClock"/> is a monotonic
    /// time used only to recognise pauses; <paramref name="atBreak"/> marks a moment where a jump would not be
    /// noticed. Repeating the same source value (another display drawing the same frame) changes nothing.
    /// </summary>
    public double Advance(double source, double rate, double wallClock, bool atBreak = false)
    {
        rate = double.IsFinite(rate) ? Math.Max(0, rate) : 1;
        if (!double.IsFinite(source)) return Time;
        if (_lastSource is not { } last)
        {
            // A scene being built adopts the wallpaper clock, but being built is itself a break: a large clock restarts.
            _lastSource = source;
            _lastWallClock = wallClock;
            Time = source * rate;
            if (Time > RebaseThreshold) Time = 0;
            return Time;
        }
        var paused = _lastWallClock is { } lastWall && wallClock - lastWall > PauseGap;
        _lastWallClock = wallClock;
        if (source == last && !atBreak && !paused) return Time;
        _lastSource = source;
        var step = source - last;
        if ((atBreak || paused || step < 0) && Time > RebaseThreshold)
        {
            Time = 0;
            return Time;
        }
        // Never integrate a backwards step or one larger than a frame may take.
        Time += Math.Min(MaximumStep, Math.Max(0, step)) * rate;
        if (Time > ForcedRebase) Time = 0;
        return Time;
    }
}

/// <summary>
/// One timeline per scene, shared by every surface showing it: each display, and the copy that fades out during a
/// scene change, stay in phase, and a rebase on one is a rebase on all. Not thread-safe: the render thread owns it.
/// </summary>
public sealed class VisualizerTimelines
{
    private readonly VisualizerTimeline[] _timelines = new VisualizerTimeline[VisualizerStyles.All.Count];
    private readonly bool[] _started = new bool[VisualizerStyles.All.Count];

    /// <summary>Star Trails' simulation, shared by every surface on these timelines.</summary>
    public StarTrailsSimulations StarTrails { get; } = new();

    public double Time(VisualizerStyle style, double source, double rate, double wallClock, bool atBreak)
    {
        var index = (int)style;
        if (!_started[index])
        {
            _timelines[index] = new VisualizerTimeline();
            _started[index] = true;
        }
        return _timelines[index].Advance(source, rate, wallClock, atBreak);
    }
}
