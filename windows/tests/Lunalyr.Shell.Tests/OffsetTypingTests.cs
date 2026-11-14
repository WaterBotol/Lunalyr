using Lunalyr.Shell.Flyout;

namespace Lunalyr.Shell.Tests;

/// <summary>Typing this song's offset (the owner, 15 Nov 2026: "+X / −X", in smaller steps).</summary>
public class OffsetTypingTests
{
    [Theory]
    [InlineData("+0.35", 0.35)]
    [InlineData("0.35", 0.35)]
    [InlineData("−1.2", -1.2)]
    [InlineData("-0.4 s", -0.4)]
    [InlineData("350ms", 0.35)]
    [InlineData("-0,25", -0.25)]
    [InlineData("+2sec", 2)]
    [InlineData("0.123", 0.12)]
    public void TypedOffsetsAreRead(string typed, double seconds) => Assert.Equal(seconds, FlyoutText.ParseOffset(typed));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("+45")]
    [InlineData("1.2.3")]
    public void AnythingElseIsNot(string typed) => Assert.Null(FlyoutText.ParseOffset(typed));

    [Fact]
    public void TheButtonsStepInTwentiethsOfASecond() => Assert.Equal(0.05, AppController.NudgeStep);
}
