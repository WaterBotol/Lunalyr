import SwiftUI

/// The wallpaper's quick controls in the menu-bar panel, under their own
/// heading, apart from the music transport above: the wallpaper type, the
/// visualiser's scene, then Pause, Hide lyrics and Copy card as controls.
/// Options… opens Settings at Wallpaper for everything else. Each tile says
/// what it acts on to VoiceOver ("Pause Wallpaper"), so it can't be taken
/// for pausing the music, and shows its global shortcut while one is
/// registered.
@MainActor
struct MenuBarWallpaperControls: View {
    @Model(\.settings) private var settings
    @Model(\.extras) private var extras
    @Model(\.hotkeys) private var hotkeys
    @Model(\.nowPlaying) private var playing
    @Environment(\.lunalyrModels) private var models
    @ObservedObject private var musicVideo = MusicVideoController.shared
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let navigation = models.resolved.navigation
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            PanelSectionHeader("Wallpaper") {
                Button("Options…") { navigation.openSettings(.wallpaper) }
                    .buttonStyle(.borderless)
                    .controlSize(.small)
                    .foregroundStyle(Color.lunalyrAccentText)
                    .help("Opens Settings at Wallpaper")
                    .accessibilityLabel("Wallpaper Options…")
            }
            // The panel never opens a file panel: Image or video with nothing
            // imported yet goes to Settings, which has the importer.
            WallpaperModeGrid(chooseImport: { navigation.openSettings(.wallpaper) })
            if settings.backgroundMode == .musicVideo, musicVideo.showingYouTubeID != nil {
                Button("Open video on YouTube") { musicVideo.openOnYouTube() }
                    .buttonStyle(.link)
                    .controlSize(.small)
                    .help("Opens the video in your browser at the song’s position")
            }
            if settings.backgroundMode == .musicVideo, musicVideo.canTapSync {
                HStack(spacing: LunalyrDesign.Spacing.small) {
                    Button(MusicVideoTapSync.buttonTitle(musicVideo.tapState)) { musicVideo.tapSyncButton() }
                        .help("Click, then press any key the moment the singing starts in the video")
                    Spacer(minLength: 0)
                    // A quarter second at a time, kept for this song's video.
                    Button("Video earlier") { musicVideo.videoEarlier() }
                        .help("The video lags the music: show it a quarter second sooner (kept for this video)")
                    Button("Video later") { musicVideo.videoLater() }
                        .help("The video runs ahead of the music: show it a quarter second later (kept for this video)")
                }
                .buttonStyle(.link)
                .controlSize(.small)
                InspectorCaption("Video timing: \(MusicVideoTiming.format(musicVideo.currentOffset))")
                if let note = MusicVideoTapSync.note(musicVideo.tapState) {
                    InspectorCaption(note)
                }
            }
            if settings.backgroundMode == .visualizer {
                MenuBarPickerRow("Scene") {
                    Picker("Scene", selection: $settings.audioVisualizerStyle) {
                        ForEach(AudioVisualizerStyle.allCases) { Text($0.title).tag($0) }
                    }
                    .help(settings.audioVisualizerStyle.detail)
                }
            }
            HStack(spacing: 0) {
                QuickToggle(extras.wallpaperPaused ? "Resume" : "Pause", symbol: extras.pauseActionSymbol,
                            isOn: extras.wallpaperPaused, detail: shortcut(.pauseWallpaper),
                            accessibilityTitle: extras.pauseActionTitle, state: "Wallpaper paused") {
                    extras.toggleWallpaperPause()
                }
                .accessibilityHint(extras.wallpaperPaused ? "Lets the wallpaper move again"
                                   : "Freezes the wallpaper, lyrics included. The music keeps playing.")
                QuickToggle(extras.lyricsVisible ? "Hide lyrics" : "Show lyrics", symbol: extras.lyricsActionSymbol,
                            isOn: !extras.lyricsVisible, detail: shortcut(.toggleLyrics),
                            accessibilityTitle: extras.lyricsActionTitle,
                            state: extras.hiddenByMirroring ? "Lyrics hidden while mirroring" : "Lyrics hidden") {
                    extras.toggleLyrics()
                }
                .accessibilityHint(extras.lyricsVisible ? "Hides the lyrics on every wallpaper"
                                   : extras.hiddenByMirroring ? "Shows the lyrics on every display until mirroring ends"
                                   : "Shows the lyrics again")
                QuickToggle("Lyric card", symbol: "photo.on.rectangle", detail: shortcut(.copyCard),
                            accessibilityTitle: LunalyrExtras.Titles.copyCard) { extras.copyLyricCard() }
                    .disabled(playing.current == nil)
                    .accessibilityHint(playing.current == nil ? "Available while a song plays"
                                       : "Copies a GIF of the cover and the selected lyrics")
            }
            .padding(.top, LunalyrDesign.Spacing.xSmall)
            if extras.wallpaperPaused {
                InspectorCaption(Self.pausedNote)
                    .transition(.opacity)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: extras.wallpaperPaused)
    }

    static let pausedNote = "Paused: your desktop is frozen until you resume it."

    private func shortcut(_ action: GlobalHotkeys.Action) -> String? {
        extras.shortcutsEnabled ? hotkeys.activeLabel(for: action) : nil
    }
}

/// A picker as a panel row: its name at the leading edge, the pop-up at the
/// trailing one, like the rows of a Settings form.
struct MenuBarPickerRow<Control: View>: View {
    let title: String
    let picker: Control

    init(_ title: String, @ViewBuilder picker: () -> Control) {
        self.title = title
        self.picker = picker()
    }

    var body: some View {
        HStack(spacing: LunalyrDesign.Spacing.small) {
            Text(title)
                .accessibilityHidden(true)
            Spacer(minLength: LunalyrDesign.Spacing.small)
            picker
                .labelsHidden()
                .pickerStyle(.menu)
                .controlSize(.small)
                .fixedSize()
        }
    }
}
