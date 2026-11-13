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

    /// A run of consecutive lines, as a contact sheet: each in its own shape.
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

/// Fable's per-line variety, its page layout and its phrase choreography,
/// without rendering. (The redesign of 14 Nov 2026 replaced the old pins:
/// five shapes and five accents with width-wrapped rows became four shapes
/// and one row per phrase; the accents (underline, ring, tile, star,
/// brackets) and the random per-word entrances are gone, and the landings
/// follow each word's role.)
@MainActor
final class FableStyleTests: XCTestCase {
    typealias Word = LyricsFetcher.Word

    func testNeighbouringLinesNeverShareAShape() {
        for seed in -60...400 {
            XCTAssertNotEqual(FableStyle.forLine(seed).shape, FableStyle.forLine(seed + 1).shape,
                              "lines \(seed) and \(seed + 1) share a shape")
        }
        XCTAssertEqual(Set((0..<4).map { FableStyle.forLine($0).shape }).count, 4, "Every shape comes round")
    }

    /// Neighbouring phrases land differently, and so does a line's first
    /// phrase from the line before's.
    func testNeighbouringPhrasesLandDifferently() {
        for seed in -10...10 {
            for phrase in 0..<6 {
                XCTAssertNotEqual(FableLanding.forPhrase(phrase, seed: seed), FableLanding.forPhrase(phrase + 1, seed: seed))
            }
            XCTAssertNotEqual(FableLanding.forPhrase(0, seed: seed), FableLanding.forPhrase(0, seed: seed + 1))
        }
    }

    func testStyleIsTheLinesOwn() {
        for seed in 0..<50 { XCTAssertEqual(FableStyle.forLine(seed), FableStyle.forLine(seed)) }
    }

    /// Rows of words of all sorts, in every shape, on both displays: no
    /// word's box meets another's, each stays in the page, and right-to-left
    /// pages begin on the right.
    func testLayoutKeepsWordsApart() {
        let pages: [([CGSize], [[Int]])] = [
            ([CGSize(width: 120, height: 80)], [[0]]),
            ([CGSize(width: 1900, height: 90), CGSize(width: 150, height: 90)], [[0, 1]]),
            ((0..<6).map { CGSize(width: 90 + CGFloat($0 * 37 % 120), height: 80 + CGFloat($0 % 2) * 30) }, [[0, 1, 2], [3, 4, 5]]),
            ((0..<8).map { CGSize(width: 60 + CGFloat($0 * 53 % 200), height: 70 + CGFloat($0 % 3) * 20) },
             [[0, 1, 2, 3], [4, 5, 6, 7]]),
            ((0..<4).map { _ in CGSize(width: 140, height: 170) }, [[0], [1, 2, 3]]),
        ]
        for shape in FableStyle.Shape.allCases {
            for (sizes, rows) in pages {
                for rtl in [false, true] {
                    let laid = FableLayout.arrange(sizes, rows: rows, shape: shape, rightToLeft: rtl, gap: 4, rowGap: 6)
                    XCTAssertEqual(laid.frames.count, sizes.count)
                    let box = CGRect(origin: .zero, size: laid.size).insetBy(dx: -0.5, dy: -0.5)
                    for (i, frame) in laid.frames.enumerated() {
                        XCTAssertTrue(box.contains(frame), "\(shape) word \(i) leaves its page")
                        XCTAssertEqual(frame.size, sizes[i])
                        for j in (i + 1)..<laid.frames.count {
                            XCTAssertFalse(frame.insetBy(dx: 0.5, dy: 0.5).intersects(laid.frames[j]),
                                           "\(shape): words \(i) and \(j) overlap")
                        }
                    }
                    if let row = rows.first, row.count > 1 {
                        XCTAssertEqual(laid.frames[row[0]].midX > laid.frames[row[1]].midX, rtl,
                                       "\(shape) reads the wrong way (rtl \(rtl))")
                    }
                    // Each phrase is its own row: a later row starts below the one before.
                    if rows.count > 1 {
                        let firstBottom = rows[0].map { laid.frames[$0].maxY }.max() ?? 0
                        let secondTop = rows[1].map { laid.frames[$0].minY }.min() ?? 0
                        XCTAssertGreaterThanOrEqual(secondTop, firstBottom - 0.5, "\(shape): rows overlap")
                    }
                }
            }
        }
    }

    private func plan(_ words: [Word]) -> (StageLyricView.FablePlan, [StageRun]) {
        let runs = StageRun.make(words, glue: [Bool](repeating: false, count: words.count))
        return (StageLyricView.makeFablePlan(runs, phrasing: StagePhrases.analyse(runs)), runs)
    }

    /// The review videos' long line: three phrases on two pages, turned at
    /// the pause before "until"; the held word is the hero.
    func testLongLineTurnsThePageAtThePause() {
        let (plan, runs) = plan(StagePhrasesTests.mixed)
        XCTAssertEqual(plan.pages.map(\.phrases), [[0, 1], [2]])
        XCTAssertEqual(plan.pages[0].rows.map { $0.map { runs[$0].text } },
                       [["and", "we", "keep", "running"], ["through", "the", "city", "lights"]], "One row per phrase")
        XCTAssertEqual(plan.hero.map { runs[$0].text }, "running", "The hero is the longest-held focal word")
        XCTAssertEqual(plan.roles.filter { $0 == .hero }.count, 1, "One hero per line")
        // The old page turns before the next is begun, never before its own last word has landed.
        let turn = plan.pages[1].turnStart
        XCTAssertLessThanOrEqual(turn, runs[8].start - StageMotion.lead)
        XCTAssertGreaterThan(turn, runs[7].start)
        XCTAssertEqual(StageLyricView.fablePageTurn(0, plan: plan, at: runs[8].start + 0.3, exitStart: .infinity), 1,
                       accuracy: 1e-9, "Turned away once the next page is being written")
        XCTAssertFalse(StageLyricView.fablePageShown(0, plan: plan, at: runs[8].start + 0.3, exitStart: .infinity))
        XCTAssertFalse(StageLyricView.fablePageShown(1, plan: plan, at: runs[7].start, exitStart: .infinity),
                       "A page shows from its first word")
    }

    /// Never more than two phrases or eight words on a page.
    func testPagesHoldAtMostTwoPhrases() {
        let words = FableRenderTests.cases[1].words
        let (plan, runs) = plan(words)
        XCTAssertGreaterThan(plan.pages.count, 2)
        for page in plan.pages {
            XCTAssertLessThanOrEqual(page.phrases.count, StageLyricView.fablePagePhrases)
            XCTAssertLessThanOrEqual(page.runs.count, StageLyricView.fablePageRuns)
        }
        XCTAssertEqual(plan.pages.flatMap(\.runs), Array(runs.indices), "Every word on one page, in order")
    }

    /// The stroke follows the word's role: a function word is a flick, a held
    /// word is written over the time it is sung, and a quick run is one
    /// stroke (each member written until the next one starts).
    func testStrokesFollowTheRoles() {
        let (plan, runs) = plan(StagePhrasesTests.mixed)
        let unit = StageMotion.fableUnit
        XCTAssertEqual(plan.roles[5], .function)
        XCTAssertEqual(plan.strokes[5].length, unit, accuracy: 1e-9, "\"the\" is a flick")
        XCTAssertGreaterThanOrEqual(plan.strokes[3].length, 1.0, "\"running\" is held, so written slowly")
        XCTAssertEqual(plan.strokes[0].length, runs[1].start - runs[0].start, accuracy: 1e-9, "The run is one stroke")
        XCTAssertEqual(plan.strokes[1].start + plan.strokes[1].length, plan.strokes[2].start, accuracy: 1e-9)
        for (index, run) in runs.enumerated() {
            XCTAssertEqual(plan.strokes[index].start, run.start - StageMotion.lead, accuracy: 1e-9, "Lands on its beat")
        }
        // Nothing is written before its beat; everything is written once its stroke ends.
        XCTAssertEqual(StageLyricView.fableWritten(plan.strokes[4], at: runs[4].start - 0.1), 0)
        XCTAssertEqual(StageLyricView.fableWritten(plan.strokes[4], at: plan.strokes[4].start + plan.strokes[4].length), 1)
    }

    /// The big landing belongs to the focal words (the hero's is biggest);
    /// content words hop, function words do not move; all land and hold still.
    func testLandingsFollowTheRoles() {
        let (plan, runs) = plan(StagePhrasesTests.mixed)
        func pose(_ id: Int, _ dt: Double, _ landing: FableLanding = .pop) -> FablePose {
            StageLyricView.fablePose(runs[id], role: plan.roles[id], landing: landing, lineTimed: false,
                                     at: runs[id].start + dt)
        }
        XCTAssertEqual(plan.roles[3], .hero)
        XCTAssertEqual(plan.roles[7], .focal)
        XCTAssertLessThan(pose(3, 0).scale, pose(7, 0).scale, "The hero pops from smaller")
        XCTAssertLessThan(pose(3, 0).spin, pose(7, 0).spin, "and swings further")
        XCTAssertLessThan(pose(7, 0, .drop).lift, 0, "A drop comes from above")
        XCTAssertGreaterThan(pose(6, 0).lift, 0, "A content word hops up")
        XCTAssertEqual(pose(6, 0).scale, 1)
        XCTAssertEqual(pose(5, 0), FablePose(), "A function word is only written in")
        XCTAssertTrue(plan.writes[5], "the: written by the pen")
        XCTAssertTrue(plan.writes[3], "running: held, so written as it is sung")
        XCTAssertFalse(plan.writes[6], "city: lands with its hop, no stroke")
        for id in runs.indices {
            XCTAssertEqual(pose(id, 0.6).lift, 0, accuracy: 1e-9, "\(runs[id].text) has landed")
            XCTAssertEqual(pose(id, 0.6).scale, 1, accuracy: 1e-9)
            XCTAssertEqual(pose(id, 0.6).spin, 0, accuracy: 1e-9)
        }
    }

    /// A fast line (12 words in 2 s): every page has turned away before the
    /// next page's first word lands, and never before its own last word.
    func testFastLinePagesNeverOverlap() {
        let words = StagePhrasesTests.even("I can't stop now we run run run into the night so fast", step: 0.17)
        let (plan, runs) = plan(words)
        XCTAssertGreaterThan(plan.pages.count, 1)
        for index in plan.pages.indices.dropFirst() {
            let page = plan.pages[index]
            let lastOld = plan.pages[index - 1].runs.map { runs[$0].start }.max() ?? 0
            XCTAssertGreaterThanOrEqual(page.turnStart, min(lastOld, page.start - StageMotion.lead - 0.1) - 1e-9)
            let gone = page.turnStart + page.turnLength
            XCTAssertLessThanOrEqual(gone, page.start - StageMotion.lead + 1e-9, "page \(index - 1) still showing")
            XCTAssertFalse(StageLyricView.fablePageShown(index - 1, plan: plan, at: page.start - StageMotion.lead,
                                                         exitStart: .infinity))
        }
    }

    /// A line-timed line is one page, its phrases written in together.
    func testLineTimedLineIsOnePage() {
        let words = "rain over the city can fly away tonight".split(separator: " ").map {
            Word(text: String($0), start: 2, end: 6)
        }
        let (plan, _) = plan(words)
        XCTAssertTrue(plan.lineTimed)
        XCTAssertEqual(plan.pages.count, 1)
        XCTAssertGreaterThan(plan.pages[0].rows.count, 1, "Still a row per phrase")
    }

    func testStrokeMaskStops() {
        XCTAssertEqual(FableStroke.stops(0).clear, 0)
        XCTAssertEqual(FableStroke.stops(1).solid, 1)
        let half = FableStroke.stops(0.5)
        XCTAssertLessThanOrEqual(half.solid, half.clear)
        XCTAssertGreaterThan(half.clear, 0)
        XCTAssertLessThan(half.solid, 1)
    }
}
