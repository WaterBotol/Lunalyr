import XCTest
@testable import VersoCustom

final class LyricTextRulesTests: XCTestCase {
    func testCreditLabelsNeedACreditTermAndAColon() {
        let credits = [
            "作词 : 方文山", "作曲：周杰伦", " 编曲 : 米津玄師/室屋光一郎", "制作人 Producer : Steve Mac", "编曲 Arranger: X",
            "人声录音室 : 雅旺录音室", "人声编辑 : 汝文博", "词版权管理方：北京梦织音传媒有限公司",
            "录音作品及MV版权：EAS MUSIC LTD", "联合出品：拳头游戏/腾讯游戏", "出品监制：霍锦 卢泓宇", "作词/作曲：某人",
            "吉他手：某人", "鼓 : 某人", "【作词】：某人", "作詞：秋元康", "작사: 누구", "Producer: Max Martin",
            "Lyrics by: Someone", "Composer & Arranger: B", "OP : Sony Music", "Mixing Engineer: C",
            // Labels with a qualifier, a suffix or a compound role.
            "电吉他 : 张三", "木吉他：李四", "原声吉他 : 王五", "电贝斯 : 赵六", "架子鼓：孙七", "母带处理 : 周八",
            "总监制：吴九", "录音棚：雅旺录音室", "弦乐团：国际首席爱乐乐团", "特别鸣谢：某人", "Executive Producers: X",
            "Electric Guitar: X", "Bass Guitar: X", "Keyboard: X", "Mixed & Mastered by: X", "Background Vocal: X",
            "Vocal Engineer: X", "Recording Studio : X", "A&R : X", "Co-Producer: X", "鼓 Drums：某人",
            "艺术指导 : 某人", "监制 Supervisor：X", "吉他 : 西田修大", "贝斯：山口寛雄/玉田豊夢", "演唱：周杰伦",
            "合唱：上海彩虹室内合唱团", "弦乐 : 国际首席爱乐乐团", "出品 Presented by：某公司", "Cover Design: X",
            "MV Director: X",
        ]
        for text in credits { XCTAssertTrue(LyricCredits.isCredit(text), text) }
        let lyrics = [
            "我 鼓起勇气 说爱你", "弹着 吉他 唱歌", "你的 鼓励 是光", "高鳴る鼓動が", "鼓起勇气对他说：我爱你",
            "男：我爱你", "合：一起唱", "和声制作", "Baby: come back", "Writer of my heart: you", "12:30 at night",
            "Chorus: la la la", "制作一首歌：给你", "",
            // An everyday word as the label needs names after it, and an aside
            // in brackets is sung.
            "音乐：让我们一起", "Music: it's all I need", "Drums: boom boom", "鼓：咚咚", "(和声：啦啦啦)",
            "（和声：啦啦啦）", "Words: they never say", "电：快充满了", "By: the river", "钢琴：叮咚叮咚",
            "合唱：我们一起", "主唱：哦",
        ]
        for text in lyrics { XCTAssertFalse(LyricCredits.isCredit(text), text) }
        // Away from other credits, an everyday label is read as a lyric; a
        // specific role is a credit wherever it appears.
        XCTAssertFalse(LyricCredits.isCredit("鼓：张三", inCredits: false))
        XCTAssertFalse(LyricCredits.isCredit("Music: Max Martin", inCredits: false))
        XCTAssertFalse(LyricCredits.isCredit("合唱：永远不分离", inCredits: false))
        XCTAssertTrue(LyricCredits.isCredit("制作人：张三", inCredits: false))
        XCTAssertTrue(LyricCredits.isCredit("Producer: Max Martin", inCredits: false))
    }

    func testGluedSyllablesJoinOnlyInSpacedScripts() {
        typealias Token = LyricScript.TimedToken
        let latin = LyricScript.words(from: [
            Token(text: "Ev", start: 1, end: 1.2, glued: false), Token(text: "'ry", start: 1.2, end: 1.5, glued: true),
            Token(text: "day", start: 1.6, end: 2, glued: false), Token(text: ",", start: 2, end: 2.1, glued: true),
        ])
        XCTAssertEqual(latin.map(\.text), ["Ev'ry", "day,"])
        XCTAssertEqual(latin.map(\.end), [1.5, 2])
        let japanese = LyricScript.words(from: [
            Token(text: "「", start: 3, end: 3.1, glued: false), Token(text: "君", start: 3.1, end: 3.4, glued: true),
            Token(text: "の", start: 3.4, end: 3.6, glued: true), Token(text: "。", start: 3.6, end: 3.7, glued: true),
        ])
        XCTAssertEqual(japanese.map(\.text), ["「君", "の。"])
        XCTAssertEqual(japanese.map(\.start), [3, 3.4])
        let russian = LyricScript.words(from: [
            Token(text: "люб", start: 5, end: 5.3, glued: false), Token(text: "лю", start: 5.3, end: 5.8, glued: true),
        ])
        XCTAssertEqual(russian.map(\.text), ["люблю"])
    }

    func testReadingsAreToldApartFromTranslations() {
        let readings = [
            ("kimi no na wa", "君の名は"), ("yu me na ra ba do re ho do yo ka tta de syo u", "夢ならばどれほどよかったでしょう"),
            ("wǒ ài nǐ", "我爱你"), ("ngo5 oi3 nei5", "我爱你"), ("saranghae", "사랑해"), ("Ya tebya lyublyu", "Я тебя люблю"),
            ("Hello sekai", "Hello 世界"), ("eien", "永遠"), ("kimi no na wa", "君の名は"),
        ]
        let translations = [
            ("Your name", "君の名は"), ("I love you", "사랑해"), ("Hello world", "Hello 世界"), ("我爱你", "I love you"),
            ("I love you", "I love you"), ("Forever", "永遠"), ("If only it were a dream", "夢ならばどれほどよかったでしょう"),
        ]
        // A fresh cache for each pair, then one cache shared by every pair of a
        // file (twice over, so its stored readings and tokenizer are reused).
        let shared = LyricScript.ReadingCache()
        for cache in [nil, shared, shared] as [LyricScript.ReadingCache?] {
            for (candidate, lyric) in readings {
                XCTAssertTrue(LyricScript.isReading(candidate, of: lyric, cache: cache ?? .init()), candidate)
            }
            for (candidate, lyric) in translations {
                XCTAssertFalse(LyricScript.isReading(candidate, of: lyric, cache: cache ?? .init()), candidate)
            }
        }
    }
}
