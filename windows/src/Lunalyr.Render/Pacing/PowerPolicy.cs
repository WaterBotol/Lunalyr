namespace Lunalyr.Render.Pacing;

/// <summary>What is holding the wallpaper back for power.</summary>
public enum PowerConstraintKind
{
    /// <summary>Lunalyr's own "Low power visuals" switch.</summary>
    LowPowerVisuals,
    /// <summary>Windows Energy saver, which Lunalyr is set to follow.</summary>
    EnergySaver,
    /// <summary>Running on battery under an "On battery" choice other than full effects.</summary>
    Battery,
}

/// <summary>A power constraint in force and how far it eases off.</summary>
public readonly record struct PowerConstraint(PowerConstraintKind Kind, PowerFallback Fallback)
{
    public static PowerConstraint LowPowerVisuals { get; } = new(PowerConstraintKind.LowPowerVisuals, PowerFallback.Patterns);
    public static PowerConstraint EnergySaver { get; } = new(PowerConstraintKind.EnergySaver, PowerFallback.Patterns);
    public static PowerConstraint OnBattery(PowerFallback fallback) => new(PowerConstraintKind.Battery, fallback);

    /// <summary>Why, phrased to open a sentence (for diagnostics and the app's notice).</summary>
    public string Reason => Kind switch
    {
        PowerConstraintKind.LowPowerVisuals => "Low power visuals is on",
        PowerConstraintKind.EnergySaver => "Energy saver is on",
        _ => "This PC is on battery power",
    };
}

/// <summary>Power saving rules, ported from the Mac's Settings (Lunalyr's own lines).</summary>
public static class PowerPolicy
{
    /// <summary>
    /// The constraint in force, if any. Low power visuals and Energy saver drop to line patterns; the battery rule
    /// does what was chosen for it. When several apply the one that eases off most wins, so "Colour only" on battery
    /// still holds while Energy saver is on; ties go to Lunalyr's own switch, then Energy saver, then the battery.
    /// </summary>
    public static PowerConstraint? Constraint(bool lowPowerVisuals, bool followsEnergySaver, bool energySaverOn,
        PowerFallback onBattery, bool isOnBattery)
    {
        PowerConstraint? strictest = null;
        void Consider(PowerConstraint candidate)
        {
            if (strictest is { } current && Strictness(candidate.Fallback) <= Strictness(current.Fallback)) return;
            strictest = candidate;
        }
        if (lowPowerVisuals) Consider(PowerConstraint.LowPowerVisuals);
        if (followsEnergySaver && energySaverOn) Consider(PowerConstraint.EnergySaver);
        if (onBattery != PowerFallback.Full && isOnBattery) Consider(PowerConstraint.OnBattery(onBattery));
        return strictest;
    }

    public static PowerConstraint? Constraint(PerformanceOptions options, bool energySaverOn, bool isOnBattery) =>
        Constraint(options.LowPowerVisuals, options.FollowEnergySaver, energySaverOn, options.OnBattery, isOnBattery);

    private static int Strictness(PowerFallback fallback) => fallback switch
    {
        PowerFallback.Full => 0,
        PowerFallback.LightEffects => 1,
        PowerFallback.Patterns => 2,
        _ => 3,
    };

    /// <summary>Power saving holds animation to 30 fps at most; it never raises a slower choice to meet it.</summary>
    public static FrameRateCap EffectiveRate(FrameRateCap chosen, bool sparingGpu) =>
        sparingGpu && Rank(chosen) > Rank(FrameRateCap.Fps30) ? FrameRateCap.Fps30 : chosen;

    // Display is the fastest choice.
    private static int Rank(FrameRateCap rate) => rate == FrameRateCap.Display ? int.MaxValue : (int)rate;
}
