import Combine
import CryptoKit
import Foundation

/// Per-song timing corrections. An offset is tuned to one lyric source's
/// timing, so it is stored per recording and source; offsets saved before
/// that distinction existed are keyed by recording only and still apply
/// to every source until one is set for the source on screen.
@MainActor
final class SongOffsets: ObservableObject {
    static let shared = SongOffsets()
    @Published private(set) var values: [String: Double] {
        didSet { resolved = nil }
    }
    /// The lyric source on screen (its provenance), kept current by the lyrics
    /// model so `offset(for:)` callers need not know it.
    @Published var activeSource: String?
    private let defaults: UserDefaults
    /// The last offset looked up. The wallpaper asks every frame, so the
    /// recording key and its hashes are only worked out when the song,
    /// the source or a saved value changes.
    private var resolved: (song: Song, source: String?, value: Double)?
    private struct Song: Equatable {
        var title: String, artist: String, album: String?, duration: Double
        init(_ query: LyricQuery) { title = query.title; artist = query.artist; album = query.album; duration = query.duration }
    }
    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        values = defaults.dictionary(forKey: "custom.songOffsets") as? [String: Double] ?? [:]
    }
    private func key(_ query: LyricQuery) -> String {
        SHA256.hash(data: Data(query.recordingKey.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    private func key(_ query: LyricQuery, source: String) -> String {
        SHA256.hash(data: Data((query.recordingKey + "|source:" + source).utf8)).map { String(format: "%02x", $0) }.joined()
    }
    /// The offset for the source on screen.
    func offset(for query: LyricQuery?) -> Double { offset(for: query, source: activeSource) }
    func offset(for query: LyricQuery?, source: String?) -> Double {
        guard let query else { return 0 }
        let song = Song(query)
        if let resolved, resolved.song == song, resolved.source == source { return resolved.value }
        let tuned = source.flatMap { $0.isEmpty ? nil : values[key(query, source: $0)] }
        let found = tuned ?? values[key(query)] ?? 0
        let value = found.isFinite ? min(30, max(-30, found)) : 0
        resolved = (song, source, value)
        return value
    }
    /// Saves the offset for the source on screen.
    func set(_ value: Double, for query: LyricQuery) { set(value, for: query, source: activeSource) }
    func set(_ value: Double, for query: LyricQuery, source: String?) {
        guard value.isFinite else { return }
        let bounded = min(30, max(-30, value))
        guard let source, !source.isEmpty else {
            let name = key(query)
            if bounded == 0 { values.removeValue(forKey: name) } else { values[name] = bounded }
            defaults.set(values, forKey: "custom.songOffsets")
            return
        }
        let name = key(query, source: source)
        // An explicit zero is kept while an older recording-wide offset
        // exists, so resetting this source does not bring that one back.
        if bounded == 0 && values[key(query)] == nil { values.removeValue(forKey: name) } else { values[name] = bounded }
        defaults.set(values, forKey: "custom.songOffsets")
    }
}
