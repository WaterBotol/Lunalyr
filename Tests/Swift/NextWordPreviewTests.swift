import XCTest
@testable import VersoCustom

final class NextWordPreviewTests: XCTestCase {
    @MainActor
    func testEveryStyleCanPreviewExactlyOneFutureWord() async {
        let settings = Settings.shared
        let savedPreview = settings.nextWordPreviewByStyle, savedReveal = settings.wordRevealByStyle
        defer { settings.nextWordPreviewByStyle = savedPreview; settings.wordRevealByStyle = savedReveal }
        for style in LyricComposition.allCases {
            settings.wordRevealByStyle[style.rawValue] = true
            settings.nextWordPreviewByStyle[style.rawValue] = true
            let next = WordPresentation.make(word: .init(text: "next", start: 2, end: 3), index: 1, nextIndex: 1, time: 1, composition: style)
            let later = WordPresentation.make(word: .init(text: "later", start: 3, end: 4), index: 2, nextIndex: 1, time: 1, composition: style)
            XCTAssertEqual(next.opacity, 0.25, accuracy: 0.001, style.title)
            XCTAssertEqual(later.opacity, 0, accuracy: 0.001, style.title)
        }
    }
    @MainActor
    func testEveryStyleCanHideThePreview() async {
        let settings = Settings.shared
        let savedPreview = settings.nextWordPreviewByStyle, savedReveal = settings.wordRevealByStyle
        defer { settings.nextWordPreviewByStyle = savedPreview; settings.wordRevealByStyle = savedReveal }
        for style in LyricComposition.allCases {
            settings.wordRevealByStyle[style.rawValue] = true
            settings.nextWordPreviewByStyle[style.rawValue] = false
            let next = WordPresentation.make(word: .init(text: "next", start: 2, end: 3), index: 1, nextIndex: 1, time: 1, composition: style)
            XCTAssertEqual(next.opacity, 0, accuracy: 0.001, style.title)
        }
    }
    func testTimedWordIdentitySurvivesMovingBetweenLineWindows() {
        let original = LyricsFetcher.Word(text: "light", start: 8.2, end: 8.8)
        let moved = LyricsFetcher.Word(text: "light", start: 8.2, end: 8.8)
        XCTAssertEqual(original.presentationID, moved.presentationID)
    }
}
