using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Tests.Lyrics.Timing;

/// <summary>When a better match replaces the lyrics on screen: the first shows at once, later ones between lines.</summary>
public sealed class LyricSwapTests
{
    private static LyricLine Line(string text, double start, double end) => new() { Text = text, Start = start, End = end };

    // Ported from LyricLookupProgressTests.swift: testUpgradesWaitForTheLineBeingSungToFinish
    [Fact]
    public void UpgradesWaitForTheLineBeingSungToFinish()
    {
        LyricLine[] lines = [Line("One", 1, 3), Line("Two", 5, 7)];
        var index = new LyricLineIndex(lines);
        Assert.Null(LyricSwap.LineBeingSung(lines, index, 0.5));
        Assert.Equal(0, LyricSwap.LineBeingSung(lines, index, 2));
        Assert.Null(LyricSwap.LineBeingSung(lines, index, 4));
        Assert.Equal(1, LyricSwap.LineBeingSung(lines, index, 6));
        // Windows additions: an instrumental break and an unknown time are no line being sung.
        LyricLine[] withBreak = [Line("One", 1, 3), new LyricLine { Start = 3, End = 9, Text = "", IsInstrumental = true }];
        Assert.Null(LyricSwap.LineBeingSung(withBreak, new LyricLineIndex(withBreak), 5));
        Assert.Null(LyricSwap.LineBeingSung(lines, index, double.NaN));
    }

    // Ported from LyricCoreReviewTests.swift: testARerunForLyricsOnScreenSwapsOnceAtALineBoundary
    [Fact]
    public void ARerunForLyricsOnScreenSwapsOnceAtALineBoundary()
    {
        static LyricLookupReport Update(int revision, bool complete, bool empty = false) => new()
        {
            Result = empty ? null : new LyricResult.Plain(["Sing"], new LyricProvenance("", "x")),
            Revision = revision,
            IsComplete = complete,
        };
        // A track's first lookup: its first match shows at once, upgrades swap between lines.
        Assert.Equal(LyricReception.Show, LyricSwap.Reception(Update(1, complete: false), applied: 0, rerun: false));
        Assert.Equal(LyricReception.SwapAtBoundary, LyricSwap.Reception(Update(2, complete: false), applied: 1, rerun: false));
        Assert.Equal(LyricReception.Ignore, LyricSwap.Reception(Update(2, complete: true), applied: 2, rerun: false));
        Assert.Equal(LyricReception.Show, LyricSwap.Reception(Update(0, complete: true, empty: true), applied: 0, rerun: false));
        // A rerun for lyrics on screen shows nothing it has not settled on.
        Assert.Equal(LyricReception.Ignore, LyricSwap.Reception(Update(1, complete: false), applied: 0, rerun: true));
        Assert.Equal(LyricReception.SwapAtBoundary, LyricSwap.Reception(Update(2, complete: true), applied: 0, rerun: true));
        // Finding nothing at all (every source switched off) clears at once.
        Assert.Equal(LyricReception.Show, LyricSwap.Reception(Update(0, complete: true, empty: true), applied: 0, rerun: true));
    }
}
