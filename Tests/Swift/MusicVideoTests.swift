import AppKit
import ImageIO
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
    /// Replies for one exact URL, ahead of `replies` by host.
    var byURL: [String: Reply] = [:]
    var failing: Set<String> = []
    private(set) var urls: [URL] = []

    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
        urls.append(url)
        let host = url.host ?? ""
        if failing.contains(host) { throw URLError(.notConnectedToInternet) }
        let reply = byURL[url.absoluteString] ?? replies[host] ?? Reply(status: 404, body: Data())
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
        XCTAssertEqual(http.hosts().first, "www.youtube.com")
        XCTAssertEqual(Set(http.hosts()), ["www.youtube.com", "i.ytimg.com"], "plus the video's own frames")
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
        XCTAssertFalse(http.hosts().contains("www.youtube.com"), "no search; only the pasted video's frames")
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
        await controller.settle()
        XCTAssertEqual(controller.status, .missing(.notEmbeddable, title: "Blinding Lights"), "no other official video")
        XCTAssertEqual(MusicVideoCache(defaults: defaults).lookup(MusicVideoSong(track).key,
                                                                  now: Date(timeIntervalSince1970: 1_800_000_000)),
                       .missing(.notEmbeddable))
    }

    func testARefusedVideoMakesWayForTheSongsNextOfficialVideo() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = .init(status: 200, body: Fixtures.searchPage([
            ("4NRXx6U8ABQ", "The Weeknd - Blinding Lights (Official Video)", "TheWeekndVEVO"),
            ("CCCCCCCCCCC", "The Weeknd - Blinding Lights (Official Music Video)", "The Weeknd"),
        ]))
        let controller = await showingController(http: http, track: track, online: true)
        XCTAssertEqual(controller.source(for: track), .youTube("CCCCCCCCCCC"))
        controller.reportPlaybackFailure(of: .youTube("CCCCCCCCCCC"))
        XCTAssertNil(controller.source(for: track), "the refused video goes at once")
        await controller.settle()
        XCTAssertEqual(controller.source(for: track), .youTube("4NRXx6U8ABQ"), "the next official video")
        let key = MusicVideoSong(track).key, now = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertEqual(MusicVideoCache(defaults: defaults).lookup(key, now: now), .found(.youTube("4NRXx6U8ABQ")))
        controller.reportPlaybackFailure(of: .youTube("4NRXx6U8ABQ"))
        await controller.settle()
        XCTAssertEqual(controller.status, .missing(.notEmbeddable, title: "Blinding Lights"), "none left")
        XCTAssertEqual(MusicVideoCache(defaults: defaults).lookup(key, now: now), .missing(.notEmbeddable))
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

    // MARK: Still-image uploads

    /// A 120×90 greyscale frame like YouTube's: 4:3 with letterbox bars,
    /// the 16:9 picture drawn by `pixel`, plus optional JPEG-like noise.
    private static func frame(noise: Int = 0, seed: UInt32 = 1, _ pixel: (Int, Int) -> Int) -> Data {
        let width = 120, height = 90, top = 11, bottom = 79
        var state = seed
        var bytes = [UInt8](repeating: 0, count: width * height)
        for y in 0..<height {
            for x in 0..<width where y >= top && y < bottom {
                var value = pixel(x, y - top)
                if noise > 0 {
                    state = state &* 1_103_515_245 &+ 12_345
                    value += Int(state >> 16) % (2 * noise + 1) - noise
                }
                bytes[y * width + x] = UInt8(max(0, min(255, value)))
            }
        }
        let provider = CGDataProvider(data: Data(bytes) as CFData)!
        let image = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 8, bytesPerRow: width,
                            space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGBitmapInfo(rawValue: 0),
                            provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)!
        let data = NSMutableData()
        let destination = CGImageDestinationCreateWithData(data, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        return data as Data
    }

    /// The album cover: a fixed pattern.
    private static func cover(noise: Int = 0, seed: UInt32 = 1) -> Data {
        frame(noise: noise, seed: seed) { x, y in (x * 2 + y * 3) % 200 + 20 }
    }

    /// A shot with a bright figure at `at` on a dark stage.
    private static func shot(at position: Int) -> Data {
        frame { x, y in (x >= position && x < position + 30 && y >= 15 && y < 50) ? 230 : 30 }
    }

    private func fingerprints(_ frames: [Data]) -> [[UInt8]] {
        frames.compactMap(MusicVideoStillFrames.fingerprint)
    }

    func testStillFramesAreToldFromMovingOnes() {
        XCTAssertEqual(MusicVideoStillFrames.isStill(fingerprints([Self.cover(), Self.cover(), Self.cover()])), true)
        XCTAssertEqual(MusicVideoStillFrames.isStill(fingerprints([
            Self.cover(noise: 6, seed: 1), Self.cover(noise: 6, seed: 2), Self.cover(noise: 6, seed: 3),
        ])), true, "compression noise on one still is still a still")
        XCTAssertEqual(MusicVideoStillFrames.isStill(fingerprints([Self.shot(at: 5), Self.shot(at: 45), Self.shot(at: 85)])),
                       false)
        XCTAssertEqual(MusicVideoStillFrames.isStill(fingerprints([Self.cover(), Self.cover(), Self.shot(at: 45)])), false,
                       "one different frame is enough")
        XCTAssertNil(MusicVideoStillFrames.isStill(fingerprints([Self.cover(), Self.cover()])))
        XCTAssertNil(MusicVideoStillFrames.fingerprint(Data("not an image".utf8)))
        let print = MusicVideoStillFrames.fingerprint(Self.cover())
        XCTAssertEqual(print?.count, 16 * 9)
        XCTAssertGreaterThan(print?.min() ?? 0, 0, "the letterbox bars are cropped off")
        XCTAssertEqual(MusicVideoStillFrames.frameURLs(for: "4NRXx6U8ABQ").map(\.absoluteString), [
            "https://i.ytimg.com/vi/4NRXx6U8ABQ/1.jpg", "https://i.ytimg.com/vi/4NRXx6U8ABQ/2.jpg",
            "https://i.ytimg.com/vi/4NRXx6U8ABQ/3.jpg",
        ])
        XCTAssertTrue(MusicVideoStillFrames.frameURLs(for: "bad").isEmpty)
    }

    private func serveFrames(_ http: FakeHTTP, _ id: String, _ frames: [Data]) {
        for (url, data) in zip(MusicVideoStillFrames.frameURLs(for: id), frames) {
            http.byURL[url.absoluteString] = .init(status: 200, body: data)
        }
    }

    private var stillFrames: [Data] { [Self.cover(), Self.cover(noise: 3, seed: 7), Self.cover()] }
    private var movingFrames: [Data] { [Self.shot(at: 5), Self.shot(at: 45), Self.shot(at: 85)] }

    func testSearchSkipsAStillUploadForTheNextCandidate() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = .init(status: 200, body: Fixtures.searchPage([
            ("AAAAAAAAAAA", "The Weeknd - Blinding Lights (Official Music Video)", "The Weeknd"),
            ("4NRXx6U8ABQ", "The Weeknd - Blinding Lights (Official Video)", "The Weeknd"),
        ]))
        serveFrames(http, "AAAAAAAAAAA", stillFrames)
        serveFrames(http, "4NRXx6U8ABQ", movingFrames)
        let resolver = resolver(http: http)
        let result = await resolver.resolve(song, online: true)
        XCTAssertEqual(result, .found(.youTube("4NRXx6U8ABQ")))
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertEqual(resolver.cache.still("AAAAAAAAAAA", now: now), true, "the verdict is kept per video")
        XCTAssertEqual(resolver.cache.still("4NRXx6U8ABQ", now: now), false)
        XCTAssertEqual(resolver.rateLimit.sent.count, 3, "the search, then one slot per frame set")

        // Another song turning up the same uploads: no frames fetched again.
        resolver.cache.remove(song.key)
        let before = http.urls.count
        _ = await resolver.resolve(song, online: true)
        XCTAssertEqual(http.urls.count, before + 1, "only the search page")
    }

    func testOnlyStillUploadsIsAMissRememberedLikeNone() async {
        let http = FakeHTTP()
        let ids = ["AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC", "DDDDDDDDDDD"]
        http.replies["www.youtube.com"] = .init(status: 200, body: Fixtures.searchPage(
            ids.map { ($0, "The Weeknd - Blinding Lights (Official Video)", "The Weeknd") }))
        for id in ids.prefix(3) { serveFrames(http, id, stillFrames) }
        serveFrames(http, "DDDDDDDDDDD", movingFrames)
        let resolver = resolver(http: http)
        let result = await resolver.resolve(song, online: true)
        XCTAssertEqual(result, .missing(.notFound), "three stills: no video, the wallpaper falls back")
        XCTAssertFalse(http.urls.contains { $0.absoluteString.contains("DDDDDDDDDDD") }, "at most three are checked")
        XCTAssertEqual(resolver.cache.lookup(song.key, now: Date(timeIntervalSince1970: 1_800_000_000)), .missing(.notFound))
    }

    func testUnreadableFramesGiveTheVideoTheBenefitOfTheDoubt() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = officialPage()
        let resolver = resolver(http: http)
        let result = await resolver.resolve(song, online: true)
        XCTAssertEqual(result, .found(.youTube("4NRXx6U8ABQ")))
        XCTAssertNil(resolver.cache.still("4NRXx6U8ABQ", now: Date(timeIntervalSince1970: 1_800_000_000)),
                     "not judged, so not remembered")
    }

    func testFoundVideosFromBeforeTheStillCheckAreLookedUpAgain() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        defaults.set([song.key: ["at": now.timeIntervalSince1970, "video": "4NRXx6U8ABQ"]], forKey: MusicVideoCache.key)
        XCTAssertNil(MusicVideoCache(defaults: defaults).lookup(song.key, now: now))
        let cache = MusicVideoCache(defaults: defaults)
        cache.store(.found(.youTube("4NRXx6U8ABQ")), for: song.key, now: now)
        XCTAssertEqual(MusicVideoCache(defaults: defaults).lookup(song.key, now: now), .found(.youTube("4NRXx6U8ABQ")))
    }

    func testAudioAndArtTrackUploadsAreRejectedAndMusicVideoRanksFirst() {
        let rejected = ["Blinding Lights (Official Audio)", "Blinding Lights (Audio)", "Blinding Lights [Audio Only]",
                        "Blinding Lights (Visualizer)", "Blinding Lights (Official Visualiser)",
                        "Blinding Lights (Lyric Video)", "Blinding Lights LYRICS", "Blinding Lights (Art Track)"]
        for title in rejected {
            let results = [YouTubeSearchPage.Result(videoID: "AAAAAAAAAAA", title: title, channel: "The Weeknd")]
            XCTAssertNil(YouTubeSearchPage.officialVideo(in: results, title: "Blinding Lights", artist: "The Weeknd"), title)
        }
        let results = [
            YouTubeSearchPage.Result(videoID: "AAAAAAAAAAA", title: "Blinding Lights (Official Video)", channel: "The Weeknd"),
            YouTubeSearchPage.Result(videoID: "BBBBBBBBBBB", title: "Blinding Lights (Official Music Video)", channel: "The Weeknd"),
            YouTubeSearchPage.Result(videoID: "CCCCCCCCCCC", title: "Blinding Lights", channel: "The Weeknd"),
        ]
        XCTAssertEqual(YouTubeSearchPage.officialVideos(in: results, title: "Blinding Lights", artist: "The Weeknd")
            .map(\.videoID), ["BBBBBBBBBBB", "AAAAAAAAAAA", "CCCCCCCCCCC"])
    }

    func testPastedStillLinkPlaysButSettingsSaysSo() async {
        let http = FakeHTTP()
        serveFrames(http, "4NRXx6U8ABQ", stillFrames)
        let controller = MusicVideoController(resolver: resolver(http: http), offsets: MusicVideoOffsets(defaults: defaults))
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertTrue(controller.usePastedLink("https://youtu.be/4NRXx6U8ABQ"))
        await controller.settle()
        XCTAssertEqual(controller.source(for: track), .youTube("4NRXx6U8ABQ"), "the user's choice plays")
        XCTAssertTrue(controller.pastedLooksStill)
        XCTAssertFalse(http.hosts().contains("www.youtube.com"))
        controller.clearPastedLink()
        XCTAssertFalse(controller.pastedLooksStill)

        let moving = FakeHTTP()
        serveFrames(moving, "BBBBBBBBBBB", movingFrames)
        let other = MusicVideoController(resolver: resolver(http: moving), offsets: MusicVideoOffsets(defaults: defaults))
        other.update(track: track, enabled: true, online: false)
        await other.settle()
        XCTAssertTrue(other.usePastedLink("BBBBBBBBBBB"))
        await other.settle()
        XCTAssertFalse(other.pastedLooksStill)
    }

    // MARK: Open on YouTube

    func testWatchLinkStartsAtTheSongsPosition() {
        XCTAssertEqual(MusicVideoController.watchURL(videoID: "4NRXx6U8ABQ", songTime: 61.9, offset: 4)?.absoluteString,
                       "https://www.youtube.com/watch?v=4NRXx6U8ABQ&t=65s", "offset included, whole seconds")
        XCTAssertEqual(MusicVideoController.watchURL(videoID: "4NRXx6U8ABQ", songTime: 0.4, offset: 0)?.absoluteString,
                       "https://www.youtube.com/watch?v=4NRXx6U8ABQ")
        XCTAssertEqual(MusicVideoController.watchURL(videoID: "4NRXx6U8ABQ", songTime: 3, offset: -10)?.absoluteString,
                       "https://www.youtube.com/watch?v=4NRXx6U8ABQ", "before the video's start: from the top")
        XCTAssertEqual(MusicVideoController.watchURL(videoID: "4NRXx6U8ABQ", songTime: .nan, offset: 0)?.absoluteString,
                       "https://www.youtube.com/watch?v=4NRXx6U8ABQ")
        XCTAssertNil(MusicVideoController.watchURL(videoID: "x&t=1", songTime: 10, offset: 0))
    }

    func testOpenLinkOnlyForYouTubeAndNeverInTests() async {
        let http = FakeHTTP()
        http.replies["www.youtube.com"] = officialPage()
        let youTube = await showingController(http: http, track: track, online: true)
        XCTAssertEqual(youTube.showingYouTubeID, "4NRXx6U8ABQ")
        var opened: [URL] = []
        youTube.openOnYouTube { opened.append($0) }
        XCTAssertTrue(opened.isEmpty, "nothing is ever opened inside the test host")
        let library = await showingController(library: FakeLibrary(localURL), track: track, online: false)
        XCTAssertNil(library.showingYouTubeID, "no YouTube link for a library video")
        XCTAssertNil(library.watchURL)
    }
}
