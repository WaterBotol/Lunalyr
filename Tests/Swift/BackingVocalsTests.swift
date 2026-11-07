import XCTest
@testable import VersoCustom

final class BackingVocalsTests: XCTestCase {
    private func words(_ pieces: [(String, Double)]) -> [LyricsFetcher.Word] {
        pieces.map { LyricsFetcher.Word(text: $0.0, start: $0.1, end: $0.1 + 0.3) }
    }

    func testBracketedWordsAreLiftedOutOfTheLead() {
        let line = words([("Hold", 1), ("me", 1.3), ("(oh", 1.6), ("oh)", 1.9), ("tonight", 2.2)])
        let split = BackingVocals.split(line, glue: [false, false, false, false, false])
        XCTAssertEqual(split.main.map(\.text), ["Hold", "me", "tonight"])
        XCTAssertEqual(split.backing.map(\.text), ["oh oh"])
        XCTAssertEqual(split.backing[0].start, 1.6, accuracy: 0.001)
        XCTAssertEqual(split.backing[0].end, 2.2, accuracy: 0.001)
        XCTAssertEqual(split.mainGlue.count, split.main.count)
    }

    func testASingleBracketedWordAndSeveralGroups() {
        let line = words([("(ooh)", 0), ("Stay", 0.4), ("(yeah)", 0.8), ("here", 1.2), ("(ha", 1.6), ("ha)", 1.9)])
        let split = BackingVocals.split(line, glue: Array(repeating: false, count: 6))
        XCTAssertEqual(split.main.map(\.text), ["Stay", "here"])
        XCTAssertEqual(split.backing.map(\.text), ["ooh", "yeah", "ha ha"])
    }

    func testALineWithNoBracketsIsUntouched() {
        let line = words([("Plain", 0), ("words", 0.4)])
        let split = BackingVocals.split(line, glue: [false, true])
        XCTAssertEqual(split.main, line)
        XCTAssertEqual(split.mainGlue, [false, true])
        XCTAssertTrue(split.backing.isEmpty)
    }

    func testAWordAfterALiftedGroupIsNotGluedToIt() {
        let line = words([("Go", 0), ("(oh)", 0.4), ("now", 0.8)])
        let split = BackingVocals.split(line, glue: [false, false, true])
        XCTAssertEqual(split.main.map(\.text), ["Go", "now"])
        XCTAssertEqual(split.mainGlue, [false, false])
    }

    func testAnUnclosedBracketTakesTheRestOfTheLine() {
        let line = words([("Stay", 0), ("(oh", 0.4), ("oh", 0.8)])
        let split = BackingVocals.split(line, glue: [false, false, false])
        XCTAssertEqual(split.main.map(\.text), ["Stay"])
        XCTAssertEqual(split.backing.map(\.text), ["oh oh"])
    }

    func testTheLayerEasesInHoldsAndLingersAfterAQuickSung() {
        let group = BackingVocals.Group(id: 0, text: "oh", start: 10, end: 10.2)
        XCTAssertEqual(BackingVocalLayer.level(of: group, at: 9.5), 0)
        XCTAssertGreaterThan(BackingVocalLayer.level(of: group, at: 10.2), 0)
        XCTAssertLessThan(BackingVocalLayer.level(of: group, at: 9.95), 1)
        XCTAssertEqual(BackingVocalLayer.level(of: group, at: 11.0), 1, "held past the 0.2 s it was sung")
        XCTAssertGreaterThan(BackingVocalLayer.level(of: group, at: 11.5), 0)
        XCTAssertEqual(BackingVocalLayer.level(of: group, at: 13), 0)
    }

    func testGroupsKeepTheirWordsWithoutBrackets() {
        let split = BackingVocals.split(words([("Go", 0), ("(oh", 0.4), ("oh)", 0.7), ("now", 1)]), glue: [false, false, false, false])
        XCTAssertEqual(split.backing[0].words.map(\.text), ["oh", "oh"])
        XCTAssertEqual(split.backing[0].words[1].start, 0.7, accuracy: 0.001)
        XCTAssertEqual(split.backing[0].glue.count, 2)
    }

    func testBackingWordsNeverAnimateInTheRollingLayouts() {
        for lead in [LyricComposition.spoken, .cascade, .cutout] {
            XCTAssertEqual(BackingVocals.style(forLead: lead, chosen: nil), .editorial, "\(lead)")
        }
        for lead in BackingVocals.choices {
            XCTAssertEqual(BackingVocals.style(forLead: lead, chosen: nil), lead)
        }
        XCTAssertEqual(BackingVocals.style(forLead: .cutout, chosen: .echo), .echo)
        XCTAssertFalse(BackingVocals.choices.contains { $0.arrivalOnly })
    }
}
