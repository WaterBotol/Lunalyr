import SwiftUI
@testable import VersoCustom

/// The menu-bar panel in each state it shows. Owned by the menu-bar
/// builder; see UISnapshotTests.swift for how to add a case. Every case is
/// also held under the panel's height budget by
/// `MenuBarTests.testEveryPanelStateFitsTheHeightBudget`.
@MainActor
extension UISnapshotCatalogue {
    static var menuBar: [UISnapshotCase] {
        let contrast: [UISnapshotRenderer.Look] = [.lightContrast, .darkContrast]
        return [
            // The states gui/spec.md §9 asks for.
            UISnapshotCase("menu-bar/idle", note: "Music open, nothing playing", chrome: .menuBarPanel, size: .menuBarPanel,
                           view: menuBarPanel),
            UISnapshotCase("menu-bar/not-running", note: "no player open", chrome: .menuBarPanel, size: .menuBarPanel,
                           player: .notRunning, view: menuBarPanel),
            UISnapshotCase("menu-bar/playing", note: "a song playing, a line being sung, the song 0.25 s earlier",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.setSongOffset(0.25)
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/paused", note: "music and wallpaper paused", chrome: .menuBarPanel, size: .menuBarPanel,
                           player: .paused,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.extras.wallpaperPaused = true
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/denied", note: "Music refused in Automation", chrome: .menuBarPanel, size: .menuBarPanel,
                           player: .denied, view: menuBarPanel),
            UISnapshotCase("menu-bar/needs-consent", note: "macOS hasn't asked about Music yet", chrome: .menuBarPanel,
                           size: .menuBarPanel, player: .needsConsent, view: menuBarPanel),
            UISnapshotCase("menu-bar/visualiser-audio", note: "Visualisers with System audio on: the scene picker",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.settings.backgroundMode = .visualizer
                               scene.settings.audioVisualizerStyle = .starTrails
                               scene.settings.audioReactiveEnabled = true
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/audio-attention", note: "System audio failing: the warning and its fixes",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.audioNeedsAttention()
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/mirroring", note: "lyrics hidden while mirroring, asked the first time",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.hideLyricsForMirroring()
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/power-saving", note: "Low power visuals drawing Line patterns instead of GPU effects",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.settings.backgroundMode = .shader
                               scene.settings.lowPowerVisuals = true
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/contrast", note: "Increase Contrast, a song playing", chrome: .menuBarPanel,
                           size: .menuBarPanel, looks: contrast, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.setSongOffset(0.25)
                           }, view: menuBarPanel),

            // Further states the panel draws.
            UISnapshotCase("menu-bar/contrast-statuses", note: "Increase Contrast, Music refused and power saving",
                           chrome: .menuBarPanel, size: .menuBarPanel, looks: contrast, player: .denied,
                           prepare: { scene in
                               scene.settings.backgroundMode = .visualizer
                               scene.settings.lowPowerVisuals = true
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/mirroring-answered", note: "lyrics hidden while mirroring, after the question",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.hideLyricsForMirroring(asked: true)
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/lyrics-hidden", note: "lyrics hidden by the person: the panel doesn't quote them",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.extras.lyricsHidden = true
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/shortcuts", note: "global shortcuts on: each tile shows its keys",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.extras.shortcutsEnabled = true
                               scene.hotkeys.setEnabled(true)
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/looking-for-lyrics", note: "a new song, lyrics being looked up",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { $0.lyricNow = LyricNow(isLoading: true) }, view: menuBarPanel),
            UISnapshotCase("menu-bar/no-synced-lyrics", note: "the lookup found no timed lyrics",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { $0.lyricNow = LyricNow(notFound: true) }, view: menuBarPanel),
            UISnapshotCase("menu-bar/between-lines", note: "an instrumental break: a note between lines",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { $0.lyricNow = LyricNow(next: "We were never meant to stay", source: "LRCLIB",
                                                             precision: "Word by word") }, view: menuBarPanel),
            UISnapshotCase("menu-bar/two-statuses", note: "four statuses raised: the panel shows the first two",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .denied,
                           prepare: { scene in
                               scene.settings.backgroundMode = .shader
                               scene.settings.lowPowerVisuals = true
                               scene.hideLyricsForMirroring()
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/update-available", note: "the daily check found an update: the row at the top",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = menuBarSinging
                               scene.updates.receive(.found(version: "2.1.0", userInitiated: false, sparkleShowsIt: false))
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/update-and-statuses", note: "an update waiting, with two statuses raised",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .denied,
                           prepare: { scene in
                               scene.settings.backgroundMode = .shader
                               scene.settings.lowPowerVisuals = true
                               scene.updates.receive(.found(version: "2.1.0", userInitiated: false, sparkleShowsIt: false))
                           }, view: menuBarPanel),
            UISnapshotCase("menu-bar/busiest", note: "the tallest: two statuses, the scene picker, a paused wallpaper",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .needsConsent,
                           prepare: { scene in
                               scene.audioNeedsAttention()
                               scene.settings.audioVisualizerStyle = .wavefieldSphere
                               scene.extras.wallpaperPaused = true
                               scene.hideLyricsForMirroring()
                           }, view: menuBarPanel),
        ]
    }

    /// The line being sung in the playing states.
    static let menuBarSinging = LyricNow(line: "Static on the radio, gold across the bay", next: "We were never meant to stay",
                                  source: "LRCLIB", precision: "Word by word")

    static func menuBarPanel(_ scene: UISnapshotScene) -> some View {
        MenuBarView()
    }
}
