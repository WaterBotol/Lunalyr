import XCTest
@testable import VersoCustom

final class LyricGapTimingTests: XCTestCase {
    @MainActor
    func testMixedTimingDropsEarlyWholePhraseCopiesBeforePlayback() {
        let mixed = """
        [00:01]The opening line
        [00:04.00]Hold the light!
        [00:04.36]<00:04.36>Hold <00:04.70>the <00:05.10>light<00:05.80>
        [00:07.00]A line with only phrase timing
        [00:10.00]Stay with me
        [00:10.51]<00:10.51>Stay <00:10.80>with <00:11.15>me <00:11.55>(oh)<00:12.0>
        """
        let lines = LyricsFetcher.parseLRC(mixed, trackDuration: 15)
        XCTAssertEqual(lines.count, 6)
        let result = LyricsFetcher.Result.synced(lines, .init(precision: "Mixed timing", source: "Imported lyrics"))
        let shown = LyricsViewModel.presentation(of: result, repairingGaps: false)
        XCTAssertEqual(shown.lines.map(\.text), ["The opening line", "Hold the light", "A line with only phrase timing", "Stay with me (oh)"])
        XCTAssertEqual(shown.lines.map(\.start), [1, 4.36, 7, 10.51])
        XCTAssertEqual(shown.lines.map(\.wordTimed), [false, true, false, true])
        XCTAssertEqual(shown.lines[0].end, 4.36, accuracy: 0.001)
        XCTAssertEqual(shown.lines[1].words.first?.start, 4.36)
        let timeline = LyricLineIndex(lines: shown.lines)
        XCTAssertEqual(timeline.line(at: 4.2), 0)
        XCTAssertEqual(timeline.line(at: 10.2), 2)
        XCTAssertEqual(shown.provenance?.precision, "Mixed timing")
    }

    func testGenuinelyRepeatedLinesWithSeparateWindowsArePreserved() {
        let first = LyricsFetcher.makeLine(text: "Hold on", start: 1, end: 1.45)
        let next = LyricsFetcher.Line(start: 1.5, end: 2.3, text: "Hold on", words: [
            .init(text: "Hold", start: 1.5, end: 1.85), .init(text: "on", start: 1.85, end: 2.3),
        ], isInstrumental: false, wordTimed: true)
        XCTAssertEqual(LyricLineIndex.removingMixedTimingCopies([first, next]), [first, next])
    }

    func testNativeWordEndMarksActualVocalWindow() {
        let line = LyricsFetcher.Line(start: 1, end: 30, text: "Hold", words: [.init(text: "Hold", start: 1.2, end: 4)], isInstrumental: false, wordTimed: true)
        for inferring in [false, true] {
            let windows = LyricGapTiming.windows(for: [line], inferringPauses: inferring)
            XCTAssertEqual(windows.first?.start, 1.2)
            XCTAssertEqual(windows.first?.end, 4)
        }
    }
    func testEstimatedWordsDoNotInventAnEarlierVocalEnd() {
        let line = LyricsFetcher.Line(start: 1, end: 30, endFromNextLine: true, text: "Hold", words: [.init(text: "Hold", start: 1, end: 25)], isInstrumental: false)
        XCTAssertEqual(LyricGapTiming.windows(for: [line]).first?.end, 30)
        // The inferred end comes from the text, never from the spread-out estimated words.
        XCTAssertEqual(LyricGapTiming.windows(for: [line], inferringPauses: true).first?.end, 6)
    }
    func testPauseInferenceIsOptInAndFindsALongBreak() {
        let lines = LyricsFetcher.parseLRC("[00:01]Hold on\n[00:35]The song returns", trackDuration: 40)
        XCTAssertEqual(lines.map(\.endFromNextLine), [true, true])
        // Off unless the caller asks, so QQ's own opt-out is never overridden.
        XCTAssertEqual(LyricGapTiming.windows(for: lines).map(\.end), [35, 40])
        let windows = LyricGapTiming.windows(for: lines, inferringPauses: true)
        XCTAssertEqual(windows.map(\.start), [1, 35])
        XCTAssertEqual(windows.map(\.end), [6, 40])
        var gap = VCVocalGap()
        let found = windows.withUnsafeBufferPointer { vc_vocal_gap($0.baseAddress, Int32($0.count), 20, 40, 2.4, &gap) }
        XCTAssertEqual(found, 1)
        XCTAssertEqual(gap.start, 6)
        XCTAssertEqual(gap.end, 35)
    }
    func testChorusLineBeforeAnInterludeEndsWithItsText() {
        // NetEase LRC: the chorus ends about 115 s, then music until 2:08.
        let lines = LyricsFetcher.parseLRC("[01:50.350]谁说站在光里的才算英雄\n[02:08.410]\n[02:08.710]他们说 要戒了你的狂 就像擦掉了污垢\n[02:16.250]他们说", trackDuration: 260)
        let windows = LyricGapTiming.windows(for: lines, inferringPauses: true)
        XCTAssertEqual(LyricGapTiming.estimatedSyllables("谁说站在光里的才算英雄"), 11)
        // The song's own pace: 16 syllables over 7.54 s.
        let pace = LyricGapTiming.secondsPerSyllable(in: lines)
        XCTAssertEqual(pace, 7.54 / 16, accuracy: 0.001)
        XCTAssertEqual(windows.first?.end ?? 0, 110.35 + 11 * pace + 3, accuracy: 0.001)
        XCTAssertEqual(windows.dropFirst().first?.start ?? 0, 128.71, accuracy: 0.001)
        var gap = VCVocalGap()
        let found = windows.withUnsafeBufferPointer { vc_vocal_gap($0.baseAddress, Int32($0.count), 125, 260, 2.4, &gap) }
        XCTAssertEqual(found, 1)
        XCTAssertEqual(gap.end, 128.71, accuracy: 0.001)
    }
    func testShortLinesInModestWindowsKeepThem() {
        // QQGapRepair leaves windows of 12 s or less alone; so does the inference.
        let lines = LyricsFetcher.parseLRC("[00:00.00]Oh\n[00:11.00]Hold on\n[00:22.50]I love you so\n[00:34.00]Let it go\n[00:45.00]Next", trackDuration: 49)
        XCTAssertEqual(LyricGapTiming.windows(for: lines, inferringPauses: true).map(\.end), [11, 22.5, 34, 45, 49])
    }
    func testSlowSongsGetLongerEstimates() {
        let quick = LyricsFetcher.parseLRC("[00:00]We keep on running through the night\n[00:03]Never looking back at all\n[00:06]Stay with me tonight\n[00:20]Every heartbeat calling out\n[00:23]End", trackDuration: 26)
        let slow = LyricsFetcher.parseLRC("[00:00]Hold me closer now\n[00:06]Under fading light\n[00:12]Stay with me tonight\n[00:26]Close your eyes\n[00:32]End", trackDuration: 35)
        XCTAssertEqual(LyricGapTiming.secondsPerSyllable(in: quick), 0.4, accuracy: 0.001)
        XCTAssertEqual(LyricGapTiming.secondsPerSyllable(in: slow), 1, accuracy: 0.001)
        // The same five-syllable line in a fourteen-second window: a break in a
        // quick song, but a held line in a ballad.
        XCTAssertEqual(LyricGapTiming.windows(for: quick, inferringPauses: true)[2].end, 11, accuracy: 0.001)
        XCTAssertEqual(LyricGapTiming.windows(for: slow, inferringPauses: true)[2].end, 26, accuracy: 0.001)
    }
    func testOrdinaryVerseAndLongSungLinesKeepTheirWindows() {
        let verse = LyricsFetcher.parseLRC("[00:01]Keep it moving\n[00:04]One more phrase\n[00:07]Hold the light\n[00:10]x", trackDuration: 14)
        XCTAssertEqual(LyricGapTiming.windows(for: verse, inferringPauses: true).map(\.end), [4, 7, 10, 14])
        // Twenty-odd syllables can fill fifteen seconds: no break is invented.
        let long = LyricsFetcher.parseLRC("[00:01]I have been waiting all of my life for a moment exactly like this one\n[00:16]Next", trackDuration: 20)
        XCTAssertEqual(LyricGapTiming.windows(for: long, inferringPauses: true).first?.end, 16)
        XCTAssertGreaterThan(LyricGapTiming.estimatedSyllables("I have been waiting all of my life for a moment exactly like this one"), 15)
    }
    func testSuppliedEndsAreNeverShortened() {
        let held = LyricsFetcher.Line(start: 1, end: 40, text: "Stay", words: [.init(text: "Stay", start: 1, end: 38)], isInstrumental: false, wordTimed: true)
        XCTAssertEqual(LyricGapTiming.windows(for: [held], inferringPauses: true).first?.end, 38)
        // Line-synced TTML often runs one paragraph into the next: that end is
        // still the source's own.
        let ttml = TTMLLyricParser.parse("<tt><body><div><p begin=\"1\" end=\"13\">Let it go</p><p begin=\"13\" end=\"16\">Next line here</p></div></body></tt>", duration: 20)
        XCTAssertEqual(ttml.map(\.endFromNextLine), [nil, nil])
        XCTAssertEqual(LyricGapTiming.windows(for: ttml, inferringPauses: true).map(\.end), [13, 16])
        let lines = [
            LyricsFetcher.makeLine(text: "Oh", start: 1, end: 30),
            LyricsFetcher.makeLine(text: "Next", start: 40, end: 42),
        ]
        XCTAssertEqual(LyricGapTiming.windows(for: lines, inferringPauses: true).map(\.end), [30, 42])
        // lyricsfile rows keep a supplied end_ms; rows without one are like LRC.
        let file = LyricsFetcher.parseLyricsFile("lines:\n- text: Let it go\n  start_ms: 1000\n  end_ms: 13000\n- text: Next line here\n  start_ms: 13000\n")
        XCTAssertEqual(file.map(\.endFromNextLine), [nil, true])
    }
    func testQQRepairLeavesNothingForTheGenericInference() {
        let lines = LyricsFetcher.parseLRC("[00:01]Hold on\n[00:35]The song returns\n[00:40]And on", trackDuration: 60)
        let repaired = QQGapRepair.repair(lines)
        XCTAssertTrue(repaired.allSatisfy { $0.endFromNextLine == nil })
        XCTAssertEqual(LyricGapTiming.windows(for: repaired, inferringPauses: true).map(\.end),
                       LyricGapTiming.windows(for: repaired).map(\.end))
    }
    @MainActor
    func testShownLineLyricsHaveTheirPausesInferredUnlessTheyAreQQs() {
        let lines = LyricsFetcher.parseLRC("[00:01]Hold on\n[00:35]The song returns", trackDuration: 40)
        // What the lyrics model shows for a result, as the setting's own
        // change shows it again.
        func shown(_ source: String, repairing: Bool = true) -> LyricsViewModel.Presentation {
            LyricsViewModel.presentation(of: .synced(lines, .init(precision: "Line-synced", source: source)), repairingGaps: repairing)
        }
        // LRCLIB, NetEase's LRC, Kugou, Kuwo, local files and imports: the
        // 34-second break after "Hold on" is no longer one held line.
        for source in ["LRCLIB", "NetEase", "Kugou Music", "Kuwo Music", "Lyrimuse local files", "Imported lyrics"] {
            XCTAssertTrue(shown(source).infersPauses, source)
            // Its estimated words are lit over the singing, not the break.
            var expected = lines
            expected[0].words = LyricsFetcher.distributeWords(in: "Hold on", from: 1, to: 6)
            XCTAssertEqual(shown(source).lines, expected, source)
            XCTAssertEqual(LyricsFetcher.estimatedSweepEnd(of: shown(source).lines[0]), 6, accuracy: 1e-9, source)
            XCTAssertEqual(shown(source).provenance?.source, source)
            XCTAssertEqual(shown(source).vocalWindows.map(\.end), [6, 40], source)
        }
        // QQ Music's lyrics are split by QQGapRepair instead, not both.
        let qq = shown("QQ Music")
        XCTAssertFalse(qq.infersPauses)
        XCTAssertTrue(qq.lines.contains { $0.isInstrumental })
        XCTAssertLessThan(qq.vocalWindows.first?.end ?? 35, 35)
        // The one setting turns both estimates off.
        for source in ["NetEase", "QQ Music"] {
            let off = shown(source, repairing: false)
            XCTAssertFalse(off.infersPauses, source)
            XCTAssertEqual(off.lines, lines, source)
            XCTAssertEqual(off.vocalWindows.map(\.end), [35, 40], source)
        }
        // Word timing is never shortened, and plain text has no windows.
        let words = LyricsFetcher.Line(start: 1, end: 35, endFromNextLine: true, text: "Hold on",
                                       words: [.init(text: "Hold", start: 1, end: 2), .init(text: "on", start: 2, end: 30)],
                                       isInstrumental: false, wordTimed: true)
        let timed = LyricsViewModel.presentation(of: .synced([words], .init(precision: "Word-synced", source: "NetEase")), repairingGaps: true)
        XCTAssertEqual(timed.vocalWindows.map(\.end), [30])
        let plain = LyricsViewModel.presentation(of: .plain(["Hold on"], .init(precision: "Plain text", source: "lyrics.ovh")),
                                                 repairingGaps: true)
        XCTAssertEqual(plain.plainLines, ["Hold on"])
        XCTAssertTrue(plain.found && plain.lines.isEmpty && plain.vocalWindows.isEmpty && !plain.infersPauses)
        XCTAssertFalse(LyricsViewModel.presentation(of: nil, repairingGaps: true).found)
    }
    func testCJKSyllablesCountPerCharacter() {
        XCTAssertEqual(LyricGapTiming.estimatedSyllables("君の名は"), 4)
        XCTAssertEqual(LyricGapTiming.estimatedSyllables("사랑해 너를"), 5)
        XCTAssertEqual(LyricGapTiming.estimatedSyllables("Hold on"), 2)
    }
    func testExplicitInstrumentalLinesAreExcluded() {
        let line = LyricsFetcher.Line(start: 5, end: 20, text: "", words: [], isInstrumental: true)
        XCTAssertTrue(LyricGapTiming.windows(for: [line], inferringPauses: true).isEmpty)
    }
    func testInvalidOrUntimedEmptyLinesAreIgnored() {
        let line = LyricsFetcher.Line(start: .nan, end: 20, endFromNextLine: true, text: "Hold", words: [.init(text: "Hold", start: 1, end: 3)], isInstrumental: false)
        XCTAssertTrue(LyricGapTiming.windows(for: [line], inferringPauses: true).isEmpty)
    }
}
