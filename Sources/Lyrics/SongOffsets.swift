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
    /// One Earlier or Later step, in seconds.
    static let step = 0.05
    /// The furthest an offset goes either way, in seconds.
    static let limit = 30.0

    /// A typed offset in seconds (positive: lyrics earlier), or nil when the
    /// text is not one. Takes an optional sign (+ - − –), a decimal point or
    /// comma and an optional unit, s, sec or ms ("350ms" is 0.35); spaces
    /// are ignored. Rounded to hundredths; beyond ±30 s is rejected.
    static func parse(_ text: String) -> Double? {
        var body = String(text.filter { !$0.isWhitespace }.lowercased())
        var scale = 1.0
        if body.hasSuffix("ms") { body.removeLast(2); scale = 0.001 }
        else if body.hasSuffix("sec") { body.removeLast(3) }
        else if body.hasSuffix("s") { body.removeLast() }
        var sign = 1.0
        if let first = body.first, "+-−–".contains(first) {
            if first != "+" { sign = -1 }
            body.removeFirst()
        }
        body = body.replacingOccurrences(of: ",", with: ".")
        guard body.contains(where: { $0.isASCII && $0.isNumber }),
              body.allSatisfy({ ($0.isASCII && $0.isNumber) || $0 == "." }),
              body.filter({ $0 == "." }).count <= 1,
              let number = Double(body) else { return nil }
        let value = (sign * number * scale * 100).rounded() / 100
        guard value.isFinite, abs(value) <= limit else { return nil }
        return value == 0 ? 0 : value
    }
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
