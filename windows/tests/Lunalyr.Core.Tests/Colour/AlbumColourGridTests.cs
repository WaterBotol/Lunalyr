using System.Numerics;
using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>
/// The palette tests from the Mac's RenderingPerformanceTests.swift (the cover's 3×3 grid and the wallpaper stops), with
/// the NSImage covers drawn as buffers, plus checks of the grid's own rules.
/// </summary>
public class AlbumColourGridTests
{
    // Ported from RenderingPerformanceTests.swift: testMonochromeArtworkRetainsItsHueAcrossGradientStops
    [Fact]
    public void MonochromeArtworkRetainsItsHueAcrossGradientStops()
    {
        var image = TestImages.Draw(90, 90, (_, _) => (0.1, 0.2, 0.7));
        var palette = AlbumColourGrid.Extract(image);
        Assert.Equal(9, palette.Length);
        Assert.All(palette, cell => Assert.True(cell.Y > 0.42));
        Assert.All(palette, cell => Assert.True(Math.Abs(cell.X - palette[0].X) < 0.01));
        Assert.True(palette[4].Z - palette[0].Z > 0.15);
        Assert.Equal(5, new AlbumWallpaperPalette(palette).Stops.Count);
    }

    // Ported from RenderingPerformanceTests.swift: testArtworkContrastsReachBothTheWallpaperAndLyricPalette
    [Fact]
    public void ArtworkContrastsReachBothTheWallpaperAndLyricPalette()
    {
        var image = TestImages.Draw(96, 96, (x, _) => x % 8 < 4 ? (0.92, 0.04, 0.03) : (0.03, 0.90, 0.92));
        var grid = AlbumColourGrid.Extract(image);
        var cover = new AlbumWallpaperPalette(grid);
        static bool Red(Vector3 rgb) => rgb.X > 0.65 && rgb.Y < 0.2 && rgb.Z < 0.2;
        static bool Cyan(Vector3 rgb) => rgb.X < 0.2 && rgb.Y > 0.65 && rgb.Z > 0.65;
        Assert.Contains(cover.Stops, Red);
        Assert.Contains(cover.Stops, Cyan);
        Assert.Equal(grid, AlbumColourGrid.Extract(image));

        var director = new LyricInkDirector();
        var ink = LyricInk.White;
        for (var step = 0; step <= 30; step++)
            ink = director.Ink(new LyricInkInputs(LyricColourMode.AlbumDrift) { Album = cover.ArtworkStops }, step * 0.2);
        double[] hues = [ink.Stops.S0.Hue, ink.Stops.S1.Hue, ink.Stops.S2.Hue, ink.Stops.S3.Hue];
        static bool CoverRed(double hue) => hue is >= 0.3 and <= 0.7;
        static bool CoverCyan(double hue) => Math.Abs(hue) > 2.8;
        Assert.Contains(hues, CoverRed);
        Assert.Contains(hues, CoverCyan);
        Assert.All(hues, hue => Assert.True(CoverRed(hue) || CoverCyan(hue), "all stops use a hue sampled from the cover"));
    }

    // Ported from RenderingPerformanceTests.swift: testWallpaperGradientKeepsColourFromAcrossTheCover
    [Fact]
    public void WallpaperGradientKeepsColourFromAcrossTheCover()
    {
        var image = TestImages.Draw(96, 96, (x, _) =>
        {
            var rgb = ColourSpaces.Rgb(new Double3(x / 12 / 8.0, 0.92, 0.82));
            return (rgb.X, rgb.Y, rgb.Z);
        });
        var cover = new AlbumWallpaperPalette(AlbumColourGrid.Extract(image));
        Assert.Equal(5, cover.Stops.Count);
        Assert.Equal(9, cover.WallpaperStops.Length);
        var hues = cover.WallpaperStops.Select(rgb => (int)Math.Round(AlbumColourGrid.Hsb(Double3.From(rgb)).X * 8, MidpointRounding.AwayFromZero) % 8)
            .ToHashSet();
        Assert.True(hues.Count >= 7, $"{hues.Count} hues");
        Assert.Equal(cover.WallpaperStops, cover.GradientStops(1));
        var measured = cover.GradientLuminance(1);
        Assert.Equal(measured, LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.ColourOnly, false, cover.Stops, 0, measured), 0.0001);
    }

    [Fact]
    public void FallbackGridIsTheMacsRgbTriplesAsHsb()
    {
        // The literals are sRGB, converted: the first is (0.23, 0.16, 0.58).
        var first = ColourSpaces.Rgb(AlbumColourGrid.FallbackHsb[0]);
        Assert.Equal(0.23, first.X, 1e-12);
        Assert.Equal(0.16, first.Y, 1e-12);
        Assert.Equal(0.58, first.Z, 1e-12);
        Assert.Equal(AlbumColourGrid.FallbackHsb, AlbumColourGrid.Extract(null));
        Assert.Equal(AlbumColourGrid.FallbackHsb, AlbumColourGrid.Extract(ArtworkPixels.Solid(8, 8, 255, 0, 0, 0)));
    }

    [Fact]
    public void AccentIsTheMostVividSampledCell()
    {
        var grid = Enumerable.Repeat(new Double3(0.5, 0.2, 0.5), 9).ToArray();
        grid[1] = new Double3(0.1, 1, 1);  // not a sampled cell
        grid[6] = new Double3(0.3, 0.8, 0.9);
        Assert.Equal(grid[6], AlbumColourGrid.Accent(grid));
        Assert.Equal(AlbumColourGrid.FallbackHsb[2], AlbumColourGrid.Accent([]));
    }

    [Fact]
    public void PaletteInterpolatesBetweenCovers()
    {
        var from = AlbumWallpaperPalette.Fallback;
        var to = new AlbumWallpaperPalette(Enumerable.Repeat(new Double3(0, 1, 1), 9).ToArray());
        Assert.Equal(from, from.Interpolated(to, 0));
        Assert.Equal(to, from.Interpolated(to, 1));
        Assert.Equal(to, from.Interpolated(to, 7));
        var middle = from.Interpolated(to, 0.5);
        Assert.Equal((from.Stops[0] + to.Stops[0]) / 2, middle.Stops[0]);
    }
}
