using Lunalyr.Core.Colour;
using Lunalyr.Render.Backgrounds;

namespace Lunalyr.Render.Tests.Backgrounds;

/// <summary>Windows' automatic text backing shape: full where lyrics are set, feathered widely, with no edge or corner.</summary>
public class FeatheredBackingTests
{
    [Fact]
    public void FullWhereTheLyricsAreSetAndNothingInTheCorners()
    {
        Assert.Equal(1, FeatheredBacking.Strength(0, 0));
        // The middle 60% × 50% of the view, corners included.
        foreach (var (x, y) in new[] { (0.3, 0.0), (-0.3, 0.0), (0.0, 0.25), (0.0, -0.25), (0.3, 0.25), (-0.3, -0.25) })
            Assert.Equal(1, FeatheredBacking.Strength(x, y), 2);
        // Most of it at the stage's sides, less beyond.
        var (halfX, halfY) = LyricLegibility.StageHalfExtent;
        Assert.InRange(FeatheredBacking.Strength(halfX, 0), 0.9, 1);
        Assert.InRange(FeatheredBacking.Strength(0, halfY), 0.9, 1);
        Assert.True(FeatheredBacking.Strength(0.5, 0) < 0.6);
        foreach (var (x, y) in new[] { (0.5, 0.5), (-0.5, 0.5), (0.5, -0.5), (-0.5, -0.5) })
            Assert.True(FeatheredBacking.Strength(x, y) < 0.01, $"corner ({x}, {y})");
        Assert.Equal(0, FeatheredBacking.Strength(double.NaN, 0));
    }

    [Fact]
    public void FallsSmoothlyAndNeverRisesOutwards()
    {
        // Along every ray from the centre the shade only lessens, and never by a visible step.
        for (var angle = 0.0; angle < Math.PI * 2; angle += Math.PI / 24)
        {
            var previous = 1.0;
            for (var r = 0.0; r <= 0.75; r += 0.0025)
            {
                var value = FeatheredBacking.Strength(r * Math.Cos(angle), r * Math.Sin(angle));
                Assert.True(value <= previous + 1e-12, $"rises at angle {angle:0.00}, r {r:0.000}");
                Assert.True(previous - value < 0.02, $"steps by {previous - value:0.000} at angle {angle:0.00}, r {r:0.000}");
                previous = value;
            }
        }
    }

    [Fact]
    public void TheImageIsPremultipliedBlack()
    {
        var image = FeatheredBacking.Image(64, 36)!;
        Assert.Equal(64 * 36 * 4, image.Length);
        for (var i = 0; i < image.Length; i += 4)
            Assert.Equal((0, 0, 0), (image[i], image[i + 1], image[i + 2]));
        Assert.Equal(255, image[(18 * 64 + 32) * 4 + 3]);
        Assert.True(image[3] <= 2, $"corner alpha {image[3]}");
        Assert.Null(FeatheredBacking.Image(0, 9));
    }
}
