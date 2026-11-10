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
        let (freeze, host, timers) = makeTimedFreeze()
        return (freeze, host, { timers.fire(600) })
    }

    /// Timers by delay, fired by hand.
    @MainActor
    final class Timers {
        var pending: [(id: Int, delay: TimeInterval, work: @MainActor () -> Void)] = []
        private var next = 0
        func add(_ delay: TimeInterval, _ work: @escaping @MainActor () -> Void) -> () -> Void {
            next += 1
            let id = next
            pending.append((id, delay, work))
            return { [weak self] in self?.pending.removeAll { $0.id == id } }
        }
        func fire(_ delay: TimeInterval) {
            let due = pending.filter { $0.delay == delay }
            pending.removeAll { $0.delay == delay }
            due.forEach { $0.work() }
        }
        func count(_ delay: TimeInterval) -> Int { pending.filter { $0.delay == delay }.count }
    }

    private func makeTimedFreeze() -> (MusicVideoFrozenFrame, FakeFreezeHost, Timers) {
        let timers = Timers()
        let freeze = MusicVideoFrozenFrame(longPause: 600) { delay, work in
            XCTAssertTrue(delay == 600 || delay == MusicVideoFrozenFrame.seekHold, "\(delay)")
            return timers.add(delay, work)
        }
        let host = FakeFreezeHost()
        freeze.host = host
        return (freeze, host, timers)
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
        XCTAssertEqual(MusicVideoHold.of(surfacePaused: true, songPlaying: false, target: 12), .wallpaperPaused,
                       "covered or wallpaper paused: the frame is held too")
        XCTAssertEqual(MusicVideoHold.of(surfacePaused: true, songPlaying: true, target: 12), .wallpaperPaused)
        XCTAssertEqual(MusicVideoHold.of(surfacePaused: false, songPlaying: false, target: nil), .other,
                       "before the video starts or past its end")
        XCTAssertEqual(MusicVideoHold.of(surfacePaused: true, songPlaying: true, target: nil), .other)
        XCTAssertEqual([MusicVideoHold.live, .userPaused, .wallpaperPaused, .outgoing, .other].filter(\.keepsFrame),
                       [.userPaused, .wallpaperPaused, .outgoing])
    }

    func testFrozenFrameShowsForHoldsThatKeepIt() {
        let rule = MusicVideoFrozenFrame.showsFrame
        XCTAssertTrue(rule(.userPaused, false, true), "user pause: the frozen frame")
        XCTAssertTrue(rule(.userPaused, true, true), "while the pause lands")
        XCTAssertTrue(rule(.wallpaperPaused, false, true), "Pause Wallpaper keeps the picture, not the stand-in")
        XCTAssertTrue(rule(.outgoing, false, true), "a song change fades the old video's picture out")
        XCTAssertFalse(rule(.other, false, true), "outside the video")
        XCTAssertTrue(rule(.live, false, true), "resumed, not playing yet: kept until it plays")
        XCTAssertFalse(rule(.live, true, true), "playing again")
        for hold in [MusicVideoHold.live, .userPaused, .wallpaperPaused, .outgoing, .other] {
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
        XCTAssertEqual(surface.holds.last, .wallpaperPaused, "covered or paused wallpaper")
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
        // Outside the video's span.
        let (outside, outsideHost, _) = makeFreeze()
        outside.notePlayer(showing: true)
        outside.noteHold(.other)
        outside.pause()
        XCTAssertEqual(outsideHost.events, ["pause"], "no picture: the gradient shows as before")
        // Buffering (or not started): the player is hidden already.
        let (freeze, host, _) = makeFreeze()
        freeze.notePlayer(showing: false)
        freeze.noteHold(.userPaused)
        freeze.pause()
        XCTAssertEqual(host.events, ["pause"])
        XCTAssertFalse(freeze.frameShown)
    }

    func testCoveringKeepsTheFrameAndLeavingTheVideoHidesIt() {
        let (freeze, host, _) = makeFreeze()
        userPause(freeze)
        freeze.noteHold(.wallpaperPaused)
        XCTAssertEqual(host.events.last, "pause", "covered or paused wallpaper: the frame stays")
        XCTAssertTrue(freeze.frameShown)
        freeze.noteHold(.other)
        XCTAssertEqual(host.events.last, "hide", "outside the video: the stand-in")
        freeze.noteHold(.userPaused)
        XCTAssertEqual(host.events.last, "show")
    }

    func testPausingTheWallpaperTakesTheFrame() {
        // Pause Wallpaper (or covering it) while the song plays: the video
        // holds its frame instead of dropping to the gradient.
        let (freeze, host, _) = makeFreeze()
        freeze.noteHold(.live)
        freeze.notePlayer(showing: true)
        freeze.noteHold(.wallpaperPaused)
        freeze.pause()
        freeze.notePlayer(showing: false)
        XCTAssertEqual(host.events, ["capture", "show", "pause"])
        XCTAssertTrue(freeze.frameShown)
        // Resumed: kept until the player plays, then handed over.
        freeze.noteHold(.live)
        XCTAssertTrue(freeze.frameShown)
        freeze.notePlayer(showing: true)
        XCTAssertEqual(host.events.last, "handOver")
    }

    func testOutgoingVideoHoldsItsFrameThroughTheSongChange() {
        // W4: the old video used to be paused and hidden, so the crossfade
        // showed the gradient. Now its picture is taken, then it pauses.
        let (freeze, host, _) = makeFreeze()
        freeze.noteHold(.live)
        freeze.notePlayer(showing: true)
        freeze.noteHold(.outgoing)
        freeze.pause()
        freeze.notePlayer(showing: false)
        XCTAssertEqual(host.events, ["capture", "show", "pause"])
        XCTAssertTrue(freeze.frameShown, "the fade shows the video's last frame")
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
        freeze.noteHold(.wallpaperPaused)
        freeze.pause()
        freeze.notePlayer(showing: false)
        XCTAssertEqual(host.events, ["capture", "show", "pause"])
        freeze.noteHold(.wallpaperPaused)
        fire()
        XCTAssertEqual(host.events, ["capture", "show", "pause", "release"])
        XCTAssertTrue(freeze.frameShown, "the picture stays while the player is closed")
        freeze.noteHold(.wallpaperPaused)
        XCTAssertEqual(host.events.last, "release", "still covered: stays closed, no second timer")
        fire()
        XCTAssertEqual(host.events.filter { $0 == "release" }.count, 1)
        freeze.noteHold(.live)
        XCTAssertEqual(host.events.last, "recreate", "shown again: a fresh player")
    }

    func testCoveredAfterAUserPauseClosesAtTheLongPauseToo() {
        let (freeze, host, fire) = makeFreeze()
        userPause(freeze)
        freeze.noteHold(.wallpaperPaused)
        fire()
        XCTAssertEqual(host.events.last, "release")
        freeze.noteHold(.userPaused)
        XCTAssertTrue(freeze.frameShown, "uncovered with the song still paused: the frozen frame, no player")
        XCTAssertFalse(host.events.contains("recreate"))
    }

    func testCoveredBrieflyKeepsThePlayer() {
        let (freeze, host, fire) = makeFreeze()
        freeze.noteHold(.live)
        freeze.noteHold(.wallpaperPaused)
        freeze.noteHold(.live)
        fire()
        XCTAssertFalse(host.events.contains("release"))
    }

    // MARK: Seeks (W8)

    func testASeekThatBuffersShowsThePictureTakenBeforeIt() {
        let (freeze, host, timers) = makeTimedFreeze()
        freeze.noteHold(.live)
        freeze.notePlayer(showing: true)
        freeze.noteSeek()
        XCTAssertEqual(host.events, ["capture"], "taken as the seek is sent")
        XCTAssertFalse(freeze.frameShown, "the player still shows")
        // The seek buffers past the grace: the player hides, the picture covers it.
        freeze.notePlayer(showing: false)
        XCTAssertEqual(host.events, ["capture", "show"])
        XCTAssertEqual(timers.count(MusicVideoFrozenFrame.seekHold), 0, "in use: no longer timed")
        freeze.noteHold(.live)
        freeze.notePlayer(showing: true)
        XCTAssertEqual(host.events.last, "handOver", "faded once the video plays again")
        XCTAssertNil(freeze.image)
    }

    func testASeekThatLandsAtOnceShowsNothingAndDropsThePicture() {
        let (freeze, host, timers) = makeTimedFreeze()
        freeze.noteHold(.live)
        freeze.notePlayer(showing: true)
        freeze.noteSeek()
        freeze.noteHold(.live)
        freeze.notePlayer(showing: true)
        XCTAssertNotNil(freeze.image, "kept for a moment in case it buffers")
        timers.fire(MusicVideoFrozenFrame.seekHold)
        XCTAssertNil(freeze.image)
        XCTAssertEqual(host.events, ["capture"], "never shown")
        // A later pause takes its own picture.
        freeze.noteHold(.userPaused)
        freeze.pause()
        XCTAssertEqual(host.events, ["capture", "capture", "show", "pause"])
    }

    func testAPauseRightAfterASeekTakesAFreshPicture() {
        let (freeze, host, _) = makeTimedFreeze()
        freeze.noteHold(.live)
        freeze.notePlayer(showing: true)
        freeze.noteSeek()
        freeze.noteHold(.userPaused)
        XCTAssertNil(freeze.image, "the seek's picture is dropped")
        freeze.pause()
        XCTAssertEqual(host.events, ["capture", "capture", "show", "pause"])
    }

    func testNoSeekPictureUnlessPlaying() {
        let (freeze, host, _) = makeTimedFreeze()
        freeze.noteHold(.live)
        freeze.notePlayer(showing: false)
        freeze.noteSeek()
        freeze.noteHold(.userPaused)
        freeze.notePlayer(showing: true)
        freeze.noteSeek()
        XCTAssertEqual(host.events, [], "hidden, or not live: nothing to take")
    }

    func testBufferingHidesThePlayerOnlyAfterAGrace() {
        let grace = YouTubeEmbed.bufferingGrace
        XCTAssertEqual(YouTubeEmbed.hideDelay(buffering: true, hold: .live, hasFrame: false), grace)
        XCTAssertGreaterThan(grace, 0.1)
        XCTAssertLessThanOrEqual(grace, 0.5)
        XCTAssertEqual(YouTubeEmbed.hideDelay(buffering: true, hold: .live, hasFrame: true), 0,
                       "a seek's picture covers it at once instead")
        XCTAssertEqual(YouTubeEmbed.hideDelay(buffering: false, hold: .live, hasFrame: false), 0, "a pause or the end")
        for hold in [MusicVideoHold.userPaused, .wallpaperPaused, .outgoing, .other] {
            XCTAssertEqual(YouTubeEmbed.hideDelay(buffering: true, hold: hold, hasFrame: false), 0)
        }
        let html = YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0)
        XCTAssertTrue(html.contains("if (state === 3) return;"), "the page itself does not fade out for buffering")
        XCTAssertFalse(html.contains("transition:opacity .5s}"), "eased like the other media reveals")
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
        XCTAssertEqual(surface.holds.last, .outgoing, "its frame is held for the fade")
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
