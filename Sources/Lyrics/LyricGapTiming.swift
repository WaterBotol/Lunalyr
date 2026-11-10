import Foundation

enum LyricGapTiming {
    /// Vocal windows for the instrumental-break cue. With `inferringPauses`, a
    /// line-only lyric whose end is merely where the next LRC line starts
    /// (`Line.endFromNextLine`) counts as sung only for as long as its text
    /// could plausibly take, so a long break no longer looks like one long
    /// sung line. Supplied word ends and supplied line ends (TTML, yrc) are
    /// never shortened. It is off unless the caller asks: the lyrics model
    /// asks for every source's line-only lyrics except QQ's, which
    /// `QQGapRepair` splits instead, and one setting turns both off.
    static func windows(for lines: [LyricsFetcher.Line], inferringPauses: Bool = false) -> [VCVocalWindow] {
        let pace = inferringPauses ? secondsPerSyllable(in: lines) : minimumSecondsPerSyllable
        return lines.indices.compactMap { index -> VCVocalWindow? in
            let line = lines[index]
            guard !line.isInstrumental, !line.words.isEmpty else { return nil }
            let start = line.wordTimed ? line.words.map(\.start).min() ?? line.start : line.start
            // Estimated words do not supply an authoritative vocal end.
            var end = line.wordTimed ? line.words.map(\.end).max() ?? line.end : line.end
            let endsAtNextLine = index + 1 == lines.count || abs(lines[index + 1].start - line.end) < 0.05
            if inferringPauses, !line.wordTimed, line.endFromNextLine == true, endsAtNextLine,
               let sung = estimatedVocalEnd(of: line, secondsPerSyllable: pace) {
                end = min(end, sung)
            }
            guard start.isFinite, end.isFinite, start >= 0, end > start else { return nil }
            return VCVocalWindow(start: start, end: end)
        }.sorted { $0.start < $1.start }
    }

    /// Estimated word timing for the lyrics without any (`wordTimed` false):
    /// each line's words fill its window (`LyricsFetcher.distributeWords`),
    /// except where that window is far longer than singing the line would
    /// take at the song's own pace (`estimatedVocalEnd`): the last line,
    /// which runs to the end of the track, or a line before a long break.
    /// Those words are spread over the estimate instead, so they do not light
    /// one by one across an outro. An ordinary line is never shortened: a
    /// cap for every line raced slow lines and held notes to their end long
    /// before the singing. Without `inferringPauses` (the setting is off,
    /// for songs with long held notes) no line is shortened. Line starts and
    /// ends, supplied word timing and supplied line ends are never changed.
    /// Every such line is spread again here, so words saved by an older
    /// estimate are corrected.
    static func estimatingWords(in lines: [LyricsFetcher.Line], inferringPauses: Bool) -> [LyricsFetcher.Line] {
        let pace = inferringPauses ? secondsPerSyllable(in: lines) : minimumSecondsPerSyllable
        return lines.indices.map { index in
            var line = lines[index]
            guard !line.wordTimed, !line.isInstrumental, !line.words.isEmpty,
                  line.start.isFinite, line.end.isFinite, line.end > line.start else { return line }
            var end = line.end
            let endsAtNextLine = index + 1 == lines.count || abs(lines[index + 1].start - line.end) < 0.05
            if inferringPauses, line.endFromNextLine == true, endsAtNextLine,
               let sung = estimatedVocalEnd(of: line, secondsPerSyllable: pace) {
                end = sung
            }
            let words = LyricsFetcher.distributeWords(in: line.text, from: line.start, to: end)
            if !words.isEmpty { line.words = words }
            return line
        }
    }

    /// The longest run of consecutive sung lines that start faster than
    /// anyone could sing them (each line's window shorter than
    /// `bunchedSecondsPerSyllable` a syllable, and under 0.6 s), or nil when
    /// no run reaches `bunchedRunLength` lines. A listener-made LRC sometimes
    /// stamps a whole chorus within a few seconds; shown as it is, the lyrics
    /// race through those lines and land at the end of them.
    static func bunchedRun(in lines: [LyricsFetcher.Line]) -> (count: Int, start: Double, end: Double)? {
        // Credit lines ("Lyrics by: …") are often stamped a moment apart before the song starts.
        let sung = lines.filter { !$0.isInstrumental && !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && $0.start.isFinite && !LyricCredits.isCredit($0.text) }
        var best: (count: Int, start: Double, end: Double)?
        var runStart: Int?
        for index in sung.indices {
            let next = index + 1 < sung.count ? sung[index + 1].start : nil
            let window = next.map { $0 - sung[index].start }
            let tooQuick = window.map { $0 >= 0 && $0 < min(0.6, Double(max(1, estimatedSyllables(sung[index].text))) * bunchedSecondsPerSyllable) } ?? false
            if tooQuick {
                if runStart == nil { runStart = index }
            } else if let first = runStart {
                // The run's last line is the one the quick lines lead into.
                let count = index - first + 1
                if count >= bunchedRunLength, count > (best?.count ?? 0) {
                    best = (count, sung[first].start, sung[index].start)
                }
                runStart = nil
            }
        }
        return best
    }
    /// Word-timed lines whose words are crammed into far less time than
    /// singing them takes (under `crushedSecondsPerSyllable` a syllable, four
    /// syllables or more): a listener-made upload sometimes squeezes a
    /// line, often one with backing vocals, into a split second, and the
    /// highlight shoots to its end. The start of each, in order.
    static func crushedLines(in lines: [LyricsFetcher.Line]) -> [Double] {
        lines.compactMap { line in
            guard line.wordTimed, !line.isInstrumental, line.words.count >= 3,
                  let first = line.words.map(\.start).min(), let last = line.words.map(\.end).max(),
                  first.isFinite, last.isFinite, !LyricCredits.isCredit(line.text) else { return nil }
            let syllables = estimatedSyllables(line.text)
            return syllables >= 4 && last - first < Double(syllables) * crushedSecondsPerSyllable ? line.start : nil
        }
    }
    static let crushedSecondsPerSyllable = 0.06

    static let bunchedSecondsPerSyllable = 0.08
    static let bunchedRunLength = 5

    /// The quickest singing the estimate assumes.
    static let minimumSecondsPerSyllable = 0.4

    /// Where a line-only lyric has most likely finished being sung, or nil
    /// when its window is not clearly longer than singing it would take. The
    /// estimate is deliberately generous (the song's own pace, at least 0.4 s
    /// a syllable, plus 3 s for a held final note, 5 to 20 s in all), and the
    /// window must be more than twice that, and over 12 s, before a break is
    /// inferred. 12 s is also the shortest window QQGapRepair splits.
    static func estimatedVocalEnd(of line: LyricsFetcher.Line, secondsPerSyllable pace: Double = minimumSecondsPerSyllable) -> Double? {
        guard !line.isInstrumental, line.start.isFinite, line.end.isFinite else { return nil }
        let perSyllable = pace.isFinite ? min(1, max(minimumSecondsPerSyllable, pace)) : minimumSecondsPerSyllable
        let sung = min(20, max(5, Double(estimatedSyllables(line.text)) * perSyllable + 3))
        return line.end - line.start > max(12, sung * 2) ? line.start + sung : nil
    }

    /// The song's own pace: the median time per syllable over its compact
    /// LRC windows (1 to 8 s, three syllables or more, last line excluded),
    /// between 0.4 and 1 s. A window also holds the breath before the next
    /// line, so this errs towards a longer estimate, as slow songs need.
    static func secondsPerSyllable(in lines: [LyricsFetcher.Line]) -> Double {
        let samples = lines.indices.dropLast().compactMap { index -> Double? in
            let line = lines[index], window = line.end - line.start
            guard line.endFromNextLine == true, !line.isInstrumental, !line.wordTimed,
                  window >= 1, window <= 8 else { return nil }
            let syllables = estimatedSyllables(line.text)
            return syllables >= 3 ? window / Double(syllables) : nil
        }.sorted()
        guard !samples.isEmpty else { return minimumSecondsPerSyllable }
        return min(1, max(minimumSecondsPerSyllable, samples[samples.count / 2]))
    }

    /// A rough syllable count: one per Han, kana or Hangul character, one per
    /// vowel group in Latin words and one per two letters in other scripts.
    static func estimatedSyllables(_ text: String) -> Int {
        let folded = text.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: Locale(identifier: "en_US_POSIX"))
        var count = 0
        for word in folded.split(whereSeparator: { !$0.isLetter && !$0.isNumber }) {
            var vowels = 0, others = 0, inVowel = false
            for character in word {
                if LyricScript.keepsSyllables(String(character)) {
                    count += 1; inVowel = false
                } else if character.isASCII {
                    let vowel = "aeiouy".contains(character)
                    if vowel && !inVowel { vowels += 1 }
                    inVowel = vowel
                } else {
                    others += 1; inVowel = false
                }
            }
            count += max(vowels, word.contains(where: \.isASCII) ? 1 : 0) + (others + 1) / 2
        }
        return count
    }
}
