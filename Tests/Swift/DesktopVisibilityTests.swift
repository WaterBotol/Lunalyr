import CoreGraphics
import XCTest
@testable import VersoCustom

final class DesktopVisibilityTests: XCTestCase {
    private func window(_ id: UInt32, layer: Int32) -> [String: Any] {
        [kCGWindowNumber as String: NSNumber(value: id), kCGWindowLayer as String: NSNumber(value: layer)]
    }
    @MainActor
    func testNormalAppUnderPointerBlocksReveal() async {
        XCTAssertFalse(DesktopVisibility.evaluate(hitWindowNumber: 77, ignoring: [], metadata: [window(77, layer: 0)]))
    }
    @MainActor
    func testBareDesktopHasNoInteractiveTarget() async {
        XCTAssertTrue(DesktopVisibility.evaluate(hitWindowNumber: 0, ignoring: [], metadata: nil))
    }
    @MainActor
    func testBackgroundDesktopSurfaceAllowsReveal() async {
        XCTAssertTrue(DesktopVisibility.evaluate(hitWindowNumber: 77, ignoring: [], metadata: [window(77, layer: -1)]))
    }
    @MainActor
    func testOwnWallpaperAndControlsDoNotBlock() async {
        XCTAssertTrue(DesktopVisibility.evaluate(hitWindowNumber: 44, ignoring: [44, 55], metadata: nil))
        XCTAssertTrue(DesktopVisibility.evaluate(hitWindowNumber: 55, ignoring: [44, 55], metadata: nil))
    }
    @MainActor
    func testMalformedUnrelatedEntryDoesNotDisableHover() async {
        let metadata: [[String: Any]] = [[:], [kCGWindowNumber as String: 999], window(77, layer: -1)]
        XCTAssertTrue(DesktopVisibility.evaluate(hitWindowNumber: 77, ignoring: [], metadata: metadata))
    }
    @MainActor
    func testAppOnAnotherMonitorDoesNotBlockDesktopHit() async {
        XCTAssertTrue(DesktopVisibility.evaluate(hitWindowNumber: 77, ignoring: [],
                                                metadata: [window(88, layer: 0), window(77, layer: -1)]))
    }
    @MainActor
    func testUnknownInteractiveTargetDoesNotActivateThroughApp() async {
        XCTAssertFalse(DesktopVisibility.evaluate(hitWindowNumber: 77, ignoring: [], metadata: nil))
        XCTAssertFalse(DesktopVisibility.evaluate(hitWindowNumber: 77, ignoring: [], metadata: [window(88, layer: -1)]))
    }
}
