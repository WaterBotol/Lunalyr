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
    }
    struct Word: Equatable {
        let index: Int
        let source: LyricsFetcher.Word
        let metrics: WordMetrics
        let label: String
        let characters: [String]
        let font: Font
        let sustain: Double
        let motionSeed: UInt64
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
        let words = key.words.filter { $0.text.contains { $0.isLetter || $0.isNumber } }
        let durations = words.map { $0.end - $0.start }.filter { $0 > 0.01 }
        let average = durations.isEmpty ? 0.3 : durations.reduce(0, +) / Double(durations.count)
        let fullness = min(Double(words.count) / 7, 1)
        return words.enumerated().map { index, word in
            let metrics = metrics(index: index, word: word, averageDuration: average, fullness: fullness, key: key)
            let label = metrics.uppercased ? word.text.uppercased() : word.text
            let font = metrics.alternateFace
                ? key.mood.face.contrasting.font(size: metrics.size, hero: metrics.isHero)
                : settings.lyricFont(size: metrics.size, hero: metrics.isHero, mood: key.mood)
            let motionSeed = word.text.utf8.reduce(UInt64(1469598103934665603)) { ($0 ^ UInt64($1)) &* 1099511628211 }
            return Word(index: index, source: word, metrics: metrics, label: label,
                        characters: label.map(String.init), font: font,
                        sustain: Easing.smoothstep(((word.end - word.start) - 0.45) / 0.85), motionSeed: motionSeed)
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
    }

    /// Derived from the word itself so a lyric always sets the same way
    /// rather than reshuffling on every redraw.
    private static func metrics(index: Int, word: LyricsFetcher.Word, averageDuration: Double, fullness: Double, key: Key) -> WordMetrics {
        let baseSize = key.baseSize, style = key.style, mood = key.mood, composition = key.composition
        var value: UInt64 = 1469598103934665603
        for byte in word.text.utf8 {
            value = (value ^ UInt64(byte)) &* 1099511628211
        }
        value = value ^ UInt64(index &* 2654435761)

        // How hard the singer leans on this word decides the weight; the hash
        // only supplies a little size jitter so the line is not mechanical.
        let held = drag(word, average: averageDuration)
        let isHero = held > 0.25
        let jitter = CGFloat(Int(value / 100) % 5) / 5.0

        // Idle sizes stay well below baseSize; the active word's scale takes
        // it clearly past all of them. A longer-held word sits larger still.
        var size = isHero
            ? baseSize * (0.58 + 0.30 * style.sizeContrast * CGFloat(held) + 0.05 * jitter)
            : baseSize * (0.64 + 0.06 * jitter)

        // Editorial widens the range hard: a spread gets its character from
        // real size contrast, not from a gentle wobble around one size.
        var alternate = false
        if composition.hasEditorialTypography || key.fontIdentity.editorial {
            size *= isHero ? 1.10 : (1.00 + 0.10 * jitter)
            // Roughly every third word takes the second face. Driven by the
            // word's own hash, so the pairing is fixed for a given lyric.
            alternate = !isHero && (composition == .constellationEditorial ? index % 2 == 0 : (value / 10_000) % 3 == 0)
        }

        var offset = sin(Double(index) * 0.85) * style.wave * baseSize * CGFloat(mood.intensity)

        if composition.hasConnections {
            // The web needs the words at genuinely different heights. On a
            // single baseline every link comes out horizontal and collinear,
            // and the whole figure reads as a rule struck through the line
            // rather than as a constellation.
            let spread = CGFloat(Int(value / 7) % 100) / 100.0 - 0.5
            let reach = CGFloat(0.30 + 0.70 * fullness) * CGFloat(key.spread)
            offset += (spread * baseSize * 2.1 + CGFloat(sin(Double(index) * 2.3)) * baseSize * 0.42) * reach
            // Satellites shrink well back so the held words carry the figure,
            // the way the small linked fragments do on the real display.
            size *= isHero ? 1.06 : 0.96
        }

        return WordMetrics(
            size: max(size, 11),
            isHero: isHero,
            rotation: Double(Int(value / 1000) % 9 - 4) * 0.35 * style.rotationSpread * mood.intensity,
            waveOffset: offset,
            uppercased: isHero && style.uppercaseHeroes,
            alternateFace: alternate
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
    }
    struct Word: Identifiable {
        let index: Int
        let source: LyricsFetcher.Word
        let hash: UInt64
        let hero: Bool
        let pointSize: CGFloat
        let tile: Bool
        let font: Font
        var id: TimedWordKey { source.presentationID }
    }
    @MainActor
    static func prepare(_ key: Key, settings: Settings) -> [Word] {
        key.words.enumerated().map { index, word in
            let voice = word.text.lowercased().filter(\.isLetter)
            let filler = ["a", "an", "the", "i", "in", "of", "to", "and", "or", "it", "is", "my", "me", "you"].contains(voice)
            let hash = word.text.utf8.reduce(UInt64(1469598103934665603)) { ($0 ^ UInt64($1)) &* 1099511628211 }
            let hero = !filler && (word.text.count >= 5 || word.end - word.start > 0.65)
            let columnSize = key.baseSize * (hero ? 0.88 : filler ? 0.62 : 0.74)
            let pointSize = key.composition == .spoken ? key.baseSize * 1.10 : columnSize
            return Word(index: index, source: word, hash: hash, hero: hero, pointSize: pointSize,
                tile: key.composition == .cutout && (hero || hash % 4 == 1),
                font: key.fontIdentity.editorial && hash % 3 == 1
                    ? key.mood.face.contrasting.font(size: pointSize, hero: hero)
                    : settings.lyricFont(size: pointSize, hero: hero || key.composition == .spoken, mood: key.mood))
        }
    }
}
