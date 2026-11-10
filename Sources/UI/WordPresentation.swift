import Foundation

struct TimedWordKey: Hashable {
    let text: String
    let start: Double
    let end: Double
    var visualSeed: UInt64 {
        text.utf8.reduce(start.bitPattern ^ 1469598103934665603) { ($0 ^ UInt64($1)) &* 1099511628211 }
    }
    init(_ word: LyricsFetcher.Word) { text = word.text; start = word.start; end = word.end }
}
extension LyricsFetcher.Word {
    var presentationID: TimedWordKey { TimedWordKey(self) }
}

/// Where "Below lyrics" draws a line's readings: under each word they read
/// (`match`), or, for a reading that cannot be split by word, once under the
/// whole line (`line`).
enum LyricReadingPairs {
    static func isDistinct(_ reading: String, from lyric: String) -> Bool {
        func key(_ value: String) -> String {
            value.folding(options: [.caseInsensitive, .widthInsensitive], locale: .current)
                .filter { $0.isLetter || $0.isNumber }
        }
        let readingKey = key(reading)
        return !readingKey.isEmpty && readingKey != key(lyric)
    }

    static func match(_ readings: [LyricsFetcher.Word]?, to words: [LyricsFetcher.Word]) -> [TimedWordKey: String] {
        guard let readings, readings.count == words.count else { return [:] }
        var result: [TimedWordKey: String] = [:]
        for (word, reading) in zip(words, readings) where isDistinct(reading.text, from: word.text) {
            result[word.presentationID] = reading.text
        }
        return result
    }

    /// The same for a line set in runs (`glue`, see `LyricRuns`): a glued
    /// run is one word on screen, so its reading is written once, on the
    /// run's first token, spaced by phrase (`LyricRuns.reading`). Joining
    /// the tokens' readings as they came ran a run's phrases together
    /// ("kyoumo" under 今日も, "bàntiānshàng" under 半天上).
    static func match(_ readings: [LyricsFetcher.Word]?, to words: [LyricsFetcher.Word],
                      glue: [Bool]) -> [TimedWordKey: String] {
        guard glue.contains(true) else { return match(readings, to: words) }
        guard let readings, readings.count == words.count else { return [:] }
        var timed: [TimedWordKey: LyricsFetcher.Word] = [:]
        for (word, reading) in zip(words, readings) where timed[word.presentationID] == nil {
            timed[word.presentationID] = reading
        }
        let visible = LyricRuns.visible(words, glue: glue)
        var result: [TimedWordKey: String] = [:]
        for range in LyricRuns.ranges(visible.glue, count: visible.words.count) {
            let tokens = visible.words[range]
            guard let first = tokens.first else { continue }
            if range.count == 1 {
                if let reading = timed[first.presentationID], isDistinct(reading.text, from: first.text) {
                    result[first.presentationID] = reading.text
                }
            } else if let reading = LyricRuns.reading(of: tokens, readings: timed) {
                result[first.presentationID] = reading
            }
        }
        return result
    }

    /// A saved or source reading that could not be split by word, which
    /// `match` places under no word: it is drawn once under the whole line.
    /// Nil when the readings are drawn word by word, and when not "Below
    /// lyrics" ("Replace lyrics" already sets such a reading in the line's
    /// place).
    static func line(_ readings: [LyricsFetcher.Word]?, for line: LyricsFetcher.Line,
                     display: RomanizationOptions.Display) -> String? {
        guard display == .below, let readings, readings.count != line.words.count else { return nil }
        guard let reading = LyricRomanizer.lineReading(readings, for: line),
              isDistinct(reading, from: line.text) else { return nil }
        return reading
    }

    /// For a rolling stage, which sets words one at a time with no line of
    /// their own: the readings by word (by run, given the line's `glue`),
    /// and a reading of the whole line under the line's first word,
    /// arriving with it.
    static func rolling(_ readings: [LyricsFetcher.Word]?, for line: LyricsFetcher.Line,
                        glue: [Bool] = []) -> [TimedWordKey: String] {
        guard let whole = self.line(readings, for: line, display: .below) else {
            return match(readings, to: line.words, glue: glue)
        }
        guard let first = line.words.first(where: { LyricRuns.isVisible($0.text) }) else { return [:] }
        return [first.presentationID: whole]
    }
}

/// A line's entrance and exit. Line-only lyrics animate as one phrase from
/// these; word-timed lines take their entrance from here and hand the exit
/// to their words, which leave one by one from `exitStart`.
struct LyricPhraseTiming {
    /// Monotone 0...1 entrance, for opacity.
    let progress: Double
    /// The same entrance as a spring, for movement: it can overshoot a
    /// touch and settle instead of stopping dead.
    let arrival: Double
    /// Monotone 0...1 exit of the phrase as a whole.
    let exit: Double
    /// When the line starts to leave.
    let exitStart: Double
    var presence: Double { progress * (1 - exit) }

    static func sweep(of line: LyricsFetcher.Line, at time: Double) -> Double {
        min(1, max(0, (time - line.start) / max(0.001, line.end - line.start)))
    }

    static let exitDuration = 0.46
    /// From `exitStart` until the last, most staggered word has gone.
    static let exitSpan = 0.70

    init(line: LyricsFetcher.Line, nextStart: Double, time: Double) {
        let duration = max(0, min(line.end, nextStart) - line.start)
        let attack = line.wordTimed ? 0.22 : min(0.32, max(0.12, duration * 0.18))
        progress = vc_settle(time - line.start, attack)
        let seed = Self.timeSeed(line.start) ^ 0x51ED_C0DE
        arrival = vc_spring_step(time - line.start,
            VCSpring(omega: 3.36 / max(0.14, attack * 1.6), zeta: 0.82 + 0.06 * vc_jitter(seed, 1)))
        exitStart = Self.exitStart(of: line, nextStart: nextStart)
        exit = vc_settle(time - exitStart, Self.exitDuration)
    }

    /// A seed from a time, by the millisecond. Never traps: a time too large
    /// for 64 bits (a malformed source's) or not a number gives 0 instead.
    static func timeSeed(_ seconds: Double) -> UInt64 {
        UInt64(bitPattern: Int64(exactly: (seconds * 1000).rounded()) ?? 0)
    }

    static func exitStart(of line: LyricsFetcher.Line, nextStart: Double) -> Double {
        min(line.end + 0.12, nextStart - 0.08)
    }

    /// The longest a line still being sung keeps the stage once the next
    /// line has started.
    static let longestHold = 4.0

    /// When line `row` hands over to the next: as the next line starts --
    /// unless `row` is a duet part or an ad-lib whose words are still being
    /// sung then (TTML, Lyrics+ and YRC keep overlapping lines), which
    /// stays until its own last word ends, at most `longestHold` later.
    /// Exiting at the next line's start cut such a line off mid-word and
    /// never showed its later words. Line-only lyrics, which never overlap,
    /// hand over at the next line's start as before.
    static func handOver(after row: Int, in lines: [LyricsFetcher.Line]) -> Double {
        guard lines.indices.contains(row) else { return .infinity }
        guard row + 1 < lines.count else { return lines[row].end + 0.8 }
        let next = lines[row + 1].start
        guard let end = LyricLineIndex.heldEnd(after: row, in: lines) else { return next }
        return min(end, next + longestHold)
    }

    /// Whether line `row` is still on the stage, held, after the next line
    /// has started.
    static func isHeld(_ row: Int, in lines: [LyricsFetcher.Line]) -> Bool {
        lines.indices.contains(row + 1) && handOver(after: row, in: lines) > lines[row + 1].start
    }

    /// Whether an earlier line still has words on their way out.
    static func stillLeaving(_ line: LyricsFetcher.Line, nextStart: Double, time: Double) -> Bool {
        time < exitStart(of: line, nextStart: nextStart) + exitSpan
    }

    static func firstWordOnset(of line: LyricsFetcher.Line) -> Double? {
        guard !line.isInstrumental else { return nil }
        guard let first = line.words.first(where: { $0.text.contains { $0.isLetter || $0.isNumber } }) else { return nil }
        return line.wordTimed ? first.start : line.start
    }

    /// Keep rolling words inside their contiguous run of real word timing.
    /// Looks back far enough that words still fading out (up to about four
    /// ranks) are never cut when the active line changes; words that ended
    /// long ago are dropped by the window itself.
    static func rollingRange(around index: Int, lines: [LyricsFetcher.Line]) -> ClosedRange<Int> {
        var first = index, last = index
        while first > max(0, index - 4), lines[first - 1].wordTimed { first -= 1 }
        if last + 1 < lines.count, lines[last + 1].wordTimed { last += 1 }
        return first...last
    }
}

struct WordPresentation: Equatable {
    /// Monotone 0...1 reveal: opacity, blur, masks.
    var progress: Double
    /// The word's spring, for travel and scale. Can pass 1 and settle back.
    var arrival: Double
    var opacity: Double
    var preview: Bool
    /// `eligibleSince` is when this word could first be previewed: the
    /// onset of the word before it, or when its line's preview was allowed.
    /// The preview ramps in from then, so it never appears part-way up.
    @MainActor
    static func make(word: LyricsFetcher.Word, index: Int, nextIndex: Int?, time: Double,
                     composition: LyricComposition, motion: VCWordMotion? = nil,
                     eligibleSince: Double = -.infinity) -> WordPresentation {
        let settings = Settings.shared
        guard settings.revealsWords(in: composition) else {
            return .init(progress: 1, arrival: 1, opacity: 1, preview: false)
        }
        let motion = motion ?? vc_word_motion(time, word.start, word.end, word.end - word.start,
            .infinity, 0, word.presentationID.visualSeed, 0, 0)
        let ahead = settings.previewsNextWord(in: composition)
        // The faint preview ramps up to a quarter and hands over, without a
        // blink, to the word's own reveal at its onset.
        let opacity: Double
        if time < word.start {
            let hint = vc_word_visibility(time, word.start, word.end, ahead ? 1 : 0,
                index == nextIndex ? 1 : 0, settings.nextWordPreviewLead)
                * Easing.smoothstep((time - eligibleSince) / 0.16)
            opacity = hint > 0 && LyricRuns.isPreviewWord(word.text) ? hint : 0
        } else {
            let carriesPreview = ahead && (motion.reveal == 1 || LyricRuns.isPreviewWord(word.text))
            opacity = carriesPreview ? 0.25 + 0.75 * motion.reveal : motion.reveal
        }
        return .init(progress: motion.reveal, arrival: motion.enter, opacity: opacity,
                     preview: time < word.start && opacity > 0)
    }
}
