import XCTest
@testable import VersoCustom

/// A held note or ad-lib ringing on into the next line: a stage-filling
/// layout's line has nowhere to move aside to, so it clears the stage as
/// the next line starts rather than sit whole under it (the owner saw the
/// new line stacked over an old one that was never removed).
@MainActor
final class StageHeldLineTests: XCTestCase {
    private func line(_ texts: [String], start: Double, step: Double = 0.4, last: Double? = nil) -> LyricsFetcher.Line {
        var words = texts.enumerated().map { index, text in
            LyricsFetcher.Word(text: text, start: start + Double(index) * step,
                               end: start + Double(index) * step + step * 0.9)
        }
        if let last { words[words.count - 1].end = last }
        let end = words.map(\.end).max() ?? start
        return LyricsFetcher.Line(start: start, end: end, text: texts.joined(separator: " "), words: words,
                                  isInstrumental: false, wordTimed: true)
    }

    /// The first line's last note rings on 2.5 s into the second line.
    private var held: [LyricsFetcher.Line] {
        [line(["so", "far", "away"], start: 10, last: 13.5), line(["and", "now", "I", "know"], start: 11)]
    }

    private func drawnExit(_ lines: [LyricsFetcher.Line], fillsStage: Bool, heldUntil: Double? = nil) -> Double {
        LyricLayer.renderedExitStart(0, in: lines, handOver: LyricLayer.handOver(after: 0, in: lines) { _ in false },
                                     rolls: false, fillsStage: fillsStage, heldUntil: heldUntil)
    }

    func testTheHeldLineIsHeldOverTheNextLine() {
        XCTAssertTrue(LyricPhraseTiming.isHeld(0, in: held))
    }

    func testAStageLineClearsAsTheNextLineStarts() {
        let lines = held
        XCTAssertEqual(drawnExit(lines, fillsStage: true), lines[1].start - 0.25, accuracy: 1e-9)
        // The pose the renderer draws from agrees.
        let pose = LyricPhraseTiming(line: lines[0], nextStart: LyricPhraseTiming.handOver(after: 0, in: lines),
                                     time: 11.5, heldUntil: nil,
                                     leavesBy: LyricLayer.stageClearance(0, in: lines, fillsStage: true, heldUntil: nil))
        XCTAssertEqual(LyricLayer.stageExitStart(lines[0], pose: pose), lines[1].start - 0.25, accuracy: 1e-9)
        // Gone (its exit settled) within a second of the next line's start.
        XCTAssertGreaterThan(pose.exit, 0.9)
    }

    func testOtherLayoutsStillHoldTheLineAside() {
        // They move a held line aside (`heldShift`), so it stays until its note ends.
        XCTAssertGreaterThan(drawnExit(held, fillsStage: false), 13.5)
    }

    func testAStageThatHandsOnKeepsItsHandOver() {
        // Torque, Hinge and Portal carrying on into a line of their own
        // layout leave by that hand-over.
        XCTAssertNil(LyricLayer.stageClearance(0, in: held, fillsStage: true, heldUntil: 11.4))
        XCTAssertGreaterThan(drawnExit(held, fillsStage: true, heldUntil: 11.4), 13.5)
    }

    func testAnOrdinaryLineChangeIsUnchanged() {
        let lines = [line(["one", "two"], start: 10), line(["three"], start: 12)]
        let before = LyricPhraseTiming.exitStart(of: lines[0], nextStart: lines[1].start)
        XCTAssertEqual(drawnExit(lines, fillsStage: true),
                       min(before, LyricLayer.stageHeldUntil(lines[0])), accuracy: 1e-9)
    }

    func testNothingClearsBeforeAnInstrumentalOrAfterTheLastLine() {
        var lines = held
        lines[1].isInstrumental = true
        XCTAssertNil(LyricLayer.stageClearance(0, in: lines, fillsStage: true, heldUntil: nil))
        XCTAssertNil(LyricLayer.stageClearance(1, in: held, fillsStage: true, heldUntil: nil))
    }

    func testAStageLineGetsAMomentEvenWhenTheNextStartsAtOnce() {
        let lines = [line(["oh"], start: 10), line(["yeah", "yeah"], start: 10.1)]
        XCTAssertEqual(LyricLayer.stageClearance(0, in: lines, fillsStage: true, heldUntil: nil)!, 10.5, accuracy: 1e-9)
    }
}
