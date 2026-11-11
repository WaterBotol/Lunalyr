using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>Fable's per-line variety and its layout, without rendering (the Mac's FableStyleTests).</summary>
public class FableLayoutTests
{
    // Ported from FableLayoutTests.swift: testNeighbouringLinesNeverShareShapeOrAccent
    [Fact]
    public void NeighbouringLinesNeverShareShapeOrAccent()
    {
        for (var seed = -60; seed <= 400; seed++)
        {
            FableStyle current = FableStyle.ForLine(seed), next = FableStyle.ForLine(seed + 1);
            Assert.True(current.Shape != next.Shape, $"lines {seed} and {seed + 1} share a shape");
            Assert.True(current.Accent != next.Accent, $"lines {seed} and {seed + 1} share an accent");
        }
    }

    // Ported from FableLayoutTests.swift: testEveryPairingComesRoundBeforeAnyRepeats
    [Fact]
    public void EveryPairingComesRoundBeforeAnyRepeats() =>
        Assert.Equal(25, Enumerable.Range(0, 25).Select(seed => FableStyle.ForLine(seed)).Distinct().Count());

    // Ported from FableLayoutTests.swift: testStyleIsTheLinesOwn
    [Fact]
    public void StyleIsTheLinesOwn()
    {
        for (var seed = 0; seed < 50; seed++) Assert.Equal(FableStyle.ForLine(seed), FableStyle.ForLine(seed));
    }

    // Ported from FableLayoutTests.swift: testHeroIsTheLongestWord
    [Fact]
    public void HeroIsTheLongestWord()
    {
        Assert.Null(FableStyle.HeroIndex(["Oh", "no"]));
        Assert.Equal(0, FableStyle.HeroIndex(["Rain", "over", "the", "city"]));
        Assert.Equal(1, FableStyle.HeroIndex(["a", "beautiful", "day"]));
    }

    // Ported from FableLayoutTests.swift: testLayoutKeepsWordsApartAndOnStage
    [Fact]
    public void LayoutKeepsWordsApartAndOnStage()
    {
        SizeD[] stages = [new(1210, 707), new(2048, 1037)];
        SizeD[][] lines =
        [
            [new(120, 80)],
            [new(1900, 90), new(150, 90)],
            [.. Enumerable.Range(0, 6).Select(i => new SizeD(90 + i * 37 % 120, 80 + i % 2 * 30))],
            [.. Enumerable.Range(0, 30).Select(i => new SizeD(60 + i * 53 % 200, 70 + i % 3 * 20))],
            [.. Enumerable.Range(0, 4).Select(_ => new SizeD(140, 170))],
        ];
        foreach (var stage in stages)
        foreach (var shape in Enum.GetValues<FableShape>())
        foreach (var sizes in lines)
        foreach (var rtl in new[] { false, true })
        {
            var laid = FableLayout.Arrange(sizes, shape, rtl, stage, 4, 6);
            Assert.Equal(sizes.Length, laid.Frames.Length);
            Assert.True(laid.Scale > 0.2);
            Assert.True(laid.Size.Width * laid.Scale <= stage.Width + 0.5);
            Assert.True(laid.Size.Height * laid.Scale <= stage.Height + 0.5);
            var box = new StageRect(0, 0, laid.Size.Width, laid.Size.Height).Inset(-0.5, -0.5);
            for (var i = 0; i < laid.Frames.Length; i++)
            {
                var frame = laid.Frames[i];
                Assert.True(box.Contains(frame), $"{shape} word {i} leaves its block");
                Assert.Equal(sizes[i], frame.Size);
                for (var j = i + 1; j < laid.Frames.Length; j++)
                    Assert.False(frame.Inset(0.5, 0.5).Intersects(laid.Frames[j]), $"{shape}: words {i} and {j} overlap");
            }
            if (sizes.Length > 1 && laid.Frames[0].MinY == laid.Frames.Min(f => f.MinY))
            {
                var firstRow = laid.Frames.Where(f => Math.Abs(f.MidY - laid.Frames[0].MidY) < 1).ToList();
                if (firstRow.Count > 1)
                    Assert.True(laid.Frames[0].MidX > firstRow.Min(f => f.MidX) == rtl, $"{shape} reads the wrong way (rtl {rtl})");
            }
        }
    }

    // Ported from FableLayoutTests.swift: testLongLinesWrapBeforeShrinking
    [Fact]
    public void LongLinesWrapBeforeShrinking()
    {
        var stage = new SizeD(1210, 707);
        var shortLine = FableLayout.Arrange([new SizeD(200, 90)], FableShape.Wave, false, stage, 4, 6);
        Assert.Equal(1, shortLine.Scale);
        var words = Enumerable.Repeat(new SizeD(180, 90), 18).ToList();
        var longLine = FableLayout.Arrange(words, FableShape.Arch, false, stage, 4, 6);
        Assert.True(longLine.Frames.Select(f => (int)(f.MinY / 10)).Distinct().Count() > 2, "a long line should take several rows");
        Assert.True(longLine.Scale > 0.6);
    }

    // Ported from FableLayoutTests.swift: testALoneLongLastWordKeepsItsRowWithinTheWidth
    [Fact]
    public void ALoneLongLastWordKeepsItsRowWithinTheWidth()
    {
        var sizes = new[] { 100, 100, 100, 1150 }.Select(w => new SizeD(w, 90)).ToList();
        var laid = FableLayout.Arrange(sizes, FableShape.Wave, false, new SizeD(1210, 707), 4, 6);
        Assert.Equal(1, laid.Scale, 3);
    }
}
