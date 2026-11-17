using Lunalyr.Core.Compositions;
using Lunalyr.Core.Floating;

namespace Lunalyr.Core.Tests.Floating;

/// <summary>After the Mac's b11a1ab3 (OpeningTitleTests.swift and FloatingLyricsTests.swift): the floating song title card's window.</summary>
public sealed class FloatingTitleFrameTests
{
    private static readonly FloatingRect Area = new(0, 70, 1512, 874);

    // Ported from OpeningTitleTests.swift: testTheCardStartsJustAboveTheLyricsCentred
    [Fact]
    public void TheCardStartsJustAboveTheLyricsCentred()
    {
        var box = new FloatingRect(338, 94, 836, 202);
        var card = FloatingTitleFrame.Start(FloatingTitleStart.Standard, box, Area, 44);
        Assert.Equal(box.MidX, card.MidX, 0.5);
        Assert.Equal(box.MaxY + FloatingTitleFrame.Gap, card.MinY, 0.5); // just above the box
        Assert.Equal(FloatingTitleFrame.DefaultSize(44), card.Size);
        Assert.True(card.Intersection(box).IsEmpty);
        // No room above a box at the top: below it instead, still on the area.
        var high = new FloatingRect(338, Area.MaxY - 202, 836, 202);
        var under = FloatingTitleFrame.Start(FloatingTitleStart.Standard, high, Area, 44);
        Assert.Equal(high.MinY - FloatingTitleFrame.Gap, under.MaxY, 0.5);
        Assert.True(Area.Contains(under));
    }

    // Ported from OpeningTitleTests.swift: testTheOldRowsPlaceBecomesTheCardsStart
    [Fact]
    public void TheOldRowsPlaceBecomesTheCardsStart()
    {
        var start = FloatingTitleStart.FromRow(new OpeningTitleRow(0.2, true, 1.3));
        Assert.Equal(new FloatingTitleStart(0.2, true, 1.3), start);
        var box = new FloatingRect(338, 400, 836, 202);
        var card = FloatingTitleFrame.Start(start, box, Area, 44);
        Assert.Equal(box.MinX + 0.2 * box.Width, card.MidX, 0.5); // across the box where the row was
        Assert.Equal(box.MinY - FloatingTitleFrame.Gap, card.MaxY, 0.5); // below it, where the row was at the foot
        Assert.Equal(FloatingTitleFrame.DefaultSize(44, 1.3).Height, card.Height);
        Assert.Equal(start, FloatingTitleStart.Decode(start.Encoded));
        Assert.Null(FloatingTitleStart.Decode([1]));
    }

    // Ported from OpeningTitleTests.swift: testTheCardIsStackedUntilItIsTooSmall
    [Fact]
    public void TheCardIsStackedUntilItIsTooSmall()
    {
        var standard = FloatingTitleFrame.Layout(FloatingTitleFrame.DefaultSize(44));
        Assert.True(standard.Stacked); // the default card is the desktop's stacked card
        Assert.True(standard.Nominal >= FloatingTitleFrame.MinimumStackedTitle);
        // The line: the height at which the stacked title reaches its minimum.
        var line = FloatingTitleFrame.MinimumStackedTitle * OpeningTitle.CoverShare(false) / FloatingTitleFrame.Fill;
        Assert.True(FloatingTitleFrame.Layout(new SizeD(600, line + 1)).Stacked); // stacked above it
        Assert.False(FloatingTitleFrame.Layout(new SizeD(600, line - 1)).Stacked); // one row below it
        // Too narrow for the stacked words, however tall: one row.
        Assert.False(FloatingTitleFrame.Layout(new SizeD(90, 200)).Stacked);
        // Resizing scales it.
        var small = FloatingTitleFrame.Layout(new SizeD(400, 60));
        var large = FloatingTitleFrame.Layout(new SizeD(800, 120));
        Assert.Equal(small.Nominal * 2, large.Nominal, 6);
        var row = FloatingTitleFrame.Layout(new SizeD(600, 28));
        Assert.Equal(28 * FloatingTitleFrame.Fill, row.RowBase * OpeningTitle.RowCover, 6); // the row's cover fills the card's height
        Assert.True(FloatingTitleFrame.MinimumSize.Height <= line); // small enough to reach the row
    }

    // Ported from FloatingLyricsTests.swift: testTheTitleCardShowsWithTheFloatingLyrics
    [Fact]
    public void TheTitleCardShowsWithTheFloatingLyrics()
    {
        Assert.True(FloatingTitleVisibility.Shows(lyrics: true, card: true));
        Assert.False(FloatingTitleVisibility.Shows(lyrics: false, card: true)); // only where floating lyrics show
        Assert.False(FloatingTitleVisibility.Shows(lyrics: true, card: false)); // Show the song title card off
    }

    // Ported from OpeningTitleTests.swift: testDraggingTheCardOnThePictureMovesItFreely
    [Fact]
    public void DraggingTheCardOnThePictureMovesItFreely()
    {
        var screen = new FloatingRect(0, 0, 1512, 982);
        var start = new FloatingRect(600, 400, 316, 70);
        // A quarter-size picture (378 × 245.5): 50 px right and 25 px up there is 200 points right and 100 up, y up.
        var moved = FloatingTitleFrame.DraggedOnPicture(start, screen, Area, 50, -25, 378, 245.5, resizing: false);
        Assert.Equal(new FloatingRect(800, 500, 316, 70), moved);
        // Anywhere on the display, far from the lyrics: into a corner, kept on it.
        var corner = FloatingTitleFrame.DraggedOnPicture(start, screen, Area, -9_000, 9_000, 378, 245.5, resizing: false);
        Assert.Equal(Area.MinX, corner.MinX);
        Assert.Equal(Area.MinY, corner.MinY);
        // Its corner: resized from its top left, no smaller than its least.
        var resized = FloatingTitleFrame.DraggedOnPicture(start, screen, Area, -60, -20, 378, 245.5, resizing: true);
        Assert.Equal(FloatingTitleFrame.MinimumSize, resized.Size);
        Assert.Equal(start.MinX, resized.MinX);
        Assert.Equal(start.MaxY, resized.MaxY);
    }
}
