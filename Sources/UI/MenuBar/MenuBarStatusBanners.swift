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

/// What needs the person's attention, under the song, each with its fix
/// on the spot: a glyph in the status colour, the title, a line of
/// explanation and mini buttons -- rows in the panel's language, no cards.
/// Nothing at all while all is well: working audio capture, for one, says
/// nothing here.
struct MenuBarStatusBanners: View {
    /// From `raised(…)`; the panel leaves the group out when it is empty.
    let statuses: [MenuBarStatus]
    /// Where the system-audio banner is in a failure and its recovery.
    var audioPhase: MenuBarAudioStatus.Phase = .attention
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ForEach(statuses, id: \.self) { status in
                Group {
                    switch status {
                    case .automation: MenuBarAutomationStatus()
                    case .audio: MenuBarAudioStatus(phase: audioPhase)
                    case .mirroring: MenuBarMirroringBanner()
                    case .powerSaving: MenuBarPowerSavingBanner()
                    }
                }
                // Fades in; leaves at once, so the controls below never
                // glide under the pointer.
                .transition(.asymmetric(insertion: .opacity, removal: .identity))
            }
        }
        .padding(.vertical, 2)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: statuses)
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
    /// A failure, then -- after Reconnect -- the attempt and its success,
    /// in one banner that stays put instead of vanishing while it reconnects
    /// and popping back if that fails.
    enum Phase: Equatable {
        case hidden
        case attention
        case reconnecting
        /// "Hearing the music again", for `recoveredHold`, then hidden.
        case recovered
    }

    /// How long the success shows before the banner goes.
    static let recoveredHold: TimeInterval = 2

    /// The next phase for a new capture state.
    static func phase(after phase: Phase, state: AudioCaptureState) -> Phase {
        if state.needsAttention { return .attention }
        switch (phase, state) {
        case (.attention, .connecting), (.reconnecting, .connecting): return .reconnecting
        case (.reconnecting, .receiving), (.reconnecting, .quiet): return .recovered
        case (.recovered, _): return .recovered
        default: return .hidden
        }
    }

    var phase: Phase = .attention
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
        Group {
            switch phase {
            case .reconnecting:
                MenuPanelStatus(.warning, title: Self.title, message: Self.reconnecting) {
                    ProgressView().controlSize(.mini)
                        .accessibilityLabel(Self.reconnecting)
                }
            case .recovered:
                MenuPanelStatus(.success, title: Self.recovered)
            case .attention, .hidden:
                MenuPanelStatus(.warning, title: Self.title, message: status) {
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
            }
        }
        .onReceive(audio.$status.removeDuplicates()) { status = $0 }
        .onReceive(audio.$state.removeDuplicates()) { state = $0 }
        .onReceive(audio.$usingTap.removeDuplicates()) { usingTap = $0 }
    }

    /// What the failure means for the wallpaper; the controller's status
    /// sentence beneath it says why and what to do.
    static let title = "The visualiser can’t hear the music"
    static let reconnecting = "Reconnecting…"
    static let recovered = "Hearing the music again"

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
            MenuPanelStatus(.neutral, title: Self.title, message: Self.question, symbol: Self.symbol) {
                Button("Keep Hiding") { extras.keepHidingWhileMirroring() }
                    .help("Hide the lyrics whenever a display is mirrored")
                Button("Stop Hiding") { extras.stopHidingWhileMirroring() }
                    .help("Keep the lyrics on screen while a display is mirrored")
            }
        } else {
            MenuPanelStatus(.neutral, title: Self.title, message: Self.note, symbol: Self.symbol) {
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
            MenuPanelStatus(.info, title: "Power saving", message: notice.message, symbol: "leaf.fill") {
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

// MARK: - Players

/// `AutomationStatusBanner` in the panel's language: a player Lunalyr may not
/// read (or not yet), or a browser whose pages it may not script, with the
/// same fixes.
@MainActor
struct MenuBarAutomationStatus: View {
    @Model(\.nowPlaying) private var playing
    @Model(\.players) private var players
    @Environment(\.lunalyrModels) private var models

    var body: some View {
        if let content = AutomationStatusBanner.content(for: playing) {
            MenuPanelStatus(content.undecided ? .info : .warning, title: content.title, message: content.message,
                            symbol: content.undecided ? "lock.shield.fill" : nil) {
                if content.undecided {
                    Button("Allow Access…") { playing.requestAutomationAccess(for: content.player) }
                        .help("macOS asks whether Lunalyr may read \(content.player.displayName)")
                } else {
                    Button("Open Privacy Settings") { models.resolved.navigation.openSystemSettings(.automation) }
                        .help("Opens Privacy & Security › Automation in System Settings")
                }
                if let other = content.alternative {
                    Button("Follow \(other.displayName) Only") { players.choice = .only(other) }
                        .help("Lunalyr stops reading \(content.player.displayName). Set Player back to Automatic to follow both again.")
                }
            }
            // The user may have just changed the setting.
            .onAppear { playing.recheckAutomation() }
        } else if let notice = WebScriptingNotice.notice(for: playing) {
            MenuPanelStatus(.info, title: notice.title, message: notice.message, symbol: "curlybraces") {
                if let other = playing.playerToFollowInstead(of: notice.browser) {
                    Button("Follow \(other.displayName) Only") { players.choice = .only(other) }
                        .help("Lunalyr stops reading \(notice.browser.displayName).")
                }
            }
        }
    }
}

// MARK: - The status row

/// One status in the panel: a glyph in its colour (success and warning use
/// the spec's glyph colours; text stays ink), the title 13 semibold, the
/// explanation 11.5 secondary, and its fixes as mini buttons under the
/// words. No card: the panel's separators group it.
struct MenuPanelStatus<Actions: View>: View {
    let status: LunalyrStatus
    let title: String
    var message: String?
    var symbol: String?
    let actions: Actions
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(_ status: LunalyrStatus, title: String, message: String? = nil, symbol: String? = nil,
         @ViewBuilder actions: () -> Actions) {
        self.status = status
        self.title = title
        self.message = message
        self.symbol = symbol
        self.actions = actions()
    }

    static var glyphWidth: CGFloat { 18 }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Image(systemName: symbol ?? status.symbol)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(Self.glyphColour(status))
                    .frame(width: Self.glyphWidth)
                    .contentTransition(LunalyrDesign.Motion.symbolSwap(reduceMotion: reduceMotion))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Color.studio(.ink))
                        .fixedSize(horizontal: false, vertical: true)
                    if let message {
                        Text(message)
                            .font(LunalyrDesign.Studio.TypeScale.caption)
                            .foregroundStyle(Color.menuPanel(.secondary))
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel([status.spokenName, title, message].compactMap { $0 }.joined(separator: ". "))
            }
            if Actions.self != EmptyView.self {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 6) { actions }
                    VStack(alignment: .leading, spacing: 4) { actions }
                }
                .buttonStyle(.menuPanelMini)
                .padding(.leading, Self.glyphWidth + 6)
            }
        }
        .padding(.horizontal, MenuPanel.inset - 4)
        .frame(maxWidth: .infinity, alignment: .leading)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: [status.rawValue, title, message ?? "", symbol ?? ""])
        .accessibilityElement(children: .contain)
    }

    /// The glyph's colour: the spec's success and warning glyph colours;
    /// the accent for information; secondary for a plain state.
    static func glyphColour(_ status: LunalyrStatus) -> Color {
        switch status {
        case .success: return .studio(.success)
        case .warning, .critical: return .studio(.warning)
        case .info: return .studio(.accent)
        case .neutral: return .menuPanel(.secondary)
        }
    }
}

extension MenuPanelStatus where Actions == EmptyView {
    init(_ status: LunalyrStatus, title: String, message: String? = nil, symbol: String? = nil) {
        self.init(status, title: title, message: message, symbol: symbol) { EmptyView() }
    }
}
