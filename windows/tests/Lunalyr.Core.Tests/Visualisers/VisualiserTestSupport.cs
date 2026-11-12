using Lunalyr.Core.Visualisers;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>Small readers over a snapshot's inline arrays, as the Mac tests use Swift's array methods.</summary>
internal static class VisualiserTestSupport
{
    public static float[] ToArray(this AudioBands bands) => ((ReadOnlySpan<float>)bands).ToArray();

    public static float[] ToArray(this AudioComponents components) => ((ReadOnlySpan<float>)components).ToArray();

    public static float[] ToArray(this AudioWaveform waveform) => ((ReadOnlySpan<float>)waveform).ToArray();

    public static float Max(this AudioBands bands) => bands.ToArray().Max();

    /// <summary>The index of the first largest value, as Swift's <c>indices.max { values[$0] &lt; values[$1] }</c>.</summary>
    public static int Peak(IReadOnlyList<float> values)
    {
        var best = 0;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] > values[best]) best = i;
        }
        return best;
    }

    /// <summary>A sine of <paramref name="frequency"/> Hz at <paramref name="amplitude"/>, sample <paramref name="first"/> onward.</summary>
    public static float[] Sine(double frequency, double amplitude, double rate, int count, long first = 0)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++) samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * (first + i) / rate));
        return samples;
    }
}
