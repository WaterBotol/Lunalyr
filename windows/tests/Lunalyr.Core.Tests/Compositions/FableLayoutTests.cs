using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.StageLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// Fable's per-line variety, its page layout and its phrase choreography, without rendering (the Mac's FableStyleTests
/// in FableLayoutTests.swift, Build 88: four shapes and one row per phrase, landings by role, pages by phrase).
/// </summary>
public class FableLayoutTests
{
    // Ported from FableLayoutTests.swift: testNeighbouringLinesNeverShareAShape
    [Fact]
    public void NeighbouringLinesNeverShareAShape()
    {
        for (var seed = -60; seed <= 400; seed++)
            Assert.True(FableStyle.ForLine(seed).Shape != FableStyle.ForLine(seed + 1).Shape, $"lines {seed} and {seed + 1} share a shape");
        Assert.Equal(4, Enumerable.Range(0, 4).Select(seed => FableStyle.ForLine(seed).Shape).Distinct().Count()); // Every shape comes round
    }

    // Ported from FableLayoutTests.swift: testNeighbouringPhrasesLandDifferently
    [Fact]
    public void NeighbouringPhrasesLandDifferently()
    {
        for (var seed = -10; seed <= 10; seed++)
        {
            for (var phrase = 0; phrase < 6; phrase++)
                Assert.NotEqual(FableGeometry.LandingFor(phrase, seed), FableGeometry.LandingFor(phrase + 1, seed));
            Assert.NotEqual(FableGeometry.LandingFor(0, seed), FableGeometry.LandingFor(0, seed + 1));
        }
    }

    // Ported from FableLayoutTests.swift: testStyleIsTheLinesOwn
    [Fact]
    public void StyleIsTheLinesOwn()
    {
        for (var seed = 0; seed < 50; seed++) Assert.Equal(FableStyle.ForLine(seed), FableStyle.ForLine(seed));
    }

    // Ported from FableLayoutTests.swift: testLayoutKeepsWordsApart
    [Fact]
    public void LayoutKeepsWordsApart()
    {
        (SizeD[] Sizes, int[][] Rows)[] pages =
        [
            ([new(120, 80)], [[0]]),
            ([new(1900, 90), new(150, 90)], [[0, 1]]),
            ([.. Enumerable.Range(0, 6).Select(i => new SizeD(90 + i * 37 % 120, 80 + i % 2 * 30))], [[0, 1, 2], [3, 4, 5]]),
            ([.. Enumerable.Range(0, 8).Select(i => new SizeD(60 + i * 53 % 200, 70 + i % 3 * 20))], [[0, 1, 2, 3], [4, 5, 6, 7]]),
            ([.. Enumerable.Range(0, 4).Select(_ => new SizeD(140, 170))], [[0], [1, 2, 3]]),
        ];
        foreach (var shape in Enum.GetValues<FableShape>())
        foreach (var (sizes, rows) in pages)
        foreach (var rtl in new[] { false, true })
        {
            var laid = FableGeometry.Arrange(sizes, rows, shape, rtl, 4, 6);
            Assert.Equal(sizes.Length, laid.Frames.Length);
            var box = new StageRect(-0.5, -0.5, laid.Size.Width + 1, laid.Size.Height + 1);
            for (var i = 0; i < laid.Frames.Length; i++)
            {
                var frame = laid.Frames[i];
                Assert.True(box.Contains(frame), $"{shape} word {i} leaves its page");
                Assert.Equal(sizes[i], frame.Size);
                for (var j = i + 1; j < laid.Frames.Length; j++)
                    Assert.False(frame.Inset(0.5, 0.5).Intersects(laid.Frames[j]), $"{shape}: words {i} and {j} overlap");
            }
            if (rows[0].Length > 1)
                Assert.True(laid.Frames[rows[0][0]].MidX > laid.Frames[rows[0][1]].MidX == rtl, $"{shape} reads the wrong way (rtl {rtl})");
            // Each phrase is its own row: a later row starts below the one before.
            if (rows.Length > 1)
            {
                var firstBottom = rows[0].Max(i => laid.Frames[i].MaxY);
                var secondTop = rows[1].Min(i => laid.Frames[i].MinY);
                Assert.True(secondTop >= firstBottom - 0.5, $"{shape}: rows overlap");
            }
        }
    }

    private static (FablePlan Plan, List<StageRun> Runs) Plan(IReadOnlyList<LyricWord> words)
    {
        var runs = Runs(words);
        return (FableGeometry.MakePlan(runs, StagePhrases.Analyse(runs)), runs);
    }

    // Ported from FableLayoutTests.swift: testLongLineTurnsThePageAtThePause
    [Fact]
    public void LongLineTurnsThePageAtThePause()
    {
        var (plan, runs) = Plan(Mixed);
        Assert.Equal([[0, 1], [2]], plan.Pages.Select(page => page.Phrases));
        Assert.Equal([["and", "we", "keep", "running"], ["through", "the", "city", "lights"]],
                     plan.Pages[0].Rows.Select(row => row.Select(id => runs[id].Text))); // One row per phrase
        Assert.Equal("running", plan.Hero is { } hero ? runs[hero].Text : null); // The hero is the longest-held focal word
        Assert.Single(plan.Roles, role => role == FableRole.Hero); // One hero per line
        // The old page turns before the next is begun, never before its own last word has landed.
        var turn = plan.Pages[1].TurnStart;
        Assert.True(turn <= runs[8].Start - StageMotion.Lead);
        Assert.True(turn > runs[7].Start);
        Assert.Equal(1, FableGeometry.PageTurn(0, plan, runs[8].Start + 0.3, double.PositiveInfinity), 1e-9); // Turned away
        Assert.False(FableGeometry.PageShown(0, plan, runs[8].Start + 0.3, double.PositiveInfinity));
        Assert.False(FableGeometry.PageShown(1, plan, runs[7].Start, double.PositiveInfinity)); // A page shows from its first word
    }

    // Ported from FableLayoutTests.swift: testPagesHoldAtMostTwoPhrases
    [Fact]
    public void PagesHoldAtMostTwoPhrases()
    {
        var (plan, runs) = Plan(Long);
        Assert.True(plan.Pages.Length > 2);
        foreach (var page in plan.Pages)
        {
            Assert.True(page.Phrases.Length <= FableGeometry.PagePhrases);
            Assert.True(page.Runs.Length <= FableGeometry.PageRuns);
        }
        Assert.Equal(Enumerable.Range(0, runs.Count), plan.Pages.SelectMany(page => page.Runs)); // Every word on one page, in order
    }

    // Ported from FableLayoutTests.swift: testStrokesFollowTheRoles
    [Fact]
    public void StrokesFollowTheRoles()
    {
        var (plan, runs) = Plan(Mixed);
        const double unit = FableGeometry.Unit;
        Assert.Equal(FableRole.Function, plan.Roles[5]);
        Assert.Equal(unit, plan.Strokes[5].Length, 1e-9); // "the" is a flick
        Assert.True(plan.Strokes[3].Length >= 1.0, "\"running\" is held, so written slowly");
        Assert.Equal(runs[1].Start - runs[0].Start, plan.Strokes[0].Length, 1e-9); // The run is one stroke
        Assert.Equal(plan.Strokes[2].Start, plan.Strokes[1].Start + plan.Strokes[1].Length, 1e-9);
        for (var index = 0; index < runs.Count; index++)
            Assert.Equal(runs[index].Start - StageMotion.Lead, plan.Strokes[index].Start, 1e-9); // Lands on its beat
        // Nothing is written before its beat; everything is written once its stroke ends.
        Assert.Equal(0, FableGeometry.Written(plan.Strokes[4], runs[4].Start - 0.1));
        Assert.Equal(1, FableGeometry.Written(plan.Strokes[4], plan.Strokes[4].Start + plan.Strokes[4].Length));
    }

    // Ported from FableLayoutTests.swift: testLandingsFollowTheRoles
    [Fact]
    public void LandingsFollowTheRoles()
    {
        var (plan, runs) = Plan(Mixed);
        FablePose Pose(int id, double dt, FableLanding landing = FableLanding.Pop) =>
            FableGeometry.Pose(runs[id], plan.Roles[id], landing, false, null, runs[id].Start + dt);
        Assert.Equal(FableRole.Hero, plan.Roles[3]);
        Assert.Equal(FableRole.Focal, plan.Roles[7]);
        Assert.True(Pose(3, 0).Scale < Pose(7, 0).Scale, "The hero pops from smaller");
        Assert.True(Pose(3, 0).Spin < Pose(7, 0).Spin, "and swings further");
        Assert.True(Pose(7, 0, FableLanding.Drop).Lift < 0, "A drop comes from above");
        Assert.True(Pose(6, 0).Lift > 0, "A content word hops up");
        Assert.Equal(1, Pose(6, 0).Scale);
        Assert.Equal(FablePose.Rest, Pose(5, 0)); // A function word is only written in
        Assert.True(plan.Writes[5], "the: written by the pen");
        Assert.True(plan.Writes[3], "running: held, so written as it is sung");
        Assert.False(plan.Writes[6], "city: lands with its hop, no stroke");
        foreach (var id in Enumerable.Range(0, runs.Count))
        {
            Assert.Equal(0, Pose(id, 0.6).Lift, 1e-9);
            Assert.Equal(1, Pose(id, 0.6).Scale, 1e-9);
            Assert.Equal(0, Pose(id, 0.6).Spin, 1e-9);
        }
    }

    // Ported from FableLayoutTests.swift: testFastLinePagesNeverOverlap
    [Fact]
    public void FastLinePagesNeverOverlap()
    {
        var (plan, runs) = Plan(Even("I can't stop now we run run run into the night so fast", step: 0.17));
        Assert.True(plan.Pages.Length > 1);
        for (var index = 1; index < plan.Pages.Length; index++)
        {
            var page = plan.Pages[index];
            var lastOld = plan.Pages[index - 1].Runs.Max(id => runs[id].Start);
            Assert.True(page.TurnStart >= Math.Min(lastOld, page.Start - StageMotion.Lead - 0.1) - 1e-9);
            Assert.True(page.TurnStart + page.TurnLength <= page.Start - StageMotion.Lead + 1e-9, $"page {index - 1} still showing");
            Assert.False(FableGeometry.PageShown(index - 1, plan, page.Start - StageMotion.Lead, double.PositiveInfinity));
        }
    }

    // Ported from FableLayoutTests.swift: testLineTimedLineIsOnePage
    [Fact]
    public void LineTimedLineIsOnePage()
    {
        var (plan, _) = Plan([.. "rain over the city can fly away tonight".Split(' ').Select(text => new LyricWord(text, 2, 6))]);
        Assert.True(plan.LineTimed);
        Assert.Single(plan.Pages);
        Assert.True(plan.Pages[0].Rows.Length > 1, "Still a row per phrase");
    }

    // Ported from FableLayoutTests.swift: testStrokeMaskStops
    [Fact]
    public void StrokeMaskStops()
    {
        Assert.Equal(0, FableGeometry.StrokeStops(0).Clear);
        Assert.Equal(1, FableGeometry.StrokeStops(1).Solid);
        var half = FableGeometry.StrokeStops(0.5);
        Assert.True(half.Solid <= half.Clear);
        Assert.True(half.Clear > 0);
        Assert.True(half.Solid < 1);
    }
}
