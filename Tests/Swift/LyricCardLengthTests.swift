import AVFoundation
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import VersoCustom

/// How long a lyric card's MP4 lasts, what its frames and sound do at the
/// ends, and what the popout is told. Files are made in a temporary folder and
/// analysed; nothing is played.
@MainActor
final class LyricCardLengthTests: XCTestCase {
    private let folder = FileManager.default.temporaryDirectory.appendingPathComponent("card-length-\(UUID().uuidString)")
    override func setUp() { try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
    override func tearDown() { try? FileManager.default.removeItem(at: folder) }

    private let threeLines: [(start: Double, end: Double)] = [(10, 11.6), (12, 13.6), (14, 15.6)]

    private func background(seconds: Double, sound: Bool = false,
                            colour: (Double) -> (CGFloat, CGFloat, CGFloat) = { _ in (0.2, 0.4, 0.6) }) async throws -> LyricCardBackground {
        let url = try await MixProbe.video(folder.appendingPathComponent("bg-\(UUID().uuidString).mp4"), seconds: seconds, colour: colour)
        if sound {
            let tone = try MixProbe.tone(folder.appendingPathComponent("bgtone-\(UUID().uuidString).wav"), frequency: 440,
                                         amplitude: 0.3, seconds: 1)
            let added = await LyricCard.addSound(from: tone, to: url)
            XCTAssertTrue(added)
        }
        let loaded = await LyricCardBackground.load(url)
        return try XCTUnwrap(loaded)
    }

    private func card(lines: [(start: Double, end: Double)] = [(10, 11.6)], background: LyricCardBackground? = nil) -> LyricCard.Content {
        var content = MixProbe.content(lines: lines)
        content.background = background
        content.backgroundStill = background?.first
        return content
    }

    private func lyricsLength(_ content: LyricCard.Content) -> Double {
        LyricCard.pageClips(content).reduce(0) { $0 + $1.duration }
    }

    // MARK: Decisions

    func testWithoutAVideoTheCardFollowsTheLines() async throws {
        let content = card()
        let plan = LyricCard.clipPlan(content)
        XCTAssertEqual(plan.length, .matchLyrics)
        XCTAssertFalse(plan.offersChoice)
        XCTAssertEqual(plan.duration, lyricsLength(content), accuracy: 1e-9)
        XCTAssertGreaterThan(plan.duration, 1.8)
        XCTAssertNil(plan.backgroundLoop)
        XCTAssertFalse(plan.holdsLastLine)
        XCTAssertNil(plan.note)

        // A picture behaves the same, whatever the choice says.
        let png = folder.appendingPathComponent("still.png")
        let context = CGContext(data: nil, width: 8, height: 8, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(red: 1, green: 0, blue: 0, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: 8, height: 8))
        let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(png as CFURL, UTType.png.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, context.makeImage()!, nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        let loadedPicture = await LyricCardBackground.load(png)
        let picture = try XCTUnwrap(loadedPicture)
        var withPicture = card(background: picture)
        withPicture.clipLength = .matchBackground
        let still = LyricCard.clipPlan(withPicture)
        XCTAssertEqual(still.length, .matchLyrics)
        XCTAssertFalse(still.offersChoice)
        XCTAssertEqual(still.duration, plan.duration, accuracy: 1e-9)
    }

    func testAShortVideoAlwaysFollowsTheLinesAndLoops() async throws {
        let short = try await background(seconds: 1.5)
        var content = card(background: short)
        content.clipLength = .matchBackground
        let plan = LyricCard.clipPlan(content)
        XCTAssertFalse(plan.offersChoice)
        XCTAssertEqual(plan.length, .matchLyrics)
        XCTAssertEqual(plan.duration, lyricsLength(content), accuracy: 1e-9)
        XCTAssertEqual(plan.backgroundLoop, LoopTiming(duration: short.duration, crossfade: 0.3))
    }

    func testALongerVideoSetsTheLengthAndTheLastLineHoldsThenFades() async throws {
        let long = try await background(seconds: 5)
        let content = card(background: long)
        let plan = LyricCard.clipPlan(content)
        XCTAssertTrue(plan.offersChoice)
        XCTAssertEqual(plan.length, .matchBackground)
        XCTAssertEqual(plan.duration, 5, accuracy: 0.01)
        XCTAssertTrue(plan.holdsLastLine)
        XCTAssertTrue(plan.fadesLastLine, "more than 3 s are left after the lines")
        XCTAssertEqual(plan.lyricOpacity(at: 2), 1)
        XCTAssertEqual(plan.lyricOpacity(at: 4.5), 1, accuracy: 1e-6)
        XCTAssertEqual(plan.lyricOpacity(at: 4.75), 0.5, accuracy: 0.03)
        XCTAssertEqual(plan.lyricOpacity(at: 5), 0, accuracy: 0.03)
        XCTAssertEqual(plan.backgroundLoop?.crossfade, 0, "the video plays once, so it never loops")
        XCTAssertTrue(plan.summary.contains("fades out"), plan.summary)

        // Less than 3 s left: held to the end without fading.
        let medium = try await background(seconds: 3.5)
        let held = LyricCard.clipPlan(card(background: medium))
        XCTAssertTrue(held.holdsLastLine)
        XCTAssertFalse(held.fadesLastLine)
        XCTAssertEqual(held.lyricOpacity(at: 3.49), 1)
        XCTAssertTrue(held.summary.contains("stays up"), held.summary)
    }

    func testMatchLyricsWithALongVideoKeepsTheOldLength() async throws {
        let long = try await background(seconds: 5)
        var content = card(background: long)
        content.clipLength = .matchLyrics
        let plan = LyricCard.clipPlan(content)
        XCTAssertTrue(plan.offersChoice)
        XCTAssertEqual(plan.length, .matchLyrics)
        XCTAssertEqual(plan.duration, lyricsLength(content), accuracy: 1e-9)
        XCTAssertFalse(plan.holdsLastLine)

        // Lines longer than the video: it loops, crossfaded.
        let short = try await background(seconds: 2.5)
        var looping = card(lines: threeLines, background: short)
        looping.clipLength = .matchLyrics
        let loopPlan = LyricCard.clipPlan(looping)
        XCTAssertEqual(loopPlan.backgroundLoop?.crossfade, 0.3)
        XCTAssertEqual(loopPlan.linesCut, 0)
    }

    func testLinesThatRunPastTheVideoAreCutWithANote() async throws {
        let short = try await background(seconds: 2.5)
        let content = card(lines: threeLines, background: short)
        XCTAssertGreaterThan(lyricsLength(content), 4.5)
        let plan = LyricCard.clipPlan(content)
        XCTAssertEqual(plan.length, .matchBackground)
        XCTAssertEqual(plan.duration, 2.5, accuracy: 0.01)
        XCTAssertEqual(plan.linesCut, 2)
        XCTAssertFalse(plan.holdsLastLine)
        XCTAssertEqual(plan.note, "2 lines won’t fit; pick fewer lines or Match lyrics")
        XCTAssertEqual(plan.summary, plan.note)
    }

    func testMaxSixtySecondsCapsALongCard() {
        var content = card(lines: [(10, 80)])
        XCTAssertEqual(LyricCard.clipPlan(content).duration, lyricsLength(content), accuracy: 1e-9)
        XCTAssertNil(LyricCard.clipPlan(content).note)
        content.capsLength = true
        let plan = LyricCard.clipPlan(content)
        XCTAssertEqual(plan.duration, 60)
        XCTAssertEqual(plan.linesCut, 1)
        XCTAssertEqual(plan.note, "1 line won’t fit in 60 s; pick fewer lines or turn off Max 60 s")
        // A short card is untouched by the cap.
        var short = card()
        short.capsLength = true
        XCTAssertEqual(LyricCard.clipPlan(short).duration, lyricsLength(short), accuracy: 1e-9)
    }

    // MARK: Loops

    func testLoopTimingCrossfadesAtEachSeam() {
        let loop = LoopTiming(duration: 2, crossfade: 0.3)
        XCTAssertEqual(loop.period, 1.7, accuracy: 1e-9)
        XCTAssertEqual(loop.seams(before: 5).count, 2)
        XCTAssertEqual(loop.seams(before: 5)[0], 1.7, accuracy: 1e-9)
        XCTAssertEqual(loop.seams(before: 5)[1], 3.4, accuracy: 1e-9)
        let first = loop.sample(at: 1.0)
        XCTAssertEqual(first.position, 1.0, accuracy: 1e-9)
        XCTAssertNil(first.previous)
        let seam = loop.sample(at: 1.8)
        XCTAssertEqual(seam.position, 0.1, accuracy: 1e-9)
        XCTAssertEqual(try XCTUnwrap(seam.previous), 1.8, accuracy: 1e-9)
        XCTAssertEqual(seam.weight, 1.0 / 3, accuracy: 1e-9)
        let after = loop.sample(at: 2.1)
        XCTAssertEqual(after.position, 0.4, accuracy: 1e-9)
        XCTAssertNil(after.previous)
        let second = loop.sample(at: 3.4 + 0.15)
        XCTAssertEqual(second.weight, 0.5, accuracy: 1e-9)
        // Without a crossfade it is a plain repeat.
        let plain = LoopTiming(duration: 2, crossfade: 0)
        XCTAssertEqual(plain.sample(at: 4.5).position, 0.5, accuracy: 1e-9)
        XCTAssertNil(plain.sample(at: 4.5).previous)
        XCTAssertTrue(plain.seams(before: 10).isEmpty)
    }

    func testALoopingVideoBlendsItsEndIntoItsStart() async throws {
        // Red for the first 0.6 s, blue after.
        let clip = try await background(seconds: 1.2) { $0 < 0.6 ? (1, 0, 0) : (0, 0, 1) }
        let loop = LoopTiming(duration: clip.duration, crossfade: 0.3)
        func colour(at time: Double) async -> (r: Int, b: Int) {
            let image = await clip.frame(at: time, loop: loop)
            let rep = NSBitmapImageRep(cgImage: image)
            let c = rep.colorAt(x: image.width / 2, y: image.height / 2)!.usingColorSpace(.sRGB)!
            return (Int(c.redComponent * 255), Int(c.blueComponent * 255))
        }
        let start = await colour(at: 0.3)
        XCTAssertGreaterThan(start.r, 200); XCTAssertLessThan(start.b, 60)
        let end = await colour(at: 0.85)
        XCTAssertLessThan(end.r, 60); XCTAssertGreaterThan(end.b, 200)
        let middle = await colour(at: 0.9 + 0.15) // halfway through the first seam
        XCTAssertTrue((90...170).contains(middle.r), "\(middle)")
        XCTAssertTrue((90...170).contains(middle.b), "\(middle)")
        let past = await colour(at: 0.9 + 0.35) // the second pass, back to red
        XCTAssertGreaterThan(past.r, 200); XCTAssertLessThan(past.b, 60)
    }

    // MARK: Frames

    private func frames(_ content: LyricCard.Content, plan: LyricCard.ClipPlan?) async -> [(time: Double, image: CGImage)] {
        var out: [(Double, CGImage)] = []
        var time = 0.0
        let ok = await LyricCard.renderFrames(content, scale: 0.25, minimumCadence: 0.1, budget: nil, plan: plan) { image, delay in
            out.append((time, image))
            time += delay
            return true
        }
        XCTAssertTrue(ok)
        out.append((time, out.last!.1)) // the end time, for totals
        return out
    }

    private func difference(_ a: CGImage, _ b: CGImage) -> Double {
        func pixels(_ image: CGImage) -> [UInt8] {
            var data = [UInt8](repeating: 0, count: a.width * a.height * 4)
            let context = CGContext(data: &data, width: a.width, height: a.height, bitsPerComponent: 8, bytesPerRow: a.width * 4,
                                    space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
            context.draw(image, in: CGRect(x: 0, y: 0, width: a.width, height: a.height))
            return data
        }
        let x = pixels(a), y = pixels(b)
        return zip(x, y).reduce(0.0) { $0 + abs(Double($1.0) - Double($1.1)) } / Double(x.count)
    }

    func testFramesHoldTheLastLineThenFadeItToTheVideosEnd() async throws {
        let long = try await background(seconds: 5)
        let content = card(background: long)
        let plan = LyricCard.clipPlan(content)
        let made = await frames(content, plan: plan)
        XCTAssertEqual(made.last!.time, 5, accuracy: 0.03)
        let shown = made.dropLast()
        let last = shown.last!.image
        func frame(near time: Double) -> CGImage { shown.min { abs($0.time - time) < abs($1.time - time) }!.image }
        let held = difference(frame(near: 3.5), last)
        let halfway = difference(frame(near: 4.75), last)
        let gone = difference(frame(near: 4.95), last)
        XCTAssertGreaterThan(held, 0.5, "the held line is on screen")
        XCTAssertLessThan(halfway, held * 0.8, "fading")
        XCTAssertLessThan(gone, halfway)
        // The held frames show the line as it ended: the same picture from 2.5 s to 4.4 s.
        XCTAssertLessThan(difference(frame(near: 2.5), frame(near: 4.4)), held * 0.3)
    }

    func testFramesStopAtTheVideosEndWhenTheLinesRunOver() async throws {
        let short = try await background(seconds: 2.5)
        let content = card(lines: threeLines, background: short)
        let made = await frames(content, plan: LyricCard.clipPlan(content))
        XCTAssertEqual(made.last!.time, 2.5, accuracy: 0.03)
    }

    func testGIFFramesKeepTheirLength() async throws {
        let long = try await background(seconds: 5)
        let content = card(background: long)
        let made = await frames(content, plan: nil)
        XCTAssertEqual(made.last!.time, lyricsLength(content), accuracy: 0.05)
    }

    // MARK: Sound

    func testTheMP4LastsAsLongAsTheBackgroundAndItsSoundLoopsAndFadesOut() async throws {
        let long = try await background(seconds: 5, sound: true)
        XCTAssertTrue(long.hasAudio)
        let files = await LyricCard.export(card(background: long), formats: [.mp4], to: folder.appendingPathComponent("long"))
        let file = try XCTUnwrap(files.first?.1)
        let asset = AVURLAsset(url: file)
        let length = try await asset.load(.duration).seconds
        XCTAssertEqual(length, 5, accuracy: 0.1)
        let tracks = try await asset.loadTracks(withMediaType: .audio)
        let audio = try XCTUnwrap(tracks.first)
        let sound = try await audio.load(.timeRange).duration.seconds
        XCTAssertEqual(sound, 5, accuracy: 0.15)
        let pcm = try await MixProbe.decodeMono(file)
        let end = min(pcm.count, Int(length * 48000))
        let middle = MixProbe.rmsDB(pcm, 96000..<144000)
        let lateFade = MixProbe.rmsDB(pcm, (end - 7200)..<(end - 4800))  // 150-100 ms before the end
        let tail = MixProbe.rmsDB(pcm, (end - 1440)..<end)               // the last 30 ms
        XCTAssertGreaterThan(middle, -30)
        XCTAssertLessThan(lateFade, middle - 3)
        XCTAssertLessThan(tail, middle - 20)
    }

    func testAnExtraSoundLoopsToTheLengthAndFadesAtTheEnd() async throws {
        var content = card()
        content.extraSounds = [LyricCard.SoundTrack(url: try MixProbe.tone(folder.appendingPathComponent("short.wav"), frequency: 500,
                                                                          amplitude: 0.3, seconds: 0.4))]
        let files = await LyricCard.export(content, formats: [.mp4], to: folder.appendingPathComponent("extra"))
        let file = try XCTUnwrap(files.first?.1)
        let length = try await AVURLAsset(url: file).load(.duration).seconds
        XCTAssertEqual(length, lyricsLength(content), accuracy: 0.05)
        let pcm = try await MixProbe.decodeMono(file)
        let end = min(pcm.count, Int(length * 48000))
        // Sound all the way through the repeats (no gaps), then a fade.
        let levels = stride(from: 4800, to: end - 19200, by: 4800).map { MixProbe.rmsDB(pcm, $0..<($0 + 4800)) }
        XCTAssertGreaterThan(levels.count, 10)
        XCTAssertLessThan((levels.max() ?? 0) - (levels.min() ?? 0), 2, "\(levels)")
        XCTAssertLessThan(MixProbe.rmsDB(pcm, (end - 1440)..<end), (levels.min() ?? 0) - 20)
    }

    private func twoPartTone() throws -> URL {
        // 300 Hz for the first second, 1200 Hz for the next two.
        let url = folder.appendingPathComponent("twopart.wav")
        let format = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 1)!
        var file: AVAudioFile? = try AVAudioFile(forWriting: url, settings: format.settings)
        let frames = AVAudioFrameCount(48000 * 3)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames)!
        buffer.frameLength = frames
        for i in 0..<Int(frames) {
            let f = i < 48000 ? 300.0 : 1200.0
            buffer.floatChannelData![0][i] = Float(sin(Double(i) * 2 * .pi * f / 48000) * 0.3)
        }
        try file?.write(from: buffer)
        file = nil
        return url
    }

    func testTheSongsWindowsAreLaidEndToEnd() async throws {
        let clip = SongAudioClip(url: try twoPartTone(), songStart: 0, songEnd: 3)
        let layer = await LyricCard.songLayer(clip, windows: [(0.2, 0.5), (1.5, 0.5)])
        let rendered = try await XCTUnwrap(layer).render(1.0)
        let samples = try XCTUnwrap(rendered).samples
        XCTAssertEqual(samples.count, 96000)
        let mono = stride(from: 0, to: samples.count, by: 2).map { samples[$0] }
        XCTAssertGreaterThan(MixProbe.amplitude(of: 300, in: mono, range: 2400..<21600), 0.25)
        XCTAssertLessThan(MixProbe.amplitude(of: 1200, in: mono, range: 2400..<21600), 0.02)
        XCTAssertGreaterThan(MixProbe.amplitude(of: 1200, in: mono, range: 26400..<45600), 0.25)
        XCTAssertLessThan(MixProbe.amplitude(of: 300, in: mono, range: 26400..<45600), 0.02)
        XCTAssertNil(try XCTUnwrap(rendered).fadeOut, "it ends with the video, which fades everything")
    }

    func testTheSongFadesWhereItIsCutAndWhereItStops() async throws {
        let clip = SongAudioClip(url: try twoPartTone(), songStart: 0, songEnd: 3)
        let cutLayer = await LyricCard.songLayer(clip, windows: [(0, 2.0)])
        let cut = try await XCTUnwrap(cutLayer).render(1.0)
        XCTAssertEqual(try XCTUnwrap(cut?.fadeOut).end, 48000)
        XCTAssertEqual(try XCTUnwrap(cut?.fadeOut).seconds, 0.4)

        let earlyLayer = await LyricCard.songLayer(clip, windows: [(0, 0.5)])
        let earlyRendered = try await XCTUnwrap(earlyLayer).render(1.0)
        let early = try XCTUnwrap(earlyRendered)
        XCTAssertEqual(try XCTUnwrap(early.fadeOut).end, 24000)

        // In the mix: full level before the fade, silence after it.
        let mixed = await LyricCard.mixdown([try XCTUnwrap(earlyLayer)], seconds: 1.0, duck: false)
        let samples = try XCTUnwrap(try XCTUnwrap(mixed))
        let mono = stride(from: 0, to: samples.count, by: 2).map { samples[$0] }
        let full = MixProbe.rmsDB(mono, 2400..<4800)
        XCTAssertLessThan(MixProbe.rmsDB(mono, 23000..<24000), full - 20)
        XCTAssertLessThan(MixProbe.rmsDB(mono, 24000..<48000), -100)
    }

    func testPartsOfTheSongTheRecordingMissesAreSilent() async throws {
        let clip = SongAudioClip(url: try twoPartTone(), songStart: 1.0, songEnd: 4.0)
        let layer = await LyricCard.songLayer(clip, windows: [(0.5, 1.0)])
        let output = try await XCTUnwrap(layer).render(1.0)
        let rendered = try XCTUnwrap(output)
        let mono = stride(from: 0, to: rendered.samples.count, by: 2).map { rendered.samples[$0] }
        XCTAssertLessThan(MixProbe.rmsDB(mono, 0..<23000), -100)
        // From 0.5 s on: the recording's start (300 Hz), lined up with the song.
        XCTAssertGreaterThan(MixProbe.amplitude(of: 300, in: mono, range: 26400..<45600), 0.25)
    }

    // MARK: Notes

    func testTheExportSaysWhenLinesDontFitOrTheSongIsMissing() async throws {
        let short = try await background(seconds: 2.5)
        var content = card(lines: threeLines, background: short)
        let range = try XCTUnwrap(LyricCard.songRange(content))
        // A recording that starts late, so the first line isn't in it.
        content.songAudio = SongAudioClip(url: try twoPartTone(), songStart: range.lowerBound + 1, songEnd: range.lowerBound + 4)
        var notes: [String] = []
        let files = await LyricCard.export(content, formats: [.mp4], to: folder.appendingPathComponent("notes"), notes: { notes.append($0) })
        let file = try XCTUnwrap(files.first?.1)
        XCTAssertTrue(notes.contains("2 lines won’t fit; pick fewer lines or Match lyrics"), "\(notes)")
        XCTAssertTrue(notes.contains { $0.hasPrefix("Parts of these lines aren’t in the song’s recording") }, "\(notes)")
        // The song is still there, silent where it wasn't recorded.
        let audio = try await AVURLAsset(url: file).loadTracks(withMediaType: .audio)
        XCTAssertEqual(audio.count, 1)
        let length = try await AVURLAsset(url: file).load(.duration).seconds
        XCTAssertEqual(length, 2.5, accuracy: 0.1)
    }
}
