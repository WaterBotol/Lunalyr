import CoreGraphics
import AppKit
import Combine
import CoreAudio
import Foundation
import IOKit.ps

/// Everything System audio follows. The controller's observers deliver
/// these to `AudioReactiveController.handle(_:)`; tests deliver them directly.
enum AudioCaptureEvent: Equatable {
    case settingsChanged
    case displaysSlept, displaysWoke
    case systemWillSleep, systemDidWake
    case sessionResigned, sessionActivated
    /// A surface that could show a visualiser came or went, or started or
    /// stopped drawing.
    case surfacesChanged
    case powerSourceChanged
    case musicPlaying(Bool)
    case outputActivityChanged
    case outputRouteChanged
    case coreAudioRestarted
}

/// Capture intent, delivered PCM and audible signal are separate states.
///
/// Capture runs only while it is useful (see `AudioCaptureGate`): switched
/// on, a visualiser is the effective wallpaper and is drawing -- on a
/// wallpaper Pause wallpaper has not frozen, or in the Settings preview, by
/// the rule their frame clocks follow -- and audio is playing.
/// Otherwise its IO stops, so the output device can idle and the Mac can
/// sleep. The user's switch is never changed by power limits.
@MainActor
final class AudioReactiveController: ObservableObject {
    static let shared = AudioReactiveController()
    nonisolated static let offMessage = "Choose a visualiser and enable system audio to follow your music."
    /// A slow backstop for a missed process notification.
    static let activityPollInterval = 10.0

    /// Capture IO is running.
    @Published private(set) var running = false
    @Published private(set) var starting = false
    @Published private(set) var status = AudioReactiveController.offMessage
    @Published private(set) var state: AudioCaptureState = .off
    @Published private(set) var levels = VCAudioLevels()
    @Published private(set) var packetsReceived = 0
    @Published private(set) var formatLabel = ""
    @Published private(set) var methodLabel = ""
    /// Whether the latest capture attempt used the system-audio tap.
    @Published private(set) var usingTap = false

    private let environment: AudioCaptureEnvironment
    private let outputActivity: AudioOutputActivitySource
    private var capture: AudioCaptureSession?
    private var processor: AudioPCMProcessor?
    /// A tap session whose IO is stopped while nothing needs it.
    private var suspended = false
    /// Numbers suspend and resume requests; only the latest one's outcome counts.
    private var deviceRequest = 0
    private var startTask: Task<Void, Never>?
    private var routeTimer: AnyCancellable?
    private var wakeTimer: AnyCancellable?
    private var recheck: AnyCancellable?
    private var healthTimer: AnyCancellable?
    private var startupWatchdog: AnyCancellable?
    private var ticket = UUID()
    /// The capture method the current session was started with.
    private var sessionMethod: AudioCaptureMethod = .automatic
    private var startedAt = 0.0, lastSample = 0.0
    private var lastAudible: Double?
    private var packetBaseline = 0
    private var droppedFrames = 0
    private var quietSince: Double?
    private var formatProblem: String?
    private var recoveryNote = ""
    /// The next connection was asked for by the user, so it may show a prompt.
    private var pendingUserStart = false
    private var userStartedAt: Double?
    /// Recovery reached compatibility capture; keep using it this session.
    private var preferCompatibility = false
    /// A failure is shown. Retry when the user asks, the route changes, the
    /// Mac wakes or playback starts again.
    private var blocked = false
    private var screenCaptureAllowed: Bool?

    private var settingsSubscription: AnyCancellable?
    private var surfaceSubscription: AnyCancellable?
    private var playingSubscription: AnyCancellable?
    private var notificationObservers: [(center: NotificationCenter, token: NSObjectProtocol)] = []
    private var halListeners: [(selector: AudioObjectPropertySelector, block: AudioObjectPropertyListenerBlock)] = []
    private var powerSource: CFRunLoopSource?
    private var activityPoll: AnyCancellable?
    private var systemObserved = false, playbackObserved = false
    private var displaysAsleep = false, systemSleeping = false, sessionActive = true
    private var surfacesHiddenSince: Double?
    /// Pause wallpaper left nothing drawing.
    private var surfacesFrozen = false
    private var musicPlaying = false
    private var musicStartedAt: Double?
    /// Since when the player has played on this Mac, while capture ran.
    private var playingHereSince: Double?
    private var outputWasActive = false
    private var outputLastActiveAt: Double?

    private var uptime: Double { environment.now() }

    init(environment: AudioCaptureEnvironment) {
        self.environment = environment
        outputActivity = environment.makeOutputActivity()
    }
    convenience init() {
        self.init(environment: .live)
    }

    // MARK: - Public controls

    func resumeIfEnabled() {
        observeSettings()
        reconcile()
    }
    /// Switches System audio on. A user start connects straight away (unless
    /// power settings, sleep or a Pause wallpaper that leaves nothing drawing
    /// pause it), even before anything plays, so macOS can ask for
    /// permission in context.
    func start(userInitiated: Bool = true) {
        observeSettings()
        environment.setEnabled(true)
        if userInitiated {
            userStartedAt = uptime
            pendingUserStart = true
            blocked = false
        }
        reconcile()
    }
    func reconnect() {
        tearDownSession()
        preferCompatibility = false
        start()
    }
    /// Compatibility capture at the user's request. It may show the Screen
    /// Recording prompt; automatic recovery never asks for a new permission.
    func tryCompatibilityCapture() {
        tearDownSession()
        preferCompatibility = true
        start()
    }
    func stop(disablePreference: Bool = true, message: String = AudioReactiveController.offMessage) {
        tearDownSession()
        recheck = nil
        userStartedAt = nil; pendingUserStart = false; preferCompatibility = false
        if disablePreference {
            blocked = false
            environment.setEnabled(false)
            observePlayback(false)
            observeSystem(false)
            setState(.off, message)
        } else {
            blocked = true
            setState(.attention(.failed), message)
        }
    }
    /// Reconnect helps whenever capture is meant to be running.
    var offersReconnect: Bool {
        switch state {
        case .off, .paused: return false
        default: return true
        }
    }
    /// The problem is one which a privacy permission commonly causes.
    var suggestsPrivacySettings: Bool {
        guard case .attention(let problem) = state else { return false }
        return problem != .stalled
    }
    /// Compatibility capture is a real alternative to a tap that is failing.
    var offersCompatibilityCapture: Bool {
        guard state.needsAttention, usingTap else { return false }
        return environment.preferences().method != .screenRecording
    }
    func openPrivacySettings() {
        let tap: Bool
        if running || starting || state.needsAttention { tap = usingTap }
        else if #available(macOS 14.2, *) { tap = environment.preferences().method != .screenRecording }
        else { tap = false }
        // System Audio Recording, or Screen Recording for compatibility capture.
        let pane = tap ? "Privacy_AudioCapture" : "Privacy_ScreenCapture"
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") {
            NSWorkspace.shared.open(url)
        }
    }
    func copyDiagnostics() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(diagnosticsReport(), forType: .string)
    }

    /// What Copy audio diagnostics puts on the pasteboard: capture, demand,
    /// the player, how the visuals are aligned with the sound, and the web
    /// scenes' own state.
    func diagnosticsReport() -> String {
        let settings = Settings.shared
        let preferences = environment.preferences()
        let inputs = demand(at: uptime, preferences)
        let seconds = { (value: Double) in "\(Int(min(value, 99_999)))s" }
        let milliseconds = { (value: Double) in "\(Int((value.isFinite ? value * 1000 : 0).rounded())) ms" }
        let output = inputs.outputIdleFor.map { $0.isFinite ? "idle for \(seconds($0))" : "idle" } ?? "playing"
        let surfaces = environment.surfaces(preferences.pauseWhenCovered, preferences.wallpaperPaused)
        let frozen = surfaces.filter { $0 == .frozen }.count
        let drawing = "\(surfaces.filter { $0 == .drawing }.count) of \(surfaces.count) drawing"
            + (frozen > 0 ? ", \(frozen) frozen by Pause wallpaper" : "")
            + (inputs.surfacesHiddenFor.map { ", none for \(seconds($0))" } ?? "")
        let playback = self.playback
        let player: String
        if playback.playerPlaying {
            let here = playback.playerOutputHere.map { $0 ? "yes" : "no" } ?? "unknown"
            player = "\(playback.playerTitle) playing; on this Mac: \(here)"
        } else {
            player = playbackObserved ? "nothing playing" : "not followed"
        }
        let scenes = environment.webScenes().trimmingCharacters(in: .whitespacesAndNewlines)
        let web = scenes.isEmpty ? " none reported" : "\n" + scenes.split(separator: "\n").map { "  " + $0 }.joined(separator: "\n")
        return """
        Lunalyr audio · \(ProcessInfo.processInfo.operatingSystemVersionString)
        Requested: \(preferences.method.title); active: \(methodLabel)
        State: \(state); running: \(running); connecting: \(starting); suspended: \(suspended); packets: \(packetsReceived); dropped frames: \(droppedFrames)
        Format: \(formatLabel)
        RMS: \(levels.rms); bass: \(levels.bass); mid: \(levels.mid); treble: \(levels.treble)
        Demand: output \(output) (process report: \(outputActivity.isActive.map { "\($0)" } ?? "unavailable"); following playback: \(playbackObserved)); displays awake: \(inputs.displaysAwake); visualiser surfaces: \(drawing); wallpaper paused: \(preferences.wallpaperPaused)
        Player: \(player)
        Alignment: output latency \(milliseconds(environment.outputLatency())); visuals delayed \(milliseconds(environment.visualizer.visualDelay)) to land with the sound
        Visualiser: \(settings.audioVisualizerStyle.title); sensitivity: \(settings.audioSensitivity)
        Web scenes:\(web)
        Status: \(status)
        """
    }

    /// The connection under way, if any, so tests can wait for it.
    var pendingStart: Task<Void, Never>? { startTask }

    // MARK: - Events

    /// The single entry point for what capture follows: the observers below
    /// call it, and tests call it directly.
    func handle(_ event: AudioCaptureEvent) {
        switch event {
        case .settingsChanged, .surfacesChanged, .powerSourceChanged:
            reconcile()
        case .displaysSlept:
            displaysAsleep = true; reconcile()
        case .displaysWoke:
            displaysAsleep = false; wake()
        case .systemWillSleep:
            systemSleeping = true; reconcile()
        case .systemDidWake:
            systemSleeping = false; wake()
        case .sessionResigned:
            sessionActive = false; reconcile()
        case .sessionActivated:
            sessionActive = true; wake()
        case .musicPlaying(let playing):
            guard playbackObserved, playing != musicPlaying else { return }
            musicPlaying = playing
            musicStartedAt = playing ? uptime : nil
            trackOutputActivity()
            reconcile()
        case .outputActivityChanged:
            guard playbackObserved else { return }
            trackOutputActivity()
            reconcile()
        case .outputRouteChanged:
            routeChanged("Audio output changed; reconnecting…")
        case .coreAudioRestarted:
            // Listeners and cached state do not survive a Core Audio restart.
            guard playbackObserved else { return }
            if environment.observesSystem {
                stopListeningToHAL()
                listenToHAL()
            }
            outputActivity.onChange = nil
            outputActivity.stop()
            outputActivity.start()
            outputActivity.onChange = { [weak self] in self?.handle(.outputActivityChanged) }
            trackOutputActivity()
            routeChanged("Core Audio restarted; reconnecting…")
        }
    }

    // MARK: - Demand

    /// The followed player's playback, and whether any of it -- or anything
    /// else -- is playing on this Mac.
    private var playback: AudioPlayback {
        let player = musicPlaying ? environment.activePlayer() : nil
        return AudioPlayback(playerPlaying: musicPlaying, playerName: player?.displayName,
            playerOutputHere: player?.outputBundleID.flatMap { outputActivity.isRunningOutput(bundleID: $0) },
            anyOutput: outputActivity.isActive)
    }

    /// The player plays here, or another process plays audio. Unknown counts
    /// as playing; a player playing on another device does not.
    private var outputActive: Bool { playback.outputActive }

    private func demand(at now: Double, _ preferences: AudioCapturePreferences) -> AudioCaptureGate.Inputs {
        AudioCaptureGate.Inputs(
            enabled: preferences.enabled,
            visualiserChosen: preferences.visualiserChosen,
            powerPause: preferences.powerPause,
            displaysAwake: !displaysAsleep && !systemSleeping && sessionActive,
            surfacesHiddenFor: surfacesHiddenSince.map { now - $0 },
            surfacesFrozen: surfacesFrozen,
            outputIdleFor: outputActive ? nil : (outputLastActiveAt.map { now - $0 } ?? .infinity),
            sinceUserStart: userStartedAt.map { now - $0 })
    }

    /// Brings capture in line with demand. Idempotent; safe to call often.
    private func reconcile() {
        recheck = nil
        let preferences = environment.preferences()
        let now = uptime
        observeSystem(preferences.enabled && preferences.visualiserChosen)
        refreshSurfaces(at: now, preferences)
        var decision = AudioCaptureGate.decide(demand(at: now, preferences))
        if observePlayback(decision.followsPlayback) {
            // Playback is known now; decide with it.
            decision = AudioCaptureGate.decide(demand(at: now, preferences))
        }
        switch decision {
        case .off:
            tearDownSession()
            blocked = false; userStartedAt = nil
            setState(.off, Self.offMessage)
        case .paused(let reason):
            // Covered or paused wallpapers may come back at any moment, and
            // their route is followed meanwhile: a tap keeps its devices for
            // a quick resume. Anything else releases the session.
            if decision.followsPlayback { pauseSession() } else { tearDownSession() }
            blocked = false
            setState(.paused, reason.message)
        case .standby:
            pauseSession()
            blocked = false
            setState(.standby, AudioCaptureGate.standbyMessage(playback))
        case .capture(let recheckAfter):
            if let recheckAfter { scheduleRecheck(after: recheckAfter) }
            guard !blocked else { return }
            if suspended { resumeSession() }
            else if capture == nil && !starting { connect(compatibility: preferCompatibility) }
        }
    }

    private func scheduleRecheck(after seconds: Double) {
        // Slightly late, so the hang time has certainly run out.
        recheck = environment.schedule(min(max(0, seconds), 3_600) + 0.05, nil) { [weak self] in
            self?.reconcile()
        }
    }

    /// Starts the hidden hang time when the last surface stops drawing, and
    /// notes whether Pause wallpaper left nothing drawing.
    private func refreshSurfaces(at now: Double, _ preferences: AudioCapturePreferences) {
        let surfaces = environment.surfaces(preferences.pauseWhenCovered, preferences.wallpaperPaused)
        surfacesFrozen = AudioCaptureGate.frozen(surfaces, wallpaperPaused: preferences.wallpaperPaused)
        if AudioCaptureGate.anySurfaceDrawing(surfaces) {
            surfacesHiddenSince = nil
        } else if surfacesHiddenSince == nil {
            surfacesHiddenSince = now
        }
    }

    /// Starts the output hang time when playback stops; short gaps between
    /// tracks keep capture.
    private func trackOutputActivity() {
        let active = outputActive
        guard active != outputWasActive else { return }
        if !active { outputLastActiveAt = uptime }
        outputWasActive = active
    }

    // MARK: - Sessions

    private func connect(compatibility: Bool, reason: String = "") {
        // A start the user asked for long ago (say, under Low Power Mode)
        // must not raise a prompt out of context when capture finally runs.
        let userInitiated = pendingUserStart
            && userStartedAt.map { uptime - $0 < AudioCaptureRecovery.userStartGrace } ?? false
        pendingUserStart = false
        tearDownSession()
        blocked = false
        let generation = ticket
        screenCaptureAllowed = nil
        starting = true; running = false
        recoveryNote = reason
        packetsReceived = 0; packetBaseline = 0; droppedFrames = 0
        formatLabel = ""; formatProblem = nil; quietSince = nil; lastAudible = nil
        levels = VCAudioLevels(); environment.visualizer.reset()
        lastSample = 0; startedAt = uptime
        let method = environment.preferences().method
        sessionMethod = method
        let tap: Bool
        if #available(macOS 14.2, *) { tap = !compatibility && method != .screenRecording } else { tap = false }
        usingTap = tap
        methodLabel = tap ? "System audio" : !compatibility ? "Screen recording compatibility"
            : userInitiated ? "Compatibility capture" : "Compatibility · automatic recovery"
        setState(.connecting, reason.isEmpty ? "Connecting audio…" : reason)
        let processor = AudioPCMProcessor(target: environment.visualizer) { [weak self] report in
            MainActor.assumeIsolated { self?.receive(report, generation: generation) }
        }
        self.processor = processor
        processor.startAnalysis()
        startupWatchdog = environment.schedule(12, nil) { [weak self] in
            guard let self, self.ticket == generation, self.starting else { return }
            self.startupTimedOut(method: method)
        }
        let request = AudioCaptureRequest(tap: tap, userInitiated: userInitiated, processor: processor,
            onFormatChange: { [weak self] in
                guard let self, self.ticket == generation else { return }
                self.routeChanged("Audio format changed; reconnecting…")
            },
            onError: { [weak self] error in
                guard let self, self.ticket == generation else { return }
                self.fail(.failed, "Audio stopped: \(error.localizedDescription) Reconnect to retry.")
            })
        let open = environment.openSession
        startTask = Task { [weak self] in
            guard let self, self.ticket == generation, !Task.isCancelled else { return }
            do {
                if !tap && method == .systemAudio && !compatibility {
                    throw LyricHTTP.Failure(message: "System-audio taps need macOS 14.2 or later. Choose compatibility capture.")
                }
                status = tap ? "Starting the system-audio callback…"
                    : "Waiting for compatibility capture; approve its permission request if shown…"
                let session = try await open(request)
                guard ticket == generation, !Task.isCancelled else { session.stop(); return }
                startupWatchdog = nil
                capture = session; starting = false; running = true
                startedAt = uptime
                startHealthTimer()
                updateHealth()
                // Demand may have changed while connecting.
                reconcile()
            } catch is ScreenRecordingDeclined {
                guard ticket == generation, !Task.isCancelled else { return }
                fail(.failed, "Screen recording permission is needed. Enable access in System Settings, then choose Reconnect.")
            } catch {
                guard ticket == generation, !Task.isCancelled else { return }
                startFailed(error, method: method)
            }
        }
    }

    private func startFailed(_ error: Error, method: AudioCaptureMethod) {
        let detail = error.localizedDescription
        if usingTap, method == .automatic, mayRecoverAutomatically() {
            preferCompatibility = true
            connect(compatibility: true, reason: "System audio failed: \(detail) Trying compatibility capture…")
            return
        }
        // A failed compatibility attempt should not be repeated automatically.
        if !usingTap { preferCompatibility = false }
        let note = recoveryNote.isEmpty ? "" : recoveryNote + "\n"
        let advice = usingTap
            ? "If macOS asked about System Audio Recording, allow Lunalyr, then Reconnect."
            : "Check Screen Recording permission for this copy of Lunalyr, then Reconnect."
        fail(.failed, note + "Couldn’t start audio: \(detail) \(advice)")
    }

    private func startupTimedOut(method: AudioCaptureMethod) {
        guard usingTap, method == .automatic else {
            fail(.failed, "Audio startup timed out. Check capture permission for this copy of Lunalyr, then Reconnect.")
            return
        }
        if mayRecoverAutomatically() {
            preferCompatibility = true
            connect(compatibility: true, reason: "System audio startup timed out. Trying compatibility capture…")
        } else {
            // Keep waiting: a permission prompt may still be open.
            setState(.attention(.slowStart), "System audio is still starting. If macOS asked for permission, allow it, or try compatibility capture.")
        }
    }

    /// Automatic recovery never stacks a second prompt on a user start that
    /// may still be waiting for one, and never asks for Screen Recording.
    private func mayRecoverAutomatically() -> Bool {
        if let started = userStartedAt, uptime - started < AudioCaptureRecovery.userStartGrace { return false }
        return screenRecordingAllowed()
    }
    private func screenRecordingAllowed() -> Bool {
        if let screenCaptureAllowed { return screenCaptureAllowed }
        let allowed = environment.screenRecordingAllowed()
        screenCaptureAllowed = allowed
        return allowed
    }

    private func fail(_ problem: AudioCaptureProblem, _ message: String) {
        tearDownSession()
        blocked = true
        setState(.attention(problem), message)
    }

    /// Stops IO while nothing needs it. A tap keeps its devices for a quick
    /// resume; other sessions stop and reconnect on demand. Device work runs
    /// off the main thread; only a failure comes back here.
    private func pauseSession() {
        if starting { tearDownSession(); return }
        guard running, let capture else { return }
        healthTimer = nil
        processor?.stopAnalysis()
        guard capture.canSuspend else { tearDownSession(); return }
        suspended = true; running = false
        levels = VCAudioLevels()
        let generation = ticket
        deviceRequest += 1
        let request = deviceRequest
        capture.suspend { [weak self] stopped in
            MainActor.assumeIsolated {
                guard let self, !stopped, self.ticket == generation, self.deviceRequest == request else { return }
                // IO would not stop cheaply: stop the session itself.
                self.tearDownSession()
            }
        }
    }

    private func resumeSession() {
        guard suspended, let capture, let processor else { return }
        processor.startAnalysis()
        suspended = false; running = true
        startedAt = uptime; lastSample = 0; lastAudible = nil; quietSince = nil
        packetBaseline = packetsReceived
        setState(.connecting, "Resuming audio…")
        startHealthTimer()
        let generation = ticket
        deviceRequest += 1
        let request = deviceRequest
        capture.resume { [weak self] error in
            MainActor.assumeIsolated {
                guard let self, error != nil, self.ticket == generation, self.deviceRequest == request else { return }
                self.rebuildSession(reason: "Reconnecting audio…")
            }
        }
    }

    /// Starts a new session if demand still calls for capture.
    private func rebuildSession(reason: String) {
        tearDownSession()
        blocked = false
        if case .capture = AudioCaptureGate.decide(demand(at: uptime, environment.preferences())) {
            connect(compatibility: preferCompatibility, reason: reason)
        } else {
            reconcile()
        }
    }

    private func tearDownSession() {
        ticket = UUID()
        startTask?.cancel(); startTask = nil
        startupWatchdog = nil
        healthTimer = nil
        capture?.stop(); capture = nil
        processor?.stopAnalysis(); processor = nil
        if running || starting || suspended {
            levels = VCAudioLevels(); environment.visualizer.reset()
        }
        // Paused states run this often; publish only real changes.
        if running { running = false }
        if starting { starting = false }
        suspended = false
        if packetsReceived != 0 { packetsReceived = 0 }
        packetBaseline = 0
    }

    /// Output route, format or Core Audio changes. A running session
    /// reconnects; an idle one is rebuilt when capture is next needed.
    private func routeChanged(_ reason: String) {
        guard environment.preferences().enabled else { return }
        routeTimer = environment.schedule(0.45, nil) { [weak self] in
            guard let self else { return }
            self.routeTimer = nil
            if self.running || self.starting {
                self.rebuildSession(reason: reason)
            } else {
                self.tearDownSession()
                self.blocked = false
                self.reconcile()
            }
        }
    }

    /// Displays or the Mac woke: let the output route settle, then follow demand.
    private func wake() {
        wakeTimer = environment.schedule(0.45, nil) { [weak self] in
            guard let self else { return }
            self.wakeTimer = nil
            self.blocked = false
            self.outputActivity.refresh()
            self.reconcile()
        }
    }

    // MARK: - Health

    private func startHealthTimer() {
        healthTimer = environment.schedule(0.5, 0.5) { [weak self] in self?.updateHealth() }
    }

    private func receive(_ report: AudioPCMProcessor.Report, generation: UUID) {
        guard ticket == generation, running || starting, report.deliveredAt >= startedAt else { return }
        lastSample = report.deliveredAt
        if let audible = report.audibleAt { lastAudible = audible }
        levels = report.levels
        if packetsReceived != report.packets { packetsReceived = report.packets }
        droppedFrames = report.droppedFrames
        if formatLabel != report.formatLabel { formatLabel = report.formatLabel }
        if let problem = report.problem { formatProblem = problem }
        updateHealth()
    }

    private func updateHealth() {
        guard running else { return }
        let now = uptime
        let method = sessionMethod
        let quiet = levels.rms < 0.00015
        // Silence means something only while the player plays on this Mac:
        // Spotify Connect, say, reports playing with nothing to hear here.
        let playback = self.playback
        let playingHere = playback.playingHere
        if playingHere && quiet {
            if quietSince == nil { quietSince = now }
        } else { quietSince = nil }
        if playingHere {
            if playingHereSince == nil { playingHereSince = now }
        } else { playingHereSince = nil }
        let packets = max(0, packetsReceived - packetBaseline)
        let elapsed = now - startedAt
        let packetAge = lastSample > 0 ? now - lastSample : 0
        if usingTap && method == .automatic {
            let silent = AudioCaptureRecovery.shouldUseCompatibility(method: method, usingTap: true,
                elapsed: elapsed, packets: packets, packetAge: packetAge,
                quietFor: quietSince.map { now - $0 } ?? 0, playing: playingHere,
                sinceUserStart: userStartedAt.map { now - $0 }, screenCaptureAllowed: screenRecordingAllowed())
            if silent || (formatProblem != nil && mayRecoverAutomatically()) {
                preferCompatibility = true
                connect(compatibility: true, reason: "System audio delivered no usable sound. Trying compatibility capture…")
                return
            }
        }
        if let formatProblem {
            setState(.attention(.failed), formatProblem)
            return
        }
        // Silence only counts while the player plays here, and after the
        // session began.
        let silentSince = max(lastAudible ?? startedAt, startedAt, musicStartedAt ?? startedAt,
                              playingHereSince ?? startedAt)
        let silentFor = packets > 0 ? now - silentSince : 0
        let health = AudioCaptureHealth.evaluate(elapsed: elapsed, packets: packets, packetAge: packetAge,
            digitalSilenceFor: silentFor, quiet: quiet, musicPlaying: playingHere, outputActive: playback.outputActive)
        let status = health.status(playback: playback, methodLabel: methodLabel,
            permission: usingTap ? "System Audio Recording" : "Screen Recording",
            firstPrompt: userStartedAt.map { now - $0 < AudioCaptureRecovery.userStartGrace } ?? false,
            note: recoveryNote)
        setState(status.state, status.message)
    }

    private func setState(_ next: AudioCaptureState, _ message: String) {
        if state != next {
            state = next
            if next == .receiving, !environment.captureConfirmed() { environment.confirmCapture() }
        }
        if status != message { status = message }
    }

    // MARK: - Observation

    private func observeSettings() {
        guard settingsSubscription == nil else { return }
        settingsSubscription = environment.preferencesChanged()
            .debounce(for: .milliseconds(100), scheduler: RunLoop.main)
            .map { [weak self] _ in self?.environment.preferences() }
            .removeDuplicates()
            .sink { [weak self] _ in self?.handle(.settingsChanged) }
    }

    /// Cheap notifications which say when capture could run again: sleep and
    /// wake, user sessions, surfaces starting or stopping drawing, and the
    /// power source. Kept while System audio is on and a visualiser is chosen.
    private func observeSystem(_ wanted: Bool) {
        guard wanted != systemObserved else { return }
        systemObserved = wanted
        displaysAsleep = false; systemSleeping = false; sessionActive = true
        surfacesHiddenSince = nil; surfacesFrozen = false
        guard wanted else {
            wakeTimer = nil
            surfaceSubscription = nil
            for observer in notificationObservers { observer.center.removeObserver(observer.token) }
            notificationObservers.removeAll()
            if let powerSource { CFRunLoopRemoveSource(CFRunLoopGetMain(), powerSource, .commonModes) }
            powerSource = nil
            return
        }
        guard environment.observesSystem else { return }
        displaysAsleep = CGDisplayIsAsleep(CGMainDisplayID()) != 0
        let workspace = NSWorkspace.shared.notificationCenter
        observe(workspace, NSWorkspace.screensDidSleepNotification, .displaysSlept)
        observe(workspace, NSWorkspace.screensDidWakeNotification, .displaysWoke)
        observe(workspace, NSWorkspace.willSleepNotification, .systemWillSleep)
        observe(workspace, NSWorkspace.didWakeNotification, .systemDidWake)
        observe(workspace, NSWorkspace.sessionDidResignActiveNotification, .sessionResigned)
        observe(workspace, NSWorkspace.sessionDidBecomeActiveNotification, .sessionActivated)
        // The wallpapers' coverage, the Settings preview's window and the
        // screens' state, as the surfaces' own frame clocks see them.
        surfaceSubscription = environment.surfacesChanged()
            .sink { [weak self] in self?.handle(.surfacesChanged) }
        observePowerSource()
    }

    /// What decides whether capture runs right now: Music's playback, other
    /// processes' output and the output route. Observed only while capture
    /// runs, stands by or waits behind covered or paused wallpapers; never
    /// while the wallpaper type, power settings or sleep rule capture out. Returns
    /// true when observation just started, so the caller decides again.
    @discardableResult
    private func observePlayback(_ wanted: Bool) -> Bool {
        guard wanted != playbackObserved else { return false }
        playbackObserved = wanted
        guard wanted else {
            stopListeningToHAL()
            playingSubscription = nil
            activityPoll = nil
            routeTimer = nil
            outputActivity.onChange = nil
            outputActivity.stop()
            musicPlaying = false; musicStartedAt = nil; playingHereSince = nil
            outputWasActive = false; outputLastActiveAt = nil
            return false
        }
        if environment.observesSystem { listenToHAL() }
        outputActivity.start()
        outputActivity.onChange = { [weak self] in self?.handle(.outputActivityChanged) }
        musicPlaying = environment.isMusicPlaying()
        musicStartedAt = musicPlaying ? uptime : nil
        playingSubscription = environment.musicPlayingChanges()
            .sink { [weak self] playing in self?.handle(.musicPlaying(playing)) }
        outputWasActive = outputActive
        outputLastActiveAt = nil
        // Process listeners do the work; this slow, tolerant poll only
        // covers a missed notification.
        activityPoll = environment.schedule(Self.activityPollInterval, Self.activityPollInterval) { [weak self] in
            self?.outputActivity.refresh()
        }
        return true
    }

    private func observe(_ center: NotificationCenter, _ name: Notification.Name, _ event: AudioCaptureEvent) {
        let token = center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.handle(event) }
        }
        notificationObservers.append((center, token))
    }

    private func listenToHAL() {
        listen(kAudioHardwarePropertyDefaultOutputDevice) { $0.handle(.outputRouteChanged) }
        listen(kAudioHardwarePropertyServiceRestarted) { controller in
            // Not from inside the listener that is about to be replaced.
            DispatchQueue.main.async { [weak controller] in
                MainActor.assumeIsolated { controller?.handle(.coreAudioRestarted) }
            }
        }
    }
    private func stopListeningToHAL() {
        for listener in halListeners {
            var address = AudioObjectPropertyAddress(mSelector: listener.selector,
                mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main, listener.block)
        }
        halListeners.removeAll()
    }
    private func listen(_ selector: AudioObjectPropertySelector,
                        _ action: @escaping @MainActor (AudioReactiveController) -> Void) {
        var address = AudioObjectPropertyAddress(mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                action(self)
            }
        }
        if AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main, block) == noErr {
            halListeners.append((selector, block))
        }
    }

    /// Battery/AC changes alter the effective wallpaper without a settings
    /// change, so follow them directly.
    private func observePowerSource() {
        guard powerSource == nil else { return }
        let context = Unmanaged.passUnretained(self).toOpaque()
        guard let source = IOPSNotificationCreateRunLoopSource({ context in
            guard let context else { return }
            let controller = Unmanaged<AudioReactiveController>.fromOpaque(context).takeUnretainedValue()
            MainActor.assumeIsolated { controller.handle(.powerSourceChanged) }
        }, context)?.takeRetainedValue() else { return }
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        powerSource = source
    }
}
