import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// A review video of Torque and Fable on a fast line (twelve words in about
/// two seconds, then a fast next line), written only when
/// LUNALYR_FAST_VIDEO_DIR is set (pass it as TEST_RUNNER_LUNALYR_FAST_VIDEO_DIR);
/// skipped otherwise. Frames go to `<dir>/<size>/<layout>/fast/frames`, drawn
/// with `StageLayoutVideoTests`' own frame and painter; encode them with ffmpeg.
@MainActor
final class StageFastLineVideoTests: XCTestCase {
    static var clip: StageLayoutVideoTests.Clip {
        let words = StagePhrasesTests.even("I can't stop now we run run run into the night so fast", step: 0.17)
        let line = StageLayoutVideoTests.line(words)
        let next = StageLayoutVideoTests.line(StageLayoutVideoTests.follower(
            ["don't", "you", "look", "back", "we", "are", "the", "fire"], after: line, step: 0.18))
        func flat(_ line: LyricsFetcher.Line) -> [Bool] { [Bool](repeating: false, count: line.words.count) }
        return StageLayoutVideoTests.Clip(name: "fast", line: line, glue: flat(line), readings: [:], next: next,
                                          nextGlue: flat(next), nextReadings: [:], hue: 0.9)
    }

    func testWriteFastLineFrames() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_FAST_VIDEO_DIR"], !dir.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_FAST_VIDEO_DIR to write the fast-line frames")
        }
        let clip = Self.clip
        for size in StageLayoutVideoTests.sizes {
            for composition in [LyricComposition.torque, .fable] {
                let frames = "\(dir)/\(size.name)/\(composition.rawValue)/fast/frames"
                try? FileManager.default.removeItem(atPath: frames)
                try FileManager.default.createDirectory(atPath: frames, withIntermediateDirectories: true)
                let painter = StageLayoutVideoTests.Painter(size: size.stage, renderer: .window)
                defer { painter.close() }
                let span = StageLayoutVideoTests.span(of: clip)
                let count = Int(((span.upperBound - span.lowerBound) * StageLayoutVideoTests.fps).rounded(.up))
                for index in 0...count {
                    try autoreleasepool {
                        let time = span.lowerBound + Double(index) / StageLayoutVideoTests.fps
                        let rep = try painter.draw(StageLayoutVideoTests.frame(clip, composition: composition, size: size,
                                                                               time: time))
                        try StageLayoutVideoTests.writePNG(rep, to: frames + String(format: "/%04d.png", index))
                    }
                }
            }
        }
    }
}
