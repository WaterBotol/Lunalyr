using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>What a saved shaderStyle / patternStyle reads as.</summary>
public sealed class BackgroundStylesTests
{
    [Theory]
    [InlineData("aurora", "aurora")]
    [InlineData(" lightTrails ", "lightTrails")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ASavedEffectIsKeptTrimmed(string? saved, string expected) =>
        Assert.Equal(expected, BackgroundStyles.Effect(saved));

    [Theory]
    [InlineData("constellation", "constellation")]
    [InlineData("3", "bubbles")]
    [InlineData(" 17 ", "crosshatch")]
    [InlineData("18", "18")]
    [InlineData("", "")]
    [InlineData("hexagons", "hexagons")]
    public void APatternSavedAsTheMacsRawValueReadsAsItsId(string? saved, string expected) =>
        Assert.Equal(expected, BackgroundStyles.Pattern(saved));

    [Fact]
    public void SettingsReadAMacRawValueAsThePattern() =>
        Assert.Equal("bubbles", new AppSettings { PatternStyle = "3" }.Normalized().PatternStyle);
}
