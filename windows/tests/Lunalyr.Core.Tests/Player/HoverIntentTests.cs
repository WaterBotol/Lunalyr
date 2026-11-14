using Lunalyr.Core.Player;

namespace Lunalyr.Core.Tests.Player;

public sealed class HoverIntentTests
{
    // Ported from DesktopScrubberTests.swift: testSlowingDownRevealsOnTheSecondSlowPoll
    [Fact]
    public void SlowingDownRevealsOnTheSecondSlowPoll()
    {
        var intent = new HoverIntent();
        Assert.False(intent.Sample(0, 0, 0, inHotZone: false));
        Assert.False(intent.Sample(2, 0, 0.045, inHotZone: true)); // one slow poll is not enough
        Assert.True(intent.IsPending);
        Assert.True(intent.Sample(4, 0, 0.09, inHotZone: true));
    }

    // Ported from DesktopScrubberTests.swift: testFlyThroughNeverReveals
    [Fact]
    public void FlyThroughNeverReveals()
    {
        var intent = new HoverIntent();
        double x = 0, t = 0;
        intent.Sample(x, 0, t, inHotZone: false);
        for (var i = 0; i < 20; i++)
        {
            x += 60; // 1333 DIP/s
            t += 0.045;
            Assert.False(intent.Sample(x, 0, t, inHotZone: true));
        }

        Assert.False(intent.IsPending);
    }

    // Ported from DesktopScrubberTests.swift: testDwellRevealsAModeratePointer
    [Fact]
    public void DwellRevealsAModeratePointer()
    {
        var intent = new HoverIntent();
        double x = 0, t = 0;
        intent.Sample(x, 0, t, inHotZone: false);
        double? revealedAt = null;
        for (var i = 0; i < 10 && revealedAt is null; i++)
        {
            x += 20; // 444 DIP/s: not slow, not a fly-through
            t += 0.045;
            if (intent.Sample(x, 0, t, inHotZone: true)) revealedAt = t;
        }

        const double first = 0.045;
        Assert.Equal(first + 0.225, revealedAt ?? 0, 0.05);
    }

    // Ported from DesktopScrubberTests.swift: testLeavingTheZoneOrAFastBurstStartsOver
    [Fact]
    public void LeavingTheZoneOrAFastBurstStartsOver()
    {
        var intent = new HoverIntent();
        intent.Sample(0, 0, 0, inHotZone: true);
        Assert.False(intent.Sample(1, 0, 0.045, inHotZone: false));
        Assert.False(intent.IsPending);
        intent.Sample(2, 0, 0.09, inHotZone: true);
        Assert.False(intent.Sample(100, 0, 0.135, inHotZone: true)); // 2000 DIP/s
        Assert.False(intent.IsPending);
    }

    // Ported from DesktopScrubberTests.swift: testDecide
    [Fact]
    public void Decide()
    {
        Assert.Null(HoverPolicy.Decide(proposed: 1, previous: null, intentReady: false, holds: false)); // waits for intent
        Assert.Equal(1, HoverPolicy.Decide(proposed: 1, previous: null, intentReady: true, holds: false));
        Assert.Equal(1, HoverPolicy.Decide(proposed: 1, previous: 1, intentReady: false, holds: false)); // already shown
        Assert.Null(HoverPolicy.Decide(proposed: null, previous: 1, intentReady: false, holds: false));
        Assert.Equal(1, HoverPolicy.Decide(proposed: null, previous: 1, intentReady: false, holds: true)); // held up
        Assert.Equal(1, HoverPolicy.Decide(proposed: 2, previous: 1, intentReady: false, holds: true)); // no cross-display reveal while held
        Assert.Null(HoverPolicy.Decide(proposed: 2, previous: 1, intentReady: false, holds: false));
    }

    // Ported from DesktopScrubberTests.swift: testDismissGuards
    [Fact]
    public void DismissGuards()
    {
        Assert.True(PlayerDismissGuard.Holds(dragging: true, searchFocused: false, lastClick: null, now: 5, overWindow: true));
        Assert.False(PlayerDismissGuard.Holds(dragging: false, searchFocused: true, lastClick: 4.5, now: 5, overWindow: true)); // a window always wins outside a drag
        Assert.True(PlayerDismissGuard.Holds(dragging: false, searchFocused: true, lastClick: null, now: 5, overWindow: false));
        Assert.True(PlayerDismissGuard.Holds(dragging: false, searchFocused: false, lastClick: 4, now: 5, overWindow: false));
        Assert.False(PlayerDismissGuard.Holds(dragging: false, searchFocused: false, lastClick: 3.7, now: 5, overWindow: false));
        Assert.False(PlayerDismissGuard.Holds(dragging: false, searchFocused: false, lastClick: null, now: 5, overWindow: false));
    }

    // Ported from DesktopScrubberTests.swift: testPollIsQuickWhileShownOrPending
    [Fact]
    public void PollIsQuickWhileShownOrPending()
    {
        Assert.Equal(0.045, HoverPolicy.PollInterval(0, shown: true, pending: false));
        Assert.Equal(0.045, HoverPolicy.PollInterval(60, shown: false, pending: true));
        Assert.Equal(0.09, HoverPolicy.PollInterval(0, shown: false, pending: false));
        Assert.Equal(HoverPolicy.RestingInterval, HoverPolicy.PollInterval(60, shown: false, pending: false));
    }

    [Fact]
    public void ShouldPollOnlyWhileSomeDesktopShows()
    {
        HashSet<long> displays = [1, 2];
        Assert.True(HoverPolicy.ShouldPoll(enabled: true, systemHidden: false, hidden: new HashSet<long> { 1 }, displays));
        Assert.False(HoverPolicy.ShouldPoll(enabled: true, systemHidden: false, hidden: new HashSet<long> { 1, 2 }, displays));
        Assert.False(HoverPolicy.ShouldPoll(enabled: false, systemHidden: false, hidden: new HashSet<long>(), displays));
        Assert.False(HoverPolicy.ShouldPoll(enabled: true, systemHidden: true, hidden: new HashSet<long>(), displays));
        Assert.True(HoverPolicy.ShouldPoll(enabled: true, systemHidden: false, hidden: new HashSet<long>(), new HashSet<long>()));
    }

    [Fact]
    public void ATypedSearchKeepsThePlayerOnItsOneDisplay()
    {
        Assert.True(HoverPolicy.ShowsPlayer(1, active: 1, pinned: null, searching: false));
        Assert.False(HoverPolicy.ShowsPlayer(2, active: 1, pinned: 1, searching: true));
        Assert.True(HoverPolicy.ShowsPlayer(1, active: null, pinned: 1, searching: true));
        Assert.False(HoverPolicy.ShowsPlayer(1, active: null, pinned: 1, searching: false));
    }

    // Ported from desktop_test.c (Test Build 88): "With the card's size set, the zones hug the card." The Mac's
    // vc_hover_set_card(640, 216) is the card's size passed in; its control-panel square was 780 pt.
    [Fact]
    public void TheZonesHugTheCard()
    {
        var big = new HoverRect(0, 0, 2560, 1440);
        var hot = HoverZones.Reveal(big, 640, 216);
        var keep = HoverZones.Keep(big, 640, 216);
        Assert.Equal(640 + 2 * HoverZones.RevealMargin, hot.Width);
        Assert.Equal(216 + 2 * HoverZones.RevealMargin, hot.Height);
        Assert.Equal((2560 - hot.Width) / 2, hot.Left);
        Assert.Equal((1440 - hot.Height) / 2, hot.Top);
        Assert.Equal(640 + 2 * HoverZones.KeepMargin, keep.Width);
        Assert.True(keep.Width < 780 && keep.Height < 780);
        var tiny = new HoverRect(0, 0, 500, 300);
        Assert.Equal(500, HoverZones.Reveal(tiny, 640, 216).Width); // never past the screen
    }

    // Windows: the zones follow the card as it is now (whatever its height), at the display's scale, centred in its
    // work area wherever that sits on the desktop.
    [Fact]
    public void TheZonesFollowTheCardsHeightAndScale()
    {
        var work = new HoverRect(-1920, 40, 0, 1080); // a display left of the main one, its taskbar on top
        const double taller = PlayerCard.RestHeight + 100;
        var rest = HoverZones.Reveal(work, PlayerCard.Width, PlayerCard.RestHeight, 1.25);
        var tall = HoverZones.Reveal(work, PlayerCard.Width, taller, 1.25);
        Assert.Equal((PlayerCard.RestHeight + 2 * HoverZones.RevealMargin) * 1.25, rest.Height, 9);
        Assert.Equal(100 * 1.25, tall.Height - rest.Height, 9);
        Assert.Equal(work.CentreX, tall.CentreX, 9);
        Assert.Equal(work.CentreY, tall.CentreY, 9);
        Assert.Equal((PlayerCard.Width + 2 * HoverZones.KeepMargin) * 1.25, HoverZones.Keep(work, PlayerCard.Width, PlayerCard.RestHeight, 1.25).Width, 9);
    }

    // Windows: with the card's zones, the pointer 40 DIPs off the card's edge does not start a reveal, but keeps a
    // shown player up there; past 56 it lingers, then goes.
    [Fact]
    public void ThePointerRevealsNearTheCardAndKeepsItAFingerFurther()
    {
        var bounds = new HoverRect(0, 0, 2560, 1440);
        var display = new HoverDisplay(1, bounds, HoverZones.Reveal(bounds, 640, 216), HoverZones.Keep(bounds, 640, 216));
        var hover = new DesktopHoverSession();
        var cardLeft = (2560 - 640) / 2.0;
        Assert.Null(hover.Step([display], cardLeft - 40, 720, false, false, 0));
        Assert.Equal(1, hover.Step([display], cardLeft - 20, 720, false, false, 1));
        Assert.Equal(1, hover.Step([display], cardLeft - 40, 720, false, false, 5));
        Assert.Equal(1, hover.Step([display], cardLeft - 60, 720, false, false, 5.5));
        Assert.Null(hover.Step([display], cardLeft - 60, 720, false, false, 5.5 + HoverPolicy.Linger));
    }
}
