using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.StageLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// Portal: its pills, icons, ornaments and shapes, fast songs and smoothness (the Mac's PortalLayoutTests at Test Build 90,
/// same inputs and values).
/// The Japanese parting needs the system word breaker, so it is in the render tests (PortalRenderTests).
/// </summary>
public class PortalLayoutTests
{
    private static readonly IWordBreaker Breaker = SimpleWordBreaker.Instance;

    /// <summary>Twelve words in 2.16 s (one every 0.18 s).</summary>
    internal static List<LyricWord> FastWords =>
        [.. "we run it back and turn it up til the lights go".Split(' ').Select((text, i) => new LyricWord(text, 1 + i * 0.18, 1 + i * 0.18 + 0.16))];

    /// <summary>A steadier line with three words arriving within 0.15 s ("we go now").</summary>
    internal static List<LyricWord> TripleWords =>
    [
        new("hold", 1.00, 1.40), new("on", 1.45, 1.90), new("we", 2.00, 2.06), new("go", 2.07, 2.13), new("now", 2.14, 2.60),
        new("into", 2.70, 3.00), new("the", 3.05, 3.20), new("night", 3.25, 4.00),
    ];

    /// <summary>Syllable-timed words, glued: beau·ti·ful, run·ning, un·for·get·ta·ble.</summary>
    internal static (List<LyricWord> Words, bool[] Glue) SyllableWords
    {
        get
        {
            (string Text, double Start, bool Glued)[] timing =
            [
                ("so", 0.80, false), ("beau", 1.00, false), ("ti", 1.20, true), ("ful", 1.40, true), ("run", 1.90, false),
                ("ning", 2.10, true), ("un", 2.60, false), ("for", 2.75, true), ("get", 2.90, true), ("ta", 3.05, true),
                ("ble", 3.20, true), ("night", 3.60, false),
            ];
            return ([.. timing.Select(t => new LyricWord(t.Text, t.Start, t.Start + 0.14))], [.. timing.Select(t => t.Glued)]);
        }
    }

    // Ported from PortalLayoutTests.swift: testGluedSyllablesAreOnePill
    [Fact]
    public void GluedSyllablesAreOnePill()
    {
        var line = SyllableWords;
        var runs = PortalGeometry.Runs(line.Words, line.Glue, Breaker);
        Assert.Equal(["so", "beautiful", "running", "unforgettable", "night"], runs.Select(run => run.Text));
        Assert.Equal(Enumerable.Range(0, 5), runs.Select(run => run.Id));
        Assert.Equal(3, runs[1].Parts.Count); // Each syllable is revealed in the pill on its own time
        Assert.Equal(2.60, runs[3].Start, 0.0001);
        // The shared runs cut a long glued word in two; Portal does not.
        Assert.True(StageRun.Make(line.Words, line.Glue).Count > runs.Count);
        // The glue Portal pairs readings by joins the same pills.
        Assert.Equal(runs.Count, PortalGeometry.Glue(line.Words, line.Glue, Breaker).Count(glued => !glued));
    }

    // Ported from PortalLayoutTests.swift: testKoreanGluedWordIsOnePill
    [Fact]
    public void KoreanGluedWordIsOnePill()
    {
        var words = new[] { "기", "억", "할", "게", "너" }
            .Select((text, i) => new LyricWord(text, 1 + i * 0.2, 1.15 + i * 0.2)).ToList();
        var runs = PortalGeometry.Runs(words, [false, true, true, true, false], Breaker);
        Assert.Equal(["기억할게", "너"], runs.Select(run => run.Text));
    }

    internal static readonly string[] PlainLine =
        "drifting slowly across empty fields beneath gentle grey willows whispering softly together".Split(' ');

    /// <summary>A steady 0…1 for a seed and an index, as the Mac's test makes it.</summary>
    internal static double Steady(int seed, int index)
    {
        var value = unchecked((ulong)((long)seed * 7919 + (index + 2L) * 104_729));
        value ^= value >> 13;
        value = unchecked(value * 0x2545F4914F6CDD1DUL);
        value ^= value >> 29;
        return value % 10_000 / 10_000.0;
    }

    // Ported from PortalLayoutTests.swift: testOrnamentsVaryAndRest
    [Fact]
    public void OrnamentsVaryAndRest()
    {
        Assert.Equal(12, PlainLine.Length);
        for (var seed = 0; seed < 40; seed++)
        {
            var icons = PortalGeometry.Icons(PlainLine, index => Steady(seed, index));
            Assert.True(PlainLine.All(word => LyricWordIcons.Symbol(word) is null));
            for (var index = 0; index < icons.Length; index++)
            {
                if (icons[index] is not { } icon) continue;
                Assert.Contains(icon, PortalGeometry.Ornaments);
                for (var back = 1; back <= 3 && index - back >= 0; back++)
                    Assert.True(icons[index - back] != icon, $"seed {seed}: {icon} repeats within three pills");
            }
            Assert.True(icons.Count(icon => icon is null) * 3 >= icons.Length, $"seed {seed}: at least a third bare");
            Assert.Contains(icons, icon => icon is not null);
        }
    }

    // Ported from PortalLayoutTests.swift: testMeaningfulWordsAlwaysShowTheirSymbolAndFunctionWordsNone
    [Fact]
    public void MeaningfulWordsAlwaysShowTheirSymbolAndFunctionWordsNone()
    {
        var texts = "call me home my love the city lights are burning bright".Split(' ');
        for (var seed = 0; seed < 20; seed++)
        {
            var icons = PortalGeometry.Icons(texts, index => Steady(seed, index));
            Assert.Equal("phone.fill", icons[0]);
            Assert.Null(icons[1]);
            Assert.Equal("house.fill", icons[2]);
            Assert.Null(icons[3]);
            Assert.Equal("heart.fill", icons[4]);
            Assert.Null(icons[5]);
            Assert.Equal("building.2.fill", icons[6]);
            Assert.Equal("flame.fill", icons[9]);
        }
        // Deterministic: the same line and seed, the same symbols.
        Assert.Equal(PortalGeometry.Icons(texts, i => Steady(3, i)), PortalGeometry.Icons(texts, i => Steady(3, i)));
    }

    // Ported from PortalLayoutTests.swift: testShapesNeverRepeatOnConsecutivePills (the shapes; drawing each shape is in
    // the render tests)
    [Fact]
    public void ShapesNeverRepeatOnConsecutivePills()
    {
        for (var seed = 0; seed < 40; seed++)
        {
            var shapes = PortalGeometry.Shapes(12, index => Steady(seed, index));
            Assert.Equal(12, shapes.Length);
            for (var index = 1; index < shapes.Length; index++)
                Assert.True(shapes[index] != shapes[index - 1], $"seed {seed} at {index}");
        }
    }

    /// <summary>The line's pace, as the stage works it out.</summary>
    private static double Speed(List<StageRun> runs) => StageShared.LineSpeed([.. runs.Select(run => run.Start)]);

    private readonly record struct Pose(double Y, double Height, double Scale, double Opacity, double Live);

    /// <summary>
    /// Each shown run's pill as Portal lays it out at one moment, on a 520-point stage with 100-point pills: its place
    /// above the live pill's (climb, and the travel in from below), shown scale (age and landing) and opacity.
    /// <paramref name="original"/> is the Portal the user liked, before Build 88 (the Mac's aa7f036).
    /// </summary>
    private static List<Pose> Poses(List<StageRun> runs, double time, bool original = false)
    {
        var shown = runs.Where(run => time >= run.Start - StageMotion.Lead).ToList();
        var speed = Speed(runs);
        double[] settle, lift;
        if (original)
        {
            // (The original's lift started 0.08 s before the onset, before the word arrived: its first frames jumped the trail.)
            settle = [.. shown.Select(run => StageShared.Glide((time - (run.Start - 0.05)) / (0.6 / speed)))];
            lift = [.. shown.Select(run => StageShared.Glide((time - (run.Start - 0.08)) / (0.36 / speed)))];
        }
        else
        {
            var motion = PortalGeometry.Motion(runs, time, speed, false);
            settle = [.. motion.Settle.Take(shown.Count)];
            lift = [.. motion.Lift.Take(shown.Count)];
        }
        var ranks = new double[shown.Count];
        double after = 0;
        for (var index = shown.Count - 1; index >= 0; index--) { ranks[index] = after; after += settle[index]; }
        var lives = ranks.Select(StageMotion.Liveness).ToArray();
        var heights = Enumerable.Range(0, shown.Count).Select(i => PortalGeometry.Scale(ranks[i]) * 100 * (1.04 + 0.1 * lives[i])).ToList();
        var fadeFrom = speed > 1.6 ? 1.0 : 1.6;
        var visible = ranks.Select(rank => 1 - Easing.Smoothstep((rank - fadeFrom) / 0.9)).ToList();
        // The live pill rests 70 points below the middle of the stage.
        var column = PortalGeometry.Column(heights, lift, visible, settle, 64 * 0.16, 330, original ? 10_000 : 190);
        return [.. Enumerable.Range(0, shown.Count).Select(i =>
        {
            var travel = (1 - settle[i]) * 520 * 0.1 / speed;
            var landing = StageMotion.Arrival(shown[i].Start, time, speed);
            var scale = PortalGeometry.Scale(ranks[i]) * (0.9 + 0.1 * Math.Min(landing, 1.05));
            return new Pose(travel + column[i], heights[i], scale, settle[i] * visible[i] * (0.85 + 0.15 * lives[i]), lives[i]);
        })];
    }

    private static List<List<StageRun>> FastRuns =>
    [
        .. new[] { FastWords, TripleWords, Mixed, Even("we keep dreaming under neon rain") }
            .Select(words => PortalGeometry.Runs(words, new bool[words.Count], Breaker)),
    ];

    // Ported from PortalLayoutTests.swift: testFastLinesNeverStackPills
    [Fact]
    public void FastLinesNeverStackPills()
    {
        foreach (var runs in FastRuns)
        {
            var time = runs[0].Start - 0.2;
            while (time < runs[^1].Start + 0.8)
            {
                var poses = Poses(runs, time);
                var overlappingPills = 0;
                for (var i = 0; i < poses.Count; i++)
                for (var j = i + 1; j < poses.Count; j++)
                {
                    var (a, b) = (poses[i], poses[j]);
                    if (!(a.Opacity > 0.3 && b.Opacity > 0.3)) continue;
                    var overlap = (a.Height + b.Height) / 2 - Math.Abs(a.Y - b.Y);
                    var smaller = Math.Min(a.Height, b.Height);
                    if (a.Live > 0.3 && b.Live > 0.3 && overlap > 0.2 * smaller) overlappingPills++;
                    Assert.True(overlap < 0.35 * smaller, $"{runs[i].Text} and {runs[j].Text} pile up at {time:0.00} s");
                }
                Assert.True(overlappingPills == 0, $"pills overlap at {time:0.00} s");
                time += 1.0 / 60;
            }
        }
    }

    // Ported from PortalLayoutTests.swift: testEveryWordEntersOnItsSungTime
    [Fact]
    public void EveryWordEntersOnItsSungTime()
    {
        foreach (var runs in FastRuns)
        {
            var speed = Speed(runs);
            foreach (var run in runs)
            {
                double Appear(double time) => PortalGeometry.Motion(runs, time, speed, false).Appear[run.Id];
                Assert.Equal(0, Appear(run.Start - 0.05), 0.0001);
                Assert.True(Appear(run.Start) > 0, run.Text);
                Assert.True(Appear(run.Start + 0.3) >= 0.5, $"{run.Text} at {run.Start}");
            }
        }
    }

    // Ported from PortalLayoutTests.swift: testQuickRunSharesOneBurst
    [Fact]
    public void QuickRunSharesOneBurst()
    {
        var runs = FastRuns[1];
        var previous = -1.0;
        for (var time = 1.95; time < 2.5; time += 1.0 / 60)
        {
            var burst = PortalGeometry.Motion(runs, time, Speed(runs), false).Burst;
            Assert.Equal(burst[2], burst[3], 0.0001);
            Assert.Equal(burst[3], burst[4], 0.0001);
            Assert.True(burst[4] >= previous);
            previous = burst[4];
        }
    }

    // Ported from PortalLayoutTests.swift: testOnlyQuickWordsShortenAndNeverBelowTheFloors
    [Fact]
    public void OnlyQuickWordsShortenAndNeverBelowTheFloors()
    {
        foreach (var runs in FastRuns)
        {
            var speed = Speed(runs);
            for (var index = 0; index < runs.Count; index++)
            {
                var times = PortalGeometry.Times(runs, index, speed);
                var next = index + 1 < runs.Count ? runs[index + 1].Start - runs[index].Start : 9;
                if (next >= 0.35)
                {
                    Assert.Equal(0.6 / speed, times.Settle, 1e-9);
                    Assert.Equal(0.36 / speed, times.Lift, 1e-9);
                }
                else
                {
                    Assert.True(times.Settle >= Math.Min(0.6 / speed, 0.28) - 1e-9);
                    Assert.True(times.Lift >= Math.Min(0.36 / speed, 0.2) - 1e-9);
                    Assert.True(times.Settle <= 0.6 / speed + 1e-9);
                }
            }
        }
    }

    /// <summary>The largest change of any pill's place, scale or opacity over one 60 Hz frame, sampled at 120 Hz through a line and its exit.</summary>
    private static (double Y, double Scale, double Opacity) LargestSteps(List<StageRun> runs, bool original)
    {
        (double Y, double Scale, double Opacity) worst = (0, 0, 0);
        for (var time = runs[0].Start - 0.2; time < runs[^1].Start + 1.0; time += 1.0 / 120)
        {
            var now = Poses(runs, time, original);
            var later = Poses(runs, time + 1.0 / 60, original);
            for (var i = 0; i < now.Count; i++)
            {
                worst.Y = Math.Max(worst.Y, Math.Abs(later[i].Y - now[i].Y));
                worst.Scale = Math.Max(worst.Scale, Math.Abs(later[i].Scale - now[i].Scale));
                worst.Opacity = Math.Max(worst.Opacity, Math.Abs(later[i].Opacity - now[i].Opacity));
            }
        }
        return worst;
    }

    private static (string Name, List<StageRun> Runs)[] Named => [("short", FastRuns[3]), ("long", FastRuns[2]), ("fast", FastRuns[0]), ("triple", FastRuns[1])];

    // Ported from PortalLayoutTests.swift: testMovesAsSmoothlyAsTheOriginal
    [Fact]
    public void MovesAsSmoothlyAsTheOriginal()
    {
        foreach (var (name, runs) in Named)
        {
            var limit = LargestSteps(runs, original: true);
            var steps = LargestSteps(runs, original: false);
            Assert.True(steps.Y <= limit.Y * 1.15, $"{name}: place {steps.Y:0.00} against {limit.Y:0.00}");
            Assert.True(steps.Scale <= limit.Scale * 1.15, $"{name}: scale {steps.Scale:0.0000} against {limit.Scale:0.0000}");
            Assert.True(steps.Opacity <= limit.Opacity * 1.15, $"{name}: opacity {steps.Opacity:0.0000} against {limit.Opacity:0.0000}");
        }
    }

    // Ported from PortalLayoutTests.swift: testNothingJumpsWhenAWordArrives
    [Fact]
    public void NothingJumpsWhenAWordArrives()
    {
        foreach (var (name, runs) in Named)
        {
            var limit = LargestSteps(runs, original: true);
            foreach (var run in runs)
            foreach (var moment in new[] { run.Start - StageMotion.Lead, run.Start })
            {
                var before = Poses(runs, moment - 1.0 / 120);
                var after = Poses(runs, moment + 1.0 / 120);
                for (var i = 0; i < before.Count && i < run.Id; i++)
                {
                    var what = $"{name} {run.Text}: pill {i} at {moment:0.000} s";
                    Assert.True(Math.Abs(after[i].Y - before[i].Y) < limit.Y * 1.15, what + " place");
                    Assert.True(Math.Abs(after[i].Scale - before[i].Scale) < limit.Scale * 1.15, what + " scale");
                    Assert.True(Math.Abs(after[i].Opacity - before[i].Opacity) < limit.Opacity * 1.15, what + " opacity");
                }
                // A pill that joins is at rest: invisible, in place.
                if (after.Count > before.Count) Assert.True(after[before.Count].Opacity < 0.01, $"{name} {run.Text}");
            }
        }
    }
}
