using Lunalyr.Core.Player;

namespace Lunalyr.Core.Tests.Player;

public sealed class ScrubSessionTests
{
    private static readonly double[] Targets = [0.0, 50, 100];

    private static ScrubSession Session(double x, double knob, double y = 0, double now = 0, bool noSnap = false) =>
        new(x, y, knob, 200, "a", Targets, now, width: 456, noSnap: noSnap);

    // Ported from DesktopScrubberTests.swift: testPressOnTheKnobKeepsTheGrabOffset
    [Fact]
    public void PressOnTheKnobKeepsTheGrabOffset()
    {
        var drag = Session(104, 100);
        Assert.True(drag.GrabbedKnob);
        Assert.Equal(100, drag.Clamped); // no jump
        drag.Move(154, 0, 1);
        Assert.Equal(150, drag.Clamped, 1e-9);
    }

    // Ported from DesktopScrubberTests.swift: testPressElsewhereJumpsThenFollowsOneToOne
    [Fact]
    public void PressElsewhereJumpsThenFollowsOneToOne()
    {
        var drag = Session(300, 100);
        Assert.False(drag.GrabbedKnob);
        Assert.Equal(300, drag.Clamped);
        drag.Move(320, 0, 1);
        Assert.Equal(320, drag.Clamped);
    }

    // Ported from DesktopScrubberTests.swift: testFineScrubMovesByTheGainedDeltaWithoutJumping
    [Fact]
    public void FineScrubMovesByTheGainedDeltaWithoutJumping()
    {
        var drag = Session(300, 100);
        drag.Move(300, 50, 1); // half speed, no horizontal move yet
        Assert.Equal(300, drag.Clamped); // changing speed never jumps
        drag.Move(340, 50, 2);
        Assert.Equal(320, drag.Clamped, 1e-9);
        Assert.Equal("Half speed", drag.RateLabel);
        drag.Move(380, 200, 3);
        Assert.Equal(324, drag.Clamped, 1e-9); // 0.1× far away
        drag.Move(400, 0, 4, fine: true);
        Assert.Equal(326, drag.Clamped, 1e-9); // Alt (the Mac's ⌥) is fine at any distance
        // Back at the bar: the knob catches up with the pointer.
        drag.Move(400, 0, 5);
        Assert.Equal(400, drag.Clamped);
        Assert.True(drag.CaughtUp);
    }

    // Ported from DesktopScrubberTests.swift: testEndsClampRubberBandAndAreFeltOnce
    [Fact]
    public void EndsClampRubberBandAndAreFeltOnce()
    {
        var drag = Session(440, 0);
        var events = drag.Move(470, 0, 1);
        Assert.Equal(456, drag.Clamped);
        Assert.Equal(ScrubEvents.ReachedEnd, events);
        Assert.True(drag.Overshoot > 0);
        Assert.True(drag.Overshoot < 14);
        events = drag.Move(520, 0, 2);
        Assert.Equal(ScrubEvents.None, events); // once, on arrival
        drag.Move(400, 0, 3);
        events = drag.Move(460, 0, 4);
        Assert.Equal(ScrubEvents.ReachedEnd, events); // again after leaving
        // Slow modes stop at the end without stretching.
        var fine = Session(450, 0);
        fine.Move(450, 100, 1);
        fine.Move(600, 100, 2);
        Assert.Equal(456, fine.Clamped);
        Assert.Equal(0, fine.Overshoot);
        Assert.Equal(199.5, fine.Commit(2)); // never the very end
    }

    // Ported from DesktopScrubberTests.swift: testSnapsWhenSlowFeltOnceAndCommandLetsGo
    [Fact]
    public void SnapsWhenSlowFeltOnceAndCommandLetsGo()
    {
        const double fiftyX = 114.0;
        var drag = Session(300, 0);
        // A slow approach: 2 DIP per 100 ms = 20 DIP/s.
        var felt = 0;
        var x = 130.0;
        var t = 0.0;
        while (x > fiftyX - 4)
        {
            x -= 2;
            t += 0.1;
            if (drag.Move(x, 0, t).HasFlag(ScrubEvents.Snapped)) felt++;
        }

        Assert.Equal(1, felt); // one alignment event on entering the snap
        Assert.Equal(50, drag.SnappedTime);
        Assert.Equal(fiftyX, drag.DisplayX, 1e-9);
        Assert.Equal(49.8, drag.Commit(t) ?? 0, 1e-9); // lands just before the line
        drag.ModifiersChanged(fine: false, noSnap: true, t + 0.1);
        Assert.Null(drag.Snapped); // Ctrl (the Mac's ⌘) disables snapping
        Assert.Equal(ScrubMath.Time(x, 200, width: 456), drag.Commit(t + 0.1) ?? 0, 1e-9);
    }

    // Ported from DesktopScrubberTests.swift: testFastSweepDoesNotSnap
    [Fact]
    public void FastSweepDoesNotSnap()
    {
        var drag = Session(300, 0);
        drag.Move(200, 0, 0.1);
        drag.Move(115, 0, 0.2); // ~850 DIP/s
        Assert.Null(drag.Snapped);
    }

    // Ported from DesktopScrubberTests.swift: testSnapHapticsAreRateLimited
    [Fact]
    public void SnapHapticsAreRateLimited()
    {
        // Two targets 1 DIP apart are never both felt within 90 ms.
        var drag = new ScrubSession(300, 0, 0, 456, "a", [0, 100, 110], 0, width: 456);
        var events = drag.Move(101, 0, 10);
        Assert.Equal(ScrubEvents.Snapped, events);
        events = drag.Move(109, 0, 10.05);
        Assert.Equal(110, drag.SnappedTime);
        Assert.Equal(ScrubEvents.None, events); // within 90 ms of the last
    }

    // Ported from DesktopScrubberTests.swift: testClicks
    [Fact]
    public void Clicks()
    {
        var onKnob = Session(101, 100);
        Assert.Null(onKnob.Commit(0.1)); // a click on the knob is not a seek
        var onBar = Session(200, 100);
        Assert.True(onBar.IsClick(0.1));
        Assert.Null(onBar.Snapped);
        Assert.Equal(ScrubMath.Time(200, 200, width: 456), onBar.Commit(0.1) ?? 0, 1e-9);
        var snappy = Session(116, 300);
        Assert.Equal(50, snappy.SnappedTime); // a click near a line lands on it
        Assert.Equal(49.8, snappy.Commit(0.1) ?? 0, 1e-9);
        Assert.Null(Session(116, 300, noSnap: true).Snapped);
    }
}
