import Accelerate
import AudioToolbox
import Foundation

/// Both capture methods feed the same bounded PCM decoder and analyser.
/// This avoids silently dropping Float64 or integer/device-specific buffers.
///
/// The capture callback may run on Core Audio's real-time IO thread, so
/// `process` only decodes the selected channels into a preallocated,
/// lock-free ring: no locks, allocation, Objective-C or Task creation. A
/// worker queue drains the ring at a steady cadence and runs the meters and
/// the visualiser FFTs there, then reports to the main thread at most
/// `publishInterval` apart.
final class AudioPCMProcessor: @unchecked Sendable {
    /// What the worker last measured, delivered on `deliveryQueue`.
    struct Report {
        var levels = VCAudioLevels()
        /// Accepted capture callbacks since this processor was created.
        var packets = 0
        /// Frames lost because the worker fell behind, or skipped as stale.
        var droppedFrames = 0
        var formatLabel = ""
        /// Uptime of this delivery, and of the last non-zero sample.
        var deliveredAt = 0.0
        var audibleAt: Double?
        var problem: String?
    }

    static let ringCapacity = 65_536
    /// Frames analysed per pass; scratch buffers are allocated once.
    static let chunkFrames = 4_096
    /// After a stall, only the newest audio is worth analysing.
    static let maximumBacklog = 16_384
    static let drainInterval = DispatchTimeInterval.microseconds(16_667)
    /// Matches AudioAnalyzer's analysis hop.
    static let analyserHop = 1_024
    static let publishInterval = 1.0 / 15

    /// The single producer for ScreenCaptureKit's sample handler. Core Audio
    /// invokes the tap's IO block directly on its own thread instead.
    let captureQueue = DispatchQueue(label: "local.verso.audio-capture", qos: .userInitiated)
    private let analysisQueue = DispatchQueue(label: "local.verso.audio-analysis", qos: .userInitiated)
    private let ring: OpaquePointer
    private let leftSamples: UnsafeMutablePointer<Float>
    private let rightSamples: UnsafeMutablePointer<Float>
    private let deliveryQueue: DispatchQueue?
    private let onReport: (Report) -> Void
    private let target: VisualizerAudio
    // Owned by the analysis queue (or by the caller of `drain` while the
    // worker is stopped, as in tests).
    private var left = VCAudioState(), right = VCAudioState()
    private var levels = VCAudioLevels()
    private var lastPublish = -Double.infinity
    private var channelCount = 0
    private var formatKey = (0, 0, 0.0)
    private var formatLabel = ""
    private var audibleAt: Double?
    private var skipped = 0
    private var hopRemaining = AudioPCMProcessor.analyserHop
    private var reportedProblem = false
    // Owned by the main thread.
    private var timer: DispatchSourceTimer?

    /// `deliveryQueue: nil` reports synchronously from `drain`, for tests.
    init(target: VisualizerAudio = .shared, deliveryQueue: DispatchQueue? = .main,
         onReport: @escaping (Report) -> Void) {
        guard let ring = vc_audio_ring_create(Int32(Self.ringCapacity)) else {
            fatalError("Could not allocate the audio ring.")
        }
        self.ring = ring
        self.target = target
        self.deliveryQueue = deliveryQueue
        self.onReport = onReport
        leftSamples = .allocate(capacity: Self.chunkFrames)
        rightSamples = .allocate(capacity: Self.chunkFrames)
        leftSamples.initialize(repeating: 0, count: Self.chunkFrames)
        rightSamples.initialize(repeating: 0, count: Self.chunkFrames)
    }
    deinit {
        timer?.cancel()
        vc_audio_ring_destroy(ring)
        leftSamples.deallocate()
        rightSamples.deallocate()
    }

    static func sampleFormat(_ format: AudioStreamBasicDescription) -> VCPCMFormat? {
        guard format.mFormatID == kAudioFormatLinearPCM, format.mChannelsPerFrame > 0,
              format.mBytesPerFrame > 0, format.mBytesPerFrame <= 512,
              format.mSampleRate.isFinite, (8000...192000).contains(format.mSampleRate),
              format.mFormatFlags & kAudioFormatFlagIsBigEndian == 0 else { return nil }
        let channels = format.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0 ? 1 : max(1, format.mChannelsPerFrame)
        let width = format.mBytesPerFrame / channels
        if format.mFormatFlags & kAudioFormatFlagIsFloat != 0 {
            if format.mBitsPerChannel == 32 && width == 4 { return VC_PCM_FLOAT32 }
            if format.mBitsPerChannel == 64 && width == 8 { return VC_PCM_FLOAT64 }
            return nil
        }
        guard format.mFormatFlags & kAudioFormatFlagIsSignedInteger != 0 else { return nil }
        if format.mBitsPerChannel == 16 && width == 2 { return VC_PCM_INT16 }
        if format.mBitsPerChannel == 24 && width == 3 { return VC_PCM_INT24 }
        if format.mBitsPerChannel == 32 && width == 4 { return VC_PCM_INT32 }
        return nil
    }

    // MARK: - Producer (real-time safe)

    /// Selects up to two channels (the trailing tap channels when
    /// `lastChannelsOnly`) and copies them into the ring. Bounded work only.
    func process(_ list: UnsafePointer<AudioBufferList>, format: AudioStreamBasicDescription, lastChannelsOnly: Bool = false) {
        guard let encoding = Self.sampleFormat(format) else {
            vc_audio_ring_note_unsupported(ring)
            return
        }
        let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: list))
        let sampleBytes = Int((format.mBitsPerChannel + 7) / 8)
        let planar = format.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
        var totalChannels = 0
        for buffer in buffers { totalChannels += Int(buffer.mNumberChannels) }
        let firstChannel = lastChannelsOnly ? max(0, totalChannels - Int(format.mChannelsPerFrame)) : 0
        var channelBase = 0, selected = 0, frames = 192_000
        var leftData: UnsafeRawPointer?, leftBytes = 0, leftStride = 0
        var rightData: UnsafeRawPointer?, rightBytes = 0, rightStride = 0
        for buffer in buffers {
            let channels = Int(buffer.mNumberChannels)
            let skip = max(0, firstChannel - channelBase)
            channelBase += channels
            guard skip < channels, let data = buffer.mData, buffer.mDataByteSize <= 2_000_000 else { continue }
            let stride = planar ? Int(format.mBytesPerFrame) : sampleBytes * channels
            guard stride > 0, stride <= 512 else { continue }
            let bytes = Int(buffer.mDataByteSize), available = min(192_000, bytes / stride)
            guard available > 0 else { continue }
            var channel = skip
            // Keep stereo channels independent: the analysers take the
            // stronger side, so opposite-phase content cannot cancel.
            while channel < channels && selected < 2 {
                let offset = channel * sampleBytes
                if bytes > offset {
                    if selected == 0 {
                        leftData = UnsafeRawPointer(data) + offset; leftBytes = bytes - offset; leftStride = stride
                    } else {
                        rightData = UnsafeRawPointer(data) + offset; rightBytes = bytes - offset; rightStride = stride
                    }
                    frames = min(frames, available)
                    selected += 1
                }
                channel += 1
            }
            if selected == 2 { break }
        }
        guard let leftData else { return }
        vc_audio_ring_write_pcm(ring, leftData, Int32(leftBytes), Int32(leftStride),
            rightData, Int32(rightBytes), Int32(rightStride), Int32(frames), encoding, format.mSampleRate)
    }

    // MARK: - Worker

    /// Starts draining at a steady cadence. Frames left from an earlier run
    /// (before a suspension) are stale and skipped first. Main thread only.
    func startAnalysis() {
        guard timer == nil else { return }
        let timer = DispatchSource.makeTimerSource(queue: analysisQueue)
        timer.schedule(deadline: .now(), repeating: Self.drainInterval, leeway: .milliseconds(4))
        timer.setEventHandler { [weak self] in _ = self?.drain() }
        analysisQueue.async { [weak self] in self?.discardBacklog() }
        timer.resume()
        self.timer = timer
    }
    /// Stops the worker; capture callbacks may continue to fill the ring.
    func stopAnalysis() {
        timer?.cancel()
        timer = nil
    }

    private func discardBacklog() {
        skipped += Int(vc_audio_ring_discard(ring, Int32(vc_audio_ring_available(ring))))
    }

    /// One consumer pass. Returns the number of frames analysed.
    @discardableResult
    func drain(now: Double = ProcessInfo.processInfo.systemUptime) -> Int {
        let backlog = Int(vc_audio_ring_available(ring))
        if backlog > Self.maximumBacklog {
            skipped += Int(vc_audio_ring_discard(ring, Int32(backlog - Self.maximumBacklog)))
        }
        var analysed = 0
        var info = VCAudioRingInfo()
        while true {
            let count = Int(vc_audio_ring_read(ring, leftSamples, rightSamples, Int32(Self.chunkFrames), &info))
            guard count > 0 else { break }
            analyse(count, info: info, now: now)
            analysed += count
        }
        let stats = vc_audio_ring_stats(ring)
        var problem: String?
        if stats.unsupported > 0, !reportedProblem {
            reportedProblem = true
            problem = "This audio format isn’t supported. Try the other capture method."
        }
        guard analysed > 0 || problem != nil else { return 0 }
        guard problem != nil || now - lastPublish >= Self.publishInterval else { return analysed }
        lastPublish = now
        let report = Report(levels: levels, packets: Int(clamping: stats.packets),
            droppedFrames: Int(clamping: stats.dropped) + skipped, formatLabel: formatLabel,
            deliveredAt: now, audibleAt: audibleAt, problem: problem)
        if let deliveryQueue {
            let onReport = onReport
            deliveryQueue.async { onReport(report) }
        } else {
            onReport(report)
        }
        return analysed
    }

    private func analyse(_ count: Int, info: VCAudioRingInfo, now: Double) {
        let stereo = info.channels > 1, rate = info.sample_rate
        let a = vc_audio_process(&left, leftSamples, Int32(count), 1, rate)
        let b = stereo ? vc_audio_process(&right, rightSamples, Int32(count), 1, rate) : a
        levels = VCAudioLevels(rms: max(a.rms, b.rms), bass: max(a.bass, b.bass),
                               mid: max(a.mid, b.mid), treble: max(a.treble, b.treble), pulse: max(a.pulse, b.pulse))
        let channels = stereo ? 2 : 1
        if channels != channelCount {
            channelCount = channels
            target.setChannelCount(channels)
        }
        // The analyser runs one FFT pass per `analyserHop` new samples and
        // its smoothing is tuned per pass. Feed pieces which end on that
        // boundary so a drain of any size keeps the same pass rate as the
        // capture callbacks did (about 47 a second at 48 kHz).
        var offset = 0
        while offset < count {
            let piece = min(count - offset, hopRemaining)
            target.append(leftSamples + offset, count: piece, sampleRate: rate, channel: 0)
            if stereo { target.append(rightSamples + offset, count: piece, sampleRate: rate, channel: 1) }
            offset += piece
            hopRemaining -= piece
            if hopRemaining == 0 { hopRemaining = Self.analyserHop }
        }
        // An unauthorised tap delivers exact digital silence; real playback,
        // even a quiet passage, carries some non-zero samples.
        var peak: Float = 0, rightPeak: Float = 0
        vDSP_maxmgv(leftSamples, 1, &peak, vDSP_Length(count))
        if stereo { vDSP_maxmgv(rightSamples, 1, &rightPeak, vDSP_Length(count)) }
        if max(peak, rightPeak) > 0 { audibleAt = now }
        let key = (channels, Int(info.format), rate)
        if key != formatKey {
            formatKey = key
            formatLabel = Self.label(channels: channels, format: VCPCMFormat(rawValue: UInt32(info.format)), rate: rate)
        }
    }

    static func label(channels: Int, format: VCPCMFormat, rate: Double) -> String {
        let bits: String
        switch format {
        case VC_PCM_FLOAT32: bits = "32bit float"
        case VC_PCM_FLOAT64: bits = "64bit float"
        case VC_PCM_INT16: bits = "16bit"
        case VC_PCM_INT24: bits = "24bit"
        case VC_PCM_INT32: bits = "32bit"
        default: bits = "PCM"
        }
        return "\(Int(rate)) Hz · \(channels)ch · \(bits)"
    }
}
