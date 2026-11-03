import AppKit
import Combine
import CoreAudio
import Foundation

/// Capture intent, delivered PCM and audible signal are separate states.
@MainActor
final class AudioReactiveController: ObservableObject {
    static let shared = AudioReactiveController()
    @Published private(set) var running = false
    @Published private(set) var starting = false
    @Published private(set) var status = "Live audio is off. Lyrics drive the background."
    @Published private(set) var levels = VCAudioLevels()
    @Published private(set) var packetsReceived = 0
    @Published private(set) var formatLabel = ""
    @Published private(set) var methodLabel = ""
    private var capture: AudioCaptureSession?
    private var startTask: Task<Void, Never>?
    private var routeTask: Task<Void, Never>?
    private var healthTimer: Timer?
    private var outputListener: AudioObjectPropertyListenerBlock?
    private var wakeObserver: NSObjectProtocol?
    private var visual = VCAudioVisualState()
    private var ticket = UUID()
    private var startedAt = 0.0, lastSample = 0.0, lastVisual = 0.0
    private var quietSince: Double?
    private var formatProblem: String?
    private var usingTap = false
    private var recoveryNote = ""

    func resumeIfEnabled() {
        if Settings.shared.audioReactiveEnabled { start() }
    }
    func start() {
        guard !running, !starting else { return }
        Settings.shared.audioReactiveEnabled = true
        installAudioObservers()
        connect(compatibility: false)
    }
    private func connect(compatibility: Bool, reason: String = "") {
        ticket = UUID(); startTask?.cancel()
        healthTimer?.invalidate(); healthTimer = nil
        capture?.stop(); capture = nil
        let generation = ticket
        starting = true; running = false; status = reason.isEmpty ? "Connecting audio…" : reason
        recoveryNote = reason
        packetsReceived = 0; formatLabel = ""; formatProblem = nil; quietSince = nil
        levels = VCAudioLevels(); visual = VCAudioVisualState()
        lastSample = 0; lastVisual = 0; startedAt = ProcessInfo.processInfo.systemUptime
        let method = Settings.shared.audioCaptureMethod
        usingTap = !compatibility && method != .screenRecording
        if #unavailable(macOS 14.2) { usingTap = false }
        let processor = AudioPCMProcessor(onLevels: { [weak controller = self] value, format in
            Task { @MainActor [weak controller] in
                guard let controller, controller.ticket == generation else { return }
                let now = ProcessInfo.processInfo.systemUptime
                let dt = controller.lastVisual == 0 ? 1.0 / 30 : now - controller.lastVisual
                controller.lastVisual = now; controller.lastSample = now
                controller.levels = value; controller.packetsReceived += 1
                if controller.formatLabel != format { controller.formatLabel = format }
                controller.visual = vc_audio_visual(&controller.visual, value, Settings.shared.audioSensitivity, dt)
                controller.updateHealth()
            }
        }, onProblem: { [weak controller = self] message in
            Task { @MainActor [weak controller] in
                guard let controller, controller.ticket == generation else { return }
                controller.formatProblem = message; controller.updateHealth()
            }
        })
        startTask = Task { [weak self] in
            guard let self else { return }
            do {
                let session: AudioCaptureSession
                if usingTap, #available(macOS 14.2, *) {
                    methodLabel = "System audio"
                    let tap = try SystemAudioCapture(processor: processor)
                    do { try await tap.start() } catch { tap.stop(); throw error }
                    session = tap
                } else {
                    if method == .systemAudio {
                        throw LyricHTTP.Failure(message: "System-audio taps need macOS 14.2 or later. Choose compatibility capture.")
                    }
                    methodLabel = compatibility ? "Compatibility · automatic recovery" : "Screen recording compatibility"
                    let screen = ScreenAudioCapture(processor: processor, onError: { [weak controller = self] error in
                        Task { @MainActor [weak controller] in
                            guard let controller, controller.ticket == generation else { return }
                            controller.stop(disablePreference: false, message: "Audio stopped: \(error.localizedDescription) Reconnect to retry.")
                        }
                    })
                    do { try await screen.start() } catch { screen.stop(); throw error }
                    session = screen
                }
                guard ticket == generation, !Task.isCancelled else { session.stop(); return }
                capture = session; starting = false; running = true
                startedAt = ProcessInfo.processInfo.systemUptime
                let timer = Timer(timeInterval: 0.5, repeats: true) { [weak controller = self] _ in
                    Task { @MainActor [weak controller] in controller?.updateHealth() }
                }
                healthTimer = timer; RunLoop.main.add(timer, forMode: .common)
                updateHealth()
            } catch {
                guard ticket == generation, !Task.isCancelled else { return }
                if method == .automatic && usingTap {
                    connect(compatibility: true, reason: "System audio could not connect. Trying compatibility capture; allow screen-recording access if asked…")
                } else {
                    stop(disablePreference: false, message: "Couldn’t start audio: \(error.localizedDescription) Check permission for this app, then Reconnect.")
                }
            }
        }
    }
    func reconnect() {
        stop(disablePreference: false)
        start()
    }
    func stop(disablePreference: Bool = true, message: String = "Live audio is off. Lyrics drive the background.") {
        ticket = UUID(); startTask?.cancel(); startTask = nil
        routeTask?.cancel(); routeTask = nil
        healthTimer?.invalidate(); healthTimer = nil
        capture?.stop(); capture = nil
        removeAudioObservers()
        running = false; starting = false
        levels = VCAudioLevels(); visual = VCAudioVisualState(); packetsReceived = 0
        lastSample = 0; lastVisual = 0; quietSince = nil
        if disablePreference { Settings.shared.audioReactiveEnabled = false }
        status = message
    }
    private func updateHealth() {
        guard running || starting else { return }
        let now = ProcessInfo.processInfo.systemUptime
        let playing = NowPlayingMonitor.shared.isPlaying
        if playing && levels.rms < 0.00015 {
            if quietSince == nil { quietSince = now }
        } else { quietSince = nil }
        let shouldRecover = AudioCaptureRecovery.shouldUseCompatibility(method: Settings.shared.audioCaptureMethod,
            usingTap: usingTap, elapsed: now - startedAt, packets: packetsReceived,
            packetAge: lastSample > 0 ? now - lastSample : 0,
            quietFor: quietSince.map { now - $0 } ?? 0, playing: playing)
        if running && (shouldRecover || (formatProblem != nil && usingTap && Settings.shared.audioCaptureMethod == .automatic)) {
            connect(compatibility: true, reason: "System audio delivered no usable sound. Trying compatibility capture; allow screen-recording access if asked…")
            return
        }
        let message: String
        if let formatProblem { message = formatProblem }
        else if packetsReceived == 0 {
            message = now - startedAt > 3
                ? "No audio samples arrived. Reconnect after granting access, or choose compatibility capture."
                : (recoveryNote.isEmpty ? "Connected; waiting for audio samples…" : recoveryNote)
        } else if now - lastSample > 1.5 {
            message = "The audio stream stopped delivering samples. Reconnect to restart it."
        } else if levels.rms < 0.00015 {
            message = "Samples are arriving, but they’re silent. If music is audible, check permission for this app and Reconnect."
        } else { message = "Receiving audio · \(methodLabel). Analysed locally; no recording is saved." }
        if status != message { status = message }
    }
    private func installAudioObservers() {
        guard outputListener == nil else { return }
        let listener: AudioObjectPropertyListenerBlock = { [weak controller = self] _, _ in
            Task { @MainActor [weak controller] in controller?.scheduleRouteRecovery() }
        }
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        if AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main, listener) == noErr {
            outputListener = listener
        }
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.screensDidWakeNotification,
            object: nil, queue: .main) { [weak controller = self] _ in
                Task { @MainActor [weak controller] in controller?.scheduleRouteRecovery() }
            }
    }
    private func removeAudioObservers() {
        if let outputListener {
            var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main, outputListener)
        }
        outputListener = nil
        if let wakeObserver { NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver) }
        wakeObserver = nil
    }
    private func scheduleRouteRecovery() {
        guard Settings.shared.audioReactiveEnabled else { return }
        routeTask?.cancel()
        routeTask = Task { [weak self] in
            do { try await Task.sleep(nanoseconds: 450_000_000) } catch { return }
            guard let self, !Task.isCancelled, Settings.shared.audioReactiveEnabled else { return }
            connect(compatibility: false, reason: "Audio output changed; reconnecting…")
        }
    }
    func reactivity(fallback: @autoclosure () -> MusicDynamics.Reactivity) -> MusicDynamics.Reactivity {
        guard running else { return Settings.shared.audioReactiveEnabled ? .init(swell: 0, pulse: 0) : fallback() }
        let fresh = lastSample > 0 ? exp(-max(0, ProcessInfo.processInfo.systemUptime - lastSample - 0.15) / 0.4) : 0
        let strength = Settings.shared.audioMotionStrength * fresh
        return .init(swell: visual.swell * strength, pulse: visual.pulse * strength,
                     bass: visual.bass * strength, mid: visual.mid * strength, treble: visual.treble * strength)
    }
    func openPrivacySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }
    func copyDiagnostics() {
        let settings = Settings.shared
        let report = """
        Verso Custom audio · \(ProcessInfo.processInfo.operatingSystemVersionString)
        Requested: \(settings.audioCaptureMethod.title); active: \(methodLabel)
        Running: \(running); connecting: \(starting); packets: \(packetsReceived)
        Format: \(formatLabel)
        RMS: \(levels.rms); bass: \(levels.bass); mid: \(levels.mid); treble: \(levels.treble)
        Sensitivity: \(settings.audioSensitivity); motion: \(settings.audioMotionStrength); response: \(settings.beatResponse)
        Status: \(status)
        """
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(report, forType: .string)
    }
}
