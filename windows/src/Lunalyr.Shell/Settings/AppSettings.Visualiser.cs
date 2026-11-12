using Lunalyr.Core.Visualisers;

namespace Lunalyr.Shell.Settings;

// The wallpaper-types hook (AppSettings.Wallpaper.cs): the visualisers' keys in range.
public sealed partial record AppSettings
{
    static partial void NormalizeVisualiser(ref AppSettings settings) => settings = settings with
    {
        // The Mac's web scenes read as the native scene the Mac stands in with; unknown values as Silk Ribbons.
        VisualizerStyle = VisualizerStyles.Parse(settings.VisualizerStyle).RawValue(),
        AudioSensitivity = InRange(settings.AudioSensitivity, SensitivityMin, SensitivityMax, 1),
        VisualizerPunch = VisualizerPreferences.ClampedPunch(settings.VisualizerPunch),
        StarTrailsColours = settings.StarTrailsColours == "original" ? "original" : "album",
    };
}
