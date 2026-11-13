import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Contact sheets of Hinge, Broadside and Portal for reviewing their layout,
/// written only when LUNALYR_LAYOUT_PREVIEW_DIR is set (pass it as
/// TEST_RUNNER_LUNALYR_LAYOUT_PREVIEW_DIR); skipped otherwise. One sheet per
/// stage size; one row per layout and line, seven moments across the line.
/// Broadside and Portal are drawn in a real (never shown) window, as on the
/// desktop; Hinge with ImageRenderer. Settings come from an isolated suite.
@MainActor
final class LayoutReworkStageAPreviewTests: XCTestCase {
    struct Sample {
        let name: String
        let words: [LyricsFetcher.Word]
        let glue: [Bool]
        let readings: [TimedWordKey: String]
    }

    static func timed(_ text: String) -> [LyricsFetcher.Word] {
        text.split(separator: " ").enumerated().map { i, w in
            LyricsFetcher.Word(text: String(w), start: 1 + Double(i) * 0.62, end: 1.55 + Double(i) * 0.62)
        }
    }

    static var samples: [Sample] {
        let short = timed("we keep dreaming under neon rain")
        let long = timed("i was walking home alone through the empty city streets tonight")
        let japanese = StageReadingTests.japanese
        return [
            Sample(name: "short", words: short, glue: [Bool](repeating: false, count: short.count), readings: [:]),
            Sample(name: "long", words: long, glue: [Bool](repeating: false, count: long.count), readings: [:]),
            Sample(name: "japanese", words: japanese.words, glue: japanese.glue, readings: japanese.readings),
        ]
    }

    static let layouts: [(LyricComposition, Double)] = [(.hinge, 0.58), (.broadside, 0.85), (.portal, 0.05)]

    /// Hinge is drawn with `ImageRenderer`: a window's cached display draws
    /// a word that is mid-swing a second time, flat, at the stage's corner.
    static func bitmap(_ view: some View, size: CGSize, window: Bool) throws -> NSBitmapImageRep {
        if window { return try StageReadingTests.bitmap(view, size: size) }
        let renderer = ImageRenderer(content: view.frame(width: size.width, height: size.height))
        renderer.scale = 1
        let image = try XCTUnwrap(renderer.nsImage)
        return try XCTUnwrap(NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation)))
    }

    func testWriteStageAPreviewSheets() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_LAYOUT_PREVIEW_DIR"] else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_LAYOUT_PREVIEW_DIR to write the sheets")
        }
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let settings = Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.LayoutReworkStageA"),
                                notifications: NotificationCenter())
        let sizes: [(String, CGSize, CGFloat, CGFloat)] = [("small", CGSize(width: 640, height: 360), 46, 0.5),
                                                           ("desktop", CGSize(width: 1400, height: 520), 64, 0.3)]
        let columns = 7
        for (label, stage, base, scale) in sizes {
            let rows = Self.layouts.count * Self.samples.count
            let cell = CGSize(width: (stage.width * scale).rounded(), height: (stage.height * scale).rounded())
            let sheet = NSImage(size: NSSize(width: cell.width * CGFloat(columns) + CGFloat(columns - 1) * 2,
                                             height: cell.height * CGFloat(rows) + CGFloat(rows - 1) * 2))
            var images: [(NSBitmapImageRep, CGRect)] = []
            var row = 0
            for (composition, hue) in Self.layouts {
                for sample in Self.samples {
                    let first = sample.words.map(\.start).min() ?? 1
                    let last = sample.words.map(\.start).max() ?? 1
                    let exit = last + 0.9
                    // Seven moments: across the line, the last two in its exit.
                    var times = (0..<5).map { first + 0.25 + (last + 0.3 - first - 0.25) * Double($0) / 4 }
                    times += [exit + 0.18, exit + 0.4]
                    for (column, time) in times.enumerated() {
                        let view = ZStack {
                            LinearGradient(colors: [Color(hue: hue, saturation: 0.55, brightness: 0.32),
                                                    Color(hue: hue, saturation: 0.6, brightness: 0.08)],
                                           startPoint: .topLeading, endPoint: .bottomTrailing)
                            StageLyricView(words: sample.words, glue: sample.glue, time: time, stageSize: stage,
                                           baseSize: base, mood: .neutral, composition: composition, seed: 3,
                                           exitStart: exit, readings: sample.readings, settings: settings)
                                .environment(\.lyricInk, LyricInk())
                                .environment(\.colorScheme, .dark)
                            Text(String(format: "%@ %.2fs", composition.rawValue, time))
                                .font(.system(size: 11 / scale * 0.5)).foregroundStyle(.white.opacity(0.45))
                                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(6)
                        }
                        .frame(width: stage.width, height: stage.height)
                        let rep = try Self.bitmap(view, size: stage, window: composition != .hinge)
                        let y = sheet.size.height - CGFloat(row + 1) * cell.height - CGFloat(row) * 2
                        images.append((rep, CGRect(x: CGFloat(column) * (cell.width + 2), y: y,
                                                   width: cell.width, height: cell.height)))
                    }
                    row += 1
                }
            }
            sheet.lockFocus()
            NSColor.black.setFill()
            NSRect(origin: .zero, size: sheet.size).fill()
            for (rep, rect) in images { rep.draw(in: rect) }
            sheet.unlockFocus()
            let tiff = try XCTUnwrap(sheet.tiffRepresentation)
            let png = try XCTUnwrap(NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]))
            try png.write(to: URL(fileURLWithPath: dir + "/stage-a-\(label).png"))
        }
    }
}
