// Hide lyrics (milestone 7; the Mac's LunalyrExtras.lyricsHidden, Lunalyr's own lines) and hiding them while a display
// is mirrored (the Mac's hiddenByMirroring, decided by LunalyrExtras): the renderer fades the wallpapers' lyrics and the
// floating window hides (FloatingVisibility's LyricsVisible input).

using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell;

public sealed partial class AppController
{
    private volatile bool _hiddenByMirroring;

    /// <summary>The lyrics are shown: not hidden with Hide lyrics, nor by a mirrored display.</summary>
    public bool LyricsShown
    {
        get { lock (_gate) return !_settings.LyricsHidden && !_hiddenByMirroring; }
    }

    /// <summary>Mirroring hides the lyrics now (<see cref="Extras.LunalyrExtras.HiddenByMirroring"/>), or no longer does.</summary>
    public void SetHiddenByMirroring(bool hidden)
    {
        bool shown;
        lock (_gate)
        {
            if (_disposed || _hiddenByMirroring == hidden) return;
            _hiddenByMirroring = hidden;
            shown = !_settings.LyricsHidden && !hidden;
        }

        _renderer.SetLyricsShown(shown, animate: true);
        _app.Info(hidden ? "Lyrics hidden while a display is mirrored." : "Mirroring no longer hides the lyrics.");
        UpdateFloating();
    }

    /// <summary>Hands Hide lyrics to the renderer: at once at start-up, faded on a change.</summary>
    private void ApplyLyricsHidden(AppSettings? old, AppSettings settings)
    {
        if (old is not null && old.LyricsHidden == settings.LyricsHidden) return;
        _renderer.SetLyricsShown(!settings.LyricsHidden && !_hiddenByMirroring, animate: old is not null);
        if (old is not null) _app.Info(settings.LyricsHidden ? "Lyrics hidden." : "Lyrics shown.");
        else if (settings.LyricsHidden) _app.Info("Lyrics start hidden (Hide lyrics was on last time).");
    }
}
