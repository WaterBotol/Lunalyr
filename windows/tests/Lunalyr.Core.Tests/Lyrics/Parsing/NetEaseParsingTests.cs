using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

/// <summary>
/// Fixtures follow NetEase's live song/lyric payloads: zero-length credit headers, timed copyright rows and
/// per-character Han timing in YRC, and a romalrc that shares the LRC timestamps.
/// </summary>
public class NetEaseParsingTests
{
    private const double Tol = 0.001;

    // Ported from NetEaseParsingTests.swift: testCreditHeadersAndCopyrightRowsAreNotLyrics
    [Fact]
    public void CreditHeadersAndCopyrightRowsAreNotLyrics()
    {
        var yrc = """
            {"t":0,"c":[{"tx":"作词: "},{"tx":"唐恬"}]}
            [0,0](0,0,0) 作词 : 唐恬
            [0,0](0,0,0) 吉他 : 高飞
            [0,0](0,0,0) 人声录音室 : 雅旺录音室
            [1,4890](1,270,0)词(270,270,0)版(540,270,0)权(810,270,0)管(1080,270,0)理(1350,270,0)方(1620,270,0)：(1890,270,0)北(2160,270,0)京
            [11140,3260](11140,270,0)录(11410,270,0)音(11680,270,0)作(11950,270,0)品(12220,270,0)及(12490,270,0)MV(12760,270,0)版(13030,270,0)权(13300,270,0)：(13570,270,0)EAS (13840,270,0)MUSIC
            [19560,2750](19560,270,0)出(19830,270,0)品(20100,270,0)监(20370,270,0)制(20640,270,0)：(20910,270,0)霍(21180,270,0)锦
            [22310,4300](22310,2880,0)都 (25190,310,0)是(25500,290,0)勇(25790,230,0)敢(26020,590,0)的
            """;
        var all = YrcParser.Parse(yrc);
        Assert.All(all.Where(line => line.Credit == true), line => Assert.True(line.IsInstrumental && line.Words.Count == 0));
        Assert.Equal(6, all.Count(line => line.Credit == true));
        var lines = all.Where(line => line.Credit != true).ToList();
        Assert.Equal(["都 是勇敢的"], lines.Select(line => line.Text));
        Assert.Equal(["都", "是", "勇", "敢", "的"], lines[0].Words.Select(word => word.Text));
        Assert.Equal(22.31, lines[0].Start, Tol);
        Assert.Equal(26.61, lines[0].End, Tol);
        Assert.All(lines, line => Assert.True(LyricLines.ValidWordTiming(line)));
    }

    // Ported from NetEaseParsingTests.swift: testLyricsThatMentionInstrumentsOrProductionAreKept
    [Fact]
    public void LyricsThatMentionInstrumentsOrProductionAreKept()
    {
        var yrc = """
            [30000,3000](30000,300,0)我 (30300,300,0)鼓(30600,300,0)起(30900,300,0)勇(31200,300,0)气 (31500,300,0)说(31800,300,0)爱(32100,300,0)你
            [34000,2000](34000,300,0)弹(34300,300,0)着 (34600,300,0)吉(34900,300,0)他 (35200,300,0)唱(35500,300,0)歌
            [37000,2000](37000,300,0)你(37300,300,0)的 (37600,300,0)鼓(37900,300,0)励 (38200,300,0)是(38500,300,0)光
            [40000,2000](40000,400,0)高(40400,400,0)鳴(40800,300,0)る(41100,300,0)鼓(41400,300,0)動(41700,300,0)が
            [43000,2000](43000,500,0)和(43500,500,0)声(44000,500,0)制(44500,500,0)作
            """;
        Assert.Equal(["我 鼓起勇气 说爱你", "弹着 吉他 唱歌", "你的 鼓励 是光", "高鳴る鼓動が", "和声制作"],
            YrcParser.Parse(yrc).Select(line => line.Text));
    }

    // Ported from NetEaseParsingTests.swift: testTimedCreditRowsWithQualifiersAreDropped
    [Fact]
    public void TimedCreditRowsWithQualifiersAreDropped()
    {
        var yrc = """
            [0,0](0,0,0) 作词 : 唐恬
            [1000,900](1000,300,0)电(1300,300,0)吉(1600,300,0)他 (1900,0,0): (1900,0,0)张三
            [2000,900](2000,300,0)鼓 (2300,0,0): (2300,0,0)李四
            [3000,900](3000,300,0)母(3300,300,0)带(3600,300,0)处(3900,0,0)理 (3900,0,0): (3900,0,0)王五
            [22310,4300](22310,2880,0)都 (25190,310,0)是(25500,290,0)勇(25790,230,0)敢(26020,590,0)的
            [27000,2000](27000,500,0)鼓(27500,0,0)：(27500,500,0)咚(28000,500,0)咚
            """;
        var all = YrcParser.Parse(yrc);
        Assert.Equal(4, all.Count(line => line.Credit == true));
        Assert.Equal(["都 是勇敢的", "鼓：咚咚"], all.Where(line => line.Credit != true).Select(line => line.Text));
    }

    // Ported from NetEaseParsingTests.swift: testBracketsInsideTheLyricAreKept
    [Fact]
    public void BracketsInsideTheLyricAreKept()
    {
        var yrc = """
            [1000,3000](1000,300,0)Oh (1300,600,0)(yeah) (1900,800,0)baby
            [5000,2000](5000,300,0)Oh (5300,100,0)((5400,300,0)yeah(5700,100,0)) (5800,600,0)baby
            """;
        var lines = YrcParser.Parse(yrc);
        Assert.Equal(["Oh (yeah) baby", "Oh (yeah) baby"], lines.Select(line => line.Text));
        Assert.Equal(["Oh", "(yeah)", "baby"], lines[0].Words.Select(word => word.Text));
        // A bracket timed on its own opens the next word instead of trailing the previous one.
        Assert.Equal(["Oh", "(yeah)", "baby"], lines[1].Words.Select(word => word.Text));
        Assert.Equal(5.4, lines[1].Words[1].Start, Tol);
        Assert.All(lines, line => Assert.True(LyricLines.ValidWordTiming(line)));
        Assert.All(lines, AssertWordsReadAsTheLine);
    }

    // Ported from NetEaseParsingTests.swift: testLatinSyllablesJoinAndPunctuationFolds
    [Fact]
    public void LatinSyllablesJoinAndPunctuationFolds()
    {
        var yrc = """
            [8000,3000](8000,300,0)beau(8300,200,0)ti(8500,400,0)ful (8900,500,0)day(9400,0,0)!
            [12000,2000](12000,300,0)Don(12300,200,0)'t (12500,600,0)stop
            """;
        var lines = YrcParser.Parse(yrc);
        Assert.Equal(["beautiful day!", "Don't stop"], lines.Select(line => line.Text));
        Assert.Equal(["beautiful", "day!"], lines[0].Words.Select(word => word.Text));
        Assert.Equal(8, lines[0].Words[0].Start, Tol);
        Assert.Equal(8.9, lines[0].Words[0].End, Tol);
        Assert.Equal(["Don't", "stop"], lines[1].Words.Select(word => word.Text));
        Assert.All(lines, AssertWordsReadAsTheLine);
    }

    // Ported from NetEaseParsingTests.swift: testPunctuationJoinsTheWordItTouches
    [Fact]
    public void PunctuationJoinsTheWordItTouches()
    {
        var yrc = """
            [1000,3000](1000,300,0)she (1300,300,0)said (1600,100,0)"(1700,300,0)Hello(2000,100,0)"
            [5000,3000](5000,300,0)Oh (5300,100,0)-(5400,300,0)yeah
            [9000,3000](9000,300,0)Oh(9300,100,0)-(9400,300,0)yeah
            [13000,3000](13000,500,0)Je (13500,500,0)t'aime (14000,4000,0)!
            [17000,2000](17000,300,0)爱(17300,100,0)，(17400,300,0)你(17700,300,0)（(18000,300,0)我(18300,100,0)）
            """;
        var lines = YrcParser.Parse(yrc);
        Assert.Equal(["she said \"Hello\"", "Oh -yeah", "Oh-yeah", "Je t'aime !", "爱，你（我）"], lines.Select(line => line.Text));
        Assert.Equal(["she", "said", "\"Hello\""], lines[0].Words.Select(word => word.Text));
        Assert.Equal(1.7, lines[0].Words[2].Start, Tol);
        Assert.Equal(["Oh", "-yeah"], lines[1].Words.Select(word => word.Text));
        Assert.Equal(["Oh-yeah"], lines[2].Words.Select(word => word.Text));
        Assert.Equal(["Je", "t'aime", "!"], lines[3].Words.Select(word => word.Text));
        Assert.Equal(16, lines[3].Words[2].End, Tol);
        Assert.Equal(["爱，", "你", "（我）"], lines[4].Words.Select(word => word.Text));
        Assert.All(lines, line => Assert.True(LyricLines.ValidWordTiming(line)));
        Assert.All(lines, AssertWordsReadAsTheLine);
    }

    /// <summary>The rendering contract: the words, in order, are the line's own text with only whitespace between them.</summary>
    private static void AssertWordsReadAsTheLine(LyricLine line)
    {
        var rest = line.Text;
        foreach (var word in line.Words)
        {
            var at = rest.IndexOf(word.Text, StringComparison.Ordinal);
            Assert.True(at >= 0 && string.IsNullOrWhiteSpace(rest[..at]), $"{word.Text} does not follow in {line.Text}");
            rest = rest[(at + word.Text.Length)..];
        }
        Assert.True(string.IsNullOrWhiteSpace(rest), line.Text);
    }

    // Ported from NetEaseParsingTests.swift: testZeroLengthRowWithSungWordsEndsWithItsWords
    [Fact]
    public void ZeroLengthRowWithSungWordsEndsWithItsWords()
    {
        var lines = YrcParser.Parse("[50000,0](50000,300,0)Hello (50300,400,0)there\r\n[52000,1000](52000,500,0)♪");
        Assert.Equal(2, lines.Count);
        Assert.Equal("Hello there", lines[0].Text);
        Assert.Equal(50.7, lines[0].End, Tol);
        Assert.True(lines[1].IsInstrumental);
    }

    // Ported from NetEaseParsingTests.swift: testRomanisedLinesAttachToTheLinesTheyRead
    [Fact]
    public void RomanisedLinesAttachToTheLinesTheyRead()
    {
        var yrc = """
            [273,3010](273,300,0)無(573,300,0)敵(873,300,0)の(1173,300,0)笑(1473,300,0)顔
            [3330,2585](3330,300,0)知(3630,300,0)り(3930,300,0)た(4230,300,0)い
            [14520,1400](14520,300,0)You're (14820,300,0)my (15120,600,0)savior
            [17215,1400](17215,300,0)今(17515,300,0)日
            """;
        var romalrc = """
            [00:00.273]mu te ki no e ga o
            [00:03.283]shi ri ta i
            [00:14.474]
            [00:16.400]kyo u
            """;
        var lines = YrcParser.AttachingRomanization(romalrc, YrcParser.Parse(yrc), 200);
        Assert.Equal(["mu te ki no e ga o", "shi ri ta i", null, null], lines.Select(line => line.Romanization));
        Assert.Equal(lines.Select(line => line.Text), YrcParser.AttachingRomanization(null, lines, 200).Select(line => line.Text));
    }

    // Ported from CreditsLayerTests.swift: testYRCKeepsCredits
    [Fact]
    public void YrcKeepsCredits()
    {
        var lines = YrcParser.Parse("[0,0](0,0,0)作词 : 方文山\n[1000,1500](1000,700,0)Hello(1700,800,0) world");
        Assert.Equal(1, lines.Count(line => line.Credit == true));
        Assert.Equal(1, lines.Count(line => !line.IsInstrumental));
    }
}
