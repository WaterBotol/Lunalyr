import XCTest
@testable import VersoCustom

/// The desktop player's ink levels against its card (docs/DESIGN.md,
/// "Desktop player"): checked over covers of every hue, on the brightest
/// wallpaper the frosted material could show through (white).
final class PlayerTokensTests: XCTestCase {
    private typealias P = LunalyrDesign.Player
    private typealias RGB = ArtworkPalette.RGB

    /// Every hue at a few strengths and brightnesses, plus pale and white-ish covers.
    private var palettes: [ArtworkPalette] {
        var result: [ArtworkPalette] = []
        for step in 0..<24 {
            let hue = Double(step) / 24
            for (saturation, brightness) in [(0.9, 0.95), (0.5, 0.9), (0.35, 1.0), (0.8, 0.5)] {
                let pixel = RGB.hsb(hue, saturation, brightness)
                result.append(ArtworkPalette.of(pixels: Array(repeating: pixel, count: 100)))
            }
        }
        result.append(.neutral)
        return result
    }

    /// The card's colour over a white wallpaper: material (white, worst
    /// case), the veil, then the album tint at its opacity.
    private func card(_ palette: ArtworkPalette) -> RGB {
        let veiled = RGB(1, 1, 1).mixed(with: RGB(0, 0, 0), P.Fill.veil)
        return veiled.mixed(with: palette.cardTint, P.Fill.tint)
    }

    private func contrast(ink: P.Ink, on ground: RGB, increaseContrast: Bool = false) -> Double {
        let text = ground.mixed(with: RGB(1, 1, 1), ink.opacity(increaseContrast: increaseContrast))
        return (text.luminance + 0.05) / (ground.luminance + 0.05)
    }

    func testSmallTypeReadsWhereItSits() {
        for palette in palettes {
            let ground = card(palette)
            XCTAssertGreaterThanOrEqual(contrast(ink: .secondary, on: ground), 4.5, "\(palette)")
            XCTAssertGreaterThanOrEqual(contrast(ink: .tertiary, on: ground), 4.5, "\(palette)")
            XCTAssertGreaterThan(contrast(ink: .tertiary, on: ground, increaseContrast: true),
                                 contrast(ink: .tertiary, on: ground))
        }
    }

    func testLevelsStayInOrder() {
        for increase in [false, true] {
            let levels = [P.Ink.primary, .secondary, .tertiary, .quaternary].map { $0.opacity(increaseContrast: increase) }
            XCTAssertEqual(levels, levels.sorted(by: >))
        }
    }

    func testCardIsConcentricWithTheCover() {
        XCTAssertEqual(P.Radius.card, P.Radius.cover + DesktopPlayerView.padding)
    }
}
