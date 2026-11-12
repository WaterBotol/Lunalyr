using Lunalyr.Render.Pacing;

namespace Lunalyr.Render.Tests;

/// <summary>Power saving never speeds anything up, and the strictest rule wins.</summary>
public sealed class PowerSavingTests
{
    // Ported from PowerSavingTests.swift: testPowerSavingNeverRaisesASlowerFrameRate (the pure part; the Settings
    // object half belongs to the Windows settings model, which isn't built yet).
    [Fact]
    public void PowerSavingNeverRaisesASlowerFrameRate()
    {
        Assert.Equal(FrameRateCap.Fps15, PowerPolicy.EffectiveRate(FrameRateCap.Fps15, sparingGpu: true));
        Assert.Equal(FrameRateCap.Fps30, PowerPolicy.EffectiveRate(FrameRateCap.Fps30, sparingGpu: true));
        Assert.Equal(FrameRateCap.Fps30, PowerPolicy.EffectiveRate(FrameRateCap.Fps60, sparingGpu: true));
        foreach (var rate in Enum.GetValues<FrameRateCap>())
            Assert.Equal(rate, PowerPolicy.EffectiveRate(rate, sparingGpu: false));
    }

    [Fact]
    public void PowerSavingHoldsTheDisplayRateAtThirty()
    {
        // Windows' extra choice is the fastest of all, so power saving holds it too.
        Assert.Equal(FrameRateCap.Fps30, PowerPolicy.EffectiveRate(FrameRateCap.Display, sparingGpu: true));
    }

    private static PowerConstraint? Constraint(bool visuals = false, bool follows = true, bool energySaver = false,
        PowerFallback rule = PowerFallback.Full, bool battery = false) =>
        PowerPolicy.Constraint(visuals, follows, energySaver, rule, battery);

    // Ported from PowerSavingTests.swift: testStrictestConstraintWinsAndBatteryRulesNeedTheBattery
    [Fact]
    public void StrictestConstraintWinsAndBatteryRulesNeedTheBattery()
    {
        Assert.Null(Constraint());
        Assert.Null(Constraint(rule: PowerFallback.ColourOnly)); // a battery rule does nothing on mains power
        Assert.Null(Constraint(rule: PowerFallback.Full, battery: true));
        Assert.Null(Constraint(follows: false, energySaver: true)); // Energy saver is only followed when asked
        Assert.Equal(PowerConstraint.EnergySaver, Constraint(energySaver: true));
        Assert.Equal(PowerConstraint.OnBattery(PowerFallback.LightEffects), Constraint(rule: PowerFallback.LightEffects, battery: true));
        // Colour only on battery is stricter than Energy saver's patterns.
        Assert.Equal(PowerConstraint.OnBattery(PowerFallback.ColourOnly),
            Constraint(energySaver: true, rule: PowerFallback.ColourOnly, battery: true));
        Assert.Equal(PowerConstraint.EnergySaver, Constraint(energySaver: true, rule: PowerFallback.LightEffects, battery: true));
        // Ties go to Lunalyr's own switch, then Energy saver.
        Assert.Equal(PowerConstraint.LowPowerVisuals, Constraint(visuals: true, energySaver: true));
        Assert.Equal(PowerConstraint.EnergySaver, Constraint(energySaver: true, rule: PowerFallback.Patterns, battery: true));
    }

    [Fact]
    public void DefaultsMatchTheMac()
    {
        var d = PerformanceOptions.Default;
        Assert.Equal(FrameRateCap.Fps60, d.FrameRate); // the owner chose 60 fps for both apps (12 Nov 2026)
        Assert.True(d.FollowEnergySaver);
        Assert.Equal(PowerFallback.LightEffects, d.OnBattery);
        Assert.True(d.PauseWhenCovered);
        Assert.False(d.LowPowerVisuals);
        Assert.False(d.PauseWallpaper);
        // With the defaults, plugging out alone eases off to inexpensive effects at 30 fps.
        Assert.Equal(PowerConstraint.OnBattery(PowerFallback.LightEffects), PowerPolicy.Constraint(d, energySaverOn: false, isOnBattery: true));
    }
}
