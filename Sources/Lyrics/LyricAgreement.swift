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
    /// How far its lines run from the timing the others agree on, in
    /// seconds (positive: later), when it is timed to another recording or
    /// master (`LyricsFetcher.timelineShift`); nil otherwise.
    var shift: Double? = nil
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
            agreement.shift = timelineShift(i, timings: timings, texts: texts, families: families, candidates: candidates)
            result[i].agreement = agreement
        }
        return result
    }

    /// The least offset, in seconds, that counts as a timeline shifted to
    /// another recording or master: more than a lyric's sync slips.
    static let timelineShiftMinimum = 1.5
    /// How closely a shifted timeline's lines keep to its own offset: at
    /// least `timelineShiftShare` of them within this many seconds of it.
    static let timelineShiftSpread = 0.5
    static let timelineShiftShare = 0.8
    /// Lines paired by their words needed to judge an offset.
    static let timelineShiftMinPairs = 6

    /// How `b`'s sung lines run against `a`'s: the median of their start
    /// differences (b minus a) over lines with the same words, paired in
    /// order, and whether nearly all of them keep to it
    /// (`timelineShiftSpread`). Nil with fewer than
    /// `timelineShiftMinPairs` pairs.
    static func timelineOffset(_ a: [(text: String, start: Double)],
                               _ b: [(text: String, start: Double)]) -> (median: Double, steady: Bool)? {
        var gaps: [Double] = [], from = 0
        for line in a {
            guard from < b.count, let match = b[from...].firstIndex(where: { $0.text == line.text }) else { continue }
            gaps.append(b[match].start - line.start)
            from = match + 1
        }
        guard gaps.count >= timelineShiftMinPairs else { return nil }
        let median = gaps.sorted()[gaps.count / 2]
        let near = gaps.filter { abs($0 - median) <= timelineShiftSpread }.count
        return (median, Double(near) >= timelineShiftShare * Double(gaps.count))
    }

    /// Whether `b` runs steadily `timelineShiftMinimum` or more from `a`.
    static func isShifted(_ a: [(text: String, start: Double)], _ b: [(text: String, start: Double)]) -> Double? {
        guard let offset = timelineOffset(a, b), offset.steady, abs(offset.median) >= timelineShiftMinimum else { return nil }
        return offset.median
    }

    /// The offset of candidate `i`'s timeline when it is timed to another
    /// recording or master, or nil. Music's own cache is the recording
    /// playing, so a timeline steadily off from its timing is shifted, and
    /// it never is itself. Otherwise it takes two timed matches from other
    /// services that agree with each other (`timingAgrees`), with the same
    /// words, both steadily off from `i` by about the same offset, while no
    /// other service's timing agrees with `i`'s: of two that disagree there
    /// is no telling which is right.
    static func timelineShift(_ i: Int, timings: [[(text: String, start: Double)]], texts: [[String]], families: [String],
                              candidates: [LyricCandidate]) -> Double? {
        guard candidates[i].source != .appleMusicCache, !timings[i].isEmpty else { return nil }
        let others = candidates.indices.filter {
            $0 != i && families[$0] != families[i] && !timings[$0].isEmpty && sameWords(texts[i], texts[$0])
        }
        if let music = others.first(where: { candidates[$0].source == .appleMusicCache }) {
            return isShifted(timings[music], timings[i])
        }
        guard !others.contains(where: { timingAgrees(timings[i], timings[$0]) }) else { return nil }
        let shifted = others.compactMap { j in isShifted(timings[j], timings[i]).map { (j, $0) } }
        for (index, a) in shifted.enumerated() {
            for b in shifted.dropFirst(index + 1) where families[a.0] != families[b.0]
                && abs(a.1 - b.1) <= timelineShiftSpread && timingAgrees(timings[a.0], timings[b.0]) {
                return (a.1 + b.1) / 2
            }
        }
        return nil
    }

    /// Sung lines, normalised for comparison. Credits and the song's own
    /// heading are left out, whether or not the source's parser set them
    /// apart (`LyricCredits`).
    static func agreementText(_ candidate: LyricCandidate) -> [String] {
        let raw: [String]
        switch candidate.result {
        case .synced(let lines, _): raw = sungLines(lines, candidate).map(\.text)
        case .plain(let lines, _): raw = LyricCredits.droppingLeadingCredits(lines, title: candidate.title, artist: candidate.artist)
        }
        return raw.map(LyricQuery.normalize).filter { !$0.isEmpty }
    }

    /// Sung lines with their start time; empty for plain text.
    static func agreementTiming(_ candidate: LyricCandidate) -> [(text: String, start: Double)] {
        guard case .synced(let lines, _) = candidate.result else { return [] }
        return sungLines(lines, candidate)
            .map { (LyricQuery.normalize($0.text), $0.start) }.filter { !$0.0.isEmpty }
    }

    private static func sungLines(_ lines: [Line], _ candidate: LyricCandidate) -> [Line] {
        LyricCredits.flagging(lines, title: candidate.title, artist: candidate.artist).filter { !$0.isInstrumental }
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
