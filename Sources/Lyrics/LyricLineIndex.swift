import Foundation

/// Prepared once for a lyric result; seeks and repeated timestamps use the
/// same last-start-at-or-before rule as the original forward scan.
struct LyricLineIndex {
    private let starts: [Double]
    private let ordered: Bool
    init(lines: [LyricsFetcher.Line]) {
        let values = lines.map(\.start)
        starts = values
        ordered = values.allSatisfy { $0.isFinite } && zip(values, values.dropFirst()).allSatisfy { pair in pair.0 <= pair.1 }
    }
    func line(at time: Double) -> Int? {
        // Retain the old early-stop rule for malformed imported timelines.
        guard ordered else {
            var found: Int?
            for (index, start) in starts.enumerated() {
                if start <= time { found = index } else { break }
            }
            return found
        }
        var low = 0, high = starts.count
        while low < high {
            let middle = low + (high - low) / 2
            if starts[middle] <= time { low = middle + 1 } else { high = middle }
        }
        return low == 0 ? nil : low - 1
    }
    /// Some mixed files carry a line-timed copy just ahead of a word-timed
    /// cue. Resolve that occurrence before either version can reach the stage.
    static func removingMixedTimingCopies(_ lines: [LyricsFetcher.Line]) -> [LyricsFetcher.Line] {
        guard lines.contains(where: \.wordTimed),
              lines.contains(where: { !$0.wordTimed && !$0.isInstrumental }) else { return lines }
        var result = lines
        var copies = Set<Int>()
        for (index, timed) in lines.enumerated() where timed.wordTimed {
            let key = timingKey(timed.text)
            let main = timingKey(timed.text.replacingOccurrences(
                of: #"^\s*[（(][^）)]*[）)]\s+|\s+[（(][^）)]*[）)]\s*$"#, with: "", options: .regularExpression))
            let neighbours = [index - 1, index + 1]
            let matching = neighbours.filter { candidate in
                guard lines.indices.contains(candidate), !copies.contains(candidate) else { return false }
                let fallback = lines[candidate]
                guard !fallback.wordTimed, !fallback.isInstrumental else { return false }
                let other = timingKey(fallback.text)
                if (other == key || (!main.isEmpty && other == main)) && isTimingCopy(fallback, of: timed) { return true }
                // A near copy: the line-timed version sits just ahead of the
                // word-timed one (sources are synced a little apart) and ends
                // by the time it starts.
                guard fallback.endFromNextLine == true else { return false }
                if candidate > index {
                    // A copy after it: the line-timed version starts while the
                    // word-timed line is still being sung (or as it ends), often
                    // as one half of a line the word timing keeps whole.
                    let vocalEnd = timed.words.lazy.map(\.end).max() ?? timed.end
                    return sameLyric(fallback.text, timed.text, minimumShare: 0.25)
                        && fallback.start >= timed.start - 0.3 && fallback.start <= vocalEnd + 0.15
                }
                return sameLyric(fallback.text, timed.text)
                    && abs(fallback.start - timed.start) <= 2.0 && fallback.end <= timed.start + 0.6
            }
            guard let duplicate = matching.min(by: {
                abs(lines[$0].start - timed.start) < abs(lines[$1].start - timed.start)
            }) else { continue }
            copies.insert(duplicate)
            if result[index].romanization == nil { result[index].romanization = lines[duplicate].romanization }
            if result[index].translation == nil { result[index].translation = lines[duplicate].translation }
        }
        guard !copies.isEmpty else { return lines }
        let keep = lines.indices.filter { !copies.contains($0) }
        for (index, next) in zip(keep, keep.dropFirst()) where next > index + 1 && lines[index].endFromNextLine == true {
            let start = lines[index].start, end = lines[next].start
            guard end > start, abs(lines[index].end - lines[index + 1].start) < 0.05 else { continue }
            result[index].end = max(lines[index].end, end)
            if !result[index].wordTimed, !result[index].isInstrumental {
                result[index].words = LyricsFetcher.distributeWords(in: lines[index].text, from: start, to: end)
            }
        }
        return keep.map { result[$0] }
    }

    /// The same words, give or take punctuation, a dropped "yeah" or a split
    /// phrase: "Dear Slim, I wrote you, but" / "Dear Slim, I wrote you but",
    /// "Give me yo, give me yo," / "give me yo".
    static func sameLyric(_ a: String, _ b: String, minimumShare: Double = 0.4) -> Bool {
        let x = timingKey(a), y = timingKey(b)
        if x == y { return true }
        let (short, long) = x.count <= y.count ? (x, y) : (y, x)
        if short.count >= 8, long.contains(short), Double(short.count) / Double(long.count) >= minimumShare { return true }
        return LyricQuery.similarity(a, b) >= 0.8
    }

    private static func timingKey(_ text: String) -> String {
        let key = LyricQuery.normalize(text).filter { !$0.isWhitespace }
        return key.isEmpty ? text : key
    }

    private static func isTimingCopy(_ fallback: LyricsFetcher.Line, of timed: LyricsFetcher.Line) -> Bool {
        let vocalEnd = timed.words.lazy.map(\.end).max() ?? timed.end
        let multipleSyllables = fallback.words.count > 1 || LyricScript.keepsSyllables(fallback.text)
        return ll_mixed_timing_copy(fallback.start, fallback.end, fallback.endFromNextLine == true ? 1 : 0,
                                    multipleSyllables ? 1 : 0, timed.start, vocalEnd) != 0
    }

    /// The line before `index` while it is still being sung at `time`: duet
    /// parts and ad-libs can overlap the line that has become active. Only
    /// supplied word timing says a line is still sounding, so line-only
    /// lyrics, whose lines end where the next begins, never overlap.
    static func heldLine(before index: Int, in lines: [LyricsFetcher.Line], at time: Double) -> Int? {
        let previous = index - 1
        guard let end = heldEnd(after: previous, in: lines), time >= lines[previous].start, time < end else { return nil }
        return previous
    }

    /// When line `row`'s last word ends, if it is still being sung after
    /// the next line starts (see `heldLine`); nil when it is not.
    ///
    /// Only a real overlap counts: a word of the line that comes once the
    /// next line has started (a duet part answering, an ad-lib), sung for at
    /// least `answerOverlap`, or a note held at least `heldOverlap` into the
    /// next line. A last syllable ringing on for a few tenths of a second --
    /// one line change in ten in a sample of Apple Music's TTML -- is an
    /// ordinary line change, and so are timings that differ only by
    /// rounding (YRC sums give 208.42000000000002 for 208.42).
    static func heldEnd(after row: Int, in lines: [LyricsFetcher.Line]) -> Double? {
        guard lines.indices.contains(row), lines.indices.contains(row + 1) else { return nil }
        let line = lines[row], next = lines[row + 1].start
        guard line.wordTimed, !line.isInstrumental, let end = line.words.map(\.end).max() else { return nil }
        let overlap = end - next
        if overlap >= heldOverlap { return end }
        guard overlap >= answerOverlap else { return nil }
        let answers = line.words.contains { word in
            word.start >= next - answerLead && word.text.contains { $0.isLetter || $0.isNumber }
        }
        return answers ? end : nil
    }

    /// How long a note must ring on into the next line for the line to stay.
    static let heldOverlap = 0.5
    /// How long a line's words must go on into the next line when one of
    /// them only starts as the next line does.
    static let answerOverlap = 0.2
    /// A word starting this close before the next line, or after it, is sung
    /// over it: an ordinary hand-over would take the line away as it begins.
    static let answerLead = 0.1
}
