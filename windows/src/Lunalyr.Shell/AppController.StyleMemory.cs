using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell;

// Remembered styles ("Use remembered styles"): a layout and text colour kept for one recording or one artist wins over
// the ones chosen while that music plays (the Mac's LyricLayerView, which reads StyleMemory.remembered(for:) for its
// composition and colour). The artist's applies from the moment the song is known; the song's own once its length is
// (its key is the recording's, as the song offsets').
public sealed partial class AppController
{
    /// <summary>The style remembered for the music playing and whether it is the song's or its artist's, or null.</summary>
    public (RememberedStyle Style, StyleScope Scope)? RememberedStyle
    {
        get { lock (_gate) return RememberedLocked(_settings); }
    }

    // Must hold _gate.
    private (RememberedStyle Style, StyleScope Scope)? RememberedLocked(AppSettings settings) =>
        _lookupTrack is { } track ? StyleMemory.Remembered(settings, _query, track.Artist) : null;

    /// <summary>The settings as the music playing sees them: its remembered layout and colour in place of the chosen ones.</summary>
    private AppSettings Effective(AppSettings settings)
    {
        lock (_gate) return StyleMemory.Applied(settings, RememberedLocked(settings)?.Style);
    }

    /// <summary>
    /// The song's length (and so its own remembered style) became known: its style and layout are worked out again.
    /// Must hold _gate; returns whether the scene changed (the caller publishes it, and the layers, outside the lock).
    /// </summary>
    private bool RestyleForSongLocked()
    {
        var style = Effective(_settings).Style();
        if (style == _scene.Style) return false;
        _scene = _scene with { Style = style };
        return true;
    }
}
