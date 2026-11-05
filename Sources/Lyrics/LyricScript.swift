import CoreFoundation
import Foundation

/// Script rules shared by the TTML, YRC and LRC parsers.
enum LyricScript {
    /// One timed piece of a line exactly as a source supplies it. `glued` means
    /// no whitespace separated it from the previous piece in the source text.
    struct TimedToken: Equatable {
        var text: String
        var start: Double
        var end: Double
        var glued: Bool
    }

    /// Han, kana and Hangul, and the scripts written without spaces between
    /// words (Thai, Lao, Myanmar, Khmer), keep every timed syllable as its own
    /// token: joining them would lose karaoke progress or swallow a phrase.
    static func keepsSyllables(_ text: String) -> Bool {
        text.unicodeScalars.contains { scalar in
            switch scalar.value {
            case 0x1100...0x11FF, 0x3130...0x318F, 0xA960...0xA97F, 0xAC00...0xD7FF: return true // Hangul
            case 0x3040...0x30FF, 0x31F0...0x31FF, 0xFF66...0xFF9F: return true // Kana
            case 0x3005, 0x3007, 0x303B, 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF, 0x20000...0x323AF: return true // Han
            case 0x0E00...0x0EFF, 0x1000...0x109F, 0x1780...0x17FF: return true // Thai, Lao, Myanmar, Khmer
            default: return false
            }
        }
    }

    /// Builds display words from timed pieces. Glued syllables of spaced
    /// scripts ("beau" "ti" "ful") form one word from the first start to the
    /// last end; glued punctuation joins its neighbour; CJK pieces stay
    /// separate. The source text, not this list, carries the original spacing.
    static func words(from tokens: [TimedToken]) -> [LyricsFetcher.Word] {
        var pieces: [TimedToken] = []
        for (index, token) in tokens.enumerated() {
            let joinsNext = index + 1 < tokens.count && tokens[index + 1].glued
            pieces += token.glued || joinsNext ? splitAtSpaces(token) : [token]
        }
        var words: [LyricsFetcher.Word] = []
        for token in pieces {
            guard token.glued, let last = words.last else {
                words.append(.init(text: token.text, start: token.start, end: token.end))
                continue
            }
            if !hasLetters(token.text) {
                // Trailing punctuation has no meaningful timing of its own.
                words[words.count - 1].text += token.text
            } else if !hasLetters(last.text) || (!keepsSyllables(last.text) && !keepsSyllables(token.text)) {
                words[words.count - 1].text += token.text
                words[words.count - 1].end = max(last.end, token.end)
            } else {
                words.append(.init(text: token.text, start: token.start, end: token.end))
            }
        }
        return words
    }

    /// A syllable piece that runs on into the next word ("lie" "ve you'll")
    /// is divided at its spaces, sharing its time out by length, so each part
    /// joins the right word. Punctuation set apart by a space stays attached.
    private static func splitAtSpaces(_ token: TimedToken) -> [TimedToken] {
        let parts = token.text.split(whereSeparator: \.isWhitespace).map(String.init)
        let span = token.end - token.start
        // A piece with no real end (an open last stamp) cannot be shared out.
        guard parts.count > 1, span > 0, span < 3600 else { return [token] }
        let total = Double(parts.reduce(0) { $0 + $1.count })
        var cursor = token.start, consumed = 0
        return parts.enumerated().map { index, part in
            let start = cursor
            consumed += part.count
            cursor = index == parts.count - 1 ? token.end : token.start + span * (Double(consumed) / total)
            return TimedToken(text: part, start: start, end: cursor, glued: index == 0 ? token.glued : !hasLetters(part))
        }
    }

    /// Letters or digits, i.e. anything sung rather than punctuation.
    static func hasLetters(_ text: String) -> Bool {
        text.contains { $0.isLetter || $0.isNumber }
    }

    private static let latinLetter = try? NSRegularExpression(pattern: #"\p{Latin}"#)
    private static let otherLetter = try? NSRegularExpression(pattern: #"(?!\p{Latin})\p{L}"#)
    private static let latinRun = try? NSRegularExpression(pattern: #"\p{Latin}+"#)
    private static let numberedSyllable = try? NSRegularExpression(pattern: #"(?i)^[a-z]+[1-6]$"#)

    private static func matches(_ regex: NSRegularExpression?, _ text: String) -> Bool {
        regex?.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) != nil
    }

    /// Letters from any script other than Latin: kana, Hangul, Cyrillic...
    static func hasNonLatinLetters(_ text: String) -> Bool { matches(otherLetter, text) }

    /// Text that has letters, all of them Latin (romaji and pinyin included).
    static func isLatin(_ text: String) -> Bool { matches(latinLetter, text) && !hasNonLatinLetters(text) }

    /// Whether a Latin-script line printed beside a non-Latin `lyric` reads it
    /// aloud (romaji, pinyin, Revised Romanization, transliterated Cyrillic)
    /// rather than translating it. The line is compared with the system's own
    /// transliteration of the lyric's non-Latin part, ignoring words both share;
    /// numbered tones (Jyutping, pinyin with digits) always count as a reading.
    /// Pass one `cache` for all the rows of a file.
    static func isReading(_ candidate: String, of lyric: String, cache: ReadingCache = ReadingCache()) -> Bool {
        guard hasNonLatinLetters(lyric), isLatin(candidate) else { return false }
        let syllables = candidate.split(whereSeparator: { !$0.isLetter && !$0.isNumber })
        let numbered = syllables.filter { matches(numberedSyllable, String($0)) }.count
        if numbered >= 2, numbered * 2 >= syllables.count { return true }
        let shared = Set(asciiWords(lyric))
        let target = Array(asciiWords(candidate).filter { !shared.contains($0) }.joined())
        guard target.count >= 2 else { return false }
        let foreign = latinRun?.stringByReplacingMatches(in: lyric, range: NSRange(lyric.startIndex..., in: lyric),
                                                         withTemplate: " ") ?? lyric
        // Kana is best read by the Japanese tokenizer, everything else by the
        // system transliteration first; kanji alone may be either language.
        let kana = foreign.unicodeScalars.contains { (0x3040...0x30FF).contains($0.value) }
        let han = foreign.unicodeScalars.contains { (0x4E00...0x9FFF).contains($0.value) }
        var readers: [(String) -> [Character]] = [cache.latin]
        if kana || han { readers.insert(cache.japanese, at: kana ? 0 : 1) }
        return readers.contains { similarity($0(foreign), target) >= 0.4 }
    }

    /// Transliterations already made, and one Japanese tokenizer, kept while
    /// a file's rows are compared: a bilingual file then costs a tokenizer
    /// once instead of once per row, and a repeated chorus costs nothing.
    final class ReadingCache {
        private var latinReadings: [String: [Character]] = [:]
        private var japaneseReadings: [String: [Character]] = [:]
        private var tokenizer: CFStringTokenizer?

        /// The system's own transliteration, as ASCII letters.
        fileprivate func latin(_ text: String) -> [Character] {
            if let known = latinReadings[text] { return known }
            let reading = Array(LyricScript.asciiWords(text.applyingTransform(.toLatin, reverse: false) ?? "").joined())
            latinReadings[text] = reading
            return reading
        }

        /// The Japanese tokenizer's reading, so kanji are read as Japanese.
        fileprivate func japanese(_ text: String) -> [Character] {
            if let known = japaneseReadings[text] { return known }
            let source = text as CFString
            let range = CFRange(location: 0, length: CFStringGetLength(source))
            let tokenizer: CFStringTokenizer
            if let existing = self.tokenizer {
                CFStringTokenizerSetString(existing, source, range)
                tokenizer = existing
            } else {
                let locale = CFLocaleCreate(kCFAllocatorDefault, CFLocaleIdentifier(rawValue: "ja" as CFString))
                tokenizer = CFStringTokenizerCreate(kCFAllocatorDefault, source, range, kCFStringTokenizerUnitWordBoundary, locale)
                self.tokenizer = tokenizer
            }
            var parts: [String] = []
            while CFStringTokenizerAdvanceToNextToken(tokenizer).rawValue != 0, parts.count < 400 {
                if let latin = CFStringTokenizerCopyCurrentTokenAttribute(tokenizer, kCFStringTokenizerAttributeLatinTranscription) as? String {
                    parts.append(latin)
                }
            }
            let reading = Array(LyricScript.asciiWords(parts.joined(separator: " ")).joined())
            japaneseReadings[text] = reading
            return reading
        }
    }

    private static func asciiWords(_ text: String) -> [String] {
        text.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: Locale(identifier: "en_US_POSIX"))
            .split(whereSeparator: { !($0.isASCII && $0.isLetter) }).map(String.init)
    }

    /// Dice coefficient over letter pairs: 1 for identical spellings, near 0
    /// for unrelated text, and tolerant of differing romanisation schemes.
    private static func similarity(_ a: [Character], _ b: [Character]) -> Double {
        func pairs(_ letters: [Character]) -> [UInt16: Int] {
            guard letters.count > 1 else { return [:] }
            var result: [UInt16: Int] = [:]
            for index in 1..<letters.count {
                result[UInt16(letters[index - 1].asciiValue ?? 0) << 8 | UInt16(letters[index].asciiValue ?? 0), default: 0] += 1
            }
            return result
        }
        let left = pairs(a), right = pairs(b)
        let total = left.values.reduce(0, +) + right.values.reduce(0, +)
        guard total > 0 else { return 0 }
        let shared = left.reduce(0) { $0 + min($1.value, right[$1.key] ?? 0) }
        return 2 * Double(shared) / Double(total)
    }

}
