using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

public class LyricTextRulesTests
{
    // Ported from LyricTextRulesTests.swift: testCreditLabelsNeedACreditTermAndAColon
    [Fact]
    public void CreditLabelsNeedACreditTermAndAColon()
    {
        string[] credits =
        [
            "作词 : 方文山", "作曲：周杰伦", " 编曲 : 米津玄師/室屋光一郎", "制作人 Producer : Steve Mac", "编曲 Arranger: X",
            "人声录音室 : 雅旺录音室", "人声编辑 : 汝文博", "词版权管理方：北京梦织音传媒有限公司",
            "录音作品及MV版权：EAS MUSIC LTD", "联合出品：拳头游戏/腾讯游戏", "出品监制：霍锦 卢泓宇", "作词/作曲：某人",
            "吉他手：某人", "鼓 : 某人", "【作词】：某人", "作詞：秋元康", "작사: 누구", "Producer: Max Martin",
            "Lyrics by: Someone", "Composer & Arranger: B", "OP : Sony Music", "Mixing Engineer: C",
            "电吉他 : 张三", "木吉他：李四", "原声吉他 : 王五", "电贝斯 : 赵六", "架子鼓：孙七", "母带处理 : 周八",
            "总监制：吴九", "录音棚：雅旺录音室", "弦乐团：国际首席爱乐乐团", "特别鸣谢：某人", "Executive Producers: X",
            "Electric Guitar: X", "Bass Guitar: X", "Keyboard: X", "Mixed & Mastered by: X", "Background Vocal: X",
            "Vocal Engineer: X", "Recording Studio : X", "A&R : X", "Co-Producer: X", "鼓 Drums：某人",
            "艺术指导 : 某人", "监制 Supervisor：X", "吉他 : 西田修大", "贝斯：山口寛雄/玉田豊夢", "演唱：周杰伦",
            "合唱：上海彩虹室内合唱团", "弦乐 : 国际首席爱乐乐团", "出品 Presented by：某公司", "Cover Design: X",
            "MV Director: X",
        ];
        foreach (var text in credits) Assert.True(LyricCredits.IsCredit(text), text);
        string[] lyrics =
        [
            "我 鼓起勇气 说爱你", "弹着 吉他 唱歌", "你的 鼓励 是光", "高鳴る鼓動が", "鼓起勇气对他说：我爱你",
            "男：我爱你", "合：一起唱", "和声制作", "Baby: come back", "Writer of my heart: you", "12:30 at night",
            "Chorus: la la la", "制作一首歌：给你", "",
            "音乐：让我们一起", "Music: it's all I need", "Drums: boom boom", "鼓：咚咚", "(和声：啦啦啦)",
            "（和声：啦啦啦）", "Words: they never say", "电：快充满了", "By: the river", "钢琴：叮咚叮咚",
            "合唱：我们一起", "主唱：哦",
        ];
        foreach (var text in lyrics) Assert.False(LyricCredits.IsCredit(text), text);
        Assert.False(LyricCredits.IsCredit("鼓：张三", inCredits: false));
        Assert.False(LyricCredits.IsCredit("Music: Max Martin", inCredits: false));
        Assert.False(LyricCredits.IsCredit("合唱：永远不分离", inCredits: false));
        Assert.True(LyricCredits.IsCredit("制作人：张三", inCredits: false));
        Assert.True(LyricCredits.IsCredit("Producer: Max Martin", inCredits: false));
    }

    // Ported from LyricTextRulesTests.swift: testGluedSyllablesJoinOnlyInSpacedScripts
    [Fact]
    public void GluedSyllablesJoinOnlyInSpacedScripts()
    {
        var latin = LyricScript.Words([
            new("Ev", 1, 1.2, false), new("'ry", 1.2, 1.5, true), new("day", 1.6, 2, false), new(",", 2, 2.1, true),
        ]);
        Assert.Equal(["Ev'ry", "day,"], latin.Select(word => word.Text));
        Assert.Equal([1.5, 2], latin.Select(word => word.End));
        var japanese = LyricScript.Words([
            new("「", 3, 3.1, false), new("君", 3.1, 3.4, true), new("の", 3.4, 3.6, true), new("。", 3.6, 3.7, true),
        ]);
        Assert.Equal(["「君", "の。"], japanese.Select(word => word.Text));
        Assert.Equal([3, 3.4], japanese.Select(word => word.Start));
        var russian = LyricScript.Words([new("люб", 5, 5.3, false), new("лю", 5.3, 5.8, true)]);
        Assert.Equal(["люблю"], russian.Select(word => word.Text));
    }

    // Ported from LyricTextRulesTests.swift: testReadingsAreToldApartFromTranslations
    [Fact]
    public void ReadingsAreToldApartFromTranslations()
    {
        (string Candidate, string Lyric)[] readings =
        [
            ("kimi no na wa", "君の名は"), ("yu me na ra ba do re ho do yo ka tta de syo u", "夢ならばどれほどよかったでしょう"),
            ("wǒ ài nǐ", "我爱你"), ("ngo5 oi3 nei5", "我爱你"), ("saranghae", "사랑해"), ("Ya tebya lyublyu", "Я тебя люблю"),
            ("Hello sekai", "Hello 世界"), ("eien", "永遠"), ("kimi no na wa", "君の名は"),
        ];
        (string Candidate, string Lyric)[] translations =
        [
            ("Your name", "君の名は"), ("I love you", "사랑해"), ("Hello world", "Hello 世界"), ("我爱你", "I love you"),
            ("I love you", "I love you"), ("Forever", "永遠"), ("If only it were a dream", "夢ならばどれほどよかったでしょう"),
        ];
        // A fresh cache for each pair, then one cache shared by every pair of a file (twice over).
        var shared = new LyricScript.ReadingCache();
        foreach (var cache in new[] { null, shared, shared })
        {
            foreach (var (candidate, lyric) in readings) Assert.True(LyricScript.IsReading(candidate, lyric, cache), candidate);
            foreach (var (candidate, lyric) in translations) Assert.False(LyricScript.IsReading(candidate, lyric, cache), candidate);
        }
    }

    [Fact]
    public void AFullerReaderCanBeSupplied()
    {
        // The romanisation workstream's reader plugs in here; a fake stands in for it.
        var cache = new LyricScript.ReadingCache(text => text.Contains('爱') ? "wo ai ni" : null);
        Assert.True(LyricScript.IsReading("wo ai ni", "我爱你", cache));
        Assert.False(LyricScript.IsReading("I love you", "我爱你", cache));
    }

    [Fact]
    public void TheBasicReaderReadsKanaHangulAndCyrillic()
    {
        Assert.Equal("arigatou", BasicLatinReading.Read("ありがとう"));
        Assert.Equal("kyou", BasicLatinReading.Read("きょう"));
        Assert.Equal("kitto", BasicLatinReading.Read("きっと"));
        Assert.Equal("saranghae", BasicLatinReading.Read("사랑해"));
        Assert.Equal("privet", BasicLatinReading.Read("привет"));
        Assert.Null(BasicLatinReading.Read("夜"));
    }

    // Ported from MixedTimingCopyTests.swift: testSectionLabelsAreNotLyrics
    [Fact]
    public void SectionLabelsAreNotLyrics()
    {
        foreach (var label in new[] { "[Verse 1]", "[ Chorus ]", "Verse 3]", "[Pre-Chorus]", "(Bridge)", "[Chorus: Yot Club]" })
        {
            Assert.True(LyricCredits.IsSectionLabel(label), label);
        }
        foreach (var lyric in new[] { "Chorus", "Verse of a love song", "[oh oh oh]", "Bridge over troubled water", "(Intro) and then we go" })
        {
            Assert.False(LyricCredits.IsSectionLabel(lyric), lyric);
        }
        static Lunalyr.Core.Lyrics.LyricLine WordTimed(string text, double start, double end)
        {
            var pieces = text.Split(' ');
            var step = (end - start) / pieces.Length;
            return new Lunalyr.Core.Lyrics.LyricLine
            {
                Start = start, End = end, Text = text, WordTimed = true,
                Words = pieces.Select((piece, i) => new Lunalyr.Core.Lyrics.LyricWord(piece, start + i * step, start + (i + 1) * step)).ToList(),
            };
        }
        var shown = LyricCredits.Flagging([WordTimed("[Verse 1]", 1, 2), WordTimed("Hold on", 3, 4)]);
        Assert.True(shown[0].IsInstrumental);
        Assert.Equal("", shown[0].Text);
        Assert.False(shown[1].IsInstrumental);
    }
}
