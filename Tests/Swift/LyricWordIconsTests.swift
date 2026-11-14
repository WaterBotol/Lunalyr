import AppKit
import XCTest
@testable import VersoCustom

final class LyricWordIconsTests: XCTestCase {
    func testInflectionsCaseAndPunctuation() {
        for word in ["love", "Love", "LOVES", "loved", "loving", "lovin'", "lover", "love,", "(love)"] {
            XCTAssertEqual(LyricWordIcons.symbol(for: word), "heart.fill", word)
        }
        for word in ["home", "homes", "House!"] { XCTAssertEqual(LyricWordIcons.symbol(for: word), "house.fill", word) }
        for word in ["call", "calling", "called", "calls"] { XCTAssertEqual(LyricWordIcons.symbol(for: word), "phone.fill", word) }
        XCTAssertEqual(LyricWordIcons.symbol(for: "nights"), "moon.fill")
        XCTAssertEqual(LyricWordIcons.symbol(for: "running"), "figure.run")
        XCTAssertEqual(LyricWordIcons.symbol(for: "cried"), "drop.fill")
        XCTAssertEqual(LyricWordIcons.symbol(for: "dancing"), "figure.dance")
        XCTAssertEqual(LyricWordIcons.symbol(for: "her"), "figure.stand.dress")
        XCTAssertEqual(LyricWordIcons.symbol(for: "his"), "figure.stand")
    }

    func testCJK() {
        XCTAssertEqual(LyricWordIcons.symbol(for: "\u{611B}"), "heart.fill")
        XCTAssertEqual(LyricWordIcons.symbol(for: "\u{C0AC}\u{B791}"), "heart.fill")
        XCTAssertEqual(LyricWordIcons.symbol(for: "\u{5BB6}"), "house.fill")
        XCTAssertEqual(LyricWordIcons.symbol(for: "\u{C9D1}"), "house.fill")
        XCTAssertEqual(LyricWordIcons.symbol(for: "\u{591C}"), "moon.fill")
        XCTAssertEqual(LyricWordIcons.symbol(for: "\u{BC24}"), "moon.fill")
        XCTAssertEqual(LyricWordIcons.symbol(for: "\u{96E8}"), "cloud.rain.fill")
        XCTAssertEqual(LyricWordIcons.symbol(for: "\u{BE44}"), "cloud.rain.fill")
        XCTAssertEqual(LyricWordIcons.symbol(for: "\u{591C}\u{7A7A}\u{306B}"), "moon.fill", "夜空に: the word with its particle")
        XCTAssertEqual(LyricWordIcons.symbol(for: "\u{BC24}\u{C5D0}"), "moon.fill", "밤에")
        XCTAssertNil(LyricWordIcons.symbol(for: "\u{306B}"), "a particle alone")
    }

    func testUnmatchedWordsHaveNone() {
        for word in ["you", "me", "the", "and", "seed", "shed", "willow", "", "...", "beautiful", "whatever"] {
            XCTAssertNil(LyricWordIcons.symbol(for: word), word)
        }
    }

    func testEverySymbolResolves() {
        let names = Set(Array(LyricWordIcons.strong.values) + Array(LyricWordIcons.weak.values)
                        + StageLyricView.portalOrnaments)
        XCTAssertGreaterThanOrEqual(LyricWordIcons.strong.count + LyricWordIcons.weak.count, 80)
        for name in names {
            XCTAssertNotNil(NSImage(systemSymbolName: name, accessibilityDescription: nil), name)
        }
    }
}
