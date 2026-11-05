import AppKit
import Combine

/// Where one surface sends the person to another: the menu-bar panel to a
/// Settings page, Settings back to onboarding, a status to the Privacy pane
/// that fixes it. Each surface asks here instead of naming another's
/// controller, so the Settings window, the menu-bar panel and onboarding can
/// change on their own, and tests can record the request instead of opening
/// windows or System Settings.
///
///     navigation.openSettings(.wallpaper)          // menu panel, "Scene settings…"
///     navigation.openOnboarding()                  // Settings, "Show Welcome Again…"
///     navigation.openSystemSettings(.automation)   // only ever from a button the user clicked
///
/// The Settings window shows `settingsRequest` when it changes and calls
/// `consumeSettingsRequest()`; see `SettingsDestination`.
@MainActor
final class LunalyrNavigation: ObservableObject {
    static let shared = LunalyrNavigation()

    /// A Settings page asked for, numbered so asking for the same page twice
    /// still reaches a window that is already open.
    struct SettingsRequest: Equatable {
        let destination: SettingsDestination
        let serial: Int
    }

    /// The latest unconsumed request for a Settings page.
    @Published private(set) var settingsRequest: SettingsRequest?
    private var serial = 0

    /// Brings up the Settings window. The app opens the real window; tests
    /// replace it.
    var presentSettings: @MainActor () -> Void
    /// Brings up onboarding from its first step, for someone who has already
    /// finished it (Settings › General offers it). First run is the app
    /// delegate's job, not this.
    var presentOnboarding: @MainActor () -> Void
    /// Whether onboarding's window is up. Settings defers to it then: the
    /// first run explains the permission prompt before anything can raise it.
    var onboardingIsVisible: @MainActor () -> Bool
    /// Brings onboarding's window forward (opening Settings, or Lunalyr
    /// again from Finder, while it is up).
    var bringOnboardingForward: @MainActor () -> Void
    /// Opens a URL outside Lunalyr: System Settings panes, the project page.
    var openURL: @MainActor (URL) -> Void

    // ONBOARDING BUILDER: the three onboarding defaults below are the
    // onboarding window's (Sources/UI/Onboarding/OnboardingWindow.swift).
    init(presentSettings: @escaping @MainActor () -> Void = { SettingsWindowController.shared.present() },
         presentOnboarding: @escaping @MainActor () -> Void = { OnboardingWindowController.shared.presentAgain() },
         onboardingIsVisible: @escaping @MainActor () -> Bool = { OnboardingWindowController.shared.isVisible },
         bringOnboardingForward: @escaping @MainActor () -> Void = { OnboardingWindowController.shared.bringToFront() },
         openURL: @escaping @MainActor (URL) -> Void = { NSWorkspace.shared.open($0) }) {
        self.presentSettings = presentSettings
        self.presentOnboarding = presentOnboarding
        self.onboardingIsVisible = onboardingIsVisible
        self.bringOnboardingForward = bringOnboardingForward
        self.openURL = openURL
    }

    /// Opens Settings, at `destination` when one is given.
    func openSettings(_ destination: SettingsDestination? = nil) {
        if let destination {
            serial += 1
            settingsRequest = SettingsRequest(destination: destination, serial: serial)
        }
        presentSettings()
    }

    /// The page asked for, once: the Settings window calls this when it
    /// shows a request.
    @discardableResult
    func consumeSettingsRequest() -> SettingsDestination? {
        defer { settingsRequest = nil }
        return settingsRequest?.destination
    }

    func openOnboarding() {
        if onboardingIsVisible() { bringOnboardingForward() } else { presentOnboarding() }
    }

    /// Opens a System Settings pane. Only ever in answer to a click: never
    /// on appear, never from a timer.
    func openSystemSettings(_ pane: SystemSettingsPane) {
        openURL(pane.url)
    }
}

/// The places in Settings other surfaces link to. Stable names: the menu
/// panel, onboarding and tests use them, and the Settings window maps each
/// to wherever that setting lives in its own layout, scrolling to the
/// section when it is part of a page.
enum SettingsDestination: String, CaseIterable, Identifiable {
    /// The wallpaper type and its scene.
    case wallpaper
    /// System audio for visualisers: capture, sensitivity, diagnostics.
    case musicResponse
    /// Layout, typeface, colour, word entrances.
    case lyricStyle
    case animatedCovers
    /// Song offset, lyric search and sources.
    case timing
    case readings
    /// Frame rate, power saving, desktop interaction.
    case performance
    /// Hide and pause, the menu-bar line, shortcuts, lyric card.
    case menuBarAndShortcuts
    /// Launch at login, updates, showing the welcome again.
    case general
    /// Which player Lunalyr follows, and Automation access to it.
    case players
    case about

    var id: String { rawValue }

    /// The page today's window (`SettingsWindowView.Page`) shows for it.
    var legacyPageName: String {
        switch self {
        case .wallpaper, .musicResponse: return "wallpaper"
        case .lyricStyle: return "typography"
        case .animatedCovers: return "covers"
        case .timing: return "lyrics"
        case .readings: return "readings"
        case .performance, .general, .players: return "performance"
        case .menuBarAndShortcuts: return "extras"
        case .about: return "about"
        }
    }
}

/// System Settings panes Lunalyr sends people to, to grant what it needs.
/// For a capture problem, `AudioReactiveController.openPrivacySettings()`
/// already picks the pane that matches the capture method in use.
enum SystemSettingsPane: String, CaseIterable {
    /// Privacy & Security › Automation: reading Music and Spotify.
    case automation
    /// Privacy & Security › Screen & System Audio Recording, System Audio
    /// Recording Only (the system-audio tap).
    case systemAudioRecording
    /// Privacy & Security › Screen & System Audio Recording (compatibility capture).
    case screenRecording
    /// Accessibility › Display (Reduce Motion, Increase Contrast…).
    case displayAccessibility

    var url: URL {
        switch self {
        case .automation: return Self.privacy("Privacy_Automation")
        case .systemAudioRecording: return Self.privacy("Privacy_AudioCapture")
        case .screenRecording: return Self.privacy("Privacy_ScreenCapture")
        case .displayAccessibility:
            return URL(string: "x-apple.systempreferences:com.apple.preference.universalaccess?Seeing_Display")!
        }
    }

    private static func privacy(_ anchor: String) -> URL {
        URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)")!
    }
}
