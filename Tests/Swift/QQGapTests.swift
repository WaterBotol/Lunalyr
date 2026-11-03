import XCTest
@testable import VersoCustom

final class QQGapTests: XCTestCase {
    func testLongMusicGapDoesNotExtendTheEstimatedWords() {
        let original = LyricsFetcher.parseLRC("[00:01]Hold on\n[00:35]The song returns", trackDuration: 40)
        let fixed = QQGapRepair.repair(original)
        XCTAssertEqual(fixed.count, 3)
        XCTAssertLessThan(fixed[0].end, 10)
        XCTAssertLessThanOrEqual(fixed[0].words.last?.end ?? 100, fixed[0].end)
        XCTAssertTrue(fixed[1].isInstrumental)
        XCTAssertEqual(fixed[1].end, 35)
        XCTAssertEqual(fixed[2].start, 35)
    }
    func testNormalVerseDoesNotGainInstrumentalBreaks() {
        let original = LyricsFetcher.parseLRC("[00:01]Keep it moving\n[00:04]One more phrase\n[00:07]Hold the light", trackDuration: 10)
        let fixed = QQGapRepair.repair(original)
        XCTAssertEqual(fixed.map(\.start), original.map(\.start))
        XCTAssertEqual(fixed.map(\.end), original.map(\.end))
    }
    func testRealHeldWordIsNotShortened() {
        let held = LyricsFetcher.Line(start: 1, end: 35, text: "Stay", words: [.init(text: "Stay", start: 1, end: 34)], isInstrumental: false, wordTimed: true)
        let fixed = QQGapRepair.repair([held])
        XCTAssertEqual(fixed.count, 1)
        XCTAssertEqual(fixed[0].words[0].end, 34)
    }
    func testKnownWordEndCreatesGapWithoutRetimingWords() {
        let line = LyricsFetcher.Line(start: 1, end: 35, text: "Stay", words: [.init(text: "Stay", start: 1, end: 4)], isInstrumental: false, wordTimed: true)
        let fixed = QQGapRepair.repair([line])
        XCTAssertEqual(fixed.count, 2)
        XCTAssertEqual(fixed[0].words[0].end, 4)
        XCTAssertTrue(fixed[1].isInstrumental)
    }
}
