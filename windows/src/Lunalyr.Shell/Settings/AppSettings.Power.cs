using Lunalyr.Render.Pacing;

namespace Lunalyr.Shell.Settings;

// Performance › Low power visuals (the Mac's Performance page, PerformancePage.swift; Lunalyr's own lines), and the
// Performance settings as the renderer follows them.
public sealed partial record AppSettings
{
    /// <summary>
    /// "Low power visuals": ease off all the time, whatever the power source. Every wallpaper type but Your wallpaper and
    /// Colour only shows Line patterns, at no more than 30 fps (Mac <c>custom.lowPowerVisuals</c>, off by default).
    /// </summary>
    public bool LowPowerVisuals { get; init; }

    /// <summary>The renderer's frame rate, pause and power rules for these settings.</summary>
    public PerformanceOptions Performance() => PerformanceOptions.Default with
    {
        FrameRate = AnimationRate switch
        {
            15 => FrameRateCap.Fps15,
            30 => FrameRateCap.Fps30,
            60 => FrameRateCap.Fps60,
            _ => FrameRateCap.Display,
        },
        PauseWhenCovered = PauseWhenCovered,
        FollowEnergySaver = PauseOnBatterySaver,
        PauseWallpaper = WallpaperPaused,
        LowPowerVisuals = LowPowerVisuals,
    };
}
