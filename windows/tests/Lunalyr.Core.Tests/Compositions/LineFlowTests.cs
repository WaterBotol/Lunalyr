using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.CompositionTestLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>How lines hand over on the stage: exits, held duet lines, previews and line-timed phrases.</summary>
public class LineFlowTests
{
    private static readonly StageRules Still = new(RollsWordTimed: false);
    private static readonly StageRules Rolling = new(RollsWordTimed: true);

    // Ported from LyricLayerFlowTests.swift: testAWholePhraseNeverLingersOverTheNextWordTimedLine
    [Fact]
    public void AWholePhraseNeverLingersOverTheNextWordTimedLine()
    {
        LyricLine[] lines =
        [
            Line(["the", "phrase"], 9, length: 1, wordTimed: false),
            Line(["the", "next", "words"], 10.5),
            Line(["a", "phrase"], 11, wordTimed: false),
        ];
        Assert.Equal(0.5, LineFlow.TimingHandoffPresence(0, lines, 10.41), 0.001);
        Assert.False(LineFlow.IsLeaving(0, 1, lines, 10.55, Rolling));
        Assert.False(LineFlow.IsLeaving(0, 2, lines, 11, Rolling));
    }

    // Ported from LyricLayerFlowTests.swift: testAnUpcomingPreviewOnlyShowsAnIndividualWord
    [Fact]
    public void AnUpcomingPreviewOnlyShowsAnIndividualWord()
    {
        var packed = Line(["Hold the light"], 10);
        var separated = Line(["Hold", "the", "light"], 10);
        Assert.False(LyricRunParts.CanPreviewFirstRun(packed.Words, []));
        Assert.False(LyricRunParts.CanPreviewFirstRun(separated.Words, [false, true, true]));
        Assert.True(LyricRunParts.CanPreviewFirstRun(separated.Words, []));
        Assert.False(PreviewWords.IsPreviewWord("夕焼けの帰り道"));

        static double Opacity(LyricLine line, double time) =>
            WordPresentation.Make(line.Words[0], 0, 0, time, new WordRevealOptions()).Opacity;
        Assert.Equal(0, Opacity(packed, 9.9));
        Assert.True(Opacity(separated, 9.9) > 0);
        Assert.True(Opacity(packed, 10.2) > 0);
    }

    private static readonly LineFit FitsAsIs = new(new SizeD(1200, 700), 1, 1);

    private static StackedLine Item(double height, double? presence = 1, double? held = null, LineFit? fit = null) =>
        new(new SizeD(1000, height), true, fit ?? FitsAsIs, presence, held);

    private static double[] Offsets(params StackedLine[] items)
    {
        var offsets = new double[items.Length];
        LineStack.Offsets(items, 700, 20, offsets);
        return offsets;
    }

    // Ported from LyricLayerFlowTests.swift: testAHeldLineRisesClearOfTheLinesInTheMiddle
    [Fact]
    public void AHeldLineRisesClearOfTheLinesInTheMiddle()
    {
        const double gap = 20;
        // Above the line that started under it, by that line's own height: its bottom a gap above the other's top,
        // at 0.7 of its size.
        var placed = Offsets(Item(100, held: 1), Item(200));
        Assert.Equal(0, placed[1]);
        Assert.Equal(-100 - gap, placed[0] + 100 * 0.7 / 2, 9);
        // A taller line in the middle pushes it higher.
        var tall = Offsets(Item(100, held: 1), Item(420));
        Assert.Equal(-210 - gap, tall[0] + 35, 9);
        // Fitted lines count at the size they are drawn.
        var shrunk = new LineFit(new SizeD(500, 700), 1, 1);
        var fitted = Offsets(Item(100, held: 1), Item(200, fit: shrunk));
        Assert.Equal(-100 * 0.5 - gap, fitted[0] + 35, 6);
        // Never past the top of the stage.
        var full = Offsets(Item(100, held: 1), Item(650));
        Assert.Equal(-350 + 35, full[0], 9);
        // On its way there, it travels with its shift.
        var half = Offsets(Item(100, held: 0.5), Item(200));
        Assert.Equal(0.5 * (-100 - gap - 100 * 0.85 / 2), half[0], 9);
        Assert.Equal(0, Offsets(Item(100, held: 0), Item(200))[0]);
        // The lines in the middle count by how present they are; a preview does not count.
        var arriving = Offsets(Item(100, held: 1), Item(200, presence: 0.5), Item(600, presence: null));
        Assert.Equal(-50 - gap, arriving[0] + 35, 9);
        Assert.Equal(0, arriving[2]);
        // Two held lines stack, the latest nearest the middle.
        var two = Offsets(Item(100, held: 1), Item(100, held: 1), Item(200));
        Assert.Equal(-100 - gap, two[1] + 35, 9);
        Assert.Equal(two[1] - 35 - gap, two[0] + 35, 9);
        // Lines that are not held, and a line not drawn, stay centred.
        Assert.Equal([0, 0], Offsets(Item(200), Item(200, presence: 0.3)));
        Assert.Equal([0, 0], Offsets(new StackedLine(SizeD.Zero, false, FitsAsIs, null, 1), Item(200)));
    }

    // Ported from LyricLayerFlowTests.swift: testAHeldLineStaysAsideWhileLaterLinesStartUnderIt. The Mac renders the
    // real layer and records the held lines its stack placed; here the same rules the layer draws by are asked
    // directly (which earlier lines are drawn, how far each held one has moved aside, and where the stack puts it).
    [Fact]
    public void AHeldLineStaysAsideWhileLaterLinesStartUnderIt()
    {
        var a = Line(["I", "will", "wait", "for", "you"], 10, step: 0.8, length: 0.8);
        var b = Line(["Oh", "yeah"], 12, step: 0.5, length: 0.5);
        var c = Line(["and", "on"], 13.4, step: 0.5, length: 0.5);
        var ringing = Line(["so", "long"], 20, step: 0.6, length: 0.63);
        var next = Line(["next", "line"], 21.2);
        LyricLine[] lines = [a, b, c, ringing, next];
        var index = new LyricLineIndex(lines);
        List<(double Travel, double Offset)> Held(double time)
        {
            var current = index.LineAt(time)!.Value;
            var drawn = new List<(int Row, double? Held)>();
            for (var row = current - 3; row < current; row++)
                if (LineFlow.IsLeaving(row, current, lines, time, Still)) drawn.Add((row, LineFlow.HeldShift(row, lines, time)));
            drawn.Add((current, null));
            var items = drawn.Select(line =>
            {
                var pose = new LyricPhraseTiming(lines[line.Row], LineFlow.HandOver(line.Row, lines, Still), time);
                return new StackedLine(new SizeD(800, 60), true, FitsAsIs, pose.Progress * (1 - pose.Exit), line.Held);
            }).ToArray();
            var offsets = new double[items.Length];
            LineStack.Offsets(items, 450, 12, offsets);
            return [.. drawn.Select((line, k) => (line.Held, offsets[k])).Where(pair => pair.Held is not null)
                .Select(pair => (pair.Held!.Value, pair.Item2))];
        }
        foreach (var time in new[] { 13.3, 13.45, 13.7 })
        {
            var held = Held(time);
            Assert.Single(held);
            Assert.Equal(1, held[0].Travel, 0.01);
            Assert.True(held[0].Offset < -10, $"above the middle at {time}");
        }
        // A last syllable 30 ms over the next line is no duet: nothing moves aside.
        foreach (var time in new[] { 21.25, 21.4, 21.6 }) Assert.Empty(Held(time));
    }

    // Ported from LyricLayerFlowTests.swift: testHugeOrInvalidLineTimesNeverTrapTheSeeds
    [Fact]
    public void HugeOrInvalidLineTimesNeverTrapTheSeeds()
    {
        Assert.Equal(12_346UL, LyricPhraseTiming.TimeSeed(12.3456));
        foreach (var start in new[] { 1e20, -1e20, double.PositiveInfinity, double.NaN })
        {
            _ = LyricPhraseTiming.TimeSeed(start);
            var line = new LyricLine { Start = start, End = start, Text = "hi" };
            _ = new LyricPhraseTiming(line, double.PositiveInfinity, 3);
            _ = new VocalGap(start, start + 10, 1).InterludeCue(start + 1, 0.5);
        }
    }

    // Ported from LyricMotionPolishTests.swift: testAWordSungLateInALongLineStillAppears
    [Fact]
    public void AWordSungLateInALongLineStillAppears()
    {
        var late = Words(["a", "slow", "line"], 10).Append(new LyricWord("finally", 21, 21.4)).ToArray();
        var longLine = Line(late, end: 30);
        var pose = new LyricPhraseTiming(longLine, 40, 21);
        var exit = LineFlow.StageExitStart(longLine, pose);
        Assert.True(exit > 21.4, "the last word must be on screen while it is sung");
        Assert.Equal(21.4 + WallpaperMotion.HoldAfterSung, exit, 9);
    }

    // Ported from LyricMotionPolishTests.swift: testAHeldLastWordIsNotCutWhileItIsSung
    [Fact]
    public void AHeldLastWordIsNotCutWhileItIsSung()
    {
        var held = Words(["hold", "this"], 10).Append(new LyricWord("note", 14, 20)).ToArray();
        var longLine = Line(held, end: 22);
        Assert.True(LineFlow.StageExitStart(longLine, new LyricPhraseTiming(longLine, 40, 15)) > 20);
    }

    // Ported from LyricMotionPolishTests.swift: testAVeryLongHeldNoteIsStillCapped
    [Fact]
    public void AVeryLongHeldNoteIsStillCapped()
    {
        var endless = Line([new LyricWord("ooh", 10, 60)], end: 60);
        var exit = LineFlow.StageExitStart(endless, new LyricPhraseTiming(endless, 80, 11));
        Assert.Equal(18 + WallpaperMotion.HoldAfterSung, exit, 9);
    }

    // Ported from LyricMotionPolishTests.swift: testTheFrameRateFollowsTheExitTheRendererDraws (the exits; the frame
    // rate the Mac's stage clock picks from them is the engine's)
    [Fact]
    public void TheFrameRateFollowsTheExitTheRendererDraws()
    {
        var lingering = Line(Words(["gone", "soon"], 10), end: 30);
        LyricLine[] lines = [lingering, Line(Words(["next"], 40))];
        var handOver = LyricPhraseTiming.HandOver(0, lines);
        var phrase = LyricPhraseTiming.ExitStartOf(lingering, handOver);
        var drawn = LineFlow.RenderedExitStart(0, lines, handOver, rolls: false, fillsStage: false);
        Assert.True(drawn < phrase);
        Assert.Equal(LineFlow.StageExitStart(lingering, new LyricPhraseTiming(lingering, handOver, 12)), drawn);
        // Rolling lines, and line-timed lines outside the stage layouts, leave at the phrase's exit.
        Assert.Equal(phrase, LineFlow.RenderedExitStart(0, lines, handOver, rolls: true, fillsStage: false));
        LyricLine[] lineTimed = [lines[0] with { WordTimed = false }, lines[1]];
        Assert.Equal(phrase, LineFlow.RenderedExitStart(0, lineTimed, handOver, rolls: false, fillsStage: false));
        Assert.Equal(drawn, LineFlow.RenderedExitStart(0, lineTimed, handOver, rolls: false, fillsStage: true));
    }

    // Ported from LyricMotionPolishTests.swift: testAHingeLineIsDrawnUntilItsFoldEnds
    [Fact]
    public void AHingeLineIsDrawnUntilItsFoldEnds()
    {
        var first = Line(Words(["fold", "away"], 10), end: 11);
        LyricLine[] lines = [first, Line(Words(["next"], 11.5))];
        var late = LyricPhraseTiming.ExitStartOf(first, 11.5) + 0.85;
        Assert.False(LineFlow.IsLeaving(0, 1, lines, late, Still));
        Assert.True(LineFlow.IsLeaving(0, 1, lines, late, Still, LyricPhraseTiming.HingeExitSpan));
    }

    // Ported from LyricMotionPolishTests.swift: testAPreviewedFirstWordAndTheRestOfTheLineMakeTheWholeLine (the held
    // first word's opacity; the Mac's pixel count of the split line is covered by CompositionRenderTests)
    [Fact]
    public void APreviewedFirstWordIsNeverFainterThanItsPreview()
    {
        Assert.Equal(0.25, LineFlow.HeldFirstRunOpacity(0.02));
        Assert.Equal(0.8, LineFlow.HeldFirstRunOpacity(0.8));
    }

    // Ported from LyricMotionPolishTests.swift: testALineLeavesBeforeTheNextOneArrivesButNotBeforeItsLastWord
    [Fact]
    public void ALineLeavesBeforeTheNextOneArrivesButNotBeforeItsLastWord()
    {
        var early = Line(Words(["done", "early"], 10), end: 12);
        Assert.Equal(11.75, LyricPhraseTiming.ExitStartOf(early, 12), 9);
        var busy = Line([new LyricWord("right", 10, 11), new LyricWord("through", 11, 11.95)], end: 12);
        Assert.Equal(12.05, LyricPhraseTiming.ExitStartOf(busy, 12), 9);
        var shortLine = Line(Words(["short"], 10), end: 10.4);
        Assert.Equal(10.52, LyricPhraseTiming.ExitStartOf(shortLine, 20), 9);
    }

    // Ported from LyricMotionPolishTests.swift: testALeavingLineLiftsAsTheNextArrives
    [Fact]
    public void ALeavingLineLiftsAsTheNextArrives()
    {
        LyricLine[] lines = [Line(Words(["first"], 10), end: 12), Line(Words(["second"], 12), end: 14)];
        Assert.Equal(0, LineFlow.IncomingArrival(0, lines, 11.9, Still));
        double last = 0;
        for (var frame = 0; frame <= 60; frame++)
        {
            var value = LineFlow.IncomingArrival(0, lines, 12 + frame / 60.0, Still);
            Assert.True(Math.Abs(value - last) < 0.2);
            last = value;
        }
        Assert.True(last > 0.95);
    }

    // Ported from LyricMotionPolishTests.swift: testBackingVocalsKeepToTheBandACreditLeavesFree
    [Fact]
    public void BackingVocalsKeepToTheBandACreditLeavesFree()
    {
        var group = BackingVocals.Split(Words(["(ooh)", "la"], 12), []).Backing[0];
        CreditBand[] topCredit = [new(10, 16, true)];
        Assert.False(BackingVocalLayout.OnTop(group, true, topCredit));
        Assert.False(BackingVocalLayout.OnTop(group, false, topCredit));
        Assert.True(BackingVocalLayout.OnTop(group, true, []));
        // Both bands busy: it keeps its own.
        CreditBand[] both = [.. topCredit, new(10, 16, false)];
        Assert.True(BackingVocalLayout.OnTop(group, true, both));
        // A credit long gone does not move it.
        Assert.True(BackingVocalLayout.OnTop(group, true, [new CreditBand(0, 5, true)]));
    }

    // Ported from RollingStackTests.swift: testARollingLineHandsItsWordsOnInsteadOfLeavingTwice
    [Fact]
    public void ARollingLineHandsItsWordsOnInsteadOfLeavingTwice()
    {
        var a = new LyricLine { Start = 10, End = 11, Text = "hold on", WordTimed = true,
            Words = [new("hold", 10, 10.5), new("on", 10.5, 11)] };
        var b = new LyricLine { Start = 11, End = 11.4, Text = "now", WordTimed = true, Words = [new("now", 11, 11.4)] };
        var c = new LyricLine { Start = 11.4, End = 13, Text = "we fly tonight", Words = [new("we fly tonight", 11.4, 13)] };
        LyricLine[] lines = [a, b, c];
        for (var step = 0; step <= 120; step++)
        {
            var time = 11.4 + step / 100.0;
            for (var row = 0; row <= 1; row++)
                Assert.False(LineFlow.IsLeaving(row, 2, lines, time, Rolling), $"the rolling stage reappeared at {time}");
        }
        Assert.Equal(1, LineFlow.TimingHandoffPresence(0, lines, 11.1));
        Assert.Equal(1, LineFlow.TimingHandoffPresence(1, lines, 11.1));
        Assert.Equal(0.5, LineFlow.TimingHandoffPresence(1, lines, 11.31), 0.001);
        Assert.Equal(0, LineFlow.TimingHandoffPresence(1, lines, 11.4));
        Assert.False(LineFlow.IsLeaving(0, 1, lines, 11.1, Rolling));
        // A current rolling stage carries on into the next rolling line; an outgoing one always fades on its own exit.
        Assert.Equal(1, LineFlow.RollingPresence(true, false, 0.4));
        Assert.Equal(0.6, LineFlow.RollingPresence(true, true, 0.4), 12);
        Assert.Equal(0.6, LineFlow.RollingPresence(false, false, 0.4), 12);
    }

    [Fact]
    public void TheNextLinesFirstWordPreviewsOnlyOnceTheCurrentLineIsSung()
    {
        LyricLine[] lines = [Line(["we", "keep", "going"], 10), Line(["onward", "now"], 12)];
        var sheet = LyricSheet.Build(lines);
        // "going" begins at 11; nothing previews until then, and only within the lead of the next onset.
        Assert.Null(LineFlow.NextLinePreview(0, sheet, 10.9, Still));
        Assert.Null(LineFlow.NextLinePreview(0, sheet, 11.3, Still));
        Assert.Equal(1, LineFlow.NextLinePreview(0, sheet, 11.7, Still));
        Assert.Null(LineFlow.NextLinePreview(0, sheet, 12, Still));
        // A rolling stage carries its words on into the next line instead.
        Assert.Null(LineFlow.NextLinePreview(0, sheet, 11.7, Rolling));
        Assert.Equal(11, sheet.PreviewSince[0]);
    }

    [Fact]
    public void ALineTimedLineKeepsTheDimmingItHadWhenThePreviewHandedOver()
    {
        LyricLine[] lines = [TimedLine("Paper boats are sailing", 10, 14), TimedLine("Every window glowing", 14, 18)];
        var sheet = LyricSheet.Build(lines);
        Assert.Equal(1, LineFlow.NextLinePreview(0, sheet, 13.8, Still));
        var carried = LineFlow.CarriedYield(0, sheet, 14.2, Still);
        Assert.True(carried > 0.9, $"{carried}");
        Assert.Equal(0, LineFlow.CarriedYield(0, sheet, 13.9, Still));

        static LyricLine TimedLine(string text, double start, double end) =>
            LyricLines.MakeLine(text, start, end) with { EndFromNextLine = true };
    }
}
