import XCTest
@testable import VersoCustom

/// Lyrics without word timing must not race through a line's words ahead of
/// the singing (Builds 61 and 62: every line's estimated words and sweep were
/// capped at about 0.45 s a syllable, so slow lines and held notes lit to the
/// end in half their time). Only an abnormally long window -- the last line
/// running through the outro, a line before a long break -- is shortened.
@MainActor
final class EstimatedWordPaceTests: XCTestCase {
    private func shown(_ lines: [LyricsFetcher.Line], repairing: Bool = true) -> [LyricsFetcher.Line] {
        LyricsViewModel.presentation(of: .synced(lines, .init(precision: "Line-synced", source: "LRCLIB")),
                                     repairingGaps: repairing).lines
    }

    /// A slow song: short lines, each held for six to eight seconds.
    private let ballad = """
    [00:10.00]Hold on
    [00:17.00]Don't let go
    [00:24.00]Stay
    [00:31.00]I will always love you
    [00:38.50]Oh, my darling
    [00:45.00]我爱你
    [00:52.00]君の名は
    [00:59.00]Hold on
    """

    func testAnOrdinarySlowLineFillsItsWindow() {
        let line = LyricsFetcher.makeLine(text: "Hold on", start: 10, end: 16)
        XCTAssertEqual(line.words.last?.end ?? 0, 10 + 6 * 0.88, accuracy: 1e-6,
                       "the words take the line's window, not 2.5 s")
        XCTAssertEqual(LyricPhraseTiming.sweep(of: line, at: 13), 0.5, accuracy: 1e-9)
        XCTAssertLessThan(LyricPhraseTiming.sweep(of: line, at: 15), 1)
        XCTAssertEqual(LyricsFetcher.estimatedSweepEnd(of: line), 16, accuracy: 1e-9)
        // Stage layouts clear a line once its words are sung: not mid-line.
        XCTAssertGreaterThan(LyricLayer.stageHeldUntil(line), 15)
    }

    func testNoLineOfASlowSongIsShortened() {
        let parsed = LyricsFetcher.parseLRC(ballad, trackDuration: 66)
        for repairing in [true, false] {
            let lines = shown(parsed, repairing: repairing)
            XCTAssertEqual(lines.count, 8)
            for line in lines {
                let window = line.end - line.start
                XCTAssertEqual((line.words.last?.end ?? 0) - line.start,
                               min(window, max(Double(line.words.count) * 0.42, window * 0.88)), accuracy: 1e-6,
                               "\(line.text) keeps its whole window")
                XCTAssertEqual(LyricsFetcher.estimatedSweepEnd(of: line), line.end, accuracy: 1e-6, line.text)
                // Half-way through the window, the sweep is half-way through the line.
                XCTAssertEqual(LyricPhraseTiming.sweep(of: line, at: line.start + window / 2), 0.5, accuracy: 1e-6,
                               line.text)
            }
        }
    }

    func testTheLastLineAndALineBeforeALongBreakAreShortened() {
        let lrc = """
        [00:10.00]Every night in my dreams
        [00:14.00]I see you, I feel you
        [00:18.00]That is how I know you go on
        [01:20.00]Far across the distance
        [01:24.00]And spaces between us
        [01:28.00]You have come to show you go on
        """
        let lines = shown(LyricsFetcher.parseLRC(lrc, trackDuration: 300))
        XCTAssertEqual(lines.count, 6)
        // Before a 62 s break, and running to the end of a 300 s track.
        for index in [2, 5] {
            let line = lines[index]
            let sung = (line.words.last?.end ?? 0) - line.start
            XCTAssertGreaterThan(sung, 4, line.text)
            XCTAssertLessThan(sung, 20, line.text)
            XCTAssertLessThan(LyricsFetcher.estimatedSweepEnd(of: line), line.start + 25, line.text)
            XCTAssertEqual(LyricPhraseTiming.sweep(of: line, at: line.start + 25), 1, line.text)
        }
        XCTAssertEqual(lines[2].end, 80, "the line's own times are unchanged")
        XCTAssertEqual(lines[5].end, 300)
        // The lines around them keep their windows.
        for index in [0, 1, 3, 4] {
            XCTAssertEqual(LyricsFetcher.estimatedSweepEnd(of: lines[index]), lines[index].end, accuracy: 1e-6)
        }
    }

    /// "Treat long pauses as instrumental" off, for songs with long held
    /// notes: no line is shortened, the last one included.
    func testNothingIsShortenedWithPauseInferenceOff() {
        let lrc = "[00:10.00]Every night in my dreams\n[00:14.00]I see you, I feel you\n[00:18.00]Hold on"
        let lines = shown(LyricsFetcher.parseLRC(lrc, trackDuration: 120), repairing: false)
        XCTAssertEqual(lines[2].words.last?.end ?? 0, 18 + 102 * 0.88, accuracy: 1e-6)
        XCTAssertEqual(LyricsFetcher.estimatedSweepEnd(of: lines[2]), 120, accuracy: 1e-6)
    }

    func testASuppliedLineEndIsNeverShortened() {
        // A line-timed TTML line: the source says it is sung for 15 s.
        var line = LyricsFetcher.makeLine(text: "Stay", start: 30, end: 45)
        line.endFromNextLine = nil
        let lines = shown([line, LyricsFetcher.makeLine(text: "Stay with me", start: 46, end: 50)])
        XCTAssertEqual(lines[0].words.last?.end ?? 0, 30 + 15 * 0.88, accuracy: 1e-6)
        XCTAssertEqual(LyricPhraseTiming.sweep(of: lines[0], at: 37.5), 0.5, accuracy: 1e-9)
    }

    /// Lyrics cached by Builds 61 and 62 kept their capped estimated words:
    /// shown again, they fill their windows.
    func testWordsSavedByTheCappedEstimateAreSpreadAgain() {
        var parsed = LyricsFetcher.parseLRC(ballad, trackDuration: 66)
        for index in parsed.indices where !parsed[index].isInstrumental {
            parsed[index].words = LyricsFetcher.distributeWords(in: parsed[index].text, from: parsed[index].start,
                                                                to: parsed[index].start + 2.5)
        }
        let lines = shown(parsed)
        XCTAssertEqual(lines[0].text, "Hold on")
        XCTAssertEqual(lines[0].words.last?.end ?? 0, 10 + 7 * 0.88, accuracy: 1e-6)
        XCTAssertEqual(LyricPhraseTiming.sweep(of: lines[0], at: 13.5), 0.5, accuracy: 1e-9)
    }

    func testSuppliedWordTimingIsLeftAlone() {
        let timed = LyricsFetcher.Line(start: 5, end: 40, text: "Hold on", words: [
            .init(text: "Hold", start: 5, end: 6), .init(text: "on", start: 6, end: 30),
        ], isInstrumental: false, wordTimed: true)
        let lines = shown([timed])
        XCTAssertEqual(lines[0].words, timed.words)
        XCTAssertTrue(lines[0].wordTimed)
    }
}
