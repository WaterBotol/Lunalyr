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
        XCTAssertNotNil(AppleMusicSearch.musicAddress("https://apple.com/a"))
        XCTAssertNil(AppleMusicSearch.musicAddress("https://notapple.com/us/album/a/1"), "a host merely ending in apple.com")
        XCTAssertNil(AppleMusicSearch.musicAddress("https://music.apple.com.evil.example/a"))
        XCTAssertNil(AppleMusicSearch.musicAddress("not a web address"))
    }

    func testSearchURLCarriesOnlyTheTypedWords() throws {
        let url = try XCTUnwrap(AppleMusicSearch.searchURL("drip & too hard", entity: "song", limit: 8))
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems
        XCTAssertEqual(items?.first { $0.name == "term" }?.value, "drip & too hard")
        XCTAssertEqual(url.host, "itunes.apple.com")
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
    private let deleteByName = "every user playlist whose name"

    func testPreparingDeletesOnlyLeftoversLunalyrMadeAndChecksEachOne() {
        let script = AppleMusicSearch.prepareQueue(persistentID: "AB12", index: 7, leftovers: ["0A1B", "2C3D"])
        XCTAssertFalse(script.contains(deleteByName), "never selects playlists by name")
        for id in ["0A1B", "2C3D"] {
            XCTAssertTrue(script.contains("first user playlist whose persistent ID is \"\(id)\""))
        }
        XCTAssertEqual(script.components(separatedBy: "delete ownList").count, 3, "one guarded delete per leftover")
        XCTAssertTrue(script.contains("if class of ownList is user playlist then"), "a folder playlist is not deleted")
        XCTAssertTrue(script.contains("if smart of ownList is false then"), "a smart playlist is not deleted")
        XCTAssertTrue(script.contains("considering case\nif ownName is \"Lunalyr Queue (new)\" then delete ownList"))
        XCTAssertTrue(script.contains("make new user playlist with properties {name:\"Lunalyr Queue (new)\"}"))
        XCTAssertTrue(script.contains("set candidate to track 7 of src"), "the clicked copy of a song that is listed twice")
        XCTAssertFalse(script.contains("delete track"))
    }

    func testPreparingWithNothingLeftOverDeletesNothing() {
        let script = AppleMusicSearch.prepareQueue(persistentID: "AB12", index: nil, leftovers: [])
        XCTAssertFalse(script.contains("delete"))
        XCTAssertFalse(script.contains("set candidate"))
    }

    func testOnlyLibrarySourcesAreCopiedOthersJustPlay() throws {
        let script = AppleMusicSearch.prepareQueue(persistentID: "AB12", index: nil, leftovers: [])
        // The playing playlist's kind, from the dictionary's own classes.
        for (kind, name) in [("library playlist", "library"), ("user playlist", "playlist"),
                             ("subscription playlist", "apple-music-playlist"), ("radio tuner playlist", "radio"),
                             ("audio CD playlist", "cd"), ("folder playlist", "folder")] {
            XCTAssertTrue(script.contains("if sourceClass is \(kind) then\nset sourceKind to \"\(name)\""), kind)
        }
        // A playlist is made only for a library song found in a copyable playlist.
        let made = try XCTUnwrap(script.range(of: """
        if inSource and inLibrary and sourceKind is in {"library", "playlist", "apple-music-playlist"} then
        set newList to make new user playlist with properties {name:"Lunalyr Queue (new)"}
        return {"ok", "made", sourceKind, total, persistent ID of newList}
        end if
        """))
        XCTAssertEqual(script.components(separatedBy: "make new user playlist").count, 2)
        // Anything else in the playing playlist plays alone, and says why.
        let alone = try XCTUnwrap(script.range(of: """
        if inSource then
        play srcTrack
        if inLibrary then return {"ok", "played", "source", sourceKind, total}
        return {"ok", "played", "notInLibrary", sourceKind, total}
        end if
        """))
        XCTAssertLessThan(made.lowerBound, alone.lowerBound)
        // A library song from elsewhere reads its album, in three bulk events.
        let album = try XCTUnwrap(script.range(of: "return {\"ok\", \"album\", sourceKind, total, albumIDs, albumDiscs, albumNumbers}"))
        XCTAssertLessThan(alone.upperBound, album.lowerBound)
        XCTAssertTrue(script.contains("set albumIDs to persistent ID of (every track of library playlist 1 whose album is albumName and album artist is albumArtist)"))
        XCTAssertTrue(script.contains("whose album is albumName and artist is trackArtist"), "no album artist: the track's artist")
        XCTAssertTrue(script.contains("if albumName is \"\" then\nplay libTrack\nreturn {\"ok\", \"played\", \"single\""))
        XCTAssertTrue(script.contains("if not inLibrary then error number -1728"))
        XCTAssertFalse(script.contains("duplicate"), "the first step copies nothing")
    }

    func testLeftoversAreClearedUpWhateverTheClickTurnsInto() throws {
        let script = AppleMusicSearch.prepareQueue(persistentID: "AB12", index: nil, leftovers: ["0A1B"])
        let cleanUp = try XCTUnwrap(script.range(of: "whose persistent ID is \"0A1B\""))
        let firstPlay = try XCTUnwrap(script.range(of: "play srcTrack"))
        XCTAssertLessThan(cleanUp.lowerBound, firstPlay.lowerBound,
                          "a half-built playlist goes even when this click only plays a song")
    }

    func testBuildingCopiesLibrarySongsAndCleansUpAfterAFailure() {
        let collect = AppleMusicSearch.collectQueue(persistentID: "AB12", index: 3, into: "FFEE")
        let fill = AppleMusicSearch.fillQueue(["0A", "0B"], into: "FFEE")
        let range = AppleMusicSearch.fillRange(from: 1, to: 25, into: "FFEE")
        for script in [collect, fill, range] {
            XCTAssertFalse(script.contains(deleteByName))
            XCTAssertFalse(script.contains("duplicate (track"))
            // A failure deletes the half-built playlist, by its ID and exact name, then raises the error again.
            let failure = script.components(separatedBy: "on error buildText number buildNumber").last ?? ""
            XCTAssertTrue(failure.contains("first user playlist whose persistent ID is \"FFEE\""))
            XCTAssertTrue(failure.contains("if ownName is \"Lunalyr Queue (new)\" then delete ownList"))
            XCTAssertTrue(failure.contains("error buildText number buildNumber"), "the error is raised again, unchanged by the clean-up")
            XCTAssertFalse(script.contains("delete track"))
        }
        XCTAssertTrue(fill.contains("duplicate (first track of library playlist 1 whose persistent ID is wantedID) to newList"),
                      "only the library's own copy of a song is copied")
        // The previous queue goes only by its own ID and exact name, case included.
        let finish = AppleMusicSearch.finishQueue(into: "FFEE", replacing: "0A1B")
        XCTAssertFalse(finish.contains(deleteByName))
        XCTAssertTrue(finish.contains("first user playlist whose persistent ID is \"0A1B\""))
        XCTAssertTrue(finish.contains("considering case\nif ownName is \"Lunalyr Queue\" then delete ownList"))
        XCTAssertTrue(finish.contains("if smart of ownList is false then"))
        XCTAssertTrue(finish.contains("if class of ownList is user playlist then"))
        XCTAssertFalse(finish.contains("delete track"))
        XCTAssertFalse(finish.contains("delete (every"))
    }

    func testBuildingWithoutAnEarlierQueueDeletesOnlyOnFailure() {
        let finish = AppleMusicSearch.finishQueue(into: "FFEE", replacing: nil)
        XCTAssertFalse(finish.contains("delete"))
        let collect = AppleMusicSearch.collectQueue(persistentID: "AB12", index: nil, into: "FFEE")
        XCTAssertEqual(collect.components(separatedBy: "delete ownList").count, 2)
        XCTAssertFalse(collect.contains("\"Lunalyr Queue\" then delete"))
    }

    func testTheStoreKeepsOnlyMusicStyleIDs() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let store = QueuePlaylistStore(defaults: defaults)
        XCTAssertNil(store.queueID)
        XCTAssertEqual(store.pendingIDs, [])
        store.queueID = "0A1B2C3D4E5F6A7B"
        store.pendingIDs = ["FFEE", "not an id\" then delete", ""]
        XCTAssertEqual(QueuePlaylistStore(defaults: defaults).queueID, "0A1B2C3D4E5F6A7B")
        XCTAssertEqual(store.pendingIDs, ["FFEE"])
        store.queueID = nil
        XCTAssertNil(store.queueID)
    }

    func testSongsWithoutALibraryIDCannotBeClicked() {
        XCTAssertFalse(PlayerQueueItem(id: "h-1", title: "Radio", artist: "", index: 0).isPlayable)
        XCTAssertTrue(PlayerQueueItem(id: "h-2", title: "Song", artist: "", index: 0, persistentID: "AB12").isPlayable)
        XCTAssertTrue(PlayerQueueItem(id: "now", title: "Now", artist: "", index: 0, isCurrent: true).isPlayable)
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
