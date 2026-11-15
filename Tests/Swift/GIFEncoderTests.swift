import AppKit
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import VersoCustom

/// The hand-made GIF encoder (Sources/Extras/GIFEncoder.swift): every file is
/// decoded with ImageIO, which composites partial frames the way browsers do.
final class GIFEncoderTests: XCTestCase {
    // MARK: Helpers

    private struct Random {
        var state: UInt64
        mutating func next() -> UInt64 {
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return state >> 33
        }
    }

    private static func pack(_ r: Int, _ g: Int, _ b: Int) -> UInt32 { UInt32(r) | UInt32(g) << 8 | UInt32(b) << 16 }

    private static func channels(_ value: UInt32) -> (Int, Int, Int) {
        (Int(value & 0xFF), Int((value >> 8) & 0xFF), Int((value >> 16) & 0xFF))
    }

    /// A dark, smooth two-way gradient, the case that bands: well over 255
    /// colours, so it has to be quantised and dithered.
    private static func gradient(width: Int, height: Int) -> [UInt32] {
        (0..<(width * height)).map { i in
            let x = Double(i % width) / Double(width), y = Double(i / width) / Double(height)
            return pack(Int(10 + 60 * x), Int(8 + 30 * y), Int(30 + 50 * (x + y) / 2))
        }
    }

    private static func image(_ pixels: [UInt32], width: Int, height: Int) -> CGImage {
        let data = pixels.withUnsafeBytes { Data($0) }
        let provider = CGDataProvider(data: data as CFData)!
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                       space: CGColorSpace(name: CGColorSpace.sRGB)!,
                       bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue), provider: provider,
                       decode: nil, shouldInterpolate: false, intent: .defaultIntent)!
    }

    private static func encode(_ frames: [[UInt32]], width: Int, height: Int, delay: Int = 4) -> (Data, GIFEncoder) {
        let encoder = GIFEncoder(width: width, height: height)
        for frame in frames {
            XCTAssertNotNil(frame.withUnsafeBufferPointer { encoder.add($0, delay: delay) })
        }
        return (encoder.data(), encoder)
    }

    private static func decode(_ data: Data) throws -> CGImageSource {
        let source = try XCTUnwrap(CGImageSourceCreateWithData(data as CFData, nil))
        XCTAssertEqual(CGImageSourceGetType(source) as String?, UTType.gif.identifier)
        return source
    }

    private static func pixels(_ source: CGImageSource, _ index: Int, width: Int, height: Int) throws -> [UInt32] {
        let frame = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, index, nil))
        XCTAssertEqual(frame.width, width)
        XCTAssertEqual(frame.height, height)
        var pixels = [UInt32](repeating: 0, count: width * height)
        GIFWriter.draw(frame, into: &pixels, width: width, height: height)
        return pixels.map { $0 & 0xFF_FFFF }
    }

    private static func delay(_ source: CGImageSource, _ index: Int) throws -> (clamped: Double, unclamped: Double) {
        let properties = try XCTUnwrap(CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any])
        let gif = try XCTUnwrap(properties[kCGImagePropertyGIFDictionary] as? [CFString: Any])
        return (try XCTUnwrap(gif[kCGImagePropertyGIFDelayTime] as? Double),
                try XCTUnwrap(gif[kCGImagePropertyGIFUnclampedDelayTime] as? Double))
    }

    /// Mean and largest per-channel difference.
    private static func error(_ a: [UInt32], _ b: [UInt32]) -> (mean: Double, max: Int) {
        var sum = 0, largest = 0
        for (x, y) in zip(a, b) {
            let p = channels(x), q = channels(y)
            let e = max(abs(p.0 - q.0), abs(p.1 - q.1), abs(p.2 - q.2))
            sum += e
            largest = max(largest, e)
        }
        return (Double(sum) / Double(max(1, a.count)), largest)
    }

    // MARK: LZW

    func testExactColoursRoundTripThroughImageIO() throws {
        // Noise fills the code table again and again (clear codes); runs make
        // long codes; every table size from 2 colours to 255 is tried.
        var random = Random(state: 7)
        for (colours, width, height) in [(2, 37, 23), (3, 64, 64), (4, 1, 1), (5, 50, 3), (16, 120, 90), (64, 200, 150),
                                         (200, 256, 256), (255, 301, 177)] {
            let palette = (0..<colours).map { index in
                Self.pack(index * 37 % 256, index * 101 % 256, (index * 13 + Int(random.next() % 7)) % 256)
            }
            let noise = (0..<(width * height)).map { _ in palette[Int(random.next() % UInt64(colours))] }
            let runs = (0..<(width * height)).map { palette[($0 / 97) % colours] }
            for pixels in [noise, runs] {
                let (data, encoder) = Self.encode([pixels], width: width, height: height)
                XCTAssertLessThanOrEqual(encoder.paletteSizes[0], 255)
                let decoded = try Self.pixels(try Self.decode(data), 0, width: width, height: height)
                XCTAssertEqual(decoded, pixels, "\(colours) colours, \(width) × \(height)")
            }
        }
    }

    func testLZWStreamHasValidSubBlocks() {
        var random = Random(state: 3)
        let indices = (0..<100_000).map { _ in UInt8(random.next() % 200) }
        let out = indices.withUnsafeBufferPointer { GIFLZW.encode($0, minCodeSize: 8) }
        XCTAssertEqual(out.first, 8)
        var position = 1
        while position < out.count, out[position] != 0 {
            XCTAssertLessThanOrEqual(Int(out[position]), 255)
            position += Int(out[position]) + 1
        }
        XCTAssertEqual(position, out.count - 1, "sub-blocks end with a single terminator")
    }

    // MARK: Palette

    func testAPaletteHoldsAtMost255ColoursAndSpendsThemWell() throws {
        let width = 320, height = 240
        // ~77 000 distinct colours: a rainbow over a dark gradient.
        let pixels = (0..<(width * height)).map { i -> UInt32 in
            let x = i % width, y = i / width
            return Self.pack(x * 255 / width, y * 255 / height, (x + y) % 256)
        }
        XCTAssertGreaterThan(Set(pixels).count, 255)
        let (data, encoder) = Self.encode([pixels], width: width, height: height)
        XCTAssertLessThanOrEqual(encoder.paletteSizes[0], 255)
        XCTAssertGreaterThan(encoder.paletteSizes[0], 200, "the colours are used, not wasted")
        let decoded = try Self.pixels(try Self.decode(data), 0, width: width, height: height)
        let error = Self.error(decoded, pixels)
        XCTAssertLessThan(error.mean, 11, "a whole rainbow in 255 colours, dithered, stays close on average")
    }

    func testADarkGradientIsDitheredNotBanded() throws {
        let width = 400, height = 300
        let source = Self.gradient(width: width, height: height)
        XCTAssertGreaterThan(Set(source).count, 1000, "more colours than a palette holds")
        let (data, _) = Self.encode([source], width: width, height: height)
        let decoded = try Self.pixels(try Self.decode(data), 0, width: width, height: height)
        XCTAssertLessThan(Self.error(decoded, source).mean, 1.5)
        // Averaged over 8 × 8 tiles (what the eye sees of dithering), the
        // gradient is reproduced within about a level: no bands.
        var worst = 0.0, tiles = 0, off = 0
        for tileY in stride(from: 0, to: height - 8, by: 8) {
            for tileX in stride(from: 0, to: width - 8, by: 8) {
                var a = SIMD3<Double>.zero, b = SIMD3<Double>.zero
                for y in tileY..<(tileY + 8) {
                    for x in tileX..<(tileX + 8) {
                        let p = Self.channels(source[y * width + x]), q = Self.channels(decoded[y * width + x])
                        a += SIMD3(Double(p.0), Double(p.1), Double(p.2))
                        b += SIMD3(Double(q.0), Double(q.1), Double(q.2))
                    }
                }
                let d = (a - b) / 64
                let error = max(abs(d.x), abs(d.y), abs(d.z))
                worst = max(worst, error)
                tiles += 1
                if error > 1.5 { off += 1 }
            }
        }
        XCTAssertLessThan(worst, 2.5)
        XCTAssertLessThan(Double(off) / Double(tiles), 0.03, "\(off) of \(tiles) tiles off by more than 1.5 levels")
    }

    // MARK: Differencing

    func testLaterFramesStoreOnlyWhatChangedAndDecodeToTheSource() throws {
        let width = 240, height = 300
        let backdrop = Self.gradient(width: width, height: height)
        var frames: [[UInt32]] = []
        for step in 0..<6 {
            var frame = backdrop
            // A "word": a white block with a soft coloured edge, moving right.
            for y in 120..<160 {
                for x in (20 + step * 20)..<(80 + step * 20) {
                    let edge = y == 120 || y == 159 || x == 20 + step * 20
                    frame[y * width + x] = edge ? Self.pack(200, 150, 220) : Self.pack(255, 255, 255)
                }
            }
            frames.append(frame)
        }
        frames.append(frames[5])  // an identical frame lengthens the one before
        let (data, encoder) = Self.encode(frames, width: width, height: height)
        XCTAssertEqual(encoder.frameCount, 6)
        XCTAssertEqual(encoder.delays, [4, 4, 4, 4, 4, 8])
        XCTAssertEqual(encoder.rects[0], GIFEncoder.Rect(x: 0, y: 0, width: width, height: height))
        for rect in encoder.rects.dropFirst() {
            XCTAssertEqual(rect.y, 120)
            XCTAssertEqual(rect.height, 40)
            XCTAssertLessThanOrEqual(rect.width, 81, "only the word's old and new place")
        }
        let source = try Self.decode(data)
        XCTAssertEqual(CGImageSourceGetCount(source), 6)
        var previous: [UInt32]?
        for index in 0..<6 {
            let decoded = try Self.pixels(source, index, width: width, height: height)
            let error = Self.error(decoded, frames[index])
            XCTAssertLessThan(error.mean, 1.5, "frame \(index)")
            if let previous {
                // The untouched backdrop is bit-for-bit what it was: no shimmer.
                let still = (0..<(width * height)).filter { $0 / width < 115 || $0 / width > 165 }
                XCTAssertTrue(still.allSatisfy { decoded[$0] == previous[$0] }, "frame \(index)")
            }
            previous = decoded
        }
    }

    func testABackdropAWordMovesOffComesBackExactly() throws {
        let width = 240, height = 200
        let backdrop = Self.gradient(width: width, height: height)
        var word = backdrop
        // A white block with a soft, coloured glow, unlike anything in the backdrop.
        for y in 60..<140 {
            for x in 40..<200 {
                let edge = min(min(x - 40, 199 - x), min(y - 60, 139 - y))
                word[y * width + x] = edge < 6 ? Self.pack(150 + edge * 15, 90 + edge * 20, 200) : Self.pack(255, 255, 255)
            }
        }
        let (data, encoder) = Self.encode([backdrop, word, backdrop], width: width, height: height)
        XCTAssertEqual(encoder.frameCount, 3)
        let source = try Self.decode(data)
        let before = try Self.pixels(source, 0, width: width, height: height)
        let shown = try Self.pixels(source, 1, width: width, height: height)
        let after = try Self.pixels(source, 2, width: width, height: height)
        XCTAssertLessThan(Self.error(shown, word).mean, 2)
        XCTAssertEqual(after, before, "nothing of the word is left behind")
    }

    func testAToleranceLeavesOutInvisibleChangesWithoutDrifting() throws {
        let width = 64, height = 64
        let encoder = GIFEncoder(width: width, height: height)
        encoder.tolerance = 2
        var frame = [UInt32](repeating: Self.pack(40, 40, 40), count: width * height)
        frame.withUnsafeBufferPointer { _ = encoder.add($0, delay: 4) }
        // Creeping up a level a frame: stored every third frame, never left behind.
        for level in 41...52 {
            frame = [UInt32](repeating: Self.pack(level, level, level), count: width * height)
            frame.withUnsafeBufferPointer { _ = encoder.add($0, delay: 4) }
        }
        XCTAssertEqual(encoder.frameCount, 5)
        let decoded = try Self.pixels(try Self.decode(encoder.data()), 4, width: width, height: height)
        XCTAssertEqual(Self.error(decoded, frame).max, 0)
    }

    // MARK: Delays and looping

    func testDelaysAreWholeCentisecondsOfAtLeastTwoAndTheGIFLoops() throws {
        XCTAssertEqual(GIFEncoder.centiseconds(0), 2)
        XCTAssertEqual(GIFEncoder.centiseconds(0.004), 2)
        XCTAssertEqual(GIFEncoder.centiseconds(0.01), 2, "1 cs would be slowed to 10 by browsers")
        XCTAssertEqual(GIFEncoder.centiseconds(0.04), 4)
        XCTAssertEqual(GIFEncoder.centiseconds(0.045), 5)
        XCTAssertEqual(GIFEncoder.centiseconds(1.8), 180)
        XCTAssertEqual(GIFEncoder.centiseconds(1000), 65535)
        let width = 16, height = 16
        let writer = GIFWriter(byteBudget: nil)
        for (index, delay) in [0.004, 0.04, 0.05, 1.8].enumerated() {
            let frame = [UInt32](repeating: Self.pack(index * 60, 20, 200 - index * 40), count: width * height)
            XCTAssertTrue(writer.add(Self.image(frame, width: width, height: height), delay: delay))
        }
        let source = try Self.decode(try XCTUnwrap(writer.finish()))
        XCTAssertEqual(CGImageSourceGetCount(source), 4)
        let expected = [0.02, 0.04, 0.05, 1.8]
        for index in 0..<4 {
            let delay = try Self.delay(source, index)
            XCTAssertEqual(delay.unclamped, expected[index], accuracy: 0.001)
            XCTAssertEqual(delay.clamped, expected[index], accuracy: 0.001, "ImageIO does not slow it down")
            XCTAssertGreaterThanOrEqual(delay.unclamped, 0.02)
        }
        let properties = try XCTUnwrap(CGImageSourceCopyProperties(source, nil) as? [CFString: Any])
        let gif = try XCTUnwrap(properties[kCGImagePropertyGIFDictionary] as? [CFString: Any])
        XCTAssertEqual(gif[kCGImagePropertyGIFLoopCount] as? Int, 0, "loops forever")
    }

    // MARK: Size budget

    /// Two seconds at 25 fps of noise sweeping over a gradient: a lot of bytes.
    private func busyFrames(width: Int, height: Int) -> [CGImage] {
        let backdrop = Self.gradient(width: width, height: height)
        var random = Random(state: 11)
        return (0..<50).map { step in
            var frame = backdrop
            for y in 40..<(height - 40) {
                for x in 0..<(width / 3) {
                    let value = Int(random.next() % 256)
                    frame[y * width + (x + step * 4) % width] = Self.pack(value, 255 - value, (value * 7) % 256)
                }
            }
            return Self.image(frame, width: width, height: height)
        }
    }

    func testAGIFOverBudgetDropsFramesFirstThenShrinks() throws {
        let width = 200, height = 160
        let frames = busyFrames(width: width, height: height)
        func make(budget: Int?) throws -> (Data, GIFWriter.Summary) {
            let writer = GIFWriter(byteBudget: budget)
            for frame in frames { XCTAssertTrue(writer.add(frame, delay: 0.04)) }
            let data = try XCTUnwrap(writer.finish())
            return (data, try XCTUnwrap(writer.summary))
        }
        let (full, unlimited) = try make(budget: nil)
        XCTAssertEqual(unlimited.frames, 50)
        XCTAssertEqual(unlimited.scale, 1)
        XCTAssertEqual(full.count, unlimited.bytes)

        // A little over: fewer frames per second, same size.
        let (lighter, fewer) = try make(budget: full.count * 3 / 4)
        XCTAssertLessThanOrEqual(lighter.count, full.count * 3 / 4)
        XCTAssertEqual(lighter.count, fewer.bytes)
        XCTAssertLessThan(fewer.frames, 50)
        XCTAssertGreaterThan(fewer.gap, 4)
        XCTAssertEqual(fewer.scale, 1)
        var source = try Self.decode(lighter)
        var total = 0.0
        for index in 0..<CGImageSourceGetCount(source) { total += try Self.delay(source, index).unclamped }
        XCTAssertEqual(total, 2, accuracy: 0.001, "the GIF lasts as long as before")
        XCTAssertEqual(try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil)).width, width)

        // Far over: smaller too.
        let (smallest, shrunk) = try make(budget: full.count / 5)
        XCTAssertLessThanOrEqual(smallest.count, full.count / 5)
        XCTAssertLessThan(shrunk.scale, 1)
        source = try Self.decode(smallest)
        XCTAssertLessThan(try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil)).width, width)
    }

    // MARK: Cancelling

    func testACancelledWriterStopsAndGivesNothing() {
        let width = 32, height = 32
        let frame = Self.image(Self.gradient(width: width, height: height), width: width, height: height)
        let writer = GIFWriter(byteBudget: nil)
        XCTAssertTrue(writer.add(frame, delay: 0.04))
        writer.cancel()
        XCTAssertFalse(writer.add(frame, delay: 0.04))
        XCTAssertNil(writer.finish())
    }

    @MainActor
    func testCancellingALyricCardGIFStopsIt() async throws {
        let words = (0..<12).map { LyricsFetcher.Word(text: "word\($0)", start: 10 + Double($0) * 0.5, end: 10.45 + Double($0) * 0.5) }
        let line = LyricsFetcher.Line(start: 10, end: 16, text: words.map(\.text).joined(separator: " "), words: words,
                                      isInstrumental: false, wordTimed: true)
        let content = LyricCard.Content(pages: [
            .init(words: words.map(\.text), animation: LyricCard.Animation(line: line, composition: .cutout, mood: .neutral, seed: 0)),
        ], title: "A Song", artist: "An Artist", artwork: nil, palette: LyricCard.palette(for: nil), reduceMotion: true)
        var reported: [Double] = []
        let task = Task { await LyricCard.gif(content, scale: 0.5, progress: { reported.append($0) }) }
        for _ in 0..<400 where reported.count < 5 { try await Task.sleep(nanoseconds: 5_000_000) }
        task.cancel()
        let result = await task.value
        XCTAssertNil(result)
        XCTAssertLessThan(reported.last ?? 0, LyricCard.gifDrawingShare, "stopped while drawing")
    }

    @MainActor
    func testALyricCardGIFReportsProgressForwardToTheEnd() async throws {
        let words = (0..<4).map { LyricsFetcher.Word(text: "word\($0)", start: 10 + Double($0) * 0.3, end: 10.28 + Double($0) * 0.3) }
        let line = LyricsFetcher.Line(start: 10, end: 11.4, text: words.map(\.text).joined(separator: " "), words: words,
                                      isInstrumental: false, wordTimed: true)
        let content = LyricCard.Content(pages: [
            .init(words: words.map(\.text), animation: LyricCard.Animation(line: line, composition: .spoken, mood: .neutral, seed: 0)),
        ], title: "A Song", artist: "An Artist", artwork: nil, palette: LyricCard.palette(for: nil), reduceMotion: true)
        var reported: [Double] = []
        var encodingAt: Int?
        let encoded = await LyricCard.gif(content, scale: 0.5, progress: { reported.append($0) },
                                          encoding: { encodingAt = reported.count })
        let gif = try XCTUnwrap(encoded)
        XCTAssertEqual(reported, reported.sorted(), "progress only moves forward")
        XCTAssertNotNil(encodingAt)
        XCTAssertEqual(reported.last ?? 0, 1, accuracy: 1e-9)
        let source = try Self.decode(gif)
        var total = 0.0
        for index in 0..<CGImageSourceGetCount(source) {
            let delay = try Self.delay(source, index)
            XCTAssertGreaterThanOrEqual(delay.unclamped, 0.02)
            XCTAssertEqual(delay.clamped, delay.unclamped, accuracy: 0.001)
            total += delay.unclamped
        }
        XCTAssertGreaterThanOrEqual(total, line.end - line.start - 0.05)
    }

    // MARK: Before and after (run on demand)

    /// Writes the old ImageIO GIF and the new one of the same ten-second card
    /// (dark gradient, cover, a sung line) to `GIF_COMPARISON_DIR`: crops of
    /// the same moment, the numbers, and optionally every source frame as PNG
    /// to `GIF_FRAMES_DIR` for timing the encoder in a Release build.
    /// `TEST_RUNNER_GIF_COMPARISON_DIR=… xcodebuild test …` sets it.
    @MainActor
    func testWriteBeforeAndAfterComparison() async throws {
        let environment = ProcessInfo.processInfo.environment
        guard let folder = environment["GIF_COMPARISON_DIR"] else { throw XCTSkip("Set GIF_COMPARISON_DIR to write the comparison") }
        let out = URL(fileURLWithPath: folder, isDirectory: true)
        try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
        let artwork = NSImage(size: NSSize(width: 300, height: 300), flipped: false) { rect in
            NSGradient(colors: [NSColor(srgbRed: 0.05, green: 0.04, blue: 0.16, alpha: 1),
                                NSColor(srgbRed: 0.30, green: 0.10, blue: 0.32, alpha: 1),
                                NSColor(srgbRed: 0.85, green: 0.45, blue: 0.30, alpha: 1)])?.draw(in: rect, angle: 60)
            NSColor(srgbRed: 0.95, green: 0.80, blue: 0.55, alpha: 1).setFill()
            NSBezierPath(ovalIn: rect.insetBy(dx: 90, dy: 90)).fill()
            return true
        }
        let labels = "Hold on to the light until the morning comes and every shadow learns to let us go".split(separator: " ").map(String.init)
        let words = labels.enumerated().map { index, text in
            LyricsFetcher.Word(text: text, start: 10.2 + Double(index) * 0.55, end: 10.7 + Double(index) * 0.55)
        }
        let line = LyricsFetcher.Line(start: 10, end: 20, text: labels.joined(separator: " "), words: words,
                                      isInstrumental: false, wordTimed: true)
        let content = LyricCard.Content(pages: [.init(words: labels, animation: .init(line: line, composition: .cutout, mood: .neutral, seed: 0))],
                                        title: "Where the Night Begins", artist: "June Arcade", artwork: artwork,
                                        palette: LyricCard.palette(for: artwork))
        let moment = 5.0
        // A stretch of the gradient to look at, and two strips of it the
        // lyrics never reach, to count flicker in.
        let crop = CGRect(x: 600, y: 0, width: 480, height: 600)
        let still = [CGRect(x: 0, y: 0, width: 1080, height: 130), CGRect(x: 960, y: 130, width: 120, height: 870)]

        // The frames alone: drawing time, the source at `moment`, optional PNGs.
        let framesFolder = environment["GIF_FRAMES_DIR"].map { URL(fileURLWithPath: $0, isDirectory: true) }
        if let framesFolder { try FileManager.default.createDirectory(at: framesFolder, withIntermediateDirectories: true) }
        var sourceAtMoment: CGImage?
        var elapsed = 0.0, drawnFrames = 0
        let drawStart = Date()
        await LyricCard.renderFrames(content, scale: 1, minimumCadence: LyricCard.gifCadence, budget: nil) { image, delay in
            if sourceAtMoment == nil, elapsed + delay > moment { sourceAtMoment = image }
            if let framesFolder {
                let name = String(format: "f%04d_%d.png", drawnFrames, Int((delay * 100).rounded()))
                try? NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])?
                    .write(to: framesFolder.appendingPathComponent(name))
            }
            elapsed += delay
            drawnFrames += 1
            return true
        }
        let drawing = Date().timeIntervalSince(drawStart)

        // Before: 12.5 fps capped at 90 frames, ImageIO's encoder.
        var old: [(CGImage, Double)] = []
        await LyricCard.renderFrames(content, scale: 1, minimumCadence: 0.08, budget: 90) { image, delay in
            old.append((image, delay))
            return true
        }
        let oldStart = Date()
        let oldData = NSMutableData()
        let destination = try XCTUnwrap(CGImageDestinationCreateWithData(oldData, UTType.gif.identifier as CFString, old.count, nil))
        CGImageDestinationSetProperties(destination, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]] as CFDictionary)
        for (image, delay) in old {
            CGImageDestinationAddImage(destination, image,
                                       [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: delay]] as CFDictionary)
        }
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        let oldEncoding = Date().timeIntervalSince(oldStart)
        old.removeAll()

        // After: drawn and encoded together.
        let newStart = Date()
        let made = await LyricCard.gif(content)
        let newData = try XCTUnwrap(made)
        let newTotal = Date().timeIntervalSince(newStart)

        func save(_ image: CGImage, _ name: String) throws {
            let data = try XCTUnwrap(NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]))
            try data.write(to: out.appendingPathComponent(name))
        }
        /// The crop with its levels stretched ×4 around its mean, so bands show.
        func boosted(_ image: CGImage) throws -> CGImage {
            let width = image.width, height = image.height
            var pixels = [UInt32](repeating: 0, count: width * height)
            GIFWriter.draw(image, into: &pixels, width: width, height: height)
            var mean = SIMD3<Double>.zero
            for value in pixels { let c = Self.channels(value); mean += SIMD3(Double(c.0), Double(c.1), Double(c.2)) }
            mean /= Double(pixels.count)
            let stretched = pixels.map { value -> UInt32 in
                let c = Self.channels(value)
                func s(_ v: Int, _ m: Double) -> Int { min(255, max(0, Int(128 + (Double(v) - m) * 4))) }
                return Self.pack(s(c.0, mean.x), s(c.1, mean.y), s(c.2, mean.z))
            }
            return Self.image(stretched, width: width, height: height)
        }
        struct Stats { var frames: Int; var duration: Double; var movingFPS: Double; var stillError: Double; var stillBanding: Double; var stillTilesOff: Int; var stillTiles: Int; var cropFlicker: Int }
        func study(_ data: Data, prefix: String) throws -> Stats {
            let source = try Self.decode(data)
            let count = CGImageSourceGetCount(source)
            var time = 0.0, shown = 0, moving = 0, movingTime = 0.0
            var previousStill: [[UInt32]]?
            var flicker = 0
            for index in 0..<count {
                let delay = try Self.delay(source, index).unclamped
                XCTAssertGreaterThanOrEqual(delay, 0.02, "\(prefix) frame \(index)")
                if time <= moment { shown = index }
                if delay < 0.5 { moving += 1; movingTime += delay }
                time += delay
                let frame = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, index, nil))
                let strips = try still.map { rect -> [UInt32] in
                    var pixels = [UInt32](repeating: 0, count: Int(rect.width * rect.height))
                    GIFWriter.draw(try XCTUnwrap(frame.cropping(to: rect)), into: &pixels, width: Int(rect.width), height: Int(rect.height))
                    return pixels
                }
                if let previousStill {
                    for (old, new) in zip(previousStill, strips) { flicker += zip(old, new).filter { $0.0 != $0.1 }.count }
                }
                previousStill = strips
            }
            let frame = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, shown, nil))
            try save(frame, "\(prefix)-frame.png")
            let cropped = try XCTUnwrap(frame.cropping(to: crop))
            try save(cropped, "\(prefix)-gradient.png")
            try save(try boosted(cropped), "\(prefix)-gradient-boosted.png")
            // Against the source, where the lyrics never reach: per pixel, and
            // averaged over 8 × 8 tiles (what the eye sees of dithering), whose
            // worst-tile error is what shows as bands.
            var errorSum = 0.0, errorCount = 0, banding = 0.0, tiles = 0, tilesOff = 0
            for rect in still {
                let w = Int(rect.width), h = Int(rect.height)
                var decoded = [UInt32](repeating: 0, count: w * h), truth = decoded
                GIFWriter.draw(try XCTUnwrap(frame.cropping(to: rect)), into: &decoded, width: w, height: h)
                GIFWriter.draw(try XCTUnwrap(sourceAtMoment?.cropping(to: rect)), into: &truth, width: w, height: h)
                let error = Self.error(decoded.map { $0 & 0xFF_FFFF }, truth.map { $0 & 0xFF_FFFF })
                errorSum += error.mean * Double(w * h)
                errorCount += w * h
                for ty in stride(from: 0, to: h - 7, by: 8) {
                    for tx in stride(from: 0, to: w - 7, by: 8) {
                        var d = SIMD3<Double>.zero
                        for y in ty..<(ty + 8) {
                            for x in tx..<(tx + 8) {
                                let p = Self.channels(decoded[y * w + x]), q = Self.channels(truth[y * w + x])
                                d += SIMD3(Double(p.0 - q.0), Double(p.1 - q.1), Double(p.2 - q.2))
                            }
                        }
                        d /= 64
                        let tile = max(abs(d.x), abs(d.y), abs(d.z))
                        banding = max(banding, tile)
                        tiles += 1
                        if tile > 1.5 { tilesOff += 1 }
                    }
                }
            }
            return Stats(frames: count, duration: time, movingFPS: Double(moving) / max(movingTime, 0.01),
                         stillError: errorSum / Double(errorCount), stillBanding: banding,
                         stillTilesOff: tilesOff, stillTiles: tiles, cropFlicker: flicker)
        }
        let source = try XCTUnwrap(sourceAtMoment)
        try save(source, "source-frame.png")
        try save(try XCTUnwrap(source.cropping(to: crop)), "source-gradient.png")
        try save(try boosted(try XCTUnwrap(source.cropping(to: crop))), "source-gradient-boosted.png")
        let before = try study(oldData as Data, prefix: "before")
        let after = try study(newData, prefix: "after")
        try (oldData as Data).write(to: out.appendingPathComponent("before.gif"))
        try newData.write(to: out.appendingPathComponent("after.gif"))
        let text = """
        Ten-second lyric card, 1080 × 1350, dark gradient + cover + sung line (cutout), Debug build.
        Frames drawn at 25 fps alone: \(drawnFrames) frames in \(String(format: "%.2f", drawing)) s.

                                before (ImageIO)    after (GIFEncoder)
        bytes                   \(String(format: "%-20d", oldData.length))\(newData.count)
        frames                  \(String(format: "%-20d", before.frames))\(after.frames)
        duration (s)            \(String(format: "%-20.2f", before.duration))\(String(format: "%.2f", after.duration))
        moving fps              \(String(format: "%-20.1f", before.movingFPS))\(String(format: "%.1f", after.movingFPS))
        encode time (s)         \(String(format: "%-20.2f", oldEncoding))\(String(format: "%.2f", newTotal)) (drawing + encoding together; drawing alone \(String(format: "%.2f", drawing)))
        gradient error          \(String(format: "%-20.2f", before.stillError))\(String(format: "%.2f", after.stillError))  (mean levels per pixel vs source, where the lyrics never reach)
        worst 8×8 tile error    \(String(format: "%-20.2f", before.stillBanding))\(String(format: "%.2f", after.stillBanding))  (levels, the tile mean against the source's)
        tiles off > 1.5 levels  \("\(before.stillTilesOff) of \(before.stillTiles)".padding(toLength: 20, withPad: " ", startingAt: 0))\(after.stillTilesOff) of \(after.stillTiles)  (broad runs of these are what shows as bands)
        gradient flicker        \(String(format: "%-20d", before.cropFlicker))\(after.cropFlicker)  (pixel changes between frames where the lyrics never reach)

        """
        try text.write(to: out.appendingPathComponent("numbers.txt"), atomically: true, encoding: .utf8)
        print(text)
    }
}
