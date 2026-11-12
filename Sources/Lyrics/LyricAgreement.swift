import Foundation

/// What the other sources say about a match: how many give the same words,
/// and, for LRCLIB's word timing, whether its timing is the odd one out.
///
/// Sources copy one another, so agreement is not proof; it is used only to
/// break ties among matches that rank level otherwise (Accuracy first), and
/// to take away LRCLIB's lead when its community-made timing disagrees with
/// two other services that agree with each other.
struct LyricAgreement: Equatable {
    /// Other sources whose words match these (capped at 2 when ranking).
    var peers = 0
    /// Other timed sources with the same words that this one's timing was
    /// compared with.
    var timedCompared = 0
    /// Its timing disagrees with every other timed source with the same
    /// words, while two of those agree with each other.
    var timingOutlier = false
}

extension LyricsFetcher {
    /// Which upstream a candidate's lyrics come from, for agreement: Music's
    /// own cache and Lyrics+ relaying Apple Music's lyrics are one source
    /// (the relay is a copy), so neither is the other's peer and the two
    /// never make up the pair that agrees against LRCLIB.
    static func agreementFamily(_ candidate: LyricCandidate) -> String {
        if candidate.source == .appleMusicCache || isAppleRelay(candidate) { return "apple" }
        return candidate.source.rawValue
    }

    /// Fills in `agreement` on each candidate from the others in `candidates`.
    static func weighAgreement(_ candidates: [LyricCandidate]) -> [LyricCandidate] {
        let texts = candidates.map(agreementText)
        let timings = candidates.map(agreementTiming)
        let families = candidates.map(agreementFamily)
        var result = candidates
        for i in candidates.indices {
            var agreement = LyricAgreement()
            var sameWordsTimed: [Int] = []
            for j in candidates.indices where j != i && families[j] != families[i] {
                guard sameWords(texts[i], texts[j]) else { continue }
                agreement.peers += 1
                if !timings[i].isEmpty, !timings[j].isEmpty { sameWordsTimed.append(j) }
            }
            agreement.timedCompared = sameWordsTimed.count
            if sameWordsTimed.count >= 2,
               !sameWordsTimed.contains(where: { timingAgrees(timings[i], timings[$0]) }) {
                agreement.timingOutlier = sameWordsTimed.enumerated().contains { index, a in
                    sameWordsTimed.dropFirst(index + 1).contains { families[a] != families[$0] && timingAgrees(timings[a], timings[$0]) }
                }
            }
            result[i].agreement = agreement
        }
        return result
    }

    /// Sung lines, normalised for comparison.
    static func agreementText(_ candidate: LyricCandidate) -> [String] {
        let raw: [String]
        switch candidate.result {
        case .synced(let lines, _): raw = lines.filter { !$0.isInstrumental }.map(\.text)
        case .plain(let lines, _): raw = lines
        }
        return raw.map(LyricQuery.normalize).filter { !$0.isEmpty }
    }

    /// Sung lines with their start time; empty for plain text.
    static func agreementTiming(_ candidate: LyricCandidate) -> [(text: String, start: Double)] {
        guard case .synced(let lines, _) = candidate.result else { return [] }
        return lines.filter { !$0.isInstrumental }
            .map { (LyricQuery.normalize($0.text), $0.start) }.filter { !$0.0.isEmpty }
    }

    /// At least 60% of the lines of the longer text appear in the other, and
    /// enough lines to say anything (four).
    static func sameWords(_ a: [String], _ b: [String]) -> Bool {
        guard min(a.count, b.count) >= 4 else { return false }
        var pool: [String: Int] = [:]
        for line in b { pool[line, default: 0] += 1 }
        var shared = 0
        for line in a where (pool[line] ?? 0) > 0 { shared += 1; pool[line]! -= 1 }
        return Double(shared) / Double(max(a.count, b.count)) >= 0.6
    }

    /// Lines with the same words, paired in order, start within 0.75 s of
    /// each other at the median, over at least four pairs.
    static func timingAgrees(_ a: [(text: String, start: Double)], _ b: [(text: String, start: Double)]) -> Bool {
        var gaps: [Double] = [], from = 0
        for line in a {
            guard let match = b[from...].firstIndex(where: { $0.text == line.text }) else { continue }
            gaps.append(abs(b[match].start - line.start))
            from = match + 1
            if from >= b.count { break }
        }
        guard gaps.count >= 4 else { return false }
        return gaps.sorted()[gaps.count / 2] <= 0.75
    }
}
