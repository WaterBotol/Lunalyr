using Lunalyr.Core.Visualisers;

namespace Lunalyr.Shell.Settings;

// The visualisers' own settings (the Mac's Visualiser and Music response sections). Keys are the Mac's without
// "custom."; defaults are the Mac's.
public sealed partial record AppSettings
{
    /// <summary>The visualiser scene (Mac <c>custom.visualizerStyle</c>), default "silk" (Silk Ribbons).</summary>
    public string VisualizerStyle { get; init; } = "silk";

    /// <summary>"System audio": scenes follow the sound playing on this PC (Mac <c>custom.audioReactiveEnabled</c>). Off by default.</summary>
    public bool AudioReactiveEnabled { get; init; }

    /// <summary>How strongly scenes respond, 0.3-3 (Mac <c>custom.audioSensitivity</c>).</summary>
    public double AudioSensitivity { get; init; } = 1;

    /// <summary>Beat punch, 0-2 (Mac <c>custom.visualizerPunch</c>).</summary>
    public double VisualizerPunch { get; init; } = 1;

    /// <summary>Star Trails' sky: "album" (the cover's colours) or "original" (Mac <c>custom.starTrailsColours</c>).</summary>
    public string StarTrailsColours { get; init; } = "album";

    public const double SensitivityMin = 0.3, SensitivityMax = 3;

    /// <summary>The scene these settings choose.</summary>
    public VisualizerStyle Visualizer() => VisualizerStyles.Parse(VisualizerStyle);
}
