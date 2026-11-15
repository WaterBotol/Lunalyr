// Adapted from Synesthia by Jon Jaques (MIT), via Lunalyr for Mac's AudioAnalyzer.swift.

using System.Runtime.CompilerServices;

namespace Lunalyr.Core.Visualisers;

/// <summary>The 64 log-spaced band values of an <see cref="AudioSnapshot"/>, stored inline.</summary>
[InlineArray(AudioSnapshot.BandCount)]
public struct AudioBands
{
    private float _element;
}

/// <summary>The 256 waveform samples of an <see cref="AudioSnapshot"/>, stored inline.</summary>
[InlineArray(AudioSnapshot.WaveformCount)]
public struct AudioWaveform
{
    private float _element;
}

/// <summary>The eight named sub-band energies of an <see cref="AudioSnapshot"/>, stored inline.</summary>
[InlineArray(AudioSnapshot.ComponentCount)]
public struct AudioComponents
{
    private float _element;
}

/// <summary>
/// One frame of analysed audio, safe to hand to the render thread.
/// </summary>
/// <remarks>
/// A plain value type on purpose: the render loop takes a <i>copy</i> once per frame via
/// <see cref="AudioAnalyzer.Latest()"/>, so it can read every field for the rest of the frame with no locking and no
/// chance of the audio thread changing it mid-read. Its fixed-size arrays are stored inline, so copying one never
/// allocates and never shares storage with the analyser.
///
/// All values are pre-normalised to roughly 0...1 (waveform: −1...1) so visualisers can use them directly as
/// intensities without knowing anything about decibels or FFT bin maths.
///
/// Always create one with <c>new AudioSnapshot()</c>: <c>default(AudioSnapshot)</c> skips the constructor, and its
/// sound types then carry onset times of 0 instead of −∞.
/// </remarks>
public struct AudioSnapshot
{
    public const int BandCount = 64;
    public const int WaveformCount = 256;
    public const int ComponentCount = 8;

    /// <summary>
    /// The frequency spectrum, reduced to 64 bands covering 30 Hz–16 kHz (or 0.45 × the sample rate, if lower).
    /// Bands are log-spaced: band edges are geometric in Hz, so every octave spans about seven bands. Each band
    /// integrates the power spectrum between its edges, interpolating between FFT bins. Smoothed and normalised to
    /// roughly 0...1.
    /// </summary>
    public AudioBands Bands;

    /// <summary>
    /// Recent time-domain samples, −1...1, downsampled to 256 points (about 21 ms). Good for drawing
    /// oscilloscope-style shapes; useless for "how loud is the bass" questions (use <see cref="Bands"/>).
    /// </summary>
    public AudioWaveform Waveform;

    /// <summary>
    /// Overall loudness (RMS mapped through dB), 0...1. With loudness normalisation on (the default) the mapping
    /// slowly adapts to the source's programme level.
    /// </summary>
    public float Level;

    /// <summary>Average energy of the low (30–250 Hz) register.</summary>
    public float Bass;

    /// <summary>Average energy of the middle (250 Hz–2 kHz) register.</summary>
    public float Mid;

    /// <summary>Average energy of the high (2–16 kHz) register.</summary>
    public float Treble;

    /// <summary>
    /// Eight named sub-band energies, 0...1 each, over these ranges in Hz: sub-bass 30–60, bass 60–150, low-mid
    /// 150–400, mid 400–1200, high-mid 1.2–3k, presence 3–6k, treble 6–10k, air 10–16k.
    /// </summary>
    public AudioComponents Components;

    /// <summary>
    /// Beat envelope: jumps to 1 on a detected bass transient (a kick drum hit), then decays exponentially.
    /// </summary>
    public float Beat;

    /// <summary>Treble transient envelope (hi-hats, a snare's snap); like <see cref="Beat"/> but faster.</summary>
    public float TrebleBeat;

    /// <summary>Spectral flux: how much new energy arrived this frame, 0...1. Spikes on any onset.</summary>
    public float Flux;

    /// <summary>Spectral centroid mapped to 0...1: low = dark/bassy sound, high = bright/airy.</summary>
    public float Centroid;

    /// <summary>Wavefield's view of the plain FFT: levels and the strongest spectral peaks.</summary>
    public WavefieldSpectrum Wavefield;

    /// <summary>
    /// What type of sound is playing (kick, snare, hat, bass, vocal, pad), evaluated at the presentation time.
    /// Filled by <see cref="VisualizerAudio"/>, which runs one detector over both channels; <see cref="AudioAnalyzer"/>
    /// leaves it empty.
    /// </summary>
    public SoundTypeFeatures Sound;

    public AudioSnapshot()
    {
        Wavefield = new WavefieldSpectrum();
        Sound = new SoundTypeFeatures();
    }
}

/// <summary>An in-memory sample window for Wavefield's own analyser (and the web scenes).</summary>
/// <param name="Samples">The samples, oldest first; the reader owns this array.</param>
/// <param name="SampleRate">The capture sample rate, in Hz.</param>
/// <param name="Level">The window's loudness, 0...1 (the pass's RMS × 2.4).</param>
/// <param name="End">Samples received up to and including the last one in <paramref name="Samples"/>.</param>
public sealed record WavefieldPCMWindow(float[] Samples, double SampleRate, float Level, long End);

/// <summary>What <see cref="AudioAnalyzer.ReadPcm"/> wrote into the caller's buffer.</summary>
/// <param name="SampleRate">The capture sample rate, in Hz.</param>
/// <param name="Level">The window's loudness, 0...1 (the pass's RMS × 2.4).</param>
/// <param name="End">Samples received up to and including the last one written.</param>
public readonly record struct WavefieldPCMInfo(double SampleRate, float Level, long End);
