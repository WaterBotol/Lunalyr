// Adapted from Wavefield by Niko Dellic (MIT), via Lunalyr for Mac's WavefieldModes.swift.

using System.Numerics;
using System.Runtime.CompilerServices;
using static Lunalyr.Core.Visualisers.SwiftMath;

namespace Lunalyr.Core.Visualisers;

/// <summary>The at most six peaks of a <see cref="WavefieldSpectrum"/>, stored inline.</summary>
[InlineArray(WavefieldSpectrum.MaximumPeaks)]
public struct WavefieldPeaks
{
    private WavefieldSpectrum.Peak _element;
}

/// <summary>
/// Wavefield's reading of one analysis pass: levels per register, energy, centroid and the strongest spectral peaks
/// with their harmonic support. A value type with inline storage, so building or copying one never allocates.
/// </summary>
/// <remarks>
/// Partial so the rest of Wavefield's model (its modes, frames and uniforms) can live in other files.
/// </remarks>
public partial struct WavefieldSpectrum
{
    /// <summary>The most peaks a spectrum keeps.</summary>
    public const int MaximumPeaks = 6;

    /// <summary>One spectral peak: its refined frequency in Hz, its amplitude and its harmonic support, 0...1.</summary>
    public readonly record struct Peak(float Frequency, float Magnitude, float Harmonics);

    public bool IsReceiving;
    public float Rms;
    public float Energy;
    public float Centroid;
    /// <summary>Low (&lt; 250 Hz), middle (&lt; 2 kHz) and high register levels.</summary>
    public Vector3 Bands;
    /// <summary>The strongest peaks, strongest first; only the first <see cref="PeakCount"/> are set.</summary>
    public WavefieldPeaks Peaks;
    public int PeakCount;

    public WavefieldSpectrum()
    {
    }

    /// <summary>The register (0 low, 1 middle, 2 high) a frequency belongs to.</summary>
    public static int Band(float frequency)
    {
        if (frequency < 250) return 0;
        return frequency < 2_000 ? 1 : 2;
    }

    /// <summary>Peak <paramref name="index"/>, which must be below <see cref="PeakCount"/>.</summary>
    public readonly Peak GetPeak(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, PeakCount);
        return Peaks[index];
    }

    /// <summary>
    /// Reads Lunalyr's FFT before log-band reduction, so the atlas sees the actual pitch, including bass bins with
    /// unequal log-band spacing. <paramref name="power"/> is the plain window's vDSP-scaled power (bin k at
    /// k × <paramref name="binHz"/>); readers build this from a stored pass, so the peak search and sort never run on
    /// the capture thread. Allocation-free.
    /// </summary>
    public WavefieldSpectrum(ReadOnlySpan<float> power, float binHz, int fftSize, float rms) : this()
    {
        IsReceiving = true;
        Rms = Min(1, Max(0, rms * 2.4f));
        // vDSP's packed real forward transform has a factor of two.
        var norm = 0.5f / fftSize;
        float power0 = 0, power1 = 0, power2 = 0, count0 = 0, count1 = 0, count2 = 0;
        float sum = 0, weighted = 0, totalPower = 0, strongest = 0;
        // Measure energy and centroid up to 24 kHz at every sample rate, so a 96 kHz device's empty top octave does
        // not dim or darken the field.
        var usable = Max(2, Min(power.Length, (int)(24_000 / Max(1, binHz)) + 1));
        var last = Min(usable, power.Length);
        for (var bin = 1; bin < last; bin++)
        {
            var frequency = bin * binHz;
            var value = Max(0, power[bin]);
            var amplitude = MathF.Sqrt(value) * norm;
            sum += amplitude;
            weighted += frequency * amplitude;
            totalPower += value;
            strongest = Max(strongest, amplitude);
            if (frequency >= 20 && frequency < 8_000)
            {
                switch (Band(frequency))
                {
                    case 0: power0 += value; count0 += 1; break;
                    case 1: power1 += value; count1 += 1; break;
                    default: power2 += value; count2 += 1; break;
                }
            }
        }
        Bands = new Vector3(
            Min(1, MathF.Sqrt(power0 / Max(1, count0)) * norm * 12),
            Min(1, MathF.Sqrt(power1 / Max(1, count1)) * norm * 12),
            Min(1, MathF.Sqrt(power2 / Max(1, count2)) * norm * 12));
        Energy = Min(1, MathF.Sqrt(totalPower / usable) * norm * 14);
        Centroid = sum > 0.000001f ? Min(1, weighted / sum / (usable * binHz)) : 0;

        var threshold = Max(0.00008f, strongest * 0.12f);
        if (!(strongest >= threshold)) return;
        var end = Min(power.Length - 1, (int)(8_000 / binHz) + 2);
        if (end <= 1) return;
        // At most every other bin is a local maximum.
        var capacity = end / 2 + 2;
        Span<float> candidateFrequency = stackalloc float[capacity];
        Span<float> candidateMagnitude = stackalloc float[capacity];
        var candidates = 0;
        for (var bin = 1; bin < end; bin++)
        {
            var centre = MathF.Sqrt(Max(0, power[bin])) * norm;
            if (!(centre >= threshold && power[bin] >= power[bin - 1] && power[bin] > power[bin + 1])) continue;
            var left = MathF.Sqrt(Max(0, power[bin - 1])) * norm;
            var right = MathF.Sqrt(Max(0, power[bin + 1])) * norm;
            var denominator = left - 2 * centre + right;
            var offset = MathF.Abs(denominator) > 0.000001f ? (left - right) * 0.5f / denominator : 0;
            var frequency = (bin + Min(0.5f, Max(-0.5f, offset))) * binHz;
            if (frequency >= 45 && frequency <= 8_000 && candidates < capacity)
            {
                // Insert in descending magnitude, after equal ones: a stable sort, as Swift's is.
                var at = candidates;
                while (at > 0 && candidateMagnitude[at - 1] < centre)
                {
                    candidateMagnitude[at] = candidateMagnitude[at - 1];
                    candidateFrequency[at] = candidateFrequency[at - 1];
                    at--;
                }
                candidateMagnitude[at] = centre;
                candidateFrequency[at] = frequency;
                candidates++;
            }
        }
        for (var c = 0; c < candidates; c++)
        {
            if (PeakCount == MaximumPeaks) break;
            float frequency = candidateFrequency[c], magnitude = candidateMagnitude[c];
            var near = false;
            for (var p = 0; p < PeakCount; p++)
            {
                if (MathF.Abs(Peaks[p].Frequency - frequency) < 58) { near = true; break; }
            }
            if (near) continue;
            float support = 0, weight = 0;
            for (var order = 2; order <= 5; order++)
            {
                var bin = Min(power.Length - 1, (int)Round(frequency * order / binHz));
                var share = 1f / order;
                support += Min(1, MathF.Sqrt(Max(0, power[bin])) * norm / magnitude) * share;
                weight += share;
            }
            Peaks[PeakCount++] = new Peak(frequency, magnitude, support / weight);
        }
    }
}
