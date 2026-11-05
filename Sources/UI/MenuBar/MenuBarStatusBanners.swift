import SwiftUI

/// The statuses the menu-bar panel can raise, most urgent first.
enum MenuBarStatus: CaseIterable, Hashable {
    /// Music or Spotify refused, or not asked yet (`AutomationStatusBanner`).
    case automation
    /// System audio failing for a visualiser, with its fixes.
    case audio
    /// The lyrics hidden while a display is mirrored; the first time, the
    /// question whether to keep doing that.
    case mirroring
    /// Power saving drawing something other than the chosen wallpaper.
    case powerSaving

    /// The panel shows at most this many at once; the rest wait in Settings,
    /// where each has its place (the preview stage, the Wallpaper page, Menu
    /// bar & shortcuts).
    static let limit = 2

    /// The statuses to show, in order, at most `limit` of them.
    static func shown(automation: Bool, audio: Bool, mirroring: Bool, powerSaving: Bool) -> [MenuBarStatus] {
        let raised = allCases.filter { status in
            switch status {
            case .automation: return automation
            case .audio: return audio
            case .mirroring: return mirroring
            case .powerSaving: return powerSaving
            }
        }
        return Array(raised.prefix(limit))
    }
}

/// What needs the person's attention, as compact banners under the song,
/// each with its fix on the spot. Nothing at all while all is well:
/// working audio capture, for one, says nothing here.
struct MenuBarStatusBanners: View {
    /// From `raised(…)`; the panel leaves the group out when it is empty.
    let statuses: [MenuBarStatus]

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            ForEach(statuses, id: \.self) { status in
                switch status {
                case .automation: AutomationStatusBanner(density: .compact)
                case .audio: MenuBarAudioStatus()
                case .mirroring: MenuBarMirroringBanner()
                case .powerSaving: MenuBarPowerSavingBanner()
                }
            }
        }
    }

    /// The statuses raised now, at most `MenuBarStatus.limit` of them.
    /// `audioNeedsAttention` is followed apart from the capture
    /// controller's meters.
    @MainActor
    static func raised(playing: NowPlayingMonitor, settings: Settings, extras: LunalyrExtras,
                       audioNeedsAttention: Bool) -> [MenuBarStatus] {
        MenuBarStatus.shown(
            automation: AutomationStatusBanner.content(for: playing) != nil,
            audio: MenuBarAudioStatus.shows(visualizer: settings.effectiveBackgroundMode == .visualizer,
                                            audioOn: settings.audioReactiveEnabled, needsAttention: audioNeedsAttention),
            mirroring: extras.hiddenByMirroring,
            powerSaving: MenuBarPowerSavingBanner.shows(chosen: settings.backgroundMode,
                                                        drawn: settings.effectiveBackgroundMode,
                                                        notice: settings.powerSavingNotice))
    }
}

// MARK: - System audio

/// System-audio capture failing for a visualiser: what went wrong, in the
/// controller's own words, with the fixes it offers. The wallpaper keeps
/// moving on its ambient spectrum when capture fails, so the failure is easy
/// to miss without this. Reads only the status and state: the controller
/// also publishes meter levels many times a second, which must not redraw
/// the menu.
struct MenuBarAudioStatus: View {
    @Environment(\.lunalyrModels) private var models
    // Filled from the controller as the view appears (a published value
    // arrives on subscription).
    @State private var status = AudioReactiveController.offMessage
    @State private var state = AudioCaptureState.off
    /// Whether compatibility capture is offered depends on it.
    @State private var usingTap = false

    var body: some View {
        let audio = models.resolved.audio
        let _ = usingTap
        let fixes = Self.fixes(for: state, offersReconnect: audio.offersReconnect,
                               suggestsPrivacySettings: audio.suggestsPrivacySettings,
                               offersCompatibilityCapture: audio.offersCompatibilityCapture)
        StatusBanner(.warning, title: Self.title, message: status, density: .compact) {
            ForEach(fixes, id: \.self) { fix in
                Button(fix.title) {
                    switch fix {
                    case .reconnect: audio.reconnect()
                    // The pane that matches the capture method in use.
                    case .privacySettings: audio.openPrivacySettings()
                    case .compatibilityCapture: audio.tryCompatibilityCapture()
                    }
                }
                .help(fix.help)
            }
        }
        .onReceive(audio.$status.removeDuplicates()) { status = $0 }
        .onReceive(audio.$state.removeDuplicates()) { state = $0 }
        .onReceive(audio.$usingTap.removeDuplicates()) { usingTap = $0 }
    }

    /// What the failure means for the wallpaper; the controller's status
    /// sentence beneath it says why and what to do.
    static let title = "The visualiser can’t hear the music"

    /// Only a problem shows here, while a visualiser is drawn with System
    /// audio on. Working capture says so in Settings › Wallpaper.
    static func shows(visualizer: Bool, audioOn: Bool, needsAttention: Bool) -> Bool {
        visualizer && audioOn && needsAttention
    }

    enum Fix: Hashable {
        case reconnect, privacySettings, compatibilityCapture

        var title: String {
            switch self {
            case .reconnect: return "Reconnect"
            case .privacySettings: return "Open Privacy Settings…"
            case .compatibilityCapture: return "Try Compatibility Capture"
            }
        }

        var help: String {
            switch self {
            case .reconnect: return "Start system audio again"
            case .privacySettings: return "Opens the Privacy & Security pane that lets Lunalyr hear system audio"
            case .compatibilityCapture: return "Uses Screen Recording permission instead of System Audio Recording."
            }
        }
    }

    /// The fixes the controller offers, only once capture needs attention.
    static func fixes(for state: AudioCaptureState, offersReconnect: Bool, suggestsPrivacySettings: Bool,
                      offersCompatibilityCapture: Bool) -> [Fix] {
        guard state.needsAttention else { return [] }
        var fixes: [Fix] = []
        if offersReconnect { fixes.append(.reconnect) }
        if suggestsPrivacySettings { fixes.append(.privacySettings) }
        if offersCompatibilityCapture { fixes.append(.compatibilityCapture) }
        return fixes
    }
}

// MARK: - Mirroring

/// The lyrics are off the screens while a display is mirrored, for
/// presenting. The first time, it asks whether to keep doing that; after
/// that it says so, with Show Anyway for this mirroring.
struct MenuBarMirroringBanner: View {
    @Model(\.extras) private var extras

    var body: some View {
        if extras.asksAboutMirroring {
            StatusBanner(.neutral, title: Self.title, message: Self.question, symbol: Self.symbol, density: .compact) {
                Button("Keep Hiding") { extras.keepHidingWhileMirroring() }
                    .help("Hide the lyrics whenever a display is mirrored")
                Button("Stop Hiding") { extras.stopHidingWhileMirroring() }
                    .help("Keep the lyrics on screen while a display is mirrored")
            }
        } else {
            StatusBanner(.neutral, title: Self.title, message: Self.note, symbol: Self.symbol, density: .compact) {
                Button("Show Anyway") { extras.showLyrics() }
                    .accessibilityLabel(LunalyrExtras.Titles.showLyricsAnyway)
                    .help("Show the lyrics on every display until mirroring ends")
            }
        }
    }

    static let title = "Lyrics hidden while mirroring"
    static let question = "Lunalyr hides them while a display is mirrored, for presenting. Keep doing that?"
    static let note = "They come back when mirroring ends."
    static let symbol = "rectangle.on.rectangle"
}

// MARK: - Power saving

/// Power saving drawing something other than the chosen wallpaper: why,
/// the one-click way out, and the settings behind it. A lighter effect or a
/// held frame rate is told on the Wallpaper page; here only a replaced
/// wallpaper, which is what someone opening the panel would wonder about.
struct MenuBarPowerSavingBanner: View {
    @Model(\.settings) private var settings
    @Environment(\.lunalyrModels) private var models

    var body: some View {
        if let notice = settings.powerSavingNotice {
            StatusBanner(.info, title: "Power saving", message: notice.message, symbol: "leaf.fill", density: .compact) {
                Button(notice.constraint.overrideTitle) { settings.overridePowerSaving(notice.constraint) }
                Button("Performance settings…") { models.resolved.navigation.openSettings(.performance) }
                    .help("Opens Settings at Performance")
            }
        }
    }

    static func shows(chosen: Settings.BackgroundMode, drawn: Settings.BackgroundMode,
                      notice: Settings.PowerSavingNotice?) -> Bool {
        notice != nil && drawn != chosen
    }
}
