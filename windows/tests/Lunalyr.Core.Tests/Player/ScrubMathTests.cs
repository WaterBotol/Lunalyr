using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Player;

namespace Lunalyr.Core.Tests.Player;

/// <summary>The seek bar's rules, without a window: fine-scrub gain, snapping to lyric lines, rubber band, where a release seeks, and the drawn structure.</summary>
public sealed class ScrubMathTests
{
    private static ScrubLine Line(double start, double end, string text = "la", bool instrumental = false) =>
        new(start, end, text, null, instrumental);

    // Ported from DesktopScrubberTests.swift: testGainStepsByDistanceAndOptionIsFine
    [Fact]
    public void GainStepsByDistanceAndOptionIsFine()
    {
        Assert.Equal(1, ScrubMath.Gain(0));
        Assert.Equal(1, ScrubMath.Gain(39.9));
        Assert.Equal(0.5, ScrubMath.Gain(-40)); // above or below the bar alike
        Assert.Equal(0.25, ScrubMath.Gain(80));
        Assert.Equal(0.25, ScrubMath.Gain(119));
        Assert.Equal(0.1, ScrubMath.Gain(500));
        Assert.Equal(0.1, ScrubMath.Gain(0, fine: true));
        Assert.Equal(1, ScrubMath.Gain(double.NaN));
        Assert.Null(ScrubMath.RateLabel(1));
        Assert.Equal("Half speed", ScrubMath.RateLabel(0.5));
        Assert.Equal("Quarter speed", ScrubMath.RateLabel(0.25));
        Assert.Equal("Fine", ScrubMath.RateLabel(0.1));
    }

    // Ported from DesktopScrubberTests.swift: testRubberBandIsSignedBoundedAndSlowing
    [Fact]
    public void RubberBandIsSignedBoundedAndSlowing()
    {
        Assert.Equal(0, ScrubMath.RubberBand(0));
        var small = ScrubMath.RubberBand(10);
        var large = ScrubMath.RubberBand(1000);
        Assert.True(small > 0);
        Assert.True(small < 10, "resists from the first point");
        Assert.True(large < 40, "never reaches the dimension");
        Assert.True(large > 35);
        Assert.Equal(-small, ScrubMath.RubberBand(-10), 1e-9);
        // The iOS formula, c = 0.55, d = 40.
        Assert.Equal((1 - 1 / (0.55 + 1)) * 40, ScrubMath.RubberBand(40), 1e-9);
    }

    // Ported from DesktopScrubberTests.swift: testPositionAndTimeRoundTripAndClamp
    [Fact]
    public void PositionAndTimeRoundTripAndClamp()
    {
        const double width = ScrubMath.BarWidth;
        Assert.Equal(100, ScrubMath.Time(width / 2, 200), 1e-9);
        Assert.Equal(0, ScrubMath.Time(-5, 200));
        Assert.Equal(200, ScrubMath.Time(9999, 200));
        Assert.Equal(width / 4, ScrubMath.X(50, 200), 1e-9);
        Assert.Equal(114, ScrubMath.X(50, 200, width: 456), 1e-9);
        Assert.Equal(0, ScrubMath.Time(10, 0));
    }

    // Ported from DesktopScrubberTests.swift: testSnapTargetsSkipInstrumentalAndAddStart
    [Fact]
    public void SnapTargetsSkipInstrumentalAndAddStart()
    {
        ScrubLine[] lines = [Line(5, 8), Line(9, 10, instrumental: true), Line(12, 14), Line(12.01, 13), Line(300, 301)];
        Assert.Equal([0, 5, 12], ScrubMath.SnapTargets(lines, 200));
    }

    // Ported from DesktopScrubberTests.swift: testSnapRadiusSpeedGateCommandAndGain
    [Fact]
    public void SnapRadiusSpeedGateCommandAndGain()
    {
        double[] targets = [0.0, 10, 20];
        const double pps = 456.0 / 200; // 2.28 DIP/s
        // 6 DIP away from 10 s: inside the 7 DIP radius.
        const double near = 10 + 6 / pps;
        Assert.Equal(1, ScrubMath.Snap(near, targets, pps));
        Assert.Null(ScrubMath.Snap(10 + 8 / pps, targets, pps)); // outside the radius
        Assert.Null(ScrubMath.Snap(near, targets, pps, speed: 300)); // a fast sweep isn't sticky
        Assert.Equal(1, ScrubMath.Snap(near, targets, pps, speed: 200));
        Assert.Null(ScrubMath.Snap(near, targets, pps, disabled: true)); // Ctrl (the Mac's ⌘) turns it off
        Assert.Null(ScrubMath.Snap(near, targets, pps, gain: 0.25)); // fine modes barely snap
        Assert.Equal(1, ScrubMath.Snap(10 + 1.5 / pps, targets, pps, gain: 0.25));
    }

    // Ported from DesktopScrubberTests.swift: testSnapRadiusIsCappedByNeighbours
    [Fact]
    public void SnapRadiusIsCappedByNeighbours()
    {
        // Lines 2 s apart at 2.28 DIP/s: 4.56 DIP apart, so the cap is 1.82 DIP.
        const double pps = 2.28;
        double[] targets = [0.0, 100, 102];
        Assert.Null(ScrubMath.Snap(100 + 2.5 / pps, targets, pps));
        Assert.Equal(1, ScrubMath.Snap(100 + 1.5 / pps, targets, pps));
    }

    // Ported from DesktopScrubberTests.swift: testCommitTargetPreRollsSnapsAndGuardsTheEnd
    [Fact]
    public void CommitTargetPreRollsSnapsAndGuardsTheEnd()
    {
        Assert.Equal(42, ScrubMath.CommitTarget(42, 200));
        Assert.Equal(41.8, ScrubMath.CommitTarget(42, 200, snappedTo: 42), 1e-9);
        Assert.Equal(0, ScrubMath.CommitTarget(0.1, 200, snappedTo: 0.1)); // never before the start
        Assert.Equal(199.5, ScrubMath.CommitTarget(200, 200)); // never so close to the end the player skips
        Assert.Equal(0, ScrubMath.CommitTarget(-3, 200));
        Assert.Equal(0, ScrubMath.CommitTarget(double.NaN, 200));
    }

    // Ported from DesktopScrubberTests.swift: testTicksAreThinnedAndSkipTheStart
    [Fact]
    public void TicksAreThinnedAndSkipTheStart()
    {
        // One per 4 DIP at 1 DIP/s; 0:00 is not a line.
        Assert.Equal([10, 20], ScrubMath.Ticks([0, 10, 10.5, 11, 20], 456, width: 456));
    }

    // Ported from DesktopScrubberTests.swift: testSectionBreaksAfterLongGapsAndInstrumentals
    [Fact]
    public void SectionBreaksAfterLongGapsAndInstrumentals()
    {
        ScrubLine[] lines = [Line(5, 8), Line(9, 12), Line(20, 22), Line(23, 30, instrumental: true), Line(31, 33), Line(34, 35)];
        Assert.Equal([20, 31], ScrubMath.SectionBreaks(lines, 200));
        Assert.Empty(ScrubMath.SectionBreaks([Line(1, 2)], 200));
    }

    // Ported from DesktopScrubberTests.swift: testRepeatedLinesMakeSpans
    [Fact]
    public void RepeatedLinesMakeSpans()
    {
        ScrubLine[] lines =
        [
            Line(5, 8, "Verse one"), Line(9, 12, "Oh, the evening"), Line(12.5, 15, "knows my name"),
            Line(20, 23, "Verse two"), Line(30, 33, "Oh the EVENING!"), Line(33.5, 36, "Knows my name"),
            Line(45, 48, "a lone repeat"), Line(60, 62, "a lone repeat"),
        ];
        // Runs of two or more returning lines; punctuation and case ignored; a single line is not a span.
        Assert.Equal([(9.0, 15.0), (30.0, 36.0)], ScrubMath.RepeatedSpans(lines, 200));
        Assert.Empty(ScrubMath.RepeatedSpans([], 200));
    }

    // Ported from DesktopScrubberTests.swift: testLyricAtTime
    [Fact]
    public void LyricAtTime()
    {
        ScrubLine[] lines = [Line(5, 8, "one"), Line(9, 12, "two"), Line(13, 20, instrumental: true), Line(21, 23, "three"), Line(40, 42, "four")];
        Assert.Equal(ScrubLyric.Instrumental, ScrubMath.Lyric(2, lines)); // an intro of 3 s or more
        Assert.Equal(ScrubLyric.Line(0), ScrubMath.Lyric(6, lines));
        Assert.Equal(ScrubLyric.Line(1), ScrubMath.Lyric(12.5, lines)); // just after its end, still that line
        Assert.Equal(ScrubLyric.Instrumental, ScrubMath.Lyric(15, lines));
        Assert.Equal(ScrubLyric.Instrumental, ScrubMath.Lyric(30, lines)); // a long gap after a line
        Assert.Equal(ScrubLyric.Line(4), ScrubMath.Lyric(41, lines));
        Assert.Equal(ScrubLyric.None, ScrubMath.Lyric(0.5, [Line(1, 2, "x")])); // a short intro shows nothing
        Assert.Equal(ScrubLyric.None, ScrubMath.Lyric(1, []));
    }

    // Ported from DesktopScrubberTests.swift: testAdjacentLineWalksThroughPreRolledSeeks
    [Fact]
    public void AdjacentLineWalksThroughPreRolledSeeks()
    {
        double[] targets = [0.0, 10, 20, 30];
        Assert.Equal(20, ScrubMath.AdjacentLine(12, targets, forward: true));
        // After a seek to 20 - 0.2 the next press still moves on.
        Assert.Equal(30, ScrubMath.AdjacentLine(19.8, targets, forward: true));
        Assert.Null(ScrubMath.AdjacentLine(31, targets, forward: true));
        Assert.Equal(20, ScrubMath.AdjacentLine(25, targets, forward: false)); // back to this line's start first
        Assert.Equal(10, ScrubMath.AdjacentLine(20.5, targets, forward: false));
        Assert.Equal(10, ScrubMath.AdjacentLine(19.8, targets, forward: false));
        Assert.Equal(0, ScrubMath.AdjacentLine(0.5, targets, forward: false));
    }

    // Ported from DesktopScrubberTests.swift: testKeyboardStepsAndJumps
    [Fact]
    public void KeyboardStepsAndJumps()
    {
        Assert.Equal(0, ScrubMath.Stepped(3, -5, 200));
        Assert.Equal(199.5, ScrubMath.Stepped(198, 5, 200));
        Assert.Equal(100, ScrubMath.PercentJump(5, 200));
        Assert.Equal(0, ScrubMath.PercentJump(0, 200));
        Assert.Null(ScrubMath.PercentJump(10, 200));
        Assert.Null(ScrubMath.PercentJump(3, 0));
    }

    // Ported from DesktopScrubberTests.swift: testHeldTimeCoversOneStaleRedraw
    [Fact]
    public void HeldTimeCoversOneStaleRedraw()
    {
        Assert.Equal(60, ScrubMath.DisplayedTime(10, 60, 0, 0.2));
        Assert.Equal(60.3, ScrubMath.DisplayedTime(60.3, 60, 0, 0.2)); // close enough: the clock
        Assert.Equal(10, ScrubMath.DisplayedTime(10, 60, 0, 0.7)); // no more than 600 ms
        Assert.Equal(10, ScrubMath.DisplayedTime(10, null, 0, 0.1));
    }

    // Ported from DesktopScrubberTests.swift: testWords
    [Fact]
    public void Words()
    {
        Assert.Equal("1:05", ScrubMath.Timestamp(65.9));
        Assert.Equal("1:02:03", ScrubMath.Timestamp(3723));
        Assert.Equal("0:00", ScrubMath.Timestamp(-1));
        Assert.Equal("-2:10", ScrubMath.TrailingLabel(83, 213, showsRemaining: true));
        Assert.Equal("3:33", ScrubMath.TrailingLabel(83, 213, showsRemaining: false));
        Assert.Equal("", ScrubMath.TrailingLabel(83, 0, showsRemaining: true));
        Assert.Equal("1:23 of 3:33, line: hello", ScrubMath.AccessibilityValue(83, 213, "hello"));
    }

    // Ported from DesktopScrubberTests.swift: testScrubLinesTakeOffTheOffsetAndDropCredits
    [Fact]
    public void ScrubLinesTakeOffTheOffsetAndDropCredits()
    {
        var credit = new LyricLine { Start = 0, End = 1, Text = "Produced by: X", IsInstrumental = true, Credit = true };
        var sung = new LyricLine { Start = 10.5, End = 12, Text = "hello" };
        var lines = ScrubLine.Lines([credit, sung], 0.5, new Dictionary<int, string> { [1] = "harou" });
        Assert.Equal([new ScrubLine(10, 11.5, "hello", "harou", false)], lines);
    }

    [Fact]
    public void NormalKeyIgnoresCaseAccentsAndPunctuation()
    {
        Assert.Equal("ohtheevening", ScrubMath.NormalKey("Oh, the EVENING!"));
        Assert.Equal("cafe", ScrubMath.NormalKey("Café"));
        Assert.Equal("夕暮れの窓", ScrubMath.NormalKey("夕暮れの窓 "));
        Assert.Equal("", ScrubMath.NormalKey("…"));
    }
}
