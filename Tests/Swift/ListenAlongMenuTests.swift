import XCTest
@testable import VersoCustom

@MainActor
final class ListenAlongMenuTests: XCTestCase {
    func testSharingTitleCountsListeners() {
        XCTAssertEqual(MenuBarListenAlong.sharingTitle(listeners: 0), "Sharing \u{00B7} no one listening yet")
        XCTAssertEqual(MenuBarListenAlong.sharingTitle(listeners: 1), "Sharing \u{00B7} 1 listening")
        XCTAssertEqual(MenuBarListenAlong.sharingTitle(listeners: 4), "Sharing \u{00B7} 4 listening")
    }
}
