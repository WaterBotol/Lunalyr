using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.StageLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// Hinge's phrase choreography (a book: a page per phrase, turned on the spine), pure functions of the line and the time
/// (the Mac's HingeBroadsideChoreographyTests, Hinge part, and HingeBroadsideFastLineTests' pile-up check).
/// </summary>
public class HingeLayoutTests
{
    private static (StagePhrasing Phrasing, List<StageRun> Runs) Line(IReadOnlyList<LyricWord> words)
    {
        var runs = Runs(words);
        return (StagePhrases.Analyse(runs), runs);
    }

    // Ported from HingeBroadsideChoreographyTests.swift: testHingeEntranceFollowsTheRole
    [Fact]
    public void HingeEntranceFollowsTheRole()
    {
        var (p, runs) = Line(Mixed);
        var entrances = p.Words.Select(HingeGeometry.EntranceOf).ToList();
        foreach (var phrase in p.Phrases) Assert.Equal(HingeEntrance.Swing, entrances[phrase.Focal]);
        Assert.Equal(p.Phrases.Count, entrances.Count(e => e == HingeEntrance.Swing)); // One swing per phrase
        // "and we keep": a quick run, so even "and" and "we" flip, together.
        Assert.Equal([HingeEntrance.Flip, HingeEntrance.Flip, HingeEntrance.Flip], entrances.Take(3));
        // "the" in "through the city lights" inks in place.
        var the = runs.FindIndex(run => run.Text == "the" && run.Id > 4);
        Assert.Equal(HingeEntrance.Ink, entrances[the]);
        // A run moves as one: at any moment its members are at the same point of the slide.
        var groupStart = runs[0].Start;
        for (var time = 1.0; time <= 1.6 + 1e-9; time += 0.05)
        {
            var slides = Enumerable.Range(0, 3)
                .Select(i => HingeGeometry.Pose(HingeEntrance.Flip, runs[i].Start, groupStart, null, time, 1, false).Swing).ToList();
            Assert.Equal(slides[0], slides[1], 1e-9);
            Assert.Equal(slides[1], slides[2], 1e-9);
        }
    }

    // Ported from HingeBroadsideChoreographyTests.swift: testHingeWordsLandOnTheirOnsets
    [Fact]
    public void HingeWordsLandOnTheirOnsets()
    {
        foreach (var entrance in new[] { HingeEntrance.Swing, HingeEntrance.Flip, HingeEntrance.Ink })
        {
            Assert.Equal(0, HingeGeometry.Pose(entrance, 5, 5, null, 5 - 0.05, 1, false).Ink); // nothing before its onset
            Assert.True(HingeGeometry.Pose(entrance, 5, 5, null, 5.1, 1, false).Ink > 0.6, $"{entrance} is not inked on time");
        }
        // The focal word bursts in: oversized and blurred at first, settled half a second later.
        var burst = HingeGeometry.Pose(HingeEntrance.Swing, 5, 5, null, 5.02, 1, false);
        Assert.True(burst.Grow > 1.2);
        Assert.True(burst.Blur > 0.5);
        var swing = HingeGeometry.Pose(HingeEntrance.Swing, 5, 5, null, 5.5, 1, false);
        Assert.True(Math.Abs(swing.Swing) < HingeGeometry.SwingAngle * 0.1);
        Assert.Equal(1, swing.Grow, 0.02);
        Assert.True(swing.Blur < 0.05);
        // Held: it grows from the spine while held, then holds still.
        var early = HingeGeometry.Pose(HingeEntrance.Swing, 5, 5, 1.2, 5.3, 1, false);
        var late = HingeGeometry.Pose(HingeEntrance.Swing, 5, 5, 1.2, 6.2, 1, false);
        var later = HingeGeometry.Pose(HingeEntrance.Swing, 5, 5, 1.2, 8, 1, false);
        Assert.True(early.Grow < late.Grow);
        Assert.Equal(late.Grow, later.Grow, 1e-9);
        // Reduce Motion: no travel, no swing, a fade.
        var reduced = HingeGeometry.Pose(HingeEntrance.Swing, 5, 5, 1.2, 5.05, 1, true);
        Assert.Equal(0, reduced.Swing);
        Assert.Equal(0, reduced.Blur);
        Assert.Equal(1, reduced.Grow);
    }

    // Ported from HingeBroadsideChoreographyTests.swift: testHingePagesByPhrase
    [Fact]
    public void HingePagesByPhrase()
    {
        var (p, runs) = Line(Mixed);
        Assert.Equal([[0], [1], [2]], HingeGeometry.Pages(p));
        Assert.Equal([[0, 1], [2]], HingeGeometry.Pages(StagePhrases.Analyse(runs, lineTimed: true)));
    }

    // Ported from HingeBroadsideChoreographyTests.swift: testHingeTurnsAtPhrasesAndShowsAtMostTwoPages
    [Fact]
    public void HingeTurnsAtPhrasesAndShowsAtMostTwoPages()
    {
        var (p, _) = Line(Mixed);
        var starts = p.Phrases.Select(phrase => phrase.Start).ToList();
        double[] Turns(double time) => HingeGeometry.Turns(starts, false, time, 1);
        // Inside the first phrase nothing turns, whatever the words do.
        for (var time = 1.0; time < starts[1] - 0.2; time += 0.1) Assert.Equal([0.0, 0, 0], Turns(time));
        // Just after the second phrase starts, the first page is turning over.
        var turning = Turns(starts[1] + 0.1)[0];
        Assert.True(turning > 0.1);
        Assert.True(turning < 1);
        // At rest, between turns: at most two pages drawn.
        foreach (var time in new[] { 2.5, 4.4, 6.9 })
        {
            var begun = starts.Select(start => time >= start - StageMotion.Lead).ToList();
            var drawn = HingeGeometry.Instances(Turns(time), begun, reduceMotion: false).Where(i => i.Opacity > 0.02).ToList();
            Assert.True(drawn.Count <= 2, $"Too many pages at {time}s");
            Assert.True(drawn.Count(i => i.Opacity > HingeGeometry.ReadDim + 0.01) <= 1, "One page is the hero; the other is read");
        }
        // Edge-on a turning page is not drawn at all (never a squashed sliver).
        Assert.DoesNotContain(HingeGeometry.Instances([0.5, 0], reduceMotion: false), i => i.Page == 0);
    }

    // Ported from HingeBroadsideChoreographyTests.swift: testHingeReduceMotionCrossFades
    [Fact]
    public void HingeReduceMotionCrossFades()
    {
        var drawn = HingeGeometry.Instances([0.1, 0], reduceMotion: true);
        Assert.True(drawn.All(i => i.Angle == 0));
        Assert.Contains(drawn, i => i.Page == 0 && !i.Back);
        Assert.Contains(drawn, i => i.Page == 0 && i.Back);
    }

    // Ported from HingeBroadsideChoreographyTests.swift: HingeBroadsideFastLineTests.testFastLineIsPhrasedWithoutPileUps
    [Fact]
    public void FastLineIsPhrasedWithoutPileUps()
    {
        var words = "we were running out of time and never looked back again".Split(' ')
            .Select((text, i) => new LyricWord(text, 1 + i * 0.19, 1 + i * 0.19 + 0.17)).ToList();
        var (phrasing, _) = Line(words);
        Assert.True(phrasing.Phrases.Count >= 3);
        // At most two pages drawn at any frame between turns.
        var starts = phrasing.Phrases.Select(phrase => phrase.Start).ToList();
        for (var time = 1.0; time <= 3.2; time += 1.0 / 30)
        {
            var turns = HingeGeometry.Turns(starts, false, time, 2.6);
            var begun = starts.Select(start => time >= start - StageMotion.Lead).ToList();
            var drawn = HingeGeometry.Instances(turns, begun, reduceMotion: false).Count(i => i.Opacity > 0.05);
            Assert.True(drawn <= 3, $"Pages pile up at {time}s");
        }
    }
}
