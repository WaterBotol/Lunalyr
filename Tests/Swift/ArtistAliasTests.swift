import XCTest
@testable import VersoCustom

/// Catalogue sources search again under an artist's native name, from a
/// stubbed MusicBrainz (no network), remembered on disk.
final class ArtistAliasTests: XCTestCase {
    private final class Stub: URLProtocol {
        nonisolated(unsafe) static var body = Data()
        nonisolated(unsafe) static var requests = 0
        nonisolated(unsafe) static var lastAgent: String?
        override class func canInit(with request: URLRequest) -> Bool { true }
        override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
        override func startLoading() {
            Stub.requests += 1
            Stub.lastAgent = request.value(forHTTPHeaderField: "User-Agent")
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!,
                                cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Stub.body)
            client?.urlProtocolDidFinishLoading(self)
        }
        override func stopLoading() {}
    }
    private var folder: URL!
    private var session: URLSession!

    override func setUp() {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [Stub.self]
        session = URLSession(configuration: config)
        Stub.requests = 0
        Stub.body = Self.yonezu
    }
    override func tearDown() { try? FileManager.default.removeItem(at: folder) }

    static let yonezu = Data("""
    {"artists":[{"name":"米津玄師","score":100,"aliases":[
      {"name":"Kenshi Yonezu","locale":"en","primary":true},
      {"name":"よねづけんし","locale":null},
      {"name":"米津玄师","locale":"zh_Hans","primary":true},
      {"name":"ハチ","locale":"ja"}]},
     {"name":"Someone Else","score":40}]}
    """.utf8)

    private func reply(_ data: Data) throws -> ArtistAliases.Reply { try JSONDecoder().decode(ArtistAliases.Reply.self, from: data) }

    func testPicksNativeNamesOfTheOneArtistNamed() throws {
        XCTAssertEqual(ArtistAliases.pick(try reply(Self.yonezu), for: "Kenshi Yonezu"), ["米津玄师", "米津玄師"])
        // Two artists strongly matching the name: nothing is guessed.
        let two = Data(#"{"artists":[{"name":"Aurora","score":100,"aliases":[{"name":"オーロラ","locale":"ja"}]},{"name":"Aurora","score":100}]}"#.utf8)
        XCTAssertEqual(ArtistAliases.pick(try reply(two), for: "Aurora"), [])
        // A weak match is not this artist.
        let weak = Data(#"{"artists":[{"name":"米津玄師","score":60,"aliases":[{"name":"Kenshi Yonezu"}]}]}"#.utf8)
        XCTAssertEqual(ArtistAliases.pick(try reply(weak), for: "Kenshi Yonezu"), [])
        XCTAssertTrue(ArtistAliases.isOtherScript("아이유"))
        XCTAssertFalse(ArtistAliases.isOtherScript("Björk"))
    }

    func testAnswersAreRememberedOnDiskAndAskedOnce() async {
        let aliases = ArtistAliases(folder: folder, session: session)
        let first = await aliases.names(for: "Kenshi Yonezu")
        XCTAssertEqual(first.first, "米津玄师")
        _ = await aliases.names(for: "kenshi  YONEZU")
        XCTAssertEqual(Stub.requests, 1)
        XCTAssertEqual(Stub.lastAgent, "Lunalyr/1.0 (desktop lyrics app)")
        let reopened = ArtistAliases(folder: folder, session: session)
        _ = await reopened.names(for: "Kenshi Yonezu")
        XCTAssertEqual(Stub.requests, 1, "read from disk")
    }

    func testCatalogueSearchesAgainUnderTheNativeNameAndKeepsTheLibraryArtist() async throws {
        let aliases = ArtistAliases(folder: folder, session: session)
        let query = LyricQuery(title: "Lemon", artist: "Kenshi Yonezu", album: nil, duration: 255)
        nonisolated(unsafe) var asked: [String] = []
        let found = try await LyricsFetcher.withAliases(.netEase, query: query, aliases: aliases, enabled: true) { q in
            asked.append(q.artist)
            guard q.artist == "米津玄師" else { return nil }
            return LyricCandidate(source: .netEase, result: .plain(["Lemon"], .init(precision: "", source: "")),
                                  title: "Lemon", artist: "米津玄師", duration: 255, score: 0)
        }
        XCTAssertEqual(asked, ["Kenshi Yonezu", "米津玄师", "米津玄師"])
        XCTAssertEqual(found?.artist, "Kenshi Yonezu")
        XCTAssertNil(LyricsFetcher.rejection(found!, query: query))
    }

    func testNoSecondSearchWhenNotNeeded() async throws {
        let aliases = ArtistAliases(folder: folder, session: session)
        let query = LyricQuery(title: "Lemon", artist: "Kenshi Yonezu", album: nil, duration: 255)
        let hit = LyricCandidate(source: .netEase, result: .plain(["Lemon"], .init(precision: "", source: "")),
                                 title: "Lemon", artist: "Kenshi Yonezu", duration: 255, score: 0)
        nonisolated(unsafe) var calls = 0
        // Found under the library's name; a source that is not a catalogue; the setting off.
        _ = try await LyricsFetcher.withAliases(.netEase, query: query, aliases: aliases, enabled: true) { _ in calls += 1; return hit }
        _ = try await LyricsFetcher.withAliases(.lrclib, query: query, aliases: aliases, enabled: true) { _ in calls += 1; return nil }
        _ = try await LyricsFetcher.withAliases(.qqMusic, query: query, aliases: aliases, enabled: false) { _ in calls += 1; return nil }
        XCTAssertEqual(calls, 3)
        XCTAssertEqual(Stub.requests, 0)
    }

    func testKnowsWhereTheArtistIsFrom() async throws {
        let yoasobi = Data(#"{"artists":[{"name":"YOASOBI","score":100,"country":"JP","area":{"name":"Japan"}},{"name":"ikura","score":51,"country":"JP"}]}"#.utf8)
        XCTAssertEqual(ArtistAliases.country(try reply(yoasobi), for: "YOASOBI"), "JP")
        // A city for an area, and no country: not known.
        let wallows = Data(#"{"artists":[{"name":"Wallows","score":100,"area":{"name":"Los Angeles"}}]}"#.utf8)
        XCTAssertNil(ArtistAliases.country(try reply(wallows), for: "Wallows"))
        let area = Data(#"{"artists":[{"name":"Our Band","score":100,"area":{"name":"South Korea"}}]}"#.utf8)
        XCTAssertEqual(ArtistAliases.country(try reply(area), for: "Our Band"), "KR")
        // Two artists of the name: nothing is guessed.
        let two = Data(#"{"artists":[{"name":"Aurora","score":100,"country":"NO"},{"name":"Aurora","score":100,"country":"JP"}]}"#.utf8)
        XCTAssertNil(ArtistAliases.country(try reply(two), for: "Aurora"))

        Stub.body = yoasobi
        let aliases = ArtistAliases(folder: folder, session: session)
        let eastAsian = await aliases.isEastAsian("YOASOBI")
        XCTAssertTrue(eastAsian)
        _ = await aliases.names(for: "YOASOBI")
        XCTAssertEqual(Stub.requests, 1, "one request answers both")
        Stub.body = wallows
        let western = await aliases.isEastAsian("Wallows")
        XCTAssertFalse(western)
    }

    func testAnEntryStoredBeforeCountriesIsAskedAgainOnlyForTheCountry() async throws {
        // As stored before the country was kept.
        let stored = #"{"\#(LyricQuery.normalize("Kenshi Yonezu"))":{"names":["米津玄师"],"storedAt":\#(Date().timeIntervalSinceReferenceDate)}}"#
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data(stored.utf8).write(to: folder.appendingPathComponent("ArtistAliases.json"))
        Stub.body = Data(#"{"artists":[{"name":"米津玄師","score":100,"country":"JP","aliases":[{"name":"Kenshi Yonezu","locale":"en"}]}]}"#.utf8)
        let aliases = ArtistAliases(folder: folder, session: session)
        let names = await aliases.names(for: "Kenshi Yonezu")
        XCTAssertEqual(names, ["米津玄师"])
        XCTAssertEqual(Stub.requests, 0)
        let eastAsian = await aliases.isEastAsian("Kenshi Yonezu")
        XCTAssertTrue(eastAsian)
        XCTAssertEqual(Stub.requests, 1)
        _ = await aliases.isEastAsian("Kenshi Yonezu")
        XCTAssertEqual(Stub.requests, 1)
    }
}
