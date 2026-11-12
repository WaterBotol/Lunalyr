using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>How an imported picture is placed, and how bright it is behind the lyric stage.</summary>
public class PictureLegibilityTests
{
    /// <summary>An opaque BGRA picture filled with <paramref name="grey"/>, with an optional lighter rectangle.</summary>
    private static byte[] Picture(int width, int height, byte grey, (int X, int Y, int W, int H, byte Grey)? patch = null)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var value = patch is { } p && x >= p.X && x < p.X + p.W && y >= p.Y && y < p.Y + p.H ? p.Grey : grey;
                var i = (y * width + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        return pixels;
    }

    // Ported from LyricLegibilityTests.swift: testImageLuminanceReadsTheRegionBehindTheLyrics
    [Fact]
    public void ImageLuminanceReadsTheRegionBehindTheLyrics()
    {
        // White in the middle third of a dark picture: what matters is the middle, where the lyric stage sits.
        var image = Picture(300, 200, (byte)(0.05 * 255), (60, 40, 180, 120, 255));
        double screenWidth = 1512, screenHeight = 982;
        var filled = PictureLegibility.StageLuminance(image, 300, 200, screenWidth, screenHeight, WallpaperPlacement.Fill);
        Assert.NotNull(filled);
        Assert.True(filled > 0.8, $"{filled}");
        var dark = Picture(300, 200, (byte)(0.1 * 255));
        var fitted = PictureLegibility.StageLuminance(dark, 300, 200, screenWidth, screenHeight, WallpaperPlacement.Fit);
        Assert.NotNull(fitted);
        Assert.True(fitted < 0.02, $"{fitted}");
        Assert.Null(PictureLegibility.StageLuminance(dark, 300, 200, 0, 0, WallpaperPlacement.Fill));
    }

    [Fact]
    public void PlacementsCoverOrFitTheScreen()
    {
        // A tall picture on a wide screen.
        var fill = WallpaperPlacements.Frame(900, 1600, 1920, 1080, WallpaperPlacement.Fill);
        Assert.Equal((0.0, (1080 - 1920 * 1600 / 900.0) / 2, 1920.0, 1920 * 1600 / 900.0), fill);
        Assert.Equal(0, WallpaperPlacements.Frame(900, 1600, 1920, 1080, WallpaperPlacement.FillTop).Y);
        var bottom = WallpaperPlacements.Frame(900, 1600, 1920, 1080, WallpaperPlacement.FillBottom);
        Assert.Equal(1080, bottom.Y + bottom.Height, 6);
        var fit = WallpaperPlacements.Frame(900, 1600, 1920, 1080, WallpaperPlacement.Fit);
        Assert.Equal((1920 - 1080 * 900 / 1600.0) / 2, fit.X, 6);
        Assert.Equal((0.0, 1080.0), (fit.Y, fit.Height));
        Assert.Equal((0.0, 0.0, 0.0, 0.0), WallpaperPlacements.Frame(0, 10, 1920, 1080, WallpaperPlacement.Fill));
    }

    [Fact]
    public void PlacementsReadTheMacsRawValues()
    {
        foreach (var placement in Enum.GetValues<WallpaperPlacement>())
            Assert.Equal(placement, WallpaperPlacements.Parse(placement.RawValue()));
        Assert.Equal(["fill", "fillTop", "fillBottom", "fit"], Enum.GetValues<WallpaperPlacement>().Select(p => p.RawValue()));
        Assert.Equal(WallpaperPlacement.Fill, WallpaperPlacements.Parse("stretch"));
        Assert.Equal(WallpaperPlacement.Fill, WallpaperPlacements.Parse(null));
    }
}
