import XCTest
@testable import VersoCustom

/// How lyrics are weighed beyond their sources' leads: word stamps a tool
/// spread over each line rank as line timing, and a timeline shifted to
/// another recording ranks after those that agree.
final class LyricRankingQualityTests: XCTestCase {
    typealias Line = LyricsFetcher.Line
    typealias Word = LyricsFetcher.Word

    let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 240)

    func options(_ order: [LyricSourceID], _ mode: LyricSourceOptions.Mode = .smart) -> LyricSourceOptions.Snapshot {
        .init(enabled: order, mode: mode, localFolder: "", localVersion: "")
    }

    /// Lines from a `LyricRankingFixtures` sample.
    static func lines(_ fixture: String) -> [Line] {
        fixture.split(separator: "\n").compactMap { row -> Line? in
            if let bar = row.firstIndex(of: "|") {
                let times = row[..<bar].split(separator: ",").compactMap { Double($0) }
                var text = "", words: [Word] = []
                for token in row[row.index(after: bar)...].split(separator: " ") {
                    let glued = token.hasPrefix("+")
                    let body = glued ? token.dropFirst() : token[...]
                    guard let at = body.lastIndex(of: "@") else { return nil }
                    let span = body[body.index(after: at)...].split(separator: "-").compactMap { Double($0) }
                    let word = String(body[..<at])
                    text += (text.isEmpty || glued ? "" : " ") + word
                    words.append(Word(text: word, start: span[0], end: span[1]))
                }
                var line = LyricsFetcher.makeLine(text: text, start: times[0], end: times[1])
                line.words = words
                line.wordTimed = true
                return line
            }
            guard let semi = row.firstIndex(of: ";") else { return nil }
            let times = row[..<semi].split(separator: ",").compactMap { Double($0) }
            return LyricsFetcher.makeLine(text: row[row.index(after: semi)...].replacingOccurrences(of: "_", with: " "),
                                          start: times[0], end: times[1])
        }
    }

    /// The same lines with each word-timed line's words spread by a tool over
    /// the line: `equal` shares, or in proportion to their letters plus one.
    /// Rounded to centiseconds, as an LRC file keeps them.
    static func spread(_ lines: [Line], equal: Bool) -> [Line] {
        lines.map { line in
            guard line.wordTimed, let last = line.words.last else { return line }
            var copy = line
            let weights = line.words.map { equal ? 1 : Double($0.text.filter { $0.isLetter || $0.isNumber }.count + 1) }
            let total = weights.reduce(0, +), span = last.end - line.start
            var cursor = line.start
            copy.words = zip(line.words, weights).map { word, weight in
                let start = (cursor * 100).rounded() / 100
                cursor += span * weight / total
                return Word(text: word.text, start: start, end: min(line.end, (cursor * 100).rounded() / 100))
            }
            return copy
        }
    }

    func candidate(_ source: LyricSourceID, _ lines: [Line], provenance: String? = nil) -> LyricCandidate {
        .init(source: source, result: .synced(lines, .init(precision: LyricsFetcher.precision(lines), source: provenance ?? source.title)),
              title: query.title, artist: query.artist, duration: query.duration, score: 0)
    }

    static let genuine: [(String, String)] = [
        ("Apple TTML via Lyrics+", LyricRankingFixtures.appleTTMLLatin), ("QQ QRC", LyricRankingFixtures.qrcLatin),
        ("NetEase YRC", LyricRankingFixtures.yrcLatin), ("LRCLIB word", LyricRankingFixtures.lrclibWordLatin),
        ("PetitLyrics", LyricRankingFixtures.petitLatin), ("Kugou KRC", LyricRankingFixtures.krcLatin),
        ("Kugou KRC, Chinese", LyricRankingFixtures.krcChinese), ("AMLL TTML, Japanese", LyricRankingFixtures.ttmlJapanese),
        ("QQ QRC, Korean", LyricRankingFixtures.qrcKorean),
    ]

    // MARK: - Spread word timing

    func testGenuineWordTimingFromEveryServiceKeepsItsTier() {
        for (name, fixture) in Self.genuine {
            let lines = Self.lines(fixture)
            let result = LyricsFetcher.Result.synced(lines, .init(precision: "", source: name))
            XCTAssertEqual(LyricsFetcher.timingTier(result) >= 2, true, "\(name) parses as word timing")
            XCTAssertFalse(LyricsFetcher.hasEvenlySpreadWordTiming(lines), name)
            XCTAssertEqual(LyricsFetcher.rankedTiming(result), 2, name)
        }
    }

    func testWordsSpreadByAToolRankAsLineTiming() {
        for (name, fixture) in Self.genuine {
            for equal in [true, false] {
                let lines = Self.spread(Self.lines(fixture), equal: equal)
                let result = LyricsFetcher.Result.synced(lines, .init(precision: "", source: name))
                XCTAssertGreaterThanOrEqual(LyricsFetcher.timingTier(result), 2, "\(name) still looks word-timed")
                XCTAssertTrue(LyricsFetcher.hasEvenlySpreadWordTiming(lines), "\(name) spread \(equal ? "equally" : "by length")")
                XCTAssertEqual(LyricsFetcher.rankedTiming(result), 1, name)
                XCTAssertEqual(LyricTraceCandidate.timing(result), "spread")
            }
        }
    }

    func testAFewSpreadLinesDoNotDemoteAFile() {
        var lines = Self.lines(LyricRankingFixtures.qrcLatin)
        let spread = Self.spread(lines, equal: true)
        for index in lines.indices.prefix(lines.count / 3) { lines[index] = spread[index] }
        XCTAssertFalse(LyricsFetcher.hasEvenlySpreadWordTiming(lines))
    }

    func testSpreadWordTimingLosesToGenuineAndCouldBeatAgrees() {
        let order: [LyricSourceID] = [.lrclib, .qqMusic, .musixmatch, .vibe]
        let real = Self.lines(LyricRankingFixtures.qrcLatin)
        let fake = candidate(.lrclib, Self.spread(real, equal: false))
        let genuine = candidate(.qqMusic, real)
        for mode in LyricSourceOptions.Mode.allCases {
            let options = options(order, mode)
            XCTAssertEqual(LyricsFetcher.screen([fake, genuine], query: query, options: options).accepted.first?.source, .qqMusic)
            // A word-capable source could still beat it; a line-only source with a lower lead could not.
            XCTAssertTrue(LyricsFetcher.couldBeat(fake, from: .qqMusic, query: query, options: options))
            XCTAssertTrue(LyricsFetcher.couldBeat(fake, from: .musixmatch, query: query, options: options))
            XCTAssertFalse(LyricsFetcher.isSettled(fake, pending: [.qqMusic], query: query, options: options))
            // LRCLIB's genuine word timing leads QQ's, so QQ is not needed.
            XCTAssertFalse(LyricsFetcher.couldBeat(candidate(.lrclib, real), from: .qqMusic, query: query, options: options))
        }
        // Spread, it ranks with line timing: Musixmatch's line timing leads it.
        let musixmatch = candidate(.musixmatch, real.map { line in LyricsFetcher.makeLine(text: line.text, start: line.start, end: line.end) })
        XCTAssertEqual(LyricsFetcher.screen([fake, musixmatch], query: query, options: options(order)).accepted.first?.source, .musixmatch)
    }

    // MARK: - Shifted timeline

    static let verses = ["Morning comes around again", "Walking down the empty street", "Every light is turning green",
                         "I can hear you calling out", "Hold the line and wait for me", "Nothing ever stays the same",
                         "Count the stars above the town", "Never let the music fade"]

    /// Line-timed verses, every line `shift` seconds later, plus `jitter(i)`.
    func timed(_ source: LyricSourceID, shift: Double = 0, jitter: (Int) -> Double = { _ in 0 }) -> LyricCandidate {
        candidate(source, Self.verses.enumerated().map { index, text in
            let start = Double(index) * 4.5 + 10 + shift + jitter(index)
            return LyricsFetcher.makeLine(text: text, start: start, end: start + 4)
        })
    }

    func testATimelineOffFromTwoThatAgreeIsShifted() {
        let order: [LyricSourceID] = [.musixmatch, .netEase, .qqMusic]
        for mode in LyricSourceOptions.Mode.allCases {
            let screened = LyricsFetcher.screen([timed(.musixmatch, shift: 2.1, jitter: { $0 % 2 == 0 ? 0.1 : -0.1 }),
                                                 timed(.netEase), timed(.qqMusic, shift: 0.2)],
                                                query: query, options: options(order, mode)).accepted
            XCTAssertEqual(screened.map(\.source), [.netEase, .qqMusic, .musixmatch], "\(mode)")
            XCTAssertEqual(screened.last?.agreement.shift ?? 0, 2.0, accuracy: 0.15)
            XCTAssertNil(screened[0].agreement.shift)
            XCTAssertNil(screened[1].agreement.shift)
        }
    }

    func testTwoThatDisagreeCannotTellWhichIsShifted() {
        let order: [LyricSourceID] = [.musixmatch, .netEase, .qqMusic, .kugou]
        let pair = LyricsFetcher.screen([timed(.musixmatch, shift: 2.5), timed(.netEase)], query: query, options: options(order)).accepted
        XCTAssertEqual(pair.first?.source, .musixmatch)
        XCTAssertTrue(pair.allSatisfy { $0.agreement.shift == nil })
        // Two against two: no telling either.
        let even = LyricsFetcher.screen([timed(.musixmatch, shift: 2.5), timed(.kugou, shift: 2.4), timed(.netEase), timed(.qqMusic)],
                                        query: query, options: options(order)).accepted
        XCTAssertTrue(even.allSatisfy { $0.agreement.shift == nil })
        XCTAssertEqual(even.first?.source, .musixmatch)
    }

    func testAnUnsteadyOffsetIsNotAShift() {
        let order: [LyricSourceID] = [.musixmatch, .netEase, .qqMusic]
        // Off by two seconds at the median, but scattered from one to four: badly synced, not another master.
        let scattered = timed(.musixmatch, jitter: { [1, 4, 2, 3.5, 1.2, 2.1, 3.8, 1.5][$0] })
        let screened = LyricsFetcher.screen([scattered, timed(.netEase), timed(.qqMusic)], query: query, options: options(order)).accepted
        XCTAssertTrue(screened.allSatisfy { $0.agreement.shift == nil })
    }

    func testMusicsOwnTimingIsTheReference() {
        let order: [LyricSourceID] = [.appleMusicCache, .musixmatch, .netEase]
        let screened = LyricsFetcher.screen([timed(.appleMusicCache), timed(.musixmatch, shift: -1.8)], query: query, options: options(order)).accepted
        XCTAssertEqual(screened.first?.source, .appleMusicCache)
        XCTAssertEqual(screened.last?.agreement.shift ?? 0, -1.8, accuracy: 0.01)
        // Music's own is never the shifted one, whatever the others say.
        let outvoted = LyricsFetcher.screen([timed(.appleMusicCache, shift: 3), timed(.musixmatch), timed(.netEase, shift: 0.1)],
                                            query: query, options: options(order)).accepted
        XCTAssertNil(outvoted.first { $0.source == .appleMusicCache }?.agreement.shift)
        XCTAssertNotNil(outvoted.first { $0.source == .musixmatch }?.agreement.shift)
    }

    func testAPendingSourceThatCouldShowTheShiftStillMatters() {
        let order: [LyricSourceID] = [.musixmatch, .netEase, .qqMusic, .vibe]
        let options = options(order)
        let found = LyricsFetcher.screen([timed(.musixmatch), timed(.netEase, shift: 2)], query: query, options: options).accepted
        let best = found[0]
        XCTAssertEqual(best.source, .musixmatch)
        // VIBE (line timing only) cannot outrank Musixmatch, but agreeing with NetEase it would show Musixmatch shifted.
        XCTAssertFalse(LyricsFetcher.couldBeat(best, from: .vibe, query: query, options: options))
        XCTAssertTrue(LyricsFetcher.matters(.vibe, best: best, onlineBest: best, query: query, options: options, among: found))
        // With another service backing Musixmatch's timing, nothing could.
        let backed = LyricsFetcher.screen([timed(.musixmatch), timed(.netEase, shift: 2), timed(.qqMusic, shift: 0.1)],
                                          query: query, options: options).accepted
        XCTAssertFalse(LyricsFetcher.matters(.vibe, best: backed[0], onlineBest: backed[0], query: query, options: options, among: backed))
    }

    func testTheSourceListSaysWhyAShiftedMatchRanksLower() async {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("shift-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let answers: [LyricSourceID: LyricCandidate] = [.musixmatch: timed(.musixmatch, shift: 2), .netEase: timed(.netEase),
                                                        .qqMusic: timed(.qqMusic, shift: 0.3)]
        let fetcher = LyricsFetcher(cacheFolder: folder, timing: .init(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.3, lrclibLimit: 3, sourceLimit: 3),
                                    isOnline: { true }) { id, _, _ in answers[id] }
        let report = await fetcher.lookup(query, force: true, options: options([.musixmatch, .netEase, .qqMusic]))
        XCTAssertEqual(report.candidates.first?.source, .netEase)
        XCTAssertTrue(report.states[.musixmatch]?.contains("timed to another recording (1.8 s later than the others)") == true,
                      report.states[.musixmatch] ?? "")
    }
}
