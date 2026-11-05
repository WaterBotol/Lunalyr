import XCTest
@testable import VersoCustom

final class NextVersionTests: XCTestCase {
    @MainActor
    func testSongOffsetsSurviveRestartWithoutLeakingBetweenRecordings() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        // A unique domain prevents tests modifying real song preferences.
        let isolated = UserDefaults(suiteName: name)!
        defer { isolated.removePersistentDomain(forName: name) }
        let a = LyricQuery(title: "Song", artist: "Artist", album: "Studio", duration: 200)
        let live = LyricQuery(title: "Song", artist: "Artist", album: "Live", duration: 210)
        let store = SongOffsets(defaults: isolated)
        store.set(1.25, for: a)
        XCTAssertEqual(SongOffsets(defaults: isolated).offset(for: a), 1.25)
        XCTAssertEqual(store.offset(for: live), 0)
        store.set(.nan, for: a)
        XCTAssertEqual(store.offset(for: a), 1.25)
        store.set(0, for: a)
        XCTAssertEqual(SongOffsets(defaults: isolated).offset(for: a), 0)
    }
    func testLyricsfileRetainsRealWordTimingsAndIgnoresMalformedTimings() {
        let text = """
        version: '1.0'
        lines:
          - text: "Stay here"
            start_ms: 1000
            end_ms: 3000
            words:
              - text: 'Stay '
                start_ms: 1000
                end_ms: 2000
              - text: here
                start_ms: 2000
                end_ms: 3000
        """
        let lines = LyricsFetcher.parseLyricsFile(text)
        XCTAssertEqual(lines.count, 1)
        XCTAssertTrue(lines[0].wordTimed)
        XCTAssertEqual(lines[0].words[1].start, 2)
        XCTAssertFalse(LyricsFetcher.parseLyricsFile(text.replacingOccurrences(of: "end_ms: 2000", with: "end_ms: 4000"))[0].wordTimed)
    }
    func testLyricsfileInfersMissingWordEnds() {
        let text = """
        lines:
        - text: 'One two'
          start_ms: 1000
          end_ms: 4000
          words:
          - text: 'One '
            start_ms: 1000
          - text: two
            start_ms: 2000
        """
        let lines = LyricsFetcher.parseLyricsFile(text)
        XCTAssertTrue(lines[0].wordTimed)
        XCTAssertEqual(lines[0].words.map(\.end), [2, 4])
    }
    func testLRCLIBWordTimingLeadsOtherServicesInEitherModeAndOrder() {
        func candidate(_ source: LyricSourceID, native: Bool, score: Double) -> LyricCandidate {
            var line = LyricsFetcher.makeLine(text: "hello", start: 1, end: 2); line.wordTimed = native
            return .init(source: source, result: .synced([line], .init(precision: "test", source: source.title)), title: "Song", artist: "Artist", score: score)
        }
        let query = LyricQuery(title: "Song", artist: "Artist", album: nil, duration: 0)
        let values = [candidate(.netEase, native: true, score: 110), candidate(.lrclib, native: true, score: 95)]
        for mode in LyricSourceOptions.Mode.allCases {
            for order in [[LyricSourceID.netEase, .lrclib], [.lrclib, .netEase]] {
                let options = LyricSourceOptions.Snapshot(enabled: order, mode: mode, localFolder: "", localVersion: "")
                XCTAssertEqual(LyricsFetcher.choose(values, query: query, options: options)?.source, .lrclib)
            }
        }
        let options = LyricSourceOptions.Snapshot(enabled: [.netEase], mode: .smart, localFolder: "", localVersion: "")
        XCTAssertEqual(LyricsFetcher.choose(values, query: query, options: options)?.source, .netEase)
    }
    @MainActor
    func testForcedLowPowerUsesLightWallpaperAndThirtyFPS() {
        let s = Settings.shared
        let mode = s.backgroundMode, rate = s.animationRate, low = s.lowPowerVisuals
        defer { s.backgroundMode = mode; s.animationRate = rate; s.lowPowerVisuals = low }
        s.lowPowerVisuals = true; s.animationRate = .smooth
        for background in [Settings.BackgroundMode.auto, .shader, .visualizer] {
            s.backgroundMode = background
            XCTAssertEqual(s.effectiveBackgroundMode, .pattern)
            XCTAssertEqual(s.effectiveAnimationRate, .balanced)
        }
    }
}

private final class LyricStubProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let exact = request.url!.path.hasSuffix("/get")
        let body = exact ? "{}" : #"[{"trackName":"Test Song","artistName":"Test Artist","duration":20,"syncedLyrics":"[00:01]Found by search\n[00:05]Next line"}]"#
        let response = HTTPURLResponse(url: request.url!, statusCode: exact ? 503 : 200, httpVersion: "HTTP/1.1", headerFields: ["Retry-After": "0"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

extension NextVersionTests {
    func testLRCLIBSearchStillRunsAfterExactEndpoint503() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [LyricStubProtocol.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let query = LyricQuery(title: "Test Song", artist: "Test Artist", album: nil, duration: 20)
        let match = try await LyricsFetcher.lrclib(query, session: session)
        XCTAssertEqual(match?.source, .lrclib)
        XCTAssertEqual(match?.title, "Test Song")
        XCTAssertEqual(match?.lineCount, 2)
    }
}
