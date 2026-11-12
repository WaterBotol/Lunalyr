using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// The stage layouts' geometry (Hinge, Broadside, Portal, Mosaic, Torque): the pure parts of the Mac's
/// StageMotionTests and StageReadingTests, with the same inputs and expected values.
/// </summary>
public class StageLayoutTests
{
    private static List<StageRun> Runs(int count, double step = 0.5, double start = 1)
    {
        var words = Enumerable.Range(0, count)
            .Select(i => new LyricWord($"word{i}", start + i * step, start + i * step + step * 0.9)).ToArray();
        return StageRun.Make(words, new bool[count]);
    }

    private static List<LyricWord> Words(string[] texts, double step) =>
        texts.Select((text, index) => new LyricWord(text, 1 + index * step, 1 + index * step + step * 0.9)).ToList();

    // The MacBook's stage (1512×982 points), as LyricViewportMetrics makes it, and the base a stage layout is handed.
    private static readonly ViewportMetrics MacBook = ViewportMetrics.For(1512, 982, 1);
    private static SizeD Stage => MacBook.Stage;
    private static double Base => MacBook.BaseSize * 0.8;

    private static PointD[] TorquePlaces(List<StageRun> line, double t, double unit = 40)
    {
        var shown = StageRun.Shown(line, t);
        var state = TorqueGeometry.State(shown, t, 1, 3, false);
        var measured = shown.Select(_ => new SizeD(unit * 4, unit * 2 * 1.2)).ToList();
        return TorqueGeometry.Stack(measured, state, 3, new StageRect(0, 0, 960, 520));
    }

    // Ported from StageMotionTests.swift: testTorqueStackNeverJumpsWhenAWordIsAdded
    [Fact]
    public void TorqueStackNeverJumpsWhenAWordIsAdded()
    {
        var line = Runs(8, 0.5);
        foreach (var run in line.Skip(1))
        {
            var onset = run.Start - StageMotion.Lead;
            var before = TorquePlaces(line, onset - 0.0005);
            var after = TorquePlaces(line, onset + 0.0005);
            for (var index = 0; index < before.Length; index++)
                Assert.True(Math.Abs(after[index].Y - before[index].Y) < 1, $"Word {index} jumped when {run.Text} was added");
        }
    }

    // Ported from StageMotionTests.swift: testTorqueLongLinesStayOnTheStage
    [Fact]
    public void TorqueLongLinesStayOnTheStage()
    {
        var line = Runs(18, 0.5);
        var newest = new List<double>();
        foreach (var count in new[] { 4, 10, 18 })
        {
            var t = line[count - 1].Start + 0.45;
            var places = TorquePlaces(line, t);
            Assert.Equal(count, places.Length);
            var y = places[count - 1].Y;
            Assert.True(y < 520, $"The newest word ran off the bottom at word {count}");
            newest.Add(y);
        }
        // It used to sink by about 2.4 type sizes between word 4 and word 10.
        Assert.True(Math.Abs(newest[1] - newest[0]) < 40 * 0.6);
        Assert.True(Math.Abs(newest[2] - newest[1]) < 40 * 0.3, "Settles once old words fade");
    }

    // Ported from StageMotionTests.swift: testHingeFitNeverStepsAsTheColumnGrows
    [Fact]
    public void HingeFitNeverStepsAsTheColumnGrows()
    {
        var texts = "Dance under stars rain over the city can fly away tonight again".Split(' ');
        var words = texts.Select((w, i) => new LyricWord(w, 1 + i * 0.6, 1.5 + i * 0.6)).ToList();
        var line = StageRun.Make(words, new bool[words.Count]);
        var stage = new SizeD(1200, 520);
        (double Fit, double Shrink) Fit(double t)
        {
            var shown = StageRun.Shown(line, t);
            var weights = shown.Select(run => StageMotion.Weight(StageMotion.Arrival(run.Start, t))).ToList();
            var barStart = shown.Count > 2 ? shown[2].Start : double.PositiveInfinity;
            var settle = Easing.Smoothstep((t - barStart - 0.25) / 0.5);
            return HingeGeometry.Fit(shown.Select(run => run.Text).ToList(), weights, settle, 40, stage);
        }
        var previous = Fit(1);
        var time = 1.0;
        while (time < 9)
        {
            time += 0.001;
            var now = Fit(time);
            Assert.True(Math.Abs(now.Fit / previous.Fit - 1) < 0.005, $"The corner's scale stepped at {time:0.000} s");
            Assert.True(Math.Abs(now.Shrink - previous.Shrink) < 0.005, $"The column shrank at once at {time:0.000} s");
            previous = now;
        }
        Assert.Equal(0.85, previous.Shrink, 3); // A long column still ends 15% smaller
    }

    // Ported from StageMotionTests.swift: testChosenEntrancesMatchTheOtherLayouts
    [Fact]
    public void ChosenEntrancesMatchTheOtherLayouts()
    {
        foreach (var style in new[] { WordArrival.Lift, WordArrival.Pop, WordArrival.Orbit, WordArrival.Glide, WordArrival.Unfold,
                                    WordArrival.CutoutFold, WordArrival.CutoutSlide })
        {
            var stage = StageShared.ChosenEntrance(style, 0.3, 40, -1, false);
            Assert.NotNull(stage);
            var elsewhere = EntranceTransform.Make(0.3, 0.3, style, 40, -1, false);
            Assert.Equal(elsewhere, stage!.Value);
        }
        Assert.Equal(40 * 0.48, StageShared.ChosenEntrance(WordArrival.Lift, 0, 40, 1, false)!.Value.OffsetY, 3); // not 0.7
        Assert.Null(StageShared.ChosenEntrance(WordArrival.Fade, 0, 40, 1, false)); // The layout's own
        Assert.Null(StageShared.ChosenEntrance(WordArrival.Lift, 0, 40, 1, true));
    }

    // Ported from StageReadingTests.swift: testHingeFitCountsReadings
    [Fact]
    public void HingeFitCountsReadings()
    {
        string[] texts = ["君の", "名前を", "呼んで", "夜空に", "消えた", "君を", "探して"];
        var weights = Enumerable.Repeat(1.0, texts.Length).ToList();
        var plain = HingeGeometry.Fit(texts, weights, 1, Base, Stage);
        var none = HingeGeometry.Fit(texts, weights, 1, Base, Stage, new string?[texts.Length]);
        var read = HingeGeometry.Fit(texts, weights, 1, Base, Stage,
                                     ["kimi no", "namae wo", "yonde", "yozora ni", "kieta", "kimi wo", "sagashite"]);
        Assert.Equal(plain.Fit, none.Fit); // No readings, no change
        Assert.True(read.Fit < plain.Fit, "The corner with its readings is set smaller to fit");
        Assert.Equal(plain.Shrink, read.Shrink);
    }

    // Ported from StageReadingTests.swift: testTorqueStackMakesRoomForReadings (the StageLayout `needs` part is the
    // Mac's SwiftUI layout; here the layer scales by TorqueGeometry.Height itself)
    [Fact]
    public void TorqueStackMakesRoomForReadings()
    {
        var runs = StageRun.Make(Words(["one", "two", "three"], 0.5), [false, false, false]);
        var state = TorqueGeometry.State(runs, 5, 1, 3, true);
        var rest = TorqueGeometry.RestSize(40);
        var slot = TorqueGeometry.ReadingSlot(rest);
        var bounds = new StageRect(0, 0, 800, 600);
        var plainSizes = Enumerable.Repeat(new SizeD(200, 100), 3).ToList();
        var plain = TorqueGeometry.Stack(plainSizes, state, 3, bounds);
        state.Slots = [slot, slot, slot];
        var readSizes = plainSizes.Select(s => new SizeD(s.Width, s.Height + slot)).ToList();
        var read = TorqueGeometry.Stack(readSizes, state, 3, bounds);
        Assert.True(read[2].Y - read[1].Y > plain[2].Y - plain[1].Y, "Neighbours move apart for the reading");
        Assert.True(read[2].Y - read[1].Y
                    >= ((100 * 0.82 + slot * 1.2) * state.Scales[1] + (100 * 0.82 + slot * 1.2) * state.Scales[2]) / 2 - 0.5);
        // Too tall for a short stage: the newest word stays whole on it.
        var shortStage = new StageRect(0, 0, 800, 220);
        var tall = TorqueGeometry.Stack(readSizes, state, 3, shortStage);
        Assert.True(tall[2].Y + (100 * 0.82 + slot * 1.2) * state.Scales[2] / 2 <= shortStage.MaxY + 0.5);
        // A stack with readings needs more room, which the layer scales to the stage.
        state.Slots = [];
        var plainHeight = TorqueGeometry.Height(plainSizes, state, rest);
        state.Slots = [slot, slot, slot];
        var readHeight = TorqueGeometry.Height(readSizes, state, rest);
        Assert.True(readHeight > plainHeight);
    }

    // Ported from StageReadingTests.swift: testBroadsideKeepsWordsOnTheLine
    [Fact]
    public void BroadsideKeepsWordsOnTheLine()
    {
        var bounds = new StageRect(0, 0, 1000, 400);
        const double slot = 40;
        SizeD[] measured = [new(200, 100 + slot), new(60, 30), new(220, 100)];
        bool[] little = [false, true, false];
        var places = BroadsideGeometry.Places(measured, little, [slot, 0, 0], [1, 1, 1], 40, bounds);
        Assert.Equal(bounds.MidY, places[0].Y - slot / 2, 2); // The word, not its reading, sits on the line
        Assert.Equal(bounds.MidY, places[2].Y, 2);
        Assert.Equal(bounds.MidY + 100 * 0.2, places[1].Y, 2); // The label lies across the word
        var plain = BroadsideGeometry.Places(measured.Select(m => m with { Height = 100 }).ToList(), little, [0, 0, 0], [1, 1, 1], 40, bounds);
        Assert.Equal(places[1].Y, plain[1].Y, 2);
    }

    // Ported from StageReadingTests.swift: testPortalKeepsPillsOnStage
    [Fact]
    public void PortalKeepsPillsOnStage()
    {
        var bounds = new StageRect(0, 0, 1000, 600);
        var size = new SizeD(300, 200);
        var low = PortalGeometry.KeepOnStage(new PointD(500, 580), size, 1, bounds);
        Assert.True(low.Y + size.Height / 2 <= bounds.MaxY);
        var middle = new PointD(500, 300);
        Assert.Equal(middle, PortalGeometry.KeepOnStage(middle, size, 1, bounds)); // A pill on the stage stays put
        var old = PortalGeometry.KeepOnStage(new PointD(950, 300), size, PortalGeometry.Scale(1.5), bounds);
        Assert.True(old.X + size.Width * 0.4 / 2 <= bounds.MaxX);
    }

    // Ported from StageReadingTests.swift: testMosaicStackMakesRoomForReadings
    [Fact]
    public void MosaicStackMakesRoomForReadings()
    {
        var plain = MosaicGeometry.StackStep(40, false);
        Assert.Equal(40 * 0.85, plain, 3);
        Assert.Equal(StageShared.ReadingSlot(MosaicGeometry.SmallSize(40)), MosaicGeometry.StackStep(40, true) - plain, 3);
    }

    // Ported from StageReadingTests.swift: testReadingSizes
    [Fact]
    public void ReadingSizes()
    {
        Assert.Equal(45, StageShared.ReadingSize(100), 3);
        Assert.Equal(12, StageShared.ReadingSize(20), 3);
        Assert.True(StageShared.ReadingSize(10) <= 10);
    }

    // Ported from StageLyricSnapshotTests.swift: testWordsOutOfOrderNeverCrashTheStageLayouts (the shown runs; the
    // drawing is covered by CompositionRenderTests)
    [Fact]
    public void WordsOutOfOrderNeverCrashTheStageLayouts()
    {
        LyricWord[] words = [new("hello", 12, 13), new("world", 10, 11), new("again", 11, 12)];
        var runs = StageRun.Make(words, [false, false, false]);
        Assert.Empty(StageRun.Shown(runs, 10.5)); // Held back until the first word arrives
        Assert.Equal([0, 1, 2], StageRun.Shown(runs, 12.5).Select(run => run.Id));
        var ordered = StageRun.Make(words.OrderBy(word => word.Start).ToList(), [false, false, false]);
        Assert.Equal(["world", "again"], StageRun.Shown(ordered, 11.5).Select(run => run.Text)); // In-order lines are unchanged
    }

    [Fact]
    public void LittleWordsBecomeFewLabelsWellApart()
    {
        var texts = "Rain over the city and the night of my dreams".Split(' ');
        Assert.True(BroadsideGeometry.IsLittle("the"));
        Assert.True(BroadsideGeometry.IsLittle("Don't"));
        Assert.False(BroadsideGeometry.IsLittle("Don't,")); // six characters with its comma
        Assert.False(BroadsideGeometry.IsLittle("city"));
        // "over" follows a big word; "the" follows a candidate; "and" is too soon after the last label.
        Assert.Equal([false, true, false, false, false, false, false, true, false, false], BroadsideGeometry.Labels(texts));
    }

    [Fact]
    public void ALineKeepsItsPaceAndChance()
    {
        Assert.Equal(1, StageShared.LineSpeed([1, 1.6, 2.2]));
        Assert.Equal(0.5 / 0.22, StageShared.LineSpeed([1, 1.22, 1.44, 1.66]), 6);
        Assert.Equal(3, StageShared.LineSpeed([1, 1.05, 1.1, 1.15]));
        Assert.Equal(StageShared.Chance(3, 2, 10), StageShared.Chance(3, 2, 10));
        Assert.NotEqual(StageShared.Chance(3, 2, 10), StageShared.Chance(4, 2, 10));
        for (var salt = 0; salt < 100; salt++) Assert.InRange(StageShared.Chance(7, salt % 9, salt), 0, 0.9999);
    }

    [Fact]
    public void StageShapesCloseAndKeepTheirSize()
    {
        Span<PointD> star = stackalloc PointD[8];
        StageShapes.Star4(new StageRect(0, 0, 10, 10), star);
        Assert.Equal(new PointD(5, 0), new PointD(Math.Round(star[0].X, 6), Math.Round(star[0].Y, 6)));
        Span<PointD> outline = stackalloc PointD[StageShapes.SketchyPoints];
        Assert.True(StageShapes.Sketchy(new StageRect(0, 0, 200, 80), 20, 3, 1.2, 4, outline));
        foreach (var point in outline)
        {
            Assert.InRange(point.X, -3.1, 203.1);
            Assert.InRange(point.Y, -3.1, 83.1);
        }
        Assert.False(StageShapes.Sketchy(new StageRect(0, 0, 0.2, 0.2), 0, 3, 0, 0, outline));
    }
}
