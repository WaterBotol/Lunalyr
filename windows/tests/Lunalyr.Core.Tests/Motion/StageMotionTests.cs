using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>
/// The stage layouts' shared motion: arrivals that start from rest, ranks that carry their speed, a live word that
/// hands over gradually, one exit for every layout, and Reduce Motion as dissolves (the maths parts of the Mac's
/// StageMotionTests).
/// </summary>
public class StageMotionTests
{
    private static List<StageRun> Runs(int count, double step = 0.5, double start = 1)
    {
        var words = Enumerable.Range(0, count)
            .Select(i => new LyricWord($"word{i}", start + i * step, start + i * step + step * 0.9)).ToArray();
        return StageRun.Make(words, new bool[count]);
    }

    private static double[] Ranks(List<StageRun> line, double t, bool reduceMotion = false)
    {
        var count = StageMotion.ShownCount(line, t);
        var ranks = new double[count];
        StageMotion.Ranks(line, count, t, 1, 3, reduceMotion, ranks);
        return ranks;
    }

    // Ported from StageMotionTests.swift: testArrivalStartsFromRestOvershootsALittleAndLands
    [Fact]
    public void ArrivalStartsFromRestOvershootsALittleAndLands()
    {
        const double onset = 2.0;
        const double begin = onset - StageMotion.Lead;
        Assert.Equal(0, StageMotion.Arrival(onset, begin - 0.01));
        Assert.True(StageMotion.Arrival(onset, begin + 0.005) < 0.01); // Starts from rest
        double peak = 0;
        for (var t = begin; t < begin + 1; t += 0.002) peak = Math.Max(peak, StageMotion.Arrival(onset, t));
        Assert.True(peak > 1.03);
        Assert.True(peak < 1.12); // A little overshoot
        Assert.Equal(1, StageMotion.Arrival(onset, begin + 1.5)); // Lands exactly
        // A fast line arrives faster.
        Assert.True(StageMotion.Arrival(onset, begin + 0.08, speed: 2.5) > StageMotion.Arrival(onset, begin + 0.08));
        // Reduce Motion: a step, which the layout dissolves.
        Assert.Equal(0, StageMotion.Arrival(onset, begin - 0.001, reduceMotion: true));
        Assert.Equal(1, StageMotion.Arrival(onset, begin, reduceMotion: true));
    }

    // Ported from StageMotionTests.swift: testRanksCarryTheirSpeedThroughEveryOnset
    [Fact]
    public void RanksCarryTheirSpeedThroughEveryOnset()
    {
        var line = Runs(6, step: 0.45);
        const double dt = 0.001;
        double Rank0(double t) => Ranks(line, t) is { Length: > 0 } r ? r[0] : 0;
        foreach (var run in line.Skip(1))
        {
            var onset = run.Start - StageMotion.Lead;
            var before = (Rank0(onset) - Rank0(onset - dt)) / dt;
            var after = (Rank0(onset + dt) - Rank0(onset)) / dt;
            Assert.True(Math.Abs(after - before) < 0.5, $"Rank speed steps at the onset of {run.Text}");
        }
    }

    // Ported from StageMotionTests.swift: testTheLiveWordRestylesOverTheHandOverNotInOneFrame
    [Fact]
    public void TheLiveWordRestylesOverTheHandOverNotInOneFrame()
    {
        Assert.Equal(1, StageMotion.Liveness(0));
        Assert.Equal(1, StageMotion.Liveness(0.15));
        Assert.Equal(0, StageMotion.Liveness(0.85));
        Assert.Equal(0, StageMotion.Liveness(2));
        // Across the next word's arrival, frame by frame at 60 fps.
        var line = Runs(3, step: 0.6);
        double? previous = null;
        double largest = 0;
        for (var frame = 0; frame < 120; frame++)
        {
            var t = 1.4 + frame / 60.0;
            var live = StageMotion.Liveness(Ranks(line, t)[0]);
            if (previous is { } p) largest = Math.Max(largest, Math.Abs(live - p));
            previous = live;
        }
        Assert.True(largest < 0.3, "The old boolean flipped every live-only property at once (a step of 1)");
        Assert.Equal(0, previous ?? 1); // The old word ends fully restyled
    }

    // Ported from StageMotionTests.swift: testEveryStageLayoutLeavesOnTheSharedLineExit
    [Fact]
    public void EveryStageLayoutLeavesOnTheSharedLineExit()
    {
        const double exit = 10.0;
        Assert.Equal(0, StageMotion.Leave(exit, exit));
        Assert.Equal(1, StageMotion.Leave(exit + WallpaperMotion.LineExit, exit));
        Assert.True(StageMotion.Leave(exit + 0.2, exit) > 0.85);
        Assert.Equal(LyricMotion.Settle(0.2, WallpaperMotion.LineExit), StageMotion.Leave(exit + 0.2, exit), 1e-9);
    }

    // Ported from StageMotionTests.swift: testHingeFoldsShutWithinTheExitSpan
    [Fact]
    public void HingeFoldsShutWithinTheExitSpan()
    {
        const double exit = 10.0;
        foreach (var order in new[] { 0.0, 0.5, 1.0 })
            Assert.True(StageMotion.HingeFold(order, exit + LyricPhraseTiming.ExitSpan, exit) >= 0.99);
        Assert.True(StageMotion.HingeFold(1, exit + 0.2, exit) < StageMotion.HingeFold(0, exit + 0.2, exit)); // Oldest first
        Assert.True(LyricMotion.Settle(LyricPhraseTiming.ExitSpan, StageMotion.HingeClose) >= 0.999);
    }

    // Ported from StageMotionTests.swift: testReduceMotionDissolvesEachStepAndFableNeedsNone
    [Fact]
    public void ReduceMotionDissolvesEachStepAndFableNeedsNone()
    {
        var line = Runs(4, step: 0.6);
        var onset = line[2].Start;
        var at = onset + 0.05;
        var shown = StageMotion.ShownCount(line, at);
        var step = StageMotion.ReducedDissolve(line, shown, layoutMovesWords: true, at, reduceMotion: true);
        Assert.NotNull(step);
        Assert.True(step > 0 && step < 1);
        Assert.Null(StageMotion.ReducedDissolve(line, shown, true, at, reduceMotion: false));
        Assert.Null(StageMotion.ReducedDissolve(line, shown, layoutMovesWords: false, at, reduceMotion: true)); // Fable
        var later = StageMotion.ShownCount(line, onset + 0.3);
        Assert.Null(StageMotion.ReducedDissolve(line, later, true, onset + 0.3, true)); // Over after the reduced dissolve
        // Ranks and arrivals are steps, so nothing travels between dissolves.
        Assert.Equal([2.0, 1, 0], Ranks(line, at, reduceMotion: true));
    }

    // Ported from StageMotionTests.swift: testGluedSyllablesFadeInRatherThanCut (the opacity part; the coloured text is
    // view code)
    [Fact]
    public void GluedSyllablesFadeInRatherThanCut()
    {
        LyricWord[] words = [new("beau", 1, 1.2), new("ti", 1.3, 1.5), new("ful", 1.6, 2)];
        var run = StageRun.Make(words, [false, true, true])[0];
        Assert.Equal(3, run.Parts.Count);
        Assert.Equal("beautiful", run.Text);
        Assert.Equal(1, StageRun.PartOpacity(0, run.Parts, 0.97)); // The first syllable arrives with its word
        Assert.Equal(0, StageRun.PartOpacity(1, run.Parts, 1.26));
        var midway = StageRun.PartOpacity(1, run.Parts, 1.33);
        Assert.True(midway > 0.05);
        Assert.True(midway < 0.99); // Fading, not cut in
        Assert.Equal(1, StageRun.PartOpacity(1, run.Parts, 1.3 - 0.03 + StageMotion.SyllableFade));
    }

    // Ported from StageMotionTests.swift: testMosaicNeverShowsBothCopiesAtDifferentSizes
    [Fact]
    public void MosaicNeverShowsBothCopiesAtDifferentSizes()
    {
        foreach (var ratio in new[] { 0.2, 0.26 })
        {
            for (var step = 0; step <= 100; step++)
            {
                var morph = StageMotion.MosaicMorph(step / 100.0, ratio);
                if (morph.BigOpacity > 0.01 && morph.SmallOpacity > 0.01)
                    Assert.Equal(ratio, morph.BigScale, 0.005);
            }
            Assert.Equal(1, StageMotion.MosaicMorph(0, ratio).BigScale);
            Assert.Equal(0, StageMotion.MosaicMorph(0, ratio).SmallOpacity);
            Assert.Equal(0, StageMotion.MosaicMorph(1, ratio).BigOpacity);
            Assert.Equal(1, StageMotion.MosaicMorph(1, ratio).SmallOpacity);
        }
    }

    [Fact]
    public void UnspacedRunsStayShortEnoughToReadAsWords()
    {
        LyricWord[] words = [new("今", 1, 1.2), new("日", 1.2, 1.4), new("も", 1.4, 1.6), new("晴", 1.6, 1.8)];
        var runs = StageRun.Make(words, [false, true, true, true]);
        Assert.Equal(["今日も", "晴"], runs.Select(r => r.Text));
        Assert.Equal([false, true, true, false], StageRun.RunGlue(words, [false, true, true, true]));
    }
}
