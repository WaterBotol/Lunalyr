// The tray flyout's wording, ported from the Mac's menu-bar panel (Sources/UI/MenuBar/*.swift,
// Sources/UI/Design/NowPlayingComponents.swift, QuickControls.swift, Sources/Extras/LunalyrExtras.swift). Those lines are
// Lunalyr's own (blame-checked against a99fd65), so the words are kept as they are; Windows-only lines say so.

using System.Globalization;

namespace Lunalyr.Shell.Flyout;

/// <summary>Every sentence and label the tray flyout shows or speaks.</summary>
public static class FlyoutText
{
    // ---- Now playing (TrackSummary, TransportControls) ----

    /// <summary>The title with nothing playing (TrackSummary).</summary>
    public const string NothingPlaying = "Nothing playing";

    /// <summary>
    /// The prompt under it. The Mac names its installed players ("Play a song in Music or Spotify"); Windows follows any
    /// player that shows in its media controls, so it names none.
    /// </summary>
    public const string IdlePrompt = "Play a song in any music app";

    /// <summary>"Play music" / "Pause music": named for the music, as the wallpaper has a Pause of its own.</summary>
    public static string TransportTitle(bool playing) => playing ? "Pause music" : "Play music";

    public const string PreviousTrack = "Previous track";

    public const string NextTrack = "Next track";

    /// <summary>The transport group's name to Narrator.</summary>
    public const string MusicPlayback = "Music playback";

    /// <summary>How long a press shows its result before the player has confirmed it (TransportControls).</summary>
    public static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>What the play/pause button shows: a press at once, then the player (TransportControls.shownPlaying).</summary>
    public static bool ShownPlaying(bool actual, bool? pending) => pending ?? actual;

    /// <summary>The artist, or what kind of thing is playing, or the idle prompt (TrackSummary.subtitle).</summary>
    public static string Subtitle(bool hasTrack, string? artist, bool isStream, string idlePrompt = IdlePrompt)
    {
        if (!hasTrack) return idlePrompt;
        if (!string.IsNullOrWhiteSpace(artist)) return artist;
        return isStream ? "Live stream" : "";
    }

    /// <summary>Windows only: the transport while <c>--no-playback</c> (or <c>--demo</c>) is set.</summary>
    public static string PlaybackOff(string reason) => $"Playback controls are off for this run ({reason}).";

    /// <summary>Windows only: a player that takes none of the three commands from other apps.</summary>
    public static string NoControls(string player) =>
        $"{(string.IsNullOrWhiteSpace(player) ? "This player" : player)} doesn't let other apps control its playback.";

    /// <summary>Windows only: a command the player was sent but didn't take.</summary>
    public static string CommandFailed(string? player) =>
        $"{(string.IsNullOrWhiteSpace(player) ? "The player" : player)} didn't respond to that.";

    // ---- The lyric line (LyricQuoteView, MenuBarLyricQuote) ----

    public const string CurrentLyric = "Current lyric";

    /// <summary>What shows between lines and without lyrics (LyricQuoteView.placeholder).</summary>
    public static string Placeholder(LyricNow now, bool hasTrack)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (!hasTrack) return "Lyrics appear here while a song plays.";
        if (now.IsLoading) return "Looking for lyrics…";
        if (now.NotFound) return "No synced lyrics for this song.";
        return now.Source is null ? "Waiting for lyrics…" : "♪";
    }

    /// <summary>"LRCLIB · Word-by-word", once lyrics are showing (MenuBarLyricQuote.source).</summary>
    public static string? Source(LyricNow now)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (now.Source is null) return null;
        return now.Precision is null ? now.Source : $"{now.Source} · {now.Precision}";
    }

    /// <summary>What Narrator reads as the lyric's value (MenuBarLyricQuote.spoken).</summary>
    public static string Spoken(LyricContent content, string placeholder)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content switch
        {
            LyricContent.Line line => line.Text,
            LyricContent.Between between => $"Between lines. Next: {between.Next}",
            _ => placeholder,
        };
    }

    // ---- Lyric timing (MenuBarLyricTiming, TimingNudge) ----

    public const string LyricTiming = "Lyric timing";

    public const string Earlier = "Earlier";

    public const string Later = "Later";

    public const string LyricsEarlier = "Lyrics earlier";

    public const string LyricsLater = "Lyrics later";

    public const string EarlierHelp = "Show lyrics a tenth of a second earlier for this song";

    public const string LaterHelp = "Show lyrics a tenth of a second later for this song";

    public const string ResetSongOffset = "Reset song offset";

    public const string ResetHelp = "Back to the lyrics’ own timing";

    public const string TimingGroup = "Lyric timing for this song";

    /// <summary>"+0.25 s", "−0.30 s" (a real minus sign), "0.00 s" (TimingNudge.format).</summary>
    public static string FormatOffset(double seconds) =>
        seconds == 0 ? "0.00 s" : seconds.ToString("+0.00;−0.00", CultureInfo.InvariantCulture) + " s";

    /// <summary>"Lyrics 0.25 seconds earlier", "No offset" (TimingNudge.spoken).</summary>
    public static string SpokenOffset(double seconds) =>
        seconds == 0
            ? "No offset"
            : string.Create(CultureInfo.InvariantCulture, $"Lyrics {Math.Abs(seconds):0.00} seconds {(seconds > 0 ? "earlier" : "later")}");

    /// <summary>The note a nudge leaves (LunalyrExtras.nudgeSync): "Lyrics 0.1 s earlier · +0.30 s for this song’s lyric source".</summary>
    public static string NudgeNote(double delta, double saved) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Lyrics {Math.Abs(delta):0.0} s {(delta > 0 ? "earlier" : "later")} · {saved.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture)} s for this song’s lyric source");

    /// <summary>The note a reset leaves (LunalyrModels.resetSongOffset).</summary>
    public const string ResetNote = "Lyrics back to their own timing for this song";

    /// <summary>The note a nudge leaves with nothing playing (LunalyrExtras.nudgeSync).</summary>
    public const string NudgeNeedsSong = "Play a song to adjust its timing";

    public const string TapToSync = "Tap to sync…";

    public const string TapToSyncHelp = "Press any key each time a lyric line starts; Lunalyr sets this song’s timing from the taps";

    // ---- Search YouTube… (the Mac's music-video picker: a window, never the browser) ----

    public const string SearchYouTube = "Search YouTube…";

    public const string SearchYouTubeHelp = "Search YouTube for this song and pick the video to use";

    public const string SearchYouTubeNeedsSong = "Available while a song plays";

    // ---- Wallpaper (MenuBarWallpaperControls, QuickToggle, WallpaperModeGrid) ----

    public const string Wallpaper = "Wallpaper";

    public const string Options = "Options…";

    public const string OptionsHelp = "Opens Settings";

    public const string WallpaperOptions = "Wallpaper Options…";

    public const string WallpaperType = "Wallpaper type";

    public const string Layout = "Layout";

    public const string Pause = "Pause";

    public const string Resume = "Resume";

    public const string PauseWallpaper = "Pause Wallpaper";

    public const string ResumeWallpaper = "Resume Wallpaper";

    public const string PauseHint = "Freezes the wallpaper, lyrics included. The music keeps playing.";

    public const string ResumeHint = "Lets the wallpaper move again";

    public const string WallpaperPausedState = "Wallpaper paused";

    /// <summary>Under the toggles while paused (MenuBarWallpaperControls.pausedNote).</summary>
    public const string PausedNote = "Paused: your desktop is frozen until you resume it.";

    /// <summary>Windows only: readings below the lyrics (romanizationDisplay "below"; off is "off").</summary>
    public const string Readings = "Readings";

    public const string ReadingsTitle = "Readings under the lyrics";

    public const string ReadingsHint = "Shows how to read Japanese, Chinese and Korean lyrics";

    /// <summary>Windows only: the song's production credits on the wallpaper (creditsLayer).</summary>
    public const string Credits = "Credits";

    public const string CreditsTitle = "Song credits on the wallpaper";

    public const string CreditsHint = "Shows who wrote and produced the song after its title";

    /// <summary>The Mac's "Match the music" composition (<c>automatic</c>).</summary>
    public const string MatchTheMusic = "Match the music";

    // ---- Footer (MenuBarFooter) ----

    public const string Settings = "Settings…";

    public const string Quit = "Quit Lunalyr";

    /// <summary>The flyout's window title, also what Narrator calls it (distinct from "Lunalyr Settings").</summary>
    public const string WindowTitle = "Lunalyr panel";
}
