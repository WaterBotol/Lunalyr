import XCTest
@testable import VersoCustom

// The per-video memory of the user's offsets, the quarter-second nudges,
// the sync loop's handling of small offset changes and lasting drift, and
// same-length uploads preferred in search. No player, no audio, no network,
// no real preferences.

private struct NoVideoLibrary: MusicVideoLibrary {
    func localVideo(for song: MusicVideoSong) async -> URL? { nil }
}

private struct AlwaysPlayable: MusicVideoPlayability {
    func check(_ url: URL) async -> MusicVideoPlayabilityResult { .playable }
}

/// Never reaches the network: every request is a 404 (a pasted link's
/// still check asks for its frames).
private final class NoHTTP: MusicVideoHTTP, @unchecked Sendable {
    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
        (Data(), HTTPURLResponse(url: url, statusCode: 404, httpVersion: nil, headerFields: [:])!)
    }
}

@MainActor
final class MusicVideoOffsetTests: XCTestCase {
    private var defaults: UserDefaults!

    override func setUp() async throws {
        let name = "LunalyrTests.MusicVideoOffsets.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
    }

    private let song = MusicVideoSong(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 200)
    private var track: NowPlayingMonitor.Track {
        NowPlayingMonitor.Track(title: "Blinding Lights", artist: "The Weeknd", duration: 200, album: "After Hours")
    }
    private let start = Date(timeIntervalSince1970: 1_800_000_000)
    private let videoID = "dQw4w9WgXcQ"

    private func showing() async -> MusicVideoController {
        let resolver = MusicVideoResolver(library: NoVideoLibrary(), playability: AlwaysPlayable(), http: NoHTTP(),
                                          cache: MusicVideoCache(defaults: defaults),
                                          links: MusicVideoLinks(defaults: defaults), now: { [start] in start })
        resolver.links.set(videoID, for: song.key)
        let controller = MusicVideoController(resolver: resolver, offsets: MusicVideoOffsets(defaults: defaults))
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        return controller
    }

    func testVideoEarlierAndLaterMoveAQuarterSecondForThisVideo() async {
        let controller = await showing()
        controller.noteVideoDuration(215, of: .youTube(videoID))
        controller.videoEarlier()
        XCTAssertEqual(controller.currentOffset, 15.25, accuracy: 1e-9)
        controller.videoEarlier()
        XCTAssertEqual(controller.currentOffset, 15.5, accuracy: 1e-9, "quarter steps add up exactly")
        controller.videoLater()
        controller.videoLater()
        controller.videoLater()
        XCTAssertEqual(controller.currentOffset, 14.75, accuracy: 1e-9)
        XCTAssertEqual(controller.offsetOrigin, .manual)
        XCTAssertEqual(MusicVideoOffsets(defaults: defaults).offset(for: song.key, video: "yt:\(videoID)"),
                       14.75, accuracy: 1e-9, "kept")
    }

    // MARK: Offsets per video

    func testTheUsersOffsetBelongsToTheVideoItWasSetOn() {
        let offsets = MusicVideoOffsets(defaults: defaults)
        offsets.setEstimate(12, for: song.key, video: "yt:BBBBBBBBBBB")
        offsets.set(8.4, for: song.key, video: "yt:AAAAAAAAAAA")
        XCTAssertEqual(offsets.offset(for: song.key, video: "yt:AAAAAAAAAAA"), 8.4)
        XCTAssertTrue(offsets.hasManual(song.key, video: "yt:AAAAAAAAAAA"))
        XCTAssertFalse(offsets.hasManual(song.key, video: "yt:BBBBBBBBBBB"))
        XCTAssertEqual(offsets.offset(for: song.key, video: "yt:BBBBBBBBBBB"), 12,
                       "another upload of the song has its own intro")
        XCTAssertEqual(offsets.origin(for: song.key, video: "yt:BBBBBBBBBBB"), .estimated)
        let reloaded = MusicVideoOffsets(defaults: defaults)
        XCTAssertEqual(reloaded.offset(for: song.key, video: "yt:AAAAAAAAAAA"), 8.4, "kept with its video")
        XCTAssertFalse(reloaded.hasManual(song.key, video: "yt:BBBBBBBBBBB"))
        reloaded.clearManual(song.key)
        XCTAssertFalse(MusicVideoOffsets(defaults: defaults).hasManual(song.key))
        // One set before videos were told apart applies to any.
        defaults.set([song.key: 3.0], forKey: MusicVideoOffsets.key)
        let legacy = MusicVideoOffsets(defaults: defaults)
        XCTAssertEqual(legacy.offset(for: song.key, video: "yt:BBBBBBBBBBB"), 3)
    }

    // MARK: Correction

    func testASmallChangeOfOffsetIsAppliedOnYouTube() {
        var corrector = MusicVideoCorrector(style: .ledSeek)
        func playing(_ drift: Double) -> MusicVideoDrift.Sample { .init(videoTime: 100 + drift, videoPlaying: true) }
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.25), shouldPlay: true, now: 10, fixedLead: 0), .none,
                       "drift alone under a second is left")
        corrector.targetMoved()
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.25), shouldPlay: true, now: 10.25, fixedLead: 0),
                       .seek(to: 100 + MusicVideoSeekLatency.initial, thenPlay: true), "a quarter-second nudge shows")
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.05), shouldPlay: true, now: 12, fixedLead: 0), .none)
        corrector.targetMoved()
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.08), shouldPlay: true, now: 14, fixedLead: 0), .none,
                       "already within a tenth")
    }

    func testDriftThatLingersIsSoughtButNotOverAndOver() {
        var corrector = MusicVideoCorrector(style: .ledSeek)
        func playing(_ drift: Double) -> MusicVideoDrift.Sample { .init(videoTime: 100 + drift, videoPlaying: true) }
        var now = 10.0
        func check(_ drift: Double) -> MusicVideoCorrector.Action {
            now += 0.25
            return corrector.decide(target: 100, sample: playing(drift), shouldPlay: true, now: now, fixedLead: 0)
        }
        var seeks = 0
        for _ in 0..<(MusicVideoSeekHysteresis.lingerChecks - 1) { if check(0.6) != .none { seeks += 1 } }
        XCTAssertEqual(seeks, 0, "a lasting 0.6 s is not sought at once")
        if case .seek = check(0.6) {} else { XCTFail("but is once it has lasted three seconds") }
        // It keeps landing 0.6 s off: the retries, then a doubled wait.
        var actions: [MusicVideoCorrector.Action] = []
        for _ in 0..<60 { actions.append(check(0.6)) }
        let more = actions.filter { if case .seek = $0 { return true } else { return false } }.count
        XCTAssertLessThanOrEqual(more, 6, "backs off: \(more) seeks in 15 s")
        XCTAssertGreaterThanOrEqual(more, 2)
    }

    func testAVPlayerSeeksForAChangedOffsetAndEasesTheRest() {
        var corrector = MusicVideoCorrector(style: .rate)
        let sample = MusicVideoDrift.Sample(videoTime: 50.5, videoPlaying: true)
        corrector.targetMoved()
        XCTAssertEqual(corrector.decide(target: 50, sample: sample, shouldPlay: true, now: 10, fixedLead: 0.05),
                       .seek(to: 50.05, thenPlay: true))
        corrector.targetMoved()
        guard case .rate = corrector.decide(target: 50, sample: .init(videoTime: 50.25, videoPlaying: true), shouldPlay: true,
                                            now: 12, fixedLead: 0.05) else { return XCTFail("a quarter second is eased") }
    }

    func testTheLoopComparesWithTheSongHalfwayThroughTheAnswerAndRetargets() {
        final class SlowSurface: MusicVideoPlaybackSurface {
            var pending: ((MusicVideoDrift.Sample?, Double?) -> Void)?
            var seeks: [Double] = []
            var seekLead: Double { 0 }
            var correctionStyle: MusicVideoCorrectionStyle { .ledSeek }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) { pending = done }
            func playVideo() {}
            func pauseVideo() {}
            func seekVideo(to seconds: Double) { seeks.append(seconds) }
        }
        var songTime = 40.0, clock = 100.0, offset = 0.0
        var drifts: [Double?] = [], readAt: [TimeInterval] = []
        let surface = SlowSurface()
        let loop = MusicVideoSyncLoop(songTime: { songTime }, songPlaying: { true }, offset: { offset }, uptime: { clock })
        loop.surface = surface
        loop.onSample = { _, drift, at in drifts.append(drift); readAt.append(at) }
        loop.tick()
        // The answer takes 0.2 s; the player read 40.1 halfway.
        songTime += 0.2
        clock += 0.2
        surface.pending?(.init(videoTime: 40.1, videoPlaying: true), 215)
        XCTAssertEqual(try XCTUnwrap(drifts.last ?? nil), 0, accuracy: 1e-9, "in step, not 0.1 s behind")
        XCTAssertEqual(readAt.last ?? 0, 100.1, accuracy: 1e-9)
        XCTAssertEqual(MusicVideoSyncLoop.midpoint(10, 30, playing: true), 30, "a jump keeps the later read")
        XCTAssertEqual(MusicVideoSyncLoop.midpoint(10, 10.2, playing: false), 10.2)
        // A quarter-second nudge is sought at once, led by the seek latency;
        // a drift that small alone never would be.
        offset = 0.25
        songTime += 0.25
        clock += 0.25
        loop.tick()
        surface.pending?(.init(videoTime: songTime, videoPlaying: true), 215)
        XCTAssertEqual(surface.seeks, [40.45 + 0.25 + MusicVideoSeekLatency.initial])
    }

    // MARK: Search

    func testLengthsOnResultsAreRead() {
        XCTAssertEqual(YouTubeSearchPage.lengthSeconds("3:45"), 225)
        XCTAssertEqual(YouTubeSearchPage.lengthSeconds("1:02:03"), 3723)
        XCTAssertEqual(YouTubeSearchPage.lengthSeconds("0:59"), 59)
        XCTAssertNil(YouTubeSearchPage.lengthSeconds("LIVE"))
        XCTAssertNil(YouTubeSearchPage.lengthSeconds("3:4x"))
        XCTAssertNil(YouTubeSearchPage.lengthSeconds("12"))
        let html = #"<script>var ytInitialData = {"contents":[{"videoRenderer":{"videoId":"dQw4w9WgXcQ","title":{"runs":[{"text":"The Weeknd - Blinding Lights (Official Video)"}]},"ownerText":{"runs":[{"text":"TheWeekndVEVO"}]},"lengthText":{"accessibility":{"accessibilityData":{"label":"4 minutes, 22 seconds"}},"simpleText":"4:22"}}}]};</script>"#
        XCTAssertEqual(YouTubeSearchPage.results(fromHTML: html).first?.duration, 262)
    }

    func testAnOfficialVideoAsLongAsTheSongComesFirst() {
        let results = [
            YouTubeSearchPage.Result(videoID: "AAAAAAAAAAA", title: "The Weeknd - Blinding Lights (Official Music Video)",
                                     channel: "TheWeekndVEVO", duration: 262),
            YouTubeSearchPage.Result(videoID: "BBBBBBBBBBB", title: "The Weeknd - Blinding Lights (Official Video)",
                                     channel: "The Weeknd", duration: 201),
            YouTubeSearchPage.Result(videoID: "CCCCCCCCCCC", title: "The Weeknd - Blinding Lights",
                                     channel: "The Weeknd", duration: 200),
        ]
        let ranked = YouTubeSearchPage.officialVideos(in: results, title: "Blinding Lights", artist: "The Weeknd",
                                                      songDuration: 200)
        XCTAssertEqual(ranked.map(\.videoID), ["BBBBBBBBBBB", "AAAAAAAAAAA", "CCCCCCCCCCC"],
                       "the same length first; a bare title (maybe the audio) gains nothing by its length")
        XCTAssertEqual(YouTubeSearchPage.officialVideos(in: results, title: "Blinding Lights", artist: "The Weeknd")
            .map(\.videoID), ["AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC"], "without the song's length, as before")
        XCTAssertEqual(YouTubeSearchPage.officialVideos(in: results, title: "Blinding Lights", artist: "The Weeknd",
                                                        songDuration: 230).first?.videoID, "AAAAAAAAAAA",
                       "none as long as the song: as before")
    }
}
