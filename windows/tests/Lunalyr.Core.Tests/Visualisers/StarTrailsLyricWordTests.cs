using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Visualisers;

namespace Lunalyr.Core.Tests.Visualisers;

public class StarTrailsLyricWordTests
{
    private static LyricLine Line((string Text, double Start, double End)[] words, bool? credit = null) => new()
    {
        Start = words[0].Start,
        End = words[^1].End,
        Text = string.Join(' ', words.Select(word => word.Text)),
        Words = [.. words.Select(word => new LyricWord(word.Text, word.Start, word.End))],
        IsInstrumental = false,
        WordTimed = true,
        Credit = credit,
    };

    // Ported from StarTrailsLyricWordTests.swift: testNewestWordAndItsPlaceInTheLine
    [Fact]
    public void NewestWordAndItsPlaceInTheLine()
    {
        LyricLine[] lines = [Line([("one", 1, 1.4), ("two", 1.5, 1.9), ("three", 2, 2.6)])];
        var word = StarTrailsLyricWord.Newest(lines, 1.6) ?? throw new Xunit.Sdk.XunitException("no word at 1.6 s");
        Assert.True(Math.Abs(word.Fraction - 0.5) <= 1e-9);
        Assert.True(Math.Abs(word.Age - 0.1) <= 1e-9);
        var last = StarTrailsLyricWord.Newest(lines, 2.1) ?? throw new Xunit.Sdk.XunitException("no word at 2.1 s");
        Assert.True(Math.Abs(last.Fraction - 1) <= 1e-9);
        Assert.NotEqual(word.Serial, last.Serial);
    }

    // Ported from StarTrailsLyricWordTests.swift: testNothingBeforeOrLongAfter
    [Fact]
    public void NothingBeforeOrLongAfter()
    {
        LyricLine[] lines = [Line([("one", 5, 5.4)])];
        Assert.Null(StarTrailsLyricWord.Newest(lines, 4));
        Assert.Null(StarTrailsLyricWord.Newest(lines, 9));
    }

    // Ported from StarTrailsLyricWordTests.swift: testCreditsLightNothing
    [Fact]
    public void CreditsLightNothing()
    {
        LyricLine[] lines = [Line([("Produced", 1, 1.4), ("by", 1.5, 1.9)], credit: true)];
        Assert.Null(StarTrailsLyricWord.Newest(lines, 1.6));
    }
}
