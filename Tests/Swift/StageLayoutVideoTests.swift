import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Review videos of the six stage layouts, written only when
/// LUNALYR_STAGE_VIDEO_DIR is set (pass it as TEST_RUNNER_LUNALYR_STAGE_VIDEO_DIR);
/// skipped otherwise. Run it with Scripts/render-stage-videos.sh, which also
/// encodes the frames to MP4 with ffmpeg (test code cannot run it).
///
/// Optional: LUNALYR_STAGE_VIDEO_LAYOUTS ("hinge,portal"; default all six)
/// and LUNALYR_STAGE_VIDEO_SIZES ("small,desk"; default both).
///
/// For every layout, size and line it writes 30 fps PNG frames to
/// `<dir>/<size>/<layout>/<line>/frames/NNNN.png`: the line arriving, being
/// sung, leaving, and the start of the line after it, drawn through
/// `LyricLineView` as the wallpaper draws a word-timed stage line (its
/// stage size, base size, mood, seed, exit start, glue and readings, and the
/// line's own presence, swell and slide from `LyricPhraseTiming`). Beside
/// them, `<line>-sheet.png` (seven moments), and per size and line a sheet
/// with one row per layout: `<dir>/<size>/sheet-<line>.png`.
///
/// Drawn in a real, never shown window as on the desktop, except Hinge:
/// `ImageRenderer` leaves out some 3D effects (Torque's turns), but a
/// window's cached display draws a Hinge word that is mid-swing a second
/// time, flat. Change `renderer(for:)` if a redesigned layout needs the other.
/// Settings are read from the shared Debug-domain instance, as the wallpaper
/// reads them; nothing is written.
@MainActor
final class StageLayoutVideoTests: XCTestCase {
    struct Clip {
        let name: String
        let line: LyricsFetcher.Line
        let glue: [Bool]
        let readings: [TimedWordKey: String]
        let next: LyricsFetcher.Line
        let nextGlue: [Bool]
        let nextReadings: [TimedWordKey: String]
        let hue: Double
    }

    struct Size {
        let name: String
        let stage: CGSize
        let base: CGFloat
    }

    static let sizes = [Size(name: "desk", stage: CGSize(width: 1400, height: 520), base: 64),
                        Size(name: "small", stage: CGSize(width: 640, height: 360), base: 46)]

    static let layouts: [LyricComposition] = [.hinge, .broadside, .portal, .mosaic, .torque, .fable]

    static let fps = 30.0

    enum Renderer { case window, image }

    static func renderer(for composition: LyricComposition) -> Renderer {
        composition == .hinge ? .image : .window
    }

    // MARK: Lines

    static func line(_ words: [LyricsFetcher.Word]) -> LyricsFetcher.Line {
        let start = words.map(\.start).min() ?? 0
        let end = (words.map(\.end).max() ?? start) + 0.3
        return LyricsFetcher.Line(start: start, end: end, text: words.map(\.text).joined(separator: " "),
                                  words: words, isInstrumental: false, wordTimed: true)
    }

    /// A following line starting a second after `line`'s last word ends.
    static func follower(_ texts: [String], after line: LyricsFetcher.Line, step: Double = 0.4) -> [LyricsFetcher.Word] {
        let start = (line.words.map(\.end).max() ?? line.end) + 1.0
        return texts.enumerated().map { i, text in
            LyricsFetcher.Word(text: text, start: start + Double(i) * step, end: start + Double(i) * step + step * 0.85)
        }
    }

    static var clips: [Clip] {
        // (a) The short line, evenly sung.
        let short = line(StagePhrasesTests.even("we keep dreaming under neon rain"))
        let shortNext = line(follower(["hold", "me", "close", "tonight"], after: short))
        // (b) Twelve words: a quick run, a held word, a 0.6 s pause.
        let long = line(StagePhrasesTests.mixed)
        let longNext = line(follower(["hold", "me", "close", "tonight"], after: long))
        // (c) Japanese, sung a character at a time, with readings.
        let japanese = StageReadingTests.japanese
        let jp = line(japanese.words)
        let jpNextWords = follower(["\u{661F}", "\u{304C}", "\u{5149}", "\u{308B}"], after: jp, step: 0.3)
        let jpNext = line(jpNextWords)
        let jpNextReadings: [TimedWordKey: String] = [jpNextWords[0].presentationID: "hoshi ga",
                                                      jpNextWords[2].presentationID: "hikaru"]
        func flat(_ line: LyricsFetcher.Line) -> [Bool] { [Bool](repeating: false, count: line.words.count) }
        return [
            Clip(name: "short", line: short, glue: flat(short), readings: [:], next: shortNext, nextGlue: flat(shortNext),
                 nextReadings: [:], hue: 0.72),
            Clip(name: "long", line: long, glue: flat(long), readings: [:], next: longNext, nextGlue: flat(longNext),
                 nextReadings: [:], hue: 0.58),
            Clip(name: "japanese", line: jp, glue: japanese.glue, readings: japanese.readings, next: jpNext,
                 nextGlue: [false, true, false, true], nextReadings: jpNextReadings, hue: 0.03),
        ]
    }

    /// The span a clip covers: from just before its first word to well into the next line.
    static func span(of clip: Clip) -> ClosedRange<Double> {
        (clip.line.start - 0.3)...(clip.next.start + 1.8)
    }

    /// The seven moments of a clip's sheet: through the line, its exit, the next line.
    static func moments(of clip: Clip) -> [Double] {
        let first = clip.line.words.map(\.start).min() ?? clip.line.start
        let last = clip.line.words.map(\.start).max() ?? clip.line.start
        let exit = exitStart(of: [clip.line, clip.next], row: 0, at: last)
        let start: Double = first + 0.25
        let reach: Double = last + 0.3 - start
        var times: [Double] = (0..<4).map { (step: Int) -> Double in
            let fraction: Double = Double(step) / 3
            return start + reach * fraction
        }
        times += [exit + 0.12, exit + 0.3, clip.next.start + 0.7]
        return times
    }

    static func exitStart(of lines: [LyricsFetcher.Line], row: Int, at time: Double) -> Double {
        let pose = LyricPhraseTiming(line: lines[row], nextStart: LyricPhraseTiming.handOver(after: row, in: lines), time: time)
        return LyricLayer.stageExitStart(lines[row], pose: pose)
    }

    // MARK: Drawing

    /// One stage line as `LyricLayer` draws a word-timed line in a stage
    /// layout: its presence, swell and slide, and the stage exit start.
    @ViewBuilder
    static func stageLine(_ lines: [LyricsFetcher.Line], row: Int, glue: [Bool], readings: [TimedWordKey: String],
                          composition: LyricComposition, size: Size, seed: Int, time: Double) -> some View {
        let line = lines[row]
        let pose = LyricPhraseTiming(line: line, nextStart: LyricPhraseTiming.handOver(after: row, in: lines), time: time)
        let settings = Settings.shared
        let handsOffPreview = settings.revealsWords(in: composition) && settings.previewsNextWord(in: composition)
        let presence = handsOffPreview ? 1 : pose.progress
        let swell: CGFloat = 0.965 + 0.035 * pose.arrival - 0.012 * pose.exit
        let arrivalSlide: Double = (1 - pose.arrival) * 0.20
        let exitSlide: Double = pose.exit * 0.10
        let slide: CGFloat = CGFloat(arrivalSlide - exitSlide) * size.base
        if time >= line.start, time < LyricLayer.stageExitStart(line, pose: pose) + 1.5 {
            LyricLineView(words: line.words, time: time, stageSize: size.stage, baseSize: size.base, mood: .neutral,
                          composition: composition, seed: seed, readings: readings, lineEnd: line.end,
                          exitStart: LyricLayer.stageExitStart(line, pose: pose), glue: glue)
                .frame(width: size.stage.width, height: size.stage.height)
                .opacity(presence)
                .scaleEffect(swell)
                .offset(y: slide)
        }
    }

    /// An album-like dark backdrop: a deep two-tone gradient with a soft glow.
    static func backdrop(hue: Double) -> some View {
        ZStack {
            LinearGradient(colors: [Color(hue: hue, saturation: 0.5, brightness: 0.30),
                                    Color(hue: (hue + 0.08).truncatingRemainder(dividingBy: 1), saturation: 0.55, brightness: 0.12),
                                    Color(hue: hue, saturation: 0.4, brightness: 0.05)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            RadialGradient(colors: [Color(hue: (hue + 0.95).truncatingRemainder(dividingBy: 1), saturation: 0.6, brightness: 0.5)
                                        .opacity(0.35), .clear],
                           center: UnitPoint(x: 0.72, y: 0.3), startRadius: 0, endRadius: 520)
        }
    }

    static func frame(_ clip: Clip, composition: LyricComposition, size: Size, time: Double) -> some View {
        let lines = [clip.line, clip.next]
        return ZStack {
            backdrop(hue: clip.hue)
            stageLine(lines, row: 0, glue: clip.glue, readings: clip.readings, composition: composition, size: size,
                      seed: 3, time: time)
            stageLine(lines, row: 1, glue: clip.nextGlue, readings: clip.nextReadings, composition: composition, size: size,
                      seed: 4, time: time)
            Text(String(format: "%@  %@  %.2fs", composition.rawValue, clip.name, time))
                .font(.system(size: max(10, size.base * 0.18), design: .monospaced))
                .foregroundStyle(.white.opacity(0.35))
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomTrailing)
                .padding(8)
        }
        .frame(width: size.stage.width, height: size.stage.height)
        .clipped()
        .environment(\.lyricInk, LyricInk())
        .environment(\.lyricReduceMotion, false)
        .environment(\.colorScheme, .dark)
    }

    /// Draws `rep` into a new bitmap of exactly `size` pixels (a window's
    /// cache is at the screen's scale).
    static func exact(_ rep: NSBitmapImageRep, size: CGSize) throws -> NSBitmapImageRep {
        let out = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width), pixelsHigh: Int(size.height),
                                                 bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                                 colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0))
        out.size = size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: out)
        NSGraphicsContext.current?.imageInterpolation = .high
        NSColor.black.setFill()
        NSRect(origin: .zero, size: size).fill()
        rep.draw(in: NSRect(origin: .zero, size: size))
        NSGraphicsContext.restoreGraphicsState()
        return out
    }

    /// Draws views of one size, reusing one window for the whole clip.
    final class Painter {
        let size: CGSize
        let renderer: Renderer
        private var window: NSWindow?
        private var hosting: NSHostingView<AnyView>?

        init(size: CGSize, renderer: Renderer) {
            self.size = size
            self.renderer = renderer
        }

        @MainActor
        func draw(_ view: some View) throws -> NSBitmapImageRep {
            switch renderer {
            case .image:
                let renderer = ImageRenderer(content: view.frame(width: size.width, height: size.height))
                renderer.scale = 1
                let image = try XCTUnwrap(renderer.nsImage)
                let rep = try XCTUnwrap(NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation)))
                return try StageLayoutVideoTests.exact(rep, size: size)
            case .window:
                let root = AnyView(view.frame(width: size.width, height: size.height))
                if hosting == nil {
                    let hosting = NSHostingView(rootView: root)
                    let window = NSWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: .borderless,
                                          backing: .buffered, defer: false)
                    window.isReleasedWhenClosed = false
                    window.backgroundColor = .black
                    window.contentView = hosting
                    hosting.frame = CGRect(origin: .zero, size: size)
                    self.hosting = hosting
                    self.window = window
                } else {
                    hosting?.rootView = root
                }
                let hosting = try XCTUnwrap(hosting)
                hosting.layoutSubtreeIfNeeded()
                let rep = try XCTUnwrap(hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds))
                hosting.cacheDisplay(in: hosting.bounds, to: rep)
                return try StageLayoutVideoTests.exact(rep, size: size)
            }
        }

        @MainActor
        func close() { window?.close(); window = nil; hosting = nil }
    }

    static func writePNG(_ rep: NSBitmapImageRep, to path: String) throws {
        let data = try XCTUnwrap(rep.representation(using: .png, properties: [:]))
        try data.write(to: URL(fileURLWithPath: path))
    }

    /// One row of images side by side (and rows stacked), 2 px apart.
    static func sheet(_ rows: [[NSBitmapImageRep]], cell: CGSize) throws -> NSBitmapImageRep {
        let columns = rows.map(\.count).max() ?? 0
        let width = CGFloat(columns) * cell.width + CGFloat(max(0, columns - 1)) * 2
        let height = CGFloat(rows.count) * cell.height + CGFloat(max(0, rows.count - 1)) * 2
        let out = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(width), pixelsHigh: Int(height),
                                                 bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                                 colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0))
        out.size = CGSize(width: width, height: height)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: out)
        NSGraphicsContext.current?.imageInterpolation = .high
        NSColor.black.setFill()
        NSRect(x: 0, y: 0, width: width, height: height).fill()
        for (r, row) in rows.enumerated() {
            for (c, rep) in row.enumerated() {
                let y = height - CGFloat(r + 1) * cell.height - CGFloat(r) * 2
                rep.draw(in: NSRect(x: CGFloat(c) * (cell.width + 2), y: y, width: cell.width, height: cell.height))
            }
        }
        NSGraphicsContext.restoreGraphicsState()
        return out
    }

    // MARK: The test

    static func chosen<T>(_ all: [T], env: String, name: (T) -> String) -> [T] {
        guard let list = ProcessInfo.processInfo.environment[env]?.lowercased(), !list.isEmpty else { return all }
        let wanted = Set(list.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) })
        return all.filter { wanted.contains(name($0).lowercased()) }
    }

    func testWriteStageLayoutVideoFrames() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let dir = environment["LUNALYR_STAGE_VIDEO_DIR"], !dir.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_STAGE_VIDEO_DIR (or run Scripts/render-stage-videos.sh) to write the videos")
        }
        let layouts = Self.chosen(Self.layouts, env: "LUNALYR_STAGE_VIDEO_LAYOUTS") { $0.rawValue }
        let sizes = Self.chosen(Self.sizes, env: "LUNALYR_STAGE_VIDEO_SIZES") { $0.name }
        XCTAssertFalse(layouts.isEmpty, "No layout matches LUNALYR_STAGE_VIDEO_LAYOUTS")
        XCTAssertFalse(sizes.isEmpty, "No size matches LUNALYR_STAGE_VIDEO_SIZES")
        let fileManager = FileManager.default
        for size in sizes {
            // Sheet cells: about 460 px wide.
            let shrink = min(1, 460 / size.stage.width)
            let cell = CGSize(width: (size.stage.width * shrink).rounded(), height: (size.stage.height * shrink).rounded())
            for clip in Self.clips {
                var rows: [[NSBitmapImageRep]] = []
                for composition in layouts {
                    let folder = "\(dir)/\(size.name)/\(composition.rawValue)/\(clip.name)"
                    let frames = folder + "/frames"
                    try? fileManager.removeItem(atPath: frames)
                    try fileManager.createDirectory(atPath: frames, withIntermediateDirectories: true)
                    let painter = Painter(size: size.stage, renderer: Self.renderer(for: composition))
                    defer { painter.close() }
                    let span = Self.span(of: clip)
                    let count = Int(((span.upperBound - span.lowerBound) * Self.fps).rounded(.up))
                    let moments = Self.moments(of: clip)
                    let momentFrames = moments.map { Int((($0 - span.lowerBound) * Self.fps).rounded()) }
                    var picked: [Int: NSBitmapImageRep] = [:]
                    for index in 0...count {
                        try autoreleasepool {
                            let time = span.lowerBound + Double(index) / Self.fps
                            let rep = try painter.draw(Self.frame(clip, composition: composition, size: size, time: time))
                            try Self.writePNG(rep, to: frames + String(format: "/%04d.png", index))
                            if momentFrames.contains(index) { picked[index] = rep }
                        }
                    }
                    let row = momentFrames.compactMap { picked[$0] }
                    XCTAssertEqual(row.count, moments.count, "\(composition.rawValue) \(clip.name)")
                    try Self.writePNG(try Self.sheet([row], cell: cell), to: "\(dir)/\(size.name)/\(composition.rawValue)/\(clip.name)-sheet.png")
                    rows.append(row)
                }
                try Self.writePNG(try Self.sheet(rows, cell: cell), to: "\(dir)/\(size.name)/sheet-\(clip.name).png")
            }
        }
    }
}
