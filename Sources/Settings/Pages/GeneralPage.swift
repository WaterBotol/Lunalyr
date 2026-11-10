import ServiceManagement
import SwiftUI

/// General: how Lunalyr's own windows look, starting at login, updates, and
/// the welcome again. The first page drawn with the Calm and Bold components
/// (design/spec.md); long explanations sit behind the ⓘ buttons.
@MainActor
struct GeneralPage: View {
    @Environment(\.lunalyrModels) private var models
    @Model(\.settings) private var s
    @Model(\.updates) private var updates

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                ThemedCard("Look", info: "How Lunalyr’s own windows look: Settings, the menu-bar panel and the welcome. It doesn’t change your wallpaper.") {
                    SettingsStylePicker()
                }
                ThemedCard("Startup") {
                    Toggle("Launch at login", isOn: $s.launchAtLogin)
                        .toggleStyle(ThemedToggleStyle())
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
                ThemedCard("Updates") {
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Lunalyr \(Self.version)")
                            Text(updates.status).lunalyrText(.caption)
                        }
                        Spacer()
                        Button("Check for Updates…") { updates.check() }
                            .buttonStyle(ThemedButtonStyle())
                            .disabled(updates.buttonDisabled)
                            .help(updates.buttonDisabled ? "Available once the update service is ready, or when its current check finishes." : "")
                    }
                }
                ThemedCard("Welcome") {
                    HStack {
                        Text("Music access, a look and the menu bar.")
                        Spacer()
                        Button("Show Welcome Again…") { models.resolved.navigation.openOnboarding() }
                            .buttonStyle(ThemedButtonStyle())
                            .help("Go through Lunalyr’s welcome again")
                    }
                }
            }
            .padding(20)
            .frame(maxWidth: .infinity)
        }
        .scrollContentBackground(.hidden)
    }

    static var version: String {
        let info = Bundle.main.infoDictionary ?? [:]
        let short = info["CFBundleShortVersionString"] as? String ?? ""
        let build = info["CFBundleVersion"] as? String
        return build.map { "\(short) (\($0))" } ?? short
    }
}
