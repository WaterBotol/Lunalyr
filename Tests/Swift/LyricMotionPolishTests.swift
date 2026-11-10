import AppKit
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

    // MARK: L5

    /// Hinge's fold runs 0.9 s: its line is drawn until the fold is done,
    /// not cut at the other layouts' 0.7 s.
    func testAHingeLineIsDrawnUntilItsFoldEnds() {
        let first = line(words(["fold", "away"], start: 10), end: 11)
        let lines = [first, line(words(["next"], start: 11.5))]
        let exit = LyricPhraseTiming.exitStart(of: first, nextStart: 11.5)
        let late = exit + 0.85
        XCTAssertFalse(LyricLayer.isLeaving(0, current: 1, lines: lines, time: late, rolls: { _ in false }))
        XCTAssertTrue(LyricLayer.isLeaving(0, current: 1, lines: lines, time: late,
                                           span: { _ in LyricPhraseTiming.exitSpan(for: .hinge) }, rolls: { _ in false }))
        XCTAssertEqual(LyricPhraseTiming.exitSpan(for: .scattered), LyricPhraseTiming.exitSpan)
    }

    // MARK: L11

    /// With Reduce Motion the rolling column no longer jumps a rank in one
    /// frame at each onset: it dissolves between two settled positions.
    func testReduceMotionDissolvesTheRollingColumnBetweenSettledPlaces() {
        let sung = words(["one", "two", "three"], start: 10, step: 1)
        let prepared = SpokenTypography.prepare(.init(words: sung, baseSize: 40, mood: .neutral, composition: .spoken,
                                                      fontIdentity: LyricFontIdentity(settings: .shared), readings: [:]),
                                                settings: .shared)
        var previous: [Double: Double] = [:]
        var time = 9.9
        while time < 12.5 {
            let cursor = SpokenTypographyView.cursor(prepared, time: time, intensity: 0, reduceMotion: true)
            let stacks = SpokenTypographyView.stacks(cursor: cursor, reduceMotion: true)
            XCTAssertLessThanOrEqual(stacks.count, 2)
            XCTAssertEqual(stacks.map(\.opacity).reduce(0, +), 1, accuracy: 1e-9)
            var now: [Double: Double] = [:]
            for stack in stacks {
                XCTAssertEqual(stack.position, stack.position.rounded(), "a settled place only")
                now[stack.position] = stack.opacity
            }
            for position in Set(now.keys).union(previous.keys) where !previous.isEmpty {
                XCTAssertLessThan(abs((now[position] ?? 0) - (previous[position] ?? 0)), 0.25,
                                  "the column jumped at \(time)")
            }
            previous = now
            time += 1.0 / 60
        }
        XCTAssertEqual(SpokenTypographyView.cursor(prepared, time: 10 + WallpaperMotion.reducedDissolve, intensity: 0,
                                                   reduceMotion: true), 1, accuracy: 1e-9)
        XCTAssertEqual(SpokenTypographyView.stacks(cursor: 1.37, reduceMotion: false).map(\.position), [1.37])
    }

    // MARK: L7

    /// A line-timed line can be drawn as its previewed first word alone,
    /// held, and the rest of the line without that word: together exactly
    /// the whole line, so the word can stay put while the rest arrives.
    func testAPreviewedFirstWordAndTheRestOfTheLineMakeTheWholeLine() throws {
        let phrase = LyricsFetcher.makeLine(text: "Hold the light", start: 1, end: 5)
        let ink = LyricInk(mode: .standard,
                           base: LyricInkSample(rest: .white, sung: .white, glow: .clear, reading: .white, sweeps: false))
        func lit(firstRun: Double? = nil, held: Bool = false, hides: Bool = false) throws -> Int {
            let stage = CGSize(width: 600, height: 220)
            let renderer = ImageRenderer(content: LyricLineView(words: phrase.words, time: 1.02, stageSize: stage,
                baseSize: 72, wordTimed: false, firstRunOnly: firstRun, heldFirstRun: held, hidesFirstRun: hides)
                .environment(\.lyricInk, ink)
                .frame(width: stage.width, height: stage.height)
                .background(.black))
            let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
            var count = 0
            for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) {
                for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
                    if let pixel = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB), pixel.redComponent > 0.6 {
                        count += 1
                    }
                }
            }
            return count
        }
        let whole = try lit()
        let first = try lit(firstRun: 1, held: true)
        let rest = try lit(hides: true)
        XCTAssertGreaterThan(first, 20)
        XCTAssertGreaterThan(rest, first)
        XCTAssertEqual(Double(first + rest), Double(whole), accuracy: Double(whole) * 0.08)
        XCTAssertEqual(LyricLayer.heldFirstRunOpacity(presence: 0.02), 0.25, "never fainter than the preview")
        XCTAssertEqual(LyricLayer.heldFirstRunOpacity(presence: 0.8), 0.8)
    }

    // MARK: L8

    /// A line starts leaving a quarter second before the next line, not
    /// 80 ms before it -- but never before its own last word is sung.
    func testALineLeavesBeforeTheNextOneArrivesButNotBeforeItsLastWord() {
        let early = line(words(["done", "early"], start: 10), end: 12)
        XCTAssertEqual(LyricPhraseTiming.exitStart(of: early, nextStart: 12), 11.75, accuracy: 1e-9)
        let busy = line([LyricsFetcher.Word(text: "right", start: 10, end: 11),
                         LyricsFetcher.Word(text: "through", start: 11, end: 11.95)], end: 12)
        XCTAssertEqual(LyricPhraseTiming.exitStart(of: busy, nextStart: 12), 12.05, accuracy: 1e-9)
        // A line that ends well before the next keeps its own end.
        let short = line(words(["short"], start: 10), end: 10.4)
        XCTAssertEqual(LyricPhraseTiming.exitStart(of: short, nextStart: 20), 10.52, accuracy: 1e-9)
    }

    func testALeavingLineLiftsAsTheNextArrives() {
        let lines = [line(words(["first"], start: 10), end: 12), line(words(["second"], start: 12), end: 14)]
        let handOver = { LyricPhraseTiming.handOver(after: $0, in: lines) }
        XCTAssertEqual(LyricLayer.incomingArrival(after: 0, in: lines, time: 11.9, handOver: handOver), 0)
        var last = 0.0
        for frame in 0...60 {
            let value = LyricLayer.incomingArrival(after: 0, in: lines, time: 12 + Double(frame) / 60, handOver: handOver)
            XCTAssertLessThan(abs(value - last), 0.2)
            last = value
        }
        XCTAssertGreaterThan(last, 0.95)
    }
}
