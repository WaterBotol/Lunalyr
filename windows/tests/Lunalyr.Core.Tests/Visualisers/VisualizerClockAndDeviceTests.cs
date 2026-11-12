using Lunalyr.Core.Visualisers;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>The scene clock, and the render-scale ladder a new scene restarts.</summary>
public class VisualizerClockAndDeviceTests
{
    // Ported from VisualizerClockAndDeviceTests.swift: testSceneClockKeepsFrameStepsAfterDaysOfRendering
    [Fact]
    public void SceneClockKeepsFrameStepsAfterDaysOfRendering()
    {
        const double step = 1.0 / 60;
        // Precondition: 55 hours in, a float can no longer hold a 60 fps step.
        const double wallpaperClock = 55.0 * 3600;
        Assert.True(Math.Abs((double)((float)(wallpaperClock + step) - (float)wallpaperClock) - step) > 0.0005);

        // A scene built that late restarts its own clock instead of copying it.
        var timeline = new VisualizerTimeline();
        Assert.Equal(0, timeline.Advance(wallpaperClock, 1, 0));
        var previous = timeline.ShaderTime;
        for (var frame = 1; frame <= 600; frame++)
        {
            timeline.Advance(wallpaperClock + frame * step, 1, frame * step);
            Assert.True(Math.Abs((double)(timeline.ShaderTime - previous) - step) <= 0.00005);
            previous = timeline.ShaderTime;
        }

        // A scene that has run for over an hour keeps running through continuous playback, then restarts at the next
        // natural break.
        var running = new VisualizerTimeline();
        running.Advance(0, 1, 0);
        var source = 0.0;
        while (running.Time <= VisualizerTimeline.RebaseThreshold)
        {
            source += 0.25;
            running.Advance(source, 1, source);
        }
        var before = running.Time;
        Assert.True(Math.Abs(running.Advance(source + step, 1, source + step) - (before + step)) <= 1e-9,
            "Continuous playback never jumps");
        Assert.Equal(0, running.Advance(source + 2 * step, 1, source + 2 * step, atBreak: true));
    }

    // Ported from VisualizerClockAndDeviceTests.swift: testBreaksOnlyRebaseALargeClockAndPausesCount
    [Fact]
    public void BreaksOnlyRebaseALargeClockAndPausesCount()
    {
        var timeline = new VisualizerTimeline();
        timeline.Advance(10, 1, 0);
        Assert.True(Math.Abs(timeline.Advance(10.2, 1, 0.2, atBreak: true) - 10.2) <= 1e-9, "A young scene keeps its clock through a break");
        Assert.True(Math.Abs(timeline.Advance(10.2, 1, 0.21) - 10.2) <= 1e-9, "Another display drawing the same frame changes nothing");
        Assert.True(Math.Abs(timeline.Advance(3_610.2, 1, 0.3) - 10.45) <= 1e-9, "A scene that was off screen resumes where it was");
        var aged = new VisualizerTimeline();
        aged.Advance(0, 100, 0);
        var clock = 0.0;
        while (aged.Time <= VisualizerTimeline.RebaseThreshold)
        {
            clock += 0.25;
            aged.Advance(clock, 100, clock);
        }
        Assert.Equal(0, aged.Advance(clock + 0.1, 100, clock + 9)); // A wake or long pause is a break
        var endless = new VisualizerTimeline();
        endless.Advance(0, 1, 0);
        var source = 0.0;
        while (endless.Time > 0 || source == 0)
        {
            source += 0.25;
            endless.Advance(source, 10_000, source);
            if (source > 100) break;
        }
        Assert.True(endless.Time < VisualizerTimeline.ForcedRebase, "A stream with no break at all is rebased once a float would fail");
    }

    // Ported from VisualizerClockAndDeviceTests.swift: testReduceMotionChangesSpeedWithoutJumpingTheScene
    [Fact]
    public void ReduceMotionChangesSpeedWithoutJumpingTheScene()
    {
        var timeline = new VisualizerTimeline();
        timeline.Advance(100, 1, 0);
        var before = timeline.Advance(100.1, 1, 0.1);
        var slowed = timeline.Advance(100.2, 0.15, 0.2);
        Assert.True(Math.Abs(slowed - before - 0.015) <= 1e-9, "The old clock × 0.15 jumped to a different scene");
        Assert.Equal(slowed, timeline.Advance(100.15, 1, 0.3)); // Never run backwards
    }

    // Ported from VisualizerSafetyTests.swift: testANewSceneStartsAtFullResolution (the ladder half; moving to another
    // GPU is covered by Lunalyr.Render.Tests' VisualiserLayerTests).
    [Fact]
    public void ANewSceneStartsAtFullResolution()
    {
        var state = new VisualizerSceneState();
        var timelines = new VisualizerTimelines();
        state.Prepare(VisualizerStyle.Tunnel, 0, new VisualizerResponse(), null, new AudioSnapshot(), 90_000, timelines);
        for (var i = 0; i < 4; i++) state.Ladder.Adjust(0.05, 1.0 / 30);
        Assert.Equal(RenderScaleLadder.Scales[0], state.Ladder.Scale);
        state.Prepare(VisualizerStyle.Tunnel, 0.1, new VisualizerResponse(), null, new AudioSnapshot(), 90_000.1, timelines);
        Assert.Equal(RenderScaleLadder.Scales[0], state.Ladder.Scale); // The same scene keeps its rung
        state.Prepare(VisualizerStyle.Silk, 0.2, new VisualizerResponse(), null, new AudioSnapshot(), 90_000.2, timelines);
        Assert.Equal(1, state.Ladder.Scale);
    }

    // Windows' own: the ladder's hysteresis and the per-scene pixel budget (the Mac's drawableSize).
    [Fact]
    public void LadderStepsDownWhenOverBudgetAndUpOnlyWithRoom()
    {
        var ladder = new RenderScaleLadder();
        ladder.Adjust(0.9 / 60, 1.0 / 60);
        Assert.Equal(0.875, ladder.Scale);
        ladder.Adjust(0.7 / 60, 1.0 / 60); // Predicted 0.91 of the budget: stays
        Assert.Equal(0.875, ladder.Scale);
        ladder.Adjust(0.4 / 60, 1.0 / 60);
        Assert.Equal(1, ladder.Scale);
        Assert.Equal((1788, 1006), RenderScaleLadder.DrawableSize(2560, 1440, VisualizerStyle.Silk, false));
        Assert.Equal((1299, 731),RenderScaleLadder.DrawableSize(2560, 1440, VisualizerStyle.Tunnel, false));
        Assert.Equal((625, 351), RenderScaleLadder.DrawableSize(2560, 1440, VisualizerStyle.Silk, true));
        Assert.Equal((640, 360), RenderScaleLadder.DrawableSize(640, 360, VisualizerStyle.Silk, false));
        var timer = new GpuFrameTimer();
        timer.Record(0);
        timer.Record(0.004);
        Assert.Null(timer.Drain(2));
        timer.Record(0.006);
        Assert.Equal(0.005, timer.Drain(2)!.Value, 9);
        Assert.Null(timer.Drain(1));
    }
}
