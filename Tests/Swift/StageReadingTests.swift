import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// "Below lyrics" readings in the stage layouts Hinge, Broadside, Portal,
/// Mosaic and Torque. Drawn in a real (never shown) window, as on the
/// desktop: `ImageRenderer` leaves out some of these layouts' 3D effects.
/// Preview sheets are written to LUNALYR_STAGE_READINGS_DIR when it is set
/// (pass it as TEST_RUNNER_LUNALYR_STAGE_READINGS_DIR). Settings come from an
/// isolated suite, never the real preferences.
@MainActor
final class StageReadingTests: XCTestCase {
    static let layouts: [LyricComposition] = [.portal, .hinge, .broadside, .mosaic, .torque]

    /// The MacBook's stage, as `LyricViewportMetrics` makes it.
    static let screen = CGSize(width: 1512, height: 982)
    static var stage: CGSize { LyricViewportMetrics(size: screen).stageSize }
    static var base: CGFloat { LyricViewportMetrics(size: screen).baseSize * 0.8 }

    struct Line {
        let name: String
        let words: [LyricsFetcher.Word]
        let glue: [Bool]
        let readings: [TimedWordKey: String]
        var lastStart: Double { words.map(\.start).max() ?? 1 }
    }

    static func words(_ texts: [String], step: Double) -> [LyricsFetcher.Word] {
        texts.enumerated().map { i, text in
            LyricsFetcher.Word(text: text, start: 1 + Double(i) * step, end: 1 + Double(i) * step + step * 0.9)
        }
    }

    /// 君の名前を呼んで夜空に消えた, sung a character at a time; a glued run
    /// carries its reading on its first character (`LyricReadingPairs.match`).
    static var japanese: Line {
        let w = words(["君", "の", "名", "前", "を", "呼", "ん", "で", "夜", "空", "に", "消", "え", "た"], step: 0.22)
        let glue = [false, true, false, true, true, false, true, true, false, true, true, false, true, true]
        let readings: [TimedWordKey: String] = [w[0].presentationID: "kimi no", w[2].presentationID: "namae wo",
                                                w[5].presentationID: "yonde", w[8].presentationID: "yozora ni",
                                                w[11].presentationID: "kieta"]
        return Line(name: "japanese", words: w, glue: glue, readings: readings)
    }

    static var korean: Line {
        let w = words(["사랑해", "너를", "영원히", "기억할게"], step: 0.5)
        var readings: [TimedWordKey: String] = [:]
        for (word, reading) in zip(w, ["saranghae", "neoreul", "yeongwonhi", "gieokhalge"]) { readings[word.presentationID] = reading }
        return Line(name: "korean", words: w, glue: [false, false, false, false], readings: readings)
    }

    static func settings() -> VersoCustom.Settings {
        Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.StageReadings"), notifications: NotificationCenter())
    }

    static func stage(_ line: Line, _ composition: LyricComposition, at time: Double, readings: [TimedWordKey: String]? = nil,
                      settings: VersoCustom.Settings, reduceMotion: Bool = false, seed: Int = 3) -> some View {
        StageLyricView(words: line.words, glue: line.glue, time: time, stageSize: stage, baseSize: base, mood: .neutral,
                       composition: composition, seed: seed, readings: readings ?? line.readings, settings: settings)
            .environment(\.lyricInk, LyricInk())
            .environment(\.lyricReduceMotion, reduceMotion)
            .environment(\.colorScheme, .dark)
            .frame(width: stage.width, height: stage.height)
    }

    /// Lays `view` out in a borderless window and draws it, as the desktop does.
    static func bitmap(_ view: some View, size: CGSize) throws -> NSBitmapImageRep {
        let hosting = NSHostingView(rootView: view.frame(width: size.width, height: size.height))
        let window = NSWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: .borderless,
                              backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.backgroundColor = .clear
        window.isOpaque = false
        window.contentView = hosting
        hosting.frame = CGRect(origin: .zero, size: size)
        hosting.layoutSubtreeIfNeeded()
        let rep = try XCTUnwrap(hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds))
        hosting.cacheDisplay(in: hosting.bounds, to: rep)
        window.close()
        return rep
    }

    /// Draws `view` with `ImageRenderer`, which keeps these layouts' 3D
    /// turns in place (a window's cached display draws a turned layout at
    /// its corner) but may leave out some effects; the sheets only.
    static func rendered(_ view: some View, size: CGSize) throws -> NSBitmapImageRep {
        let renderer = ImageRenderer(content: view.frame(width: size.width, height: size.height))
        renderer.scale = 1
        return NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
    }

    /// Writes a sheet per layout: the Japanese line as it builds and once
    /// sung, and the Korean line once sung, each over a dark wallpaper with
    /// the stage's edge marked.
    func testWritePreviewSheets() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_STAGE_READINGS_DIR"] else { return }
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let settings = Self.settings()
        let ja = Self.japanese, ko = Self.korean
        let cells: [(Line, Double, String)] = [
            (ja, 1 + 0.22 * 6, "building"), (ja, ja.lastStart + 0.9, "sung"), (ko, ko.lastStart + 0.9, "sung"),
        ]
        let stage = Self.stage, screen = Self.screen
        for composition in Self.layouts {
            let sheet = HStack(spacing: 4) {
                ForEach(Array(cells.enumerated()), id: \.offset) { _, cell in
                    ZStack {
                        LinearGradient(colors: [Color(red: 0.16, green: 0.1, blue: 0.3), Color(red: 0.05, green: 0.05, blue: 0.12)],
                                       startPoint: .topLeading, endPoint: .bottomTrailing)
                        // The stage's edge; the rest of the cell is the screen around it.
                        Rectangle().strokeBorder(Color.white.opacity(0.25), lineWidth: 2)
                            .frame(width: stage.width, height: stage.height)
                        Self.stage(cell.0, composition, at: cell.1, settings: settings)
                        Text("\(composition.title) · \(cell.0.name) · \(cell.2)")
                            .font(.system(size: 20)).foregroundStyle(.white.opacity(0.6))
                            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(10)
                    }
                    .frame(width: screen.width, height: screen.height)
                    .clipped()
                }
            }
            let size = CGSize(width: screen.width * 3 + 8, height: screen.height)
            let rep = try ProcessInfo.processInfo.environment["LUNALYR_STAGE_READINGS_WINDOW"] == nil
                ? Self.rendered(sheet.background(Color.black), size: size)
                : Self.bitmap(sheet.background(Color.black), size: size)
            try rep.representation(using: .png, properties: [:])?
                .write(to: URL(fileURLWithPath: dir + "/" + composition.rawValue + ".png"))
        }
        // Mosaic and Portal set each line their own way: six lines of each.
        for composition in [LyricComposition.mosaic, .portal] {
            let sheet = LazyVGrid(columns: Array(repeating: GridItem(.fixed(stage.width), spacing: 4), count: 3), spacing: 4) {
                ForEach(0..<6, id: \.self) { seed in
                    ZStack {
                        LinearGradient(colors: [Color(red: 0.16, green: 0.1, blue: 0.3), Color(red: 0.05, green: 0.05, blue: 0.12)],
                                       startPoint: .topLeading, endPoint: .bottomTrailing)
                        Self.stage(ja, composition, at: 1 + 0.22 * 10, settings: settings, seed: seed)
                    }
                    .frame(width: stage.width, height: stage.height)
                    .clipped()
                }
            }
            let size = CGSize(width: stage.width * 3 + 8, height: stage.height * 2 + 4)
            let rep = try Self.rendered(sheet.background(Color.black), size: size)
            try rep.representation(using: .png, properties: [:])?
                .write(to: URL(fileURLWithPath: dir + "/" + composition.rawValue + "-lines.png"))
        }
    }

    // MARK: Drawn

    /// How many pixels differ between two drawings of the same size.
    static func difference(_ a: NSBitmapImageRep, _ b: NSBitmapImageRep) -> Int {
        guard a.pixelsWide == b.pixelsWide, a.pixelsHigh == b.pixelsHigh else { return .max }
        var count = 0
        for y in stride(from: 0, to: a.pixelsHigh, by: 2) {
            for x in stride(from: 0, to: a.pixelsWide, by: 2) {
                guard let p = a.colorAt(x: x, y: y), let q = b.colorAt(x: x, y: y) else { continue }
                if abs(p.alphaComponent - q.alphaComponent) > 0.05 || abs(p.redComponent - q.redComponent) > 0.05
                    || abs(p.greenComponent - q.greenComponent) > 0.05 { count += 1 }
            }
        }
        return count
    }

    /// Each layout draws a line's readings under its words, and draws exactly
    /// what it drew before when there are none. (Before, only Fable drew
    /// them: Portal and the others showed no romanisation at all.)
    func testEveryLayoutDrawsReadings() throws {
        let settings = Self.settings()
        let stage = Self.stage
        for line in [Self.japanese, Self.korean] {
            for composition in Self.layouts {
                for reduceMotion in [false, true] {
                    let time = line.lastStart + 0.9
                    let with = try Self.bitmap(Self.stage(line, composition, at: time, settings: settings,
                                                          reduceMotion: reduceMotion), size: stage)
                    let without = try Self.bitmap(Self.stage(line, composition, at: time, readings: [:], settings: settings,
                                                             reduceMotion: reduceMotion), size: stage)
                    let label = "\(composition.title) · \(line.name)\(reduceMotion ? " · reduce motion" : "")"
                    XCTAssertGreaterThan(Self.difference(with, without), 150, "\(label): no reading was drawn")
                }
            }
        }
    }

    /// With "Below lyrics" off the layer hands over no readings, and a
    /// reading for a word not in the line is not drawn either.
    func testNoReadingsDrawNothing() throws {
        let settings = Self.settings()
        let line = Self.japanese
        let stranger = LyricsFetcher.Word(text: "雨", start: 40, end: 41)
        let time = line.lastStart + 0.9
        for composition in Self.layouts {
            let none = try Self.bitmap(Self.stage(line, composition, at: time, readings: [:], settings: settings), size: Self.stage)
            let other = try Self.bitmap(Self.stage(line, composition, at: time, readings: [stranger.presentationID: "ame"],
                                                   settings: settings), size: Self.stage)
            XCTAssertEqual(Self.difference(none, other), 0, "\(composition.title) drew a reading it was not given")
        }
        let run = StageRun.make(line.words, glue: line.glue)[0]
        XCTAssertNil(StageLyricView.reading(of: run, in: [:]))
        XCTAssertNil(StageLyricView.reading(of: run, in: [stranger.presentationID: "ame"]))
        XCTAssertEqual(StageLyricView.reading(of: run, in: line.readings), "kimi no")
    }

    /// A reading of a stage run is its parts' shares written together: each
    /// share carries the space before it where a new phrase starts
    /// (`LyricReadingPairs.parts`), so syllables of one word join.
    func testARunsReadingJoinsItsParts() {
        let words = Self.words(["ka", "ze"], step: 0.3)
        let run = StageRun.make(words, glue: [false, true])
        XCTAssertEqual(run.count, 1)
        XCTAssertEqual(StageLyricView.reading(of: run[0], in: [words[0].presentationID: "ka", words[1].presentationID: "ze "]),
                       "kaze")
        XCTAssertEqual(StageLyricView.reading(of: run[0], in: [words[0].presentationID: "ka", words[1].presentationID: " ze"]),
                       "ka ze")
    }

    /// Readings never reach the edge of the screen round the stage, on both
    /// of the user's displays, Japanese and Korean, as the line builds and
    /// once it is sung. (Broadside's strip and Mosaic's huge words run off
    /// the stage by design.)
    func testReadingsStayOnScreen() throws {
        let settings = Self.settings()
        for screen in [CGSize(width: 1512, height: 982), CGSize(width: 2560, height: 1440)] {
            let metrics = LyricViewportMetrics(size: screen)
            for line in [Self.japanese, Self.korean] {
                for composition in [LyricComposition.portal, .hinge, .torque] {
                    for time in [(1 + line.lastStart) / 2 + 0.6, line.lastStart + 0.9] {
                        let view = StageLyricView(words: line.words, glue: line.glue, time: time, stageSize: metrics.stageSize,
                                                  baseSize: metrics.baseSize * 0.8 * 1.5, mood: .neutral,
                                                  composition: composition, seed: 3, readings: line.readings, settings: settings)
                            .environment(\.lyricInk, LyricInk())
                            .frame(width: screen.width, height: screen.height)
                        let rep = try Self.rendered(view, size: screen)
                        XCTAssertLessThan(Self.edgeAlpha(rep, inset: 3), 0.02,
                                          "\(composition.title) · \(line.name) at \(time)s on \(Int(screen.width)) reaches the screen edge")
                    }
                }
            }
        }
    }

    /// The highest alpha in a band `inset` pixels wide round the edge.
    static func edgeAlpha(_ rep: NSBitmapImageRep, inset: Int) -> CGFloat {
        var most: CGFloat = 0
        for y in 0..<rep.pixelsHigh {
            let rowIsEdge = y < inset || y >= rep.pixelsHigh - inset
            for x in 0..<rep.pixelsWide where rowIsEdge || x < inset || x >= rep.pixelsWide - inset {
                most = max(most, rep.colorAt(x: x, y: y)?.alphaComponent ?? 0)
            }
        }
        return most
    }

    // MARK: Fitted

    /// Hinge counts its readings when it fits the corner to the stage.
    func testHingeFitCountsReadings() {
        let texts = ["君の", "名前を", "呼んで", "夜空に", "消えた", "君を", "探して"]
        let weights = [CGFloat](repeating: 1, count: texts.count)
        let stage = Self.stage
        let plain = StageLyricView.hingeFit(texts, weights: weights, settle: 1, unit: Self.base, stage: stage)
        let none = StageLyricView.hingeFit(texts, weights: weights, settle: 1, unit: Self.base, stage: stage,
                                           readings: [String?](repeating: nil, count: texts.count))
        let read = StageLyricView.hingeFit(texts, weights: weights, settle: 1, unit: Self.base, stage: stage,
                                           readings: ["kimi no", "namae wo", "yonde", "yozora ni", "kieta", "kimi wo", "sagashite"])
        XCTAssertEqual(plain.fit, none.fit, "No readings, no change")
        XCTAssertLessThan(read.fit, plain.fit, "The corner with its readings is set smaller to fit")
        XCTAssertEqual(read.shrink, plain.shrink)
    }

    /// Torque sets a word's reading with it in its row, so a row with
    /// readings is taller and the rows below it move down; a face too big
    /// for the stage is scaled to fit it whole.
    func testTorqueFaceMakesRoomForReadings() {
        let plainSizes = [CGSize](repeating: CGSize(width: 200, height: 100), count: 3)
        let rows = [[0, 1], [2]]
        let baselines: [CGFloat] = [80, 80, 80]
        let plain = TorqueFaceLayout.arrange(plainSizes, baselines: baselines, rows: rows, trailing: false,
                                             rightToLeft: false, wordGap: 10, rowGap: 0)
        let slot: CGFloat = 50
        let readSizes = plainSizes.map { CGSize(width: $0.width, height: $0.height + slot) }
        let read = TorqueFaceLayout.arrange(readSizes, baselines: baselines, rows: rows, trailing: false,
                                            rightToLeft: false, wordGap: 10, rowGap: 0)
        XCTAssertGreaterThan(read.frames[2].minY, plain.frames[2].minY, "The row below moves down for the reading")
        XCTAssertGreaterThanOrEqual(read.frames[2].minY, read.frames[0].maxY - 0.5, "Nothing overlaps a reading")
        XCTAssertGreaterThan(read.size.height, plain.size.height)
        // Too tall for a short stage: fitted whole.
        let available = CGSize(width: 800, height: 120)
        let fitted = TorqueTurn.transform(angle: 0, size: read.size, available: available)
        let box = CGRect(origin: .zero, size: read.size).applying(fitted)
        XCTAssertLessThanOrEqual(box.height, available.height + 0.5)
        XCTAssertEqual(box.midY, read.size.height / 2, accuracy: 0.5, "Fitted about its centre")
    }

    /// Broadside keeps its words on one line: a reading hangs below its
    /// word, and a label rides above the word itself, not its reading.
    func testBroadsideKeepsWordsOnTheLine() {
        let bounds = CGRect(x: 0, y: 0, width: 1000, height: 400)
        let slot: CGFloat = 40
        let measured = [CGSize(width: 200, height: 100 + slot), CGSize(width: 60, height: 30), CGSize(width: 220, height: 100)]
        let little = [false, true, false]
        let places = StageLyricView.broadsidePlaces(measured, little: little, slots: [slot, 0, 0], moves: [1, 1, 1],
                                                    unit: 40, in: bounds)
        XCTAssertEqual(places[0].y - slot / 2, bounds.midY, accuracy: 0.01, "The word, not its reading, sits on the line")
        XCTAssertEqual(places[2].y, bounds.midY, accuracy: 0.01)
        // The label rides on the shoulder of the word before: above its
        // letters (it used to lie across them), flush with its end.
        XCTAssertLessThanOrEqual(places[1].y + 15, bounds.midY - 100 * 0.36, "The label is clear of the word's letters")
        XCTAssertEqual(places[1].x + 30, places[0].x + 100, accuracy: 0.01, "The label is flush with the word's end")
        // Without the reading, the label sits in the same place: it rides on
        // the word, not on the word and its reading. (The label keeps its own
        // height, which now sets how far above the word its centre sits.)
        let plainSizes = [CGSize(width: 200, height: 100), measured[1], CGSize(width: 220, height: 100)]
        let plain = StageLyricView.broadsidePlaces(plainSizes, little: little, slots: [0, 0, 0], moves: [1, 1, 1],
                                                   unit: 40, in: bounds)
        XCTAssertEqual(plain[1].y, places[1].y, accuracy: 0.01)
    }

    /// Portal keeps a pill, reading and all, inside the stage.
    func testPortalKeepsPillsOnStage() {
        let bounds = CGRect(x: 0, y: 0, width: 1000, height: 600)
        let size = CGSize(width: 300, height: 200)
        let low = StageLyricView.keepOnStage(CGPoint(x: 500, y: 580), size: size, scale: 1, in: bounds)
        XCTAssertLessThanOrEqual(low.y + size.height / 2, bounds.maxY)
        let middle = CGPoint(x: 500, y: 300)
        XCTAssertEqual(StageLyricView.keepOnStage(middle, size: size, scale: 1, in: bounds), middle, "A pill on the stage stays put")
        let old = StageLyricView.keepOnStage(CGPoint(x: 950, y: 300), size: size, scale: StageLyricView.portalScale(rank: 1.5),
                                             in: bounds)
        XCTAssertLessThanOrEqual(old.x + size.width * 0.4 / 2, bounds.maxX)
    }

    /// Mosaic's stack sets its rows further apart when the line has readings.
    func testMosaicStackMakesRoomForReadings() {
        let plain = StageLyricView.mosaicStackStep(unit: 40, readings: false)
        XCTAssertEqual(plain, 40 * 0.85, accuracy: 0.001)
        XCTAssertEqual(StageLyricView.mosaicStackStep(unit: 40, readings: true) - plain,
                       StageLyricView.readingSlot(StageLyricView.mosaicSmallSize(unit: 40)), accuracy: 0.001)
    }

    /// A reading is legible beside its word: a share of it, never under 12 pt
    /// for a word that is itself larger.
    func testReadingSizes() {
        XCTAssertEqual(StageLyricView.readingSize(100), 45, accuracy: 0.001)
        XCTAssertEqual(StageLyricView.readingSize(20), 12, accuracy: 0.001)
        XCTAssertLessThanOrEqual(StageLyricView.readingSize(10), 10)
    }

    // MARK: Replace

    /// "Replace lyrics" hands the layouts the romanised words in place of
    /// the line's, grouped as the line was except that a Latin reading of a
    /// Japanese character is a word of its own: each layout then sets the
    /// romaji word by word, and draws it.
    func testReplaceSetsTheRomanisedWords() throws {
        let original = Self.words(["君", "の", "名", "前"], step: 0.3)
        let line = LyricsFetcher.Line(start: 1, end: 2.2, text: "君の名前", words: original, isInstrumental: false, wordTimed: true)
        let replaced = zip(original, ["kimi", "no", "na", "mae"]).map { LyricsFetcher.Word(text: $1, start: $0.start, end: $0.end) }
        let glue = LyricRuns.glue(line: line, words: replaced)
        XCTAssertEqual(StageRun.make(replaced, glue: glue).map(\.text), ["kimi", "no", "na", "mae"])
        let settings = Self.settings()
        let romaji = Line(name: "replace", words: replaced, glue: glue, readings: [:])
        let kanji = Line(name: "kanji", words: original, glue: LyricRuns.glue(line: line, words: original), readings: [:])
        for composition in Self.layouts {
            let a = try Self.bitmap(Self.stage(romaji, composition, at: 2.5, settings: settings), size: Self.stage)
            let b = try Self.bitmap(Self.stage(kanji, composition, at: 2.5, settings: settings), size: Self.stage)
            XCTAssertGreaterThan(Self.difference(a, b), 150, "\(composition.title) did not draw the romanised words")
        }
    }
}
