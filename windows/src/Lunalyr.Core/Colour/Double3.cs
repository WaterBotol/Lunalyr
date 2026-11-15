using System.Numerics;
using System.Runtime.CompilerServices;

namespace Lunalyr.Core.Colour;

/// <summary>
/// Three doubles: an sRGB colour (0...1 per channel) or an HSB triple (hue 0...1, saturation, brightness), as the
/// Mac's <c>SIMD3&lt;Double&gt;</c>. Album stops use <see cref="Vector3"/> (the Mac's <c>SIMD3&lt;Float&gt;</c>).
/// </summary>
public readonly record struct Double3(double X, double Y, double Z)
{
    public static Double3 Zero => default;

    public static Double3 Repeating(double value) => new(value, value, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double3 operator *(Double3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double3 operator /(Double3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public Double3 Clamped(double low, double high) =>
        new(Math.Min(high, Math.Max(low, X)), Math.Min(high, Math.Max(low, Y)), Math.Min(high, Math.Max(low, Z)));

    /// <summary>Widened from a float stop.</summary>
    public static Double3 From(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Narrowed to a float stop.</summary>
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
}

/// <summary>Swift's rounding conventions, where the Mac's maths depends on them.</summary>
internal static class SwiftMath
{
    /// <summary>Swift's <c>.rounded()</c>: to nearest, halves away from zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Round(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

    /// <summary>Swift's <c>Int(x)</c> for a finite double: truncation toward zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Int(double x) => (int)x;

    /// <summary>Swift's <c>min</c>: <c>y &lt; x ? y : x</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Min(double x, double y) => y < x ? y : x;

    /// <summary>Swift's <c>max</c>: <c>y &gt;= x ? y : x</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Max(double x, double y) => y >= x ? y : x;

    public static double Min(double x, double y, double z) => Min(Min(x, y), z);

    public static double Max(double x, double y, double z) => Max(Max(x, y), z);
}
