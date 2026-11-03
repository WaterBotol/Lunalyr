import AudioToolbox
import Foundation

/// Both capture methods feed the same bounded PCM decoder and analyser.
/// This avoids silently dropping Float64 or integer/device-specific buffers.
// Mutable analyser state is owned exclusively by the serial capture queue.
final class AudioPCMProcessor: @unchecked Sendable {
    let queue = DispatchQueue(label: "local.verso.audio-pcm", qos: .userInitiated)
    private var left = VCAudioState(), right = VCAudioState()
    private var lastPublish = 0.0
    private var reportedProblem = false
    private let onLevels: (VCAudioLevels, String) -> Void
    private let onProblem: (String) -> Void
    init(onLevels: @escaping (VCAudioLevels, String) -> Void, onProblem: @escaping (String) -> Void) {
        self.onLevels = onLevels; self.onProblem = onProblem
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
    func process(_ list: UnsafePointer<AudioBufferList>, format: AudioStreamBasicDescription, lastChannelsOnly: Bool = false) {
        guard let encoding = Self.sampleFormat(format), format.mBytesPerFrame > 0,
              format.mSampleRate.isFinite, (8000...192000).contains(format.mSampleRate),
              format.mBytesPerFrame <= 512 else {
            if !reportedProblem { reportedProblem = true; onProblem("This audio format isn’t supported. Try the other capture method.") }
            return
        }
        let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: list))
        let sampleBytes = Int((format.mBitsPerChannel + 7) / 8)
        let totalChannels = buffers.reduce(0) { $0 + Int($1.mNumberChannels) }
        let firstChannel = lastChannelsOnly ? max(0, totalChannels - Int(format.mChannelsPerFrame)) : 0
        var channelBase = 0
        var a = VCAudioLevels(), b = VCAudioLevels(), processed = 0
        for buffer in buffers {
            let channels = Int(buffer.mNumberChannels)
            let skip = max(0, firstChannel - channelBase)
            channelBase += channels
            guard skip < channels else { continue }
            guard let data = buffer.mData, buffer.mDataByteSize <= 2_000_000 else { continue }
            let stride = format.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
                ? Int(format.mBytesPerFrame) : sampleBytes * channels
            guard stride > 0, stride <= 512 else { continue }
            let bytes = Int(buffer.mDataByteSize), frames = min(192000, bytes / stride)
            guard frames > 0 else { continue }
            for channel in skip..<min(skip + 2 - processed, channels) {
                let offset = channel * sampleBytes
                guard bytes > offset else { continue }
                if processed == 0 {
                    a = vc_audio_process_pcm(&left, data.advanced(by: offset), Int32(bytes - offset),
                                            Int32(frames), Int32(stride), encoding, format.mSampleRate)
                } else {
                    b = vc_audio_process_pcm(&right, data.advanced(by: offset), Int32(bytes - offset),
                                            Int32(frames), Int32(stride), encoding, format.mSampleRate)
                }
                processed += 1
            }
            if processed == 2 { break }
        }
        guard processed > 0 else { return }
        if processed == 1 { b = a }
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastPublish >= 1.0 / 30 else { return }
        lastPublish = now
        let levels = VCAudioLevels(rms: max(a.rms, b.rms), bass: max(a.bass, b.bass),
                                  mid: max(a.mid, b.mid), treble: max(a.treble, b.treble), pulse: max(a.pulse, b.pulse))
        onLevels(levels, "\(Int(format.mSampleRate)) Hz · \(format.mChannelsPerFrame)ch · \(format.mBitsPerChannel)bit")
    }
}
