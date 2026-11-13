using Lunalyr.Render.Pacing;

namespace Lunalyr.Shell.Settings;

/// <summary>What the Performance page says about power saving (the Mac's PerformancePage.swift, Lunalyr's own lines).</summary>
public static class PowerCopy
{
    /// <summary>Under "Low power visuals" (the Mac's caption and its longer help).</summary>
    public const string LowPowerVisuals =
        "Shows Line patterns at no more than 30 fps. Your full wallpaper returns when this is switched off.";

    /// <summary>Under "Follow energy saver" (the Mac's "Follow macOS Low Power Mode").</summary>
    public const string FollowEnergySaver =
        "While Windows’ energy saver is on, the wallpaper shows Line patterns at no more than 30 fps, as Low power visuals does.";

    /// <summary>Under "On battery".</summary>
    public const string OnBatteryCaption = "What the wallpaper eases off to while this PC runs on battery.";

    /// <summary>"On battery"'s choices, in Windows' own words (the Mac's titles are Verso lines).</summary>
    public static IReadOnlyList<SettingsChoice> OnBatteryChoices { get; } =
    [
        new("full", "Everything stays on"),
        new("lightEffects", "Lighter effects only"),
        new("patterns", "Line patterns instead"),
        new("colourOnly", "Album colours only"),
    ];

    /// <summary>
    /// What power saving is doing now, or null when nothing: the frame rate it holds down, and a rule other than Low
    /// power visuals (whose switch already says so) that is in force. The Mac's two leaf captions, in its order.
    /// </summary>
    public static string? InEffect(AppSettings settings, PowerConstraint? power)
    {
        if (Current(settings, power) is not { } constraint) return null;
        var parts = new List<string>(2);
        var chosen = settings.Performance().FrameRate;
        if (PowerPolicy.EffectiveRate(chosen, sparingGpu: true) != chosen)
            parts.Add("Power saving is holding animation at 30 fps for now.");
        if (constraint.Kind != PowerConstraintKind.LowPowerVisuals)
            parts.Add($"{constraint.Reason} now, so power saving is in effect.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// The power saving the page speaks of: what the renderer last reported, read against the Low power visuals switch
    /// as it is now. The Mac works its constraint out from its settings, so the switch counts from the moment it is
    /// turned on, also while the wallpaper is paused (the renderer then isn't running and reports nothing), and stops
    /// counting once it is turned off, before the renderer has caught up.
    /// </summary>
    public static PowerConstraint? Current(AppSettings settings, PowerConstraint? reported)
    {
        PowerConstraint? own = settings.LowPowerVisuals ? PowerConstraint.LowPowerVisuals : null;
        if (reported is { Kind: PowerConstraintKind.LowPowerVisuals }) return own;
        return reported ?? own;
    }
}
