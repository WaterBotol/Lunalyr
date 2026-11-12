// SoundTypeDetector: what TYPE of sound is playing, for the visualisers. Ported from Lunalyr for Mac's
// SoundTypeDetector.swift.
//
// One calibrated power spectrum per detector pass (the FFT AudioAnalyzer already computes, every quarter window) goes
// in; three transient classes with onset counters and three sustained envelopes come out:
//
//   kick   35-120 Hz onset carried by the low band, confirmed by a beater click (flat 1-6 kHz increment) or by the
//          kick's downward pitch sweep (35-300 Hz centroid falling by 18% within 35 ms)
//   snare  broadband, noise-like mid transient (1-6 kHz, flat increment)
//   hat    very high, short transient (7-16 kHz, top-heavy increment)
//   bass   steady tonal partials of a bass series (f0 35-110 Hz, or an octave jump to 160 Hz) or a lone tone under
//          110 Hz; kick tails are held out
//   vocal  the harmonic lead line whose partials move together in pitch (vibrato, glides): a sung voice, or a sax,
//          violin or synth lead
//   pad    the remaining steady, long-held harmonic bed (chords, strings)
//
// Independence rules:
// - Frequencies are in Hz and mapped through `binHz`, so 44.1, 48 and 96 kHz and any FFT size land on the same regions.
// - Every time constant is in seconds and applied as exp(-dt/tau) with the hop's real audio duration, so neither the
//   hop nor the capture callback size changes the result. Internal timing runs on an audio clock (the sum of `dt`),
//   so a capture clock that bunches buffers together cannot reorder frames; only the published onset times are on the
//   capture clock.
// - Levels are relative: per-band adaptive whitening for onset novelty and a gated programme-level tracker for the
//   sustained classes, so quiet and loud masters behave alike.
//
// The algorithm and every constant come from a prototype validated on synthetic fixtures with ground truth (the
// tests' SoundTypeFixtures carry the same fixtures). Storage is allocated once, in the constructor, for the largest
// spectrum AudioAnalyzer produces, so `Process` never allocates and can run on the analysis worker. Not thread-safe:
// one caller at a time.

using System.Numerics;
using static Lunalyr.Core.Visualisers.SwiftMath;

namespace Lunalyr.Core.Visualisers;

/// <summary>Classifies the sound in each analysis pass into kick, snare, hat, bass, vocal and pad.</summary>
public sealed class SoundTypeDetector
{
    /// <summary>
    /// What each pass publishes. Transient envelopes are not here: the render side evaluates them from the onsets at
    /// its own display time.
    /// </summary>
    public record struct Output
    {
        /// <summary>Sustained envelopes, 0...1.</summary>
        public float Bass;
        public float Vocal;
        public float Pad;
        /// <summary>Onsets detected since the last reset (wrapping).</summary>
        public uint KickOnsets;
        public uint SnareOnsets;
        public uint HatOnsets;
        /// <summary>The newest onset of each class. Its strength may still rise during the top-up after the pass it fired on.</summary>
        public SoundOnset NewestKick = new();
        public SoundOnset NewestSnare = new();
        public SoundOnset NewestHat = new();

        public Output()
        {
        }
    }

    /// <summary>A closed frequency range in Hz.</summary>
    public readonly record struct Range(float Lower, float Upper);

    /// <summary>
    /// Every constant, in Hz, seconds and dB. None is per genre: each was set from measured distributions of the
    /// synthetic fixtures.
    /// </summary>
    public sealed record Tuning
    {
        public Range KickBand { get; init; } = new(35, 120);
        public Range LowMidBand { get; init; } = new(160, 800);
        public Range SnareBand { get; init; } = new(1000, 6000);
        public Range HatBand { get; init; } = new(7000, 16000);
        public Range BassBand { get; init; } = new(35, 220);
        public float BassTaperFrom { get; init; } = 110;
        public Range FlatBand { get; init; } = new(1000, 8000);
        /// <summary>Band peak-hold memory, s.</summary>
        public float WhitenTau { get; init; } = 3.0f;
        /// <summary>log(1 + λ·W) / log(1 + λ).</summary>
        public float Compression { get; init; } = 100;
        /// <summary>Compare with the frame ~21 ms earlier.</summary>
        public float FluxLag { get; init; } = 0.021f;
        /// <summary>Adaptive-threshold statistics, s.</summary>
        public float StatsTau { get; init; } = 1.0f;
        /// <summary>Programme level follower, s.</summary>
        public float ProgramRise { get; init; } = 0.25f;
        public float ProgramFall { get; init; } = 4.0f;
        // Onset thresholds: θ = max(floor, mean + k·dev)
        public float KickK { get; init; } = 2.2f;
        public float KickFloor { get; init; } = 0.12f;
        public float SnareK { get; init; } = 2.4f;
        public float SnareFloor { get; init; } = 0.09f;
        public float HatK { get; init; } = 1.8f;
        public float HatFloor { get; init; } = 0.08f;
        public float KickRefractory { get; init; } = 0.085f;
        public float SnareRefractory { get; init; } = 0.08f;
        public float HatRefractory { get; init; } = 0.06f;
        /// <summary>Re-arm below this fraction of θ.</summary>
        public float Rearm { get; init; } = 0.55f;
        /// <summary>Onset strength may still rise this long, s.</summary>
        public float TopUp { get; init; } = 0.03f;
        // Transient envelopes (the render side's normal shapes), s
        public float KickAttack { get; init; } = 0.010f;
        public float KickRelease { get; init; } = 0.26f;
        public float SnareAttack { get; init; } = 0.010f;
        public float SnareRelease { get; init; } = 0.22f;
        public float HatAttack { get; init; } = 0.008f;
        public float HatRelease { get; init; } = 0.15f;
        // Sustained envelopes, s
        public float BassAttack { get; init; } = 0.06f;
        public float BassRelease { get; init; } = 0.30f;
        public float VocalAttack { get; init; } = 0.08f;
        public float VocalRelease { get; init; } = 0.35f;
        public float PadAttack { get; init; } = 0.30f;
        public float PadRelease { get; init; } = 1.0f;
        // A prominent instance sits `offset` dB below the programme level; the envelope spans `window` dB below that.
        public float BassOffset { get; init; } = 6;
        public float BassWindow { get; init; } = 24;
        public float VocalOffset { get; init; } = 10;
        public float VocalWindow { get; init; } = 22;
        public float PadOffset { get; init; } = 16;
        public float PadWindow { get; init; } = 24;
        /// <summary>An onset is usually detected about this fraction of a window after it starts; published onset times are backdated by it.</summary>
        public float LatencyFraction { get; init; } = 0.30f;
    }

    /// <summary>The most bins a spectrum may have: AudioAnalyzer's 8192-point window.</summary>
    public const int MaximumBinCount = 4096;
    private const int Depth = 8;
    private const int BandCapacity = 128;
    private const int PeakCapacity = 64;
    private const int CarryCapacity = 128;
    private const int SeedCapacity = 8;
    /// <summary>Bounds of a ±40 cent match around a harmonic: 2^(∓40/1200).</summary>
    private const float MatchLow = 0.977_159_9f;
    private const float MatchHigh = 1.023_374_2f;

    private readonly Tuning _tuning;
    private Output _output = new();

    // Layout, rebuilt by Configure
    private float _binHz;
    private int _binCount;
    private int _bandCount;
    private (int Lower, int Upper) _kickBands, _snareBands, _hatBands;
    private int _analysedLo = 1, _analysedHi = 1;
    private Region _kickRegion, _lowMidRegion, _snareRegion, _hatRegion, _bassRegion;
    private int _flatLo = 1, _flatHi = 1;
    private int _highLo = 1, _highHi = 1;
    private int _lowLo = 1, _lowHi;
    private int _peakLo = 2, _peakHi = 1;

    // Storage, allocated once
    private readonly int _binCapacity;
    /// <summary>History ring of sanitised spectra and whitened band levels; slot `ringHead` holds the current pass while it is processed.</summary>
    private readonly float[] _ringP;
    private readonly float[] _ringL;
    private readonly float[] _ringLow;
    private readonly double[] _ringTime;
    private int _ringHead;
    private readonly float[] _increment;
    private readonly float[] _work;
    private readonly float[] _db;
    private readonly float[] _prefix;
    private readonly float[] _binFrequency;
    private readonly float[] _tilt;
    private readonly float[] _regionWeights;
    private readonly int[] _bandStart;
    private readonly int[] _bandEnd;
    private readonly float[] _bandShare;
    private readonly float[] _bandHold;
    private readonly float[] _typical;
    private readonly PeakTable _peaks = new(PeakCapacity);
    private readonly PeakTable _eligible = new(PeakCapacity);
    private readonly CarryTable _previous = new(CarryCapacity);
    private readonly CarryTable _next = new(CarryCapacity);
    private readonly int[] _seeds = new int[SeedCapacity];

    // State
    private double _clock;
    private float _programPower;
    private bool _primed;
    private OnsetTracker _kick;
    private OnsetTracker _snare;
    private OnsetTracker _hat;
    private float _previousOdfSnare;
    private float _previousFlatness;
    private float _lastBassInput;
    private float _bassEnv, _vocalEnv, _padEnv;
    private KickHold _hold;
    private TonalState _tonal = new();
    private PendingKick? _pendingKick;

    public SoundTypeDetector() : this(new Tuning())
    {
    }

    public SoundTypeDetector(Tuning tuning)
    {
        _tuning = tuning;
        const int bins = MaximumBinCount;
        _binCapacity = bins;
        _ringP = new float[Depth * bins];
        _ringL = new float[Depth * BandCapacity];
        _ringLow = new float[Depth];
        _ringTime = new double[Depth];
        Array.Fill(_ringTime, double.NegativeInfinity);
        _increment = new float[bins];
        _work = new float[bins];
        _db = new float[bins];
        _prefix = new float[bins + 1];
        _binFrequency = new float[bins];
        _tilt = new float[bins];
        _regionWeights = new float[5 * (bins + 4)];
        _bandStart = new int[BandCapacity + 1];
        _bandEnd = new int[BandCapacity + 1];
        _bandShare = new float[BandCapacity];
        _bandHold = new float[BandCapacity];
        _typical = new float[3 * OnsetTracker.History];
        _kick = new OnsetTracker(_typical, 0);
        _snare = new OnsetTracker(_typical, OnsetTracker.History);
        _hat = new OnsetTracker(_typical, 2 * OnsetTracker.History);
    }

    public Tuning Settings => _tuning;

    /// <summary>The newest pass's output.</summary>
    public Output Current => _output;

    /// <summary>Forgets everything, counters included: a new source starts from zero.</summary>
    public void Reset()
    {
        _output = new Output();
        _binCount = 0;
        _binHz = 0;
        _clock = 0;
        _programPower = 0;
        _primed = false;
        _kick = new OnsetTracker(_typical, 0);
        _snare = new OnsetTracker(_typical, OnsetTracker.History);
        _hat = new OnsetTracker(_typical, 2 * OnsetTracker.History);
        _previousOdfSnare = 0;
        _previousFlatness = 0;
        _lastBassInput = 0;
        _bassEnv = 0; _vocalEnv = 0; _padEnv = 0;
        _hold = default;
        _tonal = new TonalState();
        _pendingKick = null;
    }

    /// <summary>
    /// Capture paused for <paramref name="idle"/> seconds beyond the usual quarter second: the sustained classes fade
    /// as the render side showed them fading (AudioAnalyzer's idle fade), so the next pass attacks from there.
    /// </summary>
    public void Settle(double idle)
    {
        if (!(idle > 0) || !double.IsFinite(idle)) return;
        var fade = (float)Math.Exp(-idle / 0.2);
        _bassEnv *= fade; _vocalEnv *= fade; _padEnv *= fade;
        _output.Bass = _bassEnv; _output.Vocal = _vocalEnv; _output.Pad = _padEnv;
    }

    // MARK: - Configuration

    private void Configure(float binHz, int binCount)
    {
        _binHz = binHz;
        _binCount = binCount;
        var top = Min(16_000, binHz * binCount * 0.9f); // 0.45 fs
        // Semi-log bands, 12 per octave from 30 Hz, never narrower than a bin. The edges go into bandStart; band b
        // spans edges b..<b+1.
        var edges = 0;
        var step = 0;
        while (edges <= BandCapacity)
        {
            var f = 30 * MathF.Pow(2, step / 12f);
            var bin = Max(1, (int)Round(f / binHz));
            if (edges > 0) bin = Max(bin, _bandStart[edges - 1] + 1);
            if (bin * binHz > top || bin >= binCount) break;
            _bandStart[edges] = bin;
            edges++;
            step++;
        }
        _bandCount = Max(0, edges - 1);
        _analysedLo = edges > 0 ? _bandStart[0] : 1;
        _analysedHi = edges > 0 ? _bandStart[edges - 1] : 1;
        for (var b = 0; b < _bandCount; b++) _bandEnd[b] = _bandStart[b + 1];
        var analysed = (float)Max(1, _analysedHi - _analysedLo);
        for (var b = 0; b < _bandCount; b++) _bandShare[b] = (_bandEnd[b] - _bandStart[b]) / analysed;
        _kickBands = Bands(_tuning.KickBand, top);
        _snareBands = Bands(_tuning.SnareBand, top);
        _hatBands = Bands(_tuning.HatBand, top);

        for (var k = 0; k < binCount; k++)
        {
            _binFrequency[k] = k * binHz;
            _tilt[k] = k * binHz * 0.001f;
        }
        var stride = _binCapacity + 4;
        _kickRegion = new Region(_tuning.KickBand, binHz, binCount, top, _regionWeights, 0);
        _lowMidRegion = new Region(_tuning.LowMidBand, binHz, binCount, top, _regionWeights, stride);
        _snareRegion = new Region(_tuning.SnareBand, binHz, binCount, top, _regionWeights, 2 * stride);
        _hatRegion = new Region(_tuning.HatBand, binHz, binCount, top, _regionWeights, 3 * stride);
        _bassRegion = new Region(_tuning.BassBand, binHz, binCount, top, _regionWeights, 4 * stride, _tuning.BassTaperFrom);
        _flatLo = Max(1, (int)(_tuning.FlatBand.Lower / binHz));
        _flatHi = Max(_flatLo, Min(binCount, (int)(Min(_tuning.FlatBand.Upper, top) / binHz)));
        _highLo = Max(1, (int)(3000 / binHz));
        _highHi = Max(_highLo, Min(binCount, (int)(top / binHz)));
        _lowLo = Max(1, (int)(35 / binHz));
        _lowHi = Min(binCount - 1, (int)(300 / binHz));
        _peakLo = Max(2, (int)(35 / binHz));
        _peakHi = Min(binCount - 4, (int)(5000 / binHz));

        for (var slot = 0; slot < Depth; slot++) { _ringTime[slot] = double.NegativeInfinity; _ringLow[slot] = 0; }
        Array.Clear(_ringP);
        Array.Clear(_ringL);
        Array.Clear(_bandHold);
        _ringHead = 0;
        _tonal = new TonalState();
        _hold = default;
    }

    private float Centre(int b) => 0.5f * (_bandStart[b] + _bandEnd[b] - 1) * _binHz;

    private (int Lower, int Upper) Bands(Range r, float top)
    {
        int lo = _bandCount, hi = -1;
        for (var b = 0; b < _bandCount; b++)
        {
            if (Centre(b) >= r.Lower) { lo = b; break; }
        }
        for (var b = _bandCount - 1; b >= 0; b--)
        {
            if (Centre(b) <= Min(r.Upper, top)) { hi = b; break; }
        }
        return lo <= hi ? (lo, hi + 1) : (lo, lo);
    }

    /// <summary>The newest stored frame at least <paramref name="lag"/> seconds older than <paramref name="time"/>.</summary>
    private int Reference(double time, float lag, float dt)
    {
        var reference = -1;
        for (var back = 1; back < Depth; back++)
        {
            var i = (_ringHead - back + Depth) % Depth;
            if (!double.IsFinite(_ringTime[i])) break;
            reference = i;
            if (time - _ringTime[i] >= (double)lag - 0.25 * (double)dt) break;
        }
        return reference;
    }

    // MARK: - Per-pass processing

    /// <summary>
    /// Processes one pass. <paramref name="input"/>: one-sided power spectrum, bin k at k·<paramref name="binHz"/>,
    /// normalised so a sinusoid of amplitude A sums to A²/2 over its main lobe; NaN, infinite and negative bins count
    /// as 0. <paramref name="rawDt"/>: audio seconds since the previous pass. <paramref name="time"/>: capture time of
    /// the window's newest sample, on the clock the published onset times use. Never allocates.
    /// </summary>
    public void Process(ReadOnlySpan<float> input, float binHz, double rawDt, double time)
    {
        if (input.Length < 16 || !float.IsFinite(binHz) || !(binHz > 0) || !double.IsFinite(time)) return;
        var n = Min(input.Length, _binCapacity);
        if (binHz != _binHz || n != _binCount) Configure(binHz, n);
        var elapsed = double.IsFinite(rawDt) ? Max(0, rawDt) : 0.01;
        var dt = (float)Min(0.1, Max(0.0005, elapsed));
        _clock += elapsed;
        var now = _clock;
        var t = _tuning;
        var head = _ringHead;
        var p = _ringP.AsSpan(head * _binCapacity, _binCapacity);
        var level = _ringL.AsSpan(head * BandCapacity, BandCapacity);
        // NaN and negatives → 0 (vDSP_vthres).
        for (var k = 0; k < n; k++)
        {
            var v = input[k];
            p[k] = v >= 0 ? v : 0;
        }
        p[0] = 0;
        float sum = 0;
        for (var k = 0; k < n; k++) sum += p[k];
        if (!float.IsFinite(sum))
        {
            for (var k = 0; k < n; k++) if (!float.IsFinite(p[k])) p[k] = 0;
        }

        // 0. Programme level: a power follower that rises in ~0.25 s and falls over ~4 s, gated so silence holds it.
        float total = 0;
        for (var k = _analysedLo; k < _analysedHi; k++) total += p[k];
        var audible = 10 * MathF.Log10(total + 1e-12f) > -65;
        if (audible)
        {
            if (!_primed) { _programPower = total; _primed = true; }
            var tau = total > _programPower ? t.ProgramRise : t.ProgramFall;
            _programPower += (total - _programPower) * (1 - MathF.Exp(-dt / tau));
        }
        var G = 10 * MathF.Log10(Max(_programPower, 1e-7f));

        // 1. Bands, adaptive whitening, compression.
        var decayW = MathF.Exp(-dt / t.WhitenTau);
        var floorBase = MathF.Pow(10, (G - 70) / 10);
        var logNorm = 1 / Log1p(t.Compression);
        var compression = t.Compression;
        for (var b = 0; b < _bandCount; b++)
        {
            float s = 0;
            for (var k = _bandStart[b]; k < _bandEnd[b]; k++) s += p[k];
            var held = Max(s, _bandHold[b] * decayW);
            _bandHold[b] = held;
            var w = s / Max(Max(held, floorBase * _bandShare[b]), 1e-20f);
            level[b] = Log1p(compression * w) * logNorm;
        }

        // 2. Novelty against the frame ~21 ms earlier, whatever the hop.
        var reference = Reference(now, t.FluxLag, dt);
        float odfK = 0, odfS = 0, odfH = 0;
        float dLow = 0, dLowMid = 0, dMid = 0, dHigh = 0, eLowRef = 0;
        float flat = 0;
        float highCentroid = 0;
        if (reference >= 0)
        {
            var refL = _ringL.AsSpan(reference * BandCapacity, BandCapacity);
            var refP = _ringP.AsSpan(reference * _binCapacity, _binCapacity);
            for (var b = _kickBands.Lower; b < _kickBands.Upper; b++) odfK += Max(0, level[b] - refL[b]);
            odfK /= Max(1, _kickBands.Upper - _kickBands.Lower);
            odfS = Superflux(level, refL, _snareBands);
            odfH = Superflux(level, refL, _hatBands);
            for (var k = 0; k < n; k++)
            {
                var d = p[k] - refP[k];
                _increment[k] = d >= 0 ? d : 0;
            }
            dLow = _kickRegion.Sum(_regionWeights, _increment);
            dLowMid = _lowMidRegion.Sum(_regionWeights, _increment);
            eLowRef = _kickRegion.Sum(_regionWeights, refP);
            dMid = _snareRegion.Sum(_regionWeights, _increment);
            dHigh = _hatRegion.Sum(_regionWeights, _increment);
            flat = IncrementFlatness();
            // Centroid of the positive increment above 3 kHz: an "s" centres around 5-8 kHz, a hi-hat around 8-12 kHz.
            if (_highHi > _highLo)
            {
                float num = 0, den = 0;
                for (var k = _highLo; k < _highHi; k++) { num += _increment[k] * _binFrequency[k]; den += _increment[k]; }
                highCentroid = den > 0 ? num / den : 0;
            }
        }

        // 3. Region levels and increment shapes.
        float eKick = _kickRegion.Sum(_regionWeights, p), eSnare = _snareRegion.Sum(_regionWeights, p);
        var eHat = _hatRegion.Sum(_regionWeights, p);
        var dbKick = 10 * MathF.Log10(eKick + 1e-14f);
        var dbSnare = 10 * MathF.Log10(eSnare + 1e-14f);
        var dbHat = 10 * MathF.Log10(eHat + 1e-14f);
        var shapeHM = 10 * MathF.Log10((dHigh + 1e-14f) / (dMid + 1e-14f));        // > 0: hat-shaped
        var shapeML = 10 * MathF.Log10((dMid + 1e-14f) / (dLow + 1e-14f));         // < 0: the low band dominates
        var harmonicRatio = 10 * MathF.Log10((dLowMid + 1e-14f) / (dLow + 1e-14f)); // bass harmonics vs thump
        var riseLow = 10 * MathF.Log10((eKick + 1e-14f) / (eLowRef + 1e-14f));
        // Power centroid of 35-300 Hz: a kick's sweep pulls it down by a third within ~30 ms; a bass note's harmonic
        // pattern keeps it put.
        float lowCentroid = 0;
        if (_lowHi >= _lowLo)
        {
            float num = 0, den = 0;
            for (var k = _lowLo; k <= _lowHi; k++) { num += p[k] * _binFrequency[k]; den += p[k]; }
            lowCentroid = den > 0 ? num / den : 0;
        }

        // 4. Transients.
        var latency = (double)(t.LatencyFraction / binHz); // a fraction of the window's length
        var thrK = _kick.Threshold(t.KickK, t.KickFloor);
        var thrS = _snare.Threshold(t.SnareK, t.SnareFloor);
        var thrH = _hat.Threshold(t.HatK, t.HatFloor);

        // Kick: novelty in 35-120 Hz, a real level rise and the low band carrying the increment. It fires at once with
        // a beater click (flat mid novelty this pass or the last) or a very sharp non-harmonic rise; otherwise it waits
        // up to 35 ms for the kick's pitch sweep (the 35-300 Hz centroid starting above 105 Hz and falling steadily by
        // 18%), which a bass note, starting on its fixed pitch, does not show.
        var click = Max(odfS, _previousOdfSnare) >= 0.07f && Max(flat, _previousFlatness) >= 0.3f;
        var sharp = riseLow >= 14 && harmonicRatio <= -10;
        var kickFired = false;
        var basic = audible && _kick.Ready(now, t.KickRefractory) && odfK > thrK && riseLow > 5
            && dbKick > G - 18 && shapeML <= 6;
        var energyBefore = reference >= 0 ? _ringLow[reference] : 0;
        if (_pendingKick is { } pending)
        {
            var monotonic = lowCentroid <= pending.LastCentroid * 1.03f;
            if (!monotonic || now - pending.Start > 0.035)
            {
                _pendingKick = null;
                _hold.Cancel();
            }
            else if (lowCentroid <= pending.Centroid * 0.82f)
            {
                _pendingKick = null;
                FireKick(now, time - (now - pending.Start) - latency, Max(pending.Odf, odfK), dbKick, G);
                kickFired = true;
            }
            else
            {
                _pendingKick = pending with { LastCentroid = lowCentroid, Odf = Max(pending.Odf, odfK) };
            }
        }
        else if (basic)
        {
            if (shapeML <= 0 && (click || sharp))
            {
                FireKick(now, time - latency, odfK, dbKick, G);
                kickFired = true;
                _hold.Start(now, _lastBassInput, energyBefore);
            }
            else if (lowCentroid >= 105 && riseLow >= 8 && odfK >= 1.3f * thrK)
            {
                // A kick's sweep starts at 100-200 Hz; a bass note starts on its pitch.
                _pendingKick = new PendingKick(now, lowCentroid, lowCentroid, odfK);
                _hold.Start(now, _lastBassInput, energyBefore);
            }
        }
        var kickNear = kickFired || _pendingKick is not null || (float)(now - _kick.Last) < 0.035f;

        // While a lead voice is evidently singing, its consonants ("t", "s") are noise bursts too: demand a louder,
        // flatter snare, and a hat whose increment centres above 9.5 kHz ("s" centres at 5-9 kHz).
        var singing = _tonal.Evidence;
        // Snare: broadband, noise-like, mid-weighted increment. Near a kick it must also beat the kick's own beater click.
        var snareFired = false;
        if (audible && _snare.Ready(now, t.SnareRefractory) && odfS > thrS * (kickNear ? 1.4f : 1)
            && flat > 0.2f + 0.1f * singing && shapeHM < 4 && dbSnare > G - 26 + 12 * singing
            && (!kickNear || shapeML > -8))
        {
            snareFired = true;
            _snare.Fire(now, odfS, Clamp((dbSnare - (G - 28)) / 16, 0.25f, 1), t.TopUp);
            _output.SnareOnsets = unchecked(_output.SnareOnsets + 1);
            _output.NewestSnare = new SoundOnset(time - latency, _snare.Drive);
        }
        var snareNear = snareFired || (float)(now - _snare.Last) < 0.035f;

        // Hat: novelty above 7 kHz with a hat-shaped (high-dominated) increment. A coincident snare suppresses it
        // unless the high band clearly dominates.
        var hatLike = singing < 0.5f || highCentroid >= 9500 || shapeHM >= 9;
        if (audible && _hat.Ready(now, t.HatRefractory) && odfH > thrH * (kickNear ? 1.15f : 1)
            && shapeHM > (kickNear ? 6 : 3) && hatLike && dbHat > G - 50 + 10 * singing
            && (!snareNear || odfH > 1.6f * odfS))
        {
            _hat.Fire(now, odfH, Clamp((dbHat - (G - 40)) / 18, 0.25f, 1), t.TopUp);
            _output.HatOnsets = unchecked(_output.HatOnsets + 1);
            _output.NewestHat = new SoundOnset(time - latency, _hat.Drive);
        }
        _previousOdfSnare = odfS;
        _previousFlatness = flat;

        _kick.Advance(now, odfK, thrK, t.Rearm, dt, t.StatsTau);
        _snare.Advance(now, odfS, thrS, t.Rearm, dt, t.StatsTau);
        _hat.Advance(now, odfH, thrH, t.Rearm, dt, t.StatsTau);
        // The strength top-up reaches the newest published onset.
        if (now <= _kick.TopUpUntil) _output.NewestKick.Strength = Max(_output.NewestKick.Strength, _kick.Amplitude());
        if (now <= _snare.TopUpUntil) _output.NewestSnare.Strength = Max(_output.NewestSnare.Strength, _snare.Amplitude());
        if (now <= _hat.TopUpUntil) _output.NewestHat.Strength = Max(_output.NewestHat.Strength, _hat.Amplitude());
        _kick.Decay(dt, t.KickRelease);
        _snare.Decay(dt, t.SnareRelease);
        _hat.Decay(dt, t.HatRelease);

        // 5. Tonal analysis: stable peaks, bass series, lead series.
        TonalAnalysis(p, G, dt, now);

        // 6. Sustained classes.
        var eLow = _bassRegion.Sum(_regionWeights, p);
        var ref40 = Reference(now, 0.04f, dt);
        var lowSlope = ref40 >= 0
            ? 10 * MathF.Log10((eLow + 1e-14f) / (_ringLow[ref40] + 1e-14f)) / Max(0.02f, (float)(now - _ringTime[ref40]))
            : 0;
        var bassInput = _hold.Apply(now, lowSlope, eLow, _tonal.BassPower);
        _lastBassInput = bassInput;
        var bassDb = 10 * MathF.Log10(bassInput + 1e-14f);
        var vocalDb = 10 * MathF.Log10(_tonal.VocalPower + 1e-14f);
        var padDb = 10 * MathF.Log10(_tonal.PadPower + 1e-14f);
        var gate = audible ? 1f : 0f;
        var nBass = gate * Clamp((bassDb - (G - t.BassOffset - t.BassWindow)) / t.BassWindow, 0, 1);
        var nVocal = gate * Clamp((vocalDb - (G - t.VocalOffset - t.VocalWindow)) / t.VocalWindow, 0, 1);
        var nPad = gate * Clamp((padDb - (G - t.PadOffset - t.PadWindow)) / t.PadWindow, 0, 1);
        // When the music stops (frames under -65 dBFS) the sustained classes fade within ~1 s instead of riding their
        // musical releases.
        var quiet = audible ? float.PositiveInfinity : 0.3f;
        _bassEnv = Follow(_bassEnv, nBass, dt, t.BassAttack, Min(t.BassRelease, quiet));
        _vocalEnv = Follow(_vocalEnv, nVocal, dt, t.VocalAttack, Min(t.VocalRelease, quiet));
        _padEnv = Follow(_padEnv, nPad, dt, t.PadAttack, Min(t.PadRelease, quiet));
        _output.Bass = _bassEnv;
        _output.Vocal = _vocalEnv;
        _output.Pad = _padEnv;

        // 7. Keep this frame (its spectrum and levels are already in the slot).
        _ringLow[head] = eLow;
        _ringTime[head] = now;
        _ringHead = (head + 1) % Depth;
    }

    private void FireKick(double now, double onset, float odf, float db, float G)
    {
        _kick.Fire(now, odf, Clamp((db - (G - 20)) / 14, 0.25f, 1), _tuning.TopUp);
        _output.KickOnsets = unchecked(_output.KickOnsets + 1);
        _output.NewestKick = new SoundOnset(onset, _kick.Drive);
    }

    /// <summary>SuperFlux: the mean rise of each band over the ±1-band maximum of the reference, so vibrato does not count as an onset.</summary>
    private float Superflux(ReadOnlySpan<float> level, ReadOnlySpan<float> refL, (int Lower, int Upper) r)
    {
        float s = 0;
        var last = _bandCount - 1;
        for (var b = r.Lower; b < r.Upper; b++)
        {
            var m = Max(Max(refL[b > 0 ? b - 1 : 0], refL[b]), refL[b < last ? b + 1 : last]);
            s += Max(0, level[b] - m);
        }
        return s / Max(1, r.Upper - r.Lower);
    }

    /// <summary>
    /// Spectral flatness (geometric / arithmetic mean) of the positive power increment over 1-8 kHz, tilted by
    /// f / 1 kHz so pink noise counts as flat: ~0.3-0.6 for a noise burst or a beater click, &lt; 0.1 when a comb of
    /// harmonic partials rose. Raw power, not whitened: whitening would blow up the empty bins of a sparse spectrum and
    /// make any onset look flat.
    /// </summary>
    private float IncrementFlatness()
    {
        var count = _flatHi - _flatLo;
        if (count <= 0) return 0;
        var work = _work.AsSpan(0, count);
        float sum = 0;
        for (var i = 0; i < count; i++)
        {
            work[i] = _increment[_flatLo + i] * _tilt[_flatLo + i];
            sum += work[i];
        }
        if (!(sum > 1e-20f)) return 0;
        var mean = sum / count;
        var eps = mean * 1e-3f;
        float logSum = 0;
        for (var i = 0; i < count; i++) logSum += MathF.Log(work[i] + eps);
        return MathF.Exp(logSum / count) / (mean + eps);
    }

    // MARK: - Tonal analysis

    private void TonalAnalysis(Span<float> p, float G, float dt, double now)
    {
        var pk = _peaks;
        var prev = _previous;
        var db = _db;
        var prefix = _prefix;
        var count = 0;
        if (_peakHi > _peakLo + 2)
        {
            int lo = Max(0, _peakLo - 10), hi = Min(_binCount - 1, _peakHi + 10);
            for (var bin = lo; bin <= hi; bin++) db[bin] = 10 * MathF.Log10(p[bin] >= 1e-20f ? p[bin] : 1e-20f);
            prefix[lo] = 0;
            float running = 0;
            for (var bin = lo; bin <= hi; bin++) { running += db[bin]; prefix[bin + 1] = running; }
            // Peaks: local maxima ≥ 8 dB above the local mean, refined by a parabola, matched to the last pass's
            // partials for age and slope.
            var strongest = float.NegativeInfinity;
            for (var bin = _peakLo; bin <= _peakHi; bin++) if (db[bin] > strongest) strongest = db[bin];
            strongest = Max(strongest, -200);
            var floorDb = Max(G - 75, strongest - 70);
            // Matching tolerance as a pitch slew rate (~3600 cents/s: a 3-semitone glide in 70 ms), so a glide tracks
            // the same at any hop.
            var slewRatio = MathF.Pow(2, Clamp(3600 * dt, 30, 150) / 1200) - 1;
            var minimumTolerance = _binHz * 1.01f;
            var previousCount = _tonal.PreviousCount;
            var pf = prev.Frequency;
            var pm = prev.Matched;
            var j = 0;
            var k = _peakLo - 1;
            while (k < _peakHi)
            {
                k++;
                var c = db[k];
                if (!(c > db[k - 1] && c >= db[k + 1] && c >= db[k + 2] && (k < 3 || c >= db[k - 2]) && c > floorDb
                      && count < PeakCapacity)) continue;
                int a = k - 8 > lo + 1 ? k - 8 : lo + 1, b = k + 8 < hi ? k + 8 : hi;
                if (!(c - (prefix[b + 1] - prefix[a]) / (b - a + 1) >= 8)) continue;
                float l = db[k - 1], r = db[k + 1];
                var den = l - 2 * c + r;
                var offset = MathF.Abs(den) > 1e-6f ? Clamp(0.5f * (l - r) / den, -0.5f, 0.5f) : 0;
                var f = (k + offset) * _binHz;
                var tol = Max(minimumTolerance, f * slewRatio);
                while (j < previousCount && pf[j] < f - tol) j++;
                // The nearest unclaimed previous partial within the tolerance.
                var match = -1;
                for (var m = j; m < previousCount && pf[m] <= f + tol; m++)
                {
                    if (!pm[m] && (match < 0 || MathF.Abs(pf[m] - f) < MathF.Abs(pf[match] - f))) match = m;
                }
                float age = 0, slope = 0;
                float maxDb = c, sinceMax = 0;
                var cents = 1200 * MathF.Log2(f);
                float centsMean = cents, centsVar = 0;
                if (match >= 0)
                {
                    pm[match] = true;
                    var gap = prev.Missing[match] + dt;
                    age = prev.Age[match] + gap;
                    // Decay rate (dB/s) since this partial's running maximum, which itself leaks at 6 dB/s so old peaks
                    // are forgotten.
                    var leaked = prev.MaxDb[match] - 6 * gap;
                    if (c >= leaked) { maxDb = c; sinceMax = 0; } else { maxDb = leaked; sinceMax = prev.SinceMax[match] + gap; }
                    slope = -(maxDb - c) / Max(0.08f, sinceMax);
                    // Pitch wobble of this partial: cents rms around a 0.2 s mean.
                    var follow = 1 - MathF.Exp(-gap / 0.2f);
                    centsMean = prev.CentsMean[match] + (cents - prev.CentsMean[match]) * follow;
                    var dev = Clamp(cents - centsMean, -100, 100);
                    centsVar = prev.CentsVar[match] + (dev * dev - prev.CentsVar[match]) * follow;
                }
                pk.Frequency[count] = f;
                pk.Cents[count] = cents;
                pk.Power[count] = p[k - 1] + p[k] + p[k + 1];
                pk.CentsMean[count] = centsMean;
                pk.CentsVar[count] = centsVar;
                pk.MaxDb[count] = maxDb;
                pk.SinceMax[count] = sinceMax;
                pk.Age[count] = age;
                pk.Slope[count] = slope;
                pk.Owner[count] = 0;
                count++;
            }
        }
        _tonal.Count = count;
        const float stableAge = 0.03f;
        // How "lead-like" each partial is: a sung syllable's partials are young (< 0.4 s) or wobble in pitch (vibrato,
        // glides); a pad's are long-lived and still. This only steers the lead search; the vocal/pad split is decided
        // by the series' coherent pitch motion.
        for (var i = 0; i < count; i++)
        {
            var rms = MathF.Sqrt(pk.CentsVar[i]);
            var f = pk.Frequency[i];
            // Vibrato is only measurable in cents where a bin is narrow enough.
            var wobble = f >= 200 ? Smooth01((rms - 5) / 10) : 0;
            var young = 1 - Smooth01((pk.Age[i] - 0.4f) / 0.6f);
            pk.LeadWeight[i] = Max(wobble, young);
            // Held tone rather than a noise peak: it has lasted a little, and its frequency wanders less than ~0.4 bin
            // (rms, in bins).
            var rmsBins = rms * f * 0.000578f / _binHz;
            pk.PitchStable[i] = Smooth01((pk.Age[i] - 0.03f) / 0.1f) * (1 - Smooth01((rmsBins - 0.25f) / 0.35f));
        }

        // Bass series (f0 35-160 Hz, fundamental present, energy in its first few partials) claims its partials
        // first, so a bass line's harmonics and octave notes never read as voice or pad. The 110-160 Hz extension only
        // follows a bass line that was playing below 110 Hz within the last second (octave jumps).
        var bassTop = now < _tonal.BassLineUntil ? 160f : 110f;
        var bassSeries = BestSeries(35, bassTop, requireFundamental: true, minHits: 2, stableAge, owner: 1,
            previous: _tonal.BassF0, weighted: false, bassShaped: true);
        _tonal.BassF0 = bassSeries.F0;
        if (bassSeries.F0 > 0 && bassSeries.F0 <= 110) _tonal.BassLineUntil = now + 1.0;
        // Lead series (f0 100-1000 Hz, at least three partials) on the rest.
        var lead = BestSeries(100, 1000, requireFundamental: false, minHits: 3, stableAge, owner: 2,
            previous: _tonal.LeadF0, weighted: true, bassShaped: false);

        float tonalTotal = 0, leadPower = 0, bedPower = 0, bassPower = 0;
        float leadSteady = 0, leadMature = 0;
        for (var i = 0; i < count; i++)
        {
            if (!(pk.Age[i] >= stableAge)) continue;
            float f = pk.Frequency[i], power = pk.Power[i];
            var owner = pk.Owner[i];
            // Steady: decaying slower than 30 dB/s (pads, voices, held notes, 808 tails); a kick tail, tom or snare
            // body decays at 60-150 dB/s.
            var steady = Smooth01((pk.Slope[i] + 60) / 30);
            var mature = Smooth01((pk.Age[i] - 0.05f) / 0.25f);
            // Bass: partials of a bass series, or a lone tone below 110 Hz.
            if (owner == 1 || (owner == 0 && f < 110))
                bassPower += power * BassWeight(f) * steady * pk.PitchStable[i];
            if (owner == 1 || !(f >= 150)) continue;
            tonalTotal += power;
            if (owner == 2)
            {
                leadPower += power;
                leadSteady += power * steady;
                leadMature += power * steady * mature * pk.PitchStable[i];
            }
            else
            {
                bedPower += power * steady * mature * pk.PitchStable[i];
            }
        }
        var h1 = tonalTotal > 0 ? leadPower / tonalTotal : 0;
        var steadyShare = leadPower > 0 ? leadSteady / leadPower : 0;

        // Lead track: continuous while the f0 stays within 80 cents (octave slips of the estimate tolerated); it
        // survives 80 ms of masking.
        if (lead.F0 > 0)
        {
            var cents = 1200 * MathF.Log2(lead.F0 / 100);
            var raw = _tonal.LeadF0 > 0 ? 1200 * MathF.Log2(lead.F0 / _tonal.LeadF0) : 10_000f;
            var octave = Round(raw / 1200);
            var jump = MathF.Abs(raw - 1200 * octave);
            if (jump < 80 && MathF.Abs(octave) <= 1)
            {
                cents -= 1200 * octave;
                _tonal.TrackAge += dt + _tonal.LeadMissing;
            }
            else
            {
                _tonal.TrackAge = 0;
            }
            _tonal.LeadF0 = 100 * MathF.Pow(2, cents / 1200);
            _tonal.LeadMissing = 0;
        }
        else
        {
            _tonal.LeadMissing += dt;
            if (_tonal.LeadMissing > 0.08f) { _tonal.LeadF0 = 0; _tonal.TrackAge = 0; }
        }
        // Coherent pitch motion: each lead partial's deviation (cents) from its own 0.2 s mean, power-weighted across
        // partials above 300 Hz. Vibrato and glides move every partial of one voice together; a pad's beating partials
        // wobble independently and average out.
        float devSum = 0, devWeight = 0;
        for (var i = 0; i < count; i++)
        {
            if (pk.Owner[i] == 2 && pk.Frequency[i] >= 300 && pk.Age[i] >= 0.05f)
            {
                var dev = Clamp(pk.Cents[i] - pk.CentsMean[i], -100, 100);
                devSum += dev * pk.Power[i];
                devWeight += pk.Power[i];
            }
        }
        if (devWeight > 0)
        {
            var common = devSum / devWeight;
            _tonal.CoherentVar += (common * common - _tonal.CoherentVar) * (1 - MathF.Exp(-dt / 0.25f));
        }
        else
        {
            _tonal.CoherentVar *= MathF.Exp(-dt / 0.5f);
        }
        var motion = MathF.Sqrt(Max(0, _tonal.CoherentVar));

        var harmonic = Smooth01((lead.Coverage - 0.2f) / 0.3f) * Smooth01((h1 - 0.15f) / 0.25f);
        var continuity = Smooth01((_tonal.TrackAge - 0.04f) / 0.08f);
        var moving = Smooth01((motion - 10) / 12);
        var instant = harmonic * continuity * moving;
        // Evidence rises within ~0.15 s of vibrato or syllables and is kept through the phrase: it falls over 1 s while
        // a lead line persists and 2 s through gaps.
        var target = leadPower > 0 ? instant : 0;
        var tau = target > _tonal.Evidence ? 0.15f : (leadPower > 0 ? 1.0f : 2.0f);
        _tonal.Evidence += (target - _tonal.Evidence) * (1 - MathF.Exp(-dt / tau));
        var v = _tonal.Evidence;
        var toVocal = Smooth01((v - 0.25f) / 0.45f);
        var toPad = 1 - Smooth01((v - 0.2f) / 0.4f);
        _tonal.VocalPower = leadPower * toVocal * steadyShare;
        // The lead's partials join the pad only once they are mature, steady, not moving and held for 0.4-0.8 s: an
        // unclassified new syllable (a first sung note before its vibrato) lights neither class.
        var held = Smooth01((_tonal.TrackAge - 0.4f) / 0.4f);
        _tonal.PadPower = bedPower + leadMature * toPad * (1 - moving) * held;
        _tonal.BassPower = bassPower;

        CarryPeaks(count, dt);
    }

    /// <summary>
    /// The next pass matches against this pass's peaks plus peaks that went missing less than 45 ms ago (masked by a
    /// drum hit), merged by frequency.
    /// </summary>
    private void CarryPeaks(int count, float dt)
    {
        var pk = _peaks;
        var prev = _previous;
        var nx = _next;
        var previousCount = _tonal.PreviousCount;
        int merged = 0, a = 0, b = 0;
        while (a < count || b < previousCount)
        {
            bool takeCurrent;
            if (b >= previousCount) takeCurrent = true;
            else if (a >= count) takeCurrent = false;
            else takeCurrent = pk.Frequency[a] <= prev.Frequency[b];
            if (takeCurrent)
            {
                nx.Frequency[merged] = pk.Frequency[a]; nx.Age[merged] = pk.Age[a]; nx.Missing[merged] = 0;
                nx.CentsMean[merged] = pk.CentsMean[a]; nx.CentsVar[merged] = pk.CentsVar[a];
                nx.MaxDb[merged] = pk.MaxDb[a]; nx.SinceMax[merged] = pk.SinceMax[a];
                merged++; a++;
            }
            else
            {
                var missing = prev.Missing[b] + dt;
                if (!prev.Matched[b] && missing <= 0.045f)
                {
                    nx.Frequency[merged] = prev.Frequency[b]; nx.Age[merged] = prev.Age[b];
                    nx.Missing[merged] = missing;
                    nx.CentsMean[merged] = prev.CentsMean[b]; nx.CentsVar[merged] = prev.CentsVar[b];
                    nx.MaxDb[merged] = prev.MaxDb[b]; nx.SinceMax[merged] = prev.SinceMax[b] + dt;
                    merged++;
                }
                b++;
            }
            if (merged >= CarryCapacity) break;
        }
        prev.CopyFrom(nx, merged);
        _tonal.PreviousCount = merged;
    }

    private float BassWeight(float f)
    {
        float lo = _tuning.BassBand.Lower, hi = _tuning.BassBand.Upper, from = _tuning.BassTaperFrom;
        if (f < lo || f > hi) return 0;
        return f <= from ? 1 : Max(0.25f, 1 - 0.75f * (f - from) / (hi - from));
    }

    private record struct Series(float F0, float Score, float Coverage, bool Fundamental, bool Odd);

    /// <summary>
    /// Harmonic-sum search over candidates derived from the strongest free stable peaks; the winner claims its partials
    /// (<c>owner</c>). <paramref name="weighted"/> scales each partial by its lead weight (young or wobbling partials).
    /// </summary>
    private Series BestSeries(float lo, float hi, bool requireFundamental, int minHits, float stableAge, byte owner,
        float previous, bool weighted, bool bassShaped)
    {
        // The stable, unclaimed peaks, packed: frequency, power, √power × weight, weight, age, and the index to claim
        // through.
        var pk = _peaks;
        var el = _eligible;
        var total = _tonal.Count;
        var m = 0;
        for (var i = 0; i < total; i++)
        {
            if (pk.Age[i] >= stableAge && pk.Owner[i] == 0)
            {
                var w = weighted ? pk.LeadWeight[i] : 1;
                el.Frequency[m] = pk.Frequency[i];
                el.Power[m] = pk.Power[i];
                el.Cents[m] = MathF.Sqrt(pk.Power[i]) * w;
                el.LeadWeight[m] = w;
                el.Age[m] = pk.Age[i];
                el.Index[m] = i;
                m++;
            }
        }
        var seeds = _seeds;
        var seedCount = 0;
        for (var e = 0; e < m; e++)
        {
            if (!(el.LeadWeight[e] > 0.2f)) continue;
            if (seedCount < SeedCapacity) { seeds[seedCount] = e; seedCount++; continue; }
            var weakest = 0;
            for (var s = 1; s < SeedCapacity; s++)
            {
                if (el.Power[seeds[s]] * el.LeadWeight[seeds[s]] < el.Power[seeds[weakest]] * el.LeadWeight[seeds[weakest]])
                    weakest = s;
            }
            if (el.Power[e] * el.LeadWeight[e] > el.Power[seeds[weakest]] * el.LeadWeight[seeds[weakest]]) seeds[weakest] = e;
        }
        if (seedCount <= 0) return default;
        var best = default(Series);
        for (var s = 0; s < seedCount; s++)
        {
            var f = el.Frequency[seeds[s]];
            for (var h = 1; h <= 6; h++)
            {
                if (Score(f / h, m, lo, hi, requireFundamental, minHits, previous, bassShaped) is { } candidate
                    && candidate.Score > best.Score) best = candidate;
            }
        }
        if (previous > 0 && Score(previous, m, lo, hi, requireFundamental, minHits, previous, bassShaped) is { } again
            && again.Score > best.Score) best = again;
        if (!(best.Score > 0)) return default;
        // Octave check: an octave-up winner that only explains the even partials loses to the true f0 if that has its
        // own fundamental.
        if (Score(best.F0 / 2, m, lo, hi, requireFundamental, minHits, previous, bassShaped) is { } half
            && half.Fundamental && half.Odd && half.Score >= 0.75f * best.Score) best = half;
        var chosen = best.F0;
        float num = 0, den = 0;
        for (var e = 0; e < m; e++)
        {
            var r = el.Frequency[e] / chosen;
            var h = Round(r);
            if (!(h >= 1 && h <= 16 && r >= h * MatchLow && r <= h * MatchHigh)) continue;
            pk.Owner[el.Index[e]] = owner;
            num += el.Power[e] * el.Frequency[e] / h;
            den += el.Power[e];
        }
        if (den > 0) best.F0 = num / den;
        return best;
    }

    /// <summary>One candidate f0 against the packed peaks.</summary>
    private Series? Score(float c, int m, float lo, float hi, bool requireFundamental, int minHits, float previous,
        bool bassShaped)
    {
        if (!(c >= lo && c <= hi)) return null;
        var el = _eligible;
        var frequency = el.Frequency;
        var root = el.Cents;
        var powers = el.Power;
        var weight = el.LeadWeight;
        var age = el.Age;
        float sum = 0, power = 0, weightedOrder = 0, fundamentalAge = 0;
        uint hits = 0;
        for (var e = 0; e < m; e++)
        {
            var r = frequency[e] / c;
            var h = Round(r);
            if (h >= 1 && h <= 16 && r >= h * MatchLow && r <= h * MatchHigh)
            {
                sum += root[e];
                power += powers[e];
                weightedOrder += powers[e] * h;
                if (h == 1 && age[e] > fundamentalAge) fundamentalAge = age[e];
                if (weight[e] >= 0.3f) hits |= 1u << ((int)h - 1);
            }
        }
        // A bass note's energy sits in its first partials; a low male voice's sits in formants around harmonics 3-8.
        if (bassShaped && c > 100 && power > 0 && weightedOrder / power > 3.5f) return null;
        // Above 110 Hz only a rhythmic (recently started) note is a bass note; a held low chord tone belongs to the pad.
        if (bassShaped && c > 110 && fundamentalAge >= 0.6f) return null;
        var hmax = Max(1, Min(6, (int)(5000 / c)));
        var mask = (1u << hmax) - 1;
        var coverage = (float)BitOperations.PopCount(hits & mask) / hmax;
        var fundamental = (hits & 1) != 0;
        if (BitOperations.PopCount(hits) < minHits) return null;
        if (requireFundamental && (!fundamental || coverage < 0.5f)) return null;
        var value = sum * (0.35f + 0.65f * coverage) * (fundamental ? 1 : 0.6f);
        // A subharmonic candidate explains only even partials: no 3rd or 5th.
        var odd = (hmax >= 3 && (hits & 0b100) != 0) || (hmax >= 5 && (hits & 0b1_0000) != 0);
        if (hmax >= 3 && !odd) value *= 0.5f;
        if (previous > 0 && MathF.Abs(1200 * MathF.Log2(c / previous)) < 60) value *= 1.3f;
        return new Series(c, value, coverage, fundamental, odd);
    }

    // MARK: - Building blocks

    private static float Log1p(float x) => (float)Math.Log(1 + (double)x);

    private static float Smooth01(float x)
    {
        var c = Clamp(x, 0, 1);
        return c * c * (3 - 2 * c);
    }

    private static float Follow(float y, float x, float dt, float attack, float release) =>
        y + (x - y) * (1 - MathF.Exp(-dt / (x > y ? attack : release)));

    /// <summary>
    /// Fractional-bin weights of a frequency region, optionally tapering to 0.25 above <c>taperFrom</c>. The weights
    /// live in the detector's storage at <c>Offset</c>.
    /// </summary>
    private readonly struct Region
    {
        private readonly int _start;
        private readonly int _count;
        private readonly int _offset;

        public Region(Range r, float binHz, int binCount, float top, float[] weights, int offset, float? taperFrom = null)
        {
            float lo = r.Lower, hi = Min(r.Upper, top);
            int first = Max(1, (int)MathF.Floor(lo / binHz) - 1), last = Min(binCount - 1, (int)MathF.Ceiling(hi / binHz) + 1);
            if (!(hi > lo) || first > last) return;
            _start = first;
            _count = last - first + 1;
            _offset = offset;
            for (var k = first; k <= last; k++)
            {
                float f0 = (k - 0.5f) * binHz, f1 = (k + 0.5f) * binHz;
                var w = Max(0, Min(f1, hi) - Max(f0, lo)) / binHz;
                var f = k * binHz;
                if (taperFrom is { } from && f > from) w *= Max(0.25f, 1 - 0.75f * (f - from) / (hi - from));
                weights[offset + k - first] = w;
            }
        }

        /// <summary>Σ weight × v over the region.</summary>
        public float Sum(float[] weights, ReadOnlySpan<float> v)
        {
            if (_count <= 0) return 0;
            float s = 0;
            for (var i = 0; i < _count; i++) s += weights[_offset + i] * v[_start + i];
            return s;
        }
    }

    /// <summary>Adaptive threshold, hysteresis, refractory period and strength normalisation for one transient class.</summary>
    private struct OnsetTracker
    {
        public const int History = 16;
        public float Mean;
        public float Dev;
        public bool Armed;
        public double Last;
        public double TopUpUntil;
        public float Peak;
        public float Weight;
        /// <summary>Recent onset peaks; their 75th percentile is "a typical strong hit".</summary>
        private readonly float[] _typical;
        private readonly int _typicalOffset;
        private int _typicalIndex;
        private bool _pendingTypical;
        /// <summary>The strength handed to a new onset; decays with the class's release.</summary>
        public float Drive;

        public OnsetTracker(float[] typical, int offset)
        {
            Mean = 0;
            Dev = 0.02f;
            Armed = true;
            Last = double.NegativeInfinity;
            TopUpUntil = double.NegativeInfinity;
            Peak = 0;
            Weight = 1;
            _typical = typical;
            _typicalOffset = offset;
            _typicalIndex = 0;
            _pendingTypical = false;
            Drive = 0;
            typical.AsSpan(offset, History).Fill(0.3f);
        }

        public readonly float Threshold(float k, float floor) => Max(floor, Mean + k * Dev);

        public readonly bool Ready(double time, float refractory) => Armed && time - Last >= refractory;

        public void Fire(double time, float odf, float weight, float topUp)
        {
            Armed = false;
            Last = time;
            TopUpUntil = time + topUp;
            Peak = odf;
            Weight = weight;
            _pendingTypical = true;
            Drive = Max(Drive, Amplitude());
        }

        /// <summary>
        /// 0.3 + 0.7 × (peak / 75th percentile of recent peaks), times the loudness weight. Selection by counting, so
        /// nothing allocates.
        /// </summary>
        public readonly float Amplitude()
        {
            var typical = _typical.AsSpan(_typicalOffset, History);
            float p75 = 0;
            for (var i = 0; i < History; i++)
            {
                var candidate = typical[i];
                var below = 0;
                for (var j = 0; j < History; j++) if (typical[j] < candidate) below++;
                if (below <= 11 && candidate > p75) p75 = candidate;
            }
            return Min(1, (0.3f + 0.7f * Min(1.2f, Peak / Max(0.12f, p75))) * Weight);
        }

        public void Advance(double time, float odf, float threshold, float rearm, float dt, float statsTau)
        {
            if (time <= TopUpUntil)
            {
                if (odf > Peak) { Peak = odf; Drive = Max(Drive, Amplitude()); }
            }
            else if (_pendingTypical)
            {
                _typical[_typicalOffset + _typicalIndex] = Peak;
                _typicalIndex = (_typicalIndex + 1) % History;
                _pendingTypical = false;
            }
            if (!Armed && (odf < rearm * threshold || time - Last > 0.25)) Armed = true;
            var a = 1 - MathF.Exp(-dt / statsTau);
            Mean += (odf - Mean) * a;
            Dev += (MathF.Abs(odf - Mean) - Dev) * a;
        }

        public void Decay(float dt, float release) => Drive *= MathF.Exp(-dt / release);
    }

    /// <summary>
    /// Holds the bass level at its pre-kick value while a kick's low tail rings, so a kick never reads as bass; lets go
    /// as soon as the low band behaves like a sustained note (an 808, or a bass note under the kick).
    /// </summary>
    private struct KickHold
    {
        private bool _active;
        private double _onset;
        private float _before;
        private float _energyBefore;

        public void Start(double time, float before, float energyBefore)
        {
            _active = true; _onset = time; _before = before; _energyBefore = energyBefore;
        }

        /// <summary>A kick candidate turned out to be a bass note.</summary>
        public void Cancel() => _active = false;

        /// <summary>
        /// <paramref name="input"/>: this pass's tonal bass power; <paramref name="energy"/>/<paramref name="slope"/>:
        /// the 35-220 Hz band energy and its dB/s over the last ~40 ms.
        /// </summary>
        public float Apply(double time, float slope, float energy, float input)
        {
            if (!_active) return input;
            var age = time - _onset;
            var sustained = age >= 0.06 && slope > -30;           // an 808 or a held bass note
            var merged = age >= 0.04 && energy <= 2 * _energyBefore; // the tail fell to the pre-kick level
            if (sustained || merged || age > 0.5) { _active = false; return input; }
            return Min(input, _before);
        }
    }

    private readonly record struct PendingKick(double Start, float Centroid, float LastCentroid, float Odf);

    /// <summary>A value type, so a reset or reconfiguration never allocates.</summary>
    private struct TonalState
    {
        public TonalState()
        {
        }

        public int Count;
        public int PreviousCount;
        public float LeadMissing;
        public double BassLineUntil = double.NegativeInfinity;
        public float BassF0;
        public float LeadF0;
        public float TrackAge;
        public float CoherentVar;
        public float Evidence;
        public float VocalPower;
        public float PadPower;
        public float BassPower;
    }

    /// <summary>
    /// This pass's spectral peaks, one column per property. The series search reuses the layout for its packed
    /// candidates (<c>Cents</c> then holds √power × weight, <c>LeadWeight</c> the weight and <c>Index</c> the peak).
    /// </summary>
    private sealed class PeakTable(int capacity)
    {
        public readonly float[] Frequency = new float[capacity], Cents = new float[capacity], Power = new float[capacity],
            CentsMean = new float[capacity], CentsVar = new float[capacity], MaxDb = new float[capacity],
            SinceMax = new float[capacity], Age = new float[capacity], Slope = new float[capacity],
            LeadWeight = new float[capacity], PitchStable = new float[capacity];
        /// <summary>0 bed, 1 bass series, 2 lead series.</summary>
        public readonly byte[] Owner = new byte[capacity];
        public readonly int[] Index = new int[capacity];
    }

    /// <summary>Partials carried from one pass to the next.</summary>
    private sealed class CarryTable(int capacity)
    {
        public readonly float[] Frequency = new float[capacity], Age = new float[capacity], Missing = new float[capacity],
            CentsMean = new float[capacity], CentsVar = new float[capacity], MaxDb = new float[capacity],
            SinceMax = new float[capacity];
        public readonly bool[] Matched = new bool[capacity];

        /// <summary>Takes <paramref name="other"/>'s first <paramref name="count"/> partials, all unmatched.</summary>
        public void CopyFrom(CarryTable other, int count)
        {
            if (count <= 0) return;
            other.Frequency.AsSpan(0, count).CopyTo(Frequency);
            other.Age.AsSpan(0, count).CopyTo(Age);
            other.Missing.AsSpan(0, count).CopyTo(Missing);
            other.CentsMean.AsSpan(0, count).CopyTo(CentsMean);
            other.CentsVar.AsSpan(0, count).CopyTo(CentsVar);
            other.MaxDb.AsSpan(0, count).CopyTo(MaxDb);
            other.SinceMax.AsSpan(0, count).CopyTo(SinceMax);
            Matched.AsSpan(0, count).Clear();
        }
    }
}
