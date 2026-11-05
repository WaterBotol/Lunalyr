import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Lyric motion is sampled far faster than any display refreshes, so a jump
/// that a 60 Hz frame could hide still fails here.
final class OrganicMotionTests: XCTestCase {
    private let first = LyricsFetcher.Line(start: 10, end: 11.9, text: "Hold the night", words: [
        .init(text: "Hold", start: 10, end: 10.35), .init(text: "the", start: 10.35, end: 10.6),
        .init(text: "night", start: 10.6, end: 11.9),
    ], isInstrumental: false, wordTimed: true)
    /// Starts while the first line's last word is still being sung, so the
    /// first line is interrupted mid-motion.
    private let second = LyricsFetcher.Line(start: 11.7, end: 13.4, text: "and let it go", words: [
        .init(text: "and", start: 11.7, end: 11.9), .init(text: "let", start: 11.9, end: 12.2),
        .init(text: "it", start: 12.2, end: 12.4), .init(text: "go", start: 12.4, end: 13.4),
    ], isInstrumental: false, wordTimed: true)

    /// What one word of a scattered-family line looks like on screen: the
    /// same values the line, word, entrance and float views apply.
    private struct Visible {
        var opacity: Double
        var y: Double
        var scale: Double
    }

    @MainActor
    private func prepared(_ line: LyricsFetcher.Line) -> [ScatteredTypography.Word] {
        let key = ScatteredTypography.Key(words: line.words, baseSize: 60, style: .forLine(0), mood: .neutral,
            composition: .scattered, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared))
        return ScatteredTypography.prepare(key, settings: .shared)
    }

    @MainActor
    private func visible(_ line: LyricsFetcher.Line, nextStart: Double, time: Double,
                         tokens: [ScatteredTypography.Word]? = nil, previewSince: Double = -.infinity) -> [Visible] {
        let pose = LyricPhraseTiming(line: line, nextStart: nextStart, time: time)
        let prepared = tokens ?? self.prepared(line)
        let next = prepared.firstIndex { $0.source.start > time }
        let lineOffset = (1 - pose.arrival) * 0.20 - pose.exit * 0.10
        let lineScale = 0.965 + 0.035 * pose.arrival - 0.012 * pose.exit
        return prepared.map { token in
            let state = ScatteredWordFrame.state(token, nextIndex: next, time: time, composition: .scattered,
                exitStart: pose.exitStart,
                eligibleSince: token.index > 0 ? prepared[token.index - 1].source.start : previewSince)
            let frame = state.frame
            let drift = vc_idle_drift(token.seed, time, 0.6)
            let entrance = (1 - frame.presentation.arrival) * 0.48
            let y = lineOffset + Double(token.metrics.size / 60) * (entrance + frame.driftY + drift.y - 0.26 * state.leave)
            return Visible(opacity: frame.presentation.opacity * (1 - state.leave), y: y,
                           scale: lineScale * frame.driftScale * (1 + 0.07 * frame.heat) * drift.scale)
        }
    }

    @MainActor
    func testLineChangeHasNoJumpInPositionOrOpacity() {
        // Through the first line's entrance, its interruption by the second
        // line, the hand-over and both exits, at 1 kHz.
        var previous: [Visible]?
        var previousNext: [Visible]?
        let firstTokens = prepared(first), secondTokens = prepared(second)
        for step in 0...5200 {
            let time = 9.5 + Double(step) / 1000
            let now = visible(first, nextStart: second.start, time: time, tokens: firstTokens)
            // The second line's preview is allowed once the first line's last word begins.
            let incoming = visible(second, nextStart: 15, time: time, tokens: secondTokens, previewSince: 10.6)
            if let previous, let previousNext {
                for (a, b) in zip(previous + previousNext, now + incoming) {
                    XCTAssertLessThan(abs(a.opacity - b.opacity), 0.03, "opacity jumped at \(time)")
                    XCTAssertLessThan(abs(a.y - b.y), 0.01, "position jumped at \(time)")
                    XCTAssertLessThan(abs(a.scale - b.scale), 0.01, "scale jumped at \(time)")
                }
            }
            previous = now; previousNext = incoming
        }
    }

    @MainActor
    func testLinesAreOnlyDroppedOnceEveryWordHasLeft() {
        // The layer stops drawing an outgoing line at a fixed time; nothing
        // of it may still be visible then.
        let end = LyricPhraseTiming.exitStart(of: first, nextStart: second.start) + LyricPhraseTiming.exitSpan
        XCTAssertTrue(LyricPhraseTiming.stillLeaving(first, nextStart: second.start, time: end - 0.001))
        XCTAssertFalse(LyricPhraseTiming.stillLeaving(first, nextStart: second.start, time: end))
        for word in visible(first, nextStart: second.start, time: end - 0.001) {
            XCTAssertEqual(word.opacity, 0, accuracy: 0.0001)
        }
        // And a line is invisible when it is first drawn, ahead of its preview.
        for word in visible(second, nextStart: 15, time: second.start - 1.0) {
            XCTAssertEqual(word.opacity, 0, accuracy: 0.0001)
        }
    }

    @MainActor
    func testWordsLeaveOneByOneInReadingOrder() {
        let pose = LyricPhraseTiming(line: first, nextStart: second.start, time: 11.8)
        let key = ScatteredTypography.Key(words: first.words, baseSize: 60, style: .forLine(0), mood: .neutral,
            composition: .scattered, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared))
        let prepared = ScatteredTypography.prepare(key, settings: .shared)
        let leave = prepared.map {
            ScatteredWordFrame.state($0, nextIndex: nil, time: pose.exitStart + 0.12, composition: .scattered,
                                     exitStart: pose.exitStart).leave
        }
        XCTAssertGreaterThan(leave[0], leave[2])
        XCTAssertGreaterThan(leave[0], 0)
    }

    @MainActor
    func testThirtyAndSixtyFramesPerSecondReachTheSameState() {
        // Music re-anchors every two seconds with a few tens of ms of error.
        func raw(_ wall: Double) -> Double { wall - 100 + Double(Int(wall / 2) % 3 - 1) * 0.045 }
        let thirty = LyricClock(), sixty = LyricClock()
        _ = thirty.time(wall: 100, raw: raw(100), playing: true)
        _ = sixty.time(wall: 100, raw: raw(100), playing: true)
        let key = ScatteredTypography.Key(words: first.words, baseSize: 60, style: .forLine(0), mood: .neutral,
            composition: .constellation, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared))
        let token = ScatteredTypography.prepare(key, settings: .shared)[2]
        for frame in 1...720 {
            let wall = 100 + Double(frame) / 60
            let fast = sixty.time(wall: wall, raw: raw(wall), playing: true)
            guard frame % 2 == 0 else { continue }
            let slow = thirty.time(wall: wall, raw: raw(wall), playing: true)
            XCTAssertEqual(slow, fast, accuracy: 0.002)
            let a = ScatteredWordFrame.make(token, nextIndex: nil, time: slow + 9, composition: .constellation)
            let b = ScatteredWordFrame.make(token, nextIndex: nil, time: fast + 9, composition: .constellation)
            XCTAssertEqual(a.heat, b.heat, accuracy: 0.02)
            XCTAssertEqual(a.presentation.opacity, b.presentation.opacity, accuracy: 0.02)
        }
    }

    @MainActor
    func testReanchoringNeverJumpsTheLyricClock() {
        let clock = LyricClock()
        var last = clock.time(wall: 50, raw: 0, playing: true)
        for frame in 1...600 {
            let wall = 50 + Double(frame) / 60
            // A 60 ms correction every two seconds, alternating direction.
            let raw = wall - 50 + (Int((wall - 50) / 2).isMultiple(of: 2) ? 0.06 : -0.06)
            let time = clock.time(wall: wall, raw: raw, playing: true)
            let step = time - last
            XCTAssertGreaterThan(step, 0, "lyrics ran backwards")
            XCTAssertLessThan(abs(step - 1.0 / 60), 0.15 / 60, "pace bent more than 15%")
            last = time
        }
        XCTAssertNil(clock.lastJump)
        _ = clock.time(wall: 61, raw: 95, playing: true)
        XCTAssertEqual(clock.lastJump?.to, 95)
    }

    @MainActor
    func testSeekCrossFadesInsteadOfCutting() {
        let memory = LyricSceneMemory()
        let key = LyricSceneKey(kind: .synced, track: 1, fingerprint: 7)
        let content = LyricSceneContent(lines: [first, second])
        var wall = 200.0
        for _ in 0..<30 {
            wall += 1.0 / 60
            let t = memory.observe(key: key, content: content, time: wall - 190, wall: wall, jump: nil)
            XCTAssertNil(t.outgoing)
            XCTAssertEqual(t.incomingPresence, 1)
        }
        let jump = LyricClock.Jump(from: wall - 190, to: 80, wall: wall + 1.0 / 60)
        var lastOut = 1.0, lastIn = 0.0
        for frame in 1...60 {
            wall += 1.0 / 60
            let t = memory.observe(key: key, content: content, time: 80, wall: wall, jump: jump)
            if frame == 1 {
                XCTAssertNotNil(t.outgoing)
                XCTAssertEqual(t.outgoing?.time ?? 0, jump.from, accuracy: 0.0001)
            }
            XCTAssertLessThanOrEqual(t.outgoingPresence, lastOut + 1e-9)
            XCTAssertGreaterThanOrEqual(t.incomingPresence, lastIn - 1e-9)
            XCTAssertLessThan(abs(t.outgoingPresence - lastOut), 0.2, "outgoing cut")
            XCTAssertLessThan(abs(t.incomingPresence - lastIn), 0.2, "incoming cut")
            lastOut = t.outgoingPresence; lastIn = t.incomingPresence
        }
        XCTAssertEqual(lastIn, 1)
        XCTAssertEqual(lastOut, 0)
    }

    @MainActor
    func testAPlaybackCorrectionCannotAdvanceTheFadingSceneToAnotherLyric() throws {
        let memory = LyricSceneMemory()
        let key = LyricSceneKey(kind: .synced, track: 1, fingerprint: 7)
        let content = LyricSceneContent(lines: [first, second])
        let before = second.start - 0.08
        _ = memory.observe(key: key, content: content, time: before, wall: 200, jump: nil)
        let jump = LyricClock.Jump(from: before, to: before - 0.5, wall: 200.02)
        let transition = memory.observe(key: key, content: content, time: jump.to, wall: 200.22, jump: jump)
        let outgoing = try XCTUnwrap(transition.outgoing)
        XCTAssertGreaterThan(transition.outgoingPresence, 0)
        let time = outgoing.renderTime(at: 200.22)
        XCTAssertEqual(time, second.start.nextDown)
        XCTAssertEqual(LyricLineIndex(lines: content.lines).line(at: time), 0)
    }

    @MainActor
    func testTimingUpgradeUsesOneLyricSheetAtATime() {
        let memory = LyricSceneMemory()
        let line = LyricsFetcher.makeLine(text: "Hold on", start: 10, end: 12)
        var wordTimed = line
        wordTimed.wordTimed = true
        let old = LyricSceneKey(kind: .synced, track: 1, fingerprint: 7)
        let updated = LyricSceneKey(kind: .synced, track: 1, fingerprint: 8)
        _ = memory.observe(key: old, content: LyricSceneContent(lines: [line]), time: 10.7, wall: 200, jump: nil)
        let swap = memory.observe(key: updated, content: LyricSceneContent(lines: [wordTimed]), time: 10.8,
                                  wall: 200.1, jump: nil)
        XCTAssertNil(swap.outgoing)
        XCTAssertEqual(swap.incomingPresence, 1)
    }

    @MainActor
    func testSongChangeKeepsTheVisibleLyricsLeavingThroughATwoStepUpdate() {
        let memory = LyricSceneMemory()
        let old = LyricSceneKey(kind: .synced, track: 1, fingerprint: 7)
        _ = memory.observe(key: old, content: LyricSceneContent(lines: [first]), time: 11, wall: 300, jump: nil)
        _ = memory.observe(key: old, content: LyricSceneContent(lines: [first]), time: 11.02, wall: 300.02, jump: nil)
        // The title changes first, while the old lines are still loaded...
        let renamed = LyricSceneKey(kind: .synced, track: 2, fingerprint: 7)
        let a = memory.observe(key: renamed, content: LyricSceneContent(lines: [first]), time: 0.1, wall: 300.04, jump: nil)
        // ...then the lines are cleared a frame later.
        let loading = LyricSceneKey(kind: .loading, track: 2, fingerprint: 0)
        let b = memory.observe(key: loading, content: LyricSceneContent(), time: 0.12, wall: 300.06, jump: nil)
        XCTAssertEqual(a.outgoing?.key, old)
        XCTAssertEqual(b.outgoing?.key, old, "the scene actually on screen keeps fading out")
        XCTAssertEqual(b.outgoing?.time ?? 0, 11.02, accuracy: 0.0001)
        XCTAssertGreaterThan(b.outgoingPresence, 0.9)
    }

    @MainActor
    func testRepeatedWordsAndLinesNeverMoveIdentically() {
        let chorus = [LyricsFetcher.Word(text: "again", start: 20, end: 20.6),
                      LyricsFetcher.Word(text: "again", start: 41, end: 41.6)]
        XCTAssertNotEqual(chorus[0].presentationID.visualSeed, chorus[1].presentationID.visualSeed)
        let a = vc_word_motion(20.08, 20, 20.6, 0.6, .infinity, 0, chorus[0].presentationID.visualSeed, 0, 0)
        let b = vc_word_motion(41.08, 41, 41.6, 0.6, .infinity, 0, chorus[1].presentationID.visualSeed, 0, 0)
        XCTAssertNotEqual(a.enter, b.enter)
        let driftA = vc_idle_drift(chorus[0].presentationID.visualSeed, 30, 1)
        let driftB = vc_idle_drift(chorus[1].presentationID.visualSeed, 30, 1)
        XCTAssertNotEqual(driftA.y, driftB.y)
    }

    func testMotionFollowsTheVoice() {
        // Held notes swell further and longer than clipped syllables.
        var held = 0.0, clipped = 0.0
        for step in 0..<400 {
            let t = 10 + Double(step) * 0.005
            held = max(held, vc_word_motion(t, 10, 11.6, 1.6, .infinity, 0, 3, 0, 0).heat)
            clipped = max(clipped, vc_word_motion(t, 10, 10.06, 0.1, .infinity, 0, 3, 0, 0).heat)
        }
        XCTAssertGreaterThan(held, clipped + 0.1)
        // Quick words arrive faster than slow ones.
        XCTAssertGreaterThan(vc_word_motion(10.08, 10, 10.15, 0.15, .infinity, 0, 3, 0, 0).enter,
                             vc_word_motion(10.08, 10, 11.5, 1.5, .infinity, 0, 3, 0, 0).enter)
        // Dense delivery reads as more intense than a sparse line.
        let dense = (0..<12).map { LyricsFetcher.Word(text: "w\($0)", start: 10 + Double($0) * 0.18, end: 10 + Double($0) * 0.18 + 0.16) }
        let sparse = (0..<3).map { LyricsFetcher.Word(text: "w\($0)", start: 10 + Double($0) * 1.1, end: 10 + Double($0) * 1.1 + 0.3) }
        XCTAssertGreaterThan(OrganicMotion.vocalIntensity(dense, at: 11), OrganicMotion.vocalIntensity(sparse, at: 11) + 0.1)
    }

    func testReduceMotionKeepsOnlyCrossFades() {
        let calm = vc_word_motion(10.2, 10, 11.6, 1.6, 12, 0.5, 9, 1, 1)
        XCTAssertEqual(calm.x, 0); XCTAssertEqual(calm.y, 0); XCTAssertEqual(calm.angle, 0)
        XCTAssertEqual(calm.scale, 1); XCTAssertEqual(calm.enter, calm.reveal)
        XCTAssertEqual(OrganicMotion.idleAmplitude(composition: .constellation, mood: .neutral, reduceMotion: true), 0)
        let still = vc_idle_drift(9, 40, 0)
        XCTAssertEqual(still.x, 0); XCTAssertEqual(still.y, 0); XCTAssertEqual(still.scale, 1)
    }

    func testEchoRevealIsContinuousAcrossOnsets() {
        var last = EchoBackdrop.revealed(second.words, at: 11.5)
        XCTAssertEqual(last, 0)
        for step in 1...2500 {
            let value = EchoBackdrop.revealed(second.words, at: 11.5 + Double(step) / 1000)
            XCTAssertGreaterThanOrEqual(value, last - 1e-12)
            XCTAssertLessThan(value - last, 0.02)
            last = value
        }
        XCTAssertEqual(last, 1, accuracy: 0.0001)
    }

    func testInterludeDotsWanderIndependentlyAndSmoothly() throws {
        let gap = VCVocalGap(start: 30, end: 46, nextVocal: 4)
        var previous = try XCTUnwrap(gap.instrumentalCue(at: 32, previewLead: 0.5))
        var differs = false
        for step in 1...4000 {
            let cue = try XCTUnwrap(gap.instrumentalCue(at: 32 + Double(step) / 1000, previewLead: 0.5))
            for dot in 0..<3 {
                XCTAssertLessThan(abs(cue.bob(at: dot) - previous.bob(at: dot)), 0.02)
                XCTAssertLessThanOrEqual(abs(cue.bob(at: dot)), 1)
            }
            if abs(cue.bob(at: 0) - cue.bob(at: 1)) > 0.2 { differs = true }
            previous = cue
        }
        XCTAssertTrue(differs, "the dots should not move in lockstep")
    }

    @MainActor
    func testRollingWindowIsReusedUntilAWordJoinsOrLeaves() {
        let cache = LyricLayerCache()
        let lines = [first, second]
        func window(_ time: Double) -> [LyricsFetcher.Word] {
            let key = LyricLayerCache.RollingKey(index: 0, fingerprint: 1, display: .off, lead: 1.2, readings: 0)
            return cache.rolling(key, time: time) {
                LyricLayerCache.rollingWindow(around: 0, lines: lines, romanized: [:], time: time, lead: 1.2, display: .off)
            }.words
        }
        for step in 0..<20 { _ = window(10 + Double(step) * 0.01) }
        XCTAssertEqual(cache.rollingBuilds, 1)
        // The second line's first word joins 1.2 s before it is sung.
        XCTAssertFalse(window(10.4).contains { $0.text == "and" })
        XCTAssertTrue(window(10.6).contains { $0.text == "and" })
        XCTAssertEqual(cache.rollingBuilds, 2)
        // A backwards seek rebuilds rather than reusing a stale window.
        XCTAssertFalse(window(10.2).contains { $0.text == "and" })
    }

    func testRightToLeftAndJoinedScriptsAreRecognised() {
        XCTAssertTrue(LyricScript.isRightToLeft("سلام"))
        XCTAssertTrue(LyricScript.isRightToLeft("שלום"))
        XCTAssertFalse(LyricScript.isRightToLeft("hello"))
        XCTAssertFalse(LyricScript.allowsGlyphMotion("سلام"), "Arabic joins; glyphs must not move apart")
        XCTAssertTrue(LyricScript.allowsGlyphMotion("שלום"))
        XCTAssertTrue(LyricScript.allowsGlyphMotion("LIGHT"))
        XCTAssertFalse(LyricScript.allowsGlyphMotion("नमस्ते"))
        XCTAssertTrue(LyricScript.lineIsRightToLeft(["אני", "אוהב", "you"]))
        XCTAssertFalse(LyricScript.lineIsRightToLeft(["I", "love", "שלום"]))
    }

    @MainActor
    func testHeldArabicWordIsSetAsOneShapedWord() throws {
        // A long-held Arabic word takes the held-word path. It must render
        // exactly as wide as the same word set as one Text: one Text per
        // letter (the old path) breaks joining and changes the width.
        let word = LyricsFetcher.Word(text: "حبيبي", start: 1, end: 3.2)
        let key = ScatteredTypography.Key(words: [word], baseSize: 80, style: .forLine(4), mood: .neutral,
            composition: .scattered, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared))
        let token = try XCTUnwrap(ScatteredTypography.prepare(key, settings: .shared).first)
        XCTAssertGreaterThan(token.sustain, 0.02)
        XCTAssertTrue(token.rightToLeft)
        XCTAssertFalse(token.glyphMotion)
        let line = ScatteredLyricText(words: [word], stageWidth: 900, time: 9, baseSize: 80,
                                      style: .forLine(4), composition: .scattered)
        let plain = Text(token.label).font(token.font).foregroundStyle(.white).fixedSize()
        let lineWidth = try inkWidth(line.padding(60).background(.black))
        let plainWidth = try inkWidth(plain.padding(60).background(.black))
        // A sung held word keeps its 7% swell; per-letter Texts would add
        // isolated letter forms and per-glyph padding on top of that.
        XCTAssertEqual(lineWidth / plainWidth, 1 + 0.07 * token.sustain, accuracy: 0.03)
    }

    @MainActor
    func testRightToLeftLinesStartOnTheRight() throws {
        let layout = FlowLayout(spacing: 4, rightToLeft: true) {
            Color.red.frame(width: 20, height: 20)
            Color.blue.frame(width: 20, height: 20)
        }
        let renderer = ImageRenderer(content: layout.frame(width: 80, height: 20).background(.black))
        renderer.scale = 1
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        func redness(_ x: Int) -> CGFloat { bitmap.colorAt(x: x, y: 10)?.redComponent ?? 0 }
        XCTAssertGreaterThan(redness(bitmap.pixelsWide * 3 / 5), 0.8, "first word sits on the right")
        XCTAssertLessThan(redness(bitmap.pixelsWide * 2 / 5), 0.2)
    }

    @MainActor
    private func inkWidth(_ content: some View) throws -> CGFloat {
        let renderer = ImageRenderer(content: content)
        renderer.scale = 1
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        var low = Int.max, high = Int.min
        for y in 0..<bitmap.pixelsHigh {
            for x in 0..<bitmap.pixelsWide {
                guard let pixel = bitmap.colorAt(x: x, y: y), pixel.brightnessComponent > 0.5 else { continue }
                low = min(low, x); high = max(high, x)
            }
        }
        XCTAssertLessThan(low, high)
        return CGFloat(high - low)
    }
}
