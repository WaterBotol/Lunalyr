// What the lyric tools say: Settings › Timing (This song, Find lyrics, Without synced lyrics), the Tap to sync panel and
// the tray flyout's Tap to sync row. Ported from the Mac's TimingPage.swift, LyricTapSyncPanel.swift and
// MenuBarNowPlaying.swift (Lunalyr's own lines, checked with git blame). Worded afresh where the Mac's words sit on lines
// that trace back to the squashed Verso import: the notes after choosing or importing lyrics (LyricsViewModel.select and
// importLyrics) and the "Say so briefly" choice (Settings.NoLyricsBehaviour). "Show title and artist" and "Show nothing"
// are taken from Lunalyr's own lines (LyricStageClockTests.swift).

using System.Globalization;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.LyricTools;

public static class LyricToolsText
{
    // ---- This song ----

    public const string ThisSong = "This song";

    public const string PlayASong = "Play a song to adjust its timing or find lyrics.";

    public const string LyricsFrom = "Lyrics from";

    public const string Timing = "Timing";

    public const string MixedTiming = "Mixed timing";

    public const string MixedTimingCaption =
        "Some lines animate word by word. The other lines appear all at once because they only have a line timestamp.";

    public const string Refining =
        "Showing the best match so far. Slower sources are still being checked; a better one replaces it between lines.";

    public const string UsingSavedChoice = "Using your saved choice";

    public const string ReturnToAutomatic = "Return to Automatic";

    public const string ReturnToAutomaticName = "Return to automatic selection";

    public const string ReturnToAutomaticHelp = "Let Lunalyr choose this song’s lyrics again";

    // ---- Tap to sync (Timing page and the panel) ----

    public const string TapToSyncHelp = "Press any key each time a lyric line starts; Lunalyr sets this song’s timing from the taps";

    public const string UndoTapToSync = "Undo Tap to Sync";

    public const string UndoTapToSyncHelp = "Back to the timing from before the last tap to sync";

    /// <summary>The Timing page's button: start, or stop a session listening.</summary>
    public static string TapToSyncAction(bool listening) =>
        listening ? LyricTapSyncController.StopTitle : LyricTapSyncController.MenuTitle;

    public const string PanelTitle = "Tap to sync lyrics";

    /// <summary>The panel window's title (what Alt+Tab would show, were it listed, and what checks find it by).</summary>
    public const string PanelWindowTitle = "Lunalyr Tap to sync";

    public const string Stop = "Stop";

    public const string Undo = "Undo";

    public const string Done = "Done";

    /// <summary>Windows only: a button that taps, for a touch screen or a pointer.</summary>
    public const string Tap = "Tap";

    public const string TapHelp = "Press any key, or this button, as each lyric line starts";

    public const string StopHelp = "Stops without changing the timing (Esc)";

    public const string UndoHelp = "Back to the timing from before this tap to sync (Ctrl+Z)";

    public const string DoneHelp = "Closes this panel (Esc or Enter)";

    /// <summary>What Narrator says after each tap (the Mac's VoiceOver announcement).</summary>
    public static string TapAnnouncement(int count) =>
        count <= LyricTapSync.RequiredTaps
            ? string.Create(CultureInfo.InvariantCulture, $"Tap {count} of {LyricTapSync.RequiredTaps}")
            : string.Create(CultureInfo.InvariantCulture, $"Tap {count}");

    /// <summary>The note left after Undo (the Mac's LyricTapSyncController.undo).</summary>
    public static string Undone(double previous) => $"Tap to sync undone: lyrics back to {TimingNudge.Format(previous)}";

    // ---- Find lyrics ----

    public const string FindLyrics = "Find lyrics";

    public const string SongTitle = "Song title";

    public const string TitlePrompt = "Title";

    public const string Artist = "Artist";

    public const string ArtistPrompt = "Artist";

    public const string Search = "Search";

    public const string SearchHelp = "Ask every enabled source for these words";

    public const string UsePlayingSong = "Use Playing Song";

    public const string UsePlayingSongHelp = "Put the playing song’s title and artist back";

    public const string Searching = "Searching";

    public const string NeedsSong = "Searching and importing work on the song playing.";

    public const string ImportLyrics = "Import LRC or TTML…";

    public const string ImportHelp = "Use a lyrics file of your own for the playing song (up to 1 MB)";

    public const string ImportPickerTitle = "Import lyrics for the playing song";

    public const string UseForThisSong = "Use for This Song";

    public static string UseForThisSongName(LyricCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return $"Use {candidate.Source.Title()} for this song";
    }

    public const string CloseMatches = "Close matches not used";

    public const string Unconfirmed = "The service didn’t confirm which recording it matched";

    public const string SavedReadings = "Saved pronunciation readings";

    public static string NotUsed(string reason) => $"Not used: {reason}";

    public const string AsksEverySource = "Search asks every source.";

    public const string AsksEverySourceMore =
        "Sources that couldn’t beat the match showing weren’t asked further. Search asks every source.";

    /// <summary>Artist and, when the service reported it, the matched recording's length (the Mac's CandidateCard.details).</summary>
    public static string Details(LyricCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var parts = new List<string> { candidate.Artist.Length == 0 ? "No artist given" : candidate.Artist };
        if (candidate.Duration is double duration && duration > 0) parts.Add(LyricQuery.Clock(duration));
        return string.Join(" · ", parts);
    }

    /// <summary>The source and the timing it gives ("LRCLIB · Word-by-word").</summary>
    public static string SourceLine(LyricCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return $"{candidate.Source.Title()} · {candidate.Provenance.Precision}";
    }

    /// <summary>The state a lookup gave a source, when it says more than the source line (the ranking's source-list label).</summary>
    public static string? SourceState(LyricCandidate candidate, IReadOnlyDictionary<LyricSourceId, string> states)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(states);
        return states.TryGetValue(candidate.Source, out var state) && !string.IsNullOrWhiteSpace(state) ? state : null;
    }

    // ---- Notes after a choice (worded afresh: the Mac's are on Verso lines) ----

    public static string Chosen(bool saved) =>
        saved ? "Saved as your choice for this recording." : "Showing your choice, but it couldn’t be saved.";

    public static string Imported(bool saved) =>
        saved ? "Imported lyrics saved as your choice for this recording." : "Showing the imported lyrics, but they couldn’t be saved.";

    /// <summary>A file whose text has no LRC or TTML timing (the Mac's importLyrics, a Lunalyr line).</summary>
    public const string NoTimestamps = "This file has no usable LRC or TTML timestamps.";

    /// <summary>The source name imported lyrics are shown under.</summary>
    public const string ImportedSource = "Imported lyrics";

    /// <summary>The largest lyric file imported (the Mac's InspectorImport).</summary>
    public const int ImportLimit = 1_000_000;

    // ---- Without synced lyrics ----

    public const string WithoutSyncedLyrics = "Without synced lyrics";

    public const string WhenNoneFound = "When none are found";

    /// <summary>"When none are found": the Mac's choices in its order, by the stored values.</summary>
    public static IReadOnlyList<SettingsChoice> NoLyricsChoices { get; } =
    [
        new("notice", "Show a short note", "A calm note on the wallpaper says so, then fades after a few seconds."),
        new("trackName", "Show title and artist", "The wallpaper shows the song’s title and artist in place of lyrics."),
        new("nothing", "Show nothing", "The wallpaper shows no words at all until a song with lyrics plays."),
    ];

    // ---- Tray flyout (the Mac's MenuBarLyricTiming) ----

    public const string FlyoutTapToSync = "Tap to sync…";

    public const string FlyoutStopTapToSync = "Stop tap to sync";

    public const string FlyoutUndoTapSync = "Undo tap sync";
}
