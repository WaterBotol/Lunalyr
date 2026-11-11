using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>
/// The colour half of the Mac's LyricLegibilityTests. Not ported: picture measurement and thumbnails
/// (testImageLuminanceReadsTheRegionBehindTheLyrics, testDecodesASmallCopyOfAPictureOnDisk: imported pictures and the
/// desktop picture are another workstream), the stage presence and notification tests (lyric stage workstream), and
/// testTheDrawnBackingFollowsTheShape… (a SwiftUI render; the Windows layer's preview tool draws the same shade).
/// </summary>
public class LyricLegibilityTests
{
    /// <summary>A palette whose every stop is one colour, as a plain sleeve gives.</summary>
    private static AlbumWallpaperPalette Sleeve(Double3 rgb) =>
        new(Enumerable.Repeat(AlbumColourGrid.Hsb(rgb), 9).ToArray());

    /// <summary>White type over a backdrop after the shared scrim and the backing.</summary>
    private static double Contrast(double luminance, double existing, double backing)
    {
        var after = 1 - (1 - existing) * (1 - backing);
        return LyricLegibility.WhiteContrast(LyricLegibility.Darkened(luminance, after));
    }

    // Ported from LyricLegibilityTests.swift: testContrastMathMatchesWCAG
    [Fact]
    public void ContrastMathMatchesWCAG()
    {
        Assert.Equal(21, LyricLegibility.WhiteContrast(0), 0.001);
        Assert.Equal(1, LyricLegibility.WhiteContrast(1), 0.001);
        Assert.Equal(1, LyricLegibility.Luminance(new Double3(1, 1, 1)), 0.0001);
        Assert.Equal(0.2140, LyricLegibility.Luminance(new Double3(0.5, 0.5, 0.5)), 0.001);
        foreach (var luminance in new[] { 0.0, 0.05, 0.2, 0.6, 1.0 })
            Assert.Equal(luminance, LyricLegibility.Linear(LyricLegibility.Encoded(luminance)), 0.0005);
        Assert.Equal(4.5, LyricLegibility.TargetContrast(70));
        Assert.Equal(3, LyricLegibility.TargetContrast(118));
    }

    // Ported from LyricLegibilityTests.swift: testPaleCoversGetJustEnoughBackingForReadableWhiteType
    [Fact]
    public void PaleCoversGetJustEnoughBackingForReadableWhiteType()
    {
        const double scrim = 0.12;
        foreach (var rgb in new[] { new Double3(0.98, 0.98, 0.97), new Double3(1.0, 0.96, 0.62), new Double3(0.72, 0.95, 0.22) })
        {
            var luminance = LyricLegibility.ColourFieldLuminance(Sleeve(rgb), 0.85);
            Assert.True(Contrast(luminance, scrim, 0) < 3, "unreadable before");
            var backing = LyricLegibility.Backing(luminance, scrim, 4.5);
            Assert.True(backing > 0);
            Assert.True(backing <= LyricLegibility.MaximumBacking);
            Assert.True(Contrast(luminance, scrim, backing) >= 4.5 - 0.01);
            // Never more than needed: a little less would miss the target.
            Assert.True(Contrast(luminance, scrim, Math.Max(0, backing - 0.03)) < 4.5);
        }
    }

    // Ported from LyricLegibilityTests.swift: testDarkCoversAreLeftAlone
    [Fact]
    public void DarkCoversAreLeftAlone()
    {
        foreach (var rgb in new[] { new Double3(0.08, 0.06, 0.2), new Double3(0.25, 0.1, 0.3), new Double3(0.1, 0.2, 0.25) })
        {
            var luminance = LyricLegibility.ColourFieldLuminance(Sleeve(rgb), null);
            Assert.Equal(0, LyricLegibility.Backing(luminance, 0.12, 4.5));
        }
        // The fallback palette used before artwork arrives needs nothing either.
        var fallback = AlbumWallpaperPalette.Fallback;
        Assert.Equal(0, LyricLegibility.Backing(LyricLegibility.ColourFieldLuminance(fallback, 0.85), 0.12, 4.5));
    }

    // Ported from LyricLegibilityTests.swift: testBackingIsCappedAndCreditsExistingDarkening
    [Fact]
    public void BackingIsCappedAndCreditsExistingDarkening()
    {
        const double white = 1.0;
        Assert.Equal(LyricLegibility.MaximumBacking, LyricLegibility.Backing(white, 0, 15));
        Assert.Equal(LyricLegibility.MaximumBacking, LyricLegibility.Backing(white, 0, 30));
        var light = LyricLegibility.Backing(white, 0, 4.5);
        var darker = LyricLegibility.Backing(white, 0.3, 4.5);
        Assert.True(darker < light);
        // An animated cover's own heavy darkening already suffices.
        Assert.Equal(0, LyricLegibility.Backing(white, 0.7, 4.5));
        // Large type needs less.
        Assert.True(LyricLegibility.Backing(white, 0, 3) < light);
        Assert.Equal(0, LyricLegibility.Backing(double.NaN, 0, 4.5));
    }

    // Ported from LyricLegibilityTests.swift: testShaderAndCoverEstimatesFollowTheirSources
    [Fact]
    public void ShaderAndCoverEstimatesFollowTheirSources()
    {
        var pale = Sleeve(new Double3(0.95, 0.95, 0.9));
        Assert.True(LyricLegibility.ShaderLuminance(pale) > 0.7);
        Assert.True(LyricLegibility.VisualizerLuminance(pale) > 0.6);
        var grid = Enumerable.Repeat(new Double3(0.15, 0.1, 0.95), 9).ToArray();
        Assert.True(LyricLegibility.CoverLuminance(grid) > 0.7);
        Assert.Equal(1, LyricLegibility.Rgb(new Double3(0, 1, 1)).X, 0.0001);
        Assert.Equal(1, LyricLegibility.Rgb(new Double3(1.0 / 3, 1, 1)).Y, 0.0001);
    }

    /// <summary>White type at a point given in stage sizes from its centre: (0.5, 0) is the middle of the stage's right edge.</summary>
    private static double ContrastOnStage(double x, double y, double luminance, double scrim, double amount, double opacity = 1)
    {
        var half = LyricLegibility.StageHalfExtent;
        var strength = LyricLegibility.BackingStrength(x * 2 * half.X, y * 2 * half.Y);
        var after = 1 - (1 - scrim) * (1 - amount * strength);
        return LyricLegibility.ContrastOfWhite(opacity, LyricLegibility.Darkened(luminance, after));
    }

    // Ported from LyricLegibilityTests.swift: testTheBackingReachesItsTargetAcrossTheWholeStage
    [Fact]
    public void TheBackingReachesItsTargetAcrossTheWholeStage()
    {
        var amount = LyricLegibility.StageBacking(1, 0.12, 4.5);
        Assert.True(amount <= LyricLegibility.MaximumBacking);
        for (var i = 0; i <= 20; i++)
        for (var j = 0; j <= 20; j++)
        {
            double x = -0.5 + i * 0.05, y = -0.5 + j * 0.05;
            Assert.True(ContrastOnStage(x, y, 1, 0.12, amount) >= 4.5 - 0.01, $"at ({x}, {y}) of the stage");
        }
    }

    // Ported from LyricLegibilityTests.swift: testTheBackingEasesOutByTheScreenEdgesInsteadOfSpillingPastThem
    [Fact]
    public void TheBackingEasesOutByTheScreenEdgesInsteadOfSpillingPastThem()
    {
        static double Strength(double x, double y) => LyricLegibility.BackingStrength(x, y);
        for (var step = 0; step <= 20; step++)
        {
            var along = step / 20.0 - 0.5;
            foreach (var (px, py) in new[] { (along, -0.5), (along, 0.5), (-0.5, along), (0.5, along) })
                Assert.Equal(0, Strength(px, py));
        }
        var half = LyricLegibility.StageHalfExtent;
        Assert.Equal(0.5, Strength((half.X + 0.5) / 2, 0), 1e-9);
        Assert.Equal(0.5, Strength(0, -(half.Y + 0.5) / 2), 1e-9);
        foreach (var (dx, dy) in new[] { (1.0, 0.0), (0.0, 1.0), (1.0, 1.0), (-0.8, 0.6) })
        {
            var last = 1.0;
            for (var step = 0; step <= 100; step++)
            {
                var t = step / 200.0;
                var value = Strength(dx * t, dy * t);
                Assert.True(value <= last + 1e-12);
                last = value;
            }
        }
        double total = 0;
        var strong = 0;
        const int samples = 200;
        for (var column = 0; column < samples; column++)
        for (var row = 0; row < samples; row++)
        {
            var value = Strength((column + 0.5) / samples - 0.5, (row + 0.5) / samples - 0.5);
            total += value;
            if (value >= 0.75) strong += 1;
        }
        var stageShare = 4 * half.X * half.Y;
        var strongShare = (double)strong / (samples * samples);
        Assert.True(strongShare >= stageShare - 0.01, "the whole stage");
        Assert.True(strongShare < stageShare + 0.15);
        Assert.True(total / (samples * samples) < 0.8);
    }

    // Ported from LyricLegibilityTests.swift: testTheShadeImageIsThatShape (the image as bytes; the layer uploads it)
    [Fact]
    public void TheShadeImageIsThatShape()
    {
        const int columns = LyricLegibility.ShadeColumns, rows = LyricLegibility.ShadeRows;
        var data = LyricLegibility.ShadeImage(columns, rows);
        Assert.NotNull(data);
        foreach (var (column, row) in new[] { (0, 0), (columns / 2, rows / 2), (columns / 10, rows / 2), (columns / 20, rows / 2),
                                              (columns - 1, rows / 2), (columns / 2, rows / 14), (columns / 10, rows / 7), (columns - 1, rows - 1) })
        {
            var offset = (row * columns + column) * 4;
            double x = (column + 0.5) / columns - 0.5, y = (row + 0.5) / rows - 0.5;
            Assert.Equal(LyricLegibility.BackingStrength(x, y), data[offset + 3] / 255.0, 0.5 / 255 + 1e-9);
            Assert.Equal(0, data[offset]);
        }
        Assert.Null(LyricLegibility.ShadeImage(0, 4));
    }

    // Ported from LyricLegibilityTests.swift: testReadingsAndTranslationsAreCountedToo
    [Fact]
    public void ReadingsAndTranslationsAreCountedToo()
    {
        foreach (var luminance in new[] { 0.0, 0.1, 0.4, 1.0 })
            Assert.Equal(LyricLegibility.WhiteContrast(luminance), LyricLegibility.ContrastOfWhite(1, luminance), 1e-9);
        Assert.True(LyricLegibility.ContrastOfWhite(0.7, 0.2) < LyricLegibility.WhiteContrast(0.2));
        var ceiling = LyricLegibility.SecondaryTextCeiling;
        Assert.True(ceiling > 0);
        Assert.Equal(3, LyricLegibility.ContrastOfWhite(0.7, ceiling), 0.001);

        const double pale = 1.0, scrim = 0.12;
        var ordinary = LyricLegibility.StageBacking(pale, scrim, 4.5);
        Assert.Equal(LyricLegibility.Backing(pale, scrim, 4.5), ordinary, 1e-9);
        Assert.True(ContrastOnStage(0.5, 0, pale, scrim, ordinary, 0.7) >= 3 - 0.01);
        var large = LyricLegibility.StageBacking(pale, scrim, 3);
        var whiteOnly = LyricLegibility.Backing(pale, scrim, 3);
        Assert.True(ContrastOnStage(0, 0, pale, scrim, whiteOnly, 0.7) < 2.5);
        Assert.True(large > whiteOnly);
        Assert.True(large <= LyricLegibility.MaximumBacking);
        Assert.True(ContrastOnStage(0, 0.5, pale, scrim, large, 0.7) >= 3 - 0.01);
        Assert.Equal(0, LyricLegibility.StageBacking(0.02, scrim, 3));
        Assert.Equal(0, LyricLegibility.StageBacking(double.NaN, scrim, 4.5));
    }
}
