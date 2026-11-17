// Ported from the Mac's WallpaperTransition.swift (5857455, a4e4f6a; Lunalyr's own code): how the wallpaper's background goes
// from one layer to the next. The crossfade itself is LayerCrossfade; this decides when it may start. The outgoing layer
// stays fully drawn until the incoming one has a picture: a music video waits for its first picture (up to
// VideoFirstFrameWait), so a song change fades from the old picture to the new video, never through the stand-in; the
// stand-in shown while a song's video is looked for is never faded to over something on screen, only as a surface's
// first layer (the look-up's own timeout picks the fallback instead). Over Your wallpaper nothing is held that would leave
// the desktop showing through.

namespace Lunalyr.Render.Pacing;

/// <summary>What an incoming layer needs before the fade into it may start.</summary>
public enum TransitionGate
{
    /// <summary>Draws at once: a colour field, a gradient, a still cover, Your wallpaper.</summary>
    None,

    /// <summary>A GPU effect: holds until it is ready on this display's GPU.</summary>
    Compile,

    /// <summary>Holds until it draws its first frame (a visualiser, an imported picture), or <see cref="WallpaperTransition.FirstFrameWait"/> passes.</summary>
    FirstFrame,

    /// <summary>
    /// A video, a music video or animated cover art: holds until it shows a picture, or
    /// <see cref="WallpaperTransition.VideoFirstFrameWait"/> passes. Over Your wallpaper it does not hold.
    /// </summary>
    Video,

    /// <summary>The stand-in while the song's video is looked for: never faded to over something on screen.</summary>
    VideoLookup,
}

public static class WallpaperTransition
{
    /// <summary>How long a layer's first frame is waited for, in seconds.</summary>
    public const double FirstFrameWait = 3;

    /// <summary>How long a video's first picture is waited for: a YouTube page takes a moment to load and start.</summary>
    public const double VideoFirstFrameWait = 4;

    /// <summary>How long a song change in Music video waits for the song's video to be found before showing the fallback.</summary>
    public const double VideoLookupWait = 3;

    /// <summary>The longest the outgoing wallpaper can be held on a song change in Music video: the look-up, then the new player's first picture.</summary>
    public const double LongestHold = VideoLookupWait + VideoFirstFrameWait;

    /// <summary>
    /// Whether a layer behind <paramref name="gate"/> has something to draw, so the fade into it can run.
    /// </summary>
    /// <param name="firstFrameDrawn">Its first picture is up.</param>
    /// <param name="overdue">Its wait ran out.</param>
    /// <param name="firstLayer">Nothing is on screen to hold instead (the surface's first layer).</param>
    /// <param name="compiled">A GPU effect is ready on this display.</param>
    /// <param name="overOpaque">The layer on screen beneath covers the desktop (not Your wallpaper).</param>
    public static bool CanDraw(TransitionGate gate, bool firstFrameDrawn, bool overdue, bool firstLayer = false, bool compiled = true,
                               bool overOpaque = true) => gate switch
    {
        TransitionGate.None => true,
        TransitionGate.Compile => compiled,
        TransitionGate.FirstFrame => firstFrameDrawn || overdue,
        TransitionGate.Video => firstFrameDrawn || overdue || !overOpaque,
        _ => firstLayer || overdue || !overOpaque,
    };

    /// <summary>How long a held layer waits before it is shown anyway.</summary>
    public static double FirstFrameWaitFor(TransitionGate gate) => gate switch
    {
        TransitionGate.Video => VideoFirstFrameWait,
        // Longer than the look-up's own timeout, which decides first.
        TransitionGate.VideoLookup => VideoLookupWait + FirstFrameWait,
        _ => FirstFrameWait,
    };

    /// <summary>
    /// Whether the frame clock must keep running for the background: the layer faded to animates, or, while it is held, a
    /// ready layer still on screen beneath it does (bottom first, the last being the one faded to).
    /// </summary>
    public static bool BackgroundAnimates(IReadOnlyList<(bool Animates, bool IsReady)> layers, bool held)
    {
        if (layers.Count == 0) return false;
        if (layers[^1].Animates) return true;
        if (!held) return false;
        for (var i = 0; i < layers.Count - 1; i++)
            if (layers[i].Animates && layers[i].IsReady) return true;
        return false;
    }
}
