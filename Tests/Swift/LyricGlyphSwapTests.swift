import CoreText
import XCTest
@testable import VersoCustom

/// Accented letters a lyric face lacks are written as its plain letters,
/// so a pixel face with only A–Z and a–z (Minecrafter) draws "Supré" in
/// its own letters instead of borrowing a thin system "é".
final class LyricGlyphSwapTests: XCTestCase {
    /// A face with the plain Latin letters and digits, nothing else.
    private let plain = LyricGlyphSwap(covered: CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 "))

    func testAnAccentedLetterTheFaceLacksIsWrittenPlainly() {
        XCTAssertEqual(plain.text("Supré"), "Supre")
        XCTAssertEqual(plain.text("Ich weiß, Mädchen"), "Ich weiss, Madchen", "the comma is not a letter: it keeps its fallback")
        XCTAssertEqual(plain.text("Tōkyō Ōsaka"), "Tokyo Osaka")
        XCTAssertEqual(plain.text("Œuvre Ærø"), "OEuvre AEro")
        XCTAssertEqual(plain.text("Señor Ñandú"), "Senor Nandu")
    }

    func testOnlyLettersWithAPlainFormTheFaceHasAreSwapped() {
        XCTAssertEqual(plain.text("東京 夜に駆ける"), "東京 夜に駆ける", "no plain form: the fallback stays")
        XCTAssertEqual(plain.text("사랑해"), "사랑해")
        XCTAssertEqual(plain.text("don’t stop"), "don’t stop", "punctuation is left to its fallback")
        let accents = LyricGlyphSwap(covered: CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzé "))
        XCTAssertEqual(accents.text("café crème"), "café creme", "a letter the face has is kept")
    }

    func testAWordsTimingIsKept() {
        let line = LyricsFetcher.Line(start: 1, end: 3, text: "bought at Supré",
                                      words: [.init(text: "Supré", start: 2, end: 2.5)], isInstrumental: false,
                                      wordTimed: true, translation: "achete")
        let swapped = plain.line(line)
        XCTAssertEqual(swapped.text, "bought at Supre")
        XCTAssertEqual(swapped.words, [.init(text: "Supre", start: 2, end: 2.5)])
        XCTAssertEqual(swapped.start, 1)
        XCTAssertEqual(swapped.end, 3)
    }

    /// Only a face without accents is swapped: each lyric face has them
    /// (Charter misses a rare one or two, which keep their fallback).
    func testAFaceWithAccentsNeedsNoSwap() {
        for names in [["Charter-Roman", "Charter-Black"], ["AvenirNextCondensed-Medium", "AvenirNextCondensed-Heavy"],
                      ["Rockwell-Regular", "Rockwell-Bold"]] {
            XCTAssertNil(LyricGlyphSwap.make(faces: names.map { CTFontCreateWithName($0 as CFString, 40, nil) }), "\(names)")
        }
    }
}
