import XCTest
@testable import VersoCustom

/// A translation wider than its line does not widen what is fitted to the
/// stage, so the sung words keep their size.
final class LineAndTranslationsTests: XCTestCase {
    func testTheStackIsAsWideAsTheLineNotItsTranslation() {
        let size = LineAndTranslations.size(of: [CGSize(width: 400, height: 80), CGSize(width: 900, height: 40)],
                                            spills: [false, true], spacing: 10)
        XCTAssertEqual(size.width, 400)
        XCTAssertEqual(size.height, 130, "still tall enough for both")
    }

    func testAWideLineKeepsItsOwnWidth() {
        let size = LineAndTranslations.size(of: [CGSize(width: 40, height: 20), CGSize(width: 1000, height: 80),
                                                 CGSize(width: 600, height: 20)],
                                            spills: [true, false, true], spacing: 0)
        XCTAssertEqual(size.width, 1000)
        XCTAssertEqual(size.height, 120)
    }

    func testOnlyTranslationsStillHaveAWidth() {
        XCTAssertEqual(LineAndTranslations.size(of: [CGSize(width: 300, height: 20)], spills: [true], spacing: 4).width, 300)
        XCTAssertEqual(LineAndTranslations.size(of: [], spills: [], spacing: 4), .zero)
    }
}
