import XCTest
@testable import VersoCustom

/// Lyrics for another version of the song (Live, Remix, Acoustic…) rank
/// below lyrics for the version playing, without being rejected.
final class VersionTagTests: XCTestCase {
    private let studio = LyricQuery(title: "Our Song", artist: "Our Artist", album: "Our Album", duration: 180)
    private let options = LyricSourceOptions.Snapshot(enabled: [.netEase, .lrclib, .qqMusic, .lyricsOVH],
                                                      mode: .smart, localFolder: "", localVersion: "")

    private func candidate(_ source: LyricSourceID, words: Bool, title: String = "Our Song",
                           reported: Bool = true) -> LyricCandidate {
        var line = LyricsFetcher.makeLine(text: "Sing along", start: 1, end: 3)
        if words { line.wordTimed = true; line.words = [.init(text: "Sing", start: 1, end: 2), .init(text: "along", start: 2, end: 3)] }
        return .init(source: source, result: .synced([line], .init(precision: "fixture", source: source.title)),
                     title: title, artist: "Our Artist", duration: 180, score: 0, metadataReported: reported)
    }

    func testTagsAreReadFromQualifiersInEveryCommonForm() {
        XCTAssertEqual(VersionTags.tags("Our Song (Live)"), ["live"])
        XCTAssertEqual(VersionTags.tags("Our Song - Live at Wembley"), ["live"])
        XCTAssertEqual(VersionTags.tags("Our Song (Acoustic Version) [Remix]"), ["acoustic", "remix"])
        XCTAssertEqual(VersionTags.tags("Our Song (Club Mix)"), ["remix"])
        XCTAssertEqual(VersionTags.tags("Our Song (Extended Mix)"), ["extended"])
        XCTAssertEqual(VersionTags.tags("Our Song (Sped Up)"), ["sped up"])
        XCTAssertEqual(VersionTags.tags("Our Song (Radio Edit)"), ["edit"])
        XCTAssertEqual(VersionTags.tags("Our Song (Inst.)"), ["instrumental"])
        XCTAssertEqual(VersionTags.tags("今天没回家 (现场)"), ["live"])
        XCTAssertEqual(VersionTags.tags("夜に駆ける (Live版)"), ["live"])
        XCTAssertEqual(VersionTags.tags("曲名 ~Live ver.~"), ["live"])
        XCTAssertEqual(VersionTags.tags("Our Song [Mixed]"), ["dj mix"])
    }

    func testSameRecordingQualifiersAreNotTags() {
        for name in ["Our Song", "Our Song (2011 Remaster)", "Our Song - Remastered 2011", "Our Song (feat. Someone)",
                     "Our Song (Deluxe Edition)", "Our Song (Original Mix)", "Our Song (Mono)", "Our Song (Mixed by Someone)",
                     "Our Song (Album Version)", "Liverpool Song", "Our Song (From \"The Film\")"] {
            XCTAssertEqual(VersionTags.tags(name), [], name)
        }
    }

    func testMismatchCountsBothDirectionsButForgivesTheAlbum() {
        XCTAssertEqual(VersionTags.mismatch(studio, title: "Our Song (Live)"), ["live"])
        let live = LyricQuery(title: "Our Song (Live)", artist: "Our Artist", album: nil, duration: 180)
        XCTAssertEqual(VersionTags.mismatch(live, title: "Our Song"), ["live"])
        XCTAssertEqual(VersionTags.mismatch(live, title: "Our Song - Live"), [])
        XCTAssertEqual(VersionTags.mismatch(live, title: "Our Song (现场)"), [])
        let onLiveAlbum = LyricQuery(title: "Our Song", artist: "Our Artist", album: "Our Album (Live)", duration: 180)
        XCTAssertEqual(VersionTags.mismatch(onLiveAlbum, title: "Our Song (Live)"), [])
        XCTAssertEqual(VersionTags.mismatch(studio, title: "Our Song (2011 Remaster)"), [])
    }

    func testPlayingVersionOutranksAnotherVersionsWordTimingAndLRCLIBLead() {
        let liveWords = candidate(.lrclib, words: true, title: "Our Song (Live)")
        let studioLines = candidate(.qqMusic, words: false)
        let screened = LyricsFetcher.screen([liveWords, studioLines], query: studio, options: options)
        // Not rejected: it can still be shown when nothing else is found.
        XCTAssertEqual(screened.accepted.map(\.source), [.qqMusic, .lrclib])
        XCTAssertEqual(LyricsFetcher.choose([liveWords], query: studio, options: options)?.source, .lrclib)
    }

    func testPlainTextStillRanksBelowAnotherVersionsTimedLyrics() {
        let remix = candidate(.netEase, words: false, title: "Our Song (Remix)")
        let plain = LyricCandidate(source: .lyricsOVH, result: .plain(["Sing along"], .init(precision: "", source: "")),
                                   title: "Our Song", artist: "Our Artist", duration: nil, score: 0)
        XCTAssertEqual(LyricsFetcher.choose([plain, remix], query: studio, options: options)?.source, .netEase)
    }

    func testAServiceThatOnlyRepeatsTheSearchIsNotHeldToIt() {
        let echoed = candidate(.lyricsPlus, words: true, reported: false)
        XCTAssertTrue(LyricsFetcher.fitsVersion(echoed, studio))
    }

    func testAnotherVersionKeepsTheLookupOpen() {
        let liveWords = candidate(.lrclib, words: true, title: "Our Song (Live)")
        XCTAssertTrue(LyricsFetcher.couldBeat(liveWords, from: .qqMusic, query: studio, options: options))
        XCTAssertFalse(LyricsFetcher.isSettled(liveWords, pending: [.qqMusic], query: studio, options: options))
        XCTAssertFalse(LyricsFetcher.couldBeat(liveWords, from: .lyricsOVH, query: studio, options: options))
        let studioWords = candidate(.lrclib, words: true)
        XCTAssertFalse(LyricsFetcher.couldBeat(studioWords, from: .lyricsOVH, query: studio, options: options))
    }

    @MainActor
    func testRankingTextMentionsVersions() {
        XCTAssertTrue(LyricSourceOptions.ranking.contains("Live, Remix"))
    }
}
