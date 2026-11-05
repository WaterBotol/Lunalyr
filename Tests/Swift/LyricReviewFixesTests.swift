import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Regression tests for the persona-review findings on lyric rendering and
/// interludes (components C6 and C7).
final class LyricReviewFixesTests: XCTestCase {
    private func words(_ texts: [String], from start: Double = 10, step: Double = 0.3) -> [LyricsFetcher.Word] {
        texts.enumerated().map { .init(text: $0.element, start: start + Double($0.offset) * step,
                                       end: start + Double($0.offset + 1) * step) }
    }

    // MARK: HIGH 1 -- entrances run on time, not on an already-eased curve

    func testEntranceMovesSmoothlyAt30fpsAndOpacityLeadsTheMovement() {
        // A 0.4 s word with a 0.72 em Glide: previously it covered most of
        // its travel in two frames while still half transparent.
        let seed: UInt64 = 0xA11CE
        var previousOffset = 0.72
        for frame in 0...12 {
            let age = Double(frame) / 30
            let motion = vc_word_motion(10 + age, 10, 10.4, 0.4, .infinity, 0, seed, 0, 0)
            let offset = (1 - motion.enter) * 0.72
            XCTAssertLessThan(abs(offset - previousOffset), 0.72 * 0.3, "moved too far in one frame at \(frame)")
            if motion.enter < 0.9 {
                XCTAssertGreaterThanOrEqual(motion.reveal + 0.02, motion.enter, "opacity must lead at \(frame)")
            }
            previousOffset = offset
        }
    }

    // MARK: HIGH 2 -- syllables and characters set as the words they spell

    func testTimedSyllablesAndCharactersGlueIntoTheirWords() {
        XCTAssertEqual(LyricRuns.glue(words(["So", "beau", "ti", "ful", "to", "night"]), in: "So beautiful tonight"),
                       [false, false, true, true, false, true])
        XCTAssertEqual(LyricRuns.glue(words(["君", "の", "声", "が", "沈", "む", "よ", "う", "に"]), in: "君の声が 沈むように"),
                       [false, true, true, true, false, true, true, true, true])
        // Trailing spaces in the tokens themselves also separate words.
        XCTAssertEqual(LyricRuns.glue(words(["Stay ", "here"]), in: "Stay here"), [false, false])
        // A token missing from the text stands alone; the rest still align.
        XCTAssertEqual(LyricRuns.glue(words(["love", "zzz", "you"]), in: "love you"), [false, false, false])
        // A dropped punctuation token keeps its neighbours glued.
        let dont = LyricRuns.visible(words(["don", "'", "t", "go"]), glue: LyricRuns.glue(words(["don", "'", "t", "go"]), in: "don't go"))
        XCTAssertEqual(dont.words.map(\.text), ["don", "t", "go"])
        XCTAssertEqual(dont.glue, [false, true, false])
        XCTAssertEqual(LyricRuns.ranges([false, true, true, false, true], count: 5), [0..<3, 3..<5])
    }

    func testReadingsThatReplaceWordsKeepTheLinesGrouping() {
        // The syllables of one word still read as that word...
        let love = LyricsFetcher.Line(start: 10, end: 12, text: "люблю тебя", words: words(["лю", "блю", "те", "бя"]),
                                      isInstrumental: false, wordTimed: true)
        let latin = words(["lyu", "blyu", "te", "bya"])
        XCTAssertEqual(LyricRuns.glue(line: love, words: latin), [false, true, false, true])
        let merged = LyricRuns.merged(latin, glue: LyricRuns.glue(line: love, words: latin))
        XCTAssertEqual(merged.words.map(\.text), ["lyublyu", "tebya"])
        XCTAssertEqual(merged.words.first?.start, 10)
        // ...while a Latin reading of each character of a script written
        // without spaces is a word of its own, not "shizumuyou".
        let line = LyricsFetcher.Line(start: 10, end: 12, text: "沈むように", words: words(["沈", "む", "よ", "う", "に"]),
                                      isInstrumental: false, wordTimed: true)
        let readings = words(["shi", "zu", "mu", "yo", "u"])
        XCTAssertEqual(LyricRuns.glue(line: line, words: readings), [false, false, false, false, false])
    }

    func testRollingLayoutsMoveWholeWordsWithJoinedReadings() {
        let tokens = words(["beau", "ti", "ful", "day"])
        let glue = LyricRuns.glue(tokens, in: "beautiful day")
        let pairs = [tokens[0].presentationID: "byu", tokens[1].presentationID: "ti", tokens[2].presentationID: "fl"]
        let merged = LyricRuns.merged(tokens, glue: glue, readings: pairs)
        XCTAssertEqual(merged.words.map(\.text), ["beautiful", "day"])
        XCTAssertEqual(merged.readings[merged.words[0].presentationID], "byutifl")
        XCTAssertEqual(merged.words[0].end, tokens[2].end)
    }

    @MainActor
    func testAGluedRunSharesOneSizeFaceAndSlotAndFillsTokenByToken() throws {
        let line = words(["So", "beau", "ti", "ful"], step: 0.4)
        let key = ScatteredTypography.Key(words: line, baseSize: 60, style: .forLine(4), mood: .neutral,
            composition: .constellationEditorial, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared),
            glue: LyricRuns.glue(line, in: "So beautiful"))
        let layout = ScatteredTypography.layout(key, settings: .shared)
        XCTAssertEqual(layout.runs.count, 2, "two words, not four chips")
        let run = layout.runs[1]
        XCTAssertTrue(run.isGlued)
        XCTAssertEqual(run.label.lowercased(), "beautiful")
        XCTAssertEqual(run.boundaries, [0, 4.0 / 9, 6.0 / 9, 1])
        let tokens = layout.words[run.tokens]
        XCTAssertTrue(tokens.allSatisfy { $0.metrics == tokens.first?.metrics }, "one size, rotation and baseline offset")
        XCTAssertTrue(tokens.allSatisfy { $0.order == tokens.first?.order }, "the run leaves as one word")
        // Karaoke progress through the run is continuous across syllables.
        var last = -1.0
        for step in 0...300 {
            let time = 10.3 + Double(step) / 200
            let next = layout.words.firstIndex { $0.source.start > time }
            let states = layout.words.map { ScatteredWordFrame.state($0, nextIndex: next, time: time,
                                                                      composition: .constellationEditorial) }
            let frame = ScatteredRunFrame.make(run, states: states, reveals: false, ink: .white)
            XCTAssertGreaterThanOrEqual(frame.fill, last - 1e-9)
            if last >= 0 { XCTAssertLessThan(frame.fill - last, 0.05) }
            last = frame.fill
        }
        XCTAssertEqual(last, 1, accuracy: 1e-9)
    }

    @MainActor
    func testWordGapsStayNearASpace() throws {
        // Two settled words on one level row: the gap between their ink is
        // now about a third of an em, not 0.7 to 1.6 em.
        let line = words(["Hold", "light"], from: 1, step: 0.4)
        let view = ScatteredLyricText(words: line, stageWidth: 1200, time: 9, baseSize: 80, style: .forLine(4),
                                      composition: .scattered)
            .padding(40).background(.black)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        var columns = [Bool](repeating: false, count: bitmap.pixelsWide)
        for x in 0..<bitmap.pixelsWide {
            for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2)
            where (bitmap.colorAt(x: x, y: y)?.brightnessComponent ?? 0) > 0.5 { columns[x] = true; break }
        }
        let inked = columns.indices.filter { columns[$0] }
        let first = try XCTUnwrap(inked.first), last = try XCTUnwrap(inked.last)
        var widest = 0, run = 0
        for x in first...last { if columns[x] { run = 0 } else { run += 1; widest = max(widest, run) } }
        let key = ScatteredTypography.Key(words: line, baseSize: 80, style: .forLine(4), mood: .neutral,
            composition: .scattered, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared))
        let em = try XCTUnwrap(ScatteredTypography.layout(key, settings: .shared).runs.first).metrics.size
        XCTAssertLessThan(CGFloat(widest) / em, 0.55, "gap of \(widest) pt at \(em) pt type")
        XCTAssertGreaterThan(CGFloat(widest) / em, 0.15)
    }

    // MARK: MEDIUM 5, 7, 11 and LOW 12, 15

    func testCutoutLabelsGrowWithoutASnapAtTheOnset() {
        // The tile width is one continuous function of the word's spring.
        func width(_ time: Double) -> Double {
            min(1.06, max(0.05, vc_word_motion(time, 10, 10.6, 0.6, .infinity, 0, 77, 0, 0).enter))
        }
        XCTAssertEqual(width(9.95), 0.05)
        XCTAssertEqual(width(9.9999), width(10.0001), accuracy: 0.01)
        var previous = width(9.9)
        for step in 1...600 {
            let value = width(9.9 + Double(step) / 1000)
            XCTAssertLessThan(abs(value - previous), 0.03)
            previous = value
        }
    }

    @MainActor
    func testIncreaseContrastRaisesUnsungWordsAndTheContrastFloor() {
        let calm = LyricInkDirector(), strong = LyricInkDirector()
        var normal = LyricInk.white, increased = LyricInk.white
        let album: [SIMD3<Float>] = [SIMD3(0.8, 0.3, 0.3), SIMD3(0.2, 0.4, 0.8), SIMD3(0.5, 0.5, 0.5),
                                     SIMD3(0.9, 0.7, 0.2), SIMD3(0.2, 0.7, 0.5)]
        for frame in 0..<200 {
            normal = calm.ink(.init(mode: .albumDrift, album: album, backdrop: 0.1), wall: Double(frame) / 60)
            increased = strong.ink(.init(mode: .albumDrift, album: album, backdrop: 0.1, increaseContrast: true),
                                   wall: Double(frame) / 60)
        }
        XCTAssertEqual(normal.sample(slot: 0.3).dim, LyricInk.unsungDim)
        XCTAssertEqual(increased.sample(slot: 0.3).dim, LyricInk.increasedUnsungDim)
        let colour = NSColor(increased.sample(slot: 0.3).rest).usingColorSpace(.sRGB)!
        let y = Perceptual.luminance(sRGB: SIMD3(Double(colour.redComponent), Double(colour.greenComponent),
                                                 Double(colour.blueComponent)))
        XCTAssertGreaterThanOrEqual(Perceptual.contrast(y, increased.backdrop), LyricInk.increasedContrast - 0.02)
        XCTAssertEqual(LyricInk.white(increasedContrast: true).base.dim, LyricInk.increasedUnsungDim)
    }

    @MainActor
    func testEmojiWordsAreDrawnButPunctuationAndNoteMarksAreNot() {
        XCTAssertTrue(LyricRuns.isVisible("❤️"))
        XCTAssertTrue(LyricRuns.isVisible("👋🏽"))
        XCTAssertTrue(LyricRuns.isVisible("$5"))
        XCTAssertFalse(LyricRuns.isVisible("♪"))
        XCTAssertFalse(LyricRuns.isVisible("…"))
        XCTAssertFalse(LyricRuns.isVisible(" , "))
        let key = ScatteredTypography.Key(words: words(["I", "❤️", "you", "♪"]), baseSize: 44, style: .forLine(0),
            mood: .neutral, composition: .scattered, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared))
        XCTAssertEqual(ScatteredTypography.prepare(key, settings: .shared).map(\.source.text), ["I", "❤️", "you"])
    }

    func testUnsyncedLyricsScrollEvenlyThroughTheSong() {
        XCTAssertEqual(PlainLyricsScroll.progress(time: 0, duration: 200), 0)
        XCTAssertEqual(PlainLyricsScroll.progress(time: 199, duration: 200), 1)
        XCTAssertEqual(PlainLyricsScroll.progress(time: 50, duration: 20), 0, "too short to scroll")
        var previous = 0.0
        for step in 0...2000 {
            let value = PlainLyricsScroll.progress(time: Double(step) / 10, duration: 200)
            XCTAssertGreaterThanOrEqual(value, previous)
            XCTAssertLessThan(value - previous, 0.002)
            previous = value
        }
    }

    // MARK: HIGH 3 -- the lyric clock slows when nothing moves

    @MainActor
    func testLyricPaceSlowsOnlyWhenStillAndNeverRunsFasterThanTheWallpaper() {
        let pace = LyricPace()
        XCTAssertEqual(pace.interval(full: 1.0 / 60), 1.0 / 60)
        pace.report(.still)
        let settled = expectation(description: "published after the frame")
        DispatchQueue.main.async { settled.fulfill() }
        wait(for: [settled], timeout: 1)
        XCTAssertEqual(pace.level, .still)
        XCTAssertEqual(pace.interval(full: 1.0 / 60), 1.0 / 12, accuracy: 1e-12)
        XCTAssertEqual(pace.interval(full: 1.0 / 15), 1.0 / 12, accuracy: 1e-12)
        pace.report(.ambient)
        let ambient = expectation(description: "ambient")
        DispatchQueue.main.async { ambient.fulfill() }
        wait(for: [ambient], timeout: 1)
        XCTAssertEqual(pace.interval(full: 1.0 / 60), 1.0 / 24, accuracy: 1e-12)
        XCTAssertEqual(pace.interval(full: 1.0 / 30), 1.0 / 24, accuracy: 1e-12)
    }

    @MainActor
    func testTheStageIsPassedByWhenOnlyTheBackgroundMoves() {
        let a = LyricStage(size: CGSize(width: 1512, height: 982), mood: .neutral, accent: .mint, trackSeed: 4,
                           palette: [SIMD3(0.2, 0.3, 0.4)], interval: 1.0 / 60, paused: false)
        let b = LyricStage(size: CGSize(width: 1512, height: 982), mood: .neutral, accent: .mint, trackSeed: 4,
                           palette: [SIMD3(0.2, 0.3, 0.4)], interval: 1.0 / 60, paused: false)
        XCTAssertEqual(a, b, "a new background frame alone does not rebuild the lyrics")
        let paused = LyricStage(size: CGSize(width: 1512, height: 982), mood: .neutral, accent: .mint, trackSeed: 4,
                                palette: [SIMD3(0.2, 0.3, 0.4)], interval: 1.0 / 60, paused: true)
        XCTAssertNotEqual(a, paused)
    }

    // MARK: Fonts and translations

    @MainActor
    func testInstalledFamiliesResolveWithoutTheNetwork() throws {
        let installed = try XCTUnwrap(AppFont.installed(family: "Helvetica Neue"))
        XCTAssertNotNil(NSFont(name: installed.regular, size: 12))
        XCTAssertNotEqual(installed.regular, installed.bold)
        XCTAssertNil(AppFont.installed(family: "No Such Family Anywhere"))
        XCTAssertNil(AppFont.installed(family: "   "))
    }

    @MainActor
    func testTranslationsAreOnByDefaultAndPersist() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let isolated = UserDefaults(suiteName: name)!
        defer { isolated.removePersistentDomain(forName: name) }
        let extras = LunalyrExtras(defaults: isolated)
        XCTAssertTrue(extras.showsTranslations)
        extras.showsTranslations = false
        XCTAssertFalse(LunalyrExtras(defaults: isolated).showsTranslations)
        // The field the parser fills; old cached lyrics without it still decode.
        let line = LyricsFetcher.Line(start: 1, end: 2, text: "Hola", words: [], isInstrumental: false,
                                      translation: "Hello")
        XCTAssertEqual(line.translation, "Hello")
        let old = #"{"start":1,"end":2,"text":"Hola","words":[],"isInstrumental":false,"wordTimed":false}"#
        XCTAssertNil(try JSONDecoder().decode(LyricsFetcher.Line.self, from: Data(old.utf8)).translation)
    }

    // MARK: Reduce Motion audit (docs session)

    func testReduceMotionKeepsCutoutLabelsAndSungWordsStill() {
        // Labels: no press-on scale, at any point of the entrance.
        for arrival in stride(from: 0.0, through: 1.1, by: 0.05) {
            XCTAssertEqual(RollingUnit.tileWidth(arrival: arrival, reduceMotion: true), 1)
        }
        XCTAssertEqual(RollingUnit.tileWidth(arrival: 0, reduceMotion: false), 0.05)
        XCTAssertEqual(RollingUnit.tileWidth(arrival: 0.5, reduceMotion: false), 0.5, accuracy: 1e-9)
        // The word in focus does not zoom; emphasis stays in light and glow.
        XCTAssertEqual(ScatteredRunFrame.swell(heat: 1, glow: 1, gesture: 1.12, reduceMotion: true), 1)
        XCTAssertGreaterThan(ScatteredRunFrame.swell(heat: 1, glow: 1, gesture: 1.12, reduceMotion: false), 1.1)
        // Unsynced lyrics hold still rather than scrolling.
        XCTAssertNil(PlainLyricsScroll.progress(time: 90, duration: 200, reduceMotion: true))
        XCTAssertNotNil(PlainLyricsScroll.progress(time: 90, duration: 200, reduceMotion: false))
    }
}
