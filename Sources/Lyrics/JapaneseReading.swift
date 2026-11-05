import CoreFoundation
import Foundation

/// Japanese romaji from the system tokenizer. It reads each phrase of a line
/// at once, because kanji need their context (今日 kyou, 大人 otona), and its
/// Latin transcription is tidied into Hepburn with wapuro-style long vowels:
/// the particles は and へ read wa and e, っ doubles the next consonant across
/// token boundaries, ー repeats its vowel, small kana join their syllable, and
/// inflections are written with their stem (思った omotta, 会いたい aitai).
enum JapaneseReading {
    /// One romanised stretch of the source, with the UTF-16 range it reads.
    struct Piece: Equatable {
        var text: String
        var location: Int
        var length: Int
        /// Written straight after the previous piece, with no space.
        var joinsPrevious = false
        /// A space or the start of the line comes before this piece.
        var breakBefore = false
        var isJapanese = true
    }

    /// The system tokenizer, kept for reading many lines: creating one
    /// costs far more than reading a line with it. Use one per thread.
    final class Tokenizer {
        fileprivate let tokenizer: CFStringTokenizer
        init() {
            let locale = CFLocaleCreate(kCFAllocatorDefault, CFLocaleIdentifier(rawValue: "ja" as CFString))
            tokenizer = CFStringTokenizerCreate(kCFAllocatorDefault, "" as CFString, CFRange(location: 0, length: 0),
                                                kCFStringTokenizerUnitWordBoundary, locale)
        }
    }

    static func romanize(_ text: String) -> String { joined(pieces(text)) }

    static func joined(_ pieces: [Piece]) -> String {
        var result = ""
        for piece in pieces where !piece.text.isEmpty {
            if !result.isEmpty && !piece.joinsPrevious { result += " " }
            result += piece.text
        }
        return result
    }

    static func pieces(_ text: String, using tokenizer: Tokenizer = Tokenizer()) -> [Piece] {
        let tokens = tokenize(text, using: tokenizer.tokenizer)
        var pieces: [Piece] = []
        var lastSource = ""
        var atBreak = true
        var pendingSokuon = false
        // は or へ read ha or he begins a word (はなれないで hanarenaide), and
        // nan is written with what follows (何でも nandemo, 何度 nando).
        var startsWord = false
        for (index, token) in tokens.enumerated() {
            switch token.kind {
            case .space:
                atBreak = true; pendingSokuon = false; startsWord = false
            case .mark:
                pendingSokuon = false; startsWord = false
                let mark = punctuation(token.source)
                guard !mark.isEmpty else { continue }
                pieces.append(Piece(text: mark, location: token.location, length: token.length,
                                    joinsPrevious: !atBreak && !pieces.isEmpty, breakBefore: atBreak, isJapanese: false))
                atBreak = false
            case .latin:
                pieces.append(Piece(text: token.source, location: token.location, length: token.length,
                                    breakBefore: atBreak, isJapanese: false))
                atBreak = false; pendingSokuon = false; startsWord = false
            case .japanese:
                var reading = romaji(token, at: index, in: tokens, tokenizer: tokenizer.tokenizer)
                // Nothing joins onto a kanji the tokenizer could not read.
                let continuesPrevious = !atBreak && pieces.last.map { $0.isJapanese && !$0.text.isEmpty } == true
                if continuesPrevious, reading == "~tsu" || reading.hasPrefix("~") || isLongVowelMark(reading) {
                    // Small kana, a lone っ or ー belong to the previous syllable.
                    var previous = pieces.removeLast()
                    previous.text = isLongVowelMark(reading) ? lengthened(previous.text, by: reading) : previous.text + reading
                    previous.length = token.location + token.length - previous.location
                    pieces.append(previous)
                    // はぁ and はっ are sighs and gasps, not the start of a word.
                    startsWord = false
                    continue
                }
                if reading == "~tsu" { pendingSokuon = true; atBreak = false; continue }
                if isLongVowelMark(reading) { continue }
                if pendingSokuon { reading = "~tsu" + reading; pendingSokuon = false }
                pieces.append(Piece(text: reading, location: token.location, length: token.length,
                                    joinsPrevious: continuesPrevious && (startsWord || joins(token.source, after: lastSource)),
                                    breakBefore: atBreak))
                lastSource = reading.isEmpty ? "" : token.source
                startsWord = (token.source == "は" && reading == "ha") || (token.source == "へ" && reading == "he")
                    || (token.source == "何" && reading == "nan" && index + 1 < tokens.count
                        && (counters.contains(tokens[index + 1].source) || "でてだ".contains(tokens[index + 1].source.prefix(1))))
                atBreak = false
            }
        }
        for index in pieces.indices where pieces[index].isJapanese {
            pieces[index].text = smallKana(pieces[index].text)
        }
        resolveSokuon(&pieces)
        return pieces.filter { !$0.text.isEmpty }
    }

    // MARK: - Tokens

    private enum Kind { case space, mark, latin, japanese }
    private struct Token {
        var source: String
        var latin: String?
        var location: Int
        var length: Int
        var kind: Kind
    }

    /// Each phrase is read on its own: across a space or a Latin word the
    /// tokenizer loses the thread (行 after "Yeah yeah" reads kou, 君 after
    /// "ねえ " the suffix kun). The tokens keep their places in the line.
    private static func tokenize(_ text: String, using tokenizer: CFStringTokenizer) -> [Token] {
        let ns = text as NSString
        let length = ns.length
        guard length > 0 else { return [] }
        // The tokenizer reads full-width digits apart from what they count
        // (２|人 nin), so it is given them as ASCII digits (2人 futari). Each
        // is one UTF-16 unit either way, so the ranges are the same, and the
        // tokens keep the digits as written.
        let string = String(String.UnicodeScalarView(text.unicodeScalars.map(asciiDigit))) as CFString
        var tokens: [Token] = []
        func add(_ location: Int, _ count: Int, latin: String?) {
            let source = ns.substring(with: NSRange(location: location, length: count))
            tokens.append(Token(source: source, latin: latin, location: location, length: count, kind: kind(of: source)))
        }
        for run in runs(of: text) {
            guard !run.isSpace else { add(run.location, run.length, latin: nil); continue }
            CFStringTokenizerSetString(tokenizer, string, CFRange(location: run.location, length: run.length))
            let end = run.location + run.length
            var cursor = run.location
            while CFStringTokenizerAdvanceToNextToken(tokenizer).rawValue != 0 {
                let range = CFStringTokenizerGetCurrentTokenRange(tokenizer)
                guard range.location != kCFNotFound, range.length > 0, range.location >= cursor,
                      range.location + range.length <= end else { continue }
                if range.location > cursor { add(cursor, range.location - cursor, latin: nil) }
                add(range.location, range.length, latin: CFStringTokenizerCopyCurrentTokenAttribute(
                    tokenizer, kCFStringTokenizerAttributeLatinTranscription) as? String)
                cursor = range.location + range.length
            }
            if cursor < end { add(cursor, end - cursor, latin: nil) }
        }
        return tokens
    }

    private struct Run { var location: Int; var length: Int; var isSpace: Bool }

    /// Spaces, and runs of text that change between Latin letters and
    /// Japanese. Digits and punctuation go with the text around them (2人).
    private static func runs(of text: String) -> [Run] {
        var runs: [Run] = []
        var current: Run?
        var script: Bool?
        var offset = 0
        for scalar in text.unicodeScalars {
            let width = scalar.utf16.count
            let isSpace = scalar.properties.isWhitespace || separators.contains(scalar)
            let japanese: Bool? = isJapanese(scalar) ? true : scalar.properties.isAlphabetic ? false : nil
            if var run = current, run.isSpace == isSpace,
               isSpace || japanese == nil || script == nil || japanese == script {
                run.length += width
                current = run
                if !isSpace, let japanese { script = japanese }
            } else {
                if let run = current { runs.append(run) }
                current = Run(location: offset, length: width, isSpace: isSpace)
                script = isSpace ? nil : japanese
            }
            offset += width
        }
        if let run = current { runs.append(run) }
        return runs
    }

    private static func kind(of source: String) -> Kind {
        if source.unicodeScalars.allSatisfy({ $0.properties.isWhitespace || separators.contains($0) }) { return .space }
        if source.unicodeScalars.contains(where: isJapanese) { return .japanese }
        if source.contains(where: { $0.isLetter || $0.isNumber }) { return .latin }
        return .mark
    }

    /// The middle dot separates words in names (ジョン・レノン).
    private static let separators: Set<Unicode.Scalar> = ["\u{30FB}", "\u{FF65}"]

    static func isJapanese(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x30FB: return false
        case 0x3041...0x309F, 0x30A1...0x30FF, 0x31F0...0x31FF, 0xFF66...0xFF9F: return true // kana, ー
        case 0x3005...0x3007, 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF, 0x20000...0x3134F: return true
        default: return false
        }
    }

    static func isHiragana(_ scalar: Unicode.Scalar) -> Bool { (0x3041...0x309F).contains(scalar.value) }

    /// A full-width digit (０–９) as its ASCII digit; anything else as it is.
    private static func asciiDigit(_ scalar: Unicode.Scalar) -> Unicode.Scalar {
        guard (0xFF10...0xFF19).contains(scalar.value) else { return scalar }
        return Unicode.Scalar(scalar.value - 0xFF10 + 0x30) ?? scalar
    }

    // MARK: - Romaji

    /// Words that keep a historical は (pronounced wa) inside a single token.
    private static let exceptions = [
        "こんにちは": "konnichiwa", "こんばんは": "konbanwa", "もしくは": "moshikuwa", "あるいは": "aruiwa",
    ]
    /// A particle written after a quotation or bracket is still a particle (「愛」は).
    private static let closingMarks: Set<String> = ["」", "』", "）", ")", "】", "〉", "》", "”", "’", "\""]
    /// Counters after which 何 reads nan (何度 nando, 何人 nannin).
    private static let counters: Set<String> = [
        "度", "回", "人", "年", "時", "日", "月", "歳", "才", "本", "枚", "個", "番", "階", "分", "秒", "週",
        "件", "杯", "匹", "冊", "台", "色", "語", "曲", "倍", "十", "百", "千", "万",
    ]
    /// Accents the tokenizer only produces when it falls back to Mandarin
    /// pinyin (2人 2rén); Japanese long vowels use macrons and circumflexes.
    private static let pinyinTones = Set("áàǎéèěíìǐóòǒúùǔǘǚǜÁÀǍÉÈĚÍÌǏÓÒǑÚÙǓǗǙǛ")

    private static func romaji(_ token: Token, at index: Int, in tokens: [Token], tokenizer: CFStringTokenizer) -> String {
        if let fixed = exceptions[token.source] { return fixed }
        let before = index > 0 ? tokens[index - 1] : nil
        let after = index + 1 < tokens.count ? tokens[index + 1] : nil
        switch token.source {
        case "は", "へ":
            // A particle is written straight after its word (君の名は, 未来へ),
            // and starts a line that goes on from the one before (は笑った,
            // へ行こう). At the start of a phrase は and へ can also begin a
            // word (はなれないで) or be an interjection (は？).
            let attached = before.map { $0.kind == .japanese || $0.kind == .latin || closingMarks.contains($0.source) } ?? false
            let phrase = tokens[index...].prefix { $0.kind == .japanese }.map(\.source).joined()
            let particle = attached
                || (!wordStarts.contains(where: phrase.hasPrefix) && leadsAsParticle(at: index, in: tokens, tokenizer: tokenizer))
            if token.source == "は" { return particle ? "wa" : "ha" }
            return particle ? "e" : "he"
        case "月" where before.map(isNumber) == true, "日" where before.map(isNumber) == true,
             "日間" where before.map(isNumber) == true:
            return date(token.source, number: before?.source ?? "")
        case "君":
            // Alone 君 is kimi in lyrics; the tokenizer reads it as the suffix
            // kun after a word it takes for a name (ねえ君 nee kun).
            return "kimi"
        case "私":
            // Sung 私 is watashi; the tokenizer gives the formal watakushi.
            return "watashi"
        case "何":
            // The tokenizer always says nan. It is nan before d, n and t
            // sounds and counters (何でも, 何の, 何度) and nani elsewhere.
            if let after, after.kind == .japanese,
               counters.contains(after.source) || after.source.first.map({ "だでどなのてと".contains($0) }) == true {
                return "nan"
            }
            return "nani"
        default:
            break
        }
        let hasHan = token.source.unicodeScalars.contains { $0.properties.isIdeographic }
        let raw: String
        if let latin = token.latin, !latin.isEmpty, !(hasHan && latin.contains(where: pinyinTones.contains)) {
            raw = latin
        } else if !hasHan {
            raw = token.source.applyingTransform(.toLatin, reverse: false) ?? token.source
        } else {
            // A kanji the tokenizer cannot read (simplified Chinese 爱) gets
            // no reading rather than a Mandarin one or the kanji itself;
            // counted people (2人) are read the Japanese way.
            return counted(token.source, tokenizer: tokenizer) ?? ""
        }
        return hepburn(raw)
    }

    /// Whether the は or へ at `index`, which begins a phrase, is the
    /// particle, from the tokens after it. Before a word in kanji, katakana,
    /// digits or Latin letters it is (は笑った wa waratta, へ行こう e ikou).
    /// Before hiragana it is only where the tokenizer keeps the two apart
    /// even on their own (は|どこ): it splits a word such as はなれ off for
    /// what follows (は|なれ|ないで). A は followed by a particle of one kana
    /// or an ending, and then nothing in hiragana, begins a word the
    /// tokenizer took apart (はね hane, はよ hayo); a longer particle it
    /// splits in two still follows the particle (は|で|も wa demo). Before a
    /// pause, punctuation or nothing it is an interjection (は？).
    private static func leadsAsParticle(at index: Int, in tokens: [Token], tokenizer: CFStringTokenizer) -> Bool {
        guard index + 1 < tokens.count else { return false }
        let token = tokens[index], next = tokens[index + 1]
        guard next.kind == .japanese || next.kind == .latin, let first = next.source.unicodeScalars.first,
              !continuations.contains(first) else { return false }
        guard isHiragana(first) else { return true }
        // へ does come before another particle (へと, への).
        if token.source == "は", next.source.count == 1 && particles.contains(next.source) || endings.contains(next.source) {
            let more = index + 2 < tokens.count && tokens[index + 2].kind == .japanese
                && tokens[index + 2].source.unicodeScalars.first.map(isHiragana) == true
            if !more { return false }
        }
        return tokenCount(token.source + next.source, tokenizer: tokenizer) > 1
    }

    /// How words beginning with は or へ start where the tokenizer reads
    /// them as the particle and a word (は|しれ for はしれ, 走れ), so that
    /// every form of them reads whole (はぐして, はぐしよう, へりくつを).
    private static let wordStarts = ["はしれ", "はくしゅ", "はぐし", "へりくつ", "へ理屈"]

    /// 月 and 日 after a number, whose readings the tokenizer guesses: the
    /// month is gatsu (it says now gatsu, now tsuki: 3月 gatsu, 4月 tsuki),
    /// and a day ka from the 2nd to the 10th and on the 14th, 20th and 24th
    /// (futsuka, tooka, hatsuka) and nichi otherwise (365日), where it says
    /// ka throughout.
    private static func date(_ counter: String, number: String) -> String {
        guard counter != "月" else { return "gatsu" }
        let value = Int(String(String.UnicodeScalarView(number.unicodeScalars.map(asciiDigit)))) ?? 0
        let day = (2...10).contains(value) || [14, 20, 24].contains(value) ? "ka" : "nichi"
        return counter == "日間" ? day + "kan" : day
    }

    /// A token of ASCII or full-width digits.
    private static func isNumber(_ token: Token) -> Bool {
        !token.source.isEmpty && token.source.unicodeScalars.allSatisfy {
            (0x30...0x39).contains($0.value) || (0xFF10...0xFF19).contains($0.value)
        }
    }

    /// Small kana, っ, ー and repeat marks carry on the syllable before them
    /// (はぁ, はっ, はー), so they never follow a particle.
    private static let continuations = Set("ぁぃぅぇぉっゃゅょゎゕゖゝゞァィゥェォッャュョヮヵヶヽヾーｰ".unicodeScalars)

    /// How many tokens the tokenizer makes of a short text on its own.
    private static func tokenCount(_ text: String, tokenizer: CFStringTokenizer) -> Int {
        CFStringTokenizerSetString(tokenizer, text as CFString, CFRange(location: 0, length: (text as NSString).length))
        var count = 0
        while CFStringTokenizerAdvanceToNextToken(tokenizer).rawValue != 0 { count += 1 }
        return count
    }

    /// Digits with kanji, which the tokenizer reads as Mandarin: 1人 hitori,
    /// 2人 futari, otherwise the digits and the kanji's own reading.
    private static func counted(_ source: String, tokenizer: CFStringTokenizer) -> String? {
        var result = ""
        var rest = Substring(source)
        while !rest.isEmpty {
            let digits = rest.prefix { $0.isNumber }
            rest = rest.dropFirst(digits.count)
            var kanji = rest.prefix { !$0.isNumber }
            rest = rest.dropFirst(kanji.count)
            let number = String(digits).applyingTransform(.fullwidthToHalfwidth, reverse: false) ?? String(digits)
            if kanji.hasPrefix("人"), number == "1" || number == "2" {
                result += number == "1" ? "hitori" : "futari"
                kanji = kanji.dropFirst()
            } else {
                result += number
            }
            guard !kanji.isEmpty else { continue }
            guard let reading = transcription(String(kanji), tokenizer: tokenizer) else { return nil }
            result += reading
        }
        return result.isEmpty || result.allSatisfy(\.isNumber) ? nil : result
    }

    /// The tokenizer's reading of a short text on its own, if every token
    /// has a Japanese one.
    private static func transcription(_ text: String, tokenizer: CFStringTokenizer) -> String? {
        CFStringTokenizerSetString(tokenizer, text as CFString, CFRange(location: 0, length: (text as NSString).length))
        var reading = ""
        while CFStringTokenizerAdvanceToNextToken(tokenizer).rawValue != 0 {
            guard let latin = CFStringTokenizerCopyCurrentTokenAttribute(tokenizer, kCFStringTokenizerAttributeLatinTranscription) as? String,
                  !latin.isEmpty, !latin.contains(where: pinyinTones.contains) else { return nil }
            reading += hepburn(latin)
        }
        return reading.isEmpty ? nil : reading
    }

    private static func hepburn(_ latin: String) -> String {
        guard !isLongVowelMark(latin) else { return latin }
        var text = latin.lowercased()
        // Long vowels are written out, as the rest of the reading is (kyou).
        for (macron, plain) in [("ā", "aa"), ("ī", "ii"), ("ū", "uu"), ("ē", "ee"), ("ō", "oo"),
                                ("â", "aa"), ("î", "ii"), ("û", "uu"), ("ê", "ee"), ("ô", "oo")] {
            text = text.replacingOccurrences(of: macron, with: plain)
        }
        text = text.replacingOccurrences(of: "dzu", with: "zu")
            .replacingOccurrences(of: "dji", with: "ji")
            .replacingOccurrences(of: "dj(?=[auo])", with: "j", options: .regularExpression)
            // The apostrophe only separates n from a following vowel or y.
            .replacingOccurrences(of: "n'(?![aiueoy])", with: "n", options: .regularExpression)
        return lengthened("", by: text)
    }

    private static func isLongVowelMark(_ text: String) -> Bool {
        !text.isEmpty && text.allSatisfy { $0 == "ー" || $0 == "ｰ" }
    }

    /// Appends `marks`, writing each ー as a repeat of the vowel before it.
    private static func lengthened(_ text: String, by marks: String) -> String {
        var result = text
        for character in marks {
            if character == "ー" || character == "ｰ" {
                if let vowel = result.last, "aiueo".contains(vowel) { result.append(vowel) }
            } else {
                result.append(character)
            }
        }
        return result
    }

    private static let smallKanaRules: [(String, String)] = [
        ("vu~y([auo])", "vy$1"), ("vu~([aieo])", "v$1"),
        ("([kg])u~a", "$1wa"), ("([kg])u~([ieo])", "$1w$2"),
        ("fu~y([auo])", "fy$1"), ("fu~([aieo])", "f$1"), ("tsu~([aieo])", "ts$1"),
        ("([sz])u~i", "$1i"), ("([td])o~u", "$1u"), ("([td])e~y([auo])", "$1y$2"), ("([td])e~i", "$1i"),
        ("(sh|ch|j)i~e", "$1e"), ("(sh|ch|j)i~y([auo])", "$1$2"),
        ("([kgnhbpmr])i~e", "$1ye"), ("([kgnhbpmr])i~y([auo])", "$1y$2"),
        ("(^|[^a-z])u~([aieo])", "$1w$2"), ("(^|[^a-z])i~e", "$1ye"),
        ("~(?=[aiueo]|y[auo]|wa|k[ae])", ""), ("~(?!tsu)", ""),
    ]

    /// Small kana: ヴァ va, ウィ wi, ティ ti, ねぇ nee. っ is left for sokuon.
    private static func smallKana(_ text: String) -> String {
        guard text.contains("~") else { return text }
        var result = text
        for (pattern, template) in smallKanaRules {
            result = result.replacingOccurrences(of: pattern, with: template, options: .regularExpression)
        }
        return result
    }

    /// っ doubles the consonant that follows it, even in the next token
    /// (思っ + た = omotta). Before a vowel or a pause it is not written.
    private static func resolveSokuon(_ pieces: inout [Piece]) {
        for index in pieces.indices {
            while let marker = pieces[index].text.range(of: "~tsu") {
                let rest = pieces[index].text[marker.upperBound...]
                if !rest.isEmpty {
                    pieces[index].text.replaceSubrange(marker, with: geminate(String(rest)) ?? "")
                    continue
                }
                if index + 1 < pieces.count, pieces[index + 1].isJapanese, !pieces[index + 1].breakBefore,
                   let double = geminate(pieces[index + 1].text) {
                    pieces[index].text.replaceSubrange(marker, with: double)
                    pieces[index + 1].joinsPrevious = true
                } else {
                    pieces[index].text.removeSubrange(marker)
                }
            }
        }
    }

    private static func geminate(_ following: String) -> String? {
        if following.hasPrefix("ch") { return "t" }
        guard let first = following.first, "bcdfghjklmpqrstvwxyz".contains(first) else { return nil }
        return String(first)
    }

    static func punctuation(_ source: String) -> String {
        var result = ""
        for character in source {
            switch character {
            case "。", "｡", "．": result += "."
            case "、", "､", "，": result += ","
            case "！": result += "!"
            case "？": result += "?"
            case "〜", "～": result += "~"
            case "「", "」", "『", "』", "｢", "｣", "【", "】", "（", "）", "(", ")", "〈", "〉", "《", "》",
                 "\"", "“", "”", "‘", "’", "[", "]":
                continue
            default: result.append(character)
            }
        }
        return result
    }

    // MARK: - Spacing

    private static let particles: Set<String> = [
        "は", "が", "を", "に", "へ", "と", "で", "も", "の", "や", "か", "ね", "よ", "な", "わ", "ぞ",
        "じゃ", "って", "から", "まで", "より", "けど", "けれど", "だけ", "ほど", "など", "こそ",
        "でも", "とか", "ので", "のに", "しか", "ばかり",
    ]
    /// Endings that only ever follow a verb or adjective stem.
    private static let endings: Set<String> = [
        "た", "て", "てる", "てた", "ちゃ", "ちゃう", "ちゃっ", "じゃう", "じゃっ", "たい", "たく", "たかっ",
        "たけれ", "たら", "たり", "ます", "まし", "ませ", "ましょう", "ません", "れ", "られ", "せ", "させ",
        "ば", "ず", "ぬ",
    ]
    private static let negatives: Set<String> = ["ない", "なかっ", "なく", "なきゃ", "なけれ", "ないで"]
    private static let voicedEndings: Set<String> = ["だ", "で", "だっ", "だら", "だり"]

    /// Whether a word only ever follows another, so a space before it
    /// never begins a phrase: a particle or an ending (は, から, た, たい,
    /// として). A negative is left out, as ない also stands on its own
    /// (もう ない, へんじ ない).
    static func followsAWord(_ word: String) -> Bool {
        particles.contains(word) || endings.contains(word) || voicedEndings.contains(word) || word == "として" || word == "という"
    }

    /// Small kana, っ, ー and repeat marks, which carry on the syllable before them.
    static func carriesOn(_ scalar: Unicode.Scalar) -> Bool { continuations.contains(scalar) }

    /// Whether a word of the system's word segmentation goes on the phrase
    /// of the word before it, as a reader takes a line in, rather than
    /// beginning one (窓辺に 座って 海を 見ていた): a particle or an ending,
    /// a lone kana, small kana or ー, a negative after anything but a
    /// particle (見ない, 行かない, but 時間が ない), a kanji going on a
    /// compound the segmentation parted (何 度), a verb going on a te-form
    /// (歩いて いく, 待って いる), and よう, みたい and らしい.
    static func continuesPhrase(_ word: String, after previous: String) -> Bool {
        guard let first = word.unicodeScalars.first, let last = previous.unicodeScalars.last else { return false }
        if continuations.contains(first) || followsAWord(word) || phraseEndings.contains(word) { return true }
        if word.count == 1, isHiragana(first) || (0x30A1...0x30FA).contains(first.value) { return true }
        if negatives.contains(word) { return !particles.contains(previous) }
        if word.count == 1, first.properties.isIdeographic { return last.properties.isIdeographic }
        return (previous.hasSuffix("て") || previous.hasSuffix("で")) && teAuxiliaries.contains(word)
    }
    private static let phraseEndings: Set<String> = ["てく", "てっ", "よう", "みたい", "らしい"]
    /// Verbs that go on a te-form (〜ている, 〜ていく, 〜てしまう, 〜てくれる).
    private static let teAuxiliaries: Set<String> = [
        "いる", "いた", "いて", "いく", "いっ", "いき", "いけ", "いれ", "ゆく", "ゆき", "ゆけ", "くる", "きた", "きて",
        "こない", "しまう", "しまっ", "しまい", "しまえ", "みる", "みた", "みて", "みれ", "みよ", "おく", "おい", "おこ",
        "あげる", "あげ", "くれる", "くれ", "もらう", "もらっ", "もらい", "ほしい", "ほしく", "ほしかっ",
    ]

    /// Whether `source` is written onto the previous token (aishi + teru).
    private static func joins(_ source: String, after previous: String) -> Bool {
        guard !previous.isEmpty else { return false }
        if source == "ん" { return true }
        if (previous == "だ" && source == "って") || (previous == "で" && source == "も") { return true }
        if particles.contains(previous) { return false }
        if endings.contains(source) { return true }
        if negatives.contains(source) { return previous.unicodeScalars.last.map(isHiragana) ?? false }
        if voicedEndings.contains(source) { return previous.hasSuffix("ん") || previous.hasSuffix("ない") }
        return false
    }
}
