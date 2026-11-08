import AVFoundation
import CoreAudio
import Foundation

// MARK: - Recording the playing song, for a lyric card's sound
//
// Streamed and downloaded Apple Music tracks are protected and cannot be read
// as files, so the card's sound is recorded from what is playing: the same
// private, unmuted system-audio tap the visualisers use (macOS asks once to
// allow audio capture; nothing leaves the Mac, and nothing is muted or
// rerouted). The samples go into memory first, then to a file.

@available(macOS 14.2, *)
final class SongAudioRecorder: @unchecked Sendable {
    /// Longest recording: two minutes.
    static let maximumSeconds = 120.0

    private var tapID: AudioObjectID = 0
    private var deviceID: AudioObjectID = 0
    private var procID: AudioDeviceIOProcID?
    private var format = AudioStreamBasicDescription()
    private var capacity = 0
    private var left: UnsafeMutablePointer<Float>?
    private var right: UnsafeMutablePointer<Float>?
    /// Frames written so far; the IO thread is the only writer.
    private let written = UnsafeMutablePointer<Int>.allocate(capacity: 1)

    var sampleRate: Double { format.mSampleRate }
    var seconds: Double { format.mSampleRate > 0 ? Double(written.pointee) / format.mSampleRate : 0 }

    init() throws {
        written.initialize(to: 0)
        do { try configure() } catch { stop(); throw error }
    }

    deinit {
        stop()
        written.deallocate()
        left?.deallocate()
        right?.deallocate()
    }

    private func check(_ status: OSStatus, _ what: String) throws {
        if status != noErr { throw LyricHTTP.Failure(message: "\(what) failed (Core Audio \(status)).") }
    }

    private func configure() throws {
        let tap = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
        tap.name = "Lunalyr lyric card sound"
        tap.isPrivate = true
        tap.muteBehavior = .unmuted
        try check(AudioHardwareCreateProcessTap(tap, &tapID), "Creating the system-audio tap")

        var address = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        try check(AudioObjectGetPropertyData(tapID, &address, 0, nil, &size, &format), "Reading the tap format")
        guard format.mFormatID == kAudioFormatLinearPCM, format.mBitsPerChannel == 32,
              format.mFormatFlags & kAudioFormatFlagIsFloat != 0, format.mSampleRate > 0 else {
            throw LyricHTTP.Failure(message: "The system audio comes in a form the card can’t record.")
        }

        var outputAddress = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                                       mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var output = AudioDeviceID(0)
        size = UInt32(MemoryLayout<AudioDeviceID>.size)
        try check(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &outputAddress, 0, nil, &size, &output),
                  "Finding the music output")
        guard output != 0 else { throw LyricHTTP.Failure(message: "No audio output is available.") }
        outputAddress.mSelector = kAudioDevicePropertyDeviceUID
        var outputUID: CFString = "" as CFString
        size = UInt32(MemoryLayout<CFString>.size)
        let status = withUnsafeMutablePointer(to: &outputUID) { AudioObjectGetPropertyData(output, &outputAddress, 0, nil, &size, $0) }
        try check(status, "Reading the output identity")
        let description: [String: Any] = [
            kAudioAggregateDeviceNameKey: "Lunalyr card recorder",
            kAudioAggregateDeviceUIDKey: "local.verso.cardaudio.\(UUID().uuidString)",
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID, kAudioSubDeviceInputChannelsKey: 0]],
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapUIDKey: tap.uuid.uuidString, kAudioSubTapDriftCompensationKey: true]],
        ]
        try check(AudioHardwareCreateAggregateDevice(description as CFDictionary, &deviceID), "Creating the private audio device")

        capacity = Int(format.mSampleRate * Self.maximumSeconds)
        left = .allocate(capacity: capacity)
        right = .allocate(capacity: capacity)
        left?.initialize(repeating: 0, count: capacity)
        right?.initialize(repeating: 0, count: capacity)
    }

    func start() async throws {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceIsAlive, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var alive: UInt32 = 0
        for _ in 0..<30 {
            var size = UInt32(MemoryLayout<UInt32>.size)
            try check(AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, &alive), "Waiting for the audio device")
            if alive != 0 { break }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        guard alive != 0 else { throw LyricHTTP.Failure(message: "The audio device did not become ready.") }
        let planar = format.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
        let channels = Int(format.mChannelsPerFrame)
        let capacity = self.capacity, written = self.written
        let leftSamples = self.left!, rightSamples = self.right!
        try check(AudioDeviceCreateIOProcIDWithBlock(&procID, deviceID, nil) { _, input, _, _, _ in
            // Real-time thread: copy into the preallocated arrays, nothing else.
            let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))
            var total = 0
            for buffer in buffers { total += Int(buffer.mNumberChannels) }
            let first = max(0, total - channels)
            var frames = Int.max
            var channelIndex = 0
            var leftSource: UnsafePointer<Float>?, rightSource: UnsafePointer<Float>?
            var stride = 1
            for buffer in buffers {
                guard let data = buffer.mData?.assumingMemoryBound(to: Float.self) else { continue }
                let count = Int(buffer.mNumberChannels)
                if planar {
                    if channelIndex >= first {
                        if leftSource == nil { leftSource = UnsafePointer(data) } else if rightSource == nil { rightSource = UnsafePointer(data) }
                    }
                    frames = min(frames, Int(buffer.mDataByteSize) / MemoryLayout<Float>.size)
                } else {
                    let skip = max(0, first - channelIndex)
                    if skip < count {
                        leftSource = UnsafePointer(data) + skip
                        rightSource = skip + 1 < count ? UnsafePointer(data) + skip + 1 : leftSource
                        stride = count
                    }
                    frames = min(frames, Int(buffer.mDataByteSize) / (MemoryLayout<Float>.size * count))
                }
                channelIndex += count
            }
            guard let leftSource, frames != Int.max else { return }
            let rightFrom = rightSource ?? leftSource
            let at = written.pointee
            let room = max(0, capacity - at)
            let count = min(frames, room)
            for i in 0..<count {
                leftSamples[at + i] = leftSource[i * stride]
                rightSamples[at + i] = rightFrom[i * stride]
            }
            written.pointee = at + count
        }, "Connecting the audio callback")
        try check(AudioDeviceStart(deviceID, procID), "Starting the recording")
    }

    /// Stops and writes what was heard to a file, or returns nil if nothing was.
    func finish(to url: URL) throws -> URL? {
        stop()
        let frames = written.pointee
        guard frames > Int(format.mSampleRate * 0.5), let left, let right else { return nil }
        guard let audioFormat = AVAudioFormat(standardFormatWithSampleRate: format.mSampleRate, channels: 2),
              let buffer = AVAudioPCMBuffer(pcmFormat: audioFormat, frameCapacity: AVAudioFrameCount(frames)),
              let channels = buffer.floatChannelData else { return nil }
        buffer.frameLength = AVAudioFrameCount(frames)
        channels[0].update(from: left, count: frames)
        channels[1].update(from: right, count: frames)
        // Silence means the tap heard nothing (permission refused, or paused).
        var peak: Float = 0
        for i in stride(from: 0, to: frames, by: 16) { peak = max(peak, abs(left[i]), abs(right[i])) }
        guard peak > 0.0005 else { return nil }
        var file: AVAudioFile? = try AVAudioFile(forWriting: url, settings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: format.mSampleRate, AVNumberOfChannelsKey: 2,
            AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false,
        ])
        try file?.write(from: buffer)
        file = nil
        return url
    }

    func stop() {
        let device = deviceID, tap = tapID, proc = procID
        procID = nil; deviceID = 0; tapID = 0
        if device != 0, let proc {
            AudioDeviceStop(device, proc)
            AudioDeviceDestroyIOProcID(device, proc)
        }
        if device != 0 { AudioHardwareDestroyAggregateDevice(device) }
        if tap != 0 { AudioHardwareDestroyProcessTap(tap) }
    }
}
