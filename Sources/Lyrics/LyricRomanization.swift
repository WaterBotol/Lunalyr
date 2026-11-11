import Combine
import Foundation
import NaturalLanguage

@MainActor
final class RomanizationOptions: ObservableObject {
    static let shared = RomanizationOptions()
    enum Display: String, CaseIterable, Identifiable {
        case off, below, replace
        var id: String { rawValue }
        var title: String { self == .off ? "Off" : self == .below ? "Below lyrics" : "Replace lyrics" }
    }
    enum Language: String, CaseIterable, Identifiable {
        case automatic, japanese, mandarin, cantonese, korean, other
        var id: String { rawValue }
        var title: String {
            switch self {
            case .automatic: return "Detect language"
            case .japanese: return "Japanese · Romaji"
            case .mandarin: return "Mandarin · Pinyin"
            case .cantonese: return "Cantonese · Saved readings only"
            case .korean: return "Korean · Revised Romanization"
            case .other: return "Other scripts · Latin"
            }
        }
    }
    struct Snapshot { var display: Display; var language: Language; var toneMarks: Bool; var preferSaved: Bool }
    static let displayKey = "custom.romanizationDisplay"
    @Published var display: Display { didSet { UserDefaults.standard.set(display.rawValue, forKey: Self.displayKey) } }
    @Published var language: Language { didSet { UserDefaults.standard.set(language.rawValue, forKey: "custom.romanizationLanguage") } }
    @Published var toneMarks: Bool { didSet { UserDefaults.standard.set(toneMarks, forKey: "custom.romanizationTones") } }
    @Published var preferSaved: Bool { didSet { UserDefaults.standard.set(preferSaved, forKey: "custom.romanizationSaved") } }
    var snapshot: Snapshot { .init(display: display, language: language, toneMarks: toneMarks, preferSaved: preferSaved) }
    private init() {
        let d = UserDefaults.standard
        display = Display(rawValue: d.string(forKey: "custom.romanizationDisplay") ?? "") ?? .below
        language = Language(rawValue: d.string(forKey: "custom.romanizationLanguage") ?? "") ?? .automatic
        toneMarks = d.object(forKey: "custom.romanizationTones") as? Bool ?? true
        preferSaved = d.object(forKey: "custom.romanizationSaved") as? Bool ?? true
    }
    /// Rereads how readings are shown, part of a lyric style
    /// (`LyricStyleProfiles`).
    func reloadDisplay() {
        let saved = Display(rawValue: UserDefaults.standard.string(forKey: Self.displayKey) ?? "") ?? .below
        if display != saved { display = saved }
    }
}

/// Original implementation using macOS language services, with no online calls
/// or imported dictionaries. Saved .roma.lrc readings can supply Jyutping too.
///
/// Readings are worked out per line, never per frame: Japanese and Korean need
/// the whole line (readings and sound changes cross word boundaries), so the
/// line is read once and the result is laid back onto its words by range.
enum LyricRomanizer {
    /// Readings by line index. An entry holds one reading per word of
    /// `line.words`. A reading's time is that of the phrase it reads: a
    /// phrase written across several words (今日 kyou) gives them all its
    /// time, and on a line timed one character at a time each character
    /// carries its share of it (kyo, u); otherwise the first word carries the
    /// phrase and the rest an empty text. When a saved reading cannot be
    /// split by word, the entry is a single reading spanning the line, drawn
    /// once under the whole line (see `lineReading`). With "Replace lyrics"
    /// an entry is the words to show in place of the lyric.
    static func readings(_ lines: [LyricsFetcher.Line], options: RomanizationOptions.Snapshot) -> [Int: [LyricsFetcher.Word]] {
        guard options.display != .off else { return [:] }
        let song = songLanguage(lines)
        var result: [Int: [LyricsFetcher.Word]] = [:]
        let memo = Memo()
        for (index, line) in lines.enumerated() where !line.isInstrumental && !line.words.isEmpty {
            if Task.isCancelled { break }
            let text = LineText(line)
            let language = self.language(of: line.text, song: song, chosen: options.language)
            let trimmed = line.romanization?.trimmingCharacters(in: .whitespacesAndNewlines)
            let saved = trimmed?.isEmpty == false ? trimmed : nil
            // A reading supplied by the lyric source or imported by the user
            // comes first. A Japanese one that does not fit the line's kana
            // reads other words and gives way to the generated reading; any
            // other is kept whole when it cannot be split by word. A saved
            // reading is also the only one for lines nothing can be generated
            // for (Cantonese).
            var placement: Placement?
            if let saved, options.preferSaved { placement = memo.saved(saved, text, language) }
            if placement == nil { placement = memo.generated(text, language) }
            if placement == nil, let saved { placement = memo.saved(saved, text, language) ?? .line(saved) }
            if let words = placement?.words(for: line, display: options.display, toneMarks: options.toneMarks) {
                result[index] = words
            }
        }
        return result
    }

    /// For "Below lyrics": the line's reading written whole, or nil when its
    /// words' readings already say it. That is a saved reading that could
    /// not be split by word, which is drawn once under the whole line
    /// (`LyricReadingPairs.line`), and a line timed one character at a time
    /// whose phrases are shared out over their characters (kyo, u, mo
    /// becomes "kyou mo").
    static func lineReading(_ readings: [LyricsFetcher.Word]?, for line: LyricsFetcher.Line) -> String? {
        guard let readings, !readings.isEmpty, !line.words.isEmpty else { return nil }
        if readings.count != line.words.count {
            let text = readings.map(\.text).filter { !$0.isEmpty }.joined(separator: " ")
            return text.isEmpty || text == line.text ? nil : text
        }
        guard isCharacterTimed(line) else { return nil }
        // The parts of one phrase share its time.
        var phrases: [String] = []
        var spread = false
        var previous: (start: Double, end: Double)?
        for reading in readings {
            if let previous, previous.start == reading.start, previous.end == reading.end {
                phrases[phrases.count - 1] += reading.text
                spread = true
            } else {
                phrases.append(reading.text)
            }
            previous = (reading.start, reading.end)
        }
        let text = phrases.filter { !$0.isEmpty }.joined(separator: " ")
        return spread && !text.isEmpty ? text : nil
    }

    /// Most sung words of the line are a single character (今, 日, も).
    static func isCharacterTimed(_ line: LyricsFetcher.Line) -> Bool {
        let sung = line.words.map { $0.text.filter { $0.isLetter || $0.isNumber } }.filter { !$0.isEmpty }
        guard sung.count >= 2 else { return false }
        return sung.filter { $0.count == 1 }.count * 4 >= sung.count * 3
    }

    static func romanize(_ text: String, language: RomanizationOptions.Language, toneMarks: Bool) -> String {
        guard containsNonLatin(text) else { return text }
        let resolved = language == .automatic || language == .other
            ? self.language(of: text, song: .automatic, chosen: language) : language
        let reading: String
        switch resolved {
        case .japanese: reading = JapaneseReading.romanize(text)
        case .korean: reading = KoreanRomanizer.romanize(text)
        case .mandarin: reading = pinyin(text)
        case .cantonese: return text
        case .automatic, .other: reading = text.applyingTransform(.toLatin, reverse: false) ?? text
        }
        return normalized(reading, toneMarks: toneMarks)
    }

    /// Any letter outside the Latin script. It used to list ten scripts, so
    /// Punjabi, Bengali, Tamil, Telugu, Gujarati, Kannada, Malayalam,
    /// Georgian, Armenian and others were never romanised at all.
    static func containsNonLatin(_ text: String) -> Bool {
        text.range(of: "[\\p{L}&&\\P{Latin}]", options: .regularExpression) != nil
    }

    // MARK: - Language

    /// The song's language, from how many of its lines use each script, so a
    /// Japanese phrase in a Mandarin song does not make the song Japanese.
    /// `.automatic` means a mixed song, whose Han lines are judged one by one.
    static func songLanguage(_ lines: [LyricsFetcher.Line]) -> RomanizationOptions.Language {
        var kana = 0, hangul = 0, han = 0
        var hanText: [String] = []
        for line in lines where !line.isInstrumental {
            if line.text.range(of: "[\\p{Hiragana}\\p{Katakana}]", options: .regularExpression) != nil { kana += 1 }
            else if KoreanRomanizer.containsHangul(line.text) { hangul += 1 }
            else if line.text.range(of: "\\p{Han}", options: .regularExpression) != nil { han += 1; hanText.append(line.text) }
        }
        let total = kana + hangul + han
        guard total > 0 else { return .other }
        if hangul >= kana && hangul * 2 >= total { return .korean }
        if kana * 2 >= total { return .japanese }
        guard han * 4 >= total * 3 else { return .automatic }
        let text = hanText.joined(separator: "\n")
        // Written Cantonese through the song, not one Cantonese line in a
        // Mandarin song, which is judged on its own.
        let marked = hanText.filter { cantoneseScore($0) >= 1 }.count
        if cantoneseScore(text) >= 2, marked >= 2 || marked * 4 >= hanText.count { return .cantonese }
        return NLLanguageRecognizer.dominantLanguage(for: String(text.prefix(1200))) == .japanese ? .japanese : .mandarin
    }

    /// A line's language. Kana and Hangul settle it; the chosen language,
    /// else the song's, decides how Han characters are read.
    static func language(of text: String, song: RomanizationOptions.Language,
                         chosen: RomanizationOptions.Language) -> RomanizationOptions.Language {
        if chosen == .other { return .other }
        if text.range(of: "[\\p{Hiragana}\\p{Katakana}]", options: .regularExpression) != nil { return .japanese }
        if KoreanRomanizer.containsHangul(text) { return .korean }
        guard text.range(of: "\\p{Han}", options: .regularExpression) != nil else { return .other }
        if chosen != .automatic { return chosen }
        switch song {
        case .japanese, .korean, .cantonese: return song
        default:
            // Written Cantonese in an otherwise Mandarin song still never
            // gets Mandarin pinyin.
            if cantoneseScore(text) >= 1 { return .cantonese }
            if song == .mandarin { return .mandarin }
            return NLLanguageRecognizer.dominantLanguage(for: text) == .japanese ? .japanese : .mandarin
        }
    }

    /// Characters only written Cantonese uses.
    private static let cantoneseCharacters = Set("佢哋嘅咗喺嘢冇啲嚟嗰噉攞啱搵揾諗谂冚嘥瞓㗎喎嚿攰冧啩噃咁")
    /// Also found in Mandarin text (關係, 俾斯麥, 咩咩, a humming 唔), so they
    /// only add to other evidence.
    private static let cantoneseHints = Set("係乜睇俾咩唔")
    /// Colloquial words, which count only beside a Cantonese-only character:
    /// on their own they also turn up across Mandarin words (一點解釋,
    /// 身邊度過, 美好彩虹, 鋼琴日記).
    private static let cantoneseWords: Set<String> = [
        "係咪", "點解", "点解", "乜嘢", "邊個", "边个", "而家", "依家", "鍾意", "钟意",
        "琴日", "聽日", "听日", "屋企", "點樣", "点样", "邊度", "边度", "呢度", "嗰度", "咁樣", "好彩",
    ]

    /// Evidence of written Cantonese: one point for each distinct
    /// Cantonese-only character, for 唔 before a word (唔知, 唔好), and for
    /// each colloquial word beside them, and half a point for each hint.
    /// Without a Cantonese-only character the score stays below 1. One point
    /// marks a line; two, spread over the song, mark the whole song.
    static func cantoneseScore(_ text: String) -> Double {
        let characters = Array(text)
        var strong = Set<String>(), hints = Set<Character>(), words = Set<String>()
        for (index, character) in characters.enumerated() {
            let next = index + 1 < characters.count ? characters[index + 1] : nil
            if cantoneseCharacters.contains(character) {
                strong.insert(String(character))
            } else if character == "唔", let next, next != "唔",
                      next.unicodeScalars.first?.properties.isIdeographic == true {
                strong.insert(String([character, next]))
            } else if cantoneseHints.contains(character) {
                hints.insert(character)
            }
            if let next, cantoneseWords.contains(String([character, next])) { words.insert(String([character, next])) }
        }
        guard !strong.isEmpty else { return min(0.5, Double(hints.count) / 2) }
        return Double(strong.count + words.count) + Double(hints.count) / 2
    }

    // MARK: - Generated readings

    /// Analyses and placements worked out once per distinct line in a pass,
    /// which also keeps one tokenizer for all its lines.
    private final class Memo {
        private lazy var tokenizer = JapaneseReading.Tokenizer()
        private var analyses: [String: JapaneseAlignment.Analysis] = [:]
        private var generated: [String: Placement?] = [:]
        private var saved: [String: Placement?] = [:]

        func analysis(_ text: LineText) -> JapaneseAlignment.Analysis {
            if let known = analyses[text.key] { return known }
            let analysis = JapaneseAlignment.analyse(text.string, using: tokenizer)
            analyses[text.key] = analysis
            return analysis
        }

        func generated(_ text: LineText, _ language: RomanizationOptions.Language) -> Placement? {
            let key = language.rawValue + "\u{1}" + text.key
            if let known = generated[key] { return known }
            let placement = LyricRomanizer.generate(text, language: language, memo: self)
            generated[key] = placement
            return placement
        }

        func saved(_ reading: String, _ text: LineText, _ language: RomanizationOptions.Language) -> Placement? {
            let key = language.rawValue + "\u{1}" + reading + "\u{1}" + text.key
            if let known = saved[key] { return known }
            let placement = LyricRomanizer.savedPlacement(reading, text: text, language: language, memo: self)
            saved[key] = placement
            return placement
        }
    }

    private static func generate(_ text: LineText, language: RomanizationOptions.Language, memo: Memo) -> Placement? {
        guard containsNonLatin(text.string) else { return nil }
        let placement: Placement
        switch language {
        case .japanese:
            let analysis = memo.analysis(text)
            placement = text.place(analysis.pieces.map(Piece.init),
                                   fragments: text.fragments(analysis.characters(reading: analysis.generated)))
        case .korean:
            // Each word is romanised whole, because its sounds change across
            // syllables; each syllable keeps what it sounds like (같이 ga·chi).
            let words = text.segments(separatedBy: { $0.isWhitespace })
            var characters: [JapaneseAlignment.CharacterReading] = []
            for (phrase, word) in words.enumerated() {
                var offset = word.location
                for (character, syllable) in zip(word.text, KoreanRomanizer.syllables(word.text)) {
                    characters.append(.init(location: offset, length: character.utf16.count, text: syllable, phrase: phrase))
                    offset += character.utf16.count
                }
            }
            placement = text.place(words.map { Piece(text: KoreanRomanizer.romanize($0.text), location: $0.location, length: $0.length) },
                                   fragments: text.fragments(characters))
        case .mandarin:
            // Pinyin is read one character at a time, so each word stands alone.
            placement = .words(text.words.map(pinyin), spans: Array(text.words.indices), fragments: nil)
        case .cantonese:
            // There is no on-device Jyutping, and Mandarin pinyin would be wrong.
            return nil
        case .automatic, .other:
            placement = .words(text.words.map { $0.applyingTransform(.toLatin, reverse: false) ?? $0 },
                               spans: Array(text.words.indices), fragments: nil)
        }
        return placement.reads(text.words) ? placement : nil
    }

    private static func pinyin(_ text: String) -> String {
        guard containsNonLatin(text) else { return text }
        return punctuation(text.applyingTransform(.mandarinToLatin, reverse: false) ?? text)
    }

    /// Full-width punctuation as it is written in Latin text.
    private static func punctuation(_ text: String) -> String {
        var result = ""
        for character in text {
            switch character {
            case "，", "、": result += ","
            case "。", "．": result += "."
            case "！": result += "!"
            case "？": result += "?"
            case "：": result += ":"
            case "；": result += ";"
            case "～", "〜": result += "~"
            case "「", "」", "『", "』", "《", "》", "〈", "〉", "（", "）", "【", "】", "“", "”", "‘", "’": continue
            default: result.append(character)
            }
        }
        return result
    }

    static func normalized(_ text: String, toneMarks: Bool) -> String {
        let reading = toneMarks ? text : text.applyingTransform(.stripDiacritics, reverse: false) ?? text
        return reading.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // MARK: - Saved readings

    /// Places a saved line reading on the words it reads. A Japanese reading
    /// is matched against the line's kana and placed on the characters it
    /// spells, however it is spaced; nil means it reads other words. Other
    /// readings are split by character (Han), syllable (Hangul), word or
    /// space where the counts agree, and otherwise kept whole for the line.
    private static func savedPlacement(_ saved: String, text: LineText, language: RomanizationOptions.Language,
                                       memo: Memo) -> Placement? {
        if language == .japanese {
            let analysis = memo.analysis(text)
            guard let readings = analysis.align(saved) else { return nil }
            let placement = text.place(analysis.pieces(reading: readings).map(Piece.init),
                                       fragments: text.fragments(analysis.characters(reading: readings)))
            return placement.reads(text.words) ? placement : nil
        }
        var tokens: [String] = []
        for part in saved.split(whereSeparator: \.isWhitespace).map(String.init) {
            // Punctuation written on its own stays with the word before it.
            if tokens.isEmpty || part.contains(where: { $0.isLetter || $0.isNumber }) { tokens.append(part) }
            else { tokens[tokens.count - 1] += part }
        }
        // One syllable for each character or Hangul syllable.
        if let units = text.scriptUnits(for: language), units.count == tokens.count, units.count > 1 {
            var phrase = 0
            let characters = zip(tokens, units).map { token, unit -> JapaneseAlignment.CharacterReading in
                if !unit.joins { phrase += 1 }
                return .init(location: unit.location, length: unit.length, text: token, phrase: phrase)
            }
            return text.place(zip(tokens, units).map { Piece(text: $0, location: $1.location, length: $1.length, joinsPrevious: $1.joins) },
                              fragments: language == .korean ? text.fragments(characters) : nil)
        }
        let split = LyricsFetcher.distributeWords(in: saved, from: 0, to: 1).map(\.text)
        if split.count == text.words.count { return .words(split, spans: Array(text.words.indices), fragments: nil) }
        let spaced = text.segments(separatedBy: { $0.isWhitespace }).filter { $0.text.contains { $0.isLetter || $0.isNumber } }
        if spaced.count > 1, spaced.count == tokens.count {
            // A Korean word's reading is shared by syllable when it spells
            // what the syllables sound like.
            var characters: [JapaneseAlignment.CharacterReading] = []
            if language == .korean {
                for (phrase, (token, word)) in zip(tokens, spaced).enumerated() {
                    let syllables = KoreanRomanizer.syllables(word.text)
                    guard syllables.joined().lowercased() == token.lowercased() else { continue }
                    var offset = word.location
                    for (character, syllable) in zip(word.text, syllables) {
                        characters.append(.init(location: offset, length: character.utf16.count, text: syllable, phrase: phrase))
                        offset += character.utf16.count
                    }
                }
            }
            return text.place(zip(tokens, spaced).map { Piece(text: $0, location: $1.location, length: $1.length) },
                              fragments: characters.isEmpty ? nil : text.fragments(characters))
        }
        return .line(saved)
    }

    // MARK: - Placement

    private struct Piece {
        var text: String
        var location: Int
        var length: Int
        var joinsPrevious = false

        init(text: String, location: Int, length: Int, joinsPrevious: Bool = false) {
            self.text = text
            self.location = location
            self.length = length
            self.joinsPrevious = joinsPrevious
        }

        init(_ piece: JapaneseReading.Piece) {
            self.init(text: piece.text, location: piece.location, length: piece.length, joinsPrevious: piece.joinsPrevious)
        }
    }

    private enum Placement {
        /// One reading per word, each phrase on the word it starts in, with
        /// the last word each phrase covers. `fragments` share the phrases
        /// out over the words they cover, for lines timed by character.
        case words([String], spans: [Int], fragments: [String]?)
        /// A reading that cannot be split by word.
        case line(String)

        func reads(_ words: [String]) -> Bool {
            switch self {
            case .words(let readings, _, _):
                return zip(readings, words).contains { !$0.isEmpty && $0 != $1 }
            case .line(let text):
                return !text.isEmpty
            }
        }

        func words(for line: LyricsFetcher.Line, display: RomanizationOptions.Display, toneMarks: Bool) -> [LyricsFetcher.Word]? {
            let words = line.words
            guard let first = words.first, let last = words.last else { return nil }
            switch self {
            case .words(let raw, let spans, let fragments):
                guard raw.count == words.count, spans.count == words.count else { return nil }
                let texts = raw.map { LyricRomanizer.normalized($0, toneMarks: toneMarks) }
                guard zip(texts, words).contains(where: { !$0.isEmpty && $0 != $1.text }) else { return nil }
                // The phrase each word belongs to, by the word it starts in.
                var owner = Array(words.indices)
                var reach = -1, current = -1
                for index in words.indices {
                    if !texts[index].isEmpty {
                        current = index
                        reach = max(index, min(spans[index], words.count - 1))
                    } else if index <= reach {
                        owner[index] = current
                    }
                }
                func end(of start: Int) -> Int { start == owner[start] ? max(start, min(spans[start], words.count - 1)) : start }
                if display == .replace {
                    // A phrase spanning several words is shown across their
                    // time; a word no reading covers is shown as it is.
                    return words.indices.compactMap { index in
                        if !texts[index].isEmpty {
                            return LyricsFetcher.Word(text: texts[index], start: words[index].start, end: words[end(of: index)].end)
                        }
                        return owner[index] == index ? words[index] : nil
                    }
                }
                let shown = fragments.flatMap { $0.count == words.count && LyricRomanizer.isCharacterTimed(line) ? $0 : nil }?
                    .map { LyricRomanizer.normalized($0, toneMarks: toneMarks) } ?? texts
                return words.indices.map { index in
                    let start = owner[index]
                    return LyricsFetcher.Word(text: shown[index], start: words[start].start, end: words[end(of: start)].end)
                }
            case .line(let raw):
                let text = LyricRomanizer.normalized(raw, toneMarks: toneMarks)
                guard !text.isEmpty, text != line.text else { return nil }
                if display == .replace {
                    let words = line.wordTimed
                        ? LyricsFetcher.distributeWords(in: text, from: first.start, to: last.end)
                        : LyricsFetcher.distributeWords(in: text, from: line.start, to: LyricsFetcher.estimatedSweepEnd(of: line))
                    return words.isEmpty ? nil : words
                }
                return [LyricsFetcher.Word(text: text, start: first.start, end: last.end)]
            }
        }
    }

    /// A line's words laid end to end in one string, with each word's UTF-16
    /// range, so a reading of the whole line can be mapped back onto words.
    private struct LineText {
        let string: String
        let words: [String]
        let ranges: [NSRange]

        init(_ line: LyricsFetcher.Line) {
            var cursor = line.text.startIndex
            var gaps: [Bool?] = []
            for word in line.words {
                let found = line.text.range(of: word.text, range: cursor..<line.text.endIndex)
                gaps.append(found.map { line.text[cursor..<$0.lowerBound].contains(where: \.isWhitespace) })
                if let found { cursor = found.upperBound }
            }
            // Timed Japanese words may be spaced by their markup rather than
            // the language (今 日 も, 思っ た より), where a space would split
            // a word (omo ta) or part a particle from it (世界 は sekai ha).
            // But a source that cuts words only at spaces (KPoe, eLRC stamped
            // by phrase) spaces every break between the lyric's own phrases
            // too, where reading across a space misreads hiragana (あいたい
            // あいたい is not a itai, まって まって not ma tte). So on a line
            // spaced at every break between Japanese words, a space is taken
            // as markup only where a new phrase cannot start (`goesOn`): on a
            // line timed one character at a time, between single characters
            // or syllables (きょ う も), and before a word that only goes on
            // from the one before. A space at only some breaks is a pause
            // (あっ 君だ) and stays.
            //
            // Spacing alone cannot settle the rest, which reads as spaced (the
            // cases are pinned in RomanizationTests): a word split after a
            // kanji (明 日が is not asu ga, 大 好きだよ not daisuki, あの 時
            // not ano toki, 一人 ぼっち not hitoribotchi, 来 ない not ko nai),
            // a っ before a word in kanji (引っ 越し is not hikkoshi) or, after
            // hiragana, before more than a syllable that is no ending (がっ こう
            // is not gakkou, as あっ きみだ is a kimi), and hiragana split
            // after hiragana (だき しめ is daki shime).
            let japanese = line.words.indices.dropFirst().filter { index in
                guard let before = line.words[index - 1].text.unicodeScalars.last,
                      let after = line.words[index].text.unicodeScalars.first else { return false }
                return JapaneseReading.isJapanese(before) && JapaneseReading.isJapanese(after)
            }
            let markup = line.wordTimed && japanese.count >= 2 && japanese.allSatisfy { gaps[$0] == true }
            let byCharacter = markup && LyricRomanizer.isCharacterTimed(line)
            func sung(_ index: Int) -> String { line.words[index].text.filter { $0.isLetter || $0.isNumber } }
            func single(_ index: Int) -> Bool { Self.isSyllable(sung(index)) }
            /// Whether the word at `index` goes on from the one before it,
            /// so a markup space between them is inside a phrase.
            func goesOn(_ index: Int) -> Bool {
                let previous = sung(index - 1), word = sung(index)
                guard let first = word.unicodeScalars.first, let last = previous.unicodeScalars.last else { return false }
                // Small kana and ー never start a word (すご ー い), and the
                // language never sets a lone particle apart (は 笑った wa
                // waratta, 空 いっぱい の 星).
                if JapaneseReading.carriesOn(first) || Self.particles.contains(previous) { return true }
                // Before kanji or katakana a new word starts (今 日差しが is ima).
                guard JapaneseReading.isHiragana(first) else { return false }
                // Particles and endings only follow a word (世界 は sekai wa,
                // 会い たい aitai, ちょっ と chotto, はっ として hatto shite).
                if JapaneseReading.followsAWord(word) { return true }
                // An interjection's っ is not doubled across a pause (あっ
                // きみだ, えっ どうして): without kanji before it, it goes on
                // only into a single syllable (さっ き sakki).
                let sokuon = last == "っ" || last == "ッ"
                if single(index), !sokuon || Self.isSyllable(String(previous.dropLast())) { return true }
                // After a noun the tokenizer splits a particle's kana off a
                // word of hiragana (君 はなれて is not wa narete, 夢 のこして
                // not no koshite).
                if Self.particles.contains(String(first)) { return false }
                // Hiragana goes on from kanji with its okurigana written, or
                // from katakana, as the rest of the word (抱き しめ dakishime,
                // 引っ こし hikkoshi, アイ して ai shite). Right after a kanji
                // the tokenizer would read it as the kanji's okurigana (傷 まだ
                // is not itama da, 愛 ささやく not mana), and after hiragana
                // across phrases (ならんだ あか, もしもし かめよ): there it
                // stands apart.
                let kanji = previous.unicodeScalars.contains { $0.properties.isIdeographic }
                let katakana = previous.unicodeScalars.contains { (0x30A1...0x30FA).contains($0.value) }
                return (kanji && JapaneseReading.isHiragana(last)) || (katakana && !sokuon)
            }
            var string = "", ranges: [NSRange] = [], length = 0
            for (index, word) in line.words.enumerated() {
                if index > 0 {
                    let before = line.words[index - 1].text.unicodeScalars.last
                    let after = word.text.unicodeScalars.first
                    var spaced = gaps[index] ?? !(Self.isCJK(before) && Self.isCJK(after))
                    if spaced, markup, japanese.contains(index),
                       byCharacter || (single(index - 1) && single(index)) || goesOn(index) {
                        spaced = false
                    }
                    if spaced { string += " "; length += 1 }
                }
                let count = (word.text as NSString).length
                ranges.append(NSRange(location: length, length: count))
                string += word.text
                length += count
            }
            self.string = string
            self.words = line.words.map(\.text)
            self.ranges = ranges
        }

        var key: String { string + "\u{1}" + ranges.map { "\($0.location)+\($0.length)" }.joined(separator: ",") }

        /// Small kana written into the syllable before them (きょ, ティ).
        private static let smallKana = Set("ぁぃぅぇぉゃゅょゎァィゥェォャュョヮ")
        /// Particles of one kana, which a markup space never parts from the
        /// words around them.
        private static let particles: Set<String> = ["は", "へ", "が", "を", "に", "の", "も", "と", "で", "や"]

        /// One syllable: a character with the small kana written into it.
        private static func isSyllable(_ sung: String) -> Bool {
            !sung.isEmpty && sung.dropFirst().allSatisfy(smallKana.contains)
        }

        private static func isCJK(_ scalar: Unicode.Scalar?) -> Bool {
            guard let scalar else { return false }
            return JapaneseReading.isJapanese(scalar) || (0xAC00...0xD7A3).contains(scalar.value)
                || (0x1100...0x11FF).contains(scalar.value) || (0x3131...0x318E).contains(scalar.value)
        }

        /// The first word that ends after `location`.
        func wordIndex(at location: Int) -> Int {
            ranges.firstIndex { location < $0.location + $0.length } ?? max(0, ranges.count - 1)
        }

        /// Lays readings onto the words they start in. Pieces that join form
        /// one phrase, so a word reads whole phrases (思った omotta).
        func place(_ pieces: [Piece], fragments: [String]?) -> Placement {
            var readings = [String](repeating: "", count: ranges.count)
            var spans = Array(ranges.indices)
            var phrase: Piece?
            func flush() {
                guard let phrase, !phrase.text.isEmpty, !ranges.isEmpty else { return }
                let first = wordIndex(at: phrase.location)
                let last = max(first, wordIndex(at: phrase.location + max(phrase.length, 1) - 1))
                readings[first] += (readings[first].isEmpty ? "" : " ") + phrase.text
                spans[first] = max(spans[first], last)
            }
            for piece in pieces where !piece.text.isEmpty {
                if piece.joinsPrevious, var joined = phrase {
                    joined.text += piece.text
                    joined.length = piece.location + piece.length - joined.location
                    phrase = joined
                } else {
                    flush()
                    phrase = piece
                }
            }
            flush()
            return .words(readings, spans: spans, fragments: fragments)
        }

        /// Each word's share of readings given character by character: its
        /// characters' parts, with a space where a new phrase starts.
        func fragments(_ characters: [JapaneseAlignment.CharacterReading]) -> [String] {
            var result = [String](repeating: "", count: ranges.count)
            var phrases = [Int?](repeating: nil, count: ranges.count)
            guard !ranges.isEmpty else { return result }
            for character in characters.sorted(by: { $0.location < $1.location }) where !character.text.isEmpty {
                let index = wordIndex(at: character.location)
                if let phrase = phrases[index], phrase != character.phrase { result[index] += " " }
                result[index] += character.text
                phrases[index] = character.phrase
            }
            return result
        }

        /// Runs of characters between separators, with their UTF-16 ranges.
        func segments(separatedBy isSeparator: (Character) -> Bool) -> [(text: String, location: Int, length: Int)] {
            var result: [(text: String, location: Int, length: Int)] = []
            var current = "", start = 0, offset = 0
            for character in string {
                let width = character.utf16.count
                if isSeparator(character) {
                    if !current.isEmpty { result.append((current, start, offset - start)) }
                    current = ""
                } else {
                    if current.isEmpty { start = offset }
                    current.append(character)
                }
                offset += width
            }
            if !current.isEmpty { result.append((current, start, offset - start)) }
            return result
        }

        /// The parts a Chinese or Korean reading spells one by one: each Han
        /// character or Hangul syllable, and each run of Latin letters or
        /// digits. Syllables of one Korean word join.
        func scriptUnits(for language: RomanizationOptions.Language) -> [(location: Int, length: Int, joins: Bool)]? {
            let korean = language == .korean
            guard language != .japanese,
                  korean ? KoreanRomanizer.containsHangul(string) : string.range(of: "\\p{Han}", options: .regularExpression) != nil
            else { return nil }
            var units: [(location: Int, length: Int, joins: Bool)] = []
            var offset = 0, previousEnd = -1, latin: (location: Int, length: Int)?
            for character in string {
                let width = character.utf16.count
                let scalar = character.unicodeScalars.first
                let spelled = korean ? KoreanRomanizer.containsHangul(String(character)) : scalar?.properties.isIdeographic == true
                if !spelled, character.isLetter || character.isNumber {
                    latin = latin.map { ($0.location, $0.length + width) } ?? (offset, width)
                } else {
                    if let run = latin { units.append((run.location, run.length, false)); previousEnd = -1; latin = nil }
                    if spelled {
                        units.append((offset, width, korean && previousEnd == offset))
                        previousEnd = offset + width
                    } else {
                        previousEnd = -1
                    }
                }
                offset += width
            }
            if let run = latin { units.append((run.location, run.length, false)) }
            return units
        }
    }
}
