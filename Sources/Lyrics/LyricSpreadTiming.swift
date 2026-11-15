import Foundation

/// Word timing that a tool spread over each line rather than timing to the
/// singing: each word's share of the line equal, or in proportion to its
/// letters (or syllables). It looks word-timed but carries no more than the
/// line's start and end, so it ranks as line timing (`rankedTiming`).
///
/// Thresholds from real files (16 Nov 2026: Apple TTML via Lyrics+, QQ QRC,
/// Kugou KRC, NetEase YRC, Kuwo, AMLL, LRCLIB, PetitLyrics, Deezer, Migu
/// and Soda for eight songs in English, Chinese, Japanese and Korean):
/// a line counts as spread when the gaps between its word starts fit one
/// of those models within 25 ms (or 3% of the average gap), stamps being
/// centiseconds at best. Genuine files had at most 32% of lines that fit
/// by chance (PetitLyrics, which times short lines; 0–18% for the rest),
/// while the same files with words spread evenly or by length had 46–100%
/// (100% once lines of three words are left out). So a file is spread when
/// at least 60% of at least six judged lines are.
extension LyricsFetcher {
    static let spreadTolerance = 0.025
    static let spreadRelativeTolerance = 0.03
    static let spreadLineShare = 0.6
    static let spreadMinLines = 6

    /// Whether a line's word starts were spread by a model rather than timed,
    /// or nil when the line cannot tell (line timing, or fewer than four
    /// words). The last word is left out: its end is where the line ends.
    static func isEvenlySpread(_ line: Line) -> Bool? {
        guard line.wordTimed, line.words.count >= 4, validWordTiming(line) else { return nil }
        let words = line.words
        let gaps = zip(words, words.dropFirst()).map { $1.start - $0.start }
        let total = gaps.reduce(0, +)
        guard total > 0, gaps.allSatisfy({ $0 >= 0 }) else { return nil }
        let mean = total / Double(gaps.count)
        let tolerance = max(spreadTolerance, spreadRelativeTolerance * mean)
        func rms(_ residuals: [Double]) -> Double { (residuals.map { $0 * $0 }.reduce(0, +) / Double(residuals.count)).squareRoot() }
        // Equal shares.
        if rms(gaps.map { $0 - mean }) <= tolerance { return true }
        let letters = words.dropLast().map { Double(max(1, $0.text.filter { $0.isLetter || $0.isNumber }.count)) }
        // In proportion to letters plus a constant, as tools weigh words.
        for pad in [0, 0.5, 1, 1.5, 2, 3] {
            let weights = letters.map { $0 + pad }
            let scale = total / weights.reduce(0, +)
            if rms(zip(gaps, weights).map { $0 - scale * $1 }) <= tolerance { return true }
        }
        // A straight-line fit to letters or syllables needs a fifth word to mean anything.
        guard gaps.count >= 4 else { return false }
        for measure in [letters, words.dropLast().map { Double(syllables($0.text)) }] {
            if let residual = linearResidual(measure, gaps), residual <= tolerance { return true }
        }
        return false
    }

    /// Whether most of a file's judged lines are spread (`isEvenlySpread`).
    static func hasEvenlySpreadWordTiming(_ lines: [Line]) -> Bool {
        var judged = 0, spread = 0
        for line in lines where !line.isInstrumental {
            guard let verdict = isEvenlySpread(line) else { continue }
            judged += 1
            if verdict { spread += 1 }
        }
        return judged >= spreadMinLines && Double(spread) >= spreadLineShare * Double(judged)
    }
    static func hasEvenlySpreadWordTiming(_ result: Result) -> Bool {
        guard case .synced(let lines, _) = result else { return false }
        return hasEvenlySpreadWordTiming(lines)
    }

    /// Vowel groups in a Latin word; one a character in other scripts.
    private static func syllables(_ word: String) -> Int {
        let letters = word.lowercased().filter { $0.isLetter || $0.isNumber }
        guard letters.allSatisfy({ $0.isASCII }) else { return max(1, letters.count) }
        var groups = 0, inVowel = false
        for character in letters {
            let vowel = "aeiouy".contains(character)
            if vowel && !inVowel { groups += 1 }
            inVowel = vowel
        }
        return max(1, groups)
    }

    /// The root-mean-square residual of a least-squares line through (x, y),
    /// or nil when x does not vary.
    private static func linearResidual(_ x: [Double], _ y: [Double]) -> Double? {
        let n = Double(x.count), meanX = x.reduce(0, +) / n, meanY = y.reduce(0, +) / n
        let variance = x.map { ($0 - meanX) * ($0 - meanX) }.reduce(0, +)
        guard variance > 0 else { return nil }
        let slope = zip(x, y).map { ($0 - meanX) * ($1 - meanY) }.reduce(0, +) / variance
        let intercept = meanY - slope * meanX
        return (zip(x, y).map { pow($1 - (slope * $0 + intercept), 2) }.reduce(0, +) / n).squareRoot()
    }
}
