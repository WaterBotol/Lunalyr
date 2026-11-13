import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Contact sheets of Mosaic, Torque and Fable for reviewing their layouts by
/// eye: one row per line and stage size, seven moments a row. Only runs with
/// TEST_RUNNER_LUNALYR_LAYOUT_PREVIEW_DIR set; writes PNGs there.
@MainActor
final class LayoutReworkStageBPreviewTests: XCTestCase {
    private static let times: [Double] = [1.0, 1.9, 2.8, 3.7, 4.6, 5.5, 6.4]

    private static func line(_ texts: [String], step: Double = 0.62) -> [LyricsFetcher.Word] {
        texts.enumerated().map { i, w in
            LyricsFetcher.Word(text: w, start: 1 + Double(i) * step, end: 1.55 + Double(i) * step)
        }
    }

    private struct Case {
        var name: String
        var words: [LyricsFetcher.Word]
        var readings: [TimedWordKey: String] = [:]
        var lineEnd: Double
        /// A line that starts as this one leaves (its exit 0.25 s ahead, as
        /// `LyricPhraseTiming.exitStart` gives), drawn over it as the
        /// wallpaper does at a line change.
        var then: [LyricsFetcher.Word] = []
        var times: [Double] = LayoutReworkStageBPreviewTests.times
    }

    private static func cases() -> [Case] {
        let short = line("we keep dreaming under neon rain".split(separator: " ").map(String.init))
        let long = line("and we keep on running through the city lights until the morning".split(separator: " ").map(String.init),
                        step: 0.42)
        let japanese = line(["君の", "名前を", "呼んで", "夜空に", "消えた"], step: 0.7)
        var readings: [TimedWordKey: String] = [:]
        for (word, reading) in zip(japanese, ["kimi no", "namae wo", "yonde", "yozora ni", "kieta"]) {
            readings[TimedWordKey(word)] = reading
        }
        let first = Array(short.prefix(4))
        let next = "and we keep on".split(separator: " ").enumerated().map { i, w in
            LyricsFetcher.Word(text: String(w), start: 3.95 + Double(i) * 0.62, end: 4.5 + Double(i) * 0.62)
        }
        return [Case(name: "short", words: short, lineEnd: 5.4),
                Case(name: "change", words: first, lineEnd: 3.6, then: next, times: [3.6, 3.8, 3.9, 4.0, 4.1, 4.25, 4.6]),
                Case(name: "long", words: long, lineEnd: 5.8),
                Case(name: "japanese", words: japanese, readings: readings, lineEnd: 5.4)]
    }

    func testWriteStageBPreviewSheets() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_LAYOUT_PREVIEW_DIR"] else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_LAYOUT_PREVIEW_DIR to write the sheets")
        }
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let settings = Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("lunalyr.tests.layoutPreview.stageB"),
                                notifications: NotificationCenter())
        let sizes: [(CGSize, CGFloat, String)] = [(CGSize(width: 640, height: 360), 46, "small"),
                                                   (CGSize(width: 1400, height: 520), 64, "desk")]
        let hues: [LyricComposition: Double] = [.mosaic: 0.62, .torque: 0.78, .fable: 0.47]
        for composition in [LyricComposition.mosaic, .torque, .fable] {
            for (stage, base, label) in sizes {
                let scale: CGFloat = label == "small" ? 0.5 : 0.25
                let sheet = VStack(spacing: 2) {
                    ForEach(Array(Self.cases().enumerated()), id: \.offset) { _, item in
                        HStack(spacing: 2) {
                            ForEach(item.times, id: \.self) { time in
                                ZStack {
                                    LinearGradient(colors: [Color(hue: hues[composition] ?? 0.6, saturation: 0.55, brightness: 0.32),
                                                            Color(hue: hues[composition] ?? 0.6, saturation: 0.6, brightness: 0.08)],
                                                   startPoint: .topLeading, endPoint: .bottomTrailing)
                                    StageLyricView(words: item.words, glue: [Bool](repeating: false, count: item.words.count),
                                                   time: time, stageSize: stage, baseSize: base, mood: .neutral,
                                                   composition: composition, seed: 3, exitStart: item.lineEnd + 0.1,
                                                   readings: item.readings, settings: settings)
                                    if !item.then.isEmpty {
                                        StageLyricView(words: item.then, glue: [Bool](repeating: false, count: item.then.count),
                                                       time: time, stageSize: stage, baseSize: base, mood: .neutral,
                                                       composition: composition, seed: 4, settings: settings)
                                    }
                                }
                                .frame(width: stage.width, height: stage.height)
                                .clipped()
                            }
                        }
                    }
                }
                .background(Color.white)
                .environment(\.colorScheme, .dark)
                .environment(\.lyricInk, LyricInk())
                let renderer = ImageRenderer(content: sheet)
                renderer.scale = scale
                let image = try XCTUnwrap(renderer.nsImage)
                let rep = NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation))
                try rep?.representation(using: .png, properties: [:])?
                    .write(to: URL(fileURLWithPath: dir + "/\(composition.rawValue)-\(label).png"))
            }
        }
    }
}
