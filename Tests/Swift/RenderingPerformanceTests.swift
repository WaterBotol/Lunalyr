import SwiftUI
import XCTest
@testable import VersoCustom

final class RenderingPerformanceTests: XCTestCase {
    private let words: [LyricsFetcher.Word] = [
        .init(text: "Keep", start: 10, end: 10.3), .init(text: "the", start: 10.3, end: 10.6),
        .init(text: "light", start: 10.6, end: 13), .init(text: "♪", start: 13, end: 13.1),
    ]
    @MainActor
    private func key(size: CGFloat = 44, spread: Double = 1,
                     style: LyricComposition = .constellationEditorial) -> ScatteredTypography.Key {
        .init(words: words, baseSize: size, style: .forLine(0), mood: .neutral,
              composition: style, spread: spread, fontIdentity: LyricFontIdentity(settings: .shared))
    }
    @MainActor
    func testPlaybackAndSeekingReusePreparedTypography() {
        let memo = RenderMemo<ScatteredTypography.Key, [ScatteredTypography.Word]>()
        let source = key()
        for time in [0.0, 10.1, 11, 12, 20, 9, 11] {
            let prepared = memo.value(for: source) { ScatteredTypography.prepare(source, settings: .shared) }
            _ = ScatteredWordFrame.make(prepared[0], nextIndex: 0, time: time, composition: source.composition)
        }
        XCTAssertEqual(memo.builds, 1)
    }
    @MainActor
    func testResizingAndSpreadInvalidateWithoutChangingWordIdentity() {
        let memo = RenderMemo<ScatteredTypography.Key, [ScatteredTypography.Word]>()
        func value(_ input: ScatteredTypography.Key) -> [ScatteredTypography.Word] {
            memo.value(for: input) { ScatteredTypography.prepare(input, settings: .shared) }
        }
        let first = value(key()), doubled = value(key(size: 88))
        XCTAssertEqual(doubled[0].metrics.size, first[0].metrics.size * 2, accuracy: 0.0001)
        let wider = value(key(size: 88, spread: 1.5))
        XCTAssertNotEqual(wider[0].metrics.waveOffset, doubled[0].metrics.waveOffset)
        XCTAssertEqual(first.map(\.source.presentationID), wider.map(\.source.presentationID))
        XCTAssertEqual(memo.builds, 3)
    }
    @MainActor
    func testFontSwitchInvalidatesCachedGlyphs() {
        let settings = Settings.shared, saved = Settings.shared.fontChoice
        defer { settings.fontChoice = saved }
        let memo = RenderMemo<ScatteredTypography.Key, [ScatteredTypography.Word]>()
        settings.fontChoice = .sans
        let firstKey = key(), first = memo.value(for: firstKey) { ScatteredTypography.prepare(firstKey, settings: settings) }
        settings.fontChoice = .serif
        let secondKey = key(), second = memo.value(for: secondKey) { ScatteredTypography.prepare(secondKey, settings: settings) }
        XCTAssertNotEqual(firstKey, secondKey)
        // Index 0 uses Editorial's alternate face, which intentionally stays
        // independent of the selected regular lyric font.
        XCTAssertNotEqual(first[1].font, second[1].font)
        XCTAssertEqual(memo.builds, 2)
    }
    @MainActor
    func testLateFontIdentityAndNewReadingInvalidatePreparation() {
        var input = key()
        let loaded = LyricFontIdentity(choice: input.fontIdentity.choice, customRegular: nil,
            customBold: nil, appRegular: "LoadedRegular", appBold: "LoadedBold")
        let withFont = ScatteredTypography.Key(words: input.words, baseSize: input.baseSize, style: input.style,
            mood: input.mood, composition: input.composition, spread: input.spread, fontIdentity: loaded)
        XCTAssertNotEqual(input, withFont)
        input = .init(words: [.init(text: "Hikari", start: 10, end: 13)], baseSize: input.baseSize,
            style: input.style, mood: input.mood, composition: input.composition, spread: input.spread,
            fontIdentity: input.fontIdentity)
        XCTAssertNotEqual(input, key())
        XCTAssertEqual(ScatteredTypography.prepare(input, settings: .shared).first?.source.text, "Hikari")
    }
    @MainActor
    func testStableInkFramesStillChangeAtArrivalAndOnBackwardsSeek() {
        let settings = Settings.shared
        let saved = settings.wordRevealByStyle
        defer { settings.wordRevealByStyle = saved }
        settings.wordRevealByStyle[LyricComposition.scattered.rawValue] = true
        let token = ScatteredTypography.prepare(key(style: .scattered), settings: settings)[0]
        func frame(_ time: Double) -> ScatteredWordFrame {
            .make(token, nextIndex: 0, time: time, composition: .scattered)
        }
        XCTAssertEqual(frame(0), frame(8))
        XCTAssertEqual(frame(20), frame(30))
        XCTAssertNotEqual(frame(10.01), frame(10.08))
        XCTAssertNotEqual(frame(20), frame(8))
    }
    @MainActor
    func testPreparationKeepsPunctuationFilteringAndHeldWordGlyphs() {
        let prepared = ScatteredTypography.prepare(key(), settings: .shared)
        XCTAssertEqual(prepared.count, 3)
        XCTAssertEqual(prepared[2].label, "LIGHT")
        XCTAssertEqual(prepared[2].characters.joined(), "LIGHT")
        XCTAssertGreaterThan(prepared[2].sustain, 0.9)
        XCTAssertTrue(prepared[2].metrics.isHero)
    }
    @MainActor
    func testRollingStylesPreserveTimedWordIdentityAcrossWindows() {
        let settings = Settings.shared
        for style in [LyricComposition.spoken, .cascade, .cutout] {
            let first = SpokenTypography.prepare(.init(words: words, baseSize: 44, mood: .neutral,
                composition: style, fontIdentity: LyricFontIdentity(settings: settings)), settings: settings)
            let shifted = SpokenTypography.prepare(.init(words: Array(words.dropFirst()), baseSize: 44, mood: .neutral,
                composition: style, fontIdentity: LyricFontIdentity(settings: settings)), settings: settings)
            XCTAssertEqual(first[1].id, shifted[0].id)
            XCTAssertEqual(first[1].hash, shifted[0].hash)
            XCTAssertEqual(first[1].font, shifted[0].font)
        }
    }
    func testLineIndexMatchesForwardRuleAtBoundariesAndAfterSeeks() {
        let lines = [2.0, 5, 5, 8].map { LyricsFetcher.Line(start: $0, end: $0 + 2, text: "x", words: [], isInstrumental: false) }
        let index = LyricLineIndex(lines: lines)
        XCTAssertNil(index.line(at: 1.99))
        XCTAssertEqual(index.line(at: 2), 0)
        XCTAssertEqual(index.line(at: 5), 2)
        XCTAssertEqual(index.line(at: 100), 3)
        XCTAssertEqual(index.line(at: 3), 0)
        XCTAssertNil(index.line(at: .nan))
    }
    func testMalformedLineIndexKeepsEarlyStopAndEmptyHandling() {
        let lines = [2.0, 8, 5].map { LyricsFetcher.Line(start: $0, end: $0 + 1, text: "x", words: [], isInstrumental: false) }
        XCTAssertEqual(LyricLineIndex(lines: lines).line(at: 6), 0)
        XCTAssertNil(LyricLineIndex(lines: []).line(at: 6))
    }
}
