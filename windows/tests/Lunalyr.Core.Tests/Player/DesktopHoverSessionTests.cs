using Lunalyr.Core.Player;

namespace Lunalyr.Core.Tests.Player;

/// <summary>
/// The hover step, written for this port from a description of the Mac's behaviour (its own code and tests carry Verso
/// lines, so neither is translated): one display at a time, a linger on the same display, nothing through a window or
/// under a held button, no hand-off mid-press.
/// </summary>
public sealed class DesktopHoverSessionTests
{
    // A laptop at the origin and a monitor to its right; another to its left with negative coordinates.
    private static readonly HoverDisplay Laptop = Display(101, new HoverRect(0, 0, 1440, 900));
    private static readonly HoverDisplay Right = Display(202, new HoverRect(1440, 0, 4000, 1440));
    private static readonly HoverDisplay Left = Display(303, new HoverRect(-1920, 0, 0, 1080));
    private static readonly HoverDisplay[] Screens = [Laptop, Right, Left];

    private static HoverDisplay Display(long id, HoverRect bounds)
    {
        var hot = bounds.Centred(800, 400);
        return new HoverDisplay(id, bounds, hot, bounds.Centred(700, 300));
    }

    private static (double X, double Y) Centre(HoverDisplay d) => (d.Bounds.CentreX, d.Bounds.CentreY);

    [Fact]
    public void FindsTheDisplayUnderThePointerOnce()
    {
        Assert.Equal(0, DesktopHoverSession.DisplayAt(Screens, 720, 450));
        Assert.Equal(1, DesktopHoverSession.DisplayAt(Screens, 1440, 100)); // a shared edge belongs to one display
        Assert.Equal(2, DesktopHoverSession.DisplayAt(Screens, -960, 540));
        Assert.Equal(-1, DesktopHoverSession.DisplayAt(Screens, 5000, 5000));
        Assert.Equal(-1, DesktopHoverSession.DisplayAt(Screens, double.NaN, 200));
    }

    [Fact]
    public void ShowsOnOneDisplayAndGoesAtOnceElsewhereOrOverAWindow()
    {
        var hover = new DesktopHoverSession();
        var (lx, ly) = Centre(Laptop);
        var (rx, ry) = Centre(Right);
        Assert.Equal(101, hover.Step(Screens, lx, ly, blocked: false, held: false, now: 10));
        Assert.Equal(202, hover.Step(Screens, rx, ry, false, false, 10.01)); // straight to the other display's middle
        Assert.Null(hover.Step(Screens, lx, ly, blocked: true, false, 10.02)); // a window covers the pointer
        Assert.Equal(101, hover.Step(Screens, lx, ly, false, false, 11));
        Assert.Null(hover.Step(Screens, 1450, 100, false, false, 11.01)); // another display's corner: no linger
        Assert.Equal(303, hover.Step(Screens, Centre(Left).X, Centre(Left).Y, false, false, 12));
        Assert.Null(hover.Step(Screens, lx, ly, false, false, double.NaN));
    }

    [Fact]
    public void LingersOnTheSameDisplayUnlessAWindowTakesThePointer()
    {
        var hover = new DesktopHoverSession();
        var (lx, ly) = Centre(Laptop);
        Assert.Equal(101, hover.Step(Screens, lx, ly, false, false, 12));
        Assert.Equal(101, hover.Step(Screens, 50, 50, false, false, 12.3)); // left the zone: lingers
        Assert.Null(hover.Step(Screens, 50, 50, false, false, 12.71)); // 0.7 s after it was last inside
        Assert.Equal(101, hover.Step(Screens, lx, ly, false, false, 13));
        Assert.Null(hover.Step(Screens, 50, 50, blocked: true, false, 13.01)); // a window cancels the linger
    }

    [Fact]
    public void TheKeepZoneHoldsThePlayerPastASmallHotZone()
    {
        // On a short screen the card reaches past the hot zone: it stays usable there without lingering out.
        var bounds = new HoverRect(0, 0, 800, 600);
        var small = new HoverDisplay(606, bounds, bounds.Centred(300, 100), bounds.Centred(700, 400));
        var hover = new DesktopHoverSession();
        Assert.Equal(606, hover.Step([small], 400, 300, false, false, 20));
        Assert.Equal(606, hover.Step([small], 400, 160, false, false, 21));
        Assert.Equal(606, hover.Step([small], 400, 160, false, false, 25));
        Assert.Null(hover.Step([small], 10, 10, false, false, 25.8));
    }

    [Fact]
    public void AHeldButtonNeverStartsARevealOrCarriesItAcross()
    {
        var hover = new DesktopHoverSession();
        var (lx, ly) = Centre(Laptop);
        var (rx, ry) = Centre(Right);
        Assert.Null(hover.Step(Screens, lx, ly, false, held: true, 30)); // rubber-band selection, file drags
        Assert.Null(hover.Active);
        Assert.Equal(101, hover.Step(Screens, lx, ly, false, held: false, 30.1));
        Assert.Equal(101, hover.Step(Screens, lx, ly, false, held: true, 30.2)); // a shown player survives a press
        Assert.Equal(101, hover.Step(Screens, 50, 50, false, held: true, 30.4)); // and lingers while held
        Assert.Null(hover.Step(Screens, rx, ry, false, held: true, 30.5)); // no hand-off mid-drag
        Assert.Null(hover.Step(Screens, rx, ry, false, held: true, 30.6));
        Assert.Equal(202, hover.Step(Screens, rx, ry, false, held: false, 30.7));
        Assert.Null(hover.Step(Screens, rx, ry, blocked: true, held: true, 30.8)); // a window still wins
    }

    [Fact]
    public void AnUnpluggedDisplayLetsThePlayerGo()
    {
        var hover = new DesktopHoverSession();
        Assert.Equal(202, hover.Step(Screens, Centre(Right).X, Centre(Right).Y, false, false, 15));
        Assert.Null(hover.Step([Laptop], Centre(Right).X, Centre(Right).Y, false, false, 15.01));
    }

    [Fact]
    public void AGuardHoldRestartsTheLinger()
    {
        var hover = new DesktopHoverSession();
        Assert.Equal(101, hover.Step(Screens, Centre(Laptop).X, Centre(Laptop).Y, false, false, 40));
        hover.Hold(101, 45); // a drag kept it up until 45
        Assert.Equal(101, hover.Step(Screens, 50, 50, false, false, 45.5));
        Assert.Null(hover.Step(Screens, 50, 50, false, false, 45.8));
    }
}
