// How the GPU effects are clocked and sized. ShaderTimeBase, ShaderRenderBudget and ShaderEpochPolicy are ported
// faithfully from Lunalyr's own Mac code (ShaderBackgroundView.swift and FrameClockPolicy.swift, commit aa22c91, no
// Verso lines); EffectClock is Windows' own (the Mac's two VisualClock phases, kept continuous as AmbientClock does).

namespace Lunalyr.Core.Backgrounds;

/// <summary>
/// Keeps a shader's 32-bit time small. The wallpaper clocks run for as long as the app does; past about 2^16 seconds a
/// float no longer represents a frame step evenly and motion visibly stutters. Each layer starts from the clock's
/// position within a fixed period, so a new layer never begins at the same state, then advances continuously: it never
/// jumps while it is on screen (the Mac's <c>ShaderTimeBase</c>).
/// </summary>
public struct ShaderTimeBase
{
    public const double Period = 4096;

    private bool _set;
    private double _timeOrigin, _flowOrigin;

    /// <summary>The time and flow to hand the shader for these clock readings.</summary>
    public (float Time, float Flow) Uniforms(double time, double flow)
    {
        if (!_set)
        {
            _timeOrigin = Math.Floor(time / Period) * Period;
            _flowOrigin = Math.Floor(flow / Period) * Period;
            _set = true;
        }
        return ((float)(time - _timeOrigin), (float)(flow - _flowOrigin));
    }
}

/// <summary>How many pixels a GPU effect may shade per frame (the Mac's <c>ShaderRenderBudget</c>).</summary>
public static class ShaderRenderBudget
{
    /// <summary>The ceiling for soft wallpaper fields.</summary>
    public const double Standard = 1_800_000;

    /// <summary>For the costliest effects.</summary>
    public const double Heavy = 1_200_000;

    /// <summary>The settings window's small preview.</summary>
    public const double Preview = 220_000;

    public static double Pixels(GpuEffectCost cost, bool preview, bool economical)
    {
        if (preview) return Preview;
        var budget = cost == GpuEffectCost.Heavy ? Heavy : Standard;
        return economical ? budget * 0.6 : budget;
    }

    /// <summary>
    /// The canvas for a view of <paramref name="width"/> × <paramref name="height"/> points at
    /// <paramref name="backingScale"/>: native pixels when they fit the budget, otherwise the same aspect scaled down.
    /// </summary>
    public static (int Width, int Height) DrawableSize(double width, double height, double backingScale, double budget)
    {
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height)) return (1, 1);
        var scale = double.IsFinite(backingScale) && backingScale > 0 ? backingScale : 2;
        double nativeWidth = width * scale, nativeHeight = height * scale;
        var fit = Math.Min(1, Math.Sqrt(Math.Max(1, budget) / (nativeWidth * nativeHeight)));
        return ((int)Math.Max(1, Math.Floor(nativeWidth * fit)), (int)Math.Max(1, Math.Floor(nativeHeight * fit)));
    }
}

/// <summary>
/// When an effect's clock may start afresh (a new layer with a new <see cref="ShaderTimeBase"/>): only at natural
/// breaks, so the picture never resets in the middle of a song (the Mac's <c>ShaderEpochPolicy</c>).
/// </summary>
public static class ShaderEpochPolicy
{
    public enum Trigger { TrackChange, Wake, Elapsed }

    /// <summary>Even with no break at all, one effect's clock never runs past this (seconds).</summary>
    public const double MaximumAge = 8 * 3600;

    public static bool ShouldRenew(double age, Trigger trigger)
    {
        if (!double.IsFinite(age) || age < 0) return true;
        return trigger switch
        {
            Trigger.TrackChange => age >= 30 * 60,
            Trigger.Wake => age >= 10 * 60,
            _ => age >= MaximumAge - 1,
        };
    }
}

/// <summary>
/// The two clocks a GPU effect or line pattern runs on, from wall time: <c>Time</c> (the steady breathing) advances at
/// Motion speed, <c>Flow</c> (what the shapes move along) at 0.70 × Motion speed, and both at 15% under Reduce Motion,
/// as on the Mac. Turning Reduce Motion on or off changes how fast they run from then on, never where they are.
/// </summary>
public struct EffectClock
{
    /// <summary>The Mac's rate for ambient wallpaper motion under Reduce Motion.</summary>
    public const double ReducedMotionRate = 0.15;

    /// <summary>The flow clock's rate against the steady clock.</summary>
    public const double FlowRate = 0.70;

    private bool _started;
    private double _rate;
    private double _lastWall;
    private double _time;

    public static double Rate(double speed, bool reduceMotion) => Math.Max(0, speed) * (reduceMotion ? ReducedMotionRate : 1);

    /// <summary>The steady clock and the flow clock at <paramref name="wallSeconds"/>.</summary>
    public (double Time, double Flow) At(double wallSeconds, double speed, bool reduceMotion)
    {
        var rate = Rate(speed, reduceMotion);
        if (!double.IsFinite(wallSeconds)) return (_time, _time * FlowRate);
        if (!_started)
        {
            _started = true;
            _time = wallSeconds * rate;
        }
        else if (wallSeconds > _lastWall)
        {
            _time += (wallSeconds - _lastWall) * _rate;
        }
        else if (wallSeconds < _lastWall)
        {
            // A clock that went back (a preview drawing an earlier moment): read it as from the start.
            _time = wallSeconds * rate;
        }
        _rate = rate;
        _lastWall = wallSeconds;
        return (_time, _time * FlowRate);
    }
}
