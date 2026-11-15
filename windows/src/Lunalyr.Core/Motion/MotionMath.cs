using System.Runtime.CompilerServices;

namespace Lunalyr.Core.Motion;

/// <summary>
/// Small numeric helpers shared by the motion code. They follow C's conventions where the Mac's maths relied on them:
/// <see cref="Clamp"/> sends NaN and infinities to the low bound, and <see cref="Min"/>/<see cref="Max"/> ignore a NaN
/// argument (C's <c>fmin</c>/<c>fmax</c>), which <see cref="Math.Min(double, double)"/> does not.
/// </summary>
internal static class MotionMath
{
    public const double Pi = 3.141592653589793;

    /// <summary>Clamps a finite value into [low, high]; anything not finite becomes <paramref name="low"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Clamp(double x, double low, double high) =>
        double.IsFinite(x) ? double.MinNumber(high, double.MaxNumber(low, x)) : low;

    /// <summary>Hermite smoothstep of x clamped to [0, 1] (not-finite input counts as 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Smooth(double x)
    {
        x = Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Min(double a, double b) => double.MinNumber(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Max(double a, double b) => double.MaxNumber(a, b);

    /// <summary>SplitMix64's finaliser: a fast, well-mixed 64-bit hash step.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix64(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    /// <summary>A seeded value in [-1, 1) for one integer lattice point.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Lattice(ulong seed, long index)
    {
        unchecked
        {
            var h = Mix64(seed ^ ((ulong)index * 0xD1B54A32D192ED03UL));
            return (h >> 11) * (2.0 / 9007199254740992.0) - 1.0;
        }
    }
}

/// <summary>The wallpaper's time-driven curves, for motion computed from a time value.</summary>
public static class Easing
{
    /// <summary>
    /// Symmetric 0→1 ease: 3x² − 2x³ of x held to [0, 1]. Infinities clamp to their end; NaN gives 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Smoothstep(double x)
    {
        if (double.IsNaN(x)) return 0;
        var t = x <= 0 ? 0 : x >= 1 ? 1 : x;
        return t * t * (3 - 2 * t);
    }

    /// <summary>Critically damped arrival: 0 until age 0, exactly 1 from <paramref name="duration"/> on.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Settle(double age, double duration) => LyricMotion.Settle(age, duration);
}

/// <summary>Time-driven durations for the wallpaper, in seconds (the Mac's <c>WallpaperMotion</c> tokens).</summary>
public static class WallpaperMotion
{
    // Surfaces
    public const double SurfaceFade = 0.8;
    public const double MediaReveal = 0.6;
    public const double MediaHide = 0.3;

    // Lyric scenes (Settle, critically damped)
    /// <summary>Song change: the old scene leaving.</summary>
    public const double SceneOut = 0.45;
    /// <summary>Song change: the new scene arriving.</summary>
    public const double SceneIn = 0.55;
    /// <summary>Same-track jump (smoothstep): arrival, departure and the departure's delay.</summary>
    public const double SeekIn = 0.25;
    public const double SeekOut = 0.30;
    public const double SeekOutDelay = 0.10;
    /// <summary>Every line or stage exit, including a stage's leave.</summary>
    public const double LineExit = 0.46;
    /// <summary>A word leaving, and the stagger between words.</summary>
    public const double WordExit = 0.40;
    public const double WordExitStagger = 0.14;
    public const double LineAttack = 0.22;
    public const double PreviewRamp = 0.16;
    public const double ExitSpan = 0.70;
    public const double HoldAfterSung = 1.2;
    public const double StyleSwitch = 0.35;

    // Notices and toasts
    public const double NoticeHold = 4.0;
    public const double NoticeFade = 1.0;
    public const double IdleNoticeHold = 5.0;
    public const double ToastIn = 0.32;
    public const double ToastOutAt = 1.9;
    public const double ToastOut = 0.55;
    public const double ToastLife = 2.6;
    public const double InterludeIn = 0.5;
    public const double InterludeInDelay = 0.42;
    public const double InterludeOut = 0.4;
    public const double LoadingDelay = 0.8;
    public const double LoadingIn = 0.4;

    /// <summary>Reduce Motion: movement, scale and blur become an opacity dissolve this long.</summary>
    public const double ReducedDissolve = 0.18;
}
