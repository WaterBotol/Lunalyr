import XCTest
@testable import VersoCustom

/// An action word still moving as its line leaves settles with it.
final class ActionSettleTests: XCTestCase {
    func testAnActionRunsFullyUntilTheLineLeavesThenSettles() {
        XCTAssertEqual(WordActionEffect.settling(at: 5, lineExit: .infinity), 1)
        XCTAssertEqual(WordActionEffect.settling(at: 9.9, lineExit: 10), 1)
        let midway = WordActionEffect.settling(at: 10 + WordActionEffect.settle / 2, lineExit: 10)
        XCTAssertGreaterThan(midway, 0)
        XCTAssertLessThan(midway, 1)
        XCTAssertEqual(WordActionEffect.settling(at: 10 + WordActionEffect.settle, lineExit: 10), 0)
    }
}
