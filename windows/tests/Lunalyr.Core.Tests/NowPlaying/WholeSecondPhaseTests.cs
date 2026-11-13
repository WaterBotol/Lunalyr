using Lunalyr.Core.NowPlaying;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Core.Tests.NowPlaying;

/// <summary>
/// Apple Music for Windows as measured live (13 Nov 2026): a snapshot every ~1.024 s, position cut to whole seconds,
/// LastUpdatedTime precise to the millisecond.
/// </summary>
public sealed class WholeSecondPhaseTests
{
    private const long Frequency = TimeSpan.TicksPerSecond;

    private static long Ticks(double seconds) => (long)(seconds * Frequency);

    [Theory]
    [InlineData(89.17)]
    [InlineData(12.93)]
    [InlineData(200.5)]
    public void ARunOfWholeSecondSnapshotsPinsTheTruePositionDown(double truthAtStart)
    {
        var phase = new WholeSecondPhase();
        double worstLate = 0, last = 0;
        for (var i = 0; i < 70; i++)
        {
            var t = i * 1.024;
            var truth = truthAtStart + t;
            var reported = TimeSpan.FromSeconds(Math.Floor(truth));
            var corrected = reported.TotalSeconds + phase.Correct(reported, Ticks(t), Frequency, playing: true, rate: 1).TotalSeconds;
            if (i >= WholeSecondPhase.WholeSnapshotsRequired) worstLate = Math.Max(worstLate, Math.Abs(truth - corrected));
            last = Math.Abs(truth - corrected);
        }
        Assert.True(worstLate <= 0.5 + 1e-9, $"never more than half a second off once on (was {worstLate:0.###})");
        Assert.True(last < 0.03, $"within 30 ms after a minute (was {last:0.###})");
    }

    [Fact]
    public void TheStallAsASongStartsDoesNotPinTheEstimate()
    {
        // As recorded live (17:06:09 and 17:09:57): 0:00 is held for ~1.9 s while the song already plays, then 1 and 2
        // come 0.45 s apart, then the usual 1.024 s cadence. The estimate must recover, not stay pinned.
        var phase = new WholeSecondPhase();
        (double T, int P)[] stall = [(0.0, 0), (0.006, 0), (0.035, 0), (0.231, 0), (1.255, 0), (1.893, 1), (2.342, 2)];
        foreach (var (t, p) in stall) phase.Correct(TimeSpan.FromSeconds(p), Ticks(t), Frequency, true, 1);
        double worstAfter10 = 0, last = 0;
        for (var i = 1; i < 70; i++)
        {
            var t = 2.342 + i * 1.024;
            var truth = t; // the song really started at 0
            var reported = TimeSpan.FromSeconds(Math.Floor(truth));
            var corrected = reported.TotalSeconds + phase.Correct(reported, Ticks(t), Frequency, true, 1).TotalSeconds;
            last = Math.Abs(truth - corrected);
            if (t > 10) worstAfter10 = Math.Max(worstAfter10, last);
        }
        Assert.True(worstAfter10 <= 0.5 + 1e-9, $"never more than half a second off after the stall (was {worstAfter10:0.###})");
        Assert.True(last < 0.03, $"within 30 ms after a minute (was {last:0.###})");
    }

    [Fact]
    public void PrecisePositionsAreLeftAlone()
    {
        var phase = new WholeSecondPhase();
        for (var i = 0; i < 10; i++)
        {
            var reported = TimeSpan.FromSeconds(30.25 + i * 1.0);
            Assert.Equal(TimeSpan.Zero, phase.Correct(reported, Ticks(i), Frequency, playing: true, rate: 1));
        }
        Assert.False(phase.Active);
    }

    [Fact]
    public void ASeekStartsTheEstimateAgain()
    {
        var phase = new WholeSecondPhase();
        for (var i = 0; i < 40; i++)
            phase.Correct(TimeSpan.FromSeconds(Math.Floor(10.6 + i * 1.024)), Ticks(i * 1.024), Frequency, true, 1);
        Assert.True(phase.Uncertainty < 0.1);
        // A jump to 2:00 (true 120.2): the old run doesn't fit, so a new one starts at the middle of its second.
        var after = phase.Correct(TimeSpan.FromSeconds(120), Ticks(41), Frequency, true, 1);
        Assert.Equal(0.5, after.TotalSeconds, 3);
        Assert.Equal(1, phase.Uncertainty!.Value, 3);
    }

    [Fact]
    public void ThePhaseStaysOffUntilThreeWholeSnapshots()
    {
        var phase = new WholeSecondPhase();
        Assert.Equal(TimeSpan.Zero, phase.Correct(TimeSpan.FromSeconds(5), Ticks(0), Frequency, true, 1));
        Assert.Equal(TimeSpan.Zero, phase.Correct(TimeSpan.FromSeconds(6), Ticks(1.024), Frequency, true, 1));
        Assert.NotEqual(TimeSpan.Zero, phase.Correct(TimeSpan.FromSeconds(7), Ticks(2.048), Frequency, true, 1));
    }

    [Fact]
    public void TheSongClockCatchesUpWithAppleMusic()
    {
        var time = new ManualTimeProvider();
        var clock = new SongClock(time) { CorrectsWholeSeconds = true };
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        const double truthAtStart = 89.37;
        double error = 0;
        for (var i = 0; i < 70; i++)
        {
            var truth = truthAtStart + i * 1.024;
            clock.UpdateTimeline(new MediaTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(278), TimeSpan.FromSeconds(Math.Floor(truth)),
                time.UtcNow, TimeSpan.Zero, TimeSpan.FromSeconds(278)));
            time.AdvanceSeconds(0.5);
            error = truth + 0.5 - clock.Position.TotalSeconds;
            time.AdvanceSeconds(0.524);
        }
        Assert.True(Math.Abs(error) < 0.05, $"the shown position is within 50 ms of the music (was {error:0.###} s behind)");
    }

    [Fact]
    public void OtherPlayersAreNotCorrected()
    {
        var time = new ManualTimeProvider();
        var clock = new SongClock(time);
        clock.UpdatePlayback(PlaybackStatus.Playing, 1.0);
        for (var i = 0; i < 10; i++)
        {
            clock.UpdateTimeline(new MediaTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(278), TimeSpan.FromSeconds(30 + i),
                time.UtcNow, TimeSpan.Zero, TimeSpan.FromSeconds(278)));
            time.AdvanceSeconds(1);
        }
        Assert.Equal(40, clock.Position.TotalSeconds, 2);
    }
}
