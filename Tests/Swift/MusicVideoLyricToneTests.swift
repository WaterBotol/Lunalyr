import XCTest
@testable import VersoCustom

/// Over a music video the lyrics stay light: the footage isn't measured, and
/// guessing it light turned the words dark and muddy with a pale glow over a
/// dimmed video whenever Darkening was under about 133%.
final class MusicVideoLyricToneTests: XCTestCase {
    private let palette: [SIMD3<Float>] = [SIMD3(0.8, 0.6, 0.3), SIMD3(0.5, 0.3, 0.2), SIMD3(0.9, 0.8, 0.6)]

    func testUnmeasuredVideoNeverAsksForDarkText() {
        for dim in stride(from: 0.0, through: 0.85, by: 0.05) {
            for scrim in stride(from: 0.0, through: 2.0, by: 0.1) {
                let unbacked = LyricBackdrop.luminance(mode: .musicVideo, motion: false, palette: palette, scrim: scrim,
                                                       measured: nil, backing: 0, videoDim: dim)
                XCTAssertLessThan(unbacked, LyricInkDirector.lightBelow,
                                  "dim \(dim), darkening \(scrim): \(unbacked) would turn the lyrics dark")
            }
        }
    }

    func testTheVideoDimmingDarkensTheBackdrop() {
        let measured = 0.6
        let plain = LyricBackdrop.beforeBacking(mode: .musicVideo, motion: false, palette: palette, scrim: 0,
                                                measured: measured, videoDim: 0)
        let dimmed = LyricBackdrop.beforeBacking(mode: .musicVideo, motion: false, palette: palette, scrim: 0,
                                                 measured: measured, videoDim: 0.63)
        XCTAssertLessThan(dimmed, plain * 0.5)
    }
}
