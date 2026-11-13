using Lunalyr.Core.Floating;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Floating;

/// <summary>Windows' side of the floating window's placement: pixels, the taskbar on any edge, and the edit chrome.</summary>
public class FloatingDisplayTests
{
    // This PC's two displays: 2560×1440 at 125% and 3200×2000 at 200%, each with a 48-point taskbar.
    private static FloatingDisplay External(EdgeInsets autoHide = default) =>
        new(new Box(0, 0, 2560, 1440), new Box(0, 0, 2560, 1440 - 60), 1.25, autoHide);

    private static FloatingDisplay Laptop => new(new Box(2560, 0, 3200, 2000), new Box(2560, 0, 3200, 2000 - 96), 2);

    [Fact]
    public void BottomSitsAboveTheTaskbarWithTheMargin()
    {
        foreach (var display in new[] { External(), Laptop })
        {
            var window = display.Place(FloatingPlacement.Bottom, null, null, 44);
            // Within a pixel: a frame in points lands on whole pixels.
            Assert.InRange(window.Y + window.Height - (display.WorkArea.Y + display.WorkArea.Height - FloatingFrame.Margin * display.Scale), -1, 1);
            Assert.InRange(window.X + window.Width / 2 - (display.Bounds.X + display.Bounds.Width / 2), -1, 1);
            // 44 points: 836 × 202 points, at the display's scale.
            Assert.InRange(window.Width - 836 * display.Scale, -1, 1);
            Assert.InRange(window.Height - 202 * display.Scale, -1, 1);
        }
    }

    [Theory]
    [InlineData("top")]
    [InlineData("left")]
    [InlineData("right")]
    public void EveryPresetAvoidsATaskbarOnAnyEdge(string edge)
    {
        var bounds = new Box(0, 0, 2560, 1440);
        var work = edge switch
        {
            "top" => new Box(0, 60, 2560, 1380),
            "left" => new Box(60, 0, 2500, 1440),
            _ => new Box(0, 0, 2500, 1440),
        };
        var display = new FloatingDisplay(bounds, work, 1.25);
        foreach (var placement in FloatingPlacements.Presets)
        {
            var w = display.Place(placement, null, null, 44);
            Assert.True(w.X >= work.X && w.Y >= work.Y && w.X + w.Width <= work.X + work.Width && w.Y + w.Height <= work.Y + work.Height,
                $"{placement} on a {edge} taskbar: {w}");
        }
    }

    [Fact]
    public void AnAutoHiddenTaskbarIsAvoidedToo()
    {
        var hidden = new FloatingDisplay(new Box(0, 0, 2560, 1440), new Box(0, 0, 2560, 1440), 1.25, new EdgeInsets(0, 0, 0, 60));
        var window = hidden.Place(FloatingPlacement.Bottom, null, null, 44);
        Assert.InRange(window.Y + window.Height - (1440 - 60 - 30), -1, 1);
    }

    [Fact]
    public void PixelsAndPointsRoundTrip()
    {
        var display = Laptop;
        var frame = new FloatingRect(100, 200, 500, 150);
        var pixels = display.ToPixels(frame);
        Assert.Equal(new Box(2760, 2000 - 700, 1000, 300), pixels);
        Assert.Equal(frame, display.ToPoints(pixels));
        Assert.Equal((5.0, -10.0), display.Delta(10, 20)); // pointer down is y up's minus
    }

    [Fact]
    public void ACustomFrameStaysWithItsDisplay()
    {
        var display = Laptop;
        var window = display.Place(FloatingPlacement.Custom, new FloatingRect(100, 200, 500, 150), null, 44);
        Assert.Equal(2560 + 200, window.X);
    }

    [Fact]
    public void EditChromeHitsDoneTheGripAndTheRest()
    {
        const double w = 1045, h = 252, s = 1.25;
        var done = FloatingEditChrome.Done(w, h, s);
        Assert.Equal(FloatingHit.Done, FloatingEditChrome.HitTest(done.X + 2, done.Y + 2, w, h, s));
        Assert.Equal(FloatingHit.Resize, FloatingEditChrome.HitTest(w - 2, h - 2, w, h, s));
        Assert.Equal(FloatingHit.Resize, FloatingEditChrome.HitTest(w - (8 + 26 + 4) * s, h - 20 * s, w, h, s)); // a little beyond the circle
        Assert.Equal(FloatingHit.Move, FloatingEditChrome.HitTest(w / 2, h / 2, w, h, s));
        Assert.True(done.X + done.Width <= w && done.Y >= 0);
    }
}
