using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>"Cutout · Highlight" tiles: their timing, where they fall on a label, and the segments of a run.</summary>
public class HighlightTileTests
{
    // Ported from LyricHighlightTests.swift: testTheTilePressesInAtTheOnsetAndFadesAtTheLineEnd
    [Fact]
    public void TheTilePressesInAtTheOnsetAndFadesAtTheLineEnd()
    {
        const double onset = 10.0, release = 13.6;
        static HighlightPose Pose(double time, bool reduce = false) => LyricHighlightTiming.Pose(time, onset, release, reduce);
        Assert.False(Pose(onset - 0.001).IsVisible); // nothing before the word is sung
        Assert.False(Pose(onset).IsVisible);
        // A quick ease-out from the leading edge, about 120 ms, with no pop: it never passes the word's width and
        // never shrinks back.
        var width = 0.0;
        for (var step = 1; step <= 24; step++)
        {
            var current = Pose(onset + LyricHighlightTiming.Press * step / 24);
            Assert.True(current.IsVisible);
            Assert.True(current.Width >= width);
            Assert.True(current.Width <= 1);
            width = current.Width;
        }
        Assert.True(Pose(onset + 0.02).Width < 0.6, "it presses in rather than appearing whole");
        Assert.True(Pose(onset + 0.06).Width > 0.7, "most of the way in half way through");
        Assert.Equal(1, Pose(onset + LyricHighlightTiming.Press).Width, 9);
        Assert.Equal(1, Pose(onset + LyricHighlightTiming.Press).Opacity, 9);
        // Held for the rest of the line.
        for (var time = onset + 0.13; time < release; time += 0.05) Assert.Equal(new HighlightPose(1, 1), Pose(time));
        // Fades over about 300 ms after the line ends.
        var opacity = 1.0;
        for (var step = 1; step <= 30; step++)
        {
            var current = Pose(release + LyricHighlightTiming.Fade * step / 30);
            Assert.True(current.Opacity < opacity);
            if (current.IsVisible) Assert.Equal(1, current.Width); // it fades; it does not shrink
            opacity = current.Opacity;
        }
        Assert.False(Pose(release + LyricHighlightTiming.Fade).IsVisible);
        Assert.Equal(0.3, LyricHighlightTiming.Fade, 0.05);
        Assert.Equal(0.12, LyricHighlightTiming.Press, 0.03);
    }

    [Fact]
    public void WithReduceMotionTheTileOnlyFades()
    {
        var pose = LyricHighlightTiming.Pose(10.03, 10, 12, reduceMotion: true);
        Assert.Equal(1, pose.Width);
        Assert.InRange(pose.Opacity, 0.01, 0.99);
    }

    [Fact]
    public void TheWordsInkSwitchesWithinAFrameOfTheFade()
    {
        Assert.Equal(1, LyricHighlightTiming.TextOpacity(0.9, 0.4));
        Assert.Equal(0, LyricHighlightTiming.TextOpacity(0.1, 0.4));
        Assert.Equal(0.5, LyricHighlightTiming.TextOpacity(0.4, 0.4), 6);
    }

    [Fact]
    public void ATileReachesPastItsWordAndJustMeetsItsNeighbour()
    {
        var pose = new HighlightPose(1, 1);
        var whole = new HighlightMark(0, 1, pose).Rect(200, 40, 50, rightToLeft: false);
        Assert.Equal(new RectD(-5, -2.5, 210, 45), whole);
        var first = new HighlightMark(0, 0.5, pose).Rect(200, 40, 50, false);
        var second = new HighlightMark(0.5, 1, pose).Rect(200, 40, 50, false);
        Assert.True(first.X + first.Width > second.X, "where two words of one run meet, their tiles just overlap");
        // Right to left: the first word's tile is at the right.
        var rtl = new HighlightMark(0, 0.5, pose).Rect(200, 40, 50, true);
        Assert.Equal(200 + 5 - rtl.Width, rtl.X, 9);
        // Pressed in from the leading edge.
        var half = new HighlightMark(0, 1, new HighlightPose(1, 0.5));
        Assert.Equal(-5, half.Pressed(200, 40, 50, false).X);
        Assert.Equal(105, half.Pressed(200, 40, 50, false).Width);
        var rtlHalf = half.Pressed(200, 40, 50, true);
        Assert.Equal(205, rtlHalf.X + rtlHalf.Width, 9);
    }

    [Fact]
    public void TheSyllablesOfOneLatinWordAreOneTile()
    {
        LyricWord[] syllables = [new("beau", 5, 5.2), new("ti", 5.2, 5.4), new("ful", 5.4, 5.6)];
        bool[] glue = [false, true, true];
        var line = EditorialSetting.Layout(syllables, glue, 44, 11, 0, wordTimed: true, editorial: false);
        var segments = HighlightSegment.Of(line, syllables, glue, SimpleWordBreaker.Instance, 6)[0];
        Assert.Equal([(0.0, 1.0, 5.0)], segments.Select(segment => (segment.From, segment.To, segment.Onset)));
        Span<HighlightMark> marks = stackalloc HighlightMark[4];
        Assert.Equal(0, HighlightSegment.Marks(segments, 4.9, false, marks));
        Assert.Equal(1, HighlightSegment.Marks(segments, 5.5, false, marks));
        Assert.Equal(0, HighlightSegment.Marks(segments, 6.4, false, marks));
    }

    [Fact]
    public void OnlyTheEntranceNamedHighlightPressesTiles()
    {
        Assert.True(LyricHighlightTiming.HighlightsSungWords("cutoutHighlight"));
        Assert.False(LyricHighlightTiming.HighlightsSungWords("fade"));
        Assert.False(LyricHighlightTiming.HighlightsSungWords(null));
    }

    // Ported from LyricHighlightTests.swift: testRollingHighlightsReleaseWithTheirOwnLines (Spoken's rolling stage)
    [Fact]
    public void RollingHighlightsReleaseWithTheirOwnLines()
    {
        var first = new LyricWord("Hold", 10, 10.3);
        var second = new LyricWord("on", 10.35, 10.6);
        var third = new LyricWord("Again", 13.9, 14.5);
        LyricLine[] lines =
        [
            new() { Start = 10, End = 13.8, Text = "Hold on", Words = [first, second], WordTimed = true },
            new() { Start = 13.9, End = 15, Text = "Again", Words = [third], WordTimed = true },
        ];
        var window = new RollingSong(lines, SimpleWordBreaker.Instance, null, RomanizationDisplay.Off).Window(0, 13.7, 1.2);
        Assert.Equal([first, second, third], window.Words);
        Assert.Equal(13.8, window.LineEnds[first]);
        Assert.Equal(13.8, window.LineEnds[second]);
        Assert.Equal(15, window.LineEnds[third]);

        var words = RollingWords.Prepare(window.Words, 44, ordinals: window.Ordinals);
        var poses = words.Select(word => LyricHighlightTiming.Pose(13.7, word.Source.Start, window.LineEnds[word.Source], false)).ToArray();
        Assert.Equal([new HighlightPose(1, 1), new HighlightPose(1, 1), HighlightPose.Hidden], poses);
        var shown = RollingTokenState.Make(words[0], 2, 1, 13.7, 44, 600, 0, false, WordArrival.Glide, new WordRevealOptions(),
                                           highlightOpacity: poses[0].Opacity).Shown;
        Assert.Equal(1, shown, 3); // the normal timed fade cannot hide a held tile
    }
}

/// <summary>A rolling word's "Cutout · Highlight" tile (the Mac's CutoutTile behind a RollingUnit, padded by labelReach).</summary>
public class RollingHighlightTileTests
{
    [Fact]
    public void TheTileCoversTheWholeUnitAndALabelsPaddingMore()
    {
        // A unit 120 × 60 pixels at 2 pixels a point: 9 × 3 points more at each side, centred on the unit.
        var rect = RollingLabel.HighlightRect(120, 60, 2);
        Assert.Equal(new RectD(-78, -36, 156, 72), rect);
        // A label's padding past an unlabelled word's: the tile is a label's size.
        Assert.Equal(RollingLabel.PaddingX - RollingLabel.PlainPaddingX, RollingLabel.ReachX);
        Assert.Equal(RollingLabel.PaddingY, RollingLabel.ReachY);
    }

    [Fact]
    public void ThePressedPartGrowsFromTheLeadingEdge()
    {
        var rect = RollingLabel.HighlightRect(120, 60, 2);
        Assert.Equal(rect with { Width = 39 }, RollingLabel.Pressed(rect, 0.25, rightToLeft: false));
        Assert.Equal(rect with { X = rect.X + 117, Width = 39 }, RollingLabel.Pressed(rect, 0.25, rightToLeft: true));
        Assert.Equal(rect, RollingLabel.Pressed(rect, 1.3, rightToLeft: false));
        Assert.Equal(0, RollingLabel.Pressed(rect, -1, rightToLeft: true).Width);
    }
}
