import XCTest
@testable import VersoCustom

/// Lyric colour modes keep a colour on every backdrop; only an
/// intentionally neutral palette stays white.
final class LyricColourStaysColourTests: XCTestCase {
    func testALiftedColourNeverFadesToWhiteOnAnyBackdrop() {
        for hue in stride(from: 0.0, to: 2 * Double.pi, by: 0.35) {
            let pastel = LyricInk.pastel(OKLab(l: 0.8, chroma: 0.12, hue: hue))
            // Up to a backdrop of 0.26 (after the dark backing) every hue
            // keeps its tint. Between about 0.28 and 0.33 the hues the
            // gamut squeezes at high lightness fade; beyond it only white
            // reaches 2.5:1.
            for step in 0...13 {
                let backdrop = Double(step) / 50
                let ink = Perceptual.readable(pastel, background: backdrop, keepingColour: true)
                XCTAssertGreaterThanOrEqual(ink.chroma, 0.04, "hue \(hue) on backdrop \(backdrop) faded to white")
                XCTAssertLessThan(abs(ink.hue - pastel.hue).truncatingRemainder(dividingBy: 2 * .pi) < .pi
                                  ? abs(ink.hue - pastel.hue) : 2 * .pi - abs(ink.hue - pastel.hue), 0.2)
            }
        }
    }

    func testTheTintCostsAtMostAFewPercentOfContrast() {
        // Where a colour can reach 3:1 only by fading to white, it keeps its
        // tint down to LyricColour.tintedContrast (2.5:1), never lower.
        for hue in stride(from: 0.0, to: 2 * Double.pi, by: 0.35) {
            let pastel = LyricInk.pastel(OKLab(l: 0.8, chroma: 0.12, hue: hue))
            for backdrop in [0.12, 0.16, 0.2, 0.24, 0.27] {
                let ink = Perceptual.readable(pastel, background: backdrop, keepingColour: true)
                XCTAssertGreaterThanOrEqual(Perceptual.contrast(Perceptual.luminance(ink), backdrop),
                                            Perceptual.tintedContrast - 0.02, "hue \(hue) on \(backdrop)")
            }
        }
    }

    func testEasyBackdropsAreUnchangedAndNeutralStaysNeutral() {
        let pastel = OKLab(l: 0.84, chroma: 0.14, hue: 1.2)
        XCTAssertEqual(Perceptual.readable(pastel, background: 0.05, keepingColour: true), Perceptual.clipped(pastel))
        let grey = OKLab(l: 0.8, chroma: 0.0, hue: 0)
        XCTAssertLessThan(Perceptual.readable(grey, background: 0.9, keepingColour: true).chroma, 0.01)
    }

    func testIncreaseContrastStillWinsOverColour() {
        let pastel = OKLab(l: 0.84, chroma: 0.14, hue: 4.8)
        let strict = Perceptual.readable(pastel, background: 0.15, ratio: LyricInk.increasedContrast, keepingColour: true)
        XCTAssertGreaterThanOrEqual(Perceptual.contrast(Perceptual.luminance(strict), 0.15), 4.4)
    }
}
