import Foundation

/// Lines a romaji reading up with the Japanese line it reads. Kana are the
/// anchors: each must be spelled as it is written, allowing the usual
/// variants (wa for は, o for を, Kunrei-shiki si, tu and syo, a long vowel
/// doubled, marked with a macron or left out). Kanji take what lies between
/// the anchors, and where kanji meet with no kana between them the system's
/// own reading decides the split. A reading spelled by syllable (NetEase's
/// "yu me na ra ba"), by word or with no spaces lands on the characters it
/// reads, and a reading of different words does not fit at all.
enum JapaneseAlignment {
    /// A stretch of the line a reading spells as one: a run of kanji, a kana
    /// with its small kana (and a っ before it), a lone っ, a ー, a Latin
    /// word, or punctuation, which is not spelled.
    struct Unit {
        enum Kind { case kanji, kana, sokuon, latin, mark }
        var kind: Kind
        var location: Int
        var length: Int
        var source: String
        /// UTF-16 ranges of the characters, to share a reading out.
        var characters: [NSRange]
        /// The generated piece the unit is part of.
        var piece: Int?
        /// A っ comes first (った tta).
        var geminated = false
        /// ー, which lengthens the vowel before it.
        var lengthens = false
        var spellings: [Spelling] = []
        /// The system's reading of a kanji run, normalised, and with long
        /// vowels written short.
        var hint: [Character]? { didSet { shortHint = hint.map { collapsed($0[...]) } } }
        private(set) var shortHint: String?
    }

    struct Spelling {
        var letters: [Character]
        var cost: Double
    }

    /// A line read by the system, laid out in units.
    struct Analysis {
        let pieces: [JapaneseReading.Piece]
        let units: [Unit]
        /// Each unit's part of the generated reading.
        let generated: [String]
        /// The phrase (pieces written together) each unit belongs to.
        let phrases: [Int]

        /// Each unit's part of `saved`, or nil when it does not read this line.
        /// The parts are written in Hepburn, as the app's own readings are
        /// (`hepburn(_:generated:respelling:)`), whichever system the source used.
        func align(_ saved: String) -> [String]? {
            // A reading in kana or kanji is not romaji.
            guard !saved.unicodeScalars.contains(where: JapaneseReading.isJapanese) else { return nil }
            let reading = Reading(saved)
            guard !reading.letters.isEmpty, let ranges = JapaneseAlignment.align(reading, units) else { return nil }
            let parts = ranges.map { reading.raw($0) }
            // A kanji read otherwise than the app reads it is respelled only
            // when the reading shows elsewhere that it writes Kunrei-shiki: an
            // English word sung for a kanji (passion, vision, Asia) is shaped
            // like romaji too, and is kept as written.
            let kunrei = units.indices.contains { index in
                (units[index].kind == .kana || units[index].kind == .kanji)
                    && JapaneseAlignment.writesNonHepburn(parts[index], for: generated[index])
            }
            return units.indices.map { index in
                switch units[index].kind {
                case .mark: return generated[index]
                case .latin: return ranges[index].isEmpty ? "" : units[index].source
                default:
                    return JapaneseAlignment.hepburn(parts[index], generated: generated[index],
                                                     respelling: kunrei && units[index].kind == .kanji)
                }
            }
        }

        /// The generated pieces, respelled with `readings` (one per unit).
        func pieces(reading readings: [String]) -> [JapaneseReading.Piece] {
            var result = pieces.map { piece -> JapaneseReading.Piece in
                var piece = piece
                piece.text = ""
                return piece
            }
            for (index, unit) in units.enumerated() where !readings[index].isEmpty {
                if let piece = unit.piece {
                    result[piece].text += readings[index]
                } else {
                    result.append(.init(text: readings[index], location: unit.location, length: unit.length, breakBefore: true))
                }
            }
            return result.sorted { $0.location < $1.location }
        }

        /// Each character's share of `readings` (one per unit), for lines
        /// timed one character at a time: a kana carries its own syllable,
        /// a run of kanji is shared out by syllable (今日 kyo, u), a っ goes
        /// with the kana after it and a ー with the one before.
        func characters(reading readings: [String]) -> [CharacterReading] {
            var result: [CharacterReading] = []
            var carried = ""
            var previousEnd = -1
            var filled: Int?
            for (index, unit) in units.enumerated() {
                var text = readings[index]
                let phrase = phrases[index]
                let adjacent = previousEnd == unit.location
                previousEnd = unit.location + unit.length
                func add(_ range: NSRange, _ text: String) {
                    if !text.isEmpty { filled = result.count }
                    result.append(.init(location: range.location, length: range.length, text: text, phrase: phrase))
                }
                if !carried.isEmpty, adjacent, unit.kind == .kana || unit.kind == .kanji { text = carried + text }
                carried = ""
                switch unit.kind {
                case .mark:
                    if !text.isEmpty, let filled { result[filled].text += text }
                    unit.characters.forEach { add($0, "") }
                case .sokuon:
                    carried = text
                    unit.characters.forEach { add($0, "") }
                case .kana where unit.lengthens:
                    if adjacent, let filled, result[filled].phrase == phrase {
                        result[filled].text += text
                        text = ""
                    }
                    unit.characters.forEach { add($0, text) }
                case .kana:
                    let head = unit.geminated ? 1 : 0
                    for (offset, range) in unit.characters.enumerated() { add(range, offset == min(head, unit.characters.count - 1) ? text : "") }
                case .kanji:
                    zip(unit.characters, JapaneseAlignment.share(text, over: unit.characters.count)).forEach { add($0, $1) }
                case .latin:
                    for (offset, range) in unit.characters.enumerated() { add(range, offset == 0 ? text : "") }
                }
            }
            return result
        }
    }

    /// A character's part of a reading, and the phrase it belongs to.
    struct CharacterReading {
        var location: Int
        var length: Int
        var text: String
        var phrase: Int
    }

    static func analyse(_ text: String, using tokenizer: JapaneseReading.Tokenizer = .init()) -> Analysis {
        let pieces = JapaneseReading.pieces(text, using: tokenizer)
        var units = makeUnits(text, pieces: pieces)
        var generated = [String](repeating: "", count: units.count)
        // Each piece's reading laid on its own units gives every kanji run
        // the system's reading of it.
        for (index, piece) in pieces.enumerated() {
            let members = units.indices.filter { units[$0].piece == index }
            guard let first = members.first else { continue }
            let reading = Reading(piece.text)
            if piece.isJapanese, let ranges = align(reading, members.map { units[$0] }) {
                for (member, range) in zip(members, ranges) {
                    generated[member] = reading.raw(range)
                    if units[member].kind == .kanji { units[member].hint = Array(reading.letters[range]) }
                }
            } else {
                generated[first] = piece.text
            }
        }
        var phrase = -1
        let piecePhrases = pieces.map { piece -> Int in
            if !piece.joinsPrevious || phrase < 0 { phrase += 1 }
            return phrase
        }
        // A kanji nothing read is a phrase of its own.
        let phrases = units.indices.map { index -> Int in
            guard let piece = units[index].piece else { return phrase + 1 + index }
            return piecePhrases[piece]
        }
        return Analysis(pieces: pieces, units: units, generated: generated, phrases: phrases)
    }

    // MARK: - Units

    private enum Class { case kanji, kana, small, sokuon, long, voicing, digit, latin, mark, space }

    private static func classify(_ character: Character) -> Class {
        guard let scalar = character.unicodeScalars.first else { return .mark }
        if character.isWhitespace || scalar.value == 0x30FB || scalar.value == 0xFF65 { return .space }
        switch scalar.value {
        case 0x3063, 0x30C3, 0xFF6F: return .sokuon
        case 0x30FC, 0xFF70: return .long
        case 0x3099...0x309C, 0xFF9E, 0xFF9F: return .voicing
        case 0x3041, 0x3043, 0x3045, 0x3047, 0x3049, 0x3083, 0x3085, 0x3087, 0x308E, 0x3095, 0x3096,
             0x30A1, 0x30A3, 0x30A5, 0x30A7, 0x30A9, 0x30E3, 0x30E5, 0x30E7, 0x30EE, 0x30F5, 0x30F6,
             0x31F0...0x31FF, 0xFF67...0xFF6E:
            // ヵ and ヶ count as small only after kana; alone they are counters.
            return .small
        case 0x3005...0x3007, 0x309D, 0x309E, 0x30FD, 0x30FE: return .kanji // 々 〆 〇 ゝ ゞ ヽ ヾ
        case 0x3041...0x3096, 0x30A1...0x30FA, 0xFF66...0xFF9D: return .kana
        default: break
        }
        if scalar.properties.isIdeographic { return .kanji }
        if character.isNumber { return .digit }
        if character.isLetter { return .latin }
        return .mark
    }

    private static func makeUnits(_ text: String, pieces: [JapaneseReading.Piece]) -> [Unit] {
        var units: [Unit] = []
        var offset = 0, pieceIndex = 0
        for character in text {
            let width = character.utf16.count
            defer { offset += width }
            while pieceIndex < pieces.count, pieces[pieceIndex].location + pieces[pieceIndex].length <= offset { pieceIndex += 1 }
            let piece = pieceIndex < pieces.count && pieces[pieceIndex].location <= offset ? pieceIndex : nil
            let range = NSRange(location: offset, length: width)
            var kind = classify(character)
            guard kind != .space else { continue }
            if let last = units.last, last.piece == piece, last.location + last.length == offset {
                var extends = false
                switch (last.kind, kind) {
                case (.kanji, .kanji), (.kanji, .digit), (.latin, .latin), (.latin, .digit): extends = true
                case (.kanji, .small) where "ヵヶゕゖ".contains(character): extends = true      // 一ヶ月
                case (.latin, .kanji) where last.source.allSatisfy(\.isNumber):
                    units[units.count - 1].kind = .kanji; extends = true            // 2人
                case (.kana, .small) where !last.lengthens, (.kana, .voicing) where !last.lengthens: extends = true
                case (.sokuon, .kana):
                    units[units.count - 1].kind = .kana                             // った
                    units[units.count - 1].geminated = true; extends = true
                default: break
                }
                if extends {
                    units[units.count - 1].length += width
                    units[units.count - 1].source.append(character)
                    units[units.count - 1].characters.append(range)
                    continue
                }
            }
            var lengthens = false
            switch kind {
            case .small: kind = .kana
            case .long: kind = .kana; lengthens = true
            case .voicing: kind = .mark
            case .digit: kind = .latin
            default: break
            }
            let unitKind: Unit.Kind
            switch kind {
            case .kanji: unitKind = .kanji
            case .kana: unitKind = .kana
            case .sokuon: unitKind = .sokuon
            case .latin: unitKind = .latin
            default: unitKind = .mark
            }
            units.append(Unit(kind: unitKind, location: offset, length: width, source: String(character),
                              characters: [range], piece: piece, lengthens: lengthens))
        }
        for index in units.indices {
            let previous = index > 0 && units[index - 1].location + units[index - 1].length == units[index].location
                ? units[index - 1] : nil
            units[index].spellings = spellings(of: units[index], after: previous)
        }
        return units
    }

    // MARK: - Spelling

    private static let kana: [Character: String] = [
        "あ": "a", "い": "i", "う": "u", "え": "e", "お": "o",
        "か": "ka", "き": "ki", "く": "ku", "け": "ke", "こ": "ko", "が": "ga", "ぎ": "gi", "ぐ": "gu", "げ": "ge", "ご": "go",
        "さ": "sa", "し": "shi", "す": "su", "せ": "se", "そ": "so", "ざ": "za", "じ": "ji", "ず": "zu", "ぜ": "ze", "ぞ": "zo",
        "た": "ta", "ち": "chi", "つ": "tsu", "て": "te", "と": "to", "だ": "da", "ぢ": "ji", "づ": "zu", "で": "de", "ど": "do",
        "な": "na", "に": "ni", "ぬ": "nu", "ね": "ne", "の": "no",
        "は": "ha", "ひ": "hi", "ふ": "fu", "へ": "he", "ほ": "ho", "ば": "ba", "び": "bi", "ぶ": "bu", "べ": "be", "ぼ": "bo",
        "ぱ": "pa", "ぴ": "pi", "ぷ": "pu", "ぺ": "pe", "ぽ": "po",
        "ま": "ma", "み": "mi", "む": "mu", "め": "me", "も": "mo", "や": "ya", "ゆ": "yu", "よ": "yo",
        "ら": "ra", "り": "ri", "る": "ru", "れ": "re", "ろ": "ro", "わ": "wa", "ゐ": "i", "ゑ": "e", "を": "o", "ん": "n",
        "ゔ": "vu", "ぁ": "a", "ぃ": "i", "ぅ": "u", "ぇ": "e", "ぉ": "o", "ゃ": "ya", "ゅ": "yu", "ょ": "yo", "ゎ": "wa",
        "ゕ": "ka", "ゖ": "ke",
    ]
    /// Other ways these kana are written in readings.
    private static let variants: [Character: [String]] = [
        "は": ["wa"], "へ": ["e"], "を": ["wo"], "ゐ": ["wi"], "ゑ": ["we"], "ゔ": ["bu"], "ぢ": ["di"], "づ": ["du"],
    ]

    private static func hiragana(_ text: String) -> [Character] {
        var result: [Character] = []
        for scalar in text.precomposedStringWithCompatibilityMapping.unicodeScalars {
            switch scalar.value {
            case 0x30A1...0x30F6: result.append(Character(Unicode.Scalar(scalar.value - 0x60)!))
            case 0x31F0...0x31FF: result.append("ぅ") // small Ainu katakana: closest small kana
            default: result.append(Character(scalar))
            }
        }
        return result
    }

    private static func spellings(of unit: Unit, after previous: Unit?) -> [Spelling] {
        switch unit.kind {
        case .latin:
            return [Spelling(letters: Reading(unit.source).letters, cost: 0)]
        case .kana where unit.lengthens:
            // ー repeats the vowel before it; readings also write ou and ei,
            // a macron (doubled here) or nothing at all.
            guard let vowel = previous?.spellings.first?.letters.last(where: isVowel) else {
                return "aiueo".map { Spelling(letters: [$0], cost: 0.5) } + [Spelling(letters: [], cost: 0.3)]
            }
            var result = lengthenings(of: vowel, written: vowel)
            if vowel == "o" { result.append(Spelling(letters: ["u"], cost: 0.1)) }
            if vowel == "e" { result.append(Spelling(letters: ["i"], cost: 0.1)) }
            return result
        case .kana:
            let characters = hiragana(unit.source).filter { $0 != "っ" }
            guard let head = characters.first, let base = kana[head] else { return [] }
            var forms = [base] + (variants[head] ?? [])
            for small in characters.dropFirst() {
                guard let sound = kana[small] else { continue }
                forms = forms.flatMap { combined($0, with: sound) }
            }
            var result: [Spelling] = []
            for form in forms {
                let letters = Reading(form).letters
                guard let first = letters.first else { continue }
                if !unit.geminated {
                    result.append(Spelling(letters: letters, cost: 0))
                } else if isVowel(first) {
                    result.append(Spelling(letters: letters, cost: 0))
                } else if letters.starts(with: ["c", "h"]) {
                    result.append(Spelling(letters: ["t"] + letters, cost: 0))
                    result.append(Spelling(letters: ["c"] + letters, cost: 0))
                } else {
                    result.append(Spelling(letters: [first] + letters, cost: 0))
                }
            }
            // A vowel that lengthens the one before it (そう, ねえ, せい) may
            // be written, doubled or left out.
            if characters.count == 1, "あいうえお".contains(head), !unit.geminated, let sound = kana[head]?.first,
               previous?.kind == .kana, let vowel = previous?.spellings.first?.letters.last, isVowel(vowel),
               sound == vowel || (vowel == "o" && sound == "u") || (vowel == "e" && sound == "i") {
                return lengthenings(of: vowel, written: sound)
            }
            return result
        case .kanji, .sokuon, .mark:
            return []
        }
    }

    /// A sound that lengthens `vowel`: as written, as the vowel again
    /// (a macron, doubled), or left out.
    private static func lengthenings(of vowel: Character, written: Character) -> [Spelling] {
        var result = [Spelling(letters: [written], cost: 0)]
        if written != vowel { result.append(Spelling(letters: [vowel], cost: 0.1)) }
        result.append(Spelling(letters: [], cost: 0.3))
        return result
    }

    /// A kana followed by a small kana: きゃ kya, しゃ sha, ふぁ fa, ティ ti,
    /// ウィ wi, also written as the two kana (kiya, tei).
    private static func combined(_ head: String, with small: String) -> [String] {
        var result: [String] = []
        let stem = String(head.dropLast())
        if ["ya", "yu", "yo"].contains(small) {
            if head.hasSuffix("i") {
                result.append(["sh", "ch", "j"].contains(stem) ? stem + small.dropFirst() : stem + small)
            } else if !stem.isEmpty {
                result.append(stem + small)
            }
        } else if small == "wa" {
            if head == "ku" || head == "gu" { result.append(String(head.prefix(1)) + "wa") }
        } else {
            switch head {
            case "u": result.append(small == "u" ? "wu" : "w" + small)
            case "i": result.append("y" + small)
            case "ku", "gu": result.append(String(head.prefix(1)) + "w" + small)
            case "te", "de", "to", "do", "fu", "vu", "tsu", "su", "zu": result.append(stem + small)
            default:
                if head.hasSuffix("i") { result.append(["sh", "ch", "j"].contains(stem) ? stem + small : stem + "y" + small) }
                else if !stem.isEmpty { result.append(stem + small) }
            }
        }
        return result + [head + small]
    }

    // MARK: - Alignment

    /// The cheapest way to lay `reading` over `units`, as a range of its
    /// letters for each unit, or nil when the kana cannot all be matched.
    static func align(_ reading: Reading, _ units: [Unit]) -> [Range<Int>]? {
        let letters = reading.letters
        let count = letters.count, width = count + 1
        guard !units.isEmpty else { return count == 0 ? [] : nil }
        var cost = [Double](repeating: .infinity, count: (units.count + 1) * width)
        var back = [Int](repeating: -1, count: (units.count + 1) * width)
        cost[0] = 0
        for (index, unit) in units.enumerated() {
            for start in 0...count {
                let base = cost[index * width + start]
                guard base < .infinity else { continue }
                func relax(_ end: Int, _ step: Double) {
                    let slot = (index + 1) * width + end
                    if base + step < cost[slot] { cost[slot] = base + step; back[slot] = start }
                }
                switch unit.kind {
                case .mark:
                    relax(start, 0)
                case .kana, .latin:
                    for spelling in unit.spellings where matches(letters, at: start, spelling.letters) {
                        relax(start + spelling.letters.count, spelling.cost)
                    }
                    // A reading may leave out the Latin words of the line.
                    if unit.kind == .latin { relax(start, 0.8) }
                case .sokuon:
                    relax(start, 0.2)
                    if start < count, isConsonant(letters[start]), letters[start] != "n",
                       start + 1 == count || letters[start + 1] == letters[start]
                        || (letters[start + 1] == "c" && (letters[start] == "t" || letters[start] == "c")) {
                        relax(start + 1, 0)
                    }
                case .kanji:
                    guard start < count else { continue }
                    for end in (start + 1)...min(count, start + 14 * unit.characters.count + 4) where reading.boundary[end] {
                        relax(end, kanjiCost(letters[start..<end], unit))
                    }
                }
            }
        }
        guard cost[units.count * width + count] < .infinity else { return nil }
        var ranges = [Range<Int>](repeating: 0..<0, count: units.count)
        var end = count
        for index in stride(from: units.count, to: 0, by: -1) {
            let start = back[index * width + end]
            ranges[index - 1] = start..<end
            end = start
        }
        return ranges
    }

    private static func matches(_ letters: [Character], at start: Int, _ spelling: [Character]) -> Bool {
        guard start + spelling.count <= letters.count else { return false }
        return letters[start..<(start + spelling.count)].elementsEqual(spelling)
    }

    private static func kanjiCost(_ part: ArraySlice<Character>, _ unit: Unit) -> Double {
        let syllables = syllableCount(part)
        guard let hint = unit.hint, let shortHint = unit.shortHint else {
            return 0.6 + 0.1 * Double(abs(syllables - 2 * unit.characters.count))
        }
        if part.elementsEqual(hint) { return 0 }
        if abs(part.count - hint.count) <= 2, collapsed(part) == shortHint { return 0.05 }
        return 1 + 0.2 * Double(abs(syllables - syllableCount(hint[...])))
    }

    private static func syllableCount(_ letters: ArraySlice<Character>) -> Int {
        var count = 0
        var previous: Character?
        for (offset, letter) in zip(letters.indices, letters) {
            let next = offset + 1 < letters.endIndex ? letters[offset + 1] : nil
            if isVowel(letter) { count += 1 }
            else if letter == "n", next.map({ !isVowel($0) && $0 != "y" }) ?? true { count += 1 }
            else if let next, next == letter, previous.map(isVowel) ?? true { count += 1 }
            previous = letter
        }
        return max(count, 1)
    }

    /// Letters with long vowels written short, so kyou, kyoo and kyo agree.
    private static func collapsed(_ letters: ArraySlice<Character>) -> String {
        var result = ""
        for letter in letters {
            if let last = result.last, isVowel(letter),
               letter == last || (last == "o" && letter == "u") || (last == "e" && letter == "i") { continue }
            result.append(letter)
        }
        return result
    }

    static func isVowel(_ character: Character) -> Bool { "aiueo".contains(character) }
    private static func isConsonant(_ character: Character) -> Bool { character.isLetter && !isVowel(character) }

    // MARK: - Sharing a kanji reading

    /// Shares a reading of several kanji out by syllable, the way on'yomi
    /// fall: one or two syllables each, and no character starting with ん, a
    /// doubled consonant or a long vowel, and rarely with the second half of
    /// ai, ei or ui or with ku, ki, tsu or chi (以上 i·jou, 最後 sai·go,
    /// 唯一 yui·itsu). Readings of compounds like 今日 cannot be split; they
    /// are spread out evenly so each character keeps a short reading.
    static func share(_ reading: String, over count: Int) -> [String] {
        guard count > 1 else { return [reading] }
        let syllables = morae(of: Array(reading)).map { String($0) }
        guard syllables.count > count else { return (0..<count).map { $0 < syllables.count ? syllables[$0] : "" } }
        let total = syllables.count
        let even = Double(total) / Double(count)
        var best = [[Double]](repeating: [Double](repeating: .infinity, count: total + 1), count: count + 1)
        var cut = [[Int]](repeating: [Int](repeating: 0, count: total + 1), count: count + 1)
        best[0][0] = 0
        for group in 1...count {
            for end in group...(total - (count - group)) {
                for start in (group - 1)..<end where best[group - 1][start] < .infinity {
                    let size = end - start
                    var step = 0.3 * abs(Double(size) - even) + 0.8 * Double(max(0, size - 2))
                    if group > 1 { step += penalty(starting: start, in: syllables) }
                    if best[group - 1][start] + step < best[group][end] {
                        best[group][end] = best[group - 1][start] + step
                        cut[group][end] = start
                    }
                }
            }
        }
        var parts = [String](repeating: "", count: count)
        var end = total
        for group in stride(from: count, to: 0, by: -1) {
            let start = cut[group][end]
            parts[group - 1] = syllables[start..<end].joined()
            end = start
        }
        return parts
    }

    private static func penalty(starting index: Int, in syllables: [String]) -> Double {
        let syllable = syllables[index].lowercased().filter { $0 != "'" && $0 != "’" }
        guard let letter = syllable.first else { return 0 }
        if syllable == "n" || (syllable.count == 1 && !isVowel(letter)) { return 3 }
        let previous = syllables[index - 1].lowercased()
        if syllable.count == 1, let vowel = previous.last(where: isVowel) {
            // A vowel lengthens a full syllable before it (kyo·u), but after
            // a lone vowel it can start the next reading (唯一 yu·i·i·tsu).
            if previous.count > 1, letter == vowel || (vowel == "o" && letter == "u") || (vowel == "e" && letter == "i") { return 3 }
            if letter == "i" || letter == "u" { return 1 }
        }
        if ["ku", "ki", "tsu", "chi", "tu", "ti"].contains(syllable) { return 1 }
        return 0
    }

    // MARK: - Readings

    /// A reading's letters, normalised to Hepburn one syllable at a time,
    /// with where a kanji's reading may end and the source spelling of each
    /// syllable.
    struct Reading {
        private(set) var letters: [Character] = []
        /// `boundary[i]`: a syllable can end before letter `i`.
        private(set) var boundary: [Bool] = [true]
        private var segments: [Segment] = []
        private struct Segment {
            var raw: [Character]
            var start: Int
            var count: Int
            var verbatim: Bool
        }

        init(_ text: String) {
            for token in text.split(whereSeparator: { $0.isWhitespace || "-‐・/".contains($0) }) {
                for syllable in JapaneseAlignment.morae(of: Array(token.filter { $0.isLetter || $0.isNumber || "'’".contains($0) })) {
                    let lower = Array(String(syllable).lowercased())
                    let normal = JapaneseAlignment.normalized(lower)
                    guard !normal.isEmpty else { continue }
                    segments.append(Segment(raw: syllable, start: letters.count, count: normal.count,
                                            verbatim: normal == lower && lower.count == syllable.count))
                    letters += normal
                    boundary += [Bool](repeating: false, count: normal.count - 1) + [true]
                }
            }
            // ん before a vowel can end a reading inside a syllable (ren·ai).
            for index in letters.indices.dropFirst() where letters[index - 1] == "n" && JapaneseAlignment.isVowel(letters[index]) {
                boundary[index] = true
            }
        }

        /// The source spelling of `range` of the letters.
        func raw(_ range: Range<Int>) -> String {
            var text = ""
            for segment in segments where segment.start < range.upperBound && segment.start + segment.count > range.lowerBound {
                if segment.verbatim {
                    let lower = max(range.lowerBound, segment.start) - segment.start
                    let upper = min(range.upperBound, segment.start + segment.count) - segment.start
                    text += String(segment.raw[lower..<upper])
                } else if range.contains(segment.start) {
                    text += String(segment.raw)
                }
            }
            return text
        }
    }

    /// Splits romaji into syllables: consonants with their vowel, ん (with
    /// the apostrophe that may follow it, kan'i), the first of a doubled
    /// consonant, and runs of digits.
    static func morae(of characters: [Character]) -> [[Character]] {
        let lower = characters.map { Character($0.lowercased()) }
        var result: [[Character]] = [], current: [Character] = []
        var index = 0
        while index < characters.count {
            let letter = plain(lower[index])
            let next = index + 1 < characters.count ? plain(lower[index + 1]) : nil
            if letter == "'" || letter == "’" {
                index += 1
                continue
            }
            if current.isEmpty, letter == "n", next == "'" || next == "’" {
                result.append([characters[index], characters[index + 1]])
                index += 2
                continue
            }
            if isVowel(letter) {
                current.append(characters[index])
                result.append(current)
                current = []
            } else if lower[index].isNumber {
                if !current.isEmpty { result.append(current); current = [] }
                var end = index
                while end < characters.count, lower[end].isNumber { end += 1 }
                result.append(Array(characters[index..<end]))
                index = end
                continue
            } else if current.isEmpty, letter == "n" || (letter == "m" && next.map { "bpm".contains($0) } == true),
                      next.map({ !isVowel($0) && $0 != "y" }) ?? true {
                result.append([characters[index]])                       // ん
            } else if current.isEmpty, letter != "n", letter.isLetter, let next,
                      next == letter || (next == "c" && (letter == "t" || letter == "c")) {
                result.append([characters[index]])                       // っ
            } else {
                current.append(characters[index])
            }
            index += 1
        }
        if !current.isEmpty { result.append(current) }
        return result
    }

    /// A vowel without its accent (ō o, é e).
    private static func plain(_ letter: Character) -> Character {
        guard !letter.isASCII else { return letter }
        return String(letter).applyingTransform(.stripDiacritics, reverse: false)?.first ?? letter
    }

    private static let onsets: [String: String] = [
        "si": "shi", "ti": "chi", "tu": "tsu", "hu": "fu", "zi": "ji", "di": "ji", "du": "zu", "dzu": "zu", "dji": "ji",
        "sya": "sha", "syu": "shu", "syo": "sho", "sye": "she", "tya": "cha", "tyu": "chu", "tyo": "cho", "tye": "che",
        "cya": "cha", "cyu": "chu", "cyo": "cho", "zya": "ja", "zyu": "ju", "zyo": "jo", "zye": "je",
        "jya": "ja", "jyu": "ju", "jyo": "jo", "jye": "je", "dya": "ja", "dyu": "ju", "dyo": "jo",
    ]

    /// One syllable in Hepburn with long vowels doubled: syo sho, tu tsu,
    /// hu fu, kyō kyoo, and m before b, p or m as n.
    static func normalized(_ syllable: [Character]) -> [Character] {
        var letters: [Character] = []
        for letter in syllable {
            switch letter {
            case "ā", "â": letters += ["a", "a"]
            case "ī", "î": letters += ["i", "i"]
            case "ū", "û": letters += ["u", "u"]
            case "ē", "ê": letters += ["e", "e"]
            case "ō", "ô": letters += ["o", "o"]
            case "'", "’": continue
            default: letters.append(plain(letter))
            }
        }
        if letters == ["m"] { return ["n"] }
        for length in [3, 2] where letters.count >= length {
            if let onset = onsets[String(letters[0..<length])] {
                return Array(onset) + letters[length...]
            }
        }
        return letters
    }

    // MARK: - Showing a source's reading

    /// A unit's part of a source or saved reading, written in Hepburn like
    /// the app's own readings. Where it spells the same sounds as the
    /// generated reading (NetEase's de syo u, i jyo u, hu ri), the generated
    /// spelling is shown, in the source's capitals; that also keeps
    /// loanword spellings such as ティ ti. A reading that differs is the
    /// source's own, as written, so a source that writes は as ha still
    /// shows ha. With `respelling` (a kanji's reading, in a reading that
    /// writes Kunrei-shiki elsewhere), its Kunrei-shiki onsets are respelled
    /// where that is unambiguous (`respelled`): ma zi de syo u shows maji.
    static func hepburn(_ source: String, generated: String, respelling: Bool) -> String {
        guard !source.isEmpty else { return source }
        if !generated.isEmpty, Reading(source).letters == Reading(generated).letters {
            return cased(generated, like: source)
        }
        return respelling ? respelled(source) : source
    }

    /// Whether `source` spells the sounds of `generated`, the app's reading
    /// of the same characters, with a syllable Hepburn writes otherwise
    /// (`onsets`): de syo u for でしょう, hu ri for 降り, hi to tu for 一つ.
    /// That shows the reading uses Kunrei-shiki or a spelling like it, so
    /// its other syllables can be read the same way. Loanword spellings
    /// both write (ティ ti) are not a sign of it.
    static func writesNonHepburn(_ source: String, for generated: String) -> Bool {
        guard !source.isEmpty, !generated.isEmpty, Reading(source).letters == Reading(generated).letters else { return false }
        return nonHepburnSyllables(in: source) > nonHepburnSyllables(in: generated)
    }

    /// How many syllables of `text` start with an onset Hepburn writes otherwise.
    private static func nonHepburnSyllables(in text: String) -> Int {
        morae(of: Array(text)).filter { syllable in
            let letters = String(syllable.map { plain(Character($0.lowercased())) })
            return [3, 2].contains { letters.count >= $0 && onsets[String(letters.prefix($0))] != nil }
        }.count
    }

    /// `text` with the Kunrei-shiki and Nihon-shiki onsets that Hepburn never
    /// writes respelled (si shi, zi ji, hu fu, syo sho, tya cha, zyo jo).
    /// ti, tu, di, du, tyu and dyu stay, because Hepburn writes loanwords
    /// that way (ティ ti, ディ di, デュ dyu), and so does anything that is not
    /// romaji at all, such as an English word sung for a kanji ("music").
    /// Vowels, macrons and capitals are kept. Some English words are shaped
    /// like romaji (passion, vision, Asia), so this is only for a reading
    /// that shows it writes Kunrei-shiki (`writesNonHepburn`).
    static func respelled(_ text: String) -> String {
        let syllables = morae(of: Array(text))
        guard isRomaji(syllables) else { return text }
        return syllables.map { syllable -> String in
            let letters = syllable.map { plain(Character($0.lowercased())) }
            for length in [3, 2] where letters.count >= length {
                guard let onset = unambiguousOnsets[String(letters[0..<length])] else { continue }
                // Each entry ends with the vowel, which keeps its own spelling.
                return cased(String(onset.dropLast()), like: String(syllable)) + String(syllable[(length - 1)...])
            }
            return String(syllable)
        }.joined()
    }

    private static let unambiguousOnsets = onsets.filter { !["ti", "tu", "di", "du", "tyu", "dyu"].contains($0.key) }

    /// What comes before the vowel of a syllable in romaji, in any system.
    private static let romajiOnsets: Set<String> = [
        "", "k", "g", "s", "z", "t", "d", "n", "h", "b", "p", "m", "y", "r", "w", "f", "j", "v",
        "sh", "ch", "ts", "dz", "dj", "ky", "gy", "sy", "zy", "jy", "ty", "dy", "cy", "ny", "hy", "by", "py", "my", "ry",
        "fy", "kw", "gw",
    ]

    /// Whether every syllable is one romaji writes: a known onset and its
    /// vowel, ん, or the first of a doubled consonant.
    private static func isRomaji(_ syllables: [[Character]]) -> Bool {
        let parts = syllables.map { $0.map { plain(Character($0.lowercased())) }.filter { $0 != "'" && $0 != "’" } }
        return parts.indices.allSatisfy { index in
            let part = parts[index]
            guard let last = part.last else { return true }
            if isVowel(last) { return romajiOnsets.contains(String(part.dropLast())) }
            if part == ["n"] { return true }
            guard part.count == 1, index + 1 < parts.count, let next = parts[index + 1].first else { return false }
            return last == "m" ? "bpm".contains(next) : next == last || (next == "c" && last == "t")
        }
    }

    /// `text` in the capitals of `source`: all capitals, a capital first
    /// letter, or as it is.
    private static func cased(_ text: String, like source: String) -> String {
        let letters = source.filter(\.isLetter)
        guard let first = letters.first, first.isUppercase else { return text }
        if letters.count > 1, letters.allSatisfy(\.isUppercase) { return text.uppercased() }
        return text.prefix(1).uppercased() + text.dropFirst()
    }
}
