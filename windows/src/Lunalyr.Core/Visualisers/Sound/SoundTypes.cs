using System.Collections;
using System.Runtime.CompilerServices;

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// One sound-type onset: when it started (capture clock, backdated to the estimated start of the sound) and how
/// hard, 0...1. <c>new SoundOnset()</c> is "no onset" (time −∞); <c>default(SoundOnset)</c> is not.
/// </summary>
public record struct SoundOnset
{
    public double Time = double.NegativeInfinity;
    public float Strength;

    public SoundOnset()
    {
    }

    public SoundOnset(double time, float strength)
    {
        Time = time;
        Strength = strength;
    }
}

[InlineArray(SoundOnsetRing.Capacity)]
internal struct SoundOnsetSlots
{
    private SoundOnset _element;
}

/// <summary>
/// The newest onsets of one class, newest first, that had started by the presentation time. Scenes that keep more
/// than one flare alive (one slot per recent hit) read this rather than watching the counter, which can move twice
/// between two frames.
/// </summary>
/// <remarks>
/// A fixed-size value stored inline (no heap storage), so building or copying a snapshot never allocates. Index it
/// (<c>ring[0]</c> is the newest) or iterate it; there are always <see cref="Capacity"/> entries, and unused ones
/// have time −∞ and strength 0. Create one with <c>new SoundOnsetRing()</c>: <c>default</c> skips the constructor and
/// leaves eight onsets at time 0.
/// </remarks>
public struct SoundOnsetRing : IEquatable<SoundOnsetRing>, IReadOnlyList<SoundOnset>
{
    public const int Capacity = 8;

    private SoundOnsetSlots _slots;

    public SoundOnsetRing()
    {
        for (var i = 0; i < Capacity; i++) _slots[i] = new SoundOnset();
    }

    /// <summary>Always <see cref="Capacity"/>.</summary>
    public readonly int Count => Capacity;

    public SoundOnset this[int index]
    {
        readonly get
        {
            if ((uint)index >= Capacity) throw new ArgumentOutOfRangeException(nameof(index));
            return _slots[index];
        }
        set
        {
            if ((uint)index >= Capacity) throw new ArgumentOutOfRangeException(nameof(index));
            _slots[index] = value;
        }
    }

    public readonly bool Equals(SoundOnsetRing other)
    {
        for (var i = 0; i < Capacity; i++)
        {
            if (!_slots[i].Equals(other._slots[i])) return false;
        }
        return true;
    }

    public override readonly bool Equals(object? obj) => obj is SoundOnsetRing other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        for (var i = 0; i < Capacity; i++) hash.Add(_slots[i]);
        return hash.ToHashCode();
    }

    public static bool operator ==(SoundOnsetRing a, SoundOnsetRing b) => a.Equals(b);

    public static bool operator !=(SoundOnsetRing a, SoundOnsetRing b) => !a.Equals(b);

    public readonly Enumerator GetEnumerator() => new(this);

    readonly IEnumerator<SoundOnset> IEnumerable<SoundOnset>.GetEnumerator() => GetEnumerator();

    readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override readonly string ToString()
    {
        var parts = new string[Capacity];
        for (var i = 0; i < Capacity; i++) parts[i] = $"({_slots[i].Time}, {_slots[i].Strength})";
        return "[" + string.Join(", ", parts) + "]";
    }

    /// <summary>Iterates the entries newest first, without allocating.</summary>
    public struct Enumerator : IEnumerator<SoundOnset>
    {
        private readonly SoundOnsetRing _ring;
        private int _index;

        internal Enumerator(SoundOnsetRing ring)
        {
            _ring = ring;
            _index = -1;
        }

        public readonly SoundOnset Current => _ring[_index];

        readonly object IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < Capacity;

        public void Reset() => _index = -1;

        public readonly void Dispose()
        {
        }
    }
}

/// <summary>
/// What type of sound is playing at one presentation time (see <see cref="SoundTypeDetector"/>). Classes are not
/// exclusive: a kick and a snare on the same beat both fire, and a voice over a pad lights both.
/// </summary>
/// <remarks>Create one with <c>new SoundTypeFeatures()</c>; <c>default</c> leaves its times at 0 instead of −∞.</remarks>
public record struct SoundTypeFeatures
{
    /// <summary>
    /// Transient envelopes, 0...1, with the normal shapes (attack 8-10 ms, release 0.15-0.26 s), not scaled by Punch.
    /// The uniforms re-read the rings with the calm shapes when Reduce Motion or Dim Flashing Lights is on.
    /// </summary>
    public float Kick;
    public float Snare;
    public float Hat;
    /// <summary>Sustained envelopes, 0...1: the bass line (kick tails held out), the lead voice or melodic lead, and the harmonic bed.</summary>
    public float Bass;
    public float Vocal;
    public float Pad;
    /// <summary>
    /// Onsets counted since the source started. They never decrease, also not through silence or when the display
    /// delay grows; only a new source resets them. They can step by more than one between two frames.
    /// </summary>
    public uint KickOnsets;
    public uint SnareOnsets;
    public uint HatOnsets;
    /// <summary>Start of the newest onset at or before <see cref="PresentationTime"/> (capture clock), −∞ if none.</summary>
    public double KickTime = double.NegativeInfinity;
    public double SnareTime = double.NegativeInfinity;
    public double HatTime = double.NegativeInfinity;
    public SoundOnsetRing KickRing = new();
    public SoundOnsetRing SnareRing = new();
    public SoundOnsetRing HatRing = new();
    /// <summary>The display time these values were evaluated at, on the capture clock; −∞ when they were not read from audio.</summary>
    public double PresentationTime = double.NegativeInfinity;

    public SoundTypeFeatures()
    {
    }
}

/// <summary>
/// strength × (1 − e^(−age/attack)) × e^(−age/release), scaled so that a strength-1 onset peaks at exactly 1. A
/// function of the age alone, so it reads the same at any frame rate.
/// </summary>
public readonly record struct SoundOnsetShape
{
    public double Attack { get; }
    public double Release { get; }
    private readonly double _norm;

    public SoundOnsetShape(double attack, double release)
    {
        Attack = attack;
        Release = release;
        var peakAge = attack * Math.Log((attack + release) / attack);
        _norm = 1 / ((1 - Math.Exp(-peakAge / attack)) * Math.Exp(-peakAge / release));
    }

    public double Value(double age) => age < 0 ? 0 : (1 - Math.Exp(-age / Attack)) * Math.Exp(-age / Release) * _norm;

    /// <summary>
    /// The largest flare among the ring's onsets that had started by <paramref name="time"/>, clamped to 1. Taking the
    /// maximum rather than the sum keeps dense hats from saturating.
    /// </summary>
    public float Envelope(in SoundOnsetRing ring, double time)
    {
        var envelope = 0.0;
        for (var i = 0; i < SoundOnsetRing.Capacity; i++)
        {
            var onset = ring[i];
            if (onset.Strength > 0 && onset.Time <= time)
                envelope = SwiftMath.Max(envelope, onset.Strength * Value(time - onset.Time));
        }
        return (float)SwiftMath.Min(1, envelope);
    }
}

/// <summary>The flare shapes the transient classes are read with.</summary>
public readonly record struct SoundTypeShapes(SoundOnsetShape Kick, SoundOnsetShape Snare, SoundOnsetShape Hat)
{
    /// <summary>The detector's own envelopes: attack 8-10 ms, release 0.15-0.26 s.</summary>
    public static readonly SoundTypeShapes Normal = From(new SoundTypeDetector.Tuning());

    /// <summary>
    /// Reduce Motion and Dim Flashing Lights: slow, soft flares (attack 120 ms, release 0.45-0.6 s), which the
    /// uniforms also halve.
    /// </summary>
    public static readonly SoundTypeShapes Calm = new(new SoundOnsetShape(0.12, 0.6), new SoundOnsetShape(0.12, 0.55),
        new SoundOnsetShape(0.12, 0.45));

    public static SoundTypeShapes From(SoundTypeDetector.Tuning tuning) => new(
        new SoundOnsetShape(tuning.KickAttack, tuning.KickRelease),
        new SoundOnsetShape(tuning.SnareAttack, tuning.SnareRelease),
        new SoundOnsetShape(tuning.HatAttack, tuning.HatRelease));
}

/// <summary>
/// The detector's recent output on the capture clock, and the stateless read-time evaluation the render loop uses.
/// Every value read is a function of the stored onsets, the stored passes and the display time alone, so 30, 60 and
/// 120 fps read identical values at the same instant, and a display that lags capture (Bluetooth output) sees an
/// onset only once it is heard.
/// </summary>
/// <remarks>
/// Recording mutates fixed-size storage in place and never allocates, so the analysis worker can do it; neither
/// does a read. Not thread-safe: <see cref="SoundTypeFeed"/> guards it with a lock.
/// </remarks>
public sealed class SoundTypeHistory
{
    public const int OnsetCapacity = 16;
    /// <summary>About 0.35-0.7 s of passes: enough for the longest visual delay.</summary>
    public const int PassCapacity = 32;
    /// <summary>After this long without a pass, sustained values fade with <see cref="IdleFade"/>, as AudioAnalyzer fades its bands when capture stops.</summary>
    public const double IdleAfter = 0.25;
    public const double IdleFade = 0.2;

    private readonly OnsetLog _kick = new(), _snare = new(), _hat = new();
    private readonly double[] _passTime = new double[PassCapacity];
    private readonly float[] _passValues = new float[PassCapacity * 3];
    private int _passHead;
    private int _passCount;

    public SoundTypeHistory() => Clear();

    /// <summary>Forgets everything, as a new history would.</summary>
    public void Clear()
    {
        _kick.Clear(); _snare.Clear(); _hat.Clear();
        Array.Fill(_passTime, double.NegativeInfinity);
        Array.Clear(_passValues);
        _passHead = 0;
        _passCount = 0;
    }

    /// <summary>Capture time of the newest recorded pass, −∞ before the first.</summary>
    public double NewestPassTime => _passCount > 0 ? _passTime[_passHead] : double.NegativeInfinity;

    /// <summary>Records one detector pass captured at <paramref name="time"/>.</summary>
    public void Record(in SoundTypeDetector.Output output, double time)
    {
        _kick.Record(output.KickOnsets, output.NewestKick);
        _snare.Record(output.SnareOnsets, output.NewestSnare);
        _hat.Record(output.HatOnsets, output.NewestHat);
        // Keep pass times ordered even if the capture clock jitters.
        var stamp = _passCount > 0 ? SwiftMath.Max(time, _passTime[_passHead]) : time;
        _passHead = _passCount > 0 ? (_passHead + 1) % PassCapacity : 0;
        _passTime[_passHead] = stamp;
        _passValues[_passHead * 3] = output.Bass;
        _passValues[_passHead * 3 + 1] = output.Vocal;
        _passValues[_passHead * 3 + 2] = output.Pad;
        _passCount = Math.Min(_passCount + 1, PassCapacity);
    }

    /// <summary>
    /// Every feature as shown at <paramref name="time"/>: transients from the onsets with the normal shapes, sustained
    /// values interpolated between the passes around <paramref name="time"/>, counters and onset times as of it.
    /// </summary>
    public SoundTypeFeatures Read(double time)
    {
        var f = new SoundTypeFeatures { PresentationTime = time };
        var shapes = SoundTypeShapes.Normal;
        (f.KickOnsets, f.KickTime, f.Kick) = _kick.Read(time, shapes.Kick, ref f.KickRing);
        (f.SnareOnsets, f.SnareTime, f.Snare) = _snare.Read(time, shapes.Snare, ref f.SnareRing);
        (f.HatOnsets, f.HatTime, f.Hat) = _hat.Read(time, shapes.Hat, ref f.HatRing);
        if (_passCount <= 0) return f;
        // The newest pass captured by `time`, and the one after it.
        int older = -1, newer = -1, back = 0;
        while (back < _passCount)
        {
            var slot = (_passHead - back + PassCapacity) % PassCapacity;
            if (_passTime[slot] <= time) { older = slot; break; }
            newer = slot;
            back++;
        }
        float weight = 0;
        if (older < 0)
        {
            older = newer;
        }
        else if (newer >= 0 && _passTime[newer] > _passTime[older])
        {
            weight = (float)SwiftMath.Min(1, SwiftMath.Max(0, (time - _passTime[older]) / (_passTime[newer] - _passTime[older])));
        }
        if (newer < 0) newer = older;
        var idle = time - NewestPassTime - IdleAfter;
        var fade = idle > 0 ? (float)Math.Exp(-idle / IdleFade) : 1;
        f.Bass = Value(0) * fade;
        f.Vocal = Value(1) * fade;
        f.Pad = Value(2) * fade;
        return f;

        float Value(int index)
        {
            float a = _passValues[older * 3 + index], b = _passValues[newer * 3 + index];
            return a + (b - a) * weight;
        }
    }

    /// <summary>The newest onsets of one transient class, oldest overwritten first.</summary>
    private sealed class OnsetLog
    {
        private readonly double[] _times = new double[OnsetCapacity];
        private readonly float[] _strengths = new float[OnsetCapacity];
        /// <summary>Onsets ever recorded (wrapping), matching the detector's counter.</summary>
        private uint _total;
        private int _head;
        private int _stored;

        public OnsetLog() => Clear();

        public void Clear()
        {
            Array.Fill(_times, double.NegativeInfinity);
            Array.Clear(_strengths);
            _total = 0;
            _head = 0;
            _stored = 0;
        }

        public void Record(uint count, SoundOnset newest)
        {
            if (count != _total)
            {
                // Never step back in time, so the newest stored onset stays the newest one shown.
                var time = _stored > 0 ? SwiftMath.Max(newest.Time, _times[_head]) : newest.Time;
                _head = _stored > 0 ? (_head + 1) % OnsetCapacity : 0;
                _times[_head] = time;
                _strengths[_head] = newest.Strength;
                _stored = Math.Min(_stored + 1, OnsetCapacity);
                _total = count;
            }
            else if (_stored > 0)
            {
                // The detector's top-up: the newest onset may still grow.
                _strengths[_head] = SwiftMath.Max(_strengths[_head], newest.Strength);
            }
        }

        /// <summary>
        /// Onsets known by <paramref name="time"/> (the display may lag capture), the newest of them, and the envelope
        /// they make. The ring gets the newest of them in order, so its entry k is the k-th newest onset counted.
        /// </summary>
        public (uint Count, double Time, float Envelope) Read(double time, SoundOnsetShape shape, ref SoundOnsetRing ring)
        {
            var count = _total;
            var newest = double.NegativeInfinity;
            var envelope = 0.0;
            var filled = 0;
            var back = 0;
            while (back < _stored)
            {
                var slot = (_head - back + OnsetCapacity) % OnsetCapacity;
                var onset = _times[slot];
                back++;
                if (onset > time) { count = unchecked(count - 1); continue; }
                if (filled == 0) newest = onset;
                if (filled < SoundOnsetRing.Capacity)
                {
                    ring[filled] = new SoundOnset(onset, _strengths[slot]);
                    filled++;
                }
                envelope = SwiftMath.Max(envelope, _strengths[slot] * shape.Value(time - onset));
            }
            return (count, newest, (float)SwiftMath.Min(1, envelope));
        }
    }
}

/// <summary>
/// What one surface's scene is shown of the sound types. The visualiser swaps in an ambient spectrum while the input
/// is too quiet to show (its idle scene), and the sound types follow that switch instead of cutting.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The sustained classes fade out, and back in, over <see cref="Ramp"/> seconds of wall-clock time from the
/// frame the scene switched: a function of the elapsed time, so every frame rate fades alike.</item>
/// <item>An onset that starts while the scene is idle is hidden: it never enters the rings, its flare never shows and
/// the counters hold. When the music returns they carry on from where they held, so every step of a counter is an
/// onset this scene was shown. The onset that brought the music back (up to <see cref="Lead"/> earlier) still shows.
/// Whether an onset is hidden depends on its start, not on the frame that first saw it.</item>
/// <item>Onsets shown before the scene went idle finish their flares.</item>
/// </list>
/// The counters are therefore this surface's own. They never decrease, and restart only with the source
/// (<see cref="VisualizerAudio.Reset"/>), which this follows. Transient envelopes are re-read from the filtered rings
/// with the normal shapes. A reference type (the Mac's is a struct) so it cannot be used uninitialised; one per
/// surface, used from one thread.
/// </remarks>
public sealed class SoundTypeIdleGate
{
    /// <summary>How long the sustained classes take to fade out or in, in seconds.</summary>
    public const double Ramp = 0.25;
    /// <summary>An onset this long before the music returns still shows, in seconds.</summary>
    public const double Lead = 0.1;

    private float _presence = 1;
    private double? _lastClock;
    private bool _live = true;
    /// <summary>
    /// Onsets starting in [hiddenFrom, hiddenUntil) (display times) are hidden: from the frame the scene went idle
    /// until <see cref="Lead"/> before the frame it came back, open-ended while it is idle.
    /// </summary>
    private double _hiddenFrom = double.PositiveInfinity;
    private double _hiddenUntil = double.PositiveInfinity;
    private Hidden _kick = new(), _snare = new(), _hat = new();

    /// <summary>How much of the sustained classes shows, 0...1.</summary>
    public float SustainedPresence => _presence;

    /// <summary>
    /// The sound types <paramref name="sound"/> as this surface shows them at wall-clock time <paramref name="clock"/>
    /// (seconds, any monotonic clock), <paramref name="live"/> being whether the scene shows the live audio rather than
    /// its idle stand-in.
    /// </summary>
    public SoundTypeFeatures Frame(in SoundTypeFeatures sound, bool live, double clock)
    {
        // The time since the last frame passed in the state that frame had; a switch takes effect from this frame on.
        if (_lastClock is { } lastClock)
        {
            var step = (float)(SwiftMath.Max(0, clock - lastClock) / Ramp);
            _presence = _live ? SwiftMath.Min(1, _presence + step) : SwiftMath.Max(0, _presence - step);
        }
        else
        {
            _presence = live ? 1 : 0;
        }
        _lastClock = clock;
        var time = sound.PresentationTime;
        var returning = live && !_live;
        if (!live && _live)
        {
            _hiddenFrom = time;
            _hiddenUntil = double.PositiveInfinity;
        }
        else if (returning)
        {
            _hiddenUntil = time - Lead;
        }
        _live = live;
        var shown = sound;
        (shown.KickOnsets, shown.KickTime, shown.KickRing) =
            _kick.Show(sound.KickOnsets, sound.KickRing, _hiddenFrom, _hiddenUntil, live, returning);
        (shown.SnareOnsets, shown.SnareTime, shown.SnareRing) =
            _snare.Show(sound.SnareOnsets, sound.SnareRing, _hiddenFrom, _hiddenUntil, live, returning);
        (shown.HatOnsets, shown.HatTime, shown.HatRing) =
            _hat.Show(sound.HatOnsets, sound.HatRing, _hiddenFrom, _hiddenUntil, live, returning);
        var shapes = SoundTypeShapes.Normal;
        shown.Kick = shapes.Kick.Envelope(shown.KickRing, time);
        shown.Snare = shapes.Snare.Envelope(shown.SnareRing, time);
        shown.Hat = shapes.Hat.Envelope(shown.HatRing, time);
        shown.Bass = sound.Bass * _presence;
        shown.Vocal = sound.Vocal * _presence;
        shown.Pad = sound.Pad * _presence;
        return shown;
    }

    /// <summary>
    /// The hidden onsets of one class. The live ring's entry k is the k-th newest onset counted
    /// (<see cref="SoundTypeHistory.Read"/>), and bit k of <c>Mask</c> says whether that onset is hidden.
    /// </summary>
    private struct Hidden
    {
        private bool _started;
        private uint _mask;
        /// <summary>The live count the mask refers to.</summary>
        private uint _counted;
        /// <summary>Onsets hidden since the source started.</summary>
        private uint _total;
        /// <summary>The newest onset shown.</summary>
        private double _newest;

        public Hidden()
        {
            _newest = double.NegativeInfinity;
        }

        public (uint Count, double Time, SoundOnsetRing Ring) Show(uint count, in SoundOnsetRing ring, double hiddenFrom,
            double hiddenUntil, bool live, bool returning)
        {
            unchecked
            {
                // The first frame adopts what is there; a count that went back means a new source, whose counters
                // restarted from zero.
                if (!_started || count - _counted > uint.MaxValue / 2)
                {
                    this = new Hidden { _started = true, _counted = count };
                }
                var added = count - _counted;
                _counted = count;
                _mask = added < 32 ? _mask << (int)added : 0;
                // The onsets counted now are the ring's first `added` entries: each is hidden if it started inside the
                // window. Older ones beyond the ring (after a long pause) are hidden while the scene is idle.
                var inRing = (int)Math.Min(added, (uint)SoundOnsetRing.Capacity);
                for (var k = 0; k < inRing; k++)
                {
                    var start = ring[k].Time;
                    if (start >= hiddenFrom && start < hiddenUntil)
                    {
                        _mask |= 1u << k;
                        _total += 1;
                    }
                }
                if (!live) _total += added - (uint)inRing;
                var filled = 0;
                var shown = new SoundOnsetRing();
                for (var k = 0; k < SoundOnsetRing.Capacity && ring[k].Time > double.NegativeInfinity; k++)
                {
                    var bit = 1u << k;
                    if (returning && (_mask & bit) != 0 && ring[k].Time >= hiddenUntil)
                    {
                        // The hit that brought the music back.
                        _mask &= ~bit;
                        _total -= 1;
                    }
                    if ((_mask & bit) == 0)
                    {
                        shown[filled] = ring[k];
                        filled++;
                    }
                }
                if (filled > 0) _newest = SwiftMath.Max(_newest, shown[0].Time);
                return (count - _total, _newest, shown);
            }
        }
    }
}
