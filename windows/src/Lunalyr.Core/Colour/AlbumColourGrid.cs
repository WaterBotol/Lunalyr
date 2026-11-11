// Ported from the Mac's Sources/UI/ColorExtractor.swift. Its logic is Lunalyr's own (git blame puts only the import,
// the type's declaration line, braces and blank lines in the squashed Verso import), so it is translated faithfully,
// under a name of our own.

namespace Lunalyr.Core.Colour;

/// <summary>
/// A 3×3 artwork palette, as HSB triples (hue 0...1). Each region keeps its strongest supported hue; representative
/// accents also get a place among the five gradient stops.
/// </summary>
public static class AlbumColourGrid
{
    private const int Side = 72;
    private const int GridSide = 3;
    private const int HueBins = 24;

    private static readonly double[] Depth = [-0.12, -0.04, 0.06, 0.03, 0.12, -0.08, -0.04, 0.08, 0.02];

    /// <summary>
    /// The nine HSB cells of a cover, top-left first, row by row. The cover is read at 72 × 72 with nearest sampling,
    /// which keeps a cover's colour fields separate even when bright, contrasting shapes share one region. Pixels
    /// under half opacity are skipped; a cover with nothing opaque gets <see cref="FallbackHsb"/>.
    /// </summary>
    public static Double3[] Extract(ArtworkPixels? image)
    {
        if (image is null || image.IsEmpty) return [.. FallbackHsb];
        var regions = new Region[GridSide * GridSide];
        for (var i = 0; i < regions.Length; i++) regions[i] = new Region();
        var artworkColours = new Region();
        const int cell = Side / GridSide;
        var bytes = image.Rgba;
        for (var y = 0; y < Side; y++)
        {
            var row = ArtworkPixels.NearestSource(y, Side, image.Height) * image.Width;
            for (var x = 0; x < Side; x++)
            {
                var i = (row + ArtworkPixels.NearestSource(x, Side, image.Width)) * 4;
                if (bytes[i + 3] < 128) continue;
                var rgb = new Double3(bytes[i] / 255.0, bytes[i + 1] / 255.0, bytes[i + 2] / 255.0);
                var colour = Hsb(rgb);
                regions[y / cell * GridSide + x / cell].Add(rgb, colour);
                artworkColours.Add(rgb, colour);
            }
        }
        if (artworkColours.Count <= 0) return [.. FallbackHsb];

        var palette = new Double3[regions.Length];
        for (var index = 0; index < regions.Length; index++)
        {
            var region = regions[index];
            var ranked = region.Ranked(Math.Max(4, region.Count / 20));
            palette[index] = ranked.Count > 0 ? region.Bins[ranked[0]].Colour
                : region.Count > 0 ? region.Average : artworkColours.Average;
        }
        var used = new List<int>();
        var accents = new List<double>();
        // The colour field uses the corners and centre. Keep the most prominent accents there, then reserve the
        // remaining cells for further hues in the full wallpaper gradient.
        foreach (var bin in artworkColours.Ranked(Math.Max(6, artworkColours.Count / 65)))
        {
            var accent = artworkColours.Bins[bin].Colour;
            if (accents.Exists(hue => HueDistance(hue, accent.X) < 0.08)) continue;
            var primary = AlbumWallpaperPalette.SampleIndices;
            int? slot = null;
            var best = double.NegativeInfinity;
            for (var index = 0; index < regions.Length; index++)
            {
                var inPrimary = Array.IndexOf(primary, index) >= 0;
                if (used.Count < 4 ? !inPrimary : inPrimary) continue;
                if (used.Contains(index)) continue;
                var local = regions[index].Bins[bin].Weight;
                var sharesHue = palette[index].Y * palette[index].Z > 0.09 && HueDistance(palette[index].X, accent.X) < 0.08;
                var affinity = local + (sharesHue ? cell * cell : 0);
                // The first of equals, as Swift's max(by:) keeps.
                if (slot is null || affinity > best) { slot = index; best = affinity; }
            }
            if (slot is not { } chosen) break;
            if (palette[chosen].Y * palette[chosen].Z <= 0.09 || HueDistance(palette[chosen].X, accent.X) >= 0.08)
                palette[chosen] = accent;
            accents.Add(accent.X);
            used.Add(chosen);
            if (used.Count == 8) break;
        }
        for (var index = 0; index < palette.Length; index++)
        {
            var colour = palette[index];
            palette[index] = new Double3(colour.X, Math.Min(1, colour.Y * 1.14),
                                         Math.Min(0.96, Math.Max(0.08, colour.Z * 0.84 + 0.12 + Depth[index])));
        }
        return palette;
    }

    /// <summary>The most vivid of the five sampled cells (corners and centre).</summary>
    public static Double3 Accent(IReadOnlyList<Double3> palette)
    {
        Double3? best = null;
        foreach (var index in AlbumWallpaperPalette.SampleIndices)
        {
            if (index >= palette.Count) continue;
            var stop = palette[index];
            if (best is not { } current || current.Y * current.Z < stop.Y * stop.Z) best = stop;
        }
        return best ?? FallbackHsb[2];
    }

    private sealed class Region
    {
        public int Count;
        public Double3 Sum;
        public readonly HueBin[] Bins = new HueBin[HueBins];

        public Double3 Average => Hsb(Sum / Math.Max(1, Count));

        public void Add(Double3 rgb, Double3 colour)
        {
            Count += 1;
            Sum += rgb;
            var chroma = colour.Y * colour.Z;
            // Near-grey highlights and dark shadows inform the background brightness, but carry little reliable hue
            // information.
            if (!(colour.Y >= 0.20 && chroma >= 0.09)) return;
            var bin = (int)SwiftMath.Round(colour.X * HueBins) % HueBins;
            var weight = 0.45 + 0.55 * Math.Min(1, chroma / 0.5);
            Bins[bin].Count += 1;
            Bins[bin].Weight += weight;
            Bins[bin].Sum += rgb * weight;
        }

        /// <summary>Bins with at least <paramref name="minimum"/> pixels, heaviest first (stable on ties).</summary>
        public List<int> Ranked(int minimum)
        {
            var result = new List<int>(HueBins);
            for (var i = 0; i < HueBins; i++) if (Bins[i].Count >= minimum) result.Add(i);
            // Insertion sort: stable, and at most 24 entries.
            for (var i = 1; i < result.Count; i++)
            {
                var item = result[i];
                var j = i - 1;
                while (j >= 0 && Bins[result[j]].Weight < Bins[item].Weight) { result[j + 1] = result[j]; j--; }
                result[j + 1] = item;
            }
            return result;
        }
    }

    private struct HueBin
    {
        public int Count;
        public double Weight;
        public Double3 Sum;

        public readonly Double3 Colour => Hsb(Sum / Weight);
    }

    internal static double HueDistance(double a, double b)
    {
        var difference = Math.Abs(a - b);
        return Math.Min(difference, 1 - difference);
    }

    /// <summary>sRGB (0...1) to HSB (hue 0...1).</summary>
    public static Double3 Hsb(Double3 rgb)
    {
        var peak = SwiftMath.Max(rgb.X, rgb.Y, rgb.Z);
        var spread = peak - SwiftMath.Min(rgb.X, rgb.Y, rgb.Z);
        if (!(spread > 0)) return new Double3(0, 0, peak);
        double angle;
        if (peak == rgb.X) angle = (rgb.Y - rgb.Z) / spread;
        else if (peak == rgb.Y) angle = 2 + (rgb.Z - rgb.X) / spread;
        else angle = 4 + (rgb.X - rgb.Y) / spread;
        return new Double3((angle / 6 + 1) % 1, spread / peak, peak);
    }

    /// <summary>Used before artwork resolves or when a cover cannot be decoded (sRGB triples, kept as HSB).</summary>
    public static IReadOnlyList<Double3> FallbackHsb { get; } = Array.ConvertAll(new[]
    {
        new Double3(0.23, 0.16, 0.58), new Double3(0.12, 0.40, 0.44), new Double3(0.65, 0.28, 0.43),
        new Double3(0.15, 0.30, 0.56), new Double3(0.54, 0.25, 0.16), new Double3(0.43, 0.19, 0.56),
        new Double3(0.10, 0.46, 0.40), new Double3(0.55, 0.34, 0.16), new Double3(0.30, 0.19, 0.52),
    }, Hsb);
}
