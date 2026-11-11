import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// "Below lyrics" readings fill in syllable by syllable with the sung word
/// in every layout, not only the stage ones: Scattered, Editorial,
/// Constellation and Echo (one shaped run per glued word), the rolling
/// stages Spoken, Cascade and Cutout (one rolling word per run), and lyric
/// cards. Each token keeps its share of its run's reading; the shares
/// written together are exactly the reading drawn before, so nothing
/// reads or measures differently, and each share shows as its token is
/// sung. Uses the real Apple Music lines of `StageReadingRevealTests`.
@MainActor
final class ReadingSyllableRevealTests: XCTestCase {
    static let lines = StageReadingRevealTests.lines
    static let options = RomanizationOptions.Snapshot(display: .below, language: .automatic, toneMarks: true,
                                                      preferSaved: true)
    static let readings = LyricRomanizer.readings(lines, options: options)
    static let font = Font.system(size: 20)

    /// The part of `ink` that shows given `reveal` (nil: the whole reading).
    static func shown(_ ink: LyricReadingInk?, _ reveal: [Double]?) -> String {
        guard let ink else { return "" }
        guard let reveal = ink.revealing(reveal).reveal else { return ink.text }
        return zip(ink.shares, reveal).filter { $0.1 > 0.001 }.map(\.0.text).joined()
    }

    // MARK: The text is what it was

    /// For every line and every way a layout groups it (the line's runs,
    /// a constellation's phrases, a rolling stage's words), each run's
    /// shares written together are the reading `match` wrote on its first
    /// token before.
    func testSharesReadAsTheRunsReadingDid() {
        for (index, line) in Self.lines.enumerated() {
            let readings = Self.readings[index]
            let joined = LyricRuns.glue(line: line, words: line.words)
            for glue in [joined, LyricRuns.splittingWords(line.words, glue: joined),
                         LyricRuns.rollingGlue(line: line, words: line.words)] {
                let before = LyricReadingPairs.match(readings, to: line.words, glue: glue)
                let shares = LyricReadingPairs.shares(readings, to: line.words, glue: glue)
                for range in LyricRuns.ranges(glue, count: line.words.count) {
                    let tokens = Array(line.words[range])
                    let ink = LyricReadingInk(tokens: tokens, readings: shares, font: Self.font)
                    XCTAssertEqual(ink?.text, before[tokens[0].presentationID], "\(line.text) \(glue)")
                }
            }
        }
    }

    /// The reading draws at exactly the same size however much of it shows.
    func testAPartRevealedReadingMeasuresAsTheWholeOne() throws {
        let line = Self.lines[1]
        let glue = LyricRuns.glue(line: line, words: line.words)
        let shares = LyricReadingPairs.shares(Self.readings[1], to: line.words, glue: glue)
        let ink = try XCTUnwrap(LyricReadingInk(tokens: line.words, readings: shares, font: Self.font))
        func size(_ view: some View) throws -> CGSize {
            let renderer = ImageRenderer(content: view)
            renderer.scale = 1
            return try XCTUnwrap(renderer.nsImage).size
        }
        let whole = try size(LyricReadingInk(text: ink.text, font: Self.font))
        for time in [18.0, 19.8, 20.7, 21.0, 22.0] {
            let reveal = ink.shareOpacities(at: time, reduceMotion: false)
            XCTAssertEqual(try size(ink.revealing(reveal)), whole, "at \(time)")
            XCTAssertEqual(try size(ink.revealing(reveal).wrapping(at: 120)),
                           try size(LyricReadingInk(text: ink.text, font: Self.font).wrapping(at: 120)))
        }
    }

    // MARK: Scattered, Editorial, Constellation, Echo

    /// The run's reading at `time`, as `ScatteredLyricText` draws it.
    private func scattered(line index: Int, composition: LyricComposition, at time: Double,
                           reduceMotion: Bool = false) -> [String] {
        let line = Self.lines[index]
        var glue = LyricRuns.glue(line: line, words: line.words)
        if composition.hasConnections { glue = LyricRuns.splittingWords(line.words, glue: glue) }
        let key = ScatteredTypography.Key(words: line.words, baseSize: 60, style: .forLine(index), mood: .neutral,
            composition: composition, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared),
            readings: LyricReadingPairs.shares(Self.readings[index], to: line.words, glue: glue),
            lineEnd: line.end, glue: glue)
        let layout = ScatteredTypography.layout(key, settings: .shared)
        let prepared = layout.words
        let next = prepared.firstIndex { $0.source.start > time }
        let states = prepared.map {
            ScatteredWordFrame.state($0, nextIndex: next, time: time, composition: composition,
                                     reduceMotion: reduceMotion)
        }
        let reveals = Settings.shared.revealsWords(in: composition)
        return layout.runs.map { run in
            let frame = ScatteredRunFrame.make(run, states: states, reveals: reveals, ink: .white, time: time,
                                               reduceMotion: reduceMotion)
            return frame.readingOpacity > 0 ? Self.shown(run.reading, frame.readingReveal) : ""
        }
    }

    /// あなたを思い出す is one shaped run; its reading fills in as 思, い,
    /// 出 and す are sung (19.56, 20.57, 20.92, 21.13).
    func testScatteredReadingFillsInWithItsRun() {
        for composition in [LyricComposition.scattered, .editorial, .echo] {
            XCTAssertEqual(scattered(line: 1, composition: composition, at: 19.0), ["anata o"], "\(composition)")
            XCTAssertEqual(scattered(line: 1, composition: composition, at: 19.8), ["anata o omo"], "\(composition)")
            XCTAssertEqual(scattered(line: 1, composition: composition, at: 20.7), ["anata o omoi"], "\(composition)")
            XCTAssertEqual(scattered(line: 1, composition: composition, at: 21.0), ["anata o omoida"], "\(composition)")
            XCTAssertEqual(scattered(line: 1, composition: composition, at: 21.5), ["anata o omoidasu"], "\(composition)")
        }
        // Reduce Motion: a share is there from its onset, with no fade.
        XCTAssertEqual(scattered(line: 1, composition: .scattered, at: 20.50, reduceMotion: true), ["anata o omo"])
        XCTAssertEqual(scattered(line: 1, composition: .scattered, at: 20.55, reduceMotion: true), ["anata o omoi"])
    }

    /// A phrase sung over several words fills in over them too.
    func testScatteredReadingOfASharedPhrase() {
        XCTAssertEqual(scattered(line: 3, composition: .scattered, at: 27.8), ["wara"])
        XCTAssertEqual(scattered(line: 3, composition: .scattered, at: 28.4), ["waratte"])
        XCTAssertEqual(scattered(line: 3, composition: .scattered, at: 29.0), ["waratteta"])
    }

    /// A constellation parts the line into fragments; each fragment's
    /// reading fills in with it.
    func testConstellationFragmentsFillIn() {
        let early = scattered(line: 1, composition: .constellation, at: 19.8)
        let late = scattered(line: 1, composition: .constellation, at: 22.0)
        XCTAssertEqual(late.joined(separator: " "), "anata o omoidasu")
        XCTAssertTrue(early.contains { $0.hasSuffix("omo") }, "\(early)")
        XCTAssertFalse(early.joined().contains("omoi"), "\(early)")
    }

    /// The fade matches the stage layouts': a share is clear before its
    /// onset, fades in over `StageMotion.syllableFade`, then holds.
    func testShareFadesInAsAStageSyllableDoes() throws {
        let line = Self.lines[1]
        let shares = LyricReadingPairs.shares(Self.readings[1], to: line.words,
                                              glue: LyricRuns.glue(line: line, words: line.words))
        let ink = try XCTUnwrap(LyricReadingInk(tokens: line.words, readings: shares, font: Self.font))
        let onset = line.words[2].start
        func opacity(_ time: Double) -> Double { ink.shareOpacities(at: time, reduceMotion: false)?[2] ?? -1 }
        XCTAssertEqual(opacity(onset - 0.1), 0)
        XCTAssertGreaterThan(opacity(onset + 0.03), 0)
        XCTAssertLessThan(opacity(onset + 0.03), 1)
        XCTAssertEqual(opacity(onset + 0.5), 1, accuracy: 0.001)
        let stage = StageRun.Part(text: "い", start: onset, source: nil)
        let first = StageRun.Part(text: "思", start: line.words[1].start, source: nil)
        XCTAssertEqual(opacity(onset + 0.05), StageRun.partOpacity(1, of: [first, stage], at: onset + 0.05),
                       accuracy: 0.0001)
    }

    // MARK: Spoken, Cascade, Cutout

    /// The rolling word holding 思, and what of its reading shows at `time`.
    private func rolling(line index: Int, containing text: String, composition: LyricComposition,
                         at time: Double) throws -> (reading: String?, shown: String) {
        let line = Self.lines[index]
        let window = LyricLayerCache.rollingWindow(around: 0, lines: [line], romanized: [0: try XCTUnwrap(Self.readings[index])],
                                                   time: time, lead: 1.2, display: .below)
        let prepared = SpokenTypography.prepare(.init(words: window.words, baseSize: 60, mood: .neutral,
            composition: composition, fontIdentity: LyricFontIdentity(settings: .shared), readings: window.readings,
            parts: window.parts), settings: .shared)
        let token = try XCTUnwrap(prepared.first { $0.source.text.contains(text) })
        XCTAssertEqual(token.reading?.text, window.readings[token.source.presentationID], "the reading's text is unchanged")
        let reveal = RollingTokenState.partReveal(token, time: time, composition: composition, runOpacity: 1,
                                                  intensity: 0, reduceMotion: false)
        return (token.reading?.text, Self.shown(token.reading, token.reading?.shareOpacities(at: time, syllables: reveal,
                                                                                             reduceMotion: false)))
    }

    func testRollingWordsReadingFillsIn() throws {
        for composition in [LyricComposition.spoken, .cascade, .cutout] {
            XCTAssertEqual(try rolling(line: 1, containing: "思", composition: composition, at: 19.8).shown, "omo")
            XCTAssertEqual(try rolling(line: 1, containing: "思", composition: composition, at: 20.7).shown, "omoi")
            XCTAssertEqual(try rolling(line: 1, containing: "思", composition: composition, at: 21.0).shown, "omoida")
            let done = try rolling(line: 1, containing: "思", composition: composition, at: 22.0)
            XCTAssertEqual(done.shown, done.reading)
        }
        XCTAssertEqual(try rolling(line: 3, containing: "笑", composition: .cutout, at: 28.4).shown, "waratte")
    }

    // MARK: Lyric cards

    /// Cards pair readings as the wallpaper does: by run in the stage
    /// layouts (each run its own reading), by share elsewhere.
    func testCardsPairReadingsAsTheWallpaperDoes() {
        let line = Self.lines[1]
        let stage = LyricCard.Animation(line: line, composition: .portal, mood: .neutral, seed: 0,
                                        romanized: Self.readings[1], display: .below)
        XCTAssertEqual(StageRun.make(stage.words, glue: stage.glue).map { StageLyricView.reading(of: $0, in: stage.readings) },
                       ["anata o", "omoida", "su"])
        let editorial = LyricCard.Animation(line: line, composition: .editorial, mood: .neutral, seed: 0,
                                            romanized: Self.readings[1], display: .below)
        XCTAssertEqual(LyricReadingInk(tokens: editorial.words, readings: editorial.readings, font: Self.font)?.text,
                       "anata o omoidasu")
    }

    // MARK: Previews

    /// Editorial, Scattered, Constellation and Cutout part-way through
    /// 思い出す, written to LUNALYR_READING_PREVIEWS (pass it as
    /// TEST_RUNNER_LUNALYR_READING_PREVIEWS=<dir>). Skipped otherwise.
    func testRenderReadingPreviews() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let path = environment["LUNALYR_READING_PREVIEWS"] ?? environment["TEST_RUNNER_LUNALYR_READING_PREVIEWS"],
              !path.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_READING_PREVIEWS=<dir> to render the reading previews")
        }
        let folder = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let line = Self.lines[1]
        let readings = try XCTUnwrap(Self.readings[1])
        let stage = CGSize(width: 900, height: 360)
        let times = [19.2, 19.8, 20.7, 21.05, 21.8]
        for composition in [LyricComposition.editorial, .scattered, .constellation, .cutout] {
            let frames = times.map { time -> AnyView in
                let view: AnyView
                if composition.arrivalOnly {
                    let window = LyricLayerCache.rollingWindow(around: 0, lines: [line], romanized: [0: readings],
                                                               time: time, lead: 1.2, display: .below)
                    view = AnyView(LyricLineView(words: window.words, time: time, stageSize: stage, baseSize: 52,
                        composition: composition, seed: 7, readings: window.readings, lineEnd: line.end,
                        rollingHeightInTypeSizes: 6, following: window.following, ordinals: window.ordinals,
                        rollingLineEnds: window.lineEnds, parts: window.parts))
                } else {
                    var glue = LyricRuns.glue(line: line, words: line.words)
                    if composition.hasConnections { glue = LyricRuns.splittingWords(line.words, glue: glue) }
                    view = AnyView(LyricLineView(words: line.words, time: time, stageSize: stage, baseSize: 70,
                        composition: composition, seed: 1,
                        readings: LyricReadingPairs.shares(readings, to: line.words, glue: glue),
                        lineEnd: line.end, glue: glue))
                }
                return AnyView(ZStack {
                    LinearGradient(colors: [Color(red: 0.16, green: 0.1, blue: 0.3), Color(red: 0.05, green: 0.05, blue: 0.12)],
                                   startPoint: .topLeading, endPoint: .bottomTrailing)
                    view
                    Text("\(composition.title) · \(String(format: "%.2f", time)) s")
                        .font(.system(size: 16)).foregroundStyle(.white.opacity(0.6))
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(8)
                }
                .frame(width: stage.width, height: stage.height)
                .clipped())
            }
            let sheet = VStack(spacing: 4) { ForEach(frames.indices, id: \.self) { frames[$0] } }
                .environment(\.lyricInk, LyricInk())
                .environment(\.colorScheme, .dark)
            let renderer = ImageRenderer(content: sheet)
            renderer.scale = 1
            let image = try XCTUnwrap(renderer.cgImage)
            let data = try XCTUnwrap(NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]))
            try data.write(to: folder.appendingPathComponent("\(composition.rawValue).png"))
        }
    }
}
