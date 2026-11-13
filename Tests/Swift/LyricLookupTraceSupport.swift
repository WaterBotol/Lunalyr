import Foundation
@testable import VersoCustom

/// Keeps every event of one lookup, timed from when it was made.
final class LyricLookupTraceRecorder: LyricLookupTracer, @unchecked Sendable {
    let began = Date()
    private let lock = NSLock()
    private var stored: [LyricTraceEvent] = []

    func record(_ event: LyricTraceEvent) {
        var event = event
        event.at = (Date().timeIntervalSince(began) * 1000).rounded() / 1000
        lock.withLock { stored.append(event) }
    }
    var events: [LyricTraceEvent] { lock.withLock { stored } }
    func events(_ kind: LyricTraceEvent.Kind) -> [LyricTraceEvent] { events.filter { $0.kind == kind } }
}

/// One song's trace as written to disk: the song, a per-source digest and
/// the full event list.
struct LyricLookupTraceDocument: Codable {
    struct Song: Codable {
        var title: String
        var artist: String
        var album: String?
        var duration: Double
        var player: String?
        var genre: String?
    }
    struct Pick: Codable {
        var source: String
        var provenance: String
        var timing: String
        var title: String
        var artist: String
        var duration: Double?
        var viaOriginalTitle: Bool
    }
    struct SourceDigest: Codable {
        var source: String
        /// The state the source list would show at the end.
        var state: String
        /// picked, accepted #n, rejected: …, none, timeout, stopped, error: …, not asked.
        var verdict: String
        var elapsedMs: Int?
        var variants: [String]
        var requests: [String]
        var candidate: LyricTraceCandidate?
    }
    var song: Song
    var storefront: String?
    var mode: String
    var sources: [String]
    var startedAt: String
    var elapsedMs: Int?
    var pick: Pick?
    var digest: [SourceDigest]
    var events: [LyricTraceEvent]

    init(song: Song, storefront: String?, events: [LyricTraceEvent], startedAt: Date) {
        self.song = song
        self.storefront = storefront
        self.events = events
        self.startedAt = ISO8601DateFormatter().string(from: startedAt)
        let lookup = events.first { $0.kind == .lookup }
        mode = lookup?.message ?? ""
        sources = lookup?.sources ?? []
        let final = events.last { $0.kind == .final }
        elapsedMs = final?.elapsedMs
        if let id = final?.source, let shown = final?.ranked?.first(where: { $0.source == id }) {
            pick = Pick(source: id, provenance: shown.provenance, timing: shown.timing, title: shown.title, artist: shown.artist,
                        duration: shown.duration, viaOriginalTitle: shown.viaOriginalTitle)
        }
        digest = sources.map { id in
            let mine = events.filter { $0.source == id }
            let state = final?.states?[id] ?? "not asked"
            let ranked = final?.ranked?.first { $0.source == id }
            let rejected = final?.rejected?.first { $0.source == id }
            let verdict: String
            if let ranked {
                verdict = final?.source == id ? "picked" : "accepted #\(ranked.rank ?? 0)"
            } else if let rejected {
                verdict = rejected.reason ?? "rejected"
            } else if state.hasPrefix(LyricsFetcher.notNeeded) || state == "Cancelled" {
                verdict = "stopped"
            } else if state == "Timed out" {
                verdict = "timeout"
            } else if state.hasPrefix("No usable match") || state.hasPrefix("No match") {
                verdict = "none"
            } else if state == "not asked" || state.hasPrefix("Not checked") {
                verdict = "not asked"
            } else {
                verdict = "error: " + state
            }
            var variants: [String] = []
            for event in mine where event.kind == .variant {
                let label = event.variant ?? "listed title"
                if !variants.contains(label) { variants.append(label) }
            }
            let requests = mine.compactMap(\.request).map { request -> String in
                var outcome = "?"
                if let status = request.status { outcome = String(status) } else if let error = request.error { outcome = error }
                return "\(outcome) \(request.ms) ms \(request.url)"
            }
            return SourceDigest(source: id, state: state, verdict: verdict,
                                elapsedMs: mine.last { $0.kind == .sourceEnd }?.elapsedMs, variants: variants,
                                requests: requests, candidate: ranked ?? rejected)
        }
    }

    /// A short cell for the summary table.
    static func cell(_ digest: SourceDigest) -> String {
        let timing = digest.candidate.map { " " + $0.timing } ?? ""
        let via = digest.candidate?.viaOriginalTitle == true ? " (orig. title)" : ""
        switch digest.verdict {
        case "picked": return "**PICK**" + timing + via
        case let v where v.hasPrefix("accepted"): return v.replacingOccurrences(of: "accepted ", with: "") + timing + via
        case let v where v.hasPrefix("Rejected: "): return "rej " + v.dropFirst("Rejected: ".count) + timing
        default: return digest.verdict
        }
    }
}

extension JSONEncoder {
    static var trace: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return encoder
    }
}
