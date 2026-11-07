import AVFoundation
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import VersoCustom

@MainActor
final class LyricCardBackgroundTests: XCTestCase {
    private let folder = FileManager.default.temporaryDirectory.appendingPathComponent("card-bg-\(UUID().uuidString)")

    override func setUp() { try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
    override func tearDown() { try? FileManager.default.removeItem(at: folder) }

    private func solid(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, size: Int = 64) -> CGImage {
        let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(red: r, green: g, blue: b, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: size, height: size))
        return context.makeImage()!
    }

    private func write(_ images: [CGImage], type: UTType, name: String, delay: Double = 0.1) -> URL {
        let url = folder.appendingPathComponent(name)
        let destination = CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, images.count, nil)!
        if type == .gif {
            CGImageDestinationSetProperties(destination, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]] as CFDictionary)
        }
        for image in images {
            CGImageDestinationAddImage(destination, image,
                                       [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: delay]] as CFDictionary)
        }
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        return url
    }

    private func red(of image: CGImage) -> (r: Int, g: Int, b: Int) {
        let rep = NSBitmapImageRep(cgImage: image)
        let color = rep.colorAt(x: image.width / 2, y: 12)!.usingColorSpace(.sRGB)!
        return (Int(color.redComponent * 255), Int(color.greenComponent * 255), Int(color.blueComponent * 255))
    }

    private func content() -> LyricCard.Content {
        let words = "Hold on tight tonight".split(separator: " ").enumerated().map { i, w in
            LyricsFetcher.Word(text: String(w), start: 10 + Double(i) * 0.3, end: 10.28 + Double(i) * 0.3)
        }
        let line = LyricsFetcher.Line(start: 10, end: 11.6, text: "Hold on tight tonight", words: words,
                                      isInstrumental: false, wordTimed: true)
        let animation = LyricCard.Animation(line: line, composition: .editorial, mood: .neutral, seed: 0)
        let art = NSImage(size: NSSize(width: 64, height: 64), flipped: false) { _ in true }
        var content = LyricCard.Content(pages: [.init(words: words.map(\.text), animation: animation)], title: "A Song",
                                        artist: "An Artist", artwork: art, palette: LyricCard.palette(for: art), reduceMotion: true)
        content.aspect = .square
        return content
    }

    func testAPictureReplacesTheAlbumLook() async throws {
        let url = write([solid(1, 0, 0)], type: .png, name: "red.png")
        let loaded = await LyricCardBackground.load(url)
        let background = try XCTUnwrap(loaded)
        XCTAssertEqual(background.kind, .image)
        XCTAssertFalse(background.isAnimated)
        var card = content()
        card.background = background
        card.backgroundStill = background.first
        let png = try XCTUnwrap(LyricCard.stillPNG(card, scale: 1))
        let rep = try XCTUnwrap(NSBitmapImageRep(data: png))
        let colour = red(of: try XCTUnwrap(rep.cgImage))
        XCTAssertGreaterThan(colour.r, 80)
        XCTAssertLessThan(colour.g, 30)
        XCTAssertLessThan(colour.b, 30)
    }

    func testAGIFIsReadFrameByFrameAndLoops() async throws {
        let url = write([solid(1, 0, 0), solid(0, 1, 0), solid(0, 0, 1)], type: .gif, name: "three.gif")
        let loaded = await LyricCardBackground.load(url)
        let background = try XCTUnwrap(loaded)
        XCTAssertEqual(background.kind, .animatedImage)
        XCTAssertEqual(background.duration, 0.3, accuracy: 0.001)
        let a = await background.frame(at: 0.05)
        let b = await background.frame(at: 0.15)
        let c = await background.frame(at: 0.35)
        let first = red(of: a), second = red(of: b), looped = red(of: c)
        XCTAssertGreaterThan(first.r, 200)
        XCTAssertGreaterThan(second.g, 200)
        XCTAssertGreaterThan(looped.r, 200)
    }

    func testAVideoBackgroundPlaysUnderTheLyrics() async throws {
        let source = folder.appendingPathComponent("source.mp4")
        let made = await LyricCard.video(content(), to: source)
        XCTAssertTrue(made)
        let loaded = await LyricCardBackground.load(source)
        let background = try XCTUnwrap(loaded)
        XCTAssertEqual(background.kind, .video)
        XCTAssertGreaterThan(background.duration, 1)
        let later = await background.frame(at: background.duration + 0.4)
        XCTAssertGreaterThan(later.width, 100)

        var card = content()
        card.background = background
        card.backgroundStill = background.first
        let output = folder.appendingPathComponent("over.mp4")
        let again = await LyricCard.video(card, to: output)
        XCTAssertTrue(again)
        let seconds = try await AVURLAsset(url: output).load(.duration).seconds
        XCTAssertGreaterThan(seconds, 1.4)
    }

    func testAFileThatIsNotMediaIsRefused() async {
        let url = folder.appendingPathComponent("notes.txt")
        try? Data("hello".utf8).write(to: url)
        let loaded = await LyricCardBackground.load(url)
        XCTAssertNil(loaded)
    }

    private func tone(seconds: Double) throws -> URL {
        let url = folder.appendingPathComponent("tone.m4a")
        let format = AVAudioFormat(standardFormatWithSampleRate: 44100, channels: 1)!
        let file = try AVAudioFile(forWriting: url, settings: [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 44100,
                                                               AVNumberOfChannelsKey: 1])
        let frames = AVAudioFrameCount(44100 * seconds)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames)!
        buffer.frameLength = frames
        for i in 0..<Int(frames) { buffer.floatChannelData![0][i] = Float(sin(Double(i) * 2 * .pi * 440 / 44100)) * 0.3 }
        try file.write(from: buffer)
        return url
    }

    func testAVideoBackgroundsSoundIsCarriedIntoTheMP4AndLooped() async throws {
        // A background video with a 0.6 s tone under it.
        let silent = folder.appendingPathComponent("silent.mp4")
        let made = await LyricCard.video(content(), to: silent)
        XCTAssertTrue(made)
        let muxed = await LyricCard.addSound(from: try tone(seconds: 0.6), to: silent)
        XCTAssertTrue(muxed)
        let loaded = await LyricCardBackground.load(silent)
        let background = try XCTUnwrap(loaded)
        XCTAssertTrue(background.hasAudio)

        var card = content()
        card.background = background
        card.backgroundStill = background.first
        let directory = folder.appendingPathComponent("out")
        let files = await LyricCard.export(card, formats: [.mp4], to: directory)
        let file = try XCTUnwrap(files.first?.1)
        let asset = AVURLAsset(url: file)
        let audio = try await asset.loadTracks(withMediaType: .audio)
        XCTAssertEqual(audio.count, 1)
        let sound = try await audio[0].load(.timeRange).duration.seconds
        let video = try await asset.load(.duration).seconds
        XCTAssertEqual(sound, video, accuracy: 0.3)

        card.keepBackgroundSound = false
        let quiet = await LyricCard.export(card, formats: [.mp4], to: folder.appendingPathComponent("quiet"))
        let silentTracks = try await AVURLAsset(url: try XCTUnwrap(quiet.first?.1)).loadTracks(withMediaType: .audio)
        XCTAssertTrue(silentTracks.isEmpty)
    }
}
