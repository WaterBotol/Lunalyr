import XCTest
@testable import VersoCustom

/// Pinyin read in context, not one character at a time.
final class MandarinReadingTests: XCTestCase {
    private func read(_ text: String) -> String {
        LyricRomanizer.romanize(text, language: .mandarin, toneMarks: true)
    }

    func testACharacterTakesItsWordsReading() {
        XCTAssertEqual(read("躺在地上"), "tǎng zài dì shàng", "not de shàng")
        XCTAssertEqual(read("银行"), "yín háng")
        XCTAssertEqual(read("我了解"), "wǒ liǎo jiě")
        XCTAssertEqual(read("长大了"), "zhǎng dà le")
    }

    func testOneAndNotChangeTone() {
        XCTAssertEqual(read("一起"), "yì qǐ")
        XCTAssertEqual(read("一个人"), "yí gè rén")
        XCTAssertEqual(read("不是"), "bú shì")
        XCTAssertEqual(read("不要"), "bú yào")
        XCTAssertEqual(read("统一"), "tǒng yī", "last in its word, 一 keeps its tone")
        XCTAssertEqual(read("十一"), "shí yī", "a number keeps it")
    }

    func testPunctuationAndLatinStay() {
        XCTAssertEqual(read("Baby，我爱你"), "Baby, wǒ ài nǐ")
    }

    /// Found by the Windows port: a mark after a syllable got a space before it.
    func testPunctuationSitsAgainstTheSyllableBeforeIt() {
        XCTAssertEqual(read("我爱你，你爱我"), "wǒ ài nǐ, nǐ ài wǒ")
        XCTAssertEqual(read("你好，Baby"), "nǐ hǎo, Baby")
        XCTAssertEqual(read("你好！"), "nǐ hǎo!")
        XCTAssertEqual(read("你Baby"), "nǐ Baby")
        XCTAssertEqual(read("你好, 我"), "nǐ hǎo, wǒ")
        XCTAssertEqual(read("我说「你好」，好"), "wǒ shuō nǐ hǎo, hǎo")
    }

    func testAWordsReadingIsCutIntoOneSyllablePerCharacter() {
        XCTAssertEqual(MandarinReading.split("dìshàng", into: 2), ["dì", "shàng"])
        XCTAssertEqual(MandarinReading.split("xīan", into: 2), ["xī", "an"])
        XCTAssertEqual(MandarinReading.split("xiān", into: 1), ["xiān"])
        XCTAssertEqual(MandarinReading.split("zhōngguó", into: 2), ["zhōng", "guó"])
        XCTAssertNil(MandarinReading.split("dìshàng", into: 6))
    }

    func testEachTimedCharacterGetsItsSyllable() {
        let syllables = MandarinReading.syllables("躺在地上")
        XCTAssertEqual(MandarinReading.reading(of: "地", at: 2, syllables: syllables), "dì")
        XCTAssertEqual(MandarinReading.reading(of: "上", at: 3, syllables: syllables), "shàng")
    }
}
