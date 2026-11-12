using Lunalyr.Core.Backgrounds;

namespace Lunalyr.Core.Tests.Backgrounds;

/// <summary>The GPU effects' time base, epoch policy, pixel budget and clocks.</summary>
public sealed class ShaderTimingTests
{
    // Ported from WallpaperEnergyTests.swift: testShaderTimeStaysPreciseAfterDaysOfRendering
    [Fact]
    public void ShaderTimeStaysPreciseAfterDaysOfRendering()
    {
        const double start = 60 * 3600.0; // 60 hours of accumulated phase
        const double step = 1.0 / 30;
        var timeBase = new ShaderTimeBase();
        var previous = timeBase.Uniforms(start, start * 0.7);
        Assert.True(previous.Time < (float)ShaderTimeBase.Period);
        double worst = 0, naiveWorst = 0;
        var naivePrevious = (float)start;
        for (var frame = 1; frame <= 600; frame++)
        {
            var time = start + frame * step;
            var next = timeBase.Uniforms(time, time * 0.7);
            worst = Math.Max(worst, Math.Abs((double)(next.Time - previous.Time) - step));
            previous = next;
            // What passing the raw clock did.
            var naive = (float)time;
            naiveWorst = Math.Max(naiveWorst, Math.Abs((double)(naive - naivePrevious) - step));
            naivePrevious = naive;
        }
        Assert.True(worst < 0.0005, "steps stay even");
        Assert.True(naiveWorst > 0.005, "the raw clock visibly stuttered");
    }

    // Ported from WallpaperEnergyTests.swift: testShaderTimeContinuesWithoutJumpingAndRestartsSmall
    [Fact]
    public void ShaderTimeContinuesWithoutJumpingAndRestartsSmall()
    {
        var timeBase = new ShaderTimeBase();
        var first = timeBase.Uniforms(50_000, 35_000);
        var later = timeBase.Uniforms(50_010, 35_007);
        Assert.Equal(10, later.Time - first.Time, 0.001);
        Assert.Equal(7, later.Flow - first.Flow, 0.001);
        // A fresh base (a new layer) starts inside one period again.
        var renewed = new ShaderTimeBase();
        var restart = renewed.Uniforms(500_000, 400_000);
        Assert.True(restart.Time < (float)ShaderTimeBase.Period);
        Assert.True(restart.Flow < (float)ShaderTimeBase.Period);
    }

    // Ported from WallpaperEnergyTests.swift: testShaderClockRenewsOnlyAtNaturalBreaks
    [Fact]
    public void ShaderClockRenewsOnlyAtNaturalBreaks()
    {
        Assert.False(ShaderEpochPolicy.ShouldRenew(120, ShaderEpochPolicy.Trigger.TrackChange));
        Assert.True(ShaderEpochPolicy.ShouldRenew(3600, ShaderEpochPolicy.Trigger.TrackChange));
        Assert.False(ShaderEpochPolicy.ShouldRenew(60, ShaderEpochPolicy.Trigger.Wake));
        Assert.True(ShaderEpochPolicy.ShouldRenew(900, ShaderEpochPolicy.Trigger.Wake));
        Assert.False(ShaderEpochPolicy.ShouldRenew(3600, ShaderEpochPolicy.Trigger.Elapsed));
        Assert.True(ShaderEpochPolicy.ShouldRenew(ShaderEpochPolicy.MaximumAge, ShaderEpochPolicy.Trigger.Elapsed));
    }

    // Ported from WallpaperEnergyTests.swift: testShaderDrawableIsCappedAndKeepsItsAspect. The Mac checks its heavy
    // "fractal"; Windows' tiers are its own measurements, so any Heavy effect stands in for it.
    [Fact]
    public void ShaderDrawableIsCappedAndKeepsItsAspect()
    {
        const double budget = ShaderRenderBudget.Standard;
        // A 14-inch MacBook Pro at 2x: 5.9 MP native.
        var laptop = ShaderRenderBudget.DrawableSize(1512, 982, 2, budget);
        Assert.True(laptop.Width * (double)laptop.Height <= budget);
        Assert.Equal(1512.0 / 982.0, laptop.Width / (double)laptop.Height, 0.01);
        // A 6K display: 20 MP native, still under the budget.
        var xdr = ShaderRenderBudget.DrawableSize(3008, 1692, 2, budget);
        Assert.True(xdr.Width * (double)xdr.Height <= budget);
        // Small surfaces render natively.
        Assert.Equal((800, 600), ShaderRenderBudget.DrawableSize(400, 300, 2, budget));
        Assert.Equal((1, 1), ShaderRenderBudget.DrawableSize(0, 0, 2, budget));
        Assert.True(ShaderRenderBudget.Pixels(GpuEffectCost.Heavy, preview: false, economical: false) < budget);
        Assert.True(ShaderRenderBudget.Pixels(GpuEffectCatalog.Find("plasma")!.Cost, preview: false, economical: true) < budget);
        Assert.True(ShaderRenderBudget.Pixels(GpuEffectCatalog.Find("plasma")!.Cost, preview: true, economical: false) < 300_000);
    }

    [Fact]
    public void ADisplayAt2560By1440IsDrawnAtTheBudgetInItsOwnAspect()
    {
        var canvas = ShaderRenderBudget.DrawableSize(2560, 1440, 1, ShaderRenderBudget.Standard);
        Assert.InRange(canvas.Width * (double)canvas.Height, ShaderRenderBudget.Standard * 0.99, ShaderRenderBudget.Standard);
        Assert.Equal(16.0 / 9, canvas.Width / (double)canvas.Height, 0.01);
    }

    [Fact]
    public void TheClocksRunAtMotionSpeedAndFlowAtSeventyPercent()
    {
        var clock = new EffectClock();
        clock.At(100, 1.5, false);
        var (time, flow) = clock.At(110, 1.5, false);
        Assert.Equal(165, time, 1e-9);
        Assert.Equal(165 * 0.70, flow, 1e-9);
    }

    [Fact]
    public void ReduceMotionSlowsTheClocksWithoutMovingThem()
    {
        var clock = new EffectClock();
        var before = clock.At(100, 1, false);
        var switched = clock.At(100, 1, true);
        Assert.Equal(before.Time, switched.Time, 1e-9);
        var later = clock.At(110, 1, true);
        Assert.Equal(before.Time + 10 * EffectClock.ReducedMotionRate, later.Time, 1e-9);
        Assert.True(later.Time > switched.Time, "Reduce Motion slows the effects, it never stops them");
    }

    [Fact]
    public void TheCataloguesUseTheMacsRawValues()
    {
        Assert.Equal(27, GpuEffectCatalog.All.Count);
        Assert.Equal(27, GpuEffectCatalog.All.Select(e => e.Id).Distinct().Count());
        Assert.Equal("Marble", GpuEffectCatalog.Find("warp")!.Title);
        Assert.Equal("shader:lightTrails", GpuEffectCatalog.LayerId("lightTrails"));
        Assert.Equal("lightTrails", GpuEffectCatalog.FromLayerId("shader:lightTrails")!.Id);
        Assert.Null(GpuEffectCatalog.FromLayerId("pattern:ripples"));
        Assert.Equal(18, LinePatternCatalog.All.Count);
        for (var i = 0; i < LinePatternCatalog.All.Count; i++) Assert.Equal(i, LinePatternCatalog.All[i].RawValue);
        Assert.Equal("diagonalRain", LinePatternCatalog.Find(15)!.Id);
        Assert.Equal("Rain", LinePatternCatalog.Find(15)!.Title);
        Assert.Equal("pattern:moire", LinePatternCatalog.LayerId(10));
        Assert.Null(LinePatternCatalog.Find(18));
        Assert.DoesNotContain(GpuEffectCatalog.Affordable(sparing: true), e => e.Cost == GpuEffectCost.Heavy);
    }
}
