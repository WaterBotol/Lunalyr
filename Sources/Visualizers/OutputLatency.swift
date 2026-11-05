import CoreAudio
import Foundation

/// The default output device's presentation latency, re-read whenever the
/// default device, its latency or its sample rate changes.
///
/// System-audio capture taps the mix before it reaches the device, so what
/// the listener hears trails the capture by the device's latency: a few
/// milliseconds on built-in speakers, 100–250 ms on Bluetooth headphones.
/// `VisualizerAudio` delays the visuals by whatever part of that the analysis
/// and drawing do not already take.
final class OutputLatencyMonitor: @unchecked Sendable {
    static let shared = OutputLatencyMonitor()

    private let queue = DispatchQueue(label: "local.verso.output-latency")
    private let lock = NSLock()
    private var seconds: Double = 0
    // Touched only on `queue`.
    private var started = false
    private var device = AudioObjectID(kAudioObjectUnknown)
    private var systemListener: AudioObjectPropertyListenerBlock?
    private var deviceListener: AudioObjectPropertyListenerBlock?

    private static let deviceSelectors = [kAudioDevicePropertyLatency, kAudioDevicePropertySafetyOffset,
                                          kAudioDevicePropertyBufferFrameSize, kAudioDevicePropertyNominalSampleRate,
                                          kAudioDevicePropertyStreams]

    /// The current output latency in seconds; 0 until measured.
    var latency: Double {
        lock.lock(); defer { lock.unlock() }
        return seconds
    }

    /// Starts listening (once) and measures the current device.
    func start() {
        queue.async { [self] in
            guard !started else { return }
            started = true
            let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.refresh() }
            var address = Self.address(kAudioHardwarePropertyDefaultOutputDevice, scope: kAudioObjectPropertyScopeGlobal)
            if AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, queue, listener) == noErr {
                systemListener = listener
            }
            refresh()
        }
    }

    /// Runs on `queue`: follows the default device and re-measures it.
    private func refresh() {
        let current = Self.defaultOutputDevice() ?? AudioObjectID(kAudioObjectUnknown)
        if current != device {
            if let deviceListener, device != kAudioObjectUnknown {
                for selector in Self.deviceSelectors {
                    var address = Self.listenerAddress(selector)
                    AudioObjectRemovePropertyListenerBlock(device, &address, queue, deviceListener)
                }
            }
            device = current
            deviceListener = nil
            if current != kAudioObjectUnknown {
                let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.refresh() }
                for selector in Self.deviceSelectors {
                    var address = Self.listenerAddress(selector)
                    AudioObjectAddPropertyListenerBlock(current, &address, queue, listener)
                }
                deviceListener = listener
            }
        }
        let measured = current == kAudioObjectUnknown ? 0 : Self.outputLatency(of: current)
        lock.lock(); seconds = measured; lock.unlock()
    }

    /// Device latency, safety offset, IO buffer and the first output stream's
    /// latency, in seconds at the device's nominal rate.
    static func outputLatency(of device: AudioObjectID) -> Double {
        var rate: Float64 = 0
        var rateSize = UInt32(MemoryLayout<Float64>.size)
        var rateAddress = address(kAudioDevicePropertyNominalSampleRate, scope: kAudioObjectPropertyScopeGlobal)
        guard AudioObjectGetPropertyData(device, &rateAddress, 0, nil, &rateSize, &rate) == noErr else { return 0 }
        var stream: UInt32 = 0
        var streamsAddress = address(kAudioDevicePropertyStreams, scope: kAudioObjectPropertyScopeOutput)
        var streamsSize: UInt32 = 0
        if AudioObjectGetPropertyDataSize(device, &streamsAddress, 0, nil, &streamsSize) == noErr,
           streamsSize >= UInt32(MemoryLayout<AudioStreamID>.size) {
            var streams = [AudioStreamID](repeating: 0, count: Int(streamsSize) / MemoryLayout<AudioStreamID>.size)
            if AudioObjectGetPropertyData(device, &streamsAddress, 0, nil, &streamsSize, &streams) == noErr, let first = streams.first {
                stream = frames(kAudioStreamPropertyLatency, of: first, scope: kAudioObjectPropertyScopeGlobal)
            }
        }
        return totalLatency(device: frames(kAudioDevicePropertyLatency, of: device, scope: kAudioObjectPropertyScopeOutput),
                            safetyOffset: frames(kAudioDevicePropertySafetyOffset, of: device, scope: kAudioObjectPropertyScopeOutput),
                            buffer: frames(kAudioDevicePropertyBufferFrameSize, of: device, scope: kAudioObjectPropertyScopeGlobal),
                            stream: stream, sampleRate: rate)
    }

    static func totalLatency(device: UInt32, safetyOffset: UInt32, buffer: UInt32, stream: UInt32, sampleRate: Double) -> Double {
        guard sampleRate.isFinite, sampleRate > 0 else { return 0 }
        let frames = UInt64(device) + UInt64(safetyOffset) + UInt64(buffer) + UInt64(stream)
        return Double(frames) / sampleRate
    }

    private static func defaultOutputDevice() -> AudioObjectID? {
        var device = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var address = address(kAudioHardwarePropertyDefaultOutputDevice, scope: kAudioObjectPropertyScopeGlobal)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device) == noErr,
              device != kAudioObjectUnknown else { return nil }
        return device
    }

    private static func frames(_ selector: AudioObjectPropertySelector, of object: AudioObjectID,
                               scope: AudioObjectPropertyScope) -> UInt32 {
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        var address = address(selector, scope: scope)
        return AudioObjectGetPropertyData(object, &address, 0, nil, &size, &value) == noErr ? value : 0
    }

    private static func address(_ selector: AudioObjectPropertySelector, scope: AudioObjectPropertyScope) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }

    /// Listeners use wildcards: these properties live in different scopes.
    private static func listenerAddress(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeWildcard,
                                   mElement: kAudioObjectPropertyElementWildcard)
    }
}
