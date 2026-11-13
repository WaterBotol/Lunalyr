using System.Diagnostics;

namespace Lunalyr.Render.Pacing;

/// <summary>
/// Work a layer may do ahead of time (setting the next line) is limited per render pass, across every surface. Each
/// surface has its own layer and device, so without this every monitor sets the same upcoming line in the same pass and
/// their costs add up into a dropped frame. A layer refused here simply tries again next frame; the next line is set
/// up to 1.5 s before it is needed, so a pass or two of delay is never seen.
/// <para>
/// Two ways to take part: <see cref="TryTake"/> is one piece of work per pass; the lyric layers instead share a time
/// allowance (<see cref="Open"/>, <see cref="Spend"/>): a line is laid out at once and baked a slice at a time within it.
/// </para>
/// </summary>
public sealed class AheadWork
{
    /// <summary>The time all surfaces together may spend on work ahead of time in one pass.</summary>
    public static readonly TimeSpan Allowance = TimeSpan.FromMilliseconds(2.5);

    /// <summary><see cref="Allowance"/> in <see cref="Stopwatch"/> ticks.</summary>
    public static readonly long AllowanceTicks = (long)(Allowance.TotalSeconds * Stopwatch.Frequency);

    private bool _taken;
    private long _spent;
    private int _contenders;
    private int _contendersBefore;

    /// <summary>True for the first caller in this pass, false after that until <see cref="NewPass"/>.</summary>
    public bool TryTake()
    {
        if (_taken) return false;
        _taken = true;
        return true;
    }

    /// <summary>What is left of this pass's allowance, in <see cref="Stopwatch"/> ticks (zero or less: none).</summary>
    public long Remaining => AllowanceTicks - _spent;

    /// <summary>
    /// A surface with work ahead of time asks for its share of this pass's allowance, in <see cref="Stopwatch"/> ticks
    /// (zero or less: none this pass). The allowance is split between the surfaces that had work in the pass before, so
    /// one monitor working through its queue never keeps another from its own; a surface working alone has all of it.
    /// </summary>
    public long Open()
    {
        _contenders++;
        return Math.Min(Remaining, AllowanceTicks / Math.Max(1, _contendersBefore));
    }

    /// <summary>Counts <paramref name="ticks"/> (<see cref="Stopwatch"/> ticks) of work ahead of time against this pass.</summary>
    public void Spend(long ticks) => _spent += Math.Max(0, ticks);

    /// <summary>Called by the engine at the start of each render pass.</summary>
    public void NewPass()
    {
        _taken = false;
        _spent = 0;
        _contendersBefore = _contenders;
        _contenders = 0;
    }
}
