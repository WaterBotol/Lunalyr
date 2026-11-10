import AVFoundation
import XCTest
@testable import VersoCustom

/// Shared helpers: synthetic tones written to temporary files, and analysis of
/// written files (decoded, never played).
enum MixProbe {
    static func tone(_ url: URL, frequency: Double, amplitude: Double, seconds: Double, sampleRate: Double = 44100,
                     channels: AVAudioChannelCount = 1, aac: Bool = false) throws -> URL {
        let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: channels)!
        var settings = format.settings
        if aac { settings = [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: sampleRate, AVNumberOfChannelsKey: channels] }
        var file: AVAudioFile? = try AVAudioFile(forWriting: url, settings: settings)
        let frames = AVAudioFrameCount(sampleRate * seconds)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames)!
        buffer.frameLength = frames
        for c in 0..<Int(channels) {
            for i in 0..<Int(frames) {
                buffer.floatChannelData![c][i] = Float(sin(Double(i) * 2 * .pi * frequency / sampleRate) * amplitude)
            }
        }
        try file?.write(from: buffer)
        file = nil
        return url
    }

    /// The first audio track of `url`, decoded to float at 48 kHz with its two channels averaged.
    static func decodeMono(_ url: URL) async throws -> [Float] {
        let asset = AVURLAsset(url: url)
        let tracks = try await asset.loadTracks(withMediaType: .audio)
        let track = try XCTUnwrap(tracks.first)
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false, AVLinearPCMIsBigEndianKey: false,
            AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2,
        ])
        reader.add(output)
        reader.startReading()
        var samples: [Float] = []
        while let buffer = output.copyNextSampleBuffer() {
            guard let block = CMSampleBufferGetDataBuffer(buffer) else { continue }
            let length = CMBlockBufferGetDataLength(block)
            var chunk = [Float](repeating: 0, count: length / 4)
            chunk.withUnsafeMutableBytes { _ = CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: $0.baseAddress!) }
            samples += chunk
        }
        return stride(from: 0, to: samples.count - 1, by: 2).map { (samples[$0] + samples[$0 + 1]) / 2 }
    }

    /// Amplitude of `frequency` in `samples[range]` (Goertzel).
    static func amplitude(of frequency: Double, in samples: [Float], range: Range<Int>, sampleRate: Double = 48000) -> Double {
        let w = 2 * Double.pi * frequency / sampleRate
        let coefficient = 2 * cos(w)
        var s1 = 0.0, s2 = 0.0
        for i in range {
            let s = Double(samples[i]) + coefficient * s1 - s2
            s2 = s1; s1 = s
        }
        let power = s1 * s1 + s2 * s2 - coefficient * s1 * s2
        return 2 * sqrt(max(0, power)) / Double(range.count)
    }

    static func db(_ x: Double) -> Double { 20 * log10(max(x, 1e-12)) }

    /// RMS of `samples[range]` in dBFS.
    static func rmsDB(_ samples: [Float], _ range: Range<Int>) -> Double {
        guard !range.isEmpty else { return -240 }
        let sum = range.reduce(0.0) { $0 + Double(samples[$1]) * Double(samples[$1]) }
        return 10 * log10(max(sum / Double(range.count), 1e-24))
    }

    /// Interleaved stereo sine at 48 kHz.
    static func stereoSine(frequency: Double, amplitude: Double, seconds: Double, rate: Double = 48000) -> [Float] {
        let frames = Int(seconds * rate)
        var out = [Float](repeating: 0, count: frames * 2)
        for i in 0..<frames {
            let x = Float(sin(Double(i) * 2 * .pi * frequency / rate) * amplitude)
            out[2 * i] = x; out[2 * i + 1] = x
        }
        return out
    }

    /// A one-line card lasting about 1.9 s (line 10.0 to 11.6 plus its rest).
    @MainActor static func content(lines: [(start: Double, end: Double)] = [(10, 11.6)]) -> LyricCard.Content {
        let pages = lines.enumerated().map { index, span -> LyricCard.Page in
            let texts = ["Hold", "on", "tight", "tonight"]
            let step = (span.end - span.start) / Double(texts.count)
            let words = texts.enumerated().map { i, w in
                LyricsFetcher.Word(text: w, start: span.start + Double(i) * step, end: span.start + Double(i) * step + step * 0.9)
            }
            let line = LyricsFetcher.Line(start: span.start, end: span.end, text: texts.joined(separator: " "), words: words,
                                          isInstrumental: false, wordTimed: true)
            let animation = LyricCard.Animation(line: line, composition: .editorial, mood: .neutral, seed: index)
            return LyricCard.Page(words: texts, animation: animation)
        }
        let art = NSImage(size: NSSize(width: 64, height: 64), flipped: false) { _ in true }
        var content = LyricCard.Content(pages: pages, title: "A Song", artist: "An Artist", artwork: art,
                                        palette: LyricCard.palette(for: art), reduceMotion: true)
        content.aspect = .square
        return content
    }

    /// A small H.264 video of `seconds`, coloured by `colour(time)`, 10 frames a second.
    static func video(_ url: URL, seconds: Double, colour: (Double) -> (CGFloat, CGFloat, CGFloat) = { _ in (0.2, 0.4, 0.6) }) async throws -> URL {
        let size = 64
        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: size, AVVideoHeightKey: size,
        ])
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: size, kCVPixelBufferHeightKey as String: size,
        ])
        writer.add(input)
        XCTAssertTrue(writer.startWriting())
        writer.startSession(atSourceTime: .zero)
        let fps = 10.0
        for index in 0..<Int((seconds * fps).rounded()) {
            let time = Double(index) / fps
            var buffer: CVPixelBuffer?
            CVPixelBufferCreate(nil, size, size, kCVPixelFormatType_32BGRA, nil, &buffer)
            let pixels = try XCTUnwrap(buffer)
            CVPixelBufferLockBaseAddress(pixels, [])
            let context = CGContext(data: CVPixelBufferGetBaseAddress(pixels), width: size, height: size, bitsPerComponent: 8,
                                    bytesPerRow: CVPixelBufferGetBytesPerRow(pixels), space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                    bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)!
            let (r, g, b) = colour(time)
            context.setFillColor(red: r, green: g, blue: b, alpha: 1)
            context.fill(CGRect(x: 0, y: 0, width: size, height: size))
            CVPixelBufferUnlockBaseAddress(pixels, [])
            while !input.isReadyForMoreMediaData { try await Task.sleep(nanoseconds: 2_000_000) }
            XCTAssertTrue(adaptor.append(pixels, withPresentationTime: CMTime(seconds: time, preferredTimescale: 600)))
        }
        input.markAsFinished()
        writer.endSession(atSourceTime: CMTime(seconds: seconds, preferredTimescale: 600))
        await writer.finishWriting()
        XCTAssertEqual(writer.status, .completed)
        return url
    }
}


@MainActor
final class LyricCardMixTests: XCTestCase {
    private let folder = FileManager.default.temporaryDirectory.appendingPathComponent("card-mix-\(UUID().uuidString)")
    override func setUp() { try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
    override func tearDown() { try? FileManager.default.removeItem(at: folder) }

    // MARK: Loudness

    func testLoudnessOfSinesAtKnownLevels() throws {
        // BS.1770: a 997 Hz sine at 0 dBFS in both channels reads 0 LUFS.
        for amplitude in [1.0, 0.5, 0.1, 0.01] {
            let measured = try XCTUnwrap(LyricCardMix.loudness(MixProbe.stereoSine(frequency: 997, amplitude: amplitude, seconds: 2)))
            XCTAssertEqual(measured, 20 * log10(amplitude), accuracy: 0.15, "amplitude \(amplitude)")
        }
        // The K-weighting's shelf lifts high tones by about 4 dB.
        let high = try XCTUnwrap(LyricCardMix.loudness(MixProbe.stereoSine(frequency: 8000, amplitude: 0.1, seconds: 2)))
        XCTAssertEqual(high, -20 + 3.5, accuracy: 0.6)
        XCTAssertNil(LyricCardMix.loudness([Float](repeating: 0, count: 96000)))
    }

    func testGatingIgnoresSilenceAroundTheSound() throws {
        var samples = [Float](repeating: 0, count: 96000 * 2)
        samples += MixProbe.stereoSine(frequency: 997, amplitude: 0.1, seconds: 2)
        samples += [Float](repeating: 0, count: 96000 * 2)
        let measured = try XCTUnwrap(LyricCardMix.loudness(samples))
        // Ungated, the average over all 6 s would be -24.8 LUFS; the gates keep
        // only the blocks with sound (the ones straddling its edges pull a little).
        XCTAssertEqual(measured, -20, accuracy: 1)
    }

    func testMatchingBringsEveryLevelToTheReference() throws {
        for amplitude in [0.9, 0.2, 0.02] {
            var samples = MixProbe.stereoSine(frequency: 500, amplitude: amplitude, seconds: 1.5)
            let gain = LyricCardMix.linear(LyricCardMix.matchingGain(for: samples))
            samples = samples.map { $0 * gain }
            XCTAssertEqual(try XCTUnwrap(LyricCardMix.loudness(samples)), LyricCardMix.referenceLoudness, accuracy: 0.05)
        }
        XCTAssertEqual(LyricCardMix.matchingGain(for: [Float](repeating: 0, count: 9600)), 0)
    }

    // MARK: Sliders

    func testSliderPositionsMapToDecibels() {
        XCTAssertEqual(LyricCardMix.decibels(forLevel: 0), -.infinity)
        XCTAssertEqual(LyricCardMix.decibels(forLevel: 1), 0, accuracy: 1e-9)
        XCTAssertEqual(LyricCardMix.decibels(forLevel: 0.5), -12.04, accuracy: 0.01)
        XCTAssertEqual(LyricCardMix.decibels(forLevel: 0.25), -24.08, accuracy: 0.01)
        XCTAssertEqual(LyricCardMix.decibels(forLevel: 1.25), 6, accuracy: 1e-9)
        XCTAssertEqual(LyricCardMix.decibels(forLevel: 1.5), 12, accuracy: 1e-9)
        XCTAssertEqual(LyricCardMix.decibels(forLevel: 3), 12, accuracy: 1e-9)
        var last = -Double.infinity
        for step in 1...150 {
            let db = LyricCardMix.decibels(forLevel: Double(step) / 100)
            XCTAssertGreaterThan(db, last)
            last = db
        }
        XCTAssertEqual(LyricCardMix.label(forLevel: 0), "Off")
        XCTAssertEqual(LyricCardMix.label(forLevel: 1), "0 dB")
        XCTAssertEqual(LyricCardMix.label(forLevel: 0.5), "\u{2212}12 dB")
        XCTAssertEqual(LyricCardMix.label(forLevel: 1.5), "+12 dB")
    }

    // MARK: Limiter, fades, loops, ducking

    func testTheLimiterNeverExceedsTheCeiling() {
        var generator = SystemRandomNumberGenerator()
        var samples = (0..<(48000 * 2 * 2)).map { _ in Float.random(in: -1...1, using: &generator) }
        // Hot bursts and single-sample spikes.
        for i in stride(from: 0, to: samples.count, by: 2) where (i / 4800) % 3 == 0 { samples[i] *= 4; samples[i + 1] *= 3 }
        for i in stride(from: 101, to: samples.count, by: 9973) { samples[i] = 9 }
        samples += MixProbe.stereoSine(frequency: 100, amplitude: 6, seconds: 0.5)
        LyricCardMix.limit(&samples)
        let ceiling = LyricCardMix.ceiling
        XCTAssertLessThanOrEqual(samples.map(abs).max() ?? 0, ceiling)
        XCTAssertEqual(Double(ceiling), pow(10, -1.0 / 20), accuracy: 1e-6)
    }

    func testTheLimiterLeavesQuietSoundAlone() {
        let quiet = MixProbe.stereoSine(frequency: 440, amplitude: 0.5, seconds: 0.5)
        var limited = quiet
        LyricCardMix.limit(&limited)
        XCTAssertEqual(limited, quiet)
    }

    func testFadeOutReachesSilence() {
        var samples = [Float](repeating: 1, count: 48000 * 2)
        LyricCardMix.fadeOut(&samples, endingAt: 48000, seconds: 0.3)
        XCTAssertEqual(samples[0], 1)
        XCTAssertEqual(samples[(48000 - 14400) * 2], 1, accuracy: 0.01)
        XCTAssertEqual(samples[(48000 - 7200) * 2], 0.5, accuracy: 0.01)
        XCTAssertEqual(samples[47999 * 2], 0)
    }

    func testAFadeShorterThanAskedStartsFromFullLevel() {
        // Sound that stops 100 frames in: no jump from full level to a third of it.
        var samples = [Float](repeating: 1, count: 1000 * 2)
        LyricCardMix.fadeOut(&samples, endingAt: 100, seconds: 0.3)
        XCTAssertEqual(samples[0], 1, accuracy: 0.02)
        XCTAssertEqual(samples[50 * 2], 0.5, accuracy: 0.02)
        XCTAssertEqual(samples[99 * 2], 0)
        XCTAssertEqual(samples[500 * 2], 0)
        var none = [Float](repeating: 1, count: 20)
        LyricCardMix.fadeOut(&none, endingAt: 0, seconds: 0.3)
        XCTAssertEqual(none, [Float](repeating: 0, count: 20))
    }

    func testLoopsRepeatAndCrossfadeWithoutADip() {
        let source = [Float](repeating: 1, count: 100 * 2)
        let looped = LyricCardMix.loop(source, to: 250, crossfade: 10)
        // Seams at 90 and 180; equal-gain crossfades keep a constant level.
        XCTAssertEqual(looped.count, 500)
        for value in looped { XCTAssertEqual(value, 1, accuracy: 1e-5) }
        var ramp = [Float](repeating: 0, count: 200)
        for i in 0..<100 { ramp[2 * i] = Float(i); ramp[2 * i + 1] = Float(i) }
        let plain = LyricCardMix.loop(ramp, to: 250)
        XCTAssertEqual(plain[2 * 100], 0)
        XCTAssertEqual(plain[2 * 249], 49)
        let faded = LyricCardMix.loop(ramp, to: 250, crossfade: 10)
        XCTAssertEqual(faded[2 * 95], 95 * 0.5 + 5 * 0.5, accuracy: 1e-4) // halfway through the first seam
        XCTAssertEqual(faded[2 * 100], 10)                                 // the second pass, past the seam
        XCTAssertEqual(LyricCardMix.loop(ramp, to: 40), Array(ramp.prefix(80)))
    }

    func testDuckingFollowsTheBackgroundWithItsAttackAndRelease() {
        var trigger = [Float](repeating: 0, count: 48000 * 3 * 2)
        let burst = MixProbe.stereoSine(frequency: 300, amplitude: 0.3, seconds: 1)
        trigger.replaceSubrange((48000 * 2)..<(48000 * 4), with: burst)
        let gains = LyricCardMix.duckGains(under: trigger, frames: 48000 * 3)
        func db(_ seconds: Double) -> Double { MixProbe.db(Double(gains[Int(seconds * 48000)])) }
        XCTAssertEqual(db(0.9), 0, accuracy: 0.01)
        XCTAssertLessThan(db(1.2), -9)            // most of the way down within ~3 attack constants
        XCTAssertEqual(db(1.9), -12, accuracy: 0.2)
        XCTAssertLessThan(db(2.1), -6)             // release is slower than attack
        XCTAssertGreaterThan(db(2.99), -1)
    }

    func testTheSongIsDuckedOnlyWhileTheBackgroundSounds() async throws {
        let song = MixProbe.stereoSine(frequency: 600, amplitude: 0.2, seconds: 3)
        var background = [Float](repeating: 0, count: 48000 * 3 * 2)
        background.replaceSubrange((48000 * 2)..<(48000 * 4), with: MixProbe.stereoSine(frequency: 2000, amplitude: 0.2, seconds: 1))
        let layers = [LyricCard.SoundLayer(volume: 1, role: .song) { _ in .init(song) },
                      LyricCard.SoundLayer(volume: 1, role: .background) { _ in .init(background) }]
        let ducked = await LyricCard.mixdown(layers, seconds: 3, duck: true)
        let mixed = try XCTUnwrap(try XCTUnwrap(ducked))
        let mono = stride(from: 0, to: mixed.count, by: 2).map { mixed[$0] }
        let before = MixProbe.db(MixProbe.amplitude(of: 600, in: mono, range: 24000..<43200))
        let during = MixProbe.db(MixProbe.amplitude(of: 600, in: mono, range: 72000..<91200))
        XCTAssertEqual(during - before, -12, accuracy: 1)
        let straight = await LyricCard.mixdown(layers, seconds: 3, duck: false)
        let undisturbed = try XCTUnwrap(try XCTUnwrap(straight))
        let plain = stride(from: 0, to: undisturbed.count, by: 2).map { undisturbed[$0] }
        XCTAssertEqual(MixProbe.db(MixProbe.amplitude(of: 600, in: plain, range: 72000..<91200)), before, accuracy: 0.2)
    }

    func testEveryLayerOffLeavesNoSound() async {
        let layers = [LyricCard.SoundLayer(volume: 0) { _ in .init([Float](repeating: 0.5, count: 9600)) }]
        let mixed = await LyricCard.mixdown(layers, seconds: 0.1, duck: false)
        XCTAssertNotNil(mixed)
        XCTAssertNil(mixed!)
    }

    // MARK: The written file

    /// A loud "song" (600 Hz at -6 dBFS) and a quiet "video" sound (900 Hz at
    /// -26 dBFS), mixed at a few slider settings. After matching, the song sits
    /// exactly as far above or below the video as the sliders ask.
    func testExportedLayersSitAtTheRequestedOffsets() async throws {
        let video = folder.appendingPathComponent("v.mp4")
        let made = await LyricCard.video(MixProbe.content(), to: video)
        XCTAssertTrue(made)
        let loud = try MixProbe.tone(folder.appendingPathComponent("loud.wav"), frequency: 600, amplitude: 0.5, seconds: 3)
        let quiet = try MixProbe.tone(folder.appendingPathComponent("quiet.m4a"), frequency: 900, amplitude: 0.05, seconds: 3, aac: true)
        // The K-weighting treats 600 and 900 Hz slightly differently; account for it exactly.
        let songSource = try XCTUnwrap(LyricCardMix.loudness(MixProbe.stereoSine(frequency: 600, amplitude: 0.5, seconds: 2)))
        let videoSource = try XCTUnwrap(LyricCardMix.loudness(MixProbe.stereoSine(frequency: 900, amplitude: 0.05, seconds: 2)))
        let weighting = (songSource - MixProbe.db(0.5)) - (videoSource - MixProbe.db(0.05))
        for (songLevel, videoLevel) in [(1.0, 1.0), (0.5, 1.0), (0.5, 1.5), (1.5, 1.0), (0.25, 1.0), (1.0, 0.5)] {
            let copy = folder.appendingPathComponent("v-\(songLevel)-\(videoLevel).mp4")
            try FileManager.default.copyItem(at: video, to: copy)
            let clip = SongAudioClip(url: loud, songStart: 0, songEnd: 3)
            let song = await LyricCard.songLayer(clip, windows: [(0, 2.5)], volume: Float(songLevel))
            let other = await LyricCard.soundLayer(from: quiet, volume: Float(videoLevel), role: .background)
            let ok = await LyricCard.mux(soundOnto: copy, layers: [try XCTUnwrap(song), try XCTUnwrap(other)])
            XCTAssertTrue(ok)
            let pcm = try await MixProbe.decodeMono(copy)
            let range = 4800..<(pcm.count - 24000)
            let songDB = MixProbe.db(MixProbe.amplitude(of: 600, in: pcm, range: range))
            let videoDB = MixProbe.db(MixProbe.amplitude(of: 900, in: pcm, range: range))
            let asked = LyricCardMix.decibels(forLevel: songLevel) - LyricCardMix.decibels(forLevel: videoLevel)
            print(String(format: "MIXLEVEL song %.0f%% video %.0f%%: song %.1f dB, video %.1f dB, song minus video %.1f dB (asked %.1f)",
                         songLevel * 100, videoLevel * 100, songDB, videoDB, songDB - videoDB, asked))
            XCTAssertEqual(songDB - videoDB, asked - weighting, accuracy: 1, "song \(songLevel) video \(videoLevel)")
            XCTAssertLessThan(abs(weighting), 1)
            XCTAssertLessThanOrEqual(pcm.map(abs).max() ?? 0, 0.95)
        }
    }

    /// A background video whose sound stops a second before its picture: the
    /// sound repeats with the picture, with silence where the picture runs on.
    func testABackgroundsShortSoundTrackRepeatsWithItsPicture() async throws {
        let picture = try await MixProbe.video(folder.appendingPathComponent("picture.mp4"), seconds: 2.5)
        let tone = try MixProbe.tone(folder.appendingPathComponent("tone.wav"), frequency: 500, amplitude: 0.4, seconds: 1.5)
        let composition = AVMutableComposition()
        let pictureAsset = AVURLAsset(url: picture), toneAsset = AVURLAsset(url: tone)
        let videoTracks = try await pictureAsset.loadTracks(withMediaType: .video)
        let audioTracks = try await toneAsset.loadTracks(withMediaType: .audio)
        let videoTrack = try XCTUnwrap(videoTracks.first), audioTrack = try XCTUnwrap(audioTracks.first)
        let pictureLength = try await pictureAsset.load(.duration), toneLength = try await toneAsset.load(.duration)
        try composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)?
            .insertTimeRange(CMTimeRange(start: .zero, duration: pictureLength), of: videoTrack, at: .zero)
        try composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)?
            .insertTimeRange(CMTimeRange(start: .zero, duration: toneLength), of: audioTrack, at: .zero)
        let background = folder.appendingPathComponent("background.mov")
        let session = try XCTUnwrap(AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetPassthrough))
        session.outputURL = background
        session.outputFileType = .mov
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in session.exportAsynchronously { done.resume() } }
        XCTAssertEqual(session.status, .completed, String(describing: session.error))
        let length = try await AVURLAsset(url: background).load(.duration).seconds
        XCTAssertEqual(length, 2.5, accuracy: 0.1)

        let maybeLayer = await LyricCard.soundLayer(from: background, role: .background)
        let layer = try XCTUnwrap(maybeLayer)
        let maybeRendered = await layer.render(5)
        let rendered = try XCTUnwrap(maybeRendered)
        let left = stride(from: 0, to: rendered.samples.count, by: 2).map { rendered.samples[$0] }
        func rms(_ from: Double, _ to: Double) -> Double { MixProbe.rmsDB(left, Int(from * 48000)..<Int(to * 48000)) }
        XCTAssertGreaterThan(rms(0.2, 1.3), -20, "the first pass sounds")
        XCTAssertLessThan(rms(1.7, 2.3), -60, "silent while the picture runs on past its sound")
        let second = length + 0.2
        XCTAssertGreaterThan(rms(second, second + 1), -20, "the second pass starts with the picture's")
    }

    // MARK: Temporary files

    func testOnlyLunalyrsOwnRecordingsAreDeleted() throws {
        let manager = FileManager.default
        let recording = SongAudioSource.recordingURL(in: folder)
        let library = folder.appendingPathComponent("My Song.m4a")
        let lookalike = folder.appendingPathComponent("Music").appendingPathComponent("Lunalyr song x.caf")
        try manager.createDirectory(at: lookalike.deletingLastPathComponent(), withIntermediateDirectories: true)
        for url in [recording, library, lookalike] { try Data([1, 2, 3]).write(to: url) }
        XCTAssertTrue(SongAudioSource.isRecording(recording, in: folder))
        XCTAssertFalse(SongAudioSource.isRecording(library, in: folder))
        XCTAssertFalse(SongAudioSource.isRecording(lookalike, in: folder), "same name, but not in the temporary folder")
        SongAudioSource.discard(SongAudioClip(url: library, songStart: 0, songEnd: .infinity), in: folder)
        SongAudioSource.discard(SongAudioClip(url: recording, songStart: 3, songEnd: 9), in: folder)
        XCTAssertTrue(manager.fileExists(atPath: library.path), "the song's own file is never deleted")
        XCTAssertFalse(manager.fileExists(atPath: recording.path))
        let left = SongAudioSource.recordingURL(in: folder)
        try Data([1]).write(to: left)
        SongAudioSource.discardLeftoverRecordings(in: folder)
        XCTAssertFalse(manager.fileExists(atPath: left.path))
        XCTAssertTrue(manager.fileExists(atPath: library.path))
        XCTAssertTrue(manager.fileExists(atPath: lookalike.path))
    }

    func testACancelledExportLeavesNothingBehind() throws {
        let manager = FileManager.default
        let own = folder.appendingPathComponent("own")
        let chosen = folder.appendingPathComponent("chosen")
        for directory in [own, chosen] { try manager.createDirectory(at: directory, withIntermediateDirectories: true) }
        let earlier = chosen.appendingPathComponent("Lunalyr Lyric Card.png")
        let made = chosen.appendingPathComponent("Lunalyr Lyric Card 2.mp4")
        for url in [earlier, made, own.appendingPathComponent("Lunalyr Lyric Card.mp4")] { try Data([1]).write(to: url) }
        LyricCard.discardExport([(.mp4, made)], in: chosen, ownsDirectory: false)
        XCTAssertFalse(manager.fileExists(atPath: made.path))
        XCTAssertTrue(manager.fileExists(atPath: earlier.path), "an earlier card in the user's folder stays")
        LyricCard.discardExport([], in: own, ownsDirectory: true)
        XCTAssertFalse(manager.fileExists(atPath: own.path))
    }

    func testLoudLayersAreLimitedNotClipped() async throws {
        let video = folder.appendingPathComponent("hot.mp4")
        let made = await LyricCard.video(MixProbe.content(), to: video)
        XCTAssertTrue(made)
        var layers: [LyricCard.SoundLayer] = []
        for frequency in [200.0, 330, 470, 610] {
            let url = try MixProbe.tone(folder.appendingPathComponent("t\(Int(frequency)).wav"), frequency: frequency, amplitude: 0.9, seconds: 2)
            let layer = await LyricCard.soundLayer(from: url, volume: 1.5)
            layers.append(try XCTUnwrap(layer))
        }
        let ok = await LyricCard.mux(soundOnto: video, layers: layers)
        XCTAssertTrue(ok)
        let pcm = try await MixProbe.decodeMono(video)
        // -1 dBFS before encoding; AAC may overshoot a little.
        XCTAssertLessThanOrEqual(pcm.map(abs).max() ?? 0, 0.97)
    }
}
