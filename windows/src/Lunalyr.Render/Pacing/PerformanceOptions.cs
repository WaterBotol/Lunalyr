namespace Lunalyr.Render.Pacing;

/// <summary>
/// The wallpaper's frame-rate choice. The numbered caps present every Nth refresh (a swap-chain sync interval), so
/// frames stay evenly spaced; <see cref="Display"/> follows the monitor's refresh rate.
/// </summary>
/// <remarks>The Mac offers 15, 30 and 60 fps (re-implemented from its behaviour; that enum is Verso-attributed).
/// Windows adds <see cref="Display"/>, for 120 Hz+ panels.</remarks>
public enum FrameRateCap
{
    Display = 0,
    Fps15 = 15,
    Fps30 = 30,
    Fps60 = 60,
}

/// <summary>
/// How far the wallpaper eases off while power saving applies. The engine only caps the frame rate; which
/// background is drawn under each behaviour is the app's choice (it swaps layers through
/// <see cref="RenderEngine.SetLayers"/>).
/// </summary>
/// <remarks>Same four behaviours as the Mac's "On battery" choice, re-implemented from its behaviour.</remarks>
public enum PowerFallback
{
    /// <summary>Keep everything.</summary>
    Full,
    /// <summary>Keep effects, but only the inexpensive ones.</summary>
    LightEffects,
    /// <summary>Drop GPU effects for line patterns.</summary>
    Patterns,
    /// <summary>Plain colour only.</summary>
    ColourOnly,
}

/// <summary>The Performance settings the engine follows. Defaults match the Mac's.</summary>
public sealed record PerformanceOptions
{
    public static PerformanceOptions Default { get; } = new();

    /// <summary>Chosen frame rate. 60 fps by default (the owner's choice, 12 Nov 2026; the Mac's was 30).</summary>
    public FrameRateCap FrameRate { get; init; } = FrameRateCap.Fps60;

    /// <summary>"Low power visuals": ease off all the time, whatever the power source (the low-power tier).</summary>
    public bool LowPowerVisuals { get; init; }

    /// <summary>Follow Windows Energy saver (the Mac follows Low Power Mode). On by default.</summary>
    public bool FollowEnergySaver { get; init; } = true;

    /// <summary>What to do on battery. The Mac's default is inexpensive effects only.</summary>
    public PowerFallback OnBattery { get; init; } = PowerFallback.LightEffects;

    /// <summary>"Pause fully covered wallpapers". On by default.</summary>
    public bool PauseWhenCovered { get; init; } = true;

    /// <summary>"Pause wallpaper": freezes every wallpaper on its current frame until resumed.</summary>
    public bool PauseWallpaper { get; init; }
}
