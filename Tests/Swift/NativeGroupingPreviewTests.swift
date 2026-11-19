import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Contact sheets of Japanese, Chinese, Korean and Thai lines in every
/// stage layout, for judging how their words group and arrive. Written only
/// when LUNALYR_NATIVE_PREVIEW_DIR is set (pass it as
/// TEST_RUNNER_LUNALYR_NATIVE_PREVIEW_DIR); skipped otherwise. One sheet per
/// layout; one row per line and seed, seven moments across the line, several
/// of them part way through a word. LUNALYR_NATIVE_PREVIEW_LABEL names the
/// sheets ("before", "after").
@MainActor
final class NativeGroupingPreviewTests: XCTestCase {
    struct Sample {
        let name: String
        let words: [LyricsFetcher.Word]
        let glue: [Bool]
    }

    /// Tokens timed one after another, glued as the line's own text has them.
    static func sample(_ name: String, _ tokens: [String], text: String, step: Double = 0.3) -> Sample {
        let words = tokens.enumerated().map { index, token in
            LyricsFetcher.Word(text: token, start: 1 + Double(index) * step, end: 1 + Double(index + 1) * step)
        }
        return Sample(name: name, words: words, glue: LyricRuns.glue(words, in: text))
    }

    static func characters(_ text: String) -> [String] { text.filter { !$0.isWhitespace }.map(String.init) }

    static var samples: [Sample] {
        [
            sample("ja", characters("窓辺に座って海を見ていた"), text: "窓辺に座って海を見ていた"),
            sample("ja-katakana", characters("ストロベリーショートケーキを食べた"), text: "ストロベリーショートケーキを食べた", step: 0.2),
            sample("ja-kanji", characters("一期一会 花鳥風月"), text: "一期一会 花鳥風月"),
            sample("zh", characters("我们一起走过春天的小路"), text: "我们一起走过春天的小路"),
            sample("zh-short", characters("我爱你 你看我"), text: "我爱你 你看我", step: 0.4),
            sample("ko", ["우", "리", "의", "사", "랑", "했", "었", "는", "데", "기", "억", "할", "게"],
                   text: "우리의 사랑했었는데 기억할게", step: 0.28),
            sample("th", ["ฉัน", "รัก", "เธอ", "มาก", "กว่า", "ใคร", "เสมอ"], text: "ฉันรักเธอมากกว่าใครเสมอ", step: 0.4),
        ]
    }

    static let layouts: [(LyricComposition, Double)] = [(.mosaic, 0.9), (.hinge, 0.58), (.portal, 0.05),
                                                         (.torque, 0.75), (.fable, 0.12)]

    func testWriteNativeGroupingSheets() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let dir = environment["LUNALYR_NATIVE_PREVIEW_DIR"] else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_NATIVE_PREVIEW_DIR to write the sheets")
        }
        let label = environment["LUNALYR_NATIVE_PREVIEW_LABEL"] ?? "after"
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let settings = Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.NativeGroupingPreview"),
                                notifications: NotificationCenter())
        let stage = CGSize(width: 960, height: 400), base: CGFloat = 52, scale: CGFloat = 0.5
        let columns = 7
        // Mosaic picks each word's setting by seed: three seeds show all three.
        let seeds = [3, 8, 21]
        for (composition, hue) in Self.layouts {
            let rowSeeds = composition == .mosaic ? seeds : [3]
            let rows = Self.samples.count * rowSeeds.count
            let cell = CGSize(width: (stage.width * scale).rounded(), height: (stage.height * scale).rounded())
            let sheet = NSImage(size: NSSize(width: cell.width * CGFloat(columns) + CGFloat(columns - 1) * 2,
                                             height: cell.height * CGFloat(rows) + CGFloat(rows - 1) * 2))
            var images: [(NSBitmapImageRep, CGRect)] = []
            var row = 0
            for sample in Self.samples {
                for seed in rowSeeds {
                    let first = sample.words.map(\.start).min() ?? 1
                    let last = sample.words.map(\.end).max() ?? 1
                    // Seven moments across the line, offset so most fall part way into a token.
                    let times = (0..<columns).map { first + 0.13 + (last - first) * Double($0) / Double(columns - 1) }
                    for (column, time) in times.enumerated() {
                        let view = ZStack {
                            LinearGradient(colors: [Color(hue: hue, saturation: 0.55, brightness: 0.32),
                                                    Color(hue: hue, saturation: 0.6, brightness: 0.08)],
                                           startPoint: .topLeading, endPoint: .bottomTrailing)
                            StageLyricView(words: sample.words, glue: sample.glue, time: time, stageSize: stage,
                                           baseSize: base, mood: .neutral, composition: composition, seed: seed,
                                           settings: settings)
                                .environment(\.lyricInk, LyricInk())
                                .environment(\.colorScheme, .dark)
                            Text(String(format: "%@ %@ s%d %.2fs", composition.rawValue, sample.name, seed, time))
                                .font(.system(size: 22)).foregroundStyle(.white.opacity(0.5))
                                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(8)
                        }
                        .frame(width: stage.width, height: stage.height)
                        let rep = try LayoutReworkStageAPreviewTests.bitmap(view, size: stage,
                                                                            window: [.portal, .mosaic].contains(composition))
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
            try png.write(to: URL(fileURLWithPath: dir + "/\(label)-\(composition.rawValue).png"))
        }
    }

    /// Scattered and Echo, a row with the line's glue as written (as before
    /// they parted a long run) above a row as the wallpaper now sets it.
    func testWriteFlowSheets() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let dir = environment["LUNALYR_NATIVE_PREVIEW_DIR"] else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_NATIVE_PREVIEW_DIR to write the sheets")
        }
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let stage = CGSize(width: 960, height: 400), base: CGFloat = 52
        let picked = Self.samples.filter { ["ja", "ja-katakana", "zh", "ko", "th"].contains($0.name) }
        for (composition, hue) in [(LyricComposition.scattered, 0.72), (.echo, 0.62)] {
            let sheet = VStack(spacing: 2) {
                ForEach(Array(picked.enumerated()), id: \.offset) { _, sample in
                    ForEach([false, true], id: \.self) { parted in
                        HStack(spacing: 2) {
                            ForEach(Self.moments(sample), id: \.self) { time in
                                self.flowCell(composition, sample: sample, parted: parted, time: time, stage: stage,
                                              base: base, hue: hue)
                            }
                        }
                    }
                }
            }
            .background(Color.black)
            let renderer = ImageRenderer(content: sheet)
            renderer.scale = 0.5
            let image = try XCTUnwrap(renderer.nsImage)
            let rep = try XCTUnwrap(NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation)))
            try XCTUnwrap(rep.representation(using: .png, properties: [:]))
                .write(to: URL(fileURLWithPath: dir + "/flow-\(composition.rawValue).png"))
        }
    }

    static func moments(_ sample: Sample) -> [Double] {
        let first = sample.words.map(\.start).min() ?? 1, last = sample.words.map(\.end).max() ?? 1
        return (0..<6).map { first + 0.13 + (last + 0.6 - first) * Double($0) / 5 }
    }

    private func flowCell(_ composition: LyricComposition, sample: Sample, parted: Bool, time: Double, stage: CGSize,
                          base: CGFloat, hue: Double) -> some View {
        let glue = parted
            ? LyricRuns.splittingWords(sample.words, glue: sample.glue, into: .words, longerThan: LyricRuns.longRun)
            : sample.glue
        let last = sample.words.map(\.end).max() ?? 1
        return ZStack {
            LinearGradient(colors: [Color(hue: hue, saturation: 0.55, brightness: 0.32),
                                    Color(hue: hue, saturation: 0.6, brightness: 0.08)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            LyricLineView(words: sample.words, time: time, stageSize: stage, baseSize: base, mood: .neutral,
                          composition: composition, seed: 3, readings: [:], lineEnd: last + 0.4,
                          exitStart: last + 2, glue: glue, phraseGlue: parted ? sample.glue : nil)
                .modifier(FittedLyricStage(available: stage, userScale: 1, neutral: 0.75))
                .frame(width: stage.width, height: stage.height)
            Text(String(format: "%@ %@ %@ %.2fs", composition.rawValue, sample.name, parted ? "now" : "was", time))
                .font(.system(size: 22)).foregroundStyle(.white.opacity(0.5))
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(8)
        }
        .frame(width: stage.width, height: stage.height)
        .clipped()
        .environment(\.colorScheme, .dark)
        .environment(\.lyricInk, LyricInk())
    }

    /// The runs each sample is cut into, written beside the sheets, so the
    /// grouping can be read as text as well as seen.
    func testWriteNativeGroupingRuns() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let dir = environment["LUNALYR_NATIVE_PREVIEW_DIR"] else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_NATIVE_PREVIEW_DIR to write the runs")
        }
        let label = environment["LUNALYR_NATIVE_PREVIEW_LABEL"] ?? "after"
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        var report = ""
        for sample in Self.samples {
            let stage = StageRun.make(sample.words, glue: sample.glue).map(\.text)
            let rolling = LyricRuns.labels(sample.words, glue: LyricRuns.splittingWords(sample.words, glue: sample.glue, into: .words))
            let phrases = LyricRuns.labels(sample.words, glue: LyricRuns.splittingWords(sample.words, glue: sample.glue))
            report += "\(sample.name)\n  stage:   \(stage.joined(separator: " | "))\n"
            report += "  words:   \(rolling.joined(separator: " | "))\n  phrases: \(phrases.joined(separator: " | "))\n"
        }
        try report.write(toFile: dir + "/\(label)-runs.txt", atomically: true, encoding: .utf8)
    }
}
