import XCTest
@testable import VersoCustom

/// Stage layouts set an unspaced line (Japanese, Chinese) by its real
/// words, and Hinge opens it over several pages as it does English.
@MainActor
final class StageWordsUnspacedTests: XCTestCase {
    private func timed(_ pieces: [String], step: Double = 0.2) -> [LyricsFetcher.Word] {
        pieces.enumerated().map { LyricsFetcher.Word(text: $1, start: 1 + Double($0) * step, end: 1 + Double($0) * step + step * 0.9) }
    }

    private func runs(_ text: String) -> [StageRun] {
        let words = timed(Array(text).map(String.init))
        return StageRun.make(words, glue: LyricRuns.glue(words, in: text))
    }

    func testJapaneseRunsAreWordsNotThreeCharacterPieces() {
        let made = runs("思い描いた未来の自分にはなれなかった").map(\.text)
        XCTAssertEqual(made.joined(), "思い描いた未来の自分にはなれなかった")
        // No word cut in half: 未来 and 自分 each sit inside one run.
        XCTAssertTrue(made.contains { $0.contains("未来") })
        XCTAssertTrue(made.contains { $0.contains("自分") })
        XCTAssertTrue(made.allSatisfy { $0.count <= StageRun.unspacedWordLimit })
    }

    func testChineseRunsKeepTheirWords() {
        let made = runs("我想要和你一起看星星直到天亮").map(\.text)
        XCTAssertTrue(made.contains { $0.contains("星星") })
        XCTAssertTrue(made.contains { $0.contains("天亮") })
    }

    func testHingeOpensAnUnspacedLineOverSeveralPages() {
        for line in ["思い描いた未来の自分にはなれなかった", "我想要和你一起看星星直到天亮"] {
            let phrasing = StagePhrases.phrasing(runs(line))
            XCTAssertGreaterThanOrEqual(StageLyricView.hingePages(phrasing).count, 2, line)
            XCTAssertTrue(phrasing.phrases.allSatisfy { $0.runs.count <= StagePhrases.unspacedMaxRuns }, line)
        }
    }

    func testEnglishPhrasingIsUnchanged() {
        let text = "I can see the future that I pictured in my head"
        let words = timed(text.split(separator: " ").map { String($0) + " " }, step: 0.3)
        let phrasing = StagePhrases.phrasing(StageRun.make(words, glue: LyricRuns.glue(words, in: text)))
        XCTAssertEqual(phrasing.phrases.map(\.runs.count), [3, 4, 4])
    }
}
