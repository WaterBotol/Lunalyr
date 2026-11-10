import SwiftUI
import XCTest
@testable import VersoCustom

/// The lyric-motion polish pass (POLISH-AUDIT 2026-11-10, WP1b): words that
/// never appeared, exits at a low frame rate, scene cross-fades that popped
/// or dimmed, notices that cut.
@MainActor
final class LyricMotionPolishTests: XCTestCase {
    private func words(_ texts: [String], start: Double, step: Double = 0.5,
                       length: Double = 0.4) -> [LyricsFetcher.Word] {
        texts.enumerated().map {
            LyricsFetcher.Word(text: $0.element, start: start + Double($0.offset) * step,
                               end: start + Double($0.offset) * step + length)
        }
    }

    private func line(_ words: [LyricsFetcher.Word], end: Double? = nil, wordTimed: Bool = true) -> LyricsFetcher.Line {
        .init(start: words.first?.start ?? 0, end: end ?? words.last?.end ?? 0,
              text: words.map(\.text).joined(separator: " "), words: words, isInstrumental: false, wordTimed: wordTimed)
    }

    // MARK: L1

    func testAWordSungLateInALongLineStillAppears() {
        var late = words(["a", "slow", "line"], start: 10)
        late.append(LyricsFetcher.Word(text: "finally", start: 21, end: 21.4))
        let long = line(late, end: 30)
        let pose = LyricPhraseTiming(line: long, nextStart: 40, time: 21)
        let exit = LyricLayer.stageExitStart(long, pose: pose)
        XCTAssertGreaterThan(exit, 21.4, "the last word must be on screen while it is sung")
        XCTAssertEqual(exit, 21.4 + WallpaperMotion.holdAfterSung, accuracy: 1e-9)
    }

    func testAHeldLastWordIsNotCutWhileItIsSung() {
        var held = words(["hold", "this"], start: 10)
        held.append(LyricsFetcher.Word(text: "note", start: 14, end: 20))
        let long = line(held, end: 22)
        let exit = LyricLayer.stageExitStart(long, pose: LyricPhraseTiming(line: long, nextStart: 40, time: 15))
        XCTAssertGreaterThan(exit, 20)
    }

    func testAVeryLongHeldNoteIsStillCapped() {
        let endless = line([LyricsFetcher.Word(text: "ooh", start: 10, end: 60)], end: 60)
        let exit = LyricLayer.stageExitStart(endless, pose: LyricPhraseTiming(line: endless, nextStart: 80, time: 11))
        XCTAssertEqual(exit, 18 + WallpaperMotion.holdAfterSung, accuracy: 1e-9)
    }

    // MARK: L17

    func testTheFrameRateFollowsTheExitTheRendererDraws() {
        // A line whose end runs into a long break: the renderer clears it a
        // moment after its last word, long before the phrase's own exit.
        let lingering = line(words(["gone", "soon"], start: 10), end: 30)
        let lines = [lingering, line(words(["next"], start: 40))]
        let handOver = LyricPhraseTiming.handOver(after: 0, in: lines)
        let phrase = LyricPhraseTiming.exitStart(of: lingering, nextStart: handOver)
        let drawn = LyricLayer.renderedExitStart(0, in: lines, handOver: handOver, rolls: false, fillsStage: false)
        XCTAssertLessThan(drawn, phrase)
        XCTAssertEqual(drawn, LyricLayer.stageExitStart(lingering, pose: LyricPhraseTiming(line: lingering,
                                                                                          nextStart: handOver, time: 12)))
        // Rolling lines, and line-timed lines outside the stage layouts,
        // leave at the phrase's exit.
        XCTAssertEqual(LyricLayer.renderedExitStart(0, in: lines, handOver: handOver, rolls: true, fillsStage: false), phrase)
        var lineTimed = lines
        lineTimed[0].wordTimed = false
        XCTAssertEqual(LyricLayer.renderedExitStart(0, in: lineTimed, handOver: handOver, rolls: false, fillsStage: false),
                       phrase)
        XCTAssertEqual(LyricLayer.renderedExitStart(0, in: lineTimed, handOver: handOver, rolls: false, fillsStage: true),
                       drawn)
    }
}
