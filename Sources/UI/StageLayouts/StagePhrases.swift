import Foundation

// MARK: - Phrases and word roles for the stage layouts
//
// Kinetic type choreographs phrases, not words. This turns a line's runs
// (`StageRun`: glued syllables and CJK characters already joined, each run
// kept whole) into phrases -- the unit a layout composes and moves -- and
// gives every run a role within its phrase:
//
// - focal: the phrase's longest-held content word; it carries the layout's
//   signature move. Exactly one per phrase.
// - function: a little English word ("the", "to", "and", "you"...); it rides
//   with a neighbour and never makes an entrance of its own.
// - held: sung for at least `Rules.held` seconds; it may stretch or hold a pose.
// - run: one of several words sung in quick succession; they arrive as one group.
//
// Phrases break at a timing gap, after punctuation, and before they grow
// longer than `Rules.maxRuns`; an overlong stretch is cut into near-equal
// phrases at the best nearby place (not after a function word, not inside a
// quick run, at the biggest gap). A line-timed line (every word at one time)
// is phrased by punctuation and length alone.
//
// Pure values, read-only: nothing here draws or moves. `phrasing(_:)` caches
// the analysis per line, so a layout may ask for it on every frame.

/// What a run does in its phrase. A run can be several things at once
/// (`StageWordInfo` keeps each flag); this is the one that leads, in the order
/// focal, run, held, function, content.
enum StageWordRole: String, Equatable {
    case focal, run, held, function, content
}

/// Why a phrase starts where it does.
enum StagePhraseBreak: String, Equatable {
    /// The line's first phrase.
    case lineStart
    /// A pause of at least `Rules.gap` before it.
    case gap
    /// The phrase before ended with punctuation.
    case punctuation
    /// The stretch was too long for one phrase and was cut here.
    case length
}

/// One run's place in the line's phrasing.
struct StageWordInfo: Equatable {
    /// The run (`StageRun.id`, its index in the line).
    var run: Int
    /// The phrase it belongs to (index into `StagePhrasing.phrases`).
    var phrase: Int
    var role: StageWordRole
    var isFocal: Bool
    var isFunction: Bool
    var isHeld: Bool
    /// The quick run it belongs to (an index shared by its members), or nil.
    var runGroup: Int?
    /// How long it is sung: to its end, never past the next word's onset;
    /// 0 for a line-timed line.
    var duration: Double
    /// The pause before it (0 for the first word, overlaps and line-timed lines).
    var gapBefore: Double
}

/// A phrase: a stretch of consecutive runs, in reading order.
struct StagePhrase: Equatable {
    var index: Int
    /// Its runs (`StageRun.id`s), consecutive and in reading order.
    var runs: Range<Int>
    /// The focal run (`StageRun.id`).
    var focal: Int
    /// The earliest onset and the latest end of its runs (so out-of-order
    /// word times still give a span that holds them all).
    var start: Double
    var end: Double
    var breakBefore: StagePhraseBreak

    var count: Int { runs.count }
}

/// A line's phrases and the role of each of its runs.
struct StagePhrasing: Equatable {
    var phrases: [StagePhrase]
    /// One per run, indexed by `StageRun.id`.
    var words: [StageWordInfo]
    /// No word timing: every run starts at once.
    var lineTimed: Bool
    var rightToLeft: Bool

    static let empty = StagePhrasing(phrases: [], words: [], lineTimed: false, rightToLeft: false)

    /// The phrase run `id` belongs to.
    func phrase(of run: Int) -> StagePhrase? {
        guard words.indices.contains(run) else { return nil }
        return phrases[words[run].phrase]
    }

    /// The phrase being sung at `time`: the last whose start (less `lead`)
    /// has passed, nil before the first.
    func phraseIndex(at time: Double, lead: Double = StageMotion.lead) -> Int? {
        phrases.lastIndex { time >= $0.start - lead }
    }
}

enum StagePhrases {
    /// The thresholds the analysis uses; the defaults follow REDESIGN.md.
    struct Rules: Hashable {
        /// A pause at least this long (s) between two words starts a phrase.
        var gap = 0.35
        /// The most runs in a phrase.
        var maxRuns = 4
        /// Sung at least this long (s), a word is held.
        var held = 0.8
        /// Onsets closer than this (s) chain into a quick run...
        var runSpacing = 0.25
        /// ...of at least this many words.
        var runLength = 3
    }

    /// Small English words that ride with a neighbour: articles, pronouns,
    /// short prepositions, conjunctions and auxiliaries.
    static let functionWords: Set<String> = [
        "a", "an", "the", "this", "that", "these", "those",
        "i", "me", "my", "you", "your", "we", "us", "our", "he", "him", "his", "she", "her", "it", "its",
        "they", "them", "their", "i'm", "i'll", "i've", "i'd", "you're", "you'll", "we're", "it's", "im", "ill",
        "to", "of", "in", "on", "at", "by", "for", "with", "from", "into", "onto", "up", "out", "off",
        "under", "over", "through", "till", "until", "as", "than",
        "and", "or", "but", "so", "if", "nor", "yet", "cause", "'cause",
        "is", "am", "are", "was", "were", "be", "been", "do", "does", "did", "have", "has", "had",
        "can", "will", "would", "could", "should", "just", "not", "no",
    ]

    /// Whether `text` is a function word, ignoring case and punctuation
    /// round it (and a curly apostrophe inside it).
    static func isFunctionWord(_ text: String) -> Bool {
        let plain = text.lowercased().replacingOccurrences(of: "\u{2019}", with: "'")
            .trimmingCharacters(in: CharacterSet.punctuationCharacters.subtracting(CharacterSet(charactersIn: "'")))
            .trimmingCharacters(in: .whitespaces)
        return functionWords.contains(plain)
    }

    /// Punctuation that ends a phrase when a run ends with it.
    static let phraseEnders: Set<Character> = [",", ".", ";", ":", "!", "?", "\u{2014}", "\u{2026}",
                                               "\u{3001}", "\u{3002}", "\u{FF0C}", "\u{FF01}", "\u{FF1F}", "\u{FF1B}", "\u{FF1A}",
                                               "\u{060C}", "\u{061F}", "\u{061B}"]

    static func endsPhrase(_ text: String) -> Bool {
        var trimmed = text.trimmingCharacters(in: .whitespaces)
        // A closing quote or bracket after the punctuation still ends it.
        while let last = trimmed.last, "\"'\u{201D}\u{2019})]\u{300D}\u{300F}".contains(last) { trimmed.removeLast() }
        return trimmed.last.map { phraseEnders.contains($0) } ?? false
    }

    // MARK: Cache

    private struct Key: Hashable {
        var texts: [String]
        var starts: [Double]
        var ends: [Double]
        var lineTimed: Bool?
        var rules: Rules
    }

    private final class Cache: @unchecked Sendable {
        let lock = NSLock()
        var entries: [Key: StagePhrasing] = [:]
    }

    private static let cache = Cache()
    private static let cacheLimit = 64

    /// The phrasing of a line's runs, cached by the line (its texts and
    /// times), so asking on every frame costs a lookup.
    static func phrasing(_ runs: [StageRun], lineTimed: Bool? = nil, rules: Rules = Rules()) -> StagePhrasing {
        let key = Key(texts: runs.map(\.text), starts: runs.map(\.start), ends: runs.map(\.end),
                      lineTimed: lineTimed, rules: rules)
        cache.lock.lock()
        if let hit = cache.entries[key] { cache.lock.unlock(); return hit }
        cache.lock.unlock()
        let result = analyse(runs, lineTimed: lineTimed, rules: rules)
        cache.lock.lock()
        if cache.entries.count >= cacheLimit { cache.entries.removeAll(keepingCapacity: true) }
        cache.entries[key] = result
        cache.lock.unlock()
        return result
    }

    /// The phrasing of a line given as words and glue, as `StageLyricView` gets it.
    static func phrasing(words: [LyricsFetcher.Word], glue: [Bool], lineTimed: Bool? = nil,
                         rules: Rules = Rules()) -> StagePhrasing {
        phrasing(StageRun.make(words, glue: glue), lineTimed: lineTimed, rules: rules)
    }

    // MARK: Analysis

    /// Whether `runs` carry no word timing: more than one run, all starting together.
    static func looksLineTimed(_ runs: [StageRun]) -> Bool {
        guard runs.count > 1, let first = runs.first?.start else { return false }
        return runs.allSatisfy { abs($0.start - first) < 0.01 }
    }

    /// The analysis itself, uncached. `lineTimed` nil decides from the times.
    static func analyse(_ runs: [StageRun], lineTimed: Bool? = nil, rules: Rules = Rules()) -> StagePhrasing {
        guard !runs.isEmpty else { return .empty }
        let count = runs.count
        let untimed = lineTimed ?? looksLineTimed(runs)
        let rightToLeft = LyricScript.lineIsRightToLeft(runs.map(\.text))
        let function = runs.map { isFunctionWord($0.text) }

        // How long each run is sung, and the pause before it.
        var durations = [Double](repeating: 0, count: count)
        var gaps = [Double](repeating: 0, count: count)
        if !untimed {
            let hasEnds = runs.contains { $0.end > $0.start + 0.05 }
            let spacings = zip(runs, runs.dropFirst()).map { $1.start - $0.start }.filter { $0 > 0.02 }.sorted()
            let typical = spacings.isEmpty ? 0 : spacings[spacings.count / 2]
            for index in runs.indices {
                let run = runs[index]
                let next = index + 1 < count ? runs[index + 1].start : nil
                var sung = max(0, run.end - run.start)
                if let next, next > run.start { sung = min(sung, next - run.start) }
                durations[index] = sung
                guard index > 0 else { continue }
                let previous = runs[index - 1]
                let onsetGap = run.start - previous.start
                if hasEnds {
                    // A word without an end of its own runs on to the next.
                    let end = previous.end > previous.start + 0.05 ? previous.end : run.start
                    gaps[index] = max(0, run.start - end)
                } else {
                    // No ends at all: a pause is an onset gap well past the line's pace.
                    gaps[index] = max(0, onsetGap - typical)
                }
            }
        }
        let held = durations.map { !untimed && $0 >= rules.held }

        // Quick runs: chains of onsets closer than `runSpacing`.
        var runGroups = [Int?](repeating: nil, count: count)
        if !untimed {
            var chainStart = 0
            var group = 0
            func close(_ end: Int) {
                if end - chainStart >= rules.runLength {
                    for index in chainStart..<end { runGroups[index] = group }
                    group += 1
                }
            }
            for index in 1..<max(count, 1) {
                let spacing = runs[index].start - runs[index - 1].start
                if !(spacing >= 0 && spacing < rules.runSpacing) {
                    close(index)
                    chainStart = index
                }
            }
            close(count)
        }

        // Hard breaks: a pause, or punctuation ending the run before.
        var breaks: [StagePhraseBreak?] = [StagePhraseBreak?](repeating: nil, count: count)
        breaks[0] = .lineStart
        for index in 1..<max(count, 1) {
            if endsPhrase(runs[index - 1].text) {
                breaks[index] = .punctuation
            } else if !untimed, gaps[index] >= rules.gap {
                breaks[index] = .gap
            }
        }
        // Each stretch between hard breaks, cut to length.
        var starts: [Int] = []
        var index = 0
        while index < count {
            var end = index + 1
            while end < count, breaks[end] == nil { end += 1 }
            starts.append(index)
            for cut in lengthCuts(index..<end, function: function, groups: runGroups, gaps: gaps, maxRuns: rules.maxRuns) {
                starts.append(cut)
                breaks[cut] = .length
            }
            index = end
        }

        var phrases: [StagePhrase] = []
        var words: [StageWordInfo] = []
        for (number, first) in starts.enumerated() {
            let last = number + 1 < starts.count ? starts[number + 1] : count
            let span = first..<last
            let focal = focalRun(span, runs: runs, durations: durations, function: function, untimed: untimed)
            let start = span.map { runs[$0].start }.min() ?? runs[first].start
            let end = span.map { max(runs[$0].end, runs[$0].start) }.max() ?? runs[first].end
            phrases.append(StagePhrase(index: number, runs: span, focal: focal, start: start, end: end,
                                       breakBefore: breaks[first] ?? .length))
            for id in span {
                let role: StageWordRole = id == focal ? .focal
                    : runGroups[id] != nil ? .run
                    : held[id] ? .held
                    : function[id] ? .function : .content
                words.append(StageWordInfo(run: id, phrase: number, role: role, isFocal: id == focal,
                                           isFunction: function[id], isHeld: held[id], runGroup: runGroups[id],
                                           duration: durations[id], gapBefore: gaps[id]))
            }
        }
        return StagePhrasing(phrases: phrases, words: words, lineTimed: untimed, rightToLeft: rightToLeft)
    }

    /// Where to cut a stretch longer than `maxRuns`: into the fewest near-equal
    /// phrases, each cut moved by one if that keeps a function word with the
    /// word after it, keeps a quick run together, or lands on a bigger pause.
    static func lengthCuts(_ span: Range<Int>, function: [Bool], groups: [Int?], gaps: [Double],
                           maxRuns: Int) -> [Int] {
        let limit = max(1, maxRuns)
        guard span.count > limit else { return [] }
        let pieces = (span.count + limit - 1) / limit
        var cuts: [Int] = []
        var previous = span.lowerBound
        for piece in 1..<pieces {
            let ideal = span.lowerBound + Int((Double(span.count) * Double(piece) / Double(pieces)).rounded())
            let remaining = pieces - piece
            var best = ideal
            var bestScore = -Double.infinity
            for candidate in [ideal, ideal - 1, ideal + 1] {
                // Both sides must still fit: this phrase, and what is left for the rest.
                guard candidate > previous, candidate - previous <= limit,
                      span.upperBound - candidate <= remaining * limit,
                      span.upperBound - candidate >= remaining else { continue }
                var score = -abs(Double(candidate - ideal)) * 0.6
                if function[candidate - 1] { score -= 1 }
                if let group = groups[candidate - 1], groups[candidate] == group { score -= 2 }
                score += min(gaps[candidate], 1) * 2
                if score > bestScore + 1e-9 { bestScore = score; best = candidate }
            }
            cuts.append(best)
            previous = best
        }
        return cuts
    }

    /// The phrase's focal run: its longest-held content word (a function word
    /// only when the phrase has nothing else). Ties within 0.05 s go to the
    /// longer word, then the later one, where English puts the stress.
    /// A line-timed phrase has no durations, so its longest word leads.
    static func focalRun(_ span: Range<Int>, runs: [StageRun], durations: [Double], function: [Bool],
                         untimed: Bool) -> Int {
        let content = span.filter { !function[$0] }
        let candidates = content.isEmpty ? Array(span) : content
        var best = candidates[0]
        for id in candidates.dropFirst() {
            let longer = untimed ? 0 : durations[id] - durations[best]
            if longer > 0.05 { best = id; continue }
            if longer < -0.05 { continue }
            let letters = runs[id].text.count - runs[best].text.count
            if letters >= 0 { best = id }
        }
        return best
    }
}

extension StageLyricView {
    /// The phrasing of this line (cached; see `StagePhrases`). No layout uses
    /// it yet: it is here for the redesigned layouts.
    func phrasing(_ runs: [StageRun]) -> StagePhrasing { StagePhrases.phrasing(runs) }
}
