import XCTest
@testable import VersoCustom

/// The seek bar's rules, without a window: fine-scrub gain, snapping to
/// lyric lines, rubber band, where a release seeks, and the drawn structure.
final class ScrubMathTests: XCTestCase {
    private func line(_ start: Double, _ end: Double, _ text: String = "la", instrumental: Bool = false) -> ScrubLine {
        ScrubLine(start: start, end: end, text: text, romanized: nil, isInstrumental: instrumental)
    }

    func testGainStepsByDistanceAndOptionIsFine() {
        XCTAssertEqual(ScrubMath.gain(verticalDistance: 0), 1)
        XCTAssertEqual(ScrubMath.gain(verticalDistance: 39.9), 1)
        XCTAssertEqual(ScrubMath.gain(verticalDistance: -40), 0.5, "Above or below the bar alike")
        XCTAssertEqual(ScrubMath.gain(verticalDistance: 80), 0.25)
        XCTAssertEqual(ScrubMath.gain(verticalDistance: 119), 0.25)
        XCTAssertEqual(ScrubMath.gain(verticalDistance: 500), 0.1)
        XCTAssertEqual(ScrubMath.gain(verticalDistance: 0, fine: true), 0.1)
        XCTAssertEqual(ScrubMath.gain(verticalDistance: .nan), 1)
        XCTAssertNil(ScrubMath.rateLabel(gain: 1))
        XCTAssertEqual(ScrubMath.rateLabel(gain: 0.5), "Half speed")
        XCTAssertEqual(ScrubMath.rateLabel(gain: 0.25), "Quarter speed")
        XCTAssertEqual(ScrubMath.rateLabel(gain: 0.1), "Fine")
    }

    func testRubberBandIsSignedBoundedAndSlowing() {
        XCTAssertEqual(ScrubMath.rubberBand(overshoot: 0), 0)
        let small = ScrubMath.rubberBand(overshoot: 10)
        let large = ScrubMath.rubberBand(overshoot: 1000)
        XCTAssertGreaterThan(small, 0)
        XCTAssertLessThan(small, 10, "Resists from the first point")
        XCTAssertLessThan(large, 40, "Never reaches the dimension")
        XCTAssertGreaterThan(large, 35)
        XCTAssertEqual(ScrubMath.rubberBand(overshoot: -10), -small, accuracy: 1e-9)
        // The iOS formula, c = 0.55, d = 40.
        XCTAssertEqual(ScrubMath.rubberBand(overshoot: 40), (1 - 1 / (0.55 + 1)) * 40, accuracy: 1e-9)
    }

    func testPositionAndTimeRoundTripAndClamp() {
        XCTAssertEqual(ScrubMath.time(x: 228, duration: 200), 100, accuracy: 1e-9)
        XCTAssertEqual(ScrubMath.time(x: -5, duration: 200), 0)
        XCTAssertEqual(ScrubMath.time(x: 9999, duration: 200), 200)
        XCTAssertEqual(ScrubMath.x(time: 50, duration: 200), 114, accuracy: 1e-9)
        XCTAssertEqual(ScrubMath.time(x: 10, duration: 0), 0)
    }

    func testSnapTargetsSkipInstrumentalAndAddStart() {
        let lines = [line(5, 8), line(9, 10, instrumental: true), line(12, 14), line(12.01, 13), line(300, 301)]
        XCTAssertEqual(ScrubMath.snapTargets(lines: lines, duration: 200), [0, 5, 12])
    }

    func testSnapRadiusSpeedGateCommandAndGain() {
        let targets = [0.0, 10, 20]
        let pps = 456.0 / 200 // 2.28 pt/s
        // 6 pt away from 10 s: inside the 7 pt radius.
        let near = 10 + 6 / pps
        XCTAssertEqual(ScrubMath.snap(time: near, targets: targets, pointsPerSecond: pps), 1)
        XCTAssertNil(ScrubMath.snap(time: 10 + 8 / pps, targets: targets, pointsPerSecond: pps), "Outside the radius")
        XCTAssertNil(ScrubMath.snap(time: near, targets: targets, pointsPerSecond: pps, speed: 300), "A fast sweep isn't sticky")
        XCTAssertEqual(ScrubMath.snap(time: near, targets: targets, pointsPerSecond: pps, speed: 200), 1)
        XCTAssertNil(ScrubMath.snap(time: near, targets: targets, pointsPerSecond: pps, disabled: true), "⌘ turns it off")
        XCTAssertNil(ScrubMath.snap(time: near, targets: targets, pointsPerSecond: pps, gain: 0.25), "Fine modes barely snap")
        XCTAssertEqual(ScrubMath.snap(time: 10 + 1.5 / pps, targets: targets, pointsPerSecond: pps, gain: 0.25), 1)
    }

    func testSnapRadiusIsCappedByNeighbours() {
        // Lines 2 s apart at 2.28 pt/s: 4.56 pt apart, so the cap is 1.82 pt.
        let pps = 2.28
        let targets = [0.0, 100, 102]
        XCTAssertNil(ScrubMath.snap(time: 100 + 2.5 / pps, targets: targets, pointsPerSecond: pps))
        XCTAssertEqual(ScrubMath.snap(time: 100 + 1.5 / pps, targets: targets, pointsPerSecond: pps), 1)
    }

    func testCommitTargetPreRollsSnapsAndGuardsTheEnd() {
        XCTAssertEqual(ScrubMath.commitTarget(time: 42, duration: 200), 42)
        XCTAssertEqual(ScrubMath.commitTarget(time: 42, snappedTo: 42, duration: 200), 41.8, accuracy: 1e-9)
        XCTAssertEqual(ScrubMath.commitTarget(time: 0.1, snappedTo: 0.1, duration: 200), 0, "Never before the start")
        XCTAssertEqual(ScrubMath.commitTarget(time: 200, duration: 200), 199.5, "Never so close to the end Music skips")
        XCTAssertEqual(ScrubMath.commitTarget(time: -3, duration: 200), 0)
        XCTAssertEqual(ScrubMath.commitTarget(time: .nan, duration: 200), 0)
    }

    func testTicksAreThinnedAndSkipTheStart() {
        let ticks = ScrubMath.ticks(targets: [0, 10, 10.5, 11, 20], duration: 456, width: 456)
        XCTAssertEqual(ticks, [10, 20], "One per 4 pt at 1 pt/s; 0:00 is not a line")
    }

    func testSectionBreaksAfterLongGapsAndInstrumentals() {
        let lines = [line(5, 8), line(9, 12), line(20, 22), line(23, 30, instrumental: true), line(31, 33), line(34, 35)]
        XCTAssertEqual(ScrubMath.sectionBreaks(lines: lines, duration: 200), [20, 31])
        XCTAssertEqual(ScrubMath.sectionBreaks(lines: [line(1, 2)], duration: 200), [])
    }

    func testRepeatedLinesMakeSpans() {
        let lines = [line(5, 8, "Verse one"), line(9, 12, "Oh, the evening"), line(12.5, 15, "knows my name"),
                     line(20, 23, "Verse two"), line(30, 33, "Oh the EVENING!"), line(33.5, 36, "Knows my name"),
                     line(45, 48, "a lone repeat"), line(60, 62, "a lone repeat")]
        XCTAssertEqual(ScrubMath.repeatedSpans(lines: lines, duration: 200), [9...15, 30...36],
                       "Runs of two or more returning lines; punctuation and case ignored; a single line is not a span")
        XCTAssertEqual(ScrubMath.repeatedSpans(lines: [], duration: 200), [])
    }

    func testLyricAtTime() {
        let lines = [line(5, 8, "one"), line(9, 12, "two"), line(13, 20, instrumental: true), line(21, 23, "three"), line(40, 42, "four")]
        XCTAssertEqual(ScrubMath.lyric(at: 2, lines: lines), .instrumental, "An intro of 3 s or more")
        XCTAssertEqual(ScrubMath.lyric(at: 6, lines: lines), .line(0))
        XCTAssertEqual(ScrubMath.lyric(at: 12.5, lines: lines), .line(1), "Just after its end, still that line")
        XCTAssertEqual(ScrubMath.lyric(at: 15, lines: lines), .instrumental)
        XCTAssertEqual(ScrubMath.lyric(at: 30, lines: lines), .instrumental, "A long gap after a line")
        XCTAssertEqual(ScrubMath.lyric(at: 41, lines: lines), .line(4))
        XCTAssertEqual(ScrubMath.lyric(at: 0.5, lines: [line(1, 2, "x")]), .none, "A short intro shows nothing")
        XCTAssertEqual(ScrubMath.lyric(at: 1, lines: []), .none)
    }

    func testAdjacentLineWalksThroughPreRolledSeeks() {
        let targets = [0.0, 10, 20, 30]
        XCTAssertEqual(ScrubMath.adjacentLine(from: 12, targets: targets, forward: true), 20)
        // After a seek to 20 - 0.2 the next press still moves on.
        XCTAssertEqual(ScrubMath.adjacentLine(from: 19.8, targets: targets, forward: true), 30)
        XCTAssertNil(ScrubMath.adjacentLine(from: 31, targets: targets, forward: true))
        XCTAssertEqual(ScrubMath.adjacentLine(from: 25, targets: targets, forward: false), 20, "Back to this line's start first")
        XCTAssertEqual(ScrubMath.adjacentLine(from: 20.5, targets: targets, forward: false), 10)
        XCTAssertEqual(ScrubMath.adjacentLine(from: 19.8, targets: targets, forward: false), 10)
        XCTAssertEqual(ScrubMath.adjacentLine(from: 0.5, targets: targets, forward: false), 0)
    }

    func testKeyboardStepsAndJumps() {
        XCTAssertEqual(ScrubMath.stepped(3, by: -5, duration: 200), 0)
        XCTAssertEqual(ScrubMath.stepped(198, by: 5, duration: 200), 199.5)
        XCTAssertEqual(ScrubMath.percentJump(digit: 5, duration: 200), 100)
        XCTAssertEqual(ScrubMath.percentJump(digit: 0, duration: 200), 0)
        XCTAssertNil(ScrubMath.percentJump(digit: 10, duration: 200))
        XCTAssertNil(ScrubMath.percentJump(digit: 3, duration: 0))
    }

    func testHeldTimeCoversOneStaleRedraw() {
        XCTAssertEqual(ScrubMath.displayedTime(playback: 10, held: 60, heldSince: 0, now: 0.2), 60)
        XCTAssertEqual(ScrubMath.displayedTime(playback: 60.3, held: 60, heldSince: 0, now: 0.2), 60.3, "Close enough: the clock")
        XCTAssertEqual(ScrubMath.displayedTime(playback: 10, held: 60, heldSince: 0, now: 0.7), 10, "No more than 600 ms")
        XCTAssertEqual(ScrubMath.displayedTime(playback: 10, held: nil, heldSince: 0, now: 0.1), 10)
    }

    func testWords() {
        XCTAssertEqual(ScrubMath.timestamp(65.9), "1:05")
        XCTAssertEqual(ScrubMath.timestamp(3723), "1:02:03")
        XCTAssertEqual(ScrubMath.timestamp(-1), "0:00")
        XCTAssertEqual(ScrubMath.trailingLabel(elapsed: 83, duration: 213, showsRemaining: true), "-2:10")
        XCTAssertEqual(ScrubMath.trailingLabel(elapsed: 83, duration: 213, showsRemaining: false), "3:33")
        XCTAssertEqual(ScrubMath.trailingLabel(elapsed: 83, duration: 0, showsRemaining: true), "")
        XCTAssertEqual(ScrubMath.accessibilityValue(elapsed: 83, duration: 213, line: "hello"), "1:23 of 3:33, line: hello")
    }

    func testScrubLinesTakeOffTheOffsetAndDropCredits() {
        var credit = LyricsFetcher.Line(start: 0, end: 1, text: "Produced by: X", words: [], isInstrumental: true)
        credit.credit = true
        let sung = LyricsFetcher.Line(start: 10.5, end: 12, text: "hello", words: [], isInstrumental: false)
        let lines = ScrubLine.lines(from: [credit, sung], offset: 0.5, romanized: [1: "harou"])
        XCTAssertEqual(lines, [ScrubLine(start: 10, end: 11.5, text: "hello", romanized: "harou", isInstrumental: false)])
    }
}

final class ScrubSessionTests: XCTestCase {
    private let targets = [0.0, 50, 100]
    private func session(x: Double, knob: Double, y: Double = 0, now: Double = 0, command: Bool = false) -> ScrubSession {
        ScrubSession(x: x, y: y, knobX: knob, width: 456, duration: 200, trackKey: "a", targets: targets, now: now, command: command)
    }

    func testPressOnTheKnobKeepsTheGrabOffset() {
        var drag = session(x: 104, knob: 100)
        XCTAssertTrue(drag.grabbedKnob)
        XCTAssertEqual(drag.clamped, 100, "No jump")
        drag.move(x: 154, y: 0, now: 1)
        XCTAssertEqual(drag.clamped, 150, accuracy: 1e-9)
    }

    func testPressElsewhereJumpsThenFollowsOneToOne() {
        var drag = session(x: 300, knob: 100)
        XCTAssertFalse(drag.grabbedKnob)
        XCTAssertEqual(drag.clamped, 300)
        drag.move(x: 320, y: 0, now: 1)
        XCTAssertEqual(drag.clamped, 320)
    }

    func testFineScrubMovesByTheGainedDeltaWithoutJumping() {
        var drag = session(x: 300, knob: 100)
        drag.move(x: 300, y: 50, now: 1) // half speed, no horizontal move yet
        XCTAssertEqual(drag.clamped, 300, "Changing speed never jumps")
        drag.move(x: 340, y: 50, now: 2)
        XCTAssertEqual(drag.clamped, 320, accuracy: 1e-9)
        XCTAssertEqual(drag.rateLabel, "Half speed")
        drag.move(x: 380, y: 200, now: 3)
        XCTAssertEqual(drag.clamped, 324, accuracy: 1e-9, "0.1× far away")
        drag.move(x: 400, y: 0, now: 4, option: true)
        XCTAssertEqual(drag.clamped, 326, accuracy: 1e-9, "⌥ is fine at any distance")
        // Back at the bar: the knob catches up with the pointer.
        drag.move(x: 400, y: 0, now: 5)
        XCTAssertEqual(drag.clamped, 400)
        XCTAssertTrue(drag.caughtUp)
    }

    func testEndsClampRubberBandAndAreFeltOnce() {
        var drag = session(x: 440, knob: 0)
        var events = drag.move(x: 470, y: 0, now: 1)
        XCTAssertEqual(drag.clamped, 456)
        XCTAssertEqual(events, [.reachedEnd])
        XCTAssertGreaterThan(drag.overshoot, 0)
        XCTAssertLessThan(drag.overshoot, 14)
        events = drag.move(x: 520, y: 0, now: 2)
        XCTAssertEqual(events, [], "Once, on arrival")
        drag.move(x: 400, y: 0, now: 3)
        events = drag.move(x: 460, y: 0, now: 4)
        XCTAssertEqual(events, [.reachedEnd], "Again after leaving")
        // Slow modes stop at the end without stretching.
        var fine = session(x: 450, knob: 0)
        fine.move(x: 450, y: 100, now: 1)
        fine.move(x: 600, y: 100, now: 2)
        XCTAssertEqual(fine.clamped, 456)
        XCTAssertEqual(fine.overshoot, 0)
        XCTAssertEqual(fine.commit(at: 2), 199.5, "Never the very end")
    }

    func testSnapsWhenSlowFeltOnceAndCommandLetsGo() {
        let fiftyX = 114.0
        var drag = session(x: 300, knob: 0)
        // A slow approach: 2 pt per 100 ms = 20 pt/s.
        var felt = 0
        var x = 130.0
        var t = 0.0
        while x > fiftyX - 4 {
            x -= 2
            t += 0.1
            felt += drag.move(x: x, y: 0, now: t).filter { $0 == .snapped }.count
        }
        XCTAssertEqual(felt, 1, "One alignment haptic on entering the snap")
        XCTAssertEqual(drag.snappedTime, 50)
        XCTAssertEqual(drag.displayX, fiftyX, accuracy: 1e-9)
        XCTAssertEqual(drag.commit(at: t) ?? 0, 49.8, accuracy: 1e-9, "Lands just before the line")
        drag.modifiersChanged(option: false, command: true, now: t + 0.1)
        XCTAssertNil(drag.snapped, "⌘ disables snapping")
        XCTAssertEqual(drag.commit(at: t + 0.1) ?? 0, ScrubMath.time(x: x, duration: 200), accuracy: 1e-9)
    }

    func testFastSweepDoesNotSnap() {
        var drag = session(x: 300, knob: 0)
        drag.move(x: 200, y: 0, now: 0.1)
        drag.move(x: 115, y: 0, now: 0.2) // ~850 pt/s
        XCTAssertNil(drag.snapped)
    }

    func testSnapHapticsAreRateLimited() {
        // Two targets 1 pt apart are never both felt within 90 ms.
        var drag = ScrubSession(x: 300, y: 0, knobX: 0, width: 456, duration: 456, trackKey: "a",
                                targets: [0, 100, 110], now: 0)
        var events = drag.move(x: 101, y: 0, now: 10)
        XCTAssertEqual(events, [.snapped])
        events = drag.move(x: 109, y: 0, now: 10.05)
        XCTAssertEqual(drag.snappedTime, 110)
        XCTAssertEqual(events, [], "Within 90 ms of the last")
    }

    func testClicks() {
        let onKnob = session(x: 101, knob: 100)
        XCTAssertNil(onKnob.commit(at: 0.1), "A click on the knob is not a seek")
        let onBar = session(x: 200, knob: 100)
        XCTAssertTrue(onBar.isClick(at: 0.1))
        XCTAssertNil(onBar.snapped)
        XCTAssertEqual(onBar.commit(at: 0.1) ?? 0, ScrubMath.time(x: 200, duration: 200), accuracy: 1e-9)
        let snappy = session(x: 116, knob: 300)
        XCTAssertEqual(snappy.snappedTime, 50, "A click near a line lands on it")
        XCTAssertEqual(snappy.commit(at: 0.1) ?? 0, 49.8, accuracy: 1e-9)
        XCTAssertNil(session(x: 116, knob: 300, command: true).snapped)
    }
}

final class HoverIntentTests: XCTestCase {
    private func point(_ x: Double, _ y: Double = 0) -> CGPoint { CGPoint(x: x, y: y) }

    func testSlowingDownRevealsOnTheSecondSlowPoll() {
        var intent = HoverIntent()
        XCTAssertFalse(intent.sample(point(0), at: 0, inHotZone: false))
        XCTAssertFalse(intent.sample(point(2), at: 0.045, inHotZone: true), "One slow poll is not enough")
        XCTAssertTrue(intent.isPending)
        XCTAssertTrue(intent.sample(point(4), at: 0.09, inHotZone: true))
    }

    func testFlyThroughNeverReveals() {
        var intent = HoverIntent()
        var x = 0.0, t = 0.0
        _ = intent.sample(point(x), at: t, inHotZone: false)
        for _ in 0..<20 {
            x += 60 // 1333 pt/s
            t += 0.045
            XCTAssertFalse(intent.sample(point(x), at: t, inHotZone: true))
        }
        XCTAssertFalse(intent.isPending)
    }

    func testDwellRevealsAModeratePointer() {
        var intent = HoverIntent()
        var x = 0.0, t = 0.0
        _ = intent.sample(point(x), at: t, inHotZone: false)
        var revealedAt: Double?
        for _ in 0..<10 where revealedAt == nil {
            x += 20 // 444 pt/s: not slow, not a fly-through
            t += 0.045
            if intent.sample(point(x), at: t, inHotZone: true) { revealedAt = t }
        }
        let first = 0.045
        XCTAssertEqual(revealedAt ?? 0, first + 0.225, accuracy: 0.05)
    }

    func testLeavingTheZoneOrAFastBurstStartsOver() {
        var intent = HoverIntent()
        _ = intent.sample(point(0), at: 0, inHotZone: true)
        XCTAssertFalse(intent.sample(point(1), at: 0.045, inHotZone: false))
        XCTAssertFalse(intent.isPending)
        _ = intent.sample(point(2), at: 0.09, inHotZone: true)
        XCTAssertFalse(intent.sample(point(100), at: 0.135, inHotZone: true), "2000 pt/s")
        XCTAssertFalse(intent.isPending)
    }

    func testDecide() {
        XCTAssertNil(HoverState.decide(proposed: 1, previous: nil, intentReady: false, holds: false), "Waits for intent")
        XCTAssertEqual(HoverState.decide(proposed: 1, previous: nil, intentReady: true, holds: false), 1)
        XCTAssertEqual(HoverState.decide(proposed: 1, previous: 1, intentReady: false, holds: false), 1, "Already shown")
        XCTAssertNil(HoverState.decide(proposed: nil, previous: 1, intentReady: false, holds: false))
        XCTAssertEqual(HoverState.decide(proposed: nil, previous: 1, intentReady: false, holds: true), 1, "Held up")
        XCTAssertEqual(HoverState.decide(proposed: 2, previous: 1, intentReady: false, holds: true), 1,
                       "No cross-display reveal while held")
        XCTAssertNil(HoverState.decide(proposed: 2, previous: 1, intentReady: false, holds: false))
    }

    func testDismissGuards() {
        XCTAssertTrue(PlayerDismissGuard.holds(dragging: true, searchFocused: false, lastClick: nil, now: 5, overWindow: true))
        XCTAssertFalse(PlayerDismissGuard.holds(dragging: false, searchFocused: true, lastClick: 4.5, now: 5, overWindow: true),
                       "A window always wins outside a drag")
        XCTAssertTrue(PlayerDismissGuard.holds(dragging: false, searchFocused: true, lastClick: nil, now: 5, overWindow: false))
        XCTAssertTrue(PlayerDismissGuard.holds(dragging: false, searchFocused: false, lastClick: 4, now: 5, overWindow: false))
        XCTAssertFalse(PlayerDismissGuard.holds(dragging: false, searchFocused: false, lastClick: 3.7, now: 5, overWindow: false))
        XCTAssertFalse(PlayerDismissGuard.holds(dragging: false, searchFocused: false, lastClick: nil, now: 5, overWindow: false))
    }

    func testPollIsQuickWhileShownOrPending() {
        XCTAssertEqual(HoverState.pollInterval(stillFor: 0, shown: true, pending: false), 0.045)
        XCTAssertEqual(HoverState.pollInterval(stillFor: 60, shown: false, pending: true), 0.045)
        XCTAssertEqual(HoverState.pollInterval(stillFor: 0, shown: false, pending: false), 0.09)
        XCTAssertEqual(HoverState.pollInterval(stillFor: 60, shown: false, pending: false), HoverState.restingInterval)
    }
}

final class PlayerKeyMapTests: XCTestCase {
    private typealias K = PlayerKeyMap.KeyCode
    private func action(_ code: UInt16, _ chars: String = "", command: Bool = false, option: Bool = false,
                        control: Bool = false, editing: Bool = false, results: Bool = false) -> PlayerKeyAction? {
        PlayerKeyMap.action(keyCode: code, characters: chars, command: command, option: option, control: control,
                            editingText: editing, hasResults: results)
    }

    func testTransportAndSeekKeys() {
        XCTAssertEqual(action(K.space, " "), .playPause)
        XCTAssertEqual(action(K.left), .seekBy(-5))
        XCTAssertEqual(action(K.right), .seekBy(5))
        XCTAssertEqual(action(K.left, option: true), .line(forward: false))
        XCTAssertEqual(action(K.right, option: true), .line(forward: true))
        XCTAssertEqual(action(K.left, command: true), .track(forward: false))
        XCTAssertEqual(action(K.right, command: true), .track(forward: true))
        XCTAssertEqual(action(29, "0"), .jump(digit: 0))
        XCTAssertEqual(action(25, "9"), .jump(digit: 9))
        XCTAssertEqual(action(3, "f", command: true), .focusSearch)
        XCTAssertEqual(action(44, "/"), .focusSearch)
        XCTAssertEqual(action(K.escape), .escape)
    }

    func testTypingBelongsToTheSearchField() {
        XCTAssertNil(action(K.space, " ", editing: true), "Space types a space")
        XCTAssertNil(action(K.left, editing: true), "Arrows move the caret")
        XCTAssertNil(action(29, "0", editing: true))
        XCTAssertNil(action(44, "/", editing: true))
        XCTAssertEqual(action(K.escape, editing: true), .escape)
        XCTAssertEqual(action(K.down, editing: true, results: true), .moveSelection(1))
        XCTAssertEqual(action(K.up, editing: true, results: true), .moveSelection(-1))
        XCTAssertEqual(action(K.returnKey, editing: true, results: true), .playSelection)
        XCTAssertNil(action(K.returnKey, editing: true, results: false))
        XCTAssertNil(action(K.left, command: true, editing: true), "⌘← moves to the line start in text")
    }

    func testSystemShortcutsAreLeftAlone() {
        XCTAssertNil(action(12, "q", command: true))
        XCTAssertNil(action(K.space, " ", control: true))
        XCTAssertNil(action(K.space, " ", option: true))
        XCTAssertNil(action(0, "a"))
    }
}

final class PlayerOptimismTests: XCTestCase {
    func testPlayPauseFlipsAtOnceAndSettles() {
        var toggle = OptimisticToggle()
        XCTAssertTrue(toggle.displayed(actual: true, now: 0))
        toggle.press(actual: true, now: 0)
        XCTAssertFalse(toggle.displayed(actual: true, now: 0.1), "Shows paused before the player answers")
        toggle.reconcile(actual: false, now: 0.4)
        XCTAssertNil(toggle.promised)
        XCTAssertFalse(toggle.displayed(actual: false, now: 0.5))
    }

    func testPlayPauseFallsBackWhenThePlayerDoesNotChange() {
        var toggle = OptimisticToggle()
        toggle.press(actual: false, now: 0)
        XCTAssertTrue(toggle.displayed(actual: false, now: 1.4))
        XCTAssertFalse(toggle.displayed(actual: false, now: 1.6), "Back to the truth after 1.5 s")
        toggle.reconcile(actual: false, now: 1.6)
        XCTAssertNil(toggle.promised)
    }

    func testDoublePressCancelsOut() {
        var toggle = OptimisticToggle()
        toggle.press(actual: true, now: 0)
        toggle.press(actual: true, now: 0.1)
        XCTAssertTrue(toggle.displayed(actual: true, now: 0.2))
    }

    private func item(_ id: String, _ index: Int, current: Bool = false, pid: String? = nil) -> PlayerQueueItem {
        PlayerQueueItem(id: id, title: "t\(id)", artist: "a", index: index, isCurrent: current, persistentID: pid ?? id)
    }

    func testOptimisticNextOnlyWhenTheNextSongIsKnown() {
        let queue = [item("1", 1), item("2", 2, current: true), item("3", 3)]
        XCTAssertEqual(DesktopPlayerModel.optimisticNext(queue: queue, shuffleOn: false, kind: .appleMusic), "3")
        XCTAssertNil(DesktopPlayerModel.optimisticNext(queue: queue, shuffleOn: true, kind: .appleMusic), "Shuffle: unknown")
        XCTAssertNil(DesktopPlayerModel.optimisticNext(queue: queue, shuffleOn: false, kind: .spotify))
        XCTAssertNil(DesktopPlayerModel.optimisticNext(queue: Array(queue.prefix(2)), shuffleOn: false, kind: .appleMusic))
        var stream = queue
        stream[2].persistentID = nil
        XCTAssertNil(DesktopPlayerModel.optimisticNext(queue: stream, shuffleOn: false, kind: .appleMusic))
    }

    func testPlaylistChange() {
        let a = [item("1", 1), item("2", 2), item("3", 3)]
        let advanced = [item("2", 2), item("3", 3), item("4", 4)]
        XCTAssertFalse(DesktopPlayerModel.playlistChanged(from: a, to: advanced), "The same playlist, moved on")
        XCTAssertTrue(DesktopPlayerModel.playlistChanged(from: a, to: [item("9", 2), item("8", 3)]))
        XCTAssertTrue(DesktopPlayerModel.playlistChanged(from: [], to: a), "First read")
        XCTAssertTrue(DesktopPlayerModel.playlistChanged(from: a, to: [item("7", 50)]), "No overlap")
    }

    @MainActor
    func testShuffleNoteShowsOncePerChange() {
        let suite = "LunalyrPlayerTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let model = DesktopPlayerModel(defaults: defaults)
        XCTAssertFalse(model.takeShuffleNote(), "Shuffle off: no note")
        XCTAssertTrue(model.showsRemaining, "Remaining by default")
        model.showsRemaining = false
        XCTAssertEqual(defaults.string(forKey: DesktopPlayerModel.timeModeKey), "total")
        XCTAssertFalse(DesktopPlayerModel(defaults: defaults).showsRemaining, "Remembered")
    }
}

@MainActor
final class ScrubControllerTests: XCTestCase {
    private var seeks: [Double] = []
    private var haptics = 0
    private var clock = 100.0
    private var position = 30.0

    override func tearDown() {
        PlayerInteraction.shared.dragging = false
        PlayerInteraction.shared.lastClick = nil
        super.tearDown()
    }

    private func controller(lines: [ScrubLine] = []) -> ScrubController {
        let controller = ScrubController(displayID: 1, seek: { [unowned self] in self.seeks.append($0) },
                                         playback: { [unowned self] in self.position },
                                         haptics: PlayerHaptics { [unowned self] in self.haptics += 1 },
                                         now: { [unowned self] in self.clock }, reduceMotion: { true },
                                         previewsOnWallpaper: false)
        controller.update(.init(duration: 200, trackKey: "song", lines: lines, canSeek: true))
        return controller
    }

    func testOneSeekOnReleaseNoneWhileDragging() {
        let scrub = controller()
        scrub.mouseDown(x: 100, y: 0)
        XCTAssertTrue(PlayerInteraction.shared.dragging, "Holds the player up")
        for step in 1...20 {
            clock += 0.016
            scrub.mouseDragged(x: 100 + Double(step) * 5, y: 0)
        }
        XCTAssertEqual(seeks, [], "No Apple events while the hand moves")
        clock += 0.016
        scrub.mouseUp(x: 228, y: 0)
        XCTAssertEqual(seeks.count, 1)
        XCTAssertEqual(seeks[0], 100, accuracy: 1e-9)
        XCTAssertFalse(PlayerInteraction.shared.dragging)
        XCTAssertEqual(scrub.displayedTime(playback: 30), 100, "Holds the target over a stale redraw")
        clock += 0.7
        XCTAssertEqual(scrub.displayedTime(playback: 30), 30)
    }

    func testEscapeCancelsWithoutSeeking() {
        let scrub = controller()
        scrub.mouseDown(x: 300, y: 0)
        scrub.mouseDragged(x: 350, y: 0)
        scrub.cancel()
        XCTAssertEqual(seeks, [])
        XCTAssertNil(scrub.session)
        XCTAssertEqual(scrub.displayedTime(playback: 30), 30)
    }

    func testANewSongCancelsTheDragSilently() {
        let scrub = controller()
        scrub.mouseDown(x: 300, y: 0)
        scrub.update(.init(duration: 180, trackKey: "other", lines: [], canSeek: true))
        XCTAssertNil(scrub.session)
        scrub.mouseUp(x: 300, y: 0)
        XCTAssertEqual(seeks, [], "Never seeks the new song to the old song's place")
    }

    func testHapticsOnlyForEndsAndSnaps() {
        let lines = [ScrubLine(start: 50, end: 55, text: "a", romanized: nil, isInstrumental: false)]
        let scrub = controller(lines: lines)
        scrub.mouseDown(x: 200, y: 0)
        clock += 0.1
        scrub.mouseDragged(x: 150, y: 0) // fast: no snap
        for step in 1...20 {
            clock += 0.1
            scrub.mouseDragged(x: 150 - Double(step) * 2, y: 0)
        }
        XCTAssertEqual(haptics, 1, "Snapped onto the line once")
        clock += 0.1
        scrub.mouseDragged(x: -20, y: 0)
        XCTAssertEqual(haptics, 2, "Reached the start")
        scrub.mouseUp(x: -20, y: 0)
        XCTAssertEqual(seeks, [0])
    }

    func testKeysSeekByLineAndStep() {
        let lines = [10.0, 20, 30].map { ScrubLine(start: $0, end: $0 + 2, text: "l", romanized: nil, isInstrumental: false) }
        let scrub = controller(lines: lines)
        position = 12
        scrub.seekLine(forward: true)
        XCTAssertEqual(seeks.last ?? 0, 19.8, accuracy: 1e-9)
        XCTAssertEqual(scrub.flash ?? 0, 19.8, accuracy: 1e-9, "The time capsule shows")
        // The next press starts from the held target, not a stale clock.
        scrub.seekLine(forward: true)
        XCTAssertEqual(seeks.last ?? 0, 29.8, accuracy: 1e-9)
        scrub.seek(by: -5)
        XCTAssertEqual(seeks.last ?? 0, 24.8, accuracy: 1e-9)
        scrub.jump(digit: 5)
        XCTAssertEqual(seeks.last, 100)
        let plain = controller()
        position = 50
        plain.seekLine(forward: false)
        XCTAssertEqual(seeks.last, 40, "Without lyrics, 10 s")
    }

    func testCannotSeekDoesNothing() {
        let scrub = controller()
        scrub.update(.init(duration: 200, trackKey: "song", lines: [], canSeek: false))
        scrub.mouseDown(x: 100, y: 0)
        XCTAssertNil(scrub.session)
        scrub.seek(by: 5)
        XCTAssertEqual(seeks, [])
    }

    func testHoverSnapsAsAClickWould() {
        let lines = [ScrubLine(start: 50, end: 55, text: "a", romanized: nil, isInstrumental: false)]
        let scrub = controller(lines: lines)
        scrub.pointerMoved(x: 116, y: 0)
        XCTAssertEqual(scrub.hover?.time, 50)
        XCTAssertTrue(scrub.activity.hovering)
        scrub.pointerExited()
        XCTAssertNil(scrub.hover)
        XCTAssertFalse(scrub.activity.hovering)
    }

    func testRowKinds() {
        var track = NowPlayingMonitor.Track(title: "a", artist: "b", duration: 200)
        XCTAssertEqual(ScrubRowKind.of(track: track, canSeek: true), .seekable)
        XCTAssertEqual(ScrubRowKind.of(track: track, canSeek: false), .readOnly)
        track.duration = 0
        XCTAssertEqual(ScrubRowKind.of(track: track, canSeek: true), .elapsedOnly)
        track.isStream = true
        XCTAssertEqual(ScrubRowKind.of(track: track, canSeek: true), .live)
        track.isAdvert = true
        XCTAssertEqual(ScrubRowKind.of(track: track, canSeek: true), .advert)
        XCTAssertEqual(ScrubRowKind.of(track: nil, canSeek: true), .hidden)
    }

    func testWallpaperLyricsClearUnderADrag() {
        XCTAssertEqual(PlayerHoverFocus.look(hovered: false, previewing: false).opacity, 1)
        XCTAssertEqual(PlayerHoverFocus.look(hovered: true, previewing: false).blur, 22)
        XCTAssertLessThan(PlayerHoverFocus.look(hovered: true, previewing: true).blur, 22)
        XCTAssertGreaterThan(PlayerHoverFocus.look(hovered: true, previewing: true).opacity, 0.3)
    }

    func testScrubPreviewIsPerDisplay() {
        let preview = ScrubPreview.shared
        preview.show(42, on: 7)
        XCTAssertEqual(preview.songTime(on: 7), 42)
        XCTAssertNil(preview.songTime(on: 8))
        XCTAssertNil(preview.songTime(on: nil))
        preview.clear(on: 8)
        XCTAssertEqual(preview.songTime(on: 7), 42)
        preview.clear(on: 7)
        XCTAssertNil(preview.songTime(on: 7))
    }
}
