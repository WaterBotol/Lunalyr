import AppKit
import XCTest
@testable import VersoCustom

private func text(_ value: String) -> NSAppleEventDescriptor { NSAppleEventDescriptor(string: value) }

// MARK: - Song and search text in scripts

final class ScriptTextRoundTripTests: XCTestCase {
    /// Runs `return <literal>` as plain AppleScript: no `tell`, so nothing
    /// is sent to any other app. What comes back must be the text itself.
    private func roundTrip(_ value: String, file: StaticString = #filePath, line: UInt = #line) -> String? {
        let literal = ScriptText.quoted(value)
        guard let script = NSAppleScript(source: "set theText to \(literal)\nreturn theText") else {
            XCTFail("no script for \(literal)", file: file, line: line)
            return nil
        }
        var error: NSDictionary?
        let reply = script.executeAndReturnError(&error)
        XCTAssertNil(error, "\(literal) did not run: \(String(describing: error))", file: file, line: line)
        return reply.stringValue
    }

    func testReservedWordsQuotesAndUnicodeComeBackUnchanged() {
        let samples = [
            "back", "last", "stop", "back last stop", "end tell", "return", "error number -128",
            "\" & (do shell script \"echo hi\") & \"", "a\\\"b", "trailing\\", "\\",
            "Beyoncé — Déjà Vu", "東京事変", "🎧 Song (feat. Ü)", "¬ continued", "«data utxt0041»",
            "tab\there", "",
        ]
        for sample in samples {
            XCTAssertEqual(roundTrip(sample), sample, sample)
        }
    }

    func testLineBreaksAndControlCharactersBecomeSpaces() {
        XCTAssertEqual(roundTrip("one\ntwo\rthree\r\nfour"), "one two three  four")
        XCTAssertEqual(roundTrip("a\u{2028}b\u{2029}c\u{85}d"), "a b c d")
        XCTAssertEqual(roundTrip("nul\u{0}after\u{7}bell"), "nul after bell", "a NUL must not end the source early")
        XCTAssertFalse(ScriptText.quoted("x\u{0}y").unicodeScalars.contains("\u{0}"))
    }
}

// MARK: - "Lunalyr Queue": an orphan from an unfinished build is cleaned up

final class QueueOrphanTests: XCTestCase {
    private final class Lane: @unchecked Sendable {
        let lock = NSLock()
        var sent: [PlayerScript] = []
        var finishFails = false

        var lane: ScriptLane {
            ScriptLane { script, _, _, done in
                self.lock.withLock { self.sent.append(script) }
                switch script.name {
                case "desktop-player.queue-prepare":
                    done(.success(.scriptList([text("ok"), text("made"), text("library"), NSAppleEventDescriptor(int32: 500),
                                               text("FFEE")])))
                case "desktop-player.queue-collect":
                    done(.success(.scriptList([text("ok"), text("range"), NSAppleEventDescriptor(int32: 1),
                                               NSAppleEventDescriptor(int32: 2), NSAppleEventDescriptor(int32: 0)])))
                case "desktop-player.queue-fill":
                    done(.success(.scriptList([text("ok"), NSAppleEventDescriptor(int32: 2)])))
                case "desktop-player.queue-finish" where self.finishFails:
                    done(.failure(ScriptFailure(code: AppleEventCode.timedOut)))
                case "desktop-player.queue-finish":
                    done(.success(.scriptList([text("ok"), NSAppleEventDescriptor(int32: 0)])))
                default:
                    done(.success(.scriptList([text("ok"), text("played")])))
                }
            }
        }

        func source(_ name: String) -> String? { lock.withLock { sent.last { $0.name == name }?.source } }
    }

    private func makeStore() -> (QueuePlaylistStore, () -> Void) {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        return (QueuePlaylistStore(defaults: defaults), { defaults.removePersistentDomain(forName: name) })
    }

    func testAFinishThatNeverReportedBackIsDeletedByTheNextOne() async throws {
        let (store, cleanUp) = makeStore()
        defer { cleanUp() }
        let fake = Lane()
        var search = AppleMusicSearch()
        search.store = store
        search.desk = ScriptDesk { fake.lane }

        // A build whose finish renamed its playlist but whose reply was lost.
        store.queueID = "1111"
        fake.finishFails = true
        let outcome = try await search.playInContext(persistentID: "AB12", index: nil)
        XCTAssertEqual(outcome, .alone(.rescued), "the finish failed, so the song plays on its own")
        let rescue = try XCTUnwrap(fake.source("desktop-player.queue-rescue"))
        XCTAssertFalse(rescue.contains("if ownName is \"Lunalyr Queue\" then delete"),
                       "a playlist the finish may have renamed and started is never deleted by the rescue")
        XCTAssertEqual(store.queueID, "1111")
        XCTAssertEqual(store.pendingIDs, ["FFEE"], "the unfinished playlist stays on record")

        // The next click: the old queue and that playlist both go, each by its
        // own ID and only while named exactly "Lunalyr Queue".
        fake.finishFails = false
        let next = Lane()
        search.desk = ScriptDesk { next.lane }
        // Music hands out a fresh ID for the new build; the fake reuses
        // "FFEE", so record the orphan under another one.
        store.pendingIDs = ["2222"]
        try await search.playInContext(persistentID: "AB12", index: nil)
        let prepare = try XCTUnwrap(next.source("desktop-player.queue-prepare"))
        XCTAssertTrue(prepare.contains("whose persistent ID is \"2222\""))
        XCTAssertTrue(prepare.contains("if ownName is \"Lunalyr Queue (new)\" then delete ownList"))
        let finish = try XCTUnwrap(next.source("desktop-player.queue-finish"))
        for id in ["1111", "2222"] {
            XCTAssertTrue(finish.contains("first user playlist whose persistent ID is \"\(id)\""), id)
        }
        XCTAssertFalse(finish.contains("whose persistent ID is \"FFEE\")\nif class"), "never the new queue itself")
        XCTAssertEqual(finish.components(separatedBy: "if ownName is \"Lunalyr Queue\" then delete ownList").count, 3)
        XCTAssertFalse(finish.contains("every user playlist whose name"))
        XCTAssertEqual(store.queueID, "FFEE")
        XCTAssertEqual(store.pendingIDs, [])
    }

    func testFinishDeletesEachOldIDOnceAndNeverTheNewOne() throws {
        let finish = AppleMusicSearch.finishQueue(into: "FFEE", replacing: ["0A1B", "0A1B", "FFEE", "not hex\" then"])
        XCTAssertEqual(finish.components(separatedBy: "delete ownList").count, 2)
        XCTAssertTrue(finish.contains("whose persistent ID is \"0A1B\""))
        XCTAssertFalse(finish.contains("not hex"))
        // Shuffle is read before anything changes and put back on every path.
        let read = try XCTUnwrap(finish.range(of: "set wasShuffle to shuffle enabled"))
        let off = try XCTUnwrap(finish.range(of: "set shuffle enabled to false"))
        XCTAssertLessThan(read.lowerBound, off.lowerBound)
        XCTAssertEqual(finish.components(separatedBy: "set shuffle enabled to wasShuffle").count, 4)
    }
}

// MARK: - The player on two displays, and its history

final class PlayerDisplayAndHistoryTests: XCTestCase {
    func testASearchStaysOnTheDisplayItWasTypedOn() {
        let left: CGDirectDisplayID = 1, right: CGDirectDisplayID = 2
        // Hovered: that display only.
        XCTAssertTrue(HoverState.showsPlayer(on: left, active: left, pinned: left, searching: false))
        XCTAssertFalse(HoverState.showsPlayer(on: right, active: left, pinned: left, searching: false))
        // Typing on the left, pointer gone: still the left one, not both.
        XCTAssertTrue(HoverState.showsPlayer(on: left, active: nil, pinned: left, searching: true))
        XCTAssertFalse(HoverState.showsPlayer(on: right, active: nil, pinned: left, searching: true))
        XCTAssertFalse(HoverState.showsPlayer(on: right, active: left, pinned: left, searching: true))
        // Not searching, nothing hovered: nowhere.
        XCTAssertFalse(HoverState.showsPlayer(on: left, active: nil, pinned: left, searching: false))
    }

    func testOnlyMusicSongsGoIntoTheStripsHistory() {
        let a = NowPlayingMonitor.Track(title: "A", artist: "X", duration: 200, persistentID: "0A")
        let b = NowPlayingMonitor.Track(title: "B", artist: "X", duration: 200, persistentID: "0B")
        XCTAssertTrue(DesktopPlayerModel.belongsInHistory(previous: a, from: .appleMusic, next: b, in: .appleMusic))
        XCTAssertFalse(DesktopPlayerModel.belongsInHistory(previous: a, from: .spotify, next: b, in: .appleMusic),
                       "a Spotify song's id means nothing to Music")
        XCTAssertFalse(DesktopPlayerModel.belongsInHistory(previous: a, from: .appleMusic, next: b, in: .chrome))
        XCTAssertFalse(DesktopPlayerModel.belongsInHistory(previous: a, from: .appleMusic, next: a, in: .appleMusic))
    }
}
