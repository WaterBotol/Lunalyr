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
                case "desktop-player.queue-prepare": answer = .scriptList([text("ok"), text("made"), text("FFEE")])
                case "desktop-player.queue-collect": answer = self.collectReply
                case "desktop-player.queue-finish": answer = .scriptList([text("ok"), number(0)])
                default: answer = .scriptList([text("ok")])
                }
                done(.success(answer))
            }
        }

        var names: [String] { lock.withLock { sent.map(\.name) } }
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

    func testTheLibraryIsCopiedInOneRangedDuplicate() async throws {
        let fake = FakeMusicLane(collectReply: .scriptList([text("ok"), text("copied")]))
        let (store, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        var search = AppleMusicSearch()
        search.store = store
        search.desk = ScriptDesk { fake.lane }
        try await search.playInContext(persistentID: "AB12", index: nil)
        XCTAssertEqual(fake.names, ["desktop-player.queue-prepare", "desktop-player.queue-collect", "desktop-player.queue-finish"])

        let collect = AppleMusicSearch.collectQueue(persistentID: "AB12", index: nil, into: "FFEE")
        XCTAssertTrue(collect.contains("if class of src is library playlist then\nduplicate tracks n thru endAt of src to newList"))
        XCTAssertTrue(collect.contains("if upTo > 0 then duplicate tracks 1 thru upTo of src to newList"))
        XCTAssertFalse(collect.contains("whose persistent ID is wantedID"), "no library-wide search per song")
        XCTAssertFalse(collect.contains("repeat with idItem"))
    }

    func testChunksAndIDsAreSafe() {
        XCTAssertEqual(AppleMusicSearch.chunks([]), [])
        XCTAssertEqual(AppleMusicSearch.chunks((0..<20).map(String.init)).count, 1)
        XCTAssertEqual(AppleMusicSearch.chunks((0..<21).map(String.init)).map(\.count), [20, 1])
        let fill = AppleMusicSearch.fillQueue(["0A", "x\" then delete", "0B"], into: "FFEE")
        XCTAssertTrue(fill.contains(#"{"0A", "0B"}"#), "only Music-style IDs go into the script")
    }
}
