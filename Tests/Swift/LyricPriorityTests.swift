import XCTest
@testable import VersoCustom

/// The order matches are ranked in. Word-by-word or mixed timing comes
/// first: Music's own lyrics, then LRCLIB, then the other sources. Line
/// timing is next, Music's own first, then plain text. The configured order
/// only orders the other sources, in both match modes; saved choices and the
/// recording check still come before any of it.
final class LyricPriorityTests: XCTestCase {
    private enum Timing { case word, mixed, line, plain }
    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: "Our Album", duration: 180)
    /// A saved order like the user's own, with LRCLIB before Apple Music.
    private let userOrder: [LyricSourceID] = [.lrclib, .appleMusicCache, .lyricsPlus, .netEase, .qqMusic, .kugou, .kuwo, .lyricsOVH]
    private let music = LyricSourceID.appleMusicCache.title, lrclib = LyricSourceID.lrclib.title
    private let fast = LyricsFetcher.Timing(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.3, lrclibLimit: 3, sourceLimit: 3)
    private let couldNotBeat = LyricsFetcher.notNeeded + "it couldn’t beat the match showing"
    private var folder: URL!

    override func setUpWithError() throws {
        // Lookups write their cache here, never to Application Support.
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("LunalyrTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: folder)
    }

    /// Two lines: both word-timed, only the first, neither, or plain text.
    private func candidate(_ source: LyricSourceID, _ timing: Timing, title: String = "Our Song",
                           duration: Double? = 180) -> LyricCandidate {
        func line(_ text: String, at start: Double, timed: Bool) -> LyricsFetcher.Line {
            var line = LyricsFetcher.makeLine(text: text, start: start, end: start + 2)
            guard timed else { return line }
            line.wordTimed = true
            line.words = text.split(separator: " ").enumerated().map { index, word in
                .init(text: String(word), start: start + Double(index), end: start + Double(index) + 1)
            }
            return line
        }
        let provenance = LyricsFetcher.Provenance(precision: "fixture", source: source.title)
        let result: LyricsFetcher.Result = timing == .plain ? .plain(["Sing along", "Hold on"], provenance)
            : .synced([line("Sing along", at: 1, timed: timing != .line), line("Hold on", at: 4, timed: timing == .word)], provenance)
        return .init(source: source, result: result, title: title, artist: "Our Artist", duration: duration, score: 0)
    }
    /// Accuracy first and Source order over the same configured order.
    private func modes(_ order: [LyricSourceID]) -> [LyricSourceOptions.Snapshot] {
        LyricSourceOptions.Mode.allCases.map { .init(enabled: order, mode: $0, localFolder: "", localVersion: "") }
    }
    /// The winner, which is the same whichever order the matches arrive in.
    private func winner(_ candidates: [LyricCandidate], _ options: LyricSourceOptions.Snapshot,
                        file: StaticString = #filePath, line: UInt = #line) -> LyricSourceID? {
        let first = LyricsFetcher.ranked(candidates, query: query, options: options).first?.source
        XCTAssertEqual(LyricsFetcher.ranked(candidates.reversed(), query: query, options: options).first?.source, first, file: file, line: line)
        XCTAssertEqual(LyricsFetcher.choose(candidates.reversed(), query: query, options: options)?.source, first, file: file, line: line)
        return first
    }
    private func ranking(_ candidates: [LyricCandidate], _ options: LyricSourceOptions.Snapshot) -> [LyricSourceID] {
        LyricsFetcher.ranked(candidates, query: query, options: options).map(\.source)
    }

    func testTheFixturesCarryTheTimingLabelsTheCardsShow() {
        XCTAssertEqual(candidate(.lrclib, .word).provenance.precision, "Word-by-word")
        XCTAssertEqual(candidate(.lrclib, .mixed).provenance.precision, "Mixed timing")
        XCTAssertEqual(candidate(.lrclib, .line).provenance.precision, "Line-synced")
        XCTAssertEqual(candidate(.lrclib, .word).result.rankedTiming, 2)
        XCTAssertEqual(candidate(.lrclib, .mixed).result.rankedTiming, 2)
        XCTAssertEqual(candidate(.lrclib, .line).result.rankedTiming, 1)
        XCTAssertEqual(candidate(.lrclib, .plain).result.rankedTiming, 0)
    }

    // MARK: - Ranking

    func testMusicsWordTimingBeatsLRCLIBsWordTiming() {
        for options in modes(userOrder) {
            XCTAssertEqual(winner([candidate(.lrclib, .word), candidate(.appleMusicCache, .word)], options), .appleMusicCache, "\(options.mode)")
        }
    }

    func testMixedTimingRanksWithWordTimingSoMusicsMixedTimingStillWins() {
        for options in modes(userOrder) {
            XCTAssertEqual(winner([candidate(.lrclib, .word), candidate(.appleMusicCache, .mixed)], options), .appleMusicCache, "\(options.mode)")
        }
    }

    func testAnyWordOrMixedTimingBeatsMusicsLineTiming() {
        for options in modes(userOrder) {
            XCTAssertEqual(winner([candidate(.appleMusicCache, .line), candidate(.netEase, .word)], options), .netEase, "\(options.mode)")
            XCTAssertEqual(winner([candidate(.appleMusicCache, .line), candidate(.qqMusic, .mixed)], options), .qqMusic, "\(options.mode)")
        }
    }

    func testMusicLeadsLineTimingAndLRCLIBTakesItsPlaceInTheOrderThere() {
        let lines = [candidate(.qqMusic, .line), candidate(.lrclib, .line), candidate(.appleMusicCache, .line)]
        for options in modes(userOrder) {
            XCTAssertEqual(winner(lines, options), .appleMusicCache, "\(options.mode)")
            XCTAssertEqual(ranking(lines, options), [.appleMusicCache, .lrclib, .qqMusic], "\(options.mode)")
        }
        // With line timing, LRCLIB has no lead over the sources before it.
        for options in modes([.qqMusic, .appleMusicCache, .lrclib]) {
            XCTAssertEqual(ranking(lines, options), [.appleMusicCache, .qqMusic, .lrclib], "\(options.mode)")
        }
    }

    func testLRCLIBsMixedTimingBeatsOtherWordTiming() {
        for options in modes([.netEase, .lyricsPlus, .lrclib]) {
            XCTAssertEqual(winner([candidate(.netEase, .word), candidate(.lrclib, .mixed)], options), .lrclib, "\(options.mode)")
        }
    }

    func testWithoutMusicsLyricsLRCLIBsWordTimingLeads() {
        // Music's cache is switched off, or on with nothing for this song.
        for order in [[LyricSourceID.netEase, .lyricsPlus, .lrclib], [.appleMusicCache, .netEase, .lrclib]] {
            for options in modes(order) {
                XCTAssertEqual(winner([candidate(.netEase, .word), candidate(.lrclib, .word)], options), .lrclib, "\(options.mode)")
            }
        }
    }

    func testTheConfiguredOrderOnlyOrdersTheOtherSources() {
        let words = [LyricSourceID.kugou, .qqMusic, .netEase, .lyricsPlus, .lrclib, .appleMusicCache].map { candidate($0, .word) }
        // The user's order puts LRCLIB first; Music's own lyrics still lead.
        for options in modes(userOrder) {
            XCTAssertEqual(winner(words, options), .appleMusicCache, "\(options.mode)")
            XCTAssertEqual(ranking(words, options), [.appleMusicCache, .lrclib, .lyricsPlus, .netEase, .qqMusic, .kugou], "\(options.mode)")
        }
        // Reordering changes which of the other sources wins, never the lead.
        for options in modes([.kugou, .netEase, .qqMusic, .lyricsPlus, .lrclib, .appleMusicCache]) {
            XCTAssertEqual(ranking(words, options), [.appleMusicCache, .lrclib, .kugou, .netEase, .qqMusic, .lyricsPlus], "\(options.mode)")
            XCTAssertEqual(winner([candidate(.netEase, .word), candidate(.kugou, .mixed)], options), .kugou, "\(options.mode)")
        }
    }

    func testPlainTextIsOnlyAFallbackInTheConfiguredOrder() {
        for options in modes(userOrder) {
            XCTAssertEqual(winner([candidate(.lrclib, .plain), candidate(.lyricsOVH, .plain), candidate(.kuwo, .line)], options), .kuwo)
            XCTAssertEqual(winner([candidate(.lyricsOVH, .plain), candidate(.lrclib, .plain)], options), .lrclib, "\(options.mode)")
        }
        for options in modes([.lyricsOVH, .lrclib]) {
            XCTAssertEqual(winner([candidate(.lrclib, .plain), candidate(.lyricsOVH, .plain)], options), .lyricsOVH, "\(options.mode)")
        }
    }

    func testAccuracyFirstPrefersTheCloserMatchAmongTheOtherSourcesOnly() {
        let order: [LyricSourceID] = [.netEase, .lyricsPlus, .lrclib, .appleMusicCache]
        let accuracy = LyricSourceOptions.Snapshot(enabled: order, mode: .smart, localFolder: "", localVersion: "")
        let sourceOrder = LyricSourceOptions.Snapshot(enabled: order, mode: .priority, localFolder: "", localVersion: "")
        let live = candidate(.netEase, .word, title: "Our Song (Live)"), exact = candidate(.lyricsPlus, .mixed)
        XCTAssertEqual(winner([live, exact], accuracy), .lyricsPlus)
        XCTAssertEqual(winner([live, exact], sourceOrder), .netEase)
        // A closer match never lifts another source above LRCLIB or Music.
        let remaster = candidate(.lrclib, .mixed, title: "Our Song - Remastered 2011")
        for options in [accuracy, sourceOrder] {
            XCTAssertEqual(winner([exact, remaster], options), .lrclib, "\(options.mode)")
            XCTAssertEqual(winner([exact, remaster, candidate(.appleMusicCache, .word)], options), .appleMusicCache, "\(options.mode)")
        }
    }

    func testMusicsLineOnlyCacheGivesWayToLRCLIBsWordTiming() async throws {
        let song = LyricQuery(title: "A Song", artist: "A Singer", album: "An Album", duration: 180)
        func music(_ ttml: String) throws -> LyricCandidate? {
            let payload = try JSONSerialization.data(withJSONObject: ["data": [[
                "id": "100", "type": "songs",
                "attributes": ["name": "A Song", "artistName": "A Singer", "albumName": "An Album", "durationInMillis": 180_100] as [String: Any],
                "relationships": ["syllable-lyrics": ["data": [["type": "syllable-lyrics", "attributes": ["ttmlLocalizations": ttml]]]]],
            ]]])
            return AppleMusicCachedLyrics.candidate(song, songs: try JSONDecoder().decode(AppleMusicCachedLyrics.Envelope.self, from: payload).data)
        }
        // Music's cache holds only line timing for this song.
        let lines = try XCTUnwrap(music("<tt><body><div><p begin='1' end='3'>One two</p><p begin='5' end='7'>Three four</p></div></body></tt>"))
        XCTAssertEqual(lines.provenance.precision, "Line-synced")
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [WordTimedLRCLIBProtocol.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let found = try await LyricsFetcher.lrclib(song, session: session)
        let words = try XCTUnwrap(found)
        XCTAssertEqual(words.provenance.precision, "Word-by-word")
        let syllables = try XCTUnwrap(music("<tt><body><div><p begin='1' end='3'><span begin='1' end='2'>One</span> <span begin='2' end='3'>two</span></p></div></body></tt>"))
        XCTAssertEqual(syllables.provenance.precision, "Word-by-word")
        for options in modes(userOrder) {
            XCTAssertEqual(LyricsFetcher.ranked([lines, words], query: song, options: options).map(\.source), [.lrclib, .appleMusicCache])
            XCTAssertEqual(LyricsFetcher.ranked([words, syllables], query: song, options: options).map(\.source), [.appleMusicCache, .lrclib])
        }
    }

    func testTheRecordingCheckStillAppliesToEverySource() {
        for options in modes(userOrder) {
            let screened = LyricsFetcher.screen([candidate(.appleMusicCache, .word, title: "Another Song"),
                                                 candidate(.lrclib, .word, duration: 186), candidate(.netEase, .line)],
                                                query: query, options: options)
            XCTAssertEqual(screened.accepted.map(\.source), [.netEase], "\(options.mode)")
            XCTAssertEqual(screened.rejected.map(\.candidate.source), [.lrclib, .appleMusicCache], "\(options.mode)")
        }
    }

    // MARK: - Settling and stopping

    func testOnlyTheBestPossibleMatchSettlesTheLookup() {
        for options in modes(userOrder) {
            let services = Set(options.enabled).subtracting([.appleMusicCache])
            // Nothing can rank above Music's own word or mixed timing.
            for timing in [Timing.word, .mixed] {
                XCTAssertTrue(LyricsFetcher.isSettled(candidate(.appleMusicCache, timing), pending: services, query: query, options: options))
            }
            // Only Music's own lyrics can rank above LRCLIB's.
            XCTAssertFalse(LyricsFetcher.isSettled(candidate(.lrclib, .word), pending: [.appleMusicCache], query: query, options: options))
            XCTAssertTrue(LyricsFetcher.isSettled(candidate(.lrclib, .mixed), pending: services.subtracting([.lrclib]), query: query, options: options))
            // Music's line timing waits for every source that can have word timing.
            let ownLines = candidate(.appleMusicCache, .line)
            for source in [LyricSourceID.lrclib, .lyricsPlus, .netEase, .qqMusic, .kugou] {
                XCTAssertTrue(LyricsFetcher.couldBeat(ownLines, from: source, query: query, options: options), "\(source)")
            }
            XCTAssertTrue(LyricsFetcher.isSettled(ownLines, pending: [.kuwo, .lyricsOVH], query: query, options: options))
            // Another source's word timing waits for LRCLIB and Music.
            let other = candidate(.netEase, .word)
            XCTAssertTrue(LyricsFetcher.couldBeat(other, from: .lrclib, query: query, options: options))
            XCTAssertTrue(LyricsFetcher.couldBeat(other, from: .appleMusicCache, query: query, options: options))
        }
    }

    func testOnlyMusicsWordOrMixedTimingMakesEveryServiceUnnecessary() {
        for options in modes(userOrder) {
            for timing in [Timing.word, .mixed] {
                XCTAssertFalse(LyricsFetcher.matters(.lrclib, best: candidate(.appleMusicCache, timing), onlineBest: nil, query: query, options: options))
            }
            let ownLines = candidate(.appleMusicCache, .line)
            for source in [LyricSourceID.lrclib, .netEase] {
                XCTAssertTrue(LyricsFetcher.matters(source, best: ownLines, onlineBest: nil, query: query, options: options), "\(source)")
            }
            let words = candidate(.lrclib, .word)
            XCTAssertFalse(LyricsFetcher.matters(.netEase, best: words, onlineBest: words, query: query, options: options))
            XCTAssertTrue(LyricsFetcher.matters(.appleMusicCache, best: words, onlineBest: words, query: query, options: options))
            // Another source's word timing never makes LRCLIB unnecessary.
            let other = candidate(.netEase, .word)
            XCTAssertTrue(LyricsFetcher.matters(.lrclib, best: other, onlineBest: other, query: query, options: options))
        }
    }

    // MARK: - Lookups

    /// Holds a stub source until the test releases it, so arrival order does
    /// not depend on timers. Gives up after two seconds rather than hang.
    private actor Gate {
        private var open = false
        func release() { open = true }
        func wait() async {
            for _ in 0..<400 where !open && !Task.isCancelled { try? await Task.sleep(nanoseconds: 5_000_000) }
        }
    }
    /// What the stub sources were asked, which were stopped, and every update.
    private actor Log {
        private(set) var calls: [LyricSourceID] = []
        private(set) var cancelled: Set<LyricSourceID> = []
        private(set) var updates: [LyricLookupReport] = []
        func call(_ id: LyricSourceID) { calls.append(id) }
        func cancel(_ id: LyricSourceID) { cancelled.insert(id) }
        func update(_ report: LyricLookupReport) { updates.append(report) }
        func count(_ id: LyricSourceID) -> Int { calls.filter { $0 == id }.count }
        func shown(_ revision: Int) -> LyricLookupReport? { updates.first { $0.revision == revision } }
    }
    private struct Stub {
        let fetcher: LyricsFetcher
        let cache: URL
        func saved(_ query: LyricQuery, _ options: LyricSourceOptions.Snapshot) -> LyricSourceID? {
            SavedLyrics.readCandidate(query.recordingKey + "|" + options.fingerprint, in: cache)?.source
        }
    }
    /// Each source waits for its gate, if it has one, then gives its answer;
    /// Music's cache gives `musicLater` on its second look. Each stub has its
    /// own cache, so one lookup's saved choice never answers the next.
    private func stub(_ answers: [LyricSourceID: LyricCandidate], gates: [LyricSourceID: Gate] = [:], log: Log,
                      musicLater: LyricCandidate? = nil) -> Stub {
        let cache = folder.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let fetcher = LyricsFetcher(cacheFolder: cache, timing: fast, isOnline: { true }) { id, _, _ in
            await log.call(id)
            await gates[id]?.wait()
            if Task.isCancelled { await log.cancel(id) }
            let looks = await log.count(id)
            if id == .appleMusicCache, looks > 1, let musicLater { return musicLater }
            return answers[id]
        }
        return Stub(fetcher: fetcher, cache: cache)
    }
    private func source(of result: LyricsFetcher.Result?) -> String? {
        switch result {
        case .synced(_, let p)?: return p.source
        case .plain(_, let p)?: return p.source
        case nil: return nil
        }
    }

    func testLRCLIBsWordTimingReplacesOtherWordTimingShownFirst() async {
        for options in modes([.netEase, .lrclib, .qqMusic]) {
            let log = Log(), late = Gate()
            let stub = stub([.netEase: candidate(.netEase, .word), .lrclib: candidate(.lrclib, .word)], gates: [.lrclib: late], log: log)
            let report = await stub.fetcher.lookup(query, options: options) { update in
                await log.update(update)
                if update.revision == 1 { await late.release() }
            }
            let first = await log.shown(1)
            XCTAssertEqual(source(of: first?.result), LyricSourceID.netEase.title, "\(options.mode)")
            // LRCLIB's answer could still rank higher, so it is not stopped.
            XCTAssertEqual(first?.states[.lrclib], "Searching…", "\(options.mode)")
            XCTAssertEqual(report.revision, 2, "\(options.mode)")
            XCTAssertEqual(source(of: report.result), lrclib, "\(options.mode)")
            XCTAssertEqual(report.states[.lrclib], "Match found · showing", "\(options.mode)")
            XCTAssertEqual(stub.saved(query, options), .lrclib, "\(options.mode)")
        }
    }

    func testLRCLIBsWordTimingReplacesMusicsLineTimingShownFirst() async {
        for options in modes(userOrder) {
            let log = Log(), late = Gate()
            let stub = stub([.appleMusicCache: candidate(.appleMusicCache, .line), .lrclib: candidate(.lrclib, .word)],
                            gates: [.lrclib: late], log: log)
            let report = await stub.fetcher.lookup(query, options: options) { update in
                await log.update(update)
                if update.revision == 1 { await late.release() }
            }
            let first = await log.shown(1)
            XCTAssertEqual(source(of: first?.result), music, "\(options.mode)")
            // Music's line timing stops none of the sources that can have word timing.
            XCTAssertEqual(first?.states[.lrclib], "Searching…", "\(options.mode)")
            XCTAssertEqual(report.revision, 2, "\(options.mode)")
            XCTAssertEqual(source(of: report.result), lrclib, "\(options.mode)")
            XCTAssertEqual(report.states[.appleMusicCache], "Match found", "\(options.mode)")
            let asked = await Set(log.calls)
            XCTAssertEqual(asked, Set(userOrder), "\(options.mode)")
        }
    }

    func testALowerRankedArrivalNeverReplacesTheMatchShowing() async {
        for options in modes(userOrder) {
            // A search asks every source: Music's line timing and NetEase's
            // word timing arrive after LRCLIB's word timing is showing.
            let log = Log(), late = Gate()
            let search = stub([.lrclib: candidate(.lrclib, .word), .appleMusicCache: candidate(.appleMusicCache, .line),
                               .netEase: candidate(.netEase, .word)],
                              gates: [.appleMusicCache: late, .netEase: late], log: log)
            let report = await search.fetcher.lookup(query, force: true, options: options) { update in
                await log.update(update)
                if update.revision == 1 { await late.release() }
            }
            XCTAssertEqual(report.revision, 1, "\(options.mode)")
            XCTAssertEqual(source(of: report.result), lrclib, "\(options.mode)")
            XCTAssertEqual(report.candidates.map(\.source), [.lrclib, .netEase, .appleMusicCache], "\(options.mode)")

            // Nor does Music's line timing on its second look.
            let again = Log()
            let automatic = stub([.lrclib: candidate(.lrclib, .word)], log: again, musicLater: candidate(.appleMusicCache, .line))
            let replay = await automatic.fetcher.lookup(query, options: options)
            XCTAssertEqual(replay.revision, 1, "\(options.mode)")
            XCTAssertEqual(source(of: replay.result), lrclib, "\(options.mode)")
            XCTAssertEqual(replay.states[.appleMusicCache], "Match found", "\(options.mode)")
            let looks = await again.count(.appleMusicCache)
            XCTAssertEqual(looks, 2, "\(options.mode)")
        }
    }

    func testMusicsWordOrMixedTimingNeedsNoService() async {
        for options in modes(userOrder) {
            for timing in [Timing.word, .mixed] {
                let log = Log()
                let stub = stub([.appleMusicCache: candidate(.appleMusicCache, timing), .lrclib: candidate(.lrclib, .word)], log: log)
                let report = await stub.fetcher.lookup(query, options: options)
                XCTAssertEqual(source(of: report.result), music, "\(options.mode)")
                XCTAssertEqual(report.revision, 1, "\(options.mode)")
                XCTAssertEqual(report.states[.lrclib], LyricsFetcher.notNeeded + "Music’s own lyrics are showing", "\(options.mode)")
                let asked = await log.calls
                XCTAssertEqual(asked, [.appleMusicCache], "\(options.mode)")
            }
        }
    }

    func testLRCLIBsWordTimingStopsTheOtherServicesOnceMusicHasNone() async {
        let others: [LyricSourceID] = [.lyricsPlus, .netEase, .qqMusic, .kugou, .kuwo, .lyricsOVH]
        for options in modes(userOrder) {
            // Music has line timing, or nothing even on its second look.
            for own in [candidate(.appleMusicCache, .line), nil] {
                let log = Log(), never = Gate()
                var answers = [LyricSourceID.lrclib: candidate(.lrclib, .mixed)]
                answers[.appleMusicCache] = own
                let stub = stub(answers, gates: Dictionary(uniqueKeysWithValues: others.map { ($0, never) }), log: log)
                let report = await stub.fetcher.lookup(query, options: options)
                XCTAssertEqual(source(of: report.result), lrclib, "\(options.mode)")
                XCTAssertEqual(report.revision, 1, "\(options.mode)")
                for id in others { XCTAssertEqual(report.states[id], couldNotBeat, "\(options.mode) \(id)") }
                let stopped = await log.cancelled
                XCTAssertEqual(stopped, Set(others), "\(options.mode)")
                // Nothing stopped could have beaten it, so replays use it straight away.
                XCTAssertEqual(stub.saved(query, options), .lrclib, "\(options.mode)")
            }
        }
    }

    func testMusicsSecondLookStillRunsAfterLRCLIBStopsTheOthers() async {
        let others: [LyricSourceID] = [.lyricsPlus, .netEase, .qqMusic, .kugou, .kuwo, .lyricsOVH]
        for options in modes(userOrder) {
            let log = Log(), never = Gate()
            let stub = stub([.lrclib: candidate(.lrclib, .word)], gates: Dictionary(uniqueKeysWithValues: others.map { ($0, never) }),
                            log: log, musicLater: candidate(.appleMusicCache, .mixed))
            let report = await stub.fetcher.lookup(query, options: options) { await log.update($0) }
            let first = await log.shown(1)
            XCTAssertEqual(source(of: first?.result), lrclib, "\(options.mode)")
            XCTAssertEqual(first?.states[.netEase], couldNotBeat, "\(options.mode)")
            XCTAssertEqual(report.revision, 2, "\(options.mode)")
            XCTAssertEqual(source(of: report.result), music, "\(options.mode)")
            XCTAssertEqual(report.states[.appleMusicCache], "Match found · showing", "\(options.mode)")
        }
    }

    func testPlainTextShowsOnlyWhenNothingIsTimed() async {
        for options in modes(userOrder) {
            let plain = stub([.lyricsOVH: candidate(.lyricsOVH, .plain), .lrclib: candidate(.lrclib, .plain)], log: Log())
            let report = await plain.fetcher.lookup(query, options: options)
            XCTAssertEqual(source(of: report.result), lrclib, "\(options.mode)")
            let timed = stub([.lyricsOVH: candidate(.lyricsOVH, .plain), .lrclib: candidate(.lrclib, .plain),
                              .kuwo: candidate(.kuwo, .line)], log: Log())
            let lines = await timed.fetcher.lookup(query, options: options)
            XCTAssertEqual(source(of: lines.result), LyricSourceID.kuwo.title, "\(options.mode)")
        }
    }

    func testASavedChoiceOutranksEverything() async {
        for options in modes(userOrder) {
            let log = Log()
            let stub = stub([.appleMusicCache: candidate(.appleMusicCache, .word), .lrclib: candidate(.lrclib, .word)], log: log)
            XCTAssertTrue(SavedLyrics.save(candidate(.qqMusic, .line).result, key: query.recordingKey, pin: true, in: stub.cache))
            let automatic = await stub.fetcher.lookup(query, options: options)
            XCTAssertTrue(automatic.pinned)
            XCTAssertEqual(source(of: automatic.result), LyricSourceID.qqMusic.title, "\(options.mode)")
            let asked = await log.calls
            XCTAssertTrue(asked.isEmpty, "\(options.mode)")
            // A search lists the higher-ranked matches but keeps the saved choice showing.
            let search = await stub.fetcher.lookup(query, force: true, options: options)
            XCTAssertTrue(search.pinned)
            XCTAssertEqual(source(of: search.result), LyricSourceID.qqMusic.title, "\(options.mode)")
            XCTAssertEqual(search.candidates.first?.source, .appleMusicCache, "\(options.mode)")
        }
    }

    // MARK: - Timing & sources

    @MainActor
    func testTimingAndSourcesStatesTheOrderInTheLabelsItShows() {
        XCTAssertEqual(music, AppleMusicCachedLyrics.sourceName)
        let text = LyricSourceOptions.ranking
        // Word and mixed timing, Music, LRCLIB, the rest, then line timing -- in that order.
        let labels = ["Word-by-word", "Mixed timing", music, lrclib, "your other sources", "Line-synced"]
        let positions = labels.compactMap { text.range(of: $0)?.lowerBound }
        XCTAssertEqual(positions.count, labels.count, text)
        XCTAssertEqual(positions, positions.sorted(), text)
        for mode in LyricSourceOptions.Mode.allCases {
            XCTAssertTrue(mode.detail.contains(mode.title), mode.detail)
            XCTAssertTrue(mode.detail.contains("other sources"), mode.detail)
        }
    }
}

private extension LyricsFetcher.Result {
    var rankedTiming: Int { LyricsFetcher.rankedTiming(self) }
}

/// LRCLIB answering with syllable-stamped LRC for "A Song" by "A Singer".
private final class WordTimedLRCLIBProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let lyrics = #"[00:01.00]<00:01.00>One <00:02.00>two <00:03.00>\n[00:05.00]<00:05.00>Three <00:06.00>four <00:07.00>"#
        let match = #"{"trackName":"A Song","artistName":"A Singer","albumName":"An Album","duration":180,"syncedLyrics":"\#(lyrics)"}"#
        let body = request.url?.path.hasSuffix("/search") == true ? "[\(match)]" : match
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: [:])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}
