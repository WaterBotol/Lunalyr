using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;

namespace Lunalyr.Platform.Windows.Tests.Readings;

/// <summary>
/// Ported from StageWordsUnspacedTests.swift (the Mac's 7c2493a3): stage layouts set an unspaced line (Japanese, Chinese)
/// by its real words, and Hinge opens it over several pages as it does English. Uses Windows' own word breaker, as the
/// stage layers do.
/// </summary>
public sealed class StageWordsUnspacedTests
{
    private static readonly IWordBreaker Breaker = TestReadingServices.Windows.WordBreaker;

    private static List<LyricWord> Timed(IEnumerable<string> pieces, double step = 0.2) =>
        [.. pieces.Select((text, i) => new LyricWord(text, 1 + i * step, 1 + i * step + step * 0.9))];

    private static List<StageRun> Runs(string text)
    {
        var words = Timed(text.Select(c => c.ToString()));
        var line = new LyricLine { Start = 1, End = 1 + words.Count * 0.2, Text = text, Words = words, WordTimed = true };
        return StageRun.Make(words, LyricRuns.Glue(line, words), Breaker);
    }

    [Fact]
    public void JapaneseRunsAreWordsNotThreeCharacterPieces()
    {
        var made = Runs("思い描いた未来の自分にはなれなかった").Select(r => r.Text).ToList();
        Assert.Equal("思い描いた未来の自分にはなれなかった", string.Concat(made));
        // No word cut in half: 未来 and 自分 each sit inside one run.
        Assert.Contains(made, run => run.Contains("未来", StringComparison.Ordinal));
        Assert.Contains(made, run => run.Contains("自分", StringComparison.Ordinal));
        Assert.All(made, run => Assert.True(run.Length <= StageRun.UnspacedWordLimit, run));
    }

    [Fact]
    public void ChineseRunsKeepTheirWords()
    {
        var made = Runs("我想要和你一起看星星直到天亮").Select(r => r.Text).ToList();
        Assert.Contains(made, run => run.Contains("星星", StringComparison.Ordinal));
        Assert.Contains(made, run => run.Contains("天亮", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("思い描いた未来の自分にはなれなかった")]
    [InlineData("我想要和你一起看星星直到天亮")]
    public void HingeOpensAnUnspacedLineOverSeveralPages(string line)
    {
        var phrasing = StagePhrases.Phrasing(Runs(line));
        Assert.True(HingeGeometry.Pages(phrasing).Count >= 2, line);
        Assert.All(phrasing.Phrases, phrase => Assert.True(phrase.To - phrase.From <= StagePhraseRules.UnspacedMaxRuns, line));
    }

    [Fact]
    public void EnglishPhrasingIsUnchanged()
    {
        const string text = "I can see the future that I pictured in my head";
        var words = Timed(text.Split(' ').Select(w => w + " "), step: 0.3);
        var line = new LyricLine { Start = 1, End = 1 + words.Count * 0.3, Text = text, Words = words, WordTimed = true };
        var phrasing = StagePhrases.Phrasing(StageRun.Make(words, LyricRuns.Glue(line, words), Breaker));
        Assert.Equal([3, 4, 4], phrasing.Phrases.Select(p => p.To - p.From));
    }
}
