// Ported from the Mac's Sources/UI/AlbumWallpaperPalette.swift (Lunalyr's own code; no Verso lines).

using System.Numerics;

namespace Lunalyr.Core.Colour;

/// <summary>
/// Five stops for the colour field and effects, plus the full nine-stop wallpaper gradient. Stops are sRGB 0...1 as
/// floats, as the Mac keeps them.
/// </summary>
public sealed class AlbumWallpaperPalette : IEquatable<AlbumWallpaperPalette>
{
    /// <summary>The grid cells the five stops come from: the corners and the centre.</summary>
    public static readonly int[] SampleIndices = [0, 2, 4, 6, 8];

    private static readonly int[] Diagonal = [0, 1, 3, 2, 4, 6, 5, 7, 8];

    private readonly Vector3[] _stops;
    private readonly Vector3[] _artworkStops;

    /// <summary>From the nine HSB cells of <see cref="AlbumColourGrid.Extract"/>; missing cells take the fallback's.</summary>
    public AlbumWallpaperPalette(IReadOnlyList<Double3> grid)
    {
        var rgb = new Vector3[9];
        for (var index = 0; index < 9; index++)
        {
            var hsb = index < grid.Count ? grid[index] : AlbumColourGrid.FallbackHsb[index];
            var hue = hsb.X * 6;
            float Channel(double offset)
            {
                var k = (hue + offset) % 6;
                return (float)(hsb.Z * (1 - hsb.Y * Math.Max(0, SwiftMath.Min(k, 4 - k, 1))));
            }
            rgb[index] = new Vector3(Channel(5), Channel(3), Channel(1));
        }
        _stops = new Vector3[SampleIndices.Length];
        for (var i = 0; i < SampleIndices.Length; i++) _stops[i] = rgb[SampleIndices[i]];
        _artworkStops = rgb;
    }

    private AlbumWallpaperPalette(Vector3[] stops, Vector3[] artworkStops)
    {
        _stops = stops;
        _artworkStops = artworkStops;
    }

    /// <summary>The fallback palette, used before artwork arrives or without any.</summary>
    public static AlbumWallpaperPalette Fallback { get; } = new(AlbumColourGrid.FallbackHsb);

    /// <summary>The five sampled stops (top-left, top-right, centre, bottom-left, bottom-right), sRGB.</summary>
    public IReadOnlyList<Vector3> Stops => _stops;

    /// <summary>All nine cells, row by row, sRGB.</summary>
    public IReadOnlyList<Vector3> ArtworkStops => _artworkStops;

    /// <summary>The nine cells in diagonal order, top-left to bottom-right: the album gradient's stops.</summary>
    public Vector3[] WallpaperStops
    {
        get
        {
            var result = new Vector3[Diagonal.Length];
            for (var i = 0; i < Diagonal.Length; i++) result[i] = _artworkStops[Diagonal[i]];
            return result;
        }
    }

    public AlbumWallpaperPalette Interpolated(AlbumWallpaperPalette destination, double fraction)
    {
        var amount = (float)Math.Min(1, Math.Max(0, fraction));
        static Vector3[] Blend(Vector3[] from, Vector3[] to, float amount)
        {
            var result = new Vector3[Math.Min(from.Length, to.Length)];
            for (var i = 0; i < result.Length; i++) result[i] = from[i] + (to[i] - from[i]) * amount;
            return result;
        }
        return new AlbumWallpaperPalette(Blend(_stops, destination._stops, amount),
                                         Blend(_artworkStops, destination._artworkStops, amount));
    }

    public Vector3 Average
    {
        get
        {
            var sum = Vector3.Zero;
            foreach (var stop in _stops) sum += stop;
            return sum / _stops.Length;
        }
    }

    /// <summary>The wallpaper stops with their colour richness scaled about their own luma (Rec. 709 weights).</summary>
    public Vector3[] GradientStops(double vividness)
    {
        var stops = WallpaperStops;
        if (vividness == 1) return stops;
        for (var i = 0; i < stops.Length; i++) stops[i] = Saturated(stops[i], vividness);
        return stops;
    }

    /// <summary>One colour's richness scaled about its luma, clamped to 0...1 (the Mac's <c>gradientStops</c> rule).</summary>
    public static Vector3 Saturated(Vector3 rgb, double vividness)
    {
        var grey = new Vector3(rgb.X * 0.2126f + rgb.Y * 0.7152f + rgb.Z * 0.0722f);
        return Vector3.Clamp(grey + (rgb - grey) * (float)vividness, Vector3.Zero, Vector3.One);
    }

    public double GradientLuminance(double vividness)
    {
        var best = 0.0;
        var any = false;
        foreach (var stop in GradientStops(vividness))
        {
            var value = Perceptual.Luminance(Double3.From(stop));
            if (!any || value > best) best = value;
            any = true;
        }
        return any ? best : 0;
    }

    public bool Equals(AlbumWallpaperPalette? other) =>
        other is not null && _stops.AsSpan().SequenceEqual(other._stops) && _artworkStops.AsSpan().SequenceEqual(other._artworkStops);

    public override bool Equals(object? obj) => Equals(obj as AlbumWallpaperPalette);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var stop in _artworkStops) hash.Add(stop);
        return hash.ToHashCode();
    }
}

/// <summary>
/// The hue that fills the most of the cover, and colours to set against it. A small vivid detail can supply an accent
/// without becoming the background.
/// </summary>
public readonly struct AlbumColourHarmony
{
    public AlbumColourHarmony(IReadOnlyList<OkLab> colours)
    {
        var chromatic = new List<OkLab>(colours.Count);
        foreach (var colour in colours) if (colour.Chroma >= 0.035) chromatic.Add(colour);
        double Support(OkLab colour)
        {
            var sum = 0.0;
            foreach (var sample in chromatic)
            {
                var distance = LyricHighlight.HueDistance(colour.Hue, sample.Hue);
                sum += Math.Max(0, 1 - distance / (Math.PI / 4)) * (0.7 + Math.Min(0.3, sample.Chroma));
            }
            return sum;
        }
        double? hue = null;
        var best = 0.0;
        foreach (var colour in chromatic)
        {
            var support = Support(colour);
            if (hue is null || best < support) { hue = colour.Hue; best = support; }
        }
        BackgroundHue = hue;
    }

    public double? BackgroundHue { get; }
}
