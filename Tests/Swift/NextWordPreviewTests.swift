import XCTest
@testable import VersoCustom

final class NextWordPreviewTests: XCTestCase {
    @MainActor
    func testEveryStyleCanPreviewExactlyOneFutureWord() async {
        let settings = Settings.shared
        let savedPreview = settings.nextWordPreviewByStyle, savedReveal = settings.wordRevealByStyle
        let savedLead = settings.nextWordPreviewLead, savedLayout = settings.lyricComposition
        defer {
            settings.nextWordPreviewByStyle = savedPreview; settings.wordRevealByStyle = savedReveal
            settings.nextWordPreviewLead = savedLead; settings.lyricComposition = savedLayout
        }
        settings.nextWordPreviewLead = 0.5
        for style in LyricComposition.allCases {
            settings.lyricComposition = style
            settings.wordRevealByStyle[style.rawValue] = true
            settings.nextWordPreviewByStyle[style.rawValue] = true
            let next = WordPresentation.make(word: .init(text: "next", start: 2, end: 3), index: 1, nextIndex: 1, time: 1.75, composition: style)
            let later = WordPresentation.make(word: .init(text: "later", start: 3, end: 4), index: 2, nextIndex: 1, time: 1.75, composition: style)
            XCTAssertEqual(next.opacity, 0.125, accuracy: 0.001, style.title)
            XCTAssertEqual(later.opacity, 0, accuracy: 0.001, style.title)
        }
    }
    @MainActor
    func testEveryStyleCanHideThePreview() async {
        let settings = Settings.shared
        let savedPreview = settings.nextWordPreviewByStyle, savedReveal = settings.wordRevealByStyle
        let savedLead = settings.nextWordPreviewLead, savedLayout = settings.lyricComposition
        defer {
            settings.nextWordPreviewByStyle = savedPreview; settings.wordRevealByStyle = savedReveal
            settings.nextWordPreviewLead = savedLead; settings.lyricComposition = savedLayout
        }
        settings.nextWordPreviewLead = 0.5
        for style in LyricComposition.allCases {
            settings.lyricComposition = style
            settings.wordRevealByStyle[style.rawValue] = true
            settings.nextWordPreviewByStyle[style.rawValue] = false
            let next = WordPresentation.make(word: .init(text: "next", start: 2, end: 3), index: 1, nextIndex: 1, time: 1.75, composition: style)
            XCTAssertEqual(next.opacity, 0, accuracy: 0.001, style.title)
        }
    }
    func testPreviewUsesTheNextOnsetEvenAcrossALongGap() {
        XCTAssertEqual(vc_word_visibility(1, 10, 11, 1, 1, 0.5), 0)
        XCTAssertEqual(vc_word_visibility(9.75, 10, 11, 1, 1, 0.5), 0.125, accuracy: 0.001)
        XCTAssertEqual(vc_word_visibility(9.99, 10, 11, 1, 1, 0.5), 0.25, accuracy: 0.001)
        XCTAssertEqual(vc_word_visibility(10, 10, 11, 1, 0, 0.5), 0.25, accuracy: 0.001)
    }

    func testFirstWordOfNextLineUsesRealOnsetOrLineTimestamp() {
        var next = LyricsFetcher.Line(start: 10, end: 12, text: "… first word", words: [
            .init(text: "…", start: 10, end: 10.05),
            .init(text: "first", start: 10.2, end: 11),
            .init(text: "word", start: 11, end: 12),
        ], isInstrumental: false, wordTimed: true)
        XCTAssertEqual(LyricPhraseTiming.firstWordOnset(of: next), 10.2)
        next.wordTimed = false
        XCTAssertEqual(LyricPhraseTiming.firstWordOnset(of: next), 10)
    }

    @MainActor
    func testAutomaticCutoutAppliesToAllResolvedLayouts() {
        let settings = Settings.shared
        let layout = settings.lyricComposition, entrances = settings.wordArrivalByStyle, reveals = settings.wordRevealByStyle
        defer {
            settings.lyricComposition = layout; settings.wordArrivalByStyle = entrances; settings.wordRevealByStyle = reveals
        }
        settings.lyricComposition = nil
        settings.wordArrivalByStyle[settings.wordControlsKey] = WordArrival.cutoutSlide.rawValue
        settings.wordRevealByStyle[settings.wordControlsKey] = false
        for style in LyricComposition.allCases {
            let word = LyricsFetcher.Word(text: "light", start: 1, end: 2)
            XCTAssertEqual(settings.arrival(in: style, for: word), .cutoutSlide)
            XCTAssertLessThan(WordPresentation.make(word: word, index: 0, nextIndex: nil, time: 1.1,
                composition: style).progress, 1)
        }
    }

    func testTimedWordIdentitySurvivesMovingBetweenLineWindows() {
        let original = LyricsFetcher.Word(text: "light", start: 8.2, end: 8.8)
        let moved = LyricsFetcher.Word(text: "light", start: 8.2, end: 8.8)
        XCTAssertEqual(original.presentationID, moved.presentationID)
    }
}
