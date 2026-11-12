// Ported from Lunalyr for Mac (Sources/Visualizers/StarTrails*.swift): its SIMD4<Double> and SIMD2<Double>.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Lunalyr.Core.Visualisers;

/// <summary>Four doubles, the Mac's <c>SIMD4&lt;Double&gt;</c>: Star Trails keeps its state in double precision.</summary>
public readonly record struct Double4(double X, double Y, double Z, double W)
{
    public static Double4 Zero => default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double4 operator +(Double4 a, Double4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double4 operator -(Double4 a, Double4 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double4 operator *(Double4 a, double s) => new(a.X * s, a.Y * s, a.Z * s, a.W * s);

    /// <summary>Component <paramref name="index"/>, 0…3.</summary>
    public double this[int index] => index switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        3 => W,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>The sum of the components, in order.</summary>
    public double Sum => X + Y + Z + W;

    /// <summary>Euclidean length (<c>simd_length</c>).</summary>
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z + W * W);

    /// <summary>Narrowed per component to a shader float4.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector4 ToVector4() => new((float)X, (float)Y, (float)Z, (float)W);
}

/// <summary>Two doubles, the Mac's <c>SIMD2&lt;Double&gt;</c>, for Star Trails' space-frame maths.</summary>
internal readonly record struct Double2(double X, double Y)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double2 operator +(Double2 a, Double2 b) => new(a.X + b.X, a.Y + b.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double2 operator -(Double2 a, Double2 b) => new(a.X - b.X, a.Y - b.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double2 operator -(Double2 a) => new(-a.X, -a.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double2 operator *(Double2 a, double s) => new(a.X * s, a.Y * s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(Double2 a, Double2 b) => a.X * b.X + a.Y * b.Y;

    public double LengthSquared => X * X + Y * Y;

    public double Length => Math.Sqrt(LengthSquared);
}
