import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// The lyric stage's own frame clock. The background's clock already stops
/// when nothing on it can change (`FrameClockPolicy`); the lyrics keep their
/// own, which ran at the full wallpaper rate on every display whenever
/// nothing was playing, a song had no lyrics, or the lyrics were hidden.
@MainActor
final class LyricStageClockTests: XCTestCase {
    private let timing = LyricLayer.noticeTiming

    private func quiet(_ kind: LyricSceneKey.Kind, shown: Double = 1, target: Double = 1, playing: Bool = false,
                       colourMoves: Bool = false, noLyrics: VersoCustom.Settings.NoLyricsBehaviour = .notice,
                       age: TimeInterval? = nil) -> LyricPace.Level? {
        LyricPace.quietLevel(.init(kind: kind, shown: shown, target: target, playing: playing, colourMoves: colourMoves,
                                   noLyrics: noLyrics, noticeAge: age), timing: timing)
    }

    private func settle(_ pace: LyricPace) {
        let published = expectation(description: "published after the frame")
        DispatchQueue.main.async { published.fulfill() }
        wait(for: [published], timeout: 1)
    }

    func testNothingPlayingRestsOnceItsNoticeHasGone() {
        // "Nothing playing" arrives, holds, fades, and then needs nothing.
        XCTAssertEqual(quiet(.idle, age: 0.2), .active)
        XCTAssertEqual(quiet(.idle, age: 3), .still)
        XCTAssertEqual(quiet(.idle, age: 5.4), .active)
        XCTAssertEqual(quiet(.idle, age: timing.idleHold + timing.noticeFade + 0.1), .resting)
        XCTAssertEqual(quiet(.idle, age: 8 * 3600), .resting)
        // Nothing has played since launch: no notice, no clock.
        XCTAssertEqual(quiet(.idle, age: nil), .resting)
    }

    func testMissingLyricsRestOnceThereIsNothingLeftToChange() {
        XCTAssertEqual(quiet(.missing, age: 2), .still)
        XCTAssertEqual(quiet(.missing, age: 4.5), .active)
        XCTAssertEqual(quiet(.missing, age: 5.2), .resting)
        XCTAssertEqual(quiet(.missing, age: nil), .resting)
        // "Show nothing" never changes on its own; nor does "Show title and
        // artist" once its colour has settled.
        XCTAssertEqual(quiet(.missing, playing: true, noLyrics: .nothing, age: 0), .resting)
        XCTAssertEqual(quiet(.missing, playing: true, noLyrics: .trackName, age: 0), .resting)
    }

    func testTheTrackNameAndNoticesKeepAClockWhileTheirColourMoves() {
        // The title and artist are set in the text colour: a cover arriving
        // late eases it to new colours, and Album drift never stops.
        XCTAssertEqual(quiet(.missing, playing: true, colourMoves: true, noLyrics: .trackName, age: 0), .ambient)
        XCTAssertEqual(quiet(.missing, playing: false, colourMoves: true, noLyrics: .trackName), .ambient)
        XCTAssertEqual(quiet(.missing, noLyrics: .nothing), .resting)
        // A notice holding still in a moving colour, too; gone, it needs nothing.
        XCTAssertEqual(quiet(.missing, colourMoves: true, age: 2), .ambient)
        XCTAssertEqual(quiet(.idle, colourMoves: true, age: 3), .ambient)
        XCTAssertEqual(quiet(.idle, colourMoves: true, age: 60), .resting)
    }

    func testHiddenLyricsRestWhileTheyAreSung() {
        for kind in [LyricSceneKey.Kind.synced, .plain, .loading, .idle, .missing] {
            XCTAssertEqual(quiet(kind, shown: 0, target: 0, playing: true, colourMoves: true, age: 1), .resting, "\(kind)")
        }
        // Hiding and showing fade on every frame.
        XCTAssertEqual(quiet(.synced, shown: 0.5, target: 0, playing: true), .active)
        XCTAssertEqual(quiet(.synced, shown: 0.5, target: 1, playing: true), .active)
    }

    func testLyricsShownAgainStartTheClockOnTheirFirstFrame() {
        // Settings' Hide lyrics and the end of mirroring show the lyrics with
        // no note and nothing else changing on the stage. The one rebuild
        // that follows comes about a millisecond into the fade, with the
        // lyrics still all but invisible; resting on it left them hidden.
        var visibility = LyricVisibility(visible: false)
        XCTAssertEqual(quiet(.synced, shown: visibility.presence(at: 1000), target: visibility.target), .resting)
        visibility.set(true, wall: 1000)
        for age in [0.0, 0.0005, 0.001, 0.003] {
            let shown = visibility.presence(at: 1000 + age)
            XCTAssertLessThanOrEqual(shown, 0.001)
            for kind in [LyricSceneKey.Kind.synced, .plain, .missing, .idle] {
                XCTAssertEqual(quiet(kind, shown: shown, target: visibility.target, age: 1), .active, "\(kind) at \(age)")
            }
        }
        // Shown, the stage decides as usual.
        XCTAssertEqual(quiet(.synced, shown: visibility.presence(at: 1001), target: visibility.target), .waiting)
        // Hidden again: active while fading out, resting once gone.
        visibility.set(false, wall: 1002)
        XCTAssertEqual(quiet(.synced, shown: visibility.presence(at: 1002.1), target: visibility.target), .active)
        XCTAssertEqual(quiet(.synced, shown: visibility.presence(at: 1003), target: visibility.target), .resting)
    }

    func testPausedLyricsOnlyWaitForASeek() {
        XCTAssertEqual(quiet(.synced, playing: false), .waiting)
        XCTAssertEqual(quiet(.plain, playing: false), .waiting)
        // Colour that drifts by itself, or is still easing, keeps a slow clock.
        XCTAssertEqual(quiet(.synced, playing: false, colourMoves: true), .ambient)
        // Words being sung are decided word by word.
        XCTAssertNil(quiet(.synced, playing: true))
        XCTAssertEqual(quiet(.plain, playing: true), .ambient)
        XCTAssertEqual(quiet(.loading), .still)
    }

    func testARestingClockStopsAndDrawsAtTheRealTime() {
        let pace = LyricPace()
        pace.report(.waiting)
        settle(pace)
        XCTAssertFalse(pace.rests)
        XCTAssertEqual(pace.interval(full: 1.0 / 60), 0.5, accuracy: 1e-12)
        pace.report(.resting)
        settle(pace)
        XCTAssertTrue(pace.rests)
        // A stopped timeline hands a rebuild its last tick's date, possibly
        // hours old: the frame is drawn at the real time instead...
        let now = Date().timeIntervalSinceReferenceDate
        XCTAssertGreaterThanOrEqual(pace.wall(now - 3600), now - LyricPace.staleness)
        // ...and never earlier than a frame already drawn, once running again.
        pace.report(.active)
        settle(pace)
        XCTAssertGreaterThanOrEqual(pace.wall(now - 3600), now - LyricPace.staleness)
        XCTAssertEqual(pace.wall(now + 0.5), now + 0.5)
        XCTAssertEqual(pace.wall(now + 0.2), now + 0.5, "a frame handed a slightly older date is drawn at the newest")
    }

    func testRebuildsBetweenSlowTicksDrawAtTheRealTime() {
        // Paused, the clock looks twice a second, and a rebuild between two
        // looks -- another lyric candidate, the next track, a Settings hide
        // -- was drawn at the last look's date, up to half a second old: a
        // 0.45 s cross-fade or fade it started was all but over by the next
        // frame. Every level now draws such a frame at the real time.
        for level in [LyricPace.Level.waiting, .still, .ambient, .active] {
            let pace = LyricPace()
            pace.report(level)
            settle(pace)
            let now = Date().timeIntervalSinceReferenceDate
            XCTAssertGreaterThanOrEqual(pace.wall(now - 0.45), now - LyricPace.staleness, "\(level)")
        }
        // A tick is drawn at its own date, so frames step evenly.
        let pace = LyricPace()
        let tick = Date().timeIntervalSinceReferenceDate - 0.003
        XCTAssertEqual(pace.wall(tick), tick)
    }

    func testFramesFollowTheSystemClockWhenItStepsBack() {
        let pace = LyricPace()
        let now = Date().timeIntervalSinceReferenceDate
        // The clock set back two minutes after a frame was drawn: frames
        // carry on from the new time instead of holding at the old one.
        XCTAssertEqual(pace.wall(now + 120), now + 120)
        let after = pace.wall(now)
        XCTAssertEqual(after, now, accuracy: LyricPace.staleness + 0.01)
        XCTAssertGreaterThan(pace.wall(now + 0.1), after)
        // The colours keep moving from the new time too.
        let director = LyricInkDirector()
        var phases: [Double] = []
        for frame in 0...60 {
            phases.append(director.ink(.init(mode: .albumDrift, album: [SIMD3(0.8, 0.2, 0.3), SIMD3(0.2, 0.4, 0.9)],
                                             backdrop: 0.05), wall: 2000 + Double(frame) / 30).phase)
        }
        for frame in 0...60 {
            phases.append(director.ink(.init(mode: .albumDrift, album: [SIMD3(0.8, 0.2, 0.3), SIMD3(0.2, 0.4, 0.9)],
                                             backdrop: 0.05), wall: 1880 + Double(frame) / 30).phase)
        }
        XCTAssertGreaterThan(phases[121], phases[61], "the drift goes on after the clock stepped back")
        // A cross-fade under way when the clock stepped back is finished, not
        // held for two minutes.
        let memory = LyricSceneMemory()
        let first = LyricSceneKey(kind: .synced, track: 1, fingerprint: 1)
        let second = LyricSceneKey(kind: .synced, track: 2, fingerprint: 2)
        _ = memory.observe(key: first, content: LyricSceneContent(), time: 10, wall: 3000, jump: nil)
        let fading = memory.observe(key: second, content: LyricSceneContent(), time: 0, wall: 3001, jump: nil)
        XCTAssertNotNil(fading.outgoing)
        let stepped = memory.observe(key: second, content: LyricSceneContent(), time: 0, wall: 2881, jump: nil)
        XCTAssertNil(stepped.outgoing)
        XCTAssertEqual(stepped.incomingPresence, 1)
    }

    /// The real layer on a clock wired as `LyricStage` wires it.
    private struct ClockedLayer: View {
        @ObservedObject var pace: LyricPace
        let ticks: TickCounter
        var body: some View {
            TimelineView(.animation(minimumInterval: pace.interval(full: 1.0 / 30), paused: pace.rests)) { timeline in
                let now = pace.wall(timeline.date.timeIntervalSinceReferenceDate)
                let _ = ticks.count += 1
                LyricLayer(time: 30, baseSize: 40, maxWidth: 400, maxHeight: 220, mood: .neutral, accent: .orange,
                           trackSeed: 3, wall: now, palette: [], animating: true, pace: pace)
            }
            .frame(width: 400, height: 220)
        }
    }

    private final class TickCounter { var count = 0 }

    private func spin(until condition: () -> Bool, timeout: TimeInterval) {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition(), Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.02)) }
    }

    func testLyricsHiddenAndShownAgainFromSettingsComeBack() throws {
        let music = FakeMusicApp()
        music.queue = [FakeMusicApp.Song(id: "00000000000000A1", name: "First Light", album: "Dawn")]
        music.state = .playing
        music.position = 30
        let harness = PlayerHarness(music: music)
        harness.start()
        let model = LyricsViewModel.shared, extras = LunalyrExtras.shared
        let saved = (lines: model.lines, plain: model.plainLines, notFound: model.notFound,
                     loading: model.isLoading, hidden: extras.lyricsHidden)
        func line(_ texts: [String], start: Double) -> LyricsFetcher.Line {
            let words = texts.enumerated().map { LyricsFetcher.Word(text: $0.element, start: start + Double($0.offset) * 0.5,
                                                                  end: start + Double($0.offset) * 0.5 + 0.45) }
            return .init(start: start, end: words.last?.end ?? start, text: texts.joined(separator: " "), words: words,
                         isInstrumental: false, wordTimed: true)
        }
        model.plainLines = []
        model.notFound = false
        model.isLoading = false
        model.lines = [line(["one", "two"], start: 10), line(["three", "four"], start: 20), line(["five"], start: 40)]
        extras.lyricsHidden = false
        let pace = LyricPace(), ticks = TickCounter()
        let window = NSWindow(contentRect: NSRect(x: 2, y: 2, width: 400, height: 220), styleMask: [.borderless],
                              backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.alphaValue = 0.03
        window.contentView = NSHostingView(rootView: ClockedLayer(pace: pace, ticks: ticks)
            .environmentObject(harness.monitor))
        window.orderFrontRegardless()
        defer {
            window.orderOut(nil)
            window.contentView = nil
            extras.lyricsHidden = saved.hidden
            model.lines = saved.lines
            model.plainLines = saved.plain
            model.notFound = saved.notFound
            model.isLoading = saved.loading
        }
        spin(until: { ticks.count > 3 }, timeout: 2)
        // A timeline only ticks on a display that is awake.
        try XCTSkipIf(ticks.count <= 3, "No display is drawing frames here (none attached, or it is asleep).")
        // Hidden from Settings: no note, the lyrics fade out and the clock stops.
        extras.lyricsHidden = true
        spin(until: { pace.rests }, timeout: 3)
        XCTAssertTrue(pace.rests, "hidden lyrics rest: \(pace.level)")
        let resting = ticks.count
        // Shown again the same way: the clock must start, or nothing draws
        // the fade back in.
        extras.lyricsHidden = false
        spin(until: { ticks.count > resting + 5 }, timeout: 3)
        XCTAssertGreaterThan(ticks.count, resting + 5, "the lyric clock started again")
        XCTAssertFalse(pace.rests)
    }

    func testTheNewestReportedLevelIsTheOnePublished() {
        // A scene changing in the same turn as a notice finishing: the later
        // report must win, or the clock rests mid-cross-fade with no frame
        // left to correct it.
        let pace = LyricPace()
        pace.report(.resting)
        pace.report(.active)
        settle(pace)
        XCTAssertEqual(pace.level, .active)
        pace.report(.waiting)
        pace.report(.resting)
        settle(pace)
        XCTAssertEqual(pace.level, .resting)
        pace.report(.active)
        pace.report(.resting)
        settle(pace)
        XCTAssertEqual(pace.level, .resting)
    }

    func testTheColourDirectorSaysWhenItHasSettled() {
        let director = LyricInkDirector()
        let album: [SIMD3<Float>] = [SIMD3(0.8, 0.2, 0.3), SIMD3(0.2, 0.4, 0.9), SIMD3(0.3, 0.3, 0.3)]
        var ink = director.ink(.init(mode: .sungSweep, album: album, backdrop: 0.05), wall: 0)
        ink = director.ink(.init(mode: .sungSweep, album: album, backdrop: 0.05), wall: 0.1)
        XCTAssertFalse(ink.settled, "still easing in from white")
        for frame in 2...900 {
            ink = director.ink(.init(mode: .sungSweep, album: album, backdrop: 0.05), wall: Double(frame) / 60)
        }
        XCTAssertTrue(ink.settled)
        // Back to Static white: easing out of the colours is not settled...
        let back = director.ink(.init(mode: .standard, album: album, backdrop: 0.05), wall: 15.1)
        XCTAssertFalse(back.settled)
        // ...and plain white, once there, is.
        XCTAssertTrue(LyricInkDirector().ink(.init(mode: .standard), wall: 0).settled)
    }

    // MARK: Refused players

    func testTheIdleNoticeNamesARefusedPlayerAndHoldsLikeTheOthers() {
        XCTAssertEqual(LyricLayer.refusedTitle(.appleMusic), "Lunalyr can’t read Music")
        XCTAssertEqual(LyricLayer.refusedTitle(.spotify), "Lunalyr can’t read Spotify")
        XCTAssertTrue(LyricLayer.refusedDetail.contains("Privacy & Security › Automation"))
        // Refused at launch leaves no moment playback stopped; the notice
        // counts from when the refusal was first seen, then fades as usual.
        let cache = LyricLayerCache()
        let launch = Date(timeIntervalSinceReferenceDate: 1000)
        XCTAssertNil(cache.refusedSince(nil, now: launch))
        XCTAssertEqual(cache.refusedSince(.appleMusic, now: launch), launch)
        XCTAssertEqual(cache.refusedSince(.appleMusic, now: launch.addingTimeInterval(60)), launch)
        XCTAssertEqual(quiet(.idle, age: 60), .resting)
        // Allowed again, then another player refused: said again.
        XCTAssertNil(cache.refusedSince(nil, now: launch.addingTimeInterval(120)))
        let later = launch.addingTimeInterval(180)
        XCTAssertEqual(cache.refusedSince(.spotify, now: later), later)
    }
}
