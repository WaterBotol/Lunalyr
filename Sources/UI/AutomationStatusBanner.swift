import AppKit
import SwiftUI

/// Says why nothing is showing when macOS will not let Lunalyr read a
/// player, and how to fix it. A refusal used to look exactly like nothing
/// playing. Renders nothing unless a running player is blocked and no
/// other player is on screen. A shared component: the menu-bar panel, the
/// Settings stage and onboarding show the same banner.
///
///     AutomationStatusBanner()                     // Settings
///     AutomationStatusBanner(density: .compact)    // the menu-bar panel
@MainActor
struct AutomationStatusBanner: View {
    var density: PanelDensity = .regular
    @Model(\.nowPlaying) private var playing
    @Model(\.players) private var players
    @Environment(\.lunalyrModels) private var models

    init(density: PanelDensity = .regular) {
        self.density = density
    }

    /// Privacy & Security › Automation.
    static let privacySettingsURL = SystemSettingsPane.automation.url

    /// What the banner says, when it shows.
    struct Content: Equatable {
        /// The player Lunalyr can't read.
        var player: PlayerKind
        /// macOS has not asked yet: an invitation, not a fault.
        var undecided: Bool
        /// Offered beside the fix: following this player alone instead,
        /// for someone who doesn't use `player` with Lunalyr. With the
        /// default Automatic choice a refused player otherwise stays
        /// followed, and is pointed out every time nothing plays. Never a
        /// player that is refused too.
        var alternative: PlayerKind?

        var title: String {
            undecided ? "Lunalyr needs permission to read \(player.displayName)"
                : "Lunalyr can’t read \(player.displayName)"
        }

        var message: String {
            undecided
                ? "macOS asks once, when you choose Allow Access or next play something in \(player.displayName). Lunalyr only reads the song and its position, and controls playback only when you use its buttons."
                : "Turn on \(player.displayName) for Lunalyr in System Settings › Privacy & Security › Automation."
        }
    }

    /// The refused player the wallpaper names, if it names one, so the two
    /// agree about which player may be playing; else any blocked player.
    static func content(for playing: NowPlayingMonitor) -> Content? {
        guard playing.current == nil,
              let kind = playing.idleRefusal ?? playing.deniedPlayers.first ?? playing.blockedPlayers.first
        else { return nil }
        return Content(player: kind, undecided: playing.automation[kind] == .needsConsent,
                       alternative: playing.playerToFollowInstead(of: kind))
    }

    var body: some View {
        if let content = Self.content(for: playing) {
            // Not asked yet is an invitation, not a fault: Lunalyr waits to
            // be asked rather than raise macOS's prompt for a player the
            // user may only have open.
            StatusBanner(content.undecided ? .info : .warning, title: content.title, message: content.message,
                         symbol: content.undecided ? "lock.shield.fill" : nil, density: density) {
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
            // A browser Lunalyr may control, whose pages it may not run a
            // script in. Only the user can change that, so this only says how.
            StatusBanner(.info, title: notice.title, message: notice.message, symbol: "curlybraces", density: density) {
                if let other = playing.playerToFollowInstead(of: notice.browser) {
                    Button("Follow \(other.displayName) Only") { players.choice = .only(other) }
                        .help("Lunalyr stops reading \(notice.browser.displayName).")
                }
            }
        }
    }

    static func openPrivacySettings() {
        NSWorkspace.shared.open(privacySettingsURL)
    }
}
