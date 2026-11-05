import Foundation
import NaturalLanguage

/// Timed tokens grouped into the words a reader actually sees.
///
/// TTML, YRC and word-tagged LRC time every span: a syllable ("beau", "ti",
/// "ful") or a single CJK character is a token of its own. Setting each one
/// as a separate scattered word turned 沈むように into five chips a character
/// apart. A run is a stretch of tokens with nothing but non-space characters
/// between them in the line's own text; it is set as one shaped word, while
/// every token keeps its own timing for the karaoke fill.
enum LyricRuns {
    /// For each token, whether it continues the previous token's run.
    /// Tokens that cannot be found in `text` stand alone.
    static func glue(_ words: [LyricsFetcher.Word], in text: String) -> [Bool] {
        var result = [Bool](repeating: false, count: words.count)
        var cursor = text.startIndex
        var previousOpen = false
        for (index, word) in words.enumerated() {
            let token = word.text.trimmingCharacters(in: .whitespacesAndNewlines)
            let leading = word.text.first?.isWhitespace ?? false
            let trailing = word.text.last?.isWhitespace ?? false
            guard !token.isEmpty else { previousOpen = false; continue }
            var scan = cursor, spaced = false
            while scan < text.endIndex, text[scan].isWhitespace {
                spaced = true
                scan = text.index(after: scan)
            }
            var found: Range<String.Index>?
            if text[scan...].hasPrefix(token) {
                found = scan..<text.index(scan, offsetBy: token.count)
            } else if let range = text.range(of: token, range: scan..<text.endIndex) {
                // Something the tokens do not cover sits in between; a space
                // there still separates the words.
                if text[scan..<range.lowerBound].contains(where: \.isWhitespace) { spaced = true }
                found = range
            }
            guard let found else { previousOpen = false; continue }
            result[index] = index > 0 && previousOpen && !spaced && !leading
            cursor = found.upperBound
            previousOpen = !trailing
        }
        return result
    }

    /// Glue for the words shown for `line`. Readings that replace the words
    /// one for one take the original words' grouping -- the syllables of
    /// one word still read as that word -- except that a Latin reading of a
    /// Chinese, Japanese or Thai token is a word of its own: those scripts
    /// are written without spaces, their romanisation is not (君の名は is
    /// "kimi no na wa", never "kiminonawa").
    static func glue(line: LyricsFetcher.Line, words: [LyricsFetcher.Word]) -> [Bool] {
        if words.count == line.words.count, zip(words, line.words).contains(where: { $0.text != $1.text }) {
            var result = glue(line.words, in: line.text)
            for index in words.indices where words[index].text != line.words[index].text
                && writtenWithoutSpaces(line.words[index].text) && !writtenWithoutSpaces(words[index].text) {
                result[index] = false
                if index + 1 < result.count { result[index + 1] = false }
            }
            return result
        }
        return glue(words, in: line.text)
    }

    /// Han characters and kana, and Thai, Lao, Myanmar and Khmer: scripts
    /// that do not separate words with spaces, so a run of them can hold
    /// several words. (Korean does separate them.)
    static func writtenWithoutSpaces(_ text: String) -> Bool {
        text.unicodeScalars.contains { scalar in
            JapaneseReading.isJapanese(scalar) || spaceless.contains { $0.contains(scalar.value) }
        }
    }
    /// Thai, Lao, Myanmar and Khmer.
    private static let spaceless: [ClosedRange<UInt32>] = [0x0E00...0x0EFF, 0x1000...0x109F, 0x1780...0x17FF]

    /// Glue for a rolling stage -- Spoken, Cascade and Cutout -- which sets
    /// a line one word at a time: `glue(line:words:)` with each unspaced
    /// run parted into its words (`splittingWords`, `.words`). Kept whole, a
    /// Japanese, Chinese or Thai line, which has no spaces, rolled as one
    /// word: one label for the whole line, pressed on at its first syllable
    /// with the whole line's reading under it, so the line appeared at once
    /// and only the characters inside it followed their own timing.
    static func rollingGlue(line: LyricsFetcher.Line, words: [LyricsFetcher.Word]) -> [Bool] {
        splittingWords(words, glue: glue(line: line, words: words), into: .words)
    }

    /// How finely `splittingWords` parts an unspaced run.
    enum Parting {
        /// Phrases, for a constellation's fragments: a light word -- one of
        /// kana alone, or a single character -- stays with the word before
        /// it (沈むように 溶けてゆくように, 君の名は 希望).
        case phrases
        /// Words as a reader takes them in, for a rolling stage: in Japanese
        /// only a word that cannot begin one (`JapaneseReading.continuesPhrase`)
        /// stays with the word before it (窓辺に 座って 海を 見ていた, where
        /// phrases leave 窓辺に 座って海を見ていた), and elsewhere a single
        /// character does (我们 一起 走过 春天的 小路).
        case words
    }

    /// `glue` with each run of a script written without spaces parted into
    /// phrases or words, each one's characters still together, and only
    /// where a timed token begins. For layouts made of separate fragments --
    /// a constellation needs several to link, a rolling stage sets one word
    /// at a time -- that would otherwise get a whole unspaced line as one.
    static func splittingWords(_ words: [LyricsFetcher.Word], glue: [Bool], into parting: Parting = .phrases) -> [Bool] {
        var result = glue
        for range in ranges(glue, count: words.count) where range.count > 1 {
            let tokens = words[range]
            guard tokens.contains(where: { writtenWithoutSpaces($0.text) }) else { continue }
            var offsets: [Int] = [], text = ""
            for token in tokens {
                offsets.append(text.count)
                text += token.text
            }
            let starts = phraseStarts.starts(of: text, parting: parting)
            for (offset, index) in range.enumerated() where offset > 0 && starts.contains(offsets[offset]) {
                result[index] = false
            }
        }
        return result
    }

    /// Where phrases or words begin in a string, by character. The system's
    /// word segmentation parts out every particle and ending (沈む よう に,
    /// and Chinese nearly character by character), so some words stay with
    /// the word before them (see `Parting`). Worked out once per text.
    private final class PhraseStarts: @unchecked Sendable {
        private let lock = NSLock()
        private var known: [String: Set<Int>] = [:]

        private static func isLight(_ word: Substring) -> Bool {
            word.count <= 1 || word.unicodeScalars.allSatisfy { (0x3041...0x309F).contains($0.value) || $0.value == 0x30FC }
        }

        func starts(of text: String, parting: Parting) -> Set<Int> {
            lock.lock()
            defer { lock.unlock() }
            let key = parting == .phrases ? text : "\u{1}" + text
            if let found = known[key] { return found }
            let tokenizer = NLTokenizer(unit: .word)
            tokenizer.string = text
            let japanese = text.unicodeScalars.contains { (0x3041...0x30FF).contains($0.value) }
            var starts = Set<Int>()
            var previous: Substring?
            tokenizer.enumerateTokens(in: text.startIndex..<text.endIndex) { range, _ in
                let word = text[range]
                let stays: Bool
                switch parting {
                case .phrases: stays = Self.isLight(word)
                case .words where japanese:
                    stays = previous.map { JapaneseReading.continuesPhrase(String(word), after: String($0)) } ?? false
                case .words: stays = word.count <= 1
                }
                if !stays { starts.insert(text.distance(from: text.startIndex, to: range.lowerBound)) }
                previous = word
                return true
            }
            if known.count > 256 { known.removeAll() }
            known[key] = starts
            return starts
        }
    }
    private static let phraseStarts = PhraseStarts()

    /// The line as one string, runs apart and the tokens of each run
    /// together: what Echo repeats and a lyric card sets. Joining every
    /// token with a space set 沈むように as "沈 む よ う に".
    static func text(_ words: [LyricsFetcher.Word], glue: [Bool]) -> String {
        labels(words, glue: glue).joined(separator: " ")
    }

    /// Each run's text, in order (see `text`), punctuation included.
    static func labels(_ words: [LyricsFetcher.Word], glue: [Bool]) -> [String] {
        ranges(glue, count: words.count).map { range in
            words[range].map(\.text).joined().trimmingCharacters(in: .whitespacesAndNewlines)
        }.filter { !$0.isEmpty }
    }

    /// A glued run's reading: its tokens' readings written together. The
    /// parts of one phrase share the phrase's time (the rule
    /// `LyricRomanizer.lineReading` uses), so where the time changes a new
    /// phrase begins, and in a script written without spaces it is spaced
    /// from the last (今日も reads "kyou mo", 佢哋係 "keoi5 dei6 hai6"). The
    /// syllables of an alphabetic word stay together ("lyu" "blyu" is
    /// "lyublyu"), and a reading's own spaces are kept. Nil when nothing in
    /// the run reads differently from how it is written.
    static func reading(of tokens: ArraySlice<LyricsFetcher.Word>,
                        readings: [TimedWordKey: LyricsFetcher.Word]) -> String? {
        var text = "", read = false
        var phrase: (start: Double, end: Double)?
        var previous: LyricsFetcher.Word?
        for token in tokens {
            defer { previous = token }
            guard let reading = readings[token.presentationID] else { phrase = nil; continue }
            let piece = reading.text.trimmingCharacters(in: .whitespacesAndNewlines)
            let own = (start: reading.start, end: reading.end)
            let continues = phrase.map { $0.start == own.start && $0.end == own.end } ?? true
            phrase = own
            guard !piece.isEmpty else { continue }
            if LyricReadingPairs.isDistinct(piece, from: token.text) { read = true }
            if !text.isEmpty, !continues,
               writtenWithoutSpaces(token.text) || previous.map({ writtenWithoutSpaces($0.text) }) == true {
                text += " "
            }
            text += piece
        }
        return read && !text.isEmpty ? text : nil
    }

    /// Token index ranges, one per run.
    static func ranges(_ glue: [Bool], count: Int) -> [Range<Int>] {
        var result: [Range<Int>] = []
        var start = 0
        for index in 0..<count where index > 0 && !(index < glue.count && glue[index]) {
            result.append(start..<index)
            start = index
        }
        if count > 0 { result.append(start..<count) }
        return result
    }

    /// Music notes mark breaks rather than words.
    private static let decorations: Set<Character> = ["♩", "♪", "♫", "♬", "♭", "♮", "♯"]

    /// Whether a token is drawn: letters, numbers and symbols such as emoji,
    /// but not punctuation on its own or music-note decorations.
    static func isVisible(_ text: String) -> Bool {
        text.contains { $0.isLetter || $0.isNumber || ($0.isSymbol && !decorations.contains($0)) }
    }

    /// A source token or glued run can contain a whole phrase. Only preview
    /// a label that can be read as one word.
    static func isPreviewWord(_ text: String) -> Bool {
        var count = 0
        text.enumerateSubstrings(in: text.startIndex..., options: [.byWords, .substringNotRequired]) { _, _, _, stop in
            count += 1
            stop = count > 1
        }
        return count == 1
    }

    static func canPreviewFirstRun(_ words: [LyricsFetcher.Word], glue: [Bool]) -> Bool {
        let visible = visible(words, glue: glue)
        let ranges = ranges(visible.glue, count: visible.words.count)
        guard ranges.count > 1, let first = ranges.first else { return false }
        return isPreviewWord(visible.words[first].map(\.text).joined())
    }

    /// The visible tokens and their glue. A token stays glued across a
    /// dropped one only if nothing but glue separated them.
    static func visible(_ words: [LyricsFetcher.Word], glue: [Bool]) -> (words: [LyricsFetcher.Word], glue: [Bool]) {
        var kept: [LyricsFetcher.Word] = [], flags: [Bool] = []
        var chained = true
        for (index, word) in words.enumerated() {
            chained = chained && index < glue.count && glue[index]
            guard isVisible(word.text) else { continue }
            flags.append(!kept.isEmpty && chained)
            kept.append(word)
            chained = true
        }
        return (kept, flags)
    }

    /// One timed word per run, for layouts that move whole words. Readings
    /// of the tokens are joined under the run's word, and each glued run's
    /// tokens are kept by the word they form, so the word can still show
    /// each syllable or character arriving on its own beat.
    static func merged(_ words: [LyricsFetcher.Word], glue: [Bool], readings: [TimedWordKey: String] = [:])
        -> (words: [LyricsFetcher.Word], readings: [TimedWordKey: String], tokens: [TimedWordKey: [LyricsFetcher.Word]]) {
        var result: [LyricsFetcher.Word] = [], joined: [TimedWordKey: String] = [:]
        var parts: [TimedWordKey: [LyricsFetcher.Word]] = [:]
        for range in ranges(glue, count: words.count) {
            let tokens = words[range]
            guard range.count > 1, let first = tokens.first else {
                let word = words[range.lowerBound]
                result.append(word)
                if let reading = readings[word.presentationID] { joined[word.presentationID] = reading }
                continue
            }
            let text = tokens.map(\.text).joined().trimmingCharacters(in: .whitespacesAndNewlines)
            let word = LyricsFetcher.Word(text: text, start: first.start, end: tokens.map(\.end).max() ?? first.end)
            result.append(word)
            parts[word.presentationID] = Array(tokens)
            let reading = tokens.compactMap { readings[$0.presentationID] }.joined()
            if !reading.isEmpty { joined[word.presentationID] = reading }
        }
        return (result, joined, parts)
    }

    /// Where each token begins and ends along the run's text, 0...1 by
    /// character, so a token's reveal falls on its own letters.
    static func boundaries(_ tokens: [LyricsFetcher.Word]) -> [Double] {
        let counts = tokens.map { Double($0.text.trimmingCharacters(in: .whitespacesAndNewlines).count) }
        let total = max(1, counts.reduce(0, +))
        var result: [Double] = [0], running = 0.0
        for count in counts {
            running += count
            result.append(min(1, running / total))
        }
        return result
    }
}
