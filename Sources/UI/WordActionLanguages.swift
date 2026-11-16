import Foundation

// MARK: - Action words in other languages
//
// "Action words" match the user's list first (English forms, and any word
// in another language they add), then built-in lists for the languages
// people most often sing in. A built-in list is only used where the line
// can be in that language: Korean on Hangul, Japanese on kana or kanji,
// Chinese on hanzi without kana, Vietnamese on its own letters; the
// Latin-script lists (Spanish, Portuguese, French, German, Italian,
// Indonesian, Tagalog) on any line, so they are kept to words and stems
// that are not English words. Missing a word is preferred over moving the
// wrong one.

enum ActionLexicon {
    /// What scripts a line is written in.
    struct Script: Equatable {
        var hangul = false, kana = false, han = false, vietnamese = false

        init() {}

        init(_ text: String) {
            for scalar in text.unicodeScalars {
                switch scalar.value {
                case 0xAC00...0xD7AF, 0x1100...0x11FF, 0x3130...0x318F: hangul = true
                case 0x3040...0x30FF: kana = true
                case 0x4E00...0x9FFF, 0x3400...0x4DBF, 0xF900...0xFAFF: han = true
                case 0x1EA0...0x1EF9, 0x01A0, 0x01A1, 0x01AF, 0x01B0, 0x0110, 0x0111, 0x0102, 0x0103: vietnamese = true
                default: break
                }
            }
        }

        var cjk: Bool { hangul || kana || han }
    }

    /// A word as the lists hold it: lowercased, accents and case folded
    /// (ß → ss), letters and digits only. CJK characters are kept as they are.
    static func fold(_ word: String) -> String {
        let folded = word.folding(options: [.diacriticInsensitive, .caseInsensitive, .widthInsensitive], locale: nil)
            .replacingOccurrences(of: "ß", with: "ss")
        var kept = ""
        for scalar in folded.unicodeScalars where CharacterSet.alphanumerics.contains(scalar) {
            kept.unicodeScalars.append(scalar)
        }
        return kept
    }

    /// The action `text` (a run as written) takes, and the list key it
    /// matched: the user's list first, then the built-in lists for the
    /// line's `script`.
    static func match(_ text: String, user: [String: WordAction],
                      script: Script) -> (key: String, action: WordAction)? {
        if let found = LyricWordIcons.match(text, in: user) { return (found.key, found.value) }
        let folded = fold(text)
        guard !folded.isEmpty else { return nil }
        if let action = user[folded] { return (folded, action) }
        if script.cjk {
            // The user's own CJK words: contained in the run, longest first.
            let own = user.keys.filter { Script($0).cjk }.sorted { $0.count != $1.count ? $0.count > $1.count : $0 < $1 }
            if let key = own.first(where: { folded.contains($0) }), let action = user[key] { return (key, action) }
            let lists: [Contained] = (script.hangul ? [korean] : [])
                + (script.kana || script.han ? [japanese] : []) + (script.han && !script.kana ? [chinese] : [])
            for list in lists {
                if let found = list.match(text) { return found }
            }
        }
        if script.vietnamese, let action = vietnamese[text.lowercased().trimmingCharacters(in: .punctuationCharacters)] {
            return ("vi:" + text.lowercased(), action)
        }
        // French and Italian elide an article onto the word (l'obscurité,
        // d'amore): the word after the apostrophe is tried too.
        var candidates = [folded]
        if let cut = text.lastIndex(where: { $0 == "'" || $0 == "\u{2019}" }) {
            let after = fold(String(text[text.index(after: cut)...]))
            if !after.isEmpty, after != folded { candidates.append(after) }
        }
        for candidate in candidates {
            for list in latin {
                if let found = list.match(candidate) { return found }
            }
        }
        return nil
    }

    // MARK: Latin-script lists

    /// Whole words (folded), and stems a word may run on from by at most
    /// four letters (folded, at least four letters long).
    struct Latin {
        let language: String
        let words: [String: WordAction]
        let stems: [(stem: String, action: WordAction)]
        let exclude: Set<String>

        func match(_ folded: String) -> (key: String, action: WordAction)? {
            guard !exclude.contains(folded) else { return nil }
            if let action = words[folded] { return ("\(language):\(folded)", action) }
            for entry in stems where folded.hasPrefix(entry.stem) && folded.count - entry.stem.count <= 4 {
                return ("\(language):\(entry.stem)", entry.action)
            }
            return nil
        }
    }

    static func words(_ action: WordAction, _ list: [String]) -> [(String, WordAction)] { list.map { ($0, action) } }

    static func latinList(_ language: String, words entries: [[(String, WordAction)]],
                          stems: [(String, WordAction)], exclude: [String] = []) -> Latin {
        var words: [String: WordAction] = [:]
        for group in entries { for (word, action) in group { words[word] = action } }
        return Latin(language: language, words: words, stems: stems.map { (stem: $0.0, action: $0.1) }, exclude: Set(exclude))
    }

    static let latin: [Latin] = [spanish, portuguese, french, german, italian, indonesian, tagalog]

    static let spanish: Latin = latinList("es", words: [
        words(.spin, ["gira", "giras", "giro", "girar", "girando", "giramos"]),
        words(.float, ["volar", "vuelo", "vuela", "vuelas", "volando", "flota", "flotar", "flotando"]),
        words(.dip, ["caer", "cae", "caes", "caigo", "cayo", "caida", "cayendo", "caemos", "caen"]),
        words(.hop, ["saltar", "salto", "salta", "saltas", "saltando", "brinca", "brincar", "brincando"]),
        words(.flicker, ["fuego", "arde", "ardo", "arder", "ardes", "ardiendo"]),
        words(.frost, ["hielo", "frio", "fria", "helado", "helada"]),
        words(.dark, ["sombra", "sombras", "tinieblas"]),
        words(.shatter, ["rompe", "romper", "rompes", "rompi", "rompio", "rompiendo", "rompo", "roto", "rota", "pedazos"]),
        words(.echo, ["eco", "ecos"]),
        words(.pulse, ["latido", "latidos", "latir"]),
        words(.wave, ["olas", "mar", "oceano", "marea"]),
        words(.sink, ["hundir", "hundo", "hunde", "hundiendo", "hundirme"]),
    ], stems: [
        ("tembl", .shake), ("tiembl", .shake), ("sacud", .shake),
        ("crece", .grow), ("crezc", .grow), ("creci", .grow),
        ("encog", .shrink), ("desapare", .dissolve), ("desvanec", .dissolve),
        ("quema", .flicker), ("quemo", .flicker), ("congel", .frost),
        ("sangr", .bleed), ("oscur", .dark), ("quebr", .shatter),
        ("derrit", .melt), ("derret", .melt), ("ahog", .sink),
    ], exclude: ["sangria", "sangrias"])

    static let portuguese: Latin = latinList("pt", words: [
        words(.float, ["voar", "voo", "voa", "voando", "voei"]),
        words(.dip, ["cair", "cai", "caio", "caiu", "caindo"]),
        words(.shake, ["tremer", "tremo", "treme", "tremendo"]),
        words(.hop, ["pular", "pulo", "pula", "pulando"]),
        words(.flicker, ["fogo", "fogueira"]),
        words(.frost, ["gelo", "gelado", "gelada"]),
        words(.bleed, ["sangue"]),
        words(.echo, ["eco"]),
        words(.wave, ["onda", "ondas", "mar", "oceano"]),
        words(.grow, ["crescer", "cresce", "cresceu", "cresco", "crescendo"]),
    ], stems: [
        ("flutu", .float), ("encolh", .shrink), ("desapare", .dissolve),
        ("queim", .flicker), ("congel", .frost), ("sangr", .bleed), ("escur", .dark),
        ("quebr", .shatter), ("derret", .melt), ("batiment", .pulse), ("afund", .sink), ("afog", .sink),
    ], exclude: ["tremendo", "sangria", "sangrias"])

    static let french: Latin = latinList("fr", words: [
        words(.spin, ["tourne", "tourner", "tournent", "tournoie", "tournoyer"]),
        words(.dip, ["tomber", "tombe", "tombes", "tombent", "tombee", "tombait", "tombant", "tombons", "tombez"]),
        words(.flicker, ["feu", "flamme", "flammes"]),
        words(.frost, ["glace", "froid", "froide", "gele", "geler", "gelee", "glacee"]),
        words(.bleed, ["saigne", "saigner", "saignant", "saignent"]),
        words(.dark, ["obscurite", "tenebres", "ombre", "ombres"]),
        words(.shatter, ["casse", "casser", "cassee", "eclats", "brisee", "briser"]),
        words(.melt, ["fondre", "fondu", "fondent"]),
        words(.grow, ["grandir", "grandis", "grandit", "grandissent", "grandissant"]),
        words(.wave, ["mer", "houle"]),
        words(.sink, ["noie", "noyer", "noyee", "sombrer", "sombrent"]),
    ], stems: [
        ("envol", .float), ("flott", .float), ("trembl", .shake), ("saut", .hop),
        ("retreci", .shrink), ("disparai", .dissolve), ("disparu", .dissolve),
        ("brul", .flicker), ("battement", .pulse),
    ], exclude: ["grandiose", "sauterelle"])

    static let german: Latin = latinList("de", words: [
        words(.dip, ["fallen", "fallt", "fiel", "fielen", "falle", "fallst"]),
        words(.flicker, ["feuer", "flamme", "flammen"]),
        words(.frost, ["eis", "kalt", "kalte", "friere", "frieren", "friert", "gefroren", "erfroren"]),
        words(.hop, ["springen", "springt", "sprang"]),
        words(.dark, ["schatten", "finster", "finsternis"]),
        words(.shatter, ["gebrochen", "zerbrochen"]),
        words(.echo, ["widerhall"]),
        words(.shake, ["beben", "bebt", "bebte", "bebten"]),
        words(.wave, ["welle", "wellen", "meer", "ozean"]),
        words(.sink, ["sinken", "sinkt", "versinken", "versinkt", "versank"]),
    ], stems: [
        ("flieg", .float), ("schweb", .float), ("dreh", .spin), ("zitter", .shake),
        ("hupf", .hop), ("wachs", .grow), ("schrumpf", .shrink), ("verschw", .dissolve), ("verblass", .dissolve),
        ("brenn", .flicker), ("brannt", .flicker), ("blut", .bleed), ("dunkel", .dark), ("dunkl", .dark),
        ("zerbr", .shatter), ("schmelz", .melt), ("schmolz", .melt), ("schmilz", .melt), ("herzschlag", .pulse),
        ("ertrink", .sink), ("ertrank", .sink),
    ], exclude: ["gefallen", "drehbuch"])

    static let italian: Latin = latinList("it", words: [
        words(.spin, ["gira", "giro", "girare", "girando"]),
        words(.float, ["volare", "volo", "vola", "voli", "volando", "volato"]),
        words(.dip, ["cado", "cadi", "cade", "cadono", "caduto", "caduta", "cadendo", "cadere"]),
        words(.shake, ["tremo", "trema", "tremare", "tremando"]),
        words(.hop, ["salta", "salto", "saltare", "saltando"]),
        words(.flicker, ["fuoco", "fiamme", "fiamma"]),
        words(.frost, ["gelo", "ghiaccio", "freddo", "fredda", "gela"]),
        words(.bleed, ["sangue"]),
        words(.dark, ["buio", "ombra", "ombre"]),
        words(.shatter, ["rotto", "rotta", "frantumi"]),
        words(.echo, ["eco"]),
        words(.pulse, ["battito", "battiti"]),
        words(.wave, ["onda", "mare", "oceano"]),
        words(.grow, ["crescere", "cresce", "cresci", "cresco", "cresciuto"]),
    ], stems: [ ("svani", .dissolve), ("scompar", .dissolve), ("bruci", .flicker), ("congel", .frost),
        ("oscur", .dark), ("spezz", .shatter), ("sciogl", .melt), ("sciolt", .melt),
        ("affond", .sink), ("annega", .sink),
    ])

    static let indonesian: Latin = latinList("id", words: [
        words(.spin, ["putar", "berputar", "memutar"]),
        words(.float, ["terbang", "melayang"]),
        words(.dip, ["jatuh", "terjatuh"]),
        words(.shake, ["gemetar", "bergetar"]),
        words(.hop, ["lompat", "melompat"]),
        words(.grow, ["tumbuh", "bertumbuh"]),
        words(.dissolve, ["hilang", "menghilang", "lenyap"]),
        words(.flicker, ["terbakar", "membakar", "bakar"]),
        words(.frost, ["beku", "membeku", "dingin", "salju"]),
        words(.bleed, ["darah", "berdarah"]),
        words(.dark, ["gelap", "kegelapan"]),
        words(.shatter, ["hancur", "patah", "retak"]),
        words(.melt, ["meleleh", "mencair"]),
        words(.echo, ["gema", "bergema"]),
        words(.pulse, ["detak", "berdetak"]),
        words(.wave, ["ombak", "lautan", "gelombang", "samudra"]),
        words(.sink, ["tenggelam"]),
    ], stems: [])

    static let tagalog: Latin = latinList("tl", words: [
        words(.spin, ["ikot", "umiikot", "umikot"]),
        words(.float, ["lipad", "lumipad", "lumilipad"]),
        words(.dip, ["hulog", "nahulog", "mahulog", "bumagsak"]),
        words(.shake, ["nanginginig", "nanginig"]),
        words(.hop, ["tumalon", "tumatalon"]),
        words(.grow, ["lumaki", "lumalaki"]),
        words(.dissolve, ["nawala", "naglaho", "maglaho"]),
        words(.flicker, ["apoy", "nasusunog", "nagliliyab"]),
        words(.frost, ["yelo", "lamig", "malamig"]),
        words(.bleed, ["dugo", "duguan"]),
        words(.dark, ["dilim", "madilim", "kadiliman"]),
        words(.shatter, ["basag", "nabasag", "wasak", "durog"]),
        words(.melt, ["natunaw", "matunaw"]),
        words(.echo, ["alingawngaw"]),
        words(.pulse, ["tibok"]),
        words(.wave, ["alon", "dagat", "karagatan"]),
        words(.sink, ["nalunod", "lunod", "lumubog"]),
    ], stems: [])

    /// Vietnamese: whole syllables with their tone marks (the marks tell
    /// the words apart), only on a line with Vietnamese letters.
    static let vietnamese: [String: WordAction] = [
        "xoay": .spin, "bay": .float, "rơi": .dip, "run": .shake, "nhảy": .hop,
        "cháy": .flicker, "lửa": .flicker, "băng": .frost, "lạnh": .frost, "máu": .bleed,
        "vỡ": .shatter, "sóng": .wave, "biển": .wave, "chìm": .sink,
    ]

    // MARK: CJK lists

    /// Words a run contains (longest first), and longer words that contain
    /// one but mean something else.
    struct Contained {
        let language: String
        let entries: [(word: String, action: WordAction)]
        let exclude: [String]

        init(_ language: String, _ groups: [[(String, WordAction)]], exclude: [String] = []) {
            self.language = language
            self.entries = groups.flatMap { $0 }.map { (word: $0.0, action: $0.1) }
                .sorted { $0.word.count != $1.word.count ? $0.word.count > $1.word.count : $0.word < $1.word }
            self.exclude = exclude
        }

        func match(_ text: String) -> (key: String, action: WordAction)? {
            var rest = text
            for word in exclude { rest = rest.replacingOccurrences(of: word, with: "\u{FFFC}") }
            for entry in entries where rest.contains(entry.word) {
                return ("\(language):\(entry.word)", entry.action)
            }
            return nil
        }
    }

    static let japanese = Contained("ja", [
        words(.spin, ["回る", "回って", "回り", "廻る", "くるくる", "回転"]),
        words(.float, ["飛ぶ", "飛ん", "飛べ", "飛び", "舞い", "浮か", "浮い"]),
        words(.dip, ["落ちる", "落ち", "堕ち"]),
        words(.shake, ["震え", "揺れ", "ふるえ"]),
        words(.hop, ["跳ね", "跳ん", "跳び", "跳ぶ", "ジャンプ"]),
        words(.grow, ["育っ", "育つ"]),
        words(.shrink, ["縮"]),
        words(.dissolve, ["消え", "薄れ"]),
        words(.flicker, ["燃え", "燃や", "炎", "焦が"]),
        words(.frost, ["凍え", "凍り", "凍る", "氷", "冷た"]),
        words(.bleed, ["血"]),
        words(.dark, ["闇", "暗"]),
        words(.shatter, ["壊れ", "砕け", "割れ", "崩れ"]),
        words(.melt, ["溶け", "溶か"]),
        words(.echo, ["響", "こだま", "木霊"]),
        words(.pulse, ["鼓動"]),
        words(.wave, ["波", "海"]),
        words(.sink, ["沈", "溺れ"]),
    ], exclude: ["影響", "暗記", "暗号", "落ち着", "回答", "今回", "一回", "毎回", "前回", "海外", "暗恋", "暗戀", "沈黙"])

    static let chinese = Contained("zh", [
        words(.spin, ["旋转", "旋轉", "转圈", "轉圈", "回旋"]),
        words(.float, ["飞", "飛", "飘", "飄", "漂浮"]),
        words(.dip, ["坠落", "墜落", "跌落", "掉落", "落下"]),
        words(.shake, ["颤抖", "顫抖", "发抖", "發抖", "摇晃", "搖晃"]),
        words(.hop, ["跳"]),
        words(.grow, ["成长", "成長", "长大", "長大"]),
        words(.shrink, ["缩小", "縮小"]),
        words(.dissolve, ["消失", "消散"]),
        words(.flicker, ["燃烧", "燃燒", "火焰", "燃"]),
        words(.frost, ["冰", "冻", "凍"]),
        words(.bleed, ["血"]),
        words(.dark, ["黑暗", "暗"]),
        words(.shatter, ["破碎", "碎", "崩溃", "崩潰"]),
        words(.melt, ["融化", "溶化"]),
        words(.echo, ["回声", "回聲", "回音"]),
        words(.pulse, ["心跳"]),
        words(.wave, ["海浪", "波浪", "浪花", "海"]),
        words(.sink, ["沉没", "沉沒", "下沉", "沉入", "淹没", "淹沒"]),
    ], exclude: ["暗恋", "暗戀", "跳舞", "飞机", "飛機", "海报", "海報"])

    static let korean = Contained("ko", [
        words(.spin, ["빙빙", "회전"]),
        words(.float, ["날아", "떠올라", "떠다니", "떠다녀"]),
        words(.dip, ["떨어지", "떨어져", "떨어졌"]),
        words(.shake, ["떨려", "떨리", "흔들"]),
        words(.hop, ["뛰어올", "점프"]),
        words(.grow, ["자라나", "자라고", "자랐"]),
        words(.shrink, ["작아져", "작아지"]),
        words(.dissolve, ["사라지", "사라져", "사라졌"]),
        words(.flicker, ["불타", "타오르", "타올라", "불꽃"]),
        words(.frost, ["얼어", "얼음", "차가운"]),
        words(.bleed, ["피가", "피를", "피눈물"]),
        words(.dark, ["어둠", "어두"]),
        words(.shatter, ["부서지", "부서져", "깨져", "깨지"]),
        words(.melt, ["녹아", "녹는"]),
        words(.echo, ["메아리"]),
        words(.pulse, ["두근", "심장 소리"]),
        words(.wave, ["파도", "바다"]),
        words(.sink, ["가라앉", "잠겨"]),
    ], exclude: ["커피"])
}
