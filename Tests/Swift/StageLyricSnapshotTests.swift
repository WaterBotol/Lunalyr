import SwiftUI
import XCTest
@testable import VersoCustom

/// Writes a contact sheet of the stage layouts when LUNALYR_STAGE_SNAPSHOT_DIR
/// is set (pass it as TEST_RUNNER_LUNALYR_STAGE_SNAPSHOT_DIR); otherwise only
/// checks that every layout draws at every moment.
@MainActor
final class StageLyricSnapshotTests: XCTestCase {
    private let line: [LyricsFetcher.Word] = {
        let text = "Rain over the city can fly".split(separator: " ")
        return text.enumerated().map { i, w in LyricsFetcher.Word(text: String(w), start: 1 + Double(i) * 0.7, end: 1.6 + Double(i) * 0.7) }
    }()

    private func cell(_ composition: LyricComposition, at time: Double) -> some View {
        let stage = CGSize(width: 640, height: 300)
        return ZStack {
            LinearGradient(colors: [Color(red: 0.16, green: 0.1, blue: 0.3), Color(red: 0.05, green: 0.05, blue: 0.12)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            LyricLineView(words: line, time: time, stageSize: stage, baseSize: 40, mood: .neutral, composition: composition,
                          seed: 3, lineEnd: 6, exitStart: 6.2, glue: [Bool](repeating: false, count: line.count))
                .frame(width: stage.width, height: stage.height)
            Text(String(format: "%@ %.1fs", composition.title, time)).font(.system(size: 11)).foregroundStyle(.white.opacity(0.6))
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(6)
        }
        .frame(width: stage.width, height: stage.height)
        .environment(\.colorScheme, .dark)
        .environment(\.lyricInk, LyricInk())
    }

    func testEveryStageLayoutDraws() throws {
        let kinds: [LyricComposition] = LyricComposition.allCases.filter(\.fillsStage)
        XCTAssertFalse(kinds.isEmpty)
        let times: [Double] = [1.1, 1.8, 2.6, 3.6, 5.2, 6.4]
        let sheet = VStack(spacing: 2) {
            ForEach(kinds) { kind in
                HStack(spacing: 2) { ForEach(times, id: \.self) { self.cell(kind, at: $0) } }
            }
        }
        let renderer = ImageRenderer(content: sheet)
        renderer.scale = 0.5
        let image = try XCTUnwrap(renderer.nsImage)
        if let directory = ProcessInfo.processInfo.environment["LUNALYR_STAGE_SNAPSHOT_DIR"] {
            try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)
            let rep = NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation))
            try rep?.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: directory + "/stage.png"))
        }
    }
}

@MainActor
final class HingeSnapshotTests: XCTestCase {
    func testHingeSheet() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_STAGE_SNAPSHOT_DIR"] else { return }
        let text = "Dance under stars rain over the city can fly".split(separator: " ")
        let words = text.enumerated().map { i, w in LyricsFetcher.Word(text: String(w), start: 1 + Double(i) * 0.6, end: 1.5 + Double(i) * 0.6) }
        let times: [Double] = [1.3, 2.0, 2.5, 3.0, 3.6, 4.4, 5.2, 6.4]
        let sheet = LazyVGrid(columns: [GridItem(.fixed(760)), GridItem(.fixed(760))], spacing: 2) {
            ForEach(times, id: \.self) { t in
                ZStack {
                    Color(red: 0.99, green: 0.98, blue: 0.97)
                    LyricLineView(words: words, time: t, stageSize: CGSize(width: 760, height: 420), baseSize: 40, mood: .neutral,
                                  composition: .hinge, seed: 3, lineEnd: 9, exitStart: 9,
                                  glue: [Bool](repeating: false, count: words.count))
                        .environment(\.lyricInk, { var i = LyricInk(); i.base = LyricInkSample(rest: Color(red: 0.6, green: 0.3, blue: 0.33), sung: Color(red: 0.6, green: 0.3, blue: 0.33), glow: .clear, reading: .gray, sweeps: false); return i }())
                        .frame(width: 760, height: 420)
                    Text(String(format: "%.1fs", t)).font(.system(size: 12)).foregroundStyle(.red)
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(6)
                }.frame(width: 760, height: 420)
            }
        }.frame(width: 1522)
        let renderer = ImageRenderer(content: sheet)
        renderer.scale = 0.5
        let image = try XCTUnwrap(renderer.nsImage)
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        try NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation))?.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: dir + "/hinge.png"))
    }
}

@MainActor
final class PortalVarietyTests: XCTestCase {
    func testPortalSheet() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_STAGE_SNAPSHOT_DIR"] else { return }
        let text = "Rain over the city can fly".split(separator: " ")
        let words = text.enumerated().map { i, w in LyricsFetcher.Word(text: String(w), start: 1 + Double(i) * 0.7, end: 1.6 + Double(i) * 0.7) }
        let sheet = LazyVGrid(columns: [GridItem(.fixed(640)), GridItem(.fixed(640)), GridItem(.fixed(640))], spacing: 2) {
            ForEach(Array(0..<6), id: \.self) { seed in
                ZStack {
                    LinearGradient(colors: [Color(red: 0.16, green: 0.1, blue: 0.3), Color(red: 0.05, green: 0.05, blue: 0.12)], startPoint: .topLeading, endPoint: .bottomTrailing)
                    LyricLineView(words: words, time: 3.9, stageSize: CGSize(width: 640, height: 300), baseSize: 40, mood: .neutral,
                                  composition: .portal, seed: seed, lineEnd: 9, exitStart: 9, glue: [Bool](repeating: false, count: words.count))
                        .frame(width: 640, height: 300)
                }.frame(width: 640, height: 300).environment(\.lyricInk, LyricInk())
            }
        }.frame(width: 1924)
        let renderer = ImageRenderer(content: sheet)
        renderer.scale = 0.5
        let image = try XCTUnwrap(renderer.nsImage)
        try NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation))?.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: dir + "/portal.png"))
    }
}
