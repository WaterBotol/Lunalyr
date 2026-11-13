// Hide lyrics (milestone 7; the Mac's LunalyrExtras.lyricsHidden, Lunalyr's own lines): the renderer fades the
// wallpapers' lyrics and the floating window hides (FloatingVisibility's LyricsVisible input). Windows has no mirroring,
// which also hides them on the Mac.

using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell;

public sealed partial class AppController
{
    /// <summary>The lyrics are shown (not hidden with Hide lyrics).</summary>
    public bool LyricsShown
    {
        get { lock (_gate) return !_settings.LyricsHidden; }
    }

    /// <summary>Hands Hide lyrics to the renderer: at once at start-up, faded on a change.</summary>
    private void ApplyLyricsHidden(AppSettings? old, AppSettings settings)
    {
        if (old is not null && old.LyricsHidden == settings.LyricsHidden) return;
        _renderer.SetLyricsShown(!settings.LyricsHidden, animate: old is not null);
        if (old is not null) _app.Info(settings.LyricsHidden ? "Lyrics hidden." : "Lyrics shown.");
        else if (settings.LyricsHidden) _app.Info("Lyrics start hidden (Hide lyrics was on last time).");
    }
}
