import XCTest
@testable import VersoCustom

/// "Make the colours pastel", for the custom gradient: off keeps the picked
/// colours (lightened only as contrast needs); the contrast floors hold
/// either way; other modes always ease to pastel.
@MainActor
final class LyricPastelTests: XCTestCase {
    private let deep = ["#1E3A8A", "#B91C1C", "#047857"].compactMap(LyricColourOptions.oklab(hex:))
    private let soft = ["#F9D29D", "#F28FAD", "#9AD0F5"].compactMap(LyricColourOptions.oklab(hex:))

    private func settled(_ inputs: LyricInkDirector.Inputs) -> LyricInk {
        let director = LyricInkDirector()
        var ink = director.ink(inputs, wall: 0)
        for step in 1...80 { ink = director.ink(inputs, wall: Double(step) * 0.25) }
        return ink
    }

    private func inputs(_ mode: LyricColourMode, custom: [OKLab], backdrop: Double, pastel: Bool)
        -> LyricInkDirector.Inputs {
        var inputs = LyricInkDirector.Inputs(mode: mode, custom: custom,
                                             album: [SIMD3(0.95, 0.55, 0.42), SIMD3(0.35, 0.25, 0.55)],
                                             backdrop: backdrop)
        inputs.pastel = pastel
        return inputs
    }

    func testOffKeepsTheCustomColoursAsPicked() {
        let ink = settled(inputs(.customGradient, custom: soft, backdrop: 0.03, pastel: false))
        for (stop, picked) in zip([ink.stops.0, ink.stops.1, ink.stops.2], soft) {
            XCTAssertEqual(stop.l, picked.l, accuracy: 0.01)
            XCTAssertEqual(stop.chroma, picked.chroma, accuracy: 0.01)
        }
        let eased = settled(inputs(.customGradient, custom: soft, backdrop: 0.03, pastel: true))
        XCTAssertNotEqual(eased.stops.0.chroma, ink.stops.0.chroma, accuracy: 0.01, "on, they are eased to pastel")
    }

    func testContrastFloorsHoldWithoutPastel() {
        for backdrop in [0.02, 0.1, 0.2] {
            for custom in [deep, soft] {
                let ink = settled(inputs(.customGradient, custom: custom, backdrop: backdrop, pastel: false))
                for step in 0...12 {
                    // As the stage draws a word (`LyricInk.sample`).
                    let colour = Perceptual.readable(ink.gradient(slot: Double(step) / 12), background: backdrop,
                                                     ratio: ink.contrast, keepingColour: true)
                    let ratio = (Perceptual.luminance(colour) + 0.05) / (backdrop + 0.05)
                    XCTAssertGreaterThanOrEqual(ratio, 2.45, "backdrop \(backdrop), slot \(step)")
                }
            }
        }
    }

    func testOnlyTheCustomGradientCanTurnPastelOff() {
        XCTAssertFalse(LyricColourOptions.pastel(false, appliesTo: .customGradient))
        for mode in LyricColourMode.allCases where mode != .customGradient {
            XCTAssertTrue(LyricColourOptions.pastel(false, appliesTo: mode), "\(mode)")
        }
        XCTAssertTrue(LyricColourOptions.pastel(true, appliesTo: .customGradient))
    }

    func testTheSettingIsStoredAndOnByDefault() {
        let name = "LyricPastelTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let options = LyricColourOptions(defaults: defaults)
        XCTAssertTrue(options.pastel)
        options.pastel = false
        XCTAssertFalse(LyricColourOptions(defaults: defaults).pastel)
        XCTAssertTrue(LyricStyleProfiles.keys.contains(LyricColourOptions.Keys.pastel), "kept per surface")
    }
}
