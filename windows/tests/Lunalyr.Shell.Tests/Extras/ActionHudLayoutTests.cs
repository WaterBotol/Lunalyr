using Lunalyr.Shell.Extras;

namespace Lunalyr.Shell.Tests.Extras;

/// <summary>Where the action HUD sits and how it comes and goes. No window is ever made.</summary>
public sealed class ActionHudLayoutTests
{
    private const double PanelWidth = ActionHudLayout.PanelWidth, PanelHeight = ActionHudLayout.PanelHeight;

    // Ported from ActionHUDTests.swift: testTheHUDSitsCentredJustUnderTheMenuBar. The Mac's frames in Windows' y-down
    // pixels: its 1512×982 screen with a 33-point menu bar and a 70-point Dock is a work area from 33 to 912; "under the
    // menu bar" is under the top of the work area (a taskbar docked at the top, or the display's edge).
    [Fact]
    public void TestTheHUDSitsCentredJustUnderTheTopOfTheWorkArea()
    {
        var screen = new HudRect(0, 0, 1512, 982);
        var work = new HudRect(0, 33, 1512, 982 - 70 - 33);
        var frame = ActionHudLayout.Frame(PanelWidth, PanelHeight, screen, work);
        Assert.Equal(screen.MidX, frame.MidX, 1);
        Assert.True(frame.Top + ActionHudLayout.Margin >= work.Top, "never over the top of the work area");
        Assert.True(frame.Top < work.Top + 40, "right under it");
        Assert.True(screen.Contains(frame));

        // A second display to the left and higher up.
        var left = new HudRect(-2560, -658, 2560, 1440);
        var leftFrame = ActionHudLayout.Frame(PanelWidth, PanelHeight, left, new HudRect(-2560, -658 + 25, 2560, 1440 - 25));
        Assert.Equal(left.MidX, leftFrame.MidX, 1);
        Assert.True(left.Contains(leftFrame));

        // The work area reaching the top (a taskbar at the bottom, or an auto-hidden one at the top).
        var hidden = ActionHudLayout.Frame(PanelWidth, PanelHeight, screen, screen);
        Assert.True(hidden.Top + ActionHudLayout.Margin >= screen.Top + 24, "clear of a taskbar popping in");

        // A narrow screen never gets a window wider than itself.
        var narrow = new HudRect(0, 0, 400, 600);
        Assert.True(ActionHudLayout.Frame(PanelWidth, PanelHeight, narrow, narrow).Width <= 400);
    }

    /// <summary>Windows' own: at 150% the points become pixels, and the gap and clearance scale with them.</summary>
    [Fact]
    public void PointsScaleWithTheDisplay()
    {
        var screen = new HudRect(0, 0, 3840, 2160);
        var frame = ActionHudLayout.Frame(PanelWidth * 1.5, PanelHeight * 1.5, screen, new HudRect(0, 0, 3840, 2112), 1.5);
        Assert.Equal(PanelWidth * 1.5, frame.Width);
        Assert.Equal((24 + 6 - 10) * 1.5, frame.Top);
        Assert.Equal(1920, frame.MidX, 1);
    }

    // Ported from ActionHUDTests.swift: testThePointersScreenIncludesItsTopRow.
    [Fact]
    public void TestThePointersScreenIncludesItsEdges()
    {
        var main = new HudRect(0, 0, 1512, 982);
        var external = new HudRect(1512, 0, 2560, 1440);
        Assert.Equal(0, ActionHudLayout.IndexOf(700, 982, [main, external])); // the pointer on the very edge row is on that screen
        Assert.Equal(1, ActionHudLayout.IndexOf(2000, 1200, [main, external]));
        Assert.Null(ActionHudLayout.IndexOf(-5, 5, [main, external]));
    }

    /// <summary>The Mac's timings: in over 0.16 s settling 6 points, held 1.4 s, out over 0.28 s, then gone.</summary>
    [Fact]
    public void ItFadesInHoldsAndFadesOut()
    {
        var motion = new ActionHudMotion();
        Assert.False(motion.IsVisible);
        Assert.True(motion.Show(10, reduceMotion: false)); // appears: place and show the window
        Assert.Equal(0, motion.Alpha(10));
        Assert.Equal(-ActionHudLayout.Settle, motion.SettleOffset(10));
        Assert.True(motion.Animating(10.05));
        Assert.Equal(1, motion.Alpha(10.16));
        Assert.Equal(0, motion.SettleOffset(10.16));
        Assert.False(motion.Animating(10.5)); // holding: nothing moves
        Assert.True(motion.Advance(11.0));
        Assert.Equal(10 + ActionHudMotion.Hold, motion.HideAt);
        Assert.True(motion.Advance(11.4)); // the fade out starts
        Assert.False(motion.IsShowing);
        Assert.InRange(motion.Alpha(11.54), 0.01, 0.99);
        Assert.False(motion.Advance(11.4 + ActionHudMotion.FadeOut)); // gone: hide the window
        Assert.False(motion.IsVisible);
        Assert.Equal(0, motion.Alpha(12));
    }

    /// <summary>A second note while one shows changes the text in place and restarts only the hold.</summary>
    [Fact]
    public void ARepeatChangesInPlaceAndRestartsTheHold()
    {
        var motion = new ActionHudMotion();
        motion.Show(0, reduceMotion: false);
        Assert.False(motion.Show(1, reduceMotion: false)); // in place
        Assert.Equal(1, motion.Alpha(1));
        Assert.Equal(1 + ActionHudMotion.Hold, motion.HideAt);
        Assert.True(motion.Advance(2)); // past the first hold, still showing
        Assert.True(motion.IsShowing);

        // While it fades out, a note turns it back from where it is, without moving it again.
        motion.Advance(2.4);
        var midway = motion.Alpha(2.5);
        Assert.False(motion.Show(2.5, reduceMotion: false));
        Assert.Equal(midway, motion.Alpha(2.5), 6);
        Assert.Equal(1, motion.Alpha(2.7));
        Assert.Equal(0, motion.SettleOffset(2.5));
    }

    /// <summary>With Windows' Animation effects off (Reduce Motion) it doesn't settle: it only fades, as on the Mac.</summary>
    [Fact]
    public void ReduceMotionOnlyFades()
    {
        var motion = new ActionHudMotion();
        motion.Show(0, reduceMotion: true);
        Assert.Equal(0, motion.SettleOffset(0));
        Assert.Equal(0, motion.SettleOffset(0.05));
        Assert.Equal(1, motion.Alpha(ActionHudMotion.FadeIn));
    }
}
