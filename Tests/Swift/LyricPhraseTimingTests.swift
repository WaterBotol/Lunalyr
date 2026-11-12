import XCTest
@testable import VersoCustom

/// A whole line, or a few long phrases, timed as "words" is line timing
/// (`LyricsFetcher.isPhraseTimed`). The fixtures are the Windows port's, so
/// both apps judge the same lyrics the same way.
final class LyricPhraseTimingTests: XCTestCase {
    private typealias Token = (text: String, start: Double, end: Double)

    /// A line from 10 to 14 s, word-timed by `tokens`.
    private func line(_ text: String, _ tokens: [Token]) -> LyricsFetcher.Line {
        var line = LyricsFetcher.makeLine(text: text, start: 10, end: 14)
        line.wordTimed = true
        line.words = tokens.map { .init(text: $0.text, start: $0.start, end: $0.end) }
        return line
    }
    private func result(_ lines: [LyricsFetcher.Line]) -> LyricsFetcher.Result {
        .synced(lines, .init(precision: "fixture", source: "LRCLIB"))
    }
    private let dreams = "dreams are made of this"
    private var wholeLine: LyricsFetcher.Line { line(dreams, [(dreams, 10, 14)]) }
    private var perWord: LyricsFetcher.Line { line("made of this", [("made", 10, 11), ("of", 11, 12), ("this", 12, 13)]) }

    // MARK: - Per line

    func testOneTokenForASpacedLineIsNotWordTiming() {
        XCTAssertFalse(LyricsFetcher.validWordTiming(wholeLine))
    }
    func testOneTokenForAnUnspacedLineIsNotWordTiming() {
        XCTAssertFalse(LyricsFetcher.validWordTiming(line("故事的小黄花", [("故事的小黄花", 10, 14)])))
    }
    func testARealOneWordLineStillCounts() {
        XCTAssertTrue(LyricsFetcher.validWordTiming(line("Yeah!", [("Yeah!", 10, 11)])))
        XCTAssertTrue(LyricsFetcher.validWordTiming(line("晴天", [("晴天", 10, 11)])))
    }
    func testOneTokenAWordIsWordTiming() {
        XCTAssertTrue(LyricsFetcher.validWordTiming(perWord))
    }
    func testLongUnspacedPhrasesAreNotWordTiming() {
        // 18 characters in 2 tokens: 9 a token, over 4.
        let text = "あの日の悲しみさえ あの日の苦しみさえ"
        XCTAssertFalse(LyricsFetcher.validWordTiming(line(text, [("あの日の悲しみさえ", 10, 12), ("あの日の苦しみさえ", 12, 14)])))
    }
    func testUnspacedSyllablesAreWordTiming() {
        XCTAssertTrue(LyricsFetcher.validWordTiming(line("夢ならば", [("夢", 10, 11), ("な", 11, 12), ("ら", 12, 13), ("ば", 13, 14)])))
    }
    func testSpacedPhraseThresholds() {
        // At most half as many tokens as words, over two words: phrases.
        XCTAssertTrue(LyricsFetcher.isPhraseTimed("I have been calling you", letters: "", tokens: 2))
        XCTAssertFalse(LyricsFetcher.isPhraseTimed("I have been calling you", letters: "", tokens: 3))
        XCTAssertFalse(LyricsFetcher.isPhraseTimed("in the", letters: "", tokens: 2))
        XCTAssertTrue(LyricsFetcher.isPhraseTimed("in the", letters: "", tokens: 1))
        XCTAssertFalse(LyricsFetcher.isPhraseTimed("anything", letters: "", tokens: 0))
    }

    // MARK: - Per result

    func testOnlyWholeLineTokensHaveNoWordTiming() {
        let value = result([wholeLine, line("who am I to disagree", [("who am I to disagree", 10, 14)])])
        XCTAssertFalse(LyricsFetcher.hasWordTiming(value))
        XCTAssertEqual(LyricsFetcher.rankedTiming(value), 1)
    }
    func testLemonOnLRCLIBIsLineSynced() {
        let lines = [line("夢ならばどれほどよかったでしょう", [("夢ならばどれほどよかったでしょう", 10, 14)]),
                     line("苦い", [("苦い", 10, 11)])]
        XCTAssertEqual(LyricsFetcher.timingTier(result(lines)), 1)
        XCTAssertEqual(LyricsFetcher.precision(lines), "Line-synced")
    }
    func testOneRealLineInTenIsNotEnoughEvidence() {
        // 周杰倫 "晴天" on LRCLIB: 1 real word-timed line of 10 sung.
        let phrase = "故事的小黃花從出生那年就飄著"
        let lines = Array(repeating: line(phrase, [(phrase, 10, 14)]), count: 9)
            + [line("ㄙㄡ ㄌㄚ ㄒ一", [("ㄙㄡ", 10, 11), ("ㄌㄚ", 11, 12), ("ㄒ一", 12, 13)])]
        XCTAssertTrue(LyricsFetcher.validWordTiming(lines[9]))
        XCTAssertEqual(LyricsFetcher.timingTier(result(lines)), 1)
        XCTAssertEqual(LyricsFetcher.precision(lines), "Line-synced")
    }
    func testAWholeLineBesideARealOneIsMixed() {
        XCTAssertEqual(LyricsFetcher.timingTier(result([wholeLine, perWord])), 2)
        XCTAssertEqual(LyricsFetcher.precision([wholeLine, perWord]), "Mixed timing")
    }

    // MARK: - LRCLIB Lyricsfile

    private func lyricsfile(_ rows: [(text: String, start: Int, end: Int, words: [(String, Int, Int)])]) -> String {
        var text = "lines:\n"
        for row in rows {
            text += "  - text: \"\(row.text)\"\n    start_ms: \(row.start)\n    end_ms: \(row.end)\n"
            guard !row.words.isEmpty else { continue }
            text += "    words:\n"
            for word in row.words { text += "      - text: \"\(word.0)\"\n        start_ms: \(word.1)\n        end_ms: \(word.2)\n" }
        }
        return text
    }
    private let verse = ["I have been calling you", "all night long and more", "under the summer moon", "nothing left to say now"]
    /// Each row's words split as `groups` (word counts per token).
    private func rows(_ groups: [Int]) -> [(text: String, start: Int, end: Int, words: [(String, Int, Int)])] {
        verse.enumerated().map { index, text in
            let words = text.split(separator: " ").map(String.init)
            let start = 10_000 + index * 5_000
            var tokens: [(String, Int, Int)] = [], at = 0, time = start
            for size in groups {
                let piece = words[at..<min(at + size, words.count)].joined(separator: " ")
                tokens.append((piece + (at + size < words.count ? " " : ""), time, time + 900 * size))
                time += 900 * size; at += size
            }
            return (text, start, start + 4_800, tokens)
        }
    }

    func testARealPerWordLyricsfileIsWordByWord() {
        let lines = LyricsFetcher.parseLyricsFile(lyricsfile(rows([1, 1, 1, 1, 1])))
        XCTAssertEqual(lines.count, 4)
        XCTAssertTrue(lines.allSatisfy(\.wordTimed))
        XCTAssertEqual(LyricsFetcher.precision(lines), "Word-by-word")
    }
    func testAPhraseChunkedLyricsfileIsLineSynced() {
        let lines = LyricsFetcher.parseLyricsFile(lyricsfile(rows([3, 2])))
        XCTAssertEqual(lines.count, 4)
        XCTAssertFalse(lines.contains(where: \.wordTimed))
        XCTAssertEqual(LyricsFetcher.precision(lines), "Line-synced")
        XCTAssertEqual(LyricsFetcher.timingTier(result(lines)), 1)
        // Words are estimated across the line, one a word.
        XCTAssertEqual(lines[0].words.count, 5)
    }
    func testAOneTokenPerLineLyricsfileIsLineSynced() {
        let lines = LyricsFetcher.parseLyricsFile(lyricsfile(rows([5])))
        XCTAssertFalse(lines.contains(where: \.wordTimed))
        XCTAssertEqual(LyricsFetcher.precision(lines), "Line-synced")
    }
    @MainActor func testPhraseTimedLinesAreShownWithEstimatedWords() {
        let shown = LyricsViewModel.presentation(of: result([wholeLine]), repairingGaps: false)
        XCTAssertFalse(shown.lines[0].wordTimed)
        XCTAssertEqual(shown.lines[0].words.count, 5)
        XCTAssertEqual(shown.provenance?.precision, "Line-synced")
    }

    // MARK: - Ranking

    /// 米津玄師 "Lemon" and 周杰倫 "晴天": LRCLIB's phrase rows outranked Kugou's real word timing.
    func testKugouWordTimingBeatsLRCLIBPhraseRows() {
        let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)
        let texts = ["夢ならばどれほどよかったでしょう", "未だにあなたのことを夢にみる", "忘れた物を取りに帰るように", "古びた思い出の埃を払う"]
        let phrase = texts.enumerated().map { index, text -> LyricsFetcher.Line in
            let start = Double(10 + index * 5)
            var line = LyricsFetcher.makeLine(text: text, start: start, end: start + 4)
            line.wordTimed = true
            line.words = [.init(text: text, start: start, end: start + 4)]
            return line
        }
        let syllables = texts.enumerated().map { index, text -> LyricsFetcher.Line in
            let start = Double(10 + index * 5), step = 4 / Double(text.count)
            var line = LyricsFetcher.makeLine(text: text, start: start, end: start + 4)
            line.wordTimed = true
            line.words = text.enumerated().map { i, c in .init(text: String(c), start: start + Double(i) * step, end: start + Double(i + 1) * step) }
            return line
        }
        let lrclib = LyricCandidate(source: .lrclib, result: .synced(phrase, .init(precision: LyricsFetcher.precision(phrase), source: "LRCLIB")),
                                    title: "Our Song", artist: "Our Artist", duration: 180, score: 0)
        let kugou = LyricCandidate(source: .kugou, result: .synced(syllables, .init(precision: LyricsFetcher.precision(syllables), source: "Kugou")),
                                   title: "Our Song", artist: "Our Artist", duration: 180, score: 0)
        XCTAssertEqual(lrclib.provenance.precision, "Line-synced")
        XCTAssertEqual(kugou.provenance.precision, "Word-by-word")
        for mode in LyricSourceOptions.Mode.allCases {
            for order in [[LyricSourceID.lrclib, .kugou], [.kugou, .lrclib]] {
                let options = LyricSourceOptions.Snapshot(enabled: order, mode: mode, localFolder: "", localVersion: "")
                XCTAssertEqual(LyricsFetcher.choose([lrclib, kugou], query: query, options: options)?.source, .kugou, "\(mode) \(order)")
            }
        }
    }
}
