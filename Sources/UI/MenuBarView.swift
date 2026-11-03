import SwiftUI

struct MenuBarView: View {
    @EnvironmentObject private var playing: NowPlayingMonitor
    @ObservedObject private var lyrics = LyricsViewModel.shared
    @ObservedObject private var updates = AppUpdates.shared
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 12) {
                Group {
                    if let image = playing.artwork { Image(nsImage: image).resizable().scaledToFill() }
                    else { Image(systemName: "music.note").frame(maxWidth: .infinity, maxHeight: .infinity).background(.quaternary) }
                }.frame(width: 48, height: 48).clipShape(RoundedRectangle(cornerRadius: 9))
                VStack(alignment: .leading, spacing: 4) {
                    Text(playing.current?.title ?? "Lunalyr").font(.headline).lineLimit(2)
                    Text(playing.current?.artist ?? "Play a song in Music").font(.subheadline).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            HStack(spacing: 28) {
                Spacer()
                Button { playing.previousTrack() } label: { Image(systemName: "backward.fill") }
                Button { playing.playPause() } label: { Image(systemName: playing.isPlaying ? "pause.fill" : "play.fill").font(.title3) }
                Button { playing.nextTrack() } label: { Image(systemName: "forward.fill") }
                Spacer()
            }.buttonStyle(.plain).padding(.vertical, 4).disabled(playing.current == nil)
            if let source = lyrics.provenance {
                VStack(alignment: .leading, spacing: 5) {
                    Label(source.source, systemImage: "text.quote")
                    Label(source.precision, systemImage: "clock")
                }.font(.caption).foregroundStyle(.secondary)
            }
            Divider()
            Button { SettingsWindowController.shared.present() } label: {
                Label("Open Lunalyr", systemImage: "rectangle.leadinghalf.inset.filled").frame(maxWidth: .infinity)
            }.buttonStyle(.borderedProminent).tint(.mint)
            HStack {
                Button("Check for updates…") { updates.check() }.disabled(updates.buttonDisabled)
                Spacer()
                Button("Quit") { NSApplication.shared.terminate(nil) }
            }.font(.caption).buttonStyle(.plain).foregroundStyle(.secondary)
        }.padding(18).frame(width: 300)
    }
}
