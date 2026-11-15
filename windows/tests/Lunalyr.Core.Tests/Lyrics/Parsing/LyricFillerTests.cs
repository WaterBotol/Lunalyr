using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

/// <summary>"纯音乐，请欣赏" and its variants (tester feedback on 0.9.0; the Mac session's working set, 14 Nov 2026).</summary>
public class LyricFillerTests
{
    // The Mac's matches and non-matches (next/tester-fixes, 14 Nov 2026), plus half-width punctuation and spaces.
    [Theory]
    [InlineData("纯音乐，请欣赏")]
    [InlineData("纯音乐,请欣赏")]
    [InlineData("纯音乐 请欣赏")]
    [InlineData("纯音乐，请您欣赏")]
    [InlineData("此歌曲为没有填词的纯音乐，请您欣赏")]
    [InlineData("純音樂，請欣賞")]
    [InlineData("【纯音乐，请欣赏】")]
    [InlineData("纯音乐，请欣赏！")]
    [InlineData("  纯音乐，请欣赏！ ")]
    public void FillerLinesAreKnown(string line) => Assert.True(LyricFiller.IsFiller(line));

    [Theory]
    [InlineData("纯音乐")]
    [InlineData("请欣赏")]
    [InlineData("我爱纯音乐，请欣赏")]
    [InlineData("纯音乐，请欣赏这首歌")]
    [InlineData("这是纯音乐")]
    [InlineData("此歌曲为没有填词的纯音乐")]
    [InlineData("我爱纯音乐，请欣赏这首歌")]
    [InlineData("All my friends are heathens")]
    [InlineData("")]
    [InlineData(null)]
    public void OnlyAWholeFillerLineCounts(string? line) => Assert.False(LyricFiller.IsFiller(line));

    [Fact]
    public void FillerIsDroppedAndTheRestKept()
    {
        LyricLine Line(double start, string text) => new() { Start = start, End = start + 4, Text = text };
        IReadOnlyList<LyricLine> lines = [Line(0, "纯音乐，请欣赏"), Line(12, "晴天"), Line(16, "故事的小黄花")];
        var kept = LyricFiller.Without(lines);
        Assert.Equal(["晴天", "故事的小黄花"], kept.Select(l => l.Text));
        IReadOnlyList<LyricLine> clean = [Line(0, "晴天")];
        Assert.Same(clean, LyricFiller.Without(clean));
        Assert.Empty(LyricFiller.Without(["此歌曲为没有填词的纯音乐，请您欣赏"]));
    }
}
