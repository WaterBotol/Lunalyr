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

    // Ported from StageReadingTests.swift: testHingeFocalSizeCountsReadings
    [Fact]
    public void HingeFocalSizeCountsReadings()
    {
        var plain = HingeGeometry.FocalSize([new HingeSlab(true, true, false, false)], held: true, Base * 1.5, Stage.Height);
        var read = HingeGeometry.FocalSize([new HingeSlab(true, true, true, true)], held: true, Base * 1.5, Stage.Height);
        Assert.True(read < plain, "A page with readings sets its focal word smaller to fit");
    }

    // Ported from StageReadingTests.swift: testTorqueFaceMakesRoomForReadings
    [Fact]
    public void TorqueFaceMakesRoomForReadings()
    {
        var plainSizes = Enumerable.Repeat(new SizeD(200, 100), 3).ToList();
        int[][] rows = [[0, 1], [2]];
        double[] baselines = [80, 80, 80];
        var plain = TorqueGeometry.ArrangeFace(plainSizes, baselines, rows, trailing: false, rightToLeft: false, wordGap: 10, rowGap: 0);
        const double slot = 50;
        var readSizes = plainSizes.Select(size => size with { Height = size.Height + slot }).ToList();
        var read = TorqueGeometry.ArrangeFace(readSizes, baselines, rows, trailing: false, rightToLeft: false, wordGap: 10, rowGap: 0);
        Assert.True(read.Frames[2].MinY > plain.Frames[2].MinY, "The row below moves down for the reading");
        Assert.True(read.Frames[2].MinY >= read.Frames[0].MaxY - 0.5, "Nothing overlaps a reading");
        Assert.True(read.Size.Height > plain.Size.Height);
        // Too tall for a short stage: fitted whole.
        var available = new SizeD(800, 120);
        var box = TorqueGeometry.Turn(0, read.Size, available).Apply(new StageRect(0, 0, read.Size.Width, read.Size.Height));
        Assert.True(box.Height <= available.Height + 0.5);
        Assert.Equal(read.Size.Height / 2, box.MidY, 0.5); // Fitted about its centre
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
        // The label rides on the shoulder of the word before: above its letters, flush with its end.
        Assert.True(places[1].Y + 15 <= bounds.MidY - 100 * 0.36, "The label is clear of the word's letters");
        Assert.Equal(places[0].X + 100, places[1].X + 30, 2); // The label is flush with the word's end
        // Without the reading, the label sits in the same place: it rides on the word, not on the word and its reading.
        SizeD[] plainSizes = [new(200, 100), measured[1], new(220, 100)];
        var plain = BroadsideGeometry.Places(plainSizes, little, [0, 0, 0], [1, 1, 1], 40, bounds);
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

    // Windows' own (sweep, 14 Nov 2026): Mosaic's letter-by-letter kind drew an Arabic word's letters left to right and
    // unjoined (حتى as ح ت ى); a word whose glyphs can't move one at a time is set huge instead.
    [Fact]
    public void MosaicNeverSetsRightToLeftOrJoinedWordsLetterByLetter()
    {
        string[] whole = ["حتى", "الصباح", "שלום", "नमस्ते"];
        var lettered = 0;
        for (var seed = 0L; seed < 40; seed++)
        for (var run = 0; run < 6; run++)
        {
            if (MosaicGeometry.Kind(seed, run, "morning") == MosaicGeometry.Letters) lettered++;
            foreach (var text in whole) Assert.NotEqual(MosaicGeometry.Letters, MosaicGeometry.Kind(seed, run, text));
            // Latin and CJK keep the Mac's chance: every kind, the same one for the same seed and run.
            Assert.Equal((int)(StageShared.Chance(seed, run, 20) * 3) % 3, MosaicGeometry.Kind(seed, run, "morning"));
            Assert.Equal(MosaicGeometry.Kind(seed, run, "morning"), MosaicGeometry.Kind(seed, run, "夕焼け"));
        }
        Assert.True(lettered > 0, "some Latin words are still set letter by letter");
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
