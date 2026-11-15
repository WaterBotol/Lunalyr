using Lunalyr.Core.Motion;

namespace Lunalyr.Render;

// Hide lyrics (milestone 7, the Mac's LunalyrExtras.lyricsHidden and LyricVisibility): the wallpapers' lyric layer fades
// out over 0.45 s and back in over 0.6 s, from wherever it is, and the text backing goes with it (as it does for
// lyrics floating elsewhere). Separate from the desktop player's lyric focus, which multiplies with it.
public sealed partial class RenderEngine
{
    private LyricVisibility _lyricsShown = new(true);
    private bool _lyricsHidden;
    private readonly Lock _shownGate = new();

    /// <summary>Shows or hides the wallpapers' lyrics, fading from where they are unless <paramref name="animate"/> is false.</summary>
    public void SetLyricsShown(bool shown, bool animate = true)
    {
        lock (_shownGate)
        {
            if (animate) _lyricsShown.Set(shown, Now());
            else _lyricsShown = new LyricVisibility(shown);
            _lyricsHidden = !shown;
        }

        InputChanged();
    }

    /// <summary>How present the wallpapers' lyrics are now (1 unless hidden or fading).</summary>
    private double LyricsShownPresence(double now)
    {
        lock (_shownGate) return Math.Clamp(_lyricsShown.Presence(now), 0, 1);
    }

    /// <summary>The lyrics are hidden (or on their way out): the backing goes too.</summary>
    private bool LyricsHiddenNow
    {
        get { lock (_shownGate) return _lyricsHidden; }
    }

    // True while the fade runs (the settle curve nears its end within about a second): every frame of it is drawn.
    private bool LyricsShownMoving(double now)
    {
        lock (_shownGate) return now - _lyricsShown.ChangedAt < 1.0;
    }
}
