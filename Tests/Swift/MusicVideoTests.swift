import AppKit
import WebKit
import XCTest
@testable import VersoCustom

// Every seam is faked: no test reaches the network, the Music app, a
// permission prompt or the real preferences.

private final class FakeLibrary: MusicVideoLibrary, @unchecked Sendable {
    var video: URL?
    private(set) var asked: [String] = []
    init(_ video: URL? = nil) { self.video = video }
    func localVideo(for song: MusicVideoSong) async -> URL? {
        asked.append(song.title)
        return video
    }
}

private struct FakePlayability: MusicVideoPlayability {
    var result: MusicVideoPlayabilityResult = .playable
    func check(_ url: URL) async -> MusicVideoPlayabilityResult { result }
}

/// Replies by host; records every URL asked for.
private final class FakeHTTP: MusicVideoHTTP, @unchecked Sendable {
    struct Reply { var status: Int; var body: Data; var headers: [String: String] = [:] }
    var replies: [String: Reply] = [:]
    var failing: Set<String> = []
    private(set) var urls: [URL] = []

    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
        urls.append(url)
        let host = url.host ?? ""
        if failing.contains(host) { throw URLError(.notConnectedToInternet) }
        let reply = replies[host] ?? Reply(status: 404, body: Data())
        let response = HTTPURLResponse(url: url, statusCode: reply.status, httpVersion: nil, headerFields: reply.headers)!
        return (reply.body, response)
    }

    func hosts() -> [String] { urls.compactMap(\.host) }
}

private enum Fixtures {
    static func searchPage(_ videos: [(id: String, title: String, channel: String)]) -> Data {
        let renderers = videos.map { video in
            """
            {"videoRenderer":{"videoId":"\(video.id)","title":{"runs":[{"text":"\(video.title)"}]},
             "ownerText":{"runs":[{"text":"\(video.channel)"}]}}}
            """
        }.joined(separator: ",")
        return Data("""
        <html><script>var ytInitialData = {"contents":{"twoColumnSearchResultsRenderer":{"primaryContents":
        {"sectionListRenderer":{"contents":[{"itemSectionRenderer":{"contents":[\(renderers)]}}]}}}}};</script></html>
        """.utf8)
    }
}

@MainActor
final class MusicVideoTests: XCTestCase {
    private var defaults: UserDefaults!

    override func setUp() async throws {
        let name = "LunalyrTests.MusicVideo.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
    }

    private let song = MusicVideoSong(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 200)
    private let localURL = URL(fileURLWithPath: "/Music/Media/Music Videos/Blinding Lights.m4v")

    private func resolver(library: FakeLibrary = FakeLibrary(), playability: FakePlayability = FakePlayability(),
                          http: FakeHTTP = FakeHTTP(), now: @escaping () -> Date = { Date(timeIntervalSince1970: 1_800_000_000) },
                          clock: (() -> TimeInterval)? = nil) -> MusicVideoResolver {
        var time: TimeInterval = 0
        // The fake clock moves forward by each wait, so nothing really sleeps.
        let resolver = MusicVideoResolver(library: library, playability: playability, http: http,
                                          cache: MusicVideoCache(defaults: defaults),
                                          links: MusicVideoLinks(defaults: defaults), now: now,
                                          uptime: clock ?? { time }, sleep: { time += $0 })
        return resolver
    }

    // MARK: Source order

    func testLibraryVideoComesFirstAndNeedsNoNetwork() async {
        let http = FakeHTTP()
        let result = await resolver(library: FakeLibrary(localURL), http: http).resolve(song, online: true)
        XCTAssertEqual(result, .found(.local(localURL)))
        XCTAssertTrue(http.urls.isEmpty, "a library video sends nothing")
    }

    func testOnlineOffNeverTouchesTheNetwork() async {
        let http = FakeHTTP()
        let result = await resolver(http: http).resolve(song, online: false)
        XCTAssertEqual(result, .missing(.notInLibrary))
        XCTAssertTrue(http.urls.isEmpty)
    }

    private func officialPage() -> FakeHTTP.Reply {
        .init(status: 200, body: Fixtures.searchPage([
            ("BBBBBBBBBBB", "The Weeknd - Blinding Lights (Lyrics)", "Lyrics Hub"),
            ("4NRXx6U8ABQ", "The Weeknd - Blinding Lights (Official Video)", "The Weeknd"),
        ]))
    }

    func testProtectedLibraryVideoIsSkippedForYouTube() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = officialPage()
        let protected = FakePlayability(result: .protected)
        let offline = await resolver(library: FakeLibrary(localURL), playability: protected, http: FakeHTTP())
            .resolve(song, online: false)
        XCTAssertEqual(offline, .missing(.protectedInLibrary))
        let online = await resolver(library: FakeLibrary(localURL), playability: protected, http: http)
            .resolve(song, online: true)
        XCTAssertEqual(online, .found(.youTube("4NRXx6U8ABQ")))
    }

    func testYouTubeSearchSendsOnlyTitleAndArtist() async throws {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = officialPage()
        let result = await resolver(http: http).resolve(song, online: true)
        XCTAssertEqual(result, .found(.youTube("4NRXx6U8ABQ")))
        XCTAssertEqual(http.hosts(), ["www.youtube.com"])
        let query = try XCTUnwrap(URLComponents(url: http.urls[0], resolvingAgainstBaseURL: false))
        XCTAssertEqual(query.queryItems?.first?.value, "The Weeknd Blinding Lights official music video")
    }

    func testPastedLinkComesAfterTheLibraryAndNeedsNoSearch() async {
        let http = FakeHTTP()
        let links = MusicVideoLinks(defaults: defaults)
        XCTAssertFalse(links.set("https://example.com/watch?v=4NRXx6U8ABQ", for: song.key))
        XCTAssertTrue(links.set(" https://youtu.be/4NRXx6U8ABQ ", for: song.key))
        let pasted = await resolver(http: http).resolve(song, online: false)
        XCTAssertEqual(pasted, .found(.youTube("4NRXx6U8ABQ")), "a pasted link plays with search off")
        XCTAssertTrue(http.urls.isEmpty)
        let library = await resolver(library: FakeLibrary(localURL), http: http).resolve(song, online: false)
        XCTAssertEqual(library, .found(.local(localURL)), "the library's own file still comes first")
        MusicVideoLinks(defaults: defaults).remove(song.key)
        XCTAssertNil(MusicVideoLinks(defaults: defaults).videoID(for: song.key))
    }

    func testChangedPageFormatIsAFailureNotAMiss() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = .init(status: 200, body: Data("<html>Before you continue</html>".utf8))
        let resolver = resolver(http: http)
        let result = await resolver.resolve(song, online: true)
        XCTAssertEqual(result, .missing(.lookupFailed))
        XCTAssertNil(resolver.cache.lookup(song.key, now: Date(timeIntervalSince1970: 1_800_000_000)),
                     "a page Lunalyr can't read is not remembered as no video")
    }

    func testNothingOfficialIsNotFoundAndCached() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = .init(status: 200, body: Fixtures.searchPage([
            ("BBBBBBBBBBB", "Blinding Lights (cover)", "Someone"),
        ]))
        let first = resolver(http: http)
        let result = await first.resolve(song, online: true)
        XCTAssertEqual(result, .missing(.notFound))
        let count = http.urls.count
        let again = await resolver(http: http).resolve(song, online: true)
        XCTAssertEqual(again, .missing(.notFound))
        XCTAssertEqual(http.urls.count, count, "a cached miss is not looked up again")
    }

    // MARK: Caching

    func testFoundVideoIsCachedPerSong() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = officialPage()
        _ = await resolver(http: http).resolve(song, online: true)
        let sent = http.urls.count
        // A fresh resolver, as after a relaunch, reads the same defaults.
        let result = await resolver(http: http).resolve(song, online: true)
        XCTAssertEqual(result, .found(.youTube("4NRXx6U8ABQ")))
        XCTAssertEqual(http.urls.count, sent)
        var other = song
        other.title = "Save Your Tears"
        _ = await resolver(http: http).resolve(other, online: true)
        XCTAssertGreaterThan(http.urls.count, sent, "another song is looked up")
    }

    func testCacheEntriesExpire() {
        let cache = MusicVideoCache(defaults: defaults)
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        cache.store(.found(.youTube("4NRXx6U8ABQ")), for: "a", now: start)
        cache.store(.missing(.notFound), for: "b", now: start)
        cache.store(.missing(.lookupFailed), for: "c", now: start)
        cache.store(.found(.local(localURL)), for: "d", now: start)
        XCTAssertEqual(cache.lookup("a", now: start.addingTimeInterval(59 * 86_400)), .found(.youTube("4NRXx6U8ABQ")))
        XCTAssertNil(cache.lookup("a", now: start.addingTimeInterval(61 * 86_400)))
        XCTAssertEqual(cache.lookup("b", now: start.addingTimeInterval(6 * 86_400)), .missing(.notFound))
        XCTAssertNil(cache.lookup("b", now: start.addingTimeInterval(8 * 86_400)))
        XCTAssertNil(cache.lookup("c", now: start), "a passing failure is retried")
        XCTAssertNil(cache.lookup("d", now: start), "library files are asked about afresh")
    }

    func testCacheStaysBounded() {
        let cache = MusicVideoCache(defaults: defaults)
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        for index in 0..<(MusicVideoCache.capacity + 20) {
            cache.store(.missing(.notFound), for: "song\(index)", now: start.addingTimeInterval(Double(index)))
        }
        XCTAssertEqual(cache.count, MusicVideoCache.capacity)
        XCTAssertNil(cache.lookup("song0", now: start.addingTimeInterval(1000)), "the oldest go first")
        XCTAssertNotNil(cache.lookup("song\(MusicVideoCache.capacity + 19)", now: start.addingTimeInterval(1000)))
    }

    func testNotEmbeddableIsRemembered() async {
        let http = FakeHTTP()
        let resolver = resolver(http: http)
        resolver.markNotEmbeddable(song)
        let result = await resolver.resolve(song, online: true)
        XCTAssertEqual(result, .missing(.notEmbeddable))
        XCTAssertTrue(http.urls.isEmpty)
    }

    // MARK: Failures and rate limits

    func testOfflineIsNotCachedAndReportsLookupFailed() async {
        let http = FakeHTTP()
        http.failing = ["www.youtube.com"]
        let first = await resolver(http: http).resolve(song, online: true)
        XCTAssertEqual(first, .missing(.lookupFailed))
        http.failing = []
        http.replies["www.youtube.com"] = officialPage()
        let second = await resolver(http: http).resolve(song, online: true)
        XCTAssertEqual(second, .found(.youTube("4NRXx6U8ABQ")))
    }

    func testTooManyRequestsBacksOff() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = .init(status: 429, body: Data(), headers: ["Retry-After": "120"])
        var time: TimeInterval = 0
        let resolver = resolver(http: http, clock: { time })
        let result = await resolver.resolve(song, online: true)
        XCTAssertEqual(result, .missing(.rateLimited))
        XCTAssertEqual(resolver.cache.lookup(song.key, now: Date(timeIntervalSince1970: 1_800_000_000)), nil)
        time = 10
        var other = song
        other.title = "Save Your Tears"
        let sent = http.urls.count
        let blocked = await resolver.resolve(other, online: true)
        XCTAssertEqual(blocked, .missing(.rateLimited))
        XCTAssertEqual(http.urls.count, sent, "nothing is sent while backing off")
    }

    func testRateLimitSpacesAndCapsRequests() {
        var limit = MusicVideoRateLimit(minimumGap: 2, window: 60, maximumPerWindow: 3, defaultBackOff: 60)
        XCTAssertEqual(limit.delay(at: 0), 0)
        limit.record(at: 0)
        XCTAssertEqual(limit.delay(at: 0.5), 1.5, accuracy: 1e-9)
        limit.record(at: 2)
        limit.record(at: 4)
        XCTAssertEqual(limit.delay(at: 6), 54, accuracy: 1e-9, "three in a minute, then wait for the first to age out")
        XCTAssertEqual(limit.delay(at: 61), 0)
        limit.backOff(at: 100, retryAfter: nil)
        XCTAssertEqual(limit.delay(at: 100), 60)
        limit.backOff(at: 100, retryAfter: 10)
        XCTAssertEqual(limit.delay(at: 100), 60, "a shorter Retry-After never shortens a back-off")
    }

    // MARK: Parsing

    func testYouTubeIDsFromLinks() {
        XCTAssertEqual(YouTubeVideoID.fromURL("https://www.youtube.com/watch?v=4NRXx6U8ABQ&t=3"), "4NRXx6U8ABQ")
        XCTAssertEqual(YouTubeVideoID.fromURL("https://youtu.be/4NRXx6U8ABQ"), "4NRXx6U8ABQ")
        XCTAssertEqual(YouTubeVideoID.fromURL("https://music.youtube.com/watch?v=4NRXx6U8ABQ"), "4NRXx6U8ABQ")
        XCTAssertEqual(YouTubeVideoID.fromURL("https://www.youtube-nocookie.com/embed/4NRXx6U8ABQ"), "4NRXx6U8ABQ")
        XCTAssertNil(YouTubeVideoID.fromURL("https://evil.example/watch?v=4NRXx6U8ABQ"))
        XCTAssertNil(YouTubeVideoID.fromURL("https://www.youtube.com/watch?v=4NRXx6U8AB'"))
        XCTAssertFalse(YouTubeVideoID.isValid("4NRXx6U8AB<"))
    }

    func testYouTubeSearchPrefersTheOfficialVideo() {
        let results = YouTubeSearchPage.results(fromHTML: String(decoding: Fixtures.searchPage([
            ("CCCCCCCCCCC", "Blinding Lights (Live at the Grammys)", "The Weeknd"),
            ("DDDDDDDDDDD", "The Weeknd - Blinding Lights (Audio)", "The Weeknd - Topic"),
            ("EEEEEEEEEEE", "The Weeknd - Blinding Lights", "TheWeekndVEVO"),
            ("4NRXx6U8ABQ", "The Weeknd - Blinding Lights (Official Video)", "TheWeekndVEVO"),
        ]), as: UTF8.self))
        XCTAssertEqual(results.count, 4)
        let best = YouTubeSearchPage.officialVideo(in: results, title: "Blinding Lights", artist: "The Weeknd")
        XCTAssertEqual(best?.videoID, "4NRXx6U8ABQ")
        XCTAssertNil(YouTubeSearchPage.officialVideo(in: results, title: "Save Your Tears", artist: "The Weeknd"))
        XCTAssertTrue(YouTubeSearchPage.results(fromHTML: "<html>consent</html>").isEmpty)
    }

    // MARK: Position mapping and drift

    func testPositionMappingWithOffsets() {
        XCTAssertEqual(MusicVideoTiming.videoTime(songTime: 30, offset: 0, videoDuration: 240), 30)
        XCTAssertEqual(MusicVideoTiming.videoTime(songTime: 30, offset: 12.5, videoDuration: 240), 42.5,
                       "an intro is skipped by starting the video ahead")
        XCTAssertEqual(MusicVideoTiming.videoTime(songTime: 30, offset: -5, videoDuration: 240), 25)
        XCTAssertNil(MusicVideoTiming.videoTime(songTime: 2, offset: -5, videoDuration: 240), "before the video starts")
        XCTAssertNil(MusicVideoTiming.videoTime(songTime: 235, offset: 10, videoDuration: 240), "past its end it holds")
        XCTAssertEqual(MusicVideoTiming.videoTime(songTime: 235, offset: 10, videoDuration: nil), 245)
        XCTAssertEqual(MusicVideoTiming.videoTime(songTime: 10, offset: 1000, videoDuration: nil), 310, "offsets are bounded")
        XCTAssertNil(MusicVideoTiming.videoTime(songTime: .nan, offset: 0, videoDuration: nil))
        XCTAssertEqual(MusicVideoTiming.format(12), "+12.0 s")
        XCTAssertEqual(MusicVideoTiming.format(-0.5), "−0.5 s")
        XCTAssertEqual(MusicVideoTiming.format(0), "0.0 s")
    }

    func testDriftDecisions() {
        typealias D = MusicVideoDrift
        let playing = D.Sample(videoTime: 50, videoPlaying: true)
        // In step: nothing to do.
        XCTAssertEqual(D.decide(target: 50.2, sample: playing, shouldPlay: true, sinceLastSeek: 10), .none)
        // Drift past the threshold is sought away, with the player's lead.
        XCTAssertEqual(D.decide(target: 51, sample: playing, shouldPlay: true, sinceLastSeek: 10, seekLead: 0.25),
                       .seek(to: 51.25, thenPlay: true))
        // A seek still landing is not repeated.
        XCTAssertEqual(D.decide(target: 51, sample: playing, shouldPlay: true, sinceLastSeek: 0.5), .none)
        // Buffering is waited out.
        XCTAssertEqual(D.decide(target: 51, sample: .init(videoTime: 50, videoPlaying: false, buffering: true),
                                shouldPlay: true, sinceLastSeek: 10), .none)
        // The song plays and the video does not: play.
        XCTAssertEqual(D.decide(target: 50.1, sample: .init(videoTime: 50, videoPlaying: false), shouldPlay: true,
                                sinceLastSeek: 10), .play)
        // Paused song or hidden surface: pause.
        XCTAssertEqual(D.decide(target: 50, sample: playing, shouldPlay: false, sinceLastSeek: 10), .pause)
        // A paused video follows a scrub, without starting.
        XCTAssertEqual(D.decide(target: 90, sample: .init(videoTime: 50, videoPlaying: false), shouldPlay: false,
                                sinceLastSeek: 10), .seek(to: 90, thenPlay: false))
        XCTAssertEqual(D.decide(target: 50.5, sample: .init(videoTime: 50, videoPlaying: false), shouldPlay: false,
                                sinceLastSeek: 10), .none)
        // Past the video's end it stops.
        XCTAssertEqual(D.decide(target: nil, sample: playing, shouldPlay: true, sinceLastSeek: 10), .pause)
        XCTAssertEqual(D.decide(target: nil, sample: .init(videoTime: 239, videoPlaying: false), shouldPlay: true,
                                sinceLastSeek: 10), .none)
    }

    /// The sync loop, driven by hand against a fake player, keeps drift
    /// under half a second through a play, a scrub and a pause.
    func testSyncLoopFollowsTheSong() {
        final class FakeSurface: MusicVideoPlaybackSurface {
            var time = 0.0, playing = false
            var seeks: [Double] = []
            var seekLead: Double { 0 }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
                done(.init(videoTime: time, videoPlaying: playing), 300)
            }
            func playVideo() { playing = true }
            func pauseVideo() { playing = false }
            func seekVideo(to seconds: Double) { seeks.append(seconds); time = seconds }
        }
        var songTime = 10.0, songPlaying = true, clock = 0.0
        let surface = FakeSurface()
        let loop = MusicVideoSyncLoop(songTime: { songTime }, songPlaying: { songPlaying }, offset: { 4 },
                                      uptime: { clock })
        loop.surface = surface
        loop.tick()
        XCTAssertEqual(surface.seeks, [14])
        XCTAssertTrue(surface.playing)
        // Playing along: the video advances with the song.
        for _ in 0..<8 {
            clock += 0.25; songTime += 0.25; surface.time += 0.25
            loop.tick()
        }
        XCTAssertLessThan(abs(surface.time - (songTime + 4)), 0.5)
        XCTAssertEqual(surface.seeks.count, 1)
        // A scrub in the player.
        songTime = 120
        clock += 0.25
        loop.tick()
        XCTAssertEqual(surface.seeks.last, 124)
        // Paused in the player.
        songPlaying = false
        clock += 2
        loop.tick()
        XCTAssertFalse(surface.playing)
        // Resumed, then the wallpaper is covered.
        songPlaying = true
        clock += 0.25
        loop.tick()
        XCTAssertTrue(surface.playing)
        loop.setSurfacePaused(true)
        loop.tick()
        XCTAssertFalse(surface.playing, "a covered or paused wallpaper pauses the video")
        loop.stop()
    }

    // MARK: Controller and fallback

    func testControllerFallsBackAndReportsWhy() async {
        let library = FakeLibrary()
        let http = FakeHTTP()
        let controller = MusicVideoController(resolver: resolver(library: library, http: http),
                                              offsets: MusicVideoOffsets(defaults: defaults))
        let track = NowPlayingMonitor.Track(title: "Blinding Lights", artist: "The Weeknd", duration: 200, album: "After Hours")
        controller.update(track: track, enabled: false, online: false)
        XCTAssertEqual(controller.status, .off)
        XCTAssertTrue(library.asked.isEmpty, "nothing is looked up while another type is chosen")

        controller.update(track: track, enabled: true, online: false)
        XCTAssertEqual(controller.status, .searching(title: "Blinding Lights"))
        await controller.settle()
        XCTAssertEqual(controller.status, .missing(.notInLibrary, title: "Blinding Lights"))
        XCTAssertNil(controller.source(for: track), "no video: the wallpaper draws the fallback")
        XCTAssertTrue(http.urls.isEmpty)
        let note = MusicVideoController.note(for: controller.status, online: false, fallback: "Line patterns")
        XCTAssertEqual(note?.contains("Showing Line patterns instead."), true)

        library.video = localURL
        controller.update(track: track, enabled: true, online: true)
        await controller.settle()
        XCTAssertEqual(controller.source(for: track), .local(localURL))
        var other = track
        other.title = "Save Your Tears"
        XCTAssertNil(controller.source(for: other), "a video never shows for another song")

        controller.reportPlaybackFailure(of: .local(localURL))
        XCTAssertEqual(controller.status, .missing(.localFileFailed, title: "Blinding Lights"))
        XCTAssertNil(controller.source(for: track))

        controller.update(track: nil, enabled: true, online: true)
        XCTAssertEqual(controller.status, .idle)
        var radio = track
        radio.isStream = true
        controller.update(track: radio, enabled: true, online: true)
        XCTAssertEqual(controller.status, .missing(.notASong, title: "Blinding Lights"))
    }

    func testPastedLinkFromSettingsReplacesTheFallback() async {
        let http = FakeHTTP()
        let controller = MusicVideoController(resolver: resolver(http: http),
                                              offsets: MusicVideoOffsets(defaults: defaults))
        let track = NowPlayingMonitor.Track(title: "Blinding Lights", artist: "The Weeknd", duration: 200)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertNil(controller.source(for: track))
        XCTAssertFalse(controller.usePastedLink("not a link"))
        XCTAssertTrue(controller.usePastedLink("https://www.youtube.com/watch?v=4NRXx6U8ABQ"))
        await controller.settle()
        XCTAssertEqual(controller.source(for: track), .youTube("4NRXx6U8ABQ"))
        XCTAssertEqual(controller.pastedLink, "https://www.youtube.com/watch?v=4NRXx6U8ABQ")
        controller.clearPastedLink()
        await controller.settle()
        XCTAssertNil(controller.source(for: track))
        XCTAssertTrue(http.urls.isEmpty)
    }

    func testOffsetsArePerSongAndPersist() async {
        let offsets = MusicVideoOffsets(defaults: defaults)
        let controller = MusicVideoController(resolver: resolver(), offsets: offsets)
        let track = NowPlayingMonitor.Track(title: "Blinding Lights", artist: "The Weeknd", duration: 200)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        controller.nudgeOffset(by: 5)
        controller.nudgeOffset(by: 0.5)
        XCTAssertEqual(controller.currentOffset, 5.5)
        XCTAssertEqual(MusicVideoOffsets(defaults: defaults).offset(for: MusicVideoSong(track).key), 5.5)
        var other = track
        other.title = "Save Your Tears"
        XCTAssertEqual(controller.offset(for: other), 0)
        controller.resetOffset()
        XCTAssertEqual(controller.currentOffset, 0)
        XCTAssertTrue(offsets.values.isEmpty)
    }

    func testHeavyScenesKeepTheVideoOnTheMainDisplay() {
        let source = MusicVideoSource.youTube("4NRXx6U8ABQ")
        XCTAssertEqual(MusicVideoController.placement(source: source, preview: false, heavyScenesOnMainDisplayOnly: true,
                                                      display: 1, mainDisplay: 1), source)
        XCTAssertNil(MusicVideoController.placement(source: source, preview: false, heavyScenesOnMainDisplayOnly: true,
                                                    display: 2, mainDisplay: 1), "other displays show the fallback")
        XCTAssertEqual(MusicVideoController.placement(source: source, preview: false, heavyScenesOnMainDisplayOnly: false,
                                                      display: 2, mainDisplay: 1), source)
        XCTAssertEqual(MusicVideoController.placement(source: source, preview: true, heavyScenesOnMainDisplayOnly: true,
                                                      display: nil, mainDisplay: 1), source)
        XCTAssertNil(MusicVideoController.placement(source: nil, preview: false, heavyScenesOnMainDisplayOnly: false,
                                                    display: 1, mainDisplay: 1))
    }

    func testSettingsRememberThePreviousTypeAsTheFallback() {
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertFalse(settings.musicVideoOnline, "online lookups are off by default")
        settings.backgroundMode = .visualizer
        settings.backgroundMode = .musicVideo
        XCTAssertEqual(settings.musicVideoFallback, .visualizer)
        settings.backgroundMode = .musicVideo
        XCTAssertEqual(settings.musicVideoFallback, .visualizer)
        settings.musicVideoFallback = .musicVideo
        XCTAssertEqual(settings.musicVideoFallback, .auto, "never falls back to itself")
        XCTAssertFalse(VersoCustom.Settings.BackgroundMode.musicVideoFallbacks.contains(.musicVideo))
        XCTAssertTrue(DesktopWallpaperController.needsTransparency(.musicVideo, musicVideoFallback: .desktop))
        XCTAssertFalse(DesktopWallpaperController.needsTransparency(.musicVideo, musicVideoFallback: .pattern))
    }

    // MARK: Library and embed page

    func testLibraryHitsMatchTheSongAndArtist() {
        let hits = [
            AppleMusicVideoLibrary.Hit(title: "Blinding Lights (Live)", artist: "Someone Else", path: "/a.m4v"),
            AppleMusicVideoLibrary.Hit(title: "Blinding Lights", artist: "The Weeknd", path: "/b.m4v"),
        ]
        XCTAssertEqual(AppleMusicVideoLibrary.pick(hits, for: song), URL(fileURLWithPath: "/b.m4v"))
        XCTAssertNil(AppleMusicVideoLibrary.pick([hits[0]], for: song))
        let script = AppleMusicVideoLibrary.script
        XCTAssertTrue(script.contains("video kind is music video"))
        XCTAssertTrue(script.contains("is not running then return"), "never launches Music")
        XCTAssertFalse(script.contains("name contains"), "one list per session, not a search per song")
    }

    func testEmbedPageIsMutedInlineAndCannotNavigateAway() {
        let html = YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 42.7)
        for needle in ["videoId: '4NRXx6U8ABQ'", "mute: 1", "controls: 0", "playsinline: 1", "loop: 0", "start: 42",
                       "youtube-nocookie.com", "iframe_api"] {
            XCTAssertTrue(html.contains(needle), needle)
        }
        XCTAssertTrue(YouTubeEmbed.allows(mainFrame: true, url: YouTubeEmbed.baseURL))
        XCTAssertFalse(YouTubeEmbed.allows(mainFrame: true, url: URL(string: "https://www.youtube.com/watch?v=4NRXx6U8ABQ")))
        XCTAssertTrue(YouTubeEmbed.allows(mainFrame: false, url: URL(string: "https://www.youtube-nocookie.com/embed/x")))
        let sample = YouTubeEmbed.sample(from: ["time": 12.5, "state": 1, "duration": 240] as [String: Any])
        XCTAssertEqual(sample?.0, MusicVideoDrift.Sample(videoTime: 12.5, videoPlaying: true))
        XCTAssertEqual(sample?.1, 240)
        XCTAssertNil(YouTubeEmbed.sample(from: nil))
        XCTAssertTrue(YouTubeEmbed.fatalErrors.contains(150))
    }

    // MARK: Round 2 review: library list per Music session (R2-6)

    private final class Reads: @unchecked Sendable {
        private let lock = NSLock()
        private var value = 0
        var count: Int { lock.withLock { value } }
        func add() { lock.withLock { value += 1 } }
    }

    private final class Box<T>: @unchecked Sendable {
        private let lock = NSLock()
        private var stored: T
        init(_ value: T) { stored = value }
        var value: T {
            get { lock.withLock { stored } }
            set { lock.withLock { stored = newValue } }
        }
    }

    func testLibraryListIsReadOncePerMusicSession() async {
        let reads = Reads()
        let session = Box<String?>("pid-1")
        let mayRead = Box(true)
        let answer = Box<[AppleMusicVideoLibrary.Hit]?>([
            .init(title: "Blinding Lights", artist: "The Weeknd", path: "/b.m4v"),
            .init(title: "Save Your Tears", artist: "The Weeknd", path: "/s.m4v"),
        ])
        let clock = Box<TimeInterval>(0)
        let library = AppleMusicVideoLibrary(mayRead: { mayRead.value }, session: { session.value },
                                             reader: { reads.add(); return answer.value }, uptime: { clock.value })
        let first = await library.localVideo(for: song)
        XCTAssertEqual(first, URL(fileURLWithPath: "/b.m4v"))
        var other = song
        other.title = "Save Your Tears"
        let second = await library.localVideo(for: other)
        XCTAssertEqual(second, URL(fileURLWithPath: "/s.m4v"))
        other.title = "Starboy"
        let none = await library.localVideo(for: other)
        XCTAssertNil(none)
        XCTAssertEqual(reads.count, 1, "song changes match against the session's list")

        session.value = "pid-2"
        _ = await library.localVideo(for: song)
        XCTAssertEqual(reads.count, 2, "Music opened again: read again")
        clock.value = AppleMusicVideoLibrary.listLifetime + 1
        _ = await library.localVideo(for: song)
        XCTAssertEqual(reads.count, 3, "an old list is refreshed")

        answer.value = nil
        session.value = "pid-3"
        let failed = await library.localVideo(for: song)
        XCTAssertNil(failed)
        _ = await library.localVideo(for: song)
        XCTAssertEqual(reads.count, 5, "a failed read is not kept")

        session.value = nil
        _ = await library.localVideo(for: song)
        mayRead.value = false
        session.value = "pid-4"
        _ = await library.localVideo(for: song)
        XCTAssertEqual(reads.count, 5, "nothing is read with Music closed or not allowed")
    }

    func testConcurrentSongChangesShareOneLibraryRead() async {
        let reads = Reads()
        let library = AppleMusicVideoLibrary(mayRead: { true }, session: { "pid" }, reader: {
            reads.add()
            try? await Task.sleep(for: .milliseconds(20))
            return [.init(title: "Blinding Lights", artist: "The Weeknd", path: "/b.m4v")]
        }, uptime: { 0 })
        async let a = library.localVideo(for: song)
        async let b = library.localVideo(for: song)
        let (first, second) = await (a, b)
        XCTAssertEqual(first, URL(fileURLWithPath: "/b.m4v"))
        XCTAssertEqual(second, first)
        XCTAssertEqual(reads.count, 1)
        XCTAssertEqual(AppleMusicVideoLibrary.priority, .artwork, "the library read waits behind everything else")
    }

    // MARK: Round 2 review: silent web view (R2-9)

    func testWebViewPlaysOnlyMutedMediaAndHasNoAirPlay() {
        let configuration = YouTubeEmbed.configuration()
        XCTAssertEqual(configuration.mediaTypesRequiringUserActionForPlayback, .audio)
        XCTAssertFalse(configuration.allowsAirPlayForMediaPlayback)
        XCTAssertFalse(configuration.websiteDataStore.isPersistent)
        let html = YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0, page: 3)
        XCTAssertTrue(html.contains("mute: 1"))
        XCTAssertTrue(html.contains("player.mute(); player.playVideo()"), "muted again before every play")
        XCTAssertTrue(html.contains("player.mute(); player.seekTo"), "and before every seek")
        XCTAssertTrue(html.contains("video: '4NRXx6U8ABQ', page: 3"), "messages name their video and page")
    }

    // MARK: Round 2 review: the embed page's life (R2-13, R2-15)

    private func message(_ kind: YouTubeEmbedSession.Message.Kind, video: String = "4NRXx6U8ABQ",
                         page: Int) -> YouTubeEmbedSession.Message {
        .init(kind: kind, video: video, page: page)
    }

    /// A session whose ready timeout never runs out by itself.
    private func embedSession() -> YouTubeEmbedSession {
        YouTubeEmbedSession(sleep: { _ in try? await Task.sleep(for: .seconds(3600)) })
    }

    func testOnlyThePagesOwnMainFrameIsBelieved() {
        let base = YouTubeEmbed.baseURL
        XCTAssertTrue(YouTubeEmbed.trusts(mainFrame: true, scheme: base.scheme!, host: base.host!))
        XCTAssertFalse(YouTubeEmbed.trusts(mainFrame: false, scheme: base.scheme!, host: base.host!))
        XCTAssertFalse(YouTubeEmbed.trusts(mainFrame: true, scheme: "https", host: "www.youtube-nocookie.com"))
        XCTAssertFalse(YouTubeEmbed.trusts(mainFrame: true, scheme: "http", host: base.host!))

        let session = embedSession()
        guard case .load(_, let page) = session.attach("4NRXx6U8ABQ") else { return XCTFail("loads the page") }
        XCTAssertEqual(session.receive(message(.error(150), page: page), trusted: false), .none,
                       "a forged error from another frame is ignored")
        XCTAssertEqual(session.receive(message(.error(150), video: "BBBBBBBBBBB", page: page), trusted: true), .none)
        XCTAssertEqual(session.receive(message(.ready, page: page), trusted: true), .ready)
        XCTAssertEqual(session.receive(message(.error(150), page: page), trusted: true),
                       .fail(videoID: "4NRXx6U8ABQ", .refused))
        XCTAssertEqual(session.receive(message(.error(150), page: page), trusted: true), .none, "reported once")
        XCTAssertEqual(YouTubeEmbedSession.Message(body: ["type": "error", "value": 101, "video": "4NRXx6U8ABQ", "page": 2]),
                       message(.error(101), page: 2))
        XCTAssertNil(YouTubeEmbedSession.Message(body: "error"))
    }

    func testAStalePagesErrorIsNotBlamedOnTheNewVideo() {
        let session = embedSession()
        guard case .load(_, let old) = session.attach("4NRXx6U8ABQ") else { return XCTFail() }
        guard case .load(_, let new) = session.attach("BBBBBBBBBBB") else { return XCTFail() }
        XCTAssertEqual(session.receive(message(.error(150), video: "4NRXx6U8ABQ", page: old), trusted: true), .none)
        XCTAssertEqual(session.receive(message(.error(150), video: "BBBBBBBBBBB", page: old), trusted: true), .none,
                       "the old page's number gives it away")
        XCTAssertEqual(session.receive(message(.error(101), video: "BBBBBBBBBBB", page: new), trusted: true),
                       .fail(videoID: "BBBBBBBBBBB", .refused))
    }

    func testPlayerErrorFiveIsPassing() {
        XCTAssertEqual(YouTubeEmbed.failure(forError: 5), .transient)
        XCTAssertEqual(YouTubeEmbed.failure(forError: 150), .refused)
        XCTAssertNil(YouTubeEmbed.failure(forError: 0))
    }

    func testReadyTimeoutReportsAPassingFailure() async {
        let session = YouTubeEmbedSession(readyTimeout: 12, sleep: { _ in })
        let reported = expectation(description: "timeout")
        var effects: [YouTubeEmbedSession.Effect] = []
        session.onEffect = { effects.append($0); reported.fulfill() }
        _ = session.attach("4NRXx6U8ABQ")
        await fulfillment(of: [reported], timeout: 2)
        XCTAssertEqual(effects, [.fail(videoID: "4NRXx6U8ABQ", .transient)])

        let answered = embedSession()
        guard case .load(_, let page) = answered.attach("4NRXx6U8ABQ") else { return XCTFail() }
        XCTAssertEqual(answered.receive(message(.ready, page: page), trusted: true), .ready)
        XCTAssertEqual(answered.timedOut(page: page), .none, "a player that answered never times out")
        XCTAssertEqual(answered.timedOut(page: page - 1), .none)
    }

    func testNavigationFailuresArePassingButCancelledLoadsAreNot() {
        let session = embedSession()
        _ = session.attach("4NRXx6U8ABQ")
        XCTAssertEqual(session.navigationFailed(URLError(.cancelled)), .none, "replaced by the next load")
        XCTAssertEqual(session.navigationFailed(NSError(domain: "WebKitErrorDomain", code: 102)), .none)
        XCTAssertEqual(session.navigationFailed(URLError(.notConnectedToInternet)),
                       .fail(videoID: "4NRXx6U8ABQ", .transient))
        XCTAssertEqual(session.navigationFailed(URLError(.timedOut)), .none, "reported once")
    }

    func testCrashedPageIsReloadedOnlyALimitedNumberOfTimes() {
        let session = embedSession()
        _ = session.attach("4NRXx6U8ABQ")
        for _ in 0..<YouTubeEmbedSession.reloadLimit {
            guard case .load(let id, _) = session.contentProcessTerminated() else { return XCTFail("reloads") }
            XCTAssertEqual(id, "4NRXx6U8ABQ")
        }
        XCTAssertEqual(session.contentProcessTerminated(), .fail(videoID: "4NRXx6U8ABQ", .transient))
        XCTAssertEqual(session.contentProcessTerminated(), .none, "then left alone")
        guard case .load = session.attach("BBBBBBBBBBB") else { return XCTFail("a new video starts afresh") }
        XCTAssertEqual(session.reloads, 0)
    }

    // MARK: Round 2 review: controller reports (R2-13, R2-15)

    private func showingController(http: FakeHTTP = FakeHTTP(), library: FakeLibrary = FakeLibrary(),
                                   track: NowPlayingMonitor.Track, online: Bool) async -> MusicVideoController {
        let controller = MusicVideoController(resolver: resolver(library: library, http: http),
                                              offsets: MusicVideoOffsets(defaults: defaults))
        controller.update(track: track, enabled: true, online: online)
        await controller.settle()
        return controller
    }

    private var track: NowPlayingMonitor.Track {
        NowPlayingMonitor.Track(title: "Blinding Lights", artist: "The Weeknd", duration: 200, album: "After Hours")
    }

    func testPassingPlayerFailureIsNotCachedAndIsRetried() async throws {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = officialPage()
        let controller = await showingController(http: http, track: track, online: true)
        XCTAssertEqual(controller.source(for: track), .youTube("4NRXx6U8ABQ"))
        controller.retryDelay = 0
        controller.reportPlaybackFailure(of: .youTube("BBBBBBBBBBB"), .transient)
        XCTAssertEqual(controller.source(for: track), .youTube("4NRXx6U8ABQ"), "only the video on screen counts")
        controller.reportPlaybackFailure(of: .youTube("4NRXx6U8ABQ"), .transient)
        XCTAssertEqual(controller.status, .missing(.playerUnreachable, title: "Blinding Lights"))
        XCTAssertEqual(MusicVideoCache(defaults: defaults).lookup(MusicVideoSong(track).key,
                                                                  now: Date(timeIntervalSince1970: 1_800_000_000)),
                       .found(.youTube("4NRXx6U8ABQ")), "the found video stays remembered")
        for _ in 0..<100 where controller.source(for: track) == nil {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(controller.source(for: track), .youTube("4NRXx6U8ABQ"), "tried again")
        XCTAssertFalse(MusicVideoResolution.missing(.playerUnreachable).isCacheable)
        XCTAssertTrue(MusicVideoController.note(for: .missing(.playerUnreachable, title: "X"), online: true,
                                                fallback: "Line patterns")?.contains("trying again") == true)
    }

    func testRefusedPastedLinkIsNotRememberedAndClearingForgetsTheSong() async {
        let controller = await showingController(track: track, online: false)
        XCTAssertTrue(controller.usePastedLink("https://youtu.be/4NRXx6U8ABQ"))
        await controller.settle()
        controller.reportPlaybackFailure(of: .youTube("4NRXx6U8ABQ"), .refused)
        XCTAssertEqual(controller.status, .missing(.notEmbeddable, title: "Blinding Lights"))
        let key = MusicVideoSong(track).key
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertNil(MusicVideoCache(defaults: defaults).lookup(key, now: now), "the user's own link is not cached")

        // A "can't be embedded" left for the song (by an older build, or a
        // found video) is forgotten with the link.
        MusicVideoCache(defaults: defaults).store(.missing(.notEmbeddable), for: key, now: now)
        let poisoned = MusicVideoController(resolver: resolver(), offsets: MusicVideoOffsets(defaults: defaults))
        poisoned.update(track: track, enabled: true, online: true)
        await poisoned.settle()
        XCTAssertEqual(poisoned.source(for: track), .youTube("4NRXx6U8ABQ"), "the pasted link still plays")
        XCTAssertNotNil(MusicVideoCache(defaults: defaults).lookup(key, now: now))
        poisoned.clearPastedLink()
        await poisoned.settle()
        XCTAssertNil(MusicVideoCache(defaults: defaults).lookup(key, now: now))
        XCTAssertNotEqual(poisoned.status, .missing(.notEmbeddable, title: "Blinding Lights"))
    }

    func testRefusedFoundVideoIsStillRemembered() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = officialPage()
        let controller = await showingController(http: http, track: track, online: true)
        controller.reportPlaybackFailure(of: .youTube("4NRXx6U8ABQ"))
        XCTAssertEqual(MusicVideoCache(defaults: defaults).lookup(MusicVideoSong(track).key,
                                                                  now: Date(timeIntervalSince1970: 1_800_000_000)),
                       .missing(.notEmbeddable))
    }

    func testLocalFileFailureSaysSo() async {
        let controller = await showingController(library: FakeLibrary(localURL), track: track, online: false)
        controller.reportPlaybackFailure(of: .local(localURL))
        XCTAssertEqual(controller.status, .missing(.localFileFailed, title: "Blinding Lights"))
        let note = MusicVideoController.note(for: controller.status, online: false, fallback: "Line patterns") ?? ""
        XCTAssertFalse(note.contains("outside YouTube"))
        XCTAssertTrue(note.contains("wouldn’t play"))
        XCTAssertTrue(MusicVideoCache(defaults: defaults).count == 0)
    }

    // MARK: Round 2 review: lookups (R2-15)

    func testSearchPageIsReadOffTheMainActor() async {
        let page = Fixtures.searchPage([("4NRXx6U8ABQ", "Blinding Lights (Official Video)", "The Weeknd")])
        let results = await YouTubeSearchPage.resultsOffMainActor(page) { html in
            Thread.isMainThread ? [] : YouTubeSearchPage.results(fromHTML: html)
        }
        XCTAssertEqual(results.map(\.videoID), ["4NRXx6U8ABQ"])
    }

    func testCancelledLookupUsesNoRateLimitSlot() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = officialPage()
        let skipped = resolver(http: http)
        let task = Task { await skipped.resolve(song, online: true) }
        task.cancel()
        let result = await task.value
        XCTAssertEqual(result, .missing(.lookupFailed))
        XCTAssertTrue(http.urls.isEmpty)
        XCTAssertTrue(skipped.rateLimit.sent.isEmpty)
        XCTAssertNil(skipped.cache.lookup(song.key, now: Date(timeIntervalSince1970: 1_800_000_000)))

        // Skipped while waiting its turn: the slot is not used either.
        var time: TimeInterval = 0
        let waiting = MusicVideoResolver(library: FakeLibrary(), playability: FakePlayability(), http: http,
                                         cache: MusicVideoCache(defaults: defaults),
                                         links: MusicVideoLinks(defaults: defaults),
                                         now: { Date(timeIntervalSince1970: 1_800_000_000) }, uptime: { time },
                                         sleep: { time += $0; withUnsafeCurrentTask { $0?.cancel() } })
        var first = song
        first.title = "Save Your Tears"
        _ = await Task { await waiting.resolve(first, online: true) }.value
        XCTAssertEqual(waiting.rateLimit.sent.count, 1)
        let sent = http.urls.count
        // Its own task, so only the lookup is cancelled, not the test.
        let second = await Task { await waiting.resolve(song, online: true) }.value
        XCTAssertEqual(second, .missing(.lookupFailed))
        XCTAssertEqual(http.urls.count, sent, "nothing sent")
        XCTAssertEqual(waiting.rateLimit.sent.count, 1, "no slot used")
    }

    func testThumbnailUsesTheEphemeralSession() async {
        XCTAssertEqual(YouTubeThumbnail.url(for: "4NRXx6U8ABQ")?.absoluteString,
                       "https://i.ytimg.com/vi/4NRXx6U8ABQ/hqdefault.jpg")
        XCTAssertNil(YouTubeThumbnail.url(for: "../../x"))
        let http = FakeHTTP()
        let image = await YouTubeThumbnail.load("4NRXx6U8ABQ", http: http)
        XCTAssertNil(image)
        XCTAssertEqual(http.hosts(), ["i.ytimg.com"])
        let configuration = URLSessionMusicVideoHTTP.session.configuration
        XCTAssertEqual(configuration.httpCookieAcceptPolicy, .never)
        XCTAssertFalse(configuration.httpShouldSetCookies)
        XCTAssertEqual(configuration.urlCache?.diskCapacity ?? 0, 0, "nothing cached on disk")
    }
}
