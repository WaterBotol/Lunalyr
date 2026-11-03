import Combine
import CryptoKit
import Foundation

enum LyricSourceID: String, CaseIterable, Identifiable, Codable {
    case lyricsPlus, netEase, qqMusic, kugou, lrclib, lyrimuse
    var id: String { rawValue }
    var title: String {
        switch self {
        case .lyricsPlus: return "Lyrics+ / KPoe"
        case .netEase: return "NetEase Cloud Music"
        case .qqMusic: return "QQ Music"
        case .kugou: return "Kugou Music"
        case .lrclib: return "LRCLIB"
        case .lyrimuse: return "Lyrimuse · local files"
        }
    }
    var timing: String {
        switch self {
        case .lyricsPlus: return "Word / syllable timing"
        case .netEase: return "Word or line timing"
        case .qqMusic, .kugou, .lrclib: return "Line timing"
        case .lyrimuse: return "LRC / TTML / YRC + readings"
        }
    }
}

@MainActor
final class LyricSourceOptions: ObservableObject {
    static let shared = LyricSourceOptions()
    enum Mode: String, CaseIterable, Identifiable {
        case smart, priority
        var id: String { rawValue }
        var title: String { self == .smart ? "Best match" : "Priority order" }
    }
    struct Snapshot {
        let enabled: [LyricSourceID]
        let mode: Mode
        let localFolder: String
        let localVersion: String
        var fingerprint: String { "v3|" + mode.rawValue + "|" + enabled.map(\.rawValue).joined(separator: ",") + "|" + localVersion }
    }
    @Published var order: [LyricSourceID] {
        didSet { UserDefaults.standard.set(order.map(\.rawValue), forKey: "custom.lyricSourceOrder") }
    }
    @Published var enabled: Set<LyricSourceID> {
        didSet { UserDefaults.standard.set(enabled.map(\.rawValue).sorted(), forKey: "custom.lyricSources") }
    }
    @Published var mode: Mode {
        didSet { UserDefaults.standard.set(mode.rawValue, forKey: "custom.lyricMatchMode") }
    }
    @Published var repairQQGaps: Bool {
        didSet { UserDefaults.standard.set(repairQQGaps, forKey: "custom.repairQQGaps") }
    }
    var snapshot: Snapshot {
        Snapshot(enabled: order.filter { enabled.contains($0) }, mode: mode,
                 localFolder: LocalLyricOptions.shared.folder,
                 localVersion: enabled.contains(.lyrimuse) ? LocalLyricOptions.shared.fingerprint : "")
    }
    var fingerprint: String { snapshot.fingerprint }
    private init() {
        let d = UserDefaults.standard
        let saved = (d.stringArray(forKey: "custom.lyricSourceOrder") ?? []).compactMap(LyricSourceID.init(rawValue:))
        var unique: [LyricSourceID] = []
        for item in saved + LyricSourceID.allCases where !unique.contains(item) { unique.append(item) }
        order = unique
        enabled = Set((d.stringArray(forKey: "custom.lyricSources") ?? LyricSourceID.allCases.filter { $0 != .lyrimuse }.map(\.rawValue))
            .compactMap(LyricSourceID.init(rawValue:)))
        mode = Mode(rawValue: d.string(forKey: "custom.lyricMatchMode") ?? "") ?? .smart
        repairQQGaps = d.object(forKey: "custom.repairQQGaps") as? Bool ?? true
    }
    func move(_ source: LyricSourceID, by amount: Int) {
        guard let index = order.firstIndex(of: source) else { return }
        let next = index + amount
        guard order.indices.contains(next) else { return }
        order.swapAt(index, next)
    }
}

struct LyricQuery {
    var title: String
    var artist: String
    var album: String?
    var duration: Double
    init(title: String, artist: String, album: String?, duration: Double) {
        self.title=title;self.artist=artist;self.album=album
        self.duration=duration.isFinite ? min(max(duration,0),864000) : 0
    }
    var recordingKey: String {
        [Self.normalize(title), Self.normalize(artist), Self.normalize(album ?? ""),
         String(Int(duration.rounded()))].joined(separator: "|")
    }
    static func normalize(_ text: String) -> String {
        text.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: .current)
            .components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }.joined(separator: " ")
    }
    static func similarity(_ lhs: String, _ rhs: String) -> Double {
        let a = normalize(lhs), b = normalize(rhs)
        guard !a.isEmpty, !b.isEmpty else { return 0 }
        if a == b { return 1 }
        let aa = Set(a.split(separator: " ").map(String.init))
        let bb = Set(b.split(separator: " ").map(String.init))
        let token = Double(aa.intersection(bb).count) / Double(max(aa.count, bb.count))
        let containment = (a.contains(b) || b.contains(a)) ? 0.85 : 0
        return max(token, containment)
    }
}

struct LyricCandidate: Identifiable {
    var source: LyricSourceID
    var result: LyricsFetcher.Result
    var title: String
    var artist: String
    var duration: Double?
    var score: Double
    var id: String { source.rawValue }
    var provenance: LyricsFetcher.Provenance {
        switch result { case .synced(_, let p), .plain(_, let p): return p }
    }
    var lineCount: Int {
        switch result { case .synced(let lines, _): return lines.count; case .plain(let lines, _): return lines.count }
    }
}

struct LyricLookupReport {
    var result: LyricsFetcher.Result?
    var candidates: [LyricCandidate] = []
    var states: [LyricSourceID: String] = [:]
    var pinned = false
    var cached = false
}

enum SavedLyrics {
    struct Entry: Codable {
        var result: LyricsFetcher.Result
        var storedAt: Date
    }
    private static var folder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Verso Custom/Lyrics", isDirectory: true)
    }
    private static func file(_ key: String, pin: Bool) -> URL {
        let hash = SHA256.hash(data: Data(key.utf8)).map { String(format: "%02x", $0) }.joined()
        return folder.appendingPathComponent((pin ? "pin-" : "cache-") + hash + ".json")
    }
    static func read(_ key: String, pin: Bool) -> LyricsFetcher.Result? {
        guard let data = try? Data(contentsOf: file(key, pin: pin)), data.count <= 2_000_000,
              let entry = try? JSONDecoder().decode(Entry.self, from: data),
              pin || Date().timeIntervalSince(entry.storedAt) < 14 * 86400 else { return nil }
        return entry.result
    }
    @discardableResult
    static func save(_ result: LyricsFetcher.Result, key: String, pin: Bool) -> Bool {
        do {
            let data = try JSONEncoder().encode(Entry(result: result, storedAt: Date()))
            guard data.count <= 2_000_000 else { return false }
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try data.write(to: file(key, pin: pin), options: .atomic)
            // Bound automatic cache growth. Explicit user selections are retained.
            let files = (try? FileManager.default.contentsOfDirectory(at: folder,
                includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
            let automatic = files.filter { $0.lastPathComponent.hasPrefix("cache-") }
            if automatic.count > 150 {
                let oldest = automatic.sorted {
                    let a = (try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
                    let b = (try? $1.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
                    return a < b
                }
                for url in oldest.prefix(automatic.count - 150) { try? FileManager.default.removeItem(at: url) }
            }
            return true
        } catch { return false }
    }
    static func unpin(_ key: String) { try? FileManager.default.removeItem(at: file(key, pin: true)) }
}

enum LyricHTTP {
    struct Failure: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }
    static func json(_ base: String, _ parameters: [String: String], referer: String? = nil) async throws -> [String: Any] {
        try Task.checkCancellation()
        var parts = URLComponents(string: base)
        parts?.queryItems = parameters.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        guard let url = parts?.url else { throw Failure(message: "Invalid service address") }
        var request = URLRequest(url: url, timeoutInterval: 6)
        request.setValue("Verso-Custom/0.2 (personal desktop lyrics)", forHTTPHeaderField: "User-Agent")
        if let referer { request.setValue(referer, forHTTPHeaderField: "Referer") }
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw Failure(message: "Service response: HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)")
        }
        guard data.count <= 2_000_000 else { throw Failure(message: "Response too large") }
        guard let value = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw Failure(message: "Unexpected response format")
        }
        return value
    }
    static func string(_ value: Any?) -> String? {
        if let string = value as? String { return string }
        if let number = value as? NSNumber { return number.stringValue }
        return nil
    }
    static func text(_ value: String) -> String {
        value.replacingOccurrences(of: "&amp;", with: "&").replacingOccurrences(of: "&apos;", with: "'")
            .replacingOccurrences(of: "&quot;", with: "\"").replacingOccurrences(of: "&#39;", with: "'")
            .replacingOccurrences(of: "&lt;", with: "<").replacingOccurrences(of: "&gt;", with: ">")
    }
}
