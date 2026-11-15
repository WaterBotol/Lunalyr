using Lunalyr.Render.Pacing;

namespace Lunalyr.Render.Tests;

public sealed class FramePacingTests
{
    [Theory]
    [InlineData(120, FrameRateCap.Fps60)]
    [InlineData(60, FrameRateCap.Fps30)]
    [InlineData(240, FrameRateCap.Fps60)]
    public void ACompositionSwapChainAlwaysPacesOnTheGrid(int hz, FrameRateCap cap)
    {
        // Measured live: a sync interval of 2 on a composition swap chain still drew a 120 Hz panel at 120 fps.
        var plan = FramePacing.Plan(hz, cap, syncIntervalPaces: false);
        Assert.Equal(1u, plan.SyncInterval);
        Assert.Equal(1.0 / (int)cap, plan.GatePeriod, 9);
        Assert.False(FramePacing.Plan(60, FrameRateCap.Fps60, syncIntervalPaces: false).IsGated); // at the refresh rate: no gate
    }

    [Theory]
    [InlineData(60, FrameRateCap.Fps60, 1u)]
    [InlineData(60, FrameRateCap.Fps30, 2u)]
    [InlineData(60, FrameRateCap.Fps15, 4u)]
    [InlineData(59, FrameRateCap.Fps30, 2u)] // GDI reports 59 for 59.94 Hz panels
    [InlineData(120, FrameRateCap.Fps60, 2u)]
    [InlineData(120, FrameRateCap.Fps30, 4u)]
    [InlineData(240, FrameRateCap.Fps60, 4u)]
    [InlineData(30, FrameRateCap.Fps60, 1u)] // a cap above the refresh rate is the refresh rate
    public void WholeRatiosArePacedBySyncIntervalAlone(int hz, FrameRateCap cap, uint interval)
    {
        var plan = FramePacing.Plan(hz, cap);
        Assert.Equal(interval, plan.SyncInterval);
        Assert.False(plan.IsGated);
    }

    [Theory]
    [InlineData(144, FrameRateCap.Fps60)]
    [InlineData(280, FrameRateCap.Fps60)] // ratio 4.67: more than DXGI's largest sync interval
    [InlineData(120, FrameRateCap.Fps15)] // ratio 8
    [InlineData(165, FrameRateCap.Fps30)]
    public void OtherRatiosGoOutOnATimeGridAtEveryRefresh(int hz, FrameRateCap cap)
    {
        var plan = FramePacing.Plan(hz, cap);
        Assert.Equal(1u, plan.SyncInterval);
        Assert.Equal(1.0 / (int)cap, plan.GatePeriod, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void DisplayRateAndUnknownRefreshPresentEveryRefresh(double hz)
    {
        Assert.Equal(new PacingPlan(1, 0), FramePacing.Plan(hz, FrameRateCap.Fps30));
        Assert.Equal(new PacingPlan(1, 0), FramePacing.Plan(144, FrameRateCap.Display));
    }

    [Fact]
    public void TheGridKeepsTheAverageAndRestartsAfterAStall()
    {
        double period = 1.0 / 60;
        // On time or slightly late: the grid carries on, so the average rate is exact.
        Assert.Equal(1 + period, FramePacing.NextDue(1, 1.004, period), 12);
        // Paused for a while: start afresh instead of rushing out a burst of frames.
        Assert.Equal(5 + period, FramePacing.NextDue(1, 5, period), 12);
    }

    [Fact]
    public void SixtyOnA144HzPanelAveragesSixty()
    {
        // Simulate presenting at the first refresh after each frame is due.
        double vblank = 1.0 / 144, period = 1.0 / 60, due = 0;
        var shown = new List<double>();
        for (int i = 0; i < 600; i++)
        {
            double at = Math.Ceiling(due / vblank - 1e-9) * vblank;
            shown.Add(at);
            due = FramePacing.NextDue(due, at, period);
        }
        double fps = (shown.Count - 1) / (shown[^1] - shown[0]);
        Assert.InRange(fps, 59.5, 60.5);
        // Never more than one refresh of jitter: intervals are 2 or 3 refreshes.
        for (int i = 1; i < shown.Count; i++)
            Assert.InRange(Math.Round((shown[i] - shown[i - 1]) / vblank), 2, 3);
    }

    [Fact]
    public void Frames_drawn_early_never_push_the_grid_more_than_a_period_ahead()
    {
        // A hover fade forces frames at the display's rate (280 Hz) on a 60 fps gate: the grid stays one period ahead
        // at most, so the next ordinary frame is never seconds away (the floating lyrics' freeze after a hover).
        const double period = 1 / 60.0;
        double due = 0, now = 0;
        for (var i = 0; i < 280; i++)
        {
            now += 1 / 280.0;
            due = FramePacing.NextDue(due, now, period);
            Assert.True(due <= now + period + 1e-9, $"frame {i}: due {due - now:0.000} s ahead");
        }
    }
}
