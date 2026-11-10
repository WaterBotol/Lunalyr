import AppKit
import Combine
import XCTest
@testable import VersoCustom

// Review round 2 (R2-6): the desktop player's queue is built in short
// scripts. A fake script lane stands in for Music.

private func text(_ value: String) -> NSAppleEventDescriptor { NSAppleEventDescriptor(string: value) }
private func number(_ value: Double) -> NSAppleEventDescriptor { NSAppleEventDescriptor(double: value) }

// MARK: - R2-6: building the queue never holds the script queue for long

final class QueueBuildStepsTests: XCTestCase {
    /// Stands in for Music on the monitor's script queue: answers each step
    /// by its name, at once, and records what it was sent.
    private final class FakeMusicLane: @unchecked Sendable {
        let lock = NSLock()
        var sent: [PlayerScript] = []
        let collectReply: NSAppleEventDescriptor

        init(collectReply: NSAppleEventDescriptor) { self.collectReply = collectReply }

        var lane: ScriptLane {
            ScriptLane { script, _, _, done in
                self.lock.withLock { self.sent.append(script) }
                let answer: NSAppleEventDescriptor
                switch script.name {
                case "desktop-player.queue-prepare":
                    answer = .scriptList([text("ok"), text("made"), text("playlist"), number(300), text("FFEE")])
                case "desktop-player.queue-collect": answer = self.collectReply
                case "desktop-player.queue-finish": answer = .scriptList([text("ok"), number(0)])
                case "desktop-player.queue-fill": answer = .scriptList([text("ok"), number(Double(self.copiedPerFill(script)))])
                default: answer = .scriptList([text("ok")])
                }
                done(.success(answer))
            }
        }

        var names: [String] { lock.withLock { sent.map(\.name) } }

        /// Every song a fill names, or every position in its range, was copied.
        func copiedPerFill(_ script: PlayerScript) -> Int {
            if let match = script.source.firstMatch(of: /duplicate tracks (\d+) thru (\d+) of src/),
               let from = Int(match.1), let to = Int(match.2) { return to - from + 1 }
            return script.source.components(separatedBy: "\", \"").count
        }
    }

    private func store() -> (QueuePlaylistStore, UserDefaults, String) {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        return (QueuePlaylistStore(defaults: defaults), defaults, name)
    }

    func testAPlaylistsSongsAreCopiedInSmallSeparateScripts() async throws {
        let ids = (0..<45).map { String(format: "%04X", $0) }
        let fake = FakeMusicLane(collectReply: .scriptList([text("ok"), text("ids"), .scriptList(ids.map(text))]))
        let (store, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        var search = AppleMusicSearch()
        search.store = store
        search.desk = ScriptDesk { fake.lane }
        try await search.playInContext(persistentID: "AB12", index: 2)

        XCTAssertEqual(fake.names, ["desktop-player.queue-prepare", "desktop-player.queue-collect",
                                    "desktop-player.queue-fill", "desktop-player.queue-fill", "desktop-player.queue-fill",
                                    "desktop-player.queue-finish"],
                       "each run of songs is its own script, so polls go in between")
        let fills = fake.lock.withLock { fake.sent.filter { $0.name == "desktop-player.queue-fill" } }
        var copied: [String] = []
        for fill in fills {
            let inThis = ids.filter { fill.source.contains("\"\($0)\"") }
            XCTAssertLessThanOrEqual(inThis.count, AppleMusicSearch.queueChunk)
            copied += inThis
        }
        XCTAssertEqual(copied, ids, "every song, once, in order")
        XCTAssertEqual(store.queueID, "FFEE")
        XCTAssertEqual(store.pendingIDs, [])
    }

    func testTheLibraryIsCopiedInShortRangedDuplicates() async throws {
        // The song is the library's 140th; 150 songs from there wrap round to the first 39.
        let fake = FakeMusicLane(collectReply: .scriptList([text("ok"), text("range"), number(140), number(250), number(39)]))
        let (store, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        var search = AppleMusicSearch()
        search.store = store
        search.desk = ScriptDesk { fake.lane }
        let outcome = try await search.playInContext(persistentID: "AB12", index: nil)
        XCTAssertEqual(outcome, .queued(songs: 150, wanted: 150, fromAlbum: false))
        XCTAssertNil(outcome.note)
        let fills = fake.lock.withLock { fake.sent.filter { $0.name == "desktop-player.queue-fill" }.map(\.source) }
        let ranges = fills.compactMap { source -> ClosedRange<Int>? in
            guard let match = source.firstMatch(of: /duplicate tracks (\d+) thru (\d+) of src/),
                  let from = Int(match.1), let to = Int(match.2) else { return nil }
            return from...to
        }
        XCTAssertEqual(ranges, [140...164, 165...189, 190...214, 215...239, 240...250, 1...25, 26...39],
                       "the playing song first, in order, then the wrap round, never more than 25 in one event")
        XCTAssertEqual(fake.names.first, "desktop-player.queue-prepare")
        XCTAssertEqual(fake.names.last, "desktop-player.queue-finish")

        let collect = AppleMusicSearch.collectQueue(persistentID: "AB12", index: nil, into: "FFEE")
        XCTAssertTrue(collect.contains("if class of src is library playlist then return {\"ok\", \"range\", n, endAt, upTo}"))
        XCTAssertFalse(collect.contains("duplicate"), "collecting copies nothing")
        XCTAssertFalse(collect.contains("whose persistent ID is wantedID"), "no library-wide search per song")
    }

    func testOneSongMusicWontAddDoesNotSinkTheBuild() throws {
        let fill = AppleMusicSearch.fillRange(from: 3, to: 27, into: "FFEE")
        // The ranged copy first; only if it was refused and added nothing, one at a time.
        let ranged = try XCTUnwrap(fill.range(of: "duplicate tracks 3 thru 27 of src to newList"))
        let guardLine = try XCTUnwrap(fill.range(of: "if (count of tracks of newList) is countBefore then\nrepeat with i from 3 to 27"))
        XCTAssertLessThan(ranged.lowerBound, guardLine.lowerBound)
        XCTAssertTrue(fill.contains("duplicate track i of src to newList"))
        XCTAssertTrue(fill.contains("set src to library playlist 1"), "only library songs are copied")
        XCTAssertEqual(fill.components(separatedBy: "if errorNumber is not in \(AppleMusicSearch.skippable) then error number errorNumber").count, 3)
        XCTAssertTrue(AppleMusicSearch.skippable.contains("-10006") && AppleMusicSearch.skippable.contains("-10000"))
        XCTAssertFalse(AppleMusicSearch.skippable.contains("-1712"), "a timeout ends the step")
        XCTAssertTrue(fill.contains("return {\"ok\", copied}"))
        XCTAssertFalse(fill.contains("delete track"))
    }

    func testRangeChunks() {
        XCTAssertEqual(AppleMusicSearch.rangeChunks([]), [])
        XCTAssertEqual(AppleMusicSearch.rangeChunks([1...25]), [1...25])
        XCTAssertEqual(AppleMusicSearch.rangeChunks([1...26]), [1...25, 26...26])
        XCTAssertEqual(AppleMusicSearch.rangeChunks([0...3, 5...5]), [5...5], "position 0 does not exist")
    }

    func testChunksAndIDsAreSafe() {
        XCTAssertEqual(AppleMusicSearch.chunks([]), [])
        XCTAssertEqual(AppleMusicSearch.chunks((0..<20).map(String.init)).count, 1)
        XCTAssertEqual(AppleMusicSearch.chunks((0..<21).map(String.init)).map(\.count), [20, 1])
        let fill = AppleMusicSearch.fillQueue(["0A", "x\" then delete", "0B"], into: "FFEE")
        XCTAssertTrue(fill.contains(#"{"0A", "0B"}"#), "only Music-style IDs go into the script")
    }
}
