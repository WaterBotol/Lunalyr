import Foundation

/// The best text with the best timing. When the lyrics that lead on their
/// source (Music's own, Apple Music's via Lyrics+, Musixmatch) are timed by
/// the line only, and another match for the same recording, timed alike,
/// has genuine word timing, the leading text takes those word times: words
/// are paired line by line (case, punctuation, small spelling differences
/// and ad-libs tolerated) and a line takes them only when nearly all its
/// words pair and their times fit the line. Other lines stay line-timed, so
/// the result is word-by-word or mixed timing, and it ranks as the text's
/// source at that timing. The provenance names both
/// ("Musixmatch text · QQ Music word timing"). Lyrics in another script or
/// language, a translation, another version, a shifted or faulty timeline
/// and word stamps spread by a tool never lend timing.
extension LyricsFetcher {
    static let mergedTextMarker = " text · "
    static let mergedTimingSuffix = " word timing"
    /// How far the two matches' line starts may differ at the median.
    static let mergeLineStartTolerance = 0.3
    /// How far a line's word times may run past the line before it keeps its line timing.
    static let mergeLineSlack = 0.3
    /// The share of a line's words that must pair, and how many may not.
    static let mergeWordShare = 0.85
    static let mergeUnpairedWords = 2
    /// The share of sung lines that must take word timing for a merge to be kept.
    static let mergeLineShare = 0.5

    static func mergedProvenanceSource(text: String, timing: String) -> String {
        text + mergedTextMarker + timing + mergedTimingSuffix
    }
    /// The source of a provenance's text: itself, or the text's in a merge.
    static func textSource(ofProvenance source: String) -> String {
        guard source.hasSuffix(mergedTimingSuffix), let marker = source.range(of: mergedTextMarker, options: .backwards) else { return source }
        return String(source[..<marker.lowerBound])
    }
    /// Which source lent a merge its word timing, or nil for lyrics that are not a merge.
    static func timingDonor(ofProvenance source: String) -> String? {
        guard source.hasSuffix(mergedTimingSuffix), let marker = source.range(of: mergedTextMarker, options: .backwards) else { return nil }
        return String(source[marker.upperBound...].dropLast(mergedTimingSuffix.count))
    }
    static func timingDonor(_ candidate: LyricCandidate) -> String? {
        guard case .synced(_, let p) = candidate.result else { return nil }
        return timingDonor(ofProvenance: p.source)
    }

    // MARK: - Choosing what to merge

    /// `accepted` with the best line-timed match of a leading source given
    /// word timing from another match, when the merge would outrank every
    /// word-timed match in hand. The merge replaces that match.
    static func mergingTiming(_ accepted: [LyricCandidate], query: LyricQuery, options: LyricSourceOptions.Snapshot) -> [LyricCandidate] {
        let donors = accepted.filter { isTimingDonor($0, query: query) }
        guard let bestDonor = choose(donors, query: query, options: options) else { return accepted }
        // A donor ranked higher is tried first, so the choice does not depend on which answered first.
        let rankedDonors = donors.sorted { outranks($0, $1, query: query, options: options) }
        let recipients = accepted.filter { isMergeRecipient($0, query: query) }.sorted { outranks($0, $1, query: query, options: options) }
        for recipient in recipients {
            guard case .synced(let lines, let provenance) = recipient.result else { continue }
            var best: (lines: [Line], donor: LyricCandidate, count: Int)?
            for donor in rankedDonors where donor.source != recipient.source && sameScript(recipient, donor) {
                guard case .synced(let donorLines, _) = donor.result,
                      let offset = timelineOffset(agreementTiming(recipient), agreementTiming(donor)),
                      abs(offset.median) <= mergeLineStartTolerance,
                      let merged = mergeWordTiming(into: lines, from: donorLines) else { continue }
                let count = merged.filter(\.wordTimed).count
                if count > (best?.count ?? 0) { best = (merged, donor, count) }
            }
            guard let best, case .synced(_, let donorProvenance) = best.donor.result else { continue }
            var merged = recipient
            merged.result = .synced(best.lines, Provenance(precision: precision(best.lines),
                                                           source: mergedProvenanceSource(text: provenance.source, timing: donorProvenance.source)))
            guard outranks(merged, bestDonor, query: query, options: options) else { continue }
            return accepted.map { $0.source == recipient.source ? merged : $0 }
        }
        return accepted
    }

    /// Genuine word timing for the playing version, sound and in step with the others.
    static func isTimingDonor(_ candidate: LyricCandidate, query: LyricQuery) -> Bool {
        guard case .synced = candidate.result, timingDonor(candidate) == nil, fitsVersion(candidate, query),
              candidate.agreement.shift == nil, !(candidate.source == .lrclib && candidate.agreement.timingOutlier) else { return false }
        return rankedTiming(candidate.result) == 2 && !hasTimingFault(candidate.result)
    }
    /// Line-timed lyrics from a source that leads line timing
    /// (Music's own, Apple Music's via Lyrics+, Musixmatch), for the playing version.
    static func isMergeRecipient(_ candidate: LyricCandidate, query: LyricQuery) -> Bool {
        guard case .synced = candidate.result, timingDonor(candidate) == nil, fitsVersion(candidate, query),
              candidate.agreement.shift == nil, rankedTiming(candidate.result) == 1, !hasTimingFault(candidate.result) else { return false }
        return lead(candidate, timing: 1) < 4
    }
    private static func sameScript(_ a: LyricCandidate, _ b: LyricCandidate) -> Bool {
        guard !a.languageMismatch, !b.languageMismatch else { return false }
        let first = LyricLanguage.script(of: a), second = LyricLanguage.script(of: b)
        return first != nil && first == second
    }

    // MARK: - Pairing words

    /// A piece of a line that takes one time: a word in spaced script, a
    /// character in unspaced script. `text` keeps the punctuation around it
    /// (brackets included, for backing vocals), so the pieces cover the line.
    struct MergeUnit: Equatable {
        var text: String
        /// Lower-cased letters and numbers, without accents.
        var key: String
    }
    static func mergeUnits(_ text: String) -> [MergeUnit] {
        var units: [MergeUnit] = [], prefix = ""
        for chunk in text.split(whereSeparator: \.isWhitespace) {
            var current = "", afterUnspaced = false
            func flush() {
                guard !mergeKey(current).isEmpty else { return }
                units.append(MergeUnit(text: prefix + current, key: mergeKey(current)))
                prefix = ""; current = ""
            }
            for character in chunk {
                if character.unicodeScalars.contains(where: isUnspacedScalar) {
                    flush()
                    current = String(character)
                    flush()
                    afterUnspaced = true
                } else if character.isLetter || character.isNumber {
                    current += String(character)
                    afterUnspaced = false
                } else if openingPunctuation.contains(character) {
                    // An opening bracket or quote leads the word after it.
                    flush()
                    prefix += current + String(character)
                    current = ""
                } else if mergeKey(current).isEmpty, afterUnspaced, !units.isEmpty {
                    // Punctuation after a character in unspaced script stays with it.
                    units[units.count - 1].text += String(character)
                } else if mergeKey(current).isEmpty {
                    prefix += current + String(character)
                    current = ""
                } else {
                    current += String(character)
                }
            }
            flush()
            prefix += current
        }
        // Punctuation with no word after it joins the last word.
        if !prefix.isEmpty, !units.isEmpty { units[units.count - 1].text += prefix }
        return units
    }
    private static let openingPunctuation: Set<Character> = ["(", "[", "{", "（", "［", "「", "『", "“", "‘", "¿", "¡", "<", "《", "〈"]
    static func mergeKey(_ text: String) -> String {
        text.folding(options: [.diacriticInsensitive, .caseInsensitive, .widthInsensitive], locale: nil)
            .filter { $0.isLetter || $0.isNumber }
    }
    private static func isUnspacedScalar(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x0E00...0x0EFF, 0x1000...0x109F, 0x1780...0x17FF, 0x3040...0x30FF, 0x31F0...0x31FF,
             0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF, 0x20000...0x2FA1F: return true
        default: return false
        }
    }
    /// Whether two pieces are the same word: equal, or long enough and
    /// spelt nearly alike: five letters or more, one in five different at
    /// most ("colour"/"color", "thinkin"/"thinking").
    static func sameMergeWord(_ a: String, _ b: String) -> Bool {
        if a == b { return true }
        guard min(a.count, b.count) >= 5 else { return false }
        let x = Array(a), y = Array(b)
        var row = Array(0...y.count)
        for i in 1...x.count {
            var previous = row[0]
            row[0] = i
            for j in 1...y.count {
                let saved = row[j]
                row[j] = min(row[j] + 1, row[j - 1] + 1, previous + (x[i - 1] == y[j - 1] ? 0 : 1))
                previous = saved
            }
        }
        return Double(row[y.count]) <= 0.2 * Double(max(x.count, y.count))
    }

    /// A donor's pieces with the times of the words that sing them.
    private struct TimedUnit { var key: String; var start: Double; var end: Double }
    private static func timedUnits(_ line: Line) -> [TimedUnit] {
        // Each letter of the line belongs to one of the source's timed words.
        var owner: [Int] = []
        for (index, word) in line.words.enumerated() {
            owner += Array(repeating: index, count: mergeKey(word.text).count)
        }
        var units: [TimedUnit] = [], cursor = 0
        var spans: [(first: Int, last: Int)] = []
        for unit in mergeUnits(line.text) {
            let count = unit.key.count
            guard cursor + count <= owner.count else { return [] }
            spans.append((owner[cursor], owner[cursor + count - 1]))
            units.append(TimedUnit(key: unit.key, start: line.words[owner[cursor]].start, end: line.words[owner[cursor + count - 1]].end))
            cursor += count
        }
        guard cursor == owner.count else { return [] }
        // Pieces sung as one timed word share its time in equal parts.
        var index = 0
        while index < units.count {
            var last = index
            while last + 1 < units.count, spans[last + 1] == spans[index] { last += 1 }
            if last > index {
                let start = units[index].start, step = (units[last].end - start) / Double(last - index + 1)
                for k in index...last {
                    units[k].start = start + step * Double(k - index)
                    units[k].end = start + step * Double(k - index + 1)
                }
            }
            index = last + 1
        }
        return units
    }

    /// `recipient` with word timing from `donor` on each line whose words
    /// nearly all pair with words the donor sings around the same time, or
    /// nil when too few lines do (`mergeLineShare`).
    static func mergeWordTiming(into recipient: [Line], from donor: [Line]) -> [Line]? {
        let pool: [TimedUnit] = donor.filter { !$0.isInstrumental && validWordTiming($0) }.flatMap(timedUnits)
        guard !pool.isEmpty else { return nil }
        var merged = recipient, transferred = 0
        let sung = recipient.indices.filter { !recipient[$0].isInstrumental && recipient[$0].credit != true }
        for index in sung {
            if let words = transfer(recipient[index], pool: pool) {
                merged[index].words = words
                merged[index].wordTimed = true
                if validWordTiming(merged[index]) { transferred += 1; continue }
                merged[index] = recipient[index]
            }
        }
        guard !sung.isEmpty, Double(transferred) >= mergeLineShare * Double(sung.count),
              hasWordTimingEvidence(merged.filter { !$0.isInstrumental }) else { return nil }
        return merged
    }

    /// Word times for one line from the donor's pieces sung within it.
    private static func transfer(_ line: Line, pool: [TimedUnit]) -> [Word]? {
        let units = mergeUnits(line.text)
        guard !units.isEmpty, line.end > line.start else { return nil }
        let window = pool.filter { $0.start >= line.start - mergeLineSlack && $0.start < line.end }
        guard !window.isEmpty else { return nil }
        // Longest common subsequence of the pieces, in order.
        let n = units.count, m = window.count
        var table = Array(repeating: Array(repeating: 0, count: m + 1), count: n + 1)
        for i in stride(from: n - 1, through: 0, by: -1) {
            for j in stride(from: m - 1, through: 0, by: -1) {
                table[i][j] = sameMergeWord(units[i].key, window[j].key) ? table[i + 1][j + 1] + 1 : max(table[i + 1][j], table[i][j + 1])
            }
        }
        var pairs: [Int: Int] = [:], i = 0, j = 0
        while i < n, j < m {
            if sameMergeWord(units[i].key, window[j].key), table[i][j] == table[i + 1][j + 1] + 1 { pairs[i] = j; i += 1; j += 1 }
            else if table[i + 1][j] >= table[i][j + 1] { i += 1 } else { j += 1 }
        }
        let unpaired = n - pairs.count
        guard Double(pairs.count) >= mergeWordShare * Double(n), unpaired <= mergeUnpairedWords,
              pairs[0] != nil, pairs[n - 1] != nil else { return nil }
        var starts = Array(repeating: 0.0, count: n), ends = Array(repeating: 0.0, count: n)
        for (unit, match) in pairs { starts[unit] = window[match].start; ends[unit] = window[match].end }
        // An unpaired word (an ad-lib one source leaves out) shares the time
        // from the paired word before it to the next one.
        var k = 0
        while k < n {
            guard pairs[k] == nil else { k += 1; continue }
            var last = k
            while pairs[last + 1] == nil { last += 1 }
            let from = starts[k - 1], to = starts[last + 1], step = (to - from) / Double(last - k + 2)
            for (offset, index) in ((k - 1)...last).enumerated() {
                starts[index] = from + step * Double(offset)
                ends[index] = from + step * Double(offset + 1)
            }
            k = last + 1
        }
        guard starts[0] >= line.start - mergeLineSlack, ends[n - 1] <= line.end + mergeLineSlack else { return nil }
        var words: [Word] = []
        for index in 0..<n {
            let start = min(max(starts[index], line.start), line.end)
            let end = min(max(ends[index], start), line.end)
            guard end > start, index == 0 || start >= words[index - 1].start else { return nil }
            words.append(Word(text: units[index].text, start: start, end: end))
        }
        return words
    }
}
