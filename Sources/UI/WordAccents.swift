import AppKit
import SwiftUI

// MARK: - Word symbols and action words
//
// "Word symbols": a listed word, as it is sung, gets a small monochrome SF
// Symbol beside it in its own colour ("love" a filled heart). "Action
// words": a listed verb gets a short motion of its own on top of its
// layout's entrance ("spin" turns once, "fall" dips). Both are planned once
// per lyric line (`WordAccentPlan`), so the caps hold whatever a layout
// does with the words, and drawn by every layout from that plan. The rules
// and constants are mirrored on Windows (WORD-SYMBOLS-ACTIONS-HANDOFF.md).

extension LyricWordIcons {
    /// "Word symbols"' default list, word → SF Symbol, as decided for both
    /// apps (15 Nov 2026). Its own list rather than Portal's pill table: the
    /// pills use filled, picture-like variants of their own.
    static let defaultSymbolList: [(word: String, symbol: String)] = [
        ("love", "heart.fill"), ("loved", "heart.fill"), ("loving", "heart.fill"),
        ("heart", "heart"),
        ("broken", "heart.slash"), ("heartbreak", "heart.slash"),
        ("fire", "flame"), ("burn", "flame"), ("burning", "flame"),
        ("star", "star"), ("stars", "star"),
        ("moon", "moon"),
        ("sun", "sun.max"), ("sunshine", "sun.max"),
        ("rain", "cloud.rain"), ("raining", "cloud.rain"),
        ("night", "moon.stars"), ("midnight", "moon.stars"),
        ("dance", "figure.dance"), ("dancing", "figure.dance"),
        ("cry", "drop"), ("tears", "drop"), ("crying", "drop"),
        ("money", "dollarsign"), ("cash", "dollarsign"),
        ("crown", "crown"), ("king", "crown"), ("queen", "crown"),
        ("him", "person"), ("her", "person"),
        ("home", "house"),
        ("time", "clock"),
        ("music", "music.note"), ("song", "music.note"),
        ("sky", "cloud"),
        ("world", "globe"),
        ("call", "phone"), ("phone", "phone"),
    ]

    static let defaultSymbols: [String: String] =
        Dictionary(defaultSymbolList.map { ($0.word, $0.symbol) }, uniquingKeysWith: { first, _ in first })

    /// A word as it is matched against the lists: lowercased, with
    /// punctuation and apostrophes dropped. Nil for a word in any script
    /// but plain English letters (the feature is English only), or empty.
    static func englishKey(_ word: String) -> String? {
        let lowered = word.lowercased()
        var kept = ""
        for scalar in lowered.unicodeScalars {
            if ("a"..."z").contains(scalar) || ("0"..."9").contains(scalar) {
                kept.unicodeScalars.append(scalar)
            } else if CharacterSet.letters.contains(scalar) {
                return nil
            }
        }
        return kept.isEmpty ? nil : kept
    }

    /// The forms `word` is looked up by, most literal first: the word
    /// itself, then its simple plural and -ing / -ed stems. The exact rule
    /// (mirrored on Windows):
    /// - -ies / -ied → -y (skies → sky, cried → cry);
    /// - -es → the stem; -s → the stem (not -ss, and the stem keeps 3+ letters);
    /// - -ing, -ed, and -in' with its apostrophe (lovin') → the stem, with a
    ///   doubled last consonant undone (spinning → spin) and an -e added
    ///   (loving → love). A one-syllable stem ending consonant-vowel-consonant
    ///   takes only the -e form, so staring is stare, never star.
    /// Stems keep at least 3 letters, so seed, shed and his never fold.
    static func englishForms(_ word: String) -> [String] {
        guard let base = englishKey(word) else { return [] }
        var forms = [base]
        func add(_ form: String) {
            if form.count >= 3, !forms.contains(form) { forms.append(form) }
        }
        let vowels: Set<Character> = ["a", "e", "i", "o", "u"]
        if base.count > 4, base.hasSuffix("ies") || base.hasSuffix("ied") {
            add(String(base.dropLast(3)) + "y")
        }
        if base.hasSuffix("es"), base.count > 4 { add(String(base.dropLast(2))) }
        if base.hasSuffix("s"), !base.hasSuffix("ss"), base.count > 3 { add(String(base.dropLast())) }
        let trimmed = word.trimmingCharacters(in: .whitespaces)
        let droppedG = base.hasSuffix("in") && (trimmed.hasSuffix("in'") || trimmed.hasSuffix("in\u{2019}"))
        for suffix in ["ing", "ed"] where base.hasSuffix(suffix) || (suffix == "ing" && droppedG) {
            let length = suffix == "ing" && droppedG && !base.hasSuffix("ing") ? 2 : suffix.count
            let stem = String(base.dropLast(length))
            guard stem.count >= 3 else { continue }
            let letters = Array(stem)
            let last = letters[letters.count - 1]
            if letters.count >= 3, letters[letters.count - 2] == last, !vowels.contains(last), !"lsz".contains(last) {
                add(String(stem.dropLast()))
            }
            let syllables = stem.split(whereSeparator: { !vowels.contains($0) }).count
            let cvc = letters.count >= 3 && !vowels.contains(last) && !"wxy".contains(last)
                && vowels.contains(letters[letters.count - 2]) && !vowels.contains(letters[letters.count - 3])
            if !(cvc && syllables == 1) { add(stem) }
            add(stem + "e")
        }
        return forms
    }

    /// The list key `word` matches in `list` (its keys already in
    /// `englishKey` form), or nil.
    static func match<Value>(_ word: String, in list: [String: Value]) -> (key: String, value: Value)? {
        for form in englishForms(word) {
            if let value = list[form] { return (form, value) }
        }
        return nil
    }

    /// Whether `name` is an SF Symbol this Mac can draw. A typo in the
    /// user's list then draws nothing and takes no room.
    @MainActor
    static func isDrawable(_ name: String) -> Bool {
        if let known = drawable[name] { return known }
        let ok = NSImage(systemSymbolName: name, accessibilityDescription: nil) != nil
        drawable[name] = ok
        return ok
    }
    @MainActor private static var drawable: [String: Bool] = [:]
}

/// A motion an action word makes once, as it is sung.
enum WordAction: String, CaseIterable, Identifiable {
    /// One turn about the word's upright axis.
    case spin
    /// Drifts up, hovers, settles back.
    case float
    /// Dips down and comes back.
    case dip
    /// A small, quickly fading shake.
    case shake
    /// A hop and a small rebound.
    case hop
    /// Swells and settles.
    case grow
    /// Draws in and settles.
    case shrink
    /// Fades most of the way out and back.
    case dissolve
    /// A warm flickering glow.
    case flicker
    /// Frost spreads over the word ("Freeze"; stored as "frost").
    case frost
    /// Red soaks into the letters and runs from them.
    case bleed
    /// A shadow swallows the word from one edge, then light returns.
    case dark
    /// Letters crack into shards and snap back.
    case shatter
    /// Letters droop and drip, then firm up.
    case melt
    /// Fading copies trail behind the word.
    case echo
    /// A double thump like a heartbeat.
    case pulse
    /// A sine wave rolls through the letters.
    case wave
    /// Sinks below the line with a few bubbles.
    case sink
    // Added 23 Nov 2026.
    /// A warm sheen sweeps across the letters and soft rays fan up from behind.
    case sunlight
    /// A cool silver sheen sweeps across the letters; no rays.
    case moonlight
    /// Slides forward with a motion streak, letters bunching, then eases back.
    case rush
    /// Slices shift sideways with a brief colour split for a few frames.
    case glitch
    /// Small four-point twinkles light on a few letters in turn.
    case sparkle
    /// The last letters lift off and drift up and away, then come home.
    case free
    /// The letter spacing widens slowly, then closes.
    case stretch

    var id: String { rawValue }

    /// An action as a word list stores it: its raw value, or (for a list
    /// saved by hand or by an older build) its title or a former name.
    /// Unknown names, and actions no longer offered (explode, scream,
    /// whisper, ghost, glow, electric), give nil, so the word is ignored
    /// rather than the list rewritten.
    init?(listValue: String) {
        let name = listValue.trimmingCharacters(in: .whitespaces).lowercased()
        if let action = WordAction(rawValue: name) { self = action; return }
        let aliases: [String: WordAction] = [
            "freeze": .frost, "ice": .frost, "warm flicker": .flicker, "burn": .flicker, "fire": .flicker,
            "heartbeat": .pulse, "fade": .dissolve, "vanish": .dissolve, "fall": .dip, "jump": .hop,
            "bounce": .hop, "drown": .sink, "break": .shatter, "blood": .bleed, "darkness": .dark,
            "turn": .spin, "rotate": .spin, "fly": .float, "tremble": .shake, "ocean": .wave,
            "sun": .sunlight, "sunshine": .sunlight, "moon": .moonlight, "run": .rush, "speed": .rush,
            "static": .glitch, "shine": .sparkle, "twinkle": .sparkle, "fly away": .free, "freedom": .free,
            "far": .stretch,
        ]
        guard let action = aliases[name] else { return nil }
        self = action
    }

    var title: String {
        switch self {
        case .spin: return "Spin"
        case .float: return "Float"
        case .dip: return "Dip"
        case .shake: return "Shake"
        case .hop: return "Hop"
        case .grow: return "Grow"
        case .shrink: return "Shrink"
        case .dissolve: return "Dissolve"
        case .flicker: return "Burn"
        case .frost: return "Freeze"
        case .bleed: return "Bleed"
        case .dark: return "Dark"
        case .shatter: return "Shatter"
        case .melt: return "Melt"
        case .echo: return "Echo"
        case .pulse: return "Heartbeat"
        case .wave: return "Wave"
        case .sink: return "Sink"
        case .sunlight: return "Sunlight"
        case .moonlight: return "Moonlight"
        case .rush: return "Rush"
        case .glitch: return "Glitch"
        case .sparkle: return "Sparkle"
        case .free: return "Free"
        case .stretch: return "Stretch"
        }
    }

    /// "Action words"' default list, as decided for both apps.
    static let defaultList: [(word: String, action: WordAction)] = [
        // Plurals, -ing and -ed fold through `englishForms` (bleeds,
        // melting, echoes, shattered, drowning, spinning); irregular forms
        // are listed. Phrases (`ActionPhrases`) let an everyday word move
        // only in company: "go" floats in "let go", "turn" spins in "turn
        // around". Vivid words the motion shows, not everyday ones
        // ("love", "go", "away", "down", "turn", and the very common
        // "night", "heart", "high" and "rain", are left out: measured on
        // a few hundred songs, they made action words constant).
        ("spin", .spin), ("twirl", .spin), ("whirl", .spin), ("whirlwind", .spin), ("spiral", .spin),
        ("tornado", .spin), ("dizzy", .spin), ("carousel", .spin), ("revolve", .spin),
        ("swirl", .spin), ("orbit", .spin), ("turn around", .spin),
        ("levitate", .float), ("float", .float), ("fly", .float), ("flying", .float), ("flew", .float),
        ("soar", .float), ("drift", .float), ("heaven", .float), ("clouds", .float), ("weightless", .float),
        ("wings", .float), ("higher", .float), ("balloon", .float), ("feather", .float), ("hover", .float),
        // "let it go" too, on the owner's yes (6 lines in 3 of 180 songs).
        ("let go", .float), ("let it go", .float),
        ("fall", .dip), ("falling", .dip), ("fell", .dip), ("drop", .dip), ("dive", .dip), ("tumble", .dip),
        ("gravity", .dip), ("plunge", .dip), ("collapse", .dip), ("crumble", .dip),
        ("shake", .shake), ("shook", .shake), ("tremble", .shake), ("shiver", .shake), ("nervous", .shake),
        ("quake", .shake), ("earthquake", .shake), ("scared", .shake), ("rattle", .shake),
        ("bounce", .hop), ("jump", .hop), ("leap", .hop), ("hop", .hop), ("dance", .hop), ("dancing", .hop),
        ("grow", .grow), ("grew", .grow), ("rise", .grow), ("rising", .grow), ("bigger", .grow),
        ("bloom", .grow), ("blossom", .grow), ("swell", .grow), ("expand", .grow), ("towering", .grow),
        ("shrink", .shrink), ("smaller", .shrink), ("tiny", .shrink),
        ("fade", .dissolve), ("disappear", .dissolve), ("vanish", .dissolve), ("gone", .dissolve),
        ("dust", .dissolve), ("ghost", .dissolve), ("forget", .dissolve), ("forgotten", .dissolve),
        ("erase", .dissolve), ("evaporate", .dissolve), ("dissolve", .dissolve), ("blur", .dissolve), ("haze", .dissolve),
        ("mist", .dissolve),
        ("burn", .flicker), ("burnt", .flicker), ("fire", .flicker), ("flame", .flicker), ("blaze", .flicker),
        ("ignite", .flicker), ("spark", .flicker), ("embers", .flicker), ("smoke", .flicker), ("heat", .flicker),
        ("freeze", .frost), ("frozen", .frost), ("froze", .frost), ("cold", .frost), ("colder", .frost),
        ("ice", .frost), ("icy", .frost), ("winter", .frost), ("snow", .frost), ("numb", .frost), ("chill", .frost),
        ("frost", .frost),
        ("bleed", .bleed), ("bled", .bleed), ("blood", .bleed), ("bloody", .bleed), ("wound", .bleed), ("scar", .bleed),
        ("veins", .bleed),
        ("dark", .dark), ("darker", .dark), ("darkest", .dark), ("darkness", .dark), ("shadow", .dark),
        ("midnight", .dark), ("black", .dark), ("blackout", .dark), ("void", .dark),
        ("break", .shatter), ("broke", .shatter), ("broken", .shatter), ("shatter", .shatter), ("crack", .shatter),
        ("pieces", .shatter), ("crash", .shatter), ("smash", .shatter), ("heartbreak", .shatter),
        ("apart", .shatter), ("fracture", .shatter), ("splinter", .shatter), ("fall apart", .shatter), ("fell apart", .shatter),
        // "glass" on the owner's yes; "a glass of", "raise a glass" stay still (`ActionPhrases.blocking`).
        ("glass", .shatter),
        ("melt", .melt), ("drip", .melt), ("meltdown", .melt), ("thaw", .melt), ("liquid", .melt), ("wax", .melt),
        ("echo", .echo), ("repeat", .echo), ("resonate", .echo), ("voices", .echo), ("calling", .echo), ("hollow", .echo), ("canyon", .echo),
        ("heartbeat", .pulse), ("pulse", .pulse), ("beat", .pulse), ("alive", .pulse),
        ("pounding", .pulse), ("drum", .pulse), ("drums", .pulse), ("rhythm", .pulse),
        ("wave", .wave), ("ocean", .wave), ("sea", .wave), ("tide", .wave), ("river", .wave), ("surf", .wave),
        ("shore", .wave), ("flood", .wave), ("sway", .wave), ("water", .wave),
        ("sink", .sink), ("sank", .sink), ("sunk", .sink), ("drown", .sink), ("deep", .sink), ("deeper", .sink),
        ("underwater", .sink), ("abyss", .sink), ("anchor", .sink), ("quicksand", .sink),
        // Added 23 Nov 2026 after the owner saw the renders.
        ("sun", .sunlight), ("sunshine", .sunlight), ("sunlight", .sunlight), ("sunrise", .sunlight),
        ("sunset", .sunlight), ("dawn", .sunlight), ("daybreak", .sunlight), ("daylight", .sunlight),
        ("sunbeam", .sunlight), ("golden hour", .sunlight), ("light up", .sunlight), ("lit up", .sunlight),
        ("into the light", .sunlight), ("break of dawn", .sunlight),
        ("moon", .moonlight), ("moonlight", .moonlight), ("moonlit", .moonlight), ("moonbeam", .moonlight),
        ("starlight", .moonlight),
        // Not "run": measured on a library of 180 songs it was the most
        // frequent trigger of all (46 lines).
        ("rush", .rush), ("chase", .rush), ("speed", .rush), ("escape", .rush), ("race", .rush),
        ("fast", .rush), ("racing", .rush),
        ("glitch", .glitch), ("static", .glitch), ("signal", .glitch), ("error", .glitch),
        ("malfunction", .glitch), ("machine", .glitch), ("robot", .glitch), ("rewind", .glitch),
        ("shine", .sparkle), ("shiny", .sparkle), ("sparkle", .sparkle), ("glitter", .sparkle),
        ("diamond", .sparkle), ("gold", .sparkle), ("golden", .sparkle), ("bright", .sparkle),
        ("twinkle", .sparkle), ("stars", .sparkle),
        ("free", .free), ("freedom", .free), ("release", .free), ("birds", .free), ("break free", .free),
        ("fly away", .free),
        ("stretch", .stretch), ("reach", .stretch), ("distance", .stretch), ("endless", .stretch),
        ("far away", .stretch), ("miles away", .stretch),
    ]

    static let defaults: [String: String] =
        Dictionary(defaultList.map { ($0.word, $0.action.rawValue) }, uniquingKeysWith: { first, _ in first })

    /// New actions and their words, shown to the owner as renders before
    /// they join `defaultList` (the other languages' built-in lists leave
    /// them out, and they move only the user's own words). A word here is
    /// in no other list. Empty: the last seven were approved on 23 Nov 2026.
    static let candidateList: [(word: String, action: WordAction)] = [
    ]

    /// The actions still waiting for the owner's yes.
    static let candidates: Set<WordAction> = Set(candidateList.map(\.action))
        .subtracting(defaultList.map(\.action))

    static let candidateDefaults: [String: String] =
        Dictionary(candidateList.map { ($0.word, $0.action.rawValue) }, uniquingKeysWith: { first, _ in first })
}

// MARK: - Phrases

/// Two- and three-word action phrases ("let go", "fall apart"), and the few
/// phrases that keep a word from moving ("take a break" is no shatter).
/// A phrase is a list key whose words (each as `englishKey`) are one space
/// apart. Each of its words matches a run through `englishForms` (falling
/// apart, letting go); irregular forms are listed. The accent goes on the
/// phrase's last word, and a phrase comes before a single word starting at
/// the same place, so "fall apart" shatters where "fall" alone would dip.
enum ActionPhrases {
    /// Built-in, whatever the list: words in these phrases never move
    /// (a list phrase that is the same words wins over them).
    static let blocking: [[String]] = [
        ["take", "a", "break"], ["give", "me", "a", "break"], ["out", "cold"], ["ice", "cream"],
        ["glass", "of"], ["raise", "a", "glass"], ["raise", "your", "glass"], ["raise", "our", "glass"],
    ]

    /// `text` as a phrase key: its words as `englishKey`, one space apart.
    /// Nil for a single word, or if any word is not plain English letters.
    static func key(_ text: String) -> String? {
        let parts = text.split(whereSeparator: \.isWhitespace).map { LyricWordIcons.englishKey(String($0)) }
        guard parts.count >= 2, !parts.contains(nil) else { return nil }
        return parts.compactMap { $0 }.joined(separator: " ")
    }

    /// The line's action, if any: which run (of `runs`, the line's visible
    /// runs as they spell their words) moves, the key it matched and its
    /// action. Reading order; at each run a list phrase starting there
    /// (longest first), then a blocking phrase (its runs are skipped), then
    /// the run as a single word (`ActionLexicon.match`). The first wins.
    static func hit(_ runs: [String], actions: [String: WordAction],
                    script: ActionLexicon.Script) -> (run: Int, key: String, action: WordAction)? {
        let phrases = actions.compactMap { key, action -> (words: [String], key: String, action: WordAction)? in
            key.contains(" ") ? (key.split(separator: " ").map(String.init), key, action) : nil
        }.sorted { $0.words.count != $1.words.count ? $0.words.count > $1.words.count : $0.key < $1.key }
        var forms: [Int: [String]] = [:]
        func matches(_ words: [String], at start: Int) -> Bool {
            guard start + words.count <= runs.count else { return false }
            for (offset, word) in words.enumerated() {
                let index = start + offset
                let found = forms[index] ?? LyricWordIcons.englishForms(runs[index])
                forms[index] = found
                guard found.contains(word) else { return false }
            }
            return true
        }
        var index = 0
        while index < runs.count {
            if let phrase = phrases.first(where: { matches($0.words, at: index) }) {
                return (index + phrase.words.count - 1, phrase.key, phrase.action)
            }
            if let block = blocking.first(where: { matches($0, at: index) }) {
                index += block.count
                continue
            }
            if let found = ActionLexicon.match(runs[index], user: actions, script: script) {
                return (index, found.key, found.action)
            }
            index += 1
        }
        return nil
    }
}

/// What one word wears: its symbol, its action, or both.
struct WordAccent: Equatable {
    var symbol: String?
    var action: WordAction?
    /// A repeat of an action word already heard in the song (every second
    /// repeat): it moves gently rather than in full (`WordAccentPlan.repeats`).
    var gentle = false
}

/// The accents of one or more lines, by the key of each run's first token
/// (`TimedWordKey`). Empty when both features are off.
struct WordAccentPlan: Equatable {
    /// At most this many symbols on one line.
    static let maxSymbolsPerLine = 2
    /// At most this many action words on one line.
    static let maxActionsPerLine = 1

    var accents: [TimedWordKey: WordAccent] = [:]
    static let none = WordAccentPlan()

    var isEmpty: Bool { accents.isEmpty }
    subscript(_ key: TimedWordKey?) -> WordAccent? { key.flatMap { accents[$0] } }

    mutating func merge(_ other: WordAccentPlan) {
        accents.merge(other.accents) { first, _ in first }
    }

    /// The plan for one line: its tokens joined into runs by `glue` (a
    /// glued run, beau·ti·ful, is matched as the word it spells), each run
    /// looked up in the lists in reading order. The first two runs with a
    /// symbol get theirs, a symbol a word earlier in the line already
    /// showed is not shown again ("love … love" and "love … loving" show
    /// one heart, "him … her" one person), and only the first action word
    /// moves. Lists map
    /// `englishKey` words to SF Symbol names and `WordAction` raw values;
    /// nil turns that feature off.
    static func make(words: [LyricsFetcher.Word], glue: [Bool],
                     symbols: [String: String]?, actions: [String: WordAction]?,
                     gentle: Bool = false) -> WordAccentPlan {
        guard symbols?.isEmpty == false || actions != nil else { return .none }
        var plan = WordAccentPlan()
        var shownSymbols = 0
        var seen = Set<String>()  // symbols shown
        let runs = visibleRuns(words, glue: glue)
        let hit = actions.flatMap { ActionPhrases.hit(runs.map(\.text), actions: $0, script: script(words)) }
        for (index, run) in runs.enumerated() {
            let range = run.range, text = run.text
            guard let first = words[range].first(where: { LyricRuns.isVisible($0.text) }) else { continue }
            var accent = WordAccent()
            if let symbols, shownSymbols < maxSymbolsPerLine,
               let found = LyricWordIcons.match(text, in: symbols), !seen.contains(found.value) {
                seen.insert(found.value)
                accent.symbol = found.value
                shownSymbols += 1
            }
            if let hit, hit.run == index {
                accent.action = hit.action
                accent.gentle = gentle
            }
            if accent != WordAccent() {
                plan.accents[first.presentationID] = accent
                // Also under the run's first token as written, should a
                // layout key the run by an invisible leading token.
                if let lead = words[range].first, lead.presentationID != first.presentationID {
                    plan.accents[lead.presentationID] = accent
                }
            }
        }
        return plan
    }

    /// The word key of the line's action word, if any (the same matching
    /// as `make`, so the two always agree).
    static func actionKey(words: [LyricsFetcher.Word], glue: [Bool], actions: [String: WordAction]) -> String? {
        ActionPhrases.hit(visibleRuns(words, glue: glue).map(\.text), actions: actions, script: script(words))?.key
    }

    /// The line's runs that show something, each with the text it spells.
    private static func visibleRuns(_ words: [LyricsFetcher.Word], glue: [Bool]) -> [(range: Range<Int>, text: String)] {
        LyricRuns.ranges(glue, count: words.count).compactMap { range in
            guard words[range].contains(where: { LyricRuns.isVisible($0.text) }) else { return nil }
            return (range, words[range].map(\.text).joined())
        }
    }

    private static func script(_ words: [LyricsFetcher.Word]) -> ActionLexicon.Script {
        ActionLexicon.Script(words.map(\.text).joined(separator: " "))
    }

    /// Which lines of a song move gently: an action word acts in full the
    /// first time it is sung, and its later repeats alternate gentle and
    /// full (2nd gentle, 3rd full, 4th gentle, ...), so a chorus does not
    /// tire. A pure function of the song's lines and the lists, so seeking,
    /// layout changes and memo evictions always agree; kept per song.
    @MainActor
    static func gentleLines(_ lines: [LyricsFetcher.Line], fingerprint: Int, settings: Settings = .shared,
                            liftsBacking: Bool = BackingVocals.isOn) -> Set<Int> {
        guard let actions = settings.wordAccentLists.actions else { return [] }
        let key = RepeatKey(fingerprint: fingerprint, version: settings.wordAccentLists.version, lifts: liftsBacking)
        if let cached = repeatMemo[key] { return cached }
        var heard: [String: Int] = [:]
        var gentle = Set<Int>()
        for (index, line) in lines.enumerated() where !line.words.isEmpty {
            var words = line.words
            var glue = LyricRuns.rollingGlue(line: line, words: words)
            if liftsBacking {
                let split = BackingVocals.split(words, glue: glue)
                words = split.main; glue = split.mainGlue
            }
            guard let found = actionKey(words: words, glue: glue, actions: actions) else { continue }
            let count = heard[found, default: 0]
            if !count.isMultiple(of: 2) { gentle.insert(index) }
            heard[found] = count + 1
        }
        if repeatMemo.count > 8 { repeatMemo.removeAll(keepingCapacity: true) }
        repeatMemo[key] = gentle
        return gentle
    }
    private struct RepeatKey: Hashable {
        let fingerprint: Int
        let version: Int
        let lifts: Bool
    }
    @MainActor private static var repeatMemo: [RepeatKey: Set<Int>] = [:]

    /// The plan for one line with the app's lists, kept for the line so a
    /// frame never matches the words again. Bracketed backing vocals, which
    /// `LyricLineView` lifts out and draws without accents, are left out
    /// first (when `liftsBacking`), so "(love)" never takes the lead's
    /// symbol or action.
    @MainActor
    static func line(_ words: [LyricsFetcher.Word], glue: [Bool], settings: Settings = .shared,
                     liftsBacking: Bool = BackingVocals.isOn, gentle: Bool = false) -> WordAccentPlan {
        let lists = settings.wordAccentLists
        guard lists.symbols != nil || lists.actions != nil else { return .none }
        let key = MemoKey(words: words.map(\.presentationID), glue: glue, version: lists.version, lifts: liftsBacking,
                          gentle: gentle)
        if let cached = memo[key] { return cached }
        if memo.count > 96 { memo.removeAll(keepingCapacity: true) }
        let lead = liftsBacking ? BackingVocals.split(words, glue: glue) : nil
        let plan = make(words: lead?.main ?? words, glue: lead?.mainGlue ?? glue,
                        symbols: lists.symbols, actions: lists.actions, gentle: gentle)
        memo[key] = plan
        return plan
    }

    /// A plan built once per `key` (a rolling window's runs) and list
    /// version, for callers that combine several lines' plans.
    @MainActor
    static func kept(_ key: [TimedWordKey], settings: Settings = .shared, liftsBacking: Bool = BackingVocals.isOn,
                     build: () -> WordAccentPlan) -> WordAccentPlan {
        let lists = settings.wordAccentLists
        guard lists.symbols != nil || lists.actions != nil else { return .none }
        let memoKey = MemoKey(words: key, glue: [], version: lists.version, lifts: liftsBacking, window: true)
        if let cached = memo[memoKey] { return cached }
        if memo.count > 96 { memo.removeAll(keepingCapacity: true) }
        let plan = build()
        memo[memoKey] = plan
        return plan
    }

    private struct MemoKey: Hashable {
        let words: [TimedWordKey]
        let glue: [Bool]
        let version: Int
        var lifts = false
        var window = false
        var gentle = false
    }
    @MainActor private static var memo: [MemoKey: WordAccentPlan] = [:]

    /// Rekeys the plan for a rolling stage, whose words are glued runs
    /// already: each run by its own key, found through its first token.
    func rekeyed(for runs: [LyricsFetcher.Word], parts: [TimedWordKey: [LyricsFetcher.Word]]) -> WordAccentPlan {
        var result = WordAccentPlan()
        for run in runs {
            let key = run.presentationID
            let first = parts[key]?.first(where: { LyricRuns.isVisible($0.text) })?.presentationID ?? key
            if let accent = accents[first] ?? accents[key] { result.accents[key] = accent }
        }
        return result
    }
}

// MARK: - Motion

/// Draws an action word's motion on top of its layout's own
/// (`LivelyActionEffect`). Render-only transforms: the word's measured
/// place, and so the line's fitting, never change.
struct WordActionEffect: ViewModifier {
    let action: WordAction
    /// Seconds since the word's onset.
    let elapsed: Double
    /// The word's type size, for travel in ems.
    let size: CGFloat
    /// The word's own glow ink.
    var glow: Color = .white
    /// Seconds the word is held (end − onset); the motion grows with it.
    var hold: Double? = nil
    /// A chorus repeat (`WordAccent.gentle`): moves gently.
    var gentle = false
    /// The word's onset, to set against its line's exit.
    var start: Double = -.infinity
    @Environment(\.lyricReduceMotion) private var reduceMotion
    @Environment(\.lyricActionStrength) private var strength
    @Environment(\.lyricLineExit) private var lineExit

    /// How long an action still moving takes to settle once its line starts
    /// to leave: on a line's last word an action of 1-2 s outlasted the
    /// line and was cut off mid-motion as it left.
    static let settle = 0.35

    /// 1 until the line starts to leave, then down to 0 over `settle`.
    static func settling(at time: Double, lineExit: Double) -> Double {
        guard lineExit.isFinite else { return 1 }
        return 1 - Easing.smoothstep((time - lineExit) / settle)
    }

    func body(content: Content) -> some View {
        let settling = Self.settling(at: start + elapsed, lineExit: lineExit)
        content.modifier(LivelyActionEffect(action: action, elapsed: elapsed, hold: hold ?? 0.6,
                                            amount: strength * (gentle ? LivelyAction.gentleShare : 1) * settling,
                                            size: size, glow: glow, reduceMotion: reduceMotion, gentle: gentle))
    }
}

extension View {
    /// `accent`'s action, if any, from the word's onset `start`.
    @ViewBuilder
    func wordAction(_ accent: WordAccent?, start: Double, end: Double? = nil, time: Double, size: CGFloat,
                    glow: Color = .white) -> some View {
        if let action = accent?.action {
            modifier(WordActionEffect(action: action, elapsed: time - start, size: size, glow: glow,
                                      hold: end.map { $0 - start }, gentle: accent?.gentle ?? false, start: start))
        } else {
            self
        }
    }
}

// MARK: - Symbol

/// A word's symbol: sized and spaced from its type size, drawn in its ink.
enum WordSymbolMark {
    /// The symbol's point size against the word's.
    static let scale: CGFloat = 0.5
    /// The gap between the word and its symbol, in ems of the word.
    static let gap: CGFloat = 0.16
    /// Seconds the symbol takes to fade in from the word's onset.
    static let fadeIn = 0.3

    /// The onset-relative time a symbol's entrance needs, while it
    /// is still coming in (nil before its onset is known or once settled),
    /// so a settled symbol's view stays equal frame to frame.
    static func entering(elapsed: Double) -> Double? {
        elapsed > -0.1 && elapsed < SymbolEntrancePose.duration ? elapsed : nil
    }

    /// How much of the symbol shows, `elapsed` seconds after its word's
    /// onset: nothing before it (a faint preview of the word shows none).
    static func opacity(elapsed: Double) -> Double {
        Easing.smoothstep(elapsed / fadeIn)
    }

    static func font(_ size: CGFloat) -> Font {
        .system(size: max(6, size * scale), weight: .semibold)
    }

    /// For a layout that sets its word as `Text`: a gap and the symbol,
    /// appended to the word so the layout measures them with it. The
    /// symbol is there, clear, from the line's first frame, so the word's
    /// width never changes as it fades in.
    static func inline(_ symbol: String, size: CGFloat, colour: Color, opacity: Double) -> Text {
        (Text(" ").font(.system(size: max(4, size * gap * 2.2)))
            + Text(Image(systemName: symbol)).font(font(size)).foregroundStyle(colour.opacity(opacity))).livelyStill()
    }
}

/// A word's symbol as a view of its own, for the layouts that set the word
/// in layers: in the rest ink, with the sung ink crossing over it as the
/// word's colour fill does (`tint`).
struct WordSymbolView: View, Equatable {
    let symbol: String
    let size: CGFloat
    let rest: Color
    let sung: Color
    var tint: Double = 0
    var opacity: Double = 1
    /// Seconds since the word's onset while the symbol is still coming in
    /// (nil once it has settled, or for a preview), and the word's action:
    /// it pops in (`SymbolEntrancePose`); under Reduce Motion it only fades.
    var elapsed: Double? = nil
    var action: WordAction? = nil
    @Environment(\.lyricReduceMotion) private var reduceMotion

    /// The entrance time to draw, or nil for a plain fade (Reduce Motion,
    /// a settled symbol or a preview).
    static func entrance(elapsed: Double?, reduceMotion: Bool) -> Double? {
        guard !reduceMotion, let elapsed, elapsed < SymbolEntrancePose.duration else { return nil }
        return elapsed
    }

    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.symbol == rhs.symbol && lhs.size == rhs.size && lhs.rest == rhs.rest && lhs.sung == rhs.sung
            && lhs.tint == rhs.tint && lhs.opacity == rhs.opacity && lhs.elapsed == rhs.elapsed && lhs.action == rhs.action
    }

    var body: some View {
        let glyph = ZStack {
            Text(Image(systemName: symbol)).font(WordSymbolMark.font(size)).foregroundStyle(rest)
                .opacity(1 - tint)
            if tint > 0 {
                Text(Image(systemName: symbol)).font(WordSymbolMark.font(size)).foregroundStyle(sung)
                    .opacity(tint)
            }
        }
        Group {
            if let elapsed = Self.entrance(elapsed: elapsed, reduceMotion: reduceMotion) {
                glyph.modifier(SymbolEntranceEffect(action: action, elapsed: elapsed, size: size))
            } else {
                glyph
            }
        }
        .opacity(opacity)
        .accessibilityHidden(true)
    }
}

// MARK: - Environment

private struct LyricWordAccentsKey: EnvironmentKey {
    static let defaultValue = WordAccentPlan.none
}

extension EnvironmentValues {
    /// The accents of the line being drawn (`LyricLineView` sets them).
    var lyricWordAccents: WordAccentPlan {
        get { self[LyricWordAccentsKey.self] }
        set { self[LyricWordAccentsKey.self] = newValue }
    }
}
