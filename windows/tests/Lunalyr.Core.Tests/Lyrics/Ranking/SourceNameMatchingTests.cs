using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

public class SourceNameMatchingTests
{
    [Fact]
    public void NamesAreFoldedToWords()
    {
        Assert.Equal("beyonce halo", LyricQuery.Normalize("Beyoncé — HALO!"));
        Assert.Equal("don t stop", LyricQuery.Normalize("Don't  Stop"));
        Assert.Equal("米津玄師", LyricQuery.Normalize("米津玄師"));
        // Every non-spacing mark is folded away, a script's own vowel signs and virama included, so both names compare
        // alike. (The sources' former private copy kept them; the shared one keys saved choices and was kept. Whether
        // Foundation's diacritic folding drops Devanagari marks too is an open question for the Mac.)
        Assert.Equal("नमसत", LyricQuery.Normalize("नमस्ते!"));
        Assert.Equal(1, LyricQuery.Similarity("नमस्ते", "नमस्ते!"));
        Assert.Equal("", LyricQuery.Normalize("!!!"));
    }

    [Fact]
    public void SimilarityGradesQualifiersContainmentAndOverlap()
    {
        Assert.Equal(1, LyricQuery.Similarity("Our Song", "our song!"));
        Assert.Equal(0.95, LyricQuery.Similarity("Our Song", "Our Song (2011 Remaster)"));
        Assert.Equal(0.95, LyricQuery.Similarity("Our Song", "Our Song - Live"));
        Assert.Equal(0.95, LyricQuery.Similarity("Our Song", "Our Song (feat. Guest)"));
        Assert.Equal(0.85, LyricQuery.Similarity("Our Song", "Our Song Forever"));
        Assert.Equal(0.85, LyricQuery.Similarity("晴天", "晴天之歌"));
        Assert.Equal(0.5, LyricQuery.Similarity("Our Song", "Their Song"));
        Assert.Equal(1, LyricQuery.Similarity("!!!", "!!!"));
        Assert.Equal(0, LyricQuery.Similarity("!!!", "???"));
        Assert.Equal("our song", LyricQuery.Core("Our Song (Remastered) - Mono"));
    }

    [Fact]
    public void TheRecordingCheckComparesNamesAndLength()
    {
        var query = new LyricQuery("Our Song", "Our Artist", null, 180);
        Assert.True(LyricRanking.NamesRecording(LyricSourceId.NetEase, "Our Song", "Our Artist", 182, query));
        Assert.False(LyricRanking.NamesRecording(LyricSourceId.NetEase, "Our Song", "Our Artist", 184, query));
        Assert.False(LyricRanking.NamesRecording(LyricSourceId.NetEase, "Our Song", "Someone", 180, query));
        // A local file whose artist matches is not held to its length.
        Assert.True(LyricRanking.NamesRecording(LyricSourceId.Lyrimuse, "Our Song", "Our Artist", 200, query));
        // Without an artist, a length is required.
        var untagged = new LyricQuery("Our Song", "", null, 180);
        Assert.Equal(RecordingMatch.MissingIdentity,
            RecordingCheck.Match(1, 0.5, compareArtist: false, lengthRequired: true, compareLength: true, 180, 0));
        Assert.False(LyricRanking.NamesRecording(LyricSourceId.Lyrimuse, "Our Song", "", 200, untagged));
    }

}

/// <summary>
/// The per-source match score (<see cref="LyricMatchScore"/>), written fresh from the behaviour the Mac's callers and
/// tests pin down; its own scorer is Verso code and was not translated.
/// </summary>
public class LyricMatchScoreTests
{
    // Ported from Tests/geometry_test.c (the four vc_match_score asserts): word timing wins when the recording metadata
    // agrees; a wrong artist never wins; nor does a length 80 s off, or a confidence that is not a number.
    [Fact]
    public void GeometryTestMatchScoreAsserts()
    {
        Assert.True(LyricMatchScore.Score(1, 1, 1, true, true, true, true) > LyricMatchScore.Score(1, 1, 1, true, false, true, true));
        Assert.True(LyricMatchScore.Score(1, .2, 0, true, true, true, true) < 0);
        Assert.True(LyricMatchScore.Score(1, 1, 80, true, true, true, true) < 0);
        Assert.True(LyricMatchScore.Score(double.NaN, 1, 0, true, true, true, true) < 0);
    }

    [Fact]
    public void NamesWeighMoreThanAFewSecondsOfLength()
    {
        // LyricCoreReviewTests.swift, testLRCLIBOffersTheHitThatIsThePlayingRecording: "Our Song", 4 s too long, on the
        // album, outscores "Our Song - Remastered" by "Our Artist & Friend" on another album, whose length fits.
        var query = new LyricQuery("Our Song", "Our Artist", "Our Album", 180);
        var exact = LyricMatchScore.Score(LyricQuery.Similarity(query.Title, "Our Song"), query.ArtistScore("Our Artist"), 4, true, false, true, true);
        var edition = LyricMatchScore.Score(LyricQuery.Similarity(query.Title, "Our Song - Remastered"), query.ArtistScore("Our Artist & Friend"),
            0, true, false, true, false);
        Assert.True(exact > edition && edition > 0, $"{exact} vs {edition}");
        // testKugouFetchesTheHitThatIsThePlayingRecordingFirst: the exact title 3.5 s too long outscores "(Acoustic)".
        var shortSong = new LyricQuery("Our Song", "Our Artist", null, 120);
        Assert.True(LyricMatchScore.HitScore(shortSong, "Our Song", "Our Artist", 123.5) > LyricMatchScore.HitScore(shortSong, "Our Song (Acoustic)", "Our Artist", 120));
        Assert.True(LyricMatchScore.HitScore(shortSong, "Our Song (Acoustic)", "Our Artist", 120) > 0);
    }

    [Fact]
    public void OtherRecordingsAreRejectedAndEveryHitThatCouldBeOneScoresAboveZero()
    {
        Assert.True(LyricMatchScore.Score(0.5, 1, 0, true, false, true, false) < 0);
        Assert.True(LyricMatchScore.Score(1, 0.4, 0, true, false, true, false) < 0);
        Assert.True(LyricMatchScore.Score(1, 1, 31, true, false, true, false) < 0);
        Assert.True(LyricMatchScore.Score(1, 1, double.NaN, true, false, true, false) < 0);
        // An unknown length is not held against a hit, and a query without an artist (0.5) still matches.
        Assert.True(LyricMatchScore.Score(1, 0.5, double.NaN, false, false, true, false) > 0);
        // The weakest hit that passes still clears zero after LRCLIB's plain-text penalty.
        Assert.True(LyricMatchScore.Score(0.6, 0.5, 30, true, false, false, false) - LyricMatchScore.PlainTextPenalty > 0);
        // Closer is better: length, album and timing each add.
        Assert.True(LyricMatchScore.Score(1, 1, 0, true, false, true, false) > LyricMatchScore.Score(1, 1, 2, true, false, true, false));
        Assert.True(LyricMatchScore.Score(1, 1, 0, true, false, true, true) > LyricMatchScore.Score(1, 1, 0, true, false, true, false));
        Assert.True(LyricMatchScore.Score(1, 1, 0, true, false, true, false) > LyricMatchScore.Score(1, 1, 0, true, false, false, false));
    }
}

public class LyricTextDecoderTests
{
    static LyricTextDecoderTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const string Lrc = "[ti:夢ならば]\n[00:01.00]夢ならばどれほど\n[00:04.00]よかったでしょう";

    [Fact]
    public void UnicodeFilesAreReadWithOrWithoutAMark()
    {
        Assert.Equal(Lrc, LyricTextDecoder.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Lrc)]));
        Assert.Equal(Lrc, LyricTextDecoder.Decode(Encoding.UTF8.GetBytes(Lrc)));
        Assert.Equal(Lrc, LyricTextDecoder.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Lrc)]));
        Assert.Equal(Lrc, LyricTextDecoder.Decode(Encoding.Unicode.GetBytes(Lrc)));
        Assert.Equal(Lrc, LyricTextDecoder.Decode(Encoding.BigEndianUnicode.GetBytes(Lrc)));
    }

    [Fact]
    public void LegacyCodePagesAreRecognised()
    {
        Assert.Equal(Lrc, LyricTextDecoder.Decode(Encoding.GetEncoding(932).GetBytes(Lrc)));
        const string chinese = "[ti:晴天]\n[00:01.00]故事的小黄花\n[00:04.00]从出生那年就飘着";
        Assert.Equal(chinese, LyricTextDecoder.Decode(Encoding.GetEncoding(54936).GetBytes(chinese)));
        // EUC-KR Hangul is also valid GB18030 Hanzi byte for byte; without language detection the order decides, so
        // a Korean legacy file is read as Chinese. Known gap against the Mac.
        const string korean = "[ti:봄날]\n[00:01.00]보고 싶다";
        Assert.Contains(korean, LyricTextDecoder.Decodings(Encoding.GetEncoding(51949).GetBytes(korean)));
    }

    [Fact]
    public void XmlDeclarationsNoLongerNameTheOldEncoding()
    {
        const string ttml = "<?xml version=\"1.0\" encoding=\"Shift_JIS\"?><tt><body><p begin=\"1s\" end=\"2s\">夢</p></body></tt>";
        var decoded = LyricTextDecoder.Decode(Encoding.GetEncoding(932).GetBytes(ttml));
        Assert.Equal(ttml.Replace("Shift_JIS", "UTF-8", StringComparison.Ordinal), decoded);
        Assert.Null(LyricTextDecoder.Decode([]));
    }
}
