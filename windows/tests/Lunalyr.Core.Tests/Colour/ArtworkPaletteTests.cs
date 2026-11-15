using Lunalyr.Core.Colour;
using RGB = Lunalyr.Core.Colour.ArtworkPalette.Rgb;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>The album colours behind the desktop player's card.</summary>
public class ArtworkPaletteTests
{
    private static List<RGB> Solid(RGB colour, int count = 576) => Enumerable.Repeat(colour, count).ToList();

    // Ported from ArtworkPaletteTests.swift: testNoArtworkIsNeutral
    [Fact]
    public void NoArtworkIsNeutral()
    {
        Assert.Equal(ArtworkPalette.Neutral, ArtworkPalette.Of((ArtworkPixels?)null));
        Assert.Equal(ArtworkPalette.Neutral, ArtworkPalette.Of(new List<RGB>()));
        Assert.False(ArtworkPalette.Neutral.FromArtwork);
    }

    // Ported from ArtworkPaletteTests.swift: testGreyBlackAndWhiteCoversAreNeutral
    [Fact]
    public void GreyBlackAndWhiteCoversAreNeutral()
    {
        foreach (var grey in new[] { 0.0, 0.5, 1.0 })
            Assert.Equal(ArtworkPalette.Neutral, ArtworkPalette.Of(Solid(new RGB(grey, grey, grey))));
    }

    // Ported from ArtworkPaletteTests.swift: testAColourfulCoverKeepsItsHue
    [Fact]
    public void AColourfulCoverKeepsItsHue()
    {
        var pink = ArtworkPalette.Of(Solid(new RGB(0.96, 0.36, 0.66)));
        Assert.True(pink.FromArtwork);
        Assert.True(pink.Accent.Red > pink.Accent.Green);
        Assert.True(pink.Accent.Red > pink.Accent.Blue);
        Assert.True(pink.Deep.Red > pink.Deep.Green, "the deep shade keeps the hue");
        var teal = ArtworkPalette.Of(Solid(new RGB(0.1, 0.7, 0.75)));
        Assert.True(teal.Accent.Blue > teal.Accent.Red);
        Assert.True(teal.Accent.Green > teal.Accent.Red);
    }

    // Ported from ArtworkPaletteTests.swift: testTheStrongestColourWinsOverAGreyGround
    [Fact]
    public void TheStrongestColourWinsOverAGreyGround()
    {
        // A mostly grey cover with a red mark: the card takes the red.
        var pixels = Solid(new RGB(0.5, 0.5, 0.52), 520);
        pixels.AddRange(Solid(new RGB(0.9, 0.1, 0.12), 56));
        var palette = ArtworkPalette.Of(pixels);
        Assert.True(palette.FromArtwork);
        Assert.True(palette.Accent.Red > palette.Accent.Green * 1.5);
    }

    // Ported from ArtworkPaletteTests.swift: testPaleCoversAreDarkenedForWhiteType
    [Fact]
    public void PaleCoversAreDarkenedForWhiteType()
    {
        foreach (var colour in new[] { new RGB(0.98, 0.95, 0.97), new RGB(1, 0.97, 0.6), new RGB(0.7, 1, 0.8), new RGB(0.95, 0.36, 0.66), new RGB(1, 0.9, 0.1) })
        {
            var palette = ArtworkPalette.Of(Solid(colour));
            Assert.True(palette.Deep.ContrastWithWhite >= ArtworkPalette.DeepContrast - 0.01, $"{colour}");
            Assert.True(palette.Accent.ContrastWithWhite >= ArtworkPalette.AccentContrast - 0.01, $"{colour}");
        }
    }

    // Ported from ArtworkPaletteTests.swift: testGuardDarkensOnlyWhatNeedsIt
    [Fact]
    public void GuardDarkensOnlyWhatNeedsIt()
    {
        var white = ArtworkPalette.Guarded(new RGB(1, 1, 1), 4.5);
        Assert.True(white.ContrastWithWhite >= 4.5);
        var navy = new RGB(0.05, 0.08, 0.3);
        Assert.Equal(navy, ArtworkPalette.Guarded(navy, 4.5));
        // Darkening keeps the hue's proportions.
        var yellow = ArtworkPalette.Guarded(new RGB(1, 0.9, 0.2), 4.5);
        Assert.Equal(0.9, yellow.Green / yellow.Red, 0.001);
    }

    // Ported from ArtworkPaletteTests.swift: testContrastMatchesWCAG
    [Fact]
    public void ContrastMatchesWCAG()
    {
        Assert.Equal(21, new RGB(0, 0, 0).ContrastWithWhite, 0.01);
        Assert.Equal(1, new RGB(1, 1, 1).ContrastWithWhite, 0.01);
    }

    // Ported from ArtworkPaletteTests.swift: testReadsAnImage. The Mac draws a pink-into-purple preview cover; here a
    // generated diagonal blend of the same two system colours stands in for it.
    [Fact]
    public void ReadsAnImage()
    {
        var cover = TestImages.Diagonal(200, 200, (1.0, 0.176, 0.333), (0.686, 0.322, 0.871));
        var pixels = ArtworkPalette.Samples(cover);
        Assert.Equal(24 * 24, pixels.Count);
        var palette = ArtworkPalette.Of(cover);
        Assert.True(palette.FromArtwork);
        Assert.True(palette.Accent.Red > palette.Accent.Green, "pink into purple");
        Assert.Equal(palette, ArtworkPalette.Of(cover));
    }
}

/// <summary>Simple generated covers: no artwork from anywhere.</summary>
internal static class TestImages
{
    /// <summary>A linear blend from <paramref name="from"/> at the top-left to <paramref name="to"/> at the bottom-right.</summary>
    public static ArtworkPixels Diagonal(int width, int height, (double R, double G, double B) from, (double R, double G, double B) to)
    {
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var t = (x + y) / (double)Math.Max(1, width + height - 2);
            var i = (y * width + x) * 4;
            bytes[i] = (byte)Math.Round((from.R + (to.R - from.R) * t) * 255);
            bytes[i + 1] = (byte)Math.Round((from.G + (to.G - from.G) * t) * 255);
            bytes[i + 2] = (byte)Math.Round((from.B + (to.B - from.B) * t) * 255);
            bytes[i + 3] = 255;
        }
        return new ArtworkPixels(width, height, bytes);
    }

    /// <summary>An image filled by a function of the pixel's position, returning sRGB 0...1.</summary>
    public static ArtworkPixels Draw(int width, int height, Func<int, int, (double R, double G, double B)> colour)
    {
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b) = colour(x, y);
            var i = (y * width + x) * 4;
            bytes[i] = (byte)Math.Round(Math.Clamp(r, 0, 1) * 255);
            bytes[i + 1] = (byte)Math.Round(Math.Clamp(g, 0, 1) * 255);
            bytes[i + 2] = (byte)Math.Round(Math.Clamp(b, 0, 1) * 255);
            bytes[i + 3] = 255;
        }
        return new ArtworkPixels(width, height, bytes);
    }
}
