import XCTest
import CoreText
import SwiftUI
import AppKit
@testable import VersoCustom

/// A glued run's parts appear one by one through a mask. Its soft edge
/// must stay inside the gap between two characters, or a slice of the next,
/// still hidden character shows early (seen on a long Japanese line).
final class TokenRevealEdgeTests: XCTestCase {
    /// The mask's opacity at `x` (a share of the label), read off its stops.
    private func opacity(_ stops: [Gradient.Stop], at x: Double) -> Double {
        // Stops are (colour, location); read the alpha back out through NSColor.
        func alpha(_ stop: Gradient.Stop) -> Double { Double(NSColor(stop.color).alphaComponent) }
        guard let after = stops.firstIndex(where: { $0.location >= x }) else { return alpha(stops.last!) }
        guard after > 0 else { return alpha(stops[0]) }
        let a = stops[after - 1], b = stops[after]
        let t = b.location > a.location ? (x - a.location) / (b.location - a.location) : 1
        return alpha(a) + (alpha(b) - alpha(a)) * t
    }

    func testAHiddenCharacterShowsNothingPastTheGap() {
        let text = "好きだけじゃ生き残れない思い描いた未来"
        let font = CTFontCreateWithName("Hiragino Sans" as CFString, 52, nil)
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: [.font: font]))
        let width = CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil))
        // Two parts: all but the last two characters shown, the rest hidden.
        let split = text.count - 2
        let boundary = Double(split) / Double(text.count)
        let stops = TokenReveal.stops([0, boundary, 1], [1, 0], feather: TokenReveal.feather(width: width))
        // The hidden character's ink starts at its left side bearing.
        let glyph = CTFontGetGlyphWithName(font, "uni672A" as CFString)
        var bounds = CGRect.zero
        var g = glyph
        CTFontGetBoundingRectsForGlyphs(font, .horizontal, &g, &bounds, 1)
        let inkStart = boundary + Double(max(0.5, bounds.minX) / width)
        XCTAssertLessThan(opacity(stops, at: inkStart), 0.01, "a slice of the hidden character would show")
        XCTAssertGreaterThan(opacity(stops, at: boundary - 2 / Double(width)), 0.99)
        // The old 1.2 % edge reached it.
        let old = TokenReveal.stops([0, boundary, 1], [1, 0])
        XCTAssertGreaterThan(opacity(old, at: inkStart), 0.05)
    }

    func testANarrowLabelKeepsItsSoftEdge() {
        XCTAssertEqual(TokenReveal.feather(width: 40), 0.012, accuracy: 1e-9)
        XCTAssertEqual(TokenReveal.feather(width: 1000), 0.001, accuracy: 1e-9)
    }

    /// A mixed run's edges fall where its letters do, not by character
    /// count: "HAPPY" is wider per letter than the kana in a condensed face
    /// is narrower, and by count the edge before メ cut through メ.
    func testAMixedRunsEdgesFallBetweenItsCharacters() {
        let font = CTFontCreateWithName("AvenirNextCondensed-Heavy" as CFString, 60, nil)
        let labels = ["HAPPY", "な", "イ", "メ", "ー", "ジ"]
        let measured = LyricRuns.boundaries(labels: labels, font: font)
        let counted = LyricRuns.boundaries(labels: labels, font: nil)
        XCTAssertEqual(measured.count, labels.count + 1)
        XCTAssertEqual(measured.first, 0); XCTAssertEqual(measured.last, 1)
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: labels.joined(),
            attributes: [NSAttributedString.Key(kCTFontAttributeName as String): font]))
        let width = CTLineGetTypographicBounds(line, nil, nil, nil)
        // The edge before メ is where メ starts.
        let me = Double(CTLineGetOffsetForStringIndex(line, "HAPPYなイ".utf16.count, nil)) / width
        XCTAssertEqual(measured[3], me, accuracy: 1e-6)
        XCTAssertGreaterThan(abs(counted[3] - me), 0.02, "by count the edge sits inside a character")
        XCTAssertEqual(zip(measured, measured.dropFirst()).allSatisfy { $0 <= $1 }, true)
    }

    func testRightToLeftAndSingleTokensKeepTheCharacterShare() {
        let font = CTFontCreateWithName("GeezaPro" as CFString, 40, nil)
        XCTAssertEqual(LyricRuns.boundaries(labels: ["سلا", "م"], font: font),
                       LyricRuns.boundaries(labels: ["سلا", "م"], font: nil))
        XCTAssertEqual(LyricRuns.boundaries(labels: ["word"], font: font), [0, 1])
    }
}
