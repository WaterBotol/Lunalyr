// The redone action words' motion (the Mac's WordActionsLively.swift, 16 Nov 2026, Lunalyr's own lines, ported
// exactly): a whole-word pose (turn, scale, tilt, travel, tint, glow) and the curves the per-letter pass and its
// particles share. Every effect is identity before the onset and from its duration on; the duration grows with how
// long the word is held and may outlast the word by up to about a second. "Action word strength" (Subtle, Middle,
// Wild) and the hold scale the size of every motion (Intensity). Under Reduce Motion a word only glows
// (ReduceMotionCue).

using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Motion;

public static class LivelyAction
{
    /// <summary>Wild.</summary>
    public const double DefaultAmount = 1.0;
    public const double Subtle = 0.2, Middle = 0.5, Wild = 1.0;
    /// <summary>A chorus repeat moves at this share of the strength (the Mac's <c>WordAccent.gentle</c>).</summary>
    public const double GentleShare = 0.35;

    /// <summary>0…1: the amount, scaled from 0.6× for a quickly sung word up to 1× for one held 1.2 s or longer.</summary>
    public static double Intensity(double amount, double hold)
    {
        var held = 0.6 + 0.4 * Easing.Smoothstep((hold - 0.25) / 0.95);
        return Math.Min(1, Math.Max(0, amount)) * held;
    }

    /// <summary>Seconds from the onset; longer for a longer-held word. Every curve has come to rest a little before this.</summary>
    public static double Duration(WordAction action, double rawHold)
    {
        var h = Math.Min(2.5, Math.Max(0.2, rawHold));
        return action switch
        {
            WordAction.Spin => h >= 0.9 ? 2.1 : 1.75,
            WordAction.Float => Math.Max(1.7, h + 0.9),
            WordAction.Dip => 1.7,
            WordAction.Shake => Math.Max(0.9, Math.Min(h, 1.4) + 0.5),
            WordAction.Hop => h >= 0.9 ? 1.55 : 1.15,
            WordAction.Grow => Math.Max(1.8, h + 1.0),
            WordAction.Shrink => Math.Max(1.2, h + 0.6),
            WordAction.Dissolve => Math.Max(2.3, h + 1.3),
            WordAction.Flicker => Math.Max(1.5, h + 0.7),
            WordAction.Frost => Math.Max(2.0, h + 1.0),
            WordAction.Bleed => Math.Max(2.4, h + 1.2),
            WordAction.Dark => Math.Max(1.9, h + 0.8),
            WordAction.Shatter => 1.35,
            WordAction.Melt => Math.Max(1.7, h + 0.8),
            WordAction.Echo => Math.Max(1.2, h + 0.5),
            WordAction.Pulse => Math.Max(1.0, h + 0.5),
            WordAction.Wave => Math.Max(1.3, h + 0.6),
            _ => Math.Max(2.2, h + 1.1), // sink
        };
    }

    // ---- Curves: damped springs, sin² bumps and smoothsteps, continuous in value and speed ----

    public static double Ss(double x) => Easing.Smoothstep(x);
    public static double Clamp01(double x) => Math.Min(1, Math.Max(0, x));

    /// <summary>In over <paramref name="attack"/>, out over the last <paramref name="release"/> of <paramref name="duration"/>.</summary>
    public static double Envelope(double t, double attack, double release, double duration) =>
        Ss(t / attack) * (1 - Ss((t - (duration - release)) / release));

    public static double EaseOutBack(double x, double s)
    {
        var u = x - 1;
        return 1 + (s + 1) * u * u * u + s * u * u;
    }

    public static double EaseOutCubic(double x)
    {
        var u = 1 - Clamp01(x);
        return 1 - u * u * u;
    }

    /// <summary>
    /// A damped spring let go at t = 0, from 0 towards 1, starting at rest. <paramref name="peak"/>: seconds to its first
    /// (overshooting) peak; <paramref name="overshoot"/>: how far past 1 that peak goes (0.1 = 10 %).
    /// </summary>
    public static double Spring(double t, double peak, double overshoot)
    {
        if (!(t > 0)) return 0;
        var l = Math.Log(Math.Max(1e-4, overshoot));
        var zeta = -l / Math.Sqrt(Math.PI * Math.PI + l * l);
        var wd = Math.PI / peak;
        var w = wd / Math.Sqrt(1 - zeta * zeta);
        return 1 - Math.Exp(-zeta * w * t) * (Math.Cos(wd * t) + zeta * w / wd * Math.Sin(wd * t));
    }

    /// <summary>A sin² hump over [0, width]: starts and ends at rest.</summary>
    public static double Bump(double t, double width)
    {
        if (!(t > 0) || !(t < width)) return 0;
        var s = Math.Sin(Math.PI * t / width);
        return s * s;
    }

    /// <summary>1, falling smoothly to 0 over the last <paramref name="last"/> seconds before <paramref name="d"/>.</summary>
    public static double Rest(double t, double d, double last = 0.22) => 1 - Ss((t - (d - last)) / last);

    /// <summary>Smootherstep: at rest at both ends, with no jump in acceleration either.</summary>
    public static double Ss5(double x)
    {
        var c = Clamp01(x);
        return c * c * c * (c * (6 * c - 15) + 10);
    }

    /// <summary>A soft limit to ±<paramref name="limit"/>.</summary>
    public static double Soft(double x, double limit) => limit * Math.Tanh(x / limit);

    /// <summary>A seeded number in [0, 1), the Mac's exactly (64-bit wrapping, as Swift's Int).</summary>
    public static double Rnd(int seed, int a, int b)
    {
        unchecked
        {
            var mixed = (long)seed * 73_856_093L ^ (long)a * 19_349_663L ^ (long)b * 83_492_791L;
            var h = (ulong)mixed;
            h ^= h >> 33;
            h *= 0xff51afd7ed558ccdUL;
            h ^= h >> 33;
            h *= 0xc4ceb9fe1a85ec53UL;
            h ^= h >> 33;
            return (double)(h % 10_000) / 10_000;
        }
    }

    /// <summary>A heartbeat's thump: a smooth swell to 1 at 70 ms and a fall.</summary>
    public static double Thump(double u)
    {
        if (!(u > 0)) return 0;
        var x = u / 0.07;
        return x * x * Math.Exp(2 * (1 - x));
    }

    // ---- Shared motions (whole word and letters agree) ----

    /// <summary>
    /// Spin: the turn in degrees. A wind-up the other way, then one or two whole turns, swinging past, back a little,
    /// and settling. 0 before the onset, the whole turns from <paramref name="d"/> on.
    /// </summary>
    public static double SpinAngle(double t, double a, double hold, double d)
    {
        var turns = hold >= 0.9 && a > 0.35 ? 2.0 : 1.0;
        var target = 360 * turns;
        if (!(t > 0)) return 0;
        if (!(t < d)) return target;
        const double start = 0.16;
        var time = turns == 2 ? 1.15 : 0.85;
        var x = Clamp01((t - start) / time);
        x = x * x * x * (x * (6 * x - 15) + 10);
        var tau = t - (start + 0.72 * time);
        var swing = tau > 0 ? 40 * a * Ss(tau / 0.2) * Math.Exp(-3.2 * tau) * Math.Sin(2 * Math.PI * 1.15 * tau) : 0;
        var angle = -26 * a * Bump(t, 0.34) + target * x + swing;
        return target + (angle - target) * Rest(t, d, last: 0.3);
    }

    /// <summary>Spin: how fast it is turning, 0…1.</summary>
    public static double SpinSpeed(double t, double a, double hold, double d)
    {
        const double dt = 1.0 / 240;
        var v = (SpinAngle(t + dt, a, hold, d) - SpinAngle(t - dt, a, hold, d)) / (2 * dt);
        return Math.Min(1, Math.Abs(v) / 1100);
    }

    /// <summary>Hop: the whole word's jump in ems (negative is up), its squash and stretch, and its lean, from the foot.</summary>
    public static (double Dy, double Sy, double Tilt) Hop(double t, double a, double hold, double d)
    {
        if (!(t > 0) || !(t < d)) return (0, 1, 0);
        double dy = 0, sy = 1, tilt = 0;
        sy -= 0.17 * a * Bump(t, 0.24);
        void Flight(double start, double time, double height, double lean)
        {
            var v = (t - start) / time;
            if (!(v > 0) || !(v < 1)) return;
            dy -= height * 4 * v * (1 - v);
            var fast = Math.Abs(1 - 2 * v) * Ss(v / 0.16) * Ss((1 - v) / 0.16);
            sy += 0.16 * a * fast * (height / (0.82 * Math.Max(0.01, a)));
            tilt += lean * Math.Sin(2 * Math.PI * v) * Math.Sin(Math.PI * v);
        }
        const double land1 = 0.13 + 0.42;
        Flight(0.13, 0.42, 0.82 * a, -7 * a);
        sy -= 0.2 * a * Bump(t - land1 + 0.02, 0.2);
        double last;
        if (hold >= 0.9)
        {
            var start2 = land1 + 0.12;
            Flight(start2, 0.3, 0.34 * a, 5 * a);
            last = start2 + 0.3;
            sy -= 0.11 * a * Bump(t - last + 0.02, 0.16);
        }
        else
        {
            Flight(land1 + 0.1, 0.2, 0.12 * a, 0);
            last = land1 + 0.3;
        }
        var u = t - last - 0.12;
        if (u > 0) sy += 0.045 * a * Math.Exp(-7 * u) * Math.Sin(2 * Math.PI * 4.2 * u);
        var r = Rest(t, d);
        return (dy * r, 1 + (sy - 1) * r, tilt * r);
    }

    /// <summary>Dip: the whole word's drop in ems (positive is down): a lift, a fast fall, a spring back up past the line.</summary>
    public static double Dip(double t, double a, double d)
    {
        if (!(t > 0) || !(t < d)) return 0;
        var down = Spring(t - 0.3, peak: 0.22, overshoot: 0.08);
        var up = Spring(t - 0.62, peak: 0.32, overshoot: 0.22);
        return (0.75 * a * (down - up) - 0.1 * a * Bump(t - 0.08, 0.3)) * Rest(t, d);
    }

    /// <summary>Melt: 0 → 1 as it softens, back through a jelly wobble as it firms up.</summary>
    public static double Melt(double t, double delay, double d)
    {
        if (!(t > 0) || !(t < d)) return 0;
        var soften = Spring(t - 0.05 - delay, peak: 0.6, overshoot: 0.02);
        var firm = Spring(t - (d - 0.72) - 0.5 * delay, peak: 0.2, overshoot: 0.3);
        return (soften - firm) * Rest(t, d);
    }

    /// <summary>Reduce Motion's cue: only a glow and an ink tint, rising over the first fifth and fading over the last half.</summary>
    public static LivelyPose ReduceMotionCue(WordAction action, double elapsed, double duration, bool gentle = false)
    {
        if (!(elapsed > 0) || !(elapsed < duration)) return LivelyPose.Identity;
        var p = elapsed / duration;
        var cue = Ss(p / 0.2) * (1 - Ss((p - 0.5) / 0.5)) * (gentle ? 0.5 : 1);
        var pose = LivelyPose.Identity with { Glow = 0.6 * cue };
        return action switch
        {
            WordAction.Flicker => pose with { Warm = 0.5 * cue },
            WordAction.Frost => pose with { Cool = 0.5 * cue },
            WordAction.Bleed => pose with { Red = 0.4 * cue },
            WordAction.Dark => pose with { Dim = 0.25 * cue },
            _ => pose,
        };
    }

    /// <summary>Shrink: how far it has pulled in (0 at rest).</summary>
    public static double Shrink(double t, double a, double d)
    {
        if (!(t > 0) || !(t < d)) return 0;
        var squeeze = Spring(t - 0.12, peak: 0.24, overshoot: 0.12) - Spring(t - (d - 0.55), peak: 0.26, overshoot: 0.28);
        var tremble = 0.012 * a * Math.Sin(2 * Math.PI * 9 * t) * Ss((t - 0.3) / 0.2) * Rest(t, d, last: 0.65);
        return (0.46 * a * squeeze - 0.06 * a * Bump(t, 0.2) + tremble) * Rest(t, d);
    }

    /// <summary>Echo: the word's sway, in ems.</summary>
    public static double EchoSway(double t, double a) => 0.22 * a * Math.Sin(2 * Math.PI * 0.9 * Math.Max(0, t));

    /// <summary>
    /// A drip with weight: it swells at the lip, stretches with its neck thinning, lets go of a drop that falls and
    /// fades, and the trail springs back to a bead. Null before it is born or when too small.
    /// </summary>
    public static Drip? DripAt(double t, double born, double s, double swell, double stretch)
    {
        var u = t - born;
        if (!(u > 0) || !(s > 0.01)) return null;
        var full = 0.075 * s;
        var a = Ss5(u / swell);
        var d = new Drip { Bead = full * a, Length = 0.05 * s * a };
        var v = (u - swell) / stretch;
        if (!(v > 0)) return d;
        if (v < 1)
        {
            var p = v * v;
            return d with { Length = 0.05 * s + 0.5 * s * p, Neck = 1 - 0.6 * p, Bead = full * (1 + 0.15 * p) };
        }
        var w = (v - 1) * stretch;
        var end = 0.55 * s;
        var speed = 1.0 * s / stretch;
        return d with
        {
            DropY = end + speed * w + 0.5 * 7 * w * w,
            DropR = full * 1.15 * (1 - 0.15 * Ss(w / 0.3)),
            DropAlpha = 1 - Ss5(w / 0.55),
            Length = end * (0.25 + 0.75 * (1 - Spring(w, peak: 0.16, overshoot: 0.3))),
            Neck = 0.4 + 0.5 * Ss(w / 0.25),
            Bead = full * (0.35 + 0.25 * Ss(w / 0.3)),
        };
    }

    /// <summary>Burn: kindles over about 0.6 s, holds, cools over the last 0.7 s.</summary>
    public static double Burn(double t, double d)
    {
        if (!(t > 0) || !(t < d)) return 0;
        return Ss5(t / 0.65) * (1 - Ss5((t - (d - 0.75)) / 0.7));
    }

    /// <summary>Freeze: how far the frost has spread from the leading edge (−0.25 → 1.6 over about 1 s).</summary>
    public static double FrostFront(double t) => -0.25 + 1.85 * (1 - Math.Pow(1 - Clamp01(t / 1.1), 2.2));
    public static double FrostThaw(double t, double d) => Ss5((t - (d - 0.8)) / 0.75);

    public static double FrostCover(double t, double d)
    {
        if (!(t > 0) || !(t < d)) return 0;
        return Ss5(t / 0.9) * (1 - FrostThaw(t, d));
    }

    /// <summary>Dark: how far the shadow has come in, 0…1.</summary>
    public static double Darkness(double t, double d)
    {
        if (!(t > 0) || !(t < d)) return 0;
        return Ss5(t / 0.75) * (1 - Ss5((t - (d - 0.8)) / 0.75));
    }

    /// <summary>Dark: where the ink's edge is across the word (0 its leading side, 1 the far side).</summary>
    public static double InkFront(double t, double d) => -0.35 + 2.15 * Ss5(t / 1.0) - 2.15 * Ss5((t - (d - 0.85)) / 0.8);

    /// <summary>Sink: 0 → 1 as a letter goes under, back up quickly past the line and settling.</summary>
    public static double Sink(double t, double delay, double d)
    {
        if (!(t > 0) || !(t < d)) return 0;
        var under = Ss5((t - 0.05 - delay) / 0.95) + 0.09 * Bump(t - 0.6 - delay, 0.8);
        var up = Spring(t - (d - 0.85) - 0.35 * delay, peak: 0.3, overshoot: 0.2);
        return (under - up) * Rest(t, d);
    }
}

/// <summary>One drip's state, in ems from the lip where it forms (the Mac's <c>LivelyAction.Drip</c>).</summary>
public readonly record struct Drip
{
    public Drip() { }
    /// <summary>The trail from the lip to the bead.</summary>
    public double Length { get; init; }
    /// <summary>The trail's width at its end, against its width at the lip.</summary>
    public double Neck { get; init; } = 1;
    /// <summary>The bead's radius at the trail's end.</summary>
    public double Bead { get; init; }
    /// <summary>The released drop: how far below the lip, its radius, how much is left of it.</summary>
    public double DropY { get; init; }
    public double DropR { get; init; }
    public double DropAlpha { get; init; }
}

/// <summary>
/// The whole-word part of one frame (the Mac's <c>LivelyPose</c>). Travel in ems (up negative), angles in degrees.
/// </summary>
public readonly record struct LivelyPose
{
    public LivelyPose() { }

    public double Dx { get; init; }
    public double Dy { get; init; }
    /// <summary>About the upright axis, drawn as a real turn in perspective.</summary>
    public double Turn { get; init; }
    /// <summary>In the plane.</summary>
    public double Tilt { get; init; }
    public double Sx { get; init; } = 1;
    public double Sy { get; init; } = 1;
    /// <summary>Scale anchor: 0 the top, 0.5 the centre, 1 the foot.</summary>
    public double AnchorY { get; init; } = 0.5;
    public double Opacity { get; init; } = 1;
    public double Glow { get; init; }
    /// <summary>A wide soft bloom, 0…1.</summary>
    public double Bloom { get; init; }
    public double Warm { get; init; }
    public double Cool { get; init; }
    public double Red { get; init; }
    /// <summary>0…1: the whole word (and its label) darkened.</summary>
    public double Dim { get; init; }
    /// <summary>0…1: the whirlwind round a spinning word.</summary>
    public double Twister { get; init; }
    /// <summary>0…1: the dark closing in round the word.</summary>
    public double Vignette { get; init; }

    public static LivelyPose Identity => new();

    public bool IsIdentity => this == Identity;

    /// <summary>What the word's colour is multiplied by (never above 1).</summary>
    public (double R, double G, double B) Multiply =>
        ((1 - 0.35 * Cool) * (1 - Dim),
         Math.Max(0, 1 - 0.25 * Warm - 0.12 * Cool - 0.6 * Red) * (1 - Dim),
         Math.Max(0, 1 - 0.55 * Warm - 0.55 * Red) * (1 - Dim));

    public bool Tints => Warm > 0 || Cool > 0 || Red > 0 || Dim > 0;

    /// <summary>The turn as drawn: past edge-on the word shows its back, which reads the right way round.</summary>
    public double ShownTurn
    {
        get
        {
            var a = Turn % 360;
            return Math.Cos(a * Math.PI / 180) < 0 ? a + 180 : a;
        }
    }

    /// <summary>The pose of <paramref name="action"/> at <paramref name="t"/> seconds after the onset.</summary>
    public static LivelyPose Make(WordAction action, double t, double d, double a, double hold)
    {
        if (!(t > 0) || !(t < d)) return Identity;
        var rest = LivelyAction.Rest(t, d);
        switch (action)
        {
            case WordAction.Spin:
            {
                var speed = LivelyAction.SpinSpeed(t, a, hold, d);
                var wind = LivelyAction.Bump(t, 0.34);
                var whirl = LivelyAction.Ss(speed / 0.8);
                return Identity with
                {
                    Turn = LivelyAction.SpinAngle(t, a, hold, d),
                    Dy = (-0.24 * a * whirl + 0.05 * a * wind) * rest,
                    Sy = 1 + (0.1 * a * whirl - 0.08 * a * wind) * rest,
                    Sx = 1 + (0.06 * a * wind - 0.03 * a * whirl) * rest,
                    AnchorY = 1,
                    Tilt = -8 * a * whirl * Math.Sin(Math.PI * LivelyAction.Clamp01(t / (d * 0.7))) * rest,
                    Glow = (0.25 + 0.45 * whirl) * LivelyAction.Envelope(t, attack: 0.15, release: 0.4, duration: d),
                    Twister = whirl * a,
                };
            }
            case WordAction.Float:
            {
                var lift = LivelyAction.Ss5(t / 0.95) + 0.07 * LivelyAction.Bump(t - 0.55, 0.9) - LivelyAction.Ss5((t - (d - 0.85)) / 0.8);
                var drift = LivelyAction.Ss5((t - 0.1) / 0.8) * LivelyAction.Rest(t, d, last: 0.7);
                return Identity with
                {
                    Dy = (-0.42 * a * lift - 0.05 * a * Math.Sin(2 * Math.PI * 1.1 * t) * drift) * rest,
                    Dx = 0.08 * a * Math.Sin(2 * Math.PI * 0.6 * t) * drift,
                    Tilt = 3.5 * a * Math.Sin(2 * Math.PI * 0.8 * t + 0.5) * drift,
                    Glow = 0.45 * LivelyAction.Clamp01(lift) * rest,
                };
            }
            case WordAction.Dip:
            {
                var dy = LivelyAction.Dip(t, a, d);
                var low = Math.Max(0, dy / Math.Max(0.01, 0.75 * a));
                return Identity with
                {
                    Dy = dy,
                    Sy = 1 - 0.16 * a * low * low + 0.06 * a * Math.Max(0, -dy / Math.Max(0.01, 0.1 * a)) * 0.5,
                    Sx = 1 + 0.11 * a * low * low,
                    AnchorY = 1,
                };
            }
            case WordAction.Shake:
            {
                var fade = t < d - 0.6 ? 1 : Math.Exp(-(t - (d - 0.6)) / 0.18);
                var e = LivelyAction.Ss(t / 0.15) * fade * (0.75 + 0.25 * Math.Sin(2 * Math.PI * 1.7 * t)) * rest;
                return Identity with
                {
                    Dx = 0.045 * a * e * (0.7 * Math.Sin(2 * Math.PI * 6.5 * t) + 0.3 * Math.Sin(2 * Math.PI * 9.7 * t + 1)),
                    Dy = 0.012 * a * e * Math.Sin(2 * Math.PI * 5.9 * t + 2),
                    Tilt = 2 * a * e * (0.7 * Math.Sin(2 * Math.PI * 5.3 * t + 1.1) + 0.3 * Math.Sin(2 * Math.PI * 7.9 * t + 0.4)),
                };
            }
            case WordAction.Hop:
            {
                var h = LivelyAction.Hop(t, a, hold, d);
                return Identity with { Dy = h.Dy, Sy = h.Sy, Sx = 1 - 0.55 * (h.Sy - 1), Tilt = h.Tilt, AnchorY = 1 };
            }
            case WordAction.Grow:
            {
                var held = LivelyAction.Ss5(t / 0.85) + 0.03 * LivelyAction.Bump(t - 0.6, 0.8) - LivelyAction.Ss5((t - (d - 0.9)) / 0.85);
                var breathe = LivelyAction.Ss5((t - 0.9) / 0.6) * LivelyAction.Rest(t, d, last: 0.95);
                var s = (0.36 * a * held + 0.012 * a * Math.Sin(2 * Math.PI * 0.8 * (t - 0.9)) * breathe) * rest;
                return Identity with { Sx = 1 + s, Sy = 1 + s, Glow = 0.9 * LivelyAction.Clamp01(held) * rest };
            }
            case WordAction.Shrink:
            {
                var s = LivelyAction.Shrink(t, a, d);
                return Identity with { Sx = 1 - s, Sy = 1 - 0.72 * s };
            }
            case WordAction.Dissolve:
                return Identity with { Glow = 0.3 * LivelyAction.Envelope(t, attack: 0.6, release: 0.8, duration: d) };
            case WordAction.Flicker:
            {
                var e = LivelyAction.Burn(t, d);
                return Identity with
                {
                    Warm = 0.75 * a * e,
                    Glow = e * (0.75 + 0.12 * Math.Sin(2 * Math.PI * 1.3 * t) + 0.08 * Math.Sin(2 * Math.PI * 2.1 * t + 1)),
                };
            }
            case WordAction.Frost:
            {
                var frozen = LivelyAction.FrostCover(t, d);
                var set = LivelyAction.Bump(t - 0.95, 0.22);
                return Identity with
                {
                    Cool = 0.9 * a * frozen,
                    Sy = 1 + (0.025 * frozen - 0.02 * set) * a,
                    Sx = 1 + (-0.012 * frozen + 0.015 * set) * a,
                    AnchorY = 1,
                    Glow = 0.35 * frozen,
                };
            }
            case WordAction.Bleed:
            {
                var e = LivelyAction.Envelope(t, attack: 0.3, release: 0.5, duration: d);
                return Identity with { Red = 0.3 * a * e, Dy = 0.04 * a * e, Sy = 1 - 0.02 * a * e, AnchorY = 1 };
            }
            case WordAction.Dark:
            {
                var e = LivelyAction.Darkness(t, d);
                var sx = 1 - 0.03 * a * e;
                return Identity with { Sx = sx, Sy = sx, Dim = 0.2 * a * e, Vignette = a * e };
            }
            case WordAction.Shatter:
            {
                static double Jolt(double u) =>
                    u > 0 ? LivelyAction.Ss(u / 0.015) * Math.Exp(-u / 0.09) * Math.Sin(2 * Math.PI * 16 * u) : 0;
                return Identity with
                {
                    Dx = (0.05 * a * Jolt(t - 0.05) + 0.03 * a * Jolt(t - 0.66)) * rest,
                    Tilt = (2.5 * a * Jolt(t - 0.06) - 1.5 * a * Jolt(t - 0.67)) * rest,
                    Glow = 1.1 * Math.Exp(-Math.Pow((t - 0.7) / 0.07, 2)) + 0.6 * Math.Exp(-Math.Pow((t - 0.06) / 0.05, 2)),
                };
            }
            case WordAction.Melt:
            {
                var m = LivelyAction.Melt(t, delay: 0.08, d);
                return Identity with { Warm = 0.15 * a * LivelyAction.Clamp01(m), Sy = 1 + 0.06 * a * m, Sx = 1 + 0.02 * a * m, AnchorY = 0 };
            }
            case WordAction.Echo:
            {
                var e = LivelyAction.Envelope(t, attack: 0.2, release: 0.45, duration: d);
                return Identity with { Glow = 0.3 * e, Dx = LivelyAction.EchoSway(t, a) * e };
            }
            case WordAction.Pulse:
            {
                var s = 0.0;
                var beat = 0.0;
                var last = Math.Max(0.01, hold - 0.3);
                while (beat <= last)
                {
                    s += 0.2 * a * LivelyAction.Thump(t - beat) + 0.13 * a * LivelyAction.Thump(t - beat - 0.24);
                    beat += 0.8;
                }
                s *= rest;
                return Identity with { Sx = 1 + s, Sy = 1 + s, Glow = Math.Min(0.9, 2 * s), Warm = Math.Min(0.4, 1.2 * s) };
            }
            case WordAction.Wave:
            {
                var e = LivelyAction.Envelope(t, attack: 0.35, release: 0.5, duration: d);
                var phase = 2 * Math.PI * (0.55 - 1.5 * t);
                return Identity with { Dy = -0.07 * a * Math.Sin(phase) * e, Tilt = 4 * a * Math.Cos(phase) * e };
            }
            default: // sink
            {
                var s = LivelyAction.Sink(t, delay: 0.15, d);
                return Identity with
                {
                    Dy = 0.62 * a * s,
                    Tilt = 2.5 * a * Math.Sin(2 * Math.PI * 0.7 * t) * LivelyAction.Clamp01(s),
                    Cool = 0.3 * LivelyAction.Clamp01(s),
                };
            }
        }
    }
}

/// <summary>
/// A word's symbol pops in rather than only fading (the Mac's <c>SymbolEntrancePose</c>): a springy scale-up with a
/// little overshoot, a wobble and a rise, settling still; an action word's symbol echoes its action. Travel in ems of
/// the symbol, rotation in degrees. Reduce Motion keeps the plain fade.
/// </summary>
public readonly record struct SymbolEntrancePose
{
    public SymbolEntrancePose() { }

    public double Dx { get; init; }
    public double Dy { get; init; }
    public double Sx { get; init; } = 1;
    public double Sy { get; init; } = 1;
    public double Rotation { get; init; }
    /// <summary>Scale anchor: 0.5 the centre, 1 the foot.</summary>
    public double AnchorY { get; init; } = 0.5;
    public double Cool { get; init; }

    public static SymbolEntrancePose Rest => new();

    /// <summary>Seconds after which it is at rest.</summary>
    public const double Duration = 1.5;

    /// <summary>The onset-relative time the entrance needs while it is still coming in; null before or once settled.</summary>
    public static double? Entering(double elapsed) => elapsed > -0.1 && elapsed < Duration ? elapsed : null;

    /// <summary>The entrance time to draw, or null for a plain fade (Reduce Motion, a settled symbol or a preview).</summary>
    public static double? Entrance(double? elapsed, bool reduceMotion) =>
        reduceMotion || elapsed is not { } e || !(e < Duration) ? null : e;

    public static SymbolEntrancePose Make(WordAction? action, double e)
    {
        if (!(e > 0)) return From(action);
        if (!(e < Duration)) return Rest;
        var pop = LivelyAction.Spring(e, peak: 0.26, overshoot: 0.2);
        var wobble = LivelyAction.Spring(e, peak: 0.3, overshoot: 0.3);
        SymbolEntrancePose p;
        switch (action)
        {
            case WordAction.Spin:
            {
                var turn = LivelyAction.Spring(e, peak: 0.45, overshoot: 0.06);
                var s = 0.4 + 0.6 * pop;
                p = Rest with { Rotation = -360 * (1 - turn), Sx = s, Sy = s };
                break;
            }
            case WordAction.Bleed or WordAction.Melt:
            {
                var fall = LivelyAction.Spring(e, peak: 0.3, overshoot: 0.28);
                p = Rest with { Dy = -0.8 * (1 - fall), Sy = 1 + 0.35 * (1 - fall), Sx = 1 - 0.15 * (1 - fall), AnchorY = 1 };
                break;
            }
            case WordAction.Frost:
            {
                var set = LivelyAction.Spring(e, peak: 0.35, overshoot: 0.05);
                var s = 1.35 - 0.35 * set;
                p = Rest with { Sx = s, Sy = s, Cool = 1 - LivelyAction.Ss5((e - 0.3) / 0.9) };
                break;
            }
            case WordAction.Float or WordAction.Dissolve:
            {
                var rise = LivelyAction.Ss5(e / 0.8) + 0.08 * LivelyAction.Bump(e - 0.5, 0.8);
                var s = 0.75 + 0.25 * LivelyAction.Ss5(e / 0.6);
                p = Rest with { Dy = 0.7 * (1 - rise), Sx = s, Sy = s };
                break;
            }
            case WordAction.Sink:
            {
                var drop = LivelyAction.Ss5(e / 0.8) + 0.08 * LivelyAction.Bump(e - 0.5, 0.8);
                p = Rest with { Dy = -0.7 * (1 - drop) };
                break;
            }
            case WordAction.Hop or WordAction.Dip:
            {
                var land = LivelyAction.Spring(e, peak: 0.22, overshoot: 0.35);
                var squash = LivelyAction.Bump(e - 0.16, 0.2);
                p = Rest with { Dy = -0.9 * (1 - land), Sy = 1 - 0.25 * squash, Sx = 1 + 0.18 * squash, AnchorY = 1 };
                break;
            }
            case WordAction.Shake:
            {
                var s = 0.3 + 0.7 * pop;
                p = Rest with { Sx = s, Sy = s, Dx = 0.12 * LivelyAction.Ss(e / 0.05) * Math.Exp(-5 * e) * Math.Sin(2 * Math.PI * 7 * e) };
                break;
            }
            case WordAction.Grow:
            {
                var big = LivelyAction.Spring(e, peak: 0.32, overshoot: 0.3);
                p = Rest with { Sx = big, Sy = big };
                break;
            }
            case WordAction.Shrink:
            {
                var small = LivelyAction.Spring(e, peak: 0.3, overshoot: 0.2);
                var s = 1.8 - 0.8 * small;
                p = Rest with { Sx = s, Sy = s };
                break;
            }
            case WordAction.Pulse:
            {
                var beat = 0.22 * LivelyAction.Thump(e - 0.35) + 0.14 * LivelyAction.Thump(e - 0.59);
                var s = 0.3 + 0.7 * pop + beat;
                p = Rest with { Sx = s, Sy = s };
                break;
            }
            default:
            {
                var s = 0.3 + 0.7 * pop;
                p = Rest with { Sx = s, Sy = s, Rotation = -20 * (1 - wobble), Dy = 0.35 * (1 - pop) };
                break;
            }
        }
        // Whatever is left of a settle eases out by the end.
        var r = LivelyAction.Rest(e, Duration, last: 0.3);
        return p with
        {
            Dx = p.Dx * r, Dy = p.Dy * r, Rotation = p.Rotation * r, Cool = p.Cool * r,
            Sx = 1 + (p.Sx - 1) * r, Sy = 1 + (p.Sy - 1) * r,
        };
    }

    /// <summary>Where it starts (unseen there: its opacity is still 0).</summary>
    public static SymbolEntrancePose From(WordAction? action) => Make(action, 1e-9);
}
