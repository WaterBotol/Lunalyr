import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

// Search YouTube…: every seam is faked. No test reaches the network, plays
// anything, or touches the real preferences.

private final class SearchFakeHTTP: MusicVideoHTTP, @unchecked Sendable {
    var status = 200
    var body = Data()
    var headers: [String: String] = [:]
    private(set) var urls: [URL] = []

    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
        urls.append(url)
        return (body, HTTPURLResponse(url: url, statusCode: status, httpVersion: nil, headerFields: headers)!)
    }
}

private struct NoLibrary: MusicVideoLibrary {
    func localVideo(for song: MusicVideoSong) async -> URL? { nil }
}

private struct AlwaysPlayable: MusicVideoPlayability {
    func check(_ url: URL) async -> MusicVideoPlayabilityResult { .playable }
}

/// Stands in for the controller.
@MainActor
private final class FakeSearching: MusicVideoSearching {
    var searchSong: MusicVideoSong?
    var showingYouTubeID: String?
    var pickedVideoID: String?
    var outcome: MusicVideoSearchOutcome = .results([])
    private(set) var queries: [String] = []
    private(set) var picks: [(id: String, song: String)] = []
    private(set) var cleared = 0

    init(song: MusicVideoSong?) { searchSong = song }

    func searchYouTube(_ query: String) async -> MusicVideoSearchOutcome {
        queries.append(query)
        return outcome
    }

    func usePickedVideo(_ videoID: String, forSong songKey: String) -> Bool {
        guard searchSong?.key == songKey else { return false }
        picks.append((videoID, songKey))
        pickedVideoID = videoID
        return true
    }

    func clearPastedLink() {
        cleared += 1
        pickedVideoID = nil
    }
}

private enum SearchFixtures {
    /// A results page with view counts and upload ages in each of the shapes
    /// YouTube uses: `simpleText`, `runs`, short and long counts, or none.
    static let page = Data("""
    <html><script>var ytInitialData = {"contents":{"twoColumnSearchResultsRenderer":{"primaryContents":
    {"sectionListRenderer":{"contents":[{"itemSectionRenderer":{"contents":[
    {"videoRenderer":{"videoId":"4NRXx6U8ABQ","title":{"runs":[{"text":"The Weeknd - Blinding Lights (Official Video)"}]},
     "ownerText":{"runs":[{"text":"TheWeekndVEVO"}]},"lengthText":{"simpleText":"4:22"},
     "viewCountText":{"simpleText":"1,034,567,890 views"},"shortViewCountText":{"simpleText":"1B views"},
     "publishedTimeText":{"simpleText":"5 years ago"}}},
    {"videoRenderer":{"videoId":"fHI8X4OXluQ","title":{"runs":[{"text":"Blinding Lights (Lyrics)"}]},
     "ownerText":{"runs":[{"text":"Lyrics Hub"}]},"lengthText":{"simpleText":"3:20"},
     "viewCountText":{"runs":[{"text":"12,345"},{"text":" views"}]},
     "publishedTimeText":{"runs":[{"text":"3 weeks ago"}]}}},
    {"videoRenderer":{"videoId":"J7p4bzqLvCw","title":{"runs":[{"text":"Blinding Lights (Live)"}]},
     "ownerText":{"runs":[{"text":"The Weeknd"}]},
     "ownerBadges":[{"metadataBadgeRenderer":{"style":"BADGE_STYLE_TYPE_VERIFIED_ARTIST"}}]}}
    ]}}]}}}}};</script></html>
    """.utf8)

    static func result(_ id: String, duration: Double? = nil, views: String? = nil,
                       uploaded: String? = nil) -> YouTubeSearchPage.Result {
        .init(videoID: id, title: "Video \(id)", channel: "Channel", duration: duration, views: views, uploaded: uploaded)
    }
}

@MainActor
final class MusicVideoSearchTests: XCTestCase {
    private var defaults: UserDefaults!
    private let song = MusicVideoSong(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 200)

    override func setUp() async throws {
        let name = "LunalyrTests.MusicVideoSearch.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
    }

    private func resolver(http: SearchFakeHTTP) -> MusicVideoResolver {
        var time: TimeInterval = 0
        return MusicVideoResolver(library: NoLibrary(), playability: AlwaysPlayable(), http: http,
                                  cache: MusicVideoCache(defaults: defaults), links: MusicVideoLinks(defaults: defaults),
                                  now: { Date(timeIntervalSince1970: 1_800_000_000) },
                                  uptime: { time }, sleep: { time += $0 })
    }

    // MARK: Request URL

    func testFreeTextQueryIsSentAsTypedWithoutOfficialMusicVideo() throws {
        let url = try XCTUnwrap(YouTubeSearchPage.requestURL(query: "  The Weeknd Blinding Lights live  "))
        XCTAssertEqual(url.host, "www.youtube.com")
        XCTAssertEqual(url.path, "/results")
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems
        XCTAssertEqual(items, [URLQueryItem(name: "search_query", value: "The Weeknd Blinding Lights live")])
        XCTAssertNil(YouTubeSearchPage.requestURL(query: "   "))
        // The automatic lookup's query is unchanged.
        let automatic = try XCTUnwrap(YouTubeSearchPage.requestURL(title: "Blinding Lights", artist: "The Weeknd"))
        XCTAssertEqual(URLComponents(url: automatic, resolvingAgainstBaseURL: false)?.queryItems?.first?.value,
                       "The Weeknd Blinding Lights official music video")
    }

    func testQueryWithSymbolsIsEncoded() throws {
        let url = try XCTUnwrap(YouTubeSearchPage.requestURL(query: "AC/DC T.N.T. & more #1"))
        XCTAssertFalse(url.absoluteString.contains(" "))
        XCTAssertEqual(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first?.value,
                       "AC/DC T.N.T. & more #1")
    }

    // MARK: Parsing views and age

    func testViewsAndUploadAgeAreReadFromEachShape() {
        let results = YouTubeSearchPage.results(fromHTML: String(decoding: SearchFixtures.page, as: UTF8.self))
        XCTAssertEqual(results.map(\.videoID), ["4NRXx6U8ABQ", "fHI8X4OXluQ", "J7p4bzqLvCw"])
        XCTAssertEqual(results[0].views, "1B views", "the short count, as YouTube shows it")
        XCTAssertEqual(results[0].uploaded, "5 years ago")
        XCTAssertEqual(results[0].duration, 262)
        XCTAssertEqual(results[1].views, "12,345 views", "the long count from runs when there is no short one")
        XCTAssertEqual(results[1].uploaded, "3 weeks ago")
        XCTAssertNil(results[2].views)
        XCTAssertNil(results[2].uploaded)
        XCTAssertNil(results[2].duration)
        XCTAssertTrue(results[2].verifiedArtist)
    }

    func testResultsDefaultToNoViewsOrAge() {
        let old = YouTubeSearchPage.Result(videoID: "4NRXx6U8ABQ", title: "T", channel: "C")
        XCTAssertNil(old.views)
        XCTAssertNil(old.uploaded)
    }

    // MARK: Row labels

    func testDurationIsFormattedLikeYouTube() {
        XCTAssertEqual(MusicVideoSearchRow.duration(7), "0:07")
        XCTAssertEqual(MusicVideoSearchRow.duration(225), "3:45")
        XCTAssertEqual(MusicVideoSearchRow.duration(600), "10:00")
        XCTAssertEqual(MusicVideoSearchRow.duration(3723), "1:02:03")
        XCTAssertNil(MusicVideoSearchRow.duration(nil))
        XCTAssertNil(MusicVideoSearchRow.duration(.nan))
        XCTAssertNil(MusicVideoSearchRow.duration(-1))
    }

    func testDetailsJoinViewsAndAgeAndLeaveOutWhatIsMissing() {
        XCTAssertEqual(MusicVideoSearchRow.details(views: "12M views", uploaded: "3 years ago"), "12M views · 3 years ago")
        XCTAssertEqual(MusicVideoSearchRow.details(views: "12M views", uploaded: nil), "12M views")
        XCTAssertEqual(MusicVideoSearchRow.details(views: " ", uploaded: "2 days ago"), "2 days ago")
        XCTAssertNil(MusicVideoSearchRow.details(views: nil, uploaded: nil))
    }

    func testSameLengthHintWithinTheLengthMatch() {
        let rows = MusicVideoSearchRow.rows([SearchFixtures.result("AAAAAAAAAAA", duration: 201.5),
                                             SearchFixtures.result("BBBBBBBBBBB", duration: 203),
                                             SearchFixtures.result("CCCCCCCCCCC")],
                                            songDuration: 200, playing: nil)
        XCTAssertEqual(rows.map(\.sameLength), [true, false, false])
        XCTAssertFalse(MusicVideoSearchRow.sameLength(200, song: 0), "no song length, no hint")
        XCTAssertFalse(MusicVideoSearchRow.sameLength(200, song: nil))
    }

    func testPlayingVideoIsMarkedAndFloatsToTheTop() {
        let results = ["AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC"].map { SearchFixtures.result($0) }
        let rows = MusicVideoSearchRow.rows(results, songDuration: 200, playing: "CCCCCCCCCCC")
        XCTAssertEqual(rows.map(\.videoID), ["CCCCCCCCCCC", "AAAAAAAAAAA", "BBBBBBBBBBB"], "otherwise YouTube's order")
        XCTAssertEqual(rows.map(\.playingNow), [true, false, false])
        XCTAssertTrue(rows[0].accessibilityLabel.hasSuffix(MusicVideoSearchRow.playingNowLabel))
    }

    func testRowsAreDeduplicatedAndCapped() {
        let results = (0..<30).map { SearchFixtures.result(String(format: "video%06d", $0)) }
        let doubled = [results[0]] + results
        let rows = MusicVideoSearchRow.rows(doubled, songDuration: 200, playing: nil)
        XCTAssertEqual(rows.count, MusicVideoSearchRow.limit)
        XCTAssertEqual(Set(rows.map(\.id)).count, rows.count)
        XCTAssertEqual(rows.first?.videoID, results[0].videoID)
    }

    func testRowLabelsAndThumbnail() throws {
        let result = YouTubeSearchPage.Result(videoID: "4NRXx6U8ABQ", title: "Blinding Lights (Official Video)",
                                              channel: "The Weeknd", verifiedArtist: true, duration: 201,
                                              views: "1B views", uploaded: "5 years ago")
        let row = MusicVideoSearchRow(result, songDuration: 200, playing: nil)
        XCTAssertEqual(row.durationText, "3:21")
        XCTAssertEqual(row.details, "1B views · 5 years ago")
        XCTAssertEqual(row.accessibilityLabel,
                       "Blinding Lights (Official Video), The Weeknd, Verified artist, 3:21, 1B views · 5 years ago, Same length as the song")
        XCTAssertEqual(row.thumbnailURL?.absoluteString, "https://i.ytimg.com/vi/4NRXx6U8ABQ/mqdefault.jpg",
                       "a still image, never the video")
        let untitled = MusicVideoSearchRow(.init(videoID: "4NRXx6U8ABQ", title: "", channel: ""), songDuration: nil, playing: nil)
        XCTAssertEqual(untitled.title, "Untitled video")
    }

    // MARK: Model

    func testQueryIsPrefilledWithArtistAndTitleAndSearchedOnOpen() async {
        let source = FakeSearching(song: song)
        source.outcome = .results([SearchFixtures.result("AAAAAAAAAAA")])
        let model = MusicVideoSearchModel(source: source)
        model.prepare()
        XCTAssertEqual(model.query, "The Weeknd Blinding Lights")
        XCTAssertEqual(model.songLabel, "“Blinding Lights” by The Weeknd")
        XCTAssertEqual(model.phase, .loading)
        await model.settle()
        XCTAssertEqual(source.queries, ["The Weeknd Blinding Lights"])
        XCTAssertEqual(model.phase, .results)
        XCTAssertEqual(model.selection, "AAAAAAAAAAA")
        XCTAssertEqual(MusicVideoSearchModel.prefill(for: MusicVideoSong(title: "Song", artist: " ", duration: 1)), "Song")
    }

    func testReopeningForTheSameSongKeepsResultsAndANewSongSearchesAgain() async {
        let source = FakeSearching(song: song)
        source.outcome = .results([SearchFixtures.result("AAAAAAAAAAA"), SearchFixtures.result("BBBBBBBBBBB")])
        let model = MusicVideoSearchModel(source: source)
        model.prepare()
        await model.settle()
        source.showingYouTubeID = "BBBBBBBBBBB"
        model.prepare()
        await model.settle()
        XCTAssertEqual(source.queries.count, 1, "the same song is not searched again")
        XCTAssertEqual(model.rows.first?.videoID, "BBBBBBBBBBB", "Playing now is brought up to date")

        source.searchSong = MusicVideoSong(title: "Save Your Tears", artist: "The Weeknd", duration: 215)
        model.prepare()
        await model.settle()
        XCTAssertEqual(source.queries.last, "The Weeknd Save Your Tears")
    }

    func testEditedQueryIsSearchedAsTyped() async {
        let source = FakeSearching(song: song)
        let model = MusicVideoSearchModel(source: source)
        model.prepare()
        await model.settle()
        model.query = "blinding lights live coachella"
        model.search()
        await model.settle()
        XCTAssertEqual(source.queries.last, "blinding lights live coachella")
        XCTAssertEqual(model.phase, .empty)
    }

    func testRateLimitedAndFailedStates() async {
        let source = FakeSearching(song: song)
        let model = MusicVideoSearchModel(source: source)
        source.outcome = .rateLimited
        model.prepare()
        await model.settle()
        XCTAssertEqual(model.phase, .rateLimited)
        source.outcome = .failed
        model.search()
        await model.settle()
        XCTAssertEqual(model.phase, .failed)
        source.outcome = .results([SearchFixtures.result("AAAAAAAAAAA")])
        model.prepare()
        await model.settle()
        XCTAssertEqual(model.phase, .results, "reopening after a failure tries again")
    }

    func testNothingPlayingSearchesNothing() async {
        let source = FakeSearching(song: nil)
        let model = MusicVideoSearchModel(source: source)
        model.prepare()
        model.search()
        await model.settle()
        XCTAssertEqual(model.phase, .noSong)
        XCTAssertTrue(source.queries.isEmpty)
    }

    func testPickingUsesTheVideoForTheSongAndCloses() async {
        let source = FakeSearching(song: song)
        source.outcome = .results([SearchFixtures.result("AAAAAAAAAAA"), SearchFixtures.result("BBBBBBBBBBB")])
        let model = MusicVideoSearchModel(source: source)
        var closed = 0
        model.onClose = { closed += 1 }
        model.prepare()
        await model.settle()
        model.moveSelection(by: 1)
        XCTAssertEqual(model.selection, "BBBBBBBBBBB")
        model.moveSelection(by: 1)
        XCTAssertEqual(model.selection, "BBBBBBBBBBB", "stays at the end")
        model.pickSelection()
        XCTAssertEqual(source.picks.map(\.id), ["BBBBBBBBBBB"])
        XCTAssertEqual(source.picks.map(\.song), [song.key])
        XCTAssertEqual(closed, 1)
    }

    func testPickAfterTheSongChangedUsesNothingAndSaysSo() async {
        let source = FakeSearching(song: song)
        source.outcome = .results([SearchFixtures.result("AAAAAAAAAAA")])
        let model = MusicVideoSearchModel(source: source)
        var closed = 0
        model.onClose = { closed += 1 }
        model.prepare()
        await model.settle()
        source.searchSong = MusicVideoSong(title: "Save Your Tears", artist: "The Weeknd", duration: 215)
        model.pick("AAAAAAAAAAA")
        XCTAssertTrue(source.picks.isEmpty)
        XCTAssertEqual(closed, 0)
        XCTAssertEqual(model.notice, MusicVideoSearchModel.songChangedNote)
        XCTAssertEqual(model.query, "The Weeknd Save Your Tears")
        XCTAssertTrue(model.rows.isEmpty)
    }

    func testBackToAutomaticClearsThePick() async {
        let source = FakeSearching(song: song)
        let model = MusicVideoSearchModel(source: source)
        var closed = 0
        model.onClose = { closed += 1 }
        model.prepare()
        await model.settle()
        XCTAssertFalse(model.canGoBackToAutomatic)
        model.backToAutomatic()
        XCTAssertEqual(source.cleared, 0)
        source.pickedVideoID = "AAAAAAAAAAA"
        XCTAssertTrue(model.canGoBackToAutomatic)
        model.backToAutomatic()
        XCTAssertEqual(source.cleared, 1)
        XCTAssertEqual(closed, 1)
    }

    // MARK: Resolver and controller

    func testResolverSearchSendsOnlyTheQueryAndCachesNothing() async throws {
        let http = SearchFakeHTTP()
        http.body = SearchFixtures.page
        let resolver = resolver(http: http)
        let outcome = await resolver.searchResults(for: "The Weeknd Blinding Lights")
        guard case .results(let results) = outcome else { return XCTFail("expected results, got \(outcome)") }
        XCTAssertEqual(results.count, 3)
        XCTAssertEqual(http.urls.count, 1, "the search page only: no frames, no captions")
        XCTAssertEqual(URLComponents(url: http.urls[0], resolvingAgainstBaseURL: false)?.queryItems?.first?.value,
                       "The Weeknd Blinding Lights")
        XCTAssertEqual(resolver.cache.count, 0)
        XCTAssertEqual(resolver.rateLimit.sent.count, 1, "uses a rate-limit slot like any lookup")
    }

    func testResolverSearchStates() async {
        let http = SearchFakeHTTP()
        let resolver = resolver(http: http)
        http.body = Data("<html>Before you continue to YouTube</html>".utf8)
        let consent = await resolver.searchResults(for: "x")
        XCTAssertEqual(consent, .failed, "a page without results data is not 'no videos'")
        http.body = Data("<script>var ytInitialData = {\"contents\":{}};</script>".utf8)
        let none = await resolver.searchResults(for: "x")
        XCTAssertEqual(none, .results([]))
        http.status = 429
        http.headers = ["Retry-After": "120"]
        let limited = await resolver.searchResults(for: "x")
        XCTAssertEqual(limited, .rateLimited)
        let sentBefore = http.urls.count
        let blocked = await resolver.searchResults(for: "x")
        XCTAssertEqual(blocked, .rateLimited)
        XCTAssertEqual(http.urls.count, sentBefore, "nothing is sent while backing off")
        let empty = await resolver.searchResults(for: "  ")
        XCTAssertEqual(empty, .failed)
    }

    func testPickedVideoGoesThroughThePastedLinkPath() async {
        let http = SearchFakeHTTP()
        let controller = MusicVideoController(resolver: resolver(http: http), offsets: MusicVideoOffsets(defaults: defaults))
        let track = NowPlayingMonitor.Track(title: "Blinding Lights", artist: "The Weeknd", duration: 200, album: "After Hours")
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        let key = MusicVideoSong(track).key
        XCTAssertEqual(controller.searchSong?.key, key)
        XCTAssertFalse(controller.usePickedVideo("4NRXx6U8ABQ", forSong: "another song"))
        XCTAssertFalse(controller.usePickedVideo("not an id", forSong: key))
        XCTAssertNil(controller.pickedVideoID)
        XCTAssertTrue(controller.usePickedVideo("4NRXx6U8ABQ", forSong: key))
        await controller.settle()
        XCTAssertEqual(controller.pickedVideoID, "4NRXx6U8ABQ")
        XCTAssertEqual(controller.pastedLink, "https://www.youtube.com/watch?v=4NRXx6U8ABQ")
        XCTAssertEqual(controller.source(for: track), .youTube("4NRXx6U8ABQ"))
        controller.clearPastedLink()
        await controller.settle()
        XCTAssertNil(controller.pickedVideoID)
    }

    // MARK: Picture

    /// Renders the picker with fixture rows to the path in
    /// `LUNALYR_MV_SEARCH_PREVIEW` (run with
    /// `TEST_RUNNER_LUNALYR_MV_SEARCH_PREVIEW=<path>`). No thumbnails are
    /// fetched: stand-in tints only. Skipped otherwise.
    func testRenderPreview() async throws {
        guard let path = ProcessInfo.processInfo.environment["LUNALYR_MV_SEARCH_PREVIEW"], !path.isEmpty else {
            throw XCTSkip("Set LUNALYR_MV_SEARCH_PREVIEW to render the picker")
        }
        let source = FakeSearching(song: song)
        source.showingYouTubeID = "fHI8X4OXluQ"
        source.pickedVideoID = "fHI8X4OXluQ"
        source.outcome = .results([
            .init(videoID: "4NRXx6U8ABQ", title: "The Weeknd - Blinding Lights (Official Video)", channel: "TheWeekndVEVO",
                  duration: 262, views: "1B views", uploaded: "5 years ago"),
            .init(videoID: "fHI8X4OXluQ", title: "The Weeknd - Blinding Lights (Official Audio)", channel: "The Weeknd",
                  verifiedArtist: true, duration: 201, views: "312M views", uploaded: "5 years ago"),
            .init(videoID: "J7p4bzqLvCw", title: "The Weeknd - Blinding Lights (Live at the Super Bowl LV Halftime Show, with the full stage and fireworks)",
                  channel: "The Weeknd", verifiedArtist: true, duration: 228, views: "48M views", uploaded: "3 years ago"),
            .init(videoID: "XXXXXXXXXXX", title: "Blinding Lights (Lyrics)", channel: "Lyrics Hub", duration: 200,
                  views: "9.1M views", uploaded: "2 years ago"),
            .init(videoID: "YYYYYYYYYYY", title: "Blinding Lights - piano cover", channel: "Keys at Night", duration: 187),
        ])
        let model = MusicVideoSearchModel(source: source)
        model.prepare()
        await model.settle()
        let view = MusicVideoSearchView(model: model, preview: true).frame(width: 600)
        for (scheme, appearance, suffix) in [(ColorScheme.light, NSAppearance.Name.aqua, ""), (.dark, .darkAqua, "-dark")] {
            let renderer = ImageRenderer(content: view.environment(\.colorScheme, scheme))
            renderer.scale = 2
            var rendered: NSImage?
            NSAppearance(named: appearance)?.performAsCurrentDrawingAppearance { rendered = renderer.nsImage }
            let image = try XCTUnwrap(rendered)
            let tiff = try XCTUnwrap(image.tiffRepresentation)
            let png = try XCTUnwrap(NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]))
            let url = URL(fileURLWithPath: suffix.isEmpty ? path : path.replacingOccurrences(of: ".png", with: "\(suffix).png"))
            try png.write(to: url)
        }
    }
}
