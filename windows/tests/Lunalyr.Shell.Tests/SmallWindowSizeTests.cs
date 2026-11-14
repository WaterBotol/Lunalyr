using Lunalyr.Shell.Onboarding;

namespace Lunalyr.Shell.Tests;

/// <summary>The welcome, the lyric card and Search YouTube fit their display's work area (docs/sweep/surfaces.md).</summary>
public class SmallWindowSizeTests
{
    [Theory]
    // A 1920 × 1080 display with a 48-DIP taskbar: its work area at 100, 125, 150, 175 and 200 %.
    [InlineData(1.00, 1920, 1032)]
    [InlineData(1.25, 1920, 1020)]
    [InlineData(1.50, 1920, 1008)]
    [InlineData(1.75, 1920, 996)]
    [InlineData(2.00, 1920, 984)]
    // 1366 × 768 at 100 and 125 %, 3200 × 2000 (this PC) at 125 and 200 %.
    [InlineData(1.00, 1366, 720)]
    [InlineData(1.25, 1366, 708)]
    [InlineData(1.25, 3200, 1940)]
    [InlineData(2.00, 3200, 1904)]
    public void TheWelcomeNeverReachesPastTheWorkArea(double scale, int workWidth, int workHeight)
    {
        var (width, height) = SmallWindowSize.Fit(OnboardingCopy.WindowWidth, OnboardingCopy.WindowHeight, scale, workWidth, workHeight);
        Assert.True(width <= workWidth && height <= workHeight, $"{width}×{height} in {workWidth}×{workHeight}");
        // Where it fits, it keeps its size.
        if (OnboardingCopy.WindowHeight * scale + 2 * SmallWindowSize.Margin * scale <= workHeight)
            Assert.Equal((int)Math.Round(OnboardingCopy.WindowHeight * scale), height);
    }

    [Fact]
    public void ItKeepsItsSizeWhereItFits()
    {
        Assert.Equal((850, 800), SmallWindowSize.Fit(680, 640, 1.25, 3200, 1940));
        Assert.Equal((1520, 1440), SmallWindowSize.Fit(760, 720, 2, 3200, 1904));
    }

    [Fact]
    public void ACutWindowLeavesAMarginAndANonsenseScaleCountsAsOne()
    {
        Assert.Equal((1050, 980), SmallWindowSize.Fit(600, 640, 1.75, 1920, 1008));
        Assert.Equal((680, 640), SmallWindowSize.Fit(680, 640, double.NaN, 1920, 1032));
    }
}
