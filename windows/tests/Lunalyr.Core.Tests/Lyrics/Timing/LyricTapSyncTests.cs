using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Tests.Lyrics.Timing;

public class LyricTapSyncTests
{
    private const double Tol = 1e-9;

    /// <summary>Irregular line starts, like a real song.</summary>
    private static readonly double[] Starts = [12.0, 15.4, 19.1, 21.8, 26.5, 29.0, 33.7, 36.2, 41.9, 44.4];

    // ---- Estimate ----

    // Ported from LyricTapSyncTests.swift: testTapsLateByAFixedAmountGiveThatOffset
    [Fact]
    public void TapsLateByAFixedAmountGiveThatOffset()
    {
        // Each tap lands 0.6 s after its line (lyrics should come 0.6 s later).
        var taps = Starts[2..6].Select(s => s + 0.6).ToArray();
        var estimate = LyricTapSync.EstimateOffset(taps, Starts, 0);
        Assert.NotNull(estimate);
        Assert.Equal(-0.6, estimate.Value.Offset, Tol);
        Assert.Equal(4, estimate.Value.Used);
    }

    // Ported from LyricTapSyncTests.swift: testTooFewTapsGiveNothing
    [Fact]
    public void TooFewTapsGiveNothing()
    {
        Assert.Null(LyricTapSync.EstimateOffset([12.3, 15.7, 19.4], Starts, 0));
        Assert.Null(LyricTapSync.EstimateOffset([1, 2, 3, 4], [], 0));
    }

    // Ported from LyricTapSyncTests.swift: testAStrayTapIsIgnoredOnceEnoughTapsAgree
    [Fact]
    public void AStrayTapIsIgnoredOnceEnoughTapsAgree()
    {
        // Four good taps 0.3 s early, and one wild tap mid-line.
        var good = new[] { Starts[1], Starts[2], Starts[4], Starts[5] }.Select(s => s - 0.3).ToArray();
        double[] taps = [good[0], good[1], 24.2, good[2], good[3]];
        var estimate = LyricTapSync.EstimateOffset(taps, Starts, 0);
        Assert.NotNull(estimate);
        Assert.Equal(0.3, estimate.Value.Offset, Tol); // the stray tap moves nothing
        Assert.Equal(4, estimate.Value.Used);
        Assert.Equal(5, estimate.Value.Total);
        // With only four taps, one of them stray, the rest don't yet agree enough.
        Assert.Null(LyricTapSync.EstimateOffset(taps[..4], Starts, 0));
    }

    // Ported from LyricTapSyncTests.swift: testJitteredTapsGiveTheirMedian
    [Fact]
    public void JitteredTapsGiveTheirMedian()
    {
        double[] jitter = [0.05, -0.08, 0.12, -0.02, 0.4];
        var taps = Starts[..5].Zip(jitter, (s, j) => s + 1.0 + j).ToArray();
        var estimate = LyricTapSync.EstimateOffset(taps, Starts, 0);
        Assert.NotNull(estimate);
        // Offsets are -(1 + jitter); 0.4 is beyond the tolerance of the winning fit so may or may not count, but the
        // median stays near -1.05.
        Assert.Equal(-1.05, estimate.Value.Offset, 0.06);
    }

    // Ported from LyricTapSyncTests.swift: testADoubleTapOnOneLineCountsOnce
    [Fact]
    public void ADoubleTapOnOneLineCountsOnce()
    {
        // Two taps on one line, two on others: three lines marked, not enough.
        double[] taps = [Starts[0] + 0.2, Starts[0] + 0.25, Starts[1] + 0.2, Starts[2] + 0.2];
        Assert.Null(LyricTapSync.EstimateOffset(taps, Starts, 0));
        Assert.Equal(3, LyricTapSync.Agreeing(taps, Starts, -0.2).Count);
    }

    // Ported from LyricTapSyncTests.swift: testAnAmbiguousFitPrefersTheOffsetInForce
    [Fact]
    public void AnAmbiguousFitPrefersTheOffsetInForce()
    {
        // Evenly spaced lines: shifting by a whole line fits as well, so the fit nearest the offset already set wins.
        var even = Enumerable.Range(0, 16).Select(i => 10.0 + 2 * i).ToArray();
        double[] taps = [14.5, 16.5, 18.5, 20.5];
        Assert.Equal(-0.5, LyricTapSync.EstimateOffset(taps, even, -0.4)!.Value.Offset, Tol);
        Assert.Equal(1.5, LyricTapSync.EstimateOffset(taps, even, 1.4)!.Value.Offset, Tol);
    }

    // Ported from LyricTapSyncTests.swift: testOffsetsBeyondTheSearchWindowOrStoreLimitAreNotFound
    [Fact]
    public void OffsetsBeyondTheSearchWindowOrStoreLimitAreNotFound()
    {
        var taps = Starts[..4].Select(s => s - 20).ToArray();
        Assert.Null(LyricTapSync.EstimateOffset(taps, Starts, 0)); // Tap to sync refines; it doesn't search
        Assert.NotNull(LyricTapSync.EstimateOffset(taps, Starts, 18));
    }

    // Ported from LyricTapSyncTests.swift: testHelpers
    [Fact]
    public void Helpers()
    {
        Assert.Equal(2, LyricTapSync.Median([3, 1, 2]));
        Assert.Equal(2.5, LyricTapSync.Median([4, 1, 2, 3]));
        Assert.Null(LyricTapSync.Median([]));
        Assert.Equal(1, LyricTapSync.Nearest([1, 5, 9], 6.9));
        Assert.Equal(2, LyricTapSync.Nearest([1, 5, 9], 7.1));
        Assert.Equal(0, LyricTapSync.Nearest([1, 5, 9], -3));
        Assert.Equal(2, LyricTapSync.Nearest([1, 5, 9], 30));
        Assert.Null(LyricTapSync.Nearest([], 1));
        Assert.Equal(10.3, LyricTapSync.TapTime(10, 0.5, 0.2), Tol);
        static LyricLine Line(double start, string text, bool instrumental) =>
            new() { Start = start, End = start + 1, Text = text, IsInstrumental = instrumental };
        Assert.Equal([1.0, 7], LyricTapSync.LineStarts([Line(1, "Hello", false), Line(3, "", false), Line(5, "♪", true), Line(7, "Again", false)]));
    }

    // ---- Session ----

    // Ported from LyricTapSyncTests.swift: testSessionAppliesOnceTapsAgreeAndTimesOut
    [Fact]
    public void SessionAppliesOnceTapsAgreeAndTimesOut()
    {
        var session = new LyricTapSession();
        session.Start(Starts, 0.1, playing: true, now: 100);
        Assert.True(session.IsListening);
        Assert.Equal(100 + LyricTapSync.FirstTapLimit, session.Deadline);
        Assert.Contains("Esc", session.Message);
        for (var index = 0; index < 3; index++)
            Assert.Null(session.Tap(Starts[index] + 0.4, 101 + index));
        Assert.Equal(103 + LyricTapSync.NextTapLimit, session.Deadline);
        Assert.Contains("3 tapped, 1 more", session.Message);
        var estimate = session.Tap(Starts[3] + 0.4, 104);
        Assert.Equal(-0.4, estimate!.Value.Offset, Tol);
        Assert.Equal(new TapSyncPhase.Applied(-0.4, 0.1, 4, 4), session.Phase);
        Assert.Contains("−0.40 s", session.Message);
        Assert.Null(session.Tap(50, 105)); // no taps once applied

        var waiting = new LyricTapSession();
        waiting.Start(Starts, 0, true, 0);
        Assert.False(waiting.Expire(LyricTapSync.FirstTapLimit - 1));
        Assert.True(waiting.Expire(LyricTapSync.FirstTapLimit));
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.TimedOut), waiting.Phase);
        waiting.Dismiss();
        Assert.Equal(TapSyncPhase.Idle, waiting.Phase);
    }

    // Ported from LyricTapSyncTests.swift: testSessionRefusesWithoutASongOrLyricsAndGivesUpOnDisagreement
    [Fact]
    public void SessionRefusesWithoutASongOrLyricsAndGivesUpOnDisagreement()
    {
        var session = new LyricTapSession();
        session.Start(Starts, 0, playing: false, now: 0);
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.NotPlaying), session.Phase);
        session.Start([], 0, true, 0);
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.NoLyrics), session.Phase);

        session.Start(Starts, 0, true, 0);
        // Taps far quicker than the lines: no more than two can ever mark lines.
        for (var index = 0; index < LyricTapSync.MaximumTaps; index++) session.Tap(20 + 0.5 * index, index);
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.NoAgreement), session.Phase);
        Assert.Contains("unchanged", session.Message);

        session.Start(Starts, 0, true, 0);
        session.Cancel();
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.Cancelled), session.Phase);
    }

    // Ported from LyricTapSyncTests.swift: testTheMessageSaysHowManyMoreTapsAreAllowed
    [Fact]
    public void TheMessageSaysHowManyMoreTapsAreAllowed()
    {
        var session = new LyricTapSession();
        session.Start(Starts, 0, true, 0);
        for (var index = 0; index < LyricTapSync.RequiredTaps; index++) session.Tap(500 + index * 0.5, index);
        Assert.True(session.IsListening);
        Assert.EndsWith($"(up to {LyricTapSync.MaximumTaps - LyricTapSync.RequiredTaps} more).", session.Message);
    }

    // Ported from LyricTapSyncTests.swift: testOnlyARefusedStartAsksToPlayASong
    [Fact]
    public void OnlyARefusedStartAsksToPlayASong()
    {
        var session = new LyricTapSession();
        session.Start(Starts, 0, false, 0);
        Assert.Equal("Play a song first: the taps are timed against it.", session.Message);
        session.Start(Starts, 0, true, 0);
        session.Stop(TapSyncFailure.NotPlaying);
        Assert.StartsWith("The song paused", session.Message);
        session.Start(Starts, 0, false, 0);
        Assert.StartsWith("Play a song first", session.Message); // a refused start forgets the last session's lines
        session.Stop(TapSyncFailure.SongChanged);
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.NotPlaying), session.Phase); // only a listening session can be stopped
    }

    // ---- Controller ----

    private sealed class Fake
    {
        public string? Query = Key("Song", "Album", 200);
        public bool Playing = true;
        public double Time;
        public double Clock = 1000;
        public IReadOnlyList<double> Lines = [];
        public readonly List<string> Notes = [];

        public LyricTapContext Context => new(() => Query, () => Lines, () => Playing, () => Time, () => 0.25);

        public LyricTapSyncController Controller(SongOffsets offsets) => new(offsets, Context, () => Clock, (text, _) => Notes.Add(text));
    }

    // A stand-in for LyricQuery.RecordingKey.
    private static string Key(string title, string? album, double duration) => $"{title}|Artist|{album}|{duration}";

    // Ported from LyricTapSyncTests.swift: testControllerSavesTheOffsetForTheSourceAndUndoesIt
    // (The menu-bar entries part is Mac UI.)
    [Fact]
    public void ControllerSavesTheOffsetForTheSourceAndUndoesIt()
    {
        var offsets = new SongOffsets { ActiveSource = "LRCLIB" };
        var fake = new Fake { Lines = Starts };
        var query = fake.Query!;
        offsets.Set(0.2, query);
        var controller = fake.Controller(offsets);
        Assert.Equal(LyricTapSyncController.MenuTitle, controller.ActionTitle);
        controller.Toggle();
        Assert.True(controller.IsListening);
        Assert.Equal(LyricTapSyncController.StopTitle, controller.ActionTitle);
        // Taps heard 0.5 s after each line: tap lyric time = playback - 0.15 + 0.25.
        foreach (var start in Starts[3..7])
        {
            fake.Time = start + 0.5 + LyricTapSync.ReactionTime - 0.25;
            fake.Clock += 2;
            controller.Tap();
        }
        Assert.False(controller.IsListening);
        Assert.Equal(-0.5, offsets.OffsetFor(query), Tol);
        Assert.Equal(0, offsets.OffsetFor(query, "Other")); // saved for the lyric source on screen
        Assert.True(controller.CanUndo);
        Assert.Contains("Lyrics synced", fake.Notes[^1]);

        controller.Undo();
        Assert.Equal(0.2, offsets.OffsetFor(query), Tol);
        Assert.False(controller.CanUndo);
        Assert.Contains("undone", fake.Notes[^1]);
    }

    // Ported from LyricTapSyncTests.swift: testUndoIsOnlyOfferedWhileThatOffsetIsInForceForThatSong
    [Fact]
    public void UndoIsOnlyOfferedWhileThatOffsetIsInForceForThatSong()
    {
        var offsets = new SongOffsets();
        var fake = new Fake { Lines = Starts };
        var query = fake.Query!;
        var controller = fake.Controller(offsets);
        controller.Start();
        foreach (var start in Starts[..4])
        {
            fake.Time = start + LyricTapSync.ReactionTime - 0.25 - 0.3;
            controller.Tap();
        }
        Assert.Equal(0.3, offsets.OffsetFor(query), Tol);
        Assert.True(controller.CanUndo);
        offsets.Set(0.4, query);
        Assert.False(controller.CanUndo); // nudged since: undo would throw that away
        offsets.Set(0.3, query);
        fake.Query = Key("Next", null, 180);
        Assert.False(controller.CanUndo); // another song
    }

    // Ported from LyricTapSyncTests.swift: testControllerStopsOnSongChangePauseOrTimeout
    [Fact]
    public void ControllerStopsOnSongChangePauseOrTimeout()
    {
        var offsets = new SongOffsets();
        var fake = new Fake { Lines = Starts, Playing = false };
        var controller = fake.Controller(offsets);
        controller.Start();
        Assert.False(controller.IsListening);
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.NotPlaying), controller.Session.Phase);

        fake.Playing = true;
        fake.Lines = [];
        controller.Start();
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.NoLyrics), controller.Session.Phase);

        fake.Lines = Starts;
        controller.Start();
        fake.Query = Key("Next", null, 180);
        controller.Tap();
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.SongChanged), controller.Session.Phase); // a new song ends it
        Assert.Equal("The song changed, so Tap to sync stopped. The timing is unchanged.", fake.Notes[^1]);

        fake.Query = Key("Song", "Album", 200);
        controller.Start();
        fake.Playing = false;
        controller.Tap();
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.NotPlaying), controller.Session.Phase); // a pause ends it
        Assert.Equal("The song paused, so Tap to sync stopped. The timing is unchanged.", fake.Notes[^1]);
        fake.Playing = true;

        controller.Start();
        fake.Clock += LyricTapSync.FirstTapLimit;
        controller.ExpireIfDue();
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.TimedOut), controller.Session.Phase);
        Assert.Contains("No taps", fake.Notes[^1]);
        Assert.Equal(0, offsets.OffsetFor(fake.Query)); // nothing saved
    }

    // Ported from LyricTapSyncTests.swift: testControllerStopsWhenOtherLyricsReplaceTheOnesBeingTapped
    [Fact]
    public void ControllerStopsWhenOtherLyricsReplaceTheOnesBeingTapped()
    {
        var offsets = new SongOffsets { ActiveSource = "LRCLIB" };
        var fake = new Fake { Lines = Starts };
        var query = fake.Query!;
        var controller = fake.Controller(offsets);
        // A better source arrives mid-session: its lines start elsewhere.
        controller.Start();
        fake.Time = Starts[0] + LyricTapSync.ReactionTime - 0.25;
        controller.Tap();
        offsets.ActiveSource = "Apple Music";
        fake.Lines = Starts.Select(s => s + 1.2).ToArray();
        foreach (var start in fake.Lines.Skip(1).Take(4))
        {
            fake.Time = start + LyricTapSync.ReactionTime - 0.25;
            controller.Tap();
        }
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.LyricsChanged), controller.Session.Phase);
        Assert.StartsWith("Other lyrics loaded", controller.Session.Message);
        Assert.Equal(0, offsets.OffsetFor(query, "LRCLIB")); // nothing saved for the source no longer shown
        Assert.Equal(0, offsets.OffsetFor(query, "Apple Music"));

        // The same source reloading the same lines carries on.
        controller.Start();
        fake.Lines = fake.Lines.ToArray();
        foreach (var start in fake.Lines.Take(4))
        {
            fake.Time = start + LyricTapSync.ReactionTime - 0.25;
            controller.Tap();
        }
        Assert.IsType<TapSyncPhase.Applied>(controller.Session.Phase);
    }

    // Ported from LyricTapSyncTests.swift: testTheResultLooksAndStaysAsItShould (the dismiss delays; symbols and
    // VoiceOver announcements are Mac UI)
    [Fact]
    public void TheResultStaysLongEnoughToUndo()
    {
        Assert.Equal(8, LyricTapSyncController.DismissDelay(new TapSyncPhase.Applied(0.2, 0, 4, 4)));
        Assert.Equal(3, LyricTapSyncController.DismissDelay(new TapSyncPhase.Failed(TapSyncFailure.NoAgreement)));
    }

    [Fact]
    public void NudgesAreFormattedAsOnTheMac()
    {
        Assert.Equal("0.00 s", TimingNudge.Format(0));
        Assert.Equal("+0.25 s", TimingNudge.Format(0.25));
        Assert.Equal("−0.40 s", TimingNudge.Format(-0.4));
        Assert.Equal("Lyrics 0.25 seconds earlier", TimingNudge.Spoken(0.25));
        Assert.Equal("No offset", TimingNudge.Spoken(0));
    }
}
