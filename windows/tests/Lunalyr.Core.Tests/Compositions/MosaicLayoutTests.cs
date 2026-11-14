using Lunalyr.Core.Compositions;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.StageLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// Mosaic decluttered: the colour bar only ever one word's, fast lines without pile-ups, words in by their onset, glued
/// syllables set as one word, long lines varied, and moving as smoothly as the original (the Mac's MosaicLayoutTests at
/// Test Build 90, same inputs and values; the drawn bar is checked in StageLayoutRenderTests).
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

    // As the stage reads it: every timed piece, syllables included.
    private static double Speed(List<StageRun> runs) => StageShared.LineSpeed([.. runs.SelectMany(run => run.Parts.Select(part => part.Start))]);

    private const long Seed = 3;

    // Ported from MosaicLayoutTests.swift: testBarIsAlwaysOneWord
    [Fact]
    public void BarIsAlwaysOneWord()
    {
        foreach (var (name, runs) in Lines)
        foreach (var beat in MosaicGeometry.Beats(runs).Where(beat => beat.Kind == MosaicKind.Bar))
            Assert.True(beat.Count == 1, name);
    }

    /// <summary>Where a beat is drawn: its box, centre, scale, and the big setting's and small copy's own opacities.</summary>
    private readonly record struct Box(int Beat, StageRect Rect, double Opacity, PointD Centre, double Scale, double Big, double Small);

    /// <summary>Where each beat is drawn at <paramref name="time"/>, by Mosaic's own model, each beat's size estimated from its text.</summary>
    private static List<Box> Boxes(List<StageRun> runs, double time, double exitStart = double.PositiveInfinity)
    {
        var beats = MosaicGeometry.Beats(runs);
        var speed = Speed(runs);
        var (moves, ranks) = MosaicGeometry.Motion(beats, runs, time, speed, exitStart, Seed, false, runs.Count);
        var bounds = new StageRect(0, 0, Stage.Width, Stage.Height);
        var step = MosaicGeometry.StackStep(Unit, false);
        var result = new List<Box>();
        for (var g = 0; g < beats.Count; g++)
        {
            var beat = beats[g];
            var first = runs[beat.From];
            var appear = MosaicGeometry.Appear(first, time, speed, false, runs.Count);
            var fade = MosaicGeometry.StackFade(ranks[g]);
            if (!(appear > 0.001 && fade > 0.001)) continue;
            var big = MosaicGeometry.BigSize(beat.Kind);
            var size = Estimate(string.Join(" ", Enumerable.Range(beat.From, beat.Count).Select(id => runs[id].Text)), big);
            var morph = StageMotion.MosaicMorph(moves[g], 0.6 / big);
            var centre = MosaicGeometry.Centre(size, moves[g], ranks[g], MosaicGeometry.Sway(g), step, 0, false, bounds);
            var entrance = beat.Kind switch
            {
                MosaicKind.Huge => 0.8 + 0.2 * Math.Min(1.05, appear),
                MosaicKind.Bar => 0.7 + 0.3 * MosaicGeometry.BarSpread(first, time, speed),
                _ => 1.0,
            };
            var scale = morph.BigScale;
            var rect = new StageRect(centre.X - size.Width / 2, centre.Y - size.Height * scale / 2, size.Width * scale, size.Height * scale);
            var bigOpacity = fade * Math.Min(1, appear * 1.6) * morph.BigOpacity;
            var smallOpacity = fade * morph.SmallOpacity;
            result.Add(new Box(g, rect, Math.Max(bigOpacity, smallOpacity), centre, morph.BigScale * entrance, bigOpacity, smallOpacity));
        }
        return result;
    }

    private static SizeD Estimate(string text, double big) =>
        new(Math.Min(Stage.Width * 0.9, StageShared.Count(text) * 0.55 * big * Unit), big * Unit * 1.1);

    /// <summary>
    /// The original Mosaic (the Mac's aa7f036), word by word, for calibration: each word's kind by chance, its arrival
    /// spring, the shared ranks, settle <c>glide(min(rank, 1.2) / 1.2)</c>, the huge word's 35 % slide, the morph.
    /// </summary>
    private static List<Box> OriginalBoxes(List<StageRun> runs, double time)
    {
        var speed = Speed(runs);
        var shown = StageRun.Shown(runs, time);
        var ranks = new double[shown.Count];
        StageMotion.Ranks(shown, shown.Count, time, speed, Seed, false, ranks);
        var bounds = new StageRect(0, 0, Stage.Width, Stage.Height);
        var step = MosaicGeometry.StackStep(Unit, false);
        var result = new List<Box>();
        for (var index = 0; index < shown.Count; index++)
        {
            var run = shown[index];
            var kind = (int)(StageShared.Chance(Seed, run.Id, 20) * 3) % 3;
            var big = kind == 0 ? 3.0 : kind == 1 ? 2.3 : 2.4;
            var a = StageMotion.Arrival(run.Start, time, speed);
            var rank = ranks[index];
            var settle = StageShared.Glide(Math.Min(rank, 1.2) / 1.2);
            var size = Estimate(run.Text, big);
            var overhang = kind == 1 ? Unit * 2.3 * 0.45 : 0;
            var sway = (StageShared.Chance(Seed, index, 21) - 0.5) * 0.06;
            var home = MosaicGeometry.Home(size, sway, step, overhang, false, bounds);
            var stack = new PointD(bounds.Width * 0.14 + size.Width / 2, MosaicGeometry.RowY(rank, step, bounds));
            var centre = new PointD(home.X + (stack.X - home.X) * settle, home.Y + (stack.Y - home.Y) * settle);
            if (kind == 0) centre = centre with { X = centre.X + (1 - Math.Min(a, 1)) * Stage.Width * 0.35 };
            var morph = StageMotion.MosaicMorph(settle, 0.6 / big);
            var fade = MosaicGeometry.StackFade(rank);
            var bigOpacity = Math.Min(1, a * 1.6) * morph.BigOpacity;
            var smallOpacity = morph.SmallOpacity * fade;
            result.Add(new Box(index, default, Math.Max(bigOpacity, smallOpacity), centre, morph.BigScale, bigOpacity, smallOpacity));
        }
        return result;
    }

    /// <summary>
    /// The largest change of any item's position, scale and opacity over 1/60 s, sampled at 120 Hz; items matched by
    /// beat (an item new in the later sample counts from nothing).
    /// </summary>
    private static (double Position, double Scale, double Opacity) Steps(Func<double, List<Box>> model, double start, double end,
                                                                         IEnumerable<double>? around = null)
    {
        (double Position, double Scale, double Opacity) worst = (0, 0, 0);
        var times = new List<double>();
        for (var time = start; time < end; time += 1.0 / 120) times.Add(time);
        if (around is not null) times.AddRange(around.Select(moment => moment - 1.0 / 120));
        foreach (var t in times)
        {
            var before = new Dictionary<int, Box>();
            foreach (var box in model(t)) before.TryAdd(box.Beat, box);
            foreach (var box in model(t + 1.0 / 60))
            {
                if (!before.TryGetValue(box.Beat, out var old))
                {
                    worst.Opacity = Math.Max(worst.Opacity, Math.Max(box.Big, box.Small));
                    continue;
                }
                worst.Position = Math.Max(worst.Position, Math.Sqrt(Math.Pow(box.Centre.X - old.Centre.X, 2) + Math.Pow(box.Centre.Y - old.Centre.Y, 2)));
                worst.Scale = Math.Max(worst.Scale, Math.Abs(box.Scale - old.Scale));
                worst.Opacity = Math.Max(worst.Opacity, Math.Max(Math.Abs(box.Big - old.Big), Math.Abs(box.Small - old.Small)));
            }
        }
        return worst;
    }

    // Ported from MosaicLayoutTests.swift: testAsSmoothAsTheOriginal
    [Fact]
    public void AsSmoothAsTheOriginal()
    {
        foreach (var (name, runs) in Lines.Where(line => line.Name != "burst"))
        {
            var start = runs[0].Start - 0.3;
            var end = runs.Max(run => run.End) + 0.2;
            var original = Steps(t => OriginalBoxes(runs, t), start, end);
            var moments = runs.SelectMany(run => new[] { run.Start, run.Start - StageMotion.Lead, run.Start - MosaicGeometry.Lead });
            var ours = Steps(t => Boxes(runs, t), start, end, moments);
            Assert.True(ours.Position <= original.Position * 1.15, $"{name} position {ours.Position:0.00} against {original.Position:0.00}");
            Assert.True(ours.Scale <= original.Scale * 1.15, $"{name} scale {ours.Scale:0.0000} against {original.Scale:0.0000}");
            Assert.True(ours.Opacity <= original.Opacity * 1.15 + 0.001, $"{name} opacity {ours.Opacity:0.0000} against {original.Opacity:0.0000}");
        }
    }

    // Ported from MosaicLayoutTests.swift: testNothingJumpsWhenAWordArrives
    [Fact]
    public void NothingJumpsWhenAWordArrives()
    {
        (double, double, double)? Change(List<StageRun> runs, int beat, double a, double b)
        {
            var old = Boxes(runs, a).Where(box => box.Beat == beat).ToList();
            var now = Boxes(runs, b).Where(box => box.Beat == beat).ToList();
            if (old.Count == 0 || now.Count == 0) return null;
            return (Math.Sqrt(Math.Pow(now[0].Centre.X - old[0].Centre.X, 2) + Math.Pow(now[0].Centre.Y - old[0].Centre.Y, 2)),
                    Math.Abs(now[0].Scale - old[0].Scale), Math.Max(Math.Abs(now[0].Big - old[0].Big), Math.Abs(now[0].Small - old[0].Small)));
        }
        const double h = 1.0 / 120;
        foreach (var (name, runs) in Lines)
        {
            var beats = MosaicGeometry.Beats(runs);
            foreach (var run in runs)
            {
                var mine = beats.FindIndex(beat => beat.Contains(run.Id));
                foreach (var moment in new[] { run.Start - MosaicGeometry.Lead, run.Start - StageMotion.Lead, run.Start })
                foreach (var box in Boxes(runs, moment + h).Where(box => box.Beat != mine))
                {
                    var across = Change(runs, box.Beat, moment - h, moment + h);
                    Assert.True(across is not null, $"{name} {run.Text}: beat {box.Beat} appeared with another word");
                    var before = Change(runs, box.Beat, moment - 3 * h, moment - h) ?? (0, 0, 0);
                    var after = Change(runs, box.Beat, moment + h, moment + 3 * h) ?? (0, 0, 0);
                    var label = $"{name} {run.Text}: beat {box.Beat} jumped at {moment:0.000}";
                    Assert.True(across!.Value.Item1 <= Math.Max(before.Item1, after.Item1) * 1.5 + 0.5, label + " (place)");
                    Assert.True(across.Value.Item2 <= Math.Max(before.Item2, after.Item2) * 1.5 + 0.002, label + " (scale)");
                    Assert.True(across.Value.Item3 <= Math.Max(before.Item3, after.Item3) * 1.5 + 0.005, label + " (opacity)");
                }
            }
        }
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
                    var moves = MosaicGeometry.Motion(beats, runs, run.Start, speed, double.PositiveInfinity, Seed, false, runs.Count).Moves;
                    Assert.True(moves[g] < 0.6, $"{name} {run.Text}: already leaving when sung");
                    if (beat.Kind == MosaicKind.Letters)
                    {
                        double? next = index + 1 < runs.Count ? runs[index + 1].Start : null;
                        for (var part = 0; part < run.Parts.Count; part++)
                        {
                            var piece = run.Parts[part];
                            Assert.True(MosaicGeometry.LetterReveal(part, 0, run, next, piece.Start) >= 0.4, $"{name} {run.Text}");
                            // All in within the original's own time (0.25 s rise, letters 0.05 s apart).
                            var last = Math.Max(0, StageShared.Count(piece.Text) - 1);
                            var done = piece.Start - MosaicGeometry.Lead + 0.25 + last * 0.05;
                            Assert.True(MosaicGeometry.LetterReveal(part, last, run, next, done) >= 0.99,
                                        $"{name} {run.Text}: letters slower than the original");
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
        Assert.True(MosaicGeometry.LetterReveal(2, 0, beautiful, burst[9].Start, ful) > 0.4);
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
