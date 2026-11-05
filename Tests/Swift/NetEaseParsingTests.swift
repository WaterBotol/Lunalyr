import XCTest
@testable import VersoCustom

/// Fixtures follow NetEase's live `song/lyric` payloads: zero-length credit
/// headers, timed copyright rows and per-character Han timing in yrc, and a
/// `romalrc` that shares the LRC timestamps.
final class NetEaseParsingTests: XCTestCase {
    func testCreditHeadersAndCopyrightRowsAreNotLyrics() {
        let yrc = """
        {"t":0,"c":[{"tx":"作词: "},{"tx":"唐恬"}]}
        [0,0](0,0,0) 作词 : 唐恬
        [0,0](0,0,0) 吉他 : 高飞
        [0,0](0,0,0) 人声录音室 : 雅旺录音室
        [1,4890](1,270,0)词(270,270,0)版(540,270,0)权(810,270,0)管(1080,270,0)理(1350,270,0)方(1620,270,0)：(1890,270,0)北(2160,270,0)京
        [11140,3260](11140,270,0)录(11410,270,0)音(11680,270,0)作(11950,270,0)品(12220,270,0)及(12490,270,0)MV(12760,270,0)版(13030,270,0)权(13300,270,0)：(13570,270,0)EAS (13840,270,0)MUSIC
        [19560,2750](19560,270,0)出(19830,270,0)品(20100,270,0)监(20370,270,0)制(20640,270,0)：(20910,270,0)霍(21180,270,0)锦
        [22310,4300](22310,2880,0)都 (25190,310,0)是(25500,290,0)勇(25790,230,0)敢(26020,590,0)的
        """
        let lines = NetEaseLyricsProvider.parseYRC(yrc)
        XCTAssertEqual(lines.map(\.text), ["都 是勇敢的"])
        XCTAssertEqual(lines.first?.words.map(\.text), ["都", "是", "勇", "敢", "的"])
        XCTAssertEqual(lines.first?.start ?? 0, 22.31, accuracy: 0.001)
        XCTAssertEqual(lines.first?.end ?? 0, 26.61, accuracy: 0.001)
        XCTAssertTrue(lines.allSatisfy(LyricsFetcher.validWordTiming))
    }

    func testLyricsThatMentionInstrumentsOrProductionAreKept() {
        let yrc = """
        [30000,3000](30000,300,0)我 (30300,300,0)鼓(30600,300,0)起(30900,300,0)勇(31200,300,0)气 (31500,300,0)说(31800,300,0)爱(32100,300,0)你
        [34000,2000](34000,300,0)弹(34300,300,0)着 (34600,300,0)吉(34900,300,0)他 (35200,300,0)唱(35500,300,0)歌
        [37000,2000](37000,300,0)你(37300,300,0)的 (37600,300,0)鼓(37900,300,0)励 (38200,300,0)是(38500,300,0)光
        [40000,2000](40000,400,0)高(40400,400,0)鳴(40800,300,0)る(41100,300,0)鼓(41400,300,0)動(41700,300,0)が
        [43000,2000](43000,500,0)和(43500,500,0)声(44000,500,0)制(44500,500,0)作
        """
        let lines = NetEaseLyricsProvider.parseYRC(yrc)
        XCTAssertEqual(lines.map(\.text), ["我 鼓起勇气 说爱你", "弹着 吉他 唱歌", "你的 鼓励 是光", "高鳴る鼓動が", "和声制作"])
    }

    func testTimedCreditRowsWithQualifiersAreDropped() {
        // Timed credit rows ahead of the lyric: an everyday label ("鼓") counts
        // here because it sits among other credits.
        let yrc = """
        [0,0](0,0,0) 作词 : 唐恬
        [1000,900](1000,300,0)电(1300,300,0)吉(1600,300,0)他 (1900,0,0): (1900,0,0)张三
        [2000,900](2000,300,0)鼓 (2300,0,0): (2300,0,0)李四
        [3000,900](3000,300,0)母(3300,300,0)带(3600,300,0)处(3900,0,0)理 (3900,0,0): (3900,0,0)王五
        [22310,4300](22310,2880,0)都 (25190,310,0)是(25500,290,0)勇(25790,230,0)敢(26020,590,0)的
        [27000,2000](27000,500,0)鼓(27500,0,0)：(27500,500,0)咚(28000,500,0)咚
        """
        let lines = NetEaseLyricsProvider.parseYRC(yrc)
        XCTAssertEqual(lines.map(\.text), ["都 是勇敢的", "鼓：咚咚"])
    }

    func testBracketsInsideTheLyricAreKept() {
        let yrc = """
        [1000,3000](1000,300,0)Oh (1300,600,0)(yeah) (1900,800,0)baby
        [5000,2000](5000,300,0)Oh (5300,100,0)((5400,300,0)yeah(5700,100,0)) (5800,600,0)baby
        """
        let lines = NetEaseLyricsProvider.parseYRC(yrc)
        XCTAssertEqual(lines.map(\.text), ["Oh (yeah) baby", "Oh (yeah) baby"])
        XCTAssertEqual(lines[0].words.map(\.text), ["Oh", "(yeah)", "baby"])
        // A bracket timed on its own opens the next word instead of trailing the previous one.
        XCTAssertEqual(lines[1].words.map(\.text), ["Oh", "(yeah)", "baby"])
        XCTAssertEqual(lines[1].words[1].start, 5.4, accuracy: 0.001)
        XCTAssertTrue(lines.allSatisfy(LyricsFetcher.validWordTiming))
        lines.forEach { assertWordsReadAsTheLine($0) }
    }

    func testLatinSyllablesJoinAndPunctuationFolds() {
        let yrc = """
        [8000,3000](8000,300,0)beau(8300,200,0)ti(8500,400,0)ful (8900,500,0)day(9400,0,0)!
        [12000,2000](12000,300,0)Don(12300,200,0)'t (12500,600,0)stop
        """
        let lines = NetEaseLyricsProvider.parseYRC(yrc)
        XCTAssertEqual(lines.map(\.text), ["beautiful day!", "Don't stop"])
        XCTAssertEqual(lines[0].words.map(\.text), ["beautiful", "day!"])
        XCTAssertEqual(lines[0].words[0].start, 8, accuracy: 0.001)
        XCTAssertEqual(lines[0].words[0].end, 8.9, accuracy: 0.001)
        XCTAssertEqual(lines[1].words.map(\.text), ["Don't", "stop"])
        lines.forEach { assertWordsReadAsTheLine($0) }
    }

    func testPunctuationJoinsTheWordItTouches() {
        let yrc = """
        [1000,3000](1000,300,0)she (1300,300,0)said (1600,100,0)"(1700,300,0)Hello(2000,100,0)"
        [5000,3000](5000,300,0)Oh (5300,100,0)-(5400,300,0)yeah
        [9000,3000](9000,300,0)Oh(9300,100,0)-(9400,300,0)yeah
        [13000,3000](13000,500,0)Je (13500,500,0)t'aime (14000,4000,0)!
        [17000,2000](17000,300,0)爱(17300,100,0)，(17400,300,0)你(17700,300,0)（(18000,300,0)我(18300,100,0)）
        """
        let lines = NetEaseLyricsProvider.parseYRC(yrc)
        XCTAssertEqual(lines.map(\.text), ["she said \"Hello\"", "Oh -yeah", "Oh-yeah", "Je t'aime !", "爱，你（我）"])
        // A quote after a space opens the next word instead of gluing two words.
        XCTAssertEqual(lines[0].words.map(\.text), ["she", "said", "\"Hello\""])
        XCTAssertEqual(lines[0].words[2].start, 1.7, accuracy: 0.001)
        XCTAssertEqual(lines[1].words.map(\.text), ["Oh", "-yeah"])
        XCTAssertEqual(lines[2].words.map(\.text), ["Oh-yeah"])
        // A mark set apart by spaces keeps a token of its own, within the line.
        XCTAssertEqual(lines[3].words.map(\.text), ["Je", "t'aime", "!"])
        XCTAssertEqual(lines[3].words[2].end, 16, accuracy: 0.001)
        XCTAssertEqual(lines[4].words.map(\.text), ["爱，", "你", "（我）"])
        XCTAssertTrue(lines.allSatisfy(LyricsFetcher.validWordTiming))
        lines.forEach { assertWordsReadAsTheLine($0) }
    }

    /// The rendering contract: the words, in order, are the line's own text
    /// with only whitespace between them.
    private func assertWordsReadAsTheLine(_ line: LyricsFetcher.Line, file: StaticString = #filePath, lineNumber: UInt = #line) {
        var rest = Substring(line.text)
        for word in line.words {
            guard let range = rest.range(of: word.text), rest[..<range.lowerBound].allSatisfy(\.isWhitespace) else {
                return XCTFail("\(word.text) does not follow in \(line.text)", file: file, line: lineNumber)
            }
            rest = rest[range.upperBound...]
        }
        XCTAssertTrue(rest.allSatisfy(\.isWhitespace), line.text, file: file, line: lineNumber)
    }

    func testZeroLengthRowWithSungWordsEndsWithItsWords() {
        let lines = NetEaseLyricsProvider.parseYRC("[50000,0](50000,300,0)Hello (50300,400,0)there\r\n[52000,1000](52000,500,0)♪")
        XCTAssertEqual(lines.count, 2)
        XCTAssertEqual(lines[0].text, "Hello there")
        XCTAssertEqual(lines[0].end, 50.7, accuracy: 0.001)
        XCTAssertTrue(lines[1].isInstrumental)
    }

    func testCreditFilterAppliesToTheLRCFallback() {
        let lrc = """
        [00:00.000] 制作人 : Steve Mac
        [00:01.000] 作词 : Ed Sheeran/Johnny McDaid
        [00:02.000] 作曲 : Ed Sheeran/Johnny McDaid
        [00:09.480]The club isn't the best place to find a lover
        [00:11.750]So the bar is where I go
        """
        let lines = LyricsFetcher.parseLRC(lrc, trackDuration: 233)
        XCTAssertEqual(lines.map(\.text), ["The club isn't the best place to find a lover", "So the bar is where I go"])
    }

    func testRomanisedLinesAttachToTheLinesTheyRead() {
        let yrc = """
        [273,3010](273,300,0)無(573,300,0)敵(873,300,0)の(1173,300,0)笑(1473,300,0)顔
        [3330,2585](3330,300,0)知(3630,300,0)り(3930,300,0)た(4230,300,0)い
        [14520,1400](14520,300,0)You're (14820,300,0)my (15120,600,0)savior
        [17215,1400](17215,300,0)今(17515,300,0)日
        """
        let romalrc = """
        [00:00.273]mu te ki no e ga o
        [00:03.283]shi ri ta i
        [00:14.474]
        [00:16.400]kyo u
        """
        let lines = NetEaseLyricsProvider.attachingRomanization(romalrc, to: NetEaseLyricsProvider.parseYRC(yrc), duration: 200)
        XCTAssertEqual(lines.map(\.romanization), ["mu te ki no e ga o", "shi ri ta i", nil, nil])
        XCTAssertEqual(NetEaseLyricsProvider.attachingRomanization(nil, to: lines, duration: 200).map(\.text), lines.map(\.text))
    }
}
