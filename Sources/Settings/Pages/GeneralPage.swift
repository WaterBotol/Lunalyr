import ServiceManagement
import SwiftUI

/// General: starting at login, updates, the welcome again, and the window
/// style of Lunalyr's own windows (Standard or Bold).
@MainActor
struct GeneralPage: View {
    @Environment(\.lunalyrModels) private var models
    @Model(\.settings) private var s
    @Model(\.updates) private var updates

    var body: some View {
        ThemedForm {
            ThemedSection("Startup") {
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
            }
            ThemedSection("Updates") {
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
                Toggle("Check for updates automatically", isOn: Binding(
                    get: { updates.automaticallyChecks },
                    set: { updates.setAutomaticallyChecks($0) }))
                    .disabled(!updates.configured)
                InspectorCaption("Once a day. It never installs one without asking.",
                                 more: "Once a day, Lunalyr asks GitHub whether a newer version is out. It never installs one without asking.")
            }
            ThemedSection("Windows") {
                SettingsStylePicker()
            }
            ThemedSection("Welcome") {
                LabeledContent("Music access, a look and the menu bar") {
                    Button("Show Welcome Again…") { models.resolved.navigation.openOnboarding() }
                        .controlSize(.small)
                        .help("Go through Lunalyr’s welcome again")
                }
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
