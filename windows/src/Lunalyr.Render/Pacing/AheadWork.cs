namespace Lunalyr.Render.Pacing;

/// <summary>
/// Work a layer may do ahead of time (setting the next line) is limited to one piece per render pass, across every
/// surface. Each surface has its own layer and device, so without this every monitor sets the same upcoming line in
/// the same pass and their costs add up into a dropped frame. A layer refused here simply tries again next frame; the
/// next line is set up to 1.5 s before it is needed, so a pass or two of delay is never seen.
/// </summary>
public sealed class AheadWork
{
    private bool _taken;

    /// <summary>True for the first caller in this pass, false after that until <see cref="NewPass"/>.</summary>
    public bool TryTake()
    {
        if (_taken) return false;
        _taken = true;
        return true;
    }

    /// <summary>Called by the engine at the start of each render pass.</summary>
    public void NewPass() => _taken = false;
}
