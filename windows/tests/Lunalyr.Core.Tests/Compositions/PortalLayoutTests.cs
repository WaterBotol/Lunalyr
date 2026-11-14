using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.StageLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// Portal: its pills, icons, ornaments and shapes, and fast songs (the Mac's PortalLayoutTests, same inputs and values).
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

    /// <summary>Each run's pill height and place above the live pill's, as Portal lays them out, at one moment.</summary>
    private static List<(double Y, double Height, double Opacity, double Live)> Poses(List<StageRun> runs, double time)
    {
        var shown = runs.Where(run => time >= run.Start - StageMotion.Lead).ToList();
        var motion = PortalGeometry.Motion(runs, time, false);
        var ranks = new double[shown.Count];
        double after = 0;
        for (var index = shown.Count - 1; index >= 0; index--) { ranks[index] = after; after += motion.Handover[index]; }
        var lives = ranks.Select(StageMotion.Liveness).ToArray();
        var heights = Enumerable.Range(0, shown.Count).Select(i => 100 * PortalGeometry.Scale(ranks[i]) * (1.04 + 0.1 * lives[i])).ToList();
        var climb = PortalGeometry.Climb(heights, motion.Lift.Take(shown.Count).ToList(), 64 * 0.16);
        return [.. Enumerable.Range(0, shown.Count).Select(i =>
        {
            var visible = 1 - Easing.Smoothstep((ranks[i] - 1.0) / 0.9);
            return (-climb[i], heights[i], motion.Appear[i] * visible, lives[i]);
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

    // Ported from PortalLayoutTests.swift: testEveryWordAppearsByItsSungTime
    [Fact]
    public void EveryWordAppearsByItsSungTime()
    {
        foreach (var runs in FastRuns)
        foreach (var run in runs)
        {
            Assert.True(PortalGeometry.Motion(runs, run.Start, false).Appear[run.Id] >= 0.8, $"{run.Text} at {run.Start}");
            Assert.Equal(0, PortalGeometry.Motion(runs, run.Start - 0.08, false).Appear[run.Id], 0.0001);
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
            var burst = PortalGeometry.Motion(runs, time, false).Burst;
            Assert.Equal(burst[2], burst[3], 0.0001);
            Assert.Equal(burst[3], burst[4], 0.0001);
            Assert.True(burst[4] >= previous);
            previous = burst[4];
        }
    }
}
