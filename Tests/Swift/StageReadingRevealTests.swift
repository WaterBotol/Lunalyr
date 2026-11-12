import XCTest
@testable import VersoCustom

/// Stage layouts (Portal, Torque, Hinge, …) set a line run by run. With
/// "Below lyrics", each run's reading must be its own and arrive with it,
/// not the whole line's reading on the first run as soon as the line starts.
@MainActor
final class StageReadingRevealTests: XCTestCase {
    /// Lines of a word-timed Japanese song from Apple Music, each with the
    /// source's own romanisation for the whole line (the case reported).
    static let lines: [LyricsFetcher.Line] = {
        func line(_ romanization: String, _ words: [(String, Double, Double)]) -> LyricsFetcher.Line {
            let timed = words.map { LyricsFetcher.Word(text: $0.0, start: $0.1, end: $0.2) }
            return LyricsFetcher.Line(start: timed.first!.start, end: timed.last!.end, text: words.map(\.0).joined(),
                                      words: timed, isInstrumental: false, wordTimed: true, romanization: romanization)
        }
        return [
            line("sugite yukutokinonaka", [("過ぎてゆく", 14.88, 16.11), ("時間", 16.33, 17.23), ("の", 17.23, 17.45), ("中", 17.45, 18.2)]),
            line("anata o omoidasu", [("あなたを", 18.61, 19.56), ("思", 19.56, 20.57), ("い", 20.57, 20.92), ("出", 20.92, 21.13), ("す", 21.13, 21.69)]),
            line("monouge ni nagameru gamen ni utsutta futari",
                 [("物憂げに", 22.34, 23.56), ("眺める", 23.73, 24.86), ("画面", 24.86, 25.53), ("に", 25.53, 25.78), ("映った", 25.78, 26.67), ("二人", 26.67, 27.63)]),
            line("waratteta", [("笑っ", 27.64, 28.29), ("てい", 28.29, 28.57), ("た", 28.57, 28.87)]),
        ]
    }()

    private static let options = RomanizationOptions.Snapshot(display: .below, language: .automatic, toneMarks: true, preferSaved: true)

    /// Each run's reading, as the stage layouts pair them.
    private func stageReadings(_ index: Int) -> [String?] {
        let line = Self.lines[index]
        let readings = LyricRomanizer.readings(Self.lines, options: Self.options)[index]
        let glue = LyricRuns.glue(line: line, words: line.words)
        let pairs = LyricReadingPairs.parts(readings, to: line.words, glue: StageRun.glue(line.words, glue: glue))
        return StageRun.make(line.words, glue: glue).map { StageLyricView.reading(of: $0, in: pairs) }
    }

    /// The part of `run`'s reading that shows at `time`.
    private func visibleReading(line index: Int, run runIndex: Int, at time: Double) -> String {
        let line = Self.lines[index]
        let readings = LyricRomanizer.readings(Self.lines, options: Self.options)[index]
        let glue = LyricRuns.glue(line: line, words: line.words)
        let pairs = LyricReadingPairs.parts(readings, to: line.words, glue: StageRun.glue(line.words, glue: glue))
        let run = StageRun.make(line.words, glue: glue)[runIndex]
        guard let shown = StageLyricView.revealedReading(of: run, in: pairs, at: time, colour: .white) else { return "" }
        return shown.runs.filter { $0.foregroundColor != .clear }
            .map { String(shown[$0.range].characters) }.joined()
    }

    /// The reading fills in with the syllables it reads: 思 (omo), い (i),
    /// 出 (da), each shown as it is sung, not the whole run at once.
    func testReadingFillsInSyllableBySyllable() {
        // 思い出 is sung 思 19.56, い 20.57, 出 20.92.
        XCTAssertEqual(visibleReading(line: 1, run: 1, at: 19.0), "")
        XCTAssertEqual(visibleReading(line: 1, run: 1, at: 19.8), "omo")
        XCTAssertEqual(visibleReading(line: 1, run: 1, at: 20.7), "omoi")
        XCTAssertEqual(visibleReading(line: 1, run: 1, at: 21.0), "omoida")
        // 時間の: toki, then no.
        XCTAssertEqual(visibleReading(line: 0, run: 1, at: 16.5), "toki")
        XCTAssertEqual(visibleReading(line: 0, run: 1, at: 17.4), "toki no")
    }

    func testEachRunCarriesOnlyItsOwnReading() {
        XCTAssertEqual(stageReadings(0), ["sugite yuku", "toki no", "naka"])
        XCTAssertEqual(stageReadings(1), ["anata o", "omoida", "su"])
        XCTAssertEqual(stageReadings(2), ["monou ge ni", "nagameru", "gamen ni", "utsutta", "futari"])
        // One phrase sung over several parts is shared out over them, not
        // all on the first part (waratteta under 笑っ, nothing after).
        XCTAssertEqual(stageReadings(3), ["wara", "tteta"])
    }

    func testAPhraseIsSharedOverTheWordsItIsSungAcross() {
        let readings = LyricRomanizer.readings(Self.lines, options: Self.options)
        XCTAssertEqual(readings[3]?.map(\.text), ["wara", "tte", "ta"])
        XCTAssertEqual(readings[1]?.map(\.text), ["anata o", "omo", "i", "da", "su"])
        // The parts keep the phrase's time, so where they are drawn as one
        // run they still read as one word.
        let line = Self.lines[3]
        let pairs = LyricReadingPairs.match(readings[3], to: line.words, glue: [false, true, true])
        XCTAssertEqual(pairs[line.words[0].presentationID], "waratteta")
    }

    /// The bug: paired by the line's own (longer) runs, the first stage run
    /// carried the whole line's reading and the rest carried none.
    func testLinePairingWouldPutTheWholeReadingOnTheFirstRun() {
        let line = Self.lines[0]
        let readings = LyricRomanizer.readings(Self.lines, options: Self.options)[0]
        let glue = LyricRuns.glue(line: line, words: line.words)
        let pairs = LyricReadingPairs.match(readings, to: line.words, glue: glue)
        let runs = StageRun.make(line.words, glue: glue).map { StageLyricView.reading(of: $0, in: pairs) }
        XCTAssertEqual(runs, ["sugite yuku toki no naka", nil, nil])
    }

    /// A run's reading only shows once the run itself has arrived.
    func testReadingsArriveWithTheirRuns() {
        let line = Self.lines[0]
        let runs = StageRun.make(line.words, glue: LyricRuns.glue(line: line, words: line.words))
        XCTAssertEqual(StageLyricView.shownRuns(runs, at: 15.0).count, 1)
        XCTAssertEqual(StageLyricView.shownRuns(runs, at: 16.4).count, 2)
        XCTAssertEqual(StageLyricView.shownRuns(runs, at: 17.5).count, 3)
    }

    /// `StageRun.glue` draws exactly the runs `make` builds, for spaced,
    /// unspaced and mixed lines.
    func testStageGlueMatchesMadeRuns() {
        let samples: [[String]] = [
            ["過ぎてゆく", "時間", "の", "中"],
            ["あ", "な", "た", "を", "思", "い", "出", "す"],
            ["beau", "ti", "ful", "day"],
            ["Hel", "lo", " ", "world", "愛", "し", "て", "る"],
        ]
        for texts in samples {
            let words = texts.enumerated().map { LyricsFetcher.Word(text: $1, start: Double($0), end: Double($0) + 1) }
            let glue = [false] + Array(repeating: true, count: max(0, words.count - 1))
            let runs = StageRun.make(words, glue: glue).map(\.text)
            let stageGlue = StageRun.glue(words, glue: glue)
            let rebuilt = LyricRuns.ranges(stageGlue, count: words.count)
                .map { words[$0].map { $0.text.trimmingCharacters(in: .whitespacesAndNewlines) }.joined() }
                .filter { !$0.isEmpty }
            XCTAssertEqual(rebuilt, runs, "\(texts)")
        }
    }

    /// A word the tokenizer parts at its っ (一|回) reads as one word in
    /// the run that draws it (Hinge and the other stages), not "ik kai".
    func testAGeminateWordReadsAsOneWordInItsRun() {
        func line(_ reading: String, _ parts: [String]) -> LyricsFetcher.Line {
            var time = 40.0
            let words = parts.map { part -> LyricsFetcher.Word in
                defer { time += 0.4 }
                return .init(text: part, start: time, end: time + 0.4)
            }
            return .init(start: 40, end: time, text: parts.joined(), words: words, isInstrumental: false,
                         wordTimed: true, romanization: reading)
        }
        func stage(_ line: LyricsFetcher.Line) -> String {
            let readings = LyricRomanizer.readings([line], options: Self.options)[0]
            let glue = LyricRuns.glue(line: line, words: line.words)
            let pairs = LyricReadingPairs.parts(readings, to: line.words, glue: StageRun.glue(line.words, glue: glue))
            return StageRun.make(line.words, glue: glue).compactMap { StageLyricView.reading(of: $0, in: pairs) }
                .joined(separator: " | ")
        }
        XCTAssertEqual(stage(line("mou ikkai", ["もう", "一回"])), "mou | ikkai")
        XCTAssertEqual(stage(line("mou ikkai", ["もう", "一", "回"])), "mou ik | kai")
        XCTAssertEqual(stage(line("issho ni", ["一緒", "に"])), "issho ni")
        XCTAssertEqual(stage(line("issho ni", ["一", "緒に"])), "issho ni")
        XCTAssertEqual(stage(line("gakkou", ["学", "校"])), "gakkou")
        // Where 一 and 回 are runs of their own, each reads its own part.
        XCTAssertEqual(stage(line("mou ikkai", ["も", "う", "一", "回"])), "mou ik | kai")
    }
}
