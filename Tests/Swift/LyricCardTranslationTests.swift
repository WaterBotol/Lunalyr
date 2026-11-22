import XCTest
@testable import VersoCustom

/// Cards carry the line's translation, as the wallpaper shows it.
@MainActor
final class LyricCardTranslationTests: XCTestCase {
    func testOnlyARealTranslationIsKept() {
        XCTAssertEqual(LyricCard.translation("  The moon is beautiful \n"), "The moon is beautiful")
        XCTAssertNil(LyricCard.translation("   "))
        XCTAssertNil(LyricCard.translation(nil))
    }

    func testATranslationIsSetSmallerThanItsLineButStaysReadable() {
        XCTAssertEqual(LyricCard.Page.translationSize(200), 60)
        XCTAssertEqual(LyricCard.Page.translationSize(40), 30, "never below 30 pt on a 1080 px card")
    }

    func testAPageHasNoTranslationUnlessGivenOne() {
        XCTAssertNil(LyricCard.Page(words: ["hello"]).translation)
        XCTAssertEqual(LyricCard.Page(words: ["月"], translation: "Moon").translation, "Moon")
    }
}
