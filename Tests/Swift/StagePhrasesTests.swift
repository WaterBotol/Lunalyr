import XCTest
@testable import VersoCustom

/// The shared phrase analysis for the stage layouts (`StagePhrases`).
@MainActor
final class StagePhrasesTests: XCTestCase {
    typealias Word = LyricsFetcher.Word

    /// Evenly sung words, `step` apart, each sung for 90% of the step.
    static func even(_ text: String, step: Double = 0.62, from start: Double = 1) -> [Word] {
        text.split(separator: " ").enumerated().map { i, w in
            Word(text: String(w), start: start + Double(i) * step, end: start + Double(i) * step + step * 0.88)
        }
    }

    /// The long line of the review videos: a quick three-word run, a held
    /// word (1.25 s), a 0.6 s pause before "until".
    static var mixed: [Word] {
        let timing: [(String, Double, Double)] = [
            ("and", 1.00, 1.12), ("we", 1.15, 1.27), ("keep", 1.30, 1.50), ("running", 1.60, 2.85),
            ("through", 2.90, 3.20), ("the", 3.25, 3.45), ("city", 3.50, 3.95), ("lights", 4.00, 4.50),
            ("until", 5.10, 5.45), ("the", 5.50, 5.65), ("morning", 5.70, 6.30), ("comes", 6.35, 6.80),
        ]
        return timing.map { Word(text: $0.0, start: $0.1, end: $0.2) }
    }

    private func phrasing(_ words: [Word], glue: [Bool]? = nil, lineTimed: Bool? = nil) -> (StagePhrasing, [StageRun]) {
        let runs = StageRun.make(words, glue: glue ?? [Bool](repeating: false, count: words.count))
        return (StagePhrases.analyse(runs, lineTimed: lineTimed), runs)
    }

    private func texts(_ phrasing: StagePhrasing, _ runs: [StageRun]) -> [String] {
        phrasing.phrases.map { phrase in phrase.runs.map { runs[$0].text }.joined(separator: " ") }
    }

    /// Every run belongs to exactly one phrase, in reading order, and every
    /// phrase has one focal run of its own.
    private func assertWellFormed(_ phrasing: StagePhrasing, _ runs: [StageRun], file: StaticString = #filePath,
                                  line: UInt = #line) {
        XCTAssertEqual(phrasing.words.count, runs.count, file: file, line: line)
        XCTAssertEqual(phrasing.phrases.flatMap { Array($0.runs) }, Array(runs.indices), file: file, line: line)
        for (index, phrase) in phrasing.phrases.enumerated() {
            XCTAssertEqual(phrase.index, index, file: file, line: line)
            XCTAssertFalse(phrase.runs.isEmpty, file: file, line: line)
            XCTAssertLessThanOrEqual(phrase.count, StagePhrases.Rules().maxRuns, file: file, line: line)
            XCTAssertTrue(phrase.runs.contains(phrase.focal), file: file, line: line)
            XCTAssertEqual(phrase.runs.filter { phrasing.words[$0].isFocal }, [phrase.focal], file: file, line: line)
            XCTAssertLessThanOrEqual(phrase.start, phrase.end, file: file, line: line)
            for id in phrase.runs {
                XCTAssertEqual(phrasing.words[id].phrase, index, file: file, line: line)
                XCTAssertLessThanOrEqual(phrase.start, runs[id].start, file: file, line: line)
                XCTAssertGreaterThanOrEqual(phrase.end, runs[id].end, file: file, line: line)
            }
        }
    }

    func testShortEnglishLineIsTwoPhrasesOfThree() {
        let (p, runs) = phrasing(Self.even("we keep dreaming under neon rain"))
        assertWellFormed(p, runs)
        XCTAssertEqual(texts(p, runs), ["we keep dreaming", "under neon rain"])
        XCTAssertEqual(p.phrases.map(\.breakBefore), [.lineStart, .length])
        XCTAssertEqual(p.phrases.map { runs[$0.focal].text }, ["dreaming", "rain"], "Equal holds: the longer, then the later word")
        XCTAssertEqual(p.words.map(\.isFunction), [true, false, false, true, false, false])
        XCTAssertEqual(p.words[0].role, .function)
        XCTAssertEqual(p.words[1].role, .content)
        XCTAssertFalse(p.words.contains { $0.isHeld || $0.runGroup != nil })
        XCTAssertFalse(p.lineTimed)
        XCTAssertFalse(p.rightToLeft)
        XCTAssertEqual(p.phrases[0].start, 1, accuracy: 1e-9)
        XCTAssertEqual(p.phrases[1].start, runs[3].start, accuracy: 1e-9)
        XCTAssertEqual(p.phrases[1].end, runs[5].end, accuracy: 1e-9)
    }

    func testLongLineWithARunAHeldWordAndAPause() {
        let (p, runs) = phrasing(Self.mixed)
        assertWellFormed(p, runs)
        XCTAssertEqual(texts(p, runs), ["and we keep running", "through the city lights", "until the morning comes"])
        XCTAssertEqual(p.phrases.map(\.breakBefore), [.lineStart, .length, .gap])
        // The quick run arrives as one group.
        XCTAssertEqual(p.words.prefix(3).map(\.runGroup), [0, 0, 0])
        XCTAssertEqual(p.words.prefix(3).map(\.role), [.run, .run, .run])
        XCTAssertNil(p.words[3].runGroup)
        // The held word is the first phrase's focal word.
        XCTAssertTrue(p.words[3].isHeld)
        XCTAssertEqual(p.words[3].duration, 1.25, accuracy: 1e-9)
        XCTAssertEqual(p.phrases[0].focal, 3)
        XCTAssertEqual(p.words[3].role, .focal)
        XCTAssertEqual(runs[p.phrases[1].focal].text, "lights")
        XCTAssertEqual(runs[p.phrases[2].focal].text, "morning")
        XCTAssertEqual(p.words[8].gapBefore, 0.6, accuracy: 1e-9)
        XCTAssertEqual(p.words.filter(\.isHeld).map(\.run), [3])
        XCTAssertEqual(p.words[5].role, .function)
        // Phrase spans and lookup by time.
        XCTAssertEqual(p.phrases[2].start, 5.1, accuracy: 1e-9)
        XCTAssertEqual(p.phrases[2].end, 6.8, accuracy: 1e-9)
        XCTAssertNil(p.phraseIndex(at: 0.5))
        XCTAssertEqual(p.phraseIndex(at: 2.0), 0)
        XCTAssertEqual(p.phraseIndex(at: 3.0), 1)
        XCTAssertEqual(p.phraseIndex(at: 4.8), 1, "Still the second phrase during the pause")
        XCTAssertEqual(p.phraseIndex(at: 5.2), 2)
        XCTAssertEqual(p.phrase(of: 9)?.index, 2)
        XCTAssertNil(p.phrase(of: 12))
    }

    func testAQuickLineIsOneRunPerPhraseAndNeverSplitsARunWhenItCanHelpIt() {
        // Eight words 0.15 s apart, then a pause and two slow ones.
        var words = Self.even("hey hey we gotta go go go now", step: 0.15)
        words += Self.even("slow down", step: 0.8, from: 3)
        let (p, runs) = phrasing(words)
        assertWellFormed(p, runs)
        XCTAssertEqual(texts(p, runs), ["hey hey we gotta", "go go go now", "slow down"])
        XCTAssertEqual(p.words.prefix(8).map(\.role).filter { $0 == .run }.count, 6, "Each phrase's focal word leads it")
        XCTAssertEqual(Set(p.words.prefix(8).compactMap(\.runGroup)), [0])
        XCTAssertNil(p.words[8].runGroup)
        // A cut moves by one to keep a function word with the word after it.
        let cuts = StagePhrases.lengthCuts(0..<6, function: [false, false, true, false, false, false],
                                           groups: [nil, nil, nil, nil, nil, nil], gaps: [0, 0, 0, 0, 0, 0], maxRuns: 4)
        XCTAssertEqual(cuts, [2], "Not 'x x the | y'")
        // ... and to land on a pause.
        let paused = StagePhrases.lengthCuts(0..<6, function: [Bool](repeating: false, count: 6),
                                             groups: [nil, nil, nil, nil, nil, nil], gaps: [0, 0, 0, 0, 0.5, 0], maxRuns: 4)
        XCTAssertEqual(paused, [4])
    }

    func testAHeldNoteIsFocalAndHeld() {
        let words = [Word(text: "oh", start: 1, end: 1.3), Word(text: "baby", start: 1.35, end: 1.7),
                     Word(text: "stay", start: 1.75, end: 3.4)]
        let (p, runs) = phrasing(words)
        assertWellFormed(p, runs)
        XCTAssertEqual(p.phrases.count, 1)
        XCTAssertEqual(p.phrases[0].focal, 2)
        XCTAssertTrue(p.words[2].isHeld)
        XCTAssertEqual(p.words[2].duration, 1.65, accuracy: 1e-9)
        XCTAssertFalse(p.words[1].isHeld)
        // A held function word is still a function word; if a phrase has
        // nothing else, it leads.
        let only = [Word(text: "you", start: 1, end: 2.5)]
        let (alone, aloneRuns) = phrasing(only)
        assertWellFormed(alone, aloneRuns)
        XCTAssertEqual(alone.phrases[0].focal, 0)
        XCTAssertTrue(alone.words[0].isFunction && alone.words[0].isHeld)
        XCTAssertEqual(alone.words[0].role, .focal)
        // A word's end past the next onset does not count as held.
        let overlapping = [Word(text: "light", start: 1, end: 4), Word(text: "fades", start: 1.4, end: 1.9)]
        let (o, _) = phrasing(overlapping)
        XCTAssertEqual(o.words[0].duration, 0.4, accuracy: 1e-9)
        XCTAssertFalse(o.words[0].isHeld)
    }

    func testPunctuationEndsAPhrase() {
        let (p, runs) = phrasing(Self.even("I love you, but I can't stay."))
        assertWellFormed(p, runs)
        XCTAssertEqual(texts(p, runs), ["I love you,", "but I can't stay."])
        XCTAssertEqual(p.phrases[1].breakBefore, .punctuation)
        XCTAssertTrue(StagePhrases.endsPhrase("go!\u{201D}"))
        XCTAssertTrue(StagePhrases.endsPhrase("\u{541B}\u{3001}"))
        XCTAssertFalse(StagePhrases.endsPhrase("don't"))
        XCTAssertTrue(StagePhrases.isFunctionWord("I\u{2019}m"))
        XCTAssertTrue(StagePhrases.isFunctionWord("The,"))
        XCTAssertFalse(StagePhrases.isFunctionWord("neon"))
    }

    func testJapaneseGluedRunsStayWhole() {
        let line = StageReadingTests.japanese
        let (p, runs) = phrasing(line.words, glue: line.glue)
        assertWellFormed(p, runs)
        XCTAssertEqual(runs.map(\.text), ["\u{541B}\u{306E}", "\u{540D}\u{524D}\u{3092}", "\u{547C}\u{3093}\u{3067}",
                                          "\u{591C}\u{7A7A}\u{306B}", "\u{6D88}\u{3048}\u{305F}"])
        XCTAssertEqual(p.phrases.map(\.runs), [0..<3, 3..<5])
        XCTAssertFalse(p.words.contains(where: \.isFunction))
        XCTAssertFalse(p.words.contains { $0.runGroup != nil }, "Glued characters are one run, not a quick run")
        // The analysis through `StageLyricView`'s inputs gives the same.
        XCTAssertEqual(StagePhrases.phrasing(words: line.words, glue: line.glue), p)
    }

    func testRightToLeftKeepsReadingOrder() {
        let (p, runs) = phrasing(Self.even("\u{05D0}\u{05E0}\u{05D9} \u{05D0}\u{05D5}\u{05D4}\u{05D1} \u{05D0}\u{05D5}\u{05EA}\u{05DA} \u{05DB}\u{05DC} \u{05DB}\u{05DA} \u{05D4}\u{05E8}\u{05D1}\u{05D4}"))
        assertWellFormed(p, runs)
        XCTAssertTrue(p.rightToLeft)
        XCTAssertEqual(p.phrases.map(\.runs), [0..<3, 3..<6], "Logical order; the layout mirrors it")
        let (arabic, arabicRuns) = phrasing(Self.even("\u{0623}\u{062D}\u{0628}\u{0643} \u{064A}\u{0627} \u{0642}\u{0644}\u{0628}\u{064A}\u{060C} \u{062A}\u{0639}\u{0627}\u{0644}"))
        assertWellFormed(arabic, arabicRuns)
        XCTAssertTrue(arabic.rightToLeft)
        XCTAssertEqual(arabic.phrases.map(\.runs), [0..<3, 3..<4], "An Arabic comma ends a phrase")
    }

    func testLineTimedLinesArePhrasedByLengthAndPunctuationOnly() {
        let words = "and we keep running through the city lights, until the morning comes".split(separator: " ").map {
            Word(text: String($0), start: 10, end: 14)
        }
        let (p, runs) = phrasing(words)
        assertWellFormed(p, runs)
        XCTAssertTrue(p.lineTimed)
        XCTAssertEqual(texts(p, runs), ["and we keep running", "through the city lights,", "until the morning comes"])
        XCTAssertEqual(p.phrases.map(\.breakBefore), [.lineStart, .length, .punctuation])
        XCTAssertFalse(p.words.contains { $0.isHeld || $0.runGroup != nil })
        XCTAssertTrue(p.phrases.allSatisfy { $0.start == 10 && $0.end == 14 }, "All at once")
        XCTAssertEqual(runs[p.phrases[0].focal].text, "running", "The longest word leads")
        XCTAssertEqual(p.phraseIndex(at: 10), 2, "Every phrase is there from the start")
        // Asked for explicitly, a timed line is treated the same way.
        let (forced, _) = phrasing(Self.mixed, lineTimed: true)
        XCTAssertTrue(forced.lineTimed)
        XCTAssertEqual(forced.phrases.map(\.runs), [0..<4, 4..<8, 8..<12])
        XCTAssertFalse(forced.words.contains(where: \.isHeld))
    }

    func testWordsOutOfOrderStillGiveWellFormedPhrases() {
        let words = [Word(text: "hello", start: 12, end: 13), Word(text: "world", start: 10, end: 11),
                     Word(text: "again", start: 11, end: 12), Word(text: "tonight", start: 9, end: 9.5),
                     Word(text: "forever", start: 13, end: 15)]
        let (p, runs) = phrasing(words)
        assertWellFormed(p, runs)
        XCTAssertTrue(p.words.allSatisfy { $0.gapBefore >= 0 && $0.duration >= 0 })
        XCTAssertEqual(p.phrases.first?.start ?? 0, 9, accuracy: 1e-9)
        // A single word and an empty line.
        XCTAssertEqual(StagePhrases.analyse([]), .empty)
        let (one, oneRuns) = phrasing([Word(text: "rain", start: 1, end: 1.4)])
        assertWellFormed(one, oneRuns)
        XCTAssertEqual(one.phrases.count, 1)
        XCTAssertFalse(one.lineTimed)
    }

    func testWordsWithoutEndsFindPausesFromTheirOnsets() {
        let starts: [Double] = [1, 1.5, 2, 2.5, 4, 4.5]
        let words = zip("one two three four five six".split(separator: " "), starts).map {
            Word(text: String($0), start: $1, end: $1)
        }
        let (p, runs) = phrasing(words)
        assertWellFormed(p, runs)
        XCTAssertEqual(p.phrases.map(\.runs), [0..<4, 4..<6])
        XCTAssertEqual(p.phrases[1].breakBefore, .gap)
    }

    func testThePhrasingIsCachedPerLine() {
        let runs = StageRun.make(Self.mixed, glue: [Bool](repeating: false, count: 12))
        let first = StagePhrases.phrasing(runs)
        XCTAssertEqual(StagePhrases.phrasing(runs), first)
        XCTAssertEqual(first, StagePhrases.analyse(runs))
        let other = StageRun.make(Self.even("we keep dreaming under neon rain"), glue: [Bool](repeating: false, count: 6))
        XCTAssertNotEqual(StagePhrases.phrasing(other), first)
        // Different rules are a different entry.
        var rules = StagePhrases.Rules()
        rules.maxRuns = 2
        XCTAssertEqual(StagePhrases.phrasing(runs, rules: rules).phrases.count, 6)
        XCTAssertEqual(StagePhrases.phrasing(runs).phrases.count, 3)
    }
}
