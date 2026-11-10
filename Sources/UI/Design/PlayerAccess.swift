import AppKit
import SwiftUI

// MARK: - Player access
//
// Whether macOS lets Lunalyr read each music player (Privacy & Security ›
// Automation), with the way to fix it beside each one. Onboarding's access
// step and Settings › General show it:
//
//     PlayerAccessList()          // a row per installed player, kept current
//
// Looking never asks: the status is read silently on appear and whenever
// Lunalyr becomes active again (back from System Settings). macOS's prompt
// appears only from "Allow Access…", and System Settings opens only from
// "Open Privacy Settings", both clicked by the user.

extension PlayerAccessStatus {
    /// Under the player's name, as a sentence.
    var summary: String {
        switch self {
        case .granted: return "Allowed."
        case .denied: return "Not allowed. Turn it on in Privacy & Security › Automation."
        case .notDetermined: return "macOS hasn’t asked yet. Allow Access asks now."
        case .notRunning: return "Not open. macOS asks the first time you play something in it."
        case .notInstalled: return "Not installed."
        case .unknown: return "Checking…"
        }
    }

    var symbol: String {
        switch self {
        case .granted: return "checkmark.circle.fill"
        case .denied: return "xmark.octagon.fill"
        case .notDetermined: return "questionmark.circle.fill"
        case .notRunning: return "circle.dashed"
        case .notInstalled: return "minus.circle"
        case .unknown: return "ellipsis.circle"
        }
    }

    /// How the row is coloured.
    var lunalyrStatus: LunalyrStatus {
        switch self {
        case .granted: return .success
        case .denied: return .warning
        case .notDetermined: return .info
        case .notRunning, .notInstalled, .unknown: return .neutral
        }
    }
}

/// Reads each installed player's access silently, on demand.
@MainActor
final class PlayerAccessModel: ObservableObject {
    @Published private(set) var statuses: [PlayerKind: PlayerAccessStatus] = [:]

    func status(of kind: PlayerKind) -> PlayerAccessStatus { statuses[kind] ?? .unknown }

    /// Never prompts (`NowPlayingMonitor.accessStatus(for:)`).
    func refresh(_ players: [PlayerKind], monitor: NowPlayingMonitor) async {
        for kind in players {
            statuses[kind] = await monitor.accessStatus(for: kind)
        }
    }
}

/// A row per installed player: its access, and the fix where there is one.
struct PlayerAccessList: View {
    @Environment(\.lunalyrModels) private var models
    @Model(\.nowPlaying) private var playing
    @Model(\.players) private var players
    @StateObject private var access = PlayerAccessModel()

    init() {}

    /// Installed players, with browsers and local-file players only while
    /// they are followed: their rows would otherwise offer to ask for
    /// control of an app Lunalyr was not asked to follow.
    static func shown(installed: [PlayerKind], followed: [PlayerKind]) -> [PlayerKind] {
        installed.filter { $0.family == .musicApp || followed.contains($0) }
    }

    var body: some View {
        let models = models.resolved
        let shown = Self.shown(installed: models.installedPlayers, followed: players.followedPlayers)
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            if models.installedPlayers.isEmpty {
                InspectorCaption("Neither Music nor Spotify is installed on this Mac.")
            }
            ForEach(shown) { kind in
                PlayerAccessRow(kind: kind, status: access.status(of: kind),
                                allow: {
                                    playing.requestAutomationAccess(for: kind)
                                },
                                openSettings: { models.navigation.openSystemSettings(.automation) })
            }
        }
        .task(id: shown) { await access.refresh(shown, monitor: playing) }
        // Back from System Settings, or after macOS's prompt was answered.
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            Task { await access.refresh(shown, monitor: playing) }
        }
        .onChange(of: playing.automation) { _, _ in
            Task { await access.refresh(shown, monitor: playing) }
        }
    }
}

/// One player's access with its fix: Allow Access… while macOS hasn't
/// asked, Open Privacy Settings once refused.
struct PlayerAccessRow: View {
    let kind: PlayerKind
    let status: PlayerAccessStatus
    var allow: () -> Void = {}
    var openSettings: () -> Void = {}
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// Allow Access… was clicked and macOS hasn't answered yet: the button
    /// says so and can't be clicked twice.
    @State private var asking = false

    /// Gives up waiting for the answer after this long, so a prompt that was
    /// dismissed some other way can be asked again.
    static let askingTimeout: TimeInterval = 60

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: LunalyrDesign.Spacing.small) {
            Image(systemName: status.symbol)
                .foregroundStyle(status.lunalyrStatus.text)
                .contentTransition(LunalyrDesign.Motion.symbolSwap(reduceMotion: reduceMotion))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(kind.displayName).lunalyrText(.emphasis)
                Text(status.summary).lunalyrText(.caption)
                    .contentTransition(.opacity)
            }
            .accessibilityElement(children: .combine)
            .accessibilityLabel("\(kind.displayName): \(status.summary)")
            Spacer(minLength: LunalyrDesign.Spacing.small)
            switch status {
            case .notDetermined where asking:
                HStack(spacing: 6) {
                    ProgressView().controlSize(.mini)
                    Text(Self.askingTitle).lunalyrText(.caption)
                }
                .accessibilityElement(children: .combine)
                .transition(.opacity)
            case .notDetermined:
                Button("Allow Access…") {
                    asking = true
                    allow()
                }
                .help("macOS asks whether Lunalyr may read \(kind.displayName)")
                .transition(.opacity)
            case .denied:
                Button("Open Privacy Settings", action: openSettings)
                    .help("Opens Privacy & Security › Automation in System Settings")
                    .transition(.opacity)
            default:
                EmptyView()
            }
        }
        .controlSize(.small)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: [String(describing: status), asking ? "asking" : ""])
        .onChange(of: status) { _, _ in asking = false }
        .task(id: asking) {
            guard asking else { return }
            try? await Task.sleep(for: .seconds(Self.askingTimeout))
            guard !Task.isCancelled else { return }
            asking = false
        }
    }

    static let askingTitle = "Asking…"
}
