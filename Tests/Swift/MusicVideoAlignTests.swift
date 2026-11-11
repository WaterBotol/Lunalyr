import XCTest
@testable import VersoCustom

// Lining the music video up by dragging its own bar. Every seam is faked:
// no network, no player, no sound, no real preferences.

private struct NoVideos: MusicVideoLibrary {
    func localVideo(for song: MusicVideoSong) async -> URL? { nil }
}

private struct Playable: MusicVideoPlayability {
    func check(_ url: URL) async -> MusicVideoPlayabilityResult { .playable }
}

private struct Offline: MusicVideoHTTP {
    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) { throw URLError(.notConnectedToInternet) }
}

@MainActor
final class MusicVideoAlignTests: XCTestCase {
    private var defaults: UserDefaults!

    override func setUp() async throws {
        let name = "LunalyrTests.MusicVideoAlign.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
    }

    private let song = MusicVideoSong(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 200)
    private var track: NowPlayingMonitor.Track {
        NowPlayingMonitor.Track(title: "Blinding Lights", artist: "The Weeknd", duration: 200, album: "After Hours")
    }
    private let start = Date(timeIntervalSince1970: 1_800_000_000)
    private let videoID = "dQw4w9WgXcQ"
    private var source: MusicVideoSource { .youTube(videoID) }
    private let width = ScrubMath.barWidth

    /// Song clock and uptime the tests move by hand.
    private final class Clock {
        var song = 60.0
        var playing = true
        var uptime: TimeInterval = 100
    }

    private func showing(_ clock: Clock, lyrics: [MusicVideoLyricLine]? = nil)
        async -> (MusicVideoController, MusicVideoOffsets) {
        let resolver = MusicVideoResolver(library: NoVideos(), playability: Playable(), http: Offline(),
                                          cache: MusicVideoCache(defaults: defaults),
                                          links: MusicVideoLinks(defaults: defaults), now: { [start] in start })
        resolver.links.set(videoID, for: song.key)
        let offsets = MusicVideoOffsets(defaults: defaults)
        let controller = MusicVideoController(resolver: resolver, offsets: offsets,
                                              songClock: { (clock.song, clock.playing) }, lyrics: { _ in lyrics },
                                              uptime: { clock.uptime })
        controller.reduceMotion = { false }
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        // The player says how long the video is (an estimate: 15 s of intro)
        // and where it is.
        controller.noteVideoDuration(215, of: source)
        controller.noteVideoSample(time: clock.song + 15, playing: true, of: source)
        return (controller, offsets)
    }

    // MARK: Availability

    func testAlignNeedsAPlayingSeekableVideo() {
        typealias A = MusicVideoAlignAvailability
        XCTAssertEqual(A.of(showing: false, reduceMotion: false, songPlaying: true, responding: true),
                       .unavailable(A.noVideo))
        XCTAssertEqual(A.of(showing: true, reduceMotion: true, songPlaying: true, responding: true),
                       .unavailable(A.reduceMotion))
        XCTAssertEqual(A.of(showing: true, reduceMotion: false, songPlaying: false, responding: true),
                       .unavailable(A.notPlaying))
        XCTAssertEqual(A.of(showing: true, reduceMotion: false, songPlaying: true, responding: false),
                       .unavailable(A.notResponding))
        XCTAssertEqual(A.of(showing: true, reduceMotion: false, songPlaying: true, responding: true), .available)
        XCTAssertNil(A.available.reason)
    }

    func testTheControllerSaysWhyAlignCantStart() async {
        let clock = Clock()
        let (controller, _) = await showing(clock)
        clock.playing = false
        XCTAssertEqual(controller.alignAvailability.reason, MusicVideoAlignAvailability.notPlaying)
        XCTAssertFalse(controller.beginAlign())
        XCTAssertFalse(controller.isAligning)
        clock.playing = true
        clock.uptime += MusicVideoController.respondingWithin + 1
        XCTAssertEqual(controller.alignAvailability.reason, MusicVideoAlignAvailability.notResponding,
                       "a player that stopped answering can't be sought")
        controller.noteVideoSample(time: 76, playing: true, of: source)
        controller.reduceMotion = { true }
        XCTAssertEqual(controller.alignAvailability.reason, MusicVideoAlignAvailability.reduceMotion)
        controller.reduceMotion = { false }
        XCTAssertTrue(controller.alignAvailability.isAvailable)
        controller.update(track: nil, enabled: true, online: false)
        XCTAssertEqual(controller.alignAvailability.reason, MusicVideoAlignAvailability.noVideo)
    }

    // MARK: The bar

    func testTheBarDrawsTheSongInsideTheVideo() {
        let layout = MusicVideoAlignBar.layout(videoLength: 200, songDuration: 150, offset: 20, songTime: 30, width: 400)
        XCTAssertEqual(layout, .init(spanStart: 40, spanEnd: 340, knob: 100))
        let clipped = MusicVideoAlignBar.layout(videoLength: 100, songDuration: 150, offset: -10, songTime: 5, width: 100)
        XCTAssertEqual(clipped, .init(spanStart: 0, spanEnd: 100, knob: 0), "kept inside the bar")
        XCTAssertEqual(MusicVideoAlignBar.videoLength(reported: 215, songDuration: 200, offset: 15), 215)
        XCTAssertEqual(MusicVideoAlignBar.videoLength(reported: nil, songDuration: 200, offset: 15), 215,
                       "before the player says: long enough for the song at its offset")
        XCTAssertEqual(MusicVideoAlignBar.videoLength(reported: .nan, songDuration: 200, offset: -5), 200)
        XCTAssertEqual(MusicVideoAlignBar.secondsPerPoint(videoLength: 228, width: 456), 0.5)
        XCTAssertEqual(MusicVideoAlignBar.title(offset: 3.44), "Video +3.4 s")
        XCTAssertEqual(MusicVideoAlignBar.title(offset: -0.5), "Video \u{2212}0.5 s")
        XCTAssertEqual(MusicVideoAlignBar.spoken(offset: 3.44), "The video runs 3.4 s ahead of the song")
        XCTAssertEqual(MusicVideoAlignBar.spoken(offset: -2), "The video runs 2.0 s behind the song")
        XCTAssertEqual(MusicVideoAlignBar.spoken(offset: 0.01), "The video runs with the song")
    }

    func testDragBoundsKeepTheKnobInsideTheVideo() {
        let bounds = MusicVideoAlignBar.bounds(videoLength: 215, songTime: 60, current: 15)
        XCTAssertEqual(bounds.lowerBound, -60, "the video's start")
        XCTAssertEqual(bounds.upperBound, 215 - MusicVideoAlignBar.endGuard - 60, accuracy: 1e-9, "just before its end")
        let outside = MusicVideoAlignBar.bounds(videoLength: 215, songTime: 10, current: -30)
        XCTAssertEqual(outside.lowerBound, -30, "an offset already outside is kept, not pulled in")
    }

    // MARK: The drag

    func testTheDragMovesTheOffsetByTheHandsTravelWithFineSteps() {
        var drag = MusicVideoAlignDrag(x: 100, y: 0, knobX: 104, offset: 10, secondsPerPoint: 0.5, bounds: -50...50)
        XCTAssertEqual(drag.offset, 10, "a press on the knob keeps it where it is")
        XCTAssertFalse(drag.changed)
        drag.move(x: 110, y: 0)
        XCTAssertEqual(drag.offset, 15, "10 pt at full speed: 5 s")
        XCTAssertNil(drag.rateLabel)
        drag.move(x: 110, y: 130)
        XCTAssertEqual(drag.rateLabel, "Fine")
        XCTAssertEqual(drag.offset, 15, "moving away from the bar does not move the video")
        drag.move(x: 112, y: 130)
        XCTAssertEqual(drag.offset, 15.1, accuracy: 1e-9, "a tenth of a second for 2 pt")
        drag.move(x: 112, y: 0, option: true)
        drag.move(x: 114, y: 0, option: true)
        XCTAssertEqual(drag.offset, 15.2, accuracy: 1e-9, "⌥ is fine too")
        XCTAssertTrue(drag.changed)
        XCTAssertEqual(drag.offsetAtPress, 10)
    }

    func testAPressAwayFromTheKnobBringsItThere() {
        let drag = MusicVideoAlignDrag(x: 200, y: 0, knobX: 100, offset: 10, secondsPerPoint: 0.5, bounds: -50...100)
        XCTAssertEqual(drag.offset, 60)
        let clamped = MusicVideoAlignDrag(x: 400, y: 0, knobX: 100, offset: 10, secondsPerPoint: 0.5, bounds: -50...100)
        XCTAssertEqual(clamped.offset, 100)
    }

    func testTheEndsStopTheDragAndAreFeltOnce() {
        var drag = MusicVideoAlignDrag(x: 100, y: 0, knobX: 100, offset: 0, secondsPerPoint: 1, bounds: -5...5)
        XCTAssertFalse(drag.move(x: 103, y: 0))
        XCTAssertTrue(drag.move(x: 110, y: 0), "reached the end")
        XCTAssertEqual(drag.offset, 5)
        XCTAssertFalse(drag.move(x: 120, y: 0), "felt once")
        XCTAssertEqual(drag.offset, 5)
        drag.move(x: 118, y: 0)
        XCTAssertEqual(drag.offset, 3, "back off it at once")
    }

    // MARK: Seeking during the drag

    func testTheDragSeeksAsTheOffsetMovesAtEachPlayersPace() {
        let sample = MusicVideoDrift.Sample(videoTime: 75, videoPlaying: true)
        var youTube = MusicVideoAlignSeeker(style: .ledSeek, offset: 15)
        XCTAssertEqual(youTube.decide(offset: 15, target: 75, sample: sample, shouldPlay: true, now: 10, lead: 0.3),
                       .none, "no move, no seek")
        XCTAssertEqual(youTube.decide(offset: 17, target: 77, sample: sample, shouldPlay: true, now: 10, lead: 0.3),
                       .seek(to: 77.3, thenPlay: true))
        XCTAssertEqual(youTube.decide(offset: 18, target: 78, sample: sample, shouldPlay: true, now: 10.2, lead: 0.3),
                       .none, "YouTube's player is sought seldom")
        XCTAssertEqual(youTube.decide(offset: 18, target: 78.2, sample: sample, shouldPlay: true, now: 10.4, lead: 0.3),
                       .seek(to: 78.5, thenPlay: true), "the move that came too soon is sent by a later check")
        XCTAssertEqual(youTube.decide(offset: 18.01, target: 78.3, sample: sample, shouldPlay: true, now: 11, lead: 0.3),
                       .none, "a hundredth is not worth a seek")
        let far = MusicVideoDrift.Sample(videoTime: 60, videoPlaying: true)
        XCTAssertEqual(youTube.decide(offset: 18, target: 78.4, sample: far, shouldPlay: true, now: 20, lead: 0.3),
                       .none, "no drift correction while the hand holds the video")

        var library = MusicVideoAlignSeeker(style: .rate, offset: 15)
        XCTAssertEqual(library.decide(offset: 15.5, target: 75.5, sample: sample, shouldPlay: true, now: 10, lead: 0.05),
                       .seek(to: 75.55, thenPlay: true))
        XCTAssertEqual(library.decide(offset: 16, target: 76, sample: sample, shouldPlay: true, now: 10.1, lead: 0.05),
                       .seek(to: 76.05, thenPlay: true), "AVPlayer keeps up with the hand")
        let stopped = MusicVideoDrift.Sample(videoTime: 76, videoPlaying: false)
        XCTAssertEqual(library.decide(offset: 16, target: 76, sample: stopped, shouldPlay: true, now: 10.2, lead: 0.05),
                       .play)
        XCTAssertEqual(library.decide(offset: 16, target: nil, sample: sample, shouldPlay: true, now: 10.3, lead: 0.05),
                       .pause, "past the video's end it holds")
    }

    func testTheSyncLoopSeeksWithTheHandThenCorrectionResumes() {
        final class Surface: MusicVideoPlaybackSurface {
            var time = 75.0, playing = true
            var seeks: [Double] = [], rates: [Double] = []
            var seekLead: Double { 0.05 }
            var correctionStyle: MusicVideoCorrectionStyle { .rate }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
                done(.init(videoTime: time, videoPlaying: playing), 215)
            }
            func playVideo() { playing = true }
            func pauseVideo() { playing = false }
            func seekVideo(to seconds: Double) { seeks.append(seconds); time = seconds }
            func setVideoRate(_ rate: Double) { rates.append(rate) }
        }
        let surface = Surface()
        var offset = 15.0, dragging = false, now: TimeInterval = 100
        let loop = MusicVideoSyncLoop(songTime: { 60 }, songPlaying: { true }, offset: { offset }, uptime: { now })
        loop.alignDragging = { dragging }
        loop.surface = surface
        loop.tick()
        XCTAssertTrue(surface.seeks.isEmpty, "in step")

        dragging = true
        offset = 17
        now += 0.07
        loop.tick()
        XCTAssertEqual(surface.seeks, [77.05], "the video follows the hand")
        XCTAssertNotNil(loop.seeker)
        surface.time = 74 // a stale sample while the seek lands
        now += 0.07
        loop.tick()
        XCTAssertEqual(surface.seeks.count, 1, "no correction while the hand holds it")
        XCTAssertTrue(surface.rates.allSatisfy { $0 == 1 }, "no rate easing either")

        dragging = false
        surface.time = 77.1
        now += 0.07
        loop.tick()
        XCTAssertNil(loop.seeker)
        XCTAssertEqual(surface.seeks.count, 1, "the drag's last seek is given time to land")
        surface.time = 79
        now += MusicVideoDrift.seekSettle
        loop.tick()
        XCTAssertEqual(surface.seeks.last ?? 0, 77.05, accuracy: 1e-9,
                       "then correction resumes, with the new offset")
    }

    func testTheLoopChecksQuicklyOnlyWhileTheBarIsDragged() {
        final class Still: MusicVideoPlaybackSurface {
            var seekLead: Double { 0 }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
                done(.init(videoTime: 0, videoPlaying: true), nil)
            }
            func playVideo() {}
            func pauseVideo() {}
            func seekVideo(to seconds: Double) {}
        }
        let surface = Still()
        var dragging = false
        let loop = MusicVideoSyncLoop(songTime: { 0 }, songPlaying: { true }, offset: { 0 }, uptime: { 0 })
        loop.alignDragging = { dragging }
        loop.surface = surface
        loop.start()
        defer { loop.stop() }
        XCTAssertEqual(loop.checkInterval, MusicVideoSyncLoop.interval)
        dragging = true
        loop.tick()
        XCTAssertEqual(loop.checkInterval, MusicVideoSyncLoop.dragInterval)
        dragging = false
        loop.tick()
        XCTAssertEqual(loop.checkInterval, MusicVideoSyncLoop.interval)
    }

    // MARK: Keys

    func testAlignKeys() {
        typealias K = PlayerKeyMap.KeyCode
        func key(_ code: UInt16, shift: Bool = false, option: Bool = false, command: Bool = false,
                 editing: Bool = false) -> MusicVideoAlignKey? {
            MusicVideoAlignKey.action(keyCode: code, shift: shift, option: option, command: command, control: false,
                                      editingText: editing)
        }
        XCTAssertEqual(key(K.left), .nudge(-0.1))
        XCTAssertEqual(key(K.right), .nudge(0.1))
        XCTAssertEqual(key(K.left, shift: true), .nudge(-1))
        XCTAssertEqual(key(K.right, shift: true), .nudge(1))
        XCTAssertEqual(key(K.returnKey), .done)
        XCTAssertEqual(key(K.enter), .done)
        XCTAssertEqual(key(K.escape), .cancel)
        XCTAssertNil(key(K.right, option: true), "⌥→ stays the player's")
        XCTAssertNil(key(K.right, command: true), "⌘→ skips the song as usual")
        XCTAssertNil(key(K.space), "Space still plays and pauses")
        XCTAssertNil(key(K.left, editing: true), "the search field keeps its arrows")
    }

    // MARK: Controller

    func testDraggingTheVideosBarKeepsTheOffsetForThatVideo() async throws {
        let clock = Clock()
        let (controller, offsets) = await showing(clock)
        XCTAssertEqual(controller.currentOffset, 15)
        XCTAssertEqual(controller.offsetOrigin, .estimated)
        XCTAssertTrue(controller.beginAlign())
        XCTAssertTrue(controller.isAligning)
        let display = try XCTUnwrap(controller.alignDisplay())
        XCTAssertEqual(display.videoLength, 215)
        XCTAssertEqual(display.videoTime, 75)
        XCTAssertEqual(display.title, "Video +15.0 s")
        XCTAssertEqual(display.detail, "Drag until the picture matches the music")
        let perPoint = 215 / width
        let knob = display.layout(width: width).knob

        controller.alignMouseDown(x: knob, y: 0, width: width)
        XCTAssertTrue(controller.isAlignDragging)
        controller.alignMouseDragged(x: knob + 10, y: 0)
        XCTAssertEqual(controller.currentOffset, 15 + 10 * perPoint, accuracy: 1e-9, "the video moves with the hand")
        XCTAssertEqual(controller.offsetOrigin, .estimated, "nothing is kept until the release")
        XCTAssertFalse(offsets.hasManual(song.key))
        controller.alignMouseDragged(x: knob + 10, y: 130)
        controller.alignMouseDragged(x: knob + 20, y: 130)
        XCTAssertEqual(controller.alignDisplay()?.rateLabel, "Fine")
        XCTAssertEqual(controller.alignDisplay()?.dragging, true)
        // The song plays on: the offset under the hand does not drift.
        clock.song = 61.5
        let expected = 15 + 10 * perPoint + 1 * perPoint
        XCTAssertEqual(controller.currentOffset, expected, accuracy: 1e-9)
        controller.alignMouseUp(x: knob + 20, y: 130)
        XCTAssertFalse(controller.isAlignDragging)
        XCTAssertEqual(controller.currentOffset, (expected * 100).rounded() / 100, accuracy: 1e-9,
                       "kept, to a hundredth")
        XCTAssertEqual(controller.offsetOrigin, .manual, "the user's own: it beats the estimate")
        XCTAssertTrue(offsets.hasManual(song.key, video: "yt:\(videoID)"))
        XCTAssertFalse(offsets.hasManual(song.key, video: "yt:BBBBBBBBBBB"), "for this video only")
        XCTAssertEqual(controller.alignDisplay()?.detail, "Kept for this video")
        XCTAssertTrue(controller.isAligning, "align mode stays on for another go")
        controller.alignDone()
        XCTAssertFalse(controller.isAligning)
        XCTAssertNil(controller.alignDisplay())
        XCTAssertEqual(controller.offsetOrigin, .manual)
    }

    func testAClickWithoutAMoveKeepsNothing() async {
        let clock = Clock()
        let (controller, offsets) = await showing(clock)
        controller.beginAlign()
        let knob = controller.alignDisplay()!.layout(width: width).knob
        controller.alignMouseDown(x: knob + 2, y: 0, width: width)
        controller.alignMouseUp(x: knob + 2, y: 0)
        XCTAssertFalse(offsets.hasManual(song.key))
        XCTAssertEqual(controller.currentOffset, 15)
    }

    func testEscDuringADragSpringsBackAndEscAfterPutsTheOldOffsetBack() async {
        let clock = Clock()
        let (controller, offsets) = await showing(clock)
        controller.nudgeOffset(by: 1) // the user's own 16 s, before align mode
        controller.beginAlign()
        let knob = controller.alignDisplay()!.layout(width: width).knob
        controller.alignMouseDown(x: knob, y: 0, width: width)
        controller.alignMouseDragged(x: knob + 40, y: 0)
        XCTAssertNotEqual(controller.currentOffset, 16)
        controller.alignCancel()
        XCTAssertFalse(controller.isAlignDragging)
        XCTAssertTrue(controller.isAligning, "Esc in a drag only drops the drag")
        XCTAssertEqual(controller.currentOffset, 16)

        controller.alignMouseDown(x: knob, y: 0, width: width)
        controller.alignMouseDragged(x: knob + 40, y: 0)
        controller.alignMouseUp(x: knob + 40, y: 0)
        controller.alignNudge(by: MusicVideoAlignKey.bigStep)
        XCTAssertNotEqual(controller.currentOffset, 16)
        controller.alignCancel()
        XCTAssertFalse(controller.isAligning)
        XCTAssertEqual(controller.currentOffset, 16, "the offset from before align mode")
        XCTAssertEqual(offsets.manualEntry(song.key), .init(value: 16, video: "yt:\(videoID)"))
    }

    func testCancelGoesBackToTheEstimateWhenThereWasNoOffsetOfTheUsers() async {
        let clock = Clock()
        let (controller, offsets) = await showing(clock)
        controller.beginAlign()
        controller.alignNudge(by: -MusicVideoAlignKey.step)
        XCTAssertEqual(controller.currentOffset, 14.9, accuracy: 1e-9)
        XCTAssertEqual(controller.offsetOrigin, .manual)
        controller.alignCancel()
        XCTAssertFalse(offsets.hasManual(song.key))
        XCTAssertEqual(controller.offsetOrigin, .estimated)
        XCTAssertEqual(controller.currentOffset, 15)
    }

    func testResetReturnsToTheEstimateAndStaysInAlignMode() async {
        let clock = Clock()
        let (controller, offsets) = await showing(clock)
        controller.beginAlign()
        controller.alignNudge(by: 2)
        XCTAssertEqual(controller.currentOffset, 17)
        controller.alignReset()
        XCTAssertTrue(controller.isAligning)
        XCTAssertFalse(offsets.hasManual(song.key))
        XCTAssertEqual(controller.currentOffset, 15)
        XCTAssertEqual(controller.offsetOrigin, .estimated)
        XCTAssertEqual(controller.alignDisplay()?.detail, "Estimated")
    }

    func testDragNudgesAndTapShareOneSavedOffset() async {
        let clock = Clock()
        clock.song = 2
        let lines = [MusicVideoLyricLine(start: 15, text: "I've been tryna call"),
                     MusicVideoLyricLine(start: 20, text: "I've been on my own for long enough")]
        let (controller, offsets) = await showing(clock, lyrics: lines)
        // Tap to sync: the singing starts 23.4 s into the video.
        controller.tapSyncButton()
        clock.song = 23.6
        controller.noteVideoSample(time: 23.5, playing: true, of: source)
        clock.uptime += 0.1
        controller.tapSyncButton()
        XCTAssertEqual(controller.currentOffset, 8.4, accuracy: 1e-9)

        // The drag starts from the tap's offset…
        XCTAssertTrue(controller.beginAlign())
        let knob = controller.alignDisplay()!.layout(width: width).knob
        controller.alignMouseDown(x: knob, y: 0, width: width)
        XCTAssertEqual(controller.currentOffset, 8.4, accuracy: 1e-9)
        controller.alignMouseDragged(x: knob + 1, y: 130)
        controller.alignMouseUp(x: knob + 1, y: 130)
        let dragged = controller.currentOffset
        XCTAssertEqual(dragged, 8.4 + 0.1 * 215 / width, accuracy: 0.006)
        XCTAssertEqual(controller.tapState, .idle, "a drag starts the taps over, as a nudge does")
        // …and the menu's nudges move the same one.
        controller.videoEarlier()
        XCTAssertEqual(controller.currentOffset, dragged + MusicVideoController.fineNudge, accuracy: 0.006)
        controller.alignNudge(by: -MusicVideoAlignKey.step)
        XCTAssertEqual(controller.currentOffset, dragged + 0.15, accuracy: 0.006)
        XCTAssertEqual(offsets.values.count, 1, "one offset for the song")
        XCTAssertEqual(offsets.manualVideos[song.key], "yt:\(videoID)")
    }

    func testASongChangeEndsAlignModeKeepingWhatWasSet() async {
        let clock = Clock()
        let (controller, offsets) = await showing(clock)
        controller.beginAlign()
        controller.alignNudge(by: 1)
        let knob = controller.alignDisplay()!.layout(width: width).knob
        controller.alignMouseDown(x: knob, y: 0, width: width)
        controller.alignMouseDragged(x: knob + 30, y: 0)
        controller.update(track: NowPlayingMonitor.Track(title: "Save Your Tears", artist: "The Weeknd", duration: 215),
                          enabled: true, online: false)
        XCTAssertFalse(controller.isAligning)
        XCTAssertFalse(controller.isAlignDragging)
        XCTAssertNil(controller.align)
        XCTAssertEqual(offsets.offset(for: song.key, video: "yt:\(videoID)"), 16, "the drag in flight is dropped")
    }

    func testAPressWhoseReleaseNeverCameHoldsNothing() async {
        let clock = Clock()
        let (controller, offsets) = await showing(clock)
        controller.beginAlign()
        let knob = controller.alignDisplay()!.layout(width: width).knob
        controller.alignMouseDown(x: knob, y: 0, width: width)
        controller.alignMouseDragged(x: knob + 50, y: 0)
        XCTAssertTrue(controller.isAlignDragging)
        // The view went away under the hand; the button is up now.
        controller.buttonsHeld = { false }
        XCTAssertFalse(controller.isAlignDragging, "the sync loop corrects the video again")
        XCTAssertEqual(controller.currentOffset, 15, "and keeps it where it was")
        controller.buttonsHeld = { true }
        controller.alignMouseDown(x: knob, y: 0, width: width)
        XCTAssertEqual(controller.currentOffset, 15, "a new press starts afresh")
        controller.alignMouseUp(x: knob, y: 0)
        XCTAssertFalse(offsets.hasManual(song.key))
    }

    func testRestoringAManualEntry() {
        let offsets = MusicVideoOffsets(defaults: defaults)
        XCTAssertNil(offsets.manualEntry("song"))
        offsets.set(3.25, for: "song", video: "yt:AAAAAAAAAAA")
        let entry = offsets.manualEntry("song")
        XCTAssertEqual(entry, .init(value: 3.25, video: "yt:AAAAAAAAAAA"))
        offsets.set(9, for: "song", video: "yt:BBBBBBBBBBB")
        offsets.restoreManual(entry, for: "song")
        XCTAssertEqual(offsets.offset(for: "song", video: "yt:AAAAAAAAAAA"), 3.25)
        XCTAssertFalse(offsets.hasManual("song", video: "yt:BBBBBBBBBBB"))
        offsets.restoreManual(nil, for: "song")
        XCTAssertFalse(offsets.hasManual("song"))
        XCTAssertNil(MusicVideoOffsets(defaults: defaults).manualEntry("song"), "saved")
    }

    func testTheDisplaysWords() {
        var display = MusicVideoAlignDisplay(videoLength: 215, songDuration: 200, offset: 3.4, songTime: 10,
                                             origin: .estimated)
        XCTAssertEqual(display.detail, "Drag until the picture matches the music")
        display.origin = .manual
        XCTAssertEqual(display.detail, "Set by you \u{00B7} drag to adjust")
        display.dragging = true
        XCTAssertEqual(display.detail, "Release to keep")
        display.rateLabel = "Quarter speed"
        XCTAssertEqual(display.detail, "Quarter speed")
        display.dragging = false
        display.touched = true
        XCTAssertEqual(display.detail, "Kept for this video")
        display.origin = .estimated
        XCTAssertEqual(display.detail, "Estimated", "after Reset")
        display.origin = .none
        XCTAssertEqual(display.detail, "No offset")
        display.songTime = 300
        XCTAssertEqual(display.videoTime, 215, "never past the video's end")
    }
}
