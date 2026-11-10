import SwiftUI
import XCTest
@testable import VersoCustom

// The menu-bar panel's feedback: the audio banner through a reconnect, the
// transport answering a press at once, the toggles' press. Pure values only.

final class MenuBarPanelPolishTests: XCTestCase {
    typealias Audio = MenuBarAudioStatus

    func testReconnectKeepsTheBannerAndSaysWhenItWorked() {
        var phase = Audio.Phase.hidden
        phase = Audio.phase(after: phase, state: .attention(.stalled))
        XCTAssertEqual(phase, .attention)
        phase = Audio.phase(after: phase, state: .connecting)
        XCTAssertEqual(phase, .reconnecting, "the banner stays while it reconnects instead of vanishing")
        phase = Audio.phase(after: phase, state: .receiving)
        XCTAssertEqual(phase, .recovered)
        phase = Audio.phase(after: phase, state: .quiet)
        XCTAssertEqual(phase, .recovered, "the success holds until its timer ends it")
        XCTAssertEqual(Audio.phase(after: .reconnecting, state: .attention(.failed)), .attention,
                       "failing again shows the fixes in the same place")
        XCTAssertEqual(Audio.phase(after: .hidden, state: .connecting), .hidden, "a normal start says nothing")
        XCTAssertEqual(Audio.phase(after: .hidden, state: .receiving), .hidden)
        XCTAssertGreaterThan(Audio.recoveredHold, 1)
    }

    func testPlayPauseShowsThePressAtOnce() {
        XCTAssertTrue(TransportControls.shownPlaying(actual: false, pending: true))
        XCTAssertFalse(TransportControls.shownPlaying(actual: true, pending: false))
        XCTAssertTrue(TransportControls.shownPlaying(actual: true, pending: nil))
        XCTAssertLessThanOrEqual(TransportControls.pendingTimeout, 2)
        XCTAssertFalse(TransportControls.consentHelp.isEmpty)
    }

    func testQuickTogglesPressInExceptUnderReduceMotion() {
        XCTAssertEqual(QuickToggle.pressScale(pressed: true, reduceMotion: false), 0.92)
        XCTAssertEqual(QuickToggle.pressScale(pressed: true, reduceMotion: true), 1)
        XCTAssertEqual(QuickToggle.pressScale(pressed: false, reduceMotion: false), 1)
    }

    func testARepeatedLineIsStillANewLine() {
        let first = LyricNow(line: "yeah", index: 4, next: "yeah")
        let second = LyricNow(line: "yeah", index: 5, next: nil)
        XCTAssertNotEqual(first, second, "same words, new line: the panel animates it")
    }
}
