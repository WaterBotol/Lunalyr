import XCTest
@testable import VersoCustom

final class LyricGapTimingTests: XCTestCase {
    func testNativeWordEndMarksActualVocalWindow() {
        let line = LyricsFetcher.Line(start: 1, end: 30, text: "Hold", words: [.init(text: "Hold", start: 1.2, end: 4)], isInstrumental: false, wordTimed: true)
        let windows = LyricGapTiming.windows(for: [line])
        XCTAssertEqual(windows.first?.start, 1.2)
        XCTAssertEqual(windows.first?.end, 4)
    }
    func testEstimatedWordsDoNotInventAnEarlierVocalEnd() {
        let line = LyricsFetcher.Line(start: 1, end: 30, text: "Hold", words: [.init(text: "Hold", start: 1, end: 25)], isInstrumental: false)
        XCTAssertEqual(LyricGapTiming.windows(for: [line]).first?.end, 30)
    }
    func testExplicitInstrumentalLinesAreExcluded() {
        let line = LyricsFetcher.Line(start: 5, end: 20, text: "", words: [], isInstrumental: true)
        XCTAssertTrue(LyricGapTiming.windows(for: [line]).isEmpty)
    }
    func testInvalidOrUntimedEmptyLinesAreIgnored() {
        let line = LyricsFetcher.Line(start: .nan, end: 20, text: "Hold", words: [.init(text: "Hold", start: 1, end: 3)], isInstrumental: false)
        XCTAssertTrue(LyricGapTiming.windows(for: [line]).isEmpty)
    }
}
