import XCTest
@testable import VersoCustom

final class LyricCompositionGroupTests: XCTestCase {
    func testEveryLayoutIsInExactlyOneGroup() {
        let grouped = LyricCompositionGroup.allCases.flatMap(\.layouts)
        XCTAssertEqual(grouped.count, Set(grouped).count)
        XCTAssertEqual(Set(grouped), Set(LyricComposition.allCases))
    }
}
