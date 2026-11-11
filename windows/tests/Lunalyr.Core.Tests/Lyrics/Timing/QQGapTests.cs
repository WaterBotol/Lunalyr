using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using static Lunalyr.Core.Tests.Lyrics.Timing.TimingTestLines;

namespace Lunalyr.Core.Tests.Lyrics.Timing;

// The Mac's QQGapTests blame to the squashed import commit; their inputs and expected values are kept here as the
// behaviour spec of the re-implemented QQGapRepair.
public class QQGapTests
{
    // Ported from QQGapTests.swift: testLongMusicGapDoesNotExtendTheEstimatedWords
    [Fact]
    public void LongMusicGapDoesNotExtendTheEstimatedWords()
    {
        var original = Lrc("[00:01]Hold on\n[00:35]The song returns", 40);
        var fixedLines = QQGapRepair.Repair(original);
        Assert.Equal(3, fixedLines.Count);
        Assert.True(fixedLines[0].End < 10);
        Assert.True(fixedLines[0].Words[^1].End <= fixedLines[0].End);
        Assert.True(fixedLines[1].IsInstrumental);
        Assert.Equal(35, fixedLines[1].End);
        Assert.Equal(35, fixedLines[2].Start);
    }

    // Ported from QQGapTests.swift: testNormalVerseDoesNotGainInstrumentalBreaks
    [Fact]
    public void NormalVerseDoesNotGainInstrumentalBreaks()
    {
        var original = Lrc("[00:01]Keep it moving\n[00:04]One more phrase\n[00:07]Hold the light", 10);
        var fixedLines = QQGapRepair.Repair(original);
        Assert.Equal(original.Select(l => l.Start), fixedLines.Select(l => l.Start));
        Assert.Equal(original.Select(l => l.End), fixedLines.Select(l => l.End));
    }

    // Ported from QQGapTests.swift: testRealHeldWordIsNotShortened
    [Fact]
    public void RealHeldWordIsNotShortened()
    {
        var held = WordTimed(1, 35, "Stay", new LyricWord("Stay", 1, 34));
        var fixedLines = QQGapRepair.Repair([held]);
        Assert.Single(fixedLines);
        Assert.Equal(34, fixedLines[0].Words[0].End);
    }

    // Ported from QQGapTests.swift: testKnownWordEndCreatesGapWithoutRetimingWords
    [Fact]
    public void KnownWordEndCreatesGapWithoutRetimingWords()
    {
        var line = WordTimed(1, 35, "Stay", new LyricWord("Stay", 1, 4));
        var fixedLines = QQGapRepair.Repair([line]);
        Assert.Equal(2, fixedLines.Count);
        Assert.Equal(4, fixedLines[0].Words[0].End);
        Assert.True(fixedLines[1].IsInstrumental);
    }
}
