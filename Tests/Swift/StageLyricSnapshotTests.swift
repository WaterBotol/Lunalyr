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

