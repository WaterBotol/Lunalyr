using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// Windows-only rule: a single token covering a line of several words is line timing, not word timing
/// (LRCLIB Lyricsfile rows for line-synced uploads). Found with tools/LyricProbe on "Lemon" and "晴天".
/// </summary>
public class WholeLineTokenTests
{
    static LyricLine Line(string text, params (string Text, double Start, double End)[] words) => new()
    {
        Start = 10,
        End = 14,
        Text = text,
        WordTimed = true,
        Words = words.Select(w => new LyricWord(w.Text, w.Start, w.End)).ToArray(),
    };

    [Fact]
    public void AWholeLineTokenOverSeveralWordsIsNotWordTiming() =>
        Assert.False(LyricTimingRules.ValidWordTiming(Line("dreams are made of this", ("dreams are made of this", 10, 14))));

    [Fact]
    public void AWholeLineTokenOverAnUnspacedLineIsNotWordTiming() =>
        Assert.False(LyricTimingRules.ValidWordTiming(Line("故事的小黄花", ("故事的小黄花", 10, 14))));

    [Fact]
    public void AOneWordLineWithOneTokenStillCounts()
    {
        Assert.True(LyricTimingRules.ValidWordTiming(Line("Yeah!", ("Yeah!", 10, 11))));
        Assert.True(LyricTimingRules.ValidWordTiming(Line("晴天", ("晴天", 10, 11))));
    }

    [Fact]
    public void RealWordTimingStillCounts() =>
        Assert.True(LyricTimingRules.ValidWordTiming(Line("made of this", ("made", 10, 11), ("of", 11, 12), ("this", 12, 13))));

    [Fact]
    public void AResultOfWholeLineTokensIsLineSynced()
    {
        var lines = new[]
        {
            Line("dreams are made of this", ("dreams are made of this", 10, 14)),
            Line("who am I to disagree", ("who am I to disagree", 10, 14)),
        };
        var result = new LyricResult.Synced(lines, new LyricProvenance("", "LRCLIB"));
        Assert.False(LyricTimingRules.HasWordTiming(result));
    }

    [Fact]
    public void WholeLineTokensWithAFewOneWordLinesAreStillLineSynced()
    {
        // "Lemon" on LRCLIB: whole-line tokens, plus short lines that are genuinely one word.
        var lines = new[]
        {
            Line("夢ならばどれほどよかったでしょう", ("夢ならばどれほどよかったでしょう", 10, 14)),
            Line("苦い", ("苦い", 10, 11)),
        };
        Assert.Equal(1, LyricTimingRules.TimingTier(new LyricResult.Synced(lines, new LyricProvenance("", "LRCLIB"))));
        Assert.Equal("Line-synced", LyricTimingRules.Precision(lines));
    }

    [Fact]
    public void PhraseTokensInJapaneseAreNotWordTiming() =>
        // "Lemon" on LRCLIB: a line timed as its two space-separated phrases.
        Assert.False(LyricTimingRules.ValidWordTiming(Line("あの日の悲しみさえ あの日の苦しみさえ",
            ("あの日の悲しみさえ", 10, 12), ("あの日の苦しみさえ", 12, 14))));

    [Fact]
    public void SyllableTimingInJapaneseStillCounts() =>
        Assert.True(LyricTimingRules.ValidWordTiming(Line("夢ならば", ("夢", 10, 11), ("な", 11, 12), ("ら", 12, 13), ("ば", 13, 14))));

    [Fact]
    public void OneRealWordTimedLineMakesItMixed()
    {
        var lines = new[]
        {
            Line("dreams are made of this", ("dreams are made of this", 10, 14)),
            Line("made of this", ("made", 10, 11), ("of", 11, 12), ("this", 12, 13)),
        };
        Assert.Equal(2, LyricTimingRules.TimingTier(new LyricResult.Synced(lines, new LyricProvenance("", "LRCLIB"))));
    }
}
