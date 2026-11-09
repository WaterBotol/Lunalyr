import AppKit
import Combine
import CoreGraphics
import Foundation
import ScreenCaptureKit

/// The settings System audio follows, read in one place.
struct AudioCapturePreferences: Equatable {
    var enabled = false
    /// The chosen wallpaper type is Visualisers.
    var visualiserChosen = false
    /// Why power settings replaced the visualiser, if they did.
    var powerPause: String?
    /// “Pause fully covered wallpapers”.
    var pauseWhenCovered = false
    var method: AudioCaptureMethod = .automatic
    /// Pause wallpaper (Extras) is on. It freezes the desktop wallpapers; the
    /// Settings preview keeps moving and stops only with its own window.
    var wallpaperPaused = false

    @MainActor static func current(_ settings: Settings, extras: LunalyrExtras? = nil) -> AudioCapturePreferences {
        let extras = extras ?? .shared
        let lowPowerMode = settings.respectLowPower && ProcessInfo.processInfo.isLowPowerModeEnabled
        return AudioCapturePreferences(
            enabled: settings.audioReactiveEnabled,
            visualiserChosen: settings.backgroundMode == .visualizer,
            powerPause: AudioCaptureGate.powerPause(chosen: settings.backgroundMode,
                effective: settings.effectiveBackgroundMode, lowPowerVisuals: settings.lowPowerVisuals,
                lowPowerMode: lowPowerMode, onBattery: settings.onBattery != .full && PowerSource.isOnBattery),
            pauseWhenCovered: settings.pauseWhenCovered,
            method: settings.audioCaptureMethod,
            wallpaperPaused: extras.wallpaperPaused)
    }
}

/// One connection attempt.
struct AudioCaptureRequest {
    /// The system-audio tap; otherwise ScreenCaptureKit compatibility capture.
    var tap: Bool
    /// The user asked for this connection, so it may show a permission prompt.
    var userInitiated: Bool
    /// Receives the session's samples.
    var processor: AudioPCMProcessor
    /// The tap's format or the output's sample rate changed.
    var onFormatChange: @MainActor () -> Void
    /// The session stopped with an error.
    var onError: @MainActor (Error) -> Void
}

/// Screen Recording was declined before and still isn't allowed, so only a
/// start the user asks for may show its prompt again.
struct ScreenRecordingDeclined: Error {}

/// Whether other processes are playing audio; see `AudioOutputActivityMonitor`.
@MainActor
protocol AudioOutputActivitySource: AnyObject {
    /// nil while stopped or unknown.
    var isActive: Bool? { get }
    /// Whether the app `bundleID`, or one of its helpers, is playing on this
    /// Mac; nil while stopped or unknown.
    func isRunningOutput(bundleID: String) -> Bool?
    var onChange: (() -> Void)? { get set }
    func start()
    func stop()
    /// Reads the state again, should a notification have been missed.
    func refresh()
}

extension AudioOutputActivityMonitor: AudioOutputActivitySource {}

/// Everything `AudioReactiveController` uses outside itself. The app runs
/// `live`; tests supply their own clock, timers, preferences, drawing
/// surfaces, playback and sessions, so they never open an audio device,
/// raise a permission prompt or touch the user's settings.
@MainActor
struct AudioCaptureEnvironment {
    /// Monotonic seconds.
    var now: @MainActor () -> Double
    /// Runs `action` on the main thread after `delay` seconds, then every
    /// `repeating` seconds if given, until the result is cancelled or released.
    var schedule: @MainActor (_ delay: Double, _ repeating: Double?, _ action: @escaping @MainActor () -> Void) -> AnyCancellable
    var preferences: @MainActor () -> AudioCapturePreferences
    var setEnabled: @MainActor (Bool) -> Void
    /// Fires when any setting may have changed.
    var preferencesChanged: @MainActor () -> AnyPublisher<Void, Never>
    /// What each surface that could show a visualiser is doing, by the rule
    /// its own frame clock follows (`WindowRenderActivity.state`) with these
    /// choices -- "Pause fully covered wallpapers" and Pause wallpaper: every
    /// display's wallpaper, and the Settings preview while it is shown.
    var surfaces: @MainActor (_ pauseWhenCovered: Bool, _ wallpaperPaused: Bool) -> [WindowRenderActivity.State]
    /// Fires when a surface comes or goes, or is seen or hidden.
    var surfacesChanged: @MainActor () -> AnyPublisher<Void, Never>
    /// Whether the followed player (Music or Spotify) is playing.
    var isMusicPlaying: @MainActor () -> Bool
    /// That playback state, on the main thread.
    var musicPlayingChanges: @MainActor () -> AnyPublisher<Bool, Never>
    /// The player `isMusicPlaying` describes, if any.
    var activePlayer: @MainActor () -> PlayerKind?
    var makeOutputActivity: @MainActor () -> AudioOutputActivitySource
    /// Follow sleep, user-session, surface, power-source and Core Audio
    /// route notifications. Without them, the owner delivers those events.
    var observesSystem: Bool
    /// Screen Recording is already allowed. Never asks.
    var screenRecordingAllowed: @MainActor () -> Bool
    var visualizer: VisualizerAudio
    /// The output device's latency in seconds, which the visuals wait for.
    var outputLatency: @MainActor () -> Double
    /// What each web scene renders with and its last problem, one per line.
    var webScenes: @MainActor () -> String
    /// Opens and starts a session, or throws.
    var openSession: @MainActor (AudioCaptureRequest) async throws -> AudioCaptureSession
    /// Capture has delivered audio on this Mac before, so its permission is
    /// granted: a brief listen may start it without raising a prompt.
    var captureConfirmed: @MainActor () -> Bool = { false }
    /// Remembers that capture delivered audio.
    var confirmCapture: @MainActor () -> Void = {}
}

extension AudioCaptureEnvironment {
    static var live: AudioCaptureEnvironment {
        AudioCaptureEnvironment(
            now: { ProcessInfo.processInfo.systemUptime },
            schedule: { delay, repeating, action in scheduleOnMain(after: delay, repeating: repeating, action) },
            preferences: { .current(.shared) },
            setEnabled: { Settings.shared.audioReactiveEnabled = $0 },
            preferencesChanged: {
                Settings.shared.objectWillChange
                    .merge(with: LunalyrExtras.shared.$wallpaperPaused.removeDuplicates().map { _ in () })
                    .eraseToAnyPublisher()
            },
            surfaces: { RenderSurfaces.shared.states(pauseWhenCovered: $0, wallpaperPaused: $1) },
            surfacesChanged: { RenderSurfaces.shared.changes },
            isMusicPlaying: { NowPlayingMonitor.shared.isPlaying },
            musicPlayingChanges: { NowPlayingMonitor.shared.$isPlaying.removeDuplicates().eraseToAnyPublisher() },
            activePlayer: { NowPlayingMonitor.shared.activePlayer },
            makeOutputActivity: { AudioOutputActivityMonitor() },
            observesSystem: true,
            screenRecordingAllowed: { CGPreflightScreenCaptureAccess() },
            visualizer: .shared,
            outputLatency: { OutputLatencyMonitor.shared.latency },
            webScenes: { WebVisualizerDiagnostics.shared.report },
            openSession: { try await openLiveSession($0) },
            captureConfirmed: { UserDefaults.standard.bool(forKey: confirmedKey) },
            confirmCapture: { UserDefaults.standard.set(true, forKey: confirmedKey) })
    }

    /// Set once capture has delivered audio on this Mac.
    static let confirmedKey = "custom.audioCaptureConfirmed"

    /// A main-run-loop timer, which also fires while a menu tracks.
    static func scheduleOnMain(after delay: Double, repeating: Double?,
                               _ action: @escaping @MainActor () -> Void) -> AnyCancellable {
        let timer = Timer(fire: Date(timeIntervalSinceNow: max(0, delay)), interval: repeating ?? 0,
                          repeats: repeating != nil) { _ in MainActor.assumeIsolated { action() } }
        timer.tolerance = min(1, max(0.005, (repeating ?? delay) * 0.1))
        RunLoop.main.add(timer, forMode: .common)
        return AnyCancellable { timer.invalidate() }
    }

    private static let screenDenialKey = "custom.screenAudioPermissionDeclined"

    /// A private process tap, or ScreenCaptureKit compatibility capture.
    static func openLiveSession(_ request: AudioCaptureRequest) async throws -> AudioCaptureSession {
        if request.tap {
            guard #available(macOS 14.2, *) else {
                throw LyricHTTP.Failure(message: "System-audio taps need macOS 14.2 or later. Choose compatibility capture.")
            }
            let onFormatChange = request.onFormatChange
            let system = try SystemAudioCapture(processor: request.processor, onFormatChange: {
                MainActor.assumeIsolated { onFormatChange() }
            })
            do { try await system.start() } catch { system.stop(); throw error }
            return system
        }
        let defaults = UserDefaults.standard
        if defaults.bool(forKey: screenDenialKey), !request.userInitiated, !CGPreflightScreenCaptureAccess() {
            throw ScreenRecordingDeclined()
        }
        let onError = request.onError
        let screen = ScreenAudioCapture(processor: request.processor, onError: { error in
            Task { @MainActor in onError(error) }
        })
        do { try await screen.start() } catch {
            screen.stop()
            let failure = error as NSError
            if failure.domain == SCStreamErrorDomain,
               failure.code == SCStreamError.Code.userDeclined.rawValue {
                defaults.set(true, forKey: screenDenialKey)
            }
            throw error
        }
        defaults.removeObject(forKey: screenDenialKey)
        return screen
    }
}
