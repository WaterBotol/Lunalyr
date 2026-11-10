import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// How lines hand over to each other on the stage: rolling colours that
/// must not jump when the window moves, duet parts that overlap the next
/// line, and readings of a whole line hung under a rolling word.
final class LyricLayerFlowTests: XCTestCase {
    private func line(_ texts: [String], start: Double, step: Double = 0.5, length: Double = 0.4,
                      wordTimed: Bool = true) -> LyricsFetcher.Line {
        let words = texts.enumerated().map { LyricsFetcher.Word(text: $0.element, start: start + Double($0.offset) * step,
                                                              end: start + Double($0.offset) * step + length) }
        return .init(start: start, end: words.last?.end ?? start, text: texts.joined(separator: " "), words: words,
                     isInstrumental: false, wordTimed: wordTimed)
    }

    // MARK: Rolling colour

    @MainActor
    func testRollingWordsKeepTheirColourWhenTheWindowMoves() throws {
        let lines = (0..<6).map { row in line(["one\(row)", "two\(row)", "three\(row)"], start: 10 + Double(row) * 2) }
        // The window looks back four lines (`LyricPhraseTiming.rollingRange`):
        // on line 4 it holds lines 0 to 4; on line 5, lines 1 to 5. Every
        // word still shown has moved three places along the window.
        let early = LyricLayerCache.rollingWindow(around: 4, lines: lines, romanized: [:], time: 18.5, lead: 1.2, display: .below)
        let later = LyricLayerCache.rollingWindow(around: 5, lines: lines, romanized: [:], time: 20.5, lead: 1.2, display: .below)
        let shared = early.words.filter { later.words.contains($0) }
        XCTAssertEqual(shared.count, 12)
        func slots(_ window: LyricLayerCache.RollingWindow) -> [TimedWordKey: Double] {
            let prepared = SpokenTypography.prepare(.init(words: window.words, baseSize: 50, mood: .neutral,
                composition: .spoken, fontIdentity: LyricFontIdentity(settings: .shared), ordinals: window.ordinals),
                settings: .shared)
            return Dictionary(uniqueKeysWithValues: prepared.map { ($0.source.presentationID, $0.slot) })
        }
        let before = slots(early), after = slots(later)
        for word in shared {
            XCTAssertNotEqual(early.words.firstIndex(of: word), later.words.firstIndex(of: word))
            XCTAssertEqual(before[word.presentationID], after[word.presentationID], word.text)
        }
        // Neighbours still take well-separated places along the colours.
        let first = try XCTUnwrap(before[lines[2].words[0].presentationID])
        let second = try XCTUnwrap(before[lines[2].words[1].presentationID])
        XCTAssertGreaterThan(min(abs(first - second), 1 - abs(first - second)), 0.3)
    }

    @MainActor
    func testARollingRunOfCharactersStillArrivesCharacterByCharacter() throws {
        // A YRC line with no spaces, timed by character (an invented line),
        // rolls as its words rather than as one word for the whole line...
        let text = "夕焼けの帰り道"
        let words = text.enumerated().map { LyricsFetcher.Word(text: String($0.element), start: 10 + Double($0.offset) * 0.4,
                                                              end: 10.4 + Double($0.offset) * 0.4) }
        let road = LyricsFetcher.Line(start: 10, end: 12.8, text: text, words: words, isInstrumental: false, wordTimed: true)
        let window = LyricLayerCache.rollingWindow(around: 0, lines: [road], romanized: [:], time: 10.1, lead: 1.2,
                                                   display: .below)
        XCTAssertEqual(window.words.map(\.text), ["夕焼けの", "帰り道"])
        XCTAssertEqual(window.words.map(\.start), [words[0].start, words[4].start])
        let prepared = SpokenTypography.prepare(.init(words: window.words, baseSize: 60, mood: .neutral,
            composition: .spoken, fontIdentity: LyricFontIdentity(settings: .shared), parts: window.parts),
            settings: .shared)
        let run = try XCTUnwrap(prepared.first)
        XCTAssertEqual(run.parts, Array(words[0..<4]))
        XCTAssertEqual(prepared.last?.parts, Array(words[4...]))
        XCTAssertEqual(run.boundaries.count, run.parts.count + 1)
        // ...and each character of a word still shows on its own onset
        // rather than the whole word appearing at its first.
        func reveal(at time: Double) throws -> [Double] {
            let state = RollingTokenState.make(run, next: nil, rank: 0, time: time, composition: .spoken, baseSize: 60,
                stageWidth: 1000, intensity: 0, reduceMotion: false, arrival: .glide)
            return try XCTUnwrap(RollingTokenState.partReveal(run, time: time, composition: .spoken,
                runOpacity: state.presentation.opacity, intensity: 0, reduceMotion: false))
        }
        let early = try reveal(at: 10.5)
        XCTAssertEqual(early[0], 1)
        XCTAssertGreaterThan(early[1], 0.5, "the second character has begun")
        XCTAssertLessThan(early[3], 0.3, "later characters are not there yet: \(early)")
        XCTAssertEqual(try reveal(at: 13).min() ?? 0, 1, accuracy: 0.02)
        // A sweeping colour follows the characters too.
        XCTAssertEqual(RollingTokenState.sweep(run, time: 10.8), 2.0 / 4, accuracy: 0.02)
        XCTAssertEqual(RollingTokenState.sweep(run, time: 13), 1, accuracy: 1e-9)
        // A single word has nothing to reveal by part.
        let single = SpokenTypography.prepare(.init(words: [words[0]], baseSize: 60, mood: .neutral, composition: .spoken,
            fontIdentity: LyricFontIdentity(settings: .shared)), settings: .shared)[0]
        XCTAssertNil(RollingTokenState.partReveal(single, time: 10.5, composition: .spoken, runOpacity: 1,
                                                  intensity: 0, reduceMotion: false))
    }

    @MainActor
    func testAWholePhraseNeverLingersOverTheNextWordTimedLine() {
        let lines = [
            line(["the", "phrase"], start: 9, length: 1, wordTimed: false),
            line(["the", "next", "words"], start: 10.5),
            line(["a", "phrase"], start: 11, wordTimed: false),
        ]
        let rolls = { (row: Int) in lines[row].wordTimed }
        XCTAssertEqual(LyricLayer.timingHandoffPresence(after: 0, in: lines, time: 10.41), 0.5, accuracy: 0.001)
        XCTAssertFalse(LyricLayer.isLeaving(0, current: 1, lines: lines, time: 10.55, rolls: rolls))
        XCTAssertFalse(LyricLayer.isLeaving(0, current: 2, lines: lines, time: 11, rolls: rolls))
    }

    @MainActor
    func testAnUpcomingPreviewOnlyShowsAnIndividualWord() {
        let packed = line(["Hold the light"], start: 10)
        let separated = line(["Hold", "the", "light"], start: 10)
        XCTAssertFalse(LyricRuns.canPreviewFirstRun(packed.words, glue: []))
        XCTAssertFalse(LyricRuns.canPreviewFirstRun(separated.words, glue: [false, true, true]))
        XCTAssertTrue(LyricRuns.canPreviewFirstRun(separated.words, glue: []))
        XCTAssertFalse(LyricRuns.isPreviewWord("夕焼けの帰り道"))

        func opacity(_ line: LyricsFetcher.Line, at time: Double) -> Double {
            WordPresentation.make(word: line.words[0], index: 0, nextIndex: 0, time: time, composition: .spoken).opacity
        }
        XCTAssertEqual(opacity(packed, at: 9.9), 0)
        XCTAssertGreaterThan(opacity(separated, at: 9.9), 0)
        XCTAssertGreaterThan(opacity(packed, at: 10.2), 0)
    }

    // MARK: Duets and ad-libs

    func testAnOverlappingLineHandsOverWhenItsOwnWordsEnd() {
        // Line A is sung from 10 to 14; line B starts at 12, under it.
        var a = line(["I", "will", "wait", "for", "you"], start: 10, step: 0.8, length: 0.8)
        a.end = 14
        let b = line(["Oh", "yeah"], start: 12, step: 1, length: 1)
        let c = line(["and", "on"], start: 15)
        let lines = [a, b, c]
        XCTAssertEqual(LyricPhraseTiming.handOver(after: 0, in: lines), 14, accuracy: 1e-9)
        XCTAssertTrue(LyricPhraseTiming.isHeld(0, in: lines))
        XCTAssertEqual(LyricLineIndex.heldLine(before: 1, in: lines, at: 13), 0)
        // A's words after 12 are still revealed and it leaves after its last
        // word, not at 11.92 as when it handed over at B's start.
        let pose = LyricPhraseTiming(line: a, nextStart: LyricPhraseTiming.handOver(after: 0, in: lines), time: 13.5)
        XCTAssertEqual(pose.exit, 0)
        XCTAssertGreaterThan(pose.exitStart, 13.5)
        XCTAssertTrue(LyricPhraseTiming.stillLeaving(a, nextStart: 14, time: 14.2))
        XCTAssertFalse(LyricPhraseTiming.stillLeaving(a, nextStart: 14, time: 14.8))
        let motion = vc_word_motion(13.0, a.words[3].start, a.words[3].end, 0.8, pose.exitStart, 0.75, 7, 0, 0)
        XCTAssertGreaterThan(motion.reveal, 0.9)
        XCTAssertEqual(motion.leave, 0)
        // B is not held: it ends before C.
        XCTAssertFalse(LyricPhraseTiming.isHeld(1, in: lines))
        XCTAssertEqual(LyricPhraseTiming.handOver(after: 1, in: lines), 15)
    }

    func testOnlyARealOverlapHoldsALine() {
        // A last syllable ringing 30 ms into the next line is an ordinary
        // line change: no hold, so no shrinking and rising as it leaves.
        let ringing = line(["so", "long"], start: 20, step: 0.6, length: 0.63)   // ends 21.23
        let next = line(["next"], start: 21.2)
        XCTAssertFalse(LyricPhraseTiming.isHeld(0, in: [ringing, next]))
        XCTAssertEqual(LyricPhraseTiming.handOver(after: 0, in: [ringing, next]), 21.2)
        // Nor a few tenths of a second.
        let tail = line(["so", "long"], start: 20, step: 0.6, length: 0.9)       // ends 21.5
        XCTAssertFalse(LyricPhraseTiming.isHeld(0, in: [tail, next]))
        // Nor timings equal but for rounding, as YRC's sums give.
        var rounded = line(["爱你", "孤身"], start: 206.9, step: 0.76, length: 0.76)
        rounded.words[1].end = 208.42000000000002
        let yrcNext = line(["爱你"], start: 208.42)
        XCTAssertNil(LyricLineIndex.heldEnd(after: 0, in: [rounded, yrcNext]))
        XCTAssertFalse(LyricPhraseTiming.isHeld(0, in: [rounded, yrcNext]))
        // A note held well into the next line stays until it ends...
        let held = line(["oh", "stay"], start: 20, step: 0.6, length: 1.4)       // ends 22.0
        XCTAssertEqual(LyricLineIndex.heldEnd(after: 0, in: [held, next]) ?? 0, 22.0, accuracy: 1e-9)
        XCTAssertTrue(LyricPhraseTiming.isHeld(0, in: [held, next]))
        // ...and so does an answer that only starts as the next line does.
        var answer = line(["and", "you"], start: 20, step: 0.6, length: 0.5)
        answer.words.append(.init(text: "(yeah)", start: 21.2, end: 21.45))
        XCTAssertEqual(LyricLineIndex.heldEnd(after: 0, in: [answer, next]) ?? 0, 21.45, accuracy: 1e-9)
        // An answer of a moment is not worth holding the line for.
        answer.words[2].end = 21.3
        XCTAssertNil(LyricLineIndex.heldEnd(after: 0, in: [answer, next]))
    }

    func testAHeldLineIsCappedAndOtherLinesHandOverAsBefore() {
        // An ad-lib running far past the next line keeps the stage only so long.
        let long = line(["oh", "oh", "oh", "oh"], start: 10, step: 3, length: 3)
        let next = line(["next"], start: 12)
        XCTAssertEqual(LyricPhraseTiming.handOver(after: 0, in: [long, next]), 12 + LyricPhraseTiming.longestHold)
        // Line-only lyrics never overlap: they hand over as the next starts.
        var plain = long
        plain.wordTimed = false
        XCTAssertEqual(LyricPhraseTiming.handOver(after: 0, in: [plain, next]), 12)
        XCTAssertFalse(LyricPhraseTiming.isHeld(0, in: [plain, next]))
        // A line that has finished before the next starts, and the last line.
        let early = line(["done"], start: 8)
        XCTAssertEqual(LyricPhraseTiming.handOver(after: 0, in: [early, next]), 12)
        XCTAssertEqual(LyricPhraseTiming.handOver(after: 1, in: [early, next]), next.end + 0.8, accuracy: 1e-9)
    }

    // MARK: Where a held line goes

    private let fitsAsIs = LyricLineStack.Fit(available: CGSize(width: 1200, height: 700), userScale: 1, neutral: 1)

    private func item(_ height: CGFloat, presence: Double? = 1, held: Double? = nil,
                      fit: LyricLineStack.Fit? = nil) -> LyricLineStack.Item {
        .init(size: CGSize(width: 1000, height: height), line: .init(fit: fit ?? fitsAsIs, presence: presence), held: held)
    }

    @MainActor
    func testAHeldLineRisesClearOfTheLinesInTheMiddle() {
        let gap: CGFloat = 20
        func offsets(_ items: [LyricLineStack.Item]) -> [CGFloat] {
            LyricLineStack.offsets(items, stageHeight: 700, gap: gap)
        }
        // Above the line that started under it, by that line's own height:
        // its bottom a gap above the other's top, at 0.7 of its size.
        let placed = offsets([item(100, held: 1), item(200)])
        XCTAssertEqual(placed[1], 0)
        XCTAssertEqual(placed[0] + 100 * 0.7 / 2, -100 - gap, accuracy: 1e-9)
        // A taller line in the middle pushes it higher: a fixed share of the
        // stage left it over any line taller than that.
        let tall = offsets([item(100, held: 1), item(420)])
        XCTAssertEqual(tall[0] + 35, -210 - gap, accuracy: 1e-9)
        // Fitted lines count at the size they are drawn.
        let shrunk = LyricLineStack.Fit(available: CGSize(width: 500, height: 700), userScale: 1, neutral: 1)
        let fitted = offsets([item(100, held: 1), item(200, fit: shrunk)])
        XCTAssertEqual(fitted[0] + 35, -100 * 0.5 - gap, accuracy: 1e-6)
        // Never past the top of the stage.
        let full = offsets([item(100, held: 1), item(650)])
        XCTAssertEqual(full[0], -350 + 35, accuracy: 1e-9)
        // On its way there, it travels with its shift.
        let half = offsets([item(100, held: 0.5), item(200)])
        XCTAssertEqual(half[0], 0.5 * (-100 - gap - 100 * 0.85 / 2), accuracy: 1e-9)
        XCTAssertEqual(offsets([item(100, held: 0), item(200)])[0], 0)
        // The lines in the middle count by how present they are, so a line
        // arriving or leaving moves it smoothly; a preview does not count.
        let arriving = offsets([item(100, held: 1), item(200, presence: 0.5), item(600, presence: nil)])
        XCTAssertEqual(arriving[0] + 35, -50 - gap, accuracy: 1e-9)
        XCTAssertEqual(arriving[2], 0)
        // Two held lines stack, the latest nearest the middle.
        let two = offsets([item(100, held: 1), item(100, held: 1), item(200)])
        XCTAssertEqual(two[1] + 35, -100 - gap, accuracy: 1e-9)
        XCTAssertEqual(two[0] + 35, two[1] - 35 - gap, accuracy: 1e-9)
        // Lines that are not held, and a line not drawn, stay centred.
        XCTAssertEqual(offsets([item(200), item(200, presence: 0.3)]), [0, 0])
        XCTAssertEqual(offsets([.init(size: .zero, line: nil, held: 1), item(200)]), [0, 0])
    }

    /// The layout reads what each line says about itself, through the
    /// modifiers laid over it.
    @MainActor
    func testTheStackPlacesAHeldLineByItsMeasuredNeighbours() throws {
        var placed: [(LyricLineStack.Item, CGFloat)] = []
        LyricLineStack.recorder = { items, offsets in placed = Array(zip(items, offsets)) }
        defer { LyricLineStack.recorder = nil }
        let fit = fitsAsIs
        let view = LyricLineStack(stageSize: CGSize(width: 1000, height: 600), gap: 10) {
            Color.red.frame(width: 300, height: 100)
                .layoutValue(key: LyricLineStack.LineKey.self, value: .init(fit: fit, presence: 1))
                .modifier(HeldLineShift(travel: 1))
                .opacity(0.9)
            Color.blue.frame(height: 200)
                .layoutValue(key: LyricLineStack.LineKey.self, value: .init(fit: fit, presence: 1))
                .opacity(0.9)
            Color.clear
        }
        .frame(width: 1200, height: 800)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 0.25
        _ = renderer.cgImage
        XCTAssertEqual(placed.count, 3)
        XCTAssertEqual(placed.first?.0.held, 1)
        // Lines are set no wider than the stage, as their frame used to set them.
        XCTAssertEqual(placed[1].0.size.width, 1000, accuracy: 0.5)
        XCTAssertEqual(placed[1].0.size.height, 200, accuracy: 0.5)
        XCTAssertEqual(placed[0].1, -100 - 10 - 35, accuracy: 0.5)
        XCTAssertEqual(placed[1].1, 0)
        XCTAssertNil(placed[2].0.line)
    }

    /// A duet part held while two more lines start under it (A 10-14, B
    /// 12-13, C from 13.4): it stays aside the whole time. When C started,
    /// A became the line before last, which was drawn without the shift --
    /// back at full size in the middle, over B leaving and C arriving.
    @MainActor
    func testAHeldLineStaysAsideWhileLaterLinesStartUnderIt() throws {
        let a = line(["I", "will", "wait", "for", "you"], start: 10, step: 0.8, length: 0.8)
        let b = line(["Oh", "yeah"], start: 12, step: 0.5, length: 0.5)
        let c = line(["and", "on"], start: 13.4, step: 0.5, length: 0.5)
        let ringing = line(["so", "long"], start: 20, step: 0.6, length: 0.63)
        let next = line(["next", "line"], start: 21.2)
        try onStage([a, b, c, ringing, next]) { held in
            for time in [13.3, 13.45, 13.7] {
                let lines = held(time)
                XCTAssertEqual(lines.count, 1, "at \(time)")
                XCTAssertEqual(lines.first?.travel ?? 0, 1, accuracy: 0.01, "at \(time)")
                XCTAssertLessThan(lines.first?.offset ?? 0, -10, "above the middle at \(time)")
            }
            // A last syllable 30 ms over the next line is no duet: nothing moves aside.
            for time in [21.25, 21.4, 21.6] {
                XCTAssertEqual(held(time).count, 0, "at \(time)")
            }
        }
    }

    /// Renders the real layer at given times in Editorial, a layout that
    /// sets every line in the middle of the stage, and reports the held
    /// lines it placed.
    @MainActor
    private func onStage(_ lines: [LyricsFetcher.Line],
                         _ body: (_ held: (Double) -> [(travel: Double, offset: CGFloat)]) throws -> Void) throws {
        let music = FakeMusicApp()
        music.queue = [FakeMusicApp.Song(id: "00000000000000D1", name: "Held Line", artist: "Duet Probe", album: "Two Voices")]
        music.position = 12
        let harness = PlayerHarness(music: music)
        harness.start()
        let model = LyricsViewModel.shared, settings = Settings.shared, extras = LunalyrExtras.shared
        let saved = (lines: model.lines, plain: model.plainLines, notFound: model.notFound, loading: model.isLoading,
                     composition: settings.lyricComposition, hidden: extras.lyricsHidden)
        defer {
            model.lines = saved.lines
            model.plainLines = saved.plain
            model.notFound = saved.notFound
            model.isLoading = saved.loading
            settings.lyricComposition = saved.composition
            extras.lyricsHidden = saved.hidden
            LyricLineStack.recorder = nil
        }
        model.plainLines = []
        model.notFound = false
        model.isLoading = false
        model.lines = lines
        settings.lyricComposition = .editorial
        extras.lyricsHidden = false
        var placed: [(LyricLineStack.Item, CGFloat)] = []
        LyricLineStack.recorder = { items, offsets in placed = Array(zip(items, offsets)) }
        try body { time in
            placed = []
            let view = LyricLayer(time: time, baseSize: 40, maxWidth: 800, maxHeight: 450, mood: .neutral,
                                  accent: .orange, trackSeed: 1, wall: Date().timeIntervalSinceReferenceDate + 10)
                .environmentObject(harness.monitor)
                .frame(width: 1000, height: 600)
            let renderer = ImageRenderer(content: view)
            renderer.scale = 0.25
            _ = renderer.cgImage
            return placed.compactMap { item, offset in
                guard let travel = item.held, item.line != nil else { return nil }
                return (travel, offset)
            }
        }
    }

    // MARK: Rolling colours across glued lines

    @MainActor
    func testNeighbouringWordsAcrossGluedLinesKeepApartOnTheColours() throws {
        func characters(_ text: String, start: Double) -> LyricsFetcher.Line {
            let words = text.enumerated().map { LyricsFetcher.Word(text: String($0.element), start: start + Double($0.offset) * 0.3,
                                                                  end: start + Double($0.offset) * 0.3 + 0.3) }
            return .init(start: start, end: words.last?.end ?? start, text: text, words: words, isInstrumental: false,
                         wordTimed: true)
        }
        // Unspaced lines of 11 and 12 characters (invented), each rolling
        // as a few glued words: counted in characters, the next line's
        // first word sat a dozen places from the last, which can land on
        // almost the same colour. Places count the words shown.
        let lines = [characters("夕焼けの帰り道を歩いた", start: 10), characters("窓辺に座って海を見ていた", start: 14),
                     characters("朝の電車で手紙を読んだ", start: 21)]
        XCTAssertEqual(lines.map(\.words.count), [11, 12, 11])
        let counts = lines.map { LyricRuns.merged($0.words, glue: LyricRuns.rollingGlue(line: $0, words: $0.words)).words.count }
        XCTAssertEqual(counts, [3, 4, 4])
        XCTAssertEqual(LyricLayerCache.wordOffsets(lines: lines, romanized: [:], display: .below), [0, 3, 7, 11])
        let window = LyricLayerCache.rollingWindow(around: 2, lines: lines, romanized: [:], time: 21.5, lead: 1.2,
                                                   display: .below)
        XCTAssertEqual(window.words.count, 11)
        XCTAssertEqual(window.words.filter { window.parts[$0.presentationID] != nil }.count, 11, "every word is a glued run")
        let prepared = SpokenTypography.prepare(.init(words: window.words, baseSize: 50, mood: .neutral,
            composition: .spoken, fontIdentity: LyricFontIdentity(settings: .shared), ordinals: window.ordinals),
            settings: .shared)
        let slots = prepared.map(\.slot)
        for (first, second) in zip(slots, slots.dropFirst()) {
            XCTAssertGreaterThan(min(abs(first - second), 1 - abs(first - second)), 0.3, "\(slots)")
        }
        // Spaced words and punctuation: one place per word shown.
        var spaced = line(["Hold", "on", ",", "now"], start: 30)
        spaced.text = "Hold on , now"
        XCTAssertEqual(LyricLayerCache.wordOffsets(lines: [spaced, lines[0]], romanized: [:], display: .below), [0, 3, 6])
    }

    // MARK: Unspaced lines on a rolling stage

    /// Word-timed TTML as Music's own lyrics and Lyrics+ give a Japanese
    /// line: spans with nothing between them. An invented line.
    private static let unspacedTTML = #"<tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" itunes:timing="Word" xml:lang="ja"><body><div><p begin="10.000" end="13.000"><span begin="10.000" end="10.600">夕焼け</span><span begin="10.600" end="10.800">の</span><span begin="10.900" end="11.500">帰り道</span><span begin="11.500" end="11.700">を</span><span begin="11.800" end="12.400">歩い</span><span begin="12.400" end="13.000">た</span></p></div></body></tt>"#

    @MainActor
    func testARollingStageBuildsAnUnspacedLineWordByWord() throws {
        let lines = TTMLLyricParser.parse(Self.unspacedTTML, duration: 60)
        let line = try XCTUnwrap(lines.first)
        XCTAssertTrue(LyricsFetcher.validWordTiming(line))
        XCTAssertEqual(line.words.map(\.text), ["夕焼け", "の", "帰り道", "を", "歩い", "た"])
        // One run as written, so Scattered and Echo still set it unbroken...
        XCTAssertEqual(LyricRuns.labels(line.words, glue: LyricRuns.glue(line: line, words: line.words)), [line.text])
        // ...while Spoken, Cascade and Cutout build it word by word. As one
        // rolling word it was one label pressed on at the first syllable
        // with the whole line's reading under it.
        let readings = LyricRomanizer.readings(lines, options: .init(display: .below, language: .automatic,
                                                                     toneMarks: true, preferSaved: true))
        let window = LyricLayerCache.rollingWindow(around: 0, lines: lines, romanized: readings, time: 10.1, lead: 1.2,
                                                   display: .below)
        XCTAssertEqual(window.words.map(\.text), ["夕焼けの", "帰り道を", "歩いた"])
        XCTAssertEqual(window.words.map(\.start), [10, 10.9, 11.8])
        XCTAssertEqual(window.parts[window.words[1].presentationID]?.map(\.text), ["帰り道", "を"])
        // Each word has its own reading, and together they read the line.
        let shown = window.words.compactMap { window.readings[$0.presentationID] }
        XCTAssertEqual(shown.count, 3, "\(shown)")
        XCTAssertTrue(shown[0].hasPrefix("yuuyake"), "\(shown)")
        let byToken = Dictionary(zip(line.words.map(\.presentationID), try XCTUnwrap(readings[0])), uniquingKeysWith: { a, _ in a })
        XCTAssertEqual(shown.joined(separator: " "), LyricRuns.reading(of: line.words[...], readings: byToken))
        // While the first word is sung the last is not there yet; each
        // arrives on its own onset.
        let prepared = SpokenTypography.prepare(.init(words: window.words, baseSize: 60, mood: .neutral,
            composition: .cutout, fontIdentity: LyricFontIdentity(settings: .shared), readings: window.readings,
            parts: window.parts), settings: .shared)
        func opacity(_ index: Int, at time: Double) -> Double {
            WordPresentation.make(word: prepared[index].source, index: index,
                                  nextIndex: prepared.firstIndex { $0.source.start > time }, time: time, composition: .cutout,
                                  eligibleSince: index > 0 ? prepared[index - 1].source.start : -.infinity).opacity
        }
        XCTAssertGreaterThan(opacity(0, at: 10.4), 0.5)
        XCTAssertLessThanOrEqual(opacity(1, at: 10.4), 0.25, "at most the next word's faint preview")
        XCTAssertEqual(opacity(2, at: 10.4), 0)
        XCTAssertGreaterThan(opacity(1, at: 11.3), 0.5)
        XCTAssertEqual(opacity(2, at: 11.3), 0, accuracy: 0.25, "at most the next word's faint preview")
        XCTAssertGreaterThan(opacity(2, at: 12.3), 0.5)

        // A Chinese line timed by character and a Thai one by syllable roll
        // as their words too; Korean words, already spaced, as they are, and
        // Latin syllables as the word they spell.
        func sung(_ tokens: [String], text: String) -> LyricsFetcher.Line {
            let words = tokens.enumerated().map { LyricsFetcher.Word(text: $0.element, start: 20 + Double($0.offset) * 0.4,
                                                                    end: 20.4 + Double($0.offset) * 0.4) }
            return .init(start: 20, end: words.last?.end ?? 20, text: text, words: words, isInstrumental: false, wordTimed: true)
        }
        func rolled(_ line: LyricsFetcher.Line) -> [String] {
            LyricLayerCache.rollingWindow(around: 0, lines: [line], romanized: [:], time: 20.1, lead: 1.2, display: .below)
                .words.map(\.text)
        }
        XCTAssertEqual(rolled(sung(["我", "们", "一", "起", "走", "过", "春", "天"], text: "我们一起走过春天")),
                       ["我们", "一起", "走过", "春天"])
        XCTAssertEqual(rolled(sung(["แมว", "นอน", "บน", "โต๊ะ"], text: "แมวนอนบนโต๊ะ")), ["แมว", "นอน", "บน", "โต๊ะ"])
        XCTAssertEqual(rolled(sung(["고양", "이가", "창문", "을", "본다"], text: "고양이가 창문을 본다")), ["고양이가", "창문을", "본다"])
        XCTAssertEqual(rolled(sung(["So", "beau", "ti", "ful"], text: "So beautiful")), ["So", "beautiful"])
    }

    // MARK: Readings of a whole line on a rolling stage

    @MainActor
    func testAWholeLineReadingWrapsUnderItsRollingWord() throws {
        let font = Settings.shared.lyricFont(size: 30, hero: false, mood: .neutral)
        let long = LyricReadingInk(text: "Ya tebya ochen' lyublyu i ya budu zhdat' tebya vsegda", font: font)
        func size(_ view: some View) throws -> CGSize {
            let renderer = ImageRenderer(content: view)
            renderer.scale = 1
            return try XCTUnwrap(renderer.nsImage).size
        }
        let natural = try size(long)
        XCTAssertGreaterThan(natural.width, 400)
        let wrapped = try size(long.wrapping(at: 300))
        XCTAssertLessThanOrEqual(wrapped.width, 301)
        XCTAssertGreaterThan(wrapped.height, natural.height * 1.5, "set on more than one line")
        // A reading that fits is left exactly as it was.
        let short = LyricReadingInk(text: "Ya", font: font)
        XCTAssertEqual(try size(short.wrapping(at: 300)), try size(short))
        // On the stage: the unit's width stays within the reading's width.
        let word = SpokenTypography.prepare(.init(words: [.init(text: "Я", start: 1, end: 1.4)], baseSize: 60,
            mood: .neutral, composition: .spoken, fontIdentity: LyricFontIdentity(settings: .shared),
            readings: [LyricsFetcher.Word(text: "Я", start: 1, end: 1.4).presentationID: long.text]),
            settings: .shared)[0]
        let unit = try size(RollingUnit(token: word, readingWidth: 500))
        XCTAssertLessThanOrEqual(unit.width, 500 + 4 + 1)
    }

    /// A malformed source's line can start beyond what 64 bits of
    /// milliseconds hold; posing it, or a break before it, used to trap.
    func testHugeOrInvalidLineTimesNeverTrapTheSeeds() {
        XCTAssertEqual(LyricPhraseTiming.timeSeed(12.3456), UInt64(12_346), "Ordinary times seed as before")
        for start in [1e20, -1e20, .infinity, .nan] {
            _ = LyricPhraseTiming.timeSeed(start)
            let line = LyricsFetcher.Line(start: start, end: start, text: "hi", words: [], isInstrumental: false)
            _ = LyricPhraseTiming(line: line, nextStart: .infinity, time: 3)
            _ = VCVocalGap(start: start, end: start + 10, nextVocal: 1).instrumentalCue(at: start + 1, previewLead: 0.5)
        }
    }
}
