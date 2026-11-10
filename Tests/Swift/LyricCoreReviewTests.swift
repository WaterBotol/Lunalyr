import XCTest
@testable import VersoCustom

/// Lyric core review, 10 Nov: timestamps written by the KRC and Kuwo
/// converters, a service's choice among its own hits, LRCLIB's instrumental
/// flag, Accuracy first tie-breaks that depended on answer order, and reruns
/// for lyrics already on screen.
final class LyricCoreReviewTests: XCTestCase {
    /// Answers by host and path; nothing reaches the network.
    private final class Service: URLProtocol {
        struct Reply { var status: Int; var body: String }
        final class Store: @unchecked Sendable {
            private let lock = NSLock()
            private var replies: [String: Reply] = [:]
            func set(_ address: String, _ reply: Reply) { lock.withLock { replies[address] = reply } }
            func reply(_ url: URL) -> Reply { lock.withLock { replies[(url.host ?? "") + url.path] ?? Reply(status: 404, body: "") } }
            func reset() { lock.withLock { replies = [:] } }
        }
        static let store = Store()
        override class func canInit(with request: URLRequest) -> Bool { true }
        override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
        override func startLoading() {
            let reply = Self.store.reply(request.url!)
            let response = HTTPURLResponse(url: request.url!, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: [:])!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(reply.body.utf8))
            client?.urlProtocolDidFinishLoading(self)
        }
        override func stopLoading() {}
    }
    private var session: URLSession!
    private var folder: URL!

    override func setUpWithError() throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [Service.self]
        session = URLSession(configuration: config)
        Service.store.reset()
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("LunalyrTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws {
        session.invalidateAndCancel()
        Service.store.reset()
        try? FileManager.default.removeItem(at: folder)
    }

    // MARK: - Converted timestamps

    func testConvertedStampsNeverSaySixtySeconds() {
        // Rounding the seconds alone wrote these as "00:60.00" and "01:60.00".
        XCTAssertEqual(KugouKRC.clock(59.9996), "01:00.000")
        XCTAssertEqual(KugouKRC.clock(119.998), "01:59.998")
        XCTAssertEqual(KugouKRC.clock(0), "00:00.000")
        XCTAssertEqual(KugouKRC.clock(-2), "00:00.000")
        XCTAssertEqual(KugouKRC.clock(3725.25), "62:05.250")
    }

    func testAKRCLineOrWordAtAMinuteBoundaryIsKept() throws {
        // A line starting at 59.996 s, and a word at 1:59.996.
        let krc = "[59996,3000]<0,1000,0>Hello <1000,1000,0>world\n[118000,3000]<0,1996,0>Once <1996,1000,0>more"
        let lrc = try XCTUnwrap(KugouKRC.enhancedLRC(fromText: krc))
        let lines = LyricsFetcher.parseLRC(lrc, trackDuration: 200).filter { !$0.isInstrumental }
        XCTAssertEqual(lines.map(\.text), ["Hello world", "Once more"])
        guard lines.count == 2 else { return }
        XCTAssertEqual(lines[0].start, 59.996, accuracy: 0.001)
        XCTAssertTrue(lines.allSatisfy(\.wordTimed))
        XCTAssertEqual(lines[1].words.map(\.text), ["Once", "more"])
        XCTAssertEqual(lines[1].words[1].start, 119.996, accuracy: 0.001)
    }

    func testStampsWrittenApartAllLeadTheRow() {
        let lines = LyricsFetcher.parseLRC("[00:12.00] [00:45.00]Chorus\n[00:20.00]Verse", trackDuration: 60)
        XCTAssertEqual(lines.map(\.text), ["Chorus", "Verse", "Chorus"])
        XCTAssertEqual(lines.map(\.start), [12, 20, 45])
    }

    // MARK: - A service's own choice

    private let song = LyricQuery(title: "Our Song", artist: "Our Artist", album: "Our Album", duration: 180)
    private func row(_ title: String, artist: String = "Our Artist", album: String = "Our Album", duration: Double) -> String {
        #"{"trackName":"\#(title)","artistName":"\#(artist)","albumName":"\#(album)","duration":\#(duration),"syncedLyrics":"[00:01.00]Sing along\n[00:04.00]Next line"}"#
    }

    func testLRCLIBOffersTheHitThatIsThePlayingRecording() async throws {
        // The exact title, four seconds too long, outscores an edition that
        // fits; it fails the recording check, so the one that fits is offered.
        Service.store.set("lrclib.net/api/search", .init(status: 200, body:
            "[" + row("Our Song", duration: 184) + "," + row("Our Song - Remastered", artist: "Our Artist & Friend", album: "Other", duration: 180) + "]"))
        let offered = try await LyricsFetcher.lrclib(song, session: session)
        let found = try XCTUnwrap(offered)
        XCTAssertEqual(found.duration, 180)
        XCTAssertNil(LyricsFetcher.rejection(found, query: song))
    }

    func testKugouFetchesTheHitThatIsThePlayingRecordingFirst() async throws {
        let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 120)
        // The first hit scores higher but is 3.5 s too long for a two-minute song.
        Service.store.set("mobileservice.kugou.com/api/v3/search/song", .init(status: 200, body:
            #"{"data":{"info":[{"songname":"Our Song","singername":"Our Artist","duration":123.5,"hash":"a"},"# +
            #"{"songname":"Our Song (Acoustic)","singername":"Our Artist","duration":120,"hash":"b"}]}}"#))
        Service.store.set("lyrics.kugou.com/search", .init(status: 200, body: #"{"candidates":[{"id":"1","accesskey":"k"}]}"#))
        let lrc = Data("[00:01.00]Sing along\n[00:04.00]Next line".utf8).base64EncodedString()
        Service.store.set("lyrics.kugou.com/download", .init(status: 200, body: #"{"content":"\#(lrc)"}"#))
        let offered = try await AdditionalLyricsProviders.kugou(query, session: session)
        let found = try XCTUnwrap(offered)
        XCTAssertEqual(found.duration, 120)
        XCTAssertNil(LyricsFetcher.rejection(found, query: query))
    }

    func testAnInstrumentalRecordingOnLRCLIBHasNoLyrics() async throws {
        // An album's instrumental edition shares the sung one's names and length.
        Service.store.set("lrclib.net/api/get", .init(status: 200, body:
            #"{"trackName":"Our Song","artistName":"Our Artist","albumName":"Our Album","duration":180,"instrumental":true,"plainLyrics":null,"syncedLyrics":null}"#))
        Service.store.set("lrclib.net/api/search", .init(status: 200, body: "[" + row("Our Song", album: "Sung", duration: 180) + "]"))
        let found = try await LyricsFetcher.lrclib(song, session: session)
        XCTAssertNil(found)
    }

    // MARK: - Accuracy first

    private func words(_ source: LyricSourceID, duration: Double?) -> LyricCandidate {
        var line = LyricsFetcher.makeLine(text: "Sing along", start: 1, end: 3)
        line.wordTimed = true
        line.words = [.init(text: "Sing", start: 1, end: 2), .init(text: "along", start: 2, end: 3)]
        return LyricCandidate(source: source, result: .synced([line], .init(precision: "Word-by-word", source: source.title)),
                              title: "Our Song", artist: "Our Artist", duration: duration, score: 0)
    }

    func testAMatchWithoutALengthWaitsForOneWhoseLengthFits() {
        let options = LyricSourceOptions.Snapshot(enabled: [.lyricsPlus, .netEase], mode: .smart, localFolder: "", localVersion: "")
        let unmeasured = words(.lyricsPlus, duration: nil), measured = words(.netEase, duration: 180)
        // Ranking puts the fitting length first...
        XCTAssertTrue(LyricsFetcher.outranks(measured, unmeasured, query: song, options: options))
        // ...so a source that may still return one is worth waiting for.
        XCTAssertTrue(LyricsFetcher.couldBeat(unmeasured, from: .netEase, query: song, options: options))
        XCTAssertFalse(LyricsFetcher.isSettled(unmeasured, pending: [.netEase], query: song, options: options))
        // An exact match with a fitting length is beaten only by the order.
        XCTAssertFalse(LyricsFetcher.couldBeat(measured, from: .amll, query: song,
            options: .init(enabled: [.netEase, .amll], mode: .smart, localFolder: "", localVersion: "")))
        XCTAssertTrue(LyricsFetcher.couldBeat(measured, from: .lyricsPlus, query: song, options: options))
    }

    func testTheSameMatchWinsWhicheverSourceAnswersFirst() async {
        let options = LyricSourceOptions.Snapshot(enabled: [.lyricsPlus, .netEase], mode: .smart, localFolder: "", localVersion: "")
        let timing = LyricsFetcher.Timing(grace: 0.1, upgradeSettle: 0.05, appleRetry: 0.1, lrclibLimit: 3, sourceLimit: 3, musicFirst: 0.1)
        let unmeasured = words(.lyricsPlus, duration: nil), measured = words(.netEase, duration: 180)
        for slow in [LyricSourceID.lyricsPlus, .netEase] {
            let delays = [slow: 0.4]
            let fetcher = LyricsFetcher(cacheFolder: folder.appendingPathComponent(slow.rawValue), timing: timing,
                                        misses: LyricMissCache(), isOnline: { true }) { id, _, _ in
                try await Task.sleep(nanoseconds: UInt64((delays[id] ?? 0.02) * 1_000_000_000))
                return id == .lyricsPlus ? unmeasured : measured
            }
            let report = await fetcher.lookup(song, options: options)
            guard case .synced(_, let provenance)? = report.result else { return XCTFail("nothing shown") }
            XCTAssertEqual(provenance.source, LyricSourceID.netEase.title, "\(slow) answered last")
        }
    }

    // MARK: - Reruns

    func testARerunForLyricsOnScreenSwapsOnceAtALineBoundary() {
        func update(_ revision: Int, complete: Bool, empty: Bool = false) -> LyricLookupReport {
            let result: LyricsFetcher.Result? = empty ? nil : .plain(["Sing"], .init(precision: "", source: "x"))
            return LyricLookupReport(result: result, revision: revision, isComplete: complete)
        }
        // A track's first lookup: its first match shows at once, upgrades swap between lines.
        XCTAssertEqual(LyricsViewModel.reception(of: update(1, complete: false), applied: 0, rerun: false), .show)
        XCTAssertEqual(LyricsViewModel.reception(of: update(2, complete: false), applied: 1, rerun: false), .swapAtBoundary)
        XCTAssertEqual(LyricsViewModel.reception(of: update(2, complete: true), applied: 2, rerun: false), .ignore)
        XCTAssertEqual(LyricsViewModel.reception(of: update(0, complete: true, empty: true), applied: 0, rerun: false), .show)
        // A rerun for lyrics on screen shows nothing it has not settled on.
        XCTAssertEqual(LyricsViewModel.reception(of: update(1, complete: false), applied: 0, rerun: true), .ignore)
        XCTAssertEqual(LyricsViewModel.reception(of: update(2, complete: true), applied: 0, rerun: true), .swapAtBoundary)
        // Finding nothing at all (every source switched off) clears at once.
        XCTAssertEqual(LyricsViewModel.reception(of: update(0, complete: true, empty: true), applied: 0, rerun: true), .show)
    }
}
