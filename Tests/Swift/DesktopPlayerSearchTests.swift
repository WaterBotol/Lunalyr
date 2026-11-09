import XCTest
@testable import VersoCustom

final class DesktopPlayerSearchTests: XCTestCase {
    func testCatalogueSongsAndAlbumsBecomeMusicAddresses() throws {
        let json = """
        {"results":[
         {"wrapperType":"track","trackId":11,"trackName":"Drip Too Hard","artistName":"Lil Baby","collectionName":"Harder Than Ever",
          "trackViewUrl":"https://music.apple.com/us/album/drip-too-hard/1?i=11","artworkUrl100":"https://x/100x100bb.jpg"},
         {"wrapperType":"collection","collectionId":22,"collectionName":"Drip Harder","artistName":"Lil Baby",
          "collectionViewUrl":"https://music.apple.com/us/album/drip-harder/22","artworkUrl100":"https://x/100x100bb.jpg"},
         {"wrapperType":"artist","artistName":"Lil Baby"}]}
        """
        let found = AppleMusicSearch.parseCatalogue(Data(json.utf8))
        XCTAssertEqual(found.count, 2)
        XCTAssertEqual(found[0].kind, .song)
        XCTAssertEqual(found[0].target, .address(try XCTUnwrap(URL(string: "music://music.apple.com/us/album/drip-too-hard/1?i=11"))))
        XCTAssertEqual(found[1].kind, .album)
        XCTAssertEqual(found[0].artworkURL?.absoluteString, "https://x/200x200bb.jpg")
    }

    func testOnlyAppleAddressesAreOpened() {
        XCTAssertNil(AppleMusicSearch.musicAddress("https://evil.example/a"))
        XCTAssertEqual(AppleMusicSearch.musicAddress("https://music.apple.com/us/album/a/1")?.scheme, "music")
    }

    func testSearchURLCarriesOnlyTheTypedWords() throws {
        let url = try XCTUnwrap(AppleMusicSearch.searchURL("drip & too hard", entity: "song", limit: 8))
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems
        XCTAssertEqual(items?.first { $0.name == "term" }?.value, "drip & too hard")
        XCTAssertEqual(url.host, "itunes.apple.com")
    }

    func testArtistAlbumsKeepOnlyThatArtistWithoutRepeats() {
        let json = """
        {"results":[
         {"collectionId":1,"collectionName":"Harder Than Ever","artistName":"Lil Baby","releaseDate":"2018-10-05T07:00:00Z",
          "artworkUrl100":"https://x/100x100bb.jpg","collectionViewUrl":"https://music.apple.com/us/album/a/1"},
         {"collectionId":2,"collectionName":"Harder Than Ever","artistName":"Lil Baby","releaseDate":"2018-10-05T07:00:00Z"},
         {"collectionId":3,"collectionName":"Other","artistName":"Someone Else","releaseDate":"2019-01-01T00:00:00Z"}]}
        """
        let albums = AppleMusicSearch.parseAlbums(Data(json.utf8), artist: "Lil Baby")
        XCTAssertEqual(albums.map(\.title), ["Harder Than Ever"])
        XCTAssertEqual(albums.first?.year, "2018")
    }

    func testLibraryAndQueueRepliesParse() {
        func row(_ values: [NSAppleEventDescriptor]) -> NSAppleEventDescriptor { .scriptList(values) }
        let text = { (s: String) in NSAppleEventDescriptor(string: s) }
        let library = NSAppleEventDescriptor.scriptList([text("ok"), .scriptList([
            row([text("AB12"), text("Song"), text("Artist"), text("Album")])])])
        let results = AppleMusicSearch.parseLibrary(library)
        XCTAssertEqual(results.first?.target, .libraryTrack(persistentID: "AB12"))
        XCTAssertTrue(results.first?.inLibrary == true)

        let queue = NSAppleEventDescriptor.scriptList([text("ok"), .scriptList([
            row([text("CD34"), text("Next"), text("Artist"), NSAppleEventDescriptor(int32: 5)])])])
        let items = AppleMusicSearch.parseQueue(queue)
        XCTAssertEqual(items.first?.index, 5)
        XCTAssertEqual(items.first?.title, "Next")
    }

    func testScriptTextEscapesQuotesAndBackslashes() {
        XCTAssertEqual(ScriptText.quoted("a\"b\\c\nd"), "\"a\\\"b\\\\c d\"")
    }

    func testSpotifySearchAddress() {
        XCTAssertEqual(SpotifySearch.searchAddress("lil baby")?.absoluteString, "spotify:search:lil%20baby")
    }
}

final class DesktopPlayerQueueScriptTests: XCTestCase {
    func testPlayInContextBuildsOnlyLunalyrsOwnPlaylist() {
        let script = AppleMusicSearch.playInContext(persistentID: "AB12")
        XCTAssertTrue(script.contains("\"AB12\""))
        // The only playlist it may delete is its own, by exact name.
        XCTAssertTrue(script.contains("delete (every user playlist whose name is \"Lunalyr Queue\")"))
        XCTAssertFalse(script.contains("delete track"))
        XCTAssertFalse(script.contains("delete (every track"))
    }
}

final class ScriptDeskTests: XCTestCase {
    /// Stands in for the monitor's script queue: records what was sent and
    /// answers only when told to.
    private final class FakeLane: @unchecked Sendable {
        private let lock = NSLock()
        private var sent: [String] = []
        private var pending: [@Sendable (Result<NSAppleEventDescriptor, ScriptFailure>) -> Void] = []

        var lane: ScriptLane {
            ScriptLane { script, _, _, done in
                self.lock.withLock {
                    self.sent.append(script.name)
                    self.pending.append(done)
                }
            }
        }

        var sentNames: [String] { lock.withLock { sent } }

        func answerNext() {
            let done = lock.withLock { pending.removeFirst() }
            done(.success(NSAppleEventDescriptor(string: "ok")))
        }
    }

    private func until(_ condition: () -> Bool) async throws {
        for _ in 0..<1000 {
            if condition() { return }
            try await Task.sleep(nanoseconds: 2_000_000)
        }
        XCTFail("timed out")
    }

    func testPlaysGoAheadOfCoversAndCancelledCoversAreNeverSent() async throws {
        let fake = FakeLane()
        let desk = ScriptDesk { fake.lane }
        func job(_ name: String, _ priority: ScriptDesk.Priority) -> Task<String, Error> {
            Task {
                _ = try await desk.run(PlayerScript(name: name, source: ""), for: .appleMusic, priority: priority)
                return name
            }
        }
        let first = job("queue", .read)
        try await until { fake.sentNames == ["queue"] }
        let cover = job("cover", .artwork)
        try await until { desk.waitingCount == 1 }
        let scrolledAway = job("scrolled-away", .artwork)
        try await until { desk.waitingCount == 2 }
        let play = job("play", .command)
        try await until { desk.waitingCount == 3 }
        scrolledAway.cancel()
        try await until { desk.waitingCount == 2 }

        for count in 2...3 {
            fake.answerNext()
            try await until { fake.sentNames.count == count }
        }
        fake.answerNext()
        XCTAssertEqual(fake.sentNames, ["queue", "play", "cover"], "one at a time, the play first, the dropped cover never")
        let names = try await [first.value, play.value, cover.value]
        XCTAssertEqual(names, ["queue", "play", "cover"])
        do {
            _ = try await scrolledAway.value
            XCTFail("a cancelled cover is dropped")
        } catch is CancellationError {}
    }

    func testANotRunningReplyAndARefusalBecomeSearchErrors() {
        let notRunning = ScriptDesk.reply(.success(.scriptList([NSAppleEventDescriptor(string: "notRunning")])), for: .appleMusic)
        XCTAssertThrowsError(try notRunning.get()) { XCTAssertEqual($0 as? PlayerSearchError, .notRunning(.appleMusic)) }
        let refused = ScriptDesk.reply(.failure(ScriptFailure(code: AppleEventCode.notPermitted)), for: .spotify)
        XCTAssertThrowsError(try refused.get()) { XCTAssertEqual($0 as? PlayerSearchError, .blocked(.spotify)) }
    }

    func testCoversAreReadInOneScript() {
        let script = LibraryArtwork.script(for: ["AB12", "CD\"34"])
        XCTAssertTrue(script.source.contains("{\"AB12\", \"CD\\\"34\"}"))
        XCTAssertEqual(script.source.components(separatedBy: "tell application").count, 2, "one script, one tell block")
        XCTAssertFalse(script.cacheable)

        let text = { (s: String) in NSAppleEventDescriptor(string: s) }
        let image = NSAppleEventDescriptor(descriptorType: typeData, data: Data(repeating: 7, count: 200))!
        let reply = NSAppleEventDescriptor.scriptList([text("ok"), .scriptList([.scriptList([text("AB12"), image])])])
        XCTAssertEqual(LibraryArtwork.parse(reply)["AB12"]?.count, 200)
        XCTAssertNil(LibraryArtwork.parse(reply)["CD34"])
    }
}
