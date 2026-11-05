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
                QuickToggle("Copy card", symbol: "photo.on.rectangle", detail: shortcut(.copyCard),
                            accessibilityTitle: LunalyrExtras.Titles.copyCard) { extras.copyLyricCard() }
                    .disabled(playing.current == nil)
                    .accessibilityHint(playing.current == nil ? "Available while a song plays"
                                       : "Copies a GIF of the cover and the selected lyrics")
            }
            .padding(.top, LunalyrDesign.Spacing.xSmall)
            if extras.wallpaperPaused {
                InspectorCaption(Self.pausedNote)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
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
