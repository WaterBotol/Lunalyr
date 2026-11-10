import AppKit
import XCTest
@testable import VersoCustom

final class LyricTapSyncTests: XCTestCase {
    private func isolated() -> (UserDefaults, () -> Void) {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        return (defaults, { defaults.removePersistentDomain(forName: name) })
    }

    /// Irregular line starts, like a real song.
    private let starts: [Double] = [12.0, 15.4, 19.1, 21.8, 26.5, 29.0, 33.7, 36.2, 41.9, 44.4]

    // MARK: Estimate

    func testTapsLateByAFixedAmountGiveThatOffset() throws {
        // Each tap lands 0.6 s after its line (lyrics should come 0.6 s later).
        let taps = starts[2...5].map { $0 + 0.6 }
        let estimate = try XCTUnwrap(LyricTapSync.estimate(taps: taps, lineStarts: starts, prior: 0))
        XCTAssertEqual(estimate.offset, -0.6, accuracy: 1e-9)
        XCTAssertEqual(estimate.used, 4)
    }

    func testTooFewTapsGiveNothing() {
        XCTAssertNil(LyricTapSync.estimate(taps: [12.3, 15.7, 19.4], lineStarts: starts, prior: 0))
        XCTAssertNil(LyricTapSync.estimate(taps: [1, 2, 3, 4], lineStarts: [], prior: 0))
    }

    func testAStrayTapIsIgnoredOnceEnoughTapsAgree() throws {
        // Four good taps 0.3 s early, and one wild tap mid-line.
        let good = [starts[1], starts[2], starts[4], starts[5]].map { $0 - 0.3 }
        let taps = [good[0], good[1], 24.2, good[2], good[3]]
        let estimate = try XCTUnwrap(LyricTapSync.estimate(taps: taps, lineStarts: starts, prior: 0))
        XCTAssertEqual(estimate.offset, 0.3, accuracy: 1e-9, "the stray tap moves nothing")
        XCTAssertEqual(estimate.used, 4)
        XCTAssertEqual(estimate.total, 5)
        // With only four taps, one of them stray, the rest don't yet agree enough.
        XCTAssertNil(LyricTapSync.estimate(taps: Array(taps.prefix(4)), lineStarts: starts, prior: 0))
    }

    func testJitteredTapsGiveTheirMedian() throws {
        let jitter = [0.05, -0.08, 0.12, -0.02, 0.4]
        let taps = zip(starts[0...4], jitter).map { $0 + 1.0 + $1 }
        let estimate = try XCTUnwrap(LyricTapSync.estimate(taps: taps, lineStarts: starts, prior: 0))
        // Offsets are -(1 + jitter); 0.4 is beyond the tolerance of the
        // winning fit so may or may not count, but the median stays near -1.05.
        XCTAssertEqual(estimate.offset, -1.05, accuracy: 0.06)
    }

    func testADoubleTapOnOneLineCountsOnce() {
        // Two taps on one line, two on others: three lines marked, not enough.
        let taps = [starts[0] + 0.2, starts[0] + 0.25, starts[1] + 0.2, starts[2] + 0.2]
        XCTAssertNil(LyricTapSync.estimate(taps: taps, lineStarts: starts, prior: 0))
        XCTAssertEqual(LyricTapSync.agreeing(taps: taps, starts: starts, offset: -0.2).count, 3)
    }

    func testAnAmbiguousFitPrefersTheOffsetInForce() throws {
        // Evenly spaced lines: shifting by a whole line fits as well, so the
        // fit nearest the offset already set wins.
        let even = stride(from: 10.0, through: 40.0, by: 2.0).map { $0 }
        let taps = [14.5, 16.5, 18.5, 20.5]
        let near = try XCTUnwrap(LyricTapSync.estimate(taps: taps, lineStarts: even, prior: -0.4))
        XCTAssertEqual(near.offset, -0.5, accuracy: 1e-9)
        let other = try XCTUnwrap(LyricTapSync.estimate(taps: taps, lineStarts: even, prior: 1.4))
        XCTAssertEqual(other.offset, 1.5, accuracy: 1e-9)
    }

    func testOffsetsBeyondTheSearchWindowOrStoreLimitAreNotFound() {
        let taps = starts[0...3].map { $0 - 20 }
        XCTAssertNil(LyricTapSync.estimate(taps: taps, lineStarts: starts, prior: 0), "Tap to sync refines; it doesn't search")
        XCTAssertNotNil(LyricTapSync.estimate(taps: taps, lineStarts: starts, prior: 18))
    }

    func testHelpers() {
        XCTAssertEqual(LyricTapSync.median([3, 1, 2]), 2)
        XCTAssertEqual(LyricTapSync.median([4, 1, 2, 3]), 2.5)
        XCTAssertNil(LyricTapSync.median([]))
        XCTAssertEqual(LyricTapSync.nearest([1, 5, 9], to: 6.9), 1)
        XCTAssertEqual(LyricTapSync.nearest([1, 5, 9], to: 7.1), 2)
        XCTAssertEqual(LyricTapSync.nearest([1, 5, 9], to: -3), 0)
        XCTAssertEqual(LyricTapSync.nearest([1, 5, 9], to: 30), 2)
        XCTAssertNil(LyricTapSync.nearest([], to: 1))
        XCTAssertEqual(LyricTapSync.tapTime(playbackTime: 10, globalOffset: 0.5, reaction: 0.2), 10.3, accuracy: 1e-9)
        let line = { (start: Double, text: String, instrumental: Bool) in
            LyricsFetcher.Line(start: start, end: start + 1, text: text, words: [], isInstrumental: instrumental)
        }
        XCTAssertEqual(LyricTapSync.lineStarts([line(1, "Hello", false), line(3, "", false), line(5, "♪", true),
                                                line(7, "Again", false)]), [1, 7])
    }

    // MARK: Session

    func testSessionAppliesOnceTapsAgreeAndTimesOut() {
        var session = LyricTapSession()
        session.start(lineStarts: starts, prior: 0.1, playing: true, now: 100)
        XCTAssertTrue(session.isListening)
        XCTAssertEqual(session.deadline, 100 + LyricTapSync.firstTapLimit)
        XCTAssertTrue(session.message.contains("Esc"))
        for (index, start) in starts[0...2].enumerated() {
            XCTAssertNil(session.tap(at: start + 0.4, now: 101 + Double(index)))
        }
        XCTAssertEqual(session.deadline, 103 + LyricTapSync.nextTapLimit)
        XCTAssertTrue(session.message.contains("3 tapped, 1 more"))
        let estimate = session.tap(at: starts[3] + 0.4, now: 104)
        XCTAssertEqual(estimate?.offset ?? 0, -0.4, accuracy: 1e-9)
        XCTAssertEqual(session.phase, .applied(offset: -0.4, previous: 0.1, used: 4, total: 4))
        XCTAssertTrue(session.message.contains("−0.40 s"))
        XCTAssertNil(session.tap(at: 50, now: 105), "no taps once applied")

        var waiting = LyricTapSession()
        waiting.start(lineStarts: starts, prior: 0, playing: true, now: 0)
        XCTAssertFalse(waiting.expire(now: LyricTapSync.firstTapLimit - 1))
        XCTAssertTrue(waiting.expire(now: LyricTapSync.firstTapLimit))
        XCTAssertEqual(waiting.phase, .failed(.timedOut))
        waiting.dismiss()
        XCTAssertEqual(waiting.phase, .idle)
    }

    func testSessionRefusesWithoutASongOrLyricsAndGivesUpOnDisagreement() {
        var session = LyricTapSession()
        session.start(lineStarts: starts, prior: 0, playing: false, now: 0)
        XCTAssertEqual(session.phase, .failed(.notPlaying))
        session.start(lineStarts: [], prior: 0, playing: true, now: 0)
        XCTAssertEqual(session.phase, .failed(.noLyrics))

        session.start(lineStarts: starts, prior: 0, playing: true, now: 0)
        // Taps far quicker than the lines: no more than two can ever mark lines.
        let scattered = (0..<LyricTapSync.maximumTaps).map { 20 + 0.5 * Double($0) }
        for (index, tap) in scattered.enumerated() { session.tap(at: tap, now: Double(index)) }
        XCTAssertEqual(session.phase, .failed(.noAgreement))
        XCTAssertTrue(session.message.contains("unchanged"))

        session.start(lineStarts: starts, prior: 0, playing: true, now: 0)
        session.cancel()
        XCTAssertEqual(session.phase, .failed(.cancelled))
    }

    // MARK: Controller

    @MainActor
    private final class Fake {
        var query: LyricQuery? = LyricQuery(title: "Song", artist: "Artist", album: "Album", duration: 200)
        var playing = true
        var time = 0.0
        var clock = 1000.0
        var notes: [String] = []
        var lines: [Double] = []
        var context: LyricTapContext {
            LyricTapContext(query: { [unowned self] in query }, lineStarts: { [unowned self] in lines },
                            isPlaying: { [unowned self] in playing }, playbackTime: { [unowned self] in time },
                            globalOffset: { 0.25 })
        }
    }

    @MainActor
    func testControllerSavesTheOffsetForTheSourceAndUndoesIt() throws {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let offsets = SongOffsets(defaults: defaults)
        offsets.activeSource = "LRCLIB"
        let fake = Fake()
        fake.lines = starts
        let query = try XCTUnwrap(fake.query)
        offsets.set(0.2, for: query)
        let controller = LyricTapSyncController(offsets: offsets, context: fake.context, now: { [unowned fake] in fake.clock },
                                                presents: false, notify: { [unowned fake] text, _ in fake.notes.append(text) })
        XCTAssertEqual(controller.actionTitle, LyricTapSyncController.menuTitle)
        controller.toggle()
        XCTAssertTrue(controller.isListening)
        XCTAssertEqual(controller.actionTitle, LyricTapSyncController.stopTitle)
        // Taps heard 0.5 s after each line: tap lyric time = playback - 0.15 + 0.25.
        for start in starts[3...6] {
            fake.time = start + 0.5 + LyricTapSync.reactionTime - 0.25
            fake.clock += 2
            controller.tap()
        }
        XCTAssertFalse(controller.isListening)
        XCTAssertEqual(offsets.offset(for: query), -0.5, accuracy: 1e-9)
        XCTAssertEqual(offsets.offset(for: query, source: "Other"), 0, "saved for the lyric source on screen")
        XCTAssertTrue(controller.canUndo)
        XCTAssertEqual(fake.notes.last?.contains("Lyrics synced"), true)

        let entries = MenuBarLyricTicker.menuEntries(extras: nil, hotkeys: GlobalHotkeys(
            defaults: defaults, registrar: .init(register: { _, _, _ in true }, unregister: { _ in })), tapSync: controller)
        XCTAssertEqual(entries.compactMap { $0?.title }.filter { $0.contains("Tap") },
                       [LyricTapSyncController.menuTitle, LyricTapSyncController.undoTitle])

        controller.undo()
        XCTAssertEqual(offsets.offset(for: query), 0.2, accuracy: 1e-9)
        XCTAssertFalse(controller.canUndo)
        XCTAssertTrue(fake.notes.last?.contains("undone") ?? false)
    }

    @MainActor
    func testUndoIsOnlyOfferedWhileThatOffsetIsInForceForThatSong() throws {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let offsets = SongOffsets(defaults: defaults)
        let fake = Fake()
        fake.lines = starts
        let query = try XCTUnwrap(fake.query)
        let controller = LyricTapSyncController(offsets: offsets, context: fake.context, now: { [unowned fake] in fake.clock },
                                                presents: false, notify: { _, _ in })
        controller.start()
        for start in starts[0...3] {
            fake.time = start + LyricTapSync.reactionTime - 0.25 - 0.3
            controller.tap()
        }
        XCTAssertEqual(offsets.offset(for: query), 0.3, accuracy: 1e-9)
        XCTAssertTrue(controller.canUndo)
        offsets.set(0.4, for: query)
        XCTAssertFalse(controller.canUndo, "nudged since: undo would throw that away")
        offsets.set(0.3, for: query)
        fake.query = LyricQuery(title: "Next", artist: "Artist", album: nil, duration: 180)
        XCTAssertFalse(controller.canUndo, "another song")
    }

    @MainActor
    func testControllerStopsOnSongChangePauseOrTimeout() throws {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let offsets = SongOffsets(defaults: defaults)
        let fake = Fake()
        fake.lines = starts
        var notes: [String] = []
        let controller = LyricTapSyncController(offsets: offsets, context: fake.context, now: { [unowned fake] in fake.clock },
                                                presents: false, notify: { text, _ in notes.append(text) })
        fake.playing = false
        controller.start()
        XCTAssertFalse(controller.isListening)
        XCTAssertEqual(controller.session.phase, .failed(.notPlaying))

        fake.playing = true
        fake.lines = []
        controller.start()
        XCTAssertEqual(controller.session.phase, .failed(.noLyrics))

        fake.lines = starts
        controller.start()
        fake.query = LyricQuery(title: "Next", artist: "Artist", album: nil, duration: 180)
        controller.tap()
        XCTAssertEqual(controller.session.phase, .failed(.cancelled), "a new song ends it")

        fake.query = LyricQuery(title: "Song", artist: "Artist", album: "Album", duration: 200)
        controller.start()
        fake.clock += LyricTapSync.firstTapLimit
        controller.expireIfDue()
        XCTAssertEqual(controller.session.phase, .failed(.timedOut))
        XCTAssertEqual(notes.last?.contains("No taps"), true)
        XCTAssertEqual(offsets.offset(for: fake.query), 0, "nothing saved")
    }

    // MARK: Shortcuts

    @MainActor
    func testNewShortcutsWaitToBeSwitchedOnAndRemembered() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let registrar = GlobalHotkeys.Registrar(register: { _, _, _ in true }, unregister: { _ in })
        let hotkeys = GlobalHotkeys(defaults: defaults, registrar: registrar)
        for action in [GlobalHotkeys.Action.tapSyncLyrics, .toggleMusicVideo, .nextVisualizer] {
            XCTAssertFalse(action.isOnByDefault)
            XCTAssertFalse(hotkeys.isSwitchedOn(action), "off until switched on")
        }
        XCTAssertTrue(hotkeys.isSwitchedOn(.toggleLyrics))
        hotkeys.setEnabled(true)
        XCTAssertFalse(hotkeys.active.contains(.tapSyncLyrics))
        hotkeys.setSwitchedOn(true, for: .tapSyncLyrics)
        XCTAssertTrue(hotkeys.active.contains(.tapSyncLyrics))
        hotkeys.setSwitchedOn(false, for: .copyCard)
        let reloaded = GlobalHotkeys(defaults: defaults, registrar: registrar)
        XCTAssertTrue(reloaded.isSwitchedOn(.tapSyncLyrics))
        XCTAssertFalse(reloaded.isSwitchedOn(.toggleMusicVideo))
        XCTAssertFalse(reloaded.isSwitchedOn(.copyCard))
        XCTAssertTrue(reloaded.isSwitchedOn(.toggleLyrics))
        hotkeys.setEnabled(false)
    }

    @MainActor
    func testMusicVideoAndVisualiserShortcuts() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let settings = Settings(defaults: defaults, notifications: NotificationCenter())
        let extras = LunalyrExtras(defaults: defaults)
        extras.announce = { _ in }
        settings.backgroundMode = .shader
        extras.toggleMusicVideo(settings: settings)
        XCTAssertEqual(settings.backgroundMode, .musicVideo)
        extras.toggleMusicVideo(settings: settings)
        XCTAssertEqual(settings.backgroundMode, .shader, "back to what it fell back to")

        settings.audioVisualizerStyle = .silk
        extras.nextVisualizer(settings: settings)
        XCTAssertEqual(settings.backgroundMode, .visualizer)
        XCTAssertEqual(settings.audioVisualizerStyle, .silk, "the first press shows the visualiser chosen last")
        extras.nextVisualizer(settings: settings)
        XCTAssertEqual(settings.audioVisualizerStyle, AudioVisualizerStyle.allCases[1])
        XCTAssertEqual(LunalyrExtras.visualizer(after: AudioVisualizerStyle.allCases.last!), AudioVisualizerStyle.allCases[0])
    }
}
