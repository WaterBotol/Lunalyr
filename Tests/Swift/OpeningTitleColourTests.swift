import SwiftUI
import XCTest
@testable import VersoCustom

/// The song title card is white, or one colour of your own per line, never
/// the lyrics' colour (the owner, 20 Nov 2026).
@MainActor
final class OpeningTitleColourTests: XCTestCase {
    private func hex(_ colour: Color) -> String? { LyricColourOptions.hex(colour) }

    func testWhiteUnlessYourOwnColoursAreOn() {
        let off = OpeningTitle.Colours.resolved(custom: false, title: "#FF0000", artist: "#00FF00", album: "#0000FF")
        XCTAssertEqual(off, .white)
    }

    func testEachLineTakesItsOwnColour() {
        let on = OpeningTitle.Colours.resolved(custom: true, title: "#FF0000", artist: "#00FF00", album: "#0000FF")
        XCTAssertEqual(hex(on.title), "#FF0000")
        XCTAssertEqual(hex(on.artist), "#00FF00")
        XCTAssertEqual(hex(on.album), "#0000FF")
    }

    func testAColourThatCannotBeReadIsWhite() {
        let on = OpeningTitle.Colours.resolved(custom: true, title: "nonsense", artist: "", album: "#12345")
        XCTAssertEqual(hex(on.title), "#FFFFFF")
        XCTAssertEqual(hex(on.artist), "#FFFFFF")
        XCTAssertEqual(hex(on.album), "#FFFFFF")
    }
}
