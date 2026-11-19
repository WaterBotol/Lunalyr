import CoreText
import XCTest
@testable import VersoCustom

/// Highlight tiles reach past a word's line box as far as its letters do:
/// accented capitals, kana and kanji under a face with a short box stuck
/// out above the tile (the owner, 19 Nov 2026, with Minecrafter).
final class LyricInkReachTests: XCTestCase {
    private let face = CTFontCreateWithName("Helvetica" as CFString, 100, nil)

    func testPlainLowercaseFitsTheBox() {
        let reach = LyricInkReach.of("ace", face: face)
        XCTAssertEqual(reach.top, 0)
        XCTAssertEqual(reach.bottom, 0)
    }

    func testAccentedCapitalsStandAboveTheBox() {
        // Helvetica's ascent is 0.77 em; "À" reaches about 0.92 em.
        XCTAssertGreaterThan(LyricInkReach.of("ÀÉÎ", face: face).top, 8)
    }

    func testFallbackGlyphsAreMeasured() {
        // Kana and kanji come from a fallback face; they are measured as drawn.
        let ascent = CTFontGetAscent(face)
        let reach = LyricInkReach.of("君の名前", face: face)
        XCTAssertGreaterThanOrEqual(reach.top, 0)
        XCTAssertEqual(LyricInkReach.of("君の名前", face: face), reach, "remembered")
        _ = ascent
    }

    func testTheTileGrowsByTheReach() {
        let mark = LyricHighlightMark(from: 0, to: 1, pose: LyricHighlightTiming.Pose(opacity: 1, width: 1))
        let size = CGSize(width: 300, height: 100)
        let plain = mark.rect(in: size, pointSize: 100, rightToLeft: false)
        let reach = LyricInkReach(top: 15, bottom: 4)
        let tall = mark.rect(in: size, pointSize: 100, rightToLeft: false, ink: reach)
        XCTAssertEqual(tall.minY, plain.minY - 15, accuracy: 1e-9)
        XCTAssertEqual(tall.maxY, plain.maxY + 4, accuracy: 1e-9)
        XCTAssertEqual(tall.width, plain.width)
        // The mask that colours the text on the tile reaches as far.
        XCTAssertEqual(mark.pressed(in: size, pointSize: 100, rightToLeft: false, ink: reach).minY, tall.minY,
                       accuracy: 1e-9)
    }

    func testRollingTilesReachAsFar() {
        var tile = RollingUnit.Tile(width: 1, opacity: 1, reach: CGSize(width: 9, height: 3))
        tile.ink = LyricInkReach(top: 12, bottom: 2)
        XCTAssertEqual(tile.top, 15)
        XCTAssertEqual(tile.bottom, 5)
    }
}
