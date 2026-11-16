import XCTest
@testable import VersoCustom

final class ShowingLayoutTests: XCTestCase {
    func testCustomMixNamesTheLayoutThisSongTook() {
        XCTAssertEqual(ShowingLayout.title(chosen: nil, showing: nil), "Custom Mix")
        XCTAssertEqual(ShowingLayout.title(chosen: nil, showing: .hinge), "Custom Mix · \(LyricComposition.hinge.title)")
        XCTAssertEqual(ShowingLayout.title(chosen: .cutout, showing: .hinge), LyricComposition.cutout.title)
    }

    @MainActor
    func testANoteArrivesOnceAndLater() {
        let showing = ShowingLayout()
        showing.note(.portal)
        XCTAssertNil(showing.layout, "never published inside a view update")
        let done = expectation(description: "published")
        DispatchQueue.main.async { done.fulfill() }
        wait(for: [done], timeout: 1)
        XCTAssertEqual(showing.layout, .portal)
    }
}
