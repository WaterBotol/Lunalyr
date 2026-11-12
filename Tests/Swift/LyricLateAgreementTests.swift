import XCTest
@testable import VersoCustom

/// LRCLIB's word timing on screen gives way when two later answers agree
/// against it: its lead goes, and the better match replaces it (between
/// lines, through `LyricsViewModel.reception`). Nothing else swaps that
/// did not before, and a saved choice never does.
final class LyricLateAgreementTests: XCTestCase {
    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)
    private let verses = ["First line here", "Second line here", "Third line here", "Fourth line here", "Fifth line here"]
    private var folder: URL!

    override func setUpWithError() throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("LunalyrTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: folder)
    }

    /// Five lines, every word timed, starting `shift` seconds late.
    private func candidate(_ source: LyricSourceID, shift: Double = 0, words: Bool = true) -> LyricCandidate {
        let lines = verses.enumerated().map { index, text -> LyricsFetcher.Line in
            let start = Double(index * 5 + 2) + shift
            var line = LyricsFetcher.makeLine(text: text, start: start, end: start + 4)
            guard words else { return line }
            line.wordTimed = true
            line.words = text.split(separator: " ").enumerated().map { i, word in
                .init(text: String(word), start: start + Double(i), end: start + Double(i) + 1)
            }
            return line
        }
        return .init(source: source, result: .synced(lines, .init(precision: "fixture", source: source.title)),
                     title: "Our Song", artist: "Our Artist", duration: 180, score: 0)
    }
    /// The user's own order puts LRCLIB first.
    private func options(_ mode: LyricSourceOptions.Mode = .smart) -> LyricSourceOptions.Snapshot {
        .init(enabled: [.lrclib, .netEase, .qqMusic, .kugou], mode: mode, localFolder: "", localVersion: "")
    }
    private func screened(_ candidates: [LyricCandidate], _ mode: LyricSourceOptions.Mode = .smart) -> [LyricCandidate] {
        LyricsFetcher.screen(candidates, query: query, options: options(mode)).accepted
    }

    // MARK: - Ranking

    func testADemotedLRCLIBRanksAfterTheOthersWhateverTheOrder() {
        let late = candidate(.lrclib, shift: 3)
        for mode in LyricSourceOptions.Mode.allCases {
            let ranked = screened([late, candidate(.netEase), candidate(.qqMusic, shift: 0.3)], mode)
            XCTAssertEqual(ranked.first?.source, .netEase, "\(mode)")
            XCTAssertEqual(ranked.last?.source, .lrclib, "\(mode)")
        }
    }

    // MARK: - Publication

    func testTheMatchOnScreenIsWeighedAgainstLaterAnswers() {
        for mode in LyricSourceOptions.Mode.allCases {
            var publication = LyricsFetcher.Publication(options: options(mode), query: query)
            let late = candidate(.lrclib, shift: 3)
            XCTAssertEqual(publication.step(best: late, settled: true, graceOver: false, settleOver: false, latest: screened([late], mode)), .publish)
            // One service disagreeing is not enough.
            let one = screened([late, candidate(.netEase)], mode)
            XCTAssertEqual(one.first?.source, .lrclib)
            XCTAssertEqual(publication.step(best: one.first, settled: false, graceOver: true, settleOver: false, latest: one), .wait)
            // A second agreeing with it: LRCLIB's lead goes and the better match takes over.
            let two = screened([late, candidate(.netEase), candidate(.qqMusic, shift: 0.3)], mode)
            XCTAssertEqual(publication.step(best: two.first, settled: true, graceOver: true, settleOver: false, latest: two), .publish, "\(mode)")
            XCTAssertEqual(publication.shown?.source, .netEase)
            XCTAssertEqual(publication.revision, 2)
        }
    }

    func testWithoutTheLatestAnswersTheStaleLeadHeld() {
        // What happened before: the shown match kept the agreement it had when it was shown.
        var publication = LyricsFetcher.Publication(options: options(), query: query)
        let late = candidate(.lrclib, shift: 3)
        _ = publication.step(best: late, settled: true, graceOver: true, settleOver: true)
        let two = screened([late, candidate(.netEase), candidate(.qqMusic, shift: 0.3)])
        XCTAssertFalse(publication.isUpgrade(two.first!, over: publication.shown!))
        XCTAssertEqual(publication.step(best: two.first, settled: true, graceOver: true, settleOver: true, latest: two), .publish)
    }

    func testASavedChoiceIsNeverSwapped() {
        var locked = LyricsFetcher.Publication(options: options(), query: query, locked: true)
        let two = screened([candidate(.lrclib, shift: 3), candidate(.netEase), candidate(.qqMusic, shift: 0.3)])
        XCTAssertEqual(locked.step(best: two.first, settled: true, graceOver: true, settleOver: true, latest: two), .wait)
        XCTAssertEqual(locked.revision, 1)
    }

    func testLRCLIBAgreeingWithOneKeepsItsLeadOnScreen() {
        var publication = LyricsFetcher.Publication(options: options(), query: query)
        let lrclib = candidate(.lrclib)
        _ = publication.step(best: lrclib, settled: true, graceOver: true, settleOver: true, latest: [lrclib])
        let all = screened([lrclib, candidate(.netEase), candidate(.qqMusic, shift: 3), candidate(.kugou, shift: 3.2)])
        XCTAssertEqual(all.first?.source, .lrclib)
        XCTAssertEqual(publication.step(best: all.first, settled: true, graceOver: true, settleOver: true, latest: all), .wait)
    }

    // MARK: - Which sources keep answering

    func testSourcesKeepAnsweringWhileTheyCouldStillDemoteLRCLIB() {
        let lrclib = screened([candidate(.lrclib, shift: 3)])[0]
        // Nothing else in hand: any word-capable source could be the first of two.
        XCTAssertTrue(LyricsFetcher.couldDemote(lrclib, from: .netEase, among: [lrclib], query: query))
        XCTAssertFalse(LyricsFetcher.couldBeat(lrclib, from: .netEase, query: query, options: options(.priority)))
        XCTAssertTrue(LyricsFetcher.matters(.netEase, best: lrclib, onlineBest: lrclib, query: query, options: options(.priority), among: [lrclib]))
        // Line timing alone could only demote it beside a word-timed match in hand.
        XCTAssertFalse(LyricsFetcher.couldDemote(lrclib, from: .kuwo, among: [lrclib], query: query))
        let netEase = candidate(.netEase)
        XCTAssertTrue(LyricsFetcher.couldDemote(lrclib, from: .kuwo, among: [lrclib, netEase], query: query))
        // Plain text never times anything.
        XCTAssertFalse(LyricsFetcher.couldDemote(lrclib, from: .lyricsOVH, among: [lrclib, netEase], query: query))
        // LRCLIB agreeing with a match in hand keeps its lead whatever arrives.
        let backed = screened([candidate(.lrclib), candidate(.netEase)])
        let shown = backed.first { $0.source == .lrclib }!
        XCTAssertFalse(LyricsFetcher.couldDemote(shown, from: .qqMusic, among: backed, query: query))
        // Only LRCLIB's lead is weighed this way.
        XCTAssertFalse(LyricsFetcher.couldDemote(netEase, from: .qqMusic, among: [netEase], query: query))
        // LRCLIB's line timing has no lead to lose.
        let line = candidate(.lrclib, shift: 3, words: false)
        XCTAssertFalse(LyricsFetcher.couldDemote(line, from: .netEase, among: [line], query: query))
    }

    // MARK: - A lookup

    private func lookup(_ answers: [LyricSourceID: (delay: Double, candidate: LyricCandidate)]) -> LyricsFetcher {
        LyricsFetcher(cacheFolder: folder, timing: .init(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.3, lrclibLimit: 3, sourceLimit: 3),
                      isOnline: { true }) { id, _, _ in
            guard let answer = answers[id] else { return nil }
            try await Task.sleep(nanoseconds: UInt64(answer.delay * 1_000_000_000))
            return answer.candidate
        }
    }
    private actor Updates {
        var seen: [LyricLookupReport] = []
        func add(_ report: LyricLookupReport) { seen.append(report) }
    }
    private func source(of result: LyricsFetcher.Result?) -> String? {
        if case .synced(_, let p)? = result { return p.source }
        return nil
    }

    func testALateAgreementReplacesLRCLIBOnScreen() async {
        let updates = Updates()
        let fetcher = lookup([.lrclib: (0, candidate(.lrclib, shift: 3)), .netEase: (0.4, candidate(.netEase)),
                              .qqMusic: (0.7, candidate(.qqMusic, shift: 0.3))])
        let report = await fetcher.lookup(query, options: options(.priority)) { await updates.add($0) }
        let first = await updates.seen.first { $0.revision == 1 }
        XCTAssertEqual(source(of: first?.result), LyricSourceID.lrclib.title)
        XCTAssertEqual(report.revision, 2)
        XCTAssertEqual(source(of: report.result), LyricSourceID.netEase.title)
        XCTAssertEqual(report.states[.lrclib], "Match found · timing differs from other sources")
        // The view model swaps it in at a line boundary.
        XCTAssertEqual(LyricsViewModel.reception(of: report, applied: 1, rerun: false), .swapAtBoundary)
        // And the better match is what replays use.
        XCTAssertEqual(SavedLyrics.readCandidate(query.recordingKey + "|" + options(.priority).fingerprint, in: folder)?.source, .netEase)
    }

    func testLaterAnswersThatBackLRCLIBChangeNothing() async {
        let fetcher = lookup([.lrclib: (0, candidate(.lrclib)), .netEase: (0.4, candidate(.netEase, shift: 3)),
                              .qqMusic: (0.7, candidate(.qqMusic, shift: 0.2))])
        let report = await fetcher.lookup(query, options: options(.priority))
        XCTAssertEqual(report.revision, 1)
        XCTAssertEqual(source(of: report.result), LyricSourceID.lrclib.title)
    }
}
