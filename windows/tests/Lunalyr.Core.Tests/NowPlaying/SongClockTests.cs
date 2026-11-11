using Lunalyr.Core.NowPlaying;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Core.Tests.NowPlaying;

public class SongClockTests
{
    private const double Tol = 0.001; // 1 ms

    private readonly ManualTimeProvider _time = new();
    private readonly List<string> _log = [];
    private readonly List<SeekDetectedEventArgs> _seeks = [];

    private SongClock NewClock(SongClockOptions? options = null)
    {
        var clock = new SongClock(_time, options, _log.Add);
        clock.SeekDetected += (_, e) => _seeks.Add(e);
        return clock;
    }

    // A timeline snapshot taken `ageSeconds` ago (wall clock), for a song of `duration` seconds (null = unknown).
    private MediaTimeline Snap(double position, double ageSeconds = 0, double? duration = 240, double start = 0) =>
        new(TimeSpan.FromSeconds(start),
            duration is { } d ? TimeSpan.FromSeconds(start + d) : TimeSpan.Zero,
            TimeSpan.FromSeconds(start + position),
            _time.UtcNow - TimeSpan.FromSeconds(ageSeconds),
            TimeSpan.Zero,
            duration is { } d2 ? TimeSpan.FromSeconds(start + d2) : TimeSpan.Zero);

    private SongClock PlayingAt(double position, SongClockOptions? options = null, double? duration = 240)
    {
        var clock = NewClock(options);
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        clock.UpdateTimeline(Snap(position, duration: duration));
        return clock;
    }

    private static double Sec(SongClock clock) => clock.Position.TotalSeconds;

    // ---- Basics ---------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutAnyTimelineThePositionIsZeroWithNoConfidence()
    {
        var clock = NewClock();
        var r = clock.Read();
        Assert.Equal(TimeSpan.Zero, r.Position);
        Assert.False(r.HasTimeline);
        Assert.False(r.IsPlaying);
        Assert.Equal(0.0, r.Confidence);
        Assert.Equal(TimelineQuality.Unknown, r.Quality);
        Assert.Null(r.Duration);
    }

    [Fact]
    public void ExtrapolatesWhilePlaying()
    {
        var clock = PlayingAt(10);
        Assert.Equal(10, Sec(clock), Tol);
        _time.AdvanceSeconds(2.5);
        Assert.Equal(12.5, Sec(clock), Tol);
        _time.AdvanceMilliseconds(16.7);
        Assert.Equal(12.5167, Sec(clock), Tol);
        Assert.True(clock.IsPlaying);
    }

    [Fact]
    public void ExtrapolatesFromLastUpdatedTimeNotFromArrival()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        clock.UpdateTimeline(Snap(10, ageSeconds: 3));
        Assert.Equal(13, Sec(clock), Tol);
        _time.AdvanceSeconds(1);
        Assert.Equal(14, Sec(clock), Tol);
    }

    [Fact]
    public void SnapshotAgeIsIgnoredWhilePaused()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        clock.UpdateTimeline(Snap(42, ageSeconds: 30));
        Assert.Equal(42, Sec(clock), Tol);
        _time.AdvanceSeconds(10);
        Assert.Equal(42, Sec(clock), Tol);
        Assert.False(clock.IsPlaying);
        Assert.Equal(1.0, clock.Confidence);
    }

    [Theory]
    [InlineData(PlaybackStatus.Stopped)]
    [InlineData(PlaybackStatus.Changing)]
    [InlineData(PlaybackStatus.Opened)]
    [InlineData(PlaybackStatus.Closed)]
    public void OnlyPlayingAdvancesTheClock(PlaybackStatus status)
    {
        var clock = NewClock();
        clock.UpdatePlayback(status, 1.0);
        clock.UpdateTimeline(Snap(20));
        _time.AdvanceSeconds(5);
        Assert.Equal(20, Sec(clock), Tol);
        Assert.Equal(status, clock.Status);
    }

    [Fact]
    public void WallClockChangesAfterIngestionDoNotMoveThePosition()
    {
        var clock = PlayingAt(10);
        _time.ShiftWallClock(TimeSpan.FromHours(-1));
        _time.AdvanceSeconds(2);
        Assert.Equal(12, Sec(clock), Tol);
    }

    // ---- Pause / resume -----------------------------------------------------------------------------------------

    [Fact]
    public void PauseFreezesAndResumeContinuesFromTheFrozenPosition()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(5);
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        Assert.Equal(15, Sec(clock), Tol);
        _time.AdvanceSeconds(10);
        Assert.Equal(15, Sec(clock), Tol);
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        _time.AdvanceSeconds(2);
        Assert.Equal(17, Sec(clock), Tol);
    }

    [Fact]
    public void ResumeBeforeTheTimelineUpdateDoesNotJumpByThePauseLength()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        var playing = Snap(10);
        clock.UpdateTimeline(playing);
        _time.AdvanceSeconds(5);
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        var paused = Snap(15);
        clock.UpdateTimeline(paused);
        _time.AdvanceSeconds(120);

        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        // The player re-reports the pause-time snapshot (two minutes old) after resuming: it's a duplicate.
        Assert.False(clock.UpdateTimeline(paused));
        _time.AdvanceSeconds(1);
        Assert.Equal(16, Sec(clock), Tol);
        Assert.Empty(_seeks);
    }

    [Fact]
    public void PauseSnapshotArrivingBeforeThePausedStatusIsHandled()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(5);
        clock.UpdateTimeline(Snap(15.05)); // Timeline first (still "playing"), status next.
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        _time.AdvanceSeconds(3);
        var shown = Sec(clock);
        Assert.InRange(shown, 15.0, 15.05);
        _time.AdvanceSeconds(3);
        Assert.Equal(shown, Sec(clock), Tol);
    }

    // ---- Rate -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ExtrapolatesAtOneAndAHalfTimes()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.5);
        clock.UpdateTimeline(Snap(10));
        _time.AdvanceSeconds(4);
        Assert.Equal(16, Sec(clock), Tol);
        Assert.Equal(1.5, clock.Read().Rate);
    }

    [Fact]
    public void SnapshotAgeIsScaledByTheRate()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 2.0);
        clock.UpdateTimeline(Snap(10, ageSeconds: 2));
        Assert.Equal(14, Sec(clock), Tol);
    }

    [Fact]
    public void RateChangeMidPlaybackRebases()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(2);
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.5);
        Assert.Equal(12, Sec(clock), Tol);
        _time.AdvanceSeconds(2);
        Assert.Equal(15, Sec(clock), Tol);
        clock.UpdatePlayback(PlaybackStatus.Playing, 0.5);
        _time.AdvanceSeconds(2);
        Assert.Equal(16, Sec(clock), Tol);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MissingOrInvalidRateMeansNormalSpeed(double? rate)
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, rate);
        clock.UpdateTimeline(Snap(10));
        _time.AdvanceSeconds(3);
        Assert.Equal(13, Sec(clock), Tol);
        Assert.Equal(1.0, clock.Read().Rate);
        if (rate is not null) Assert.Contains(_log, l => l.Contains("rate"));
        else Assert.Empty(_log);
    }

    [Fact]
    public void AbsurdRatesAreClamped()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 1000);
        Assert.Equal(16.0, clock.Read().Rate);
        clock.UpdatePlayback(PlaybackStatus.Playing, 0.001);
        Assert.Equal(0.1, clock.Read().Rate);
    }

    // ---- Seeks ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ForwardSeekJumpsAndRaisesOneEvent()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(5);
        clock.UpdateTimeline(Snap(60));
        Assert.Equal(60, Sec(clock), Tol);
        var seek = Assert.Single(_seeks);
        Assert.Equal(15, seek.From.TotalSeconds, Tol);
        Assert.Equal(60, seek.To.TotalSeconds, Tol);
        Assert.False(seek.IsBackward);

        _time.AdvanceSeconds(1);
        clock.UpdateTimeline(Snap(61));
        Assert.Single(_seeks);
        Assert.Equal(61, Sec(clock), Tol);
    }

    [Fact]
    public void BackwardSeekJumpsBackImmediately()
    {
        var clock = PlayingAt(100);
        _time.AdvanceSeconds(5);
        clock.UpdateTimeline(Snap(20));
        Assert.Equal(20, Sec(clock), Tol);
        var seek = Assert.Single(_seeks);
        Assert.True(seek.IsBackward);
        Assert.Equal(105, seek.From.TotalSeconds, Tol);
    }

    [Fact]
    public void SeekWhilePausedJumpsAndStaysFrozen()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        clock.UpdateTimeline(Snap(30));
        _time.AdvanceSeconds(2);
        clock.UpdateTimeline(Snap(90));
        Assert.Equal(90, Sec(clock), Tol);
        Assert.Single(_seeks);
        _time.AdvanceSeconds(2);
        Assert.Equal(90, Sec(clock), Tol);
    }

    [Fact]
    public void SeekUsesTheSnapshotsAgeToo()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(5);
        clock.UpdateTimeline(Snap(60, ageSeconds: 0.5)); // Seek reported half a second late.
        Assert.Equal(60.5, Sec(clock), Tol);
        Assert.Equal(60.5, Assert.Single(_seeks).To.TotalSeconds, Tol);
    }

    [Fact]
    public void JustUnderTheThresholdIsDriftJustOverIsASeek()
    {
        var clock = PlayingAt(10);
        clock.UpdateTimeline(Snap(11.49, ageSeconds: 0));
        Assert.Empty(_seeks);
        _time.AdvanceSeconds(1); // Let the slew finish.
        clock.UpdateTimeline(Snap(12.49 + 1.51));
        Assert.Single(_seeks);
    }

    [Fact]
    public void SeekThresholdIsConfigurable()
    {
        var clock = PlayingAt(10, new SongClockOptions { SeekThreshold = TimeSpan.FromSeconds(3) });
        clock.UpdateTimeline(Snap(12));
        Assert.Empty(_seeks);
        Assert.Equal(10, Sec(clock), Tol); // Slewing, not jumping.
        _time.AdvanceSeconds(1);
        clock.UpdateTimeline(Snap(20));
        Assert.Single(_seeks);
    }

    [Fact]
    public void RepeatOneLoopBackToTheStartIsABackwardJump()
    {
        var clock = PlayingAt(238, duration: 240);
        _time.AdvanceSeconds(3);
        Assert.Equal(240, Sec(clock), Tol); // Held at the end.
        clock.UpdateTimeline(Snap(0.5, duration: 240));
        Assert.Equal(0.5, Sec(clock), Tol);
        Assert.True(Assert.Single(_seeks).IsBackward);
    }

    [Fact]
    public void SeekHandlerSeesTheNewPosition()
    {
        var clock = PlayingAt(10);
        double seen = -1;
        clock.SeekDetected += (_, _) => seen = clock.Position.TotalSeconds;
        clock.UpdateTimeline(Snap(100));
        Assert.Equal(100, seen, Tol);
    }

    // ---- Drift slew -----------------------------------------------------------------------------------------------

    [Fact]
    public void ForwardDriftIsSlewedOverTheSlewDuration()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(5);
        clock.UpdateTimeline(Snap(15.3)); // We're 300 ms behind.
        Assert.Equal(15.0, Sec(clock), Tol); // No visible jump.
        _time.AdvanceMilliseconds(200);
        Assert.Equal(15.5 - 0.15, Sec(clock), Tol); // Halfway through the 400 ms slew.
        _time.AdvanceMilliseconds(200);
        Assert.Equal(15.7, Sec(clock), Tol);
        _time.AdvanceSeconds(1);
        Assert.Equal(16.7, Sec(clock), Tol);
        Assert.Empty(_seeks);
    }

    [Theory]
    [InlineData(0.1, 1.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(1.4, 1.0)]
    [InlineData(1.4, 1.5)]
    [InlineData(1.0, 0.5)]
    public void BackwardDriftNeverRunsTimeBackwards(double ahead, double rate)
    {
        var options = SongClockOptions.Default;
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, rate);
        clock.UpdateTimeline(Snap(50));
        _time.AdvanceSeconds(2);
        var before = Sec(clock);
        clock.UpdateTimeline(Snap(50 + (2 * rate) - ahead)); // Player says we're `ahead` seconds ahead.
        Assert.Equal(before, Sec(clock), Tol);

        var expectedSlew = Math.Max(options.SlewDuration.TotalSeconds, ahead / (rate * options.MaxBackwardSlewFraction));
        var minSpeed = rate * (1 - options.MaxBackwardSlewFraction);
        var previous = Sec(clock);
        const int stepMs = 5;
        var steps = (int)Math.Ceiling((expectedSlew + 0.5) * 1000 / stepMs);
        for (var i = 1; i <= steps; i++)
        {
            _time.AdvanceMilliseconds(stepMs);
            var now = Sec(clock);
            Assert.True(now - previous >= (minSpeed * stepMs / 1000) - 1e-6, $"At step {i} the clock moved {now - previous:0.000000}s.");
            previous = now;
        }

        // Converged on the player's position.
        var truth = 50 + (2 * rate) - ahead + (steps * stepMs / 1000.0 * rate);
        Assert.Equal(truth, Sec(clock), Tol);
        Assert.Empty(_seeks);
    }

    [Fact]
    public void LargeBackwardDriftTakesLongerThanTheSlewDuration()
    {
        var clock = PlayingAt(50);
        _time.AdvanceSeconds(2);
        clock.UpdateTimeline(Snap(50.6)); // 1.4 s ahead.
        _time.AdvanceMilliseconds(400);
        Assert.True(Sec(clock) > 51.0 + 0.1, "Still correcting after 400 ms, not snapped.");
        _time.AdvanceMilliseconds(1500); // 1.4 / 0.75 = 1.867 s in total.
        Assert.Equal(50.6 + 1.9, Sec(clock), Tol);
    }

    [Fact]
    public void PauseDuringSlewFreezesAndResumeFinishesTheSlew()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(5);
        clock.UpdateTimeline(Snap(15.4)); // 400 ms behind.
        _time.AdvanceMilliseconds(100);
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        var frozen = Sec(clock);
        Assert.Equal(15.5 - 0.3, frozen, Tol);
        _time.AdvanceSeconds(30);
        Assert.Equal(frozen, Sec(clock), Tol);
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        Assert.Equal(frozen, Sec(clock), Tol);
        _time.AdvanceMilliseconds(300); // The remaining 300 ms of the slew.
        Assert.Equal(15.8, Sec(clock), Tol);
        _time.AdvanceSeconds(1);
        Assert.Equal(16.8, Sec(clock), Tol);
    }

    [Fact]
    public void RateChangeDuringBackwardSlewStaysMonotonic()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(2);
        clock.UpdateTimeline(Snap(11)); // 1 s ahead.
        _time.AdvanceMilliseconds(100);
        clock.UpdatePlayback(PlaybackStatus.Playing, 0.5);
        var previous = Sec(clock);
        for (var i = 0; i < 600; i++)
        {
            _time.AdvanceMilliseconds(5);
            var now = Sec(clock);
            Assert.True(now >= previous - 1e-9);
            previous = now;
        }
    }

    [Fact]
    public void SmallDriftWhilePausedIsAppliedDirectly()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        clock.UpdateTimeline(Snap(30));
        clock.UpdateTimeline(Snap(30.4));
        Assert.Equal(30.4, Sec(clock), Tol);
        Assert.Empty(_seeks);
    }

    [Fact]
    public void ANewSnapshotDuringASlewStartsFromWhatIsShown()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(1);
        clock.UpdateTimeline(Snap(11.3));
        _time.AdvanceMilliseconds(100);
        var shown = Sec(clock);
        clock.UpdateTimeline(Snap(11.2)); // Contradicts the last one; still drift-sized.
        Assert.Equal(shown, Sec(clock), Tol);
        Assert.Empty(_seeks);
    }

    // ---- Timeline quality -----------------------------------------------------------------------------------------

    [Fact]
    public void SilentPlayerWhilePlayingBecomesSeekOnlyAndKeepsExtrapolating()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(5.9);
        Assert.Equal(TimelineQuality.Unknown, clock.Quality);
        _time.AdvanceSeconds(0.1);
        Assert.Equal(TimelineQuality.SeekOnly, clock.Quality);
        Assert.Equal(0.85, clock.Confidence, Tol);

        _time.AdvanceSeconds(120);
        var r = clock.Read();
        Assert.True(r.IsPlaying);
        Assert.Equal(136, r.Position.TotalSeconds, Tol);
        Assert.Equal(TimelineQuality.SeekOnly, r.Quality);
    }

    [Fact]
    public void ALaterMatchingUpdateConfirmsSeekOnly()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(30);
        Assert.Equal(TimelineQuality.SeekOnly, clock.Quality);
        clock.UpdateTimeline(Snap(40.1)); // E.g. the pause snapshot: matches our extrapolation.
        Assert.Equal(TimelineQuality.SeekOnly, clock.Quality);
        Assert.Equal(0.95, clock.Confidence, Tol);
        Assert.Contains(_log, l => l.Contains("SeekOnly"));
    }

    [Fact]
    public void SeekOnlyIsNotDeclaredWhilePaused()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        clock.UpdateTimeline(Snap(10));
        _time.AdvanceSeconds(60);
        Assert.Equal(TimelineQuality.Unknown, clock.Quality);
    }

    [Fact]
    public void StatusChangesRestartTheSilenceTimer()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(4);
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        _time.AdvanceSeconds(10);
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        _time.AdvanceSeconds(4);
        Assert.Equal(TimelineQuality.Unknown, clock.Quality);
        _time.AdvanceSeconds(2);
        Assert.Equal(TimelineQuality.SeekOnly, clock.Quality);
    }

    [Fact]
    public void DuplicateSnapshotsDoNotCountAsUpdates()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        var s = Snap(10);
        clock.UpdateTimeline(s);
        _time.AdvanceSeconds(3);
        Assert.False(clock.UpdateTimeline(s));
        Assert.Equal(13, Sec(clock), Tol); // Not re-anchored to the old snapshot's position.
        _time.AdvanceSeconds(3);
        Assert.Equal(TimelineQuality.SeekOnly, clock.Quality);
    }

    [Fact]
    public void PeriodicFreshUpdatesMakeThePlayerLive()
    {
        var clock = PlayingAt(10);
        for (var i = 1; i <= 2; i++)
        {
            Assert.Equal(TimelineQuality.Unknown, clock.Quality);
            _time.AdvanceSeconds(1);
            clock.UpdateTimeline(Snap(10 + i + 0.02));
        }

        Assert.Equal(TimelineQuality.Live, clock.Quality);
        Assert.Equal(1.0, clock.Confidence);
    }

    [Fact]
    public void LivePlayerThatGoesQuietStaysLiveWithLowerConfidence()
    {
        var clock = PlayingAt(10);
        for (var i = 1; i <= 3; i++)
        {
            _time.AdvanceSeconds(1);
            clock.UpdateTimeline(Snap(10 + i));
        }

        Assert.Equal(TimelineQuality.Live, clock.Quality);
        _time.AdvanceSeconds(9);
        Assert.Equal(1.0, clock.Confidence);
        _time.AdvanceSeconds(2);
        Assert.Equal(TimelineQuality.Live, clock.Quality);
        Assert.Equal(0.6, clock.Confidence, Tol);
        Assert.Equal(24, Sec(clock), Tol); // Still extrapolating.
    }

    [Fact]
    public void UpdatesRightAfterStatusChangesAreNotLiveEvidence()
    {
        // A seek-only player: every update comes with a pause or resume.
        var clock = NewClock();
        var position = 10.0;
        for (var round = 0; round < 5; round++)
        {
            clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
            clock.UpdateTimeline(Snap(position));
            _time.AdvanceSeconds(8);
            position += 8;
            clock.UpdateTimeline(Snap(position)); // Pause snapshot first...
            clock.UpdatePlayback(PlaybackStatus.Paused, 1.0); // ...then the status.
            _time.AdvanceSeconds(2);
        }

        Assert.Equal(TimelineQuality.SeekOnly, clock.Quality);
    }

    [Fact]
    public void OldSnapshotsAreNotLiveEvidence()
    {
        var clock = PlayingAt(10);
        for (var i = 1; i <= 4; i++)
        {
            _time.AdvanceSeconds(1);
            clock.UpdateTimeline(Snap(10 + i - 3, ageSeconds: 3)); // Re-emitted with a 3 s old timestamp.
        }

        Assert.NotEqual(TimelineQuality.Live, clock.Quality);
    }

    [Fact]
    public void SeekOnlyPlayerCanLaterProveLive()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(10);
        Assert.Equal(TimelineQuality.SeekOnly, clock.Quality);
        for (var i = 1; i <= 3; i++)
        {
            _time.AdvanceSeconds(1);
            clock.UpdateTimeline(Snap(20 + i));
        }

        Assert.Equal(TimelineQuality.Live, clock.Quality);
    }

    [Fact]
    public void SeeksAreNotLiveEvidence()
    {
        var clock = PlayingAt(10);
        for (var i = 1; i <= 4; i++)
        {
            _time.AdvanceSeconds(1);
            clock.UpdateTimeline(Snap(i * 30));
        }

        Assert.Equal(4, _seeks.Count);
        Assert.Equal(TimelineQuality.Unknown, clock.Quality);
    }

    // ---- Track change ---------------------------------------------------------------------------------------------

    [Fact]
    public void TrackChangeResetsToZeroAndRunsFromThereWhilePlaying()
    {
        var clock = PlayingAt(100);
        _time.AdvanceSeconds(1);
        clock.ResetTrack();
        var r = clock.Read();
        Assert.Equal(TimeSpan.Zero, r.Position);
        Assert.False(r.HasTimeline);
        Assert.Null(r.Duration);
        Assert.Equal(0.25, r.Confidence);
        _time.AdvanceSeconds(2);
        Assert.Equal(2, Sec(clock), Tol);
        Assert.Empty(_seeks);
    }

    [Fact]
    public void NewTracksFirstSnapshotSeedsWithoutASeek()
    {
        var clock = PlayingAt(100);
        clock.ResetTrack();
        _time.AdvanceSeconds(0.2);
        clock.UpdateTimeline(Snap(0.2, duration: 180));
        Assert.Equal(0.2, Sec(clock), Tol);
        Assert.Equal(180, clock.Read().Duration!.Value.TotalSeconds, Tol);
        Assert.Empty(_seeks);
    }

    [Fact]
    public void ThePreviousTracksTimelineIsNotReusedAfterAReset()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        var old = Snap(200, duration: 240);
        clock.UpdateTimeline(old);
        _time.AdvanceSeconds(1);
        clock.ResetTrack();
        Assert.False(clock.UpdateTimeline(old)); // Re-read right after the metadata change: still the old track's.
        _time.AdvanceSeconds(1);
        Assert.Equal(1, Sec(clock), Tol);
        Assert.False(clock.Read().HasTimeline);
    }

    [Fact]
    public void TrackChangeKeepsTheLearnedQuality()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(10);
        Assert.Equal(TimelineQuality.SeekOnly, clock.Quality);
        clock.ResetTrack();
        clock.UpdateTimeline(Snap(0));
        Assert.Equal(TimelineQuality.SeekOnly, clock.Quality);
    }

    [Fact]
    public void NewTracksTimelineArrivingBeforeItsMetadataIsNotASeek()
    {
        var clock = PlayingAt(200, duration: 240);
        _time.AdvanceSeconds(1);
        clock.UpdateTimeline(Snap(0.1, duration: 180)); // Different duration: a different track.
        Assert.Empty(_seeks);
        Assert.Equal(0.1, Sec(clock), Tol);
        Assert.Equal(180, clock.Read().Duration!.Value.TotalSeconds, Tol);
        Assert.Contains(_log, l => l.Contains("duration changed"));
    }

    [Theory]
    [InlineData(180)] // New duration: re-synced without a seek.
    [InlineData(240)] // Same duration: looks like a backward seek on the old track until the metadata arrives.
    public void TimelineThenMetadataKeepsTheNewTracksTimeline(double newDuration)
    {
        var clock = PlayingAt(200, duration: 240);
        _time.AdvanceSeconds(1);
        var first = Snap(0.05, duration: newDuration);
        clock.UpdateTimeline(first);
        _time.AdvanceMilliseconds(300);
        clock.ResetTrack(); // The metadata change lands after the timeline.
        var r = clock.Read();
        Assert.True(r.HasTimeline);
        Assert.Equal(0.35, r.Position.TotalSeconds, Tol);
        Assert.False(clock.UpdateTimeline(first)); // The monitor's re-read is a duplicate.
        _time.AdvanceSeconds(1);
        Assert.Equal(1.35, Sec(clock), Tol);
        Assert.Equal(newDuration == 240 ? 1 : 0, _seeks.Count);
    }

    [Fact]
    public void MetadataThenTimelineSeedsFromTheNewTimeline()
    {
        var clock = PlayingAt(200, duration: 240);
        _time.AdvanceSeconds(1);
        var old = Snap(201, duration: 240);
        clock.UpdateTimeline(old);
        clock.ResetTrack();
        clock.UpdateTimeline(old); // Monitor re-reads: still the old track's.
        _time.AdvanceMilliseconds(200);
        clock.UpdateTimeline(Snap(0.1, duration: 180));
        Assert.Equal(0.1, Sec(clock), Tol);
        Assert.Empty(_seeks);
    }

    [Fact]
    public void MetadataArrivingInStagesKeepsTheTimelineReceivedInBetween()
    {
        var clock = PlayingAt(200, duration: 240);
        clock.ResetTrack();                              // Stage 1: e.g. the tab title.
        _time.AdvanceMilliseconds(200);
        var b = Snap(0.1, duration: 180);
        clock.UpdateTimeline(b);
        _time.AdvanceSeconds(2.5);
        clock.ResetTrack();                              // Stage 2: the real title and artist.
        Assert.False(clock.UpdateTimeline(b));           // The monitor's re-read is a duplicate, not the "old track".
        Assert.Equal(2.6, Sec(clock), Tol);
        Assert.True(clock.Read().HasTimeline);
        Assert.Empty(_seeks);

        _time.AdvanceSeconds(1);
        clock.UpdateTimeline(Snap(3.6, duration: 180));  // Live updates carry on without a seek.
        Assert.Empty(_seeks);
    }

    [Fact]
    public void AMetadataChangeLongAfterTheLastTrackChangeIsANewTrack()
    {
        var clock = PlayingAt(200, duration: 240);
        clock.ResetTrack();
        _time.AdvanceMilliseconds(200);
        clock.UpdateTimeline(Snap(0.1, duration: 180));
        _time.AdvanceSeconds(6); // Past MetadataSettleTime.
        clock.ResetTrack();
        Assert.False(clock.Read().HasTimeline);
        Assert.Equal(0, Sec(clock), Tol);
    }

    [Fact]
    public void NewTracksTimelineNeverLeavesThePositionPastTheNewDuration()
    {
        var clock = PlayingAt(200, duration: 240);
        clock.UpdateTimeline(Snap(200.2, duration: 120)); // Odd player: new duration, old position.
        Assert.Equal(120, Sec(clock), Tol);
        _time.AdvanceSeconds(5);
        Assert.Equal(120, Sec(clock), Tol);
    }

    // ---- Timestamps -----------------------------------------------------------------------------------------------

    [Fact]
    public void SlightlyFutureTimestampIsTreatedAsNowWithoutComment()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        clock.UpdateTimeline(Snap(10, ageSeconds: -0.2));
        Assert.Equal(10, Sec(clock), Tol);
        Assert.Empty(_log);
        Assert.NotEqual(0.5, clock.Confidence);
    }

    [Fact]
    public void FarFutureTimestampIsClampedLoggedAndLowersConfidence()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        clock.UpdateTimeline(Snap(10, ageSeconds: -3600));
        Assert.Equal(10, Sec(clock), Tol);
        Assert.Contains(_log, l => l.Contains("future"));
        Assert.Equal(0.5, clock.Confidence);
        _time.AdvanceSeconds(1);
        Assert.Equal(11, Sec(clock), Tol);

        clock.UpdateTimeline(Snap(11.05)); // A sane snapshot restores confidence.
        Assert.NotEqual(0.5, clock.Confidence);
    }

    [Fact]
    public void DefaultTimestampWithUnknownDurationDoesNotOverflow()
    {
        // What a player with no timeline reports: 1601-01-01 (FILETIME 0), EndTime 0.
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 16);
        var bogus = new MediaTimeline(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5),
            new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero));
        clock.UpdateTimeline(bogus);
        var r = clock.Read();
        Assert.Equal(5, r.Position.TotalSeconds, Tol);
        Assert.Null(r.Duration);
        Assert.Equal(0.5, r.Confidence);
        Assert.Contains(_log, l => l.Contains("implausibly old"));

        clock.UpdateTimeline(new MediaTimeline(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue));
        clock.UpdateTimeline(new MediaTimeline(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MaxValue));
        Assert.Equal(0, Sec(clock), Tol);
    }

    [Fact]
    public void MinutesOldTimestampOnAttachIsTrusted()
    {
        // Attaching to a seek-only player mid-song: its last update was the resume two minutes ago.
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        clock.UpdateTimeline(Snap(30, ageSeconds: 120, duration: 240));
        Assert.Equal(150, Sec(clock), Tol);
        Assert.NotEqual(0.5, clock.Confidence);
        Assert.Empty(_log);
    }

    [Fact]
    public void StaleTimestampThatRunsPastTheEndIsClampedToTheDuration()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        clock.UpdateTimeline(Snap(30, ageSeconds: 600, duration: 240));
        Assert.Equal(240, Sec(clock), Tol);
        Assert.Contains(_log, l => l.Contains("past the end"));
    }

    // ---- Duration -------------------------------------------------------------------------------------------------

    [Fact]
    public void UnknownDurationNeverClampsTheEnd()
    {
        var clock = PlayingAt(10, duration: null);
        Assert.Null(clock.Read().Duration);
        _time.AdvanceSeconds(3600);
        Assert.Equal(3610, Sec(clock), Tol);
    }

    [Fact]
    public void PositionPastTheDurationIsClamped()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        clock.UpdateTimeline(Snap(300, duration: 240));
        Assert.Equal(240, Sec(clock), Tol);
        Assert.Contains(_log, l => l.Contains("past the duration"));
    }

    [Fact]
    public void PlayingStopsAtTheEndOfTheSong()
    {
        var clock = PlayingAt(235, duration: 240);
        _time.AdvanceSeconds(10);
        Assert.Equal(240, Sec(clock), Tol);
    }

    [Fact]
    public void PositionIsRelativeToStartTime()
    {
        var clock = NewClock();
        clock.UpdatePlayback(PlaybackStatus.Paused, 1.0);
        clock.UpdateTimeline(Snap(15, duration: 200, start: 100));
        var r = clock.Read();
        Assert.Equal(15, r.Position.TotalSeconds, Tol);
        Assert.Equal(200, r.Duration!.Value.TotalSeconds, Tol);
    }

    // ---- Session --------------------------------------------------------------------------------------------------

    [Fact]
    public void SessionResetForgetsEverything()
    {
        var clock = PlayingAt(10);
        _time.AdvanceSeconds(10);
        Assert.Equal(TimelineQuality.SeekOnly, clock.Quality);
        clock.ResetSession();
        var r = clock.Read();
        Assert.Equal(TimelineQuality.Unknown, r.Quality);
        Assert.False(r.IsPlaying);
        Assert.False(r.HasTimeline);
        Assert.Equal(TimeSpan.Zero, r.Position);
        Assert.Equal(PlaybackStatus.Closed, clock.Status);
        _time.AdvanceSeconds(10);
        Assert.Equal(TimeSpan.Zero, clock.Position);
    }

    // ---- Options ---------------------------------------------------------------------------------------------------

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SongClock(_time, new SongClockOptions { SeekThreshold = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SongClock(_time, new SongClockOptions { MaxBackwardSlewFraction = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SongClock(_time, new SongClockOptions { LiveEvidenceRequired = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SongClock(_time, new SongClockOptions { FutureTolerance = TimeSpan.FromSeconds(-1) }));
    }

    [Fact]
    public async Task ReadsAreSafeAlongsideUpdatesFromOtherThreads()
    {
        var clock = PlayingAt(10);
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested || reads == 0)
            {
                var r = clock.Read();
                Assert.InRange(r.Position, TimeSpan.Zero, TimeSpan.FromSeconds(240));
                reads++;
            }

            return reads;
        }, TestContext.Current.CancellationToken);

        for (var i = 0; i < 2000; i++)
        {
            _time.AdvanceMilliseconds(10);
            clock.UpdateTimeline(Snap(10 + (i * 0.01) + ((i % 7) * 0.01)));
            if (i % 100 == 0) clock.UpdatePlayback(i % 200 == 0 ? PlaybackStatus.Paused : PlaybackStatus.Playing, 1.0);
        }

        await stop.CancelAsync();
        Assert.True(await reader > 0);
    }
}
