namespace Lunalyr.Render;

// The desktop player's hold on the wallpaper lyrics: faint while the card is up so it reads, clearer while its seek bar
// is dragged so the lyric under the hand can be seen following it (the Mac's PlayerHoverFocus; Windows fades only, with
// no blur). A straight fade from wherever it was, drawn as the lyric layer's opacity.
public sealed partial class RenderEngine
{
    // Written by any thread through SetLyricFocus; read on the render thread.
    private (double From, double To, double Start, double Seconds) _lyricFocus = (1, 1, 0, 0);
    private readonly Lock _focusGate = new();

    /// <summary>
    /// Fades the lyric composition to <paramref name="opacity"/> (0–1) over <paramref name="seconds"/>, from wherever
    /// it is now. Idle and still surfaces redraw for the fade.
    /// </summary>
    public void SetLyricFocus(double opacity, double seconds)
    {
        opacity = Math.Clamp(double.IsFinite(opacity) ? opacity : 1, 0, 1);
        seconds = Math.Max(0, double.IsFinite(seconds) ? seconds : 0);
        var now = Now();
        lock (_focusGate)
        {
            var current = LyricFocusAt(_lyricFocus, now);
            if (_lyricFocus.To == opacity) return; // already there or on its way
            _lyricFocus = (current, opacity, now, seconds);
        }

        InputChanged();
    }

    /// <summary>The lyric layer's opacity now (1 unless the desktop player holds it).</summary>
    public double LyricFocusOpacity
    {
        get
        {
            lock (_focusGate) return LyricFocusAt(_lyricFocus, Now());
        }
    }

    // True while a fade is under way: the loop keeps drawing until it ends.
    private bool LyricFocusMoving(double now)
    {
        lock (_focusGate) return now < _lyricFocus.Start + _lyricFocus.Seconds;
    }

    private static double LyricFocusAt((double From, double To, double Start, double Seconds) focus, double now)
    {
        if (!(focus.Seconds > 0) || now >= focus.Start + focus.Seconds) return focus.To;
        var t = Math.Clamp((now - focus.Start) / focus.Seconds, 0, 1);
        var eased = t * t * (3 - 2 * t);
        return focus.From + (focus.To - focus.From) * eased;
    }
}
