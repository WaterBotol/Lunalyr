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

    func testArtistWordsStayLitForTheWholeCue() throws {
        let cue = try XCTUnwrap(LyricCreditCues.cues(in: [], title: "Human Nature", artist: "Yot Club").first)
        XCTAssertEqual(cue.words.map(\.text), ["Human", "Nature", "-", "Yot", "Club"])
        let artist = Array(cue.words.suffix(2))
        XCTAssertTrue(artist.allSatisfy { $0.end >= cue.end - 0.001 })
        XCTAssertTrue(cue.words.prefix(2).allSatisfy { $0.end < cue.end - 3 })
        XCTAssertTrue(zip(cue.words, cue.words.dropFirst()).allSatisfy { $0.start <= $1.start })
    }
}
