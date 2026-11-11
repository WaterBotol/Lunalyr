using static Lunalyr.Core.Motion.MotionMath;

namespace Lunalyr.Core.Motion;

/// <summary>A damped spring: natural angular frequency (rad/s) and damping ratio.</summary>
public readonly record struct Spring(double Omega, double Zeta);

/// <summary>A word's pose. X and Y are measured in multiples of the lyric type size (so the same at any display resolution); angle in degrees.</summary>
public readonly record struct WordPose(double X, double Y, double Scale, double Angle, double Opacity, double Blur)
{
    /// <summary>Exact rest: no offset, scale 1, fully opaque, no blur.</summary>
    public static WordPose Rest => new(0, 0, 1, 0, 1, 0);
}

/// <summary>Everything one timed word does at one moment (see <see cref="LyricMotion.ForWord"/>).</summary>
/// <param name="Reveal">0…1, monotone: opacity, blur and masks.</param>
/// <param name="Enter">Spring arrival for travel, scale and turn; may overshoot.</param>
/// <param name="Heat">Vocal emphasis: rises into the word, releases after it (0…1.15).</param>
/// <param name="Leave">0…1, this word's share of the line exit.</param>
/// <param name="X">Vocal gesture offset while the word is sung.</param>
/// <param name="Y">Vocal gesture offset while the word is sung.</param>
/// <param name="Scale">Vocal gesture scale (1 at rest).</param>
/// <param name="Angle">Vocal gesture turn, degrees.</param>
public readonly record struct WordMotion(
    double Reveal, double Enter, double Heat, double Leave, double X, double Y, double Scale, double Angle)
{
    public static WordMotion AtRest => new(0, 0, 0, 0, 0, 0, 1, 0);
}

/// <summary>
/// Organic lyric motion (the Mac's <c>LyricMotion.c</c>). Every function is either a pure function of time
/// (seek-safe and identical at any frame rate) or an exactly integrated filter whose result does not depend on how
/// often it is sampled. All of it is allocation-free: it runs every frame for every word.
/// </summary>
public static class LyricMotion
{
    // ---- Seeded variation ----------------------------------------------------------------------------------------

    /// <summary>Seeded value in [-1, 1) for per-word variation; <paramref name="channel"/> picks an independent stream.</summary>
    public static double Jitter(ulong seed, int channel) =>
        Lattice(Mix64(unchecked(seed + 0x632BE59BD9B4E019UL)), channel);

    /// <summary>Smooth seeded gradient noise in [-1, 1], continuous in value and slope; 0 on whole numbers.</summary>
    public static double Noise(ulong seed, double t)
    {
        if (!double.IsFinite(t) || Math.Abs(t) > 4.0e15) return 0;
        var whole = Math.Floor(t);
        var f = t - whole;
        var i = (long)whole;
        double g0 = Lattice(seed, i), g1 = Lattice(seed, unchecked(i + 1));
        var u = f * f * f * (f * (f * 6 - 15) + 10);
        // Gradient noise peaks at 0.5 for unit gradients.
        return Clamp(2 * ((1 - u) * g0 * f + u * g1 * (f - 1)), -1, 1);
    }

    /// <summary>Two octaves of <see cref="Noise"/>: less regular, still smooth.</summary>
    public static double Noise2(ulong seed, double t) =>
        Clamp((Noise(seed, t) + .5 * Noise(Mix64(seed), t * 2.07 + 19.31)) / 1.5, -1, 1);

    // ---- Springs -------------------------------------------------------------------------------------------------

    private static Spring Sane(Spring s)
    {
        double omega = s.Omega, zeta = s.Zeta;
        if (!double.IsFinite(omega) || omega <= 0) omega = 12;
        if (!double.IsFinite(zeta) || zeta <= 0) zeta = 1;
        return new Spring(Clamp(omega, .5, 200), Clamp(zeta, .05, 4));
    }

    // After this age the residual is provably below 1e-4.
    private static double SpringCutoff(Spring s)
    {
        double z = s.Zeta, w = s.Omega;
        if (z < .999)
        {
            var root = Math.Sqrt(1 - z * z);
            return Math.Log(1e4 / root) / (z * w);
        }
        if (z <= 1.001) return 12.5 / w;
        double q = Math.Sqrt(z * z - 1), r1 = -w * (z - q), r2 = -w * (z + q);
        var bound = (Math.Abs(r1) + Math.Abs(r2)) / Math.Abs(r1 - r2);
        return Math.Log(bound * 1e4) / Math.Abs(r1);
    }

    /// <summary>
    /// Step response of a spring released from rest at age 0 towards 1. Closed form, so a target that changes again
    /// before the first move settles is the sum of two step responses: velocity carries across the interruption.
    /// Lands on exactly 1 once the residual is below 1e-4.
    /// </summary>
    public static double SpringStep(double age, Spring spring)
    {
        if (!double.IsFinite(age) || age <= 0) return 0;
        var s = Sane(spring);
        if (age >= SpringCutoff(s)) return 1;
        double z = s.Zeta, w = s.Omega;
        if (z < .999)
        {
            double root = Math.Sqrt(1 - z * z), wd = w * root;
            return 1 - Math.Exp(-z * w * age) * (Math.Cos(wd * age) + z / root * Math.Sin(wd * age));
        }
        if (z <= 1.001) return 1 - Math.Exp(-w * age) * (1 + w * age);
        double q = Math.Sqrt(z * z - 1), r1 = -w * (z - q), r2 = -w * (z + q);
        return 1 + (r2 * Math.Exp(r1 * age) - r1 * Math.Exp(r2 * age)) / (r1 - r2);
    }

    /// <summary>Derivative of <see cref="SpringStep"/> with respect to age.</summary>
    public static double SpringVelocity(double age, Spring spring)
    {
        if (!double.IsFinite(age) || age <= 0) return 0;
        var s = Sane(spring);
        if (age >= SpringCutoff(s)) return 0;
        double z = s.Zeta, w = s.Omega;
        if (z < .999)
        {
            var root = Math.Sqrt(1 - z * z);
            return w / root * Math.Exp(-z * w * age) * Math.Sin(w * root * age);
        }
        if (z <= 1.001) return w * w * age * Math.Exp(-w * age);
        double q = Math.Sqrt(z * z - 1), r1 = -w * (z - q), r2 = -w * (z + q);
        return r1 * r2 * (Math.Exp(r1 * age) - Math.Exp(r2 * age)) / (r1 - r2);
    }

    /// <summary>Monotone critically damped arrival that is 0 until age 0 and lands on exactly 1 at duration.</summary>
    public static double Settle(double age, double duration)
    {
        if (!double.IsFinite(age) || age <= 0) return 0;
        if (!double.IsFinite(duration) || duration <= 0 || age >= duration) return 1;
        // Critically damped from rest; the small remaining tail is folded away over the last 40%, so the curve stays
        // monotone and finishes exactly.
        double w = 9 / duration, residual = Math.Exp(-w * age) * (1 + w * age);
        var window = 1 - Smooth((age - .6 * duration) / (.4 * duration));
        return Clamp(1 - residual * window, 0, 1);
    }

    /// <summary>
    /// A spring tuned to one sung word: short syllables snap, held notes bloom, energy loosens the damping, and the
    /// seed varies every word a little.
    /// </summary>
    public static Spring WordSpring(ulong seed, double duration, double cadence, double energy)
    {
        duration = Clamp(duration, .04, 30);
        cadence = double.IsFinite(cadence) && cadence > 0 ? Clamp(cadence, .04, 30) : duration;
        energy = Clamp(energy, 0, 1);
        var held = Smooth((duration - .35) / 1.1);
        // Rise time follows the syllable. Critically damped, 10-90% rise is about 3.36 / omega, so omega is derived
        // from the rise directly.
        var rise = Clamp(.42 * Min(duration, cadence) + .07, .10, .34)
            * (1 + .14 * Jitter(seed, 7)) * (1 + .35 * held);
        return new Spring(3.36 / rise, Clamp(.78 - .12 * energy + .14 * held + .06 * Jitter(seed, 8), .58, 1));
    }

    /// <summary>
    /// Everything one timed word does, as a pure function of playback time. <paramref name="exitStart"/> is the line's
    /// exit onset (<see cref="double.PositiveInfinity"/> when it has none) and <paramref name="order"/> the word's
    /// reading position in its line, 0…1, which staggers the exit.
    /// </summary>
    public static WordMotion ForWord(double time, double start, double end, double cadence, double exitStart,
                                        double order, ulong seed, double energy, bool reduceMotion)
    {
        if (!double.IsFinite(time) || !double.IsFinite(start)) return WordMotion.AtRest;
        if (!double.IsFinite(end) || end < start) end = start;
        double duration = Clamp(end - start, .04, 30), age = time - start;
        energy = Clamp(energy, 0, 1);
        var revealSpan = Clamp(.65 * duration, .16, .46) * (1 + .08 * Jitter(seed, 1));
        var reveal = Settle(age, revealSpan);
        var enter = reduceMotion ? reveal : SpringStep(age, WordSpring(seed, duration, cadence, energy));
        // Emphasis gathers with the onset (a breath ahead of it) and releases when the voice leaves the word. The
        // release is a second step on the same spring, so velocity carries through it, and a clipped syllable carries
        // less weight than a held one.
        var rise = Clamp(.32 * duration + .05, .07, .22) * (1 + .1 * Jitter(seed, 2));
        var swell = new Spring(3.36 / rise, Clamp(.72 + .08 * Jitter(seed, 3) - .08 * energy, .55, .95));
        // The voice lets go more slowly than it arrives: a longer, better damped release leaves a short afterglow and
        // never rings below rest.
        var fall = new Spring(3.36 / (rise * 1.35), Clamp(.86 + .05 * Jitter(seed, 11), .8, .95));
        var release = Max(end, start + .08);
        var weight = .72 + .28 * Smooth((duration - .08) / .5);
        var heat = Clamp(weight * (SpringStep(time - (start - .02), swell) - SpringStep(time - release, fall)), 0, 1.15);
        double leave = 0;
        if (double.IsFinite(exitStart))
        {
            var stagger = Clamp(order, 0, 1) * .14 + (.5 + .5 * Jitter(seed, 4)) * .05;
            // A word stays a moment once it has fully arrived: in a very fast line it otherwise left about a tenth of
            // a second after its onset, a flash rather than a word.
            var leaves = Max(exitStart + stagger, start + revealSpan + .20);
            leave = Settle(time - leaves, .40 * (1 + .1 * Jitter(seed, 5)));
        }
        if (reduceMotion || heat <= 0) return new WordMotion(reveal, enter, heat, leave, 0, 0, 1, 0);
        double hot = Min(heat, 1), held = Smooth((duration - .25) / 1.1);
        var arc = Math.Sin(Clamp(age / duration, 0, 1) * Pi);
        var wind = Noise2(seed, age * (.75 + .4 * energy)) * held;
        var x = Noise(seed ^ 0x9E37UL, age * .6) * .025 * held * hot;
        var y = (-.13 * held * arc + wind * .055 - energy * .035) * hot;
        var scale = 1 + (held * arc * .12 + energy * .045) * hot;
        var angle = wind * 1.2 * hot;
        return new WordMotion(reveal, enter, heat, leave, x, y, scale, angle);
    }

    /// <summary>Rolling layouts: one onset's share of the cursor, as a seeded spring.</summary>
    public static double RollingHandoff(double age, double cadence, ulong seed, double energy)
    {
        if (!double.IsFinite(age) || age <= 0) return 0;
        cadence = double.IsFinite(cadence) ? Clamp(cadence, .06, 30) : .4;
        var rise = Clamp(cadence * .5, .12, .36) * (1 + .1 * Jitter(seed, 9));
        var s = new Spring(3.36 / rise, Clamp(.86 - .08 * Clamp(energy, 0, 1) + .05 * Jitter(seed, 10), .74, 1));
        return SpringStep(age, s);
    }

    /// <summary>Slow seeded float for settled words, in point-size units and degrees. Amplitude 0 returns exact rest.</summary>
    public static WordPose IdleDrift(ulong seed, double time, double amplitude)
    {
        if (!double.IsFinite(time) || !double.IsFinite(amplitude) || amplitude <= 0) return WordPose.Rest;
        amplitude = Min(amplitude, 1.5);
        var rate = .13 + .06 * (.5 + .5 * Jitter(seed, 6));
        return new WordPose(
            X: Noise2(seed ^ 0xA1UL, time * rate) * .018 * amplitude,
            Y: Noise2(seed ^ 0xB2UL, time * rate * 1.17) * .024 * amplitude,
            Scale: 1 + Noise(seed ^ 0xD4UL, time * rate * .9) * .006 * amplitude,
            Angle: Noise(seed ^ 0xC3UL, time * rate * .8) * .45 * amplitude,
            Opacity: 1,
            Blur: 0);
    }
}

/// <summary>
/// Playback position smoothed for display. Small re-anchoring errors are absorbed by briefly bending the rate (never
/// more than a few per cent); real jumps are adopted at once and reported so the scene can cross-fade.
/// A mutable struct: keep it in a field and call <see cref="Advance"/> on it.
/// </summary>
public struct LyricClockState
{
    public double Time;
    public double Wall;
    public bool Valid;

    /// <summary>
    /// Advances to wall time <paramref name="wall"/> given the player's raw position. Returns true when a jump (a seek,
    /// a clock step back) was adopted at once rather than eased.
    /// </summary>
    public bool Advance(double wall, double raw, bool playing)
    {
        if (!double.IsFinite(wall) || !double.IsFinite(raw)) return false;
        if (!Valid || !double.IsFinite(Time) || !double.IsFinite(Wall))
        {
            Time = raw; Wall = wall; Valid = true;
            return false;
        }
        var dt = wall - Wall;
        if (dt < -1) { Time = raw; Wall = wall; return true; }
        // Repeated evaluation of one frame, or a slightly older display.
        if (dt <= 0) return false;
        Wall = wall;
        dt = Min(dt, .25);
        var predicted = Time + (playing ? dt : 0);
        var error = raw - predicted;
        if (Math.Abs(error) > .30) { Time = raw; return true; }
        var pull = error * (1 - Math.Exp(-dt / .45));
        if (playing)
        {
            // Bend the rate, never reverse it: at most a few per cent for the usual re-anchoring jitter, more only for
            // a larger correction.
            var cap = (.06 + .5 * Math.Abs(error)) * dt;
            pull = Clamp(pull, -cap, cap);
        }
        else
        {
            pull = Clamp(pull, 0, .5 * dt);
        }
        Time = predicted + pull;
        return false;
    }
}

/// <summary>
/// Critically damped follower, exactly integrated over dt, then limited to a maximum rate in units per second. Used
/// for colour and audio so neither can flash. A mutable struct: keep it in a field and call <see cref="Follow"/>.
/// </summary>
public struct Follower
{
    public double Value;
    public double Velocity;
    public bool Valid;

    public double Follow(double target, double dt, double tau, double maxRate)
    {
        if (!double.IsFinite(target)) target = Valid && double.IsFinite(Value) ? Value : 0;
        if (!Valid || !double.IsFinite(Value) || !double.IsFinite(Velocity))
        {
            Value = target; Velocity = 0; Valid = true;
            return target;
        }
        if (!double.IsFinite(dt) || dt <= 0) return Value;
        dt = Min(dt, 1);
        var w = 1 / (double.IsFinite(tau) && tau > 1e-4 ? tau : 1e-4);
        double y0 = Value - target, v0 = Velocity, e = Math.Exp(-w * dt);
        var next = target + (y0 + (v0 + w * y0) * dt) * e;
        var velocity = (v0 - w * (v0 + w * y0) * dt) * e;
        if (double.IsFinite(maxRate) && maxRate > 0)
        {
            var step = maxRate * dt;
            if (next - Value > step) { next = Value + step; velocity = Min(velocity, maxRate); }
            else if (Value - next > step) { next = Value - step; velocity = Max(velocity, -maxRate); }
        }
        Value = next; Velocity = velocity;
        return next;
    }
}
