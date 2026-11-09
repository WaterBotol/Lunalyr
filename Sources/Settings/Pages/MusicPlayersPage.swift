import SwiftUI

/// Music players: which player Lunalyr follows, whether it follows web
/// players in browsers, and whether macOS lets it read each one.
@MainActor
struct MusicPlayersPage: View {
    var scrollRequest: SettingsScrollRequest?
    @Model(\.players) private var players
    @Environment(\.lunalyrModels) private var models

    var body: some View {
        let installed = Set(models.resolved.installedPlayers)
        let offered = PlayerOptions.Choice.offered(installed: installed, current: players.choice,
                                                   webPlayers: players.followsWebPlayers)
        let browsers = PlayerKind.browsers.filter(installed.contains)
        ThemedForm {
            ThemedSection("Music players") {
                if offered.count <= 4 {
                    ChoiceSegments("Player", selection: $players.choice, cases: offered, label: \.title)
                } else {
                    Picker("Player", selection: $players.choice) {
                        ForEach(offered) { Text($0.title).tag($0) }
                    }
                }
                InspectorCaption("Automatic follows whichever player is playing.", more: "Automatic follows whichever player is playing. Choosing one means Lunalyr never sends the others anything.")
                Toggle("Follow web players in browsers", isOn: $players.followsWebPlayers)
                InspectorCaption(Self.webCaption, more: Self.webDetail(browsers))
                PlayerAccessList()
                    .padding(.vertical, LunalyrDesign.Spacing.xSmall)
                InspectorCaption("Lunalyr only reads the song and its position.", more: "Lunalyr reads the song and its position, and controls playback only when you use its buttons. Allow Access asks macOS; once refused, access is turned back on in Privacy & Security › Automation.")
            }
            .id(SettingsAnchor.players)
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .scrollsToSettingsAnchor(scrollRequest)
    }

    static let webCaption = "YouTube Music, SoundCloud and Amazon Music in Safari, Chrome, Arc, Brave or Edge."

    /// How each installed browser lets Lunalyr read its pages.
    static func webDetail(_ browsers: [PlayerKind]) -> String {
        let steps = (browsers.isEmpty ? PlayerKind.browsers : browsers).map { WebScriptingNotice.steps(for: $0) }
        return "Lunalyr reads the song by running a small script in the music tab, which each browser allows only once you turn on Allow JavaScript from Apple Events. "
            + steps.joined(separator: " ")
            + " Lunalyr never changes this setting itself. The Amazon Music app can't be read; its web player can."
    }
}
