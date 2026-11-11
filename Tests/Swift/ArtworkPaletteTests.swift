import AppKit
import XCTest
@testable import VersoCustom

/// The album colours behind the desktop player's card.
@MainActor
final class ArtworkPaletteTests: XCTestCase {
    private typealias RGB = ArtworkPalette.RGB

    private func solid(_ colour: RGB, count: Int = 576) -> [RGB] { Array(repeating: colour, count: count) }

    func testNoArtworkIsNeutral() {
        XCTAssertEqual(ArtworkPalette.of(nil as NSImage?), .neutral)
        XCTAssertEqual(ArtworkPalette.of(pixels: []), .neutral)
        XCTAssertFalse(ArtworkPalette.neutral.fromArtwork)
    }

    func testGreyBlackAndWhiteCoversAreNeutral() {
        for grey in [0.0, 0.5, 1.0] {
            XCTAssertEqual(ArtworkPalette.of(pixels: solid(RGB(grey, grey, grey))), .neutral, "grey \(grey)")
        }
    }

    func testAColourfulCoverKeepsItsHue() {
        let pink = ArtworkPalette.of(pixels: solid(RGB(0.96, 0.36, 0.66)))
        XCTAssertTrue(pink.fromArtwork)
        XCTAssertGreaterThan(pink.accent.red, pink.accent.green)
        XCTAssertGreaterThan(pink.accent.red, pink.accent.blue)
        XCTAssertGreaterThan(pink.deep.red, pink.deep.green, "the deep shade keeps the hue")
        let teal = ArtworkPalette.of(pixels: solid(RGB(0.1, 0.7, 0.75)))
        XCTAssertGreaterThan(teal.accent.blue, teal.accent.red)
        XCTAssertGreaterThan(teal.accent.green, teal.accent.red)
    }

    func testTheStrongestColourWinsOverAGreyGround() {
        // A mostly grey cover with a red mark: the card takes the red.
        var pixels = solid(RGB(0.5, 0.5, 0.52), count: 520)
        pixels += solid(RGB(0.9, 0.1, 0.12), count: 56)
        let palette = ArtworkPalette.of(pixels: pixels)
        XCTAssertTrue(palette.fromArtwork)
        XCTAssertGreaterThan(palette.accent.red, palette.accent.green * 1.5)
    }

    func testPaleCoversAreDarkenedForWhiteType() {
        // Near-white with a pale tint, pastel yellow (the hardest for white
        // type), and a strong colour: all pass the guard.
        for colour in [RGB(0.98, 0.95, 0.97), RGB(1, 0.97, 0.6), RGB(0.7, 1, 0.8), RGB(0.95, 0.36, 0.66), RGB(1, 0.9, 0.1)] {
            let palette = ArtworkPalette.of(pixels: solid(colour))
            XCTAssertGreaterThanOrEqual(palette.deep.contrastWithWhite, ArtworkPalette.deepContrast - 0.01, "\(colour)")
            XCTAssertGreaterThanOrEqual(palette.accent.contrastWithWhite, ArtworkPalette.accentContrast - 0.01, "\(colour)")
        }
    }

    func testGuardDarkensOnlyWhatNeedsIt() {
        let white = ArtworkPalette.guarded(RGB(1, 1, 1), contrast: 4.5)
        XCTAssertGreaterThanOrEqual(white.contrastWithWhite, 4.5)
        let navy = RGB(0.05, 0.08, 0.3)
        XCTAssertEqual(ArtworkPalette.guarded(navy, contrast: 4.5), navy, "already legible: untouched")
        // Darkening keeps the hue's proportions.
        let yellow = ArtworkPalette.guarded(RGB(1, 0.9, 0.2), contrast: 4.5)
        XCTAssertEqual(yellow.green / yellow.red, 0.9, accuracy: 0.001)
    }

    func testContrastMatchesWCAG() {
        XCTAssertEqual(RGB(0, 0, 0).contrastWithWhite, 21, accuracy: 0.01)
        XCTAssertEqual(RGB(1, 1, 1).contrastWithWhite, 1, accuracy: 0.01)
    }

    func testReadsAnImage() {
        let cover = PlayerPreviewScene.cover((.systemPink, .systemPurple), seed: 3)
        let pixels = ArtworkPalette.samples(of: cover)
        XCTAssertEqual(pixels.count, 24 * 24)
        let palette = ArtworkPalette.of(cover)
        XCTAssertTrue(palette.fromArtwork)
        XCTAssertGreaterThan(palette.accent.red, palette.accent.green, "pink into purple")
        XCTAssertEqual(ArtworkPalette.of(cover), palette, "the same cover, the same palette (cached)")
    }
}
