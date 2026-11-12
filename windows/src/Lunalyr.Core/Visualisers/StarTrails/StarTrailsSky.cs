// Ported from Lunalyr for Mac's Sources/Visualizers/StarTrailsSky.swift (Lunalyr's own code; no Verso lines).

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Visualisers;

/// <summary>The nine cover tints of a <see cref="StarTrailsSky"/>, stored inline.</summary>
[InlineArray(StarTrailsSky.TintCount)]
public struct StarTrailsSkyTints
{
    private Double4 _element;
}

/// <summary>
/// The reference nebula provides the light and shape. Album mode adds a separate field of nine cover tints, so colour
/// can vary across the sky independently of the cloud density. A value type with inline tints: making or comparing one
/// never allocates.
/// </summary>
public readonly struct StarTrailsSky : IEquatable<StarTrailsSky>
{
    /// <summary>The number of reference sky colours.</summary>
    public const int Count = 11;

    /// <summary>The number of cover tints (the cover's 3 × 3 grid).</summary>
    public const int TintCount = 9;

    private static readonly Double3[] OriginalColourArray =
    [
        new(0.031, 0.008, 0.075), // Deep sky
        new(0.055, 0.035, 0.112), new(0.082, 0.043, 0.145), new(0.118, 0.059, 0.188), new(0.180, 0.078, 0.259),
        new(0.235, 0.110, 0.310), new(0.298, 0.141, 0.353), new(0.360, 0.165, 0.392), // Cloud ramp
        new(0.647, 0.282, 0.565), new(0.831, 0.369, 0.706), // Wisps
        new(1.0, 0.97, 1.0), // Dust stars
    ];

    private static readonly Vector4[] PackedOriginalArray =
        Array.ConvertAll(OriginalColourArray, colour => new Vector4((float)colour.X, (float)colour.Y, (float)colour.Z, 0));

    /// <summary>The reference sky colours, display space: deep sky, the cloud ramp, wisps, dust stars.</summary>
    public static IReadOnlyList<Double3> OriginalColours => OriginalColourArray;

    /// <summary>The reference colours as the shader reads them (W 0).</summary>
    public static ReadOnlySpan<Vector4> PackedOriginal => PackedOriginalArray;

    private readonly StarTrailsSkyTints _tints;

    private StarTrailsSky(in StarTrailsSkyTints tints)
    {
        _tints = tints;
    }

    /// <summary>
    /// Linear RGB, scaled to at most unit luminance. W is the strength of the album effect, also used for the nebula's
    /// softness.
    /// </summary>
    [UnscopedRef]
    public ReadOnlySpan<Double4> Tints => _tints;

    /// <summary>The violet original: white tints at no strength.</summary>
    public static StarTrailsSky Original { get; } = MakeOriginal();

    private static StarTrailsSky MakeOriginal()
    {
        var tints = new StarTrailsSkyTints();
        ((Span<Double4>)tints).Fill(new Double4(1, 1, 1, 0));
        return new StarTrailsSky(tints);
    }

    /// <summary>The cover's sky: its nine cells as tints, hue kept, luminance mostly normalised.</summary>
    public static StarTrailsSky Album(AlbumWallpaperPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var tints = new StarTrailsSkyTints();
        var stops = palette.ArtworkStops;
        for (var i = 0; i < TintCount; i++)
        {
            var colour = Double3.From(stops[i]);
            var luminance = Perceptual.Luminance(colour);
            var linear = new Double3(Perceptual.Linear(colour.X), Perceptual.Linear(colour.Y), Perceptual.Linear(colour.Z));
            // Keep a little of the cover's light and dark structure. The shader multiplies this by the original cloud
            // luminance.
            var gain = 0.68 + 0.28 * Math.Sqrt(luminance);
            var tint = luminance > 0.0005 ? linear * (gain / luminance) : Double3.Repeating(gain);
            tints[i] = new Double4(tint.X, tint.Y, tint.Z, 1);
        }
        return new StarTrailsSky(tints);
    }

    public bool Equals(StarTrailsSky other) => Tints.SequenceEqual(other.Tints);

    public override bool Equals(object? obj) => obj is StarTrailsSky other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var tint in Tints) hash.Add(tint);
        return hash.ToHashCode();
    }

    public static bool operator ==(StarTrailsSky left, StarTrailsSky right) => left.Equals(right);

    public static bool operator !=(StarTrailsSky left, StarTrailsSky right) => !left.Equals(right);
}

/// <summary>
/// Three equal lags blend the album's tints and effect strength in linear light. A new cover arrives over about 2.5
/// seconds without a pop.
/// </summary>
/// <remarks>Mutable value type: keep it in a non-readonly field and mutate it in place.</remarks>
public struct StarTrailsSkyFade
{
    public const double Tau = 0.42;

    private StarTrailsSkyTints _a, _b, _c;

    public StarTrailsSkyFade(StarTrailsSky sky)
    {
        _target = sky;
        _a = default;
        _b = default;
        _c = default;
        sky.Tints.CopyTo(_a);
        sky.Tints.CopyTo(_b);
        sky.Tints.CopyTo(_c);
    }

    private StarTrailsSky _target;

    public readonly StarTrailsSky Target => _target;

    public void Aim(StarTrailsSky sky) => _target = sky;

    public void Step(double dt)
    {
        if (!(dt > 0)) return;
        var k = 1 - Math.Exp(-dt / Tau);
        var target = _target.Tints;
        for (var i = 0; i < target.Length; i++)
        {
            _a[i] += (target[i] - _a[i]) * k;
            _b[i] += (_a[i] - _b[i]) * k;
            _c[i] += (_b[i] - _c[i]) * k;
        }
    }

    /// <summary>Writes the reference colours, then the current tints (<see cref="StarTrailsSky.Count"/> + 9 float4s).</summary>
    public readonly void Write(Span<Vector4> output)
    {
        StarTrailsSky.PackedOriginal.CopyTo(output);
        var target = _target.Tints;
        for (var i = 0; i < StarTrailsSky.TintCount; i++)
        {
            var tint = _c[i];
            var goal = target[i];
            var value = (tint - goal).Length < 1e-7 ? goal : tint;
            output[StarTrailsSky.Count + i] = value.ToVector4();
        }
    }
}
