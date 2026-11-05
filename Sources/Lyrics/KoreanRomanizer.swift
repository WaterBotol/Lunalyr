import Foundation

/// Revised Romanization of Korean (2000), spelled from pronunciation the way
/// the standard requires: inside a word, liaison, ㅎ aspiration and elision,
/// palatalisation, nasalisation and liquidisation are applied across syllable
/// boundaries. Tensification is not written, as in the standard, and no
/// hyphens are added. A word is a run of Hangul syllables; anything else
/// (spaces, Latin, punctuation) passes through unchanged and ends the run.
enum KoreanRomanizer {
    static func romanize(_ text: String) -> String { syllables(text).joined() }

    /// The reading of each character of `text`: a Hangul syllable's sounds
    /// after the changes its word brings (같이 ga, chi; 국물 gung, mul), any
    /// other character itself.
    static func syllables(_ text: String) -> [String] {
        var result: [String] = []
        var word: [Syllable] = []
        for character in text {
            let composed = String(character).precomposedStringWithCanonicalMapping.unicodeScalars
            if composed.count == 1, let scalar = composed.first, let syllable = Syllable(scalar) {
                word.append(syllable)
            } else {
                result += spell(word)
                word.removeAll()
                result.append(String(character))
            }
        }
        return result + spell(word)
    }

    /// Hangul syllables, precomposed or written as conjoining jamo (NFD).
    static func containsHangul(_ text: String) -> Bool {
        text.unicodeScalars.contains {
            Syllable($0) != nil || (0x1100...0x11FF).contains($0.value)
                || (0xA960...0xA97F).contains($0.value) || (0xD7B0...0xD7FF).contains($0.value)
        }
    }

    // MARK: - Jamo

    private enum Consonant {
        case g, kk, n, d, tt, r, m, b, pp, s, ss, j, jj, ch, k, t, p, h, ng
        var letters: String {
            switch self {
            case .g: return "g"
            case .kk: return "kk"
            case .n: return "n"
            case .d: return "d"
            case .tt: return "tt"
            case .r: return "r"
            case .m: return "m"
            case .b: return "b"
            case .pp: return "pp"
            case .s: return "s"
            case .ss: return "ss"
            case .j: return "j"
            case .jj: return "jj"
            case .ch: return "ch"
            case .k: return "k"
            case .t: return "t"
            case .p: return "p"
            case .h: return "h"
            case .ng: return "ng"
            }
        }
        /// The sound a final consonant settles on before another consonant
        /// or at the end of a word.
        var neutral: Coda {
            switch self {
            case .g, .kk, .k: return .k
            case .n: return .n
            case .r: return .l
            case .m: return .m
            case .b, .pp, .p: return .p
            case .ng: return .ng
            case .d, .tt, .s, .ss, .j, .jj, .ch, .t, .h: return .t
            }
        }
        /// The aspirated sound an obstruent takes next to ㅎ.
        var aspirated: Consonant {
            switch self {
            case .g, .kk, .k: return .k
            case .b, .pp, .p: return .p
            case .j, .jj, .ch: return .ch
            default: return .t
            }
        }
    }

    private enum Coda {
        case none, k, n, t, l, m, p, ng
        var letters: String {
            switch self {
            case .none: return ""
            case .k: return "k"
            case .n: return "n"
            case .t: return "t"
            case .l: return "l"
            case .m: return "m"
            case .p: return "p"
            case .ng: return "ng"
            }
        }
    }

    private static let initials: [Consonant?] = [
        .g, .kk, .n, .d, .tt, .r, .m, .b, .pp, .s, .ss, nil, .j, .jj, .ch, .k, .t, .p, .h,
    ]
    private static let vowels = [
        "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae",
        "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i",
    ]
    private static let vowelI = 20
    /// ㅕ: 여 and 혀 are often 이 and 히 contracted with 어 (붙이어 붙여,
    /// 갇히어 갇혀), so ㅌ before 여, and ㄷ or ㅌ before 혀, palatalise as
    /// they do before 이 and 히.
    private static let vowelYeo = 6
    private static func palatalises(_ vowel: Int) -> Bool { vowel == vowelI || vowel == vowelYeo }
    /// Final consonants; double finals keep both of their consonants.
    private static let finals: [[Consonant]] = [
        [], [.g], [.kk], [.g, .s], [.n], [.n, .j], [.n, .h], [.d], [.r], [.r, .g], [.r, .m],
        [.r, .b], [.r, .s], [.r, .t], [.r, .p], [.r, .h], [.m], [.b], [.b, .s], [.s], [.ss],
        [.ng], [.j], [.ch], [.k], [.t], [.p], [.h],
    ]

    private struct Syllable {
        let scalar: UInt32
        let initial: Int, vowel: Int, final: Int
        init?(_ scalar: Unicode.Scalar) {
            guard (0xAC00...0xD7A3).contains(scalar.value) else { return nil }
            let index = Int(scalar.value - 0xAC00)
            self.scalar = scalar.value
            initial = index / 588
            vowel = index % 588 / 28
            final = index % 28
        }
        var startsWithVowel: Bool { initial == 11 }
    }

    // MARK: - Spelling

    private static func spell(_ word: [Syllable]) -> [String] {
        guard !word.isEmpty else { return [] }
        var onsets = word.map { initials[$0.initial] }
        var codas = [Coda](repeating: .none, count: word.count)
        for index in word.indices {
            let final = finals[word[index].final]
            if index + 1 < word.count {
                let joined = join(final, in: word, at: index)
                codas[index] = joined.coda
                onsets[index + 1] = joined.onset
            } else {
                codas[index] = wordFinal(final, word[index])
            }
        }
        return word.indices.map { index in
            var text = ""
            if let onset = onsets[index] {
                // ㄹ after ㄹ is written ll; elsewhere an initial ㄹ is r.
                text += onset == .r && index > 0 && codas[index - 1] == .l ? "l" : onset.letters
            }
            return text + vowels[word[index].vowel] + codas[index].letters
        }
    }

    private static func wordFinal(_ final: [Consonant], _ syllable: Syllable) -> Coda {
        guard let head = final.first else { return .none }
        guard final.count == 2 else { return head.neutral }
        switch (head, final[1]) {
        case (.r, .g), (.r, .m), (.r, .p): return final[1].neutral
        case (.r, .b): return isBalp(syllable) ? .p : .l
        default: return head.neutral
        }
    }

    /// 밟 keeps ㅂ where other ㄼ finals keep ㄹ (밟다 bapda, 넓다 neolda).
    private static func isBalp(_ syllable: Syllable) -> Bool { syllable.scalar == 0xBC1F }

    /// ㄴ is inserted before a few vowel-initial morphemes in compounds
    /// (꽃잎 kkonnip, 한여름 hannyeoreum). It is lexical, so only common
    /// lyric words are listed; elsewhere a final simply moves across.
    private static func insertsN(_ word: [Syllable], at index: Int) -> Bool {
        let next = word[index + 1].scalar
        if next == 0xC78E { return true } // 잎
        let current = word[index].scalar
        let after = index + 2 < word.count ? word[index + 2].scalar : nil
        switch (current, next, after) {
        case (0xD55C, 0xC5EC, 0xB984?), (0xB2A6, 0xC5EC, 0xB984?), (0xCCAB, 0xC5EC, 0xB984?): return true // 한/늦/첫 여름
        case (0xC19C, 0xC774, 0xBD88?): return true // 솜이불
        case (0xB2F4, 0xC694, nil): return word.count == 2 // 담요
        default: return false
        }
    }

    /// Resolves a final consonant against the next syllable's initial.
    private static func join(_ final: [Consonant], in word: [Syllable], at index: Int) -> (coda: Coda, onset: Consonant?) {
        let current = word[index], next = word[index + 1]
        var initial = initials[next.initial]
        guard let head = final.first else { return (.none, initial) }
        let tail = final.count == 2 ? final[1] : nil
        if initial == nil, insertsN(word, at: index) { initial = .n }

        guard let onset = initial else {
            // Liaison: the final, or the second consonant of a pair, opens
            // the next syllable. ㄷ/ㅌ before 이 (ㅌ also before 여) palatalise;
            // ㅎ falls silent.
            if head == .ng && tail == nil { return (.ng, nil) }
            if let tail {
                if tail == .h { return (.none, head) }                          // 많아 mana, 싫어 sireo
                if head == .r && tail == .t && palatalises(next.vowel) { return (.l, .ch) }
                return (head.neutral, tail)                                     // 없어 eopseo, 읽어 ilgeo
            }
            if head == .h { return (.none, nil) }                               // 좋아 joa
            if next.vowel == vowelI && head == .d { return (.none, .j) }        // 굳이 guji
            if palatalises(next.vowel) && head == .t { return (.none, .ch) }    // 같이 gachi, 붙여 buchyeo
            return (.none, head)                                                // 한국어 hangugeo
        }

        if final.last == .h {
            // ㅎ, ㄶ, ㅀ: aspirate a following ㄱ/ㄷ/ㅈ, merge into ㅅ,
            // and assimilate before ㄴ (놓는 nonneun, 않는 anneun, 싫니 silli).
            let base: Coda = tail == nil ? .none : head.neutral
            switch onset {
            case .g, .d, .j: return (base, onset.aspirated)                     // 놓다 nota, 많고 manko
            case .s, .h: return (base, onset)
            case .n: return base == .l ? (.l, .r) : (.n, .n)
            default: return assimilate(base == .none ? .t : base, onset)
            }
        }

        var single = head
        if let tail {
            if onset == .h {
                // A pair before ㅎ keeps its first consonant and aspirates
                // the second (밝히다 balkida, 앉히다 anchida).
                return tail == .m ? (.m, .h) : (head.neutral, tail.aspirated)
            }
            switch (head, tail) {
            case (.r, .g): single = onset == .g ? .r : .g                       // 읽다 ikda, 읽고 ilgo
            case (.r, .m), (.r, .p): single = tail                              // 젊다 jeomda, 읊다 eupda
            case (.r, .b): single = isBalp(current) ? .b : .r                   // 밟다 bapda, 넓다 neolda
            default: single = head                                              // ㄳ ㄵ ㄽ ㄾ ㅄ
            }
        }
        if onset == .h {
            switch single.neutral {
            case .k, .p: return (.none, single.aspirated)                       // 축하 chuka, 입학 ipak
            case .t:
                if (single == .d || single == .t) && palatalises(next.vowel) { return (.none, .ch) } // 굳히다 guchida, 갇혀 gachyeo
                return (.none, single.aspirated)                                // 못해 motae
            default: return (single.neutral, .h)                                // 전화 jeonhwa
            }
        }
        return assimilate(single.neutral, onset)
    }

    /// Nasalisation and liquidisation between a settled final and an initial.
    private static func assimilate(_ coda: Coda, _ onset: Consonant) -> (coda: Coda, onset: Consonant?) {
        switch onset {
        case .n, .m:
            switch coda {
            case .k: return (.ng, onset)                                        // 국물 gungmul
            case .t: return (.n, onset)                                         // 있는 inneun
            case .p: return (.m, onset)                                         // 합니다 hamnida
            case .l where onset == .n: return (.l, .r)                          // 설날 seollal
            default: return (coda, onset)
            }
        case .r:
            switch coda {
            case .n, .l: return (.l, .r)                                        // 신라 silla, 몰라 molla
            case .m, .ng: return (coda, .n)                                     // 종로 jongno
            case .k: return (.ng, .n)                                           // 독립 dongnip
            case .p: return (.m, .n)                                            // 법률 beomnyul
            case .t: return (.n, .n)
            case .none: return (.none, .r)
            }
        default:
            return (coda, onset)
        }
    }
}
