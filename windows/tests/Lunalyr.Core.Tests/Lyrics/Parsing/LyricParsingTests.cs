using System.Globalization;
using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

public class LyricParsingTests
{
    private const double Tol = 0.001;

    private static IReadOnlyList<LyricLine> Lrc(string text, double duration) => LrcParser.Parse(text, duration);

    // Ported from LyricParsingTests.swift: testEnhancedLRCUsesActualWordStartsAndEnds
    [Fact]
    public void EnhancedLrcUsesActualWordStartsAndEnds()
    {
        var lines = Lrc("[00:01.00]<00:01.00>Hold <00:02.00>on<00:04.00>\n[00:06.00]Next line", 12);
        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].WordTimed);
        Assert.Equal(1, lines[0].Words[0].Start, Tol);
        Assert.Equal(4, lines[0].Words[1].End, Tol);
        Assert.False(lines[1].WordTimed);
        Assert.Equal("Mixed timing", LyricTimingRules.Precision(lines));
    }

    // Ported from LyricParsingTests.swift: testLineOnlyDataIsExplicitlyEstimated
    [Fact]
    public void LineOnlyDataIsExplicitlyEstimated()
    {
        var lines = Lrc("[00:01]A small line\n[00:05]Another line", 10);
        Assert.Equal(2, lines.Count);
        Assert.False(lines[0].WordTimed);
        Assert.Equal("Line-synced", LyricTimingRules.Precision(lines));
        Assert.All(lines[0].Words, word => Assert.True(word.Start >= 1 && word.End <= 5));
    }

    // Ported from LyricParsingTests.swift: testOffsetsAndRepeatedTimestamps
    [Fact]
    public void OffsetsAndRepeatedTimestamps()
    {
        // A positive LRC offset shows the lyrics sooner.
        var lines = Lrc("[offset:250]\n[00:01.00][00:10.00]Again\n[00:05.00]", 15);
        Assert.Equal([0.75, 4.75, 9.75], lines.Select(line => line.Start));
        Assert.True(lines[1].IsInstrumental);
        Assert.Equal("Again", lines[2].Text);
    }

    // Ported from LyricParsingTests.swift: testOffsetSignFollowsTheLRCConvention
    [Fact]
    public void OffsetSignFollowsTheLrcConvention()
    {
        double Start(string tag) => Lrc(tag + "\n[00:10.00]Line", 20).FirstOrDefault()?.Start ?? 0;
        Assert.Equal(9.5, Start("[offset:+500]"), Tol);
        Assert.Equal(9.5, Start("[offset: 500]"), Tol);
        Assert.Equal(10.5, Start("[OFFSET:-500]"), Tol);
        Assert.Equal(8.75, Start("[offset: +1250 ]"), Tol);
        var words = Lrc("[offset:1000]\n[00:10.00]<00:10.00>Hold <00:11.00>on<00:12.00>", 20)[0].Words;
        Assert.Equal([9.0, 10.0], words.Select(word => word.Start));
        Assert.Equal(11, words[^1].End);
    }

    // Ported from LyricParsingTests.swift: testMalformedWordTimingFallsBackSafely
    [Fact]
    public void MalformedWordTimingFallsBackSafely()
    {
        var lines = Lrc("[00:01]<00:04>Backwards <00:02>words", 8);
        Assert.Single(lines);
        Assert.False(lines[0].WordTimed);
        Assert.Equal("Backwards words", lines[0].Text);
    }

    // Ported from LyricParsingTests.swift: testZeroLengthWordStampsKeepThatLineLineSynced
    [Fact]
    public void ZeroLengthWordStampsKeepThatLineLineSynced()
    {
        var lines = Lrc("[00:01]<00:01>Keep <00:01>moving<00:03>\n[00:04]<00:04>Hold <00:05>on<00:06>", 9);
        Assert.Equal([false, true], lines.Select(line => line.WordTimed));
        Assert.Equal(["Keep", "moving"], lines[0].Words.Select(word => word.Text));
        Assert.Equal([4.0, 5.0], lines[1].Words.Select(word => word.Start));
        Assert.Equal("Mixed timing", LyricTimingRules.Precision(lines));
    }

    // Ported from LyricParsingTests.swift: testWordStampsBeyondTheNextLineFallBackToEstimatedTiming
    [Fact]
    public void WordStampsBeyondTheNextLineFallBackToEstimatedTiming()
    {
        var lines = Lrc("[00:01]<00:01>one <00:10>two\n[00:04]next", 8);
        Assert.False(lines[0].WordTimed);
        Assert.All(lines[0].Words, word => Assert.True(word.End <= 4));
    }

    // Ported from LyricParsingTests.swift: testOffsetBeforeTheStartClampsAtZero
    [Fact]
    public void OffsetBeforeTheStartClampsAtZero()
    {
        Assert.Equal(0, Lrc("[offset:+2000]\n[00:01]A line", 8)[0].Start);
    }

    // Ported from LyricParsingTests.swift: testSharedTimestampsBecomeOneLineWithReadingOrTranslation
    // (the LyricLineIndex assertions belong to the line-index workstream and are not ported here)
    [Fact]
    public void SharedTimestampsBecomeOneLineWithReadingOrTranslation()
    {
        var text = """
            [00:10.00]君の名は
            [00:10.00]kimi no na wa
            [00:14.00]I love you
            [00:14.00]我爱你
            [00:18.00]我爱你
            [00:18.00]wo ai ni
            [00:22.00]사랑해
            [00:22.00]I love you
            [00:26.00]
            [00:26.00]Last line
            [00:30.00]Echo
            [00:30.00]Echo
            """;
        var lines = Lrc(text, 40);
        Assert.Equal(["君の名は", "I love you", "我爱你", "사랑해", "Last line", "Echo"], lines.Select(line => line.Text));
        Assert.Equal([10.0, 14, 18, 22, 26, 30], lines.Select(line => line.Start));
        Assert.Equal([14.0, 18, 22, 26, 30, 40], lines.Select(line => line.End));
        Assert.Equal(["kimi no na wa", null, "wo ai ni", null, null, null], lines.Select(line => line.Romanization));
        Assert.Equal([null, "我爱你", null, "I love you", null, null], lines.Select(line => line.Translation));
    }

    // Ported from LyricParsingTests.swift: testSharedTimestampPlaceholdersAndWordTimedCopies
    [Fact]
    public void SharedTimestampPlaceholdersAndWordTimedCopies()
    {
        var placeholder = Lrc("[00:10.00]♪\n[00:10.00]Hello there\n[00:15.00]Next\n[00:15.00]♪", 20);
        Assert.Equal(["Hello there", "Next"], placeholder.Select(line => line.Text));
        Assert.Equal([false, false], placeholder.Select(line => line.IsInstrumental));
        Assert.Equal([null, null], placeholder.Select(line => line.Translation));
        Assert.Equal(15, placeholder[0].End);
        var copy = Lrc("[00:10.00]Hello world\n[00:10.00]<00:10.00>Hello <00:11.00>world<00:12.00>\n[00:15.00]Next", 20);
        Assert.Equal(["Hello world", "Next"], copy.Select(line => line.Text));
        Assert.True(copy[0].WordTimed);
        Assert.Equal([10.0, 11.0], copy[0].Words.Select(word => word.Start));
        Assert.Equal(12, copy[0].Words[^1].End);
        Assert.Null(copy[0].Translation);
    }

    // Ported from LyricParsingTests.swift: testRowsMovedBeforeTheStartByAnOffsetStayApart
    // (the LyricLineIndex assertion belongs to the line-index workstream)
    [Fact]
    public void RowsMovedBeforeTheStartByAnOffsetStayApart()
    {
        var lines = Lrc("[offset:+800]\n[00:00.10]Song - Artist\n[00:00.50]First words here\n[00:05.00]Next", 20);
        Assert.Equal(["First words here", "Next"], lines.Select(line => line.Text));
        Assert.Equal(0, lines[0].Start);
        Assert.Equal(4.2, lines[^1].Start, Tol);
        Assert.Equal([null, null], lines.Select(line => line.Translation));
    }

    // Ported from LyricParsingTests.swift: testUnknownTrackDurationStillEndsTheLastLine
    [Fact]
    public void UnknownTrackDurationStillEndsTheLastLine()
    {
        foreach (var duration in new[] { double.NaN, double.PositiveInfinity, 0 })
        {
            Assert.Equal([3.0, 7.0], Lrc("[00:01]First\n[00:03]Last", duration).Select(line => line.End));
        }
    }

    // Ported from LyricParsingTests.swift: testFinalSyllableStampCanStillBeDivided
    [Fact]
    public void FinalSyllableStampCanStillBeDivided()
    {
        var lines = Lrc("[00:01.00]<00:01.00>beau<00:01.50>tiful day\n[00:05.00]next", 10);
        Assert.True(lines[0].WordTimed);
        Assert.Equal(["beautiful", "day"], lines[0].Words.Select(word => word.Text));
        Assert.Equal(1, lines[0].Words[0].Start, Tol);
        Assert.Equal(1.5 + 3.5 * 5 / 8, lines[0].Words[1].Start, Tol);
        Assert.Equal(5, lines[0].Words[1].End, Tol);
        Assert.True(LyricTimingRules.ValidWordTiming(lines[0]));
    }

    // Ported from LyricParsingTests.swift: testEnhancedSyllableStampsJoinIntoWords
    [Fact]
    public void EnhancedSyllableStampsJoinIntoWords()
    {
        var lines = Lrc("[00:01.00]<00:01.00>beau<00:01.20>ti<00:01.40>ful <00:02.00>day<00:03.00>\n[00:04.00]<00:04.00>君<00:04.50>の<00:05.00>", 8);
        Assert.Equal("beautiful day", lines[0].Text);
        Assert.Equal(["beautiful", "day"], lines[0].Words.Select(word => word.Text));
        Assert.Equal([2.0, 3.0], lines[0].Words.Select(word => word.End));
        Assert.Equal(["君", "の"], lines[1].Words.Select(word => word.Text));
        Assert.All(lines, line => Assert.True(LyricTimingRules.ValidWordTiming(line)));
    }

    // Ported from LyricParsingTests.swift: testCreditLinesAreDroppedButLyricsAboutInstrumentsStay
    [Fact]
    public void CreditLinesAreDroppedButLyricsAboutInstrumentsStay()
    {
        var text = """
            [00:00.000] 作词 : 唐恬
            [00:00.463] 作曲 : 钱雷
            [00:01.852] 吉他 : 高飞
            [00:02.315] 人声录音 : 利伟明
            [00:04.630]词版权管理方：北京梦织音传媒有限公司
            [00:05.070]出品监制：霍锦 卢泓宇
            [00:22.290]都 是勇敢的
            [00:28.500]我 鼓起勇气 说爱你
            [00:31.000]弹着 吉他 唱歌
            [00:34.000]男：你的 鼓励 是光
            """;
        var all = Lrc(text, 60);
        var lines = all.Where(line => line.Credit != true).ToList();
        Assert.Equal(6, all.Count(line => line.Credit == true));
        Assert.Equal(["都 是勇敢的", "我 鼓起勇气 说爱你", "弹着 吉他 唱歌", "男：你的 鼓励 是光"], lines.Select(line => line.Text));
        Assert.Equal(22.29, lines[0].Start, Tol);
    }

    // Ported from LyricParsingTests.swift: testEverydayLabelsCountOnlyAmongCredits
    [Fact]
    public void EverydayLabelsCountOnlyAmongCredits()
    {
        var text = """
            [00:00.00]光年之外 - 邓紫棋
            [00:00.50]作词：邓紫棋
            [00:01.00]鼓：张三
            [00:01.50]电吉他：李四
            [00:12.00]感受停在我发端的指尖
            [00:16.00]音乐：响起来
            [00:20.00]鼓：咚咚咚
            [03:50.00]制作人：王五
            [03:51.00]和声：赵六
            """;
        var all = Lrc(text, 240);
        Assert.Equal(["光年之外 - 邓紫棋", "作词：邓紫棋", "鼓：张三", "电吉他：李四", "制作人：王五", "和声：赵六"],
            all.Where(line => line.Credit == true).Select(line => line.Text));
        Assert.Equal(["感受停在我发端的指尖", "音乐：响起来", "鼓：咚咚咚"], all.Where(line => line.Credit != true).Select(line => line.Text));
    }

    // Ported from LyricParsingTests.swift: testUntimedAndOversizedFilesAreRejected
    [Fact]
    public void UntimedAndOversizedFilesAreRejected()
    {
        Assert.Empty(Lrc("Plain untimed text", 10));
        Assert.Empty(Lrc(new string('x', 1_000_001), 10));
    }

    // Ported from LyricParsingTests.swift: testRepeatedTimestampsCannotExpandWithoutBound
    [Fact]
    public void RepeatedTimestampsCannotExpandWithoutBound()
    {
        var stamps = string.Concat(Enumerable.Repeat("[00:00.00]", 40_000));
        var syllables = stamps + string.Concat(Enumerable.Repeat("<00:00.00>a", 50_000));
        var words = string.Concat(Enumerable.Repeat("[00:00.00]", 20_000)) + string.Concat(Enumerable.Repeat("a ", 300_000));
        foreach (var (name, row) in new[] { ("word stamps", syllables), ("plain words", words) })
        {
            var crafted = "[00:01.00]Before\n" + row + "\n[00:30.00]After";
            Assert.True(Encoding.UTF8.GetByteCount(crafted) <= LrcParser.InputLimit, name);
            var lines = Lrc(crafted, 60);
            // Reading stops at the row that would expand past the limit.
            Assert.Equal(["Before"], lines.Select(line => line.Text));
            Assert.True(lines.Sum(line => line.Words.Count) <= LrcParser.ExpansionLimit, name);
        }
        // Real files repeat a row a few times; many short copies are all read.
        var chorus = string.Concat(Enumerable.Range(0, 2_000).Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"[{i / 120:00}:{(i % 120) / 2.0:00.00}]"))) + "La la la";
        var repeated = Lrc(chorus + "\n[17:00.00]End", 1_100);
        Assert.Equal(2_001, repeated.Count);
        Assert.Equal(2_000, repeated.Count(line => line.Text == "La la la"));
        // Word stamps still move with each copy of their row.
        var twice = Lrc("[00:01.00][00:11.00]<00:01.00>Hold <00:02.00>on<00:03.00>\n[00:20.00]End", 30);
        Assert.Equal(3, twice.Count);
        Assert.Equal([1.0, 2.0], twice[0].Words.Select(word => word.Start));
        Assert.Equal([11.0, 12.0], twice[1].Words.Select(word => word.Start));
        Assert.Equal([20.0], twice[2].Words.Select(word => word.Start));
        Assert.Equal([true, true], twice.Take(2).Select(line => line.WordTimed));
    }

    // Ported from LyricCoreReviewTests.swift: testStampsWrittenApartAllLeadTheRow
    [Fact]
    public void StampsWrittenApartAllLeadTheRow()
    {
        var lines = Lrc("[00:12.00] [00:45.00]Chorus\n[00:20.00]Verse", 60);
        Assert.Equal(["Chorus", "Verse", "Chorus"], lines.Select(line => line.Text));
        Assert.Equal([12.0, 20, 45], lines.Select(line => line.Start));
    }

    // Ported from NetEaseParsingTests.swift: testCreditFilterAppliesToTheLRCFallback
    [Fact]
    public void CreditFilterAppliesToTheLrcFallback()
    {
        var lrc = """
            [00:00.000] 制作人 : Steve Mac
            [00:01.000] 作词 : Ed Sheeran/Johnny McDaid
            [00:02.000] 作曲 : Ed Sheeran/Johnny McDaid
            [00:09.480]The club isn't the best place to find a lover
            [00:11.750]So the bar is where I go
            """;
        var all = Lrc(lrc, 233);
        Assert.Equal(3, all.Count(line => line.Credit == true));
        Assert.Equal(["The club isn't the best place to find a lover", "So the bar is where I go"],
            all.Where(line => line.Credit != true).Select(line => line.Text));
    }

    [Fact]
    public void LrcLinesEndWhereTheNextLineBegins()
    {
        Assert.All(Lrc("[00:01]Hold on\n[00:35]The song returns", 40), line => Assert.True(line.EndFromNextLine));
    }

    [Fact]
    public void EstimatedWordsShareTheWindowByLength()
    {
        var words = EstimatedWords.Distribute("Hold on, tight", 0, 10);
        Assert.Equal(["Hold", "on,", "tight"], words.Select(word => word.Text));
        Assert.Equal(0, words[0].Start);
        Assert.Equal(8.8, words[^1].End, 1e-9);
        Assert.Equal(["君", "の", "名", "は"], EstimatedWords.Distribute("君の名は", 0, 4).Select(word => word.Text));
        Assert.Equal(["사랑해", "너를"], EstimatedWords.Distribute("사랑해 너를", 0, 4).Select(word => word.Text));
        Assert.Equal(["can't", "stop"], EstimatedWords.Distribute("can't stop", 0, 4).Select(word => word.Text));
    }
}
