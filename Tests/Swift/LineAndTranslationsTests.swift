import XCTest
@testable import VersoCustom

/// A translation wider than its line does not widen what is fitted to the
/// stage, so the sung words keep their size.
final class LineAndTranslationsTests: XCTestCase {
    func testTheStackIsAsWideAsTheLineNotItsTranslation() {
        let size = LineAndTranslations.size(of: [CGSize(width: 400, height: 80), CGSize(width: 900, height: 40)],
                                            spills: [false, true], spacing: 10)
        XCTAssertEqual(size.width, 400)
        XCTAssertEqual(size.height, 130, "still tall enough for both")
    }

    func testAWideLineKeepsItsOwnWidth() {
        let size = LineAndTranslations.size(of: [CGSize(width: 40, height: 20), CGSize(width: 1000, height: 80),
                                                 CGSize(width: 600, height: 20)],
                                            spills: [true, false, true], spacing: 0)
        XCTAssertEqual(size.width, 1000)
        XCTAssertEqual(size.height, 120)
    }

    func testOnlyTranslationsStillHaveAWidth() {
        XCTAssertEqual(LineAndTranslations.size(of: [CGSize(width: 300, height: 20)], spills: [true], spacing: 4).width, 300)
        XCTAssertEqual(LineAndTranslations.size(of: [], spills: [], spacing: 4), .zero)
    }
}

/// Translations lean, readings and CJK translations do not.
final class TranslationSlantTests: XCTestCase {
    func testLatinCyrillicAndGreekTranslationsLean() {
        XCTAssertTrue(TranslationSlant.leans("The moon is beautiful"))
        XCTAssertTrue(TranslationSlant.leans("Луна прекрасна"))
        XCTAssertTrue(TranslationSlant.leans("Το φεγγάρι"))
    }

    func testChineseJapaneseAndKoreanStayUpright() {
        XCTAssertFalse(TranslationSlant.leans("月色真美"))
        XCTAssertFalse(TranslationSlant.leans("月が綺麗ですね"))
        XCTAssertFalse(TranslationSlant.leans("달이 예쁘네요"))
        XCTAssertFalse(TranslationSlant.leans(""), "a reading passes no text and stays upright")
    }
}

/// Echo's repeats step back on the side a translation sits at.
final class EchoUnderTranslationTests: XCTestCase {
    func testRepeatsOnTheTranslationsSideAreQuieted() {
        XCTAssertEqual(EchoBackdrop.strength(flipped: true, translationEdge: .bottom), EchoBackdrop.quietUnderTranslation)
        XCTAssertEqual(EchoBackdrop.strength(flipped: false, translationEdge: .bottom), 1)
        XCTAssertEqual(EchoBackdrop.strength(flipped: false, translationEdge: .top), EchoBackdrop.quietUnderTranslation)
        XCTAssertEqual(EchoBackdrop.strength(flipped: true, translationEdge: .top), 1)
        XCTAssertEqual(EchoBackdrop.strength(flipped: true, translationEdge: nil), 1)
    }
}
