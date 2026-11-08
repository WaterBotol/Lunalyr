import XCTest
@testable import VersoCustom

final class LayerExitTests: XCTestCase {
    func testEachKindLeavesItsOwnWayAndHoldsStillUntilItGoes() {
        for exit in [LayerExit.credit(towardTop: true), .title, .backing] {
            let held = exit.pose(0, size: 100)
            XCTAssertEqual(held.offset, 0); XCTAssertEqual(held.scale, 1); XCTAssertEqual(held.blur, 0)
        }
        let credit = LayerExit.credit(towardTop: true).pose(1, size: 100)
        let low = LayerExit.credit(towardTop: false).pose(1, size: 100)
        XCTAssertLessThan(credit.offset, 0); XCTAssertGreaterThan(low.offset, 0)
        XCTAssertGreaterThan(credit.blur, 0)
        let title = LayerExit.title.pose(1, size: 100)
        XCTAssertGreaterThan(title.scale, 1); XCTAssertGreaterThan(title.blur, credit.blur)
        let backing = LayerExit.backing.pose(1, size: 100)
        XCTAssertLessThan(backing.scale, 1); XCTAssertEqual(backing.blur, 0); XCTAssertGreaterThan(backing.offset, 0)
    }

    func testCueExitStartsAtItsEnd() {
        let cue = LyricCreditCues.Cue(id: 0, text: "x", start: 1, end: 5, words: [])
        XCTAssertEqual(LyricCreditCues.exitProgress(of: cue, at: 5), 0)
        XCTAssertEqual(LyricCreditCues.exitProgress(of: cue, at: 5 + LyricCreditCues.fadeOut), 1, accuracy: 1e-9)
        XCTAssertEqual(LyricCreditCues.exitProgress(of: cue, at: 2), 0)
    }
}
