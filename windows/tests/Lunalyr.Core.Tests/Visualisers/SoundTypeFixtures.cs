// Deterministic synthetic music with ground truth, for the sound-type tests. Ported from Lunalyr for Mac's
// Tests/Swift/SoundTypeFixtures.swift. Every instrument is procedural (sines and seeded, filtered noise), so no audio
// files or devices are involved, and nothing is ever played. The instruments and arrangements are the ones the
// detector's constants were validated on. The Mac synthesises them with Accelerate; these helpers do the same vector
// operations in plain loops (the biquads run in double precision).

using Lunalyr.Core.Visualisers;

namespace Lunalyr.Core.Tests.Visualisers;

internal enum SoundClass { Kick, Snare, Hat, Bass, Vocal, Pad }

internal static class SoundClassExtensions
{
    public static readonly SoundClass[] All = Enum.GetValues<SoundClass>();

    public static bool IsTransient(this SoundClass c) => c is SoundClass.Kick or SoundClass.Snare or SoundClass.Hat;
}

internal sealed class SoundTruth
{
    public Dictionary<SoundClass, List<double>> Onsets { get; } = [];
    public Dictionary<SoundClass, List<(double Lower, double Upper)>> Active { get; } = [];

    public void Onset(SoundClass c, double t)
    {
        if (!Onsets.TryGetValue(c, out var list)) Onsets[c] = list = [];
        list.Add(t);
    }

    public void Span(SoundClass c, double lower, double upper)
    {
        if (!Active.TryGetValue(c, out var list)) Active[c] = list = [];
        list.Add((lower, upper));
    }

    public IReadOnlyList<double> OnsetsOf(SoundClass c) => Onsets.TryGetValue(c, out var list) ? list : [];

    public IReadOnlyList<(double Lower, double Upper)> ActiveOf(SoundClass c) => Active.TryGetValue(c, out var list) ? list : [];
}

internal sealed record SoundFixture(string Name, float[] Samples, double Fs, SoundTruth Truth)
{
    public double Seconds => Samples.Length / Fs;

    /// <summary>The same music through a different master: scaled, then optionally soft-clipped by tanh(x·drive)/tanh(drive).</summary>
    public SoundFixture Mastered(float gain, float? softClip = null, string name = "")
    {
        var output = new Vec(Samples);
        output.Scale(gain);
        if (softClip is { } drive) output.SoftClip(drive);
        return new SoundFixture(name, output.ToArray(), Fs, Truth);
    }

    /// <summary>The same music followed by digital silence.</summary>
    public SoundFixture AppendingSilence(double seconds) =>
        new(Name + "+silence", [.. Samples, .. new float[(int)(seconds * Fs)]], Fs, Truth);
}

internal sealed class SplitMix(ulong state)
{
    private ulong _state = state;

    public ulong Next()
    {
        unchecked
        {
            _state += 0x9E37_79B9_7F4A_7C15;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EB;
            return z ^ (z >> 31);
        }
    }

    public float Uniform() => (float)(Next() >> 40) / (1 << 24);

    /// <summary><paramref name="count"/> uniform values in −1...1; the same sequence as <see cref="Uniform"/>.</summary>
    public void Fill(float[] p, int count)
    {
        unchecked
        {
            var z0 = _state;
            for (var i = 0; i < count; i++)
            {
                z0 += 0x9E37_79B9_7F4A_7C15;
                var z = z0;
                z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9;
                z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EB;
                z ^= z >> 31;
                p[i] = (float)(long)(z >> 40) * (2f / 16_777_216) - 1;
            }
            _state = z0;
        }
    }
}

/// <summary>A buffer of floats for in-place vector work (the Mac's vDSP-backed Vec).</summary>
internal sealed class Vec
{
    public readonly float[] P;

    public Vec(int n, float value = 0)
    {
        P = new float[Math.Max(1, n)];
        if (value != 0) Array.Fill(P, value);
    }

    public Vec(float[] values)
    {
        P = new float[Math.Max(1, values.Length)];
        values.CopyTo(P, 0);
    }

    public int N => P.Length;

    public float[] ToArray() => (float[])P.Clone();

    /// <summary>Seconds since the start, for <paramref name="n"/> samples at <paramref name="fs"/>, from <paramref name="offset"/> seconds (vDSP_vramp in single precision).</summary>
    public static Vec Time(int n, double fs, double offset = 0)
    {
        var v = new Vec(n);
        float a = (float)offset, b = (float)(1 / fs);
        for (var i = 0; i < v.N; i++) v.P[i] = a + i * b;
        return v;
    }

    /// <summary>p = exp(scale·p)</summary>
    public Vec Exp(float scale)
    {
        for (var i = 0; i < N; i++) P[i] = MathF.Exp(P[i] * scale);
        return this;
    }

    /// <summary>p = min(1, max(0, p·scale + offset))</summary>
    public Vec Clamped(float scale, float offset = 0)
    {
        for (var i = 0; i < N; i++)
        {
            var v = P[i] * scale + offset;
            P[i] = v < 0 ? 0 : v > 1 ? 1 : v;
        }
        return this;
    }

    public Vec Multiply(Vec other)
    {
        var m = Math.Min(N, other.N);
        for (var i = 0; i < m; i++) P[i] *= other.P[i];
        return this;
    }

    /// <summary>p += other × gain</summary>
    public void Add(Vec other, float gain = 1, int at = 0)
    {
        var m = Math.Min(other.N, N - at);
        if (at < 0 || m <= 0) return;
        for (var i = 0; i < m; i++) P[at + i] += other.P[i] * gain;
    }

    public void Scale(float gain)
    {
        for (var i = 0; i < N; i++) P[i] *= gain;
    }

    public void SoftClip(float drive)
    {
        Scale(drive);
        for (var i = 0; i < N; i++) P[i] = MathF.Tanh(P[i]);
        Scale(1 / MathF.Tanh(drive));
    }
}

/// <summary>The same for doubles, where phases need the precision.</summary>
internal sealed class VecD
{
    public readonly double[] P;

    /// <summary>p[i] = start + i·step</summary>
    public VecD(int n, double start, double step)
    {
        P = new double[Math.Max(1, n)];
        for (var i = 0; i < P.Length; i++) P[i] = start + i * step;
    }

    public int N => P.Length;

    /// <summary>sin and cos of p, in single precision.</summary>
    public (Vec Sin, Vec Cos) SinCos()
    {
        Vec s = new(N), c = new(N);
        for (var i = 0; i < N; i++)
        {
            s.P[i] = (float)Math.Sin(P[i]);
            c.P[i] = (float)Math.Cos(P[i]);
        }
        return (s, c);
    }

    /// <summary>sin and cos of 2π·p for p in cycles; only the fraction matters.</summary>
    public (Vec Sin, Vec Cos) Cycles()
    {
        for (var i = 0; i < N; i++) P[i] = (P[i] - Math.Truncate(P[i])) * (2 * Math.PI);
        return SinCos();
    }
}

/// <summary>RBJ cookbook biquads in cascade (vDSP_biquad); the state carries across calls.</summary>
internal sealed class Filter
{
    public enum Kind { Lowpass, Highpass, Bandpass }

    private readonly double[] _coefficients;
    private readonly double[] _state;

    public Filter(double fs, params (Kind Kind, double Frequency, double Q)[] sections)
    {
        _coefficients = new double[5 * sections.Length];
        for (var s = 0; s < sections.Length; s++)
        {
            var (kind, frequency, q) = sections[s];
            var w = 2 * Math.PI * Math.Min(frequency, fs * 0.49) / fs;
            double c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            double b0, b1, b2;
            switch (kind)
            {
                case Kind.Lowpass: b0 = (1 - c) / 2; b1 = 1 - c; b2 = (1 - c) / 2; break;
                case Kind.Highpass: b0 = (1 + c) / 2; b1 = -(1 + c); b2 = (1 + c) / 2; break;
                default: b0 = alpha; b1 = 0; b2 = -alpha; break;
            }
            var a0 = 1 + alpha;
            _coefficients[5 * s] = b0 / a0;
            _coefficients[5 * s + 1] = b1 / a0;
            _coefficients[5 * s + 2] = b2 / a0;
            _coefficients[5 * s + 3] = -2 * c / a0;
            _coefficients[5 * s + 4] = (1 - alpha) / a0;
        }
        _state = new double[4 * sections.Length];
    }

    /// <summary>One pole: y = b·x + a·y[−1].</summary>
    public Filter(double pole, double gain)
    {
        _coefficients = [gain, 0, 0, -pole, 0];
        _state = new double[4];
    }

    public Vec Run(Vec x)
    {
        var y = new Vec(x.N);
        var sections = _coefficients.Length / 5;
        for (var i = 0; i < x.N; i++)
        {
            double v = x.P[i];
            for (var s = 0; s < sections; s++)
            {
                int c = 5 * s, z = 4 * s;
                var output = _coefficients[c] * v + _coefficients[c + 1] * _state[z] + _coefficients[c + 2] * _state[z + 1]
                    - _coefficients[c + 3] * _state[z + 2] - _coefficients[c + 4] * _state[z + 3];
                _state[z + 1] = _state[z]; _state[z] = v;
                _state[z + 3] = _state[z + 2]; _state[z + 2] = output;
                v = output;
            }
            y.P[i] = (float)v;
        }
        return y;
    }
}

/// <summary>Adds instruments into a sample buffer and records what it added.</summary>
internal sealed class SoundSynth
{
    public double Fs { get; }
    private readonly Vec _out;
    private readonly SoundTruth _truth = new();
    private readonly SplitMix _rng;

    public SoundSynth(double seconds, double fs, ulong seed)
    {
        Fs = fs;
        _out = new Vec((int)(seconds * fs));
        _rng = new SplitMix(seed);
    }

    public SoundFixture Fixture(string name) => new(name, _out.ToArray(), Fs, _truth);

    private int Index(double t) => (int)Math.Round(t * Fs, MidpointRounding.AwayFromZero);

    private int Length(int start, double seconds) => Math.Max(0, Math.Min((int)(seconds * Fs), _out.N - start));

    private Vec Time(int n, double offset = 0) => Vec.Time(n, Fs, offset);

    /// <summary>Uniform noise in −1...1.</summary>
    private Vec Noise(int n)
    {
        var v = new Vec(n);
        _rng.Fill(v.P, v.N);
        return v;
    }

    /// <summary>The first <paramref name="m"/> of <paramref name="n"/> noise values; the generator moves on by all n.</summary>
    private Vec Noise(int n, int keeping)
    {
        var all = Noise(n);
        var v = new Vec(keeping);
        Array.Copy(all.P, v.P, keeping);
        return v;
    }

    private const int TableLength = 4096;

    /// <summary>One period of Σ amplitudes[h − 1]·sin(2π·h·j/L), plus a guard sample.</summary>
    private static Vec Wavetable(float[] amplitudes)
    {
        const int length = TableLength;
        var table = new Vec(length + 1);
        var angle = Vec.Time(length + 1, length / (2 * Math.PI));
        var term = new Vec(length + 1);
        for (var h = 0; h < amplitudes.Length; h++)
        {
            float scale = h + 1;
            for (var i = 0; i < term.N; i++) term.P[i] = MathF.Sin(angle.P[i] * scale);
            table.Add(term, amplitudes[h]);
        }
        table.P[length] = table.P[0];
        return table;
    }

    /// <summary><paramref name="table"/> played at <paramref name="frequency"/>, from <paramref name="phase"/> radians at sample −1.</summary>
    private Vec Play(Vec table, double frequency, double phase, int n)
    {
        var cycles = new VecD(n, phase / (2 * Math.PI) + frequency / Fs, frequency / Fs);
        var y = new Vec(n);
        for (var i = 0; i < n; i++)
        {
            var c = cycles.P[i];
            var index = (float)((c - Math.Truncate(c)) * TableLength);
            // vDSP_vlint: A[⌊b⌋] + frac(b)·(A[⌊b⌋+1] − A[⌊b⌋]).
            var whole = (int)index;
            var fraction = index - whole;
            if (whole >= TableLength) { y.P[i] = table.P[TableLength]; continue; }
            y.P[i] = table.P[whole] + fraction * (table.P[whole + 1] - table.P[whole]);
        }
        return y;
    }

    /// <summary>
    /// Σ_h amplitude_h(i)·sin(h·φ_i), from sin φ and cos φ by the recurrence sin((h+1)φ) = 2cos φ·sin(hφ) − sin((h−1)φ).
    /// <paramref name="amplitude"/> fills harmonic h's per-sample amplitude (h is 1-based).
    /// </summary>
    private static Vec Harmonics((Vec Sin, Vec Cos) baseWave, int count, Action<int, Vec> amplitude)
    {
        var n = baseWave.Sin.N;
        Vec y = new(n), a = new(n), twoCos = new(n);
        for (var i = 0; i < n; i++) twoCos.P[i] = baseWave.Cos.P[i] * 2;
        Vec below = new(n), current = new(n);
        Array.Copy(baseWave.Sin.P, current.P, n);
        for (var harmonic = 1; harmonic <= Math.Max(1, count); harmonic++)
        {
            if (harmonic > 1)
            {
                for (var i = 0; i < n; i++) below.P[i] = twoCos.P[i] * current.P[i] - below.P[i];
                (below, current) = (current, below);
            }
            amplitude(harmonic, a);
            for (var i = 0; i < n; i++) y.P[i] = current.P[i] * a.P[i] + y.P[i];
        }
        return y;
    }

    // MARK: Drums

    public enum KickKind { Acoustic, Electronic, EightOhEight }

    public void Kick(double t0, float gain, KickKind kind = KickKind.Electronic, float click = 0.25f)
    {
        _truth.Onset(SoundClass.Kick, t0);
        var (f1, f2, sweep, decay, seconds) = kind switch
        {
            KickKind.Acoustic => (140.0, 58.0, 0.02, 0.09, 0.5),
            KickKind.Electronic => (160.0, 52.0, 0.03, 0.14, 0.6),
            _ => (95.0, 49.0, 0.04, 0.55, 1.4),
        };
        int start = Index(t0), n = Length(start, seconds);
        if (n <= 0) return;
        // Phase = 2π/fs·Σ_{j≤i} f(j/fs) for f(t) = f2 + (f1 − f2)·e^(−t/sweep):
        // f2·(i + 1) + k·(1 − r^(i+1)) with r = e^(−1/(fs·sweep)), k = (f1 − f2)/(1 − r).
        var r = Math.Exp(-1 / (Fs * sweep));
        var k = (f1 - f2) / (1 - r);
        var steps = new VecD(n, 1, 1);
        var scale = -1 / (Fs * sweep);
        var radians = 2 * Math.PI / Fs;
        for (var i = 0; i < n; i++)
        {
            var geometric = Math.Exp(steps.P[i] * scale) * -k + k;
            steps.P[i] = (steps.P[i] * f2 + geometric) * radians;
        }
        var y = steps.SinCos().Sin;
        y.Multiply(Time(n).Clamped(1f / 0.0015f));
        y.Multiply(Time(n).Exp((float)(-1 / decay)));
        if (kind == KickKind.EightOhEight) y.SoftClip(1.6f);
        _out.Add(y, gain, start);
        // The beater click: band-passed noise with a 1.5 ms decay, gone in 30 ms. The noise is drawn for every sample
        // of the kick, as the validated realisation draws it, so every later instrument gets the same noise.
        var m = Math.Min(n, (int)(0.03 * Fs));
        var c = new Filter(Fs, (Filter.Kind.Bandpass, 3500, 0.7)).Run(Noise(n, keeping: m));
        c.Multiply(Time(m).Exp(-1f / 0.0015f));
        _out.Add(c, gain * click * 3, start);
    }

    public void Snare(double t0, float gain)
    {
        _truth.Onset(SoundClass.Snare, t0);
        int start = Index(t0), n = Length(start, 0.45);
        if (n <= 0) return;
        var body = new VecD(n, 190 / Fs, 190 / Fs).Cycles().Sin.Multiply(Time(n).Exp(-1f / 0.06f));
        var second = new VecD(n, 335 / Fs, 335 / Fs).Cycles().Sin.Multiply(Time(n).Exp(-1f / 0.04f));
        for (var i = 0; i < body.N; i++) body.P[i] = body.P[i] * 0.55f + second.P[i] * 0.3f;
        var wires = new Filter(Fs, (Filter.Kind.Highpass, 1500, 0.707), (Filter.Kind.Lowpass, 9000, 0.707)).Run(Noise(n));
        body.Add(wires.Multiply(Time(n).Exp(-1f / 0.11f)), 1.6f);
        body.Multiply(Time(n).Clamped(1f / 0.0006f));
        _out.Add(body, gain, start);
    }

    public void Clap(double t0, float gain)
    {
        _truth.Onset(SoundClass.Snare, t0);
        int start = Index(t0), n = Length(start, 0.35);
        if (n <= 0) return;
        // Three 4 ms bursts and a 70 ms tail.
        var envelope = new Vec(n);
        foreach (var (offset, tau, weight) in new[] { (0.0, 0.004, 1f), (0.009, 0.004, 1f), (0.019, 0.004, 1f), (0.02, 0.07, 0.6f) })
        {
            var from = Math.Min(n, (int)Math.Ceiling(offset * Fs));
            if (from >= n) continue;
            envelope.Add(Time(n - from, from / Fs - offset).Exp((float)(-1 / tau)), weight, from);
        }
        var x = new Filter(Fs, (Filter.Kind.Highpass, 900, 0.707), (Filter.Kind.Lowpass, 4500, 0.707)).Run(Noise(n));
        _out.Add(x.Multiply(envelope), gain * 1.4f, start);
    }

    public void Hat(double t0, float gain, bool open = false)
    {
        _truth.Onset(SoundClass.Hat, t0);
        int start = Index(t0), n = Length(start, open ? 0.9 : 0.2);
        if (n <= 0) return;
        var x = new Filter(Fs, (Filter.Kind.Highpass, 7000, 0.707), (Filter.Kind.Highpass, 7000, 0.707)).Run(Noise(n));
        x.Add(new Filter(Fs, (Filter.Kind.Bandpass, 10_500, 1.2)).Run(x), 1.5f);
        x.Multiply(Time(n).Clamped(1f / 0.0003f));
        x.Multiply(Time(n).Exp((float)(-1 / (open ? 0.22 : 0.028))));
        _out.Add(x, gain, start);
    }

    // MARK: Tonal instruments

    /// <summary>Band-limited saw through a gentle low-pass, sharp attack.</summary>
    public void BassNote(double t0, double duration, double frequency, float gain, bool pluck = false)
    {
        _truth.Span(SoundClass.Bass, t0, t0 + duration);
        int start = Index(t0), n = Length(start, duration + 0.03);
        var count = Math.Max(1, (int)(2500 / frequency));
        var phase = (double)_rng.Uniform() * 2 * Math.PI;
        if (n <= 0) return;
        Vec y;
        if (pluck)
        {
            // The cutoff falls from 1550 to 450 Hz, so amplitudes change per sample.
            var inverse = Time(n).Exp(-1f / 0.12f);
            for (var i = 0; i < inverse.N; i++)
            {
                var cutoff = inverse.P[i] * 1100f + 450f;
                cutoff *= cutoff;
                cutoff *= cutoff;
                inverse.P[i] = 1 / cutoff; // cutoff^−4
            }
            var baseWave = new VecD(n, phase / (2 * Math.PI) + frequency / Fs, frequency / Fs).Cycles();
            y = Harmonics(baseWave, count, (h, amplitude) =>
            {
                var f4 = (float)Math.Pow(h * frequency, 4);
                for (var i = 0; i < amplitude.N; i++) amplitude.P[i] = 1 / MathF.Sqrt(inverse.P[i] * f4 + 1);
                amplitude.Scale(1f / h);
            });
        }
        else
        {
            var amplitudes = new float[count];
            for (var h = 1; h <= count; h++) amplitudes[h - 1] = (float)(1.0 / h / Math.Sqrt(1 + Math.Pow(h * frequency / 700, 4)));
            y = Play(Wavetable(amplitudes), frequency, phase, n);
        }
        // 4 ms attack, a slow (or plucked) decay, a 25 ms release.
        var envelope = Time(n).Clamped(1f / 0.004f).Multiply(Time(n).Exp(pluck ? -1f / 0.4f : -1f / 3));
        var releaseFrom = Math.Min(n, (int)Math.Floor(duration * Fs) + 1);
        if (releaseFrom < n)
        {
            var tail = Time(n - releaseFrom, releaseFrom / Fs - duration).Clamped(-1f / 0.025f, 1);
            for (var i = 0; i < tail.N; i++) envelope.P[releaseFrom + i] *= tail.P[i];
        }
        _out.Add(y.Multiply(envelope), gain, start);
    }

    public readonly record struct Syllable(double Start, double Duration, double Midi, int Vowel);

    /// <summary>(F, bandwidth, gain) of each vowel's formants: a, i, u, e, o.</summary>
    private static readonly (double F, double B, double A)[][] Vowels =
    [
        [(800, 90, 1), (1150, 110, 0.6), (2900, 160, 0.25)],
        [(300, 60, 1), (2300, 130, 0.45), (3000, 170, 0.3)],
        [(350, 70, 1), (600, 90, 0.7), (2700, 160, 0.15)],
        [(450, 70, 1), (2000, 120, 0.5), (2550, 150, 0.3)],
        [(500, 80, 1), (850, 100, 0.65), (2800, 160, 0.2)],
    ];

    /// <summary>
    /// Additive sung voice: f0 glides between notes, delayed vibrato, jitter, formant-shaped harmonics and syllabic
    /// envelopes. Optional consonants.
    /// </summary>
    public void Vocal(IReadOnlyList<Syllable> syllables, float gain, bool consonants = false, double vibrato = 35)
    {
        if (syllables.Count == 0) return;
        var first = syllables[0];
        var last = syllables[^1];
        double t0 = first.Start, t1 = last.Start + last.Duration;
        int start = Index(t0), n = Length(start, t1 - t0 + 0.08);
        if (n <= 0) return;
        foreach (var s in syllables) _truth.Span(SoundClass.Vocal, s.Start, s.Start + s.Duration);
        static double Hz(double midi) => 440 * Math.Pow(2, (midi - 69) / 12);
        // Syllable s sounds from sample bounds[s] (its start) to bounds[s + 1].
        var bounds = new int[syllables.Count + 1];
        for (var s = 0; s < syllables.Count; s++)
            bounds[s] = s == 0 ? 0 : Math.Min(n, (int)Math.Ceiling((syllables[s].Start - t0) * Fs));
        bounds[syllables.Count] = n;
        // Per syllable: the glide from the previous note, the vibrato depth (delayed 0.18 s) and the envelope (35 ms
        // attack, 50 ms release, smoothstepped).
        Vec baseF0 = new(n), depth = new(n), envelope = new(n);
        for (var s = 0; s < syllables.Count; s++)
        {
            if (bounds[s] >= bounds[s + 1]) continue;
            int from = bounds[s], m = bounds[s + 1] - from;
            var syllable = syllables[s];
            var local = t0 + from / Fs - syllable.Start;
            var target = Hz(syllable.Midi);
            if (s == 0)
            {
                Array.Fill(baseF0.P, (float)target, from, m);
            }
            else
            {
                var previous = Hz(syllables[s - 1].Midi);
                var glide = Time(m, local).Clamped(1f / 0.07f).Exp((float)Math.Log(target / previous));
                glide.Scale((float)previous);
                Array.Copy(glide.P, 0, baseF0.P, from, m);
            }
            var vibratoDepth = Time(m, local).Clamped(1f / 0.2f, -0.18f / 0.2f);
            vibratoDepth.Scale((float)vibrato);
            Array.Copy(vibratoDepth.P, 0, depth.P, from, m);
            var e = Time(m, local).Clamped(1f / 0.035f)
                .Multiply(Time(m, local).Clamped(-1f / 0.05f, (float)(1 + syllable.Duration / 0.05)));
            for (var i = 0; i < m; i++)
            {
                var v = e.P[i];
                envelope.P[from + i] = v * v * (v * -2f + 3f);
            }
        }
        // cents = depth·sin(2π·5.6·t) + 3·tanh(jitter/50), the jitter a leaky random walk.
        var cents = new VecD(n, 5.6 * t0, 5.6 / Fs).Cycles().Sin.Multiply(depth);
        var jitter = new Filter(pole: 0.998, gain: 0.6).Run(Noise(n));
        jitter.Scale(1f / 50);
        for (var i = 0; i < jitter.N; i++) jitter.P[i] = MathF.Tanh(jitter.P[i]);
        cents.Add(jitter, 3);
        var f0 = baseF0.Multiply(cents.Exp((float)(Math.Log(2.0) / 1200)));
        // The phase is the running sum of 2π·f0/fs.
        var radians = 2 * Math.PI / Fs;
        var phase = new VecD(n, 0, 0);
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            sum += radians * f0.P[i];
            phase.P[i] = sum;
        }
        // Formant gains, held for 32 samples at a time as each block's first sample's f0 and vowel give them.
        const int harmonicCount = 39;
        var blocks = (n + 31) / 32;
        var blockF0 = new Vec(blocks);
        for (var b = 0; b < blocks; b++) blockF0.P[b] = f0.P[32 * b];
        var gains = new Vec[harmonicCount];
        for (var h = 0; h < harmonicCount; h++) gains[h] = new Vec(blocks + 1);
        for (var s = 0; s < syllables.Count; s++)
        {
            // The blocks whose first sample lies in syllable s.
            int b = (bounds[s] + 31) / 32, e = Math.Min(blocks, (bounds[s + 1] + 31) / 32);
            if (b >= e) continue;
            var m = e - b;
            for (var h = 1; h <= harmonicCount; h++)
            {
                var g = gains[h - 1].P;
                Array.Fill(g, 0.02f, b, m);
                foreach (var (formant, bandwidth, amplitude) in Vowels[syllables[s].Vowel])
                {
                    float harmonic = h, minusF = (float)-formant, scale = (float)(2 / bandwidth), weight = (float)amplitude;
                    for (var k = 0; k < m; k++)
                    {
                        var x = (blockF0.P[b + k] * harmonic + minusF) * scale;
                        x = x * x + 1;
                        g[b + k] += weight / x; // a / (1 + x²)
                    }
                }
                var falloff = (float)(1 / Math.Pow(h, 0.7));
                var limit = 6000f / h;
                for (var k = 0; k < m; k++)
                {
                    g[b + k] *= falloff;
                    if (blockF0.P[b + k] >= limit) g[b + k] = 0;
                }
            }
        }
        var y = Harmonics(phase.SinCos(), harmonicCount, (h, amplitude) =>
        {
            var g = gains[h - 1].P;
            for (var i = 0; i < amplitude.N; i++) amplitude.P[i] = g[i / 32];
        });
        _out.Add(y.Multiply(envelope), gain * 0.5f, start);
        if (!consonants) return;
        var sibilance = new Filter(Fs, (Filter.Kind.Highpass, 4500, 0.707), (Filter.Kind.Bandpass, 7000, 0.9));
        for (var k = 0; k < syllables.Count; k++)
        {
            if (k % 3 != 1) continue;
            // A sibilant just before the syllable ("s"), soft attack.
            int at = Index(syllables[k].Start - 0.11), m = (int)(0.12 * Fs);
            var x = sibilance.Run(Noise(m)).Multiply(Time(m).Clamped(1f / 0.02f));
            x.Multiply(Time(m).Clamped(-1f / 0.04f, 1 + 0.08f / 0.04f));
            _out.Add(x, gain * 0.22f, at);
        }
        var plosive = new Filter(Fs, (Filter.Kind.Bandpass, 4000, 0.8));
        for (var k = 0; k < syllables.Count; k++)
        {
            if (k % 3 != 2) continue;
            // A plosive "t": a 15 ms burst.
            int at = Index(syllables[k].Start - 0.02), m = (int)(0.015 * Fs);
            _out.Add(plosive.Run(Noise(m)).Multiply(Time(m).Exp(-1f / 0.005f)), gain * 0.3f, at);
        }
    }

    /// <summary>Supersaw-style pad chord: three detuned voices per note, slow attack.</summary>
    public void Pad(double t0, double duration, double[] midis, float gain, double attack = 0.7, double release = 1.0)
    {
        _truth.Span(SoundClass.Pad, t0 + 0.3, t0 + duration);
        int start = Index(t0), n = Length(start, duration + release);
        var voices = new List<(double F, double Phase)>();
        foreach (var midi in midis)
        {
            var f = 440 * Math.Pow(2, (midi - 69) / 12);
            foreach (var detune in new[] { -8.0, 0, 7 })
                voices.Add((f * Math.Pow(2, detune / 1200), (double)_rng.Uniform() * 2 * Math.PI));
        }
        if (n <= 0) return;
        var y = new Vec(n);
        foreach (var (f, phase) in voices)
        {
            var count = Math.Max(1, (int)(5000 / f));
            var amplitudes = new float[count];
            for (var h = 1; h <= count; h++) amplitudes[h - 1] = (float)(1.0 / h / Math.Sqrt(1 + Math.Pow(h * f / 2000, 2)));
            y.Add(Play(Wavetable(amplitudes), f, phase, n));
        }
        // A raised-cosine attack, a linear release and a slow ±1.5 dB swell.
        var envelope = new Vec(n, 1);
        var rise = Math.Min(n, (int)Math.Ceiling(attack * Fs));
        if (rise > 0)
        {
            // 0.5 − 0.5·cos(π·t/attack); cos(x) = sin(x + π/2).
            var curve = new VecD(rise, 0.25, 1 / (2 * attack * Fs)).Cycles().Sin;
            for (var i = 0; i < curve.N; i++) envelope.P[i] = curve.P[i] * -0.5f + 0.5f;
        }
        var releaseFrom = Math.Min(n, (int)Math.Floor(duration * Fs) + 1);
        if (releaseFrom < n)
        {
            var tail = Time(n - releaseFrom, releaseFrom / Fs - duration).Clamped((float)(-1 / release), 1);
            for (var i = 0; i < tail.N; i++) envelope.P[releaseFrom + i] *= tail.P[i];
        }
        var swell = new VecD(n, 0.15 * t0, 0.15 / Fs).Cycles().Sin;
        for (var i = 0; i < swell.N; i++) swell.P[i] = swell.P[i] * 0.17f + 1;
        _out.Add(y.Multiply(envelope.Multiply(swell)), gain, start);
    }

    public void Noise(double t0, double duration, float gain, bool pink)
    {
        int start = Index(t0), n = Length(start, duration);
        if (n <= 0) return;
        var w = Noise(n);
        if (!pink) { _out.Add(w, gain, start); return; }
        // Paul Kellet's economy pink filter: three one-pole stages plus the input.
        var y = new Vec(n);
        foreach (var (a, b) in new[] { (0.99765, 0.0990460), (0.96300, 0.2965164), (0.57000, 1.0526913) })
            y.Add(new Filter(pole: a, gain: b).Run(w));
        y.Add(w, 0.1848f);
        _out.Add(y, gain * 0.25f, start);
    }
}

/// <summary>The fixture set.</summary>
internal static class SoundFixtures
{
    public const double Bpm = 120.0;
    public static double Beat => 60 / Bpm;

    public static SoundFixture Kicks(double fs, SoundSynth.KickKind kind = SoundSynth.KickKind.Electronic)
    {
        var s = new SoundSynth(10, fs, 11);
        double t = 0.5;
        var k = 0;
        float[] gains = [1.0f, 0.75f, 0.9f, 0.65f];
        while (t < 9.6)
        {
            s.Kick(t, 0.55f * gains[k % 4], kind);
            t += Beat; k++;
        }
        return s.Fixture(kind == SoundSynth.KickKind.EightOhEight ? "808" : "kick");
    }

    public static SoundFixture Hats(double fs)
    {
        var s = new SoundSynth(10, fs, 12);
        double t = 0.5;
        var k = 0;
        while (t < 9.6)
        {
            s.Hat(t, 0.16f * (k % 4 == 0 ? 1 : (k % 2 == 0 ? 0.7f : 0.5f)), open: k % 32 == 30);
            t += Beat / 4; k++;
        }
        return s.Fixture("hats");
    }

    public static SoundFixture Snares(double fs)
    {
        var s = new SoundSynth(10, fs, 13);
        var t = 0.5 + Beat;
        var k = 0;
        while (t < 9.6)
        {
            if (k % 4 == 3) s.Clap(t, 0.35f); else s.Snare(t, 0.32f);
            t += 2 * Beat; k++;
        }
        return s.Fixture("snare");
    }

    public static SoundSynth.Syllable[] Melody()
    {
        // Two phrases; syllables 0.18-0.75 s with small gaps and legato runs.
        (double, double, double, int)[] notes =
        [
            (0.6, 0.30, 69, 0), (0.95, 0.25, 71, 3), (1.25, 0.55, 72, 4), (1.85, 0.20, 71, 1), (2.08, 0.75, 69, 0),
            (3.0, 0.28, 64, 2), (3.32, 0.28, 67, 0), (3.62, 0.62, 69, 3), (4.3, 0.22, 67, 4), (4.55, 0.9, 64, 0),
            (5.8, 0.25, 72, 1), (6.1, 0.25, 74, 0), (6.4, 0.6, 76, 4), (7.05, 0.3, 74, 3), (7.4, 1.1, 72, 0),
        ];
        return [.. notes.Select(n => new SoundSynth.Syllable(n.Item1, n.Item2, n.Item3, n.Item4))];
    }

    public static SoundFixture Vocal(double fs, bool consonants)
    {
        var s = new SoundSynth(9.5, fs, 14);
        s.Vocal(Melody(), 0.30f, consonants);
        return s.Fixture(consonants ? "vocal+consonants" : "vocal");
    }

    public static readonly double[][] Chords = [[57, 60, 64, 67], [53, 57, 60, 64], [48, 55, 60, 64], [55, 59, 62, 67]];

    public static SoundFixture Pads(double fs)
    {
        var s = new SoundSynth(11, fs, 15);
        for (var i = 0; i < Chords.Length; i++) s.Pad(0.5 + i * 2.5, 2.6, Chords[i], 0.018f);
        return s.Fixture("pad");
    }

    public static SoundFixture Bassline(double fs)
    {
        var s = new SoundSynth(10, fs, 16);
        double[] roots = [33, 29, 36, 31]; // A1 F1 C2 G1: 55, 43.7, 65.4, 49 Hz
        double t = 0.5;
        var k = 0;
        while (t < 6.4)
        {
            // Staccato-ish 8ths with rests.
            var midi = roots[(k / 8) % 4] + (k % 8 == 6 ? 12 : 0);
            if (k % 8 != 3)
                s.BassNote(t, Beat / 2 * 0.8, 440 * Math.Pow(2, (midi - 69) / 12), 0.22f, pluck: k % 16 >= 8);
            t += Beat / 2; k++;
        }
        // Legato whole notes.
        double[] whole = [33.0, 29, 31];
        for (var i = 0; i < whole.Length; i++)
            s.BassNote(6.5 + i * 1.0, 0.98, 440 * Math.Pow(2, (whole[i] - 69) / 12), 0.22f);
        return s.Fixture("bass");
    }

    /// <summary>
    /// A 16.5-second arrangement: four-on-the-floor kick, snare/clap on 2 and 4, 8th/16th hats with open hats, bass
    /// line, pad chords, sung phrases with consonants. Stem RMS relative to the mix: kick −4.5, bass −4.8, vocal −7,
    /// snare −12, pad −14, hats −22 dB, a typical pop balance.
    /// </summary>
    public static SoundFixture Mix(double fs, ulong seed = 21)
    {
        var s = new SoundSynth(16.5, fs, seed);
        var bar = 4 * Beat;
        for (var b = 0; b < 8; b++)
        {
            var t0 = 0.25 + b * bar;
            for (var q = 0; q < 4; q++)
            {
                var tq = t0 + q * Beat;
                if (!(b == 4 && q >= 2)) s.Kick(tq, 0.5f * (q % 2 == 0 ? 1 : 0.85f));
                if (q == 1 || q == 3)
                {
                    if (b % 2 == 1 && q == 3) s.Clap(tq, 0.42f); else s.Snare(tq, 0.40f);
                }
            }
            var sixteenths = b == 2 || b == 3 || b == 6;
            for (var e = 0; e < (sixteenths ? 16 : 8); e++)
            {
                var te = t0 + e * (sixteenths ? Beat / 4 : Beat / 2) + (sixteenths ? 0 : Beat / 4);
                s.Hat(te, 0.07f * (e % 2 == 0 ? 1 : 0.75f), open: !sixteenths && e == 7 && b % 2 == 1);
            }
            double[] roots = [33.0, 29, 36, 31];
            var root = roots[b % 4];
            for (var e = 0; e < 8; e++)
            {
                if (e == 3) continue;
                var te = t0 + e * Beat / 2;
                s.BassNote(te, Beat / 2 * 0.85, 440 * Math.Pow(2, (root + (e == 6 ? 12 : 0) - 69) / 12), 0.16f);
            }
        }
        for (var i = 0; i < Chords.Length; i++) s.Pad(0.25 + i * 2 * bar, 2 * bar, Chords[i], 0.014f);
        var phrase = Melody().Select(x => x with { Start = x.Start + 3.6 }).ToArray();
        s.Vocal(phrase, 0.45f, consonants: true);
        return s.Fixture("mix");
    }

    public static SoundFixture PinkNoise(double fs)
    {
        var s = new SoundSynth(8, fs, 17);
        s.Noise(0.5, 7, 0.15f, pink: true);
        return s.Fixture("pink noise");
    }
}

// MARK: - Feeding fixtures to the detector

/// <summary>
/// The calibrated power spectrum AudioAnalyzer hands the sound-type detector, computed directly: a Hann window
/// (vDSP_HANN_NORM) over the fftSize samples ending at a position, zero-padded at the start, one plain FFT,
/// |X|² / (2·N·Σw²) and bin 0 cleared.
/// </summary>
internal sealed class PlainSpectrum
{
    public int FftSize { get; }
    private readonly RealFft _fft;
    private readonly float[] _window, _windowed;
    public float[] Power { get; }
    private readonly float _scale;

    public PlainSpectrum(int fftSize)
    {
        FftSize = fftSize;
        _fft = new RealFft(fftSize);
        _window = new float[fftSize];
        _windowed = new float[fftSize];
        Power = new float[fftSize / 2];
        for (var n = 0; n < fftSize; n++) _window[n] = (float)(Math.Sqrt(2.0 / 3.0) * (1 - Math.Cos(2 * Math.PI * n / fftSize)));
        float sumSquares = 0;
        foreach (var w in _window) sumSquares += w * w;
        _scale = 1 / (2 * (float)fftSize * sumSquares);
    }

    /// <summary>The spectrum of the window ending just before samples[position].</summary>
    public float[] Compute(float[] samples, int position)
    {
        var available = Math.Min(position, FftSize);
        Array.Clear(_windowed, 0, FftSize - available);
        for (var i = 0; i < available; i++)
        {
            var at = FftSize - available + i;
            _windowed[at] = samples[position - available + i] * _window[at];
        }
        _fft.PowerSpectrum(_windowed, Power);
        for (var k = 0; k < Power.Length; k++) Power[k] *= _scale;
        Power[0] = 0;
        return Power;
    }
}

/// <summary>
/// A fixture fed to a SoundTypeDetector the way AudioAnalyzer feeds it (a spectrum every quarter window, or every
/// fftSize / hopDivisor samples, stamped with the time of its newest sample), straight from memory, and the history
/// read at every pass as the render loop would read it.
/// </summary>
internal sealed class SoundTypeRun
{
    public SoundFixture Fixture { get; }
    public List<double> Times { get; } = [];
    public List<SoundTypeFeatures> Reads { get; } = [];
    /// <summary>Published (backdated) onset times of each transient class.</summary>
    public Dictionary<SoundClass, List<double>> Onsets { get; } =
        new() { [SoundClass.Kick] = [], [SoundClass.Snare] = [], [SoundClass.Hat] = [] };

    public SoundTypeRun(SoundFixture fixture, int fftSize, int hopDivisor = 4)
    {
        Fixture = fixture;
        var hop = fftSize / hopDivisor;
        var spectrum = new PlainSpectrum(fftSize);
        var detector = new SoundTypeDetector();
        var history = new SoundTypeHistory();
        var binHz = (float)fixture.Fs / fftSize;
        var last = new SoundTypeFeatures();
        var all = fixture.Samples;
        for (var position = hop; position <= all.Length; position += hop)
        {
            var time = position / fixture.Fs;
            detector.Process(spectrum.Compute(all, position), binHz, hop / fixture.Fs, time);
            history.Record(detector.Current, time);
            var read = history.Read(time);
            if (read.KickOnsets != last.KickOnsets) Onsets[SoundClass.Kick].Add(read.KickTime);
            if (read.SnareOnsets != last.SnareOnsets) Onsets[SoundClass.Snare].Add(read.SnareTime);
            if (read.HatOnsets != last.HatOnsets) Onsets[SoundClass.Hat].Add(read.HatTime);
            last = read;
            Times.Add(time);
            Reads.Add(read);
        }
    }

    // MARK: Scoring

    /// <summary>Greedy one-to-one matching of published onsets to the truth: an estimate may lie 25 ms before to <paramref name="window"/> after the true onset.</summary>
    public (double F1, double Recall, int FalsePositives) F1(SoundClass c, double window = 0.06)
    {
        var truth = Fixture.Truth.OnsetsOf(c).Order().ToArray();
        var used = new bool[truth.Length];
        int hits = 0, misses = 0;
        foreach (var onset in Onsets.GetValueOrDefault(c) ?? [])
        {
            var k = -1;
            for (var i = 0; i < truth.Length; i++)
            {
                if (!used[i] && onset - truth[i] >= -0.025 && onset - truth[i] <= window) { k = i; break; }
            }
            if (k >= 0) { used[k] = true; hits++; } else { misses++; }
        }
        var unmatched = used.Count(u => !u);
        var precision = hits + misses == 0 ? 1 : (double)hits / (hits + misses);
        var recall = hits + unmatched == 0 ? 1 : (double)hits / (hits + unmatched);
        return (precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), recall, misses);
    }

    public static float Value(SoundTypeFeatures f, SoundClass c) => c switch
    {
        SoundClass.Kick => f.Kick,
        SoundClass.Snare => f.Snare,
        SoundClass.Hat => f.Hat,
        SoundClass.Bass => f.Bass,
        SoundClass.Vocal => f.Vocal,
        _ => f.Pad,
    };

    public int Count(SoundClass c) => Onsets.GetValueOrDefault(c)?.Count ?? 0;

    /// <summary>The 95th percentile of a class after the first 0.4 s.</summary>
    public float P95(SoundClass c)
    {
        var v = Times.Zip(Reads).Where(x => x.First > 0.4).Select(x => Value(x.Second, c)).Order().ToArray();
        return v.Length == 0 ? 0 : v[(int)((v.Length - 1) * 0.95)];
    }

    /// <summary>Mean inside the class's spans (skipping 0.2 s of each start) and outside them (skipping a 0.6 s release after each end).</summary>
    public (float Inside, float Outside) Spans(SoundClass c)
    {
        var spans = Fixture.Truth.ActiveOf(c);
        List<float> inside = [], outside = [];
        for (var i = 0; i < Times.Count; i++)
        {
            var t = Times[i];
            if (spans.Any(s => t >= s.Lower + 0.2 && t <= s.Upper))
                inside.Add(Value(Reads[i], c));
            else if (t > 0.4 && !spans.Any(s => t >= s.Lower - 0.05 && t <= s.Upper + 0.6))
                outside.Add(Value(Reads[i], c));
        }
        return (Mean(inside), Mean(outside));
    }

    /// <summary>The largest value of a class within 70 ms after each true onset.</summary>
    public float[] HitPeaks(SoundClass c) =>
        [.. Fixture.Truth.OnsetsOf(c).Select(t =>
        {
            var values = Times.Zip(Reads).Where(x => x.First >= t && x.First <= t + 0.07).Select(x => Value(x.Second, c)).ToArray();
            return values.Length == 0 ? 0 : values.Max();
        })];

    /// <summary>The mean of class <paramref name="c"/> from <paramref name="a"/> to <paramref name="b"/> seconds after each true onset of <paramref name="o"/>.</summary>
    public float Mean(SoundClass c, SoundClass o, double a, double b)
    {
        List<float> values = [];
        foreach (var t in Fixture.Truth.OnsetsOf(o))
        {
            for (var i = 0; i < Times.Count; i++)
            {
                if (Times[i] >= t + a && Times[i] <= t + b) values.Add(Value(Reads[i], c));
            }
        }
        return Mean(values);
    }

    public static float Mean(List<float> values)
    {
        if (values.Count == 0) return 0;
        float sum = 0;
        foreach (var v in values) sum += v;
        return sum / values.Count;
    }
}
