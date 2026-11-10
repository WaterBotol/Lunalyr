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

    // MARK: Scene memory (L4, L9, W6, L21)

    private var sheet: [LyricsFetcher.Line] {
        (0..<20).map { row in line(words(["line", "\(row)"], start: Double(row) * 4), end: Double(row) * 4 + 3) }
    }

    /// Rapid jumps while scrubbing: no scene's presence ever steps, nothing
    /// visible is dropped, and no more than two scenes leave at once.
    func testScrubbingNeverPopsAScene() {
        let memory = LyricSceneMemory()
        let key = LyricSceneKey(kind: .synced, track: 1, fingerprint: 3)
        let content = LyricSceneContent(lines: sheet)
        var wall = 500.0, time = 10.0
        _ = memory.observe(key: key, content: content, time: time, wall: wall, jump: nil)
        var previous: [Int: Double] = [:]
        var previousIncoming = 1.0
        for frame in 1...120 {
            wall += 1.0 / 60
            time += 1.0 / 60
            // A jump every six frames for the first second.
            var jump: LyricClock.Jump?
            if frame <= 60, frame.isMultiple(of: 6) {
                jump = LyricClock.Jump(from: time, to: time + 7, wall: wall)
                time += 7
            }
            let t = memory.observe(key: key, content: content, time: time, wall: wall, jump: jump)
            XCTAssertLessThanOrEqual(t.leaving.count, LyricSceneMemory.maxLeaving)
            var now: [Int: Double] = [:]
            for leaving in t.leaving { now[leaving.scene.id] = leaving.presence }
            for (id, presence) in now {
                if let before = previous[id] {
                    XCTAssertLessThan(abs(presence - before), 0.2, "a leaving scene stepped at frame \(frame)")
                } else {
                    // A newly leaving scene starts where the incoming one was.
                    XCTAssertEqual(presence, previousIncoming, accuracy: 0.2, "frame \(frame)")
                }
            }
            for (id, before) in previous where now[id] == nil {
                XCTAssertLessThanOrEqual(before, 0.2, "a visible scene was dropped at frame \(frame)")
            }
            if now.keys.allSatisfy({ previous[$0] != nil }) {
                XCTAssertGreaterThanOrEqual(t.incomingPresence, previousIncoming - 1e-9,
                                            "the arriving scene was pushed back at frame \(frame)")
            }
            previous = now
            previousIncoming = t.incomingPresence
        }
        XCTAssertEqual(previousIncoming, 1)
        XCTAssertTrue(previous.isEmpty)
    }

    /// A seek redraws much the same line: the two scenes together never
    /// leave it noticeably dimmer, and neither is blurred.
    func testASeekNeitherDimsNorBlursTheLine() throws {
        let memory = LyricSceneMemory()
        let key = LyricSceneKey(kind: .synced, track: 1, fingerprint: 3)
        let content = LyricSceneContent(lines: sheet)
        _ = memory.observe(key: key, content: content, time: 10, wall: 600, jump: nil)
        let jump = LyricClock.Jump(from: 10.02, to: 10.6, wall: 600.02)
        var wall = 600.02
        var sawOutgoing = false
        while wall < 601 {
            let t = memory.observe(key: key, content: content, time: 10.6 + (wall - 600.02), wall: wall, jump: jump)
            if let outgoing = t.outgoing {
                sawOutgoing = true
                XCTAssertFalse(outgoing.blurs)
            }
            let coverage = 1 - (1 - t.incomingPresence) * (1 - t.outgoingPresence)
            XCTAssertGreaterThan(coverage, 0.95, "dimmed at +\(wall - 600.02) s")
            wall += 1.0 / 60
        }
        XCTAssertTrue(sawOutgoing)
        // A song change still dissolves softly.
        let other = LyricSceneKey(kind: .synced, track: 2, fingerprint: 4)
        let change = memory.observe(key: other, content: content, time: 0, wall: 602, jump: nil)
        XCTAssertTrue(try XCTUnwrap(change.outgoing).blurs)
    }

    /// A better source for the new song arriving while the old song's
    /// lyrics are still fading out keeps them fading, and keeps the new
    /// song's entrance going.
    func testALyricUpgradeAtSongStartKeepsTheSongChangeFade() {
        let memory = LyricSceneMemory()
        let old = LyricSceneKey(kind: .synced, track: 1, fingerprint: 3)
        let new = LyricSceneKey(kind: .synced, track: 2, fingerprint: 4)
        let upgraded = LyricSceneKey(kind: .synced, track: 2, fingerprint: 5)
        _ = memory.observe(key: old, content: LyricSceneContent(lines: sheet), time: 30, wall: 700, jump: nil)
        let change = memory.observe(key: new, content: LyricSceneContent(lines: sheet), time: 0, wall: 700.1, jump: nil)
        XCTAssertEqual(change.outgoing?.key, old)
        let upgrade = memory.observe(key: upgraded, content: LyricSceneContent(lines: sheet), time: 0.15,
                                     wall: 700.25, jump: nil)
        XCTAssertEqual(upgrade.outgoing?.key, old, "the old song keeps fading out")
        XCTAssertGreaterThan(upgrade.outgoingPresence, 0.05)
        XCTAssertLessThan(upgrade.incomingPresence, 1, "the new song's entrance carries on")
        XCTAssertGreaterThan(upgrade.incomingPresence, change.incomingPresence)
    }

    /// A better source that shows a different line cross-fades briefly,
    /// without blur, instead of cutting; one showing the same line still
    /// takes over in place.
    func testAnUpgradeShowingAnotherLineCrossFades() throws {
        let memory = LyricSceneMemory()
        let old = LyricSceneKey(kind: .synced, track: 1, fingerprint: 3)
        let upgraded = LyricSceneKey(kind: .synced, track: 1, fingerprint: 5)
        var reworded = sheet
        reworded[5].text = "other words"
        _ = memory.observe(key: old, content: LyricSceneContent(lines: sheet), time: 21, wall: 800, jump: nil)
        let swap = memory.observe(key: upgraded, content: LyricSceneContent(lines: reworded), time: 21.02,
                                  wall: 800.02, jump: nil)
        let outgoing = try XCTUnwrap(swap.outgoing)
        XCTAssertFalse(outgoing.blurs)
        XCTAssertEqual(swap.outgoingPresence, 1, accuracy: 0.01)
        XCTAssertEqual(swap.incomingPresence, 0, accuracy: 0.01)
        let done = memory.observe(key: upgraded, content: LyricSceneContent(lines: reworded), time: 21.3,
                                  wall: 800.02 + LyricSceneMemory.upgradeFade + 0.01, jump: nil)
        XCTAssertNil(done.outgoing)
        XCTAssertEqual(done.incomingPresence, 1)
        XCTAssertTrue(LyricSceneMemory.activeLineDiffers(LyricSceneContent(lines: sheet),
                                                          LyricSceneContent(lines: reworded), at: 21))
        XCTAssertFalse(LyricSceneMemory.activeLineDiffers(LyricSceneContent(lines: sheet),
                                                           LyricSceneContent(lines: reworded), at: 25))
    }
}
