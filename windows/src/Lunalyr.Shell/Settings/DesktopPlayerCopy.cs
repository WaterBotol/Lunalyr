namespace Lunalyr.Shell.Settings;

/// <summary>Settings › Performance › Desktop: the desktop player's switch.</summary>
public static class DesktopPlayerCopy
{
    /// <summary>
    /// The caption under "Show player on hover" (the Mac's DesktopOptions caption, without its search and queue: Windows
    /// has no player search).
    /// </summary>
    public static string HoverCaption(bool on) => on
        ? "Rest the pointer in the middle of an uncovered desktop to show the player: the song, its seek bar and the play controls. Clicks elsewhere reach the desktop."
        : "The desktop never shows the player.";
}
