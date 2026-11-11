import AppKit
import XCTest
@testable import VersoCustom

// The music video's timing: the user's own offset (nudge, tap, drag), the
// length estimate, and nothing found automatically. Every seam is faked: no
// network, no audio, no player, no real preferences.

private final class SyncHTTP: MusicVideoHTTP, @unchecked Sendable {
    struct Reply { var status: Int; var body: Data }
    /// Replies by URL path ("/watch", "/api/timedtext").
    var byPath: [String: Reply] = [:]
    private(set) var urls: [URL] = []

    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
        urls.append(url)
        let reply = byPath[url.path] ?? Reply(status: 404, body: Data())
        return (reply.body, HTTPURLResponse(url: url, statusCode: reply.status, httpVersion: nil, headerFields: [:])!)
    }
}

private struct NoLibrary: MusicVideoLibrary {
    var video: URL?
    func localVideo(for song: MusicVideoSong) async -> URL? { video }
}

private struct Playable: MusicVideoPlayability {
    func check(_ url: URL) async -> MusicVideoPlayabilityResult { .playable }
}


@MainActor
final class MusicVideoSyncTests: XCTestCase {
    private var defaults: UserDefaults!

    override func setUp() async throws {
        let name = "LunalyrTests.MusicVideoSync.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
    }

    private let song = MusicVideoSong(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 200)
    private var track: NowPlayingMonitor.Track {
        NowPlayingMonitor.Track(title: "Blinding Lights", artist: "The Weeknd", duration: 200, album: "After Hours")
    }
    private let start = Date(timeIntervalSince1970: 1_800_000_000)

    private func resolver(http: SyncHTTP = SyncHTTP(), library: NoLibrary = NoLibrary()) -> MusicVideoResolver {
        var time: TimeInterval = 0
        return MusicVideoResolver(library: library, playability: Playable(), http: http,
                                  cache: MusicVideoCache(defaults: defaults), links: MusicVideoLinks(defaults: defaults),
                                  now: { [start] in start }, uptime: { time }, sleep: { time += $0 })
    }

    func testDurationEstimateOnlyForAPlausibleIntro() {
        XCTAssertEqual(MusicVideoDurationEstimate.intro(videoDuration: 215.04, songDuration: 200), 15)
        XCTAssertEqual(MusicVideoDurationEstimate.intro(videoDuration: 202, songDuration: 200), 2)
        XCTAssertNil(MusicVideoDurationEstimate.intro(videoDuration: 201, songDuration: 200), "too close to say")
        XCTAssertNil(MusicVideoDurationEstimate.intro(videoDuration: 260, songDuration: 200), "a different edit")
        XCTAssertNil(MusicVideoDurationEstimate.intro(videoDuration: 190, songDuration: 200))
        XCTAssertNil(MusicVideoDurationEstimate.intro(videoDuration: nil, songDuration: 200))
    }


    // MARK: Offsets: the user's own, then the estimate

    func testManualOffsetBeatsTheEstimate() {
        let offsets = MusicVideoOffsets(defaults: defaults)
        let video = "yt:dQw4w9WgXcQ"
        XCTAssertEqual(offsets.origin(for: song.key, video: video), .none)
        offsets.setEstimate(15, for: song.key, video: video)
        XCTAssertEqual(offsets.offset(for: song.key, video: video), 15)
        XCTAssertEqual(offsets.origin(for: song.key, video: video), .estimated)
        offsets.nudge(by: 0.5, for: song.key, video: video)
        XCTAssertEqual(offsets.offset(for: song.key, video: video), 15.5, accuracy: 1e-9, "nudged from the estimate")
        XCTAssertEqual(offsets.origin(for: song.key, video: video), .manual)
        offsets.set(0, for: song.key)
        XCTAssertEqual(offsets.origin(for: song.key, video: video), .manual, "a nudge to 0 is kept")
        offsets.clearManual(song.key)
        XCTAssertEqual(offsets.offset(for: song.key, video: video), 15, "back to the estimate")
        XCTAssertEqual(offsets.origin(for: song.key, video: video), .estimated)
        let reloaded = MusicVideoOffsets(defaults: defaults)
        XCTAssertEqual(reloaded.offset(for: song.key, video: video), 0, "estimates are not stored")
    }

    /// Offsets Lunalyr once found by itself (captions, soundtrack, cuts)
    /// are not applied, and are dropped from the store when it loads. The
    /// user's own are kept.
    func testSavedAutomaticOffsetsAreIgnoredAndDropped() {
        let other = MusicVideoSong(title: "Save Your Tears", artist: "The Weeknd", album: "After Hours", duration: 215)
        defaults.set([song.key: ["video": "yt:dQw4w9WgXcQ", "offset": 12.3, "at": start.timeIntervalSince1970,
                                 "kind": "captions", "clock": 2],
                      other.key: ["video": "file:/a.m4v", "offset": 8.0, "at": start.timeIntervalSince1970,
                                  "kind": "audio"]],
                     forKey: MusicVideoOffsets.retiredAutomaticKey)
        defaults.set([other.key: 4.5], forKey: MusicVideoOffsets.key)
        defaults.set([other.key: "file:/a.m4v"], forKey: MusicVideoOffsets.manualVideoKey)

        let offsets = MusicVideoOffsets(defaults: defaults)
        XCTAssertNil(defaults.object(forKey: MusicVideoOffsets.retiredAutomaticKey), "dropped on load")
        XCTAssertEqual(offsets.offset(for: song.key, video: "yt:dQw4w9WgXcQ"), 0)
        XCTAssertEqual(offsets.origin(for: song.key, video: "yt:dQw4w9WgXcQ"), .none)
        offsets.setEstimate(15, for: song.key, video: "yt:dQw4w9WgXcQ")
        XCTAssertEqual(offsets.offset(for: song.key, video: "yt:dQw4w9WgXcQ"), 15, "the estimate, not the old 12.3")
        XCTAssertEqual(offsets.offset(for: other.key, video: "file:/a.m4v"), 4.5, "the user's own is kept")
        XCTAssertEqual(offsets.origin(for: other.key, video: "file:/a.m4v"), .manual)
    }

    // MARK: Controller

    private func controller(_ resolver: MusicVideoResolver, offsets: MusicVideoOffsets? = nil,
                            songClock: @escaping @MainActor () -> (time: Double, playing: Bool) = { (0, true) },
                            lyrics: @escaping @MainActor (MusicVideoSong) -> [MusicVideoLyricLine]? = { _ in nil },
                            uptime: @escaping () -> TimeInterval = { 0 }) -> MusicVideoController {
        MusicVideoController(resolver: resolver, offsets: offsets ?? MusicVideoOffsets(defaults: defaults),
                             songClock: songClock, lyrics: lyrics, uptime: uptime)
    }

    private let libraryURL = URL(fileURLWithPath: "/Music/Media/Music Videos/Blinding Lights.m4v")

    /// A video showing, with the song playing, gets nothing but the length
    /// estimate: no lookup of its timing, and no audio capture (the music
    /// video has no way to start one any more).
    func testAVideoGetsOnlyTheEstimateUntilTheUserSetsIt() async {
        let http = SyncHTTP()
        let youTube = resolver(http: http)
        youTube.links.set("dQw4w9WgXcQ", for: song.key)
        let controller = controller(youTube)
        controller.update(track: track, enabled: true, online: true)
        await controller.settle()
        XCTAssertTrue(controller.isShowing(.youTube("dQw4w9WgXcQ")))
        XCTAssertTrue(http.urls.allSatisfy { $0.path != "/api/timedtext" && $0.path != "/watch" },
                      "no captions are fetched: \(http.urls)")
        XCTAssertEqual(controller.offsetOrigin, .none)
        XCTAssertNil(MusicVideoController.timingLabel(origin: controller.offsetOrigin))
        controller.noteVideoDuration(215, of: .youTube("dQw4w9WgXcQ"))
        XCTAssertEqual(controller.currentOffset, 15)
        XCTAssertEqual(MusicVideoController.timingLabel(origin: controller.offsetOrigin), "Estimated")

        let library = self.controller(resolver(library: NoLibrary(video: libraryURL)))
        library.update(track: track, enabled: true, online: false)
        await library.settle()
        XCTAssertTrue(library.isShowing(.local(libraryURL)))
        XCTAssertEqual(library.offsetOrigin, .none, "a library video is not listened to")
    }

    func testResetGoesBackToTheEstimate() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        let controller = controller(resolver)
        controller.update(track: track, enabled: true, online: true)
        await controller.settle()
        controller.noteVideoDuration(212, of: .youTube("dQw4w9WgXcQ"))
        XCTAssertEqual(controller.currentOffset, 12)
        controller.nudgeOffset(by: -0.5)
        XCTAssertEqual(controller.currentOffset, 11.5)
        XCTAssertEqual(MusicVideoController.timingLabel(origin: controller.offsetOrigin), "Offset set by you")
        controller.resetOffset()
        XCTAssertEqual(controller.currentOffset, 12)
        XCTAssertEqual(controller.offsetOrigin, .estimated)
    }

    func testTimingLabelStates() {
        XCTAssertEqual(MusicVideoController.timingLabel(origin: .manual), "Offset set by you")
        XCTAssertEqual(MusicVideoController.timingLabel(origin: .estimated), "Estimated")
        XCTAssertNil(MusicVideoController.timingLabel(origin: .none))
    }

    // MARK: Tap to sync

    private let sungLines = [MusicVideoLyricLine(start: 15, text: "I've been tryna call"),
                             MusicVideoLyricLine(start: 20, text: "I've been on my own for long enough"),
                             MusicVideoLyricLine(start: 58, text: "I said, ooh, I'm blinded by the lights"),
                             MusicVideoLyricLine(start: 62.5, text: "No, I can't sleep until I feel your touch"),
                             MusicVideoLyricLine(start: 67, text: "I said, ooh, I'm drowning in the night")]

    func testTapLineChoiceAndMaths() {
        XCTAssertEqual(MusicVideoTapSync.reactionTime, 0.2)
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 30, firstLine: true)?.start, 15,
                       "armed before the singing: the first line, wherever the video is")
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 3, firstLine: false)?.start, 15)
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 62.7, firstLine: false)?.start, 62.5, "just passed")
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 61, firstLine: false)?.start, 62.5, "upcoming")
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 59, firstLine: false)?.start, 58)
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 300, firstLine: false)?.start, 67)
        XCTAssertNil(MusicVideoTapSync.line(in: [], reference: 3, firstLine: true))
        XCTAssertTrue(MusicVideoTapSync.armedBeforeFirstLine(songTime: 2, lines: sungLines))
        XCTAssertFalse(MusicVideoTapSync.armedBeforeFirstLine(songTime: 16, lines: sungLines))
        XCTAssertEqual(MusicVideoTapSync.offset(videoTimeAtTap: 23.6, lineStart: 15), 8.4, accuracy: 1e-9,
                       "the reaction time is taken off the tap")
        XCTAssertEqual(MusicVideoTapSync.averaged([8.4, 8.6]), 8.5)
        XCTAssertNil(MusicVideoTapSync.averaged([]))
        XCTAssertEqual(MusicVideoTapSync.describe(8.4), "Video starts 8.4 s ahead")
        XCTAssertEqual(MusicVideoTapSync.describe(-2), "Video starts 2.0 s behind")
        XCTAssertEqual(MusicVideoTapSync.describe(0.01), "Video starts with the song")
    }

    func testTapToSyncSetsTheUsersOffsetAndASecondTapRefinesIt() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        var songTime = 2.0, clock: TimeInterval = 100
        let lines = sungLines
        let controller = controller(resolver, songClock: { (songTime, true) },
                                    lyrics: { _ in lines }, uptime: { clock })
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertTrue(controller.canTapSync)
        XCTAssertEqual(MusicVideoTapSync.buttonTitle(controller.tapState), "Tap to sync")

        // Armed during the intro: the tap marks where the singing starts.
        controller.tapSyncButton()
        XCTAssertEqual(controller.tapState, .armed(firstLine: true))
        XCTAssertEqual(MusicVideoTapSync.buttonTitle(controller.tapState), "Tap now")
        XCTAssertEqual(MusicVideoTapSync.note(controller.tapState),
                       "Watch the video. Press any key, or Tap now, the moment the singing starts in the video. Esc cancels.")
        // The singing starts 23.4 s into the video; the tap lands 0.2 s later.
        songTime = 23.6
        controller.noteVideoSample(time: 23.5, playing: true, of: .youTube("dQw4w9WgXcQ"))
        clock += 0.1
        controller.tapSyncButton()
        XCTAssertEqual(controller.currentOffset, 8.4, accuracy: 1e-9)
        XCTAssertEqual(controller.offsetOrigin, .manual, "the tap beats the estimate")
        XCTAssertEqual(MusicVideoController.timingLabel(origin: controller.offsetOrigin), "Offset set by you")
        XCTAssertEqual(controller.tapState, .done(offset: 8.4, taps: 1, firstLine: true))
        XCTAssertEqual(MusicVideoTapSync.note(controller.tapState),
                       "Video starts 8.4 s ahead, from where the singing starts. Tap again to refine.")
        XCTAssertEqual(MusicVideoTapSync.buttonTitle(controller.tapState), "Tap again")

        // Later in the song: the next line sung in the video. The video now
        // runs 8.4 s ahead; that line really starts 8.6 s in.
        songTime = 60
        controller.tapSyncButton()
        XCTAssertEqual(controller.tapState, .armed(firstLine: false))
        XCTAssertEqual(MusicVideoTapSync.note(controller.tapState),
                       "Watch the video. Press any key, or Tap now, when the next line is sung in the video. Esc cancels.")
        clock += 10
        songTime = 62.9
        controller.noteVideoSample(time: 71.3, playing: true, of: .youTube("dQw4w9WgXcQ"))
        controller.tap()
        XCTAssertEqual(controller.currentOffset, 8.5, accuracy: 1e-9, "the two taps are averaged")
        XCTAssertEqual(controller.tapState, .done(offset: 8.5, taps: 2, firstLine: false))
        XCTAssertEqual(MusicVideoTapSync.note(controller.tapState),
                       "Video starts 8.5 s ahead, from the next line sung in the video. Averaged over 2 taps.")

        // A nudge starts the taps over.
        controller.nudgeOffset(by: 0.5)
        XCTAssertEqual(controller.tapState, .idle)
        XCTAssertEqual(controller.currentOffset, 9.0, accuracy: 1e-9)
    }

    func testTapWithoutAVideoSampleUsesTheSyncedPosition() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        var songTime = 1.0
        let lines = sungLines
        let controller = controller(resolver, songClock: { (songTime, true) }, lyrics: { _ in lines })
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        controller.beginTapSync()
        // With no offset yet the video is kept at the song's own time.
        songTime = 23.6
        controller.tap()
        XCTAssertEqual(controller.currentOffset, 8.4, accuracy: 1e-9)
    }

    func testTapToSyncNeedsLyricsPlaybackAndAVideo() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        var lines: [MusicVideoLyricLine]? = nil
        var playing = true
        let controller = controller(resolver, songClock: { (5, playing) }, lyrics: { _ in lines })
        XCTAssertFalse(controller.canTapSync, "nothing showing")
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        controller.beginTapSync()
        XCTAssertEqual(controller.tapState, .failed(.noLyrics))
        XCTAssertEqual(MusicVideoTapSync.note(controller.tapState),
                       "Tap to sync needs this song’s timed lyrics, and they haven’t loaded.")
        lines = sungLines
        playing = false
        controller.beginTapSync()
        XCTAssertEqual(controller.tapState, .failed(.notPlaying))
        playing = true
        controller.beginTapSync()
        controller.cancelTapSync()
        XCTAssertEqual(controller.tapState, .idle)
        XCTAssertFalse(controller.offsets.hasManual(song.key), "cancelled: nothing set")
        controller.beginTapSync()
        controller.update(track: nil, enabled: true, online: false)
        XCTAssertEqual(controller.tapState, .idle, "a song change ends it")
        XCTAssertFalse(controller.canTapSync)
    }

    func testOnlyPlainKeysOutsideTextInputCountAsTheTap() {
        func action(_ code: UInt16, _ modifiers: NSEvent.ModifierFlags = [], repeat isRepeat: Bool = false,
                    typing: Bool = false) -> MusicVideoTapSync.KeyAction {
            MusicVideoTapSync.keyAction(keyCode: code, modifiers: modifiers, isRepeat: isRepeat, textInputFocused: typing)
        }
        XCTAssertEqual(action(49), .tap, "space")
        XCTAssertEqual(action(0, .shift), .tap, "a shifted letter is still a plain key")
        XCTAssertEqual(action(126, .function), .tap, "an arrow key")
        XCTAssertEqual(action(53), .cancel, "Esc")
        XCTAssertEqual(action(49, typing: true), .pass, "typing in a text field")
        XCTAssertEqual(action(53, typing: true), .pass)
        XCTAssertEqual(action(9, .command), .pass, "Command-V")
        XCTAssertEqual(action(8, .control), .pass)
        XCTAssertEqual(action(0, .option), .pass)
        XCTAssertEqual(action(55, .command), .pass, "Command alone")
        XCTAssertEqual(action(56, .shift), .pass, "Shift alone")
        XCTAssertEqual(action(49, repeat: true), .pass, "a held key")
    }
}
