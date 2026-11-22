import XCTest
@testable import VersoCustom

/// Rows of an LRC that share a timestamp: a translation only when the file
/// is bilingual, and only one the listener can read; otherwise a second
/// singer's words, kept on the line.
final class LRCSharedRowsTests: XCTestCase {
    private func parse(_ text: String, _ languages: [String] = ["en-AU"]) -> [LyricsFetcher.Line] {
        LyricsFetcher.parseLRC(text, trackDuration: 60, preferredLanguages: languages).filter { !$0.isInstrumental }
    }

    func testAJapaneseFileWithEnglishRowsIsTranslated() {
        let lines = parse("""
        [00:01.00]月が綺麗ですね
        [00:01.00]The moon is beautiful
        [00:05.00]走り出す夜
        [00:05.00]A night that starts running
        """)
        XCTAssertEqual(lines.map(\.text), ["月が綺麗ですね", "走り出す夜"])
        XCTAssertEqual(lines.map(\.translation), ["The moon is beautiful", "A night that starts running"])
    }

    func testAChineseTranslationIsLeftOutForAReaderWhoCantReadIt() {
        let file = """
        [00:01.00]月が綺麗ですね
        [00:01.00]月色真美
        [00:05.00]走り出す夜
        [00:05.00]奔向夜晚
        """
        XCTAssertEqual(parse(file, ["en-AU"]).map(\.translation), [nil, nil])
        XCTAssertEqual(parse(file, ["en-AU"]).map(\.text), ["月が綺麗ですね", "走り出す夜"], "nor is it joined to the lyric")
        XCTAssertEqual(parse(file, ["zh-Hans-CN"]).map(\.translation), ["月色真美", "奔向夜晚"])
    }

    func testADuetsSharedRowsStayLyricsNotATranslation() {
        let lines = parse("""
        [00:01.00]I will wait for you tonight
        [00:01.00]And I will be there when you call
        [00:05.00]Hold on to everything we know
        [00:05.00]We never let each other go
        """)
        XCTAssertEqual(lines.map(\.translation), [nil, nil])
        XCTAssertEqual(lines.first?.text, "I will wait for you tonight / And I will be there when you call")
    }

    func testTheSameScriptInAnotherLanguageIsATranslation() {
        let lines = parse("""
        [00:01.00]Te espero esta noche bajo la luna
        [00:01.00]I wait for you tonight under the moon
        [00:05.00]Y cada palabra que me dices se queda conmigo
        [00:05.00]And every word you tell me stays with me
        """)
        XCTAssertEqual(lines.map(\.translation), ["I wait for you tonight under the moon",
                                                  "And every word you tell me stays with me"])
    }

    func testAReadingIsStillARomanisation() {
        let lines = parse("""
        [00:01.00]君の名は
        [00:01.00]kimi no na wa
        [00:01.00]Your name
        """)
        XCTAssertEqual(lines.first?.romanization, "kimi no na wa")
        XCTAssertEqual(lines.first?.translation, "Your name")
    }

    func testTheDominantScriptTreatsKanjiAndKanaAsOne() {
        XCTAssertEqual(TranslationLanguages.dominantScript(of: "月が綺麗ですね 漢字漢字漢字"), .kana)
        XCTAssertEqual(TranslationLanguages.dominantScript(of: "月色真美"), .han)
        XCTAssertEqual(TranslationLanguages.dominantScript(of: "Baby 我爱你 我爱你"), .han)
        XCTAssertNil(TranslationLanguages.dominantScript(of: "123 ♪"))
    }
}
