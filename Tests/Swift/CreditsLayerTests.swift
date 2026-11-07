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
}
