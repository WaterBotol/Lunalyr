import CoreAudio
import Foundation

/// Whether any other process is playing audio, from Core Audio's process
/// objects. It only reads HAL state: no audio IO runs and no permission is
/// needed. Lunalyr's own IO is ignored, so capture cannot keep itself alive.
///
/// Each process's bundle identifier is read once, so capture can also tell
/// whether the followed player itself is playing on this Mac -- Spotify
/// Connect and a player's own AirPlay report "playing" with no sound here.
@MainActor
final class AudioOutputActivityMonitor {
    /// nil until started, or when this system does not report process activity.
    private(set) var isActive: Bool?
    var onChange: (() -> Void)?
    private var listListener: AudioObjectPropertyListenerBlock?
    private struct Process {
        let pid: pid_t
        let bundleID: String?
        let listener: AudioObjectPropertyListenerBlock
        /// As last read by `update`.
        var runningOutput = false
    }
    private var processes: [AudioObjectID: Process] = [:]
    private let ownPID = getpid()

    nonisolated static func anyOtherProcessPlaying(_ processes: [(pid: pid_t, runningOutput: Bool)], ownPID: pid_t) -> Bool {
        processes.contains { $0.pid != ownPID && $0.runningOutput }
    }

    /// Whether a process of the app `bundleID` -- the app itself, or a helper
    /// whose identifier extends it (`com.spotify.client.helper`) -- is
    /// playing. nil when no process reported its identifier at all.
    nonisolated static func app(_ bundleID: String, isPlayingAmong processes: [(bundleID: String?, runningOutput: Bool)]) -> Bool? {
        guard processes.contains(where: { $0.bundleID != nil }) else { return nil }
        return processes.contains { process in
            guard process.runningOutput, let identifier = process.bundleID else { return false }
            return identifier == bundleID || identifier.hasPrefix(bundleID + ".")
        }
    }

    /// Whether the app `bundleID` (or one of its helpers) is playing on this
    /// Mac; nil while stopped or unknown.
    func isRunningOutput(bundleID: String) -> Bool? {
        guard isActive != nil else { return nil }
        return Self.app(bundleID, isPlayingAmong: processes.values.map { ($0.bundleID, $0.runningOutput) })
    }

    func start() {
        guard listListener == nil else { return }
        var address = Self.propertyAddress(kAudioHardwarePropertyProcessObjectList)
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
        guard AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main, listener) == noErr else {
            set(nil)
            return
        }
        listListener = listener
        refresh()
    }

    func stop() {
        if let listListener {
            var address = Self.propertyAddress(kAudioHardwarePropertyProcessObjectList)
            AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main, listListener)
        }
        listListener = nil
        for process in Array(processes.keys) { forget(process) }
        isActive = nil
    }

    /// Follows the current process list, with one listener per process.
    /// Also a cheap backstop the controller polls, should a notification be missed.
    func refresh() {
        guard listListener != nil else { return }
        guard let current = Self.processObjects() else { set(nil); return }
        let alive = Set(current)
        for process in Array(processes.keys) where !alive.contains(process) { forget(process) }
        for process in current where processes[process] == nil {
            guard let pid: pid_t = Self.read(process, kAudioProcessPropertyPID) else { continue }
            var address = Self.propertyAddress(kAudioProcessPropertyIsRunningOutput)
            let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
                MainActor.assumeIsolated { self?.update() }
            }
            if AudioObjectAddPropertyListenerBlock(process, &address, .main, listener) == noErr {
                processes[process] = Process(pid: pid, bundleID: Self.bundleID(of: process), listener: listener)
            }
        }
        update()
    }

    private func forget(_ process: AudioObjectID) {
        guard let entry = processes.removeValue(forKey: process) else { return }
        var address = Self.propertyAddress(kAudioProcessPropertyIsRunningOutput)
        AudioObjectRemovePropertyListenerBlock(process, &address, .main, entry.listener)
    }

    private func update() {
        for process in Array(processes.keys) {
            let running: UInt32 = Self.read(process, kAudioProcessPropertyIsRunningOutput) ?? 0
            processes[process]?.runningOutput = running != 0
        }
        set(Self.anyOtherProcessPlaying(processes.values.map { ($0.pid, $0.runningOutput) }, ownPID: ownPID))
    }

    private func set(_ value: Bool?) {
        guard value != isActive else { return }
        isActive = value
        onChange?()
    }

    private static func propertyAddress(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                   mElement: kAudioObjectPropertyElementMain)
    }
    private static func read<Value>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> Value? {
        var address = propertyAddress(selector)
        var size = UInt32(MemoryLayout<Value>.size)
        let value = UnsafeMutablePointer<Value>.allocate(capacity: 1)
        defer { value.deallocate() }
        guard AudioObjectGetPropertyData(object, &address, 0, nil, &size, value) == noErr,
              size == UInt32(MemoryLayout<Value>.size) else { return nil }
        return value.pointee
    }
    /// The process's bundle identifier, which the caller owns.
    private static func bundleID(of process: AudioObjectID) -> String? {
        var address = propertyAddress(kAudioProcessPropertyBundleID)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(process, &address, 0, nil, &size, &value) == noErr,
              let retained = value else { return nil }
        let identifier = retained.takeRetainedValue() as String
        return identifier.isEmpty ? nil : identifier
    }
    private static func processObjects() -> [AudioObjectID]? {
        var address = propertyAddress(kAudioHardwarePropertyProcessObjectList)
        let system = AudioObjectID(kAudioObjectSystemObject)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr else { return nil }
        var list = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &list) == noErr else { return nil }
        return Array(list.prefix(Int(size) / MemoryLayout<AudioObjectID>.size))
    }
}
