import SwiftUI

// The panel's rows under the Look switcher (SPEC.md section 7, items 4-6):
// Lyric timing and Tap to sync…, the Wallpaper and Player submenus (with
// the visualiser's Scene and the music video's rows beneath Wallpaper while
// they apply), the Pause / Hide lyrics / Floating switches, and Open Lyric
// Card….

/// "Lyric timing   Earlier · 0.00 s · Later": this song's lyrics a tenth
/// of a second earlier or later, the per-song, per-source offset the
/// shortcuts and Timing & sources change too; then Tap to sync…. Both
/// belong to a song: shown, but disabled, with nothing playing.
struct MenuBarLyricTiming: View {
    @ObservedObject private var tapSync = LyricTapSyncController.shared
    @Model(\.offsets) private var offsets
    @Model(\.nowPlaying) private var playing
    @Environment(\.lunalyrModels) private var models
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let models = models.resolved
        let hasSong = playing.current != nil
        let offset = models.songOffset
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Text("Lyric timing")
                    .font(MenuPanel.rowFont)
                    .foregroundStyle(Color.studio(.ink).opacity(hasSong ? 1 : 0.4))
                    .accessibilityHidden(true)
                Spacer(minLength: 6)
                if offset != 0 {
                    Button {
                        models.resetSongOffset()
                    } label: {
                        Image(systemName: "arrow.counterclockwise")
                    }
                    .buttonStyle(.menuPanelMini)
                    .help("Back to the lyrics’ own timing")
                    .accessibilityLabel("Reset song offset")
                }
                Button("Earlier") { models.nudgeSongOffset(by: 0.1) }
                    .buttonStyle(.menuPanelMini)
                    .help("Show lyrics a tenth of a second earlier for this song")
                    .accessibilityLabel("Lyrics earlier")
                    .accessibilityHint("Shows lyrics a tenth of a second earlier for this song")
                Text(TimingNudge.format(offset))
                    .font(.system(size: 13).monospacedDigit())
                    .foregroundStyle(Color.studio(.ink).opacity(hasSong ? 1 : 0.4))
                    .contentTransition(reduceMotion ? .opacity : .numericText(value: offset))
                    .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                               value: offset)
                    .frame(minWidth: 50)
                    .accessibilityHidden(true)
                Button("Later") { models.nudgeSongOffset(by: -0.1) }
                    .buttonStyle(.menuPanelMini)
                    .help("Show lyrics a tenth of a second later for this song")
                    .accessibilityLabel("Lyrics later")
                    .accessibilityHint("Shows lyrics a tenth of a second later for this song")
            }
            .padding(.horizontal, MenuPanel.inset)
            .frame(height: 28)
            .disabled(!hasSong)
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Lyric timing for this song")
            .accessibilityValue(TimingNudge.spoken(offset))
            // Tap to sync: a key pressed as each line starts sets the timing.
            MenuPanelRow(tapSync.isListening ? "Stop tap to sync" : "Tap to sync…") { tapSync.toggle() }
                .help("Press any key each time a lyric line starts; Lunalyr sets this song’s timing from the taps")
                .disabled(!hasSong && !tapSync.isListening)
            if tapSync.canUndo {
                MenuPanelRow("Undo tap sync") { tapSync.undo() }
                    .help("Back to the timing from before the last tap to sync")
            }
        }
    }
}

/// Wallpaper › (the eight types, the chosen one checked) and, while they
/// apply, the visualiser's Scene › and the music video's rows; then
/// Player ›. Image or video with nothing imported opens Settings at
/// Wallpaper, which has the importer: the panel never opens a file panel.
@MainActor
struct MenuBarWallpaperControls: View {
    @Model(\.settings) private var settings
    @Model(\.players) private var options
    @Environment(\.lunalyrModels) private var models
    @ObservedObject private var musicVideo = MusicVideoController.shared

    var body: some View {
        let navigation = models.resolved.navigation
        VStack(alignment: .leading, spacing: 0) {
            MenuPanelSubmenuRow("Wallpaper", value: settings.backgroundMode.title) {
                // Everything else about the wallpaper, as the panel's
                // Options… link opened it.
                Self.wallpaperItems(settings: settings) { navigation.openSettings(.wallpaper) }
                    + [.separator(), MenuPanelMenuItem(id: "options", title: "Wallpaper Settings…",
                                                       help: "Opens Settings at Wallpaper") {
                        navigation.openSettings(.wallpaper)
                    }]
            }
            if settings.backgroundMode == .visualizer {
                MenuPanelSubmenuRow("Scene", value: settings.audioVisualizerStyle.title) {
                    AudioVisualizerStyle.allCases.map { style in
                        MenuPanelMenuItem(id: "\(style)", title: style.title,
                                          checked: settings.audioVisualizerStyle == style,
                                          help: style.detail) { settings.audioVisualizerStyle = style }
                    }
                }
                .help(settings.audioVisualizerStyle.detail)
            }
            if settings.backgroundMode == .musicVideo {
                MenuBarMusicVideoRows(musicVideo: musicVideo)
            }
            MenuPanelSubmenuRow("Player", value: options.choice.title) {
                PlayerOptions.Choice.offered(installed: Set(models.resolved.installedPlayers), current: options.choice,
                                             webPlayers: options.followsWebPlayers,
                                             localPlayers: options.followsLocalPlayers).map { choice in
                    MenuPanelMenuItem(id: "\(choice)", title: choice.title, checked: options.choice == choice) {
                        options.choice = choice
                    }
                }
            }
            .help("Which music player Lunalyr follows")
        }
    }

    /// The Wallpaper submenu: Lunalyr's five, then the three that start
    /// from something of the person's, the chosen one checked; choosing one
    /// does what a tile of `WallpaperModeGrid` does.
    static func wallpaperItems(settings: Settings, chooseImport: @escaping () -> Void) -> [MenuPanelMenuItem] {
        let modes = Settings.BackgroundMode.generated + Settings.BackgroundMode.own
        return modes.map { mode in
            var title = mode.title
            // Power saving draws this one instead of the choice.
            if settings.effectiveBackgroundMode == mode && settings.backgroundMode != mode {
                title += " (power saving)"
            }
            return MenuPanelMenuItem(id: "\(mode)", title: title, checked: settings.backgroundMode == mode,
                                     help: mode.summary) {
                WallpaperModeGrid.choose(mode, settings: settings, chooseImport: chooseImport)
            }
        }
    }
}

/// The music video's rows while it is the wallpaper: its timing against the
/// song (Video earlier / later, a quarter second a click), Tap to sync and
/// Align… for the video, Search YouTube… and Open video on YouTube.
struct MenuBarMusicVideoRows: View {
    @ObservedObject var musicVideo: MusicVideoController

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if musicVideo.canTapSync {
                HStack(spacing: 6) {
                    Text("Video timing")
                        .font(MenuPanel.rowFont)
                        .foregroundStyle(Color.studio(.ink))
                        .accessibilityHidden(true)
                    Spacer(minLength: 6)
                    // A quarter second at a time, kept for this song's video.
                    Button("Earlier") { musicVideo.videoEarlier() }
                        .buttonStyle(.menuPanelMini)
                        .help("The video lags the music: show it a quarter second sooner (kept for this video)")
                        .accessibilityLabel("Video earlier")
                    Text(MusicVideoTiming.format(musicVideo.currentOffset))
                        .font(.system(size: 13).monospacedDigit())
                        .foregroundStyle(Color.studio(.ink))
                        .frame(minWidth: 50)
                        .accessibilityHidden(true)
                    Button("Later") { musicVideo.videoLater() }
                        .buttonStyle(.menuPanelMini)
                        .help("The video runs ahead of the music: show it a quarter second later (kept for this video)")
                        .accessibilityLabel("Video later")
                }
                .padding(.horizontal, MenuPanel.inset)
                .frame(height: 28)
                .accessibilityElement(children: .contain)
                .accessibilityLabel("Video timing")
                .accessibilityValue(MusicVideoTiming.format(musicVideo.currentOffset))
                MenuPanelRow(MusicVideoTapSync.buttonTitle(musicVideo.tapState)) { musicVideo.tapSyncButton() }
                    .help("Click, then press any key the moment the singing starts in the video")
                // Drag the video in the desktop player (same offset as the nudges).
                MenuPanelRow(musicVideo.isAligning ? "Aligning…" : "Align…") { startAlign() }
                    .disabled(!musicVideo.isAligning && alignReason != nil)
                    .help(alignReason
                          ?? "Drag the video\u{2019}s own bar in the desktop player until the picture matches the music (kept for this video)")
                if let note = MusicVideoTapSync.note(musicVideo.tapState) {
                    MenuPanelCaption(note)
                }
            }
            if musicVideo.hasSong {
                // Wrong upload? Pick another from YouTube's results.
                MenuPanelRow("Search YouTube…") { MusicVideoSearchWindowController.shared.show() }
                    .help("Search YouTube for this song and pick the video to use")
                if musicVideo.showingYouTubeID != nil {
                    MenuPanelRow("Open video on YouTube") { musicVideo.openOnYouTube() }
                        .help("Opens the video in your browser at the song’s position")
                }
            }
        }
    }

    /// Why Align… can't start, nil when it can: it needs the desktop player.
    private var alignReason: String? {
        guard DesktopOptions.shared.showsControlsOnHover else {
            return "Turn on Settings > Performance > Show player on hover to drag the video on the desktop"
        }
        return musicVideo.alignAvailability.reason
    }

    /// Align mode, with the desktop player brought up to drag the video in.
    private func startAlign() {
        guard musicVideo.beginAlign() else { return }
        HoverState.shared.pinForAlign()
    }
}

/// Pause, Hide lyrics and Floating as switch rows (each says what it acts
/// on to VoiceOver, "Pause Wallpaper", so it can't be taken for pausing the
/// music, and shows its global shortcut while one is registered); Edit
/// position… beneath Floating while the lyrics float; a note while the
/// wallpaper is paused.
@MainActor
struct MenuBarToggles: View {
    @Model(\.extras) private var extras
    @Model(\.hotkeys) private var hotkeys
    @ObservedObject private var floating = FloatingLyricsOptions.shared
    @ObservedObject private var floatingState = FloatingLyricsController.shared.state
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            QuickToggle("Pause", isOn: extras.wallpaperPaused, detail: shortcut(.pauseWallpaper),
                        accessibilityTitle: extras.pauseActionTitle, state: "Wallpaper paused") {
                extras.toggleWallpaperPause()
            }
            .accessibilityHint(extras.wallpaperPaused ? "Lets the wallpaper move again"
                               : "Freezes the wallpaper, lyrics included. The music keeps playing.")
            if extras.wallpaperPaused {
                MenuPanelCaption(Self.pausedNote)
                    .transition(.opacity)
            }
            QuickToggle("Hide lyrics", isOn: !extras.lyricsVisible, detail: shortcut(.toggleLyrics),
                        accessibilityTitle: extras.lyricsActionTitle,
                        state: extras.hiddenByMirroring ? "Lyrics hidden while mirroring" : "Lyrics hidden") {
                extras.toggleLyrics()
            }
            .accessibilityHint(extras.lyricsVisible ? "Hides the lyrics on every wallpaper"
                               : extras.hiddenByMirroring ? "Shows the lyrics on every display until mirroring ends"
                               : "Shows the lyrics again")
            // Lyrics over the apps instead of on the desktop.
            QuickToggle("Floating", isOn: floating.isFloating, accessibilityTitle: "Floating lyrics",
                        state: "Lyrics floating over apps") {
                floating.toggleSurface()
            }
            .accessibilityHint(floating.isFloating ? "Puts the lyrics back on the desktop"
                               : "Shows the lyrics over every app instead of on the desktop")
            if floating.isFloating {
                MenuPanelRow(floatingState.editing ? "Done placing lyrics" : "Edit position…") {
                    FloatingLyricsController.shared.toggleEditing()
                }
                .help(floatingState.editing ? "Locks the floating lyrics in place"
                      : "Drag the floating lyrics to move them, or their corner to resize")
                .transition(.opacity)
            }
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: extras.wallpaperPaused)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: floating.isFloating)
    }

    static let pausedNote = "Paused: your desktop is frozen until you resume it."

    private func shortcut(_ action: GlobalHotkeys.Action) -> String? {
        extras.shortcutsEnabled ? hotkeys.activeLabel(for: action) : nil
    }
}

/// Open Lyric Card…: an action, not a toggle -- it opens the lyric-card
/// window for the line playing (the same name in Settings, the menu-bar
/// line's menu and the shortcut). Disabled with nothing playing.
@MainActor
struct MenuBarLyricCardRow: View {
    @Model(\.extras) private var extras
    @Model(\.hotkeys) private var hotkeys
    @Model(\.nowPlaying) private var playing
    /// Nil: `LunalyrExtras.copyLyricCard()`, which opens the window.
    var action: (() -> Void)?

    static let title = LunalyrExtras.Titles.copyCard

    static func isEnabled(hasTrack: Bool) -> Bool { hasTrack }

    /// What a click does: opens the window while a song plays; nothing
    /// otherwise (the row is disabled then too). True when it opened.
    @discardableResult
    static func press(hasTrack: Bool, open: () -> Void) -> Bool {
        guard isEnabled(hasTrack: hasTrack) else { return false }
        open()
        return true
    }

    var body: some View {
        let enabled = Self.isEnabled(hasTrack: playing.current != nil)
        MenuPanelRow(Self.title, value: extras.shortcutsEnabled ? hotkeys.activeLabel(for: .copyCard) : nil) {
            Self.press(hasTrack: playing.current != nil, open: action ?? { extras.copyLyricCard() })
        }
        .disabled(!enabled)
        .accessibilityHint(enabled ? "Opens the lyric card window for the line playing" : "Available while a song plays")
    }
}
