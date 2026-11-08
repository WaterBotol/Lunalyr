import SwiftUI

/// Music players: which player Lunalyr follows and whether macOS lets it
/// read each one.
@MainActor
struct MusicPlayersPage: View {
    var scrollRequest: SettingsScrollRequest?
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
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .scrollsToSettingsAnchor(scrollRequest)
    }
}
