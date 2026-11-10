import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Fable rendered offscreen across the lines that used to break it, at the
/// sizes of both of the user's displays, with readings shown below. Sheets
/// are written to LUNALYR_STAGE_SNAPSHOT_DIR when it is set (pass it as
/// TEST_RUNNER_LUNALYR_STAGE_SNAPSHOT_DIR). Settings come from an isolated
/// suite, never the real preferences.
@MainActor
final class FableRenderTests: XCTestCase {
    /// A display, as `LyricViewportMetrics` turns it into a stage.
    struct Display {
        let name: String
        let screen: CGSize
        var stage: CGSize { LyricViewportMetrics(size: screen).stageSize }
        /// The size the layer hands a stage layout (`LyricLayerView`).
        var base: CGFloat { LyricViewportMetrics(size: screen).baseSize * 0.8 }
    }

    static let displays = [
        Display(name: "macbook", screen: CGSize(width: 1512, height: 982)),
        Display(name: "qhd27", screen: CGSize(width: 2560, height: 1440)),
    ]

    struct Case {
        let name: String
        let words: [LyricsFetcher.Word]
        var glue: [Bool]
        var readings: [TimedWordKey: String] = [:]
        var end: Double { (words.map(\.end).max() ?? 1) }
    }

    static func words(_ texts: [String], from start: Double = 1, step: Double = 0.5) -> [LyricsFetcher.Word] {
        texts.enumerated().map { i, text in
            LyricsFetcher.Word(text: text, start: start + Double(i) * step, end: start + Double(i) * step + step * 0.9)
        }
    }

    static func spaced(_ line: String, step: Double = 0.5) -> Case {
        let w = words(line.split(separator: " ").map(String.init), step: step)
        return Case(name: "", words: w, glue: [Bool](repeating: false, count: w.count))
    }

    static func named(_ name: String, _ c: Case) -> Case { Case(name: name, words: c.words, glue: c.glue, readings: c.readings) }

    static var cases: [Case] {
        var list: [Case] = []
        list.append(named("normal", spaced("Rain over the city can fly")))
        list.append(named("long", spaced("And if the night keeps calling out my name I will follow every echo down the empty streets until the morning finds us", step: 0.3)))
        list.append(named("short", spaced("Oh")))
        list.append(named("longword", spaced("Supercalifragilisticexpialidocious yeah")))
        // Japanese, glued syllables, with readings below.
        let ja = words(["今", "日", "も", "雨", "が", "降", "る", "け", "ど"], step: 0.35)
        var jaCase = Case(name: "japanese", words: ja, glue: [false, true, true, false, true, false, true, false, true])
        let jaReadings = ["kyo", "u", "mo", "ame", "ga", "fu", "ru", "ke", "do"]
        for (word, reading) in zip(ja, jaReadings) { jaCase.readings[word.presentationID] = reading }
        // A glued run carries one reading on its first token.
        jaCase.readings = [ja[0].presentationID: "kyou mo", ja[3].presentationID: "ame ga", ja[5].presentationID: "furu", ja[7].presentationID: "kedo"]
        list.append(jaCase)
        let ko = words(["사랑해", "너를", "영원히", "기억할게"], step: 0.5)
        var koCase = Case(name: "korean", words: ko, glue: [false, false, false, false])
        for (word, reading) in zip(ko, ["saranghae", "neoreul", "yeongwonhi", "gieokhalge"]) { koCase.readings[word.presentationID] = reading }
        list.append(koCase)
        list.append(named("arabic", spaced("أنا أحبك يا قمر الليل الجميل")))
        list.append(named("hebrew", spaced("אני אוהב אותך עד הסוף")))
        list.append(named("emoji", spaced("dancing 💃🏽 all night 🌙✨ with you 👨‍👩‍👧")))
        list.append(named("credits", spaced("Written by: Jane Doe, John Smith & The Orchestra")))
        list.append(named("instrumental", spaced("♪")))
        return list
    }

    static func settings() -> VersoCustom.Settings {
        Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("lunalyr.tests.fable"), notifications: NotificationCenter())
    }

    static func stage(_ c: Case, on display: Display, at time: Double, seed: Int = 3, settings: VersoCustom.Settings,
                      reduceMotion: Bool = false, exitStart: Double = .infinity) -> some View {
        StageLyricView(words: c.words, glue: c.glue, time: time, stageSize: display.stage, baseSize: display.base,
                       mood: .neutral, composition: .fable, seed: seed, exitStart: exitStart, readings: c.readings,
                       settings: settings)
            .environment(\.lyricInk, LyricInk())
            .environment(\.lyricReduceMotion, reduceMotion)
            .environment(\.colorScheme, .dark)
    }

    static func framed(_ c: Case, on display: Display, at time: Double, seed: Int = 3, settings: VersoCustom.Settings,
                       reduceMotion: Bool = false) -> some View {
        ZStack {
            LinearGradient(colors: [Color(red: 0.16, green: 0.1, blue: 0.3), Color(red: 0.05, green: 0.05, blue: 0.12)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            // The stage's own edge, so clipping is plain to see.
            Rectangle().strokeBorder(Color.white.opacity(0.25), lineWidth: 2)
            stage(c, on: display, at: time, seed: seed, settings: settings, reduceMotion: reduceMotion)
            Text("\(c.name) · \(display.name) · line \(seed) · \(String(format: "%.1f", time))s\(reduceMotion ? " · reduce motion" : "")")
                .font(.system(size: 20)).foregroundStyle(.white.opacity(0.6))
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(10)
        }
        .frame(width: display.stage.width, height: display.stage.height)
    }

    static func bitmap(_ view: some View, scale: CGFloat) throws -> NSBitmapImageRep {
        let renderer = ImageRenderer(content: view)
        renderer.scale = scale
        let image = try XCTUnwrap(renderer.cgImage)
        return NSBitmapImageRep(cgImage: image)
    }

    static func write(_ rep: NSBitmapImageRep, _ name: String) throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_STAGE_SNAPSHOT_DIR"] else { return }
        try FileManager.default.createDirectory(atPath: dir + "/fable", withIntermediateDirectories: true)
        try rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: dir + "/fable/" + name + ".png"))
    }

    /// The highest alpha in a band `inset` pixels wide round the edge.
    static func edgeAlpha(_ rep: NSBitmapImageRep, inset: Int) -> CGFloat {
        var most: CGFloat = 0
        let w = rep.pixelsWide, h = rep.pixelsHigh
        for y in 0..<h {
            let rowIsEdge = y < inset || y >= h - inset
            var x = 0
            while x < w {
                if rowIsEdge || x < inset || x >= w - inset {
                    most = max(most, rep.colorAt(x: x, y: y)?.alphaComponent ?? 0)
                    x += 1
                } else {
                    x = w - inset
                }
            }
        }
        return most
    }

    /// Every case on both displays, half sung and fully sung, with readings
    /// below. Nothing may reach the stage's edge: the band round it stays clear.
    func testFableStaysOnStageForEveryKindOfLine() throws {
        let settings = Self.settings()
        for display in Self.displays {
            for c in Self.cases {
                let lastStart = c.words.map(\.start).max() ?? 1
                for (label, time) in [("half", (1 + lastStart) / 2 + 0.05), ("full", lastStart + 1.0)] {
                    let rep = try Self.bitmap(Self.stage(c, on: display, at: time, settings: settings)
                        .frame(width: display.stage.width, height: display.stage.height), scale: 1)
                    let alpha = Self.edgeAlpha(rep, inset: 3)
                    XCTAssertLessThan(alpha, 0.02, "\(c.name) on \(display.name) (\(label)) reaches the stage edge")
                    if label == "full" {
                        try Self.write(try Self.bitmap(Self.framed(c, on: display, at: time, settings: settings), scale: 0.5),
                                       "\(display.name)-\(c.name)")
                    }
                }
            }
        }
    }

    /// One line, many renders: the same moment always draws the same pixels.
    func testSameLineSameMomentDrawsTheSame() throws {
        let display = Self.displays[0]
        let c = Self.cases[0]
        for seed in 0..<6 {
            let first = try Self.bitmap(Self.stage(c, on: display, at: 3.2, seed: seed, settings: Self.settings())
                .frame(width: display.stage.width, height: display.stage.height), scale: 0.5)
            let second = try Self.bitmap(Self.stage(c, on: display, at: 3.2, seed: seed, settings: Self.settings())
                .frame(width: display.stage.width, height: display.stage.height), scale: 0.5)
            XCTAssertEqual(first.tiffRepresentation, second.tiffRepresentation, "line \(seed) drew differently on a re-render")
        }
    }

    /// Once every word has landed the line holds still: nothing loops on while
    /// it is held, and it is gone once it has left.
    func testSettledLineHoldsStillAndLeaves() throws {
        let display = Self.displays[1]
        let settings = Self.settings()
        for c in [Self.cases[0], Self.cases[4]] {
            let lastStart = c.words.map(\.start).max() ?? 1
            func draw(_ time: Double, exit: Double = .infinity) throws -> NSBitmapImageRep {
                try Self.bitmap(Self.stage(c, on: display, at: time, settings: settings, exitStart: exit)
                    .frame(width: display.stage.width, height: display.stage.height), scale: 0.25)
            }
            XCTAssertEqual(try draw(lastStart + 1).tiffRepresentation, try draw(lastStart + 4).tiffRepresentation,
                           "\(c.name) still moves after its words have landed")
            let gone = try draw(lastStart + 3, exit: lastStart + 2)
            var most: CGFloat = 0
            for y in stride(from: 0, to: gone.pixelsHigh, by: 2) {
                for x in stride(from: 0, to: gone.pixelsWide, by: 2) { most = max(most, gone.colorAt(x: x, y: y)?.alphaComponent ?? 0) }
            }
            XCTAssertLessThan(most, 0.01, "\(c.name) is still drawn after it left")
        }
    }

    /// The settings Fable honours: Editorial typography changes the faces, a
    /// chosen word entrance changes how words arrive, and Cutout · Highlight
    /// puts sung words on tiles -- all inside the stage.
    func testHonoursEditorialTypographyAndChosenEntrances() throws {
        let display = Self.displays[0]
        let c = Self.cases[0]
        let plain = Self.settings()
        func draw(_ settings: VersoCustom.Settings, at time: Double) throws -> NSBitmapImageRep {
            try Self.bitmap(Self.stage(c, on: display, at: time, settings: settings)
                .frame(width: display.stage.width, height: display.stage.height), scale: 0.5)
        }
        let editorial = Self.settings()
        editorial.editorialTypography = true
        editorial.editorialAmount = 1
        XCTAssertNotEqual(try draw(plain, at: 5).tiffRepresentation, try draw(editorial, at: 5).tiffRepresentation)
        let pop = Self.settings()
        pop.wordArrivalByStyle = [LyricComposition.fable.rawValue: WordArrival.lift.rawValue]
        XCTAssertNotEqual(try draw(plain, at: 3.08).tiffRepresentation, try draw(pop, at: 3.08).tiffRepresentation)
        let tiles = Self.settings()
        tiles.wordArrivalByStyle = [LyricComposition.fable.rawValue: WordArrival.cutoutHighlight.rawValue]
        for (name, settings) in [("editorial", editorial), ("tiles", tiles)] {
            let rep = try Self.bitmap(Self.stage(c, on: display, at: 5, settings: settings)
                .frame(width: display.stage.width, height: display.stage.height), scale: 1)
            XCTAssertLessThan(Self.edgeAlpha(rep, inset: 3), 0.02, "\(name) reaches the stage edge")
            try Self.write(try Self.bitmap(Self.framed(c, on: display, at: 5, settings: settings), scale: 0.5), "setting-\(name)")
        }
    }

    /// A run of consecutive lines, as a contact sheet: each in its own shape and accent.
    func testWriteVarietySheet() throws {
        guard ProcessInfo.processInfo.environment["LUNALYR_STAGE_SNAPSHOT_DIR"] != nil else { return }
        let settings = Self.settings()
        let display = Self.displays[0]
        let c = Self.cases[0]
        for seed in 0..<6 {
            try Self.write(try Self.bitmap(Self.framed(c, on: display, at: 3.25, seed: seed, settings: settings), scale: 0.5),
                           "variety-line\(seed)")
        }
        for (label, reduce) in [("motion", false), ("reduce", true)] {
            for time in [1.15, 2.1] {
                try Self.write(try Self.bitmap(Self.framed(c, on: display, at: time, settings: settings, reduceMotion: reduce), scale: 0.5),
                               String(format: "entrance-%@-%.2f", label, time))
            }
        }
    }
}

/// Fable's per-line variety and its layout, without rendering.
final class FableStyleTests: XCTestCase {
    func testNeighbouringLinesNeverShareShapeOrAccent() {
        for seed in -60...400 {
            let this = FableStyle.forLine(seed), next = FableStyle.forLine(seed + 1)
            XCTAssertNotEqual(this.shape, next.shape, "lines \(seed) and \(seed + 1) share a shape")
            XCTAssertNotEqual(this.accent, next.accent, "lines \(seed) and \(seed + 1) share an accent")
        }
    }

    func testEveryPairingComesRoundBeforeAnyRepeats() {
        let styles = (0..<25).map { FableStyle.forLine($0) }
        XCTAssertEqual(Set(styles.map { "\($0.shape)-\($0.accent)" }).count, 25)
    }

    func testStyleIsTheLinesOwn() {
        for seed in 0..<50 { XCTAssertEqual(FableStyle.forLine(seed), FableStyle.forLine(seed)) }
    }

    func testHeroIsTheLongestWord() {
        XCTAssertNil(FableStyle.heroIndex(["Oh", "no"]))
        XCTAssertEqual(FableStyle.heroIndex(["Rain", "over", "the", "city"]), 0)
        XCTAssertEqual(FableStyle.heroIndex(["a", "beautiful", "day"]), 1)
    }

    /// Words of all sorts, in every shape, on both displays: no word's box
    /// meets another's, the block fits once scaled, and right-to-left lines
    /// begin on the right.
    func testLayoutKeepsWordsApartAndOnStage() {
        let stages = [CGSize(width: 1210, height: 707), CGSize(width: 2048, height: 1037)]
        let lines: [[CGSize]] = [
            [CGSize(width: 120, height: 80)],
            [CGSize(width: 1900, height: 90), CGSize(width: 150, height: 90)],
            (0..<6).map { CGSize(width: 90 + CGFloat($0 * 37 % 120), height: 80 + CGFloat($0 % 2) * 30) },
            (0..<30).map { CGSize(width: 60 + CGFloat($0 * 53 % 200), height: 70 + CGFloat($0 % 3) * 20) },
            (0..<4).map { _ in CGSize(width: 140, height: 170) },
        ]
        for stage in stages {
            for shape in FableStyle.Shape.allCases {
                for sizes in lines {
                    for rtl in [false, true] {
                        let laid = FableLayout.arrange(sizes, shape: shape, rightToLeft: rtl, available: stage, gap: 4, rowGap: 6)
                        XCTAssertEqual(laid.frames.count, sizes.count)
                        XCTAssertGreaterThan(laid.scale, 0.2)
                        XCTAssertLessThanOrEqual(laid.size.width * laid.scale, stage.width + 0.5)
                        XCTAssertLessThanOrEqual(laid.size.height * laid.scale, stage.height + 0.5)
                        let box = CGRect(origin: .zero, size: laid.size).insetBy(dx: -0.5, dy: -0.5)
                        for (i, frame) in laid.frames.enumerated() {
                            XCTAssertTrue(box.contains(frame), "\(shape) word \(i) leaves its block")
                            XCTAssertEqual(frame.size, sizes[i])
                            for j in (i + 1)..<laid.frames.count {
                                XCTAssertFalse(frame.insetBy(dx: 0.5, dy: 0.5).intersects(laid.frames[j]),
                                               "\(shape): words \(i) and \(j) overlap")
                            }
                        }
                        if sizes.count > 1, laid.frames[0].minY == laid.frames.map(\.minY).min() {
                            let firstRow = laid.frames.filter { abs($0.midY - laid.frames[0].midY) < 1 }
                            if firstRow.count > 1 {
                                XCTAssertEqual(laid.frames[0].midX > firstRow.map(\.midX).min()!, rtl,
                                               "\(shape) reads the wrong way (rtl \(rtl))")
                            }
                        }
                    }
                }
            }
        }
    }

    /// A short line sets large; a long one is wrapped before it is shrunk.
    func testLongLinesWrapBeforeShrinking() {
        let stage = CGSize(width: 1210, height: 707)
        let short = FableLayout.arrange([CGSize(width: 200, height: 90)], shape: .wave, rightToLeft: false,
                                        available: stage, gap: 4, rowGap: 6)
        XCTAssertEqual(short.scale, 1)
        let words = (0..<18).map { _ in CGSize(width: 180, height: 90) }
        let long = FableLayout.arrange(words, shape: .arch, rightToLeft: false, available: stage, gap: 4, rowGap: 6)
        XCTAssertGreaterThan(Set(long.frames.map { Int($0.minY / 10) }).count, 2, "a long line should take several rows")
        XCTAssertGreaterThan(long.scale, 0.6)
    }

    /// Short words then one very long one: the long word gets its own row
    /// rather than pulling a short word down beside it past the stage's edge.
    func testALoneLongLastWordKeepsItsRowWithinTheWidth() {
        let sizes = [100, 100, 100, 1150].map { CGSize(width: $0, height: 90) }
        let laid = FableLayout.arrange(sizes, shape: .wave, rightToLeft: false,
                                       available: CGSize(width: 1210, height: 707), gap: 4, rowGap: 6)
        XCTAssertEqual(laid.scale, 1, accuracy: 0.001)
    }
}
