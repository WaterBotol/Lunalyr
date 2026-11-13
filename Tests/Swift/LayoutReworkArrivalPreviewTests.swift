import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Contact sheets of the arrival layouts (Spoken, Cascade, Cutout) at the
/// gallery's stage and a desktop-like one. Only runs with
/// TEST_RUNNER_LUNALYR_LAYOUT_PREVIEW_DIR set; writes nothing otherwise.
@MainActor
final class LayoutReworkArrivalPreviewTests: XCTestCase {
    private struct Line {
        let name: String
        let words: [LyricsFetcher.Word]
        var readings: [TimedWordKey: String] = [:]
    }

    private func line(_ name: String, _ items: [(String, String?)]) -> Line {
        var words: [LyricsFetcher.Word] = [], readings: [TimedWordKey: String] = [:]
        for (index, item) in items.enumerated() {
            let word = LyricsFetcher.Word(text: item.0, start: 1 + Double(index) * 0.62, end: 1.55 + Double(index) * 0.62)
            words.append(word)
            if let reading = item.1 { readings[word.presentationID] = reading }
        }
        return Line(name: name, words: words, readings: readings)
    }

    func testWriteArrivalLayoutPreviews() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_LAYOUT_PREVIEW_DIR"] else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_LAYOUT_PREVIEW_DIR to write the arrival layout previews")
        }
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let neon = line("neon", "we keep dreaming under neon rain".split(separator: " ").map { (String($0), nil) })
        let long = line("long", "and we keep on running through the city lights until morning"
            .split(separator: " ").map { (String($0), nil) })
        let japanese = line("japanese", [("夢見る", "yumemiru"), ("少女じゃ", "shoujo ja"), ("いられない", "irarenai"),
                                         ("抱きしめて", "dakishimete"), ("永遠に", "eien ni"), ("輝いて", "kagayaite")])
        let stages: [(name: String, size: CGSize, base: CGFloat)] = [
            ("small", CGSize(width: 640, height: 360), 46),
            ("desktop", CGSize(width: 1400, height: 520), 64),
        ]
        let times = (0..<7).map { 1.0 + Double($0) * 0.9 }
        let layouts: [(LyricComposition, Double)] = [(.spoken, 0.78), (.cascade, 0.47), (.cutout, 0.72)]
        for stage in stages {
            let scale: CGFloat = stage.size.width > 1000 ? 0.32 : 0.5
            for (composition, hue) in layouts {
                let sheet = VStack(spacing: 2) {
                    ForEach([neon, long, japanese], id: \.name) { lyric in
                        HStack(spacing: 2) {
                            ForEach(times, id: \.self) { time in
                                self.cell(lyric, composition: composition, hue: hue, time: time,
                                          stage: stage.size, base: stage.base)
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
                    .write(to: URL(fileURLWithPath: "\(dir)/\(composition.rawValue)-\(stage.name).png"))
            }
        }
    }

    private func cell(_ lyric: Line, composition: LyricComposition, hue: Double, time: Double,
                      stage: CGSize, base: CGFloat) -> some View {
        ZStack {
            LinearGradient(colors: [Color(hue: hue, saturation: 0.55, brightness: 0.32),
                                    Color(hue: hue, saturation: 0.6, brightness: 0.08)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            LyricLineView(words: lyric.words, time: time, stageSize: stage, baseSize: base, mood: .neutral,
                          composition: composition, seed: 3, readings: lyric.readings,
                          lineEnd: lyric.words.last.map { $0.end + 0.2 }, exitStart: .infinity,
                          glue: [Bool](repeating: false, count: lyric.words.count))
                .frame(width: stage.width, height: stage.height)
        }
        .frame(width: stage.width, height: stage.height)
        .clipped()
    }
}
