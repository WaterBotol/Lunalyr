namespace Lunalyr.Core.Visualisers;

/// <summary>
/// A quiet, slowly changing spectrum for the wallpaper renderer's idle scene (the Mac's
/// <c>AudioVisualizerView.Coordinator.ambientSpectrum(at:)</c>). Capture, meters and beat detection stay on the real
/// audio.
/// </summary>
public static class AmbientSpectrum
{
    /// <summary>The ambient spectrum at scene time <paramref name="time"/>, in seconds. Never allocates.</summary>
    public static AudioSnapshot At(double time)
    {
        var ambient = new AudioSnapshot();
        for (var band = 0; band < AudioSnapshot.BandCount; band++)
        {
            var x = (double)band / (AudioSnapshot.BandCount - 1);
            ambient.Bands[band] = (float)(0.16 + 0.055 * Math.Sin(7 * x + 0.24 * time) + 0.025 * Math.Cos(13 * x - 0.17 * time));
        }
        const int bandsPerComponent = AudioSnapshot.BandCount / AudioSnapshot.ComponentCount;
        for (var component = 0; component < AudioSnapshot.ComponentCount; component++)
        {
            var start = component * bandsPerComponent;
            float sum = 0;
            for (var band = start; band < start + bandsPerComponent; band++) sum += ambient.Bands[band];
            ambient.Components[component] = sum / bandsPerComponent;
        }
        ambient.Bass = ambient.Components[1];
        ambient.Mid = ambient.Components[3];
        ambient.Treble = ambient.Components[6];
        ambient.Level = 0.12f;
        ambient.Centroid = 0.45f;
        return ambient;
    }
}
