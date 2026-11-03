import Combine
import CryptoKit
import Foundation

@MainActor
final class SongOffsets: ObservableObject {
    static let shared = SongOffsets()
    @Published private(set) var values: [String: Double]
    private let defaults: UserDefaults
    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        values = defaults.dictionary(forKey: "custom.songOffsets") as? [String: Double] ?? [:]
    }
    private func key(_ query: LyricQuery) -> String {
        SHA256.hash(data: Data(query.recordingKey.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    func offset(for query: LyricQuery?) -> Double {
        guard let query else { return 0 }
        let value = values[key(query)] ?? 0
        return value.isFinite ? min(30, max(-30, value)) : 0
    }
    func set(_ value: Double, for query: LyricQuery) {
        guard value.isFinite else { return }
        let name = key(query), bounded = min(30, max(-30, value))
        if bounded == 0 { values.removeValue(forKey: name) } else { values[name] = bounded }
        defaults.set(values, forKey: "custom.songOffsets")
    }
}
