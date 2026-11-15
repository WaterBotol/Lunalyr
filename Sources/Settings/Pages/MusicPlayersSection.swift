import SwiftUI

/// General › Music players: which player Lunalyr follows and whether it
/// follows web and local-file players; whether macOS lets it read each one
/// under More options. A section in General's form.
@MainActor
struct MusicPlayersSection: View {
    @Model(\.players) private var players
    @Environment(\.lunalyrModels) private var models

    var body: some View {
        let installed = Set(models.resolved.installedPlayers)
        let offered = PlayerOptions.Choice.offered(installed: installed, current: players.choice,
                                                   webPlayers: players.followsWebPlayers,
                                                   localPlayers: players.followsLocalPlayers)
        let browsers = PlayerKind.browsers.filter(installed.contains)
        let localPlayers = PlayerKind.localPlayers.filter(installed.contains)
        ThemedSection("Music players") {
            if offered.count <= 4 {
                ChoiceSegments("Player", selection: $players.choice, cases: offered, label: \.title)
            } else {
                ChoiceMenu("Player", selection: $players.choice, selectedTitle: \.title) {
                    ForEach(offered) { Text($0.title).tag($0) }
                }
            }
            InspectorCaption("Automatic follows whichever player is playing.", more: "Automatic follows whichever player is playing. Choosing one means Lunalyr never sends the others anything.")
            Toggle("Follow web players in browsers", isOn: $players.followsWebPlayers)
            InspectorCaption(Self.webCaption, more: Self.webDetail(browsers))
            if !localPlayers.isEmpty || players.followsLocalPlayers {
                Toggle(Self.localTitle(localPlayers), isOn: $players.followsLocalPlayers)
                InspectorCaption(Self.localCaption, more: Self.localDetail)
            }
            MoreOptions {
                PlayerAccessList()
                InspectorCaption("Lunalyr only reads the song and its position.", more: "Lunalyr reads the song and its position, and controls playback only when you use its buttons. Allow Access asks macOS; once refused, access is turned back on in Privacy & Security › Automation.")
                    .environment(\.captionFollowsControl, false)
                Toggle(Self.lookAheadTitle, isOn: $players.looksAhead)
                InspectorCaption(Self.lookAheadCaption, more: Self.lookAheadDetail)
            }
        }
        .id(SettingsAnchor.players)
    }

    /// "Follow VLC and VOX": the local-file players on this Mac.
    static func localTitle(_ installed: [PlayerKind]) -> String {
        "Follow " + OnboardingCopy.list((installed.isEmpty ? PlayerKind.localPlayers : installed).map(\.displayName))
    }

    static let localCaption = "Lyrics are looked up by the title or file name playing."
    static let localDetail = "Lunalyr reads the title playing in VLC, Swinsian or VOX, or the file name when there is no title, and looks its lyrics up with lyric services such as LRCLIB under that name. Leave this off if you play personal videos in them."

    static let lookAheadTitle = "Look ahead in the queue"
    static let lookAheadCaption = "Lyrics and videos for the next songs in Music are ready when they start."
    static let lookAheadDetail = "Lunalyr reads Music’s Up Next on your Mac and looks up the next few songs before they play, the way it looks up the playing one: lyric services (and, with the music-video wallpaper’s online search on, YouTube) receive their titles and artists a few minutes early, including songs you may skip. It eases off on battery and stops in Low Power Mode or offline. Turn it off to look up only the song that is playing."

    static let webCaption = "YouTube Music, SoundCloud and Amazon Music in Safari, Chrome, Arc, Brave or Edge."

    /// How each installed browser lets Lunalyr read its pages.
    static func webDetail(_ browsers: [PlayerKind]) -> String {
        let steps = (browsers.isEmpty ? PlayerKind.browsers : browsers).map { WebScriptingNotice.steps(for: $0) }
        return "Lunalyr reads the song by running a small script in the music tab, which each browser allows only once you turn on Allow JavaScript from Apple Events. "
            + steps.joined(separator: " ")
            + " Lunalyr never changes this setting itself. The Amazon Music app can't be read; its web player can."
    }
}
