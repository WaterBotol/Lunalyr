import AppKit
import AVFoundation
import CoreVideo
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

// MARK: - Lyric cards as MP4, GIF and PNG
//
//     let files = try await LyricCard.export(content, formats: [.mp4, .png], to: folder) { progress in … }
//     LyricCard.publish(files, to: .general)       // pastes as attachments
//
// MP4 streams its frames to the encoder as they are drawn, 30 a second, so a
// whole song costs no more memory than one line. PNG is one crisp still of
// all the selected lines, drawn at twice the card's size.

extension LyricCard.Content {
    /// The background video's own sound, when it has some and it's wanted.
    var backgroundSoundSource: URL? {
        guard keepBackgroundSound, let background, background.kind == .video, background.hasAudio else { return nil }
        return background.url
    }

    /// Where the MP4's sound comes from, if anywhere.
    var soundSource: URL? { soundFile ?? backgroundSoundSource }
}

extension LyricCard {
    static let videoFramesPerSecond = 30.0
    /// MP4s are 720 pixels on their short side (a third smaller than the card): sharp on a phone, small to send.
    static let videoScale: CGFloat = 2.0 / 3.0

    /// One crisp PNG of the selected lines together.
    static func stillPNG(_ content: Content, scale: CGFloat = 2) -> Data? {
        var still = content
        still.pages = [content.stillPage]
        let renderer = ImageRenderer(content: LyricCardView(content: still, ink: ink(for: still)))
        renderer.scale = scale
        guard let image = renderer.cgImage else { return nil }
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, image, nil)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }

    /// An H.264 video of the card's animation at `videoScale` of its size,
    /// lasting as `plan` says (by default `clipPlan(content)`).
    static func video(_ content: Content, to url: URL, plan: ClipPlan? = nil, progress: ((Double) -> Void)? = nil) async -> Bool {
        let plan = plan ?? clipPlan(content)
        let pixels = CGSize(width: (content.size.width * videoScale).rounded(), height: (content.size.height * videoScale).rounded())
        guard let writer = try? AVAssetWriter(outputURL: url, fileType: .mp4) else { return false }
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: Int(pixels.width),
            AVVideoHeightKey: Int(pixels.height),
            AVVideoColorPropertiesKey: [
                AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2,
            ],
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: 4_000_000,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
                AVVideoMaxKeyFrameIntervalKey: 60,
            ],
        ])
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: Int(pixels.width),
            kCVPixelBufferHeightKey as String: Int(pixels.height),
        ])
        guard writer.canAdd(input) else { print("LYRIC-MP4 cannot add input"); return false }
        writer.add(input)
        guard writer.startWriting() else { print("LYRIC-MP4 start failed: \(String(describing: writer.error))"); return false }
        writer.startSession(atSourceTime: .zero)

        var time = 0.0
        let ok = await renderFrames(content, scale: videoScale, minimumCadence: 1 / videoFramesPerSecond, budget: nil,
                                    plan: plan, progress: progress) { image, delay in
            guard let buffer = pixelBuffer(from: image, size: pixels, pool: adaptor.pixelBufferPool) else {
                print("LYRIC-MP4 no pixel buffer")
                return false
            }
            while !input.isReadyForMoreMediaData {
                if Task.isCancelled { return false }
                try? await Task.sleep(nanoseconds: 4_000_000)
            }
            guard adaptor.append(buffer, withPresentationTime: CMTime(seconds: time, preferredTimescale: 600)) else {
                print("LYRIC-MP4 append failed at \(time): \(String(describing: writer.error))")
                return false
            }
            time += delay
            return true
        }
        guard ok, !Task.isCancelled else {
            print("LYRIC-MP4 frames ok=\(ok) cancelled=\(Task.isCancelled) status=\(writer.status.rawValue) error=\(String(describing: writer.error))")
            writer.cancelWriting()
            try? FileManager.default.removeItem(at: url)
            return false
        }
        input.markAsFinished()
        writer.endSession(atSourceTime: CMTime(seconds: time, preferredTimescale: 600))
        await writer.finishWriting()
        if writer.status != .completed { print("LYRIC-MP4 finish: \(writer.status.rawValue) \(String(describing: writer.error))") }
        return writer.status == .completed
    }

    /// One strand of sound under the video: its slider level, what it is, and
    /// how to render it for a given length.
    struct SoundLayer {
        enum Role { case song, background, file }
        /// The slider position, 0 to 1.5 (see `LyricCardMix.decibels(forLevel:)`).
        var volume: Float
        var role: Role = .file
        /// The strand's sound for that many seconds; nil when it can't be read.
        var render: (Double) async -> Rendered?
    }

    /// A strand's samples, interleaved stereo at `LyricCardMix.sampleRate`,
    /// and where it fades out, if it stops before the end. The fade is applied
    /// after the strand's loudness is measured, so it doesn't skew the matching.
    struct Rendered {
        var samples: [Float]
        var fadeOut: (end: Int, seconds: Double)?
        init(_ samples: [Float], fadeOut: (end: Int, seconds: Double)? = nil) { self.samples = samples; self.fadeOut = fadeOut }
    }

    /// `soundURL` looped (or cut) to the video's length. A `crossfade` (in
    /// seconds) overlaps each repeat with the end of the one before, as a
    /// background video's picture does.
    static func soundLayer(from soundURL: URL, volume: Float = 1, role: SoundLayer.Role = .file,
                           crossfade: Double = 0) async -> SoundLayer? {
        let sound = AVURLAsset(url: soundURL)
        guard (try? await sound.loadTracks(withMediaType: .audio).first) != nil,
              let soundLength = try? await sound.load(.duration), soundLength.seconds > 0.05 else { return nil }
        return SoundLayer(volume: volume, role: role) { seconds in
            let frames = Int((seconds * LyricCardMix.sampleRate).rounded())
            guard let source = await LyricCardMix.decode(soundURL, seconds: min(soundLength.seconds, seconds + 0.05)),
                  !source.isEmpty else { return nil }
            return Rendered(LyricCardMix.loop(source, to: frames, crossfade: Int(crossfade * LyricCardMix.sampleRate)))
        }
    }

    /// Lays `soundURL`'s audio under the finished video at `videoURL`, looping
    /// it (or cutting it) to the video's length, and replaces the file. False,
    /// with the file untouched, when there is no sound.
    @discardableResult
    static func addSound(from soundURL: URL, to videoURL: URL, volume: Float = 1) async -> Bool {
        guard let layer = await soundLayer(from: soundURL, volume: volume) else { return false }
        return await mux(soundOnto: videoURL, layers: [layer])
    }

    /// Renders every layer for the video's length, brings each to the same
    /// loudness, applies its slider level (real gain, above 100% too), ducks
    /// the song under the background's sound if asked, sums them, fades the
    /// last `LyricCardMix.endFade` seconds and limits peaks to -1 dBFS. The result is
    /// written as AAC and laid under the video, which is copied as it is, and
    /// `videoURL` is replaced. False, with the file untouched, if it can't.
    static func mux(soundOnto videoURL: URL, layers: [SoundLayer], duck: Bool = false) async -> Bool {
        guard !layers.isEmpty else { return false }
        let video = AVURLAsset(url: videoURL)
        guard let videoTrack = try? await video.loadTracks(withMediaType: .video).first,
              let length = try? await video.load(.duration), length.seconds > 0 else { return false }
        guard let mix = await mixdown(layers, seconds: length.seconds, duck: duck) else { return false }
        guard let mix else { return true } // every layer turned off: leave the video silent
        let folder = videoURL.deletingLastPathComponent()
        let sound = folder.appendingPathComponent("mix-\(UUID().uuidString).m4a")
        let output = folder.appendingPathComponent("sound-\(UUID().uuidString).mp4")
        defer {
            try? FileManager.default.removeItem(at: sound)
            try? FileManager.default.removeItem(at: output)
        }
        do { try LyricCardMix.writeAAC(mix, to: sound) } catch { return false }
        let audio = AVURLAsset(url: sound)
        let composition = AVMutableComposition()
        guard let audioTrack = try? await audio.loadTracks(withMediaType: .audio).first,
              let audioLength = try? await audio.load(.duration),
              let outVideo = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid),
              let outSound = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)
        else { return false }
        do {
            try outVideo.insertTimeRange(CMTimeRange(start: .zero, duration: length), of: videoTrack, at: .zero)
            try outSound.insertTimeRange(CMTimeRange(start: .zero, duration: CMTimeMinimum(length, audioLength)), of: audioTrack, at: .zero)
        } catch { return false }
        guard let session = AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetPassthrough) else { return false }
        session.outputURL = output
        session.outputFileType = .mp4
        await withCheckedContinuation { (finished: CheckedContinuation<Void, Never>) in
            session.exportAsynchronously { finished.resume() }
        }
        guard session.status == .completed else { return false }
        return (try? FileManager.default.replaceItemAt(videoURL, withItemAt: output)) != nil
    }

    /// The finished mix for `seconds`; .some(nil) when every layer is off, nil when one can't be read.
    static func mixdown(_ layers: [SoundLayer], seconds: Double, duck: Bool) async -> [Float]?? {
        let channels = LyricCardMix.channels
        let frames = Int((seconds * LyricCardMix.sampleRate).rounded())
        var strands: [(role: SoundLayer.Role, samples: [Float])] = []
        for layer in layers {
            let level = LyricCardMix.decibels(forLevel: Double(layer.volume))
            guard level.isFinite else { continue }
            guard let rendered = await layer.render(seconds) else { return nil }
            var samples = rendered.samples
            if samples.count != frames * channels {
                samples = Array(samples.prefix(frames * channels))
                samples += repeatElement(0, count: frames * channels - samples.count)
            }
            let gain = LyricCardMix.linear(LyricCardMix.matchingGain(for: samples) + level)
            if let fade = rendered.fadeOut { LyricCardMix.fadeOut(&samples, endingAt: fade.end, seconds: fade.seconds) }
            samples.withUnsafeMutableBufferPointer { pointer in
                for index in pointer.indices { pointer[index] *= gain }
            }
            strands.append((layer.role, samples))
        }
        guard !strands.isEmpty else { return .some(nil) }
        if duck, let trigger = strands.first(where: { $0.role == .background })?.samples,
           strands.contains(where: { $0.role == .song }) {
            let gains = LyricCardMix.duckGains(under: trigger, frames: frames)
            for index in strands.indices where strands[index].role == .song {
                strands[index].samples.withUnsafeMutableBufferPointer { pointer in
                    for frame in 0..<frames {
                        for channel in 0..<channels { pointer[frame * channels + channel] *= gains[frame] }
                    }
                }
            }
        }
        var mix = [Float](repeating: 0, count: frames * channels)
        mix.withUnsafeMutableBufferPointer { out in
            for strand in strands {
                strand.samples.withUnsafeBufferPointer { input in
                    for index in out.indices { out[index] += input[index] }
                }
            }
        }
        LyricCardMix.fadeOut(&mix, endingAt: frames, seconds: LyricCardMix.endFade)
        LyricCardMix.limit(&mix)
        return .some(mix)
    }

    private static func pixelBuffer(from image: CGImage, size: CGSize, pool: CVPixelBufferPool?) -> CVPixelBuffer? {
        var buffer: CVPixelBuffer?
        if let pool {
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buffer)
        } else {
            CVPixelBufferCreate(nil, Int(size.width), Int(size.height), kCVPixelFormatType_32BGRA, nil, &buffer)
        }
        guard let buffer else { return nil }
        CVPixelBufferLockBaseAddress(buffer, [])
        defer { CVPixelBufferUnlockBaseAddress(buffer, []) }
        guard let context = CGContext(data: CVPixelBufferGetBaseAddress(buffer), width: Int(size.width), height: Int(size.height),
                                      bitsPerComponent: 8, bytesPerRow: CVPixelBufferGetBytesPerRow(buffer),
                                      space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        else { return nil }
        context.draw(image, in: CGRect(origin: .zero, size: size))
        return buffer
    }

    /// Makes each chosen format, writing them to `directory`. Returns the files made.
    static func export(_ content: Content, formats: Set<CardFormat>, to directory: URL,
                       notes: @escaping (String) -> Void = { _ in },
                       progress: @escaping (Double) -> Void = { _ in }) async -> [(CardFormat, URL)] {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        let order = CardFormat.allCases.filter(formats.contains)
        var made: [(CardFormat, URL)] = []
        for (index, format) in order.enumerated() {
            guard !Task.isCancelled else { break }
            let file = directory.appendingPathComponent("Lunalyr Lyric Card.\(format.rawValue)")
            let share: (Double) -> Void = { progress((Double(index) + $0) / Double(order.count)) }
            switch format {
            case .png:
                if let data = stillPNG(content), (try? data.write(to: file, options: .atomic)) != nil { made.append((format, file)) }
            case .gif:
                if let data = await gif(content), (try? data.write(to: file, options: .atomic)) != nil { made.append((format, file)) }
            case .mp4:
                let plan = clipPlan(content)
                if let note = plan.note { notes(note) }
                if await video(content, to: file, plan: plan, progress: share) {
                    made.append((format, file))
                    var layers: [SoundLayer] = []
                    let level = Float(content.soundVolume)
                    if let clip = content.songAudio {
                        let windows = songWindows(content)
                        if let range = songRange(content), !clip.covers(range) {
                            notes("Parts of these lines aren’t in the song’s recording, so they’re silent. Record the song again.")
                        }
                        if let layer = await songLayer(clip, windows: windows, volume: level) {
                            layers.append(layer)
                        } else {
                            notes("The MP4 has no song: couldn’t use the song’s audio.")
                        }
                    }
                    var tracks = content.extraSounds
                    if let file = content.soundFile { tracks.insert(LyricCard.SoundTrack(url: file, volume: content.soundVolume), at: 0) }
                    for track in tracks {
                        if let layer = await soundLayer(from: track.url, volume: Float(track.volume)) { layers.append(layer) }
                        else { notes("Couldn’t read the audio in \(track.url.lastPathComponent)") }
                    }
                    if let video = content.backgroundSoundSource, content.backgroundVolume > 0 {
                        // Repeats with the picture, crossfaded at the same seams.
                        if let layer = await soundLayer(from: video, volume: Float(content.backgroundVolume), role: .background,
                                                        crossfade: plan.backgroundLoop?.crossfade ?? 0) {
                            layers.append(layer)
                        }
                    }
                    if !layers.isEmpty, !(await mux(soundOnto: file, layers: layers, duck: content.duckSong)) {
                        notes("The MP4 has no sound: couldn’t add it.")
                    }
                }
            }
            share(1)
        }
        return made
    }

    /// Puts the files on the pasteboard as separate attachments (a PNG also
    /// as image data, so apps that paste pictures take it too).
    @discardableResult
    static func publish(_ files: [(CardFormat, URL)], to pasteboard: NSPasteboard) -> Bool {
        guard !files.isEmpty else { return false }
        let items = files.compactMap { format, url -> NSPasteboardItem? in
            let item = NSPasteboardItem()
            guard item.setString(url.absoluteString, forType: .fileURL) else { return nil }
            if format == .png, let data = try? Data(contentsOf: url) { item.setData(data, forType: .png) }
            return item
        }
        guard !items.isEmpty else { return false }
        pasteboard.clearContents()
        return pasteboard.writeObjects(items)
    }

    static var exportDirectory: URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("Lunalyr Lyric Cards", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
    }
}
