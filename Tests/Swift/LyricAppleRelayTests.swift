import XCTest
@testable import VersoCustom

/// Apple Music's lyrics relayed by Lyrics+ rank as Apple Music's: after
/// Music's own cache (the exact track), ahead of LRCLIB and the others.
final class LyricAppleRelayTests: XCTestCase {
    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)

    /// Two lines, word-timed (two tokens each) or line-timed.
    private func candidate(_ source: LyricSourceID, words: Bool, label: String? = nil) -> LyricCandidate {
        let lines = [("Sing along", 1.0), ("Hold on", 4.0)].map { text, start -> LyricsFetcher.Line in
            var line = LyricsFetcher.makeLine(text: text, start: start, end: start + 2)
            guard words else { return line }
            line.wordTimed = true
            line.words = text.split(separator: " ").enumerated().map { i, word in
                .init(text: String(word), start: start + Double(i), end: start + Double(i) + 1)
            }
            return line
        }
        return .init(source: source, result: .synced(lines, .init(precision: "fixture", source: label ?? source.title)),
                     title: "Our Song", artist: "Our Artist", duration: 180, score: 0)
    }
    private func relay(words: Bool = true) -> LyricCandidate { candidate(.lyricsPlus, words: words, label: "Apple via Lyrics+") }
    private func options(_ order: [LyricSourceID], _ mode: LyricSourceOptions.Mode) -> LyricSourceOptions.Snapshot {
        .init(enabled: order, mode: mode, localFolder: "", localVersion: "")
    }
    private func winner(_ candidates: [LyricCandidate], order: [LyricSourceID]) -> [LyricSourceID?] {
        LyricSourceOptions.Mode.allCases.flatMap { mode in
            [LyricsFetcher.choose(candidates, query: query, options: options(order, mode))?.source,
             LyricsFetcher.choose(candidates.reversed(), query: query, options: options(order, mode))?.source]
        }
    }

    func testTheRelayIsToldApartByItsUpstream() {
        // KPoe's "Apple (via Lyrics+)" becomes "Apple via Lyrics+" (`KPoeLyricsProvider.hit`).
        XCTAssertTrue(LyricsFetcher.isAppleRelay(provenanceSource: "Apple via Lyrics+"))
        XCTAssertTrue(LyricsFetcher.isAppleRelay(provenanceSource: "Apple Music via Lyrics+"))
        for other in ["Musixmatch via Lyrics+", "Spotify via Lyrics+", "QQ via Lyrics+", "Lyrics+ via Lyrics+", "Apple", "Applesauce via Lyrics+"] {
            XCTAssertFalse(LyricsFetcher.isAppleRelay(provenanceSource: other), other)
        }
        XCTAssertTrue(LyricsFetcher.isAppleRelay(relay()))
        // Only Lyrics+ relays: another source naming Apple is not one.
        XCTAssertFalse(LyricsFetcher.isAppleRelay(candidate(.netEase, words: true, label: "Apple via Lyrics+")))
    }

    func testTheRelayOutranksLRCLIBAndOthersAtWordTimingInAnyOrder() {
        for order in [[LyricSourceID.lrclib, .netEase, .lyricsPlus], [.lyricsPlus, .lrclib, .netEase]] {
            XCTAssertEqual(Set(winner([candidate(.lrclib, words: true), relay(), candidate(.netEase, words: true)], order: order)), [.lyricsPlus])
        }
    }

    func testMusicsOwnCacheWinsATieWithTheRelay() {
        for order in [[LyricSourceID.lyricsPlus, .appleMusicCache], [.appleMusicCache, .lyricsPlus]] {
            XCTAssertEqual(Set(winner([relay(), candidate(.appleMusicCache, words: true)], order: order)), [.appleMusicCache])
            XCTAssertEqual(Set(winner([relay(words: false), candidate(.appleMusicCache, words: false)], order: order)), [.appleMusicCache])
        }
    }

    func testTheRelayLeadsOtherLineTiming() {
        let order: [LyricSourceID] = [.netEase, .lrclib, .lyricsPlus]
        XCTAssertEqual(Set(winner([candidate(.netEase, words: false), candidate(.lrclib, words: false), relay(words: false)], order: order)), [.lyricsPlus])
        // Word timing still comes first.
        XCTAssertEqual(Set(winner([candidate(.netEase, words: true), relay(words: false)], order: order)), [.netEase])
    }

    func testLyricsPlusFromAnotherUpstreamIsAnotherSource() {
        let musixmatch = candidate(.lyricsPlus, words: true, label: "Musixmatch via Lyrics+")
        XCTAssertEqual(Set(winner([musixmatch, candidate(.lrclib, words: true)], order: [.lyricsPlus, .lrclib])), [.lrclib])
        XCTAssertEqual(Set(winner([musixmatch, candidate(.netEase, words: true)], order: [.netEase, .lyricsPlus])), [.netEase])
    }

    func testAPendingLyricsPlusCouldStillBeatLRCLIBButNotMusic() {
        let order: [LyricSourceID] = [.appleMusicCache, .lrclib, .lyricsPlus]
        for mode in LyricSourceOptions.Mode.allCases {
            XCTAssertTrue(LyricsFetcher.couldBeat(candidate(.lrclib, words: true), from: .lyricsPlus, query: query, options: options(order, mode)))
            XCTAssertFalse(LyricsFetcher.couldBeat(candidate(.appleMusicCache, words: true), from: .lyricsPlus, query: query, options: options(order, mode)))
            XCTAssertFalse(LyricsFetcher.couldBeat(relay(), from: .lrclib, query: query, options: options(order, mode)))
        }
    }

    @MainActor func testTheSourceListNamesTheRelay() async {
        let options = options([.lyricsPlus, .lrclib], .smart)
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("LunalyrTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let relay = relay(), lrclib = candidate(.lrclib, words: true)
        let fetcher = LyricsFetcher(cacheFolder: folder, timing: .init(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.3, lrclibLimit: 3, sourceLimit: 3),
                                    isOnline: { true }) { id, _, _ in id == .lyricsPlus ? relay : lrclib }
        let report = await fetcher.lookup(query, options: options)
        XCTAssertEqual(report.states[.lyricsPlus], "Match found · Apple Music’s lyrics · showing")
        XCTAssertTrue(LyricSourceOptions.ranking.contains("Apple Music’s lyrics from Lyrics+"))
    }
}
