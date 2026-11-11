using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.CompositionTestLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>Editorial's settings, word metrics and rows (re-implemented from docs/compositions/editorial.md), and the fits.</summary>
public class EditorialSettingTests
{
    [Fact]
    public void LinesCycleThroughFiveSettings()
    {
        Assert.Equal(LineSetting.ForLine(0), LineSetting.ForLine(5));
        Assert.Equal(LineSetting.ForLine(3), LineSetting.ForLine(-3));
        Assert.Equal(RowAlignment.Leading, LineSetting.ForLine(1).Rows);
        Assert.Equal(RowAlignment.Trailing, LineSetting.ForLine(3).Rows);
        Assert.Equal(5, Enumerable.Range(0, 5).Select(LineSetting.ForLine).Distinct().Count());
        _ = LineSetting.ForLine(int.MinValue);
    }

    [Fact]
    public void AHeldWordLeansHarderThanItsNeighbours()
    {
        LyricWord[] words = [new("we", 10, 10.3), new("keep", 10.3, 10.6), new("dreaming", 10.6, 12.4), new("on", 12.4, 12.7)];
        var line = EditorialSetting.Layout(words, new bool[words.Length], 100, 11, 2, wordTimed: true, editorial: true);
        Assert.Equal(4, line.Runs.Count);
        var dreaming = line.Runs[2].Metrics;
        Assert.True(dreaming.IsHero);
        Assert.True(dreaming.Uppercased, "setting 2 capitalises heroes");
        Assert.Equal("DREAMING", line.Runs[2].Label);
        Assert.False(dreaming.AlternateFace);
        foreach (var other in new[] { 0, 1, 3 })
        {
            Assert.False(line.Runs[other].Metrics.IsHero);
            Assert.InRange(line.Runs[other].Metrics.Size, 64, 70 * 1.1);
            Assert.InRange(line.Runs[other].Metrics.Rotation, -1.4 * 2.1 - 1e-9, 1.4 * 2.1 + 1e-9);
        }
        // A function word is never leaned on, however long it is held.
        Assert.Equal(0, EditorialSetting.Held(new LyricWord("the", 0, 5), 0.3));
        Assert.Equal(0, EditorialSetting.Held(new LyricWord("Youre,", 0, 5), 0.3));
        // The same line always sets the same way.
        var again = EditorialSetting.Layout(words, new bool[words.Length], 100, 11, 2, wordTimed: true, editorial: true);
        Assert.Equal(line.Runs.Select(run => run.Metrics), again.Runs.Select(run => run.Metrics));
    }

    [Fact]
    public void ALineTimedLineIsOneLevelPhrase()
    {
        var words = Words(["paper", "boats", "sailing"], 10, step: 1, length: 1);
        var line = EditorialSetting.Layout(words, new bool[3], 100, 11, 0, wordTimed: false, editorial: false);
        Assert.All(line.Runs, run =>
        {
            Assert.Equal(68, run.Metrics.Size, 9);
            Assert.Equal(0, run.Metrics.Rotation);
            Assert.Equal(0, run.Metrics.WaveOffset);
            Assert.False(run.Metrics.Uppercased);
            Assert.False(run.Metrics.AlternateFace);
        });
        // Never below the 11-point floor.
        var tiny = EditorialSetting.Layout(words, new bool[3], 10, 11, 0, wordTimed: false, editorial: false);
        Assert.All(tiny.Runs, run => Assert.Equal(11, run.Metrics.Size));
    }

    [Fact]
    public void GluedSyllablesSetAsOneRunAndPunctuationIsNotDrawn()
    {
        LyricWord[] words = [new("beau", 10, 10.2), new("ti", 10.2, 10.4), new("ful", 10.4, 10.8), new(",", 10.8, 10.8), new("night", 11, 11.5)];
        var line = EditorialSetting.Layout(words, [false, true, true, false, false], 100, 11, 0, wordTimed: true, editorial: true);
        Assert.Equal(2, line.Runs.Count);
        Assert.Equal("beautiful", line.Runs[0].Label);
        Assert.True(line.Runs[0].IsGlued);
        Assert.Equal([0, 4 / 9.0, 6 / 9.0, 1], line.Runs[0].Boundaries);
        Assert.Equal(new LyricWord("beautiful", 10, 10.8), line.Runs[0].Source);
        Assert.Equal(4, line.Tokens.Count);
    }

    [Fact]
    public void RowsFillInReadingOrderAndAlign()
    {
        SizeD[] slots = [new(100, 40), new(200, 50), new(150, 30), new(300, 40)];
        var centres = new PointD[4];
        var size = EditorialFlow.Place(slots, 400, 10, 5, RowAlignment.Leading, false, centres);
        // 100 + 10 + 200 = 310 fits; + 160 does not: rows [0,1], [2], [3]... 150 + 10 + 300 > 400.
        Assert.Equal(new SizeD(310, 50 + 5 + 30 + 5 + 40), size);
        Assert.Equal(new PointD(50, 25), centres[0]);
        Assert.Equal(new PointD(210, 25), centres[1]);
        Assert.Equal(new PointD(75, 55 + 15), centres[2]);
        // Right to left: the first slot sits rightmost, and leading is the right edge.
        EditorialFlow.Place(slots, 400, 10, 5, RowAlignment.Leading, true, centres);
        Assert.True(centres[0].X > centres[1].X);
        Assert.Equal(310 - 75, centres[2].X);
        // Centred rows.
        EditorialFlow.Place(slots, 400, 10, 5, RowAlignment.Center, false, centres);
        Assert.Equal(155, centres[2].X);
        // A slot wider than the stage keeps a row of its own and is reported at its real width.
        Assert.Equal(900, EditorialFlow.Place([new(900, 10)], 400, 10, 5, RowAlignment.Center, false, centres).Width);
    }

    [Fact]
    public void AGluedRunRevealsByTokenWhileItsFirstTokenCarriesTheRun()
    {
        LyricWord[] words = [new("夕", 10, 10.4), new("焼", 10.4, 10.8), new("け", 10.8, 11.2)];
        var line = EditorialSetting.Layout(words, [false, true, true], 100, 11, 0, wordTimed: true, editorial: true);
        var tokens = new EditorialTokenFrame[3];
        var options = new WordRevealOptions();
        for (var i = 0; i < 3; i++)
            tokens[i] = EditorialTokenFrame.Make(line.Tokens[i], 1, 10.5, 0, double.PositiveInfinity, false,
                                                 i > 0 ? words[i - 1].Start : double.NegativeInfinity, options);
        var opacities = new double[3];
        var run = EditorialRunFrame.Make(line.Runs[0], tokens, true, opacities);
        Assert.True(run.RevealsByToken);
        Assert.Equal(1, run.Presentation.Opacity);
        Assert.True(opacities[0] > 0.9);
        Assert.InRange(opacities[1], 0.25, 0.9);
        Assert.Equal(0, opacities[2]);
        Assert.Equal(opacities[0], run.ReadingOpacity(opacities));
    }

    [Fact]
    public void EmphasisSwellsWithinItsInset()
    {
        Assert.Equal(1.07 + 0.03, EditorialRunFrame.Swell(1, 1, 1, false), 9);
        Assert.Equal(1, EditorialRunFrame.Swell(1, 1, 1.2, true));
        Assert.Equal(1.06, EditorialRunFrame.CappedSwell(1.5, 1, 1000), 9);
        Assert.Equal(1.05, EditorialRunFrame.CappedSwell(1.05, 12, 100), 9);
    }

    [Fact]
    public void TheViewportFollowsTheMacsMetrics()
    {
        var metrics = ViewportMetrics.For(2560, 1440, 1.25, 0.075);
        Assert.Equal(2048 * 0.046 * 1.25, metrics.BaseSize, 9);
        Assert.Equal(new SizeD(2048, 1036.8), metrics.Stage);
        Assert.Equal(1, metrics.UserScale);
        Assert.Equal(124 * 2, ViewportMetrics.For(7680, 4320, 2, 0.075).BaseSize, 9);
        Assert.Equal(24, ViewportMetrics.For(320, 200, 1, 0.075).BaseSize, 9);
        Assert.Equal(1.5, ViewportMetrics.For(1920, 1080, 1, 0.5).UserScale);
        Assert.Equal(0.7, ViewportMetrics.For(1920, 1080, 1, 0.01).UserScale);
    }

    [Fact]
    public void ALineIsFittedToThreeQuartersOfTheStageAndNeverPastIt()
    {
        var stage = new SizeD(1000, 600);
        Assert.Equal(1, LyricStageFit.PresentationScale(new SizeD(400, 100), stage, 1));
        Assert.Equal(0.75, LyricStageFit.PresentationScale(new SizeD(1000, 100), stage, 1), 9);
        // The slider scales the fitted size, but never past the whole stage.
        Assert.Equal(1.0, LyricStageFit.PresentationScale(new SizeD(1000, 100), stage, 1.5), 9);
        Assert.Equal(1.5, LyricStageFit.PresentationScale(new SizeD(400, 100), stage, 1.5), 9);
        Assert.Equal(1, LyricStageFit.Scale(new SizeD(0, 10), stage));
    }

    [Fact]
    public void AWordIsFittedAroundItsOwnCentre()
    {
        var stage = new SizeD(1000, 600);
        Assert.Equal(1, WordStageFit.Scale(new SizeD(200, 50), stage, new PointD(500, 300), 1, 0, 10));
        Assert.Equal((100 - 10) * 2 / 400.0, WordStageFit.Scale(new SizeD(400, 50), stage, new PointD(100, 300), 1, 0, 10), 9);
        Assert.Equal(0, WordStageFit.Scale(new SizeD(400, 50), stage, new PointD(double.NaN, 300), 1, 0, 10));
    }

    [Fact]
    public void MergedRunsJoinGluedTokensAndTheirReadings()
    {
        LyricWord[] words = [new("夕焼け", 10, 10.6), new("の", 10.6, 10.8), new("道", 11, 11.4)];
        var readings = new Dictionary<LyricWord, string> { [words[0]] = "yuuyake", [words[1]] = "no" };
        var merged = LyricRunParts.Merged(words, [false, true, false], readings);
        Assert.Equal(["夕焼けの", "道"], merged.Words.Select(word => word.Text));
        Assert.Equal(new LyricWord("夕焼けの", 10, 10.8), merged.Words[0]);
        Assert.Equal("yuuyakeno", merged.Readings[merged.Words[0]]);
        Assert.Equal(words[..2], merged.Tokens[merged.Words[0]]);
        Assert.Equal([0, 0.75, 1], LyricRunParts.Boundaries(words[..2]));
    }
}
