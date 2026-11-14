// Ported from the Mac's Sources/Player/ArtworkPalette.swift (the desktop player's sleeve colours; Lunalyr's own code,
// no Verso lines). The Mac's per-image cache is left to the caller: the palette is worked out once per cover.

namespace Lunalyr.Core.Colour;

/// <summary>
/// The colours the desktop player's card is washed in, taken from the album's own artwork: the cover's strongest colour
/// (<see cref="Accent"/>, for the record's label, the drag fill and the capsule rims) and a deep shade of it
/// (<see cref="Deep"/>, the bottom of the wash). Both pass a contrast guard: a pale or near-white cover is darkened
/// until white type stays legible on it. With no artwork, a neutral grey-blue.
/// </summary>
/// <param name="Accent">The cover's strongest colour, bright enough to read as colour on the card.</param>
/// <param name="Deep">A deep shade under the type.</param>
/// <param name="FromArtwork">False for the neutral fallback (no artwork, or a grey cover).</param>
public readonly record struct ArtworkPalette(ArtworkPalette.Rgb Accent, ArtworkPalette.Rgb Deep, bool FromArtwork)
{
    /// <summary>An sRGB colour, 0...1 per channel.</summary>
    public readonly record struct Rgb(double Red, double Green, double Blue)
    {
        /// <summary>WCAG relative luminance (sRGB).</summary>
        public double Luminance
        {
            get
            {
                static double Channel(double value)
                {
                    var c = Math.Min(1, Math.Max(0, value));
                    return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                }
                return 0.2126 * Channel(Red) + 0.7152 * Channel(Green) + 0.0722 * Channel(Blue);
            }
        }

        /// <summary>WCAG contrast ratio of white type on this colour.</summary>
        public double ContrastWithWhite => 1.05 / (Luminance + 0.05);

        /// <summary>Hue (0...1), saturation and brightness.</summary>
        public (double Hue, double Saturation, double Brightness) Hsb
        {
            get
            {
                var hsb = AlbumColourGrid.Hsb(new Double3(Red, Green, Blue));
                return (hsb.X, hsb.Y, hsb.Z);
            }
        }

        public static Rgb FromHsb(double hue, double saturation, double brightness)
        {
            var rgb = ColourSpaces.Rgb(new Double3(hue, Math.Min(1, Math.Max(0, saturation)), Math.Min(1, Math.Max(0, brightness))));
            return new Rgb(rgb.X, rgb.Y, rgb.Z);
        }

        public Rgb Scaled(double factor) => new(Red * factor, Green * factor, Blue * factor);

        public Rgb Mixed(Rgb other, double amount) =>
            new(Red + (other.Red - Red) * amount, Green + (other.Green - Green) * amount, Blue + (other.Blue - Blue) * amount);
    }

    public static ArtworkPalette Neutral { get; } = new(new Rgb(0.50, 0.52, 0.62), new Rgb(0.13, 0.13, 0.17), false);

    /// <summary>The desktop player card's flat tint: the deep shade with a little of the accent.</summary>
    public Rgb CardTint => Deep.Mixed(Accent, FromArtwork ? 0.08 : 0.04);

    /// <summary>
    /// White type on <see cref="Deep"/> reaches at least this, so the card's quieter ink levels (docs/DESIGN.md) still
    /// read 4.5:1 over a bright wallpaper.
    /// </summary>
    public const double DeepContrast = 9.0;

    /// <summary>The accent carries the big title at the wash's top: AA for large type.</summary>
    public const double AccentContrast = 3.0;

    /// <summary>The palette of a decoded cover (read at 24 × 24), or <see cref="Neutral"/> without one.</summary>
    public static ArtworkPalette Of(ArtworkPixels? image) => image is null ? Neutral : Of(Samples(image));

    /// <summary>The palette from a cover's pixels (sRGB, 0...1).</summary>
    public static ArtworkPalette Of(IReadOnlyList<Rgb> pixels)
    {
        if (pixels.Count == 0) return Neutral;
        double tr = 0, tg = 0, tb = 0;
        foreach (var pixel in pixels) { tr += pixel.Red; tg += pixel.Green; tb += pixel.Blue; }
        double count = pixels.Count;
        var average = new Rgb(tr / count, tg / count, tb / count);
        // The strongest hue: pixels binned by hue, each weighted by how vivid it is, so a few saturated pixels beat a
        // wide grey ground.
        const int bins = 12;
        var weight = new double[bins];
        var sums = new Rgb[bins];
        foreach (var pixel in pixels)
        {
            var hsb = pixel.Hsb;
            if (!(hsb.Saturation > 0.2 && hsb.Brightness > 0.18)) continue;
            var bin = Math.Min(bins - 1, (int)(hsb.Hue * bins));
            var w = hsb.Saturation * hsb.Saturation * (0.4 + hsb.Brightness);
            weight[bin] += w;
            sums[bin] = new Rgb(sums[bin].Red + pixel.Red * w, sums[bin].Green + pixel.Green * w, sums[bin].Blue + pixel.Blue * w);
        }
        var strongest = 0;
        for (var i = 1; i < bins; i++) if (weight[strongest] < weight[i]) strongest = i;
        // Colour only when enough of the cover has it (2% of a fully vivid cover).
        var vivid = weight[strongest] > count * 0.02;
        var dominant = vivid
            ? new Rgb(sums[strongest].Red / weight[strongest], sums[strongest].Green / weight[strongest], sums[strongest].Blue / weight[strongest])
            : average;
        var dominantHsb = dominant.Hsb;
        // A grey, black or white cover: the neutral card.
        if (!(vivid || average.Hsb.Saturation > 0.12)) return Neutral;
        // The accent: the dominant hue, lifted to read as colour, then guarded.
        var accent = Guarded(Rgb.FromHsb(dominantHsb.Hue, Math.Max(0.45, Math.Min(0.85, dominantHsb.Saturation)),
                                         Math.Max(0.62, Math.Min(0.95, dominantHsb.Brightness))), AccentContrast);
        // The deep shade: the dominant hue, dark, a touch of the cover's average.
        var deepBase = Rgb.FromHsb(dominantHsb.Hue, Math.Max(0.35, Math.Min(0.8, dominantHsb.Saturation * 0.9)), 0.32)
            .Mixed(average.Scaled(0.4), 0.2);
        return new ArtworkPalette(accent, Guarded(deepBase, DeepContrast), true);
    }

    /// <summary><paramref name="colour"/> darkened, keeping its hue, until white type on it reaches <paramref name="contrast"/>.</summary>
    public static Rgb Guarded(Rgb colour, double contrast)
    {
        var result = colour;
        var steps = 0;
        while (result.ContrastWithWhite < contrast && steps < 60)
        {
            result = result.Scaled(0.94);
            steps += 1;
        }
        return result;
    }

    /// <summary>
    /// The cover averaged down to <paramref name="side"/> × <paramref name="side"/> and read back as sRGB pixels; pixels
    /// at half opacity or less are skipped. (The Mac draws the cover small with medium interpolation; this averages,
    /// so values can differ in the last bit or two, not in kind.)
    /// </summary>
    public static List<Rgb> Samples(ArtworkPixels image, int side = 24)
    {
        var small = image.Averaged(side);
        var pixels = new List<Rgb>(side * side);
        var bytes = small.Rgba;
        for (var index = 0; index < side * side; index++)
        {
            var alpha = bytes[index * 4 + 3] / 255.0;
            if (!(alpha > 0.5)) continue;
            // Straight alpha already: no division by coverage.
            pixels.Add(new Rgb(bytes[index * 4] / 255.0, bytes[index * 4 + 1] / 255.0, bytes[index * 4 + 2] / 255.0));
        }
        return pixels;
    }
}
