// Adapted from Wavefield by Niko Dellic (MIT), via Lunalyr for Mac's WavefieldModes.swift.

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Lunalyr.Core.Visualisers.SwiftMath;

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// One resonance as the Wavefield shader reads it (its <c>WavefieldMode</c>, 48 bytes). The heatmap only reads topology,
/// motion and excitation; pitch colours belong to other Wavefield palettes, not the Default template.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WavefieldModeUniforms
{
    /// <summary>m, n, topology (the mode's weight), band index.</summary>
    public Vector4 Shape;

    /// <summary>Phase, coherence, log frequency, layer.</summary>
    public Vector4 Motion;

    /// <summary>Excitation, pulse, 0, 0.</summary>
    public Vector4 Drive;

    public WavefieldModeUniforms(Vector4 shape, Vector4 motion, Vector4 drive)
    {
        Shape = shape;
        Motion = motion;
        Drive = drive;
    }
}

/// <summary>The at most <see cref="WavefieldFrame.MaximumModes"/> modes of a <see cref="WavefieldFrame"/>, stored inline.</summary>
[InlineArray(WavefieldFrame.MaximumModes)]
public struct WavefieldModeArray
{
    private WavefieldModeUniforms _element;
}

/// <summary>
/// What one Wavefield frame draws: its modes (bound as an array of <see cref="WavefieldModeUniforms"/>) and the field's
/// scalar state for the uniform option slots. A value type with inline storage, so returning one never allocates.
/// </summary>
public struct WavefieldFrame
{
    /// <summary>
    /// The most modes a frame carries: live frames keep the eight strongest, the ambient frame twelve. The shader clamps
    /// its count to this.
    /// </summary>
    public const int MaximumModes = 12;

    /// <summary>The number of uniform option slots (<c>p0</c>…<c>p15</c>) <see cref="Apply"/> writes into.</summary>
    public const int OptionSlots = 16;

    /// <summary>The modes; only the first <see cref="ModeCount"/> are set.</summary>
    public WavefieldModeArray ModeStorage;
    public int ModeCount;
    /// <summary>Low, middle and high register levels, and the RMS.</summary>
    public Vector4 BandsAndRms;
    /// <summary>Kick, flux and treble onsets, and the energy.</summary>
    public Vector4 OnsetsAndEnergy;
    public float Change;
    public float Pulse;

    /// <summary>The frame's modes, strongest first (the GPU array; bind at least one element even when empty).</summary>
    [UnscopedRef]
    public ReadOnlySpan<WavefieldModeUniforms> Modes => ((ReadOnlySpan<WavefieldModeUniforms>)ModeStorage)[..ModeCount];

    /// <summary>
    /// The field's scalar state in the uniform option slots: <c>p3</c> the mode count, <c>p4</c> change, <c>p5</c>
    /// pulse, <c>p6…p9</c> bands and RMS, <c>p10…p13</c> onsets and energy. Other slots are left as they are.
    /// </summary>
    /// <param name="options">The uniforms' 16 option slots, <c>p0</c> first.</param>
    public readonly void Apply(Span<float> options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Length, OptionSlots, nameof(options));
        options[3] = ModeCount;
        options[4] = Change;
        options[5] = Pulse;
        options[6] = BandsAndRms.X;
        options[7] = BandsAndRms.Y;
        options[8] = BandsAndRms.Z;
        options[9] = BandsAndRms.W;
        options[10] = OnsetsAndEnergy.X;
        options[11] = OnsetsAndEnergy.Y;
        options[12] = OnsetsAndEnergy.Z;
        options[13] = OnsetsAndEnergy.W;
    }
}

/// <summary>
/// Wavefield's free-plate atlas and asymmetric modal bank. A peak excites its two nearest resonances, and the bank
/// retains their tails across frames. Allocation-free per frame: the bank is a fixed array over the atlas.
/// </summary>
/// <remarks>A class where the Mac has a struct: it is the renderer's long-lived, mutable bank.</remarks>
public sealed class WavefieldModes
{
    private readonly record struct Resonance(float M, float N)
    {
        public float Frequency => 220 * (M * M + N * N) / 13;
        public float FrequencyNorm => Min(1f, Max(0f, MathF.Log2(Frequency / 70) / MathF.Log2(7_200f / 70)));
        public int Band => WavefieldSpectrum.Band(Frequency);
        public float Seed
        {
            get
            {
                var hash = Math.Sin((double)M * 12.9898 + (double)N * 78.233) * 43_758.5453;
                return (float)Math.Abs(hash % 1);
            }
        }
    }

    private struct Target
    {
        public float Weight;
        public float Excitation;
        public float Pulse;
    }

    private struct Mode
    {
        public float Weight;
        public float Excitation;
        public float Pulse;
        public float Phase;
    }

    /// <summary>The 120 free-plate resonances (1 ≤ m &lt; n ≤ 16), by frequency; ties keep their (m, n) order.</summary>
    private static readonly Resonance[] Atlas = BuildAtlas();
    private static readonly float[] AtlasFrequency = Array.ConvertAll(Atlas, r => r.Frequency);
    private static readonly float[] AtlasSeed = Array.ConvertAll(Atlas, r => r.Seed);
    private static readonly float[] AtlasNorm = Array.ConvertAll(Atlas, r => r.FrequencyNorm);
    private static readonly int[] AtlasBand = Array.ConvertAll(Atlas, r => r.Band);
    private static readonly Vector3 BandScales = new(1.18f, 1.12f, 1.08f);

    private readonly Mode[] _bank = new Mode[Atlas.Length];
    private readonly bool[] _inBank = new bool[Atlas.Length];
    private readonly Target[] _targets = new Target[Atlas.Length];
    private readonly bool[] _targeted = new bool[Atlas.Length];
    private Vector3 _bands;
    private float _rms;
    private double? _lastTime;

    /// <summary>The number of resonances in the atlas.</summary>
    public static int AtlasCount => Atlas.Length;

    private static Resonance[] BuildAtlas()
    {
        var list = new List<Resonance>(120);
        for (var m = 1; m < 16; m++)
        {
            for (var n = m + 1; n <= 16; n++) list.Add(new Resonance(m, n));
        }
        // OrderBy is stable, as the Mac's sort is in practice: (1, 8) stays before (4, 7).
        return [.. list.OrderBy(r => r.Frequency)];
    }

    /// <summary>
    /// The next frame. <paramref name="reduced"/> is calm (Reduce Motion or Dim Flashing Lights): onsets are removed and
    /// the sensitivity no longer amplifies energy. <paramref name="transient"/> is the Punch control,
    /// <paramref name="motion"/> scales phase travel (Reduce Motion) and <paramref name="patternRate"/> slows how
    /// quickly modes morph in and out. <paramref name="clock"/> is wall seconds; <paramref name="time"/> the scene time.
    /// </summary>
    public WavefieldFrame Frame(in AudioSnapshot sound, double clock, float time, float sensitivity, bool reduced,
                                float transient = 1, float? motion = null, float? patternRate = null)
    {
        var delta = (float)Min(0.1, Max(0, clock - (_lastTime ?? clock - 1.0 / 30)));
        _lastTime = clock;
        var motionScale = motion ?? (reduced ? 0.15f : 1f);
        var patternDelta = delta * (patternRate ?? (reduced ? 0.25f : 1f));
        transient = reduced ? 0 : Max(0f, transient);
        ref readonly var spectrum = ref sound.Wavefield;
        if (!spectrum.IsReceiving)
        {
            Array.Clear(_inBank);
            _bands = Vector3.Zero;
            _rms = 0;
            return Ambient(time);
        }

        var gain = Min(3f, Max(0.3f, sensitivity));
        _bands = new Vector3(
            Follow(_bands.X, Min(1f, spectrum.Bands.X * gain), delta, 1f / 26, 1f / 9),
            Follow(_bands.Y, Min(1f, spectrum.Bands.Y * gain), delta, 1f / 26, 1f / 9),
            Follow(_bands.Z, Min(1f, spectrum.Bands.Z * gain), delta, 1f / 26, 1f / 9));
        _rms = Follow(_rms, Min(1f, spectrum.Rms * gain), delta, 1f / 24, 1f / 8);
        var bandSum = spectrum.Bands.X + spectrum.Bands.Y + spectrum.Bands.Z;
        var energy = Min(1f, spectrum.Rms * 0.42f + bandSum / 3 * 0.34f + spectrum.Energy * 0.24f);
        var change = sound.Flux * transient;
        var onsets = new Vector3(sound.Beat, sound.Flux, sound.TrebleBeat) * transient;
        var pulse = Min(1f, onsets.X * 0.72f + Max(onsets.X * 0.32f, Max(onsets.Y * 0.42f, onsets.Z * 0.50f)) + change * 0.22f);
        Targets(in spectrum, energy, onsets, pulse, sensitivity);
        for (var index = 0; index < Atlas.Length; index++)
        {
            if (_targeted[index] && !_inBank[index])
            {
                _inBank[index] = true;
                _bank[index] = new Mode { Phase = AtlasSeed[index] * 2 * MathF.PI };
            }
        }
        for (var index = 0; index < Atlas.Length; index++)
        {
            if (!_inBank[index]) continue;
            ref var mode = ref _bank[index];
            var target = _targeted[index] ? _targets[index] : default;
            mode.Weight = Follow(mode.Weight, target.Weight, patternDelta, 0.12f, 0.27f);
            mode.Excitation = Follow(mode.Excitation, Min(1f, target.Excitation * gain), patternDelta, 0.03f, 0.18f);
            mode.Pulse = Max(mode.Pulse, Min(1f, target.Pulse * gain)) * MathF.Exp(-delta / 0.072f);
            var rate = 0.06f + AtlasNorm[index] * 0.32f + mode.Weight * 0.3f
                + mode.Pulse * (0.5f + AtlasSeed[index] * 0.3f);
            mode.Phase = (mode.Phase + delta * rate * motionScale) % (2 * MathF.PI);
            if (mode.Weight < 0.0001f && target.Weight == 0) _inBank[index] = false;
        }

        float magnitude = 0, harmonic = 0;
        for (var p = 0; p < spectrum.PeakCount; p++)
        {
            magnitude += spectrum.Peaks[p].Magnitude;
        }
        for (var p = 0; p < spectrum.PeakCount; p++)
        {
            harmonic += spectrum.Peaks[p].Magnitude * spectrum.Peaks[p].Harmonics;
        }
        var harmonicity = harmonic / Max(0.000001f, magnitude);

        var frame = new WavefieldFrame();
        // The strongest (ties: lowest index) modes above 0.01, at most eight: a selection pass over the bank.
        Span<bool> taken = stackalloc bool[Atlas.Length];
        taken.Clear();
        for (var rank = 0; rank < 8; rank++)
        {
            var best = -1;
            for (var index = 0; index < Atlas.Length; index++)
            {
                if (!_inBank[index] || taken[index] || !(_bank[index].Weight > 0.01f)) continue;
                if (best < 0 || _bank[index].Weight > _bank[best].Weight) best = index;
            }
            if (best < 0) break;
            taken[best] = true;
            ref readonly var mode = ref _bank[best];
            frame.ModeStorage[rank] = Uniforms(best, mode.Weight, mode.Phase, mode.Weight * 0.7f + harmonicity * 0.3f,
                rank == 0 ? 0 : rank < 3 ? 0.3f : 0.7f, mode.Excitation, mode.Pulse);
            frame.ModeCount = rank + 1;
        }
        if (frame.ModeCount == 0) return Ambient(time);
        // Calm frames never let the sensitivity amplify energy beyond the field's own level; their onsets, change and
        // pulse are already zero.
        frame.BandsAndRms = new Vector4(_bands, _rms);
        frame.OnsetsAndEnergy = new Vector4(onsets * gain, Min(1f, energy * (reduced ? Min(1f, gain) : gain)));
        frame.Change = Min(1f, change * gain);
        frame.Pulse = Min(1f, pulse * gain);
        return frame;
    }

    /// <summary>Fills <see cref="_targets"/> (flagged in <see cref="_targeted"/>) for this frame's peaks.</summary>
    private void Targets(in WavefieldSpectrum spectrum, float energy, Vector3 onsets, float pulse, float sensitivity)
    {
        Array.Clear(_targeted);
        Array.Clear(_targets);
        var any = false;
        var strongestPeak = spectrum.PeakCount > 0 ? spectrum.Peaks[0].Magnitude : 0;
        Span<int> neighbours = stackalloc int[4];
        for (var rank = 0; rank < spectrum.PeakCount; rank++)
        {
            var peak = spectrum.Peaks[rank];
            var peakEnergy = Min(1f, peak.Magnitude * 120);
            var band = WavefieldSpectrum.Band(peak.Frequency);
            var weight = peak.Magnitude / strongestPeak * (0.4f + peakEnergy * 0.6f)
                * (0.7f + peak.Harmonics * 0.3f) * BandScales[band] / (1 + rank * 0.6f);
            // Log-distance is monotonic on each side of the insertion point: only the two neighbours on each side can
            // be among the closest two.
            var upper = Atlas.Length;
            for (var i = 0; i < Atlas.Length; i++)
            {
                if (AtlasFrequency[i] >= peak.Frequency) { upper = i; break; }
            }
            int from = Max(0, upper - 2), to = Min(Atlas.Length, upper + 2), count = 0;
            for (var candidate = from; candidate < to; candidate++)
            {
                // Insertion sort with the Mac's comparator: log distance, then m + n, then index.
                var at = count++;
                while (at > 0 && Closer(candidate, neighbours[at - 1], peak.Frequency))
                {
                    neighbours[at] = neighbours[at - 1];
                    at--;
                }
                neighbours[at] = candidate;
            }
            for (var share = 0; share < Min(2, count); share++)
            {
                var index = neighbours[share];
                var portion = share == 0 ? 1f : 0.42f;
                var onset = onsets[AtlasBand[index]];
                ref var target = ref _targets[index];
                target.Weight += weight * portion;
                target.Excitation += (peakEnergy * 0.6f + energy * 0.3f + spectrum.Rms * 0.2f) * weight * portion;
                target.Pulse = Max(target.Pulse, Min(1f, pulse * 0.5f + onset * 0.5f + onsets.X * 0.4f) * portion);
                _targeted[index] = true;
                any = true;
            }
        }
        if (!any && spectrum.Rms > 0.0001f)
        {
            var frequency = 70 * MathF.Pow(7_200f / 70, spectrum.Centroid);
            var best = 0;
            for (var i = 1; i < Atlas.Length; i++)
            {
                if (MathF.Abs(MathF.Log2(AtlasFrequency[i] / frequency)) < MathF.Abs(MathF.Log2(AtlasFrequency[best] / frequency))) best = i;
            }
            _targets[best] = new Target { Weight = spectrum.Rms, Excitation = energy, Pulse = pulse };
            _targeted[best] = true;
            any = true;
        }
        if (!any)
        {
            return;
        }
        var strongest = float.NegativeInfinity;
        for (var i = 0; i < Atlas.Length; i++)
        {
            if (_targeted[i]) strongest = Max(strongest, _targets[i].Weight);
        }
        if (!(strongest > 0.000001f))
        {
            Array.Clear(_targeted);
            return;
        }
        var level = Min(1f, MathF.Pow((spectrum.Rms + energy) * 0.5f, 1 / 1.5f) * 1.8f * sensitivity);
        for (var i = 0; i < Atlas.Length; i++)
        {
            if (!_targeted[i]) continue;
            ref var target = ref _targets[i];
            target = new Target
            {
                Weight = MathF.Pow(target.Weight / strongest, 1.72f) * (0.55f + 0.45f * level),
                Excitation = Min(1f, MathF.Pow(Min(1f, target.Excitation / strongest), 1 / 1.5f) * 1.2f * 1.45f),
                Pulse = target.Pulse,
            };
        }
    }

    /// <summary>The Mac's neighbour order: is atlas entry <paramref name="a"/> closer to the peak than <paramref name="b"/>?</summary>
    private static bool Closer(int a, int b, float frequency)
    {
        var distanceA = MathF.Abs(MathF.Log2(AtlasFrequency[a] / frequency));
        var distanceB = MathF.Abs(MathF.Log2(AtlasFrequency[b] / frequency));
        if (MathF.Abs(distanceA - distanceB) > 0.000001f) return distanceA < distanceB;
        float sumA = Atlas[a].M + Atlas[a].N, sumB = Atlas[b].M + Atlas[b].N;
        if (sumA != sumB) return sumA < sumB;
        return a < b;
    }

    private static WavefieldFrame Ambient(float time)
    {
        var shimmer = 0.5f + 0.5f * MathF.Sin(time * 0.42f);
        var frame = new WavefieldFrame();
        for (var index = 0; index < 12; index++)
        {
            var atlasIndex = index + 4;
            var rate = 0.08f + AtlasNorm[atlasIndex] * 0.18f;
            var offset = index * 0.73f;
            var wave = 0.5f + 0.5f * MathF.Sin(time * rate + offset);
            var phase = AtlasSeed[atlasIndex] * 2 * MathF.PI;
            var coherence = 0.42f + AtlasNorm[atlasIndex] * 0.28f;
            var layer = index % 3 == 0 ? 1f : 0f;
            frame.ModeStorage[index] = Uniforms(atlasIndex, 0.16f + wave * 0.08f, phase, coherence, layer,
                0.18f + wave * 0.08f, 0.08f + wave * 0.08f);
        }
        frame.ModeCount = 12;
        var onset = 0.02f + shimmer * 0.01f;
        frame.BandsAndRms = new Vector4(0.18f + shimmer * 0.03f, 0.24f + shimmer * 0.04f, 0.12f + shimmer * 0.02f,
                                        0.18f + shimmer * 0.04f);
        frame.OnsetsAndEnergy = new Vector4(onset, onset, onset, 0.22f + shimmer * 0.04f);
        frame.Change = 0;
        frame.Pulse = 0.04f;
        return frame;
    }

    private static WavefieldModeUniforms Uniforms(int index, float weight, float phase, float coherence, float layer,
                                                  float excitation, float pulse)
    {
        var resonance = Atlas[index];
        return new WavefieldModeUniforms(new Vector4(resonance.M, resonance.N, weight, AtlasBand[index]),
            new Vector4(phase, coherence, AtlasNorm[index], layer), new Vector4(excitation, pulse, 0, 0));
    }

    private static float Follow(float value, float target, float dt, float attack, float release) =>
        value + (target - value) * (1 - MathF.Exp(-dt / (target > value ? attack : release)));
}
