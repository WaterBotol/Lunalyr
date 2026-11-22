import CoreFoundation
import Foundation

/// Mandarin pinyin read in context. The system's word tokenizer knows a
/// character's reading in its word (地上 dìshàng, 银行 yínháng, 了解 liǎojiě,
/// 觉得 juéde), where reading one character at a time takes the commonest
/// (de shàng, yín xíng). Its reading of a word comes unspaced, so it is cut
/// back into one syllable per character; a word it cannot cut, or reads
/// with no Latin, falls back to the character-by-character reading. The
/// tone changes of 一 and 不 are applied (一起 yìqǐ, 一个 yígè, 不是 búshì).
enum MandarinReading {
    /// Pinyin for each Han character of `text`, by its UTF-16 offset.
    static func syllables(_ text: String) -> [Int: String] {
        let string = text as NSString
        guard string.length > 0,
              let tokenizer = CFStringTokenizerCreate(nil, text as CFString, CFRange(location: 0, length: string.length),
                                                      kCFStringTokenizerUnitWord, Locale(identifier: "zh-Hans") as CFLocale)
        else { return [:] }
        var result: [Int: String] = [:]
        while CFStringTokenizerAdvanceToNextToken(tokenizer).rawValue != 0 {
            let range = CFStringTokenizerGetCurrentTokenRange(tokenizer)
            let characters = han(in: string, from: range.location, length: range.length)
            guard !characters.isEmpty else { continue }
            let latin = CFStringTokenizerCopyCurrentTokenAttribute(tokenizer, kCFStringTokenizerAttributeLatinTranscription) as? String
            let parts = latin.flatMap { split($0.lowercased(), into: characters.count) }
                ?? characters.map { single($0.text) }
            for (character, part) in zip(characters, parts) { result[character.offset] = part }
        }
        applyToneChanges(&result, in: string)
        return result
    }

    /// `word`, found at `location` in the line, with each Han character
    /// replaced by its syllable and the syllables spaced apart.
    static func reading(of word: String, at location: Int, syllables: [Int: String]) -> String {
        var out = "", offset = location
        for character in word {
            let length = character.utf16.count
            if character.unicodeScalars.first?.properties.isIdeographic == true {
                let syllable = syllables[offset] ?? single(String(character))
                if !out.isEmpty, out.last != " " { out += " " }
                out += syllable + " "
            } else {
                out.append(character)
            }
            offset += length
        }
        return out.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "  ", with: " ")
    }

    private static func han(in string: NSString, from start: Int, length: Int) -> [(offset: Int, text: String)] {
        var found: [(Int, String)] = []
        var index = start
        while index < start + length {
            let range = string.rangeOfComposedCharacterSequence(at: index)
            let text = string.substring(with: range)
            if text.unicodeScalars.first?.properties.isIdeographic == true { found.append((range.location, text)) }
            index = range.location + range.length
        }
        return found
    }

    private static func single(_ character: String) -> String {
        (character.applyingTransform(.mandarinToLatin, reverse: false) ?? character).lowercased()
    }

    /// `reading` cut into exactly `count` pinyin syllables, keeping its
    /// tone marks; nil when it cannot be.
    static func split(_ reading: String, into count: Int) -> [String]? {
        let letters = Array(reading.filter { !$0.isWhitespace && $0 != "'" && $0 != "’" })
        guard count > 0, !letters.isEmpty else { return nil }
        if count == 1 { return [String(letters)] }
        let plain = letters.map { String($0).applyingTransform(.stripDiacritics, reverse: false)?.lowercased() ?? String($0) }
        var memo: [Int: [Int: [String]?]] = [:]
        func cut(_ from: Int, _ left: Int) -> [String]? {
            if left == 0 { return from == letters.count ? [] : nil }
            if let known = memo[from]?[left] { return known }
            var answer: [String]?
            // Longest syllable first: xian before xi + an.
            for end in stride(from: min(letters.count, from + 6), to: from, by: -1) {
                guard inventory.contains(plain[from..<end].joined()), let rest = cut(end, left - 1) else { continue }
                answer = [String(letters[from..<end])] + rest
                break
            }
            memo[from, default: [:]][left] = .some(answer)
            return answer
        }
        return cut(0, count)
    }

    /// 一 reads yí before a fourth tone and yì before the others, except in
    /// numbers; 不 reads bú before a fourth tone.
    private static func applyToneChanges(_ syllables: inout [Int: String], in string: NSString) {
        let numerals: Set<Character> = Set("〇零一二三四五六七八九十百千万亿两第")
        func character(at offset: Int) -> Character? {
            guard offset >= 0, offset < string.length else { return nil }
            return string.substring(with: NSRange(location: offset, length: 1)).first
        }
        for (offset, syllable) in syllables {
            guard let here = character(at: offset), here == "一" || here == "不",
                  let next = syllables[offset + 1] else { continue }
            if here == "一" {
                let around = [character(at: offset - 1), character(at: offset + 1)].compactMap { $0 }
                guard syllable == "yī", !around.contains(where: numerals.contains) else { continue }
                switch tone(of: next) {
                case 4: syllables[offset] = "yí"
                case 1, 2, 3: syllables[offset] = "yì"
                default: break
                }
            } else if syllable == "bù", tone(of: next) == 4 {
                syllables[offset] = "bú"
            }
        }
    }

    static func tone(of syllable: String) -> Int {
        for character in syllable {
            if "āēīōūǖ".contains(character) { return 1 }
            if "áéíóúǘ".contains(character) { return 2 }
            if "ǎěǐǒǔǚ".contains(character) { return 3 }
            if "àèìòùǜ".contains(character) { return 4 }
        }
        return 0
    }

    /// Every pinyin syllable, without tone marks (ü written u), as ICU's
    /// Han-Latin gives them.
    static let inventory: Set<String> = Set((
        "a ai an ang ao ba bai ban bang bao bei ben beng bi bian biao bie bin bing bo bu ca cai can cang cao " +
        "ce cen ceng cha chai chan chang chao che chen cheng chi chong chou chu chua chuai chuan chuang chui " +
        "chun chuo ci cong cou cu cuan cui cun cuo da dai dan dang dao de den deng di dia dian diao die ding " +
        "diu dong dou du duan dui dun duo e ei en eng er fa fan fang fei fen feng fiao fo fou fu ga gai gan " +
        "gang gao ge gei gen geng gong gou gu gua guai guan guang gui gun guo ha hai han hang hao he hei hen " +
        "heng hm hong hou hu hua huai huan huang hui hun huo ji jia jian jiang jiao jie jin jing jiong jiu ju " +
        "juan jue jun ka kai kan kang kao ke kei ken keng kong kou ku kua kuai kuan kuang kui kun kuo la lai " +
        "lan lang lao le lei leng li lia lian liang liao lie lin ling liu lo long lou lu luan lue lun luo m " +
        "ma mai man mang mao me mei men meng mi mian miao mie min ming miu mo mou mu na nai nan nang nao ne " +
        "nei nen neng ng ni nian niang niao nie nin ning niu nong nou nu nuan nue nun nuo o ou pa pai pan " +
        "pang pao pei pen peng pi pian piao pie pin ping po pou pu qi qia qian qiang qiao qie qin qing qiong " +
        "qiu qu quan que qun ran rang rao re ren reng ri rong rou ru rua ruan rui run ruo sa sai san sang sao " +
        "se sen seng sha shai shan shang shao she shei shen sheng shi shou shu shua shuai shuan shuang shui " +
        "shun shuo si song sou su suan sui sun suo ta tai tan tang tao te teng ti tian tiao tie ting tong tou " +
        "tu tuan tui tun tuo wa wai wan wang wei wen weng wo wu xi xia xian xiang xiao xie xin xing xiong xiu " +
        "xu xuan xue xun ya yan yang yao ye yi yin ying yo yong you yu yuan yue yun za zai zan zang zao ze " +
        "zei zen zeng zha zhai zhan zhang zhao zhe zhen zheng zhi zhong zhou zhu zhua zhuai zhuan zhuang zhui " +
        "zhun zhuo zi zong zou zu zuan zui zun zuo "
    ).split(separator: " ").map(String.init))
}
