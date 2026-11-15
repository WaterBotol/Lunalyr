import XCTest
@testable import VersoCustom

/// How lyrics are weighed beyond their sources' leads: word stamps a tool
/// spread over each line rank as line timing.
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
}
