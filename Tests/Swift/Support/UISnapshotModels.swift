import AppKit
import Combine
import SwiftUI
@testable import VersoCustom

/// Isolated stores for drawing the GUI: a throwaway preferences suite, fake
/// players (`PlayerHarness`: no Apple event is ever sent), a capture
/// controller that never opens a device, and navigation that only records.
/// Lyric lookup has no injectable store yet, so the lyric view model and
/// its options are the app's shared ones: read, never written here.
///
///     let scene = UISnapshotScene(player: .playing)
///     scene.settings.backgroundMode = .visualizer
///     MenuBarView().lunalyrModels(scene.models)
///     scene.tearDown()   // removes the preferences suite
@MainActor
final class UISnapshotScene {
    /// What the fake players are doing.
    enum Player {
        /// Music open, nothing playing.
        case idle
        /// Music playing `UISnapshotScene.song`, with its cover.
        case playing
        /// The same song, paused.
        case paused
        /// Music open, and Lunalyr refused in Privacy & Security › Automation.
        case denied
        /// Music open, and macOS hasn't asked about it yet.
        case needsConsent
        /// No player running.
        case notRunning
    }

    static let song = FakeMusicApp.Song(id: "0000000000005A1E", name: "Golden Hour Static", artist: "Lunar Tides",
                                        album: "Night Bloom", genre: "Pop", duration: 214, streamTitle: nil,
                                        artwork: UISnapshotScene.cover)

    let suiteName = "LunalyrUISnapshots-\(UUID().uuidString)"
    let defaults: UserDefaults
    let settings: VersoCustom.Settings
    let extras: LunalyrExtras
    let hotkeys: GlobalHotkeys
    let players: PlayerOptions
    let harness: PlayerHarness
    let audio: AudioReactiveController
    let navigation: LunalyrNavigation
    let models: LunalyrModels
    /// What `LyricNow` reports to the views; set it before drawing.
    var lyricNow: LyricNow {
        get { lyricBox.value }
        set { lyricBox.value = newValue }
    }
    private let lyricBox: LyricNowBox
    /// Every navigation request, in order: "settings:wallpaper", "onboarding", "url:…".
    private(set) var navigated: [String] = []

    init(player: Player = .idle, lyricNow: LyricNow = .none) {
        defaults = UserDefaults(suiteName: suiteName)!
        let notifications = NotificationCenter()
        settings = VersoCustom.Settings(defaults: defaults, notifications: notifications)
        extras = LunalyrExtras(defaults: defaults)
        hotkeys = GlobalHotkeys(defaults: defaults, registrar: .init(register: { _, _, _ in true }, unregister: { _ in }))
        players = PlayerOptions(defaults: defaults)
        let lyricBox = LyricNowBox(lyricNow)
        self.lyricBox = lyricBox

        let music = FakeMusicApp()
        music.queue = [Self.song]
        music.position = 63
        music.state = player == .paused ? .paused : player == .playing ? .playing : .stopped
        var services = NowPlayingServices.offline
        services.followedPlayers = players.playersPublisher
        let running: Set<String> = player == .notRunning ? [] : ["com.apple.Music"]
        harness = PlayerHarness(music: music, running: running, services: services)
        switch player {
        case .denied: harness.permission.statuses["com.apple.Music"] = OSStatus(AppleEventCode.notPermitted)
        case .needsConsent: harness.permission.statuses["com.apple.Music"] = OSStatus(AppleEventCode.wouldRequireConsent)
        default: harness.permission.status = 0
        }
        harness.start()

        audio = AudioReactiveController(environment: UISnapshotScene.captureEnvironment())
        navigation = LunalyrNavigation(presentSettings: {}, presentOnboarding: {}, openURL: { _ in })
        models = LunalyrModels(
            settings: settings, colours: LyricColourOptions(defaults: defaults),
            highlightColours: LyricHighlightOptions(defaults: defaults), styleMemory: StyleMemory(defaults: defaults),
            extras: extras, hotkeys: hotkeys, desktop: DesktopOptions(defaults: defaults), players: players,
            visualizer: VisualizerPreferences(defaults: defaults), offsets: SongOffsets(defaults: defaults),
            nowPlaying: harness.monitor, audio: audio, updates: AppUpdates(defaults: defaults), navigation: navigation,
            installedPlayers: [.appleMusic, .spotify],
            lyricNow: { _ in lyricBox.value })
        // Requests are recorded, never acted on.
        navigation.presentSettings = { [weak self] in self?.navigated.append("settings:\(self?.navigation.settingsRequest?.destination.rawValue ?? "")") }
        navigation.presentOnboarding = { [weak self] in self?.navigated.append("onboarding") }
        navigation.openURL = { [weak self] url in self?.navigated.append("url:\(url.absoluteString)") }
    }

    var colours: LyricColourOptions { models.colours }
    /// Never started: no Sparkle, no feed. `updates.receive(_:)` stands in
    /// for what Sparkle would report.
    var updates: AppUpdates { models.updates }
    var offsets: SongOffsets { models.offsets }
    var styleMemory: StyleMemory { models.styleMemory }
    var visualizer: VisualizerPreferences { models.visualizer }
    var desktop: DesktopOptions { models.desktop }

    /// The `@AppStorage` the Settings window keeps its page and preview in.
    /// `name` is a page's stored value (today's: wallpaper, typography,
    /// covers, lyrics, readings, performance, extras, about).
    func showSettingsPage(_ name: String, preview: Bool = true, largePreview: Bool = false) {
        defaults.set(name, forKey: "custom.settingsPage")
        defaults.set(preview, forKey: "custom.settingsShowsPreview")
        defaults.set(largePreview, forKey: "custom.settingsLargePreview")
    }

    /// Saves `seconds` as the playing song's offset, as the timing nudge
    /// and the Timing page do. Needs a song: `.playing` or `.paused`.
    func setSongOffset(_ seconds: Double) {
        guard let query = models.currentQuery else { return }
        offsets.set(seconds, for: query)
    }

    /// System audio switched on and failing, as after macOS withdrew its
    /// permission: the status asks for attention and offers its fixes.
    func audioNeedsAttention(_ message: String = "No audio is arriving from the system. If macOS asked about System Audio Recording, allow Lunalyr, then Reconnect.") {
        settings.backgroundMode = .visualizer
        settings.audioReactiveEnabled = true
        audio.stop(disablePreference: false, message: message)
    }

    /// A display is mirrored and the lyrics are hidden for presenting (the
    /// default). `asked` false: the first time, so the menu asks whether to
    /// keep doing that.
    func hideLyricsForMirroring(asked: Bool = false) {
        if asked { extras.hidesWhileMirroring = true }
        extras.updateMirroring(true)
    }

    /// Runs what the fake players were asked meanwhile (access checks,
    /// reads), so their answers reach the views before the capture.
    func pump() {
        harness.scheduler.drain()
    }

    func tearDown() {
        defaults.removePersistentDomain(forName: suiteName)
    }

    // MARK: - Fixtures

    /// A capture controller that stays off: its sessions can't open.
    static func captureEnvironment(preferences: AudioCapturePreferences = .init()) -> AudioCaptureEnvironment {
        AudioCaptureEnvironment(
            now: { 1_000 },
            schedule: { _, _, _ in AnyCancellable {} },
            preferences: { preferences },
            setEnabled: { _ in },
            preferencesChanged: { Empty(completeImmediately: false).eraseToAnyPublisher() },
            surfaces: { _, _ in [] },
            surfacesChanged: { Empty(completeImmediately: false).eraseToAnyPublisher() },
            isMusicPlaying: { false },
            musicPlayingChanges: { Empty(completeImmediately: false).eraseToAnyPublisher() },
            activePlayer: { nil },
            makeOutputActivity: { SilentOutputActivity() },
            observesSystem: false,
            screenRecordingAllowed: { false },
            visualizer: VisualizerAudio(),
            outputLatency: { 0 },
            webScenes: { "" },
            openSession: { _ in throw SnapshotCaptureUnavailable() })
    }

    /// A square album cover: a warm gradient with a soft sun, so artwork
    /// placement and rounding can be judged.
    static let cover: Data = {
        let size = 600
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                                   samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                   bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        let rect = NSRect(x: 0, y: 0, width: size, height: size)
        NSGradient(colors: [NSColor(srgbRed: 0.98, green: 0.62, blue: 0.38, alpha: 1),
                            NSColor(srgbRed: 0.85, green: 0.30, blue: 0.48, alpha: 1),
                            NSColor(srgbRed: 0.24, green: 0.16, blue: 0.42, alpha: 1)])?.draw(in: rect, angle: -90)
        NSColor(srgbRed: 1, green: 0.86, blue: 0.62, alpha: 0.9).setFill()
        NSBezierPath(ovalIn: NSRect(x: 190, y: 250, width: 220, height: 220)).fill()
        NSColor(srgbRed: 0.16, green: 0.10, blue: 0.30, alpha: 0.85).setFill()
        let hills = NSBezierPath()
        hills.move(to: NSPoint(x: 0, y: 0))
        hills.line(to: NSPoint(x: 0, y: 210))
        hills.curve(to: NSPoint(x: 600, y: 170), controlPoint1: NSPoint(x: 220, y: 330), controlPoint2: NSPoint(x: 380, y: 90))
        hills.line(to: NSPoint(x: 600, y: 0))
        hills.close()
        hills.fill()
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])!
    }()
}

private struct SnapshotCaptureUnavailable: Error {}

@MainActor
private final class LyricNowBox {
    var value: LyricNow
    init(_ value: LyricNow) { self.value = value }
}

@MainActor
private final class SilentOutputActivity: AudioOutputActivitySource {
    var isActive: Bool? { nil }
    var onChange: (() -> Void)?
    func isRunningOutput(bundleID: String) -> Bool? { nil }
    func start() {}
    func stop() {}
    func refresh() {}
}
