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

protocol AudioCaptureSession: AnyObject {
    func stop()
    /// Whether IO can stop while the session is kept for a quick `resume`.
    /// Otherwise an idle session is stopped and demand starts a new one.
    var canSuspend: Bool { get }
    /// Stops IO without blocking the caller. `completion` runs on the main
    /// queue, with false if the session should be stopped instead.
    func suspend(completion: @escaping (Bool) -> Void)
    /// Restarts IO without blocking the caller. `completion` runs on the
    /// main queue, with the error if IO could not restart.
    func resume(completion: @escaping (Error?) -> Void)
}

extension AudioCaptureSession {
    var canSuspend: Bool { false }
    func suspend(completion: @escaping (Bool) -> Void) { DispatchQueue.main.async { completion(false) } }
    func resume(completion: @escaping (Error?) -> Void) {
        DispatchQueue.main.async { completion(LyricHTTP.Failure(message: "This capture method can’t resume.")) }
    }
}

/// A private, unmuted process tap. It never changes the output volume or
/// routes playback away from the speakers, and it never captures a microphone.
@available(macOS 14.2, *)
final class SystemAudioCapture: AudioCaptureSession {
    private var tapID: AudioObjectID = 0
    private var deviceID: AudioObjectID = 0
    private var outputID: AudioObjectID = 0
    private var outputRate = 0.0
    private var procID: AudioDeviceIOProcID?
    private var streamFormat = AudioStreamBasicDescription()
    private let processor: AudioPCMProcessor
    private let onFormatChange: () -> Void
    private var listeners: [(object: AudioObjectID, selector: AudioObjectPropertySelector,
                             block: AudioObjectPropertyListenerBlock)] = []
    /// Starting and stopping device IO, and destroying the devices, can take
    /// tens of milliseconds (longer on Bluetooth routes). It runs here, off
    /// the main thread and in the order it was asked for.
    private static let control = DispatchQueue(label: "local.verso.audio-device", qos: .userInitiated)

    /// `onFormatChange` runs on the main queue when the tap's format or the
    /// output's sample rate no longer matches the one being decoded.
    init(processor: AudioPCMProcessor, onFormatChange: @escaping () -> Void = {}) throws {
        self.processor = processor
        self.onFormatChange = onFormatChange
        do { try configure() } catch { stop(); throw error }
    }
    private func check(_ status: OSStatus, _ operation: String) throws {
        if status != noErr { throw LyricHTTP.Failure(message: "\(operation) failed (Core Audio \(status)).") }
    }
    private func configure() throws {
        let tap = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
        tap.name = "Lunalyr music response"
        tap.isPrivate = true
        tap.muteBehavior = .unmuted
        try check(AudioHardwareCreateProcessTap(tap, &tapID), "Creating the system-audio tap")

        guard let format = Self.tapFormat(tapID) else {
            throw LyricHTTP.Failure(message: "Reading the tap format failed.")
        }
        streamFormat = format
        guard AudioPCMProcessor.sampleFormat(streamFormat) != nil else {
            throw LyricHTTP.Failure(message: "The system-audio tap returned an unsupported PCM format. Try compatibility capture.")
        }
        // A physical output supplies a stable clock. A tap-only aggregate can
        // be created successfully yet never deliver sound on some routes.
        var address = Self.propertyAddress(kAudioHardwarePropertyDefaultOutputDevice)
        var output = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        try check(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &output), "Finding the music output")
        guard output != 0 else { throw LyricHTTP.Failure(message: "No audio output is available.") }
        outputID = output
        outputRate = Self.nominalRate(output) ?? 0
        address.mSelector = kAudioDevicePropertyDeviceUID
        var outputUID: CFString = "" as CFString
        size = UInt32(MemoryLayout<CFString>.size)
        let uidStatus = withUnsafeMutablePointer(to: &outputUID) {
            AudioObjectGetPropertyData(output, &address, 0, nil, &size, $0)
        }
        try check(uidStatus, "Reading the output identity")
        let description: [String: Any] = [
            kAudioAggregateDeviceNameKey: "Lunalyr private analyser",
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
        // The IO block decodes with the format read above. Reconnect rather
        // than misread samples after a sample-rate or layout change.
        listen(tapID, kAudioTapPropertyFormat)
        listen(outputID, kAudioDevicePropertyNominalSampleRate)
    }
    func start() async throws {
        // HAL device creation is asynchronous. Yield instead of blocking the
        // UI, and do not start an aggregate which has not become alive.
        var address = Self.propertyAddress(kAudioDevicePropertyDeviceIsAlive)
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
        // No dispatch queue: the HAL calls the block directly on its real-time
        // IO thread, which only copies samples into the processor's ring.
        try check(AudioDeviceCreateIOProcIDWithBlock(&procID, deviceID, nil) { _, buffers, _, _, _ in
            // Taps are appended after physical sub-device inputs. Read only
            // the tap's final channels, even on a headset with microphone IO.
            input.process(buffers, format: format, lastChannelsOnly: true)
        }, "Connecting the audio callback")
        if let procID { disableOutputStreams(for: procID) }
        try check(AudioDeviceStart(deviceID, procID), "Starting system audio")
    }
    // Suspend, resume and stop are called on the main thread, once `start`
    // has returned; the device work itself runs on `control`.
    var canSuspend: Bool { deviceID != 0 && procID != nil }
    func suspend(completion: @escaping (Bool) -> Void) {
        guard deviceID != 0, let procID else { DispatchQueue.main.async { completion(false) }; return }
        let device = deviceID
        Self.control.async {
            let stopped = AudioDeviceStop(device, procID) == noErr
            DispatchQueue.main.async { completion(stopped) }
        }
    }
    func resume(completion: @escaping (Error?) -> Void) {
        guard deviceID != 0, let procID else {
            DispatchQueue.main.async { completion(LyricHTTP.Failure(message: "The system-audio device is no longer available.")) }
            return
        }
        let device = deviceID
        Self.control.async {
            let status = AudioDeviceStart(device, procID)
            let failure = status == noErr ? nil
                : LyricHTTP.Failure(message: "Restarting system audio failed (Core Audio \(status)).")
            DispatchQueue.main.async { completion(failure) }
        }
    }
    func stop() {
        for listener in listeners {
            var address = Self.propertyAddress(listener.selector)
            AudioObjectRemovePropertyListenerBlock(listener.object, &address, .main, listener.block)
        }
        listeners.removeAll()
        let device = deviceID, tap = tapID, proc = procID
        procID = nil; deviceID = 0; tapID = 0
        guard device != 0 || tap != 0 else { return }
        // After any suspend or resume still under way, never alongside it.
        Self.control.async {
            if device != 0, let proc {
                AudioDeviceStop(device, proc)
                AudioDeviceDestroyIOProcID(device, proc)
            }
            if device != 0 { AudioHardwareDestroyAggregateDevice(device) }
            if tap != 0 { AudioHardwareDestroyProcessTap(tap) }
        }
    }
    deinit { stop() }

    /// The analyser only reads input. Without this, the IO proc also feeds
    /// (silent) output streams, keeping the speakers or headphones busy.
    private func disableOutputStreams(for procID: AudioDeviceIOProcID) {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyIOProcStreamUsage,
            mScope: kAudioObjectPropertyScopeOutput, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(deviceID, &address, 0, nil, &size) == noErr,
              Int(size) >= Self.streamUsageHeader else { return }
        let bytes = max(Int(size), MemoryLayout<AudioHardwareIOProcStreamUsage>.size)
        let usage = UnsafeMutableRawPointer.allocate(byteCount: bytes, alignment: MemoryLayout<AudioHardwareIOProcStreamUsage>.alignment)
        defer { usage.deallocate() }
        usage.initializeMemory(as: UInt8.self, repeating: 0, count: bytes)
        usage.storeBytes(of: unsafeBitCast(procID, to: UnsafeMutableRawPointer.self), as: UnsafeMutableRawPointer.self)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, usage) == noErr,
              Self.disableAllStreams(in: usage, byteCount: Int(size)) else { return }
        // Best effort: capture still works if the HAL declines.
        AudioObjectSetPropertyData(deviceID, &address, 0, nil, size, usage)
    }

    /// Byte offset of AudioHardwareIOProcStreamUsage.mStreamIsOn.
    static let streamUsageHeader = MemoryLayout<UnsafeMutableRawPointer>.size + MemoryLayout<UInt32>.size
    /// Turns every stream off in a stream-usage record of `byteCount` bytes.
    /// Returns false if there is nothing to change or the record is malformed.
    static func disableAllStreams(in usage: UnsafeMutableRawPointer, byteCount: Int) -> Bool {
        guard byteCount >= streamUsageHeader else { return false }
        let count = Int(usage.load(fromByteOffset: MemoryLayout<UnsafeMutableRawPointer>.size, as: UInt32.self))
        guard count > 0, streamUsageHeader + count * MemoryLayout<UInt32>.size <= byteCount else { return false }
        for stream in 0..<count {
            usage.storeBytes(of: UInt32(0), toByteOffset: streamUsageHeader + stream * MemoryLayout<UInt32>.size, as: UInt32.self)
        }
        return true
    }

    /// Whether a new tap format would be decoded differently.
    static func formatChanged(_ old: AudioStreamBasicDescription, _ new: AudioStreamBasicDescription) -> Bool {
        old.mSampleRate != new.mSampleRate || old.mFormatID != new.mFormatID || old.mFormatFlags != new.mFormatFlags
            || old.mChannelsPerFrame != new.mChannelsPerFrame || old.mBitsPerChannel != new.mBitsPerChannel
            || old.mBytesPerFrame != new.mBytesPerFrame
    }

    private func listen(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) {
        guard object != 0 else { return }
        var address = Self.propertyAddress(selector)
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.formatMayHaveChanged() }
        if AudioObjectAddPropertyListenerBlock(object, &address, .main, block) == noErr {
            listeners.append((object, selector, block))
        }
    }
    /// Notifications can repeat or arrive without a change, so compare with
    /// what this session decodes; an unchanged session never reconnects.
    private func formatMayHaveChanged() {
        let tapChanged = Self.tapFormat(tapID).map { Self.formatChanged(streamFormat, $0) } ?? false
        let rateChanged = Self.nominalRate(outputID).map { $0 != outputRate } ?? false
        if tapChanged || rateChanged { onFormatChange() }
    }

    private static func propertyAddress(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                   mElement: kAudioObjectPropertyElementMain)
    }
    private static func tapFormat(_ tap: AudioObjectID) -> AudioStreamBasicDescription? {
        guard tap != 0 else { return nil }
        var address = propertyAddress(kAudioTapPropertyFormat)
        var format = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        guard AudioObjectGetPropertyData(tap, &address, 0, nil, &size, &format) == noErr else { return nil }
        return format
    }
    private static func nominalRate(_ device: AudioObjectID) -> Double? {
        guard device != 0 else { return nil }
        var address = propertyAddress(kAudioDevicePropertyNominalSampleRate)
        var rate = Float64(0)
        var size = UInt32(MemoryLayout<Float64>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &rate) == noErr else { return nil }
        return rate
    }
}
