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

    /// <summary>
    /// What power saving is doing now, or null when nothing: the frame rate it holds down, and a rule other than Low
    /// power visuals (whose switch already says so) that is in force. The Mac's two leaf captions, in its order.
    /// </summary>
    public static string? InEffect(AppSettings settings, PowerConstraint? power)
    {
        if (power is not { } constraint) return null;
        var parts = new List<string>(2);
        var chosen = settings.Performance().FrameRate;
        if (PowerPolicy.EffectiveRate(chosen, sparingGpu: true) != chosen)
            parts.Add("Power saving is holding animation at 30 fps for now.");
        if (constraint.Kind != PowerConstraintKind.LowPowerVisuals)
            parts.Add($"{constraint.Reason} now, so power saving is in effect.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
