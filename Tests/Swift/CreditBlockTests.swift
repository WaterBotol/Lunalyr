import XCTest
@testable import VersoCustom

/// Instrument and role credits in a song's opening or closing credit block,
/// and a bare title or artist heading the lyrics, are not sung lines. Seen
/// in a lookup of "晴天 — 周杰伦": Kugou and QQ open with "吉他：蔡科俊Again",
/// Lyrics+ with "周杰伦".
final class CreditBlockTests: XCTestCase {
    private func line(_ text: String, _ start: Double) -> LyricsFetcher.Line {
        LyricsFetcher.Line(start: start, end: start + 2, text: text, words: [], isInstrumental: false)
    }
    private func sung(_ lines: [LyricsFetcher.Line], title: String = "晴天", artist: String = "周杰伦") -> [String] {
        LyricCredits.flagging(lines, title: title, artist: artist).filter { !$0.isInstrumental }.map(\.text)
    }

    func testInstrumentAndRoleCredits() {
        let credits = [
            "吉他：蔡科俊Again", "吉他: 蔡科俊Again", "贝斯：张三", "贝斯手：张三", "鼓：李四", "键盘：王五", "钢琴：周杰伦",
            "弦乐：国际首席爱乐乐团", "和声：周杰伦", "编曲：钟兴民", "制作人：周杰伦", "监制：周杰伦", "混音：杨瑞代",
            "母带：某人", "录音：杨瑞代", "录音室：阿尔发录音室", "混音室：白金录音室", "人声：周杰伦", "OP：杰威尔音乐有限公司",
            "SP：索尼音乐", "出品：杰威尔音乐", "发行：索尼音乐", "企划：某人", "统筹：某人", "和音：某人", "合声：某人",
            "演唱：周杰伦", "原唱：周杰伦", "词：方文山", "曲：周杰伦", "词/曲：周杰伦",
            "貝斯：張三", "鍵盤：王五", "編曲：鍾興民", "製作人：周杰倫", "監製：周杰倫", "錄音：楊瑞代", "混音師：某人",
            "作詞：秋元康", "作曲：秋元康", "編曲：秋元康", "ギター：西田修大", "ベース：山口寛雄", "ドラム：玉田豊夢",
            "작사: 김이나", "작곡: 이민수", "편곡: 이민수", "기타: 홍준호",
            "Guitar: Steve Lukather", "Bass: Pino Palladino", "Drums: Steve Gadd", "Keys: Greg Phillinganes",
            "Mixed by: Serban Ghenea", "Mastered by: Bob Ludwig", "Recorded by: Sam Holland", "Vocal by: Someone Else",
            "Mixed by Serban Ghenea", "Mastered by Bob Ludwig",
        ]
        for text in credits { XCTAssertTrue(LyricCredits.isCredit(text), text) }
        // Sung lines with a colon in them stay lyrics.
        for text in ["爱情：不是这样", "Note: I love you", "鼓：咚咚", "吉他：弹给你听", "ギター：かき鳴らして", "Guitar: play it loud"] {
            XCTAssertFalse(LyricCredits.isCredit(text), text)
        }
    }

    func testKugouAndQQOpenWithAGuitarCredit() {
        let lrc = """
        [00:00.00]晴天 - 周杰伦
        [00:01.00]词：方文山
        [00:02.00]曲：周杰伦
        [00:03.00]编曲：钟兴民
        [00:04.00]吉他：蔡科俊Again
        [00:05.00]和声：周杰伦
        [00:29.00]故事的小黄花
        [00:32.00]从出生那年就飘着
        """
        let lines = LyricsFetcher.parseLRC(lrc, trackDuration: 270)
        XCTAssertEqual(lines.filter { $0.credit == true }.count, 6)
        XCTAssertEqual(lines.filter { !$0.isInstrumental }.map(\.text), ["故事的小黄花", "从出生那年就飘着"])

        // The guitar credit as the very first line, with nothing before it.
        let bare = LyricsFetcher.parseLRC("[00:00.50]吉他：蔡科俊Again\n[00:29.00]故事的小黄花\n[00:32.00]从出生那年就飘着",
                                          trackDuration: 270)
        XCTAssertEqual(bare.filter { !$0.isInstrumental }.map(\.text), ["故事的小黄花", "从出生那年就飘着"])
        XCTAssertEqual(sung([line("吉他：蔡科俊Again", 0.5), line("故事的小黄花", 29)]), ["故事的小黄花"])
    }

    func testNetEaseStyleCreditBlock() {
        let lrc = """
        [00:00.000] 作词 : 方文山
        [00:01.000] 作曲 : 周杰伦
        [00:02.000] 编曲 : 钟兴民
        [00:03.000] 制作人 : 周杰伦
        [00:04.000] 吉他 : 蔡科俊Again
        [00:05.000] 贝斯 : 陈任佑
        [00:06.000] 鼓 : 陈柏州
        [00:07.000] 和声 : 周杰伦
        [00:08.000] 录音 : 杨瑞代
        [00:09.000] 混音 : 杨大纬
        [00:29.000]故事的小黄花
        [00:32.000]从出生那年就飘着
        """
        let lines = LyricsFetcher.parseLRC(lrc, trackDuration: 270)
        XCTAssertEqual(lines.filter { $0.credit == true }.count, 10)
        XCTAssertEqual(lines.filter { !$0.isInstrumental }.map(\.text), ["故事的小黄花", "从出生那年就飘着"])
    }

    func testClosingCreditBlock() {
        let lines = [line("故事的小黄花", 29), line("从出生那年就飘着", 32), line("吉他：蔡科俊Again", 260),
                     line("和声：周杰伦", 261), line("OP：杰威尔音乐有限公司", 262)]
        XCTAssertEqual(sung(lines), ["故事的小黄花", "从出生那年就飘着"])
        // An everyday label that is sung, closing the song, stays.
        XCTAssertEqual(sung([line("故事的小黄花", 29), line("钢琴：叮咚叮咚", 32)]), ["故事的小黄花", "钢琴：叮咚叮咚"])
    }

    func testSungLinesWithAColonMidSongStay() {
        let lines = [line("词：方文山", 0), line("故事的小黄花", 29), line("爱情：不是这样", 32), line("从出生那年就飘着", 35)]
        XCTAssertEqual(sung(lines), ["故事的小黄花", "爱情：不是这样", "从出生那年就飘着"])
        let english = [line("Produced by: Max Martin", 0), line("Hold on", 10), line("Note: I love you", 14), line("Let go", 18)]
        XCTAssertEqual(sung(english, title: "Song", artist: "Singer"), ["Hold on", "Note: I love you", "Let go"])
        // An everyday credit is read as a lyric away from the credits.
        XCTAssertEqual(sung([line("Hold on", 10), line("吉他：蔡科俊Again", 14), line("Let go", 18)]),
                       ["Hold on", "吉他：蔡科俊Again", "Let go"])
    }

    func testLyricsPlusOpensWithTheArtistAlone() {
        let lines = [line("周杰伦", 0), line("故事的小黄花", 29), line("从出生那年就飘着", 32)]
        XCTAssertEqual(sung(lines), ["故事的小黄花", "从出生那年就飘着"])
        // Traditional script, either way round.
        XCTAssertEqual(sung([line("周杰倫", 0), line("故事的小黄花", 29)]), ["故事的小黄花"])
        XCTAssertEqual(sung([line("周杰伦", 0), line("故事的小黄花", 29)], title: "晴天", artist: "周杰倫"), ["故事的小黄花"])
        // The title alone, and title and artist together, either way round.
        for heading in ["晴天", "晴天 - 周杰伦", "周杰伦 - 晴天", "晴天 – 周杰倫"] {
            XCTAssertEqual(sung([line(heading, 0), line("故事的小黄花", 29)]), ["故事的小黄花"], heading)
        }
        // Artist, then title, both heading the lyrics.
        XCTAssertEqual(sung([line("周杰伦", 0), line("晴天", 1), line("故事的小黄花", 29)]), ["故事的小黄花"])
    }

    func testANameIsOnlyAHeaderAtTheStart() {
        // Later in the song, the artist's or title's name is sung.
        let lines = [line("故事的小黄花", 29), line("周杰伦", 32), line("晴天", 35)]
        XCTAssertEqual(sung(lines), ["故事的小黄花", "周杰伦", "晴天"])
        // A title sung alone at the start that comes back later is sung.
        let hook = [line("Hello", 0), line("It's me", 4), line("Hello", 30)]
        XCTAssertEqual(sung(hook, title: "Hello", artist: "Adele"), ["Hello", "It's me", "Hello"])
        // A line that only starts with the title is sung.
        XCTAssertEqual(sung([line("晴天的时候", 0), line("故事的小黄花", 29)]), ["晴天的时候", "故事的小黄花"])
    }

    func testPlainLyricsDropTheHeader() {
        XCTAssertEqual(LyricCredits.droppingLeadingCredits(["周杰伦", "", "故事的小黄花", "从出生那年就飘着"], title: "晴天", artist: "周杰伦"),
                       ["故事的小黄花", "从出生那年就飘着"])
        XCTAssertEqual(LyricCredits.droppingLeadingCredits(["晴天 - 周杰伦", "词：方文山", "吉他：蔡科俊Again", "故事的小黄花"],
                                                           title: "晴天", artist: "周杰伦"), ["故事的小黄花"])
        XCTAssertEqual(LyricCredits.droppingLeadingCredits(["故事的小黄花", "周杰伦"], title: "晴天", artist: "周杰伦"),
                       ["故事的小黄花", "周杰伦"])
    }

    /// Agreement compares sung lines only: a source that left its credits
    /// and heading in as lyrics (Lyrics+) still agrees with one that set
    /// them apart.
    func testAgreementLeavesOutCreditsAndHeadings() {
        let verses = ["故事的小黄花", "从出生那年就飘着", "童年的荡秋千", "随记忆一直晃到现在"]
        func candidate(_ source: LyricSourceID, _ texts: [String]) -> LyricCandidate {
            let lines = texts.enumerated().map { LyricsFetcher.makeLine(text: $1, start: Double($0 * 5), end: Double($0 * 5 + 4)) }
            return .init(source: source, result: .synced(lines, .init(precision: "fixture", source: source.title)),
                         title: "晴天", artist: "周杰伦", duration: 270, score: 0)
        }
        let kpoe = candidate(.lyricsPlus, ["周杰伦", "吉他：蔡科俊Again"] + verses)
        let netEase = candidate(.netEase, verses)
        XCTAssertEqual(LyricsFetcher.agreementText(kpoe), verses)
        XCTAssertEqual(LyricsFetcher.agreementTiming(kpoe).map(\.text), verses)
        XCTAssertTrue(LyricsFetcher.sameWords(LyricsFetcher.agreementText(kpoe), LyricsFetcher.agreementText(netEase)))
        let plain = LyricCandidate(source: .lrclib, result: .plain(["周杰伦", "词：方文山"] + verses, .init(precision: "Plain", source: "x")),
                                   title: "晴天", artist: "周杰伦", duration: 270, score: 0)
        XCTAssertEqual(LyricsFetcher.agreementText(plain), verses)
    }
}
