using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

public class SourceMatchingTests
{
    [Fact]
    public void NamesAreFoldedToWords()
    {
        Assert.Equal("beyonce halo", SourceMatching.Normalize("Beyoncé — HALO!"));
        Assert.Equal("don t stop", SourceMatching.Normalize("Don't  Stop"));
        Assert.Equal("米津玄師", SourceMatching.Normalize("米津玄師"));
        // Marks that are part of a script stay inside the word.
        Assert.Equal("नमस्ते", SourceMatching.Normalize("नमस्ते!"));
        Assert.Equal("", SourceMatching.Normalize("!!!"));
    }

    [Fact]
    public void SimilarityGradesQualifiersContainmentAndOverlap()
    {
        Assert.Equal(1, SourceMatching.Similarity("Our Song", "our song!"));
        Assert.Equal(0.95, SourceMatching.Similarity("Our Song", "Our Song (2011 Remaster)"));
        Assert.Equal(0.95, SourceMatching.Similarity("Our Song", "Our Song - Live"));
        Assert.Equal(0.95, SourceMatching.Similarity("Our Song", "Our Song (feat. Guest)"));
        Assert.Equal(0.85, SourceMatching.Similarity("Our Song", "Our Song Forever"));
        Assert.Equal(0.85, SourceMatching.Similarity("晴天", "晴天之歌"));
        Assert.Equal(0.5, SourceMatching.Similarity("Our Song", "Their Song"));
        Assert.Equal(1, SourceMatching.Similarity("!!!", "!!!"));
        Assert.Equal(0, SourceMatching.Similarity("!!!", "???"));
        Assert.Equal("our song", SourceMatching.Core("Our Song (Remastered) - Mono"));
    }

    [Fact]
    public void TheRecordingCheckComparesNamesAndLength()
    {
        var query = new LyricQuery("Our Song", "Our Artist", null, 180);
        Assert.True(SourceMatching.NamesRecording(LyricSourceId.NetEase, "Our Song", "Our Artist", 182, query));
        Assert.False(SourceMatching.NamesRecording(LyricSourceId.NetEase, "Our Song", "Our Artist", 184, query));
        Assert.False(SourceMatching.NamesRecording(LyricSourceId.NetEase, "Our Song", "Someone", 180, query));
        // A local file whose artist matches is not held to its length.
        Assert.True(SourceMatching.NamesRecording(LyricSourceId.Lyrimuse, "Our Song", "Our Artist", 200, query));
        // Without an artist, a length is required.
        var untagged = new LyricQuery("Our Song", "", null, 180);
        Assert.Equal(HitCheck.MissingIdentity,
            SourceMatching.Check(1, 0.5, compareArtist: false, lengthRequired: true, compareLength: true, 180, 0));
        Assert.False(SourceMatching.NamesRecording(LyricSourceId.Lyrimuse, "Our Song", "", 200, untagged));
    }

    [Fact]
    public void TheMatchScoreRejectsOtherRecordingsAndRewardsFit()
    {
        Assert.True(SourceMatching.MatchScore(0.6, 1, 0, true, false, true, false) < 0);
        Assert.True(SourceMatching.MatchScore(1, 0.4, 0, true, false, true, false) < 0);
        Assert.True(SourceMatching.MatchScore(1, 1, 26, true, false, true, false) < 0);
        Assert.Equal(48 + 27 + 14 + 7, SourceMatching.MatchScore(1, 1, 0, true, false, true, false));
        Assert.Equal(48 + 27 - 22 + 20 + 4, SourceMatching.MatchScore(1, 1, 25, true, true, true, true));
        Assert.Equal(48 + 27, SourceMatching.MatchScore(1, 1, 99, false, false, false, false));
        Assert.True(SourceMatching.MatchScore(double.NaN, 1, 0, false, false, false, false) < 0);
    }
}

public class LyricFileDecoderTests
{
    static LyricFileDecoderTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const string Lrc = "[ti:夢ならば]\n[00:01.00]夢ならばどれほど\n[00:04.00]よかったでしょう";

    [Fact]
    public void UnicodeFilesAreReadWithOrWithoutAMark()
    {
        Assert.Equal(Lrc, LyricFileDecoder.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Lrc)]));
        Assert.Equal(Lrc, LyricFileDecoder.Decode(Encoding.UTF8.GetBytes(Lrc)));
        Assert.Equal(Lrc, LyricFileDecoder.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Lrc)]));
        Assert.Equal(Lrc, LyricFileDecoder.Decode(Encoding.Unicode.GetBytes(Lrc)));
        Assert.Equal(Lrc, LyricFileDecoder.Decode(Encoding.BigEndianUnicode.GetBytes(Lrc)));
    }

    [Fact]
    public void LegacyCodePagesAreRecognised()
    {
        Assert.Equal(Lrc, LyricFileDecoder.Decode(Encoding.GetEncoding(932).GetBytes(Lrc)));
        const string chinese = "[ti:晴天]\n[00:01.00]故事的小黄花\n[00:04.00]从出生那年就飘着";
        Assert.Equal(chinese, LyricFileDecoder.Decode(Encoding.GetEncoding(54936).GetBytes(chinese)));
        // EUC-KR Hangul is also valid GB18030 Hanzi byte for byte; without language detection the order decides, so
        // a Korean legacy file is read as Chinese. Known gap against the Mac.
        const string korean = "[ti:봄날]\n[00:01.00]보고 싶다";
        Assert.Contains(korean, LyricFileDecoder.Decodings(Encoding.GetEncoding(51949).GetBytes(korean)));
    }

    [Fact]
    public void XmlDeclarationsNoLongerNameTheOldEncoding()
    {
        const string ttml = "<?xml version=\"1.0\" encoding=\"Shift_JIS\"?><tt><body><p begin=\"1s\" end=\"2s\">夢</p></body></tt>";
        var decoded = LyricFileDecoder.Decode(Encoding.GetEncoding(932).GetBytes(ttml));
        Assert.Equal(ttml.Replace("Shift_JIS", "UTF-8", StringComparison.Ordinal), decoded);
        Assert.Null(LyricFileDecoder.Decode([]));
    }
}
