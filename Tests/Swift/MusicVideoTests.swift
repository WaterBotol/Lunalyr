import AppKit
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
        XCTAssertEqual(controller.status, .missing(.notEmbeddable, title: "Blinding Lights"))
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
        let script = AppleMusicVideoLibrary.script(title: "Say \"Hi\"")
        XCTAssertTrue(script.contains("video kind is music video"))
        XCTAssertTrue(script.contains("is not running then return"), "never launches Music")
        XCTAssertTrue(script.contains(#"\"Hi\""#))
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
}
