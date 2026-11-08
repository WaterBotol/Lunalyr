import Foundation

/// Production credits that lyric services publish as timed lines, e.g.
/// "作词 : 方文山", "人声录音室 : 雅旺录音室", "词版权管理方：…" or "Producer: …".
/// A line counts as a credit only when it opens with a label made entirely of
/// credit terms, followed by a colon. A term inside an ordinary lyric
/// ("我 鼓起勇气 说爱你", "弹着 吉他 唱歌", "高鳴る鼓動が") never matches.
/// A label that is one everyday word a lyric could also open with ("音乐",
/// "鼓", "Music", "Drums") needs names after the colon, and a place among
/// other credits, so "鼓：咚咚" and "Music: it's all I need" stay lyrics.
enum LyricCredits {
    /// `inCredits`: no lyric has come before this line yet, or the line just
    /// before it was a credit.
    static func isCredit(_ text: String, inCredits: Bool = true) -> Bool {
        let line = text.trimmingCharacters(in: .whitespacesAndNewlines)
        // A line wholly in brackets is a sung aside: "(和声：啦啦啦)".
        if let first = line.first, let last = line.last, "(（".contains(first), ")）".contains(last) { return false }
        guard let colon = line.prefix(48).firstIndex(where: { $0 == ":" || $0 == "：" }) else {
            return inCredits && isColonlessCredit(line)
        }
        let label = line[..<colon].trimmingCharacters(in: CharacterSet.whitespaces.union(brackets))
        guard !label.isEmpty, label.count <= 32,
              let kind = englishRole(label) ?? chineseRole(Array(label)) else { return false }
        return kind == .specific || (inCredits && looksLikeNames(line[line.index(after: colon)...]))
    }

    /// Every source's lines, whichever parser made them: credits the parsers
    /// did not already set apart (KPoe, LRCLIB's lyrics files, a cached
    /// result) are flagged here, in song order. Safe to run again.
    static func flagging(_ lines: [LyricsFetcher.Line], title: String = "", artist: String = "") -> [LyricsFetcher.Line] {
        let names = TrackNames(title: title, artist: artist)
        var result = lines
        func convert(_ index: Int) {
            result[index].isInstrumental = true
            result[index].words = []
            result[index].wordTimed = false
            result[index].romanization = nil
            result[index].translation = nil
            result[index].credit = true
            result[index].end = result[index].start + 0.01
        }
        func nextSung(after index: Int) -> Int? {
            result.indices.dropFirst(index + 1).first { !result[$0].isInstrumental || result[$0].credit == true }
        }
        var inCredits = true
        for index in result.indices {
            if result[index].credit == true { inCredits = true; continue }
            if result[index].isInstrumental { continue }
            let text = result[index].text
            // "[Verse 1]", "[ Chorus ]", "Verse 3]": section labels, not sung.
            if isSectionLabel(text) {
                result[index].isInstrumental = true
                result[index].text = ""
                result[index].words = []
                result[index].wordTimed = false
                result[index].romanization = nil
                result[index].translation = nil
                continue
            }
            var credit = isCredit(text, inCredits: inCredits)
            if !credit, inCredits, names.isHeading(text) { credit = true }
            // A bare title or artist line heading the credits.
            if !credit, inCredits, names.isLoneName(text), let next = nextSung(after: index),
               result[next].credit == true || isCredit(result[next].text, inCredits: true) || names.isHeading(result[next].text) {
                credit = true
            }
            if credit { inCredits = true; convert(index) } else { inCredits = false }
        }
        return markingTitleHeader(result)
    }

    private static let sectionWords = "verse|chorus|pre-?chorus|post-?chorus|bridge|intro|outro|hook|refrain|interlude|instrumental|solo|breakdown|drop|ad-?libs?"

    /// A bracketed section label ("[Verse 1]", "[Chorus: Name]", "Verse 3]").
    /// A bare "Chorus" is left alone: it could be sung.
    static func isSectionLabel(_ text: String) -> Bool {
        let line = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let first = line.first, let last = line.last, "[【(（".contains(first) || "]】)）".contains(last) else { return false }
        let pattern = "^[\\[【(（]?\\s*(\(sectionWords))\\b[^\\]】)）]*[\\]】)）]?$"
        return line.range(of: pattern, options: [.regularExpression, .caseInsensitive]) != nil
    }

    /// The playing song's names, and the ways a lyric file repeats them in
    /// its heading: "Title - Artist", "Artist - Title", "Title (Artist)",
    /// "Title by Artist", "Artist: Title", "Title / Artist".
    struct TrackNames {
        let title: String
        let artist: String
        let primaryArtist: String

        init(title: String, artist: String) {
            self.title = LyricQuery.normalize(title)
            self.artist = LyricQuery.normalize(artist)
            // "Yot Club & Someone", "A, B", "A feat. B": the first name.
            let first = artist.components(separatedBy: CharacterSet(charactersIn: ",&;/、")).first ?? artist
            let lead = first.components(separatedBy: " feat").first?.components(separatedBy: " ft").first ?? first
            self.primaryArtist = LyricQuery.normalize(lead)
        }

        private var usable: Bool { !title.isEmpty }

        /// A line carrying both the title and the artist and little else.
        func isHeading(_ text: String) -> Bool {
            guard usable, !primaryArtist.isEmpty else { return false }
            let line = LyricQuery.normalize(text)
            guard line.count <= title.count + artist.count + 14,
                  Self.contains(line, title), Self.contains(line, primaryArtist) else { return false }
            return true
        }

        /// A line that is only the title, or only the artist.
        func isLoneName(_ text: String) -> Bool {
            guard usable else { return false }
            let line = LyricQuery.normalize(text)
            return !line.isEmpty && (line == title || line == artist || line == primaryArtist)
        }

        /// Whole-word containment on normalised text.
        private static func contains(_ line: String, _ phrase: String) -> Bool {
            !phrase.isEmpty && " \(line) ".contains(" \(phrase) ")
        }

        func isEither(_ text: String) -> Bool { isHeading(text) || isLoneName(text) }
    }

    /// Unsynced lyrics have no time to show a credit at, so the ones opening
    /// the text (and the heading above them) are dropped.
    static func droppingLeadingCredits(_ lines: [String], title: String = "", artist: String = "") -> [String] {
        let names = TrackNames(title: title, artist: artist)
        func blank(_ text: String) -> Bool { text.trimmingCharacters(in: .whitespaces).isEmpty }
        var index = lines.firstIndex { !blank($0) } ?? lines.count
        // "Title - Artist" heading right above the credits.
        if index < lines.count, lines[index].contains(" - ") || lines[index].contains(" – ") || names.isHeading(lines[index]),
           let next = lines[(index + 1)...].first(where: { !blank($0) }), isCredit(next, inCredits: true) {
            index += 1
        }
        let heading = index
        while index < lines.count, blank(lines[index]) || isCredit(lines[index], inCredits: true) { index += 1 }
        return index == heading ? lines : Array(lines[index...])
    }

    /// Services open the credits with the song's own heading ("Human Nature -
    /// Yot Club"). That line is not sung either, so when it sits right ahead
    /// of the credits it becomes one.
    static func markingTitleHeader(_ lines: [LyricsFetcher.Line]) -> [LyricsFetcher.Line] {
        guard let index = lines.firstIndex(where: { !$0.isInstrumental }), index + 1 < lines.count,
              lines[..<index].allSatisfy({ $0.isInstrumental }), lines[index + 1].credit == true, lines[index].start < 20,
              lines[index].text.contains(" - ") || lines[index].text.contains(" – ") else { return lines }
        var result = lines
        result[index].isInstrumental = true
        result[index].words = []
        result[index].wordTimed = false
        result[index].romanization = nil
        result[index].translation = nil
        result[index].credit = true
        result[index].end = result[index].start + 0.01
        return result
    }

    /// "Produced by Max Martin", "Written by Tove Lo & Ali Payami": a role
    /// word, "by", then names, with no colon. Only before the first lyric or
    /// straight after another credit, and only past-tense roles, so a lyric
    /// like "Written by the stars" (no names) or later in the song never
    /// matches.
    private static func isColonlessCredit(_ line: String) -> Bool {
        let words = line.split(whereSeparator: \.isWhitespace).map(String.init)
        guard words.count >= 3, words.count <= 12,
              let by = words.prefix(4).firstIndex(where: { $0.lowercased() == "by" }), by >= 1,
              !words.prefix(by).contains(where: { $0.lowercased() == "by" }) else { return false }
        let label = words[...by].joined(separator: " ")
        guard let kind = englishRole(label), kind == .specific,
              words[..<by].allSatisfy({ englishRoles.contains($0.lowercased()) || englishQualifiers.contains($0.lowercased()) }),
              words[..<by].contains(where: { $0.lowercased().hasSuffix("ed") || ["written", "prod."].contains($0.lowercased()) }) else { return false }
        return looksLikeNames(Substring(words[(by + 1)...].joined(separator: " ")))
    }

    private enum Kind { case specific, everyday }

    private static let brackets = CharacterSet(charactersIn: "[]()（）【】「」〔〕<>《》")

    /// Terms that can begin a credit label or follow another one: "人声" +
    /// "录音" + "室", "词" + "版权" + "管理方", "出品" + "监制".
    private static let roles: [String] = [
        "作词作曲", "作词", "作詞", "作曲", "编曲", "編曲", "词曲", "詞曲", "词", "詞", "曲", "作者",
        "制作人", "製作人", "制作", "製作", "监制", "監製", "出品人", "出品", "联合", "聯合", "发行", "發行",
        "混音", "混缩", "母带", "母帶", "录音", "錄音", "录制", "錄製", "人声", "人聲", "和声", "和聲",
        "和音", "合声", "合聲", "伴唱", "伴奏", "配唱", "主唱", "演唱", "原唱", "翻唱", "合唱", "童声", "童聲",
        "编写", "編寫", "编辑", "編輯", "编配", "編配", "配器", "编程", "編程", "采样", "採樣", "音效",
        "音频", "音頻", "吉他", "贝斯", "貝斯", "鼓", "键盘", "鍵盤", "钢琴", "鋼琴", "弦乐", "弦樂",
        "管乐", "管樂", "打击乐", "打擊樂", "提琴", "小提琴", "大提琴", "中提琴", "长笛", "長笛", "笛子",
        "二胡", "古筝", "古箏", "琵琶", "口琴", "合成器", "乐队", "樂隊", "乐团", "樂團", "乐手", "樂手", "器乐", "器樂",
        "乐器", "樂器", "电子琴", "電子琴", "萨克斯", "薩克斯", "小号", "小號", "长号", "長號", "圆号", "圓號",
        "手风琴", "手風琴", "竖琴", "豎琴", "扬琴", "揚琴", "唢呐", "嗩吶", "尤克里里",
        "演奏", "工程师", "工程師", "版权", "版權", "管理方", "管理", "作品", "统筹", "統籌", "策划",
        "策劃", "企划", "企劃", "宣传", "宣傳", "推广", "推廣", "营销", "營銷", "封面", "设计", "設計",
        "摄影", "攝影", "视觉", "視覺", "后期", "後期", "美术", "美術", "文案", "音乐", "音樂", "总监",
        "總監", "艺人", "藝人", "经纪", "經紀", "项目", "項目", "执行", "執行", "助理", "指导", "指導",
        "声乐", "聲樂", "鸣谢", "鳴謝", "艺术", "藝術", "작사", "작곡", "편곡", "프로듀서", "MV", "OP", "SP", "ISRC",
    ]
    /// Terms that only qualify the role after them: "电" + "吉他", "架子" + "鼓",
    /// "总" + "监制", "特别" + "鸣谢".
    private static let modifiers: [String] = [
        "电", "電", "木", "原声", "原聲", "架子", "总", "總", "主", "副", "低音", "第一", "第二", "首席", "特别", "特別",
    ]
    /// Terms that only complete a preceding one: "吉他" + "手", "管理" + "方",
    /// "母带" + "处理", "录音" + "棚", "弦乐" + "团".
    private static let suffixes: [String] = [
        "工程师", "工程師", "工作室", "公司", "团队", "團隊", "处理", "處理", "师", "師", "手", "方", "室", "棚",
        "团", "團", "组", "組", "队", "隊", "部", "人", "者",
    ]
    /// Separators between two terms: "作词/作曲", "录音作品及MV版权".
    private static let joins: [String] = ["和", "及", "与", "與", "/", "／", "&", "＆", "、", ",", "，", "·", "・", "+", "＋", " ", "\u{3000}"]
    /// Whole labels a lyric could also begin with: instruments, "music", and
    /// the vocal parts that duets mark in the lyric itself ("合唱：…").
    private static let everyday: Set<String> = [
        "音乐", "音樂", "鼓", "和声", "和聲", "合声", "合聲", "和音", "人声", "人聲", "合唱", "伴唱", "主唱", "演唱",
        "童声", "童聲", "吉他", "贝斯", "貝斯", "钢琴", "鋼琴", "键盘", "鍵盤", "弦乐", "弦樂", "提琴", "笛子", "口琴",
        "二胡", "古筝", "古箏", "琵琶",
    ]

    private static let roleTerms = roles.map(Array.init)
    private static let modifierTerms = modifiers.map(Array.init)
    private static let suffixTerms = suffixes.map(Array.init)
    private static let joinTerms = joins.map(Array.init)

    /// Segments the label into credit terms; an English role may follow
    /// ("作词 Lyricist", "鼓 Drums").
    private static func chineseRole(_ label: [Character]) -> Kind? {
        let count = label.count
        // complete[i]: label[..<i] is a run of terms ending in a role or suffix.
        // open[i]: it is empty or ends in a separator or modifier, so a role
        // must follow.
        var complete = [Bool](repeating: false, count: count + 1)
        var open = [Bool](repeating: false, count: count + 1)
        open[0] = true
        func mark(_ terms: [[Character]], at index: Int, in table: inout [Bool]) {
            for term in terms where index + term.count <= count && label[index..<index + term.count].elementsEqual(term) {
                table[index + term.count] = true
            }
        }
        for index in 0..<count where complete[index] || open[index] {
            if complete[index] {
                let rest = String(label[index...]).trimmingCharacters(in: .whitespaces)
                if rest.first?.isASCII == true, englishRole(rest) != nil { return .specific }
                mark(suffixTerms, at: index, in: &complete)
                mark(joinTerms, at: index, in: &open)
            }
            mark(roleTerms, at: index, in: &complete)
            mark(modifierTerms, at: index, in: &open)
        }
        guard complete[count] else { return nil }
        return everyday.contains(String(label)) ? .everyday : .specific
    }

    /// Role words: one of them, with any qualifiers and joins, makes a label.
    private static let englishRoles: Set<String> = [
        "producer", "producers", "production", "produced", "prod", "prod.", "coproducer", "composer", "composers", "composed",
        "composition", "lyricist", "lyricists", "lyrics", "lyric", "writer", "writers", "written", "songwriter",
        "songwriters", "songwriting", "arranger", "arrangers", "arranged", "arrangement", "arrangements", "engineer",
        "engineers", "engineered", "engineering", "mixer", "mixing", "mixed", "mix", "mastering", "mastered",
        "recording", "recorded", "programming", "programmed", "programmer", "publisher", "publishers", "publishing",
        "published", "copyright", "isrc", "a&r", "op", "sp", "studio", "studios", "editor", "editing", "edited",
        "director", "conductor", "orchestrator", "orchestration", "orchestrated", "coordinator", "management",
        "manager", "label", "remix", "remixer", "remixed", "performer", "performers", "performed", "supervisor",
        "supervision", "supervised", "direction", "coordination", "presented", "presenter", "release", "released",
        "distribution", "distributed", "distributor", "marketing", "promotion", "design", "designer", "designed",
        "photography", "photographer", "artwork", "planning", "planner", "mv",
    ]
    /// Instruments and other everyday words: alone, they need names after the
    /// colon ("Drums: Steve Gadd", not "Drums: boom boom").
    private static let englishEveryday: Set<String> = [
        "music", "words", "vocals", "vocal", "voice", "guitar", "guitars", "bass", "drums", "drum", "percussion",
        "piano", "keys", "keyboard", "keyboards", "synth", "synths", "synthesizer", "strings", "string", "violin",
        "violins", "viola", "cello", "horns", "horn", "brass", "saxophone", "sax", "trumpet", "trombone", "flute",
        "harp", "organ", "choir", "orchestra", "beat", "beats",
    ]
    /// Words that only qualify the rest: "Executive", "Electric", "by".
    private static let englishQualifiers: Set<String> = [
        "executive", "co", "additional", "assistant", "associate", "lead", "background", "backing", "original",
        "sub", "electric", "acoustic", "by", "chief", "head", "main", "rhythm", "steel", "pedal", "upright",
        "double", "grand", "first", "second", "solo", "art", "artistic", "creative", "visual", "cover", "graphic",
        "project",
    ]

    /// "Producer", "Lyrics by", "Executive Producers", "Mixed & Mastered by",
    /// "Electric Guitar", "A&R", "OP"; a lone "Keyboard" is an everyday label.
    private static func englishRole(_ label: String) -> Kind? {
        var words: [String] = []
        for token in label.lowercased().split(whereSeparator: { $0.isWhitespace || "/,+-".contains($0) }) {
            words += token == "a&r" ? ["a&r"] : token.split(separator: "&").map(String.init)
        }
        words.removeAll { $0 == "and" }
        guard !words.isEmpty,
              words.allSatisfy({ englishRoles.contains($0) || englishEveryday.contains($0) || englishQualifiers.contains($0) }),
              words.contains(where: { !englishQualifiers.contains($0) }) else { return nil }
        return words.count == 1 && englishEveryday.contains(words[0]) ? .everyday : .specific
    }

    private static let particles: Set<String> = ["van", "von", "de", "da", "di", "del", "der", "den", "du", "la", "le", "y"]
    /// Pronouns and particles of sung Chinese that names do not contain.
    private static let sungCharacters = Set("我你妳他她它们們的了吗嗎呢吧啊哦喔啦呀么麼着著过過")
    /// Endings of the studios, orchestras and companies credited by name.
    private static let organisations = ["团", "團", "队", "隊", "组合", "組合", "公司", "工作室", "录音棚", "錄音棚",
                                        "录音室", "錄音室", "传媒", "傳媒", "文化", "唱片", "音乐", "音樂"]

    /// Names as credits list them: "高飞", "霍锦 卢泓宇", "Max Martin", "Sony
    /// Music", "西田修大/玉田豊夢", "国际首席爱乐乐团". Sung words rarely look
    /// like that: "我们一起", "咚咚", "叮咚叮咚", "it's all I need", "boom boom".
    private static func looksLikeNames(_ value: Substring) -> Bool {
        let items = value.split(whereSeparator: { "/／、,，&＆;；|".contains($0) })
            .map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        guard !items.isEmpty, items.count <= 12 else { return false }
        return items.allSatisfy { item in
            let words = item.split(whereSeparator: \.isWhitespace).map(String.init)
            guard words.count <= 5, words.contains(where: { !particles.contains($0.lowercased()) }) else { return false }
            return words.allSatisfy { word in
                guard LyricScript.keepsSyllables(word) else {
                    return (word.first?.isUppercase ?? false) || particles.contains(word.lowercased())
                }
                // Han, kana or Hangul names have no pronoun or particle in them,
                // and people's names run two to four characters without being
                // one sound repeated.
                guard sungCharacters.isDisjoint(with: word) else { return false }
                if word.count <= 24, organisations.contains(where: { word.count > $0.count && word.hasSuffix($0) }) { return true }
                return (2...4).contains(word.count) && Set(word).count * 2 > word.count
            }
        }
    }
}
