import AVFoundation
import Foundation

// MARK: - Mixing a lyric card's sound
//
// Each strand of sound (the song, the background video's own sound, audio
// files) is decoded to plain samples, brought to the same loudness, set to the
// level its slider asks for, summed, faded out at the end and run through a
// peak limiter, then written as AAC and laid under the video. Doing it here
// rather than with AVAudioMix means levels above 100% are real gain, a mastered
// song no longer drowns out a phone video at the same setting, and nothing clips.
//
// Samples are interleaved stereo Float at `sampleRate` throughout.

enum LyricCardMix {
    static let sampleRate = 48_000.0
    static let channels = 2
    /// Every strand is matched to this loudness before its slider level is applied.
    static let referenceLoudness = -16.0
    /// How far matching may turn a strand up or down. Near-silence stays near-silent.
    static let matchingRange = -30.0...24.0
    /// The limiter's ceiling, in dBFS.
    static let ceilingDecibels = -1.0
    static var ceiling: Float { Float(pow(10, ceilingDecibels / 20)) }
    /// Every MP4's sound fades out over its last this-many seconds, so it never ends on a click.
    static let endFade = 0.3
    /// The song fades out over this where it is cut or stops before the MP4 does.
    static let songCutFade = 0.4

    // MARK: Slider levels

    /// Slider positions run from 0 to `maximumLevel`; 1 is "as matched".
    static let maximumLevel = 1.5
    /// The gain a slider position stands for, in dB: -inf at 0, 40·log10(x)
    /// up to 1 (so 50% is -12 dB, clearly quieter), then straight up to +12 dB
    /// at 150%.
    static func decibels(forLevel level: Double) -> Double {
        guard level > 0.005 else { return -.infinity }
        if level <= 1 { return 40 * log10(level) }
        return min(level, maximumLevel) * 24 - 24
    }

    /// What the slider shows: "Off", "−12 dB", "0 dB", "+12 dB".
    static func label(forLevel level: Double) -> String {
        let db = decibels(forLevel: level)
        guard db.isFinite else { return "Off" }
        let rounded = Int(db.rounded())
        if rounded == 0 { return "0 dB" }
        return rounded > 0 ? "+\(rounded) dB" : "\u{2212}\(-rounded) dB"
    }

    static func linear(_ decibels: Double) -> Float { decibels.isFinite ? Float(pow(10, decibels / 20)) : 0 }

    // MARK: Loudness

    /// Integrated loudness in LUFS (ITU-R BS.1770: K-weighting, 400 ms blocks
    /// overlapping by 75%, absolute gate at -70 and relative gate at -10 LU).
    /// Nil for silence.
    static func loudness(_ samples: [Float], channels: Int = channels, sampleRate: Double = sampleRate) -> Double? {
        let frames = samples.count / channels
        guard frames > 0 else { return nil }
        // Squared K-weighted samples, summed across channels, per 100 ms step.
        let step = max(1, Int(sampleRate * 0.1))
        let steps = (frames + step - 1) / step
        var energy = [Double](repeating: 0, count: steps)
        for channel in 0..<channels {
            var shelf = Biquad.kShelf(sampleRate), highPass = Biquad.kHighPass(sampleRate)
            samples.withUnsafeBufferPointer { input in
                for frame in 0..<frames {
                    let y = highPass.process(shelf.process(Double(input[frame * channels + channel])))
                    energy[frame / step] += y * y
                }
            }
        }
        // 400 ms blocks every 100 ms; a clip shorter than one block is one block.
        var blocks: [Double] = []
        if steps < 4 {
            blocks = [energy.reduce(0, +) / Double(frames)]
        } else {
            for start in 0...(steps - 4) {
                let count = min(4 * step, frames - start * step)
                blocks.append(energy[start..<(start + 4)].reduce(0, +) / Double(count))
            }
        }
        func lufs(_ meanSquare: Double) -> Double { -0.691 + 10 * log10(max(meanSquare, 1e-20)) }
        let absolute = blocks.filter { lufs($0) > -70 }
        guard !absolute.isEmpty else { return nil }
        let threshold = lufs(absolute.reduce(0, +) / Double(absolute.count)) - 10
        let relative = absolute.filter { lufs($0) > threshold }
        return lufs(relative.reduce(0, +) / Double(max(1, relative.count)))
    }

    /// The gain, in dB, that brings `samples` to `referenceLoudness`; 0 for silence.
    static func matchingGain(for samples: [Float]) -> Double {
        guard let measured = loudness(samples) else { return 0 }
        return min(max(referenceLoudness - measured, matchingRange.lowerBound), matchingRange.upperBound)
    }

    struct Biquad {
        var b0, b1, b2, a1, a2: Double
        var z1 = 0.0, z2 = 0.0

        mutating func process(_ x: Double) -> Double {
            let y = b0 * x + z1
            z1 = b1 * x - a1 * y + z2
            z2 = b2 * x - a2 * y
            return y
        }

        /// BS.1770's head-related high shelf (+4 dB above ~1.7 kHz), for any sample rate.
        static func kShelf(_ rate: Double) -> Biquad {
            let f0 = 1681.974450955533, gain = 3.999843853973347, q = 0.7071752369554196
            let k = tan(.pi * f0 / rate)
            let vh = pow(10, gain / 20), vb = pow(vh, 0.4996667741545416)
            let a0 = 1 + k / q + k * k
            return Biquad(b0: (vh + vb * k / q + k * k) / a0, b1: 2 * (k * k - vh) / a0, b2: (vh - vb * k / q + k * k) / a0,
                          a1: 2 * (k * k - 1) / a0, a2: (1 - k / q + k * k) / a0)
        }

        /// BS.1770's RLB high-pass at ~38 Hz.
        static func kHighPass(_ rate: Double) -> Biquad {
            let f0 = 38.13547087602444, q = 0.5003270373238773
            let k = tan(.pi * f0 / rate)
            let a0 = 1 + k / q + k * k
            return Biquad(b0: 1, b1: -2, b2: 1, a1: 2 * (k * k - 1) / a0, a2: (1 - k / q + k * k) / a0)
        }
    }

    // MARK: Shaping

    /// Look-ahead peak limiter: no sample leaves above `ceiling`. Gain comes
    /// down smoothly over the look-ahead before a peak and recovers over `release`.
    static func limit(_ samples: inout [Float], channels: Int = channels, ceiling: Float = ceiling,
                      lookahead: Double = 0.005, release: Double = 0.1, sampleRate: Double = sampleRate) {
        let frames = samples.count / channels
        guard frames > 0 else { return }
        let ahead = max(1, Int(lookahead * sampleRate))
        // The gain each frame needs on its own.
        var needed = [Float](repeating: 1, count: frames)
        var any = false
        samples.withUnsafeBufferPointer { input in
            for frame in 0..<frames {
                var peak: Float = 0
                for channel in 0..<channels { peak = max(peak, abs(input[frame * channels + channel])) }
                if peak > ceiling { needed[frame] = ceiling / peak; any = true }
            }
        }
        guard any else { return }
        // The lowest need over the next `ahead` frames (a sliding minimum), then
        // recovery limited by the release, then a moving average over the
        // look-ahead. Every value averaged for a frame is at most that frame's
        // need, so the average is too.
        var window: [Int] = []
        var head = 0
        var released = [Float](repeating: 1, count: frames)
        let recovery = Float(1 - exp(-1 / (release * sampleRate)))
        var previous: Float = 1
        var next = 0
        for frame in 0..<frames {
            while next < min(frames, frame + ahead + 1) {
                while window.count > head, needed[window[window.count - 1]] >= needed[next] { window.removeLast() }
                window.append(next)
                next += 1
            }
            while window[head] < frame { head += 1 }
            if head > 4096 { window.removeFirst(head); head = 0 }
            let target = needed[window[head]]
            previous = min(target, previous + (1 - previous) * recovery)
            released[frame] = previous
        }
        var sum: Float = 0
        samples.withUnsafeMutableBufferPointer { output in
            for frame in 0..<frames {
                sum += released[frame]
                if frame > ahead { sum -= released[frame - ahead - 1] }
                let gain = min(sum / Float(min(frame, ahead) + 1), needed[frame])
                for channel in 0..<channels {
                    let index = frame * channels + channel
                    output[index] = min(max(output[index] * gain, -ceiling), ceiling)
                }
            }
        }
    }

    /// Fades to silence over `seconds` ending at `end` (in frames); silence after it.
    static func fadeOut(_ samples: inout [Float], endingAt end: Int, seconds: Double, channels: Int = channels,
                        sampleRate: Double = sampleRate) {
        let frames = samples.count / channels
        let end = min(max(0, end), frames)
        let length = max(1, Int(seconds * sampleRate))
        let start = max(0, end - length)
        samples.withUnsafeMutableBufferPointer { output in
            for frame in start..<end {
                let gain = Float(end - frame - 1) / Float(length)
                for channel in 0..<channels { output[frame * channels + channel] *= gain }
            }
            for index in (end * channels)..<(frames * channels) { output[index] = 0 }
        }
    }

    /// Per-frame gains that lower a strand by `depth` dB while `trigger` is
    /// above `threshold` dBFS (measured as RMS over 10 ms), with `attack` and
    /// `release` time constants.
    static func duckGains(under trigger: [Float], frames: Int, threshold: Double = -40, depth: Double = -12,
                          attack: Double = 0.05, release: Double = 0.3, channels: Int = channels,
                          sampleRate: Double = sampleRate) -> [Float] {
        let block = max(1, Int(sampleRate * 0.01))
        let low = linear(depth)
        let limit = Float(pow(10, threshold / 20))
        let fall = Float(1 - exp(-1 / (attack * sampleRate))), rise = Float(1 - exp(-1 / (release * sampleRate)))
        var gains = [Float](repeating: 1, count: frames)
        var gain: Float = 1
        let available = trigger.count / channels
        trigger.withUnsafeBufferPointer { input in
            var start = 0
            while start < frames {
                let end = min(frames, start + block)
                var energy: Float = 0
                var counted = 0
                for frame in start..<min(end, available) {
                    for channel in 0..<channels { let x = input[frame * channels + channel]; energy += x * x }
                    counted += channels
                }
                let rms = counted > 0 ? (energy / Float(counted)).squareRoot() : 0
                let target: Float = rms > limit ? low : 1
                for frame in start..<end {
                    gain += (target - gain) * (target < gain ? fall : rise)
                    gains[frame] = gain
                }
                start = end
            }
        }
        return gains
    }

    /// `source` repeated (or cut) to `frames`. With a crossfade, each repeat
    /// starts `crossfade` frames before the previous one ends and the two are
    /// blended linearly across that overlap, the same way the picture is.
    static func loop(_ source: [Float], to frames: Int, crossfade: Int = 0, channels: Int = channels) -> [Float] {
        let length = source.count / channels
        var output = [Float](repeating: 0, count: frames * channels)
        guard length > 0, frames > 0 else { return output }
        let overlap = length > frames ? 0 : min(max(0, crossfade), length / 2)
        let period = length - overlap
        source.withUnsafeBufferPointer { input in
            output.withUnsafeMutableBufferPointer { out in
                var pass = 0
                while pass * period < frames {
                    let offset = pass * period
                    let followed = offset + period < frames
                    for frame in 0..<min(length, frames - offset) {
                        var gain: Float = 1
                        if pass > 0, frame < overlap { gain *= Float(frame) / Float(overlap) }
                        if followed, overlap > 0, frame >= period { gain *= Float(length - frame) / Float(overlap) }
                        for channel in 0..<channels {
                            out[(offset + frame) * channels + channel] += input[frame * channels + channel] * gain
                        }
                    }
                    pass += 1
                }
            }
        }
        return output
    }

    // MARK: Reading and writing

    /// The first audio track of `url` from `start` for `seconds` (to the end
    /// when nil) as interleaved stereo at `sampleRate`. Nil when it has no
    /// readable sound.
    static func decode(_ url: URL, from start: Double = 0, seconds: Double? = nil) async -> [Float]? {
        let asset = AVURLAsset(url: url)
        guard let track = try? await asset.loadTracks(withMediaType: .audio).first,
              let reader = try? AVAssetReader(asset: asset) else { return nil }
        let described = (try? await track.load(.formatDescriptions))?.first
        let sourceChannels = described.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame } ?? 2
        let readChannels = sourceChannels == 1 ? 1 : 2
        var settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM, AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false, AVLinearPCMIsBigEndianKey: false,
            AVSampleRateKey: sampleRate, AVNumberOfChannelsKey: readChannels,
        ]
        if sourceChannels > 2 {
            var layout = AudioChannelLayout()
            layout.mChannelLayoutTag = kAudioChannelLayoutTag_Stereo
            settings[AVChannelLayoutKey] = Data(bytes: &layout, count: MemoryLayout<AudioChannelLayout>.size)
        }
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: settings)
        output.alwaysCopiesSampleData = false
        guard reader.canAdd(output) else { return nil }
        reader.add(output)
        reader.timeRange = CMTimeRange(start: CMTime(seconds: max(0, start), preferredTimescale: 48_000),
                                       duration: seconds.map { CMTime(seconds: max(0, $0), preferredTimescale: 48_000) } ?? .positiveInfinity)
        guard reader.startReading() else { return nil }
        var read: [Float] = []
        if let seconds { read.reserveCapacity(Int(seconds * sampleRate) * readChannels + 4096) }
        while let buffer = output.copyNextSampleBuffer() {
            if Task.isCancelled { reader.cancelReading(); return nil }
            guard let block = CMSampleBufferGetDataBuffer(buffer) else { continue }
            let length = CMBlockBufferGetDataLength(block)
            let count = length / MemoryLayout<Float>.size
            let base = read.count
            read.append(contentsOf: repeatElement(0, count: count))
            read.withUnsafeMutableBufferPointer { pointer in
                _ = CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: count * MemoryLayout<Float>.size,
                                               destination: pointer.baseAddress! + base)
            }
        }
        guard reader.status == .completed else { return nil }
        if let seconds { read = Array(read.prefix(Int((seconds * sampleRate).rounded()) * readChannels)) }
        guard readChannels == 1 else { return read }
        var stereo = [Float](repeating: 0, count: read.count * 2)
        for (index, sample) in read.enumerated() { stereo[index * 2] = sample; stereo[index * 2 + 1] = sample }
        return stereo
    }

    /// Writes interleaved stereo samples as AAC in an M4A.
    static func writeAAC(_ samples: [Float], to url: URL) throws {
        guard let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: AVAudioChannelCount(channels)) else {
            throw CocoaError(.fileWriteUnknown)
        }
        let file = try AVAudioFile(forWriting: url, settings: [
            AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: channels, AVEncoderBitRateKey: 192_000,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)
        let frames = samples.count / channels
        let chunk = 32_768
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(chunk)),
              let planes = buffer.floatChannelData else { throw CocoaError(.fileWriteUnknown) }
        var start = 0
        while start < frames {
            let count = min(chunk, frames - start)
            for frame in 0..<count {
                for channel in 0..<channels { planes[channel][frame] = samples[(start + frame) * channels + channel] }
            }
            buffer.frameLength = AVAudioFrameCount(count)
            try file.write(from: buffer)
            start += count
        }
    }
}
