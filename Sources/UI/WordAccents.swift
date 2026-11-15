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
    /// A cool, paler frost tint.
    case frost

    var id: String { rawValue }

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
        case .flicker: return "Warm flicker"
        case .frost: return "Frost"
        }
    }

    /// Seconds, from the word's onset.
    var duration: Double {
        switch self {
        case .shake: return 0.6
        case .dip, .hop: return 0.7
        case .spin, .grow, .shrink: return 0.8
        case .float, .dissolve, .flicker, .frost: return 0.9
        }
    }

    /// "Action words"' default list, as decided for both apps.
    static let defaultList: [(word: String, action: WordAction)] = [
        ("spin", .spin),
        ("levitate", .float), ("float", .float), ("fly", .float), ("flying", .float),
        ("fall", .dip), ("falling", .dip), ("drop", .dip),
        ("shake", .shake), ("tremble", .shake), ("shiver", .shake),
        ("bounce", .hop), ("jump", .hop),
        ("grow", .grow), ("rise", .grow), ("rising", .grow),
        ("shrink", .shrink),
        ("fade", .dissolve), ("disappear", .dissolve), ("vanish", .dissolve),
        ("burn", .flicker), ("fire", .flicker),
        ("freeze", .frost), ("frozen", .frost), ("cold", .frost),
    ]

    static let defaults: [String: String] =
        Dictionary(defaultList.map { ($0.word, $0.action.rawValue) }, uniquingKeysWith: { first, _ in first })
}

/// What one word wears: its symbol, its action, or both.
struct WordAccent: Equatable {
    var symbol: String?
    var action: WordAction?
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
                     symbols: [String: String]?, actions: [String: WordAction]?) -> WordAccentPlan {
        guard symbols?.isEmpty == false || actions?.isEmpty == false else { return .none }
        var plan = WordAccentPlan()
        var shownSymbols = 0, moved = 0
        var seen = Set<String>()  // symbols shown
        for range in LyricRuns.ranges(glue, count: words.count) {
            let text = words[range].map(\.text).joined()
            guard let first = words[range].first(where: { LyricRuns.isVisible($0.text) }) else { continue }
            var accent = WordAccent()
            if let symbols, shownSymbols < maxSymbolsPerLine,
               let found = LyricWordIcons.match(text, in: symbols), !seen.contains(found.value) {
                seen.insert(found.value)
                accent.symbol = found.value
                shownSymbols += 1
            }
            if let actions, moved < maxActionsPerLine, let found = LyricWordIcons.match(text, in: actions) {
                accent.action = found.value
                moved += 1
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

    /// The plan for one line with the app's lists, kept for the line so a
    /// frame never matches the words again. Bracketed backing vocals, which
    /// `LyricLineView` lifts out and draws without accents, are left out
    /// first (when `liftsBacking`), so "(love)" never takes the lead's
    /// symbol or action.
    @MainActor
    static func line(_ words: [LyricsFetcher.Word], glue: [Bool], settings: Settings = .shared,
                     liftsBacking: Bool = BackingVocals.isOn) -> WordAccentPlan {
        let lists = settings.wordAccentLists
        guard lists.symbols != nil || lists.actions != nil else { return .none }
        let key = MemoKey(words: words.map(\.presentationID), glue: glue, version: lists.version, lifts: liftsBacking)
        if let cached = memo[key] { return cached }
        if memo.count > 96 { memo.removeAll(keepingCapacity: true) }
        let lead = liftsBacking ? BackingVocals.split(words, glue: glue) : nil
        let plan = make(words: lead?.main ?? words, glue: lead?.mainGlue ?? glue,
                        symbols: lists.symbols, actions: lists.actions)
        memo[key] = plan
        return plan
    }

    /// A plan built once per `key` (a rolling window's runs) and list
    /// version, for callers that combine several lines' plans.
    @MainActor
    static func kept(_ key: [TimedWordKey], settings: Settings = .shared, build: () -> WordAccentPlan) -> WordAccentPlan {
        let lists = settings.wordAccentLists
        guard lists.symbols != nil || lists.actions != nil else { return .none }
        let memoKey = MemoKey(words: key, glue: [], version: lists.version, lifts: BackingVocals.isOn, window: true)
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

/// One frame of an action word's motion, relative to where its layout has
/// the word: travel in ems of the word's type size, a turn about its
/// upright axis in degrees, and the colour cues. Exactly `identity`
/// before the onset and from `duration` on, so a word is never left
/// displaced, scaled, faded or tinted.
struct WordActionPose: Equatable {
    var dx = 0.0
    var dy = 0.0
    var turn = 0.0
    var scale = 1.0
    var opacity = 1.0
    /// 0...1: a glow in the word's own glow ink.
    var glow = 0.0
    /// 0...1: the word's ink multiplied warmer (orange) or cooler (blue).
    /// Multiplying never lightens, so dark lyrics keep their contrast.
    var warm = 0.0
    var cool = 0.0

    static let identity = WordActionPose()
    var isIdentity: Bool { self == .identity }

    /// Rises over the first `rise` of 0...1 and falls over the last `fall`.
    static func bump(_ p: Double, rise: Double, fall: Double) -> Double {
        Easing.smoothstep(p / rise) * (1 - Easing.smoothstep((p - (1 - fall)) / fall))
    }

    /// The pose `elapsed` seconds after the word's onset. Under Reduce
    /// Motion nothing moves, scales or fades: the word only glows in its
    /// own ink (warmer or cooler for the colour actions) and settles back.
    static func make(_ action: WordAction, elapsed: Double, reduceMotion: Bool) -> WordActionPose {
        let duration = action.duration
        guard elapsed > 0, elapsed < duration else { return .identity }
        let p = elapsed / duration
        var pose = WordActionPose()
        if reduceMotion {
            let cue = bump(p, rise: 0.2, fall: 0.5)
            switch action {
            case .flicker: pose.warm = 0.5 * cue; pose.glow = 0.5 * cue
            case .frost: pose.cool = 0.5 * cue
            default: pose.glow = 0.6 * cue
            }
            return pose
        }
        switch action {
        case .spin:
            // Smootherstep, so the turn starts and ends at rest.
            pose.turn = 360 * (p * p * p * (p * (p * 6 - 15) + 10))
        case .float:
            let lift = bump(p, rise: 0.35, fall: 0.3)
            pose.dy = -0.22 * lift - 0.02 * sin(2 * .pi * 2 * p) * lift
        case .dip:
            pose.dy = 0.16 * bump(p, rise: 0.3, fall: 0.55)
        case .shake:
            pose.dx = 0.045 * sin(2 * .pi * 5 * p) * (1 - p) * Easing.smoothstep(p / 0.08)
        case .hop:
            pose.dy = p < 0.6 ? -0.24 * sin(.pi * p / 0.6) : -0.06 * sin(.pi * (p - 0.6) / 0.4)
        case .grow:
            pose.scale = 1 + 0.16 * bump(p, rise: 0.35, fall: 0.4)
        case .shrink:
            pose.scale = 1 - 0.16 * bump(p, rise: 0.35, fall: 0.4)
        case .dissolve:
            pose.opacity = 1 - 0.7 * bump(p, rise: 0.3, fall: 0.45)
        case .flicker:
            let heat = bump(p, rise: 0.15, fall: 0.35)
            pose.warm = 0.6 * heat
            pose.glow = heat * (0.6 + 0.4 * abs(sin(2 * .pi * 6 * p)))
            pose.opacity = 1 - 0.12 * heat * abs(sin(2 * .pi * 7.3 * p))
        case .frost:
            pose.cool = 0.7 * bump(p, rise: 0.25, fall: 0.4)
        }
        return pose
    }

    /// The colour a word's ink is multiplied by.
    var multiply: Color {
        Color(red: 1 - 0.35 * cool, green: 1 - 0.25 * warm - 0.12 * cool, blue: 1 - 0.55 * warm)
    }
}

/// Draws an action word's motion on top of its layout's own. Render-only
/// transforms: the word's measured place, and so the line's fitting, never
/// change.
struct WordActionEffect: ViewModifier {
    let action: WordAction
    /// Seconds since the word's onset.
    let elapsed: Double
    /// The word's type size, for travel in ems.
    let size: CGFloat
    /// The word's own glow ink.
    var glow: Color = .white
    @Environment(\.lyricReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        let pose = WordActionPose.make(action, elapsed: elapsed, reduceMotion: reduceMotion)
        content
            .colorMultiply(pose.multiply)
            .shadow(color: glow.opacity(0.75 * pose.glow), radius: max(1, size * 0.22 * CGFloat(pose.glow)))
            // The turn about the upright axis as a flat 2D squeeze (cos of
            // the angle across): no 3D layer, and never wider than the word.
            .scaleEffect(x: CGFloat(pose.scale * cos(pose.turn * .pi / 180)), y: CGFloat(pose.scale))
            .offset(x: CGFloat(pose.dx) * size, y: CGFloat(pose.dy) * size)
            .opacity(pose.opacity)
    }
}

extension View {
    /// `accent`'s action, if any, from the word's onset `start`.
    @ViewBuilder
    func wordAction(_ accent: WordAccent?, start: Double, time: Double, size: CGFloat, glow: Color = .white) -> some View {
        if let action = accent?.action {
            modifier(WordActionEffect(action: action, elapsed: time - start, size: size, glow: glow))
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
        Text(" ").font(.system(size: max(4, size * gap * 2.2)))
            + Text(Image(systemName: symbol)).font(font(size)).foregroundStyle(colour.opacity(opacity))
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

    var body: some View {
        ZStack {
            Text(Image(systemName: symbol)).font(WordSymbolMark.font(size)).foregroundStyle(rest)
                .opacity(1 - tint)
            if tint > 0 {
                Text(Image(systemName: symbol)).font(WordSymbolMark.font(size)).foregroundStyle(sung)
                    .opacity(tint)
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
