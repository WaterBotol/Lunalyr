import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Review videos of Mosaic on fast lines, written only when
/// LUNALYR_MOSAIC_FAST_DIR is set (pass it as TEST_RUNNER_LUNALYR_MOSAIC_FAST_DIR);
/// skipped otherwise. Reuses `StageLayoutVideoTests`' drawing; writes 30 fps
/// PNG frames to `<dir>/<size>/mosaic/<clip>/frames/NNNN.png` for ffmpeg.
///
/// Clips: "fast", eleven words (three of them glued syllables) inside 2.1 s,
/// followed closely by the next line; "burst", three words inside 0.1 s and
/// a three-syllable and a two-syllable glued word.
@MainActor
final class MosaicFastVideoTests: XCTestCase {
    typealias Word = LyricsFetcher.Word

    static func words(_ timing: [(String, Double, Double)]) -> [Word] {
        timing.map { Word(text: $0.0, start: $0.1, end: $0.2) }
    }

    /// "we were running out of time so we never said goodbye": 14 tokens,
    /// 11 words, 0.155 s apart.
    static var fast: (words: [Word], glue: [Bool]) {
        let texts = ["we", "were", "run", "ning", "out", "of", "time", "so", "we", "nev", "er", "said", "good", "bye"]
        let words = texts.enumerated().map { i, text in
            Word(text: text, start: 1 + Double(i) * 0.155, end: 1 + Double(i) * 0.155 + 0.14)
        }
        let glued: Set<Int> = [3, 10, 13]
        return (words, texts.indices.map { glued.contains($0) })
    }

    /// "and then oh my god you came back beautiful tonight": "oh my god"
    /// inside 0.1 s, "beau·ti·ful" and "to·night" glued.
    static var burst: (words: [Word], glue: [Bool]) {
        let words = words([("and", 1.0, 1.3), ("then", 1.4, 1.7), ("oh", 1.8, 1.84), ("my", 1.85, 1.89), ("god", 1.9, 2.4),
                           ("you", 2.5, 2.8), ("came", 2.9, 3.2), ("back", 3.3, 3.7), ("beau", 3.8, 3.95), ("ti", 4.0, 4.15),
                           ("ful", 4.2, 4.6), ("to", 4.8, 4.95), ("night", 5.0, 6.0)])
        let glued: Set<Int> = [9, 10, 12]
        return (words, words.indices.map { glued.contains($0) })
    }

    static var clips: [StageLayoutVideoTests.Clip] {
        let fast = Self.fast
        let fastLine = StageLayoutVideoTests.line(fast.words)
        // The next line follows 0.35 s after, just as fast.
        let after = (fast.words.map(\.end).max() ?? 3) + 0.35
        let fastNextWords = ["and", "it", "felt", "like", "flying"].enumerated().map { i, text in
            Word(text: text, start: after + Double(i) * 0.18, end: after + Double(i) * 0.18 + 0.16)
        }
        let fastNext = StageLayoutVideoTests.line(fastNextWords)
        let burst = Self.burst
        let burstLine = StageLayoutVideoTests.line(burst.words)
        let burstNext = StageLayoutVideoTests.line(StageLayoutVideoTests.follower(["hold", "me", "close", "tonight"], after: burstLine))
        func flat(_ line: LyricsFetcher.Line) -> [Bool] { [Bool](repeating: false, count: line.words.count) }
        return [
            .init(name: "fast", line: fastLine, glue: fast.glue, readings: [:], next: fastNext, nextGlue: flat(fastNext),
                  nextReadings: [:], hue: 0.9),
            .init(name: "burst", line: burstLine, glue: burst.glue, readings: [:], next: burstNext, nextGlue: flat(burstNext),
                  nextReadings: [:], hue: 0.4),
        ]
    }

    func testWriteMosaicFastFrames() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_MOSAIC_FAST_DIR"], !dir.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_MOSAIC_FAST_DIR to write Mosaic's fast-line frames")
        }
        let fileManager = FileManager.default
        for size in StageLayoutVideoTests.sizes {
            for clip in Self.clips {
                let frames = "\(dir)/\(size.name)/mosaic/\(clip.name)/frames"
                try? fileManager.removeItem(atPath: frames)
                try fileManager.createDirectory(atPath: frames, withIntermediateDirectories: true)
                let painter = StageLayoutVideoTests.Painter(size: size.stage, renderer: .window)
                defer { painter.close() }
                let span = StageLayoutVideoTests.span(of: clip)
                let count = Int(((span.upperBound - span.lowerBound) * StageLayoutVideoTests.fps).rounded(.up))
                for index in 0...count {
                    try autoreleasepool {
                        let time = span.lowerBound + Double(index) / StageLayoutVideoTests.fps
                        let rep = try painter.draw(StageLayoutVideoTests.frame(clip, composition: .mosaic, size: size, time: time))
                        try StageLayoutVideoTests.writePNG(rep, to: frames + String(format: "/%04d.png", index))
                    }
                }
            }
        }
    }
}
