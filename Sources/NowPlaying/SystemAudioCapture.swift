import CoreAudio
import Foundation

enum AudioCaptureMethod: String, CaseIterable, Identifiable {
    case automatic, systemAudio, screenRecording
    var id: String { rawValue }
    var title: String {
        switch self {
        case .automatic: return "Automatic"
        case .systemAudio: return "System audio"
        case .screenRecording: return "Screen recording compatibility"
        }
    }
}

protocol AudioCaptureSession: AnyObject { func stop() }

/// A private, unmuted process tap. It never changes the output volume or
/// routes playback away from the speakers, and it never captures a microphone.
@available(macOS 14.2, *)
final class SystemAudioCapture: AudioCaptureSession {
    private var tapID: AudioObjectID = 0
    private var deviceID: AudioObjectID = 0
    private var procID: AudioDeviceIOProcID?
    private var streamFormat = AudioStreamBasicDescription()
    private let processor: AudioPCMProcessor

    init(processor: AudioPCMProcessor) throws {
        self.processor = processor
        do { try configure() } catch { stop(); throw error }
    }
    private func check(_ status: OSStatus, _ operation: String) throws {
        if status != noErr { throw LyricHTTP.Failure(message: "\(operation) failed (Core Audio \(status)).") }
    }
    private func configure() throws {
        let tap = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
        tap.name = "Verso Custom music response"
        tap.isPrivate = true
        tap.muteBehavior = .unmuted
        try check(AudioHardwareCreateProcessTap(tap, &tapID), "Creating the system-audio tap")

        var address = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat,
            mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        try check(AudioObjectGetPropertyData(tapID, &address, 0, nil, &size, &streamFormat), "Reading the tap format")
        guard AudioPCMProcessor.sampleFormat(streamFormat) != nil else {
            throw LyricHTTP.Failure(message: "The system-audio tap returned an unsupported PCM format. Try compatibility capture.")
        }
        // A physical output supplies a stable clock. A tap-only aggregate can
        // be created successfully yet never deliver sound on some routes.
        address.mSelector = kAudioHardwarePropertyDefaultOutputDevice
        var output = AudioDeviceID(0)
        size = UInt32(MemoryLayout<AudioDeviceID>.size)
        try check(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &output), "Finding the music output")
        guard output != 0 else { throw LyricHTTP.Failure(message: "No audio output is available.") }
        address.mSelector = kAudioDevicePropertyDeviceUID
        var outputUID: CFString = "" as CFString
        size = UInt32(MemoryLayout<CFString>.size)
        let uidStatus = withUnsafeMutablePointer(to: &outputUID) {
            AudioObjectGetPropertyData(output, &address, 0, nil, &size, $0)
        }
        try check(uidStatus, "Reading the output identity")
        let description: [String: Any] = [
            kAudioAggregateDeviceNameKey: "Verso Custom private analyser",
            kAudioAggregateDeviceUIDKey: "local.verso.audio.\(UUID().uuidString)",
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID,
                                                    kAudioSubDeviceInputChannelsKey: 0]],
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapUIDKey: tap.uuid.uuidString,
                                               kAudioSubTapDriftCompensationKey: true]]
        ]
        try check(AudioHardwareCreateAggregateDevice(description as CFDictionary, &deviceID), "Creating the private audio device")
    }
    func start() async throws {
        // HAL device creation is asynchronous. Yield instead of blocking the
        // UI, and do not start an aggregate which has not become alive.
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceIsAlive,
            mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var alive: UInt32 = 0
        for _ in 0..<30 {
            try Task.checkCancellation()
            var size = UInt32(MemoryLayout<UInt32>.size)
            try check(AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, &alive), "Waiting for the audio device")
            if alive != 0 { break }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        guard alive != 0 else { throw LyricHTTP.Failure(message: "The audio device did not become ready.") }
        let input = processor
        let format = streamFormat
        try check(AudioDeviceCreateIOProcIDWithBlock(&procID, deviceID, input.queue) { _, buffers, _, _, _ in
            // Taps are appended after physical sub-device inputs. Read only
            // the tap's final channels, even on a headset with microphone IO.
            input.process(buffers, format: format, lastChannelsOnly: true)
        }, "Connecting the audio callback")
        try check(AudioDeviceStart(deviceID, procID), "Starting system audio")
    }
    func stop() {
        if deviceID != 0, let procID {
            AudioDeviceStop(deviceID, procID)
            AudioDeviceDestroyIOProcID(deviceID, procID)
        }
        procID = nil
        if deviceID != 0 { AudioHardwareDestroyAggregateDevice(deviceID); deviceID = 0 }
        if tapID != 0 { AudioHardwareDestroyProcessTap(tapID); tapID = 0 }
    }
    deinit { stop() }
}
