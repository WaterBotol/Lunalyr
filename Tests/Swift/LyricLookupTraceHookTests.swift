import XCTest
@testable import VersoCustom

/// The lookup trace (`LyricLookupTracer`), offline: what it records with a
/// tracer injected, and that nothing is recorded or built without one.
final class LyricLookupTraceHookTests: XCTestCase {
    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)
    private let options = LyricSourceOptions.Snapshot(enabled: [.lyricsPlus, .netEase, .lrclib], mode: .smart, localFolder: "", localVersion: "")
    private let fast = LyricsFetcher.Timing(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.3, lrclibLimit: 2, sourceLimit: 2)
    private var folder: URL!

    override func setUpWithError() throws {
        // Lookups write their cache here, never to Application Support.
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("LunalyrTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        Stub.asked = []
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: folder) }

    private func candidate(_ source: LyricSourceID, title: String = "Our Song", artist: String = "Our Artist",
                           duration: Double? = 180, words: Bool = true) -> LyricCandidate {
        let lines = (0..<6).map { index -> LyricsFetcher.Line in
            var line = LyricsFetcher.makeLine(text: "Sing along \(index)", start: Double(index * 4 + 1), end: Double(index * 4 + 4))
            if words {
                line.wordTimed = true
                line.words = [.init(text: "Sing", start: line.start, end: line.start + 1),
                              .init(text: "along", start: line.start + 1, end: line.start + 2),
                              .init(text: "\(index)", start: line.start + 2, end: line.start + 2.5)]
            }
            return line
        }
        return LyricCandidate(source: source, result: .synced(lines, .init(precision: LyricsFetcher.precision(lines), source: source.title)),
                              title: title, artist: artist, duration: duration, score: 100)
    }

    // MARK: - A whole lookup

    func testRecordsSourcesRejectionsPublicationAndThePick() async throws {
        let recorder = LyricLookupTraceRecorder()
        let good = candidate(.lrclib), wrongLength = candidate(.netEase, duration: 240)
        let fetcher = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }, fetch: { id, _, _ in
            switch id {
            case .lrclib:
                LyricTrace.note("inside the LRCLIB fetch")
                return good
            case .netEase: return wrongLength
            default: return nil
            }
        }, tracer: recorder)
        let report = await fetcher.lookup(query, force: true, options: options)
        XCTAssertNotNil(report.result)

        let lookup = try XCTUnwrap(recorder.events(.lookup).first)
        XCTAssertEqual(lookup.query, LyricTraceQuery(query), "the query as received")
        XCTAssertEqual(lookup.sources, ["lyricsPlus", "netEase", "lrclib"])
        XCTAssertEqual(Set(recorder.events(.sourceStart).compactMap(\.source)), ["lyricsPlus", "netEase", "lrclib"])
        XCTAssertEqual(Set(recorder.events(.sourceEnd).compactMap(\.source)), ["lyricsPlus", "netEase", "lrclib"])
        // Hooks inside a source's fetch are filed under that source.
        XCTAssertEqual(recorder.events(.note).first { $0.message == "inside the LRCLIB fetch" }?.source, "lrclib")

        let final = try XCTUnwrap(recorder.events(.final).last)
        XCTAssertEqual(final.source, "lrclib", "the pick")
        XCTAssertEqual(final.ranked?.map(\.source), ["lrclib"])
        XCTAssertEqual(final.ranked?.first?.rank, 1)
        XCTAssertEqual(final.ranked?.first?.timing, "word")
        XCTAssertEqual(final.ranked?.first?.titleSimilarity, 1)
        let rejected = try XCTUnwrap(final.rejected?.first)
        XCTAssertEqual(rejected.source, "netEase")
        XCTAssertEqual(rejected.verdict, "rejected")
        XCTAssertEqual(rejected.reason, "Rejected: duration 4:00 vs 3:00")
        XCTAssertEqual(rejected.durationDelta, 60)
        XCTAssertNotNil(final.states?["lyricsPlus"])

        let published = recorder.events(.publication).compactMap(\.publication).filter { $0.step == "publish" }
        XCTAssertEqual(published.first?.shownAfter, "lrclib")
        XCTAssertNil(published.first?.shownBefore)

        // The written document digests it per source.
        let document = LyricLookupTraceDocument(song: .init(title: query.title, artist: query.artist, duration: 180),
                                                storefront: nil, events: recorder.events, startedAt: Date())
        XCTAssertEqual(document.pick?.source, "lrclib")
        XCTAssertEqual(document.digest.map(\.verdict), ["none", "Rejected: duration 4:00 vs 3:00", "picked"])
        XCTAssertNoThrow(try JSONEncoder.trace.encode(document))
    }

    func testAnAutomaticLookupRecordsTheSourcesItStopped() async throws {
        let recorder = LyricLookupTraceRecorder()
        // LRCLIB's word timing: neither Kuwo (line timing at best) nor lyrics.ovh (plain text) could beat it.
        let options = LyricSourceOptions.Snapshot(enabled: [.lrclib, .kuwo, .lyricsOVH], mode: .smart, localFolder: "", localVersion: "")
        let good = candidate(.lrclib)
        let fetcher = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }, fetch: { id, _, _ in
            if id == .lrclib { return good }
            try await Task.sleep(nanoseconds: 1_500_000_000)
            return nil
        }, tracer: recorder)
        _ = await fetcher.lookup(query, options: options)
        let stopped = recorder.events(.stop)
        XCTAssertTrue(stopped.allSatisfy { $0.message?.hasPrefix(LyricsFetcher.notNeeded) == true })
        XCTAssertEqual(Set(stopped.flatMap { $0.sources ?? [] }), ["kuwo", "lyricsOVH"])
        XCTAssertEqual(recorder.events(.final).last?.states?["kuwo"]?.hasPrefix(LyricsFetcher.notNeeded), true)
        XCTAssertEqual(recorder.events(.cache).last?.message, "online choice saved to the cache")
    }

    // MARK: - Query variants

    func testRecordsTheArtistAliasVariantsAndTheirRequests() async throws {
        let recorder = LyricLookupTraceRecorder()
        let aliases = ArtistAliases(folder: folder, session: Stub.session)
        let lemon = LyricQuery(title: "Lemon", artist: "Kenshi Yonezu", album: nil, duration: 255)
        let native = candidate(.netEase, title: "Lemon", artist: "米津玄師", duration: 255)
        let found = try await LyricTrace.$tracer.withValue(recorder) {
            try await LyricTrace.within(source: .netEase) {
                try await LyricsFetcher.withAliases(.netEase, query: lemon, aliases: aliases, enabled: true) { q in
                    q.artist == "米津玄師" ? native : nil
                }
            }
        }
        XCTAssertEqual(found?.artist, "Kenshi Yonezu")
        let variants = recorder.events(.variant)
        XCTAssertEqual(variants.map(\.variant), ["artist alias “米津玄师”", "artist alias “米津玄師”"])
        XCTAssertEqual(variants.map(\.query?.artist), ["米津玄师", "米津玄師"])
        XCTAssertTrue(variants.allSatisfy { $0.source == "netEase" })
        let results = recorder.events(.variantResult)
        XCTAssertEqual(results.map(\.message), ["nothing under the library artist: trying aliases", "nothing under this alias",
                                                "used; recorded under the library artist"])
        XCTAssertEqual(results.last?.candidate?.artist, "米津玄師")
        let request = try XCTUnwrap(recorder.events(.request).first?.request)
        XCTAssertEqual(request.status, 200)
        XCTAssertTrue(request.url.hasPrefix("https://musicbrainz.org/ws/2/artist?"))
    }

    func testRecordsTheOriginalTitleVariantAndWhyTheListedAnswerWasPassedOver() async throws {
        let recorder = LyricLookupTraceRecorder()
        let titles = OriginalTitles(folder: folder, session: Stub.session, storefront: "us", spacing: 0)
        let racing = LyricQuery(title: "Racing into the Night", artist: "YOASOBI", album: nil, duration: 258)
        let japanese = LyricCandidate(source: .lrclib, result: .plain(Array(repeating: "沈むように溶けてゆくように", count: 6),
                                                                      .init(precision: "Not synced", source: "LRCLIB")),
                                      title: "夜に駆ける", artist: "YOASOBI", duration: 258, score: 90)
        let found = try await LyricTrace.$tracer.withValue(recorder) {
            try await LyricTrace.within(source: .lrclib) {
                try await LyricsFetcher.withOriginalTitle(.lrclib, query: racing, titles: titles, enabled: true) { q in
                    q.title == "夜に駆ける" ? japanese : nil
                }
            }
        }
        XCTAssertTrue(found?.viaOriginalTitle == true)
        XCTAssertEqual(recorder.events(.variant).map(\.variant), [nil, "original title “夜に駆ける”"])
        XCTAssertEqual(recorder.events(.variant).map(\.query?.title), ["Racing into the Night", "夜に駆ける"])
        XCTAssertEqual(recorder.events(.variantResult).map(\.message),
                       ["nothing under the listed title: looking up the original title", "used; recorded under the listed title"])
        XCTAssertEqual(recorder.events(.request).count, 2, "iTunes search, then the Japanese storefront")
        XCTAssertEqual(recorder.events(.results).first?.results?.returned, 1)
    }

    func testRequestAddressesLoseKeysAndTokens() {
        let url = URL(string: "https://lyrics.kugou.com/download?id=1&accesskey=ABC&fmt=krc&token=T&api_key=K&g_tk=5381&keyword=Lemon")
        let shown = LyricTrace.redacted(url)
        XCTAssertFalse(shown.contains("ABC") || shown.contains("=T&") || shown.contains("=K"))
        XCTAssertTrue(shown.contains("accesskey=REDACTED") && shown.contains("token=REDACTED") && shown.contains("api_key=REDACTED"))
        // Search terms and harmless parameters stay readable.
        XCTAssertTrue(shown.contains("id=1") && shown.contains("fmt=krc") && shown.contains("g_tk=5381") && shown.contains("keyword=Lemon"))
    }

    // MARK: - Off by default

    func testNothingIsRecordedOrBuiltWithoutATracer() async throws {
        XCTAssertFalse(LyricTrace.isOn)
        var built = false
        func event() -> LyricTraceEvent { built = true; return LyricTraceEvent(kind: .note) }
        LyricTrace.emit(event())
        XCTAssertFalse(built, "events are not even built when no tracer is set")

        // A lookup without a tracer, alongside a traced one: none of its events reach the tracer.
        let recorder = LyricLookupTraceRecorder()
        nonisolated(unsafe) var tracingSeen: [Bool] = []
        let lock = NSLock()
        let plain = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }, fetch: { _, _, _ in
            lock.withLock { tracingSeen.append(LyricTrace.isOn) }
            return nil
        })
        let good = candidate(.lrclib)
        let traced = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true },
                                   fetch: { id, _, _ in id == .lrclib ? good : nil }, tracer: recorder)
        let other = LyricQuery(title: "Other Song", artist: "Other Artist", album: nil, duration: 200)
        async let untracedReport = plain.lookup(other, force: true, options: options)
        async let tracedReport = traced.lookup(query, force: true, options: options)
        _ = await (untracedReport, tracedReport)
        XCTAssertEqual(tracingSeen, [false, false, false])
        XCTAssertTrue(recorder.events.allSatisfy { $0.query == nil || $0.query == LyricTraceQuery(query) })
        XCTAssertFalse(recorder.events.contains { $0.candidate?.title == "Other Song" })
        XCTAssertEqual(recorder.events(.lookup).count, 1)
        XCTAssertFalse(LyricTrace.isOn)
    }

    // MARK: - Stubbed catalogues (no network)

    private final class Stub: URLProtocol {
        nonisolated(unsafe) static var asked: [URL] = []
        static var session: URLSession {
            let config = URLSessionConfiguration.ephemeral
            config.protocolClasses = [Stub.self]
            return URLSession(configuration: config)
        }
        override class func canInit(with request: URLRequest) -> Bool { true }
        override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
        override func startLoading() {
            let url = request.url!
            Stub.asked.append(url)
            let body: String
            switch (url.host ?? "", url.path) {
            case ("musicbrainz.org", _):
                body = #"{"artists":[{"name":"米津玄師","score":100,"aliases":[{"name":"Kenshi Yonezu","locale":"en","primary":true},{"name":"米津玄师","locale":"zh_Hans","primary":true}]}]}"#
            case (_, "/search"):
                body = #"{"resultCount":1,"results":[{"trackId":1001,"artistName":"YOASOBI","trackName":"Racing into the Night","trackTimeMillis":258000}]}"#
            default:
                body = #"{"resultCount":1,"results":[{"trackId":1001,"artistName":"YOASOBI","collectionName":"夜に駆ける - Single","trackName":"夜に駆ける","trackTimeMillis":258000}]}"#
            }
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil)!,
                                cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(body.utf8))
            client?.urlProtocolDidFinishLoading(self)
        }
        override func stopLoading() {}
    }
}
