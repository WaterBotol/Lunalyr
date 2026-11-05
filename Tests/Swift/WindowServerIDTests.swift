import XCTest
@testable import VersoCustom

/// Converting window numbers must never trap: a launch crash came from a
/// window number that did not fit a 32-bit CGWindowID.
final class WindowServerIDTests: XCTestCase {
    func testOrdinaryNumbersConvert() {
        XCTAssertEqual(WindowServerID.id(forWindowNumber: 1), 1)
        XCTAssertEqual(WindowServerID.id(forWindowNumber: 4_242), 4_242)
        XCTAssertEqual(WindowServerID.id(forWindowNumber: Int(UInt32.max)), UInt32.max)
    }

    func testNumbersWithoutAWindowServerWindowAreNil() {
        XCTAssertNil(WindowServerID.id(forWindowNumber: 0))
        XCTAssertNil(WindowServerID.id(forWindowNumber: -1))
        XCTAssertNil(WindowServerID.id(forWindowNumber: Int.min))
    }

    func testNumbersTooLargeForCGWindowIDAreNilInsteadOfTrapping() {
        XCTAssertNil(WindowServerID.id(forWindowNumber: Int(UInt32.max) + 1))
        XCTAssertNil(WindowServerID.id(forWindowNumber: Int.max))
    }
}
