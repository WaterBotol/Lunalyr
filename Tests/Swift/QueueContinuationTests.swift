import AppKit
import XCTest
@testable import VersoCustom

// Picking a song in the desktop player carries on with what follows it:
// the playlist it is in, else its album; and when it can't, the song still
// plays and the strip says why. A fake script lane stands in for Music.

private func text(_ value: String) -> NSAppleEventDescriptor { NSAppleEventDescriptor(string: value) }
private func number(_ value: Int) -> NSAppleEventDescriptor { NSAppleEventDescriptor(int32: Int32(value)) }
private func list(_ items: [NSAppleEventDescriptor]) -> NSAppleEventDescriptor { .scriptList(items) }

/// Answers each queue step by its short name ("prepare", "fill", …).
private final class ScriptedMusic: @unchecked Sendable {
    let lock = NSLock()
    var sent: [PlayerScript] = []
    var answer: (String, PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure>
    /// Holds a step's answer back until the test lets it go.
    var hold: String?
    var held: (() -> Void)?

    init(_ answer: @escaping (String, PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure>) {
        self.answer = answer
    }

    var lane: ScriptLane {
        ScriptLane { script, _, _, done in
            let step = script.name.replacingOccurrences(of: "desktop-player.queue-", with: "")
            let reply = self.lock.withLock { () -> (() -> Void)? in
                self.sent.append(script)
                let outcome = self.answer(step, script)
                guard self.hold == step else { return { done(outcome) } }
                self.hold = nil
                self.held = { done(outcome) }
                return nil
            }
            reply?()
        }
    }

    var steps: [String] { lock.withLock { sent.map { $0.name.replacingOccurrences(of: "desktop-player.queue-", with: "") } } }
    func source(_ step: String) -> String? { lock.withLock { sent.last { $0.name == "desktop-player.queue-\(step)" }?.source } }
    func sources(_ step: String) -> [String] { lock.withLock { sent.filter { $0.name == "desktop-player.queue-\(step)" }.map(\.source) } }
}

/// IDs quoted in a fill script, in order.
private func copiedIDs(_ source: String) -> [String] {
    guard let open = source.range(of: "repeat with idItem in {"),
          let close = source.range(of: "}", range: open.upperBound..<source.endIndex) else { return [] }
    return source[open.upperBound..<close.lowerBound].split(separator: ",").map {
        $0.trimmingCharacters(in: CharacterSet(charactersIn: " \""))
    }
}

/// Copies every song a fill names.
private func filled(_ script: PlayerScript) -> NSAppleEventDescriptor {
    list([text("ok"), number(copiedIDs(script.source).count)])
}

final class QueueContinuationTests: XCTestCase {
    private var cleanUps: [() -> Void] = []

    override func tearDown() {
        cleanUps.forEach { $0() }
        cleanUps = []
        super.tearDown()
    }

    private func search(_ music: ScriptedMusic) -> (AppleMusicSearch, QueuePlaylistStore) {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        cleanUps.append { defaults.removePersistentDomain(forName: name) }
        var search = AppleMusicSearch()
        search.store = QueuePlaylistStore(defaults: defaults)
        search.desk = ScriptDesk { music.lane }
        return (search, search.store)
    }

    private static let albumReply = list([
        text("ok"), text("album"), text("playlist"), number(40),
        // Library order: B2 (disc 1 track 2), A1, C3, D0 (no track number), E (disc 2 track 1), B2 again.
        list(["00B2", "00A1", "00C3", "00D0", "002E", "00B2"].map(text)),
        list([1, 1, 1, 1, 2, 1].map(number)),
        list([2, 1, 3, 0, 1, 2].map(number)),
    ])

    // MARK: Album order

    func testAnAlbumPlaysOnFromTheClickedSongInDiscAndTrackOrder() {
        let tracks = [
            AlbumTrack(id: "00B2", disc: 1, number: 2), AlbumTrack(id: "00A1", disc: 1, number: 1),
            AlbumTrack(id: "00C3", disc: 0, number: 3), AlbumTrack(id: "00D0", disc: 1, number: 0),
            AlbumTrack(id: "002E", disc: 2, number: 1), AlbumTrack(id: "00B2", disc: 1, number: 2),
        ]
        XCTAssertEqual(AppleMusicSearch.albumQueue(startingAt: "00A1", tracks: tracks),
                       ["00A1", "00B2", "00C3", "00D0", "002E"],
                       "disc 0 counts as disc 1; an unnumbered song after the numbered ones; each song once")
        XCTAssertEqual(AppleMusicSearch.albumQueue(startingAt: "00C3", tracks: tracks), ["00C3", "00D0", "002E"],
                       "nothing from before the clicked song")
        XCTAssertEqual(AppleMusicSearch.albumQueue(startingAt: "002E", tracks: tracks), ["002E"], "then nothing more")
        XCTAssertEqual(AppleMusicSearch.albumQueue(startingAt: "FFFF", tracks: tracks), ["FFFF"])
        XCTAssertEqual(AppleMusicSearch.albumQueue(startingAt: "00A1", tracks: [AlbumTrack(id: "00A1"), AlbumTrack(id: "x\" then")]),
                       ["00A1"], "only Music-style IDs")
        let long = (1...200).map { AlbumTrack(id: String(format: "%04X", $0), disc: 1, number: $0) }
        XCTAssertEqual(AppleMusicSearch.albumQueue(startingAt: "0001", tracks: long).count, AppleMusicSearch.queueLength)
    }

    // MARK: Replies

    func testRepliesParse() {
        XCTAssertEqual(QueuePrepared([text("ok"), text("made"), text("library"), number(9000), text("F396B0D76A99857A")]),
                       .made("F396B0D76A99857A", source: "library", songs: 9000))
        XCTAssertEqual(QueuePrepared([text("ok"), text("made"), text("library"), number(9), text("not an id")]), .unreadable)
        XCTAssertEqual(QueuePrepared([text("ok"), text("played"), text("notInLibrary"), text("playlist"), number(12)]),
                       .played(.notInLibrary, source: "playlist", songs: 12))
        XCTAssertEqual(QueuePrepared([text("ok"), text("played"), text("source"), text("radio"), number(0)]),
                       .played(.source, source: "radio", songs: 0))
        XCTAssertEqual(QueuePrepared([text("ok")]), .unreadable)
        XCTAssertEqual(QueuePrepared(Self.albumReply.scriptItems),
                       .album([AlbumTrack(id: "00B2", disc: 1, number: 2), AlbumTrack(id: "00A1", disc: 1, number: 1),
                               AlbumTrack(id: "00C3", disc: 1, number: 3), AlbumTrack(id: "00D0", disc: 1, number: 0),
                               AlbumTrack(id: "002E", disc: 2, number: 1), AlbumTrack(id: "00B2", disc: 1, number: 2)],
                              source: "playlist", songs: 40))
        // Lists of different lengths: library order, no numbers.
        XCTAssertEqual(QueuePrepared([text("ok"), text("album"), text("none"), number(0), list([text("00A1"), text("00B2")]),
                                      list([number(1)]), list([number(1), number(2)])]),
                       .album([AlbumTrack(id: "00A1"), AlbumTrack(id: "00B2")], source: "none", songs: 0))

        XCTAssertEqual(QueueCollected([text("ok"), text("ids"), list([text("0A"), text("bad id!"), text("0B")])]), .ids(["0A", "0B"]))
        XCTAssertEqual(QueueCollected([text("ok"), text("range"), number(140), number(250), number(39)]), .ranges([140...250, 1...39]))
        XCTAssertEqual(QueueCollected([text("ok"), text("range"), number(1), number(150), number(0)]), .ranges([1...150]))
        XCTAssertEqual(QueueCollected([text("ok"), text("range"), number(5), number(4), number(0)]), .unreadable)
        XCTAssertEqual(QueueCollected([text("ok"), text("copied")]), .unreadable)
        XCTAssertEqual(QueueCollected.copied([text("ok"), number(20)]), 20)
        XCTAssertEqual(QueueCollected.copied([text("ok")]), 0)
    }

    // MARK: Builds

    func testASearchResultFromOutsideThePlaylistCarriesOnThroughItsAlbum() async throws {
        let music = ScriptedMusic { step, script in
            switch step {
            case "prepare": return .success(Self.albumReply)
            case "make": return .success(list([text("ok"), text("made"), text("00FE")]))
            case "fill": return .success(filled(script))
            default: return .success(list([text("ok"), number(0)]))
            }
        }
        let (search, store) = search(music)
        let note = try await search.playNoting(PlayerSearchResult(
            id: "lib-00B2", kind: .song, title: "Song", artist: "Artist", album: "Album", artworkURL: nil,
            target: .libraryTrack(persistentID: "00B2"), inLibrary: true))
        XCTAssertEqual(music.steps, ["prepare", "make", "fill", "finish"])
        XCTAssertEqual(copiedIDs(try XCTUnwrap(music.source("fill"))), ["00B2", "00C3", "00D0", "002E"])
        XCTAssertFalse(try XCTUnwrap(music.source("prepare")).contains("set candidate"), "a search result has no playlist position")
        XCTAssertEqual(note, "Then the rest of its album.")
        XCTAssertEqual(store.queueID, "00FE")
        XCTAssertEqual(store.pendingIDs, [])
    }

    func testTheLastSongOnAnAlbumPlaysAloneWithoutAPlaylist() async throws {
        let music = ScriptedMusic { step, _ in
            step == "prepare" ? .success(Self.albumReply) : .success(list([text("ok"), text("played")]))
        }
        let (search, store) = search(music)
        let outcome = try await search.playInContext(persistentID: "002E", index: nil, origin: "search")
        XCTAssertEqual(outcome, .alone(.album))
        XCTAssertEqual(music.steps, ["prepare", "play-alone"])
        XCTAssertTrue(try XCTUnwrap(music.source("play-alone")).contains("whose persistent ID is \"002E\""))
        XCTAssertNotNil(outcome.note)
        XCTAssertEqual(store.pendingIDs, [], "nothing was made")
    }

    func testASourceThatCantBeCopiedPlaysTheSongAndSaysSo() async throws {
        let music = ScriptedMusic { _, _ in
            .success(list([text("ok"), text("played"), text("source"), text("radio"), number(0)]))
        }
        let (search, _) = search(music)
        let note = try await search.playQueueItemNoting(PlayerQueueItem(id: "q", title: "Song", artist: "", index: 3,
                                                                           persistentID: "00AA"))
        XCTAssertEqual(music.steps, ["prepare"])
        XCTAssertEqual(note, QueueSolo.source.note)
        XCTAssertTrue(try XCTUnwrap(music.source("prepare")).contains("set candidate to track 3 of src"))
    }

    func testAFailedBuildStillPlaysTheSongAndDeletesItsHalfBuiltPlaylist() async throws {
        let music = ScriptedMusic { step, _ in
            switch step {
            case "prepare": return .success(list([text("ok"), text("made"), text("library"), number(800), text("00FE")]))
            case "collect": return .failure(ScriptFailure(code: AppleEventCode.timedOut))
            default: return .success(list([text("ok"), text("played")]))
            }
        }
        let (search, store) = search(music)
        let outcome = try await search.playInContext(persistentID: "AB12", index: 5)
        XCTAssertEqual(outcome, .alone(.rescued))
        XCTAssertEqual(music.steps, ["prepare", "collect", "rescue"])
        let rescue = try XCTUnwrap(music.source("rescue"))
        XCTAssertTrue(rescue.contains("first user playlist whose persistent ID is \"00FE\""))
        XCTAssertTrue(rescue.contains("if ownName is \"Lunalyr Queue (new)\" then delete ownList"), "only the half-built one, by ID and exact name")
        XCTAssertFalse(rescue.contains("\"Lunalyr Queue\" then delete"))
        XCTAssertTrue(rescue.contains("set candidate to track 5 of src"), "the clicked copy of the song")
        XCTAssertEqual(store.pendingIDs, ["00FE"], "kept on record in case the delete did not go through")
        XCTAssertNotNil(outcome.note)
    }

    func testABuildThatCopiedNothingPlaysTheSongInstead() async throws {
        let music = ScriptedMusic { step, _ in
            switch step {
            case "prepare": return .success(list([text("ok"), text("made"), text("playlist"), number(3), text("00FE")]))
            case "collect": return .success(list([text("ok"), text("ids"), list([text("0A"), text("0B")])]))
            case "fill": return .success(list([text("ok"), number(0)]))
            default: return .success(list([text("ok"), text("played")]))
            }
        }
        let (search, _) = search(music)
        let outcome = try await search.playInContext(persistentID: "0A", index: 1)
        XCTAssertEqual(outcome, .alone(.rescued))
        XCTAssertEqual(music.steps, ["prepare", "collect", "fill", "rescue"], "an empty queue is never played")
    }

    func testAPlaylistWithSongsMissingFromTheLibrarySaysSomeAreLeftOut() async throws {
        let music = ScriptedMusic { step, _ in
            switch step {
            case "prepare": return .success(list([text("ok"), text("made"), text("apple-music-playlist"), number(4), text("00FE")]))
            case "collect": return .success(list([text("ok"), text("ids"), list(["0A", "0B", "0C", "0D"].map(text))]))
            case "fill": return .success(list([text("ok"), number(2)]))
            default: return .success(list([text("ok"), number(0)]))
            }
        }
        let (search, _) = search(music)
        let outcome = try await search.playInContext(persistentID: "0A", index: 1)
        XCTAssertEqual(outcome, .queued(songs: 2, wanted: 4, fromAlbum: false))
        XCTAssertNotNil(outcome.note)
    }

    func testAFinishThatCouldNotPlayFallsBackToTheSongAlone() async throws {
        let music = ScriptedMusic { step, script in
            switch step {
            case "prepare": return .success(list([text("ok"), text("made"), text("playlist"), number(3), text("00FE")]))
            case "collect": return .success(list([text("ok"), text("ids"), list([text("0A"), text("0B")])]))
            case "fill": return .success(filled(script))
            case "finish": return .success(list([text("ok"), number(-10000)]))
            default: return .success(list([text("ok"), text("played")]))
            }
        }
        let (search, store) = search(music)
        let outcome = try await search.playInContext(persistentID: "0A", index: 1)
        XCTAssertEqual(outcome, .alone(.rescued))
        XCTAssertEqual(store.queueID, "00FE", "the finished queue is Lunalyr's queue now")
        XCTAssertFalse(try XCTUnwrap(music.source("rescue")).contains("delete"), "nothing half-built is left to delete")
    }

    func testWhenEvenTheRescueFailsTheErrorIsReported() async throws {
        let music = ScriptedMusic { _, _ in .failure(ScriptFailure(code: -1728)) }
        let (search, _) = search(music)
        do {
            try await search.playInContext(persistentID: "0A", index: nil)
            XCTFail("nothing played")
        } catch {
            XCTAssertEqual(error as? PlayerSearchError, .scriptFailed(-1728))
        }
        XCTAssertEqual(music.steps, ["prepare", "rescue"])
    }

    func testOnlyTheLatestOfQuickClicksPlays() async throws {
        let music = ScriptedMusic { step, script in
            switch step {
            case "prepare" where script.source.contains("\"0A\""):
                return .success(list([text("ok"), text("made"), text("playlist"), number(3), text("00F1")]))
            case "prepare": return .success(list([text("ok"), text("made"), text("playlist"), number(3), text("00F2")]))
            case "collect": return .success(list([text("ok"), text("ids"), list([text("0A"), text("0B")])]))
            case "fill": return .success(filled(script))
            default: return .success(list([text("ok"), number(0)]))
            }
        }
        music.hold = "prepare"
        let (search, store) = search(music)
        let first = Task { try await search.playInContext(persistentID: "0A", index: 1) }
        while music.lock.withLock({ music.held == nil }) { try await Task.sleep(nanoseconds: 5_000_000) }
        let second = Task { try await search.playInContext(persistentID: "0B", index: 2) }
        try await Task.sleep(nanoseconds: 200_000_000)
        music.lock.withLock { music.held }?()

        let firstOutcome = try await first.value
        let secondOutcome = try await second.value
        XCTAssertEqual(firstOutcome, .superseded)
        XCTAssertEqual(secondOutcome, .queued(songs: 2, wanted: 2, fromAlbum: false))
        XCTAssertEqual(music.sources("finish").count, 1, "only the second click plays")
        XCTAssertTrue(try XCTUnwrap(music.source("finish")).contains("whose persistent ID is \"00F2\""))
        XCTAssertTrue(try XCTUnwrap(music.sources("prepare").last).contains("whose persistent ID is \"00F1\""),
                      "the second click deletes the first one's half-built playlist")
        XCTAssertEqual(store.queueID, "00F2")
        XCTAssertEqual(store.pendingIDs, [])
    }

    // MARK: Notes, errors, records

    func testEveryWayOfPlayingAloneSaysWhyInPlainWords() {
        for why in [QueueSolo.source, .notInLibrary, .single, .album, .rescued] {
            XCTAssertFalse(why.note.isEmpty)
            XCTAssertLessThan(why.note.count, 110, "short enough for the strip")
            XCTAssertEqual(QueueOutcome.alone(why).note, why.note)
        }
        XCTAssertNil(QueueOutcome.queued(songs: 150, wanted: 150, fromAlbum: false).note)
        XCTAssertNil(QueueOutcome.superseded.note)
        XCTAssertGreaterThan(PlayerNoteToast.noteSeconds(QueueSolo.notInLibrary.note), 2.5)
        XCTAssertEqual(PlayerNoteToast.noteSeconds(nil), 2.5)
    }

    func testFailuresKeepTheirAppleScriptErrorNumber() {
        let timedOut = ScriptDesk.reply(.failure(ScriptFailure(code: AppleEventCode.timedOut)), for: .appleMusic)
        XCTAssertThrowsError(try timedOut.get()) { XCTAssertEqual($0 as? PlayerSearchError, .scriptFailed(-1712)) }
        XCTAssertEqual(PlayerSearchError.logText(PlayerSearchError.scriptFailed(-10006)), "error -10006")
        XCTAssertEqual(PlayerSearchError.logText(PlayerSearchError.notRunning(.appleMusic)), "Music is not running")
        XCTAssertEqual(DesktopPlayerModel.words(for: PlayerSearchError.scriptFailed(-1712), player: .appleMusic),
                       DesktopPlayerModel.words(for: PlayerSearchError.failed, player: .appleMusic))
    }

    func testOnlyTheLatestUnfinishedBuildsStayOnRecord() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let store = QueuePlaylistStore(defaults: defaults)
        let old = (1...12).map { String(format: "%04X", $0) }
        store.record("00FF", after: old + ["00FF"])
        XCTAssertEqual(store.pendingIDs.count, QueuePlaylistStore.pendingLimit)
        XCTAssertEqual(store.pendingIDs.last, "00FF")
        XCTAssertEqual(store.pendingIDs.filter { $0 == "00FF" }.count, 1)
    }
}

// MARK: - Every queue script compiles against Music's own dictionary

/// Compiles, never runs, each script against a copy of Music's scripting
/// dictionary in a stand-in app bundle, so nothing is sent to Music (or to
/// anything else): a reserved word used as a name, or a term Music does not
/// define, fails here instead of failing every build live.
final class QueueScriptCompileTests: XCTestCase {
    func testEveryQueueScriptCompilesAgainstMusicsDictionary() throws {
        let sdef = URL(fileURLWithPath: "/System/Applications/Music.app/Contents/Resources/com.apple.Music.sdef")
        guard FileManager.default.fileExists(atPath: sdef.path) else { throw XCTSkip("Music's dictionary is not on this Mac") }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("LunalyrDictionary-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: folder) }
        let app = folder.appendingPathComponent("MusicDictionary.app")
        let resources = app.appendingPathComponent("Contents/Resources")
        try FileManager.default.createDirectory(at: resources, withIntermediateDirectories: true)
        try FileManager.default.copyItem(at: sdef, to: resources.appendingPathComponent("Dictionary.sdef"))
        let info: [String: Any] = [
            "CFBundleIdentifier": "local.lunalyr-tests.music-dictionary", "CFBundleName": "MusicDictionary",
            "CFBundlePackageType": "APPL", "NSAppleScriptEnabled": true, "OSAScriptingDefinition": "Dictionary.sdef",
        ]
        try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0)
            .write(to: app.appendingPathComponent("Contents/Info.plist"))

        let bodies: [String: String] = [
            "prepare": AppleMusicSearch.prepareQueue(persistentID: "AB12", index: 7, leftovers: ["0A1B", "2C3D"]),
            "prepare, no position": AppleMusicSearch.prepareQueue(persistentID: "AB12", index: nil, leftovers: []),
            "make": AppleMusicSearch.makeQueue(),
            "play alone": AppleMusicSearch.playAlone(persistentID: "AB12"),
            "collect": AppleMusicSearch.collectQueue(persistentID: "AB12", index: 3, into: "FFEE"),
            "fill": AppleMusicSearch.fillQueue(["0A", "0B"], into: "FFEE"),
            "fill range": AppleMusicSearch.fillRange(from: 3, to: 27, into: "FFEE"),
            "finish": AppleMusicSearch.finishQueue(into: "FFEE", replacing: ["0A1B", "2C3D"]),
            "rescue": AppleMusicSearch.rescueQueue(persistentID: "AB12", index: 4, newID: "FFEE"),
            "rescue, nothing made": AppleMusicSearch.rescueQueue(persistentID: "AB12", index: nil, newID: nil),
        ]
        let music = "tell application id \"\(AppleMusicSearch.bundleID)\""
        let running = "if application id \"\(AppleMusicSearch.bundleID)\" is not running then return {\"notRunning\"}"
        for (name, body) in bodies.sorted(by: { $0.key < $1.key }) {
            let live = PlayerScriptSource.query(AppleMusicSearch.bundleID, timeout: AppleMusicSearch.queueStepTimeout, body)
            XCTAssertTrue(live.contains(music) && live.contains(running), name)
            let source = live.replacingOccurrences(of: running, with: "")
                .replacingOccurrences(of: music, with: "tell application \(ScriptText.quoted(app.path))")
            let script = try XCTUnwrap(NSAppleScript(source: source))
            var error: NSDictionary?
            XCTAssertTrue(script.compileAndReturnError(&error), "\(name): \(error?[NSAppleScript.errorMessage] ?? "")")
        }
    }
}
