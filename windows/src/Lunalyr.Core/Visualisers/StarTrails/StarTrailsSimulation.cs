// Ported from Lunalyr for Mac's Sources/Visualizers/StarTrailsSimulation.swift (Lunalyr's own code; no Verso lines).
//
// StarTrailsSimulation is the CPU half of Star Trails. It owns everything with memory and packs the 205-float4
// StarTrailsState (3,280 bytes) the fragment shader reads:
//   * the two craft: a formation orbit (180° apart, clockwise, 3.55 s, 0.095 × 0.075 H) with seeded wander. They
//     glide: the path, speed and heading are smooth functions of the motion clock, and music shapes them only through
//     `cruise`, a critically damped (three-pole) average of the level over several seconds that sets the cruising speed
//     and the weave's width. Hits never move a craft; they light it instead (below). The heading is the path's
//     analytic tangent, so it never swings on a hit either;
//   * light from hits, each rising from zero when the simulation learns of the onset, never mid-flare: a kick lays a
//     bright packet into the trail being laid and blooms the craft's engines, a snare puts a brief sparkle and rim
//     light on each craft. Kicks and bass attacks also pulse the coloured glow along both trails;
//   * the trail history: space-frame craft positions per substep, drifting to the lower left at 0.97 H/s, resampled
//     to 56 uniform axis samples per trail. Each sample keeps the width and glow it was laid with, so kick packets,
//     sung phrases (pink, warmer while the voice sings) and bass notes (lime) ride away;
//   * the scenery's drift: the camera follows the craft, so the clouds, both star layers and the asteroids drift
//     steadily along −f, against the craft's mean heading f. Parallax: clouds 0.05, dust stars 0.08, bright stars
//     0.12, asteroids 0.14–0.34 H/s, each constant in motion time, so the drift never jerks; each scroll wraps at 256 H;
//   * onset rings per drum type (kick 6, snare 6, hat 8 slots). Onsets closer than the minimum gap merge; slots × gap
//     = flare life, so a lit slot is never reused (no pops);
//   * two seeded epicentres per kick (outside the lyric stage, low in the sky), riding with the bright stars;
//   * envelopes, the 0.5 s calm ramp, the three scenery scrolls, morph angles (2π), twinkle clock (64 s), vocal phase
//     (4π) and ten asteroids;
//   * the sky's colours (StarTrailsSky: the violet original or the album cover's), which crossfade over about 2.5 s
//     when the cover changes.
// One simulation serves every display (StarTrailsSimulations): it is aspect-free, and `Pack` places it on each
// surface. Nothing allocates per frame.

using System.Numerics;
using Lunalyr.Core.Colour;
using static Lunalyr.Core.Visualisers.SwiftMath;

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// The sound types one frame shows, with each onset's start on the simulation's wall clock (null = unknown: now).
/// Envelopes are 0…1; counters only grow (a decrease is a reset).
/// </summary>
/// <remarks>Create one with <c>new StarTrailsSound()</c>: <c>default</c> skips the word defaults.</remarks>
public record struct StarTrailsSound
{
    public double Kick, Snare, Hat;
    public double Bass, Vocal, Pad;
    public uint KickOnsets, SnareOnsets, HatOnsets;
    public double? KickTime, SnareTime, HatTime;
    public double Level;

    /// <summary>
    /// The sung word that began most recently: a serial (0 = none), when it began on the simulation's wall clock (null =
    /// now), how far along its line it sits (0…1) and how strongly it is sung (0…1). Vocal stars light word by word
    /// from it.
    /// </summary>
    public uint WordSerial;
    public double? WordTime;
    public double WordFraction = 0.5, WordStrength = 0.7;

    public StarTrailsSound()
    {
    }

    /// <summary>Sensitivity shaping: identity at 1, <c>1 − (1 − x)^s</c> otherwise.</summary>
    public static double Shaped(float x, double sensitivity)
    {
        var s = Min(3, Max(0.3, double.IsFinite(sensitivity) ? sensitivity : 1));
        return 1 - Math.Pow(1 - Min(1, Max(0, (double)x)), s);
    }

    /// <summary>
    /// What <paramref name="frame"/> shows. The detector's types when it has read them; otherwise the analyser's bands
    /// stand in (kick = beat, hat = trebleBeat, bass = 60–150 Hz, vocal = 1.2–3 kHz less flux, pad = mid).
    /// <paramref name="wallNow"/> is the simulation's wall time of this frame.
    /// </summary>
    public static StarTrailsSound From(in AudioSnapshot frame, double sensitivity, double wallNow)
    {
        var s = new StarTrailsSound();
        ref readonly var f = ref frame.Sound;
        s.Level = Min(1f, Max(0f, frame.Level));
        if (double.IsFinite(f.PresentationTime))
        {
            s.Kick = Shaped(f.Kick, sensitivity); s.Snare = Shaped(f.Snare, sensitivity);
            s.Hat = Shaped(f.Hat, sensitivity); s.Bass = Shaped(f.Bass, sensitivity);
            s.Vocal = Shaped(f.Vocal, sensitivity); s.Pad = Shaped(f.Pad, sensitivity);
            s.KickOnsets = f.KickOnsets; s.SnareOnsets = f.SnareOnsets; s.HatOnsets = f.HatOnsets;
            var presentation = f.PresentationTime;
            double? OnWall(double t) => double.IsFinite(t) ? wallNow - Max(0, presentation - t) : null;
            s.KickTime = OnWall(f.KickTime); s.SnareTime = OnWall(f.SnareTime); s.HatTime = OnWall(f.HatTime);
        }
        else
        {
            s.Kick = Shaped(frame.Beat, sensitivity);
            s.Hat = Shaped(frame.TrebleBeat, sensitivity);
            s.Bass = Shaped(frame.Components[1], sensitivity);
            s.Vocal = Shaped(frame.Components[4] * (1 - Min(1f, Max(0f, frame.Flux))), sensitivity);
            s.Pad = Shaped(frame.Mid, sensitivity);
        }
        return s;
    }
}

/// <summary>splitmix64 → [0, 1): the simulation's only randomness.</summary>
/// <remarks>Mutable value type: keep it in a non-readonly field or local and pass it by <c>ref</c>.</remarks>
public struct StarTrailsRandom
{
    private ulong _state;

    public StarTrailsRandom(ulong seed)
    {
        _state = seed;
    }

    public double Next()
    {
        unchecked
        {
            _state += 0x9E37_79B9_7F4A_7C15;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EB;
            z ^= z >> 31;
            return (double)(z >> 11) / (double)(1UL << 53);
        }
    }

    public double Uniform(double a, double b) => a + (b - a) * Next();

    /// <summary>The generator for item <paramref name="index"/> of stream <paramref name="salt"/>.</summary>
    public static StarTrailsRandom Seeded(int index, int salt) =>
        new(unchecked((ulong)((long)index * 0x9E37_79B1L + (long)salt * 0x85EB_CA77L)) & 0xFFFF_FFFF);
}

/// <summary>Slot indices of StarTrailsState (float4 units), mirrored by the constants at the top of the shader.</summary>
public static class StarTrailsLayout
{
    public const int Hdr0 = 0, Hdr1 = 1, Hdr2 = 2, Env0 = 3, Env1 = 4, Stage = 5, Clock = 6;
    public const int Craft = 7, Onset = 11, Live = 51, Asteroid = 52, Trail = 72;
    public const int TrailSamples = 56, Asteroids = 10;

    /// <summary>184: <see cref="StarTrailsSky.Count"/> colours.</summary>
    public const int Sky = Trail + 2 * TrailSamples;

    /// <summary>195: nine album tints.</summary>
    public const int SkyTints = Sky + StarTrailsSky.Count;

    /// <summary>204: the newest sung word (serial mod 4096, age s, strength, position along its line).</summary>
    public const int Word = SkyTints + StarTrailsSky.TintCount;

    /// <summary>205 float4s.</summary>
    public const int Float4Count = Word + 1;

    /// <summary>3,280 bytes.</summary>
    public const int ByteCount = Float4Count * 16;

    /// <summary>The fragment buffer index the state is bound at on the Mac.</summary>
    public const int BufferIndex = 6;
}

/// <summary>Star Trails' CPU simulation: the craft, their trails, onsets, scenery and sky. See the file header.</summary>
public sealed class StarTrailsSimulation
{
    public enum Flight
    {
        Centre,
        Beside,
    }

    // Motion (on-screen seconds at the default Speed), measured from the reference.
    public const double Axis = 29.5 * Math.PI / 180;
    public const double OrbitPeriod = 3.55, OrbitAlong = 0.095, OrbitCross = 0.075;
    public const double Drift = 0.97;

    /// <summary>The scenery's drift against the flight, H per motion second (far layers slower).</summary>
    public const double CloudDrift = 0.05, DustDrift = 0.08, StarDrift = 0.12;

    public const double TrailS0 = -0.16, TrailS1 = 1.75;
    public const double Substep = 1.0 / 120.0;
    public const double World = 256.0;

    /// <summary>
    /// History keeps a sample at most every 1/240 s of motion, so slow motion (Reduce Motion, a low Speed) does not need
    /// a longer ring.
    /// </summary>
    public const double HistorySpacing = 1.0 / 240.0;

    public const int HistoryCapacity = 768;

    /// <summary>A kick's packet peaks 100 ms after it starts (58 ms rise) and has faded by 0.4 s.</summary>
    public const double PacketPeak = 0.10;

    /// <summary>The snare's sparkle peaks at 70 ms and is gone by 0.3 s.</summary>
    public const double SparklePeak = 0.07;

    /// <summary>An onset ring: slots, life, minimum gap, first slot; slots × gap ≥ life.</summary>
    public readonly record struct Ring(int Slots, double Life, double Gap, int First);

    private static readonly Ring[] RingArray =
        [new(6, 0.90, 0.15, 0), new(6, 0.78, 0.13, 6), new(8, 0.44, 0.055, 12)];

    /// <summary>The kick, snare and hat rings.</summary>
    public static IReadOnlyList<Ring> Rings => RingArray;

    private const int PendingCapacity = 16;

    private static readonly Double2 F = new(Math.Cos(Axis), Math.Sin(Axis));
    private static readonly Double2 N = new(-Math.Sin(Axis), Math.Cos(Axis));

    /// <summary>The sky frame's x axis (across the flight); its y axis is F. Mirrors the shader's.</summary>
    private static readonly Double2 M = new(Math.Sin(Axis), -Math.Cos(Axis));

    private struct Follower(double attack, double release, double v = 0)
    {
        public double Attack = attack, Release = release, V = v;

        public void Step(double target, double dt)
        {
            var tau = target > V ? Attack : Release;
            V += (target - V) * (1 - Math.Exp(-dt / Max(1e-4, tau)));
        }
    }

    /// <summary>
    /// Three equal first-order lags in series: a critically damped smoother whose output and its first two derivatives
    /// stay continuous whatever the input does, so a quantity it shapes (the craft's cruising speed and weave) can
    /// change only slowly and smoothly.
    /// </summary>
    private struct Smooth3(double tau, double value)
    {
        private readonly double _tau = tau;
        private double _a = value, _b = value, _c = value;

        public readonly double Value => _c;

        /// <summary>d value / dt (per wall second).</summary>
        public readonly double Rate => (_b - _c) / _tau;

        public void Step(double target, double dt)
        {
            var k = 1 - Math.Exp(-dt / _tau);
            _a += (target - _a) * k; _b += (_a - _b) * k; _c += (_b - _c) * k;
        }
    }

    private readonly struct Wander
    {
        private readonly Double4 _f, _p, _a;

        public Wander(ulong seed)
        {
            var r = new StarTrailsRandom(seed);
            Span<double> f = stackalloc double[4], p = stackalloc double[4], a = stackalloc double[4];
            for (var i = 0; i < 4; i++) { f[i] = r.Uniform(0.11, 0.37); p[i] = r.Uniform(0, 6.283); a[i] = r.Uniform(0.4, 1.0); }
            _f = new Double4(f[0], f[1], f[2], f[3]);
            _p = new Double4(p[0], p[1], p[2], p[3]);
            var sum = new Double4(a[0], a[1], a[2], a[3]).Sum;
            _a = new Double4(a[0] / sum, a[1] / sum, a[2] / sum, a[3] / sum);
        }

        public double Value(double t)
        {
            var v = 0.0;
            for (var i = 0; i < 4; i++) v += _a[i] * Math.Sin(2 * Math.PI * _f[i] * t + _p[i]);
            return v;
        }

        /// <summary>d/dt of the wander.</summary>
        public double Rate(double t)
        {
            var v = 0.0;
            for (var i = 0; i < 4; i++) v += _a[i] * 2 * Math.PI * _f[i] * Math.Cos(2 * Math.PI * _f[i] * t + _p[i]);
            return v;
        }
    }

    /// <summary>
    /// <c>T</c>: the onset's time on the wall clock (stars ripple from it). <c>Lit</c>: when its light on the craft and
    /// trails starts, the substep the simulation learned of it, so that light always rises from zero. <c>Sky</c>: the
    /// bright stars' scroll then (a kick's epicentres ride with them).
    /// </summary>
    private struct Onset
    {
        public int Serial;
        public double T, Lit, Strength, Pan;
        public Double4 Extra;
        public double Sky;
    }

    private struct Sample
    {
        public double T, X, Y, Glow, Width, Hot;
    }

    /// <summary>A fixed ring of trail samples, oldest to newest.</summary>
    private sealed class History
    {
        private readonly Sample[] _buffer = new Sample[HistoryCapacity];
        private int _start;

        public int Count { get; private set; }

        /// <summary><paramref name="back"/> 0 is the newest sample.</summary>
        public ref readonly Sample Back(int back) => ref _buffer[(_start + Count - 1 - back) % _buffer.Length];

        public ref readonly Sample Oldest => ref _buffer[_start];

        public void Append(in Sample s)
        {
            if (Count == _buffer.Length) { _start = (_start + 1) % _buffer.Length; Count -= 1; }
            _buffer[(_start + Count) % _buffer.Length] = s;
            Count += 1;
        }

        public void ReplaceNewest(in Sample s) => _buffer[(_start + Count - 1) % _buffer.Length] = s;

        public void DropOldest() { _start = (_start + 1) % _buffer.Length; Count -= 1; }

        public void Shift(double dt, Double2 d)
        {
            for (var i = 0; i < Count; i++)
            {
                ref var sample = ref _buffer[(_start + i) % _buffer.Length];
                sample.T += dt; sample.X += d.X; sample.Y += d.Y;
            }
        }
    }

    private struct AsteroidState
    {
        public double X, Y, Vx, Vy, Radius, Rot, Spin, RimGain;
        public double Seed, Blur, Life, Age, Lit;
    }

    private readonly record struct TrailPoint(double S, double C, Sample E);

    // Options and safety state.

    /// <summary>VisualizerResponse.Punch, 0…2.</summary>
    public double Punch { get; set; } = 1;

    /// <summary>Gain of every sound-type star.</summary>
    public double StarLight { get; set; } = 1;

    /// <summary>1 when VisualizerResponse.Calm (Reduce Motion or Dim Flashing Lights).</summary>
    public double CalmTarget { get; set; }

    /// <summary>The lyric stage ellipse: uv centre, uv radii.</summary>
    public Double4 Stage { get; set; } = new(0.5, 0.5, 0.32, 0.24);

    /// <summary>Tests only: the same sky for every case.</summary>
    public double? FreezeSky { get; set; }

    public double Wall { get; private set; }
    public double Motion { get; private set; }
    public double Calm { get; private set; }

    /// <summary>Scenery scrolls along the flight axis, [0, 256) H: bright stars.</summary>
    public double Sky { get; private set; }

    /// <summary>The dust stars' scroll.</summary>
    public double DustSky { get; private set; }

    /// <summary>The clouds' scroll.</summary>
    public double CloudSky { get; private set; }

    private double _thetaA = 1.3, _thetaB = 4.1, _twinkle, _vocalPhase;
    private double _phase = 2 * Math.PI * -0.3 / OrbitPeriod;

    /// <summary>The only musical input to motion: the level, critically damped over several seconds.</summary>
    private Smooth3 _cruise = new(0.8, 0.3);

    private readonly Follower[] _display =
    [
        new(0.03, 0.12), new(0.03, 0.12), new(0.03, 0.12), new(0.12, 0.5), new(0.10, 0.6), new(0.8, 1.6),
    ];

    private Follower _level = new(0.3, 0.8, 0.1);
    private Follower _breath = new(0.6, 1.4);

    /// <summary>Kick bloom on the engines (light only), following the kick packets.</summary>
    private Follower _engine = new(0.04, 0.30);

    /// <summary>The snare's sparkle on the craft (light only), 0…1.</summary>
    private double _sparkle;

    private bool _bassAttackArmed = true;
    private double _bassAttackTime = double.NegativeInfinity;
    private double _bassAttackStrength;
    private double _trailPulse;

    /// <summary>Each craft's heading: the analytic tangent of its path in the space frame.</summary>
    private readonly double[] _headings = new double[2];

    /// <summary>The sky's colours, crossfading to the latest target.</summary>
    private StarTrailsSkyFade _skyColours;

    private readonly Wander[] _wanderAlong;
    private readonly Wander[] _wanderCross;
    private readonly History[] _history = [new(), new()];
    private readonly Onset[][] _onsets = [new Onset[RingArray[0].Slots + 1], new Onset[RingArray[1].Slots + 1], new Onset[RingArray[2].Slots + 1]];
    private readonly int[] _onsetCount = new int[3];
    private readonly int[] _serial = new int[3];
    private readonly uint[] _lastCount = new uint[3];
    private readonly bool[] _hasLastCount = new bool[3];
    private uint _wordSeen;
    private double _wordT = double.NegativeInfinity, _wordFraction = 0.5, _wordStrength;
    private readonly double[][] _pending = [new double[PendingCapacity], new double[PendingCapacity], new double[PendingCapacity]];
    private readonly int[] _pendingCount = new int[3];
    private StarTrailsRandom _asteroidRandom;
    private readonly AsteroidState[] _asteroids = new AsteroidState[StarTrailsLayout.Asteroids];
    private StarTrailsSound _sound = new();

    /// <summary>The youngest age at which a slot of each kind was evicted (tests: ≥ its life).</summary>
    private readonly double[] _youngestEviction = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];

    /// <summary>The youngest age at which a slot of each kind (kick, snare, hat) was evicted; ∞ if none was.</summary>
    public ReadOnlySpan<double> YoungestEviction => _youngestEviction;

    /// <param name="seed">Seeds the wander and the asteroids.</param>
    /// <param name="calm">Starts calm (no ramp).</param>
    /// <param name="sky">The sky to start on; null for <see cref="StarTrailsSky.Original"/>.</param>
    public StarTrailsSimulation(ulong seed = 3, bool calm = false, StarTrailsSky? sky = null)
    {
        _skyColours = new StarTrailsSkyFade(sky ?? StarTrailsSky.Original);
        CalmTarget = calm ? 1 : 0;
        Calm = CalmTarget;
        unchecked
        {
            _wanderAlong = [new Wander(seed * 11 + 1), new Wander(seed * 11 + 3)];
            _wanderCross = [new Wander(seed * 11 + 2), new Wander(seed * 11 + 4)];
            _asteroidRandom = new StarTrailsRandom(seed + 101);
        }
        for (var i = 0; i < StarTrailsLayout.Asteroids; i++) _asteroids[i] = Spawn(prewarm: true);
        // Prewarm 2.2 s of near-silence so the trails reach past the screen corner before intro fades in.
        _sound.Level = 0.04;
        var steps = (int)Round(2.2 / Substep);
        for (var i = 0; i < steps; i++) Step(Substep, Substep);
        Wall = 0;
    }

    // MARK: Advancing

    /// <summary>The sky to show; a change crossfades over about 2.5 s of wall time.</summary>
    public StarTrailsSky SkyTarget
    {
        get => _skyColours.Target;
        set => _skyColours.Aim(value);
    }

    /// <summary>
    /// One rendered frame. <paramref name="wallDelta"/>: real seconds (envelopes, onset ages, flares).
    /// <paramref name="motionDelta"/>: the scene-time step over 0.70 (Speed and Reduce Motion's ×0.15 already applied).
    /// The frame is split into equal substeps of at most 1/120 s, so the state lands exactly on the frame time. Zero for
    /// both changes nothing. Allocation-free.
    /// </summary>
    public void Advance(double wallDelta, double motionDelta, in StarTrailsSound sound)
    {
        _sound = sound;
        CollectOnsets();
        var wallDt = Math.Min(Math.Max(double.IsFinite(wallDelta) ? wallDelta : 0, 0), 0.25);
        var motionDt = Math.Min(Math.Max(double.IsFinite(motionDelta) ? motionDelta : 0, 0), 0.25);
        if (!(wallDt > 0 || motionDt > 0)) return;
        var steps = Math.Max(1, (int)Math.Ceiling(wallDt / Substep - 1e-9));
        for (var i = 0; i < steps; i++) Step(motionDt / steps, wallDt / steps);
        if (Motion > 1e5) Rebase();
    }

    /// <summary>Shifts the motion clock back to 0 after about a day, before double precision matters.</summary>
    private void Rebase()
    {
        var shift = F * (Drift * Motion);
        for (var c = 0; c < 2; c++) _history[c].Shift(-Motion, -shift);
        Motion = 0;
    }

    private double Envelope(int kind) => kind switch
    {
        0 => _sound.Kick,
        1 => _sound.Snare,
        _ => _sound.Hat,
    };

    private void CollectOnsets()
    {
        if (_sound.WordSerial != 0 && _sound.WordSerial != _wordSeen)
        {
            _wordSeen = _sound.WordSerial;
            _wordT = Min(Wall, Max(_sound.WordTime ?? Wall, Wall - 0.25));
            _wordFraction = Min(1, Max(0, _sound.WordFraction));
            _wordStrength = Min(1, Max(0, _sound.WordStrength));
        }
        for (var k = 0; k < 3; k++)
        {
            var (count, time) = k switch
            {
                0 => (_sound.KickOnsets, _sound.KickTime),
                1 => (_sound.SnareOnsets, _sound.SnareTime),
                _ => (_sound.HatOnsets, _sound.HatTime),
            };
            if (!_hasLastCount[k] || count < _lastCount[k])
            {
                // First frame or reset.
                _lastCount[k] = count;
                _hasLastCount[k] = true;
                continue;
            }
            var added = (int)Math.Min(4u, count - _lastCount[k]);
            if (added > 0)
            {
                var onset = time ?? Wall;
                if (onset > Wall + 0.25) onset = Wall;
                for (var i = 0; i < added; i++)
                {
                    if (_pendingCount[k] >= PendingCapacity) continue;
                    _pending[k][_pendingCount[k]++] = Max(onset - (added - 1 - i) * 0.03, Wall - 0.25);
                }
            }
            _lastCount[k] = count;
        }
    }

    /// <summary><paramref name="lit"/>: the start of the substep that accepts it (or the onset itself, if later).</summary>
    private void Accept(int kind, double t, double lit)
    {
        var ring = RingArray[kind];
        var onsets = _onsets[kind];
        if (_onsetCount[kind] > 0 && t - onsets[_onsetCount[kind] - 1].T < ring.Gap) return; // merge into the previous onset
        _serial[kind] = (_serial[kind] + 1) % (1 << 24);
        var rng = StarTrailsRandom.Seeded(_serial[kind], 11 + kind);
        var extra = Double4.Zero;
        if (kind == 0) extra = Epicentres(ref rng);
        else if (kind == 2) extra = new Double4(DustSky, 0, 0, 0); // hats glitter in the dust
        var strength = Min(1, Max(0.3, 0.3 + 0.9 * Envelope(kind)));
        onsets[_onsetCount[kind]++] = new Onset
        {
            Serial = _serial[kind], T = t, Lit = Max(t, lit), Strength = strength, Pan = 0, Extra = extra, Sky = Sky,
        };
        if (_onsetCount[kind] > ring.Slots)
        {
            _youngestEviction[kind] = Min(_youngestEviction[kind], Wall - onsets[0].T);
            Array.Copy(onsets, 1, onsets, 0, _onsetCount[kind] - 1);
            _onsetCount[kind]--;
        }
    }

    /// <summary>
    /// Two kick epicentres (uv on the screen at the onset) outside the lyric stage, low in the sky, 0.45 H apart.
    /// <see cref="Pack(double, Span{Vector4}, Flight)"/> places them in the bright stars' sky frame.
    /// </summary>
    public Double4 Epicentres(ref StarTrailsRandom rng)
    {
        Double2? first = null, second = null;
        var tries = 0;
        var stage = Stage;
        while (second is null && tries < 64)
        {
            tries += 1;
            var u = rng.Uniform(0.04, 0.96);
            var y = 0.06 + 0.80 * Math.Pow(rng.Next(), 1.6);
            var e = new Double2((u - stage.X) / stage.Z, (y - stage.Y) / stage.W);
            if (e.LengthSquared < 1.15 * 1.15) continue;
            if (first is { } firstPoint)
            {
                if (new Double2((u - firstPoint.X) * 16 / 9, y - firstPoint.Y).Length < 0.45) continue;
                second = new Double2(u, y);
            }
            else
            {
                first = new Double2(u, y);
            }
        }
        var a = first ?? new Double2(0.12, 0.2);
        var b = second ?? (first is null ? new Double2(0.88, 0.2) : new Double2(a.X < 0.5 ? 0.88 : 0.12, 0.2));
        return new Double4(a.X, a.Y, b.X, b.Y);
    }

    private AsteroidState Spawn(bool prewarm = false)
    {
        var a = new AsteroidState();
        var depth = Math.Pow(_asteroidRandom.Next(), 1.4);
        var speed = 0.14 + 0.20 * depth;                  // nearer rocks drift faster, all faster than the stars
        var angle = Axis + Math.PI;                       // against the flight, as all the scenery
        var across = _asteroidRandom.Uniform(-0.34, 0.34);
        var ahead = prewarm ? _asteroidRandom.Uniform(-0.85, 0.75) : _asteroidRandom.Uniform(0.35, 0.85);
        a.Radius = 0.007 + 0.016 * depth + _asteroidRandom.Uniform(0, 0.003);
        a.Rot = _asteroidRandom.Uniform(0, 6.283);
        a.Spin = (_asteroidRandom.Next() < 0.5 ? -1.0 : 1.0) * _asteroidRandom.Uniform(0.25, 0.9);
        a.RimGain = _asteroidRandom.Uniform(0.6, 1.0);
        a.Seed = (int)(_asteroidRandom.Next() * 61);
        a.Life = _asteroidRandom.Uniform(6, 11);
        a.Vx = speed * Math.Cos(angle); a.Vy = speed * Math.Sin(angle);
        a.Blur = Smoothstep(0.62, 0.95, depth);
        a.X = F.X * ahead + N.X * across; a.Y = F.Y * ahead + N.Y * across;
        if (prewarm) a.Age = _asteroidRandom.Uniform(0.8, a.Life - 0.8);
        return a;
    }

    private void Step(double dtM, double dtW)
    {
        Motion += dtM;
        Wall += dtW;
        double t = Motion, w = Wall;
        Calm += Min(Max(CalmTarget - Calm, -dtW / 0.5), dtW / 0.5);            // 0.5 s ramp: a toggle never pops
        _skyColours.Step(dtW);
        var transient = Min(Max(Punch, 0), 2) * (1 - Calm);
        for (var k = 0; k < 3; k++)
        {
            // Accept the onsets now due, oldest first; their light starts with this substep.
            var pending = _pending[k];
            while (true)
            {
                var due = -1;
                for (var i = 0; i < _pendingCount[k]; i++)
                {
                    if (pending[i] <= w && (due < 0 || pending[i] < pending[due])) due = i;
                }
                if (due < 0) break;
                var at = pending[due];
                Array.Copy(pending, due + 1, pending, due, _pendingCount[k] - due - 1);
                _pendingCount[k]--;
                Accept(k, at, w - dtW);
            }
            var target = Min(1, Max(0.3, 0.3 + 0.9 * Envelope(k)));
            var onsets = _onsets[k];
            for (var i = 0; i < _onsetCount[k]; i++)
            {
                // Strength: the envelope's peak, rate-limited.
                if (w - onsets[i].T < 0.08) onsets[i].Strength = Min(Max(onsets[i].Strength, target), onsets[i].Strength + dtW / 0.09);
            }
        }
        _display[0].Step(_sound.Kick, dtW); _display[1].Step(_sound.Snare, dtW); _display[2].Step(_sound.Hat, dtW);
        // The tonal bass envelope also follows sustained notes. Compare it with its slower display envelope to catch
        // attacks only.
        var bassRise = _sound.Bass - _display[3].V;
        if (bassRise <= 0.055) _bassAttackArmed = true;
        if (_bassAttackArmed && bassRise >= 0.14 && w - _bassAttackTime >= 0.16)
        {
            _bassAttackArmed = false;
            _bassAttackTime = w - dtW;
            _bassAttackStrength = Min(1, 0.45 + 1.1 * bassRise);
        }
        _display[3].Step(_sound.Bass, dtW); _display[4].Step(_sound.Vocal, dtW); _display[5].Step(_sound.Pad, dtW);
        _level.Step(_sound.Level, dtW);
        _cruise.Step(_sound.Level, dtW);
        var e = _cruise.Value;
        if (CalmTarget > 0.5) { _breath.Attack = 1.2; _breath.Release = 2.0; } else { _breath.Attack = 0.6; _breath.Release = 1.4; }
        _breath.Step(_sound.Pad, dtW);
        _vocalPhase = (_vocalPhase + dtW * 1.6 * _sound.Vocal) % (4 * Math.PI);
        _twinkle = (_twinkle + dtW) % 64;
        var omega = 2 * Math.PI / OrbitPeriod * (0.84 + 0.24 * e);               // orbit rate, per motion second
        _phase = (_phase + omega * dtM) % (2 * Math.PI);
        // The scenery drifts against the flight at constant speeds (the camera follows the craft).
        Sky = (Sky + StarDrift * dtM) % World;
        DustSky = (DustSky + DustDrift * dtM) % World;
        CloudSky = (CloudSky + CloudDrift * dtM) % World;
        _thetaA = (_thetaA + (0.030 + 0.03 * e + 0.02 * _breath.V) * dtM) % (2 * Math.PI);
        _thetaB = (_thetaB + (0.0185 + 0.02 * e) * dtM) % (2 * Math.PI);
        if (FreezeSky is { } frozen) { Sky = frozen; DustSky = frozen; CloudSky = frozen; _thetaA = 1.3; _thetaB = 4.1; }
        // Hits are light, never motion: kick packets in the trails being laid and the engines' bloom, the snare's
        // sparkle. Each rises from zero at its `Lit` time.
        double pulse = 0, flash = 0;
        for (var i = 0; i < _onsetCount[0]; i++)
        {
            ref readonly var o = ref _onsets[0][i];
            pulse = Max(pulse, o.Strength * Bump(w - o.Lit, PacketPeak));
        }
        for (var i = 0; i < _onsetCount[1]; i++)
        {
            ref readonly var o = ref _onsets[1][i];
            flash = Max(flash, o.Strength * Bump(w - o.Lit, SparklePeak));
        }
        // Band-only frames have a kick envelope without an onset counter.
        var kickGlow = _sound.KickOnsets == 0 && _sound.KickTime is null ? _display[0].V : pulse;
        var bassGlow = _bassAttackStrength * Bump(w - _bassAttackTime, 0.085);
        _trailPulse = Max(kickGlow, 0.85 * bassGlow) * transient;
        pulse *= transient;
        _sparkle = Min(1, flash * transient);
        _engine.Step(pulse, dtW);
        var size = 0.88 + 0.18 * e;
        var sizeRate = dtM > 1e-9 ? 0.18 * _cruise.Rate * Min(dtW / dtM, 50) : 0;  // d size / d motion time
        for (var c = 0; c < 2; c++)
        {
            var ph = _phase + (c == 1 ? 0 : Math.PI);                                // craft 0 = pink (voice), 1 = lime (bass)
            double sinPh = Math.Sin(ph), cosPh = Math.Cos(ph);
            var along = OrbitAlong * size * sinPh + 0.004 * _wanderAlong[c].Value(t);
            var cross = OrbitCross * size * cosPh + 0.004 * _wanderCross[c].Value(t);
            var p = F * along + N * cross + F * (Drift * t);                        // space frame, relative to the centre
            if (dtM > 1e-9)
            {
                // The path's tangent (kept while paused).
                var dAlong = OrbitAlong * (sizeRate * sinPh + size * cosPh * omega) + 0.004 * _wanderAlong[c].Rate(t);
                var dCross = OrbitCross * (sizeRate * cosPh - size * sinPh * omega) + 0.004 * _wanderCross[c].Rate(t);
                var v = F * (dAlong + Drift) + N * dCross;
                _headings[c] = Math.Atan2(v.Y, v.X);
            }
            var lead = c == 0 ? _display[4].V : _display[3].V;
            var soft = 1 - 0.5 * Calm;
            var sample = new Sample
            {
                T = t, X = p.X, Y = p.Y, Glow = 1 + 1.2 * pulse + 0.30 * soft * lead + 0.08 * (_level.V - 0.4),
                Width = 1 + 0.35 * soft * lead, Hot = pulse,
            };
            var history = _history[c];
            if (history.Count >= 2 && t - history.Back(1).T < HistorySpacing) history.ReplaceNewest(sample);
            else history.Append(sample);
            while (history.Count > 4 && (t - history.Oldest.T) * Drift > TrailS1 + 0.6) history.DropOldest();
        }
        for (var i = 0; i < _asteroids.Length; i++)
        {
            var a = _asteroids[i];
            a.X += a.Vx * dtM; a.Y += a.Vy * dtM;
            var kick = _display[0].V * (1 - Calm) * a.RimGain;
            a.Rot += a.Spin * (0.85 + 0.5 * e) * dtM;                                // a little livelier when loud, never per hit
            a.Rot %= 2 * Math.PI;
            a.Age += dtM;
            a.Lit += (kick - a.Lit) * (1 - Math.Exp(-dtW / (kick > a.Lit ? 0.09 : 0.35))); // a kick catches the rim (light)
            if (a.Age > a.Life || Math.Abs(a.X) > 1.9 || Math.Abs(a.Y) > 0.62) a = Spawn();
            _asteroids[i] = a;
        }
    }

    // MARK: Shapes

    public static double Smoothstep(double a, double b, double x)
    {
        var t = Min(Max((x - a) / (b - a), 0), 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The light of a hit: zero value and slope at age 0 (it never pops), peak 1 at age = tau, then a smooth fall (16 %
    /// at 3 tau). The 10-90 % rise takes 0.58 tau.
    /// </summary>
    public static double Bump(double age, double tau)
    {
        if (!(double.IsFinite(age) && age > 0)) return 0;
        var x = age / tau;
        return x * x * Math.Exp(2 * (1 - x));
    }

    // MARK: Packing

    private static TrailPoint Point(History h, int back, Double2 offset)
    {
        ref readonly var e = ref h.Back(back);
        var sp = new Double2(e.X, e.Y) - offset;
        return new TrailPoint(-Double2.Dot(sp, F), Double2.Dot(sp, N), e);
    }

    /// <summary>56 samples at uniform axis distance s (cross offset, width, glow, hot); returns the head's s.</summary>
    private double TrailSamples(int c, Span<Vector4> output)
    {
        var offset = F * (Drift * Motion);
        var h = _history[c];
        TrailPoint head = Point(h, 0, offset), next = Point(h, 1, offset);
        var slope = (next.C - head.C) / Max(1e-6, next.S - head.S);
        const double ds = (TrailS1 - TrailS0) / (StarTrailsLayout.TrailSamples - 1);
        var j = 0;
        TrailPoint a = head, b = next;
        for (var i = 0; i < StarTrailsLayout.TrailSamples; i++)
        {
            var s = TrailS0 + i * ds;
            Double4 v;
            if (s <= head.S)
            {
                v = new Double4(head.C + slope * (s - head.S), head.E.Width, head.E.Glow, head.E.Hot);
            }
            else
            {
                while (j + 1 < h.Count && b.S < s)
                {
                    j += 1;
                    a = b;
                    if (j + 1 < h.Count) b = Point(h, j + 1, offset);
                }
                if (j + 1 >= h.Count)
                {
                    v = new Double4(a.C, a.E.Width, a.E.Glow, a.E.Hot);
                }
                else
                {
                    var u = (s - a.S) / Max(1e-9, b.S - a.S);
                    v = new Double4(a.C + (b.C - a.C) * u, a.E.Width + (b.E.Width - a.E.Width) * u,
                                    a.E.Glow + (b.E.Glow - a.E.Glow) * u, a.E.Hot + (b.E.Hot - a.E.Hot) * u);
                }
            }
            output[i] = v.ToVector4();
        }
        return head.S;
    }

    /// <summary>
    /// The shader state for one surface, written to <paramref name="state"/> (at least
    /// <see cref="StarTrailsLayout.Float4Count"/> float4s). Pure and allocation-free.
    /// </summary>
    public void Pack(double aspect, Span<Vector4> state, Flight flight = Flight.Centre)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(state.Length, StarTrailsLayout.Float4Count, nameof(state));
        var st = state[..StarTrailsLayout.Float4Count];
        st.Clear();
        aspect = double.IsFinite(aspect) && aspect > 0 ? aspect : 16.0 / 9;
        var uv = flight == Flight.Centre ? new Double2(0.488, 0.491) : new Double2(0.70, 0.64);
        var centre = new Double2(uv.X * aspect, uv.Y);
        st[StarTrailsLayout.Hdr0] = new Double4(centre.X, centre.Y, Axis, aspect).ToVector4();
        st[StarTrailsLayout.Hdr1] = new Double4(TrailS0, (TrailS1 - TrailS0) / (StarTrailsLayout.TrailSamples - 1), Sky, DustSky).ToVector4();
        var punchGain = 0.55 + 0.45 * Min(Max(Punch, 0), 2);
        st[StarTrailsLayout.Hdr2] = new Double4(_thetaA, _thetaB, Calm, StarLight * (punchGain + (0.7 - punchGain) * Calm)).ToVector4();
        st[StarTrailsLayout.Env0] = new Double4(_trailPulse, _display[1].V, _display[2].V, _display[3].V).ToVector4();
        st[StarTrailsLayout.Env1] = new Double4(_display[4].V, _display[5].V, _level.V, _breath.V).ToVector4();
        st[StarTrailsLayout.Stage] = Stage.ToVector4();
        st[StarTrailsLayout.Clock] = new Double4(_twinkle, _vocalPhase, CloudSky, 1 / Drift).ToVector4();
        var offset = F * (Drift * Motion);
        for (var c = 0; c < 2; c++)
        {
            ref readonly var h = ref _history[c].Back(0);
            var p = centre + new Double2(h.X, h.Y) - offset;
            var ph = _phase + (c == 1 ? 0 : Math.PI);
            var headS = TrailSamples(c, st.Slice(StarTrailsLayout.Trail + c * StarTrailsLayout.TrailSamples, StarTrailsLayout.TrailSamples));
            var lead = c == 0 ? _display[4].V : _display[3].V;
            st[StarTrailsLayout.Craft + 2 * c] = new Double4(p.X, p.Y, _headings[c], 0.55 * Math.Sin(ph)).ToVector4();
            // Engine glow (the trail's own sound swells it), head s, snare sparkle, kick bloom.
            st[StarTrailsLayout.Craft + 2 * c + 1] = new Double4(0.55 + 0.27 * lead, headS, _sparkle, _engine.V).ToVector4();
        }
        Span<float> live = stackalloc float[3];
        for (var k = 0; k < 3; k++)
        {
            var ring = RingArray[k];
            var lit = 0;
            for (var slot = 0; slot < ring.Slots; slot++)
            {
                var index = StarTrailsLayout.Onset + 2 * (ring.First + slot);
                if (slot < _onsetCount[k])
                {
                    ref readonly var o = ref _onsets[k][slot];
                    var age = Wall - o.T;
                    if (age >= 0 && age <= ring.Life) lit += 1;
                    st[index] = new Double4(o.Serial, age, o.Strength, o.Pan).ToVector4();
                    if (k == 0)
                    {
                        // The epicentres in the bright stars' sky frame (x across the flight, y along it plus their
                        // scroll at the onset), for this surface's aspect.
                        var a = OnSky(o.Extra.X, o.Extra.Y, aspect, o.Sky);
                        var b = OnSky(o.Extra.Z, o.Extra.W, aspect, o.Sky);
                        st[index + 1] = new Double4(a.X, a.Y, b.X, b.Y).ToVector4();
                    }
                    else
                    {
                        st[index + 1] = o.Extra.ToVector4();
                    }
                }
                else
                {
                    st[index] = new Vector4(0, -1, 0, 0);
                }
            }
            live[k] = lit;
        }
        st[StarTrailsLayout.Live] = new Vector4(live[0], live[1], live[2], StarTrailsLayout.Asteroids);
        st[StarTrailsLayout.Word] = new Vector4(_wordSeen % 4096, (float)Min(Wall - _wordT, 30), (float)_wordStrength, (float)_wordFraction);
        _skyColours.Write(st.Slice(StarTrailsLayout.Sky, StarTrailsSky.Count + StarTrailsSky.TintCount));
        for (var i = 0; i < _asteroids.Length; i++)
        {
            ref readonly var a = ref _asteroids[i];
            var fade = Smoothstep(0, 0.8, a.Age) * (1 - Smoothstep(a.Life - 0.8, a.Life, a.Age));
            var rot = a.Rot - 2 * Math.PI * Math.Floor(a.Rot / (2 * Math.PI));
            st[StarTrailsLayout.Asteroid + 2 * i] = new Double4(centre.X + a.X, centre.Y + a.Y, a.Radius, rot).ToVector4();
            st[StarTrailsLayout.Asteroid + 2 * i + 1] = new Double4(a.Seed, a.Blur, fade, a.Lit).ToVector4();
        }
    }

    private static Double2 OnSky(double u, double y, double aspect, double sky)
    {
        var p = new Double2(u * aspect, y);
        return new Double2(Double2.Dot(p, M), (Double2.Dot(p, F) + sky) % World);
    }

    /// <summary>The state as a new array (tests and the static render path).</summary>
    public Vector4[] Pack(double aspect, Flight flight = Flight.Centre)
    {
        var state = new Vector4[StarTrailsLayout.Float4Count];
        Pack(aspect, state, flight);
        return state;
    }

    /// <summary>
    /// A state for a static frame (tests and stills): a fresh simulation advanced through <paramref name="time"/>
    /// seconds (at most 24) at 30 fps with <paramref name="frame"/>'s sound held constant, under the sky of
    /// <paramref name="palette"/> (null: the original). Allocates, as it builds a simulation.
    /// </summary>
    public static Vector4[] Snapshot(in AudioSnapshot frame, double time, double sensitivity, bool calm, double aspect,
                                     AlbumWallpaperPalette? palette = null)
    {
        var simulation = new StarTrailsSimulation(calm: calm, sky: palette is null ? StarTrailsSky.Original : StarTrailsSky.Album(palette));
        var frames = (int)Round(Min(24, Max(0, double.IsFinite(time) ? time : 0)) * 30);
        for (var i = 0; i < frames; i++)
        {
            var sound = StarTrailsSound.From(frame, sensitivity, (i + 1) / 30.0);
            simulation.Advance(1.0 / 30, 1.0 / 30, sound);
        }
        return simulation.Pack(aspect);
    }
}

/// <summary>
/// One Star Trails simulation shared by every surface on a set of timelines (each display, and the copy that fades out
/// in a scene change). It advances once per new scene time; another display drawing the same frame only packs it for
/// its own aspect. Use from the render thread only.
/// </summary>
public sealed class StarTrailsSimulations
{
    /// <summary>The wallpaper's flow clock runs at this fraction of wall time × Speed (the Mac's VisualClock rate).</summary>
    public const double VisualizerRate = 0.70;

    private double _lastSceneTime, _lastClock;
    private AlbumWallpaperPalette? _palette;
    private StarTrailsSky _sky = StarTrailsSky.Original;

    public StarTrailsSimulation? Simulation { get; private set; }

    /// <summary>
    /// Advances to <paramref name="sceneTime"/> (the shared timeline's value; the wallpaper's flow clock runs at 0.70 ×
    /// wall × Speed) at <paramref name="clock"/> seconds, and returns the simulation. <paramref name="palette"/>: the
    /// cover whose colours the sky takes (null: the original violet sky). Allocation-free except on the first call and
    /// when the cover changes.
    /// </summary>
    public StarTrailsSimulation Advance(double sceneTime, double clock, in AudioSnapshot frame, VisualizerResponse response,
                                        double sensitivity, AlbumWallpaperPalette? palette = null,
                                        StarTrailsLyricWord? word = null)
    {
        if (!Equals(palette, _palette))
        {
            _palette = palette;
            _sky = palette is null ? StarTrailsSky.Original : StarTrailsSky.Album(palette);
        }
        if (Simulation is { } existing) existing.SkyTarget = _sky;
        if (Simulation is not { } simulation)
        {
            var made = new StarTrailsSimulation(calm: response.Calm, sky: _sky)
            {
                Punch = VisualizerPreferences.ClampedPunch(response.Punch),
            };
            Simulation = made;
            _lastSceneTime = sceneTime; _lastClock = clock;
            made.Advance(0, 0, Sound(frame, sensitivity, word, made.Wall));
            return made;
        }
        if (sceneTime == _lastSceneTime) return simulation;
        var wallDelta = Math.Min(0.25, Math.Max(0, clock - _lastClock));
        // A rebase of the scene clock (it went back) is not motion.
        var motionDelta = Math.Max(0, sceneTime - _lastSceneTime) / VisualizerRate;
        _lastSceneTime = sceneTime; _lastClock = clock;
        simulation.CalmTarget = response.Calm ? 1 : 0;
        simulation.Punch = VisualizerPreferences.ClampedPunch(response.Punch);
        simulation.Advance(wallDelta, motionDelta, Sound(frame, sensitivity, word, simulation.Wall + wallDelta));
        return simulation;
    }

    private static StarTrailsSound Sound(in AudioSnapshot frame, double sensitivity, StarTrailsLyricWord? word, double wallNow)
    {
        var sound = StarTrailsSound.From(frame, sensitivity, wallNow);
        if (word is { } sung)
        {
            sound.WordSerial = sung.Serial; sound.WordTime = wallNow - sung.Age;
            sound.WordFraction = sung.Fraction; sound.WordStrength = sung.Strength;
        }
        return sound;
    }
}
