using Lunalyr.Core.Motion;
using Lunalyr.Tests.Shared;
using Level = Lunalyr.Core.Motion.LyricPace.Level;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>
/// The lyric stage's own frame clock: it runs at the display rate only while something moves, and rests once nothing
/// on the stage can change.
/// </summary>
public class LyricStageClockTests
{
    private readonly ManualTimeProvider _time = new();

    private static Level? Quiet(LyricSceneKind kind, double shown = 1, double target = 1, bool playing = false,
                                bool colourMoves = false, NoLyricsDisplay noLyrics = NoLyricsDisplay.Notice, double? age = null) =>
        LyricPace.QuietLevel(new LyricPace.Quiet(kind, shown, target, playing, colourMoves, noLyrics, age), NoticeTiming.Default);

    private static void Settle(LyricPace pace) => pace.Flush();

    // Ported from LyricStageClockTests.swift: testNothingPlayingRestsOnceItsNoticeHasGone
    [Fact]
    public void NothingPlayingRestsOnceItsNoticeHasGone()
    {
        var timing = NoticeTiming.Default;
        Assert.Equal(Level.Active, Quiet(LyricSceneKind.Idle, age: 0.2));
        Assert.Equal(Level.Still, Quiet(LyricSceneKind.Idle, age: 3));
        Assert.Equal(Level.Active, Quiet(LyricSceneKind.Idle, age: 5.4));
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Idle, age: timing.IdleHold + timing.NoticeFade + 0.1));
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Idle, age: 8 * 3600));
        // Nothing has played since launch: no notice, no clock.
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Idle, age: null));
    }

    // Ported from LyricStageClockTests.swift: testMissingLyricsRestOnceThereIsNothingLeftToChange
    [Fact]
    public void MissingLyricsRestOnceThereIsNothingLeftToChange()
    {
        Assert.Equal(Level.Still, Quiet(LyricSceneKind.Missing, age: 2));
        Assert.Equal(Level.Active, Quiet(LyricSceneKind.Missing, age: 4.5));
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Missing, age: 5.2));
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Missing, age: null));
        // "Show nothing" never changes on its own; nor does "Show title and artist" once its colour has settled.
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Missing, playing: true, noLyrics: NoLyricsDisplay.Nothing, age: 0));
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Missing, playing: true, noLyrics: NoLyricsDisplay.TrackName, age: 0));
    }

    // Ported from LyricStageClockTests.swift: testTheTrackNameAndNoticesKeepAClockWhileTheirColourMoves
    [Fact]
    public void TheTrackNameAndNoticesKeepAClockWhileTheirColourMoves()
    {
        Assert.Equal(Level.Ambient, Quiet(LyricSceneKind.Missing, playing: true, colourMoves: true, noLyrics: NoLyricsDisplay.TrackName, age: 0));
        Assert.Equal(Level.Ambient, Quiet(LyricSceneKind.Missing, playing: false, colourMoves: true, noLyrics: NoLyricsDisplay.TrackName));
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Missing, noLyrics: NoLyricsDisplay.Nothing));
        // A notice holding still in a moving colour, too; gone, it needs nothing.
        Assert.Equal(Level.Ambient, Quiet(LyricSceneKind.Missing, colourMoves: true, age: 2));
        Assert.Equal(Level.Ambient, Quiet(LyricSceneKind.Idle, colourMoves: true, age: 3));
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Idle, colourMoves: true, age: 60));
    }

    // Ported from LyricStageClockTests.swift: testHiddenLyricsRestWhileTheyAreSung
    [Fact]
    public void HiddenLyricsRestWhileTheyAreSung()
    {
        foreach (var kind in new[] { LyricSceneKind.Synced, LyricSceneKind.Plain, LyricSceneKind.Loading, LyricSceneKind.Idle, LyricSceneKind.Missing })
            Assert.Equal(Level.Resting, Quiet(kind, shown: 0, target: 0, playing: true, colourMoves: true, age: 1));
        // Hiding and showing fade on every frame.
        Assert.Equal(Level.Active, Quiet(LyricSceneKind.Synced, shown: 0.5, target: 0, playing: true));
        Assert.Equal(Level.Active, Quiet(LyricSceneKind.Synced, shown: 0.5, target: 1, playing: true));
    }

    // Ported from LyricStageClockTests.swift: testLyricsShownAgainStartTheClockOnTheirFirstFrame
    [Fact]
    public void LyricsShownAgainStartTheClockOnTheirFirstFrame()
    {
        var visibility = new LyricVisibility(visible: false);
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Synced, shown: visibility.Presence(1000), target: visibility.Target));
        visibility.Set(true, 1000);
        foreach (var age in new[] { 0.0, 0.0005, 0.001, 0.003 })
        {
            var shown = visibility.Presence(1000 + age);
            Assert.True(shown <= 0.001);
            foreach (var kind in new[] { LyricSceneKind.Synced, LyricSceneKind.Plain, LyricSceneKind.Missing, LyricSceneKind.Idle })
                Assert.Equal(Level.Active, Quiet(kind, shown: shown, target: visibility.Target, age: 1));
        }
        // Shown, the stage decides as usual.
        Assert.Equal(Level.Waiting, Quiet(LyricSceneKind.Synced, shown: visibility.Presence(1001), target: visibility.Target));
        // Hidden again: active while fading out, resting once gone.
        visibility.Set(false, 1002);
        Assert.Equal(Level.Active, Quiet(LyricSceneKind.Synced, shown: visibility.Presence(1002.1), target: visibility.Target));
        Assert.Equal(Level.Resting, Quiet(LyricSceneKind.Synced, shown: visibility.Presence(1003), target: visibility.Target));
    }

    // Ported from LyricStageClockTests.swift: testPausedLyricsOnlyWaitForASeek
    [Fact]
    public void PausedLyricsOnlyWaitForASeek()
    {
        Assert.Equal(Level.Waiting, Quiet(LyricSceneKind.Synced, playing: false));
        Assert.Equal(Level.Waiting, Quiet(LyricSceneKind.Plain, playing: false));
        // Colour that drifts by itself, or is still easing, keeps a slow clock.
        Assert.Equal(Level.Ambient, Quiet(LyricSceneKind.Synced, playing: false, colourMoves: true));
        // Words being sung are decided word by word.
        Assert.Null(Quiet(LyricSceneKind.Synced, playing: true));
        Assert.Equal(Level.Ambient, Quiet(LyricSceneKind.Plain, playing: true));
        Assert.Equal(Level.Still, Quiet(LyricSceneKind.Loading));
    }

    // Ported from LyricStageClockTests.swift: testARestingClockStopsAndDrawsAtTheRealTime
    [Fact]
    public void ARestingClockStopsAndDrawsAtTheRealTime()
    {
        var pace = new LyricPace(_time);
        pace.Report(Level.Waiting);
        Settle(pace);
        Assert.False(pace.Rests);
        Assert.Equal(0.5, pace.Interval(1.0 / 60), 1e-12);
        pace.Report(Level.Resting);
        Settle(pace);
        Assert.True(pace.Rests);
        // A stopped timeline hands a rebuild its last tick's date, possibly hours old: the frame is drawn at the real
        // time instead...
        var now = pace.Now;
        Assert.True(pace.Wall(now - 3600) >= now - LyricPace.Staleness);
        // ...and never earlier than a frame already drawn, once running again.
        pace.Report(Level.Active);
        Settle(pace);
        Assert.True(pace.Wall(now - 3600) >= now - LyricPace.Staleness);
        Assert.Equal(now + 0.5, pace.Wall(now + 0.5));
        Assert.Equal(now + 0.5, pace.Wall(now + 0.2)); // a frame handed a slightly older date is drawn at the newest
    }

    // Ported from LyricStageClockTests.swift: testRebuildsBetweenSlowTicksDrawAtTheRealTime
    [Fact]
    public void RebuildsBetweenSlowTicksDrawAtTheRealTime()
    {
        foreach (var level in new[] { Level.Waiting, Level.Still, Level.Ambient, Level.Active })
        {
            var pace = new LyricPace(_time);
            pace.Report(level);
            Settle(pace);
            var now = pace.Now;
            Assert.True(pace.Wall(now - 0.45) >= now - LyricPace.Staleness, level.ToString());
        }
        // A tick is drawn at its own date, so frames step evenly.
        var fresh = new LyricPace(_time);
        var tick = fresh.Now - 0.003;
        Assert.Equal(tick, fresh.Wall(tick));
    }

    // Ported from LyricStageClockTests.swift: testFramesFollowTheSystemClockWhenItStepsBack
    // (The colour director's part belongs to the colour workstream.)
    [Fact]
    public void FramesFollowTheSystemClockWhenItStepsBack()
    {
        var pace = new LyricPace(_time);
        var now = pace.Now;
        // The clock set back two minutes after a frame was drawn: frames carry on from the new time instead of holding
        // at the old one.
        Assert.Equal(now + 120, pace.Wall(now + 120));
        var after = pace.Wall(now);
        Assert.Equal(now, after, LyricPace.Staleness + 0.01);
        Assert.True(pace.Wall(now + 0.1) > after);
        // A cross-fade under way when the clock stepped back is finished, not held for two minutes.
        var memory = new LyricSceneMemory();
        var first = new LyricSceneKey(LyricSceneKind.Synced, 1, 1);
        var second = new LyricSceneKey(LyricSceneKind.Synced, 2, 2);
        var content = new LyricSceneContent();
        memory.Observe(first, content, 10, 3000, null);
        var fading = memory.Observe(second, content, 0, 3001, null);
        Assert.NotNull(fading.Outgoing);
        var stepped = memory.Observe(second, content, 0, 2881, null);
        Assert.Null(stepped.Outgoing);
        Assert.Equal(1, stepped.IncomingPresence);
    }

    // Ported from LyricStageClockTests.swift: testTheNewestReportedLevelIsTheOnePublished
    [Fact]
    public void TheNewestReportedLevelIsTheOnePublished()
    {
        // A scene changing in the same turn as a notice finishing: the later report must win, or the clock rests
        // mid-cross-fade with no frame left to correct it.
        var pace = new LyricPace(_time);
        pace.Report(Level.Resting);
        pace.Report(Level.Active);
        Settle(pace);
        Assert.Equal(Level.Active, pace.Current);
        pace.Report(Level.Waiting);
        pace.Report(Level.Resting);
        Settle(pace);
        Assert.Equal(Level.Resting, pace.Current);
        pace.Report(Level.Active);
        pace.Report(Level.Resting);
        Settle(pace);
        Assert.Equal(Level.Resting, pace.Current);
    }

    [Fact]
    public void AReportAsksForOneFlushAndAChangeIsAnnounced()
    {
        var pace = new LyricPace(_time);
        var changes = new List<Level>();
        pace.Changed += changes.Add;
        Assert.False(pace.Report(Level.Active)); // already published
        Assert.True(pace.Report(Level.Still));
        Assert.False(pace.Report(Level.Waiting)); // a flush is already due
        Assert.True(pace.Flush());
        Assert.False(pace.Flush());
        Assert.Equal([Level.Waiting], changes);
    }
}
