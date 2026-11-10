import AppKit
import XCTest
@testable import VersoCustom

// The menu-bar line: a width fixed per song, long lines in two halves, and
// one wake-up per change instead of four a second. Pure functions only; no
// status item is created.

final class MenuBarTickerTests: XCTestCase {
    typealias Ticker = MenuBarLyricTicker
    typealias Line = LyricsFetcher.Line
    typealias Word = LyricsFetcher.Word

    private let tenPoints: (String) -> CGFloat = { CGFloat($0.count * 10) }

    private func line(_ start: Double, _ end: Double, _ text: String, words: [Word] = [],
                      instrumental: Bool = false) -> Line {
        Line(start: start, end: end, text: text, words: words, isInstrumental: instrumental)
    }

    /// Forty lines over about three minutes, each sung for 3 s of its 4.5 s.
    private var song: [Line] {
        (0..<40).map { index in
            let start = 10 + Double(index) * 4.5
            return line(start, start + 3, "Line number \(index) of the song")
        }
    }

    @MainActor
    func testTheLineChangesAtItsStartAndGivesWayToANoteAfterIt() {
        let lines = [line(10, 12, "First"), line(20, 22, "Second"), line(30, 31, "...", instrumental: true),
                     line(40, 42, "Last")]
        XCTAssertEqual(Ticker.lineState(lines: lines, index: nil, pageSwitches: [], time: 5),
                       .init(line: nil, page: 0, nextEvent: 10), "before the first line, it waits for it")
        XCTAssertEqual(Ticker.lineState(lines: lines, index: 0, pageSwitches: [], time: 11),
                       .init(line: 0, page: 0, nextEvent: 12 + Ticker.finishedAfter))
        XCTAssertEqual(Ticker.lineState(lines: lines, index: 0, pageSwitches: [], time: 14),
                       .init(line: nil, page: 0, nextEvent: 20), "a finished line gives way to ♪")
        XCTAssertEqual(Ticker.lineState(lines: lines, index: 2, pageSwitches: [], time: 30.5).line, nil,
                       "an instrumental break shows ♪")
        XCTAssertEqual(Ticker.lineState(lines: lines, index: 3, pageSwitches: [], time: 50),
                       .init(line: 3, page: 0, nextEvent: nil), "the last line stays")
    }

    @MainActor
    func testALongLinesSecondHalfTakesOverOnTime() {
        let lines = [line(10, 16, "A long line"), line(20, 22, "Next")]
        XCTAssertEqual(Ticker.lineState(lines: lines, index: 0, pageSwitches: [13], time: 11),
                       .init(line: 0, page: 0, nextEvent: 13))
        XCTAssertEqual(Ticker.lineState(lines: lines, index: 0, pageSwitches: [13], time: 13.2),
                       .init(line: 0, page: 1, nextEvent: 16 + Ticker.finishedAfter))
    }

    @MainActor
    func testItWakesOncePerChangeNotFourTimesASecond() {
        let lines = song
        var time = 0.0
        var wakes = 0
        var shownStarts = Set<Int>()
        let end = lines.last!.end + 10
        while time < end {
            let index = LyricLineIndex(lines: lines).line(at: time)
            let state = Ticker.lineState(lines: lines, index: index, pageSwitches: [], time: time)
            if let shown = state.line, abs(time - lines[shown].start) < 0.1 { shownStarts.insert(shown) }
            time += Ticker.wait(untilEventIn: state.nextEvent.map { $0 - time }, ceiling: Ticker.maxWait)
            wakes += 1
        }
        XCTAssertEqual(shownStarts.count, lines.count, "every line appears within 0.1 s of its start")
        let seconds = end
        XCTAssertLessThan(Double(wakes) / seconds, 1.0, "at most about one wake-up a second, was \(wakes) in \(seconds) s")
        XCTAssertGreaterThan(Double(wakes) / seconds, 0.3)
    }

    @MainActor
    func testWaitsStayBetweenTheFloorAndTheCeiling() {
        XCTAssertEqual(Ticker.wait(untilEventIn: nil, ceiling: 2), 2)
        XCTAssertEqual(Ticker.wait(untilEventIn: 30, ceiling: 2), 2, "a scrub is still caught within 2 s")
        XCTAssertEqual(Ticker.wait(untilEventIn: -1, ceiling: 2), Ticker.minWait)
        XCTAssertEqual(Ticker.wait(untilEventIn: 0.5, ceiling: 2), 0.51, accuracy: 1e-9,
                       "just after the change, so the new line is read")
    }

    @MainActor
    func testTheWidthIsSetOncePerSongFromItsWidestLine() {
        let lines = [line(1, 2, "Short"), line(3, 4, "A rather longer line"), line(5, 6, "..", instrumental: true)]
        let layout = Ticker.layout(lines: lines, readings: lines.map { .init(text: $0.text, words: []) },
                                   maxWidth: 400, measure: tenPoints)
        XCTAssertEqual(layout.width, 200 + Ticker.widthPadding, "the widest line, not each line's own width")
        XCTAssertEqual(layout.pages, [["Short"], ["A rather longer line"], [".."]])

        let capped = Ticker.layout(lines: lines, readings: lines.map { .init(text: $0.text, words: []) },
                                   maxWidth: 120, measure: tenPoints)
        XCTAssertLessThanOrEqual(capped.width, 120 + Ticker.widthPadding, "never wider than the length setting allows")
        XCTAssertEqual(capped.pages[1].count, 2, "the line too wide for it is shown in halves")
    }

    @MainActor
    func testWideScriptsAreMeasuredNotCounted() {
        let latin = Ticker.width(of: "abcdefghij")
        let japanese = Ticker.width(of: "あいうえおかきくけこ")
        XCTAssertGreaterThan(japanese, latin * 1.4, "ten kana are far wider than ten letters")
        XCTAssertGreaterThan(Ticker.maxWidth(forCharacters: 36), Ticker.maxWidth(forCharacters: 16))
    }

    @MainActor
    func testALongLineIsShownInTwoHalvesSplitNearItsMiddle() {
        let pages = Ticker.pages("I could stay awake just to hear you breathing", maxWidth: 260, measure: tenPoints)
        XCTAssertEqual(pages, ["I could stay awake just", "to hear you breathing"])
        XCTAssertTrue(pages.allSatisfy { tenPoints($0) <= 260 })

        let kana = Ticker.pages("あいうえおかきくけこさしすせそ", maxWidth: 100, measure: tenPoints)
        XCTAssertEqual(kana.count, 2, "writing without spaces splits at its middle")
        XCTAssertEqual(kana.joined(), "あいうえおかきくけこさしすせそ")

        let huge = Ticker.pages(String(repeating: "word ", count: 40), maxWidth: 100, measure: tenPoints)
        XCTAssertEqual(huge.count, 2, "never more than two halves")
        XCTAssertTrue(huge.allSatisfy { tenPoints($0) <= 100 && $0.hasSuffix("…") }, "each half shortened to fit")

        XCTAssertEqual(Ticker.pages("Fits", maxWidth: 100, measure: tenPoints), ["Fits"])
    }

    @MainActor
    func testTheSecondHalfStartsWithItsFirstSungWord() {
        let text = "one two three four"
        let words = [Word(text: "one", start: 10, end: 10.5), Word(text: "two", start: 10.5, end: 11),
                     Word(text: "three", start: 12, end: 12.5), Word(text: "four", start: 12.5, end: 13)]
        let timed = line(10, 13, text, words: words)
        let pages = ["one two", "three four"]
        XCTAssertEqual(Ticker.pageSwitch(line: timed, words: words, text: text, pages: pages), 12)

        // Line timing only: a share of the line by characters.
        let untimed = line(10, 20, text)
        let at = Ticker.pageSwitch(line: untimed, words: [], text: text, pages: pages)
        XCTAssertEqual(at, 10 + 10 * 7.0 / 17.0, accuracy: 1e-9)

        // A start-only line whose end is the next line's start: the singing
        // is assumed to finish well before then.
        var startOnly = line(10, 60, text)
        startOnly.endFromNextLine = true
        XCTAssertLessThan(Ticker.pageSwitch(line: startOnly, words: [], text: text, pages: pages), 12)

        let layout = Ticker.layout(lines: [timed], readings: [.init(text: text, words: words)], maxWidth: 100,
                                   measure: tenPoints)
        XCTAssertEqual(layout.pageSwitches, [12])
    }

    @MainActor
    func testTheRollUsesTheMotionTokens() {
        let roll = Ticker.transition(reduceMotion: false)
        XCTAssertEqual(roll.type, .push)
        XCTAssertEqual(roll.duration, LunalyrDesign.Motion.CA.line)
        let calm = Ticker.transition(reduceMotion: true)
        XCTAssertEqual(calm.type, .fade)
        XCTAssertEqual(calm.duration, LunalyrDesign.Motion.CA.reduced)
    }

    @MainActor
    func testThePausedNoteUsesASymbolNotAnEmoji() {
        XCTAssertFalse(Ticker.pausedText.unicodeScalars.contains { $0.properties.isEmojiPresentation || $0.value == 0x23F8 })
        XCTAssertNotNil(NSImage(systemSymbolName: Ticker.pausedSymbol, accessibilityDescription: nil))
        XCTAssertGreaterThanOrEqual(Ticker.hideDelay, 1.5, "a skip between songs does not pop the menu bar")
    }
}
