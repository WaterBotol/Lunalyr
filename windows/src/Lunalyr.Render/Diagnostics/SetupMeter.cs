using System.Diagnostics;

namespace Lunalyr.Render.Diagnostics;

/// <summary>
/// The render-thread time a lyric layer spends setting lines up (laying their text out and baking it), for the
/// line-setup benchmark (<c>CompositionPreview -setup</c>). Sections may nest; the outermost one counts. Reading it
/// once a frame gives that frame's setup time. Allocates nothing.
/// </summary>
internal sealed class SetupMeter
{
    private int _depth;
    private long _since;
    private long _ticks;

    public void Enter()
    {
        if (_depth++ == 0) _since = Stopwatch.GetTimestamp();
    }

    public void Exit()
    {
        if (--_depth == 0) _ticks += Stopwatch.GetTimestamp() - _since;
    }

    /// <summary>Milliseconds of setup since the last call.</summary>
    public double TakeMilliseconds()
    {
        var ms = _ticks * 1000.0 / Stopwatch.Frequency;
        _ticks = 0;
        return ms;
    }
}
