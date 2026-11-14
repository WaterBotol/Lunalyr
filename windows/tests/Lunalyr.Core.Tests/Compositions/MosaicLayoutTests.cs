using Lunalyr.Core.Compositions;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.StageLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// Mosaic decluttered: the colour bar only ever one word's, fast lines without pile-ups, words in by their onset, glued
/// syllables set as one word, and long lines varied (the Mac's MosaicLayoutTests, same inputs and values; the drawn bar
/// is checked in MosaicRenderTests).
/// </summary>
public class MosaicLayoutTests
{
    private static readonly SizeD Stage = new(1400, 520);
    private const double Unit = 64 * 0.8;

    private static List<(string Name, List<StageRun> Runs)> Lines =>
    [
        ("fast", Runs(Fast.Words, Fast.Glue)),
        ("burst", Runs(Burst.Words, Burst.Glue)),
        ("long", Runs(Mixed)),
        ("short", Runs(Even("we keep dreaming under neon rain"))),
    ];

    private static double Speed(List<StageRun> runs) => StageShared.LineSpeed([.. runs.Select(run => run.Start)]);

    // Ported from MosaicLayoutTests.swift: testBarIsAlwaysOneWord
    [Fact]
    public void BarIsAlwaysOneWord()
    {
        foreach (var (name, runs) in Lines)
        foreach (var beat in MosaicGeometry.Beats(runs).Where(beat => beat.Kind == MosaicKind.Bar))
            Assert.True(beat.Count == 1, name);
    }

    private readonly record struct Box(int Beat, StageRect Rect, double Opacity);

    /// <summary>Where each beat is drawn at <paramref name="time"/>, by Mosaic's own model, each beat's size estimated from its text.</summary>
    private static List<Box> Boxes(List<StageRun> runs, double time, double exitStart = double.PositiveInfinity)
    {
        var beats = MosaicGeometry.Beats(runs);
        var speed = Speed(runs);
        var moves = MosaicGeometry.Moves(beats, runs, time, speed, exitStart, false, runs.Count);
        var ranks = MosaicGeometry.Ranks(moves);
        var bounds = new StageRect(0, 0, Stage.Width, Stage.Height);
        var step = MosaicGeometry.StackStep(Unit, false);
        var result = new List<Box>();
        for (var g = 0; g < beats.Count; g++)
        {
            var beat = beats[g];
            var appear = MosaicGeometry.Appear(runs[beat.From], time, speed, false, runs.Count);
            var fade = MosaicGeometry.StackFade(ranks[g]);
            if (!(appear > 0.001 && fade > 0.001)) continue;
            var big = MosaicGeometry.BigSize(beat.Kind);
            var text = string.Join(" ", Enumerable.Range(beat.From, beat.Count).Select(id => runs[id].Text));
            var width = Math.Min(Stage.Width * 0.9, StageShared.Count(text) * 0.55 * big * Unit);
            var size = new SizeD(width, big * Unit * 1.1);
            var morph = StageMotion.MosaicMorph(moves[g], 0.6 / big);
            var centre = MosaicGeometry.Centre(size, moves[g], ranks[g], 0, step, 0, false, bounds);
            var scale = morph.BigScale;
            var rect = new StageRect(centre.X - size.Width / 2, centre.Y - size.Height * scale / 2, size.Width * scale, size.Height * scale);
            var opacity = fade * Math.Max(Math.Min(1, appear * 1.6) * morph.BigOpacity, morph.SmallOpacity);
            result.Add(new Box(g, rect, opacity));
        }
        return result;
    }

    // Ported from MosaicLayoutTests.swift: testFastLinesHaveNoOverlappingWords
    [Fact]
    public void FastLinesHaveNoOverlappingWords()
    {
        var fast = Lines[0].Runs;
        Assert.True(fast.Count >= 10);
        Assert.True(fast[^1].Start - fast[0].Start <= 2.2);
        foreach (var (name, runs) in Lines)
        {
            var exit = runs.Max(run => run.End) + 0.3;
            var time = runs[0].Start - 0.3;
            while (time < exit + 0.6)
            {
                var boxes = Boxes(runs, time, exit).Where(box => box.Opacity > 0.3).ToList();
                for (var a = 0; a < boxes.Count; a++)
                {
                    var rect = boxes[a].Rect;
                    Assert.True(rect.MinX >= -1, $"{name} {time}: off stage");
                    Assert.True(rect.MaxX <= Stage.Width + 1, $"{name} {time}: off stage");
                    for (var b = a + 1; b < boxes.Count; b++)
                    {
                        var p = rect.Inset(2, 2);
                        var q = boxes[b].Rect.Inset(2, 2);
                        var w = Math.Min(p.MaxX, q.MaxX) - Math.Max(p.MinX, q.MinX);
                        var h = Math.Min(p.MaxY, q.MaxY) - Math.Max(p.MinY, q.MinY);
                        Assert.True(w <= 0 || h <= 0 || w * h < 1, $"{name} {time:0.000}: beats {boxes[a].Beat} and {boxes[b].Beat} overlap");
                    }
                }
                time += 1.0 / 60;
            }
        }
    }

    // Ported from MosaicLayoutTests.swift: testQuickRunsShareABeat
    [Fact]
    public void QuickRunsShareABeat()
    {
        var burst = Lines[1].Runs;
        var beats = MosaicGeometry.Beats(burst);
        var oh = burst.FindIndex(run => run.Text == "oh");
        var beat = beats.First(b => b.Contains(oh));
        Assert.Equal(["oh", "my", "god"], Enumerable.Range(beat.From, beat.Count).Select(id => burst[id].Text));
        var fast = MosaicGeometry.Beats(Lines[0].Runs);
        Assert.True(fast.All(b => b.Count <= MosaicGeometry.BeatRuns));
        Assert.True(fast.Count >= 4);
    }

    // Ported from MosaicLayoutTests.swift: testEveryWordAppearsBySungTime
    [Fact]
    public void EveryWordAppearsBySungTime()
    {
        foreach (var (name, runs) in Lines)
        {
            var beats = MosaicGeometry.Beats(runs);
            var speed = Speed(runs);
            for (var g = 0; g < beats.Count; g++)
            {
                var beat = beats[g];
                for (var index = beat.From; index < beat.To; index++)
                {
                    var run = runs[index];
                    var moves = MosaicGeometry.Moves(beats, runs, run.Start, speed, double.PositiveInfinity, false, runs.Count);
                    Assert.True(moves[g] < 0.6, $"{name} {run.Text}: already leaving when sung");
                    if (beat.Kind == MosaicKind.Letters)
                    {
                        double? next = index + 1 < runs.Count ? runs[index + 1].Start : null;
                        for (var part = 0; part < run.Parts.Count; part++)
                        {
                            var piece = run.Parts[part];
                            Assert.True(MosaicGeometry.LetterReveal(part, 0, run, next, piece.Start) >= 0.5, $"{name} {run.Text}");
                            var following = part + 1 < run.Parts.Count ? run.Parts[part + 1].Start : next ?? piece.Start + 0.5;
                            var last = Math.Max(0, StageShared.Count(piece.Text) - 1);
                            Assert.True(MosaicGeometry.LetterReveal(part, last, run, next, Math.Max(following, piece.Start + 0.02)) >= 0.99,
                                        $"{name} {run.Text}: letters still rising at the next onset");
                        }
                    }
                    else
                    {
                        var appear = MosaicGeometry.Appear(run, run.Start, speed, false, runs.Count);
                        Assert.True(Math.Min(1, appear * 1.6) >= 0.95, $"{name} {run.Text}");
                    }
                }
            }
        }
    }

    // Ported from MosaicLayoutTests.swift: testGluedSyllablesAreOneWord
    [Fact]
    public void GluedSyllablesAreOneWord()
    {
        var burst = Lines[1].Runs;
        Assert.Equal(["and", "then", "oh", "my", "god", "you", "came", "back", "beautiful", "tonight"], burst.Select(run => run.Text));
        var beats = MosaicGeometry.Beats(burst);
        foreach (var word in new[] { "beautiful", "tonight" })
        {
            var index = burst.FindIndex(run => run.Text == word);
            Assert.Single(beats, beat => beat.Contains(index));
        }
        var fast = Lines[0].Runs;
        Assert.Equal(11, fast.Count);
        Assert.Contains(fast, run => run.Text == "running");
        Assert.Contains(fast, run => run.Text == "goodbye");
        // "ful" rises at its own onset, not with "beau".
        var beautiful = burst[8];
        Assert.Equal(3, beautiful.Parts.Count);
        var ful = beautiful.Parts[2].Start;
        Assert.True(MosaicGeometry.LetterReveal(2, 0, beautiful, burst[9].Start, ful - MosaicGeometry.Lead - 0.01) < 0.01);
        Assert.True(MosaicGeometry.LetterReveal(2, 0, beautiful, burst[9].Start, ful) > 0.5);
    }

    // Ported from MosaicLayoutTests.swift: testLongLinesVaryTreatments
    [Fact]
    public void LongLinesVaryTreatments()
    {
        var eleven = Runs(Even("you were the light that kept me going through the darkest night", step: 0.5));
        foreach (var runs in new[] { Lines[2].Runs, eleven, Lines[3].Runs })
        {
            var kinds = MosaicGeometry.Beats(runs).Select(beat => beat.Kind).ToList();
            for (var i = 1; i < kinds.Count; i++)
                Assert.True(kinds[i] != kinds[i - 1], $"{string.Join(" ", runs.Select(r => r.Text))}: {string.Join(",", kinds)}");
        }
        var longLine = Lines[2].Runs;
        var running = longLine.FindIndex(run => run.Text == "running");
        Assert.Equal(MosaicKind.Huge, MosaicGeometry.Beats(longLine).First(beat => beat.Contains(running)).Kind);
        Assert.Equal(3, MosaicGeometry.Beats(eleven).Select(beat => beat.Kind).Distinct().Count());
    }
}
