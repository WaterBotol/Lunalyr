import SwiftUI
import XCTest
@testable import VersoCustom

/// Contact sheets of the flow layouts (Scattered, Editorial, Constellation,
/// Constellation + Editorial, Echo), written only when
/// LUNALYR_LAYOUT_PREVIEW_DIR is set (pass it as
/// TEST_RUNNER_LUNALYR_LAYOUT_PREVIEW_DIR). One sheet per stage size and
/// line: a row per layout, seven moments across. Each line is fitted to the
/// stage as the wallpaper fits it (`FittedLyricStage`). Reads settings only.
@MainActor
final class LayoutReworkFlowPreviewTests: XCTestCase {
    private static let layouts: [LyricComposition] = [.scattered, .editorial, .constellation, .constellationEditorial, .echo]
    private static let times: [Double] = [1.3, 2.1, 2.9, 3.7, 4.6, 5.6, 6.2]

    private struct Sample {
        let name: String
        let words: [LyricsFetcher.Word]
        let glue: [Bool]
        let readings: [TimedWordKey: String]
        let lineEnd: Double
        let exitStart: Double
    }

    private static func timed(_ texts: [String], step: Double, length: Double) -> [LyricsFetcher.Word] {
        texts.enumerated().map { index, text in
            LyricsFetcher.Word(text: text, start: 1 + Double(index) * step, end: 1 + Double(index) * step + length)
        }
    }

    private static var samples: [Sample] {
        let short = timed("we keep dreaming under neon rain".split(separator: " ").map(String.init), step: 0.62, length: 0.55)
        let long = timed("and every night I wander through the city calling out your name".split(separator: " ").map(String.init),
                         step: 0.38, length: 0.34)
        let kana = ["夢", "を", "見", "て", "い", "る", "雨", "の", "中"]
        let japanese = timed(kana, step: 0.42, length: 0.38)
        let romaji = ["yume", "wo", "mi", "te", "i", "ru", "ame", "no", "naka"]
        var readings: [TimedWordKey: String] = [:]
        for (word, reading) in zip(japanese, romaji) { readings[word.presentationID] = reading }
        return [
            Sample(name: "short", words: short, glue: [Bool](repeating: false, count: short.count), readings: [:],
                   lineEnd: 5.4, exitStart: 5.5),
            Sample(name: "long", words: long, glue: [Bool](repeating: false, count: long.count), readings: [:],
                   lineEnd: 5.6, exitStart: 5.7),
            Sample(name: "japanese", words: japanese, glue: LyricRuns.glue(japanese, in: "夢を見ている 雨の中"),
                   readings: readings, lineEnd: 5.2, exitStart: 5.4),
        ]
    }

    private func cell(_ composition: LyricComposition, sample: Sample, time: Double, stage: CGSize,
                      baseSize: CGFloat, hue: Double) -> some View {
        // As the wallpaper sets a word-timed line (`LyricLayer`): dense webs
        // set long lines smaller and get a slightly inset stage.
        let density = sample.words.reduce(0) { $0 + $1.text.count + 3 }
        let densityScale = composition.hasConnections ? min(1.0, max(0.52, sqrt(110.0 / Double(max(density, 110))))) : 1.0
        let available = composition.hasConnections
            ? CGSize(width: stage.width * 0.92, height: stage.height * 0.86) : stage
        let glue = composition.hasConnections ? LyricRuns.splittingWords(sample.words, glue: sample.glue) : sample.glue
        return ZStack {
            LinearGradient(colors: [Color(hue: hue, saturation: 0.55, brightness: 0.32),
                                    Color(hue: hue, saturation: 0.6, brightness: 0.08)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            LyricLineView(words: sample.words, time: time, stageSize: stage, baseSize: baseSize * CGFloat(densityScale),
                          mood: .neutral, composition: composition, seed: 3, readings: sample.readings,
                          lineEnd: sample.lineEnd, exitStart: sample.exitStart, glue: glue)
                .modifier(FittedLyricStage(available: available, userScale: 1, neutral: 0.75))
                .frame(width: stage.width, height: stage.height)
            Text(String(format: "%@ %.1fs", composition.title, time))
                .font(.system(size: 11)).foregroundStyle(.white.opacity(0.5))
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(6)
        }
        .frame(width: stage.width, height: stage.height)
        .clipped()
        .environment(\.colorScheme, .dark)
        .environment(\.lyricInk, LyricInk())
    }

    func testWriteFlowLayoutSheets() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_LAYOUT_PREVIEW_DIR"] else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_LAYOUT_PREVIEW_DIR to write the sheets")
        }
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let stages: [(name: String, size: CGSize, base: CGFloat, scale: CGFloat)] = [
            ("gallery", CGSize(width: 640, height: 360), 46, 0.5),
            ("desktop", CGSize(width: 1400, height: 520), 64, 0.25),
        ]
        let hues: [Double] = [0.72, 0.58, 0.85, 0.05, 0.62]
        for stage in stages {
            for sample in Self.samples {
                let sheet = VStack(spacing: 2) {
                    ForEach(Array(Self.layouts.enumerated()), id: \.offset) { row, composition in
                        HStack(spacing: 2) {
                            ForEach(Self.times, id: \.self) { time in
                                self.cell(composition, sample: sample, time: time, stage: stage.size,
                                          baseSize: stage.base, hue: hues[row])
                            }
                        }
                    }
                }
                .background(Color.white)
                let renderer = ImageRenderer(content: sheet)
                renderer.scale = stage.scale
                let image = try XCTUnwrap(renderer.nsImage)
                let rep = try XCTUnwrap(NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation)))
                try XCTUnwrap(rep.representation(using: .png, properties: [:]))
                    .write(to: URL(fileURLWithPath: dir + "/\(stage.name)-\(sample.name).png"))
            }
        }
    }
}
