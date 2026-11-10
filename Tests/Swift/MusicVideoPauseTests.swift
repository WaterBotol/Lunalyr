import AppKit
import XCTest
@testable import VersoCustom

// A paused song holds the music video's frame. Every seam is faked: no web
// view, no player, no network, no preferences.

@MainActor
private final class FakeFreezeHost: MusicVideoFreezeHost {
    var events: [String] = []
    /// What the next capture returns; nil is a failed snapshot.
    var picture: CGImage? = MusicVideoPauseTests.image(gray: 0.6)
    /// Holds the capture until the test answers it.
    var deferCapture = false
    var pendingCapture: ((CGImage?) -> Void)?

    func captureFrame(_ done: @escaping @MainActor (CGImage?) -> Void) {
        events.append("capture")
        if deferCapture { pendingCapture = done } else { done(picture) }
    }
    func showFrozenFrame(_ image: CGImage) { events.append("show") }
    func hideFrozenFrame(handOver: Bool) { events.append(handOver ? "handOver" : "hide") }
    func sendPause() { events.append("pause") }
    func releasePlayer() { events.append("release") }
    func recreatePlayer() { events.append("recreate") }
}

@MainActor
final class MusicVideoPauseTests: XCTestCase {
    nonisolated static func image(gray: CGFloat) -> CGImage {
        let context = CGContext(data: nil, width: 4, height: 4, bitsPerComponent: 8, bytesPerRow: 16,
                                space: CGColorSpaceCreateDeviceRGB(),
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(CGColor(gray: gray, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 4, height: 4))
        return context.makeImage()!
    }

    /// A frozen frame whose long-pause timer the test fires by hand.
    private func makeFreeze() -> (MusicVideoFrozenFrame, FakeFreezeHost, () -> Void) {
        var pending: [@MainActor () -> Void] = []
        let freeze = MusicVideoFrozenFrame(longPause: 600) { delay, work in
            XCTAssertEqual(delay, 600)
            pending.append(work)
            return { pending.removeAll() }
        }
        let host = FakeFreezeHost()
        freeze.host = host
        return (freeze, host, { let due = pending; pending.removeAll(); due.forEach { $0() } })
    }

    /// Playing, then the user pauses the song: the loop asks for a pause.
    private func userPause(_ freeze: MusicVideoFrozenFrame) {
        freeze.noteHold(.live)
        freeze.notePlayer(showing: true)
        freeze.noteHold(.userPaused)
        freeze.pause()
        // YouTube's state 2: the player is hidden.
        freeze.notePlayer(showing: false)
    }

    // MARK: The rule

    func testHoldFollowsWhyTheVideoStopped() {
        XCTAssertEqual(MusicVideoHold.of(surfacePaused: false, songPlaying: true, target: 12), .live)
        XCTAssertEqual(MusicVideoHold.of(surfacePaused: false, songPlaying: false, target: 12), .userPaused,
                       "the user paused the song")
        XCTAssertEqual(MusicVideoHold.of(surfacePaused: true, songPlaying: false, target: 12), .other,
                       "covered, wallpaper paused or Reduce Motion")
        XCTAssertEqual(MusicVideoHold.of(surfacePaused: true, songPlaying: true, target: 12), .other)
        XCTAssertEqual(MusicVideoHold.of(surfacePaused: false, songPlaying: false, target: nil), .other,
                       "before the video starts or past its end")
    }

    func testFrozenFrameShowsOnlyForAUserPause() {
        let rule = MusicVideoFrozenFrame.showsFrame
        XCTAssertTrue(rule(.userPaused, false, true), "user pause: the frozen frame")
        XCTAssertTrue(rule(.userPaused, true, true), "while the pause lands")
        XCTAssertFalse(rule(.other, false, true), "covered, paused wallpaper, Reduce Motion, outside the video")
        XCTAssertTrue(rule(.live, false, true), "resumed, not playing yet: kept until it plays")
        XCTAssertFalse(rule(.live, true, true), "playing again")
        for hold in [MusicVideoHold.live, .userPaused, .other] {
            XCTAssertFalse(rule(hold, false, false), "no picture, the gradient as before")
        }
        // The player itself still shows only while it plays.
        XCTAssertEqual([-1, 0, 1, 2, 3, 5].filter(YouTubeEmbed.showsVideo(state:)), [1])
    }

    func testSyncLoopTellsTheSurfaceWhy() {
        final class Surface: MusicVideoPlaybackSurface {
            var time = 30.0, playing = true
            var holds: [MusicVideoHold] = []
            var calls: [String] = []
            var seekLead: Double { 0 }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
                done(.init(videoTime: time, videoPlaying: playing), 200)
            }
            func playVideo() { calls.append("play"); playing = true }
            func pauseVideo() { calls.append("pause"); playing = false }
            func seekVideo(to seconds: Double) { calls.append("seek"); time = seconds }
            func noteHold(_ hold: MusicVideoHold) { holds.append(hold) }
        }
        var songTime = 30.0, songPlaying = true, clock = 0.0
        let surface = Surface()
        let loop = MusicVideoSyncLoop(songTime: { songTime }, songPlaying: { songPlaying }, offset: { 0 },
                                      uptime: { clock })
        loop.surface = surface
        loop.tick()
        XCTAssertEqual(surface.holds.last, .live)

        songPlaying = false
        clock += 1
        loop.tick()
        XCTAssertEqual(surface.holds.last, .userPaused, "told before the pause")
        XCTAssertEqual(surface.calls.last, "pause")

        loop.setSurfacePaused(true)
        XCTAssertEqual(surface.holds.last, .other, "covered or paused wallpaper, Reduce Motion")
        loop.stop()
        loop.setSurfacePaused(false)
        XCTAssertEqual(surface.holds.last, .userPaused)

        songTime = 250
        clock += 1
        loop.tick()
        XCTAssertEqual(surface.holds.last, .other, "past the video's end (its length is known by now)")
        songTime = -3
        loop.tick()
        XCTAssertEqual(surface.holds.last, .other, "before the video starts")
        loop.stop()
    }

    func testLibraryVideoJustPausesAndStaysVisible() {
        // AVPlayer shows its paused frame; it is never hidden for a pause.
        final class Local: MusicVideoPlaybackSurface {
            var playing = true
            var seekLead: Double { 0 }
            var correctionStyle: MusicVideoCorrectionStyle { .rate }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
                done(.init(videoTime: 30, videoPlaying: playing), 200)
            }
            func playVideo() { playing = true }
            func pauseVideo() { playing = false }
            func seekVideo(to seconds: Double) {}
        }
        let local = Local()
        let loop = MusicVideoSyncLoop(songTime: { 30 }, songPlaying: { false }, offset: { 0 }, uptime: { 0 })
        loop.surface = local
        loop.tick()
        XCTAssertFalse(local.playing)
        loop.stop()
    }

    // MARK: Pause and resume

    func testUserPauseTakesThePictureThenPauses() {
        let (freeze, host, _) = makeFreeze()
        userPause(freeze)
        XCTAssertEqual(host.events, ["capture", "show", "pause"], "picture first, shown, then the pause")
        XCTAssertTrue(freeze.frameShown)
        // Further pauses while it lands take no new picture.
        freeze.notePlayer(showing: true)
        freeze.pause()
        XCTAssertEqual(host.events, ["capture", "show", "pause", "pause"])
    }

    func testOtherPausesKeepTheFallback() {
        // Covered, wallpaper paused, Reduce Motion, outside the video.
        let (covered, coveredHost, _) = makeFreeze()
        covered.notePlayer(showing: true)
        covered.noteHold(.other)
        covered.pause()
        XCTAssertEqual(coveredHost.events, ["pause"], "no picture: the gradient shows as before")
        // Buffering (or not started): the player is hidden already.
        let (freeze, host, _) = makeFreeze()
        freeze.notePlayer(showing: false)
        freeze.noteHold(.userPaused)
        freeze.pause()
        XCTAssertEqual(host.events, ["pause"])
        XCTAssertFalse(freeze.frameShown)
    }

    func testCoveringHidesTheFrameAndUncoveringBringsItBack() {
        let (freeze, host, _) = makeFreeze()
        userPause(freeze)
        freeze.noteHold(.other)
        XCTAssertEqual(host.events.last, "hide")
        freeze.noteHold(.userPaused)
        XCTAssertEqual(host.events.last, "show")
    }

    func testResumeHandsOverOnlyOncePlaying() {
        let (freeze, host, _) = makeFreeze()
        userPause(freeze)
        host.events = []
        freeze.noteHold(.live)
        freeze.notePlayer(showing: false)
        XCTAssertEqual(host.events, [], "seeking and buffering: the frame stays")
        XCTAssertTrue(freeze.frameShown)
        freeze.notePlayer(showing: true)
        XCTAssertEqual(host.events, ["handOver"], "faded out once playing")
        XCTAssertNil(freeze.image, "out of date now")
        // The next pause takes a new picture.
        host.events = []
        freeze.noteHold(.userPaused)
        freeze.pause()
        XCTAssertEqual(host.events, ["capture", "show", "pause"])
    }

    func testFailedSnapshotFallsBackToTheGradient() {
        let (freeze, host, fire) = makeFreeze()
        host.picture = nil
        userPause(freeze)
        XCTAssertEqual(host.events, ["capture", "pause"])
        XCTAssertFalse(freeze.frameShown)
        fire()
        XCTAssertEqual(host.events.last, "release", "closed after a long pause even without a frame: nothing on screen changes")
        XCTAssertFalse(freeze.frameShown)
        // A picture with nothing in it (WebKit left the video out) is a failure too.
        XCTAssertTrue(MusicVideoFrozenFrame.looksBlank(Self.image(gray: 0)))
        XCTAssertFalse(MusicVideoFrozenFrame.looksBlank(Self.image(gray: 0.3)))
    }

    func testResumeDuringTheSnapshotDoesNotPause() {
        let (freeze, host, _) = makeFreeze()
        host.deferCapture = true
        freeze.notePlayer(showing: true)
        freeze.noteHold(.userPaused)
        freeze.pause()
        freeze.pause()
        XCTAssertEqual(host.events, ["capture"], "one picture at a time")
        freeze.noteHold(.live)
        host.pendingCapture?(Self.image(gray: 0.5))
        XCTAssertEqual(host.events, ["capture"], "playing again: nothing shown, nothing paused")
        XCTAssertFalse(freeze.frameShown)
    }

    func testNewVideoDropsTheFrame() {
        let (freeze, host, _) = makeFreeze()
        userPause(freeze)
        freeze.reset()
        XCTAssertEqual(host.events.last, "hide")
        XCTAssertNil(freeze.image)
        freeze.noteHold(.userPaused)
        XCTAssertEqual(host.events.last, "hide", "nothing to show for the new video")
    }

    // MARK: Long pause

    func testLongPauseClosesThePlayerAndResumeReopensIt() {
        let (freeze, host, fire) = makeFreeze()
        userPause(freeze)
        host.events = []
        fire()
        XCTAssertEqual(host.events, ["release"], "closed after about ten minutes")
        XCTAssertTrue(freeze.frameShown, "the frozen frame stays")
        freeze.noteHold(.userPaused)
        XCTAssertEqual(host.events, ["release"], "still paused: stays closed")

        freeze.noteHold(.live)
        XCTAssertEqual(host.events, ["release", "recreate"])
        XCTAssertTrue(freeze.frameShown, "kept while the new player loads")
        freeze.noteHold(.live)
        XCTAssertEqual(host.events.filter { $0 == "recreate" }.count, 1, "opened once")
        freeze.notePlayer(showing: true)
        XCTAssertEqual(host.events.last, "handOver")
    }

    func testResumeBeforeTheLongPauseKeepsThePlayer() {
        let (freeze, host, fire) = makeFreeze()
        userPause(freeze)
        freeze.noteHold(.live)
        fire()
        XCTAssertFalse(host.events.contains("release"))
    }

    func testCoveredForALongTimeClosesThePlayerAndShowingReopensIt() {
        // The wallpaper covered, hidden or paused for ten minutes: the hidden
        // YouTube page is closed so it uses nothing.
        let (freeze, host, fire) = makeFreeze()
        freeze.noteHold(.live)
        freeze.notePlayer(showing: true)
        freeze.noteHold(.other)
        freeze.pause()
        freeze.notePlayer(showing: false)
        XCTAssertEqual(host.events, ["pause"])
        freeze.noteHold(.other)
        fire()
        XCTAssertEqual(host.events, ["pause", "release"])
        freeze.noteHold(.other)
        XCTAssertEqual(host.events, ["pause", "release"], "still covered: stays closed, no second timer")
        fire()
        XCTAssertEqual(host.events, ["pause", "release"])
        freeze.noteHold(.live)
        XCTAssertEqual(host.events, ["pause", "release", "recreate"], "shown again: a fresh player")
    }

    func testCoveredAfterAUserPauseClosesAtTheLongPauseToo() {
        let (freeze, host, fire) = makeFreeze()
        userPause(freeze)
        freeze.noteHold(.other)
        fire()
        XCTAssertEqual(host.events.last, "release")
        freeze.noteHold(.userPaused)
        XCTAssertTrue(freeze.frameShown, "uncovered with the song still paused: the frozen frame, no player")
        XCTAssertFalse(host.events.contains("recreate"))
    }

    func testCoveredBrieflyKeepsThePlayer() {
        let (freeze, host, fire) = makeFreeze()
        freeze.noteHold(.live)
        freeze.noteHold(.other)
        freeze.noteHold(.live)
        fire()
        XCTAssertFalse(host.events.contains("release"))
    }

    // MARK: Song change and hidden surfaces

    func testAVideoFadingOutAfterASongChangeIsHeldNotSought() {
        final class Surface: MusicVideoPlaybackSurface {
            var time = 95.0, playing = true
            var calls: [String] = []
            var holds: [MusicVideoHold] = []
            var seekLead: Double { 0 }
            var correctionStyle: MusicVideoCorrectionStyle { .rate }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
                done(.init(videoTime: time, videoPlaying: playing), 200)
            }
            func playVideo() { calls.append("play"); playing = true }
            func pauseVideo() { calls.append("pause"); playing = false }
            func seekVideo(to seconds: Double) { calls.append("seek \(seconds)"); time = seconds }
            func setVideoRate(_ rate: Double) { calls.append("rate") }
            func noteHold(_ hold: MusicVideoHold) { holds.append(hold) }
        }
        // The next song has started at 0:01; the old video was at 1:35.
        var current = false
        let surface = Surface()
        let loop = MusicVideoSyncLoop(songTime: { 1 }, songPlaying: { true }, offset: { 0 }, uptime: { 10 })
        loop.isCurrent = { current }
        loop.surface = surface
        loop.tick()
        XCTAssertEqual(surface.calls, ["pause"], "held where it is, never sought to the new song's time")
        XCTAssertEqual(surface.holds.last, .other)
        loop.tick()
        XCTAssertEqual(surface.calls, ["pause"], "and left alone while it fades")
        // The same video found again for the next song: it follows once more.
        current = true
        loop.tick()
        XCTAssertEqual(Array(surface.calls.suffix(2)), ["seek 1.0", "play"])
        XCTAssertEqual(surface.holds.last, .live)
        loop.stop()
    }

    func testAHiddenSurfaceWithNothingToSampleStopsChecking() {
        final class Empty: MusicVideoPlaybackSurface {
            var samples = 0
            var seekLead: Double { 0 }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
                samples += 1
                done(nil, nil)
            }
            func playVideo() {}
            func pauseVideo() {}
            func seekVideo(to seconds: Double) {}
        }
        let surface = Empty()
        let loop = MusicVideoSyncLoop(songTime: { 30 }, songPlaying: { true }, offset: { 0 }, uptime: { 0 })
        loop.surface = surface
        loop.start()
        XCTAssertTrue(loop.isRunning, "visible: keeps checking until the player loads")
        loop.setSurfacePaused(true)
        loop.tick()
        XCTAssertFalse(loop.isRunning, "hidden, closed or not loaded: the loop rests")
        loop.setSurfacePaused(false)
        XCTAssertTrue(loop.isRunning, "shown again: checks again")
        loop.stop()
    }
}
