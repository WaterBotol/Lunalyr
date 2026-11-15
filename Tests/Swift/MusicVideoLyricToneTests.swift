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

    // MARK: The floating lyrics over a music video

    func testFloatingLyricsOverAMusicVideoStayLight() {
        for strength in stride(from: 0.0, through: 1.0, by: 0.05) {
            for dim in stride(from: 0.0, through: 0.85, by: 0.05) {
                for scrim in stride(from: 0.0, through: 2.0, by: 0.25) {
                    let behind = FloatingBackdrop.behind(mode: .musicVideo, scrim: scrim, videoDim: dim)
                    for reduceTransparency in [false, true] {
                        let backdrop = FloatingBackdrop.luminance(strength: strength,
                                                                  reduceTransparency: reduceTransparency, behind: behind)
                        XCTAssertLessThan(backdrop, LyricInkDirector.lightBelow,
                                          "shade \(strength), dim \(dim), darkening \(scrim): \(backdrop) would turn the floating lyrics dark")
                    }
                }
            }
        }
    }

    func testFloatingLyricsOverAMusicVideoFollowTheDesktopRule() {
        let behind = FloatingBackdrop.behind(mode: .musicVideo, scrim: 0.4, videoDim: 0.3)
        let desktop = LyricBackdrop.luminance(mode: .musicVideo, motion: false, palette: palette, scrim: 0.4,
                                              measured: nil, backing: 0, videoDim: 0.3)
        XCTAssertEqual(behind, desktop, accuracy: 0.0001, "one rule for the video, desktop or floating")
        XCTAssertEqual(FloatingBackdrop.luminance(strength: 0, reduceTransparency: false, behind: behind), behind,
                       accuracy: 0.0001)
    }

    func testFloatingLyricsElsewhereStillJudgeAnUnknownApp() {
        for mode in [VersoCustom.Settings.BackgroundMode.desktop, .custom, .visualizer, .colourOnly] {
            XCTAssertEqual(FloatingBackdrop.behind(mode: mode, scrim: 1, videoDim: 0.5),
                           LyricLegibility.unknownBackdropLuminance, mode.rawValue)
        }
    }

    /// The text colours themselves: the ink turns dark over an unknown app
    /// with no shade, never over a music video.
    @MainActor func testFloatingInkOverAMusicVideoIsNotDark() {
        func darkText(behind: Double) -> Bool {
            let director = LyricInkDirector()
            let backdrop = FloatingBackdrop.luminance(strength: 0, reduceTransparency: false, behind: behind)
            var inputs = LyricInkDirector.Inputs(mode: .albumDrift, album: palette, backdrop: backdrop)
            inputs.toneBackdrop = backdrop
            inputs.publishesTone = false
            _ = director.ink(inputs, wall: 100)
            _ = director.ink(inputs, wall: 101)
            return director.darkText
        }
        XCTAssertFalse(darkText(behind: FloatingBackdrop.behind(mode: .musicVideo, scrim: 0, videoDim: 0)))
        XCTAssertTrue(darkText(behind: 0.6), "a light app with no shade still gets dark text")
    }
}
