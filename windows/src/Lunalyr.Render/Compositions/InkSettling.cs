using Lunalyr.Core.Colour;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Compositions;

/// <summary>
/// The ink a scene's palette settles on (<see cref="LyricInks.Settled(Palette, LyricStyle)"/>: the colour director run
/// to rest, a few milliseconds of arithmetic), worked out on the thread pool from the moment a new song's colours
/// arrive. A song played from its start first needs it for its opening title, half a second in, by when it is ready.
/// Needed sooner, the render thread works it out itself rather than wait (the pool may have it on a slower core), as it
/// always did: the result is the same either way.
/// </summary>
internal sealed class InkSettling
{
    private readonly Palette _palette;
    private readonly LyricStyle _style;
    private volatile bool _done;
    private LyricInk _ink;

    public InkSettling(Palette palette, LyricStyle style)
    {
        _palette = palette;
        _style = style;
        ThreadPool.UnsafeQueueUserWorkItem(static settling => settling.Settle(), this, preferLocal: false);
    }

    /// <summary>The settled ink: the pool's when it is ready, otherwise worked out here.</summary>
    public LyricInk Ink => _done ? _ink : LyricInks.Settled(_palette, _style);

    private void Settle()
    {
        try
        {
            _ink = LyricInks.Settled(_palette, _style);
            _done = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Worked out (and reported) on the render thread instead, when it is needed.
        }
    }
}
