import Accelerate
import AVFoundation
import Foundation
import os

// Audio alignment for library videos: the video file's own soundtrack,
// decoded (never played), against the song as it sounds right now, from
// the capture the visualisers already run. Both become onset envelopes at
// `MusicVideoAudioAlign.rate` frames a second, and the lag where they
// correlate best is how far into the video the song starts.

enum MusicVideoAudioAlign {
    /// Envelope frames per second.
    static let rate = 50.0
    /// The live window compared, seconds.
    static let window: ClosedRange<Double> = 20...30
    /// Offsets searched, seconds either way.
    static let maximumLag = 60.0
    /// The peak must correlate at least this well…
    static let minimumPeak: Float = 0.3
    /// …and this much better than the best lag more than `peakExclusion` away.
    static let peakRatio: Float = 1.3
    static let peakExclusion = 1.0

    struct Result: Equatable, Sendable {
        var offset: Double
        var peak: Float
        var runnerUp: Float
    }

    /// Onsets from per-frame energies: the rise in log energy, rises only.
    /// Loudness and mastering differ between a video and the album track;
    /// where the hits fall does not.
    static func onsets(_ energies: [Float]) -> [Float] {
        guard energies.count > 1 else { return [] }
        let logs = energies.map { log10(max($0, 1e-9)) }
        var out = [Float](repeating: 0, count: logs.count)
        for i in 1..<logs.count { out[i] = max(0, logs[i] - logs[i - 1]) }
        return out
    }

    /// Mean-square energy per frame of `hop` samples.
    static func energies(_ samples: UnsafeBufferPointer<Float>, hop: Int) -> [Float] {
        guard hop > 0, let base = samples.baseAddress else { return [] }
        let frames = samples.count / hop
        var out = [Float](repeating: 0, count: frames)
        for frame in 0..<frames {
            var value: Float = 0
            vDSP_measqv(base + frame * hop, 1, &value, vDSP_Length(hop))
            out[frame] = value
        }
        return out
    }

    /// The offset (video time − song time) at which `live` (onsets for
    /// song times starting at `liveStart`) best matches `video` (onsets from
    /// the video's start), or nil without a clear peak.
    static func align(live: [Float], liveStart: Double, video: [Float], rate: Double = rate,
                      maximumLag: Double = maximumLag) -> Result? {
        let n = live.count
        guard n >= Int(window.lowerBound * rate * 0.9), video.count >= n, liveStart.isFinite else { return nil }
        let liveNormal = standardized(live)
        guard liveNormal.contains(where: { $0 != 0 }) else { return nil }
        let startIndex = Int((liveStart * rate).rounded())
        let lagFrames = Int((maximumLag * rate).rounded())
        // Running sums over the video for each window's mean and spread.
        var prefix = [Double](repeating: 0, count: video.count + 1)
        var prefixSquares = [Double](repeating: 0, count: video.count + 1)
        for i in 0..<video.count {
            prefix[i + 1] = prefix[i] + Double(video[i])
            prefixSquares[i + 1] = prefixSquares[i] + Double(video[i]) * Double(video[i])
        }
        var scores: [(lag: Int, score: Float)] = []
        scores.reserveCapacity(2 * lagFrames + 1)
        video.withUnsafeBufferPointer { videoPointer in
            liveNormal.withUnsafeBufferPointer { livePointer in
                for lag in -lagFrames...lagFrames {
                    let begin = startIndex + lag
                    guard begin >= 0, begin + n <= video.count else { continue }
                    let sum = prefix[begin + n] - prefix[begin]
                    let squares = prefixSquares[begin + n] - prefixSquares[begin]
                    let variance = squares - sum * sum / Double(n)
                    guard variance > 1e-12 else { continue }
                    var dot: Float = 0
                    vDSP_dotpr(livePointer.baseAddress!, 1, videoPointer.baseAddress! + begin, 1, &dot, vDSP_Length(n))
                    // The live side is standardised (mean 0), so the video's
                    // mean drops out of the dot product.
                    scores.append((lag, Float(Double(dot) / sqrt(variance))))
                }
            }
        }
        guard let best = scores.max(by: { $0.score < $1.score }), best.score >= minimumPeak else { return nil }
        let exclusion = Int(peakExclusion * rate)
        let runnerUp = scores.filter { abs($0.lag - best.lag) > exclusion }.map(\.score).max() ?? 0
        guard best.score >= peakRatio * max(runnerUp, 0.01) else { return nil }
        let offset = Double(best.lag) / rate
        return Result(offset: (offset * 100).rounded() / 100, peak: best.score, runnerUp: runnerUp)
    }

    /// Zero mean, unit length.
    static func standardized(_ values: [Float]) -> [Float] {
        guard !values.isEmpty else { return [] }
        var mean: Float = 0
        vDSP_meanv(values, 1, &mean, vDSP_Length(values.count))
        let centred = values.map { $0 - mean }
        var squares: Float = 0
        vDSP_svesq(centred, 1, &squares, vDSP_Length(centred.count))
        guard squares > 1e-12 else { return [Float](repeating: 0, count: values.count) }
        let scale = 1 / sqrt(squares)
        return centred.map { $0 * scale }
    }

    /// The live window as onsets with the song time of its first frame:
    /// the newest frames, taken while the song played without a break.
    /// `songTimeNow` and `now` are read together; song time is assumed to
    /// have run with the clock across the window, so a window that crosses
    /// the song's start, a gap in capture or a seek is refused.
    static func liveWindow(_ frames: [AudioEnvelopeRecorder.Frame], now: TimeInterval, songTimeNow: Double,
                           songStartedAt: TimeInterval, rate: Double = rate) -> (onsets: [Float], start: Double)? {
        let usable = frames.filter { $0.time > songStartedAt && $0.time <= now }
        guard let last = usable.last else { return nil }
        let wanted = Int(window.upperBound * rate)
        var picked = Array(usable.suffix(wanted))
        // Stop at a gap: capture paused or the song was sought.
        if let gap = picked.indices.dropFirst().last(where: { picked[$0].time - picked[$0 - 1].time > 0.25 }) {
            picked = Array(picked[gap...])
        }
        guard let first = picked.first, Double(picked.count) >= window.lowerBound * rate * 0.9 else { return nil }
        let start = songTimeNow - (now - first.time)
        guard start >= 0, songTimeNow - (now - last.time) > start else { return nil }
        return (onsets(picked.map(\.energy)), start)
    }
}

// MARK: - Live envelope

/// The last half minute of the captured audio as energy frames, filled by
/// `AudioPCMProcessor` on its analysis queue while the visualisers' capture
/// runs. Nothing is captured for this: when capture is off, it stays empty.
final class AudioEnvelopeRecorder: @unchecked Sendable {
    static let shared = AudioEnvelopeRecorder()

    struct Frame: Equatable, Sendable {
        /// Uptime when the frame's audio was captured.
        var time: TimeInterval
        var energy: Float
    }

    static let capacity = Int(MusicVideoAudioAlign.window.upperBound * MusicVideoAudioAlign.rate) + 100
    private let lock = OSAllocatedUnfairLock()
    private var frames: [Frame] = []
    // Owned by the analysis queue.
    private var accumulated: Float = 0
    private var accumulatedCount = 0

    /// Samples from one analysis pass, the newest ending at `now`.
    func append(_ samples: UnsafePointer<Float>, count: Int, sampleRate: Double, now: TimeInterval) {
        guard count > 0, sampleRate > 0 else { return }
        let hop = max(1, Int(sampleRate / MusicVideoAudioAlign.rate))
        var newFrames: [Frame] = []
        var index = 0
        while index < count {
            let piece = min(count - index, hop - accumulatedCount)
            var squares: Float = 0
            vDSP_svesq(samples + index, 1, &squares, vDSP_Length(piece))
            accumulated += squares
            accumulatedCount += piece
            index += piece
            if accumulatedCount == hop {
                let age = Double(count - index) / sampleRate
                newFrames.append(Frame(time: now - age, energy: accumulated / Float(hop)))
                accumulated = 0
                accumulatedCount = 0
            }
        }
        guard !newFrames.isEmpty else { return }
        lock.withLock {
            frames.append(contentsOf: newFrames)
            if frames.count > Self.capacity { frames.removeFirst(frames.count - Self.capacity) }
        }
    }

    func snapshot() -> [Frame] { lock.withLock { frames } }

    func reset() {
        lock.withLock { frames.removeAll() }
    }
}

// MARK: - Video envelope

/// The video file's soundtrack as energy frames, decoded with
/// AVAssetReader: read only, never played, nothing reaches an output.
enum MusicVideoSoundtrack {
    static let sampleRate = 22_050.0

    static func energies(of url: URL) async -> [Float]? {
        await Task.detached(priority: .utility) { () -> [Float]? in
            guard !AppRuntime.isUnitTesting else { return nil }
            let asset = AVURLAsset(url: url)
            guard let track = try? await asset.loadTracks(withMediaType: .audio).first,
                  let reader = try? AVAssetReader(asset: asset) else { return nil }
            let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
                AVFormatIDKey: kAudioFormatLinearPCM,
                AVLinearPCMBitDepthKey: 32,
                AVLinearPCMIsFloatKey: true,
                AVLinearPCMIsNonInterleaved: false,
                AVLinearPCMIsBigEndianKey: false,
                AVNumberOfChannelsKey: 1,
                AVSampleRateKey: sampleRate,
            ])
            output.alwaysCopiesSampleData = false
            guard reader.canAdd(output) else { return nil }
            reader.add(output)
            guard reader.startReading() else { return nil }
            let hop = Int(sampleRate / MusicVideoAudioAlign.rate)
            var pending: [Float] = []
            var energies: [Float] = []
            while let buffer = output.copyNextSampleBuffer() {
                if Task.isCancelled { reader.cancelReading(); return nil }
                guard let block = CMSampleBufferGetDataBuffer(buffer) else { continue }
                let length = CMBlockBufferGetDataLength(block)
                var chunk = [Float](repeating: 0, count: length / MemoryLayout<Float>.size)
                chunk.withUnsafeMutableBytes { raw in
                    _ = CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: raw.baseAddress!)
                }
                pending += chunk
                let whole = pending.count / hop * hop
                if whole > 0 {
                    pending.withUnsafeBufferPointer { pointer in
                        energies += MusicVideoAudioAlign.energies(UnsafeBufferPointer(rebasing: pointer[0..<whole]), hop: hop)
                    }
                    pending.removeFirst(whole)
                }
            }
            return reader.status == .completed && !energies.isEmpty ? energies : nil
        }.value
    }
}
