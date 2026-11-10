import XCTest
@testable import VersoCustom

/// Other services' words break ties, and LRCLIB's word timing gives up its
/// lead only when two other services agree on timing it disagrees with.
final class LyricAgreementTests: XCTestCase {
    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)
    private let verses = ["First line here", "Second line here", "Third line here", "Fourth line here", "Fifth line here"]

    private func candidate(_ source: LyricSourceID, words: Bool, texts: [String]? = nil, shift: Double = 0) -> LyricCandidate {
        let lines = (texts ?? verses).enumerated().map { index, text -> LyricsFetcher.Line in
            let start = Double(index * 5 + 2) + shift
            var line = LyricsFetcher.makeLine(text: text, start: start, end: start + 4)
            if words {
                line.wordTimed = true
                line.words = [.init(text: text, start: start, end: start + 4)]
            }
            return line
        }
        return .init(source: source, result: .synced(lines, .init(precision: "fixture", source: source.title)),
                     title: "Our Song", artist: "Our Artist", duration: 180, score: 0)
    }
    private func options(_ order: [LyricSourceID], _ mode: LyricSourceOptions.Mode = .smart) -> LyricSourceOptions.Snapshot {
        .init(enabled: order, mode: mode, localFolder: "", localVersion: "")
    }

    func testWordsAndTimingComparisons() {
        let a = LyricsFetcher.agreementText(candidate(.netEase, words: true))
        XCTAssertTrue(LyricsFetcher.sameWords(a, LyricsFetcher.agreementText(candidate(.qqMusic, words: false))))
        XCTAssertFalse(LyricsFetcher.sameWords(a, ["Something", "else", "entirely", "here", "now"]))
        XCTAssertFalse(LyricsFetcher.sameWords(Array(a.prefix(3)), Array(a.prefix(3))), "too short to say")
        let t = LyricsFetcher.agreementTiming(candidate(.netEase, words: true))
        XCTAssertTrue(LyricsFetcher.timingAgrees(t, LyricsFetcher.agreementTiming(candidate(.kugou, words: true, shift: 0.4))))
        XCTAssertFalse(LyricsFetcher.timingAgrees(t, LyricsFetcher.agreementTiming(candidate(.kugou, words: true, shift: 2))))
    }

    func testAgreementBreaksTiesAmongOtherSourcesInAccuracyFirstOnly() {
        let lone = candidate(.netEase, words: true, texts: ["Wrong one", "Wrong two", "Wrong three", "Wrong four", "Wrong five"])
        let backed = candidate(.qqMusic, words: true), backer = candidate(.kugou, words: false)
        let order: [LyricSourceID] = [.netEase, .qqMusic, .kugou]
        XCTAssertEqual(LyricsFetcher.screen([lone, backed, backer], query: query, options: options(order)).accepted.first?.source, .qqMusic)
        XCTAssertEqual(LyricsFetcher.screen([lone, backed, backer], query: query, options: options(order, .priority)).accepted.first?.source, .netEase)
    }

    func testLRCLIBKeepsItsLeadUnlessTwoOthersAgreeAgainstIt() {
        let order: [LyricSourceID] = [.netEase, .qqMusic, .lrclib]
        let late = candidate(.lrclib, words: true, shift: 3)
        // One other service disagreeing is not enough.
        var top = LyricsFetcher.screen([late, candidate(.netEase, words: true)], query: query, options: options(order)).accepted.first
        XCTAssertEqual(top?.source, .lrclib)
        // Two agreeing with each other, both against LRCLIB: it is the odd one out.
        for mode in LyricSourceOptions.Mode.allCases {
            let screened = LyricsFetcher.screen([late, candidate(.netEase, words: true), candidate(.qqMusic, words: true, shift: 0.3)],
                                                query: query, options: options(order, mode))
            XCTAssertEqual(screened.accepted.first?.source, .netEase, "\(mode)")
            XCTAssertTrue(screened.accepted.first { $0.source == .lrclib }!.agreement.timingOutlier)
        }
        // LRCLIB agreeing with one of them keeps the lead.
        top = LyricsFetcher.screen([candidate(.lrclib, words: true), candidate(.netEase, words: true), candidate(.qqMusic, words: true, shift: 3)],
                                   query: query, options: options(order)).accepted.first
        XCTAssertEqual(top?.source, .lrclib)
    }

    func testAgreementDoesNotHoldTheLookupOpen() {
        let order: [LyricSourceID] = [.netEase, .qqMusic, .lrclib]
        let best = LyricsFetcher.screen([candidate(.lrclib, words: true)], query: query, options: options(order, .priority)).accepted[0]
        XCTAssertFalse(LyricsFetcher.couldBeat(best, from: .qqMusic, query: query, options: options(order, .priority)))
    }

    func testAnotherVersionIsNotCountedAsAgreement() {
        var live = candidate(.netEase, words: true, shift: 6)
        live.title = "Our Song (Live)"
        var liveToo = candidate(.qqMusic, words: true, shift: 6)
        liveToo.title = "Our Song (Live)"
        let screened = LyricsFetcher.screen([candidate(.lrclib, words: true), live, liveToo], query: query, options: options([.netEase, .qqMusic, .lrclib]))
        XCTAssertEqual(screened.accepted.first?.source, .lrclib)
        XCTAssertFalse(screened.accepted.first!.agreement.timingOutlier)
    }
}
