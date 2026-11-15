import XCTest
@testable import VersoCustom

/// How lyrics are weighed beyond their sources' leads: word stamps a tool
/// spread over each line rank as line timing, and a timeline shifted to
/// another recording ranks after those that agree, and the leading text
/// timed by the line takes another match's word timing.
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

    // MARK: - Merging text and timing

    /// QQ's word-timed lines (the donor), and Musixmatch's text for them, timed
    /// by the line 0.1 s later: lower-cased from line 3, punctuation dropped
    /// from line 5, an ad-lib "(oh)" on line 4, different words on line 6,
    /// and a translation on line 2.
    func mergePair() -> (donor: LyricCandidate, recipient: LyricCandidate, donorLines: [Line], recipientLines: [Line]) {
        let donorLines = Self.lines(LyricRankingFixtures.qrcLatin)
        var recipientLines: [Line] = donorLines.enumerated().map { index, line in
            var text = line.text
            if index >= 3 { text = text.lowercased() }
            if index >= 5 { text = text.filter { !",.?!\"".contains($0) } }
            if index == 4 { text = text.split(separator: " ").enumerated().map { $0 == 2 ? "(oh) " + $1 : String($1) }.joined(separator: " ") }
            if index == 6 { text = "Entirely other words sung here instead" }
            let end = index + 1 < donorLines.count ? donorLines[index + 1].start + 0.1 : line.end
            var timed = LyricsFetcher.makeLine(text: text, start: line.start + 0.1, end: end)
            timed.endFromNextLine = true
            return timed
        }
        recipientLines[2].translation = "A translation"
        recipientLines[2].romanization = "a reading"
        return (candidate(.qqMusic, donorLines, provenance: "QQ Music"), candidate(.musixmatch, recipientLines, provenance: "Musixmatch"),
                donorLines, recipientLines)
    }

    func testLineTimedLeadingTextTakesWordTimingWhereItAligns() throws {
        let pair = mergePair()
        for mode in LyricSourceOptions.Mode.allCases {
            let ranked = LyricsFetcher.screen([pair.donor, pair.recipient], query: query,
                                              options: options([.qqMusic, .musixmatch], mode)).accepted
            let top = try XCTUnwrap(ranked.first)
            XCTAssertEqual(top.source, .musixmatch, "\(mode)")
            guard case .synced(let lines, let provenance) = top.result else { return XCTFail("not synced") }
            XCTAssertEqual(provenance.source, "Musixmatch text · QQ Music word timing")
            XCTAssertEqual(top.provenance.precision, "Mixed timing")
            XCTAssertEqual(LyricsFetcher.rankedTiming(top.result), 2)
            XCTAssertEqual(LyricsFetcher.timingDonor(top), "QQ Music")
            XCTAssertFalse(LyricsFetcher.isAppleRelay(top))
            // The text, line times, readings and translations are Musixmatch's own.
            XCTAssertEqual(lines.map(\.text), pair.recipientLines.map(\.text))
            XCTAssertEqual(lines.map(\.start), pair.recipientLines.map(\.start))
            XCTAssertEqual(lines[2].translation, "A translation")
            XCTAssertEqual(lines[2].romanization, "a reading")
            // Aligned lines carry QQ's word times; the line with other words keeps line timing.
            XCTAssertTrue(lines[3].wordTimed)
            XCTAssertEqual(lines[3].words.map(\.start).dropFirst(), pair.donorLines[3].words.map(\.start).dropFirst())
            XCTAssertTrue(lines[5].wordTimed, "punctuation dropped")
            XCTAssertFalse(lines[6].wordTimed, "other words")
            XCTAssertEqual(lines[6].words, pair.recipientLines[6].words)
            XCTAssertGreaterThan(lines.filter(\.wordTimed).count, lines.count * 3 / 4)
        }
    }

    func testAnAdLibKeepsItsBracketsForBackingVocals() throws {
        let pair = mergePair()
        let top = try XCTUnwrap(LyricsFetcher.screen([pair.donor, pair.recipient], query: query, options: options([.musixmatch, .qqMusic])).accepted.first)
        guard case .synced(let lines, _) = top.result else { return XCTFail("not synced") }
        XCTAssertTrue(lines[4].wordTimed)
        XCTAssertTrue(LyricsFetcher.validWordTiming(lines[4]))
        let split = BackingVocals.split(lines[4].words, glue: Array(repeating: false, count: lines[4].words.count))
        XCTAssertEqual(split.backing.map(\.text), ["oh"])
        XCTAssertEqual(split.main.count, lines[4].words.count - 1)
    }

    func testUnitsCoverTheLineInEveryScript() {
        XCTAssertEqual(LyricsFetcher.mergeUnits("Don't (oh) stop, now!").map(\.text), ["Don't", "(oh)", "stop,", "now!"])
        XCTAssertEqual(LyricsFetcher.mergeUnits("Don't (oh) stop, now!").map(\.key), ["dont", "oh", "stop", "now"])
        XCTAssertEqual(LyricsFetcher.mergeUnits("故事的小黄花（Yeah）").map(\.text), ["故", "事", "的", "小", "黄", "花", "（Yeah）"])
        XCTAssertEqual(LyricsFetcher.mergeUnits("夜に駆ける、君と").map(\.text), ["夜", "に", "駆", "け", "る、", "君", "と"])
        XCTAssertEqual(LyricsFetcher.mergeUnits("이 밤 그날의").map(\.text), ["이", "밤", "그날의"])
        XCTAssertTrue(LyricsFetcher.sameMergeWord("colour", "color"))
        XCTAssertTrue(LyricsFetcher.sameMergeWord("thinkin", "thinking"))
        XCTAssertFalse(LyricsFetcher.sameMergeWord("love", "live"))
    }

    func testMergesIntoChineseTextFromKugou() throws {
        let donorLines = Self.lines(LyricRankingFixtures.krcChinese)
        let recipientLines = donorLines.map { line -> Line in
            var timed = LyricsFetcher.makeLine(text: line.text, start: line.start, end: line.end)
            timed.endFromNextLine = true
            return timed
        }
        let ranked = LyricsFetcher.screen([candidate(.kugou, donorLines, provenance: "Kugou Music"),
                                           candidate(.appleMusicCache, recipientLines, provenance: "Music")],
                                          query: query, options: options([.appleMusicCache, .kugou])).accepted
        XCTAssertEqual(ranked.first?.source, .appleMusicCache)
        XCTAssertEqual(LyricsFetcher.timingDonor(ranked[0]), "Kugou Music")
        XCTAssertEqual(LyricsFetcher.rankedTiming(ranked[0].result), 2)
    }

    func testNoMergeAcrossScriptsTranslationsOrTimelines() {
        let pair = mergePair()
        let order = options([.musixmatch, .qqMusic, .kugou])
        func merged(_ donor: LyricCandidate, _ recipient: LyricCandidate? = nil) -> Bool {
            LyricsFetcher.screen([donor, recipient ?? pair.recipient], query: query, options: order).accepted
                .contains { LyricsFetcher.timingDonor($0) != nil }
        }
        XCTAssertTrue(merged(pair.donor))
        // A translation timed alike: the words never pair.
        let translated = pair.donorLines.enumerated().map { index, line -> Line in
            var copy = line
            copy.words = line.words.enumerated().map { Word(text: "tr\(index)x\($0)", start: $1.start, end: $1.end) }
            copy.text = copy.words.map(\.text).joined(separator: " ")
            return copy
        }
        XCTAssertFalse(merged(candidate(.qqMusic, translated)))
        // Another script: Korean word timing never lends to Latin text, nor Latin to Korean.
        let korean = Self.lines(LyricRankingFixtures.qrcKorean)
        XCTAssertFalse(merged(candidate(.qqMusic, korean)))
        let koreanText = korean.map { LyricsFetcher.makeLine(text: $0.text, start: $0.start, end: $0.end) }
        XCTAssertFalse(merged(pair.donor, candidate(.musixmatch, koreanText)))
        // Timed a second later: another master.
        let late = pair.donorLines.map { line -> Line in
            var copy = line
            copy.start += 1; copy.end += 1
            copy.words = line.words.map { Word(text: $0.text, start: $0.start + 1, end: $0.end + 1) }
            return copy
        }
        XCTAssertFalse(merged(candidate(.qqMusic, late)))
        // Word stamps a tool spread are no timing to lend.
        XCTAssertFalse(merged(candidate(.kugou, Self.spread(pair.donorLines, equal: true))))
        // Another version's word timing is not this recording's.
        var live = pair.donor
        live.title = "Our Song (Live)"
        XCTAssertFalse(merged(live))
    }

    func testOnlyALeadingSourceTakesTiming() {
        // NetEase's line timing leads nothing: QQ's own word timing shows, unmerged.
        let pair = mergePair()
        var netEase = pair.recipient
        netEase.source = .netEase
        let ranked = LyricsFetcher.screen([pair.donor, netEase], query: query, options: options([.netEase, .qqMusic])).accepted
        XCTAssertEqual(ranked.first?.source, .qqMusic)
        XCTAssertTrue(ranked.allSatisfy { LyricsFetcher.timingDonor($0) == nil })
    }

    func testEarlyStoppingKeepsSourcesThatCouldLendTiming() throws {
        let pair = mergePair()
        let order: [LyricSourceID] = [.musixmatch, .qqMusic, .kugou, .vibe]
        let options = options(order, .priority)
        // QQ's words do not pair with Musixmatch's text here, so QQ's word timing shows.
        let unrelated = pair.recipientLines.enumerated().map { index, line in
            LyricsFetcher.makeLine(text: index % 2 == 0 ? line.text : "Different words for line \(index) here", start: line.start, end: line.end)
        }
        var found = LyricsFetcher.screen([pair.donor, candidate(.musixmatch, unrelated)], query: query, options: options).accepted
        let qq = try XCTUnwrap(found.first)
        XCTAssertEqual(qq.source, .qqMusic)
        // Kugou cannot beat QQ, but its word timing might still pair with Musixmatch's text; VIBE has none.
        XCTAssertFalse(LyricsFetcher.couldBeat(qq, from: .kugou, query: query, options: options))
        XCTAssertTrue(LyricsFetcher.matters(.kugou, best: qq, onlineBest: qq, query: query, options: options, among: found))
        XCTAssertFalse(LyricsFetcher.isSettled(qq, pending: [.kugou], query: query, options: options, among: found))
        XCTAssertFalse(LyricsFetcher.matters(.vibe, best: qq, onlineBest: qq, query: query, options: options, among: found))
        // Once merged, Musixmatch leads and Kugou is not needed.
        found = LyricsFetcher.screen([pair.donor, pair.recipient], query: query, options: options).accepted
        let merged = try XCTUnwrap(found.first)
        XCTAssertEqual(merged.source, .musixmatch)
        XCTAssertFalse(LyricsFetcher.matters(.kugou, best: merged, onlineBest: merged, query: query, options: options, among: found))
        XCTAssertTrue(LyricsFetcher.isSettled(merged, pending: [.kugou], query: query, options: options, among: found))
    }

    func testTheMergeReplacesTheSameSourceOnScreen() throws {
        let pair = mergePair()
        let options = options([.musixmatch, .qqMusic, .kugou])
        var publication = LyricsFetcher.Publication(options: options, query: query)
        let first = LyricsFetcher.screen([pair.recipient], query: query, options: options).accepted
        XCTAssertEqual(publication.step(best: first.first, settled: false, graceOver: true, settleOver: false, latest: first), .publish)
        let later = LyricsFetcher.screen([pair.recipient, pair.donor], query: query, options: options).accepted
        XCTAssertEqual(publication.step(best: later.first, settled: true, graceOver: true, settleOver: false, latest: later), .publish)
        XCTAssertEqual(publication.shown.flatMap(LyricsFetcher.timingDonor), "QQ Music")
        // The same lyrics again change nothing.
        XCTAssertEqual(publication.step(best: later.first, settled: true, graceOver: true, settleOver: true, latest: later), .wait)
        // A donor that lends more lines once it answers replaces the first merge, so what shows is what is listed.
        let fuller = candidate(.kugou, pair.donorLines, provenance: "Kugou Music")
        var sparse = pair.donor
        if case .synced(let lines, let p) = sparse.result {
            sparse.result = .synced(lines.enumerated().map { index, line in
                guard index % 4 == 1 else { return line }
                var copy = line
                copy.words = line.words.map { Word(text: $0.text + "x", start: $0.start, end: $0.end) }
                copy.text = copy.words.map(\.text).joined(separator: " ")
                return copy
            }, p)
        }
        var stepwise = LyricsFetcher.Publication(options: options, query: query)
        let early = LyricsFetcher.screen([pair.recipient, sparse], query: query, options: options).accepted
        XCTAssertEqual(stepwise.step(best: early.first, settled: false, graceOver: true, settleOver: false, latest: early), .publish)
        let full = LyricsFetcher.screen([pair.recipient, sparse, fuller], query: query, options: options).accepted
        XCTAssertEqual(LyricsFetcher.timingDonor(full[0]), "Kugou Music")
        XCTAssertEqual(stepwise.step(best: full.first, settled: true, graceOver: true, settleOver: true, latest: full), .publish)
        XCTAssertTrue(LyricsFetcher.sameResult(stepwise.shown?.result, full[0].result))
    }

    func testTheLookupShowsAndSavesTheMerge() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("merge-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let pair = mergePair()
        let answers: [LyricSourceID: LyricCandidate] = [.musixmatch: pair.recipient, .qqMusic: pair.donor]
        let fetcher = LyricsFetcher(cacheFolder: folder, timing: .init(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.3, lrclibLimit: 3, sourceLimit: 3),
                                    isOnline: { true }) { id, _, _ in answers[id] }
        let report = await fetcher.lookup(query, options: options([.musixmatch, .qqMusic]))
        guard case .synced(_, let provenance)? = report.result else { return XCTFail("nothing shown") }
        XCTAssertEqual(provenance.source, "Musixmatch text · QQ Music word timing")
        XCTAssertTrue(report.states[.musixmatch]?.contains("word timing from QQ Music") == true, report.states[.musixmatch] ?? "")
        // Saved, the merge is what a replay shows.
        let again = await LyricsFetcher(cacheFolder: folder, isOnline: { true }) { _, _, _ in nil }
            .lookup(query, options: options([.musixmatch, .qqMusic]))
        XCTAssertTrue(LyricsFetcher.sameResult(again.result, report.result))
    }

    func testAMergedAppleRelayKeepsItsLead() {
        XCTAssertTrue(LyricsFetcher.isAppleRelay(provenanceSource: LyricsFetcher.textSource(
            ofProvenance: LyricsFetcher.mergedProvenanceSource(text: "Apple via Lyrics+", timing: "QQ Music"))))
        var relay = mergePair().recipient
        relay.source = .lyricsPlus
        if case .synced(let lines, _) = relay.result { relay.result = .synced(lines, .init(precision: "", source: "Apple via Lyrics+")) }
        let ranked = LyricsFetcher.screen([mergePair().donor, relay], query: query, options: options([.qqMusic, .lyricsPlus])).accepted
        XCTAssertEqual(ranked.first?.source, .lyricsPlus)
        XCTAssertTrue(LyricsFetcher.isAppleRelay(ranked[0]))
        XCTAssertEqual(LyricsFetcher.traceLead(ranked[0]), 1)
    }

    // MARK: - Automatic lookups and searches

    /// An automatic choice is saved for two weeks; a search asks again. A
    /// source that could have beaten the choice but gave no answer (timed
    /// out, failed, paused) leaves it unsaved, so automatic lookups do not
    /// keep showing less than a search finds.
    func testAChoiceASilentBetterSourceMightHaveBeatenIsNotSaved() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("unsaved-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let qq = candidate(.qqMusic, Self.lines(LyricRankingFixtures.qrcLatin), provenance: "QQ Music")
        let musixmatch = candidate(.musixmatch, Self.lines(LyricRankingFixtures.qrcLatin), provenance: "Musixmatch")
        let timing = LyricsFetcher.Timing(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.3, lrclibLimit: 3, sourceLimit: 3)
        let order = options([.musixmatch, .qqMusic, .sodaMusic])
        struct Down: Error {}
        // Musixmatch fails and Soda Music fails: QQ shows, but is not saved (Musixmatch leads it).
        let first = await LyricsFetcher(cacheFolder: folder, timing: timing, isOnline: { true }) { id, _, _ in
            if id == .qqMusic { return qq }
            throw Down()
        }.lookup(query, options: order)
        XCTAssertEqual(first.candidates.first?.source, .qqMusic)
        // The next automatic lookup asks again and finds Musixmatch's.
        let second = await LyricsFetcher(cacheFolder: folder, timing: timing, isOnline: { true }) { id, _, _ in
            id == .qqMusic ? qq : id == .musixmatch ? musixmatch : nil
        }.lookup(query, options: order)
        XCTAssertFalse(second.cached)
        guard case .synced(_, let provenance)? = second.result else { return XCTFail("nothing shown") }
        XCTAssertEqual(provenance.source, "Musixmatch")
        // Musixmatch answered this time, so its choice is saved and replayed without asking.
        let third = await LyricsFetcher(cacheFolder: folder, timing: timing, isOnline: { true }) { _, _, _ in nil }.lookup(query, options: order)
        XCTAssertTrue(third.cached)
        XCTAssertTrue(LyricsFetcher.sameResult(third.result, second.result))
    }

    func testASourceThatCouldOnlyTieDoesNotHoldBackSaving() async {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("tie-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let qq = candidate(.qqMusic, Self.lines(LyricRankingFixtures.qrcLatin), provenance: "QQ Music")
        let timing = LyricsFetcher.Timing(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.3, lrclibLimit: 3, sourceLimit: 3)
        let order = options([.qqMusic, .sodaMusic], .priority)
        struct Down: Error {}
        _ = await LyricsFetcher(cacheFolder: folder, timing: timing, isOnline: { true }) { id, _, _ in
            if id == .qqMusic { return qq }
            throw Down()
        }.lookup(query, options: order)
        let again = await LyricsFetcher(cacheFolder: folder, timing: timing, isOnline: { true }) { _, _, _ in nil }.lookup(query, options: order)
        XCTAssertTrue(again.cached, "Soda Music leads no better than QQ: the choice stands")
        XCTAssertTrue(LyricsFetcher.couldOutlead(qq, from: .musixmatch, query: query))
        XCTAssertFalse(LyricsFetcher.couldOutlead(qq, from: .sodaMusic, query: query))
    }
}
