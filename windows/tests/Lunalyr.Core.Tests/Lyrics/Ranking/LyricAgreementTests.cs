using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>Other services' words break ties, and LRCLIB's word timing gives up its lead only when two other services agree on timing it disagrees with.</summary>
public sealed class LyricAgreementTests
{
    private readonly LyricQuery _query = new("Our Song", "Our Artist", null, 180);
    private static readonly string[] Verses = ["First line here", "Second line here", "Third line here", "Fourth line here", "Fifth line here"];

    private static LyricCandidate Candidate(LyricSourceId source, bool words, string[]? texts = null, double shift = 0)
    {
        var lines = (texts ?? Verses).Select((text, index) =>
        {
            var start = index * 5 + 2 + shift;
            var line = L.Line(text, start, start + 4);
            return words ? L.Timed(line, L.Word(text, start, start + 4)) : line;
        }).ToList();
        return new LyricCandidate(source, L.Synced(lines, "fixture", source.Title()), "Our Song", "Our Artist", 180, 0);
    }

    private static LyricSourceSnapshot Options(LyricMatchMode mode, params LyricSourceId[] order) => new(order, mode);

    // Ported from LyricAgreementTests.swift: testWordsAndTimingComparisons
    [Fact]
    public void WordsAndTimingComparisons()
    {
        var a = LyricAgreementRules.Text(Candidate(NetEase, true));
        Assert.True(LyricAgreementRules.SameWords(a, LyricAgreementRules.Text(Candidate(QQMusic, false))));
        Assert.False(LyricAgreementRules.SameWords(a, ["Something", "else", "entirely", "here", "now"]));
        Assert.False(LyricAgreementRules.SameWords([.. a.Take(3)], [.. a.Take(3)]), "too short to say");
        var t = LyricAgreementRules.Timing(Candidate(NetEase, true));
        Assert.True(LyricAgreementRules.TimingAgrees(t, LyricAgreementRules.Timing(Candidate(Kugou, true, shift: 0.4))));
        Assert.False(LyricAgreementRules.TimingAgrees(t, LyricAgreementRules.Timing(Candidate(Kugou, true, shift: 2))));
    }

    // Ported from LyricAgreementTests.swift: testAgreementBreaksTiesAmongOtherSourcesInAccuracyFirstOnly
    [Fact]
    public void AgreementBreaksTiesAmongOtherSourcesInAccuracyFirstOnly()
    {
        var lone = Candidate(NetEase, true, ["Wrong one", "Wrong two", "Wrong three", "Wrong four", "Wrong five"]);
        var backed = Candidate(QQMusic, true);
        var backer = Candidate(Kugou, false);
        LyricSourceId[] order = [NetEase, QQMusic, Kugou];
        Assert.Equal(QQMusic, LyricRanking.Screen([lone, backed, backer], _query, Options(LyricMatchMode.AccuracyFirst, order)).Accepted[0].Source);
        Assert.Equal(NetEase, LyricRanking.Screen([lone, backed, backer], _query, Options(LyricMatchMode.SourceOrder, order)).Accepted[0].Source);
    }

    // Ported from LyricAgreementTests.swift: testLRCLIBKeepsItsLeadUnlessTwoOthersAgreeAgainstIt
    [Fact]
    public void LrclibKeepsItsLeadUnlessTwoOthersAgreeAgainstIt()
    {
        LyricSourceId[] order = [NetEase, QQMusic, Lrclib];
        var late = Candidate(Lrclib, true, shift: 3);
        // One other service disagreeing is not enough.
        var top = LyricRanking.Screen([late, Candidate(NetEase, true)], _query, Options(LyricMatchMode.AccuracyFirst, order)).Accepted[0];
        Assert.Equal(Lrclib, top.Source);
        // Two agreeing with each other, both against LRCLIB: it is the odd one out.
        foreach (var mode in LyricMatchModeInfo.All)
        {
            var screened = LyricRanking.Screen([late, Candidate(NetEase, true), Candidate(QQMusic, true, shift: 0.3)], _query, Options(mode, order));
            Assert.Equal(NetEase, screened.Accepted[0].Source);
            Assert.True(screened.Accepted.First(c => c.Source == Lrclib).Agreement.TimingOutlier);
        }
        // LRCLIB agreeing with one of them keeps the lead.
        top = LyricRanking.Screen([Candidate(Lrclib, true), Candidate(NetEase, true), Candidate(QQMusic, true, shift: 3)],
            _query, Options(LyricMatchMode.AccuracyFirst, order)).Accepted[0];
        Assert.Equal(Lrclib, top.Source);
    }

    // Ported from LyricAgreementTests.swift: testAgreementDoesNotHoldTheLookupOpen
    [Fact]
    public void AgreementDoesNotHoldTheLookupOpen()
    {
        var options = Options(LyricMatchMode.SourceOrder, NetEase, QQMusic, Lrclib);
        var best = LyricRanking.Screen([Candidate(Lrclib, true)], _query, options).Accepted[0];
        Assert.False(LyricRanking.CouldBeat(best, QQMusic, _query, options));
    }

    // Ported from LyricAgreementTests.swift: testAnotherVersionIsNotCountedAsAgreement
    [Fact]
    public void AnotherVersionIsNotCountedAsAgreement()
    {
        var live = Candidate(NetEase, true, shift: 6) with { Title = "Our Song (Live)" };
        var liveToo = Candidate(QQMusic, true, shift: 6) with { Title = "Our Song (Live)" };
        var screened = LyricRanking.Screen([Candidate(Lrclib, true), live, liveToo], _query, Options(LyricMatchMode.AccuracyFirst, NetEase, QQMusic, Lrclib));
        Assert.Equal(Lrclib, screened.Accepted[0].Source);
        Assert.False(screened.Accepted[0].Agreement.TimingOutlier);
    }
}
