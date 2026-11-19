import XCTest
@testable import VersoCustom

/// Lines in scripts other than the Latin alphabet group and arrive as a
/// native reader expects: whole words, never a word cut apart or its
/// characters scattered.
@MainActor
final class NativeGroupingTests: XCTestCase {
    private func timed(_ pieces: [String], step: Double = 0.2) -> [LyricsFetcher.Word] {
        pieces.enumerated().map { LyricsFetcher.Word(text: $1, start: 1 + Double($0) * step, end: 1 + Double($0 + 1) * step) }
    }

    private func characters(_ text: String) -> [LyricsFetcher.Word] {
        timed(text.filter { !$0.isWhitespace }.map(String.init))
    }

    // MARK: Mosaic's letter-by-letter setting

    func testMosaicSetsOnlyStandingAlphabetsLetterByLetter() {
        for word in ["dreaming", "Ты", "αγάπη", "Người", "naïve", "don't", "café!"] {
            XCTAssertTrue(StageLyricView.mosaicSetsLetters(word), word)
        }
        for word in ["未来", "ストロベリー", "사랑해", "เธอ", "ສະບາຍ", "မင်္ဂလာ", "ស្រលាញ់", "नमस्ते", "حبيبي", "שלום", "love未来"] {
            XCTAssertFalse(StageLyricView.mosaicSetsLetters(word), word)
        }
    }

    // MARK: Stage runs cut only between words

    private func stageRuns(_ text: String) -> [String] {
        let words = characters(text)
        return StageRun.make(words, glue: LyricRuns.glue(words, in: text.filter { !$0.isWhitespace })).map(\.text)
    }

    func testStageRunsKeepALongKatakanaWordWhole() {
        let runs = stageRuns("ストロベリーショートケーキを食べた")
        XCTAssertTrue(runs.contains("ストロベリー"), "\(runs)")
        XCTAssertTrue(runs.contains { $0.hasPrefix("ショートケーキ") }, "\(runs)")
    }

    func testStageRunsKeepAKoreanWordWhole() {
        let tokens = ["우", "리", "의", "사", "랑", "했", "었", "는", "데"]
        let words = timed(tokens)
        let runs = StageRun.make(words, glue: LyricRuns.glue(words, in: "우리의 사랑했었는데")).map(\.text)
        XCTAssertEqual(runs, ["우리의", "사랑했었는데"])
    }

    func testStageRunsStillCutARunTheSystemCouldNotPart() {
        let tokens = Array(repeating: "ラ", count: 30)
        let runs = StageRun.make(timed(tokens), glue: [false] + Array(repeating: true, count: 29))
        XCTAssertGreaterThan(runs.count, 1)
        XCTAssertTrue(runs.allSatisfy { $0.text.count <= StageRun.unspacedWordLimit })
    }

    // MARK: A word of these scripts arrives whole

    func testAKoreanWordArrivesWholeAndLightsAsItIsSung() {
        let words = timed(["사", "랑", "해"], step: 0.4)
        let run = StageRun.make(words, glue: [false, true, true])[0]
        XCTAssertEqual(StageRun.partOpacity(2, of: run.parts, at: 0.9), 0, "Nothing before the word arrives")
        XCTAssertEqual(StageRun.partOpacity(2, of: run.parts, at: 1.05), StageRun.waitingPart, accuracy: 1e-9,
                       "The rest of the word is there, faint, from its first syllable")
        XCTAssertEqual(StageRun.partOpacity(2, of: run.parts, at: 1.8 - 0.03 + StageMotion.syllableFade), 1, accuracy: 1e-6)
        let between = StageRun.partOpacity(1, of: run.parts, at: 1.45)
        XCTAssertGreaterThan(between, StageRun.waitingPart)
        XCTAssertLessThan(between, 1)
    }

    func testAJapaneseWordArrivesWhole() {
        let run = StageRun.make(timed(["未", "来"]), glue: [false, true])[0]
        XCTAssertEqual(StageRun.partOpacity(1, of: run.parts, at: 1.0), StageRun.waitingPart, accuracy: 1e-9)
    }

    func testALatinWordStillArrivesSyllableBySyllable() {
        let run = StageRun.make(timed(["beau", "ti", "ful"]), glue: [false, true, true])[0]
        XCTAssertFalse(StageRun.showsWhole(run.parts))
        XCTAssertEqual(StageRun.partOpacity(1, of: run.parts, at: 1.05), 0)
    }

    // MARK: Chinese words

    private func rollingLabels(_ text: String) -> [String] {
        let words = characters(text)
        return LyricRuns.labels(words, glue: LyricRuns.splittingWords(words, glue: LyricRuns.glue(words, in: text),
                                                                      into: .words))
    }

    func testChineseParticlesStayWithTheWordBefore() {
        XCTAssertEqual(rollingLabels("我们一起走过春天的小路"), ["我们", "一起", "走过", "春天的", "小路"])
    }

    func testChineseSingleCharactersStayTogetherNotWithTheWordBefore() {
        XCTAssertEqual(rollingLabels("我爱你"), ["我爱你"])
        XCTAssertEqual(rollingLabels("走过小路我想你"), ["走过", "小路", "我想你"])
        XCTAssertEqual(rollingLabels("紅蓮華"), ["紅蓮華"])
    }

    func testThaiSingleCharactersKeepTheirRule() {
        let words = timed(["ฉัน", "รัก", "เธอ", "มาก", "กว่า", "ใคร"])
        let text = "ฉันรักเธอมากกว่าใคร"
        let labels = LyricRuns.labels(words, glue: LyricRuns.splittingWords(words, glue: LyricRuns.glue(words, in: text),
                                                                            into: .words))
        XCTAssertEqual(labels.joined(), text)
        XCTAssertTrue(labels.contains("มากกว่า"), "\(labels)")
    }

    // MARK: Emphasis

    func testReadingLengthCountsWideCharactersDouble() {
        XCTAssertEqual(LyricScript.readingLength("love"), 4)
        XCTAssertEqual(LyricScript.readingLength("未来"), 4)
        XCTAssertEqual(LyricScript.readingLength("사랑해"), 6)
        XCTAssertEqual(LyricScript.readingLength("窓辺に、"), 6)
        XCTAssertEqual(LyricScript.readingLength("เธอ"), 3)
    }

    func testLightWordsAreFunctionWordsInStagePhrasing() {
        for word in ["我", "の", "나", "the"] { XCTAssertTrue(StagePhrases.isFunctionWord(word), word) }
        for word in ["未来", "사랑", "夢"] { XCTAssertFalse(StagePhrases.isFunctionWord(word), word) }
    }

    func testAStagePhraseLeansOnTheLongerUnspacedWord() {
        let words = timed(["夢", "未来"], step: 0.3)
        let runs = StageRun.make(words, glue: [false, false])
        let phrasing = StagePhrases.analyse(runs, lineTimed: false)
        XCTAssertEqual(phrasing.phrases.first?.focal, 1)
    }

    // MARK: Scattered and Echo part a long run

    func testOnlyALongUnspacedRunIsParted() {
        let short = characters("君の名は")
        XCTAssertEqual(LyricRuns.splittingWords(short, glue: LyricRuns.glue(short, in: "君の名は"), into: .words,
                                                longerThan: LyricRuns.longRun),
                       LyricRuns.glue(short, in: "君の名は"))
        let long = characters("窓辺に座って海を見ていた")
        let parted = LyricRuns.splittingWords(long, glue: LyricRuns.glue(long, in: "窓辺に座って海を見ていた"), into: .words,
                                              longerThan: LyricRuns.longRun)
        XCTAssertEqual(LyricRuns.labels(long, glue: parted), ["窓辺に", "座って", "海を", "見ていた"])
    }

    func testScatteredAndEchoPartLongRunsOnly() {
        XCTAssertEqual(LyricLayer.GlueParting(.scattered), .longRuns)
        XCTAssertEqual(LyricLayer.GlueParting(.echo), .longRuns)
        XCTAssertEqual(LyricLayer.GlueParting(.constellation), .phrases)
        for composition in [LyricComposition.spoken, .cascade, .cutout, .hinge, .portal, .mosaic, .torque, .fable] {
            XCTAssertEqual(LyricLayer.GlueParting(composition), LyricLayer.GlueParting.none, composition.rawValue)
        }
    }
}
