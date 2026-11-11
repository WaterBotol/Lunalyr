using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>
/// The palette and colour-field maths pinned by the Mac's WallpaperEnergyTests and WallpaperSurfaceMotionTests. The rest of
/// those suites tests the Mac's views, Metal path and BackgroundCrossfade/BackgroundChoiceHold (layer switching and
/// song-change holds, which belong to the Windows render engine, not to a layer).
/// </summary>
public class WallpaperPaletteTests
{
    // Ported from WallpaperEnergyTests.swift: testColourFieldStaysWithinItsPixelBudgetOnLargeDisplays
    [Fact]
    public void ColourFieldStaysWithinItsPixelBudgetOnLargeDisplays()
    {
        const double budget = ColourFieldBudget.Standard;
        // A 14-inch MacBook Pro (5.9 MP), a 5K (14.7 MP) and a 6K (20.4 MP) display.
        foreach (var (width, height) in new[] { (1512.0, 982.0), (2560.0, 1440.0), (3008.0, 1692.0) })
        {
            var canvas = ColourFieldBudget.Canvas(width, height, 2, budget);
            Assert.True(canvas.PixelsAt(2) <= budget, $"{width}x{height}");
            Assert.True(canvas.PixelsAt(2) > budget * 0.99, $"{width}x{height}: spends what it may");
            Assert.Equal(0, canvas.Width * 2 % 1);
            Assert.Equal(0, canvas.Height * 2 % 1);
            // Stretched to cover the display, one canvas pixel past each edge.
            Assert.Equal(width + 2 * width / (canvas.Width * 2), canvas.Width * canvas.StretchX, 0.001);
            Assert.True(canvas.Height * canvas.StretchY > height);
            Assert.Equal(1, canvas.StretchX / canvas.StretchY, 0.01);
            var saving = ColourFieldBudget.Canvas(width, height, 2, ColourFieldBudget.Pixels(economical: true));
            Assert.True(saving.PixelsAt(2) <= budget * 0.6);
        }
        // A small surface (the Settings preview) and an unlimited budget draw natively, unstretched.
        Assert.Equal(new ColourFieldBudget.CanvasSize(256, 144, 1, 1), ColourFieldBudget.Canvas(256, 144, 2, budget));
        var unlimited = ColourFieldBudget.Canvas(1512, 982, 2, double.PositiveInfinity);
        Assert.Equal((1.0, 1.0), (unlimited.StretchX, unlimited.StretchY));
        Assert.Equal(1, ColourFieldBudget.Factor(0, 0, 2, budget));
    }

    [Fact]
    public void TheLayerCanvasIsInPhysicalPixels()
    {
        // A 4K surface at 200% is the 5K case's 1920 x 1080 points: the same quarter-megapixel canvas.
        var (w, h) = ColourField.CanvasSize(3840, 2160, 2);
        Assert.True(w * h <= ColourFieldBudget.Standard);
        Assert.True(w * h > ColourFieldBudget.Standard * 0.98);
        Assert.Equal(3840.0 / 2160, (double)w / h, 0.01);
        Assert.Equal((320, 180), ColourField.CanvasSize(320, 180, 1));
        // A degenerate surface still gets a usable canvas.
        var (dw, dh) = ColourField.CanvasSize(0, 100, 1);
        Assert.True(dw >= 4 && dh >= 4);
        var (nw, nh) = ColourField.CanvasSize(double.NaN, 100, double.NaN);
        Assert.True(nw >= 4 && nh >= 4);
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testCoverLuminanceFromTheColoursOnScreenMatchesThePalette
    [Fact]
    public void CoverLuminanceFromTheColoursOnScreenMatchesThePalette()
    {
        IReadOnlyList<Double3>[] grids =
        [
            AlbumColourGrid.FallbackHsb,
            Enumerable.Range(0, 9).Select(i => new Double3(i / 9.0, 0.8, 0.3 + i * 0.07)).ToArray(),
        ];
        foreach (var grid in grids)
            Assert.Equal(LyricLegibility.CoverLuminance(grid), LyricLegibility.CoverLuminance(new AlbumWallpaperPalette(grid)), 1e-4);
    }

    [Fact]
    public void AHandSetBackingKeepsItsEllipse()
    {
        var half = LyricLegibility.StageHalfExtent;
        Assert.Equal(1, LyricLegibility.ManualBackingStrength(0, 0));
        // 0.9 at 55% of the ellipse's radius, 0.45 at 80%, none at its rim and beyond.
        var rx = half.X * 1.3;
        Assert.Equal(0.9, LyricLegibility.ManualBackingStrength(rx * 0.55, 0), 1e-9);
        Assert.Equal(0.45, LyricLegibility.ManualBackingStrength(rx * 0.8, 0), 1e-9);
        Assert.Equal(0, LyricLegibility.ManualBackingStrength(rx, 0), 1e-9);
        Assert.Equal(0, LyricLegibility.ManualBackingStrength(0.5, 0.5));
        // Lighter at the stage's ends than in its middle; nothing at its corners.
        Assert.True(LyricLegibility.ManualBackingStrength(half.X, 0) < 0.75);
        Assert.True(LyricLegibility.ManualBackingStrength(half.X, half.Y) < 0.03);
        Assert.Null(LyricLegibility.ManualShadeImage(0, 3));
    }
}
