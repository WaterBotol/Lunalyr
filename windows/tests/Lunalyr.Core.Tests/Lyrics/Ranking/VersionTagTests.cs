using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>Lyrics for another version of the song (Live, Remix, Acoustic…) rank below lyrics for the version playing, without being rejected.</summary>
public sealed class VersionTagTests
{
    private readonly LyricQuery _studio = new("Our Song", "Our Artist", "Our Album", 180);
    private readonly LyricSourceSnapshot _options = L.Options(LyricMatchMode.AccuracyFirst, NetEase, Lrclib, QQMusic, LyricsOvh);

    private static LyricCandidate Candidate(LyricSourceId source, bool words, string title = "Our Song", bool reported = true)
    {
        var line = L.Line("Sing along", 1, 3);
        if (words)
        {
            line = L.Timed(line, L.Word("Sing", 1, 2), L.Word("along", 2, 3));
        }
        return new LyricCandidate(source, L.Synced([line], "fixture", source.Title()), title, "Our Artist", 180, 0) { MetadataReported = reported };
    }

    private static void Tags(string name, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), VersionTags.Tags(name).Order(StringComparer.Ordinal));

    // Ported from VersionTagTests.swift: testTagsAreReadFromQualifiersInEveryCommonForm
    [Fact]
    public void TagsAreReadFromQualifiersInEveryCommonForm()
    {
        Tags("Our Song (Live)", "live");
        Tags("Our Song - Live at Wembley", "live");
        Tags("Our Song (Acoustic Version) [Remix]", "acoustic", "remix");
        Tags("Our Song (Club Mix)", "remix");
        Tags("Our Song (Extended Mix)", "extended");
        Tags("Our Song (Sped Up)", "sped up");
        Tags("Our Song (Radio Edit)", "edit");
        Tags("Our Song (Inst.)", "instrumental");
        Tags("今天没回家 (现场)", "live");
        Tags("夜に駆ける (Live版)", "live");
        Tags("曲名 ~Live ver.~", "live");
        Tags("Our Song [Mixed]", "dj mix");
    }

    // Windows-only: the span table matches kana with voicing marks, Hangul and full-width forms.
    [Fact]
    public void SpanTagsMatchWhateverTheFoldingDoesToVoicingMarks()
    {
        Tags("夜に駆ける (ライブ)", "live");
        Tags("밤양갱 (라이브)", "live");
        Tags("曲名 (オフボーカル)", "instrumental");
        Tags("曲名 （ＬＩＶＥ）", "live");
        Tags("曲名 (アコースティック)", "acoustic");
    }

    // Ported from VersionTagTests.swift: testSameRecordingQualifiersAreNotTags
    [Theory]
    [InlineData("Our Song")]
    [InlineData("Our Song (2011 Remaster)")]
    [InlineData("Our Song - Remastered 2011")]
    [InlineData("Our Song (feat. Someone)")]
    [InlineData("Our Song (Deluxe Edition)")]
    [InlineData("Our Song (Original Mix)")]
    [InlineData("Our Song (Mono)")]
    [InlineData("Our Song (Mixed by Someone)")]
    [InlineData("Our Song (Album Version)")]
    [InlineData("Liverpool Song")]
    [InlineData("Our Song (From \"The Film\")")]
    public void SameRecordingQualifiersAreNotTags(string name) => Assert.Empty(VersionTags.Tags(name));

    // Ported from VersionTagTests.swift: testMismatchCountsBothDirectionsButForgivesTheAlbum
    [Fact]
    public void MismatchCountsBothDirectionsButForgivesTheAlbum()
    {
        Assert.Equal(["live"], VersionTags.Mismatch(_studio, "Our Song (Live)"));
        var live = new LyricQuery("Our Song (Live)", "Our Artist", null, 180);
        Assert.Equal(["live"], VersionTags.Mismatch(live, "Our Song"));
        Assert.Empty(VersionTags.Mismatch(live, "Our Song - Live"));
        Assert.Empty(VersionTags.Mismatch(live, "Our Song (现场)"));
        var onLiveAlbum = new LyricQuery("Our Song", "Our Artist", "Our Album (Live)", 180);
        Assert.Empty(VersionTags.Mismatch(onLiveAlbum, "Our Song (Live)"));
        Assert.Empty(VersionTags.Mismatch(_studio, "Our Song (2011 Remaster)"));
        Assert.Equal("Live / Remix", VersionTags.Describe(["remix", "live"]));
    }

    // Ported from VersionTagTests.swift: testPlayingVersionOutranksAnotherVersionsWordTimingAndLRCLIBLead
    [Fact]
    public void PlayingVersionOutranksAnotherVersionsWordTimingAndLrclibLead()
    {
        var liveWords = Candidate(Lrclib, true, "Our Song (Live)");
        var studioLines = Candidate(QQMusic, false);
        var screened = LyricRanking.Screen([liveWords, studioLines], _studio, _options);
        // Not rejected: it can still be shown when nothing else is found.
        Assert.Equal([QQMusic, Lrclib], screened.Accepted.Select(c => c.Source));
        Assert.Equal(Lrclib, LyricRanking.Choose([liveWords], _studio, _options)?.Source);
    }

    // Ported from VersionTagTests.swift: testPlainTextStillRanksBelowAnotherVersionsTimedLyrics
    [Fact]
    public void PlainTextStillRanksBelowAnotherVersionsTimedLyrics()
    {
        var remix = Candidate(NetEase, false, "Our Song (Remix)");
        var plain = new LyricCandidate(LyricsOvh, L.Plain(["Sing along"], "", ""), "Our Song", "Our Artist", null, 0);
        Assert.Equal(NetEase, LyricRanking.Choose([plain, remix], _studio, _options)?.Source);
    }

    // Ported from VersionTagTests.swift: testAServiceThatOnlyRepeatsTheSearchIsNotHeldToIt
    [Fact]
    public void AServiceThatOnlyRepeatsTheSearchIsNotHeldToIt() =>
        Assert.True(LyricRanking.FitsVersion(Candidate(LyricsPlus, true, reported: false), _studio));

    // Ported from VersionTagTests.swift: testAnotherVersionKeepsTheLookupOpen
    [Fact]
    public void AnotherVersionKeepsTheLookupOpen()
    {
        var liveWords = Candidate(Lrclib, true, "Our Song (Live)");
        Assert.True(LyricRanking.CouldBeat(liveWords, QQMusic, _studio, _options));
        Assert.False(LyricRanking.IsSettled(liveWords, [QQMusic], _studio, _options));
        Assert.False(LyricRanking.CouldBeat(liveWords, LyricsOvh, _studio, _options));
        Assert.False(LyricRanking.CouldBeat(Candidate(Lrclib, true), LyricsOvh, _studio, _options));
    }

    // Ported from VersionTagTests.swift: testRankingTextMentionsVersions
    [Fact]
    public void RankingTextMentionsVersions() => Assert.Contains("Live, Remix", LyricSourceSnapshot.RankingText, StringComparison.Ordinal);
}
