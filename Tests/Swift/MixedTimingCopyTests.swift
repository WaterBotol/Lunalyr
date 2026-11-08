import XCTest
@testable import VersoCustom

final class MixedTimingCopyTests: XCTestCase {
    private func lineTimed(_ text: String, _ start: Double, _ end: Double) -> LyricsFetcher.Line {
        var line = LyricsFetcher.makeLine(text: text, start: start, end: end)
        line.endFromNextLine = true
        return line
    }

    private func wordTimed(_ text: String, _ start: Double, _ end: Double) -> LyricsFetcher.Line {
        let pieces = text.split(separator: " ").map(String.init)
        let step = (end - start) / Double(pieces.count)
        let words = pieces.enumerated().map { LyricsFetcher.Word(text: $1, start: start + Double($0) * step, end: start + Double($0 + 1) * step) }
        return LyricsFetcher.Line(start: start, end: end, text: text, words: words, isInstrumental: false, wordTimed: true)
    }

    func testANearCopyJustAheadOfTheWordTimedLineIsDropped() {
        // The line-timed version differs by a comma and sits 1.2 s early, so
        // the whole line used to sweep past before the real one played.
        let lines = [lineTimed("Dear Slim, I wrote you, but you still ain't callin'", 48.94, 50.10),
                     wordTimed("Dear Slim, I wrote you but you still ain't callin'", 50.10, 52.4),
                     lineTimed("Different line altogether here", 52.4, 55)]
        let shown = LyricLineIndex.removingMixedTimingCopies(lines)
        XCTAssertEqual(shown.map(\.text), ["Dear Slim, I wrote you but you still ain't callin'", "Different line altogether here"])
        XCTAssertTrue(shown[0].wordTimed)
    }

    func testAHalfLineCopyDuringTheWordTimedLineIsDropped() {
        // Stan: the line-timed file splits a line in two; the word-timed one
        // keeps it whole, so the second half used to flash up while the line
        // was being sung (and again after it).
        let lines = [wordTimed("the morning rain clouds up my window and I can't see at all", 8.34, 14.28),
                     lineTimed("And I can't see at all", 10.96, 13.28),
                     wordTimed("And even if I could it'd all be grey", 14.28, 20.16)]
        XCTAssertEqual(LyricLineIndex.removingMixedTimingCopies(lines + [lineTimed("A different closing line", 20.2, 24)]).map(\.text),
                       ["the morning rain clouds up my window and I can't see at all",
                        "And even if I could it'd all be grey", "A different closing line"])
    }

    func testARepeatAfterTheWordTimedLineHasEndedStays() {
        let lines = [wordTimed("Say it again", 87.65, 90.0), lineTimed("Say it again", 91.64, 94.01)]
        XCTAssertEqual(LyricLineIndex.removingMixedTimingCopies(lines).count, 2)
    }

    func testASplitPhraseCountsAsACopy() {
        let lines = [lineTimed("Give me yo, give me yo,", 4.64, 5.60), wordTimed("give me yo", 5.71, 6.4)]
        XCTAssertEqual(LyricLineIndex.removingMixedTimingCopies(lines).count, 1)
    }

    func testARealRepeatFurtherAwayStays() {
        let lines = [lineTimed("Say it again", 91.0, 94.01), wordTimed("Say it again", 94.01, 96)]
        XCTAssertEqual(LyricLineIndex.removingMixedTimingCopies(lines).count, 2)
    }

    func testADifferentLineStays() {
        let lines = [lineTimed("Hey baby, even though I hate ya", 11.07, 12.75), wordTimed("I wanna love ya", 12.93, 14)]
        XCTAssertEqual(LyricLineIndex.removingMixedTimingCopies(lines).count, 2)
    }

    func testSectionLabelsAreNotLyrics() {
        for label in ["[Verse 1]", "[ Chorus ]", "Verse 3]", "[Pre-Chorus]", "(Bridge)", "[Chorus: Yot Club]"] {
            XCTAssertTrue(LyricCredits.isSectionLabel(label), label)
        }
        for lyric in ["Chorus", "Verse of a love song", "[oh oh oh]", "Bridge over troubled water", "(Intro) and then we go"] {
            XCTAssertFalse(LyricCredits.isSectionLabel(lyric), lyric)
        }
        let shown = LyricCredits.flagging([wordTimed("[Verse 1]", 1, 2), wordTimed("Hold on", 3, 4)])
        XCTAssertTrue(shown[0].isInstrumental)
        XCTAssertEqual(shown[0].text, "")
        XCTAssertFalse(shown[1].isInstrumental)
    }
}
