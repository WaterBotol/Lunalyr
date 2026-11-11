using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

public sealed class LyricSelectionTests
{
    private readonly LyricQuery _query = new("Our Song", "Our Artist", null, 180);

    private static LyricCandidate Candidate(LyricSourceId source, bool word, double score = 100, string title = "Our Song", double duration = 180)
    {
        var line = L.Line("Sing along", 1, 3);
        if (word)
        {
            line = L.Timed(line, L.Word("Sing", 1, 2), L.Word("along", 2, 3));
        }
        return new LyricCandidate(source, L.Synced([line], "fixture", source.Title()), title, "Our Artist", duration, score);
    }

    // Ported from LyricSelectionTests.swift: testLineOnlyPriorityDoesNotHideAvailableRealWords
    [Fact]
    public void LineOnlyPriorityDoesNotHideAvailableRealWords()
    {
        var options = L.Options(LyricMatchMode.SourceOrder, Lrclib, NetEase);
        LyricCandidate[] values = [Candidate(Lrclib, false, 999), Candidate(NetEase, true, 1)];
        Assert.Equal(NetEase, LyricRanking.Choose(LyricRanking.Ranked(values, _query, options), _query, options)?.Source);
    }

    // Ported from LyricSelectionTests.swift: testAccuracyModeUsesTimingRatherThanProviderScoreOrCatalogue
    [Fact]
    public void AccuracyModeUsesTimingRatherThanProviderScoreOrCatalogue()
    {
        var options = L.Options(LyricMatchMode.AccuracyFirst, QQMusic, NetEase);
        LyricCandidate[] values = [Candidate(QQMusic, false, 999), Candidate(NetEase, true, 1)];
        Assert.Equal(NetEase, LyricRanking.Ranked(values, _query, options)[0].Source);
    }

    // Ported from LyricSelectionTests.swift: testWrongRecordingCannotWinWithPerfectWordTiming
    [Fact]
    public void WrongRecordingCannotWinWithPerfectWordTiming()
    {
        var options = L.Options(LyricMatchMode.AccuracyFirst, LyricsPlus, Lrclib);
        LyricCandidate[] values = [Candidate(LyricsPlus, true, title: "Unrelated Song", duration: 230), Candidate(Lrclib, false)];
        Assert.Equal([Lrclib], LyricRanking.Ranked(values, _query, options).Select(c => c.Source));
    }

    // Ported from LyricSelectionTests.swift: testSourceOrderBreaksEqualQualityTies
    [Fact]
    public void SourceOrderBreaksEqualQualityTies()
    {
        var options = L.Options(LyricMatchMode.AccuracyFirst, NetEase, LyricsPlus);
        LyricCandidate[] values = [Candidate(LyricsPlus, true, 999), Candidate(NetEase, true, 1)];
        Assert.Equal(NetEase, LyricRanking.Ranked(values, _query, options)[0].Source);
    }

    // Ported from LyricSelectionTests.swift: testPunctuationDifferencesDoNotDiscardRealWordTiming
    [Fact]
    public void PunctuationDifferencesDoNotDiscardRealWordTiming()
    {
        var line = ((LyricResult.Synced)Candidate(NetEase, true).Result).Lines[0] with { Text = "Sing, along!" };
        Assert.True(LyricTimingRules.HasWordTiming(L.Synced([line], "", "")));
        line = line with { Words = [.. line.Words.Take(line.Words.Count - 1)] };
        Assert.False(LyricTimingRules.ValidWordTiming(line));
    }
}
