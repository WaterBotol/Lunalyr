import SwiftUI
import XCTest
@testable import VersoCustom

final class CreditsLayerTests: XCTestCase {
    func testProducedByIsACredit() {
        for text in ["Produced by: Max Martin", "produced by：Max Martin", "Prod. by: Metro Boomin", "Produced By: Metro Boomin & Southside",
                     "Produced by Max Martin", "Written by Tove Lo & Ali Payami"] {
            XCTAssertTrue(LyricCredits.isCredit(text), text)
        }
    }

    func testColonlessCreditNeedsTheStartOfTheSong() {
        XCTAssertFalse(LyricCredits.isCredit("Produced by Max Martin", inCredits: false))
        XCTAssertFalse(LyricCredits.isCredit("Written by the stars tonight"))
        XCTAssertFalse(LyricCredits.isCredit("Made by hand and by heart"))
    }

    func testLRCKeepsCreditsApartFromLyrics() {
        let lrc = "[00:00.00]Produced by: Max Martin\n[00:00.00]Written by: Someone Else\n[00:12.00]Hold on\n[00:16.00]Let go"
        let lines = LyricsFetcher.parseLRC(lrc, trackDuration: 30)
        let credits = lines.filter { $0.credit == true }
        XCTAssertEqual(credits.map(\.text), ["Produced by: Max Martin", "Written by: Someone Else"])
        XCTAssertTrue(credits.allSatisfy { $0.isInstrumental && $0.words.isEmpty })
        XCTAssertEqual(lines.filter { !$0.isInstrumental }.map(\.text), ["Hold on", "Let go"])
    }

    func testYRCKeepsCredits() {
        let yrc = "[0,0](0,0,0)作词 : 方文山\n[1000,1500](1000,700,0)Hello(1700,800,0) world"
        let lines = NetEaseLyricsProvider.parseYRC(yrc)
        XCTAssertEqual(lines.filter { $0.credit == true }.count, 1)
        XCTAssertEqual(lines.filter { !$0.isInstrumental }.count, 1)
    }

    func testCuesArriveOneAfterAnother() {
        let lines = LyricsFetcher.parseLRC("[00:00.00]Produced by: A B\n[00:00.00]Mixed by: C D\n[00:20.00]Hello", trackDuration: 30)
        let cues = LyricCreditCues.cues(in: lines)
        XCTAssertEqual(cues.count, 2)
        XCTAssertEqual(cues[1].start, cues[0].start + LyricCreditCues.stagger, accuracy: 0.001)
        XCTAssertEqual(LyricCreditCues.level(of: cues[0], at: cues[0].start - 1), 0)
        XCTAssertEqual(LyricCreditCues.level(of: cues[0], at: cues[0].start + 1.5), 1)
        XCTAssertEqual(LyricCreditCues.level(of: cues[0], at: cues[0].end + 5), 0)
    }

    func testAppleTTMLCreditsAndTitleHeadingAreCredits() {
        let ttml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal"><body><div>
        <p begin="0.5" end="3">Human Nature - Yot Club</p>
        <p begin="3" end="5">Lyrics by：Ryan Kaiser/Harrison Lipton</p>
        <p begin="5" end="7">Composed by：Ryan Kaiser/Harrison Lipton</p>
        <p begin="7" end="9">Produced by：Ryan Kaiser/Harrison Lipton</p>
        <p begin="12" end="16">Hold on tight</p>
        </div></body></tt>
        """
        let lines = TTMLLyricParser.parse(ttml, duration: 200)
        XCTAssertEqual(lines.filter { $0.credit == true }.count, 4)
        XCTAssertEqual(lines.filter { !$0.isInstrumental }.map(\.text), ["Hold on tight"])
    }

    @MainActor func testAnySourcesCreditsAreFlaggedWhenShown() {
        func line(_ text: String, _ start: Double) -> LyricsFetcher.Line {
            LyricsFetcher.Line(start: start, end: start + 2, text: text,
                               words: [.init(text: text, start: start, end: start + 2)], isInstrumental: false, wordTimed: true)
        }
        let source = [line("Human Nature - Yot Club", 0), line("Lyrics by：Ryan Kaiser/Harrison Lipton", 2),
                      line("Produced by：Ryan Kaiser/Harrison Lipton", 4), line("Always felt like you were gonna stick around", 8)]
        let shown = LyricsViewModel.presentation(of: .synced(source, .init(precision: "Word", source: "KPoe")), repairingGaps: false)
        XCTAssertEqual(shown.lines.filter { $0.credit == true }.count, 3)
        XCTAssertEqual(shown.lines.filter { !$0.isInstrumental }.map(\.text), ["Always felt like you were gonna stick around"])
        let plain = LyricsViewModel.presentation(of: .plain(["Human Nature - Yot Club", "Lyrics by: A B", "", "Always felt"], .init(precision: "Plain", source: "x")), repairingGaps: false)
        XCTAssertEqual(plain.plainLines, ["Always felt"])
    }

    func testTrackHeadingVariantsAreCredits() {
        func line(_ text: String, _ start: Double) -> LyricsFetcher.Line {
            LyricsFetcher.Line(start: start, end: start + 2, text: text, words: [], isInstrumental: false)
        }
        for heading in ["Human Nature - Yot Club", "Yot Club - Human Nature", "Human Nature (Yot Club)", "Human Nature by Yot Club",
                        "Yot Club: Human Nature", "HUMAN NATURE / YOT CLUB", "Human Nature – Yot Club & Friends"] {
            let shown = LyricCredits.flagging([line(heading, 0), line("Always felt like you", 5)], title: "Human Nature", artist: "Yot Club")
            XCTAssertEqual(shown[0].credit, true, heading)
            XCTAssertNil(shown[1].credit)
        }
    }

    func testALoneTitleOnlyCountsAheadOfCredits() {
        func line(_ text: String, _ start: Double) -> LyricsFetcher.Line {
            LyricsFetcher.Line(start: start, end: start + 2, text: text, words: [], isInstrumental: false)
        }
        let heading = LyricCredits.flagging([line("Human Nature", 0), line("Produced by: A B", 1), line("Hello", 5)], title: "Human Nature", artist: "Yot Club")
        XCTAssertEqual(heading.map { $0.credit == true }, [true, true, false])
        let chorus = LyricCredits.flagging([line("Hello", 0), line("Human Nature", 5), line("Hello again", 9)], title: "Human Nature", artist: "Yot Club")
        XCTAssertTrue(chorus.allSatisfy { $0.credit != true })
        let bare = LyricCredits.flagging([line("Human Nature", 0), line("Hello", 5)], title: "Human Nature", artist: "Yot Club")
        XCTAssertTrue(bare.allSatisfy { $0.credit != true })
    }

    func testEverySongOpensWithItsTitleAndArtist() {
        let none = LyricsFetcher.parseLRC("[00:12.00]Hello\n[00:16.00]World", trackDuration: 30)
        let cues = LyricCreditCues.cues(in: none, title: "Human Nature", artist: "Yot Club")
        XCTAssertEqual(cues.map(\.text), ["Human Nature - Yot Club"])
        // Not added twice when the lyrics carry it.
        let listed = LyricsFetcher.parseLRC("[00:00.00]Yot Club - Human Nature\n[00:00.50]Produced by: A B\n[00:12.00]Hello", trackDuration: 30)
        let flagged = LyricCredits.flagging(listed, title: "Human Nature", artist: "Yot Club")
        XCTAssertEqual(LyricCreditCues.cues(in: flagged, title: "Human Nature", artist: "Yot Club").count, 2)
    }

    func testTitleAndArtistShowOnlyOnce() {
        func credit(_ text: String, _ start: Double) -> LyricsFetcher.Line {
            LyricsFetcher.Line(start: start, end: start + 0.01, text: text, words: [], isInstrumental: true, credit: true)
        }
        let lines = [credit("Human Nature", 0), credit("Human Nature - Yot Club", 0.5), credit("Yot Club - Human Nature", 1),
                     credit("Yot Club", 1.5), credit("Produced by: A B", 2), credit("Produced by: A B", 3)]
        let cues = LyricCreditCues.cues(in: lines, title: "Human Nature", artist: "Yot Club")
        XCTAssertEqual(cues.map(\.text), ["Human Nature - Yot Club", "Produced by: A B"])
        let bare = LyricCreditCues.cues(in: [credit("Human Nature", 0), credit("Yot Club", 1)], title: "Human Nature", artist: "Yot Club")
        XCTAssertEqual(bare.map(\.text), ["Human Nature - Yot Club"])
    }

    func testOpeningCueNamesTheArtistForHighlighting() {
        let cues = LyricCreditCues.cues(in: [], title: "Human Nature", artist: "Yot Club")
        XCTAssertEqual(cues.first?.title, "Human Nature")
        XCTAssertEqual(cues.first?.artist, "Yot Club")
    }

    func testArtistWordsAreMarkedForTheHighlightTile() throws {
        let cue = try XCTUnwrap(LyricCreditCues.cues(in: [], title: "Human Nature", artist: "Yot Club").first)
        XCTAssertEqual(cue.words.map(\.text), ["Human", "Nature", "-", "Yot", "Club"])
        XCTAssertEqual(cue.highlightFrom, cue.words[3].start)
        XCTAssertTrue(cue.words.prefix(3).allSatisfy { $0.start < (cue.highlightFrom ?? 0) })
        XCTAssertTrue(zip(cue.words, cue.words.dropFirst()).allSatisfy { $0.start <= $1.start })
        let credit = LyricsFetcher.Line(start: 1, end: 1.01, text: "Produced by: A B", words: [], isInstrumental: true, credit: true)
        XCTAssertNil(LyricCreditCues.cues(in: [credit], title: "Human Nature", artist: "Yot Club")[1].highlightFrom)
    }

    func testStageRunsCutLongGluedLinesIntoShortRuns() {
        // An unspaced line comes with every word glued to the one before.
        let line = "今夜月色真美风也温柔"
        let words = line.enumerated().map { LyricsFetcher.Word(text: String($1), start: Double($0) * 0.3, end: Double($0) * 0.3 + 0.28) }
        let runs = StageRun.make(words, glue: [false] + [Bool](repeating: true, count: words.count - 1))
        XCTAssertGreaterThan(runs.count, 2)
        XCTAssertTrue(runs.allSatisfy { $0.text.count <= 3 })
        XCTAssertEqual(runs.map(\.text).joined(), line)
        // Syllables of one word still join.
        let syllables = ["beau", "ti", "ful"].enumerated().map { LyricsFetcher.Word(text: $1, start: Double($0), end: Double($0) + 0.9) }
        XCTAssertEqual(StageRun.make(syllables, glue: [false, true, true]).map(\.text), ["beautiful"])
    }

    /// Credits in the Echo layout used to take a string's `hashValue` as
    /// their seed. Most such seeds overflowed Echo's `abs(seed) * 31` into a
    /// negative number, and converting that to `UInt64` crashed the app as
    /// the song's opening title cue was drawn.
    @MainActor
    func testEchoTakesAnySeedWithoutTrapping() {
        XCTAssertEqual(EchoBackdrop.wanderSeed(41_234, slot: 3), UInt64(41_234 * 31 + 3), "Ordinary seeds drift as before")
        for seed in [Int.max, Int.min, -7, 0, 7_381_234_567_890_123_456, -5_102_938_475_610_293_847] {
            _ = (0..<5).map { EchoBackdrop.wanderSeed(seed, slot: $0) }
            let echo = EchoBackdrop(text: "Title - Artist", font: { .system(size: $0) }, baseSize: 40,
                                    stageSize: CGSize(width: 800, height: 400), seed: seed, presence: 1, time: 3)
            let renderer = ImageRenderer(content: echo.frame(width: 800, height: 400))
            XCTAssertNotNil(renderer.cgImage, "seed \(seed)")
        }
    }
}
