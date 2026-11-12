import XCTest
@testable import VersoCustom

/// Progressive lookups: the first acceptable match is shown without waiting
/// for the slowest source, and is replaced only by a higher-ranked one.
final class LyricLookupProgressTests: XCTestCase {
    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)
    private var folder: URL!

    override func setUpWithError() throws {
        // Lookups write their cache here, never to Application Support.
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("LunalyrTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: folder)
    }

    /// Tier 0 plain text, 1 line-synced, 3 complete word timing.
    private func candidate(_ source: LyricSourceID, tier: Int, duration: Double? = 180, title: String = "Our Song") -> LyricCandidate {
        let provenance = LyricsFetcher.Provenance(precision: "fixture", source: source.title)
        var line = LyricsFetcher.makeLine(text: "Sing along", start: 1, end: 3)
        if tier == 3 { line.wordTimed = true; line.words = [.init(text: "Sing", start: 1, end: 2), .init(text: "along", start: 2, end: 3)] }
        let result: LyricsFetcher.Result = tier == 0 ? .plain(["Sing along"], provenance) : .synced([line], provenance)
        return .init(source: source, result: result, title: title, artist: "Our Artist", duration: duration, score: 0)
    }
    private func source(of result: LyricsFetcher.Result?) -> String? {
        switch result {
        case .synced(_, let p)?: return p.source
        case .plain(_, let p)?: return p.source
        case nil: return nil
        }
    }
    private let fast = LyricsFetcher.Timing(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.3, lrclibLimit: 3, sourceLimit: 3)

    // MARK: - Publication policy

    func testFirstMatchWaitsForTheGracePeriodUnlessNothingCouldBeatIt() {
        let options = LyricSourceOptions.Snapshot(enabled: [.lyricsPlus, .lrclib], mode: .smart, localFolder: "", localVersion: "")
        var publication = LyricsFetcher.Publication(options: options, query: query)
        let line = candidate(.lrclib, tier: 1)
        XCTAssertEqual(publication.step(best: line, settled: false, graceOver: false, settleOver: false), .wait)
        XCTAssertEqual(publication.step(best: line, settled: false, graceOver: true, settleOver: false), .publish)
        XCTAssertEqual(publication.revision, 1)
        var settled = LyricsFetcher.Publication(options: options, query: query)
        XCTAssertEqual(settled.step(best: line, settled: true, graceOver: false, settleOver: false), .publish)
    }

    func testOnlyAHigherRankedMatchReplacesWhatIsShowing() {
        let options = LyricSourceOptions.Snapshot(enabled: [.lyricsPlus, .netEase, .lrclib, .lyricsOVH], mode: .smart, localFolder: "", localVersion: "")
        var publication = LyricsFetcher.Publication(options: options, query: query)
        _ = publication.step(best: candidate(.lyricsPlus, tier: 0), settled: false, graceOver: true, settleOver: false)
        // Plain text from a source lower in the order never replaces it.
        XCTAssertEqual(publication.step(best: candidate(.lyricsOVH, tier: 0), settled: true, graceOver: true, settleOver: true), .wait)
        // Line timing could still be beaten by word timing: hold briefly first.
        XCTAssertEqual(publication.step(best: candidate(.lrclib, tier: 1), settled: false, graceOver: true, settleOver: false), .holdForUpgrade)
        XCTAssertEqual(publication.step(best: candidate(.lrclib, tier: 1), settled: false, graceOver: true, settleOver: true), .publish)
        XCTAssertEqual(publication.shown?.source, .lrclib)
        // Each higher-ranked arrival takes over in turn, so the lookup ends on the best.
        XCTAssertEqual(publication.step(best: candidate(.netEase, tier: 3), settled: true, graceOver: true, settleOver: false), .publish)
        XCTAssertEqual(publication.step(best: candidate(.lyricsPlus, tier: 3), settled: true, graceOver: true, settleOver: false), .publish)
        XCTAssertEqual(publication.step(best: candidate(.netEase, tier: 3), settled: true, graceOver: true, settleOver: true), .wait)
        XCTAssertEqual(publication.shown?.source, .lyricsPlus)
        XCTAssertEqual(publication.revision, 4)
    }

    func testSavedChoicesAreNeverReplacedAndMusicsOwnLyricsLeadOnlyTheirTiming() {
        let options = LyricSourceOptions.Snapshot(enabled: [.appleMusicCache, .netEase], mode: .smart, localFolder: "", localVersion: "")
        var locked = LyricsFetcher.Publication(options: options, query: query, locked: true)
        XCTAssertEqual(locked.step(best: candidate(.netEase, tier: 3), settled: true, graceOver: true, settleOver: true), .wait)
        XCTAssertEqual(locked.revision, 1)
        let publication = LyricsFetcher.Publication(options: options, query: query)
        XCTAssertTrue(publication.isUpgrade(candidate(.appleMusicCache, tier: 3), over: candidate(.netEase, tier: 3)))
        XCTAssertFalse(publication.isUpgrade(candidate(.netEase, tier: 3), over: candidate(.appleMusicCache, tier: 3)))
        // Music's line timing gives way to word timing, and leads other line timing.
        XCTAssertTrue(publication.isUpgrade(candidate(.netEase, tier: 3), over: candidate(.appleMusicCache, tier: 1)))
        XCTAssertFalse(publication.isUpgrade(candidate(.appleMusicCache, tier: 1), over: candidate(.netEase, tier: 3)))
        XCTAssertTrue(publication.isUpgrade(candidate(.appleMusicCache, tier: 1), over: candidate(.netEase, tier: 1)))
        XCTAssertFalse(publication.isUpgrade(candidate(.netEase, tier: 1), over: candidate(.appleMusicCache, tier: 1)))
    }

    func testMusicsOwnWordTimingTakesOverAMatchThatWasAlreadyUpgraded() {
        let options = LyricSourceOptions.Snapshot(enabled: [.appleMusicCache, .lyricsPlus, .lrclib], mode: .smart, localFolder: "", localVersion: "")
        var publication = LyricsFetcher.Publication(options: options, query: query)
        _ = publication.step(best: candidate(.lrclib, tier: 1), settled: true, graceOver: true, settleOver: false)
        XCTAssertEqual(publication.step(best: candidate(.lyricsPlus, tier: 3), settled: true, graceOver: true, settleOver: false), .publish)
        // Nothing could beat Music's own word timing, so it takes over at once.
        let own = candidate(.appleMusicCache, tier: 3)
        let settled = LyricsFetcher.isSettled(own, pending: [.lyricsPlus, .lrclib], query: query, options: options)
        XCTAssertTrue(settled)
        XCTAssertEqual(publication.step(best: own, settled: settled, graceOver: true, settleOver: false), .publish)
        XCTAssertEqual(publication.shown?.source, .appleMusicCache)
        XCTAssertEqual(publication.revision, 3)
    }

    func testSettlesOnlyWhenNoPendingSourceCouldWin() {
        let options = LyricSourceOptions.Snapshot(enabled: [.lyricsPlus, .netEase, .lrclib, .kuwo], mode: .smart, localFolder: "", localVersion: "")
        let words = candidate(.netEase, tier: 3)
        // Lyrics+ ranks first on a tie, and LRCLIB's word timing leads every other source's.
        XCTAssertFalse(LyricsFetcher.isSettled(words, pending: [.lyricsPlus], query: query, options: options))
        XCTAssertFalse(LyricsFetcher.isSettled(words, pending: [.lrclib], query: query, options: options))
        // Kuwo only ever has line timing.
        XCTAssertTrue(LyricsFetcher.isSettled(words, pending: [.kuwo], query: query, options: options))
        XCTAssertTrue(LyricsFetcher.isSettled(candidate(.lrclib, tier: 3), pending: [.netEase, .kuwo], query: query, options: options))
        // Lyrics+ may relay Apple Music's lyrics, which rank above LRCLIB's.
        XCTAssertFalse(LyricsFetcher.isSettled(candidate(.lrclib, tier: 3), pending: [.lyricsPlus], query: query, options: options))
        XCTAssertFalse(LyricsFetcher.isSettled(candidate(.lrclib, tier: 1), pending: [.lyricsPlus], query: query, options: options))
    }

    @MainActor
    func testUpgradesWaitForTheLineBeingSungToFinish() {
        let lines = [LyricsFetcher.makeLine(text: "One", start: 1, end: 3), LyricsFetcher.makeLine(text: "Two", start: 5, end: 7)]
        let index = LyricLineIndex(lines: lines)
        XCTAssertNil(LyricsViewModel.lineBeingSung(lines, index: index, at: 0.5))
        XCTAssertEqual(LyricsViewModel.lineBeingSung(lines, index: index, at: 2), 0)
        XCTAssertNil(LyricsViewModel.lineBeingSung(lines, index: index, at: 4))
        XCTAssertEqual(LyricsViewModel.lineBeingSung(lines, index: index, at: 6), 1)
    }

    // MARK: - Lookups

    private actor Calls {
        private(set) var made: [LyricSourceID] = []
        func add(_ id: LyricSourceID) { made.append(id) }
        func count(_ id: LyricSourceID) -> Int { made.filter { $0 == id }.count }
    }
    private actor Updates {
        private(set) var seen: [LyricLookupReport] = []
        func add(_ report: LyricLookupReport) { seen.append(report) }
    }
    /// Holds a stub source until the test releases it, so arrival order does
    /// not depend on timers. Gives up after two seconds rather than hang.
    private actor Gate {
        private var open = false
        func release() { open = true }
        func wait() async {
            for _ in 0..<400 where !open && !Task.isCancelled { try? await Task.sleep(nanoseconds: 5_000_000) }
        }
    }
    /// Answers after `delay` seconds, or not at all when cancelled first.
    private func fetcher(_ script: [LyricSourceID: (delay: Double, candidate: LyricCandidate?)], calls: Calls,
                         online: Bool = true, misses: LyricMissCache = LyricMissCache()) -> LyricsFetcher {
        LyricsFetcher(cacheFolder: folder, timing: fast, misses: misses, isOnline: { online }) { id, _, _ in
            await calls.add(id)
            guard let step = script[id] else { return nil }
            try await Task.sleep(nanoseconds: UInt64(step.delay * 1_000_000_000))
            return step.candidate
        }
    }
    /// Each source waits for its gate, if it has one, then answers.
    private func gated(_ answers: [LyricSourceID: LyricCandidate?], gates: [LyricSourceID: Gate], calls: Calls) -> LyricsFetcher {
        LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }) { id, _, _ in
            await calls.add(id)
            await gates[id]?.wait()
            return answers[id] ?? nil
        }
    }

    func testSlowSourceDoesNotHoldBackAFastMatch() async {
        let options = LyricSourceOptions.Snapshot(enabled: [.lyricsPlus, .lrclib], mode: .smart, localFolder: "", localVersion: "")
        let calls = Calls(), updates = Updates(), slow = Gate()
        // Lyrics+ only answers once something is showing.
        let lookup = gated([.lrclib: candidate(.lrclib, tier: 1), .lyricsPlus: nil], gates: [.lyricsPlus: slow], calls: calls)
        let report = await lookup.lookup(query, options: options) { update in
            await updates.add(update)
            if update.revision == 1 { await slow.release() }
        }
        let shown = await updates.seen.first { $0.revision == 1 }
        XCTAssertEqual(source(of: shown?.result), LyricSourceID.lrclib.title)
        XCTAssertEqual(shown?.states[.lyricsPlus], "Searching…")
        XCTAssertTrue(report.isComplete)
        XCTAssertEqual(report.revision, 1)
        XCTAssertEqual(report.states[.lrclib], "Match found · showing")
        XCTAssertEqual(report.states[.lyricsPlus], "No usable match / unavailable")
    }

    func testEachHigherRankedMatchReplacesTheLastAndTheBestIsCached() async {
        let options = LyricSourceOptions.Snapshot(enabled: [.lyricsPlus, .netEase, .lrclib], mode: .smart, localFolder: "", localVersion: "")
        let calls = Calls(), updates = Updates(), netEase = Gate(), lyricsPlus = Gate()
        // Line timing shows first; NetEase's word timing replaces it; Lyrics+
        // then ties on timing and is first in the order, so it replaces that.
        let lookup = gated([.lrclib: candidate(.lrclib, tier: 1), .netEase: candidate(.netEase, tier: 3),
                            .lyricsPlus: candidate(.lyricsPlus, tier: 3)],
                           gates: [.netEase: netEase, .lyricsPlus: lyricsPlus], calls: calls)
        let report = await lookup.lookup(query, options: options) { update in
            await updates.add(update)
            if update.revision == 1 { await netEase.release() }
            if update.revision == 2 { await lyricsPlus.release() }
        }
        let revisions = await updates.seen.map(\.revision)
        XCTAssertEqual(Set(revisions), [0, 1, 2])
        let upgraded = await updates.seen.first { $0.revision == 2 }
        XCTAssertEqual(source(of: upgraded?.result), LyricSourceID.netEase.title)
        XCTAssertEqual(report.revision, 3)
        XCTAssertEqual(source(of: report.result), LyricSourceID.lyricsPlus.title)
        XCTAssertEqual(report.candidates.first?.source, .lyricsPlus)
        // Replays get the overall best straight from the cache.
        let saved = SavedLyrics.readCandidate(query.recordingKey + "|" + options.fingerprint, in: folder)
        XCTAssertEqual(saved?.source, .lyricsPlus)
        let replay = await lookup.lookup(query, options: options)
        XCTAssertTrue(replay.cached)
        XCTAssertEqual(source(of: replay.result), LyricSourceID.lyricsPlus.title)
        let made = await calls.made.count
        XCTAssertEqual(made, 3)
    }

    func testMusicsOwnLyricsAreCheckedAgainAndReplaceTheFirstMatch() async {
        let options = LyricSourceOptions.Snapshot(enabled: [.appleMusicCache, .lrclib], mode: .smart, localFolder: "", localVersion: "")
        let calls = Calls(), updates = Updates()
        let apple = candidate(.appleMusicCache, tier: 3), line = candidate(.lrclib, tier: 1)
        // A long grace period: the first match can only show early if Music's
        // pending second look does not count as a source still to answer.
        let timing = LyricsFetcher.Timing(grace: 30, upgradeSettle: 0.1, appleRetry: 1, lrclibLimit: 3, sourceLimit: 3)
        let lookup = LyricsFetcher(cacheFolder: folder, timing: timing, isOnline: { true }) { id, _, _ in
            await calls.add(id)
            if id == .lrclib { return line }
            return await calls.count(.appleMusicCache) > 1 ? apple : nil
        }
        let report = await lookup.lookup(query, options: options) { await updates.add($0) }
        let first = await updates.seen.first { $0.revision == 1 }
        XCTAssertEqual(source(of: first?.result), LyricSourceID.lrclib.title)
        XCTAssertEqual(report.revision, 2)
        XCTAssertEqual(source(of: report.result), LyricSourceID.appleMusicCache.title)
        let checks = await calls.count(.appleMusicCache)
        XCTAssertEqual(checks, 2)
        // Music's cache is read afresh each time; only the online choice is stored.
        XCTAssertEqual(SavedLyrics.readCandidate(query.recordingKey + "|" + options.fingerprint, in: folder)?.source, .lrclib)
    }

    func testMusicsLateLyricsReplaceAMatchEvenAfterTheUpgrade() async {
        let options = LyricSourceOptions.Snapshot(enabled: [.appleMusicCache, .lyricsPlus, .lrclib], mode: .smart, localFolder: "", localVersion: "")
        let calls = Calls(), updates = Updates(), lyricsPlus = Gate(), music = Gate()
        let line = candidate(.lrclib, tier: 1), words = candidate(.lyricsPlus, tier: 3), own = candidate(.appleMusicCache, tier: 3)
        let timing = LyricsFetcher.Timing(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.05, lrclibLimit: 3, sourceLimit: 3)
        let lookup = LyricsFetcher(cacheFolder: folder, timing: timing, isOnline: { true }) { id, _, _ in
            await calls.add(id)
            switch id {
            case .lrclib: return line
            case .lyricsPlus: await lyricsPlus.wait(); return words
            case .appleMusicCache:
                // Music has loaded its lyrics by its second look, which ends
                // after Lyrics+ has already replaced the first match.
                guard await calls.count(.appleMusicCache) > 1 else { return nil }
                await music.wait()
                return own
            default: return nil
            }
        }
        let report = await lookup.lookup(query, options: options) { update in
            await updates.add(update)
            if update.revision == 1 { await lyricsPlus.release() }
            if update.revision == 2 { await music.release() }
        }
        let upgraded = await updates.seen.first { $0.revision == 2 }
        XCTAssertEqual(source(of: upgraded?.result), LyricSourceID.lyricsPlus.title)
        XCTAssertEqual(report.revision, 3)
        XCTAssertEqual(source(of: report.result), LyricSourceID.appleMusicCache.title)
        XCTAssertEqual(report.states[.appleMusicCache], "Match found · showing")
    }

    func testMusicsOwnLyricsNeedNoNetworkButASearchAsksEverySource() async {
        let options = LyricSourceOptions.Snapshot(enabled: [.appleMusicCache, .lyricsPlus, .netEase, .lrclib, .qqMusic], mode: .smart,
                                                  localFolder: "", localVersion: "")
        let calls = Calls()
        let lookup = fetcher([.appleMusicCache: (0, candidate(.appleMusicCache, tier: 3))], calls: calls)
        let report = await lookup.lookup(query, options: options)
        XCTAssertEqual(source(of: report.result), LyricSourceID.appleMusicCache.title)
        XCTAssertEqual(report.revision, 1)
        XCTAssertEqual(report.states[.lrclib], "Not needed: Music’s own lyrics are showing")
        var made = await calls.made
        XCTAssertEqual(made, [.appleMusicCache])
        XCTAssertNil(SavedLyrics.readCandidate(query.recordingKey + "|" + options.fingerprint, in: folder))
        let search = await lookup.lookup(query, force: true, options: options)
        XCTAssertEqual(source(of: search.result), LyricSourceID.appleMusicCache.title)
        made = await calls.made
        XCTAssertEqual(Set(made), Set(options.enabled))
    }

    private actor Flag {
        private(set) var value = false
        func set() { value = true }
    }

    func testASettledMatchStopsTheSourcesThatCouldNotBeatIt() async {
        let options = LyricSourceOptions.Snapshot(enabled: [.lyricsPlus, .netEase, .lrclib], mode: .smart, localFolder: "", localVersion: "")
        let words = candidate(.lyricsPlus, tier: 3), netEase = Gate(), stopped = Flag()
        // Lyrics+ has word timing and LRCLIB nothing better; NetEase, lower in
        // the order, could only tie, so it is stopped rather than waited for.
        let lookup = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }) { id, _, _ in
            switch id {
            case .lyricsPlus: return words
            case .netEase:
                await netEase.wait()
                if Task.isCancelled { await stopped.set() }
                return nil
            default: return nil
            }
        }
        let report = await lookup.lookup(query, options: options)
        XCTAssertTrue(report.isComplete)
        XCTAssertEqual(source(of: report.result), LyricSourceID.lyricsPlus.title)
        XCTAssertEqual(report.states[.netEase], "Not needed: it couldn’t beat the match showing")
        let cancelled = await stopped.value
        XCTAssertTrue(cancelled)
        // Nothing stopped could have beaten it, so replays use it straight away.
        XCTAssertEqual(SavedLyrics.readCandidate(query.recordingKey + "|" + options.fingerprint, in: folder)?.source, .lyricsPlus)

        // A search asks every source, so all the alternatives are listed.
        let open = Gate()
        let search = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }) { id, _, _ in
            if id == .netEase { await open.wait(); return nil }
            return id == .lyricsPlus ? words : nil
        }
        let searched = await search.lookup(query, force: true, options: options) { update in
            if update.revision == 1 { await open.release() }
        }
        XCTAssertEqual(searched.states[.netEase], "No usable match / unavailable")
    }

    func testMissesAreRememberedBrieflyButNotWhenOffline() async {
        let options = LyricSourceOptions.Snapshot(enabled: [.lrclib, .netEase], mode: .smart, localFolder: "", localVersion: "")
        let calls = Calls()
        let lookup = fetcher([:], calls: calls)
        let first = await lookup.lookup(query, options: options)
        XCTAssertNil(first.result)
        XCTAssertFalse(first.offline)
        let again = await lookup.lookup(query, options: options)
        XCTAssertEqual(again.states[.lrclib], "No match a few minutes ago")
        var made = await calls.made.count
        XCTAssertEqual(made, 2)
        _ = await lookup.lookup(query, force: true, options: options)
        made = await calls.made.count
        XCTAssertEqual(made, 4)

        let offlineCalls = Calls()
        let offline = fetcher([:], calls: offlineCalls, online: false)
        let lost = await offline.lookup(query, options: options)
        XCTAssertTrue(lost.offline)
        _ = await offline.lookup(query, options: options)
        made = await offlineCalls.made.count
        XCTAssertEqual(made, 4)
        XCTAssertTrue(LyricMissCache(lifetime: 600).answered("key").sources.isEmpty)
        let expiring = LyricMissCache(lifetime: 600)
        expiring.insert("key", sources: [.lrclib], now: Date(timeIntervalSince1970: 0))
        expiring.insert("key", sources: [.netEase], now: Date(timeIntervalSince1970: 300))
        XCTAssertEqual(expiring.answered("key", now: Date(timeIntervalSince1970: 599)).sources, [.lrclib, .netEase])
        // Each answer expires on its own clock.
        XCTAssertEqual(expiring.answered("key", now: Date(timeIntervalSince1970: 601)).sources, [.netEase])
        XCTAssertTrue(expiring.answered("key", now: Date(timeIntervalSince1970: 901)).sources.isEmpty)
    }

    private final class FakeClock: @unchecked Sendable {
        private let lock = NSLock()
        private var time = Date(timeIntervalSince1970: 1_000_000)
        var now: Date { lock.withLock { time } }
        func advance(_ seconds: TimeInterval) { lock.withLock { time += seconds } }
    }

    func testAReplayDoesNotRenewAnOlderMiss() async {
        struct Busy: Error {}
        let options = LyricSourceOptions.Snapshot(enabled: [.lrclib, .netEase], mode: .smart, localFolder: "", localVersion: "")
        let clock = FakeClock(), calls = Calls()
        // LRCLIB has nothing; NetEase keeps failing, so it is asked every time.
        let lookup = LyricsFetcher(cacheFolder: folder, timing: fast, misses: LyricMissCache(lifetime: 600, clock: { clock.now }),
                                   isOnline: { true }) { id, _, _ in
            await calls.add(id)
            if id == .netEase { throw Busy() }
            return nil
        }
        _ = await lookup.lookup(query, options: options)
        clock.advance(400)
        let replay = await lookup.lookup(query, options: options)
        XCTAssertEqual(replay.states[.lrclib], "No match a few minutes ago")
        // Ten minutes after its own answer LRCLIB is asked again, however
        // often the song was replayed meanwhile.
        clock.advance(400)
        let later = await lookup.lookup(query, options: options)
        XCTAssertEqual(later.states[.lrclib], "No usable match / unavailable")
        let lrclib = await calls.count(.lrclib), netEase = await calls.count(.netEase)
        XCTAssertEqual(lrclib, 2)
        XCTAssertEqual(netEase, 3)
    }

    func testCloseMatchesAreListedAgainOnAReplayWithoutAsking() async {
        let options = LyricSourceOptions.Snapshot(enabled: [.lrclib], mode: .smart, localFolder: "", localVersion: "")
        let calls = Calls()
        // Another edit, six seconds longer than the recording playing.
        let lookup = fetcher([.lrclib: (0, candidate(.lrclib, tier: 1, duration: 186))], calls: calls)
        let first = await lookup.lookup(query, options: options)
        XCTAssertNil(first.result)
        XCTAssertEqual(first.rejected.map(\.reason), ["Rejected: duration 3:06 vs 3:00"])
        let replay = await lookup.lookup(query, options: options)
        XCTAssertEqual(replay.rejected.map(\.reason), ["Rejected: duration 3:06 vs 3:00"])
        XCTAssertEqual(replay.states[.lrclib], "Rejected: duration 3:06 vs 3:00")
        let asked = await calls.count(.lrclib)
        XCTAssertEqual(asked, 1)
    }

    func testOnlySourcesThatAnsweredAreSkippedOnAReplay() async {
        struct Busy: Error {}
        let options = LyricSourceOptions.Snapshot(enabled: [.lrclib, .netEase], mode: .smart, localFolder: "", localVersion: "")
        let calls = Calls()
        let lookup = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }) { id, _, _ in
            await calls.add(id)
            if id == .lrclib { throw Busy() }
            return nil
        }
        _ = await lookup.lookup(query, options: options)
        let replay = await lookup.lookup(query, options: options)
        XCTAssertEqual(replay.states[.netEase], "No match a few minutes ago")
        let lrclib = await calls.count(.lrclib), netEase = await calls.count(.netEase)
        XCTAssertEqual(lrclib, 2)
        XCTAssertEqual(netEase, 1)
    }

    func testStreamsAndSavedChoicesNeedNoLookup() async throws {
        let options = LyricSourceOptions.Snapshot(enabled: [.lrclib], mode: .smart, localFolder: "", localVersion: "")
        let calls = Calls()
        let lookup = fetcher([.lrclib: (0, candidate(.lrclib, tier: 1))], calls: calls)
        let stream = await lookup.lookup(LyricQuery(title: "Radio One", artist: "", album: nil, duration: 0), options: options)
        XCTAssertTrue(stream.skippedStream)
        XCTAssertNil(stream.result)
        XCTAssertTrue(SavedLyrics.save(candidate(.netEase, tier: 3).result, key: query.recordingKey, pin: true, in: folder))
        let pinned = await lookup.lookup(query, options: options)
        XCTAssertTrue(pinned.pinned)
        XCTAssertEqual(source(of: pinned.result), LyricSourceID.netEase.title)
        let made = await calls.made.count
        XCTAssertEqual(made, 0)
    }

    func testEditingTheTracksLocalFileReReadsOnlyThatSource() async throws {
        let lyrics = folder.appendingPathComponent("Lyrimuse", isDirectory: true)
        try FileManager.default.createDirectory(at: lyrics, withIntermediateDirectories: true)
        let options = LyricSourceOptions.Snapshot(enabled: [.lyrimuse, .lrclib], mode: .smart, localFolder: lyrics.path, localVersion: "")
        let calls = Calls(), plain = candidate(.lrclib, tier: 0)
        // Network sources are stubbed; the local source is read for real.
        let lookup = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }) { id, query, path in
            await calls.add(id)
            if id == .lrclib { return plain }
            return try LocalLyricBridge.candidate(query, folder: path)
        }
        let first = await lookup.lookup(query, options: options)
        XCTAssertEqual(source(of: first.result), LyricSourceID.lrclib.title)
        let unrelated = lyrics.appendingPathComponent("Another Tune.lrc")
        try "[ti:Another Tune]\n[ar:Our Artist]\n[00:01.00]Elsewhere".write(to: unrelated, atomically: false, encoding: .utf8)
        let ignored = await lookup.refreshLocal(query, previous: first, options: options)
        XCTAssertNil(ignored)
        let file = lyrics.appendingPathComponent("Our Artist - Our Song.lrc")
        try "[ti:Our Song]\n[ar:Our Artist]\n[length:03:00]\n[00:01.00]Sing along\n[00:04.00]Next line".write(to: file, atomically: false, encoding: .utf8)
        let update = await lookup.refreshLocal(query, previous: first, options: options)
        XCTAssertEqual(update?.revision, first.revision + 1)
        XCTAssertTrue(update?.candidates.first.map { $0.source == .lyrimuse && $0.duration == 180 } ?? false)
        if case .synced(let lines, _)? = update?.result { XCTAssertEqual(lines.count, 2) } else { XCTFail("Local lyrics should show") }
        let made = await calls.count(.lrclib)
        XCTAssertEqual(made, 1)
    }

    func testResultsThatDifferOnlyInTranslationsAreNotTheSame() {
        let provenance = LyricsFetcher.Provenance(precision: "Line-synced", source: "Lyrimuse local files")
        let line = LyricsFetcher.makeLine(text: "Sing along", start: 1, end: 3)
        var translated = line
        translated.translation = "Canta conmigo"
        var timedByStart = line
        timedByStart.endFromNextLine = true
        XCTAssertTrue(LyricsFetcher.sameResult(.synced([line], provenance), .synced([line], provenance)))
        XCTAssertFalse(LyricsFetcher.sameResult(.synced([line], provenance), .synced([translated], provenance)))
        XCTAssertFalse(LyricsFetcher.sameResult(.synced([line], provenance), .synced([timedByStart], provenance)))
    }

    func testAnEditThatOnlyAddsTranslationsReplacesTheLines() async throws {
        let lyrics = folder.appendingPathComponent("Translated", isDirectory: true)
        try FileManager.default.createDirectory(at: lyrics, withIntermediateDirectories: true)
        let options = LyricSourceOptions.Snapshot(enabled: [.lyrimuse], mode: .smart, localFolder: lyrics.path, localVersion: "")
        let lookup = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }) { _, query, path in
            try LocalLyricBridge.candidate(query, folder: path)
        }
        let file = lyrics.appendingPathComponent("Our Artist - Our Song.lrc")
        let header = "[ti:Our Song]\n[ar:Our Artist]\n[length:03:00]\n"
        try (header + "[00:01.00]Sing along\n[00:04.00]Next line").write(to: file, atomically: false, encoding: .utf8)
        let first = await lookup.lookup(query, options: options)
        XCTAssertEqual(first.revision, 1)
        // The same lines, each now with a translation at its timestamp.
        try (header + "[00:01.00]Sing along\n[00:01.00]Canta conmigo\n[00:04.00]Next line\n[00:04.00]La siguiente")
            .write(to: file, atomically: false, encoding: .utf8)
        let update = await lookup.refreshLocal(query, previous: first, options: options)
        XCTAssertEqual(update?.revision, first.revision + 1)
        guard case .synced(let lines, _)? = update?.result else { return XCTFail("The edited file should show") }
        XCTAssertEqual(lines.map(\.translation), ["Canta conmigo", "La siguiente"])
    }

    func testFingerprintCoversOnlyTheTracksFilesIncludingEditsInPlace() throws {
        let lyrics = folder.appendingPathComponent("Prints", isDirectory: true)
        try FileManager.default.createDirectory(at: lyrics, withIntermediateDirectories: true)
        let own = lyrics.appendingPathComponent("Our Song.lrc")
        try "[00:01.00]One".write(to: own, atomically: false, encoding: .utf8)
        let before = LocalLyricBridge.fingerprint(query, folder: lyrics.path)
        try "[00:01.00]Other".write(to: lyrics.appendingPathComponent("Different.lrc"), atomically: false, encoding: .utf8)
        XCTAssertEqual(LocalLyricBridge.fingerprint(query, folder: lyrics.path), before)
        let handle = try FileHandle(forWritingTo: own)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data("\n[00:02.00]Two".utf8))
        try handle.close()
        let edited = LocalLyricBridge.fingerprint(query, folder: lyrics.path)
        XCTAssertNotEqual(edited, before)
        try "<tt/>".write(to: lyrics.appendingPathComponent("Our Song.ttml"), atomically: false, encoding: .utf8)
        XCTAssertNotEqual(LocalLyricBridge.fingerprint(query, folder: lyrics.path), edited)
    }
}
