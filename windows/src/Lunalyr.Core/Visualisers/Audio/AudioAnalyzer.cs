// Adapted from Synesthia by Jon Jaques (MIT), via Lunalyr for Mac's AudioAnalyzer.swift.

using System.Diagnostics;
using static Lunalyr.Core.Visualisers.SwiftMath;

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// One spectrum handed to a spectrum observer (<see cref="AudioAnalyzer.ObserveSpectra"/>). Valid only during the call.
/// </summary>
public readonly ref struct SpectrumPass
{
    /// <summary>
    /// Calibrated one-sided power of the plain (not zero-padded) window: bin k sits at k × <see cref="BinHz"/>, and a
    /// sinusoid of amplitude A sums to A²/2 over its main lobe. Bin 0 (DC and the packed Nyquist term) is zero.
    /// </summary>
    public ReadOnlySpan<float> Power { get; }

    public float BinHz { get; }

    /// <summary>Capture time of the window's newest sample, on the analyser's clock.</summary>
    public double Time { get; }

    /// <summary>Audio seconds since the previous spectrum.</summary>
    public double Dt { get; }

    /// <summary>
    /// Stream position of the window's newest sample: samples received since this analyser was created, which a reset
    /// or a new sample rate does not rewind (<see cref="AudioAnalyzer.Align"/> takes another's). The two channels'
    /// spectra of the same capture buffers share it.
    /// </summary>
    public long Position { get; }

    public SpectrumPass(ReadOnlySpan<float> power, float binHz, double time, double dt, long position)
    {
        Power = power;
        BinHz = binHz;
        Time = time;
        Dt = dt;
        Position = position;
    }
}

/// <summary>Receives a spectrum on the appending thread, with the analyser's lock held.</summary>
public delegate void SpectrumObserver(in SpectrumPass pass);

/// <summary>
/// Streams mono samples in from a capture thread, analyses them, and keeps a short timestamped history that readers
/// sample under a lock.
/// </summary>
/// <remarks>
/// <para><b>The pipeline.</b></para>
/// <list type="number">
/// <item>Capture code (any one thread at a time) calls <see cref="Append(ReadOnlySpan{float}, double, double)"/> with
/// PCM as it arrives. Samples land in a ring buffer.</item>
/// <item>Every hop (half a window) an analysis pass runs over the newest window: Hann window → FFT → 64 log-spaced
/// bands → auto-gain dB mapping → smoothing → derived features (flux, centroid, registers, …). The window lasts about
/// 43 ms at every sample rate (2048 samples at 48 kHz, 4096 at 96 kHz), so bins stay ~23 Hz apart and levels match
/// across devices; all smoothing uses time constants in seconds.</item>
/// <item>A fast time-domain path filters each sample into a bass and a treble band and detects kicks and hi-hats on
/// ~5 ms blocks. Its onsets carry their own timestamps, so <see cref="AudioSnapshot.Beat"/> does not wait for the next
/// FFT hop.</item>
/// <item>Each pass is stored with the time of its newest sample. The render loop calls <see cref="Latest(double)"/> with
/// the time it wants to show and gets a value-type copy built from the matching pass.</item>
/// <item>While a spectrum observer is set (<see cref="ObserveSpectra"/>), every quarter window it receives a calibrated
/// power spectrum of the plain window: the full pass's own bins, and one extra plain FFT halfway between passes.
/// <see cref="VisualizerAudio"/>'s sound-type detector runs on these.</item>
/// </list>
/// <para><b>Threading.</b> Shared between the capture thread and the render thread; one lock guards all mutable state.
/// Neither the writer path nor <see cref="Latest(double)"/> allocates: every buffer is preallocated for the largest
/// supported window and reused, and readers copy values out.</para>
/// <para><b>Clock.</b> Times are seconds on a monotonic clock (the Mac uses <c>CACurrentMediaTime</c>); the default is
/// <see cref="Now"/>, and a constructor argument replaces it.</para>
/// </remarks>
public sealed class AudioAnalyzer
{
    /// <summary>The analysis window lasts about this long at every sample rate.</summary>
    public const double WindowSeconds = 0.0427;
    public const int MaximumFFTSize = 8192;
    /// <summary>
    /// The window is zero-padded to this many times its length before the FFT. That samples the same 43 ms spectrum
    /// every ~6 Hz instead of every ~23 Hz, so a bass note's band follows its real frequency rather than snapping to
    /// the nearest coarse bin. It adds no latency: the window itself is unchanged.
    /// </summary>
    public const int Oversampling = 4;
    public const int MaximumTransformSize = MaximumFFTSize * Oversampling;
    /// <summary>Ring buffer length: enough for a delayed 8192-sample window at 192 kHz.</summary>
    public const int HistoryCapacity = 65_536;
    /// <summary>Analysis passes kept for delayed reads (~0.35 s of history).</summary>
    public const int PassCapacity = 16;
    /// <summary>An unusually large buffer analyses only its newest hops.</summary>
    public const int MaximumPassesPerAppend = 32;
    public const double MinimumFrequency = 30.0;
    public const double MaximumFrequency = 16_000.0;
    /// <summary>
    /// The history holds the input minus its DC offset (a one-pole high-pass at this cutoff, in Hz). An inaudible offset
    /// of 0.002 used to read as a bass glow at half height and kept a silent source "audible" for ever.
    /// </summary>
    public const double DcCutoff = 5.0;

    /// <summary>Edges of the eight named components, in Hz.</summary>
    public static ReadOnlySpan<double> ComponentEdges => [30, 60, 150, 400, 1200, 3000, 6000, 10_000, 16_000];

    /// <summary>Edges of bass, mid and treble, in Hz.</summary>
    public static ReadOnlySpan<double> RegisterEdges => [30, 250, 2000, 16_000];

    /// <summary>
    /// Time constants in seconds. At 48 kHz they reproduce the per-pass factors this analyser used to apply every 1024
    /// samples, so the look does not speed up at 96 kHz or slow at 44.1 kHz.
    /// </summary>
    public static class TimeConstant
    {
        public const double BandAttack = 0.0203;
        public const double BandRelease = 0.167;
        public const double Flux = 0.1415;
        public const double Centroid = 0.2025;
        public const double KickAverage = 0.8425;
        public const double KickEnvelope = 0.2025;
        public const double TrebleAverage = 0.5226;
        public const double TrebleEnvelope = 0.0956;
        public const double AgcDown = 0.1313;
        public const double AgcUp = 1.056;
        public const double IdleSlow = 0.20, IdleMid = 0.16, IdleFast = 0.10;
    }

    /// <summary>Where the tracker steers programme peaks, in dB.</summary>
    private const float AgcReferenceDB = -8;
    /// <summary>The tracker only updates above this; silence must hold the gain.</summary>
    private const float AgcGateDB = -55;
    /// <summary>Gain clamp; the boost cap keeps a whisper-quiet source's noise floor out of the visible range.</summary>
    private const float AgcMaxBoostDB = 24;
    private const float AgcMaxCutDB = 12;
    /// <summary>Peak decay: quiet passages within a song brighten over seconds, so musical dynamics survive.</summary>
    private const float AgcPeakDecayDBPerSecond = 2.484f;
    /// <summary>
    /// vDSP_HANN_NORM's gain: w[n] = 0.8165 × (1 − cos(2πn/N)), i.e. √(2/3), which makes Σw² = N. The band dB mapping
    /// was tuned with it.
    /// </summary>
    private static readonly double HannNormGain = Math.Sqrt(2.0 / 3.0);
    /// <summary>Each band covers at most its width in bins plus two partial bins, and each bin segment contributes two weights.</summary>
    private const int WeightCapacity = 2 * (MaximumTransformSize / 2 + 2 * AudioSnapshot.BandCount);
    private const int ScalarsPerPass = 7;

    private readonly Lock _lock = new();
    private readonly Func<double> _now;
    private readonly RealFft _fft = new(MaximumTransformSize);

    // Configuration, rebuilt when the sample rate changes
    private double _sampleRate = 48_000;
    private int _fftSize = 2048;
    private int _hop = 1024;
    private double _hopSeconds = 1024.0 / 48_000;
    /// <summary>Bins of the zero-padded transform: oversampling × fftSize / 2.</summary>
    private int _binCount = 4096;
    /// <summary>
    /// Hann window: a raised-cosine fade applied to the input so the chunk's hard edges don't smear energy across the
    /// whole spectrum ("spectral leakage").
    /// </summary>
    private readonly float[] _window = new float[MaximumFFTSize];
    /// <summary>Band b's mean power is Σ weightValues[i] × power[weightBins[i]] for i in weightStart[b]..&lt;weightStart[b + 1].</summary>
    private readonly int[] _weightStart = new int[AudioSnapshot.BandCount + 1];
    private readonly int[] _weightBins = new int[WeightCapacity];
    private readonly float[] _weightValues = new float[WeightCapacity];
    /// <summary>Which bands make up each named component and each register: [lower, upper).</summary>
    private readonly int[] _componentLower = new int[AudioSnapshot.ComponentCount], _componentUpper = new int[AudioSnapshot.ComponentCount];
    private readonly int[] _registerLower = new int[3], _registerUpper = new int[3];

    // Sample history
    private readonly float[] _history = new float[HistoryCapacity];
    /// <summary>Samples received since the last reset; the ring index is written % capacity.</summary>
    private long _written;
    /// <summary>Samples received since the last analysis pass.</summary>
    private int _pending;
    /// <summary>When the newest sample was captured.</summary>
    private double _newestSampleTime = double.NegativeInfinity;
    private float _dcPole = 1;
    private float _dcInput, _dcOutput;

    // Analysis scratch
    /// <summary>The windowed samples followed by zero padding (see <see cref="Oversampling"/>).</summary>
    private readonly float[] _windowed = new float[MaximumTransformSize];
    private readonly float[] _power = new float[MaximumTransformSize / 2];
    /// <summary>This pass's unsmoothed band values, swapped with previousRaw at the end of each pass.</summary>
    private float[] _raw = new float[AudioSnapshot.BandCount];
    private float[] _previousRaw = new float[AudioSnapshot.BandCount];
    /// <summary>Per-band values after asymmetric attack/release smoothing.</summary>
    private readonly float[] _smoothed = new float[AudioSnapshot.BandCount];
    private float _fluxEnvelope;
    private float _centroidSmoothed = 0.4f;

    // Stored passes (flat, preallocated)
    private readonly double[] _passTime = new double[PassCapacity];
    private readonly float[] _passBands = new float[PassCapacity * AudioSnapshot.BandCount];
    private readonly float[] _passComponents = new float[PassCapacity * AudioSnapshot.ComponentCount];
    private readonly float[] _passWaveform = new float[PassCapacity * AudioSnapshot.WaveformCount];
    private readonly float[] _passPower = new float[PassCapacity * MaximumFFTSize / 2];
    /// <summary>level, bass, mid, treble, flux, centroid, rms per pass.</summary>
    private readonly float[] _passScalars = new float[PassCapacity * ScalarsPerPass];
    private readonly int[] _passBinCount = new int[PassCapacity];
    private readonly float[] _passBinHz = new float[PassCapacity];
    private readonly int[] _passFFTSize = new int[PassCapacity];
    private int _passCount;

    // Fast transient path
    private Biquad _lowFilter, _highFilter;
    private int _blockLength = 256;
    private int _blockFill;
    private float _blockLow, _blockHigh, _previousBlockLow;
    private float _kickAverage, _trebleAverage;
    /// <summary>
    /// One-shot latches with hysteresis: set on the block that fires an onset, cleared only once the energy falls back
    /// below the release threshold, so a held note hovering at the trigger threshold cannot re-fire every few blocks.
    /// </summary>
    private bool _kickLatched, _trebleLatched;
    private readonly double[] _kickOnsets = new double[4];
    private readonly double[] _trebleOnsets = new double[4];
    private int _kickOnsetCount, _trebleOnsetCount;

    // Spectra for the sound-type detector
    private SpectrumObserver? _spectrumObserver;
    private readonly float[] _spectrum = new float[MaximumFFTSize / 2];
    /// <summary>|X|² of vDSP's packed real FFT (2× the DFT) to calibrated power: 1 / (2 · N · Σw²).</summary>
    private float _spectrumScale = 1;
    /// <summary>written at the previous spectrum.</summary>
    private long _spectrumPosition;
    /// <summary>Samples received in all (see <see cref="SpectrumPass.Position"/>).</summary>
    private long _streamPosition;

    // Loudness normalisation (a slow AGC): the fixed dB mappings assume mastered-music levels; when enabled, these
    // adapt them to the actual programme level.
    private bool _agcEnabled = true;
    /// <summary>Slow-moving estimate of the programme's peak short-term level, in dB.</summary>
    private float _agcPeakDB = AgcReferenceDB;
    /// <summary>Set on the tracker's first audible pass; until then the peak snaps to the observed level.</summary>
    private bool _agcPrimed;
    /// <summary>The gain currently added to both dB mappings, in dB.</summary>
    private float _agcGainDB;

    /// <param name="now">The clock in seconds; <see cref="Now"/> when omitted.</param>
    public AudioAnalyzer(Func<double>? now = null)
    {
        _now = now ?? Now;
        Configure(48_000);
    }

    /// <summary>A monotonic clock in seconds (the Mac's <c>CACurrentMediaTime</c>).</summary>
    public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>The FFT length whose window is closest to <see cref="WindowSeconds"/> (log scale).</summary>
    public static int FftSize(double sampleRate)
    {
        var exponent = Round(Math.Log2(sampleRate * WindowSeconds));
        return 1 << (int)Min(13, Max(9, exponent));
    }

    /// <summary>Centre frequency of band <paramref name="index"/>, in Hz, for a sample rate.</summary>
    public static double BandCentre(int index, double sampleRate)
    {
        var top = Min(MaximumFrequency, sampleRate * 0.45);
        return MinimumFrequency * Math.Pow(top / MinimumFrequency, (index + 0.5) / AudioSnapshot.BandCount);
    }

    // MARK: - Reading

    /// <summary>The newest analysis. Called once per rendered frame.</summary>
    public AudioSnapshot Latest() => Latest(_now());

    /// <summary>
    /// The analysis as of <paramref name="time"/>: the newest pass whose samples had all arrived by then, with transient
    /// envelopes and the idle fade evaluated at exactly that time. Never allocates.
    /// </summary>
    public AudioSnapshot Latest(double time)
    {
        var snapshot = new AudioSnapshot();
        Span<float> spectrum = stackalloc float[MaximumFFTSize / 2];
        var spectrumBins = -1;
        float binHz = 0, rms = 0;
        var fftSize = 0;
        double idle;
        lock (_lock)
        {
            var slot = PassSlot(time);
            if (slot >= 0)
            {
                _passBands.AsSpan(slot * AudioSnapshot.BandCount, AudioSnapshot.BandCount).CopyTo(snapshot.Bands);
                _passComponents.AsSpan(slot * AudioSnapshot.ComponentCount, AudioSnapshot.ComponentCount).CopyTo(snapshot.Components);
                _passWaveform.AsSpan(slot * AudioSnapshot.WaveformCount, AudioSnapshot.WaveformCount).CopyTo(snapshot.Waveform);
                var scalars = slot * ScalarsPerPass;
                snapshot.Level = _passScalars[scalars];
                snapshot.Bass = _passScalars[scalars + 1];
                snapshot.Mid = _passScalars[scalars + 2];
                snapshot.Treble = _passScalars[scalars + 3];
                snapshot.Flux = _passScalars[scalars + 4];
                snapshot.Centroid = _passScalars[scalars + 5];
                spectrumBins = _passBinCount[slot];
                _passPower.AsSpan(slot * MaximumFFTSize / 2, spectrumBins).CopyTo(spectrum);
                binHz = _passBinHz[slot];
                fftSize = _passFFTSize[slot];
                rms = _passScalars[scalars + 6];
            }
            snapshot.Beat = Envelope(_kickOnsets, time, TimeConstant.KickEnvelope);
            snapshot.TrebleBeat = Envelope(_trebleOnsets, time, TimeConstant.TrebleEnvelope);
            // Fade toward silence if the source stopped feeding us: without this the last frame of audio would freeze
            // on screen when capture stops.
            idle = time - _newestSampleTime - 0.25;
        }

        if (idle > 0)
        {
            var slow = (float)Math.Exp(-idle / TimeConstant.IdleSlow);
            var mid = (float)Math.Exp(-idle / TimeConstant.IdleMid);
            var fast = (float)Math.Exp(-idle / TimeConstant.IdleFast);
            for (var i = 0; i < AudioSnapshot.BandCount; i++) snapshot.Bands[i] *= slow;
            for (var i = 0; i < AudioSnapshot.ComponentCount; i++) snapshot.Components[i] *= slow;
            for (var i = 0; i < AudioSnapshot.WaveformCount; i++) snapshot.Waveform[i] *= mid;
            snapshot.Level *= slow; snapshot.Bass *= slow; snapshot.Mid *= slow; snapshot.Treble *= slow;
            snapshot.Flux *= fast;
        }
        else if (spectrumBins >= 0)
        {
            snapshot.Wavefield = new WavefieldSpectrum(spectrum[..spectrumBins], binHz, fftSize, rms);
        }
        snapshot.Flux = Min(snapshot.Flux, 1);
        return snapshot;
    }

    /// <summary>The newest 2048 samples Wavefield's own analyser expects, as of now.</summary>
    public WavefieldPCMWindow? WavefieldPcm() => WavefieldPcm(_now());

    /// <summary>The newest 2048 samples Wavefield's own analyser expects, as of <paramref name="time"/>.</summary>
    public WavefieldPCMWindow? WavefieldPcm(double time) => Pcm(time, 2048);

    /// <summary>
    /// <paramref name="count"/> samples (at most <see cref="HistoryCapacity"/> / 2) ending with the newest sample
    /// captured by <paramref name="time"/>, or null if capture has stopped or not begun. Allocates the returned array
    /// (as the Mac does); <see cref="ReadPcm"/> fills a caller's buffer instead.
    /// </summary>
    public WavefieldPCMWindow? Pcm(double time, int count)
    {
        lock (_lock)
        {
            if (!PcmAvailable(time)) return null;
            var samples = new float[Min(HistoryCapacity / 2, Max(1, count))];
            var info = ReadPcmLocked(time, samples);
            return new WavefieldPCMWindow(samples, info.SampleRate, info.Level, info.End);
        }
    }

    /// <summary>
    /// The allocation-free <see cref="Pcm"/>: fills <paramref name="destination"/> (at most
    /// <see cref="HistoryCapacity"/> / 2 samples of it) with the samples ending with the newest one captured by
    /// <paramref name="time"/>. False, with nothing written, if capture has stopped or not begun.
    /// </summary>
    public bool ReadPcm(double time, Span<float> destination, out WavefieldPCMInfo info)
    {
        lock (_lock)
        {
            if (destination.IsEmpty || !PcmAvailable(time))
            {
                info = default;
                return false;
            }
            info = ReadPcmLocked(time, destination[..Min(HistoryCapacity / 2, destination.Length)]);
            return true;
        }
    }

    private bool PcmAvailable(double time) => _passCount > 0 && time - _newestSampleTime <= 0.25;

    private WavefieldPCMInfo ReadPcmLocked(double time, Span<float> samples)
    {
        var count = samples.Length;
        var late = Max(0, _newestSampleTime - time);
        var end = Max(0L, _written - (long)Round(late * _sampleRate));
        // Never read samples the ring has already overwritten.
        var available = (int)Max(0L, Min(Min((long)count, end), HistoryCapacity - (_written - end)));
        samples[..(count - available)].Clear();
        ReadHistory(end, available, samples[(count - available)..]);
        var slot = PassSlot(time);
        var rms = slot >= 0 ? _passScalars[slot * ScalarsPerPass + 6] : 0;
        return new WavefieldPCMInfo(_sampleRate, Min(1, Max(0, rms * 2.4f)), end);
    }

    /// <summary>Samples received so far, for readers that want everything since their last frame.</summary>
    public long SampleCount
    {
        get
        {
            lock (_lock) return _written;
        }
    }

    /// <summary>
    /// Turns loudness normalisation on or off. Callable from any thread; takes effect on the next analysis pass, and the
    /// applied gain ramps back rather than jumping.
    /// </summary>
    public void SetAutoGainEnabled(bool enabled)
    {
        lock (_lock) _agcEnabled = enabled;
    }

    /// <summary>
    /// Clears all analysis state; called when the user switches audio sources so the old source's tail doesn't bleed
    /// into the new one.
    /// </summary>
    public void Reset()
    {
        lock (_lock) ResetLocked();
    }

    /// <summary>
    /// Hands <paramref name="observer"/> a spectrum every quarter window (~10.7 ms): each full pass's own bins, and one
    /// extra plain FFT halfway between passes. It is called on the appending thread with this analyser's lock held, so
    /// it must not block, allocate or call back into this analyser. Null stops it, and the extra FFTs with it.
    /// </summary>
    public void ObserveSpectra(SpectrumObserver? observer)
    {
        lock (_lock) _spectrumObserver = observer;
    }

    /// <summary>
    /// Restarts this analyser, with an empty history, at <paramref name="other"/>'s sample rate, stream position and hop
    /// phase. Fed the same buffers from then on, the two analyse every instant at the same position:
    /// <see cref="VisualizerAudio"/> aligns the right channel's analyser when the input turns stereo, as it sat idle
    /// while the input was mono. Call on the appending thread.
    /// </summary>
    public void Align(AudioAnalyzer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(other, this)) return;
        double rate;
        long position, written, sinceSpectrum;
        int phase;
        lock (other._lock)
        {
            rate = other._sampleRate;
            position = other._streamPosition;
            phase = other._pending;
            written = other._written;
            sinceSpectrum = other._written - other._spectrumPosition;
        }
        lock (_lock)
        {
            if (rate != _sampleRate) Configure(rate); else ResetLocked();
            _streamPosition = position;
            _pending = phase;
            // The history stays silent; counting on from `written` keeps the window and the spacing of spectra (dt)
            // identical to the other's.
            _written = written;
            _spectrumPosition = written - sinceSpectrum;
        }
    }

    // MARK: - Writing

    /// <summary>Appends mono samples captured just now.</summary>
    public void Append(ReadOnlySpan<float> samples, double sampleRate) => Append(samples, sampleRate, _now());

    /// <summary>
    /// Appends mono samples whose last sample was captured at <paramref name="hostTime"/> and runs every analysis pass
    /// and onset block they complete. Never allocates.
    /// </summary>
    public void Append(ReadOnlySpan<float> samples, double sampleRate, double hostTime)
    {
        var count = samples.Length;
        if (count <= 0 || count > 192_000 || !double.IsFinite(sampleRate) || sampleRate < 8000 || sampleRate > 192_000
            || !double.IsFinite(hostTime)) return;
        lock (_lock)
        {
            if (sampleRate != _sampleRate) Configure(sampleRate);
            SettleAfterGap(hostTime - count / sampleRate);
            var firstIndex = _written;
            var firstAnalysed = count - MaximumPassesPerAppend * _hop;
            // Process in hop-sized chunks so every completed hop is analysed at its own position and time. A spectrum
            // observer also gets the halfway point of each hop.
            var half = _hop / 2;
            var offset = 0;
            while (offset < count)
            {
                var boundary = _spectrumObserver is not null && _pending < half ? half : _hop;
                var chunk = Min(count - offset, boundary - _pending);
                WriteHistory(samples.Slice(offset, chunk));
                DetectTransients(chunk, firstIndex + offset, firstIndex + count - 1, hostTime);
                offset += chunk;
                _pending += chunk;
                _streamPosition += chunk;
                var time = hostTime - (count - offset) / sampleRate;
                if (_pending >= _hop)
                {
                    _pending = 0;
                    if (offset >= firstAnalysed)
                    {
                        ProcessLocked(time);
                        ObserveSpectrum(time, fullPass: true);
                    }
                }
                else if (boundary == half && _pending == half && offset >= firstAnalysed)
                {
                    ObserveSpectrum(time, fullPass: false);
                }
            }
            _newestSampleTime = hostTime;
        }
    }

    // MARK: - Configuration (call with the lock held)

    private void Configure(double sampleRate)
    {
        _sampleRate = sampleRate;
        _fftSize = FftSize(sampleRate);
        var transformSize = _fftSize * Oversampling;
        _hop = _fftSize / 2;
        _hopSeconds = _hop / sampleRate;
        _binCount = transformSize / 2;
        _dcPole = (float)Math.Exp(-2 * Math.PI * DcCutoff / sampleRate);
        // vDSP_hann_window with vDSP_HANN_NORM: the periodic form, scaled.
        for (var n = 0; n < _fftSize; n++)
            _window[n] = (float)(HannNormGain * (1 - Math.Cos(2 * Math.PI * n / _fftSize)));
        float sumSquares = 0;
        for (var n = 0; n < _fftSize; n++) sumSquares += _window[n] * _window[n];
        _spectrumScale = 1 / (2 * (float)_fftSize * sumSquares);
        // The padding stays zero: each pass only rewrites the window itself.
        Array.Clear(_windowed);
        RebuildBands();
        _lowFilter = Biquad.LowPass(150, 0.7071, sampleRate);
        _highFilter = Biquad.HighPass(Min(6000, sampleRate * 0.4), 0.7071, sampleRate);
        _blockLength = Max(32, (int)Round(sampleRate * 0.00533));
        ResetLocked();
    }

    private void ResetLocked()
    {
        Array.Clear(_history);
        _written = 0;
        _pending = 0;
        _spectrumPosition = 0;
        _dcInput = 0; _dcOutput = 0;
        _newestSampleTime = double.NegativeInfinity;
        Array.Clear(_smoothed); Array.Clear(_raw); Array.Clear(_previousRaw);
        _fluxEnvelope = 0;
        _centroidSmoothed = 0.4f;
        Array.Fill(_passTime, double.NegativeInfinity);
        _passCount = 0;
        _lowFilter.Clear(); _highFilter.Clear();
        _blockFill = 0; _blockLow = 0; _blockHigh = 0; _previousBlockLow = 0;
        _kickAverage = 0; _trebleAverage = 0;
        _kickLatched = false; _trebleLatched = false;
        Array.Fill(_kickOnsets, double.NegativeInfinity);
        Array.Fill(_trebleOnsets, double.NegativeInfinity);
        _agcPeakDB = AgcReferenceDB;
        _agcPrimed = false;
        _agcGainDB = 0;
    }

    /// <summary>
    /// Precomputes how the FFT's linear bins integrate into each band. Band edges are geometric between 30 Hz and
    /// 16 kHz; each band's value is the mean of the power spectrum over its edges, with the spectrum treated as linear
    /// between bins: a band narrower than a bin takes the interpolated value at its own frequency, and a wide band
    /// averages every bin it covers, weighting the two partial bins at its ends.
    /// </summary>
    private void RebuildBands()
    {
        var binHz = _sampleRate / (_fftSize * Oversampling);
        double fMin = MinimumFrequency, fMax = Min(MaximumFrequency, _sampleRate * 0.45);
        var next = 0;
        for (var band = 0; band < AudioSnapshot.BandCount; band++)
        {
            _weightStart[band] = next;
            var lowHz = fMin * Math.Pow(fMax / fMin, (double)band / AudioSnapshot.BandCount);
            var highHz = fMin * Math.Pow(fMax / fMin, (double)(band + 1) / AudioSnapshot.BandCount);
            // In bin units; bin 0 holds DC and the packed Nyquist term, and the main lobe of DC spans the first few
            // padded bins.
            var low = Max((double)Oversampling, lowHz / binHz);
            var high = Min((double)(_binCount - 1), Max(low + 0.0001, highHz / binHz));
            var width = high - low;
            var bin = (int)Math.Floor(low);
            while (bin < high && bin < _binCount - 1)
            {
                double t0 = Max(low, bin) - bin, t1 = Min(high, bin + 1) - bin;
                // ∫ P(k)(1 - t) + P(k+1) t over [t0, t1], divided by the width.
                var square = (t1 * t1 - t0 * t0) / 2;
                AddWeight(bin, (float)(((t1 - t0) - square) / width), ref next);
                AddWeight(bin + 1, (float)(square / width), ref next);
                bin++;
            }
        }
        _weightStart[AudioSnapshot.BandCount] = next;

        for (var c = 0; c < AudioSnapshot.ComponentCount; c++)
            (_componentLower[c], _componentUpper[c]) = BandRange(ComponentEdges[c], ComponentEdges[c + 1]);
        for (var r = 0; r < 3; r++)
            (_registerLower[r], _registerUpper[r]) = BandRange(RegisterEdges[r], RegisterEdges[r + 1]);
    }

    private (int Lower, int Upper) BandRange(double lowHz, double highHz)
    {
        var lower = AudioSnapshot.BandCount - 1;
        for (var b = 0; b < AudioSnapshot.BandCount; b++)
        {
            if (BandCentre(b, _sampleRate) >= lowHz) { lower = b; break; }
        }
        var upper = lower;
        for (var b = AudioSnapshot.BandCount - 1; b >= 0; b--)
        {
            if (BandCentre(b, _sampleRate) < highHz) { upper = b; break; }
        }
        return (lower, Max(lower + 1, upper + 1));
    }

    private void AddWeight(int bin, float weight, ref int next)
    {
        if (!(weight > 0) || next >= _weightBins.Length) return;
        _weightBins[next] = bin;
        _weightValues[next] = weight;
        next++;
    }

    // MARK: - Writing helpers (call with the lock held)

    /// <summary>
    /// Stores samples with the DC offset removed and non-finite samples replaced by silence, so neither reaches any
    /// analysis or reader.
    /// </summary>
    private void WriteHistory(ReadOnlySpan<float> samples)
    {
        var pole = _dcPole;
        float previousInput = _dcInput, previousOutput = _dcOutput;
        var ring = _history;
        var index = (int)(_written % HistoryCapacity);
        for (var i = 0; i < samples.Length; i++)
        {
            var x = float.IsFinite(samples[i]) ? samples[i] : 0;
            var y = x - previousInput + pole * previousOutput;
            previousInput = x;
            previousOutput = y;
            ring[index] = y;
            index++;
            if (index == HistoryCapacity) index = 0;
        }
        _dcInput = previousInput;
        // Silence decays the filter towards subnormals; stop there.
        _dcOutput = MathF.Abs(previousOutput) < 1e-20f ? 0 : previousOutput;
        _written += samples.Length;
    }

    /// <summary>Copies <paramref name="count"/> samples ending at sample index <paramref name="end"/> (exclusive).</summary>
    private void ReadHistory(long end, int count, Span<float> target)
    {
        if (count <= 0) return;
        var copied = 0;
        while (copied < count)
        {
            var start = (int)((end - count + copied) % HistoryCapacity);
            var run = Min(count - copied, HistoryCapacity - start);
            _history.AsSpan(start, run).CopyTo(target[copied..]);
            copied += run;
        }
    }

    /// <summary>
    /// After a capture gap, decays the smoothed state the way the visuals faded while nothing arrived, so the next note
    /// attacks from there.
    /// </summary>
    private void SettleAfterGap(double firstSampleTime)
    {
        var idle = firstSampleTime - _newestSampleTime - 0.25;
        if (!(idle > 0) || !double.IsFinite(idle)) return;
        var slow = (float)Math.Exp(-idle / TimeConstant.IdleSlow);
        for (var i = 0; i < _smoothed.Length; i++) _smoothed[i] *= slow;
        _fluxEnvelope *= (float)Math.Exp(-idle / TimeConstant.IdleFast);
    }

    // MARK: - Fast onset path (call with the lock held)

    /// <summary>
    /// Filters samples into a bass band (150 Hz low-pass, kick drums) and a treble band (6 kHz high-pass, hi-hats) and
    /// evaluates both on blocks of about 5 ms. Each onset is stamped with the capture time of the block that fired it.
    /// It reads the <paramref name="count"/> samples just written to the history at <paramref name="firstIndex"/>.
    /// </summary>
    private void DetectTransients(int count, long firstIndex, long lastIndex, double hostTime)
    {
        var blockSeconds = _blockLength / _sampleRate;
        var kickFollow = (float)(1 - Math.Exp(-blockSeconds / TimeConstant.KickAverage));
        var trebleFollow = (float)(1 - Math.Exp(-blockSeconds / TimeConstant.TrebleAverage));
        for (var i = 0; i < count; i++)
        {
            var x = _history[(int)((firstIndex + i) % HistoryCapacity)];
            float low = _lowFilter.Process(x), high = _highFilter.Process(x);
            _blockLow += low * low;
            _blockHigh += high * high;
            _blockFill++;
            if (_blockFill != _blockLength) continue;
            float length = _blockLength;
            // Two blocks for the bass: a kick's fundamental needs ~10 ms.
            var lowEnergy = (_blockLow + _previousBlockLow) / (2 * length);
            var lowLevel = Normalized(lowEnergy);
            var highLevel = Normalized(_blockHigh / length);
            _previousBlockLow = _blockLow;
            _blockLow = 0; _blockHigh = 0; _blockFill = 0;
            var time = hostTime - (lastIndex - (firstIndex + i)) / _sampleRate;

            // Kick: 30% above its own slow average (and above an absolute floor, so silence can't trigger) fires once
            // per onset; the latch re-arms only below 1.1× the average.
            _kickAverage += (lowLevel - _kickAverage) * kickFollow;
            if (!_kickLatched && lowLevel > _kickAverage * 1.3f && lowLevel > 0.15f)
            {
                _kickLatched = true;
                _kickOnsets[_kickOnsetCount % _kickOnsets.Length] = time;
                _kickOnsetCount++;
            }
            else if (lowLevel < _kickAverage * 1.1f)
            {
                _kickLatched = false;
            }
            // Hi-hats: the same scheme over the top of the spectrum.
            _trebleAverage += (highLevel - _trebleAverage) * trebleFollow;
            if (!_trebleLatched && highLevel > _trebleAverage * 1.35f && highLevel > 0.10f)
            {
                _trebleLatched = true;
                _trebleOnsets[_trebleOnsetCount % _trebleOnsets.Length] = time;
                _trebleOnsetCount++;
            }
            else if (highLevel < _trebleAverage * 1.1f)
            {
                _trebleLatched = false;
            }
        }
    }

    /// <summary>The band dB mapping applied to a mean-square energy.</summary>
    private float Normalized(float energy)
    {
        var db = 10 * MathF.Log10(Max(energy, 1e-18f));
        return Max(0, Min(1, (db + _agcGainDB + 72) / 66));
    }

    /// <summary>1 at the newest onset no later than <paramref name="time"/>, decaying exponentially.</summary>
    private static float Envelope(double[] onsets, double time, double decay)
    {
        var newest = double.NegativeInfinity;
        foreach (var onset in onsets)
        {
            if (onset <= time && onset > newest) newest = onset;
        }
        if (!double.IsFinite(newest)) return 0;
        return (float)Math.Exp(-(time - newest) / decay);
    }

    // MARK: - Analysis (call with the lock held)

    /// <summary>Fills the window with the newest fftSize samples (zero-padded at the start), times the Hann curve.</summary>
    private void FillWindow()
    {
        var fftSize = _fftSize;
        var available = (int)Min((long)fftSize, _written);
        var target = _windowed.AsSpan(0, fftSize);
        if (available < fftSize) target[..(fftSize - available)].Clear();
        ReadHistory(_written, available, target[(fftSize - available)..]);
        for (var i = 0; i < fftSize; i++) target[i] *= _window[i];
    }

    /// <summary>One full analysis pass over the window ending at the newest sample, stored as captured at <paramref name="time"/>.</summary>
    private void ProcessLocked(double time)
    {
        // 1. Window. 2. FFT of the zero-padded window: vDSP-scaled squared magnitudes per bin.
        FillWindow();
        _fft.PowerSpectrum(_windowed.AsSpan(0, _fftSize * Oversampling), _power);

        // 3. Overall loudness: RMS in dB, and the auto-gain update it feeds, computed before the bands because they
        //    apply this pass's gain.
        var rms = RmsOfNewest(_fftSize);
        var levelDB = 20 * MathF.Log10(Max(rms, 1e-9f));
        UpdateAutoGain(levelDB);
        var level = Max(0, Min(1, (levelDB + _agcGainDB + 60) / 60));

        // 4. Reduce the bins to 64 bands, then map through decibels: −72 dB → 0, −6 dB → 1, shifted by the auto-gain.
        var norm = 1.0f / _fftSize;
        for (var band = 0; band < AudioSnapshot.BandCount; band++)
        {
            float mean = 0;
            for (var i = _weightStart[band]; i < _weightStart[band + 1]; i++) mean += _weightValues[i] * _power[_weightBins[i]];
            var db = 20 * MathF.Log10(Max(MathF.Sqrt(Max(mean, 0)) * norm, 1e-9f));
            _raw[band] = Max(0, Min(1, (db + _agcGainDB + 72) / 66));
        }
        // 5. Asymmetric attack/release smoothing: rise fast when a band jumps but fall slowly.
        var attack = (float)(1 - Math.Exp(-_hopSeconds / TimeConstant.BandAttack));
        var release = (float)(1 - Math.Exp(-_hopSeconds / TimeConstant.BandRelease));
        for (var band = 0; band < AudioSnapshot.BandCount; band++)
        {
            var v = _raw[band];
            _smoothed[band] += (v - _smoothed[band]) * (v > _smoothed[band] ? attack : release);
        }

        // Spectral flux: sum of per-band energy increases since the last pass.
        float fluxNow = 0;
        for (var band = 0; band < AudioSnapshot.BandCount; band++) fluxNow += Max(0, _raw[band] - _previousRaw[band]);
        _fluxEnvelope = Max(Min(fluxNow * 0.9f, 1), _fluxEnvelope * (float)Math.Exp(-_hopSeconds / TimeConstant.Flux));

        // Spectral centroid: energy-weighted mean band index, 0 (dark) ... 1 (bright), updated only when there's enough
        // total energy for the ratio to mean anything.
        float weighted = 0, total = 0;
        for (var band = 0; band < AudioSnapshot.BandCount; band++)
        {
            weighted += _raw[band] * band;
            total += _raw[band];
        }
        if (total > 0.02f)
        {
            var instant = weighted / (total * (AudioSnapshot.BandCount - 1));
            _centroidSmoothed += (instant - _centroidSmoothed) * (float)(1 - Math.Exp(-_hopSeconds / TimeConstant.Centroid));
        }

        // Keep this pass's raw bands for the next flux computation.
        (_raw, _previousRaw) = (_previousRaw, _raw);

        Store(time, level, rms);
    }

    /// <summary>
    /// Hands the spectrum observer the calibrated power of the newest window. After a full pass that is the pass's own
    /// transform: every oversampling-th bin of the padded FFT is exactly a bin of the plain one. Halfway between passes
    /// it is one plain FFT of the window.
    /// </summary>
    private void ObserveSpectrum(double time, bool fullPass)
    {
        var observer = _spectrumObserver;
        if (observer is null) return;
        var bins = _fftSize / 2;
        var scale = _spectrumScale;
        var output = _spectrum.AsSpan(0, bins);
        if (fullPass)
        {
            for (var k = 0; k < bins; k++) output[k] = _power[k * Oversampling] * scale;
        }
        else
        {
            // The padding beyond the window is untouched: the next full pass still sees zeros there.
            FillWindow();
            _fft.PowerSpectrum(_windowed.AsSpan(0, _fftSize), output);
            for (var k = 0; k < bins; k++) output[k] *= scale;
        }
        output[0] = 0;
        var dt = (_written - _spectrumPosition) / _sampleRate;
        _spectrumPosition = _written;
        var binHz = (float)_sampleRate / _fftSize;
        observer(new SpectrumPass(output, binHz, time, dt, _streamPosition));
    }

    /// <summary>The RMS of the window's raw samples (before the Hann curve); zero padding counts toward the mean.</summary>
    private float RmsOfNewest(int count)
    {
        count = (int)Min((long)count, _written);
        if (count <= 0) return 0;
        float sum = 0;
        var done = 0;
        while (done < count)
        {
            var start = (int)((_written - count + done) % HistoryCapacity);
            var run = Min(count - done, HistoryCapacity - start);
            float part = 0;
            foreach (var x in _history.AsSpan(start, run)) part += x * x;
            sum += part;
            done += run;
        }
        return MathF.Sqrt(sum / _fftSize);
    }

    /// <summary>Stores this pass in the next history slot without allocating.</summary>
    private void Store(double time, float level, float rms)
    {
        var slot = _passCount % PassCapacity;
        _passCount++;
        _passTime[slot] = time;
        var bands = slot * AudioSnapshot.BandCount;
        for (var band = 0; band < AudioSnapshot.BandCount; band++) _passBands[bands + band] = _smoothed[band];
        var components = slot * AudioSnapshot.ComponentCount;
        for (var c = 0; c < AudioSnapshot.ComponentCount; c++)
            _passComponents[components + c] = Average(_componentLower[c], _componentUpper[c]);
        var scalars = slot * ScalarsPerPass;
        _passScalars[scalars] = level;
        _passScalars[scalars + 1] = Average(_registerLower[0], _registerUpper[0]);
        _passScalars[scalars + 2] = Average(_registerLower[1], _registerUpper[1]);
        _passScalars[scalars + 3] = Average(_registerLower[2], _registerUpper[2]);
        _passScalars[scalars + 4] = Min(_fluxEnvelope, 1);
        _passScalars[scalars + 5] = _centroidSmoothed;
        _passScalars[scalars + 6] = rms;
        // Downsample the newest hop (~21 ms) to 256 points for the waveform.
        var stride = Max(1, _hop / AudioSnapshot.WaveformCount);
        var span = stride * AudioSnapshot.WaveformCount;
        var waveform = slot * AudioSnapshot.WaveformCount;
        for (var i = 0; i < AudioSnapshot.WaveformCount; i++)
        {
            var index = _written - span + (long)i * stride;
            _passWaveform[waveform + i] = index >= 0 ? _history[(int)(index % HistoryCapacity)] : 0;
        }
        // Wavefield's peak analysis keeps its tuned resolution: every oversampling-th padded bin is exactly a bin of the
        // plain FFT.
        var spectrum = slot * MaximumFFTSize / 2;
        var plainBins = _fftSize / 2;
        for (var bin = 0; bin < plainBins; bin++) _passPower[spectrum + bin] = _power[bin * Oversampling];
        _passBinCount[slot] = plainBins;
        _passBinHz[slot] = (float)_sampleRate / _fftSize;
        _passFFTSize[slot] = _fftSize;

        float Average(int lower, int upper)
        {
            float sum = 0;
            for (var i = lower; i < upper; i++) sum += _smoothed[i];
            return sum / Max(1, upper - lower);
        }
    }

    /// <summary>The newest stored pass captured no later than <paramref name="time"/>; the oldest one when every stored pass is newer; −1 when none.</summary>
    private int PassSlot(double time)
    {
        if (_passCount <= 0) return -1;
        int best = -1, oldest = -1;
        var stored = Min(_passCount, PassCapacity);
        for (var slot = 0; slot < stored; slot++)
        {
            var t = _passTime[slot];
            if (t <= time && (best < 0 || t > _passTime[best])) best = slot;
            if (oldest < 0 || t < _passTime[oldest]) oldest = slot;
        }
        return best >= 0 ? best : oldest;
    }

    /// <summary>
    /// Advances the loudness-normalisation tracker by one analysis pass: track the programme's recent peak short-term
    /// level, aim the gain at reference − peak, and smooth the applied value asymmetrically (down fast, up over ~1 s).
    /// Below the gate nothing updates: silence holds the gain. When disabled the target is 0, so toggling ramps.
    /// </summary>
    private void UpdateAutoGain(float levelDB)
    {
        if (_agcEnabled && levelDB > AgcGateDB)
        {
            if (_agcPrimed)
            {
                _agcPeakDB = Max(levelDB, _agcPeakDB - AgcPeakDecayDBPerSecond * (float)_hopSeconds);
            }
            else
            {
                _agcPeakDB = levelDB;
                _agcPrimed = true;
            }
        }
        var target = _agcEnabled ? Max(-AgcMaxCutDB, Min(AgcMaxBoostDB, AgcReferenceDB - _agcPeakDB)) : 0;
        var tau = target < _agcGainDB ? TimeConstant.AgcDown : TimeConstant.AgcUp;
        _agcGainDB += (target - _agcGainDB) * (float)(1 - Math.Exp(-_hopSeconds / tau));
    }
}

/// <summary>A second-order filter section (RBJ cookbook), transposed direct form II.</summary>
public struct Biquad
{
    private float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    /// <summary>A pass-through section.</summary>
    public Biquad()
    {
        _b0 = 1;
    }

    public static Biquad LowPass(double frequency, double q, double sampleRate)
    {
        double w = 2 * Math.PI * frequency / sampleRate, alpha = Math.Sin(w) / (2 * q), c = Math.Cos(w);
        var a0 = 1 + alpha;
        var b0 = (float)((1 - c) / 2 / a0);
        return new Biquad
        {
            _b0 = b0, _b1 = (float)((1 - c) / a0), _b2 = b0,
            _a1 = (float)(-2 * c / a0), _a2 = (float)((1 - alpha) / a0),
        };
    }

    public static Biquad HighPass(double frequency, double q, double sampleRate)
    {
        double w = 2 * Math.PI * frequency / sampleRate, alpha = Math.Sin(w) / (2 * q), c = Math.Cos(w);
        var a0 = 1 + alpha;
        var b0 = (float)((1 + c) / 2 / a0);
        return new Biquad
        {
            _b0 = b0, _b1 = (float)(-(1 + c) / a0), _b2 = b0,
            _a1 = (float)(-2 * c / a0), _a2 = (float)((1 - alpha) / a0),
        };
    }

    public float Process(float x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        if (!float.IsFinite(y)) { Clear(); return 0; }
        return y;
    }

    public void Clear()
    {
        _z1 = 0;
        _z2 = 0;
    }
}
