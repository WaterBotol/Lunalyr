import ServiceManagement
import SwiftUI

/// General: the music players Lunalyr follows, what shows on screen,
/// global shortcuts, and the app itself (starting at login, updates, the
/// window style; checking now, the welcome again and About under More
/// options). Music players, Menu bar & shortcuts and About were pages of
/// their own before.
@MainActor
struct GeneralPage: View {
    var scrollRequest: SettingsScrollRequest?
    @Environment(\.lunalyrModels) private var models
    @Model(\.settings) private var s
    @Model(\.updates) private var updates
    @State private var showsAbout = false

    /// The App section's button that opens About.
    static let aboutTitle = "About Lunalyr…"

    init(scrollRequest: SettingsScrollRequest? = nil) {
        self.scrollRequest = scrollRequest
    }

    var body: some View {
        ThemedForm {
            MusicPlayersSection()
            ExtrasInspector()
            app
                .id(SettingsAnchor.app)
        }
        .scrollsToSettingsAnchor(scrollRequest)
        .sheet(isPresented: $showsAbout) {
            AboutSheet()
        }
    }

    private var app: some View {
        ThemedSection("App") {
            Toggle("Launch at login", isOn: $s.launchAtLogin)
            if let error = s.launchAtLoginError {
                if s.launchAtLoginNeedsApproval {
                    StatusBanner(.warning, title: "Waiting for your approval", message: error) {
                        Button("Open Login Items…") { SMAppService.openSystemSettingsLoginItems() }
                            .help("Opens System Settings › General › Login Items")
                    }
                } else {
                    StatusBanner(.warning, title: "Launch at login didn’t change", message: error)
                }
            }
            Toggle("Check for updates automatically", isOn: Binding(
                get: { updates.automaticallyChecks },
                set: { updates.setAutomaticallyChecks($0) }))
                .disabled(!updates.configured)
            InspectorCaption("Once a day. It never installs one without asking.",
                             more: "Once a day, Lunalyr asks GitHub whether a newer version is out. It never installs one without asking.")
            // The update the daily check found, waiting, isn't tucked away.
            if updates.badge.pendingVersion != nil {
                versionRow
            }
            SettingsStylePicker()
            MoreOptions {
                if updates.badge.pendingVersion == nil {
                    versionRow
                }
                LabeledContent("Music access, a look and the menu bar") {
                    Button("Show Welcome Again…") { models.resolved.navigation.openOnboarding() }
                        .controlSize(.small)
                        .help("Go through Lunalyr’s welcome again")
                }
                LabeledContent("Version, credits and licences") {
                    Button(Self.aboutTitle) { showsAbout = true }
                        .controlSize(.small)
                        .help("What Lunalyr is built on, its project page and what stays on this Mac")
                }
            }
        }
    }

    /// The version and the update status, with Check for Updates… (or
    /// Update… while one waits).
    private var versionRow: some View {
        HStack(alignment: .center, spacing: 10) {
            VStack(alignment: .leading, spacing: 1) {
                Text("Lunalyr \(Self.version)")
                    .foregroundStyle(Color.studio(.ink))
                Text(updates.status)
                    .font(LunalyrDesign.Studio.TypeScale.caption)
                    .foregroundStyle(Color.studio(.ink2))
            }
            .accessibilityElement(children: .combine)
            Spacer(minLength: 8)
            if updates.badge.pendingVersion != nil {
                // The update the daily check found, waiting.
                Button("Update…") { updates.check() }
                    .controlSize(.small)
                    .help("See what’s new and install the update")
            } else {
                Button("Check for Updates…") { updates.check() }
                    .controlSize(.small)
                    .disabled(updates.buttonDisabled)
                    .help(updates.buttonDisabled ? "Available once the update service is ready, or when its current check finishes." : "")
            }
        }
    }

    static var version: String {
        let info = Bundle.main.infoDictionary ?? [:]
        let short = info["CFBundleShortVersionString"] as? String ?? ""
        let build = info["CFBundleVersion"] as? String
        return build.map { "\(short) (\($0))" } ?? short
    }
}
