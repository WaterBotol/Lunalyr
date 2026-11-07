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
        XCTAssertTrue(print(.smart, [.lrclib]).hasPrefix("v9|"))
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
}
