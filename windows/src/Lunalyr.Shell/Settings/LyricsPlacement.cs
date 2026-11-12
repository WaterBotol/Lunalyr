using Lunalyr.Core.Floating;

namespace Lunalyr.Shell.Settings;

/// <summary>Which surface draws the lyrics (the Mac's <c>NowPlayingView.drawsLyrics</c>).</summary>
public static class LyricsPlacement
{
    /// <summary>
    /// The desktop draws the lyrics unless they float; the settings preview always does, to show the style being
    /// edited.
    /// </summary>
    public static bool DrawsLyrics(bool settingsPreview, LyricsSurface surface) => settingsPreview || surface == LyricsSurface.Desktop;
}
