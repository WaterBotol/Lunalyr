import XCTest
@testable import VersoCustom

/// Before a visualiser's stage meter has read a frame, its lyrics are never
/// guessed dark: a bright cover over a dark scene left them near-invisible.
final class VisualiserInkGuessTests: XCTestCase {
    private let pale: [SIMD3<Float>] = [SIMD3(0.95, 0.9, 0.85), SIMD3(0.9, 0.95, 0.9), SIMD3(1, 0.95, 0.8)]

    func testAnUnmeasuredVisualiserStaysUnderTheLightInkLine() {
        let guess = LyricBackdrop.beforeBacking(mode: .visualizer, motion: false, palette: pale, scrim: 0)
        XCTAssertLessThanOrEqual(guess, LyricBackdrop.unmeasuredVisualizerCeiling)
    }

    func testAMeasuredVisualiserIsTakenAsMeasured() {
        let measured = LyricBackdrop.beforeBacking(mode: .visualizer, motion: false, palette: pale, scrim: 0, drawn: 0.8)
        XCTAssertGreaterThan(measured, 0.5, "a bright scene, once read, can still take dark ink")
    }
}
