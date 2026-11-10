import SwiftUI
import XCTest
@testable import VersoCustom

/// Sanity of the motion tokens (Sources/UI/Design/MotionTokens.swift): every
/// duration is positive and ordered sensibly, and Reduce Motion always means a
/// plain fade.
final class MotionTokensTests: XCTestCase {
    typealias Motion = LunalyrDesign.Motion

    func testDurationsArePositive() {
        let durations: [Double] = [
            Motion.Hold.confirm, Motion.Hold.hud, Motion.Hold.placeholderDelay, Motion.Hold.spinnerDelay,
            Motion.CA.quick, Motion.CA.standard, Motion.CA.reduced, Motion.CA.line,
            WallpaperMotion.surfaceFade, WallpaperMotion.mediaReveal, WallpaperMotion.mediaHide,
            WallpaperMotion.sceneOut, WallpaperMotion.sceneIn,
            WallpaperMotion.seekIn, WallpaperMotion.seekOut, WallpaperMotion.seekOutDelay,
            WallpaperMotion.lineExit, WallpaperMotion.wordExit, WallpaperMotion.wordExitStagger,
            WallpaperMotion.lineAttack, WallpaperMotion.previewRamp, WallpaperMotion.exitSpan,
            WallpaperMotion.holdAfterSung, WallpaperMotion.styleSwitch,
            WallpaperMotion.noticeHold, WallpaperMotion.noticeFade, WallpaperMotion.idleNoticeHold,
            WallpaperMotion.toastIn, WallpaperMotion.toastOutAt, WallpaperMotion.toastOut, WallpaperMotion.toastLife,
            WallpaperMotion.interludeIn, WallpaperMotion.interludeInDelay, WallpaperMotion.interludeOut,
            WallpaperMotion.loadingDelay, WallpaperMotion.loadingIn,
            WallpaperMotion.reducedDissolve,
        ]
        for duration in durations {
            XCTAssertGreaterThan(duration, 0)
        }
    }

    func testDurationsAreOrderedSensibly() {
        XCTAssertLessThan(Motion.CA.quick, Motion.CA.standard)
        XCTAssertLessThan(WallpaperMotion.mediaHide, WallpaperMotion.mediaReveal, "exits are shorter than entrances")
        XCTAssertLessThan(WallpaperMotion.sceneOut, WallpaperMotion.sceneIn)
        XCTAssertLessThanOrEqual(WallpaperMotion.toastIn, WallpaperMotion.toastOutAt)
        XCTAssertLessThanOrEqual(WallpaperMotion.toastOutAt + WallpaperMotion.toastOut, WallpaperMotion.toastLife + 0.0001)
        XCTAssertLessThan(Motion.Hold.placeholderDelay, Motion.Hold.spinnerDelay)
        XCTAssertEqual(Motion.Hold.spinnerDelay, WallpaperMotion.loadingDelay)
    }

    func testReduceMotionIsAShortFade() {
        // The three Reduce Motion dissolves agree.
        XCTAssertEqual(Motion.CA.reduced, WallpaperMotion.reducedDissolve)
        XCTAssertEqual(Motion.reduced, Animation.easeInOut(duration: WallpaperMotion.reducedDissolve))
        // Every SwiftUI token, the bouncy one included, becomes that fade.
        for animation in [Motion.quick, Motion.standard, Motion.step, Motion.exit, Motion.emphasised, Motion.celebrate] {
            XCTAssertEqual(Motion.animation(animation, reduceMotion: true), Motion.reduced)
            XCTAssertNotEqual(Motion.animation(animation, reduceMotion: false), Motion.reduced)
        }
        // Symbol swaps cross-fade; reveals do not move.
        XCTAssertEqual(Motion.symbolSwap(reduceMotion: true), .opacity)
        XCTAssertNotEqual(Motion.symbolSwap(reduceMotion: false), .opacity)
        XCTAssertEqual(Motion.revealOffset(reduceMotion: true), 0)
        XCTAssertEqual(Motion.revealOffset(reduceMotion: false), -4)
    }
}
