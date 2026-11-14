import XCTest
@testable import VersoCustom

/// Window rules from docs/DESIGN.md ("Windows") that code can check.
final class WindowDesignTests: XCTestCase {
    func testShortCaptionsShowWhole() {
        let caption = "Stops bright wallpapers washing the menu bar out."
        XCTAssertEqual(InspectorCaption.split(caption).short, caption)
        XCTAssertNil(InspectorCaption.split(caption).full)
    }

    func testLongCaptionsFoldOnlyAtASentenceEnd() {
        let caption = "Each song picks a look to suit its energy and feel. Animated covers play where Apple Music has them, "
            + "and the look changes when the song does."
        let split = InspectorCaption.split(caption)
        XCTAssertEqual(split.short, "Each song picks a look to suit its energy and feel.")
        XCTAssertEqual(split.full, caption)
    }

    func testACaptionIsNeverCutMidSentence() {
        let captions = [
            "Each song picks a look to suit its energy and feel, with animated covers where Apple Music has them and the album allows it.",
            "Light covers and images get a soft shadow behind the lyrics, just strong enough to read them, and none on dark ones.",
        ]
        for caption in captions {
            let split = InspectorCaption.split(caption)
            XCTAssertFalse(split.short.hasSuffix("\u{2026}"), split.short)
            XCTAssertEqual(split.short, caption, "a long first sentence shows whole")
            XCTAssertNil(split.full)
        }
    }
}
