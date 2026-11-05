import CryptoKit
import XCTest
@testable import VersoCustom

/// Per-song offsets are tuned to the lyric source on screen.
final class SongOffsetTests: XCTestCase {
    @MainActor
    func testOffsetsFollowTheLyricSourceAndOlderOffsetsStillApply() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        // A unique domain prevents tests modifying real song preferences.
        let isolated = UserDefaults(suiteName: name)!
        defer { isolated.removePersistentDomain(forName: name) }
        let song = LyricQuery(title: "Song", artist: "Artist", album: "Studio", duration: 200)
        let store = SongOffsets(defaults: isolated)
        store.set(1, for: song)
        // Offsets saved before sources were told apart are keyed by recording only.
        let legacy = SHA256.hash(data: Data(song.recordingKey.utf8)).map { String(format: "%02x", $0) }.joined()
        XCTAssertEqual(isolated.dictionary(forKey: "custom.songOffsets")?[legacy] as? Double, 1)
        XCTAssertEqual(store.offset(for: song, source: "LRCLIB"), 1)

        store.activeSource = "NetEase"
        store.set(0.4, for: song)
        XCTAssertEqual(store.offset(for: song), 0.4)
        XCTAssertEqual(store.offset(for: song, source: "LRCLIB"), 1)
        store.set(0, for: song)
        XCTAssertEqual(store.offset(for: song), 0, "Resetting one source must not bring back the older offset")
        let reloaded = SongOffsets(defaults: isolated)
        XCTAssertEqual(reloaded.offset(for: song, source: "NetEase"), 0)
        XCTAssertEqual(reloaded.offset(for: song, source: "LRCLIB"), 1)

        let other = LyricQuery(title: "Other", artist: "Artist", album: nil, duration: 100)
        store.set(0.5, for: other)
        store.set(0, for: other)
        XCTAssertEqual(isolated.dictionary(forKey: "custom.songOffsets")?.count, 2)

        // The wallpaper asks every frame; a remembered answer is only reused
        // for the same recording and source, and never outlives a change.
        XCTAssertEqual(reloaded.offset(for: song, source: "LRCLIB"), 1)
        let longer = LyricQuery(title: "Song", artist: "Artist", album: "Studio", duration: 230)
        XCTAssertEqual(reloaded.offset(for: longer, source: "LRCLIB"), 0)
        reloaded.set(-0.3, for: song, source: "LRCLIB")
        XCTAssertEqual(reloaded.offset(for: song, source: "LRCLIB"), -0.3)
        reloaded.activeSource = "LRCLIB"
        XCTAssertEqual(reloaded.offset(for: song), -0.3)
        reloaded.activeSource = "NetEase"
        XCTAssertEqual(reloaded.offset(for: song), 0)
    }
}
