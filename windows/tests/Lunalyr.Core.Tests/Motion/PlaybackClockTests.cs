using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Motion;

public class PlaybackClockTests
{
    // Ported from PlaybackClockTests.swift: testSmallCorrectionsEaseInWithoutJumping
    [Fact]
    public void SmallCorrectionsEaseInWithoutJumping()
    {
        var clock = new PlaybackClock();
        clock.Sync(10, sampledAt: 0, running: true, now: 0, continuous: false);
        // Two seconds on, the player says 12.12: 120 ms ahead of the extrapolation.
        clock.Sync(12.12, sampledAt: 2, running: true, now: 2, continuous: true);
        Assert.Equal(12, clock.Position(2)!.Value, 1e-9); // No jump at the re-sync
        Assert.True(clock.IsSlewing(2.2));
        const double settled = 2 + PlaybackClock.SlewDuration;
        Assert.Equal(12.12 + PlaybackClock.SlewDuration, clock.Position(settled)!.Value, 1e-9);
        Assert.False(clock.IsSlewing(settled + 0.001));
        Assert.Equal(15.12, clock.Position(5)!.Value, 1e-9);
    }

    // Ported from PlaybackClockTests.swift: testCorrectionsNeverRunBackwardsAndDoNotDependOnFrameRate
    [Fact]
    public void CorrectionsNeverRunBackwardsAndDoNotDependOnFrameRate()
    {
        foreach (var error in new[] { -0.24, -0.1, 0.05, 0.24 })
        {
            var reference = new Dictionary<double, double>();
            foreach (var rate in new[] { 30.0, 60.0, 120.0 })
            {
                var clock = new PlaybackClock();
                clock.Sync(50, 0, true, 0, false);
                clock.Sync(52 + error, 2, true, 2, true);
                var last = clock.Position(2)!.Value;
                for (var frame = 1; frame <= (int)rate; frame++)
                {
                    var position = clock.Position(2 + frame / rate)!.Value;
                    Assert.True(position > last, $"error {error} at {rate} Hz");
                    last = position;
                }
                foreach (var instant in new[] { 2.1, 2.2, 2.3, 2.5 })
                {
                    var position = clock.Position(instant)!.Value;
                    if (reference.TryGetValue(instant, out var expected)) Assert.Equal(expected, position, 1e-12);
                    else reference[instant] = position;
                }
                Assert.Equal(53 + error, clock.Position(3)!.Value, 1e-9); // Lands on the sample
            }
        }
    }

    // Ported from PlaybackClockTests.swift: testSeeksNewTracksAndPausesMoveAtOnce
    [Fact]
    public void SeeksNewTracksAndPausesMoveAtOnce()
    {
        var clock = new PlaybackClock();
        clock.Sync(10, 0, true, 0, false);
        clock.Sync(80, 2, true, 2, true);
        Assert.Equal(80, clock.Position(2)!.Value, 1e-9); // A real seek jumps
        clock.Sync(0.05, 3, true, 3, false);
        Assert.Equal(0.05, clock.Position(3)!.Value, 1e-9); // A new track jumps
        clock.SetRunning(false, 4);
        Assert.Equal(1.05, clock.Position(9)!.Value, 1e-9); // Paused holds
        clock.SetRunning(true, 9);
        Assert.Equal(2.05, clock.Position(10)!.Value, 1e-9); // Resumes where it held
        clock.Reset();
        Assert.Null(clock.Position(10));
    }

    // Ported from PlaybackClockTests.swift: testTheSampleTimeIsHonoured
    [Fact]
    public void TheSampleTimeIsHonoured()
    {
        var clock = new PlaybackClock();
        // Sampled at 100, applied 80 ms later.
        clock.Sync(30, 100, true, 100.08, false);
        Assert.Equal(30.08, clock.Position(100.08)!.Value, 1e-9);
        clock.Sync(31, 101, false, 101.5, true);
        Assert.Equal(31, clock.Position(101.5)!.Value, 1e-9); // Paused samples do not advance
    }
}
