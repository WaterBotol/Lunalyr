import AppKit
import XCTest
@testable import VersoCustom

enum FakeImages {
    /// A valid 2x2 PNG.
    static let png: Data = {
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 2, pixelsHigh: 2, bitsPerSample: 8,
                                   samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                   colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        return rep.representation(using: .png, properties: [:])!
    }()
}

/// Serves the catalogue and image requests these tests make. Nothing leaves the process.
private final class ArtworkStubProtocol: URLProtocol {
    private static let lock = NSLock()
    private static var routes: [String: (status: Int, body: Data)] = [:]
    private static var seen: [URLRequest] = []

    static func reset(_ newRoutes: [String: (Int, Data)]) {
        lock.lock(); defer { lock.unlock() }
        routes = newRoutes.mapValues { (status: $0.0, body: $0.1) }
        seen = []
    }

    static var requests: [URLRequest] {
        lock.lock(); defer { lock.unlock() }
        return seen
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let url = request.url!
        let key = (url.host ?? "") + url.path
        Self.lock.lock()
        Self.seen.append(request)
        let route = Self.routes[key] ?? (status: 404, body: Data())
        Self.lock.unlock()
        let response = HTTPURLResponse(url: url, statusCode: route.status, httpVersion: "HTTP/1.1", headerFields: [:])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: route.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

/// Holds each expected request until all of them have arrived, or a second
/// has passed, and says which it was: requests made one after another can
/// only meet by timing out.
private actor Rendezvous {
    private let expected: Set<String>
    private var arrived: Set<String> = []
    private var waiting: [CheckedContinuation<Void, Never>] = []
    private var timedOut = false
    private(set) var metTogether = false

    init(_ expected: Set<String>) {
        self.expected = expected
    }

    nonisolated func expects(_ path: String) -> Bool { expected.contains(path) }

    func arrive(_ path: String) async {
        arrived.insert(path)
        if arrived.isSuperset(of: expected) {
            if !timedOut { metTogether = true }
            release()
            return
        }
        guard !timedOut else { return }
        await withCheckedContinuation { continuation in
            waiting.append(continuation)
            if waiting.count == 1 {
                // On the actor, as the Task inherits it.
                Task {
                    try? await Task.sleep(nanoseconds: 1_000_000_000)
                    self.timeOut()
                }
            }
        }
    }

    private func timeOut() {
        timedOut = true
        release()
    }

    private func release() {
        let resumed = waiting
        waiting = []
        resumed.forEach { $0.resume() }
    }
}

final class ArtworkLookupTests: XCTestCase {
    private func stubbedSession() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [ArtworkStubProtocol.self]
        return URLSession(configuration: config)
    }

    private func result(_ title: String, _ artist: String, album: String? = nil, ms: Int? = nil,
                        art: String = "https://is1-ssl.mzstatic.com/x/100x100bb.jpg") -> ArtworkProvider.Result {
        .init(artworkUrl100: art, trackName: title, artistName: artist, collectionName: album, trackTimeMillis: ms)
    }

    func testACatalogueCoverMustBeTheSameArtists() {
        let others = [result("Yesterday", "Someone Covering", album: "Help!", ms: 125_000), result("Yesterday", "Another Band")]
        XCTAssertNil(ArtworkProvider.bestMatch(in: others, title: "Yesterday", artist: "The Beatles", album: "Help!", duration: 125),
                     "A same-titled song by someone else is never this track's cover")
        let right = result("Yesterday", "The Beatles", album: "Help!", ms: 125_000)
        XCTAssertEqual(ArtworkProvider.bestMatch(in: others + [right], title: "Yesterday", artist: "The Beatles",
                                                 album: "Help!", duration: 125)?.artistName, "The Beatles")
    }

    func testCollaboratorCreditsMatchButNameFragmentsDoNot() {
        XCTAssertTrue(ArtworkProvider.artistMatches("Daft Punk", wanted: "Daft Punk & Pharrell Williams"))
        XCTAssertTrue(ArtworkProvider.artistMatches("Daft Punk, Pharrell Williams & Nile Rodgers", wanted: "Daft Punk"))
        XCTAssertTrue(ArtworkProvider.artistMatches("Calvin Harris", wanted: "Calvin Harris feat. Rihanna"))
        XCTAssertTrue(ArtworkProvider.artistMatches("Beyonce", wanted: "Beyoncé"))
        XCTAssertTrue(ArtworkProvider.artistMatches("宇多田ヒカル", wanted: "宇多田ヒカル"))
        XCTAssertFalse(ArtworkProvider.artistMatches("Max", wanted: "Ava Max"))
        XCTAssertFalse(ArtworkProvider.artistMatches("Someone Covering", wanted: "The Beatles"))
        XCTAssertFalse(ArtworkProvider.artistMatches(nil, wanted: "The Beatles"))
        XCTAssertFalse(ArtworkProvider.artistMatches("The Beatles", wanted: ""))
    }

    func testTheCatalogueSearchPicksTheArtistsCoverAndSkipsUnknownArtists() async {
        let search = #"{"results":[{"trackName":"Yesterday","artistName":"Someone Covering","artworkUrl100":"https://is1-ssl.mzstatic.com/wrong/100x100bb.jpg"},{"trackName":"Yesterday","artistName":"The Beatles","collectionName":"Help!","trackTimeMillis":125000,"artworkUrl100":"https://is1-ssl.mzstatic.com/right/100x100bb.jpg"}]}"#
        ArtworkStubProtocol.reset([
            "itunes.apple.com/search": (200, Data(search.utf8)),
            "is1-ssl.mzstatic.com/right/600x600bb.jpg": (200, FakeImages.png),
        ])
        let session = stubbedSession()
        defer { session.invalidateAndCancel() }
        let provider = ArtworkProvider(session: session)
        let image = await provider.artworkData(title: "Yesterday", artist: "The Beatles", album: "Help!", duration: 125)
        XCTAssertEqual(image, FakeImages.png)
        XCTAssertFalse(ArtworkStubProtocol.requests.contains { $0.url?.path.contains("wrong") == true })
        XCTAssertTrue(ArtworkStubProtocol.requests.allSatisfy { $0.timeoutInterval == ArtworkProvider.timeout })

        ArtworkStubProtocol.reset([:])
        let unknown = await provider.artworkData(title: "Untitled", artist: "", album: nil, duration: 100)
        XCTAssertNil(unknown)
        XCTAssertTrue(ArtworkStubProtocol.requests.isEmpty, "Without an artist a title search is a guess")
    }

    // MARK: Animated covers

    private static let coverHost = "https://mvod.itunes.apple.com"

    /// A byte-range playlist over one complete MP4, as Apple publishes most covers.
    private static func progressivePlaylist(_ file: String) -> Data {
        Data("""
        #EXTM3U
        #EXT-X-MAP:URI="\(file)",BYTERANGE="895@0"
        #EXTINF:3,
        #EXT-X-BYTERANGE:1000@895
        \(file)
        #EXT-X-ENDLIST
        """.utf8)
    }

    /// Routes for one album whose page carries the given renditions.
    private static func albumRoutes(square: String?, tall: String?) -> [String: (Int, Data)] {
        let search = #"{"results":[{"collectionViewUrl":"https://music.apple.com/us/album/dawn/123","collectionName":"Dawn","artistName":"Artist"}]}"#
        let renditions = [("motionDetailSquare", square), ("motionDetailTall", tall)]
            .compactMap { name, path in path.map { "\"\(name)\":{\"video\":\"\(coverHost)\($0)\"}" } }
            .joined(separator: ",")
        let page = """
        <script id="serialized-server-data" type="application/json">
        {"data":[{"data":{"sections":[{"items":[{"videoArtwork":{"dictionary":{\(renditions)}}}]}]}}]}
        </script>
        """
        return ["itunes.apple.com/search": (200, Data(search.utf8)),
                "music.apple.com/us/album/dawn/123": (200, Data(page.utf8))]
    }

    private func motion(_ routes: [String: (Int, Data)]) async throws -> MotionArtworkProvider.Motion {
        ArtworkStubProtocol.reset(routes)
        let session = stubbedSession()
        defer { session.invalidateAndCancel() }
        let found = await MotionArtworkProvider(session: session).motion(album: "Dawn", artist: "Artist", title: "First Light")
        return try XCTUnwrap(found)
    }

    private func playlistRequests(_ path: String) -> Int {
        ArtworkStubProtocol.requests.filter { $0.url?.path == path }.count
    }

    /// Portrait displays play the tall cover only as one progressive file,
    /// so it is resolved the way the square one is.
    func testTheTallAnimatedCoverIsResolvedForPortraitDisplays() async throws {
        var routes = Self.albumRoutes(square: "/a/square/master.m3u8", tall: "/a/tall/master.m3u8")
        routes["mvod.itunes.apple.com/a/square/master.m3u8"] = (200, Self.progressivePlaylist("square.mp4"))
        routes["mvod.itunes.apple.com/a/tall/master.m3u8"] = (200, Self.progressivePlaylist("tall.mp4"))
        let motion = try await motion(routes)
        XCTAssertEqual(motion.square.absoluteString, Self.coverHost + "/a/square/square.mp4")
        XCTAssertEqual(motion.tall?.absoluteString, Self.coverHost + "/a/tall/tall.mp4")
        XCTAssertEqual(motion.rendition(portrait: true), motion.tall, "The tall cover reaches portrait displays")
        XCTAssertEqual(motion.rendition(portrait: false), motion.square)
    }

    func testATallCoverThatIsNoSingleFileLeavesPortraitDisplaysTheSquareOne() async throws {
        var routes = Self.albumRoutes(square: "/b/square/master.m3u8", tall: "/b/tall/master.m3u8")
        routes["mvod.itunes.apple.com/b/square/master.m3u8"] = (200, Self.progressivePlaylist("square.mp4"))
        routes["mvod.itunes.apple.com/b/tall/master.m3u8"] = (200, Data("""
        #EXTM3U
        #EXTINF:3,
        part1.ts
        #EXTINF:3,
        part2.ts
        #EXT-X-ENDLIST
        """.utf8))
        let motion = try await motion(routes)
        XCTAssertEqual(motion.tall?.absoluteString, Self.coverHost + "/b/tall/master.m3u8", "Kept as Apple published it")
        XCTAssertEqual(motion.rendition(portrait: true), motion.square)
    }

    /// On a Mac without a portrait display the square cover must not wait
    /// for the tall one's playlists: both renditions are resolved at once.
    func testBothRenditionsAreResolvedAtOnce() async throws {
        var routes = Self.albumRoutes(square: "/d/square/master.m3u8", tall: "/d/tall/master.m3u8")
        routes["mvod.itunes.apple.com/d/square/master.m3u8"] = (200, Self.progressivePlaylist("square.mp4"))
        routes["mvod.itunes.apple.com/d/tall/master.m3u8"] = (200, Self.progressivePlaylist("tall.mp4"))
        let served = routes
        let playlists = Rendezvous(["/d/square/master.m3u8", "/d/tall/master.m3u8"])
        let provider = MotionArtworkProvider(fetch: { request in
            guard let url = request.url else { return nil }
            // Each master playlist is held until the other is asked for too.
            if playlists.expects(url.path) { await playlists.arrive(url.path) }
            let route = served[(url.host ?? "") + url.path] ?? (404, Data())
            guard let response = HTTPURLResponse(url: url, statusCode: route.0, httpVersion: "HTTP/1.1",
                                                 headerFields: [:]) else { return nil }
            return (route.1, response)
        })
        let motion = await provider.motion(album: "Dawn", artist: "Artist", title: "First Light")
        XCTAssertEqual(motion?.square.absoluteString, Self.coverHost + "/d/square/square.mp4")
        XCTAssertEqual(motion?.tall?.absoluteString, Self.coverHost + "/d/tall/tall.mp4")
        let together = await playlists.metTogether
        XCTAssertTrue(together, "The square cover waited for the tall one's playlist")
    }

    func testAnAlbumWithOnlyATallCoverResolvesItOnce() async throws {
        var routes = Self.albumRoutes(square: nil, tall: "/c/tall/master.m3u8")
        routes["mvod.itunes.apple.com/c/tall/master.m3u8"] = (200, Self.progressivePlaylist("tall.mp4"))
        let motion = try await motion(routes)
        XCTAssertEqual(motion.square.absoluteString, Self.coverHost + "/c/tall/tall.mp4")
        XCTAssertEqual(motion.tall, motion.square)
        XCTAssertEqual(playlistRequests("/c/tall/master.m3u8"), 1, "One rendition, one fetch")
    }

    func testPublishedCoversAreBoundedHTTPSImagesOnly() async {
        XCTAssertGreaterThanOrEqual(ArtworkProvider.timeout, 6)
        XCTAssertLessThanOrEqual(ArtworkProvider.timeout, 8)
        ArtworkStubProtocol.reset([
            "i.scdn.co/image/cover": (200, FakeImages.png),
            "i.scdn.co/image/page": (200, Data("<html>not a cover</html>".utf8)),
        ])
        let session = stubbedSession()
        defer { session.invalidateAndCancel() }
        let provider = ArtworkProvider(session: session)
        let cover = await provider.imageData(at: URL(string: "https://i.scdn.co/image/cover")!)
        XCTAssertEqual(cover, FakeImages.png)
        XCTAssertEqual(ArtworkStubProtocol.requests.first?.timeoutInterval, ArtworkProvider.timeout)
        let page = await provider.imageData(at: URL(string: "https://i.scdn.co/image/page")!)
        XCTAssertNil(page)
        let insecure = await provider.imageData(at: URL(string: "http://i.scdn.co/image/cover")!)
        XCTAssertNil(insecure)
        XCTAssertEqual(ArtworkStubProtocol.requests.count, 2, "Plain HTTP is never fetched")
    }
}

/// Answers every motion-artwork request with one status and body.
private final class MotionStubProtocol: URLProtocol {
    private static let lock = NSLock()
    private static var reply: (status: Int, body: Data) = (404, Data())
    private static var count = 0

    static func reset(status: Int, body: String) {
        lock.lock(); defer { lock.unlock() }
        reply = (status, Data(body.utf8))
        count = 0
    }

    static var requests: Int {
        lock.lock(); defer { lock.unlock() }
        return count
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        Self.lock.lock()
        Self.count += 1
        let reply = Self.reply
        Self.lock.unlock()
        let response = HTTPURLResponse(url: request.url!, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: [:])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: reply.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

final class MotionArtworkCacheTests: XCTestCase {
    private func stubbedProvider() -> (MotionArtworkProvider, URLSession) {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MotionStubProtocol.self]
        let session = URLSession(configuration: config)
        return (MotionArtworkProvider(session: session), session)
    }

    func testAnAnsweredMissIsRememberedForTheSession() async {
        MotionStubProtocol.reset(status: 200, body: #"{"results":[]}"#)
        let (provider, session) = stubbedProvider()
        defer { session.invalidateAndCancel() }
        let first = await provider.motion(album: "Quiet Album", artist: "Quiet Artist", title: "One")
        XCTAssertNil(first)
        let asked = MotionStubProtocol.requests
        XCTAssertGreaterThan(asked, 0)
        let second = await provider.motion(album: "Quiet Album", artist: "Quiet Artist", title: "Two")
        XCTAssertNil(second)
        XCTAssertEqual(MotionStubProtocol.requests, asked, "The next song of the album is not searched again")
    }

    func testFailingToReachAppleIsNotCachedAsAMiss() async {
        MotionStubProtocol.reset(status: 503, body: "")
        let (provider, session) = stubbedProvider()
        defer { session.invalidateAndCancel() }
        let first = await provider.motion(album: "Busy Album", artist: "Busy Artist", title: "One")
        XCTAssertNil(first)
        let asked = MotionStubProtocol.requests
        _ = await provider.motion(album: "Busy Album", artist: "Busy Artist", title: "One")
        XCTAssertGreaterThan(MotionStubProtocol.requests, asked)
    }

    func testTheAlbumKeyIgnoresAnEmptyAlbum() {
        XCTAssertEqual(MotionArtworkProvider.cacheKey(album: "", artist: "A", title: "T"),
                       MotionArtworkProvider.cacheKey(album: nil, artist: "A", title: "T"))
        XCTAssertNotEqual(MotionArtworkProvider.cacheKey(album: "", artist: "A", title: "T"),
                          MotionArtworkProvider.cacheKey(album: "", artist: "A", title: "U"),
                          "Album-less singles do not share one cover")
        XCTAssertEqual(MotionArtworkProvider.cacheKey(album: "LP", artist: "A", title: "T"),
                       MotionArtworkProvider.cacheKey(album: "LP", artist: "A", title: "U"))
    }
}
