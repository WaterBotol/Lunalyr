using Lunalyr.Core.Floating;

namespace Lunalyr.Core.Tests.Floating;

/// <summary>Ported from FloatingLyricsTests.swift (the Mac's 7e2b22b9): the box and the see-through window round it.</summary>
public sealed class FloatingSpillMarginTests
{
    // Ported from FloatingLyricsTests.swift: testSpillMarginFollowsTextSize
    [Fact]
    public void SpillMarginFollowsTextSize()
    {
        Assert.Equal(160, FloatingFrame.SpillMargin(30)); // never less than 160 points
        Assert.Equal(160, FloatingFrame.SpillMargin(44));
        Assert.Equal(165, FloatingFrame.SpillMargin(110)); // 1.5 times a large text size
        Assert.Equal(160, FloatingFrame.SpillMargin(double.NaN));
        for (var size = 22.0; size <= 110; size += 0.7)
        {
            var margin = FloatingFrame.SpillMargin(size);
            Assert.True(margin >= size * 1.5);
            Assert.Equal(Math.Round(margin), margin); // whole points, so the box stays on whole points
        }
    }

    // Ported from FloatingLyricsTests.swift: testWindowGoesRoundTheBoxAndBack
    [Fact]
    public void WindowGoesRoundTheBoxAndBack()
    {
        var box = new FloatingRect(412, 94, 688, 202);
        var window = FloatingFrame.Window(box, 160);
        Assert.Equal(new FloatingRect(252, -66, 1008, 522), window); // 160 points more on every side
        Assert.Equal(box.X + box.Width / 2, window.X + window.Width / 2);
        Assert.Equal(box.Y + box.Height / 2, window.Y + window.Height / 2);
        Assert.Equal(box, FloatingFrame.Box(window, 160));
        // Off the screen's edge is fine; the conversion does not clamp.
        var corner = new FloatingRect(0, 0, 200, 80);
        Assert.Equal(corner, FloatingFrame.Box(FloatingFrame.Window(corner, 165), 165));
        // No margin: the window is the box.
        Assert.Equal(box, FloatingFrame.Window(box, 0));
    }
}
