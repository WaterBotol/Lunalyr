// Small deterministic helpers the line painters share: hashing a song's seed into stable fractions, wrapping the
// pattern clock. Windows' own.

using System.Numerics;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal static class PainterMath
{
    public const float Tau = MathF.PI * 2;

    /// <summary>A stable value in 0...1 for (seed, index, salt).</summary>
    public static float Hash(int seed, int index, int salt)
    {
        unchecked
        {
            var h = (uint)seed * 0x9E3779B1u;
            h ^= (uint)index * 0x85EBCA77u + 0x632BE59Bu;
            h = (h ^ (h >> 15)) * 0x2C1B3C6Du;
            h ^= (uint)salt * 0xC2B2AE3Du + 0x27D4EB2Fu;
            h = (h ^ (h >> 12)) * 0x297A2D39u;
            h ^= h >> 15;
            return (h >> 8) / 16777216f;
        }
    }

    /// <summary>A value between <paramref name="min"/> and <paramref name="max"/> for (seed, index, salt).</summary>
    public static float Range(int seed, int index, int salt, float min, float max) => min + (max - min) * Hash(seed, index, salt);

    /// <summary>The fractional part of <paramref name="value"/> × <paramref name="rate"/> + <paramref name="offset"/>, worked out in double.</summary>
    public static float Cycle(double value, double rate, double offset = 0)
    {
        var x = value * rate + offset;
        return (float)(x - Math.Floor(x));
    }

    /// <summary>sin(value × rate + offset), with the angle wrapped in double so long-running clocks stay smooth.</summary>
    public static float Wave(double value, double rate, double offset = 0) => MathF.Sin(Cycle(value, rate / (2 * Math.PI), offset / (2 * Math.PI)) * Tau);

    /// <summary>An angle value × rate (radians), wrapped into 0...2π in double.</summary>
    public static float Angle(double value, double rate, double offset = 0) => Cycle(value, rate / (2 * Math.PI), offset / (2 * Math.PI)) * Tau;

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    public static Vector2 Polar(float radius, float angle) => new(radius * MathF.Cos(angle), radius * MathF.Sin(angle));
}
