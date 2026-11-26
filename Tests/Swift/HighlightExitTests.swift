import SwiftUI
import XCTest
@testable import VersoCustom

/// How a highlight leaves (`LyricHighlightTiming`): tile and text as one
/// object, eased in and out over about 0.42 s, with no pop, no colour
/// snapping back, and opacity only under Reduce Motion.
final class HighlightExitTests: XCTestCase {
    private typealias Timing = LyricHighlightTiming
    private let onset = 10.0, release = 13.6
    /// One frame at 30 fps, the slowest the stage draws at.
    private let frame = 1.0 / 30

    private func pose(_ time: Double, reduce: Bool = false) -> Timing.Pose {
        Timing.pose(time: time, onset: onset, release: release, reduceMotion: reduce)
    }

    func testTheExitIsEasedAndAboutFourHundredMilliseconds() {
        XCTAssertGreaterThanOrEqual(Timing.exitDuration, 0.35)
        XCTAssertLessThanOrEqual(Timing.exitDuration, 0.5)
        XCTAssertEqual(Timing.exitProgress(-1), 0)
        XCTAssertEqual(Timing.exitProgress(0), 0)
        XCTAssertEqual(Timing.exitProgress(Timing.exitDuration), 1)
        XCTAssertEqual(Timing.exitProgress(Timing.exitDuration / 2), 0.5, accuracy: 1e-9)
        XCTAssertEqual(Timing.exitProgress(.infinity), 1)
        XCTAssertEqual(Timing.exitProgress(-.infinity), 0)
        // Eased in and out: it leaves rest and arrives at gone slowly.
        XCTAssertLessThan(Timing.exitProgress(frame), 0.01, "no jump on the first frame")
        XCTAssertGreaterThan(Timing.exitProgress(Timing.exitDuration - frame), 0.99, "no cut on the last frame")
    }

    func testTheTileFallsSteadilyWithNoPop() {
        // Held whole to the release.
        XCTAssertEqual(pose(release - 0.001), Timing.Pose(opacity: 1, width: 1))
        var previous = pose(release)
        XCTAssertEqual(previous.opacity, 1, accuracy: 1e-9)
        var largestStep = 0.0
        for step in 1...600 {
            let time = release + Timing.exitDuration * 1.2 * Double(step) / 600
            let current = pose(time)
            XCTAssertLessThanOrEqual(current.opacity, previous.opacity + 1e-12, "monotone at \(time)")
            XCTAssertLessThanOrEqual(current.leaving, 1)
            if current.isVisible {
                XCTAssertLessThanOrEqual(current.scale, previous.scale + 1e-12, "it only shrinks")
                XCTAssertGreaterThanOrEqual(current.blur, previous.blur - 1e-12, "it only softens")
                XCTAssertEqual(current.width, 1, "it never folds back to its leading edge")
            }
            previous = current
        }
        for time in stride(from: release - frame, through: release + Timing.exitDuration + frame, by: frame / 4) {
            largestStep = max(largestStep, pose(time).opacity - pose(time + frame).opacity)
        }
        // Smootherstep at its steepest: 1.875 / duration a second.
        XCTAssertLessThan(largestStep, 0.16, "no frame drops more than about a sixth")
        XCTAssertFalse(pose(release + Timing.exitDuration).isVisible, "gone when the exit ends")
        // It shrinks and softens only a little, about its centre.
        let gone = Timing.Pose.leaving(1, reduceMotion: false)
        XCTAssertEqual(gone.scale, 1 - Timing.exitScale, accuracy: 1e-9)
        XCTAssertGreaterThan(gone.scale, 0.9)
        XCTAssertLessThan(gone.blur, 0.1)
    }

    func testTheTextLeavesWithItsTile() {
        // Whatever the ink's switch, the word wears the tile's colour while
        // the tile is whole, its own once the tile has gone, and eases from
        // one to the other on the tile's own curve -- never in one frame.
        for switchAt in stride(from: 0.05, through: 0.95, by: 0.05) {
            var previous = 1.0
            var largestStep = 0.0
            var started: Double?, finished: Double?
            for step in 0...420 {
                let time = release - 0.05 + Double(step) / 840
                let p = pose(time)
                let text = p.textOpacity(switchAt: switchAt)
                XCTAssertLessThanOrEqual(text, previous + 1e-12, "the colour eases back one way, switch \(switchAt)")
                if time < release { XCTAssertEqual(text, 1, accuracy: 1e-9) }
                if !p.isVisible { XCTAssertEqual(text, 0, accuracy: 1e-9, "no tile, no tile colour") }
                if text < 0.999, started == nil { started = time }
                if text < 0.001, finished == nil { finished = time }
                previous = text
            }
            for time in stride(from: release, through: release + Timing.exitDuration, by: frame / 4) {
                let now = pose(time).textOpacity(switchAt: switchAt)
                let next = pose(time + frame).textOpacity(switchAt: switchAt)
                largestStep = max(largestStep, now - next)
            }
            let begins = try? XCTUnwrap(started), ends = try? XCTUnwrap(finished)
            guard let begins, let ends else { return XCTFail("the colour never changed, switch \(switchAt)") }
            XCTAssertGreaterThanOrEqual(begins, release, "the text holds while the tile does")
            XCTAssertLessThanOrEqual(ends, release + Timing.exitDuration, "and is back by the time the tile has gone")
            XCTAssertGreaterThanOrEqual(ends - begins, 3 * frame, "eased over several frames, switch \(switchAt)")
            XCTAssertLessThan(largestStep, 0.5, "no snap, switch \(switchAt)")
        }
    }

    func testThePressStillSwitchesQuickly() {
        // The press is about a frame either way: unchanged.
        XCTAssertEqual(Timing.Pose(opacity: 0.5, width: 1).textOpacity(switchAt: 0.4), 1, accuracy: 1e-9)
        XCTAssertEqual(Timing.Pose(opacity: 0.3, width: 1).textOpacity(switchAt: 0.4), 0, accuracy: 1e-9)
    }

    func testReduceMotionOnlyFades() {
        for step in 0...60 {
            let time = release + Timing.exitDuration * Double(step) / 60
            let reduced = pose(time, reduce: true)
            XCTAssertEqual(reduced.scale, 1, "no shrink")
            XCTAssertEqual(reduced.blur, 0, "no blur")
            if reduced.isVisible { XCTAssertEqual(reduced.width, 1) }
            // The same eased curve, as opacity.
            XCTAssertEqual(reduced.opacity, pose(time).opacity, accuracy: 1e-9)
        }
        let label = Timing.Pose.leaving(0.5, reduceMotion: true)
        XCTAssertEqual(label.scale, 1)
        XCTAssertEqual(label.blur, 0)
        XCTAssertEqual(label.opacity, 0.5, accuracy: 1e-9)
    }

    func testAWordThatLeavesCarriesItsTile() {
        // The flowing layouts: the word's own exit takes its tile with it,
        // so the tile holds whole however long after the release.
        for time in [release, release + 0.2, release + 1, release + 30] {
            let carried = Timing.pose(time: time, onset: onset, release: release, reduceMotion: false, carried: true)
            XCTAssertEqual(carried, Timing.Pose(opacity: 1, width: 1))
            XCTAssertEqual(carried.textOpacity(switchAt: 0.4), 1)
        }
    }

    func testARollingLabelLiftsAwayAsAHighlightLeaves() {
        var previous = 1.0
        for step in 0...100 {
            let rank = 0.3 + Double(step) / 100
            let hold = RollingUnit.labelHold(rank: rank)
            let tile = RollingUnit.Tile(width: 1, pose: .leaving(1 - hold, reduceMotion: false), pointSize: 40,
                                        textSwitch: 0.4)
            XCTAssertEqual(tile.opacity, hold, accuracy: 1e-9)
            XCTAssertLessThanOrEqual(tile.textOpacity, previous + 1e-12)
            XCTAssertLessThanOrEqual(tile.scale, 1)
            if hold >= 1 { XCTAssertEqual(tile.textOpacity, 1); XCTAssertEqual(tile.blur, 0) }
            if hold <= 0 { XCTAssertEqual(tile.textOpacity, 0) }
            previous = tile.textOpacity
        }
    }

    func testALabelNeverLiftsQuickerThanAHighlightLeaves() {
        // The stack can step a word back in a few frames: its label still
        // takes the whole exit from the next word's onset.
        let handover = 20.0
        XCTAssertEqual(RollingUnit.labelLeaving(rank: 0, time: 25, handover: handover), 0, "in focus: whole")
        XCTAssertEqual(RollingUnit.labelLeaving(rank: 1, time: handover - 0.01, handover: handover), 0)
        var previous = 0.0
        for step in 0...42 {
            let time = handover + Double(step) / 100
            let leaving = RollingUnit.labelLeaving(rank: 1, time: time, handover: handover)
            XCTAssertGreaterThanOrEqual(leaving, previous)
            XCTAssertEqual(leaving, Timing.exitProgress(time - handover), accuracy: 1e-12)
            previous = leaving
        }
        XCTAssertEqual(previous, 1, accuracy: 1e-9)
        // A slow step back keeps its own pace; with no next word, the rank alone.
        XCTAssertEqual(RollingUnit.labelLeaving(rank: 0.5, time: handover + 5, handover: handover),
                       1 - RollingUnit.labelHold(rank: 0.5), accuracy: 1e-12)
        XCTAssertEqual(RollingUnit.labelLeaving(rank: 0.7, time: 0, handover: nil),
                       1 - RollingUnit.labelHold(rank: 0.7), accuracy: 1e-12)
    }

    func testTorquesFadeEndsAtRest() {
        // The turn accelerates away, but the fade arrives at nothing slowly:
        // no face, tile and all, drops its last third in one frame.
        let exitStart = 21.92, duration = StageMotion.torqueExit
        XCTAssertEqual(StageLyricView.torqueFade(at: exitStart, exitStart: exitStart), 0)
        XCTAssertEqual(StageLyricView.torqueFade(at: exitStart + duration, exitStart: exitStart), 1)
        let last = 1 - StageLyricView.torqueFade(at: exitStart + duration - frame, exitStart: exitStart)
        XCTAssertLessThan(last, 0.1, "under a tenth left for the last frame")
        XCTAssertEqual(StageLyricView.torqueFade(at: exitStart, exitStart: .infinity), 0)
    }

    func testPortalsLineLeavesOnTheHighlightCurve() {
        let exitStart = 21.92
        var previous = 0.0
        for step in 0...60 {
            let time = exitStart - 0.1 + Double(step) / 100
            let leave = StageLyricView.portalLeave(at: time, exitStart: exitStart)
            XCTAssertGreaterThanOrEqual(leave, previous)
            XCTAssertEqual(leave, Timing.exitProgress(time - exitStart), accuracy: 1e-12)
            previous = leave
        }
        XCTAssertEqual(StageLyricView.portalLeave(at: exitStart, exitStart: exitStart), 0)
        XCTAssertEqual(StageLyricView.portalLeave(at: exitStart + Timing.exitDuration, exitStart: exitStart), 1)
        XCTAssertEqual(StageMotion.highlightLeave(at: exitStart + 0.1, exitStart: exitStart),
                       Timing.exitProgress(0.1), accuracy: 1e-12, "Mosaic leaves on the same curve")
        XCTAssertLessThan(StageMotion.highlightLeave(at: exitStart + frame, exitStart: exitStart), 0.01,
                          "no pop on the first frame (the settle dropped about 0.14)")
        // Gone well inside the time a leaving line is still drawn.
        XCTAssertLessThan(Timing.exitDuration, LyricPhraseTiming.exitSpan(for: .portal))
    }

    func testTheWordStaysLegibleWhileItsColourEasesBack() {
        // As `LyricHighlightTests.testTheWordStaysReadableWhileItsTileFades`,
        // through the exit: light tiles with deep ink, under white and
        // pastel lyrics, over the darkest to the brightest backdrop behind
        // the stage. A word going from deep ink to its own light colour
        // while its tile goes from light to the dark backdrop must pass the
        // tile's lightness somewhere; it passes it in a few frames, eased,
        // and is at its usual floor the rest of the time.
        var worst = Double.infinity, longest = 0.0
        let step = 1.0 / 600
        for brightest in [0.01, 0.05, 0.12, 0.2] {
            for hue in stride(from: 0.0, to: 360, by: 30) {
                guard let tile = LyricHighlight.tile(hue: hue * .pi / 180, chroma: 0.16, backdrop: brightest)?.tile
                else { continue }
                let ink = LyricHighlight.text(on: tile)
                for pastel in [OKLab.white, OKLab(l: 0.86, chroma: 0.08, hue: 1)] {
                    let lyric = Perceptual.readable(pastel, background: brightest)
                    let at = LyricHighlight.textSwitch(tile: tile, text: ink, backdrop: brightest,
                                                       lyric: Perceptual.luminance(lyric))
                    for backdrop in [0, brightest / 2, brightest] {
                        var low = 0.0
                        for time in stride(from: release, through: release + Timing.exitDuration, by: step) {
                            let leaving = pose(time)
                            let opacity = leaving.opacity
                            let shown = leaving.textOpacity(switchAt: at)
                            let behind = LyricLegibility.linear(opacity * LyricLegibility.encoded(Perceptual.luminance(tile))
                                + (1 - opacity) * LyricLegibility.encoded(backdrop))
                            let text = Perceptual.luminance(LyricHighlight.blend(ink, over: lyric, opacity: shown))
                            let contrast = Perceptual.contrast(text, behind)
                            worst = min(worst, contrast)
                            if opacity <= 0.001 || opacity >= 0.98 {
                                XCTAssertGreaterThanOrEqual(contrast, 2.99, "whole or gone, at its floor")
                            }
                            if contrast < 1.9 { low += step }
                        }
                        longest = max(longest, low)
                        XCTAssertLessThanOrEqual(low, 0.07,
                            "below 1.9:1 for at most a few frames: hue \(hue), \(backdrop) of \(brightest), switch \(at)")
                    }
                }
            }
        }
        print("HighlightExitTests: worst contrast through the exit \(worst), longest under 1.9:1 \(longest) s")
    }

    // MARK: Cutout's label turns over like a flap (owner's pick, 26 Nov 2026)

    func testCutoutLabelFlipsOnAndCarriesOnOver() {
        // Edge on and unseen before the onset, a little past flat with the
        // spring, flat when it lands, and on over as it leaves.
        let before = RollingUnit.label(arrival: 0, leaving: 0, pointSize: 60, textSwitch: 0.4, ink: .none, reduceMotion: false)
        XCTAssertEqual(before.flip, -90)
        XCTAssertEqual(before.opacity, 0, accuracy: 1e-9)
        XCTAssertEqual(before.width, 1, "always the word's full width: no more press from the leading edge")
        XCTAssertGreaterThan(RollingUnit.labelFlip(arrival: 1.06, leaving: 0, reduceMotion: false), 0, "a little past")
        let landed = RollingUnit.label(arrival: 1, leaving: 0, pointSize: 60, textSwitch: 0.4, ink: .none, reduceMotion: false)
        XCTAssertEqual(landed.flip, 0)
        XCTAssertEqual(landed.opacity, 1, accuracy: 1e-9)
        XCTAssertEqual(RollingUnit.labelFlip(arrival: 1, leaving: 1, reduceMotion: false), 90, "carries on over")
        var last = -91.0
        for step in 0...20 {
            let leaving = Double(step) / 20
            let flip = RollingUnit.labelFlip(arrival: 1, leaving: leaving, reduceMotion: false)
            XCTAssertGreaterThanOrEqual(flip, last, "one way round")
            last = flip
        }
    }

    func testCutoutLabelFlipIsMeasuredOnTheLabelsOwnHeight() {
        // Where a corner of the label lands at a turn, as a share of the
        // label: the same for a short label and a tall one (a reading under
        // the word, a script reaching high and low), and for a short word
        // and a long one, so every label turns the same way to the eye.
        func corner(_ size: CGSize, _ degrees: Double, _ point: CGPoint) -> CGPoint {
            let projected = point.applying(LabelFlip(degrees: degrees).effectValue(size: size))
            return CGPoint(x: projected.x / size.width, y: projected.y / size.height)
        }
        for degrees in [-60.0, -30, 10, 45, 75] {
            for unit in [CGPoint(x: 0, y: 0), CGPoint(x: 1, y: 0), CGPoint(x: 0, y: 1), CGPoint(x: 1, y: 1)] {
                let short = CGSize(width: 240, height: 60)
                let reference = corner(short, degrees, CGPoint(x: unit.x * short.width, y: unit.y * short.height))
                for size in [CGSize(width: 240, height: 110), CGSize(width: 720, height: 60), CGSize(width: 96, height: 40)] {
                    let other = corner(size, degrees, CGPoint(x: unit.x * size.width, y: unit.y * size.height))
                    XCTAssertEqual(other.y, reference.y, accuracy: 1e-6, "\(size) at \(degrees)°")
                    XCTAssertEqual(other.x, reference.x, accuracy: 1e-6, "\(size) at \(degrees)°")
                }
            }
        }
        // Flat is no change at all; turned, the near edge swells and the far one narrows.
        XCTAssertEqual(CGPoint(x: 10, y: 5).applying(LabelFlip(degrees: 0).effectValue(size: CGSize(width: 100, height: 40))),
                       CGPoint(x: 10, y: 5))
        let size = CGSize(width: 200, height: 50)
        let top = CGPoint(x: 0, y: 0).applying(LabelFlip(degrees: 40).effectValue(size: size))
        let bottom = CGPoint(x: 0, y: 50).applying(LabelFlip(degrees: 40).effectValue(size: size))
        XCTAssertNotEqual(top.x, bottom.x, accuracy: 1)
        XCTAssertLessThan(bottom.y - top.y, 50, "turned, the label is shorter")
    }
}
