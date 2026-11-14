import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// A layout-blind look for jumps as a word stops being sung, written only
/// when LUNALYR_EXIT_PROBE_DIR is set (pass it as
/// TEST_RUNNER_LUNALYR_EXIT_PROBE_DIR); skipped otherwise.
///
/// Every layout draws three lines (an even one, a fast one with words
/// 0.15 s apart, and one with a held note) through `LyricLineView`, as the
/// review videos do (`StageLayoutVideoTests.frame`), at 120 Hz. For each
/// moment it measures how much the picture changes over one 60 Hz frame:
/// the mean change of every pixel's lightness, and the largest mean change
/// of any 24-pixel tile (one word jumping shows there even when the rest of
/// the stage is still). Across each word end and onset the change from
/// t - 1/120 to t + 1/120 is set against the changes in the frames around
/// it; a jump is a change far larger than its neighbours'.
///
/// Optional: LUNALYR_EXIT_PROBE_LAYOUTS ("spoken,hinge"; default all 14).
/// Writes `<dir>/<layout>-<line>.csv` and `<dir>/summary.txt`.
@MainActor
final class WordExitProbeTests: XCTestCase {
    typealias Word = LyricsFetcher.Word
    typealias V = StageLayoutVideoTests

    static let size = V.Size(name: "small", stage: CGSize(width: 640, height: 360), base: 46)
    static let rate = 120.0
    static let tile = 24

    struct Line {
        let name: String
        let words: [Word]
    }

    static var lines: [Line] {
        let normal = StagePhrasesTests.even("we keep dreaming under neon rain")
        let fast = "we run it back and turn it up til the lights go".split(separator: " ").enumerated().map { i, text in
            Word(text: String(text), start: 1 + Double(i) * 0.15, end: 1 + Double(i) * 0.15 + 0.13)
        }
        let held: [Word] = [
            Word(text: "hold", start: 1.00, end: 1.40), Word(text: "on", start: 1.45, end: 1.80),
            Word(text: "to", start: 1.85, end: 2.10), Word(text: "forever", start: 2.15, end: 4.00),
            Word(text: "now", start: 4.30, end: 4.80), Word(text: "love", start: 4.85, end: 5.40),
        ]
        return [Line(name: "normal", words: normal), Line(name: "fast", words: fast), Line(name: "held", words: held)]
    }

    struct Luma {
        let width: Int, height: Int
        let values: [Float]
    }

    static func luma(_ rep: NSBitmapImageRep) -> Luma {
        let width = rep.pixelsWide, height = rep.pixelsHigh
        var values = [Float](repeating: 0, count: width * height)
        guard let data = rep.bitmapData else { return Luma(width: width, height: height, values: values) }
        let row = rep.bytesPerRow, step = rep.bitsPerPixel / 8
        for y in 0..<height {
            for x in 0..<width {
                let p = data + y * row + x * step
                values[y * width + x] = (0.2126 * Float(p[0]) + 0.7152 * Float(p[1]) + 0.0722 * Float(p[2])) / 255
            }
        }
        return Luma(width: width, height: height, values: values)
    }

    /// Mean change, and the largest tile's mean change.
    static func change(_ a: Luma, _ b: Luma) -> (mean: Double, tile: Double) {
        var total = 0.0
        let columns = (a.width + tile - 1) / tile, rows = (a.height + tile - 1) / tile
        var tiles = [Double](repeating: 0, count: columns * rows)
        for y in 0..<a.height {
            for x in 0..<a.width {
                let d = Double(abs(a.values[y * a.width + x] - b.values[y * a.width + x]))
                total += d
                tiles[(y / tile) * columns + x / tile] += d
            }
        }
        return (total / Double(a.width * a.height), (tiles.max() ?? 0) / Double(tile * tile))
    }

    func testProbeWordExits() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let dir = environment["LUNALYR_EXIT_PROBE_DIR"], !dir.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_EXIT_PROBE_DIR to probe the word exits")
        }
        let layouts = V.chosen(LyricComposition.allCases, env: "LUNALYR_EXIT_PROBE_LAYOUTS") { $0.rawValue }
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        var summary = ""
        for composition in layouts {
            for probe in Self.lines {
                let line = V.line(probe.words)
                let next = V.line(V.follower(["hold", "me", "close"], after: line, step: 0.4))
                let clip = V.Clip(name: probe.name, line: line, glue: [Bool](repeating: false, count: probe.words.count),
                                  readings: [:], next: next, nextGlue: [false, false, false], nextReadings: [:], hue: 0.6)
                let painter = V.Painter(size: Self.size.stage, renderer: V.renderer(for: composition))
                defer { painter.close() }
                let first = line.start - 0.2
                let last = (probe.words.map(\.end).max() ?? line.end) + 0.6
                let count = Int(((last - first) * Self.rate).rounded(.up))
                var frames: [Luma] = []
                for index in 0...count {
                    try autoreleasepool {
                        let time = first + Double(index) / Self.rate
                        let rep = try painter.draw(V.frame(clip, composition: composition, size: Self.size, time: time)
                            .environment(\.lyricReduceMotion, environment["LUNALYR_EXIT_PROBE_REDUCED"] == "1"))
                        frames.append(Self.luma(rep))
                    }
                }
                // D[i]: the change over one 60 Hz frame starting at frame i.
                var steps: [(mean: Double, tile: Double)] = []
                for i in 0..<(frames.count - 2) { steps.append(Self.change(frames[i], frames[i + 2])) }
                var csv = "time,mean,tile\n"
                for (i, step) in steps.enumerated() {
                    csv += String(format: "%.4f,%.6f,%.6f\n", first + Double(i) / Self.rate, step.mean, step.tile)
                }
                try csv.write(toFile: "\(dir)/\(composition.rawValue)-\(probe.name).csv", atomically: true, encoding: .utf8)
                // Events: every end and onset.
                var events: [(String, Double)] = []
                for word in probe.words {
                    events.append(("end " + word.text, word.end))
                    events.append(("onset " + word.text, word.start))
                }
                for (label, moment) in events.sorted(by: { $0.1 < $1.1 }) {
                    let centre = Int(((moment - 1.0 / Self.rate - first) * Self.rate).rounded())
                    guard centre >= 8, centre + 8 < steps.count else { continue }
                    let across = steps[centre].tile
                    let around = [-8, -7, -6, -5, -4, 4, 5, 6, 7, 8].map { steps[centre + $0].tile }.sorted()
                    let neighbour = around[around.count / 2]
                    let ratio = across / max(neighbour, 0.0005)
                    summary += String(format: "%@ %@ %@ t=%.3f across=%.4f around=%.4f ratio=%.1f\n",
                                      composition.rawValue, probe.name, label, moment, across, neighbour, ratio)
                }
            }
        }
        try summary.write(toFile: "\(dir)/summary.txt", atomically: true, encoding: .utf8)
    }
}

extension WordExitProbeTests {
    /// The moments the word-exit fixes are about, for review: frames at
    /// 60 fps, written only when LUNALYR_EXIT_STRIP_DIR is set (pass it as
    /// TEST_RUNNER_LUNALYR_EXIT_STRIP_DIR), to `<dir>/<layout>-<line>/NNNN.png`
    /// with a contact sheet of every frame beside them
    /// (`<dir>/<layout>-<line>-sheet.png`). Encode with ffmpeg afterwards.
    static let strips: [(layout: LyricComposition, line: String, from: Double, to: Double)] = [
        (.torque, "fast", 1.45, 1.85), (.mosaic, "fast", 2.10, 2.45), (.mosaic, "held", 2.20, 2.70),
        (.cascade, "held", 1.90, 2.30), (.cascade, "held", 4.15, 4.50), (.spoken, "fast", 1.20, 1.60),
        (.scattered, "held", 1.90, 2.30), (.cutout, "fast", 2.20, 2.60), (.broadside, "normal", 2.75, 3.25),
    ]

    func testWriteExitStrips() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_EXIT_STRIP_DIR"], !dir.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_EXIT_STRIP_DIR to write the word-exit review frames")
        }
        let fileManager = FileManager.default
        for strip in Self.strips {
            guard let probe = Self.lines.first(where: { $0.name == strip.line }) else { continue }
            let line = V.line(probe.words)
            let next = V.line(V.follower(["hold", "me", "close"], after: line, step: 0.4))
            let clip = V.Clip(name: probe.name, line: line, glue: [Bool](repeating: false, count: probe.words.count),
                              readings: [:], next: next, nextGlue: [false, false, false], nextReadings: [:], hue: 0.6)
            let name = String(format: "%@-%@-%.2f", strip.layout.rawValue, strip.line, strip.from)
            let frames = "\(dir)/\(name)"
            try? fileManager.removeItem(atPath: frames)
            try fileManager.createDirectory(atPath: frames, withIntermediateDirectories: true)
            let painter = V.Painter(size: Self.size.stage, renderer: V.renderer(for: strip.layout))
            defer { painter.close() }
            let count = Int(((strip.to - strip.from) * 60).rounded())
            var reps: [NSBitmapImageRep] = []
            for index in 0...count {
                try autoreleasepool {
                    let time = strip.from + Double(index) / 60
                    let rep = try painter.draw(V.frame(clip, composition: strip.layout, size: Self.size, time: time))
                    try V.writePNG(rep, to: frames + String(format: "/%04d.png", index))
                    reps.append(rep)
                }
            }
            let cell = CGSize(width: 320, height: 180)
            let rows = stride(from: 0, to: reps.count, by: 6).map { Array(reps[$0..<min(reps.count, $0 + 6)]) }
            try V.writePNG(try V.sheet(rows, cell: cell), to: "\(dir)/\(name)-sheet.png")
        }
    }
}
