import SwiftUI

/// General: which player Lunalyr follows and whether macOS lets it read
/// each one, starting at login, updates, and the welcome again.
@MainActor
struct GeneralPage: View {
    var scrollRequest: SettingsScrollRequest?
    @Environment(\.lunalyrModels) private var models
    @Model(\.settings) private var s
    @Model(\.updates) private var updates
    @Model(\.players) private var players

    var body: some View {
        Form {
            Section("Music players") {
                ChoiceSegments("Player", selection: $players.choice) {
                    ForEach(PlayerOptions.Choice.allCases) { Text($0.title).tag($0) }
                }
                InspectorCaption("Automatic follows whichever player is playing. Choosing one means Lunalyr never sends the other anything.")
                PlayerAccessList()
                    .padding(.vertical, LunalyrDesign.Spacing.xSmall)
                InspectorCaption("Lunalyr reads the song and its position, and controls playback only when you use its buttons. Allow Access asks macOS; once refused, access is turned back on in Privacy & Security › Automation.")
            }
            .id(SettingsAnchor.players)
            Section("Appearance") {
                SettingsStylePicker()
                    .padding(.vertical, LunalyrDesign.Spacing.xSmall)
                InspectorCaption("How Lunalyr’s own windows look: Settings, the menu-bar panel and the welcome.")
            }
            Section("Startup") {
                Toggle("Launch at login", isOn: $s.launchAtLogin)
                if let error = s.launchAtLoginError {
                    StatusBanner(.warning, title: "Launch at login didn’t change", message: error)
                }
            }
            Section("Updates") {
                LabeledContent {
                    Button("Check for Updates…") { updates.check() }
                        .disabled(updates.buttonDisabled)
                } label: {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Lunalyr \(Self.version)")
                        Text(updates.status).lunalyrText(.caption)
                    }
                }
                if updates.buttonDisabled {
                    DisabledReason("Available once the update service is ready, or when its current check finishes.")
                }
            }
            Section("Welcome") {
                LabeledContent {
                    Button("Show Welcome Again…") { models.resolved.navigation.openOnboarding() }
                        .help("Go through Lunalyr’s welcome again")
                } label: {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Welcome to Lunalyr")
                        Text("Music access, picking a look and the menu bar, a step at a time.")
                            .lunalyrText(.caption)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .scrollsToSettingsAnchor(scrollRequest)
    }

    static var version: String {
        let info = Bundle.main.infoDictionary ?? [:]
        let short = info["CFBundleShortVersionString"] as? String ?? ""
        let build = info["CFBundleVersion"] as? String
        return build.map { "\(short) (\($0))" } ?? short
    }
}
