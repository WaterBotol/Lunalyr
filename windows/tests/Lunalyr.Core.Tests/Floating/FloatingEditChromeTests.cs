using Lunalyr.Core.Floating;

namespace Lunalyr.Core.Tests.Floating;

/// <summary>Edit mode's chrome lines up (the visual sweep of the smaller surfaces, docs/sweep/surfaces.md).</summary>
public class FloatingEditChromeTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void TheHintAndDoneShareTheirTopAndBottom(double scale)
    {
        var done = FloatingEditChrome.Done(900 * scale, 220 * scale, scale);
        var hint = FloatingEditChrome.HintBox(240 * scale, scale);
        Assert.Equal(done.Y, hint.Y, 6);
        Assert.Equal(done.Height, hint.Height, 6);
    }
}
