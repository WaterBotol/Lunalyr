import XCTest
@testable import VersoCustom

final class StarTrailsLyricWordTests: XCTestCase {
    private func line(_ words: [(String, Double, Double)], credit: Bool? = nil) -> LyricsFetcher.Line {
        var line = LyricsFetcher.Line(start: words.first!.1, end: words.last!.2, text: words.map(\.0).joined(separator: " "),
                                      words: words.map { LyricsFetcher.Word(text: $0.0, start: $0.1, end: $0.2) },
                                      isInstrumental: false)
        line.wordTimed = true
        line.credit = credit
        return line
    }

    func testNewestWordAndItsPlaceInTheLine() throws {
        let lines = [line([("one", 1, 1.4), ("two", 1.5, 1.9), ("three", 2, 2.6)])]
        let word = try XCTUnwrap(StarTrailsLyricWord.newest(in: lines, at: 1.6))
        XCTAssertEqual(word.fraction, 0.5, accuracy: 1e-9)
        XCTAssertEqual(word.age, 0.1, accuracy: 1e-9)
        let last = try XCTUnwrap(StarTrailsLyricWord.newest(in: lines, at: 2.1))
        XCTAssertEqual(last.fraction, 1, accuracy: 1e-9)
        XCTAssertNotEqual(word.serial, last.serial)
    }

    func testNothingBeforeOrLongAfter() {
        let lines = [line([("one", 5, 5.4)])]
        XCTAssertNil(StarTrailsLyricWord.newest(in: lines, at: 4))
        XCTAssertNil(StarTrailsLyricWord.newest(in: lines, at: 9))
    }

    func testCreditsLightNothing() {
        let lines = [line([("Produced", 1, 1.4), ("by", 1.5, 1.9)], credit: true)]
        XCTAssertNil(StarTrailsLyricWord.newest(in: lines, at: 1.6))
    }
}
