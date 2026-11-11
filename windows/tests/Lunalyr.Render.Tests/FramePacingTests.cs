using Lunalyr.Render.Pacing;

namespace Lunalyr.Render.Tests;

public sealed class FramePacingTests
{
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
}
