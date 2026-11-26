import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Cutout's label turning over like a flap (`LabelFlip`), on small and large
/// type, with readings under the words, and on scripts that reach high and
/// low or run right to left. Written when LUNALYR_ACTION_PREVIEW_DIR is set.
@MainActor
final class CutoutLabelFlipPreviewTests: XCTestCase {
    static let canvas = CGSize(width: 1920, height: 1080)

    struct Pane {
        let name: String
        let base: CGFloat
        let items: [(String, String?)]
        var hue = 0.62

        /// Held long enough that every word wears a label (a held word is a
        /// hero on Cutout), so each hand-over is label to label.
        var words: [LyricsFetcher.Word] {
            var time = 10.0
            return items.map { item in
                defer { time += 0.8 }
                return LyricsFetcher.Word(text: item.0, start: time, end: time + 0.7)
            }
        }

        var readings: [TimedWordKey: String] {
            var readings: [TimedWordKey: String] = [:]
            for (word, item) in zip(words, items) { if let reading = item.1 { readings[word.presentationID] = reading } }
            return readings
        }
    }

    static func plain(_ line: String) -> [(String, String?)] { line.split(separator: " ").map { (String($0), nil) } }

    static let panes: [Pane] = [
        Pane(name: "Small type", base: 34, items: plain("keep thinking about tonight forever")),
        Pane(name: "Large type", base: 84, items: plain("hold tight tonight darling"), hue: 0.02),
        Pane(name: "Japanese, romaji under", base: 56,
             items: [("夢見る", "yumemiru"), ("少女", "shoujo"), ("じゃ", "ja"), ("いられない", "irarenai"), ("永遠に", "eien ni")],
             hue: 0.85),
        Pane(name: "Mandarin, pinyin under", base: 56,
             items: [("我", "wǒ"), ("想", "xiǎng"), ("你", "nǐ"), ("每个", "měi gè"), ("夜晚", "yè wǎn")], hue: 0.45),
        Pane(name: "Thai (marks above and below)", base: 56, items: plain("ฉัน คิดถึง เธอ ทุก คืน"), hue: 0.12),
        Pane(name: "Arabic (right to left)", base: 56, items: plain("أنا أفكر فيك كل ليلة"), hue: 0.55),
    ]

    static func pane(_ pane: Pane, time: Double, size: CGSize) -> some View {
        let words = pane.words
        return ZStack {
            StageLayoutVideoTests.backdrop(hue: pane.hue)
            LyricLineView(words: words, time: time, stageSize: size, baseSize: pane.base, mood: .neutral, composition: .cutout,
                          seed: 3, accent: Color(hue: 0.12, saturation: 0.55, brightness: 0.8), readings: pane.readings,
                          lineEnd: (words.last?.end ?? time) + 0.3)
                .frame(width: size.width, height: size.height)
        }
        .frame(width: size.width, height: size.height)
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    static func frame(time: Double) -> some View {
        let gap: CGFloat = 14, label: CGFloat = 28
        let size = CGSize(width: (canvas.width - 4 * gap) / 3, height: (canvas.height - 3 * gap - 2 * label) / 2)
        func cell(_ i: Int) -> some View {
            VStack(spacing: 4) {
                pane(panes[i], time: time, size: size)
                Text(panes[i].name).font(.system(size: 18, weight: .semibold, design: .rounded))
                    .foregroundStyle(.white.opacity(0.85))
            }
        }
        return ZStack {
            Color(white: 0.06)
            VStack(spacing: gap) {
                HStack(spacing: gap) { cell(0); cell(1); cell(2) }
                HStack(spacing: gap) { cell(3); cell(4); cell(5) }
            }
        }
        .frame(width: canvas.width, height: canvas.height)
        .environment(\.lyricInk, LyricInk())
        .environment(\.colorScheme, .dark)
    }

    func testWriteCutoutLabelFlip() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_ACTION_PREVIEW_DIR"], !dir.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_ACTION_PREVIEW_DIR to write the Cutout label flip previews")
        }
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let painter = StageLayoutVideoTests.Painter(size: Self.canvas, renderer: .image)
        defer { painter.close() }
        // Moments around the hand-over to the third word (onset 11.6).
        let moments = [11.55, 11.63, 11.68, 11.75, 11.85, 12.1]
        let reps = try moments.map { try painter.draw(Self.frame(time: $0)) }
        try StageLayoutVideoTests.writePNG(try StageLayoutVideoTests.sheet([Array(reps[0..<2]), Array(reps[2..<4]), Array(reps[4..<6])],
                                                                          cell: CGSize(width: 960, height: 540)),
                                           to: "\(dir)/cutout-label-flip.png")
        let start = 9.7, end = 13.9
        let count = Int(((end - start) * ActionWordsPreviewTests.fps).rounded(.up))
        let movie = try ActionWordsPreviewTests.Movie(path: "\(dir)/cutout-label-flip.mp4", size: Self.canvas)
        for index in 0..<count {
            let rep = try painter.draw(Self.frame(time: start + Double(index) / ActionWordsPreviewTests.fps))
            try movie.append(try XCTUnwrap(rep.cgImage))
        }
        movie.finish()
        let slow = try ActionWordsPreviewTests.Movie(path: "\(dir)/cutout-label-flip-half-speed.mp4", size: Self.canvas)
        for index in 0..<(2 * count) {
            let rep = try painter.draw(Self.frame(time: start + Double(index) / (2 * ActionWordsPreviewTests.fps)))
            try slow.append(try XCTUnwrap(rep.cgImage))
        }
        slow.finish()
    }
}
