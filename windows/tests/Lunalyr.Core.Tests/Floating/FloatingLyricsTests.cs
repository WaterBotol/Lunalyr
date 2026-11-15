using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Floating;

namespace Lunalyr.Core.Tests.Floating;

/// <summary>
/// Floating lyrics' frame maths, when the window shows and how dark its backdrop reads: ported from the Mac's
/// FloatingLyricsTests.swift with its inputs (points, y up, the Mac's own convention, which FloatingFrame keeps).
/// </summary>
public class FloatingLyricsTests
{
    // A 1512 by 982 laptop screen with a notch: a 38-point menu bar and a 70-point Dock at the bottom.
    private static readonly FloatingRect Screen = new(0, 0, 1512, 982);
    private static readonly FloatingRect Visible = new(0, 70, 1512, 982 - 70 - 38);

    // ---- Frame maths ----

    // Ported from FloatingLyricsTests.swift: testUsableAreaKeepsClearOfMenuBarDockAndNotch
    [Fact]
    public void UsableAreaKeepsClearOfMenuBarDockAndNotch()
    {
        var area = FloatingFrame.UsableArea(Screen, Visible, 32);
        Assert.Equal(Visible, area);
        // Menu bar hidden: the visible frame reaches the top, the notch does not.
        var hidden = FloatingFrame.UsableArea(Screen, new FloatingRect(0, 70, 1512, 912), 32);
        Assert.Equal(982 - 32, hidden.MaxY);
        Assert.Equal(70, hidden.MinY);
    }

    // Ported from FloatingLyricsTests.swift: testPresetsSitInsideTheAreaWithMargins
    [Fact]
    public void PresetsSitInsideTheAreaWithMargins()
    {
        var area = FloatingFrame.UsableArea(Screen, Visible, 32);
        var size = new SizeD(800, 200);
        const double m = FloatingFrame.Margin;
        foreach (var placement in FloatingPlacements.Presets)
        {
            var frame = FloatingFrame.Preset(placement, size, area);
            Assert.True(area.InsetBy(m - 0.5, m - 0.5).Contains(frame) || placement == FloatingPlacement.Centre, placement.ToString());
            Assert.Equal(size, frame.Size);
        }
        Assert.Equal(area.MinY + m, FloatingFrame.Preset(FloatingPlacement.Bottom, size, area).MinY); // just above the Dock
        Assert.Equal(area.MaxY - m, FloatingFrame.Preset(FloatingPlacement.Top, size, area).MaxY); // just below the menu bar
        Assert.Equal(area.MidX, FloatingFrame.Preset(FloatingPlacement.Bottom, size, area).MidX);
        Assert.Equal(area.MinX + m, FloatingFrame.Preset(FloatingPlacement.TopLeft, size, area).MinX);
        Assert.Equal(area.MaxX - m, FloatingFrame.Preset(FloatingPlacement.BottomRight, size, area).MaxX);
        Assert.Equal(area.MidY, FloatingFrame.Preset(FloatingPlacement.Centre, size, area).MidY);
    }

    // Ported from FloatingLyricsTests.swift: testPresetsFollowADockOnTheSide (on Windows: a taskbar on the left)
    [Fact]
    public void PresetsFollowADockOnTheSide()
    {
        // Dock on the left: the visible frame starts after it.
        var left = new FloatingRect(80, 0, 1432, 944);
        var area = FloatingFrame.UsableArea(Screen, left, 0);
        Assert.Equal(80 + FloatingFrame.Margin, FloatingFrame.Preset(FloatingPlacement.BottomLeft, new SizeD(400, 120), area).MinX);
    }

    // Ported from FloatingLyricsTests.swift: testOversizedWindowsShrinkToTheArea
    [Fact]
    public void OversizedWindowsShrinkToTheArea()
    {
        var area = new FloatingRect(0, 0, 600, 300);
        var frame = FloatingFrame.Preset(FloatingPlacement.Bottom, new SizeD(2000, 900), area);
        Assert.Equal(new SizeD(600, 300), frame.Size);
        Assert.True(area.Contains(frame));
        Assert.Equal(FloatingFrame.MinimumSize, FloatingFrame.Fitted(new SizeD(10, 10), area));
    }

    // Ported from FloatingLyricsTests.swift: testDefaultSizeFollowsTextSize
    [Fact]
    public void DefaultSizeFollowsTextSize()
    {
        var area = new FloatingRect(0, 0, 1512, 874);
        var small = FloatingFrame.DefaultSize(30, area);
        var large = FloatingFrame.DefaultSize(64, area);
        Assert.True(small.Width < large.Width);
        Assert.True(small.Height < large.Height);
        Assert.True(large.Width <= area.Width - 2 * FloatingFrame.Margin);
        var tiny = new FloatingRect(0, 0, 300, 200);
        Assert.True(FloatingFrame.DefaultSize(110, tiny).Width <= 300);
    }

    // Ported from FloatingLyricsTests.swift: testCustomFramesAreClampedOntoTheScreen
    [Fact]
    public void CustomFramesAreClampedOntoTheScreen()
    {
        var area = FloatingFrame.UsableArea(Screen, Visible, 32);
        // Saved on a bigger display, now off the edge of this one.
        var frame = FloatingFrame.Resolve(FloatingPlacement.Custom, new FloatingRect(1400, -50, 700, 180), null, 44, Screen, area);
        Assert.True(area.Contains(frame));
        Assert.Equal(new SizeD(700, 180), frame.Size);
        // Relative to its display: a second display to the right gets the same spot.
        var second = new FloatingRect(1512, 0, 1920, 1080);
        var there = FloatingFrame.Resolve(FloatingPlacement.Custom, new FloatingRect(100, 100, 500, 150), null, 44, second, second);
        Assert.Equal((1612.0, 100.0), (there.X, there.Y));
        var relative = FloatingFrame.Relative(there, second);
        Assert.Equal((100.0, 100.0), (relative.X, relative.Y));
        // Custom with nothing saved falls back to a preset.
        var fallback = FloatingFrame.Resolve(FloatingPlacement.Custom, null, null, 44, Screen, area);
        Assert.True(area.Contains(fallback));
        // A preset keeps the size given on that display.
        var sized = FloatingFrame.Resolve(FloatingPlacement.Top, null, new SizeD(520, 130), 44, Screen, area);
        Assert.Equal(new SizeD(520, 130), sized.Size);
    }

    // Ported from FloatingLyricsTests.swift: testDraggingMovesWithinTheAreaAndResizesFromTheCorner
    [Fact]
    public void DraggingMovesWithinTheAreaAndResizesFromTheCorner()
    {
        var area = new FloatingRect(0, 0, 1000, 800);
        var start = new FloatingRect(100, 100, 400, 150);
        Assert.Equal(new FloatingRect(150, 120, 400, 150), FloatingFrame.Dragged(start, 50, 20, resizing: false, area));
        Assert.Equal(0, FloatingFrame.Dragged(start, -500, 0, resizing: false, area).MinX);
        // Down and right grows it, top left corner fixed.
        var grown = FloatingFrame.Dragged(start, 100, -50, resizing: true, area);
        Assert.Equal(new FloatingRect(100, 50, 500, 200), grown);
        Assert.Equal(start.MaxY, grown.MaxY);
        var shrunk = FloatingFrame.Dragged(start, -1000, 1000, resizing: true, area);
        Assert.Equal(FloatingFrame.MinimumSize, shrunk.Size);
        var huge = FloatingFrame.Dragged(start, 5000, -5000, resizing: true, area);
        Assert.True(huge.MaxX <= area.MaxX);
        Assert.True(huge.MinY >= area.MinY);
    }

    // Windows' own: any edge or corner resizes, so the shape (aspect) can change freely.
    [Fact]
    public void ResizingFromAnEdgeMovesOnlyThatEdgeWithinTheAreaAndTheMinimum()
    {
        var area = new FloatingRect(0, 0, 1000, 800);
        var start = new FloatingRect(100, 100, 400, 150); // y up: bottom 100, top 250
        Assert.Equal(new FloatingRect(100, 100, 500, 150), FloatingFrame.Resized(start, 100, 0, FloatingEdges.Right, area));
        Assert.Equal(new FloatingRect(50, 100, 450, 150), FloatingFrame.Resized(start, -50, 0, FloatingEdges.Left, area));
        Assert.Equal(new FloatingRect(100, 100, 400, 200), FloatingFrame.Resized(start, 0, 50, FloatingEdges.Top, area));
        Assert.Equal(new FloatingRect(100, 40, 400, 210), FloatingFrame.Resized(start, 0, -60, FloatingEdges.Bottom, area));
        // The bottom-right corner matches the Mac's corner grip.
        Assert.Equal(FloatingFrame.Dragged(start, 100, -50, resizing: true, area),
            FloatingFrame.Resized(start, 100, -50, FloatingEdges.Right | FloatingEdges.Bottom, area));
        // Never past the area, never below the minimum, and the other edges stay put.
        var wide = FloatingFrame.Resized(start, 5000, 0, FloatingEdges.Right, area);
        Assert.Equal((100.0, 1000.0), (wide.MinX, wide.MaxX));
        var narrow = FloatingFrame.Resized(start, -5000, 0, FloatingEdges.Right, area);
        Assert.Equal(FloatingFrame.MinimumSize.Width, narrow.Width);
        var tall = FloatingFrame.Resized(start, 0, 5000, FloatingEdges.Top, area);
        Assert.Equal((100.0, 800.0), (tall.MinY, tall.MaxY));
        var leftMost = FloatingFrame.Resized(start, -5000, 0, FloatingEdges.Left, area);
        Assert.Equal((0.0, 500.0), (leftMost.MinX, leftMost.MaxX));
    }

    // Ported from FloatingLyricsTests.swift: testFramesEncodeAndDisplaysFallBackToTheMainOne
    [Fact]
    public void FramesEncodeAndDisplaysFallBackToTheMainOne()
    {
        var rect = new FloatingRect(1, 2, 3, 4);
        Assert.Equal(rect, FloatingFrame.Decode(FloatingFrame.Encode(rect)));
        Assert.Null(FloatingFrame.Decode([1, 2, 3]));
        Assert.Null(FloatingFrame.Decode([1, 2, 0, 4]));
        Assert.Null(FloatingFrame.Decode([double.NaN, 2, 3, 4]));
        Assert.Equal(0, FloatingFrame.ChosenIndex("", ["A", "B"]));
        Assert.Equal(1, FloatingFrame.ChosenIndex("B", ["A", "B"]));
        Assert.Equal(0, FloatingFrame.ChosenIndex("gone", ["A", "B"])); // a disconnected choice uses the main display
        Assert.Null(FloatingFrame.ChosenIndex("A", []));
    }

    // Ported from FloatingLyricsTests.swift: testPickerGridHasEveryPresetOnce
    [Fact]
    public void PickerGridHasEveryPresetOnce()
    {
        var cells = FloatingPlacements.Presets.Select(p => p.Cell()).OfType<(int, int)>().Select(c => $"{c.Item1},{c.Item2}");
        Assert.Equal(FloatingPlacements.Presets.Count, cells.Distinct().Count());
        Assert.Null(FloatingPlacement.Custom.Cell());
        Assert.DoesNotContain(FloatingPlacement.Custom, FloatingPlacements.Presets);
    }

    [Fact]
    public void RawValuesAreTheMacs()
    {
        Assert.Equal(["topLeft", "top", "topRight", "centre", "bottomLeft", "bottom", "bottomRight", "custom"],
            FloatingPlacements.All.Select(p => p.Raw()));
        foreach (var placement in FloatingPlacements.All) Assert.Equal(placement, FloatingPlacements.Parse(placement.Raw()));
        Assert.Null(FloatingPlacements.Parse("sideways"));
        Assert.Equal("floating", LyricsSurface.Floating.Raw());
        Assert.Equal(LyricsSurface.Desktop, LyricsSurfaces.Parse("desktop"));
        Assert.Null(LyricsSurfaces.Parse("wall"));
    }

    // ---- When the window shows ----

    private static FloatingVisibility.Input Playing =>
        new(LyricsSurface.Floating, LyricsVisible: true, HasTrack: true, IsPlaying: true, HasTimedLines: true);

    // Ported from FloatingLyricsTests.swift: testOnlyOneSurfaceDrawsTheLyrics (the window's half; the wallpaper's half
    // is RenderEngine's, tested in Lunalyr.Render.Tests)
    [Fact]
    public void OnlyOneSurfaceShowsTheFloatingWindow()
    {
        Assert.False(FloatingVisibility.Shows(Playing with { Surface = LyricsSurface.Desktop })); // no floating window while the desktop has them
        Assert.True(FloatingVisibility.Shows(Playing));
    }

    // Ported from FloatingLyricsTests.swift: testShowsOnlyWithLyricsPlaying
    [Fact]
    public void ShowsOnlyWithLyricsPlaying()
    {
        Assert.True(FloatingVisibility.Shows(Playing));
        Assert.False(FloatingVisibility.Shows(Playing with { HasTrack = false, IsPlaying = false })); // nothing playing
        var input = Playing with { HasTimedLines = false };
        Assert.False(FloatingVisibility.Shows(input)); // no lyrics (still loading)
        Assert.True(FloatingVisibility.Shows(input with { HasPlainLines = true })); // untimed lyrics are shown
        Assert.False(FloatingVisibility.Shows(Playing with { NotFound = true })); // no lyrics found
        Assert.False(FloatingVisibility.Shows(Playing with { AllInstrumental = true })); // an instrumental
        Assert.False(FloatingVisibility.Shows(Playing with { LyricsVisible = false })); // Hide lyrics or mirroring
    }

    // Ported from FloatingLyricsTests.swift: testAShortPauseKeepsTheLyricsUp
    [Fact]
    public void AShortPauseKeepsTheLyricsUp()
    {
        var input = Playing with { IsPlaying = false, PausedFor = 3 };
        Assert.True(FloatingVisibility.Shows(input));
        Assert.Equal(FloatingVisibility.PauseGrace - 3, FloatingVisibility.HidesAfter(input) ?? -1, 3);
        input = input with { PausedFor = FloatingVisibility.PauseGrace + 1 };
        Assert.False(FloatingVisibility.Shows(input));
        Assert.Null(FloatingVisibility.HidesAfter(input));
        Assert.Null(FloatingVisibility.HidesAfter(Playing));
    }

    // Ported from FloatingLyricsTests.swift: testEditPositionShowsTheWindowEvenWithNothingPlaying
    [Fact]
    public void EditPositionShowsTheWindowEvenWithNothingPlaying()
    {
        var input = new FloatingVisibility.Input(LyricsSurface.Floating, Editing: true);
        Assert.True(FloatingVisibility.Shows(input));
        Assert.False(FloatingVisibility.Shows(input with { Surface = LyricsSurface.Desktop })); // nothing to place while the lyrics are on the desktop
    }

    // ---- The look ----

    // Ported from FloatingLyricsTests.swift: testBackdropDarkensWhatTheColoursAreJudgedAgainst
    [Fact]
    public void BackdropDarkensWhatTheColoursAreJudgedAgainst()
    {
        var none = FloatingBackdrop.Luminance(0, reduceTransparency: false);
        var full = FloatingBackdrop.Luminance(1, reduceTransparency: false);
        var solid = FloatingBackdrop.Luminance(1, reduceTransparency: true);
        Assert.Equal(LyricLegibility.UnknownBackdropLuminance, none, 4);
        Assert.True(full < none);
        Assert.True(solid < full); // Reduce Transparency draws a plain panel, darker than the soft shade
    }

    // Windows only: a tester with transparency effects off and the shade at 0% saw a constant black pill (15 Nov 2026).
    [Fact]
    public void With_transparency_off_the_panel_follows_the_shade_and_none_at_zero()
    {
        Assert.Equal(0, FloatingBackdrop.Panel(0));
        Assert.Equal(0, FloatingBackdrop.Panel(double.NaN));
        Assert.Equal(FloatingBackdrop.SolidShade, FloatingBackdrop.Panel(1), 6);
        Assert.Equal(FloatingBackdrop.SolidShade * 0.6, FloatingBackdrop.Panel(0.6), 6);
        Assert.Equal(LyricLegibility.UnknownBackdropLuminance, FloatingBackdrop.Luminance(0, reduceTransparency: true), 4);
    }

    // Ported from FloatingLyricsTests.swift: testFloatingTextSizeReplacesTheScreenBasedSize
    [Fact]
    public void FloatingTextSizeReplacesTheScreenBasedSize()
    {
        var window = ViewportMetrics.ForText(800, 200, 1, 44);
        Assert.Equal(44, window.BaseSize);
        Assert.True(window.Stage.Width > 800 * 0.8); // the stage fills nearly the whole window
        var screen = ViewportMetrics.For(1512, 982, 1);
        Assert.Equal(1512 * 0.8, screen.Stage.Width, 3); // the wallpaper's is unchanged
        // At 200% the text size is in points: twice the pixels.
        Assert.Equal(88, ViewportMetrics.ForText(1600, 400, 2, 44).BaseSize);
    }
}
