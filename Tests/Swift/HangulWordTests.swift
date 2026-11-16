import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// A Korean line from Apple Music, timed syllable block by syllable block
/// (wave to earth, "love.", L18): 비로소 우리의 세상이 완벽해 Oh-oh.
/// On a rolling stage 우리의 showed only 우 -- held for 1.5 s -- in focus,
/// and, under Editorial typography, in AppleMyungjo's one thin weight
/// while 비로소 above it was in Apple SD Gothic Neo Bold.
@MainActor
final class HangulWordTests: XCTestCase {
    /// The paragraph exactly as Apple Music's TTML has it.
    static let ttml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" \
        xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Word" xml:lang="en"><body dur="5:07.750">\
        <div begin="3:20.286" end="3:38.890"><p begin="3:20.286" end="3:38.890" itunes:key="L18" ttm:agent="v1">\
        <span begin="3:20.286" end="3:20.790">비</span><span begin="3:20.790" end="3:21.259">로</span>\
        <span begin="3:21.259" end="3:21.825">소</span> <span begin="3:21.825" end="3:23.307">우</span>\
        <span begin="3:23.307" end="3:23.825">리</span><span begin="3:23.825" end="3:27.122">의</span> \
        <span begin="3:28.051" end="3:28.520">세</span><span begin="3:28.520" end="3:29.054">상</span>\
        <span begin="3:29.054" end="3:29.555">이</span> <span begin="3:29.555" end="3:30.968">완</span>\
        <span begin="3:30.968" end="3:31.502">벽</span><span begin="3:31.502" end="3:32.386">해</span> \
        <span begin="3:34.790" end="3:35.825">Oh-</span><span begin="3:35.825" end="3:38.890">oh</span></p></div></body></tt>
        """

    private func line() throws -> LyricsFetcher.Line {
        let line = try XCTUnwrap(TTMLLyricParser.parse(Self.ttml, duration: 307.75).first { $0.wordTimed })
        XCTAssertEqual(line.text, "비로소 우리의 세상이 완벽해 Oh-oh")
        return line
    }

    /// While 우 is sung (3:21.825 to 3:23.307).
    private let during우 = 202.6

    // MARK: Words, not syllables

    /// The syllable blocks of each spaced word roll as that word, each
    /// block keeping its own timing.
    func testASyllableTimedKoreanLineRollsAsItsWords() throws {
        let line = try line()
        // Twelve Hangul blocks, each timed on its own; "Oh-" "oh" is one
        // Latin word (`LyricScript.words`).
        XCTAssertEqual(line.words.map(\.text).prefix(6), ["비", "로", "소", "우", "리", "의"])
        XCTAssertEqual(line.words.count, 13)
        let all = LyricRuns.merged(line.words, glue: LyricRuns.rollingGlue(line: line, words: line.words)).words
        XCTAssertEqual(all.map(\.text), ["비로소", "우리의", "세상이", "완벽해", "Oh-oh"])
        let early = LyricLayerCache.rollingWindow(around: 0, lines: [line], romanized: [:], time: during우, lead: 1.2,
                                                  display: .below)
        let ours = try XCTUnwrap(early.words.first { $0.text == "우리의" })
        XCTAssertEqual(early.parts[ours.presentationID]?.map(\.text), ["우", "리", "의"])
    }

    /// The word in focus is the whole word 우리의, not a lone 우: it is
    /// there in full from its first block, and the sweep still runs
    /// through it block by block.
    func testTheKoreanWordInFocusShowsWholeAndSweepsBySyllable() throws {
        let line = try line()
        let window = LyricLayerCache.rollingWindow(around: 0, lines: [line], romanized: [:], time: during우, lead: 1.2,
                                                   display: .below)
        for composition in [LyricComposition.spoken, .cascade, .cutout] {
            let prepared = SpokenTypography.prepare(.init(words: window.words, baseSize: 60, mood: .neutral,
                composition: composition, fontIdentity: LyricFontIdentity(settings: .shared), parts: window.parts),
                settings: .shared)
            let ours = try XCTUnwrap(prepared.first { $0.source.text == "우리의" })
            XCTAssertEqual(ours.parts.count, 3)
            let reveal = RollingTokenState.partReveal(ours, time: during우, composition: composition, runOpacity: 1,
                                                      intensity: 0, reduceMotion: false)
            XCTAssertTrue(reveal == nil || reveal?.allSatisfy { $0 >= 0.999 } == true,
                          "\(composition): 리 and 의 must not be hidden while 우 is sung: \(reveal ?? [])")
            let sweep = RollingTokenState.sweep(ours, time: during우)
            XCTAssertGreaterThan(sweep, 0)
            XCTAssertLessThan(sweep, 1.0 / 3 + 1e-9, "the sweep is still inside 우")
        }
    }

    /// Japanese and Chinese runs, which can hold several words, still
    /// arrive character by character; so do a Latin word's syllables.
    func testUnspacedAndLatinRunsStillRevealByPart() {
        XCTAssertFalse(LyricRuns.revealsByPart("우리의"))
        XCTAssertFalse(LyricRuns.revealsByPart("사랑해요!"))
        XCTAssertTrue(LyricRuns.revealsByPart("夕焼けの"))
        XCTAssertTrue(LyricRuns.revealsByPart("我们"))
        XCTAssertTrue(LyricRuns.revealsByPart("beautiful"))
    }

    // MARK: One face for Hangul

    private func editorialSettings() -> VersoCustom.Settings {
        let settings = VersoCustom.Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.HangulWord"),
                                notifications: NotificationCenter())
        settings.editorialTypography = true
        settings.editorialAmount = 1
        return settings
    }

    /// Editorial typography sets some words in the contrasting face. That
    /// face (Charter, Rockwell) has no Hangul, so the system drew such a
    /// word in AppleMyungjo, one thin weight -- a different face from the
    /// Hangul around it. A word the contrasting face cannot draw keeps the
    /// main face, focused or not; a Latin word still takes the contrast.
    func testEditorialNeverSetsHangulInAFaceWithoutHangul() throws {
        let settings = editorialSettings()
        let line = try line()
        let words = LyricRuns.merged(line.words, glue: LyricRuns.rollingGlue(line: line, words: line.words)).words
            + [LyricsFetcher.Word(text: "dream", start: 240, end: 241)]
        let prepared = SpokenTypography.prepare(.init(words: words, baseSize: 60, mood: .neutral, composition: .spoken,
            fontIdentity: LyricFontIdentity(settings: settings)), settings: settings)
        // 우리의 is one of the words Editorial picks for the contrast.
        let ours = try XCTUnwrap(prepared.first { $0.source.text == "우리의" })
        XCTAssertEqual(ours.hash % 3, 1)
        for word in prepared where word.source.text.unicodeScalars.contains(where: { (0xAC00...0xD7A3).contains($0.value) }) {
            XCTAssertEqual(word.font, settings.lyricFont(size: word.pointSize, hero: true, mood: .neutral),
                           "\(word.source.text) is in the main face")
        }
        let dream = try XCTUnwrap(prepared.last)
        XCTAssertEqual(dream.hash % 3, 1)
        XCTAssertEqual(dream.font, settings.lyricFont(size: dream.pointSize, hero: true, mood: .neutral, alternate: true),
                       "a Latin word still takes the contrasting face")
        XCTAssertNotEqual(dream.font, settings.lyricFont(size: dream.pointSize, hero: true, mood: .neutral))
    }

    /// The same in a scattered line: every Hangul run in the main face.
    func testScatteredHangulRunsKeepTheMainFace() throws {
        let settings = editorialSettings()
        let line = try line()
        let glue = LyricRuns.glue(line: line, words: line.words)
        let layout = ScatteredTypography.layout(.init(words: line.words, baseSize: 60, style: .forLine(0), mood: .neutral,
            composition: .scattered, spread: 1, fontIdentity: LyricFontIdentity(settings: settings), glue: glue),
            settings: settings)
        XCTAssertEqual(layout.runs.map(\.label), ["비로소", "우리의", "세상이", "완벽해", "Oh-oh"])
        for run in layout.runs where run.label != "Oh-oh" {
            XCTAssertEqual(run.font, settings.lyricFont(size: run.metrics.size, hero: run.metrics.isHero || run.metrics.isFocal,
                                                        mood: .neutral), run.label)
        }
    }

    /// What the faces actually draw Hangul in: the contrasting serif's
    /// fallback is AppleMyungjo, whatever weight is asked for.
    func testCharterHasNoHangulOfItsOwn() {
        XCTAssertFalse(LyricFace.serif.draws("우리의"))
        XCTAssertFalse(LyricFace.slab.draws("우리의"))
        XCTAssertTrue(LyricFace.serif.draws("dream, oh-oh"))
        let charter = CTFontCreateWithName("Charter-Black" as CFString, 60, nil)
        let fallback = CTFontCreateForString(charter, "우리의" as CFString, CFRange(location: 0, length: 3))
        XCTAssertNotEqual(CTFontCopyPostScriptName(fallback) as String, "Charter-Black")
    }
}
