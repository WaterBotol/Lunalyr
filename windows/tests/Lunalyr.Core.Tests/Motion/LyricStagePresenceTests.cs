using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>
/// How much of the lyric stage is in use (the Mac's LyricStagePresence): what the text backing follows, and what
/// "When none are found" leaves on the stage.
/// </summary>
public class LyricStagePresenceTests
{
    private static readonly NoticeTiming Timing = new(IdleHold: 5, NoticeHold: 4, NoticeFade: 1);

    private static LyricStagePresence.Inputs Inputs(bool hasTrack = true, double? idleSince = null, bool loading = false, bool timed = false,
                                                    bool plain = false, bool notFound = false, double? notFoundAt = null,
                                                    NoLyricsDisplay noLyrics = NoLyricsDisplay.Notice, bool shown = true) =>
        new(hasTrack, idleSince, loading, timed, plain, notFound, notFoundAt, noLyrics, shown);

    // Ported from LyricLegibilityTests.swift: testHiddenLyricsTakeTheBackingWithThem
    [Fact]
    public void HiddenLyricsTakeTheBackingWithThem()
    {
        const double now = 1000;
        // Hidden by the user or while a display is mirrored: the stage is empty, so no shade is left over the picture.
        LyricStagePresence.Inputs[] states =
        [
            Inputs(timed: true), Inputs(plain: true), Inputs(loading: true), Inputs(notFound: true, noLyrics: NoLyricsDisplay.TrackName),
            Inputs(notFound: true, notFoundAt: now), Inputs(hasTrack: false, idleSince: now),
        ];
        foreach (var state in states)
        {
            Assert.Equal(1, LyricStagePresence.Presence(state, now, Timing));
            Assert.Equal(0, LyricStagePresence.Presence(state with { LyricsShown = false }, now, Timing));
        }
        // Callers that never mention it keep the lyrics shown.
        var unspecified = new LyricStagePresence.Inputs(true, null, false, true, false, false, null, NoLyricsDisplay.Notice);
        Assert.True(unspecified.LyricsShown);
    }

    // Ported from LyricLegibilityTests.swift: testBackingIsDrawnOnlyWhileTheStageShowsSomething
    [Fact]
    public void BackingIsDrawnOnlyWhileTheStageShowsSomething()
    {
        const double now = 1000;
        // Lyrics, plain lines, a loading notice or a track name: in use.
        Assert.Equal(1, LyricStagePresence.Presence(Inputs(timed: true), now, Timing));
        Assert.Equal(1, LyricStagePresence.Presence(Inputs(plain: true), now, Timing));
        Assert.Equal(1, LyricStagePresence.Presence(Inputs(loading: true), now, Timing));
        Assert.Equal(1, LyricStagePresence.Presence(Inputs(notFound: true, noLyrics: NoLyricsDisplay.TrackName), now, Timing));
        // Missing lyrics set to show nothing: an empty stage.
        Assert.Equal(0, LyricStagePresence.Presence(Inputs(notFound: true, noLyrics: NoLyricsDisplay.Nothing), now, Timing));
        // Nothing has played since launch.
        Assert.Equal(0, LyricStagePresence.Presence(Inputs(hasTrack: false), now, Timing));
    }

    // Ported from LyricLegibilityTests.swift: testBackingFadesAwayWithTheNoticesSoAStoppedPlayerLeavesTheDesktopAlone
    [Fact]
    public void BackingFadesAwayWithTheNoticesSoAStoppedPlayerLeavesTheDesktopAlone()
    {
        const double stopped = 1000;
        var idle = Inputs(hasTrack: false, idleSince: stopped);
        // "Nothing playing" holds for five seconds, then fades over one.
        Assert.Equal(1, LyricStagePresence.Presence(idle, stopped + 4.9, Timing));
        var midway = LyricStagePresence.Presence(idle, stopped + 5.5, Timing);
        Assert.InRange(midway, 0.2, 0.8);
        Assert.Equal(0, LyricStagePresence.Presence(idle, stopped + 6, Timing));
        Assert.Equal(0, LyricStagePresence.Presence(idle, stopped + 3600, Timing));
        // "Lyrics unavailable" holds for four.
        var missing = Inputs(notFound: true, notFoundAt: stopped);
        Assert.Equal(1, LyricStagePresence.Presence(missing, stopped + 3, Timing));
        Assert.Equal(0, LyricStagePresence.Presence(missing, stopped + 5, Timing));
        Assert.Equal(0, LyricStagePresence.Presence(Inputs(notFound: true), stopped, Timing)); // no notice up, nothing to back
    }
}
