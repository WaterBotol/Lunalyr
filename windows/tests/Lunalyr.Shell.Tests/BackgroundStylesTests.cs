using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>What a saved shaderStyle / patternStyle pins.</summary>
public sealed class BackgroundStylesTests
{
    [Theory]
    [InlineData("aurora", "aurora")]
    [InlineData(" lightTrails ", "lightTrails")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("Aurora", "")]
    [InlineData("sparkles", "")]
    public void AnEffectThisBuildDrawsStaysPinnedAnythingElseIsMatchTheMusic(string? saved, string expected) =>
        Assert.Equal(expected, BackgroundStyles.Effect(saved));

    [Theory]
    [InlineData("constellation", "constellation")]
    [InlineData("3", "bubbles")]
    [InlineData("17", "crosshatch")]
    [InlineData("18", "")]
    [InlineData("-1", "")]
    [InlineData("", "")]
    [InlineData("hexagons", "")]
    public void APatternReadsAsItsIdOrTheMacsRawValue(string? saved, string expected) =>
        Assert.Equal(expected, BackgroundStyles.Pattern(saved));
}
