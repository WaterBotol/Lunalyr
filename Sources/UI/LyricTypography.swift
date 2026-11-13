import SwiftUI

/// A last-value cache. It never publishes while a view is being evaluated.
/// Font loading, source/reading changes and layout controls form part of keys.
@MainActor
final class RenderMemo<Key: Equatable, Value>: ObservableObject {
    private var key: Key?
    private var cached: Value?
    private(set) var builds = 0
    func value(for requested: Key, build: () -> Value) -> Value {
        if key == requested, let cached { return cached }
        let value = build()
        key = requested; cached = value; builds += 1
        return value
    }
}

struct LyricFontIdentity: Equatable {
    var editorial: Bool = false
    var editorialAmount: Double = 1
    var randomize: Bool = false
    let choice: String
    let customRegular: String?
    let customBold: String?
    let appRegular: String?
    let appBold: String?
}

extension LyricFontIdentity {
    @MainActor
    init(settings: Settings) {
        editorial = settings.editorialTypography
        editorialAmount = settings.editorialAmount
        randomize = settings.randomizeLyricFonts
        choice = settings.fontChoice.rawValue
        customRegular = settings.googleRegular; customBold = settings.googleBold
        appRegular = AppFont.regular; appBold = AppFont.bold
    }
}

enum ScatteredTypography {
    struct Key: Equatable {
        let words: [LyricsFetcher.Word]
        let baseSize: CGFloat
        let style: LyricLineStyle
        let mood: TrackMood
        let composition: LyricComposition
        let spread: Double
        let fontIdentity: LyricFontIdentity
        var readings: [TimedWordKey: String] = [:]
        var wordTimed = true
        var lineEnd: Double?
        /// Per word: continues the previous word's run (see `LyricRuns`).
        var glue: [Bool] = []
    }
    struct Word: Equatable {
        let index: Int
        let source: LyricsFetcher.Word
        /// The run's metrics: every token of a glued run shares one size,
        /// face, baseline and slot.
        let metrics: WordMetrics
        let label: String
        let characters: [String]
        let font: Font
        let reading: LyricReadingInk?
        let sustain: Double
        /// Seeded from the word and its timestamp, so the same word sung
        /// twice never moves the same way.
        let seed: UInt64
        /// Time to the next onset: sets how briskly the word moves.
        let cadence: Double
        /// Reading position in the line, 0...1: staggers exits, places colour.
        let order: Double
        var slot: Double { order }
        let rightToLeft: Bool
        let lineRightToLeft: Bool
        /// Whether glyphs may be swelled one at a time (see `LyricScript`).
        let glyphMotion: Bool
        /// The run this token belongs to.
        var run = 0
    }
    /// Glued tokens as the layout places them: one shaped label, one slot.
    /// A line of separate words has one run per word.
    struct Run: Equatable {
        let index: Int
        let tokens: Range<Int>
        let label: String
        let font: Font
        let metrics: WordMetrics
        let reading: LyricReadingInk?
        /// The run as one timed word: first onset to last end.
        let source: LyricsFetcher.Word
        let seed: UInt64
        let order: Double
        /// Held-ness of a single-token run (per-glyph lighting); glued runs
        /// light through the shared sweep instead.
        let sustain: Double
        let rightToLeft: Bool
        let glyphMotion: Bool
        /// Where each token begins and ends along the label, 0...1 by
        /// character, so a token's fill and reveal fall on its own letters.
        let boundaries: [Double]
        /// The words a reader takes in along the run, each highlighted on
        /// its own by "Cutout · Highlight": one for a single word or a word
        /// sung in syllables, several for a run of Japanese or Chinese.
        var segments: [Segment] = []
        var isGlued: Bool { tokens.count > 1 }
        /// Fixed clearance around the ink: the slot never grows with the word.
        var inset: CGSize { CGSize(width: metrics.size * 0.12, height: metrics.size * 0.05) }
    }
    /// One sung word of a run: where it falls along the run's label, when
    /// it is sung, and the end of its line.
    struct Segment: Equatable {
        var from: Double
        var to: Double
        var onset: Double
        var end: Double
        var release: Double
    }
    struct Layout: Equatable {
        let words: [Word]
        let runs: [Run]
        let rightToLeft: Bool
    }
    private static let functionWords: Set<String> = [
        "a", "an", "the", "and", "or", "but", "so", "if", "as", "of", "to", "in",
        "on", "at", "by", "for", "with", "from", "up", "out", "is", "am", "are",
        "was", "were", "be", "been", "do", "did", "it", "its", "i", "im", "you",
        "youre", "me", "my", "we", "he", "she", "they", "them", "this", "that",
        "there", "here", "just", "got", "gonna", "wanna",
    ]
    @MainActor
    static func prepare(_ key: Key, settings: Settings) -> [Word] {
        layout(key, settings: settings).words
    }

    /// Tokens and the runs they form. Size, weight and placement are worked
    /// out per run, from the run's own timing, so a word split into timed
    /// syllables sets exactly as the whole word would.
    @MainActor
    static func layout(_ key: Key, settings: Settings) -> Layout {
        let visible = LyricRuns.visible(key.words, glue: key.glue)
        let words = visible.words
        let ranges = LyricRuns.ranges(visible.glue, count: words.count)
        let sources = ranges.map { range -> LyricsFetcher.Word in
            guard range.count > 1 else { return words[range.lowerBound] }
            let tokens = words[range]
            return LyricsFetcher.Word(text: tokens.map(\.text).joined().trimmingCharacters(in: .whitespaces),
                                      start: tokens.first?.start ?? 0, end: tokens.map(\.end).max() ?? 0)
        }
        let durations = sources.map { $0.end - $0.start }.filter { $0 > 0.01 }
        let average = durations.isEmpty ? 0.3 : durations.reduce(0, +) / Double(durations.count)
        let fullness = min(Double(ranges.count) / 7, 1)
        let focal = key.wordTimed ? focalRun(sources, average: average) : nil
        let lineRightToLeft = LyricScript.lineIsRightToLeft(words.map(\.text))
        let span = Double(max(ranges.count - 1, 1))
        var tokens: [Word] = []
        var runs: [Run] = []
        tokens.reserveCapacity(words.count)
        for (runIndex, range) in ranges.enumerated() {
            let source = sources[runIndex]
            let metrics = metrics(index: runIndex, word: source, averageDuration: average, fullness: fullness,
                                  focal: focal, count: ranges.count, key: key)
            let seed = source.presentationID.visualSeed
            let font = settings.lyricFont(size: metrics.size, hero: metrics.isHero || metrics.isFocal, mood: key.mood,
                seed: seed, alternate: metrics.alternateFace)
            // Readings are secondary: smaller, regular weight, same face.
            let readingFont = settings.lyricFont(size: metrics.size * 0.56, hero: false, mood: key.mood,
                seed: seed, alternate: metrics.alternateFace)
            let order = ranges.count > 1 ? Double(runIndex) / span : 0.5
            let runCadence = runIndex + 1 < ranges.count
                ? max(0.05, sources[runIndex + 1].start - source.start) : max(source.end - source.start, 0.12)
            var labels: [String] = []
            for index in range {
                let word = words[index]
                let label = metrics.uppercased ? word.text.uppercased() : word.text
                labels.append(label)
                tokens.append(Word(index: index, source: word, metrics: metrics, label: label,
                    characters: label.map(String.init), font: font,
                    reading: key.readings[word.presentationID].map { LyricReadingInk(text: $0, font: readingFont) },
                    sustain: Easing.smoothstep(((word.end - word.start) - 0.45) / 0.85),
                    seed: word.presentationID.visualSeed,
                    // A run enters at the pace of the run, not of its first syllable.
                    cadence: index == range.lowerBound ? runCadence : OrganicMotion.cadence(of: words, at: index),
                    order: order,
                    rightToLeft: LyricScript.isRightToLeft(label),
                    lineRightToLeft: lineRightToLeft,
                    glyphMotion: LyricScript.allowsGlyphMotion(label),
                    run: runIndex))
            }
            let label = range.count > 1
                ? labels.joined().trimmingCharacters(in: .whitespaces) : labels.first ?? ""
            let total = Double(max(1, labels.reduce(0) { $0 + $1.count }))
            var boundaries: [Double] = [0], running = 0.0
            for text in labels {
                running += Double(text.count)
                boundaries.append(min(1, running / total))
            }
            // Each token's share of the run's reading, so the reading can
            // fill in as the run is sung (`LyricReadingInk.shares`).
            runs.append(Run(index: runIndex, tokens: range, label: label, font: font, metrics: metrics,
                reading: LyricReadingInk(tokens: Array(words[range]), readings: key.readings, font: readingFont),
                source: source, seed: seed, order: order,
                sustain: range.count == 1 ? tokens[range.lowerBound].sustain : 0,
                rightToLeft: LyricScript.isRightToLeft(label), glyphMotion: LyricScript.allowsGlyphMotion(label),
                boundaries: boundaries))
        }
        segment(&runs, words: words, ranges: ranges, glue: visible.glue,
                lineEnd: LyricHighlightTiming.lineEnd(key.lineEnd, words: words))
        return Layout(words: tokens, runs: runs, rightToLeft: lineRightToLeft)
    }

    /// Each run's sung words. A run of a script written without spaces is
    /// parted where a reader would start a word (`LyricRuns.splittingWords`),
    /// so a Japanese line set as one run is highlighted word by word, not
    /// all at once. Syllables of one Latin word stay one word. Every
    /// segment releases at the same line end.
    private static func segment(_ runs: inout [Run], words: [LyricsFetcher.Word], ranges: [Range<Int>],
                                glue: [Bool], lineEnd: Double) {
        let parted = LyricRuns.splittingWords(words, glue: glue, into: .words)
        for (runIndex, range) in ranges.enumerated() {
            let bounds = runs[runIndex].boundaries
            var start = range.lowerBound
            for index in range.lowerBound...range.upperBound {
                let begins = index == range.upperBound || (index > range.lowerBound && !(index < parted.count && parted[index]))
                guard begins, index > start else { continue }
                let from = bounds.indices.contains(start - range.lowerBound) ? bounds[start - range.lowerBound] : 0
                let to = bounds.indices.contains(index - range.lowerBound) ? bounds[index - range.lowerBound] : 1
                runs[runIndex].segments.append(Segment(from: from, to: to, onset: words[start].start,
                    end: words[start..<index].map(\.end).max() ?? words[start].end, release: lineEnd))
                start = index
            }
        }
    }
    private static func drag(_ word: LyricsFetcher.Word, average: Double) -> Double {
        let cleaned = word.text.lowercased()
            .trimmingCharacters(in: CharacterSet.punctuationCharacters.union(.whitespaces))
        guard cleaned.count > 2, !functionWords.contains(cleaned), average > 0.01 else { return 0 }
        return Easing.smoothstep(((word.end - word.start) / average - 1.15) / 0.85)
    }
    struct WordMetrics: Equatable {
        var size: CGFloat
        var isHero: Bool
        var rotation: Double
        var waveOffset: CGFloat
        var uppercased: Bool
        /// Set this word in the contrasting typeface (editorial only).
        var alternateFace: Bool
        /// The line's focal word: set largest and heaviest, so each line
        /// has one clear place for the eye to land.
        var isFocal = false
        /// Starts a new row of the flow (`FlowRowBreakKey`): Editorial and
        /// Constellation + Editorial give the focal word a row of its own.
        var breakBefore = false
    }

    /// The run the line is built around: the word the singer leans on
    /// longest, else the longest word that carries meaning. Function words
    /// ("the", "and") only lead when nothing else is there.
    static func focalRun(_ sources: [LyricsFetcher.Word], average: Double) -> Int? {
        var best: Int?
        var bestScore = -Double.infinity
        for (index, word) in sources.enumerated() {
            let cleaned = word.text.lowercased()
                .trimmingCharacters(in: CharacterSet.punctuationCharacters.union(.whitespaces))
            guard !cleaned.isEmpty else { continue }
            let length = Double(min(cleaned.count, 10))
            var score = drag(word, average: average) * 6 + length
            if functionWords.contains(cleaned) { score -= 20 }
            // Ties go to the later word: a line resolves forwards.
            if score >= bestScore { bestScore = score; best = index }
        }
        return best
    }

    /// Deliberate heights for the constellation's stars, in ems: up, down,
    /// small, large, so the links between neighbours always run on a slant
    /// and the figure never collapses into a ruled line.
    private static let starHeights: [CGFloat] = [-0.55, 0.40, -0.20, 0.62, -0.42, 0.18, 0.52, -0.30]

    /// Derived from the word itself so a lyric always sets the same way
    /// rather than reshuffling on every redraw.
    ///
    /// Each flow layout sets three tiers -- the focal word, ordinary words
    /// and small function words -- so a line has real hierarchy instead of
    /// every word at one size. The tiers stay within a ratio of about 1.8
    /// so the smallest word is still easily read.
    private static func metrics(index: Int, word: LyricsFetcher.Word, averageDuration: Double, fullness: Double,
                                focal: Int?, count: Int, key: Key) -> WordMetrics {
        let baseSize = key.baseSize, style = key.style, mood = key.mood, composition = key.composition
        var value: UInt64 = 1469598103934665603
        for byte in word.text.utf8 {
            value = (value ^ UInt64(byte)) &* 1099511628211
        }
        value = value ^ UInt64(index &* 2654435761)

        // How hard the singer leans on this word decides the weight; the hash
        // only supplies a little size jitter so the line is not mechanical.
        let held = key.wordTimed ? drag(word, average: averageDuration) : 0
        let isHero = held > 0.25
        let jitter = CGFloat(Int(value / 100) % 5) / 5.0
        let cleaned = word.text.lowercased()
            .trimmingCharacters(in: CharacterSet.punctuationCharacters.union(.whitespaces))
        let isFocal = key.wordTimed && index == focal
        let isMinor = !isFocal && functionWords.contains(cleaned)

        let editorialAmount = key.fontIdentity.editorial ? key.fontIdentity.editorialAmount
            : composition.hasEditorialTypography ? 1 : 0
        // Whether the hash lets this word take the second face at the
        // current editorial amount.
        let takesAlternate = Double((value / 700_000) % 100) < editorialAmount * 100

        // A line without word timing is read as one phrase: one size, level
        // and upright, as a single sentence would be set.
        guard key.wordTimed else {
            let alternate = editorialAmount > 0 && !isHero && (value / 10_000) % 3 == 0 && takesAlternate
            return WordMetrics(size: max(baseSize * 0.68, 11), isHero: false, rotation: 0, waveOffset: 0,
                               uppercased: false, alternateFace: alternate)
        }

        // Tier sizes, in ems of the base size.
        let tiers: (focal: CGFloat, normal: CGFloat, minor: CGFloat)
        switch composition {
        case .editorial: tiers = (1.08, 0.74, 0.60)
        case .constellation: tiers = (1.04, 0.70, 0.60)
        case .constellationEditorial: tiers = (1.06, 0.70, 0.60)
        case .echo: tiers = (0.98, 0.80, 0.66)
        default: tiers = (1.04, 0.72, 0.60)
        }
        var size: CGFloat
        if isFocal {
            size = tiers.focal
        } else if isMinor {
            size = tiers.minor
        } else {
            // A held word that is not the focus still sits a step up.
            size = tiers.normal + 0.03 * jitter + 0.14 * style.sizeContrast * CGFloat(held)
        }
        // Editorial's kicker -- the words before the focal one -- is set
        // small, so the spread reads kicker, headline, then text.
        if composition == .editorial, !isFocal, let focal, index < focal { size = tiers.minor }
        size = min(size, tiers.focal) * baseSize

        var alternate = false
        // A held word is set in capitals where the line's style asks for it.
        var uppercased = isHero && style.uppercaseHeroes
        var rotation = 0.0
        var offset: CGFloat = 0
        var breakBefore = false
        // Words before the focal word in Editorial: the small kicker.
        let kicker = focal.map { index < $0 } ?? false
        switch composition {
        case .editorial:
            // A spread: a small kicker, the focal word alone on its own
            // row in the second face, then the rest of the line under it.
            // Level and upright; the contrast is in the type, not the tilt.
            if let focal, focal > 0 { breakBefore = index == focal || index == focal + 1 }
            else if let focal { breakBefore = index == focal + 1 }
            alternate = editorialAmount > 0 && takesAlternate
                && (isFocal || (!kicker && (value / 10_000) % 3 == 0))
        case .constellationEditorial:
            // The focal word is the bright star on a row of its own; the
            // other words spread above and below it as satellites, every
            // other one in the second face.
            if let focal { breakBefore = (index == focal && focal > 0) || index == focal + 1 }
            alternate = editorialAmount > 0 && takesAlternate && (isFocal || index % 2 == 0)
            if !isFocal {
                let height = starHeights[(index + 3) % starHeights.count]
                offset = height * 0.45 * baseSize * CGFloat(key.spread)
            }
        case .constellation:
            // A field of stars at deliberate heights; the focal word holds
            // its row's line and the others climb and dip around it.
            if !isFocal {
                let reach = CGFloat(0.45 + 0.55 * fullness) * CGFloat(key.spread)
                let wobble = CGFloat(Int(value / 7) % 100) / 100.0 - 0.5
                offset = (starHeights[index % starHeights.count] + 0.18 * wobble) * baseSize * reach
            }
            alternate = editorialAmount > 0 && !isFocal && (value / 10_000) % 3 == 0 && takesAlternate
        case .echo:
            // One calm level line: the repeats behind it carry the idea.
            alternate = editorialAmount > 0 && !isFocal && (value / 10_000) % 3 == 0 && takesAlternate
        default:
            // Scattered: words set loose -- each tilted and lifted a little
            // off the baseline by its own hash. The focal word stays level,
            // the anchor the loose words gather round.
            if !isFocal {
                let lift = CGFloat(Int(value / 13) % 100) / 100.0 - 0.5
                offset = lift * 0.52 * baseSize * CGFloat(0.5 + 0.5 * mood.intensity)
                    + CGFloat(sin(Double(index) * 0.85)) * style.wave * baseSize
                rotation = Double(Int(value / 1000) % 9 - 4) * 0.7 * max(0.6, style.rotationSpread) * mood.intensity
            }
            uppercased = (isHero || isFocal) && style.uppercaseHeroes
            alternate = editorialAmount > 0 && !isFocal && (value / 10_000) % 3 == 0 && takesAlternate
        }

        return WordMetrics(
            size: max(size, 11),
            isHero: isHero,
            rotation: rotation,
            waveOffset: offset,
            uppercased: uppercased,
            alternateFace: alternate,
            isFocal: isFocal,
            breakBefore: breakBefore && count > 1
        )
    }
}


enum SpokenTypography {
    struct Key: Equatable {
        let words: [LyricsFetcher.Word]
        let baseSize: CGFloat
        let mood: TrackMood
        let composition: LyricComposition
        let fontIdentity: LyricFontIdentity
        var readings: [TimedWordKey: String] = [:]
        /// The onset after the last word, when that word is not in `words`
        /// yet. Its pace must not change when the next word joins.
        var following: Double?
        /// Each word's place in the whole song (see `Word.slot`).
        var ordinals: [TimedWordKey: Int] = [:]
        /// The tokens of each word that is a glued run (see `Word.parts`).
        var parts: [TimedWordKey: [LyricsFetcher.Word]] = [:]
    }
    struct Word: Identifiable {
        let index: Int
        let source: LyricsFetcher.Word
        let hash: UInt64
        /// Per timed word, so repeats of one word still move differently.
        let seed: UInt64
        let cadence: Double
        let rightToLeft: Bool
        let hero: Bool
        let pointSize: CGFloat
        let tile: Bool
        let font: Font
        let reading: LyricReadingInk?
        /// Where along the ink's colours the word sits, 0...1. Consecutive
        /// words take well-separated places. Keyed by the word's place in
        /// the song, never by its place in the rolling window: that shifts
        /// whenever a line leaves the window, and every word's colour cut
        /// to a new hue with it.
        var slot = 0.0
        /// The syllables or characters a glued run is made of, each with
        /// its own timing, and where each falls along the word (0...1 by
        /// character). Empty for a single token.
        var parts: [LyricsFetcher.Word] = []
        var boundaries: [Double] = []
        /// The largest entrance turn this word may make on a rolling stage,
        /// in degrees (see `RollingStack.turnLimit`). Worked out once here,
        /// not on every frame.
        var turnLimit: Double = 12
        var id: TimedWordKey { source.presentationID }

        static func slot(ordinal: Int) -> Double {
            (Double(ordinal) * 0.618_034).truncatingRemainder(dividingBy: 1)
        }
    }
    @MainActor
    static func prepare(_ key: Key, settings: Settings) -> [Word] {
        key.words.enumerated().map { index, word in
            let voice = word.text.lowercased().filter(\.isLetter)
            let filler = ["a", "an", "the", "i", "in", "of", "to", "and", "or", "it", "is", "my", "me", "you"].contains(voice)
            let hash = word.text.utf8.reduce(UInt64(1469598103934665603)) { ($0 ^ UInt64($1)) &* 1099511628211 }
            let hero = !filler && (word.text.count >= 5 || word.end - word.start > 0.65)
            let columnSize = key.baseSize * (hero ? 0.88 : filler ? 0.62 : 0.74)
            // A word with its reading under it is set a little smaller, so
            // the pair reads as one unit rather than a word and a half.
            let compact: CGFloat = key.readings[word.presentationID] == nil ? 1 : 0.88
            let pointSize = (key.composition == .spoken ? key.baseSize * 1.10 : columnSize) * compact
            let heavy = hero || key.composition == .spoken
            let alternate = key.fontIdentity.editorial && hash % 3 == 1
                && Double((hash / 300) % 100) < key.fontIdentity.editorialAmount * 100
            let seed = word.presentationID.visualSeed
            let isLast = index == key.words.count - 1
            let tile = key.composition == .cutout && (hero || hash % 4 == 1)
            let reading = key.readings[word.presentationID]
            return Word(index: index, source: word, hash: hash, seed: seed,
                cadence: isLast && (key.following ?? -.infinity) > word.start
                    ? (key.following ?? 0) - word.start : OrganicMotion.cadence(of: key.words, at: index),
                rightToLeft: LyricScript.isRightToLeft(word.text), hero: hero, pointSize: pointSize,
                tile: tile,
                font: settings.lyricFont(size: pointSize, hero: heavy, mood: key.mood, seed: seed, alternate: alternate),
                reading: reading.map { text in
                    let font = settings.lyricFont(size: pointSize * 0.56, hero: false, mood: key.mood,
                                                  seed: seed, alternate: alternate)
                    // A glued run's reading, kept by its tokens' shares so it
                    // fills in as they are sung; the same text either way.
                    if let tokens = key.parts[word.presentationID],
                       let shared = LyricReadingInk(tokens: tokens, readings: key.readings, font: font),
                       shared.text == text {
                        return shared
                    }
                    return LyricReadingInk(text: text, font: font)
                },
                slot: Word.slot(ordinal: key.ordinals[word.presentationID] ?? index),
                parts: key.parts[word.presentationID] ?? [],
                boundaries: key.parts[word.presentationID].map(LyricRuns.boundaries) ?? [],
                turnLimit: RollingStack.turnLimit(text: word.text, reading: reading, pointSize: pointSize, tile: tile))
        }
    }
}


struct LyricReadingInk: View, Equatable {
    let text: String
    let font: Font
    /// Readings sit back from the lyric they annotate.
    var colour: Color = .white.opacity(0.7)
    /// The widest the reading may run before it wraps, centred. Nil sets it
    /// on one line. A rolling stage hangs a reading of the whole line under
    /// the line's first word, where on one line it could run off the stage.
    var maxWidth: CGFloat?
    /// A glued word's reading, token by token: each token's share of it
    /// (`LyricReadingPairs.shares`) and when that token is sung. Written
    /// together they are `text`. Empty for a reading of one token.
    var shares: [Share] = []
    /// Per share, how far it shows on this frame (`revealing`). Nil shows
    /// the whole reading.
    var reveal: [Double]?

    struct Share: Equatable {
        var text: String
        var start: Double
    }

    init(text: String, font: Font, colour: Color = .white.opacity(0.7), maxWidth: CGFloat? = nil) {
        self.text = text; self.font = font; self.colour = colour; self.maxWidth = maxWidth
    }

    /// The reading of a run of tokens, from each token's share (missing
    /// shares are empty). Nil when no token has one. The first share's
    /// leading space is dropped, so the text is exactly the shares joined.
    init?(tokens: [LyricsFetcher.Word], readings: [TimedWordKey: String], font: Font) {
        var shares: [Share] = []
        for token in tokens {
            var share = readings[token.presentationID] ?? ""
            if shares.allSatisfy({ $0.text.isEmpty }) { share = String(share.drop(while: \.isWhitespace)) }
            shares.append(Share(text: share, start: token.start))
        }
        let text = shares.map(\.text).joined()
        guard !text.isEmpty else { return nil }
        self.init(text: text, font: font)
        if shares.count > 1 { self.shares = shares }
    }

    /// The same reading in the colour of the word it belongs to.
    func tinted(_ colour: Color) -> LyricReadingInk {
        var copy = self
        copy.colour = colour
        return copy
    }

    /// The same reading, wrapping at `width`.
    func wrapping(at width: CGFloat) -> LyricReadingInk {
        var copy = self
        copy.maxWidth = width
        return copy
    }

    /// The same reading with each share showing as far as `opacities` say
    /// (one per share; see `shareOpacities`). A share not shown yet is
    /// clear but keeps its room, so the reading never shifts or resizes as
    /// it fills in.
    func revealing(_ opacities: [Double]?) -> LyricReadingInk {
        var copy = self
        copy.reveal = opacities.flatMap { $0.count == shares.count && $0.contains { $0 < 1 } ? $0 : nil }
        return copy
    }

    /// How far each share shows at `time`: from its token's onset it fades
    /// in over `StageMotion.syllableFade` (as a stage word's syllables do),
    /// and never ahead of its own syllable, whose opacity (relative to the
    /// word's) is `syllables` where the layout reveals a word syllable by
    /// syllable. The first share comes with the word. With Reduce Motion a
    /// share appears at its onset. Nil for a reading of one token.
    func shareOpacities(at time: Double, syllables: [Double]? = nil, reduceMotion: Bool) -> [Double]? {
        guard shares.count > 1 else { return nil }
        return shares.indices.map { index in
            guard index > 0 else { return 1 }
            let age = time - (shares[index].start - 0.03)
            if reduceMotion { return age >= 0 ? 1 : 0 }
            let own = vc_settle(age, StageMotion.syllableFade)
            guard let syllables, syllables.count == shares.count else { return own }
            return min(own, max(0, syllables[index]))
        }
    }

    /// The reading as drawn: one string, each share in `colour` as far as
    /// it shows.
    static func revealed(_ shares: [Share], _ reveal: [Double], colour: Color) -> AttributedString {
        var result = AttributedString()
        for (share, shown) in zip(shares, reveal) where !share.text.isEmpty {
            var piece = AttributedString(share.text)
            piece.foregroundColor = shown <= 0.001 ? .clear : colour.opacity(min(1, shown))
            result.append(piece)
        }
        return result
    }

    private var label: Text {
        guard let reveal, reveal.count == shares.count else { return Text(text) }
        return Text(Self.revealed(shares, reveal, colour: colour))
    }

    var body: some View {
        if let maxWidth {
            CappedWidthLayout(maxWidth: maxWidth) {
                label
                    .font(font)
                    .foregroundStyle(colour)
                    .multilineTextAlignment(.center)
            }
        } else {
            label
                .font(font)
                .foregroundStyle(colour)
                .fixedSize()
        }
    }
}

/// Sets its one child at its own width up to `maxWidth`, and wraps it at
/// `maxWidth` beyond that.
struct CappedWidthLayout: Layout {
    let maxWidth: CGFloat

    private func proposal(for child: LayoutSubview) -> ProposedViewSize {
        let ideal = child.sizeThatFits(.unspecified)
        return ideal.width <= maxWidth ? .unspecified : ProposedViewSize(width: max(1, maxWidth), height: nil)
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let child = subviews.first else { return .zero }
        return child.sizeThatFits(self.proposal(for: child))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard let child = subviews.first else { return }
        child.place(at: CGPoint(x: bounds.midX, y: bounds.midY), anchor: .center, proposal: self.proposal(for: child))
    }
}
