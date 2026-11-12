import XCTest
@testable import VersoCustom

/// Recording checks explain every rejection, and the cache key only
/// changes with settings that change the choice.
final class LyricRankingTests: XCTestCase {
    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)

    private func candidate(_ source: LyricSourceID, words: Bool, title: String = "Our Song", artist: String = "Our Artist",
                           duration: Double? = 180, reported: Bool = true) -> LyricCandidate {
        var line = LyricsFetcher.makeLine(text: "Sing along", start: 1, end: 3)
        if words { line.wordTimed = true; line.words = [.init(text: "Sing", start: 1, end: 2), .init(text: "along", start: 2, end: 3)] }
        return .init(source: source, result: .synced([line], .init(precision: "fixture", source: source.title)),
                     title: title, artist: artist, duration: duration, score: 0, metadataReported: reported)
    }

    func testFormerNearMissesAreAcceptedAndRealMismatchesExplained() {
        let options = LyricSourceOptions.Snapshot(enabled: [.netEase, .lrclib], mode: .smart, localFolder: "", localVersion: "")
        let remaster = LyricQuery(title: "Our Song (2011 Remaster)", artist: "Our Artist", album: nil, duration: 180)
        let screened = LyricsFetcher.screen([candidate(.lrclib, words: false, title: "Our Song - Remastered 2011")], query: remaster, options: options)
        XCTAssertEqual(screened.accepted.map(\.source), [.lrclib])

        // A six-second-longer edit with word timing used to beat the exact match.
        let longer = candidate(.netEase, words: true, duration: 186)
        let result = LyricsFetcher.screen([longer, candidate(.lrclib, words: false)], query: query, options: options)
        XCTAssertEqual(result.accepted.map(\.source), [.lrclib])
        XCTAssertEqual(result.rejected.first?.reason, "Rejected: duration 3:06 vs 3:00")
        XCTAssertEqual(LyricsFetcher.choose(result.accepted, query: query, options: options)?.source, .lrclib)
        XCTAssertEqual(LyricsFetcher.rejection(candidate(.qqMusic, words: false, artist: "Someone Else"), query: query),
                       "Rejected: artist “Someone Else”")
    }

    func testBlankArtistMatchesOnTitleAndDurationOnly() {
        let untagged = LyricQuery(title: "Our Song", artist: " ", album: nil, duration: 180)
        XCTAssertNil(LyricsFetcher.rejection(candidate(.lrclib, words: false, artist: "Anyone"), query: untagged))
        XCTAssertEqual(LyricsFetcher.rejection(candidate(.lrclib, words: false, duration: nil), query: untagged),
                       "Rejected: no artist or song length to compare")
        XCTAssertEqual(untagged.artistScore("Anyone"), 0.5)
    }

    func testServiceThatReportsNoRecordingIsStillCheckedAgainstTheSongLength() {
        var long = candidate(.lyricsPlus, words: true, duration: nil, reported: false)
        long.result = .synced([LyricsFetcher.makeLine(text: "Coda", start: 195, end: 199)], .init(precision: "", source: ""))
        XCTAssertEqual(LyricsFetcher.rejection(long, query: query), "Rejected: lyrics run past the end of this recording")
        XCTAssertNil(LyricsFetcher.rejection(candidate(.lyricsPlus, words: true, duration: nil, reported: false), query: query))
    }

    func testCacheFingerprintIgnoresInactiveSettingsAndLocalSources() {
        func print(_ mode: LyricSourceOptions.Mode, _ enabled: [LyricSourceID]) -> String {
            LyricSourceOptions.Snapshot(enabled: enabled, mode: mode, localFolder: "", localVersion: "x").fingerprint
        }
        // v9 adds AMLL and Kugou's word timing, so choices cached before are looked up again.
        XCTAssertTrue(print(.smart, [.lrclib]).hasPrefix("v14|"))
        XCTAssertNotEqual(print(.smart, [.lrclib]), print(.priority, [.lrclib]))
        XCTAssertEqual(print(.smart, [.lrclib]), print(.smart, [.lyrimuse, .lrclib, .appleMusicCache]))
        XCTAssertNotEqual(print(.smart, [.lrclib, .netEase]), print(.smart, [.netEase, .lrclib]))
    }

    func testLocalFilesWithTheArtistAreNotRejectedOnTheirLength() {
        // Exported for the playing track: the artist matches, so [length:] does not decide.
        let tagged = candidate(.lyrimuse, words: false, duration: 200)
        XCTAssertNil(LyricsFetcher.rejection(tagged, query: query))
        // Without an artist, the length is all that confirms the recording.
        let untagged = LyricQuery(title: "Our Song", artist: "", album: nil, duration: 180)
        XCTAssertEqual(LyricsFetcher.rejection(candidate(.lyrimuse, words: false, artist: "", duration: 200), query: untagged),
                       "Rejected: duration 3:20 vs 3:00")
        XCTAssertEqual(LocalLyricBridge.length("03:41"), 221)
        XCTAssertEqual(LocalLyricBridge.length("221"), 221)
        XCTAssertEqual(LocalLyricBridge.length("221000"), 221, "A bare number too large for seconds is milliseconds")
        XCTAssertEqual(LocalLyricBridge.length("221000ms"), 221)
        XCTAssertNil(LocalLyricBridge.length("99:99:99"))
        XCTAssertNil(LocalLyricBridge.length("0"))
    }

    func testReportedNamesInAnotherScriptFallBackToTheLibrarysNames() {
        XCTAssertNil(LyricQuery.checkable("米津玄師", against: "Kenshi Yonezu"))
        XCTAssertNil(LyricQuery.checkable(nil, against: "Our Song"))
        XCTAssertEqual(LyricQuery.checkable("Another Song", against: "Our Song"), "Another Song")
        XCTAssertEqual(LyricQuery.checkable("Señorita", against: "Senorita"), "Señorita")
        XCTAssertEqual(LyricQuery.checkable("夜に駆ける", against: "夜に駆ける"), "夜に駆ける")
    }

    func testDurationsFromBrokenRepliesDoNotCrashTheClock() {
        XCTAssertEqual(LyricQuery.clock(221), "3:41")
        XCTAssertEqual(LyricQuery.clock(3723), "1:02:03")
        XCTAssertEqual(LyricQuery.clock(1e19), "240:00:00")
        XCTAssertEqual(LyricQuery.clock(-.infinity), "0:00")
        XCTAssertEqual(LyricQuery.clock(.nan), "0:00")
    }

    func testSymbolOnlyNamesPassTheRecordingCheck() {
        let butterfly = LyricQuery(title: "🦋", artist: "!!!", album: nil, duration: 200)
        let match = LyricCandidate(source: .lrclib, result: .plain(["la"], .init(precision: "", source: "")),
                                   title: "🦋", artist: "!!!", duration: 200, score: 0)
        XCTAssertNil(LyricsFetcher.rejection(match, query: butterfly))
    }

    // MARK: Bunched lines

    /// A chorus as a listener-made LRC sometimes stamps it: seventeen lines
    /// inside five seconds (from a real LRCLIB upload).
    private func bunchedChorus(from start: Double = 92) -> [LyricsFetcher.Line] {
        var lines = [LyricsFetcher.makeLine(text: "No place I'd rather be", start: start - 3, end: start)]
        var time = start
        for index in 0..<17 {
            let next = time + 0.3
            lines.append(LyricsFetcher.makeLine(text: index % 3 == 0 ? "N-n-no, no, no" : "No place I'd rather be", start: time, end: next))
            time = next
        }
        lines.append(LyricsFetcher.makeLine(text: "Sing it once again", start: time, end: time + 4))
        return lines
    }

    private func steady(_ count: Int = 20, every gap: Double = 2.5) -> [LyricsFetcher.Line] {
        (0..<count).map { LyricsFetcher.makeLine(text: "No place I'd rather be", start: 10 + Double($0) * gap, end: 10 + Double($0 + 1) * gap) }
    }

    func testABunchedChorusIsFoundAndSteadyLinesAreNot() {
        let run = LyricGapTiming.bunchedRun(in: bunchedChorus())
        XCTAssertEqual(run?.count, 18)
        XCTAssertEqual(run?.start ?? 0, 92, accuracy: 0.01)
        XCTAssertNil(LyricGapTiming.bunchedRun(in: steady()))
        // Fast but singable: eight syllables a second over 1 s lines.
        XCTAssertNil(LyricGapTiming.bunchedRun(in: steady(every: 1)))
        // Four quick ad-libs in a row are a stutter, not a broken file.
        var stutter = steady()
        for index in 0..<3 { stutter.insert(LyricsFetcher.makeLine(text: "Hey", start: 30.1 + Double(index) * 0.1, end: 30.2), at: 9 + index) }
        XCTAssertNil(LyricGapTiming.bunchedRun(in: stutter.sorted { $0.start < $1.start }))
        // Credits stamped half a second apart before the song are not lyrics.
        let credits = ["Human Nature - Yot Club", "Lyrics by：Ryan Kaiser", "Composed by：Ryan Kaiser", "Produced by：Ryan Kaiser"]
            .enumerated().map { LyricsFetcher.makeLine(text: $1, start: Double($0) * 0.5, end: Double($0 + 1) * 0.5) }
        XCTAssertNil(LyricGapTiming.bunchedRun(in: credits + steady()))
        // Instrumental markers and blank lines do not count as sung.
        var marked = steady()
        marked.insert(LyricsFetcher.Line(start: 20.1, end: 22, text: "", words: [], isInstrumental: true, wordTimed: false), at: 5)
        XCTAssertNil(LyricGapTiming.bunchedRun(in: marked))
    }

    func testSoundTimingOutranksBunchedTimingButNotPlainText() {
        let options = LyricSourceOptions.Snapshot(enabled: [.lrclib, .netEase], mode: .priority, localFolder: "", localVersion: "")
        var bunched = candidate(.lrclib, words: false)
        bunched.result = .synced(bunchedChorus(), .init(precision: "Line-synced", source: "LRCLIB"))
        var sound = candidate(.netEase, words: false)
        sound.result = .synced(steady(), .init(precision: "Line-synced", source: "NetEase"))
        XCTAssertEqual(LyricsFetcher.ranked([bunched, sound], query: query, options: options).map(\.source), [.netEase, .lrclib])
        // Still shown when it is the only timing found.
        var plain = candidate(.netEase, words: false)
        plain.result = .plain(["No place I'd rather be"], .init(precision: "Not synced", source: "NetEase"))
        XCTAssertEqual(LyricsFetcher.ranked([plain, bunched], query: query, options: options).map(\.source), [.lrclib, .netEase])
    }

    /// "Hurts Me" as one LRCLIB word-timed upload has it: a line's eleven
    /// words inside 0.04 s.
    private func crammed(at start: Double = 99.09) -> LyricsFetcher.Line {
        let text = "I don't need it (I'm obsessed when I don't have it)"
        let words = text.split(separator: " ").enumerated().map { index, word in
            LyricsFetcher.Word(text: String(word), start: start + Double(index) * 0.004, end: start + Double(index + 1) * 0.004)
        }
        return LyricsFetcher.Line(start: start, end: start + 0.04, text: text, words: words, isInstrumental: false, wordTimed: true)
    }

    private func wordTimed(_ lines: [LyricsFetcher.Line]) -> [LyricsFetcher.Line] {
        lines.map { line in
            var timed = line
            let parts = line.text.split(separator: " ")
            let step = min(0.3, (line.end - line.start) / Double(parts.count))
            timed.words = parts.enumerated().map { .init(text: String($1), start: line.start + Double($0) * step, end: line.start + Double($0 + 1) * step) }
            timed.wordTimed = true
            return timed
        }
    }

    func testCrammedWordsAreFoundAndSungWordsAreNot() {
        let song = wordTimed(steady())
        XCTAssertNil(LyricsFetcher.timingFault(song))
        XCTAssertEqual(LyricGapTiming.crushedLines(in: song + [crammed()]), [99.09])
        XCTAssertEqual(LyricsFetcher.timingFault(song + [crammed()]), "a line crammed at 1:39")
        XCTAssertEqual(LyricsFetcher.timingFault(song + [crammed(), crammed(at: 120)]), "2 lines crammed from 1:39")
        // A quick "oh-oh-oh" is three syllables: not enough to judge.
        let quick = LyricsFetcher.Line(start: 5, end: 5.1, text: "oh oh oh", words: [.init(text: "oh", start: 5, end: 5.03),
            .init(text: "oh", start: 5.03, end: 5.06), .init(text: "oh", start: 5.06, end: 5.1)], isInstrumental: false, wordTimed: true)
        XCTAssertNil(LyricsFetcher.timingFault([quick] + song))
    }

    func testCrammedLRCLIBWordTimingLosesItsLeadToSoundWordTiming() {
        let options = LyricSourceOptions.Snapshot(enabled: [.lrclib, .kugou], mode: .smart, localFolder: "", localVersion: "")
        var lrclib = candidate(.lrclib, words: true)
        lrclib.result = .synced(wordTimed(steady()) + [crammed()], .init(precision: "Word-by-word", source: "LRCLIB"))
        var kugou = candidate(.kugou, words: true)
        kugou.result = .synced(wordTimed(steady()), .init(precision: "Word-by-word", source: "Kugou Music"))
        XCTAssertEqual(LyricsFetcher.ranked([lrclib, kugou], query: query, options: options).map(\.source), [.kugou, .lrclib])
        // Sound, LRCLIB's word timing keeps its lead.
        lrclib.result = .synced(wordTimed(steady()), .init(precision: "Word-by-word", source: "LRCLIB"))
        XCTAssertEqual(LyricsFetcher.ranked([kugou, lrclib], query: query, options: options).map(\.source), [.lrclib, .kugou])
    }
}
