import XCTest
@testable import VersoCustom

/// Providers report the recording they matched, and Lyrics+ copes with
/// community mirrors that come and go.
final class LyricProviderTests: XCTestCase {
    func testNetEasePicksTheEditionThatFitsAndReportsIt() {
        typealias Song = NetEaseLyricsProvider.SearchHit
        let artists = ["Our Artist"]
        let songs = [
            Song(id: 1, name: "Our Song (Live)", artists: artists, duration: 186_000),
            Song(id: 2, name: "Our Song", artists: artists, duration: 185_000),
            Song(id: 3, name: "Our Song", artists: ["Someone Else"], duration: 180_000),
            Song(id: 4, name: "Our Song", artists: artists + ["Guest"], duration: 180_400),
            Song(id: 5, name: "Our Songbook", artists: artists, duration: 180_000),
        ]
        let match = NetEaseLyricsProvider.bestSong(songs, title: "Our Song", artist: "Our Artist", duration: 180)
        XCTAssertEqual(match, .init(id: 4, title: "Our Song", artist: "Our Artist, Guest", duration: 180.4))
        // Editions eight or more seconds apart are not offered at all.
        XCTAssertNil(NetEaseLyricsProvider.bestSong([songs[0]], title: "Our Song", artist: "Our Artist", duration: 170))
        // The closest edit within reach is still reported, so ranking can say why it is rejected.
        XCTAssertEqual(NetEaseLyricsProvider.bestSong([songs[1]], title: "Our Song", artist: "Our Artist", duration: 180)?.duration, 185)
    }

    private final class Mirror: URLProtocol {
        struct Reply { var status: Int; var body: String }
        final class Store: @unchecked Sendable {
            private let lock = NSLock()
            private var replies: [String: Reply] = [:]
            private var requests: [String: Int] = [:]
            func set(_ host: String, _ reply: Reply) { lock.withLock { replies[host] = reply } }
            func take(_ host: String) -> Reply {
                lock.withLock {
                    requests[host, default: 0] += 1
                    return replies[host] ?? Reply(status: 500, body: "")
                }
            }
            func count(_ host: String) -> Int { lock.withLock { requests[host] ?? 0 } }
        }
        static let store = Store()
        override class func canInit(with request: URLRequest) -> Bool { true }
        override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
        override func startLoading() {
            let reply = Self.store.take(request.url?.host ?? "")
            let response = HTTPURLResponse(url: request.url!, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: [:])!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(reply.body.utf8))
            client?.urlProtocolDidFinishLoading(self)
        }
        override func stopLoading() {}
    }
    private static let words = #"{"type":"Word","metadata":{"source":"Apple (via Lyrics+)","title":"Our Song (Remastered)","artist":["Our Artist"]},"lyrics":[{"time":1000,"duration":2000,"text":"Sing along","syllabus":[{"time":1000,"duration":900,"text":"Sing "},{"time":2000,"duration":1000,"text":"along"}]}]}"#

    private func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [Mirror.self]
        return URLSession(configuration: config)
    }
    private func hosts(_ count: Int) -> [String] {
        let run = UUID().uuidString.prefix(8).lowercased()
        return (0..<count).map { "kpoe-\(run)-\($0).test" }
    }

    func testANodesNotFoundIsCheckedWithOneOtherMirror() async throws {
        let names = hosts(3), session = session(), urls = names.map { "https://" + $0 }
        defer { session.invalidateAndCancel() }
        // The first node is up but has lost its upstream; the next one serves.
        Mirror.store.set(names[0], .init(status: 404, body: #"{"error":"Lyrics not found"}"#))
        Mirror.store.set(names[1], .init(status: 200, body: Self.words))
        Mirror.store.set(names[2], .init(status: 200, body: Self.words))
        let hit = try await KPoeLyricsProvider.lookup(title: "Our Song", artist: "Our Artist", album: nil, duration: 180,
                                                      hosts: urls, session: session)
        XCTAssertEqual(hit?.lines.first?.words.map(\.text), ["Sing", "along"])
        XCTAssertEqual(Mirror.store.count(names[2]), 0)
        // Contradicted, the empty-handed node rests: the serving one is asked first.
        _ = try await KPoeLyricsProvider.lookup(title: "Our Song", artist: "Our Artist", album: nil, duration: 180,
                                                hosts: urls, session: session)
        XCTAssertEqual(Mirror.store.count(names[0]), 1)
        XCTAssertEqual(Mirror.store.count(names[1]), 2)
    }

    func testNotFoundEverywhereEndsAfterOneSecondOpinion() async throws {
        let names = hosts(3), session = session()
        defer { session.invalidateAndCancel() }
        for name in names { Mirror.store.set(name, .init(status: 404, body: #"{"error":"Lyrics not found"}"#)) }
        let hit = try await KPoeLyricsProvider.lookup(title: "Our Song", artist: "Our Artist", album: nil, duration: 180,
                                                      hosts: names.map { "https://" + $0 }, session: session)
        XCTAssertNil(hit)
        XCTAssertEqual(names.map { Mirror.store.count($0) }, [1, 1, 0])
    }

    func testDeadMirrorsRestAndMatchedRecordingIsReported() async throws {
        let names = hosts(2), session = session(), urls = names.map { "https://" + $0 }
        defer { session.invalidateAndCancel() }
        Mirror.store.set(names[0], .init(status: 404, body: "<html>error code: 1042</html>"))
        Mirror.store.set(names[1], .init(status: 200, body: Self.words))
        let hit = try await KPoeLyricsProvider.lookup(title: "Our Song", artist: "Our Artist", album: nil, duration: 180,
                                                      hosts: urls, session: session)
        XCTAssertEqual(hit?.title, "Our Song (Remastered)")
        XCTAssertEqual(hit?.artist, "Our Artist")
        XCTAssertEqual(hit?.source, "Apple")
        XCTAssertEqual(hit?.lines.first?.words.map(\.text), ["Sing", "along"])
        // The failed node is tried after the healthy one next time.
        _ = try await KPoeLyricsProvider.lookup(title: "Our Song", artist: "Our Artist", album: nil, duration: 180,
                                                hosts: urls, session: session)
        XCTAssertEqual(Mirror.store.count(names[0]), 1)
        XCTAssertEqual(Mirror.store.count(names[1]), 2)
    }

    func testLyricsPlusKeepsTheSyllablesOfAnUnspacedLineApart() async throws {
        // Syllables as Lyrics+ sends them, each timed, written together
        // unless a space ends the one before. Invented lines.
        func entry(_ text: String, _ syllables: [(String, Int, Int)]) -> String {
            let parts = syllables.map { #"{"time":\#($0.1),"duration":\#($0.2),"text":"\#($0.0)"}"# }
            let start = syllables[0].1, end = syllables[syllables.count - 1].1 + syllables[syllables.count - 1].2
            return #"{"time":\#(start),"duration":\#(end - start),"text":"\#(text)","syllabus":[\#(parts.joined(separator: ","))]}"#
        }
        let entries = [
            entry("夕焼けの帰り道", [("夕焼け", 1000, 600), ("の", 1600, 200), ("帰り", 1800, 400), ("道", 2200, 500)]),
            entry("我们一起走", [("我", 3000, 300), ("们", 3300, 300), ("一", 3600, 300), ("起", 3900, 300), ("走", 4200, 600)]),
            entry("แมวนอนบนโต๊ะ", [("แมว", 5000, 400), ("นอน", 5400, 400), ("บน", 5800, 300), ("โต๊ะ", 6100, 600)]),
            entry("고양이가 창문을 본다", [("고양", 7000, 300), ("이가 ", 7300, 300), ("창문", 7600, 400), ("을 ", 8000, 200),
                                     ("본다", 8200, 600)]),
            entry("So beautiful, tonight", [("So ", 9000, 300), ("beau", 9300, 300), ("ti", 9600, 200), ("ful", 9800, 300),
                                            (", ", 10100, 50), ("to", 10200, 300), ("night", 10500, 500)]),
        ]
        let names = hosts(1), session = session()
        defer { session.invalidateAndCancel() }
        Mirror.store.set(names[0], .init(status: 200, body:
            #"{"type":"Word","metadata":{"source":"Apple"},"lyrics":[\#(entries.joined(separator: ","))]}"#))
        let hit = try await KPoeLyricsProvider.lookup(title: "Our Song", artist: "Our Artist", album: nil, duration: 180,
                                                      hosts: names.map { "https://" + $0 }, session: session)
        let lines = try XCTUnwrap(hit?.lines)
        // Han, kana, Thai and Hangul syllables stay timed tokens of their
        // own, as TTML and YRC keep them; joined up to the next space, each
        // unspaced line was one word timed from its first syllable.
        XCTAssertEqual(lines.map { $0.words.map(\.text) }, [
            ["夕焼け", "の", "帰り", "道"], ["我", "们", "一", "起", "走"], ["แมว", "นอน", "บน", "โต๊ะ"],
            ["고양", "이가", "창문", "을", "본다"], ["So", "beautiful,", "tonight"],
        ])
        XCTAssertEqual(lines[0].words.map(\.start), [1, 1.6, 1.8, 2.2])
        XCTAssertEqual(lines[0].words.map(\.end), [1.6, 1.8, 2.2, 2.7])
        XCTAssertEqual(lines[2].words.map(\.start), [5, 5.4, 5.8, 6.1])
        // Latin syllables still make the word they spell, first start to last end.
        XCTAssertEqual(lines[4].words[1].start, 9.3, accuracy: 1e-9)
        XCTAssertEqual(lines[4].words[1].end, 10.1, accuracy: 1e-9)
        XCTAssertEqual(lines[4].words[2].start, 10.2, accuracy: 1e-9)
        XCTAssertTrue(lines.allSatisfy(LyricsFetcher.validWordTiming))
        XCTAssertEqual(LyricsFetcher.precision(lines), "Word-by-word")
        // On screen the Japanese tokens still run together as written, and
        // the Korean syllables as their words.
        XCTAssertEqual(LyricRuns.glue(line: lines[0], words: lines[0].words), [false, true, true, true])
        XCTAssertEqual(LyricRuns.labels(lines[3].words, glue: LyricRuns.glue(line: lines[3], words: lines[3].words)),
                       ["고양이가", "창문을", "본다"])
    }

    /// Answers music.163.com by path: search, then lyrics.
    private final class NetEase: URLProtocol {
        final class Store: @unchecked Sendable {
            private let lock = NSLock()
            private var replies: [String: Mirror.Reply] = [:]
            func set(_ path: String, _ reply: Mirror.Reply) { lock.withLock { replies[path] = reply } }
            func reply(_ path: String) -> Mirror.Reply { lock.withLock { replies[path] ?? Mirror.Reply(status: 500, body: "") } }
        }
        static let store = Store()
        override class func canInit(with request: URLRequest) -> Bool { true }
        override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
        override func startLoading() {
            let reply = Self.store.reply(request.url?.path ?? "")
            let response = HTTPURLResponse(url: request.url!, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: [:])!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(reply.body.utf8))
            client?.urlProtocolDidFinishLoading(self)
        }
        override func stopLoading() {}
    }

    func testNetEaseOutagesAreErrorsButAMissingSongIsNot() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [NetEase.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)
        func fails(_ reason: String) async {
            do { _ = try await LyricsFetcher.netEase(query, session: session); XCTFail(reason) } catch {}
        }
        // Outages throw, so a lookup asks again next time rather than
        // remembering "no lyrics".
        NetEase.store.set("/api/search/get", .init(status: 503, body: ""))
        await fails("A 503 is an outage")
        NetEase.store.set("/api/search/get", .init(status: 200, body: #"{"code":-460,"message":"Cheating"}"#))
        await fails("An error reply is an outage")
        NetEase.store.set("/api/search/get", .init(status: 200, body: #"{"result":{"songCount":0},"code":200}"#))
        let missing = try await LyricsFetcher.netEase(query, session: session)
        XCTAssertNil(missing)
        NetEase.store.set("/api/search/get", .init(status: 200, body:
            #"{"result":{"songs":[{"id":7,"name":"Our Song","artists":[{"name":"Our Artist"}],"duration":181000}]},"code":200}"#))
        NetEase.store.set("/api/song/lyric", .init(status: 200, body: #"{"lrc":{"lyric":"[00:01.00]Sing along\n[00:04.00]Next line"}}"#))
        let found = try await LyricsFetcher.netEase(query, session: session)
        XCTAssertEqual(found?.duration, 181)
        XCTAssertEqual(found?.lineCount, 2)
        XCTAssertEqual(found?.metadataReported, true)
        NetEase.store.set("/api/song/lyric", .init(status: 502, body: ""))
        await fails("A failed lyric request is an outage")
    }

    /// Answers Kugou by host and path, as the live service did when checked,
    /// and remembers each request.
    private final class Kugou: URLProtocol {
        final class Store: @unchecked Sendable {
            private let lock = NSLock()
            private var replies: [String: Mirror.Reply] = [:]
            private var seen: [URL] = []
            func set(_ address: String, _ reply: Mirror.Reply) { lock.withLock { replies[address] = reply } }
            func reply(_ url: URL) -> Mirror.Reply {
                lock.withLock {
                    seen.append(url)
                    return replies[(url.host ?? "") + url.path] ?? Mirror.Reply(status: 500, body: "")
                }
            }
            var requests: [URL] { lock.withLock { seen } }
            /// Forgets every reply and request, so a test run sees only its own.
            func reset() { lock.withLock { replies = [:]; seen = [] } }
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

    func testKugouSearchesItsWorkingHostThenFetchesTheLyrics() async throws {
        // mobilecdn.kugou.com's certificate no longer names it, so the
        // search goes to mobileservice.kugou.com, which serves the same API.
        XCTAssertEqual(URL(string: AdditionalLyricsProviders.kugouSearch)?.host, "mobileservice.kugou.com")
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [Kugou.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        // The store outlives a test run, as when the test is repeated.
        Kugou.store.reset()
        defer { Kugou.store.reset() }
        Kugou.store.set("mobileservice.kugou.com/api/v3/search/song", .init(status: 200, body:
            #"{"status":1,"errcode":0,"data":{"total":1,"info":[{"filename":"Our Artist - Our Song","songname":"Our Song","singername":"Our Artist","duration":180,"hash":"b3a52a7a958bf0aed0ebfba2e9a818b7"}]}}"#))
        Kugou.store.set("lyrics.kugou.com/search", .init(status: 200, body:
            #"{"status":200,"errcode":200,"candidates":[{"id":"274944371","accesskey":"2A7B35884B3C20E9D3281686BA59A3F8","duration":180000,"song":"Our Song","singer":"Our Artist"}]}"#))
        let lrc = Data("[ti:Our Song]\n[00:01.00]Sing along\n[00:04.00]Next line".utf8).base64EncodedString()
        Kugou.store.set("lyrics.kugou.com/download", .init(status: 200, body: #"{"status":200,"fmt":"lrc","content":"\#(lrc)"}"#))
        let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)
        let found = try await AdditionalLyricsProviders.kugou(query, session: session)
        XCTAssertEqual(found?.source, .kugou)
        XCTAssertEqual(found?.title, "Our Song")
        XCTAssertEqual(found?.duration, 180)
        XCTAssertEqual(found?.lineCount, 2)
        let requests = Kugou.store.requests
        XCTAssertEqual(requests.map { ($0.host ?? "") + $0.path },
                       ["mobileservice.kugou.com/api/v3/search/song", "lyrics.kugou.com/search", "lyrics.kugou.com/download"])
        guard requests.count == 3 else { return }
        func value(_ name: String, _ index: Int) -> String? {
            URLComponents(url: requests[index], resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == name }?.value
        }
        XCTAssertEqual(value("keyword", 0), "Our Song Our Artist")
        // The lyric search asks for the song the search found, by its hash and length.
        XCTAssertEqual(value("hash", 1), "b3a52a7a958bf0aed0ebfba2e9a818b7")
        XCTAssertEqual(value("duration", 1), "180000")
        XCTAssertEqual(value("accesskey", 2), "2A7B35884B3C20E9D3281686BA59A3F8")
    }

    func testNoUsableMirrorIsAnErrorRatherThanAMiss() async {
        let names = hosts(2), session = session()
        defer { session.invalidateAndCancel() }
        do {
            _ = try await KPoeLyricsProvider.lookup(title: "Our Song", artist: "Our Artist", album: nil, duration: 180,
                                                    hosts: names.map { "https://" + $0 }, session: session)
            XCTFail("Expected an error")
        } catch {
            XCTAssertTrue(error.localizedDescription.hasPrefix("Lyrics+ mirrors unavailable"))
        }
    }
}
