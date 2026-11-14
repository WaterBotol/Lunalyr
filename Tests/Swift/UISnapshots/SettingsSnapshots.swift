import AppKit
import SwiftUI
@testable import VersoCustom

/// The Settings window: every page at the size it first opens with on a
/// 13-inch Air (1102 x 780), each whole page, each page's inspector at its
/// real 380-point width, and the states its banners, chips and notes answer.
/// Owned by the Settings builder; see UISnapshotTests.swift for how to add a
/// case.
@MainActor
extension UISnapshotCatalogue {
    /// The pages' stored values (`custom.settingsPage`), in sidebar order.
    static var settingsPages: [String] {
        SettingsPage.Group.allCases.flatMap(\.pages).map(\.rawValue)
    }

    static var settings: [UISnapshotCase] {
        var cases: [UISnapshotCase] = []
        for page in settingsPages {
            cases.append(UISnapshotCase("settings/\(page)", note: "13-inch default window, nothing playing",
                                        chrome: .settingsWindow, size: .settingsWindow,
                                        prepare: { $0.showSettingsPage(page) }, view: settingsWindow))
            cases.append(UISnapshotCase("settings/\(page)-full", note: "the whole page, preview hidden",
                                        chrome: .settingsWindow, size: .settingsPage,
                                        prepare: { $0.showSettingsPage(page, preview: false) }, view: settingsWindow))
        }
        for page in SettingsPage.Group.allCases.flatMap(\.pages) where page.showsStage {
            // The picture pages' inspector is 286 wide; the others' pages 600.
            let width = LookObject(page: page) == nil ? SettingsDetail.columnWidth + 2 * SettingsDetail.formPadding
                : LunalyrDesign.Studio.Layout.inspectorWidth
            cases.append(UISnapshotCase("settings/\(page.rawValue)-inspector",
                                        note: "the whole inspector at its \(Int(width))-point width, a song playing",
                                        chrome: .plain, size: .fitting(width: width),
                                        player: .playing, view: { _ in SettingsInspectorProbe(page: page) }))
        }
        cases += wallpaperStates + otherPageStates + colourStates + lookStates
        return cases
    }

    private static var colourStates: [UISnapshotCase] {
        LyricColourMode.choices.map { mode in
            UISnapshotCase("settings/lyric-colour-\(mode.rawValue)", note: mode.title,
                           chrome: .plain, size: .fitting(width: LunalyrDesign.Layout.inspectorWidth),
                           prepare: { scene in
                               scene.colours.mode = mode
                               if mode == .customGradient { scene.models.highlightColours.mode = .custom }
                           }, view: { _ in Form { LyricColourSection() }.formStyle(.grouped).lunalyrTint() })
        }
    }

    private static var wallpaperStates: [UISnapshotCase] {
        [
            UISnapshotCase("settings/wallpaper-playing", note: "a song playing, with its cover",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { $0.showSettingsPage("wallpaper") }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-visualiser", note: "Visualisers with System audio on",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper")
                               scene.settings.backgroundMode = .visualizer
                               scene.settings.audioReactiveEnabled = true
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-visualiser-full", note: "Visualisers with System audio on, the whole page",
                           chrome: .settingsWindow, size: .settingsPage, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper", preview: false)
                               scene.settings.backgroundMode = .visualizer
                               scene.settings.audioReactiveEnabled = true
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-wavefield-controls",
                           note: "Wavefield’s selectors and sensitivity beside the larger preview",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper", largePreview: true)
                               scene.settings.backgroundMode = .visualizer
                               scene.settings.audioVisualizerStyle = .wavefieldOriginal
                               scene.settings.audioSensitivity = 1.35
                               scene.settings.audioReactiveEnabled = true
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-music-response", note: "opened at Music response (the menu's destination)",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("typography")
                               scene.settings.backgroundMode = .visualizer
                               scene.navigation.openSettings(.musicResponse)
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-audio-attention", note: "System audio failing: the warning and its fixes",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { scene in
                               scene.audioNeedsAttention()
                               scene.navigation.openSettings(.musicResponse)
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-audio-attention-full", note: "System audio failing, the whole page",
                           chrome: .settingsWindow, size: .settingsPage, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper", preview: false)
                               scene.audioNeedsAttention()
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-your-wallpaper", note: "Your wallpaper (lyrics only)",
                           chrome: .settingsWindow, size: .settingsWindow,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper")
                               scene.settings.backgroundMode = .desktop
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-imported", note: "an imported video",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper")
                               scene.settings.customWallpaperPath = "/nonexistent/Dunes at dusk.mov"
                               scene.settings.customWallpaperName = "Dunes at dusk.mov"
                               scene.settings.backgroundMode = .custom
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-power-saving", note: "Low power visuals on: the power-saving banner",
                           chrome: .settingsWindow, size: .settingsWindow,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper")
                               scene.settings.backgroundMode = .shader
                               scene.settings.lowPowerVisuals = true
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-paused", note: "wallpaper paused: the chip above the preview",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper")
                               scene.extras.wallpaperPaused = true
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-paused-hidden-preview", note: "paused, with the preview hidden",
                           chrome: .settingsWindow, size: .settingsWindow, player: .denied,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper", preview: false)
                               scene.extras.wallpaperPaused = true
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-denied", note: "Music refused in Automation",
                           chrome: .settingsWindow, size: .settingsWindow, player: .denied,
                           prepare: { $0.showSettingsPage("wallpaper") }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-needs-consent", note: "macOS hasn't asked about Music yet",
                           chrome: .settingsWindow, size: .settingsWindow, player: .needsConsent,
                           prepare: { $0.showSettingsPage("wallpaper") }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-large", note: "1200 x 780 with the larger preview",
                           chrome: .settingsWindow, size: .settingsWindowLarge, player: .playing,
                           prepare: { $0.showSettingsPage("wallpaper", largePreview: true) }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-larger-preview", note: "13-inch default with the larger preview",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { $0.showSettingsPage("wallpaper", largePreview: true) }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-larger-text", note: "13-inch Air at its Larger Text resolution (984 x 510)",
                           chrome: .settingsWindow, size: .settingsWindowLargerText, player: .playing,
                           prepare: { $0.showSettingsPage("wallpaper") }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-larger-text-denied", note: "Larger Text, Music refused and the wallpaper paused",
                           chrome: .settingsWindow, size: .settingsWindowLargerText, player: .denied,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper")
                               scene.extras.wallpaperPaused = true
                           }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-narrow", note: "a 700 x 700 window: the stage above the inspector",
                           chrome: .settingsWindow, size: .frame(CGSize(width: 700, height: 700)), player: .playing,
                           prepare: { $0.showSettingsPage("wallpaper") }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-narrow-short", note: "the smallest window, 700 x 420: the strip beside the preview",
                           chrome: .settingsWindow, size: .frame(CGSize(width: 700, height: 420)), player: .playing,
                           prepare: { $0.showSettingsPage("wallpaper") }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-narrow-large", note: "700 x 700 with the larger preview",
                           chrome: .settingsWindow, size: .frame(CGSize(width: 700, height: 700)), player: .denied,
                           prepare: { $0.showSettingsPage("wallpaper", largePreview: true) }, view: settingsWindow),
            UISnapshotCase("settings/wallpaper-contrast", note: "Increase Contrast appearances",
                           chrome: .settingsWindow, size: .settingsWindow, looks: [.lightContrast, .darkContrast],
                           player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("wallpaper")
                               scene.extras.wallpaperPaused = true
                           }, view: settingsWindow),
        ]
    }

    private static var otherPageStates: [UISnapshotCase] {
        [
            UISnapshotCase("settings/typography-playing-full", note: "Lyric style with a song playing (remember a style)",
                           chrome: .settingsWindow, size: .settingsPage, player: .playing,
                           prepare: { $0.showSettingsPage("typography", preview: false) }, view: settingsWindow),
            UISnapshotCase("settings/typography-remembered", note: "a style remembered for the playing song",
                           chrome: .plain, size: .fitting(width: LunalyrDesign.Layout.inspectorWidth), player: .playing,
                           prepare: { scene in
                               if let track = scene.models.nowPlaying.current {
                                   scene.styleMemory.remember(.init(composition: "echo", colour: "albumDrift"),
                                                              for: track, scope: .song)
                               }
                               scene.colours.mode = .customGradient
                               scene.models.highlightColours.mode = .custom
                           }, view: { _ in SettingsInspectorProbe(page: .typography) }),
            UISnapshotCase("settings/lyrics-playing", note: "Timing & sources with a song playing and an offset",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("lyrics")
                               scene.setSongOffset(0.25)
                           }, view: settingsWindow),
            UISnapshotCase("settings/lyrics-paused-full", note: "Timing & sources with the song paused: still this song",
                           chrome: .settingsWindow, size: .settingsPage, player: .paused,
                           prepare: { scene in
                               scene.showSettingsPage("lyrics", preview: false)
                               scene.setSongOffset(-0.3)
                           }, view: settingsWindow),
            UISnapshotCase("settings/covers-playing", note: "Animated covers with a song playing",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { $0.showSettingsPage("covers") }, view: settingsWindow),
            UISnapshotCase("settings/covers-needs-match", note: "covers on, wallpaper not Match the music; two excluded albums",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("covers")
                               scene.settings.useMotionArtwork = true
                               scene.settings.backgroundMode = .visualizer
                               scene.settings.motionExclusions = [VersoCustom.Settings.exclusionKey(artist: "Lunar Tides", album: "Night Bloom"),
                                                                  VersoCustom.Settings.exclusionKey(artist: "The Quiet Hours", album: "Paper Lanterns")]
                           }, view: settingsWindow),
            UISnapshotCase("settings/share-discord-on", note: "Discord and lyric card with Rich Presence on",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("share")
                               scene.extras.showsDiscordPresence = true
                           }, view: settingsWindow),
            UISnapshotCase("settings/general-pilot", note: "General in the chosen look, with the sidebar",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { $0.showSettingsPage("general") }, view: settingsWindow),
            UISnapshotCase("settings/general-denied", note: "Music players with Music refused: the access row's fix",
                           chrome: .settingsWindow, size: .settingsWindow, player: .denied,
                           prepare: { $0.showSettingsPage("players") }, view: settingsWindow),
            UISnapshotCase("settings/general-needs-consent", note: "General before macOS has asked about Music",
                           chrome: .settingsWindow, size: .settingsWindow, player: .needsConsent,
                           prepare: { $0.navigation.openSettings(.players) }, view: settingsWindow),
            UISnapshotCase("settings/general-contrast", note: "General under Increase Contrast",
                           chrome: .settingsWindow, size: .settingsWindow, looks: [.lightContrast, .darkContrast],
                           player: .denied, prepare: { $0.showSettingsPage("general") }, view: settingsWindow),
            UISnapshotCase("settings/extras-mirroring", note: "Menu bar & shortcuts while mirroring hides the lyrics",
                           chrome: .settingsWindow, size: .settingsWindow, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("extras")
                               scene.hideLyricsForMirroring(asked: true)
                           }, view: settingsWindow),
            UISnapshotCase("settings/extras-shortcuts-full", note: "global shortcuts on, the menu-bar line shown",
                           chrome: .settingsWindow, size: .settingsPage, player: .playing,
                           prepare: { scene in
                               scene.showSettingsPage("extras", preview: false)
                               scene.extras.shortcutsEnabled = true
                               scene.extras.showsMenuBarLine = true
                           }, view: settingsWindow),
            UISnapshotCase("settings/performance-battery", note: "on battery with power saving in effect",
                           chrome: .settingsWindow, size: .settingsWindow,
                           prepare: { scene in
                               scene.showSettingsPage("performance")
                               scene.settings.lowPowerVisuals = true
                               scene.settings.animationRate = .smooth
                               scene.settings.pauseWhenCovered = true
                           }, view: settingsWindow),
            UISnapshotCase("settings/about-large", note: "About at 1200 x 780",
                           chrome: .settingsWindow, size: .settingsWindowLarge,
                           prepare: { $0.showSettingsPage("about") }, view: settingsWindow),
        ]
    }

    /// The picture pages (spec §3): each object with a song playing, nothing
    /// playing, the Floating view, and the smallest window.
    private static var lookStates: [UISnapshotCase] {
        [
            UISnapshotCase("settings/look-wallpaper-playing", note: "Look, Wallpaper selected, a song playing",
                           chrome: .settingsWindow, size: .frame(CGSize(width: 1100, height: 720)), player: .playing,
                           prepare: { $0.showSettingsPage("wallpaper") }, view: settingsWindow),
            UISnapshotCase("settings/look-lyrics-playing", note: "Look, Lyric style selected: the line's handles",
                           chrome: .settingsWindow, size: .frame(CGSize(width: 1100, height: 720)), player: .playing,
                           prepare: { $0.showSettingsPage("typography") }, view: settingsWindow),
            UISnapshotCase("settings/look-covers-playing", note: "Look, Animated covers selected",
                           chrome: .settingsWindow, size: .frame(CGSize(width: 1100, height: 720)), player: .playing,
                           prepare: { $0.showSettingsPage("covers") }, view: settingsWindow),
            UISnapshotCase("settings/look-nothing-playing", note: "Look with nothing playing: the sample line",
                           chrome: .settingsWindow, size: .frame(CGSize(width: 1100, height: 720)),
                           prepare: { $0.showSettingsPage("typography") }, view: settingsWindow),
            UISnapshotCase("settings/look-floating", note: "Look, Floating over apps, Lyric style selected",
                           chrome: .settingsWindow, size: .frame(CGSize(width: 1100, height: 720)), player: .playing,
                           prepare: { $0.showSettingsPage("typography") }, view: floatingSettingsWindow),
            UISnapshotCase("settings/look-smallest", note: "Look at the smallest window, 860 x 420",
                           chrome: .settingsWindow, size: .frame(CGSize(width: 860, height: 420)), player: .playing,
                           prepare: { $0.showSettingsPage("typography") }, view: settingsWindow),
            UISnapshotCase("settings/general-1100", note: "a page without a picture at 1100 x 720",
                           chrome: .settingsWindow, size: .frame(CGSize(width: 1100, height: 720)),
                           prepare: { $0.showSettingsPage("general") }, view: settingsWindow),
        ]
    }

    /// Settings with the lyrics floating, from isolated floating options.
    static func floatingSettingsWindow(_ scene: UISnapshotScene) -> some View {
        let options = FloatingLyricsOptions(defaults: scene.defaults)
        options.surface = .floating
        return settingsWindow(scene).environment(\.lookFloatingOptions, options)
    }

    /// The window Settings draws in, configured as the app configures it.
    static var settingsWindowChrome: UISnapshotRenderer.Chrome {
        .window(styleMask: SettingsWindowController.styleMask, configure: SettingsWindowController.configure)
    }

    static func settingsWindow(_ scene: UISnapshotScene) -> some View {
        SettingsWindowView(previewDisplay: SettingsPreviewDisplay(size: UISnapshotDisplay.screen))
    }
}

/// One page's inspector on its own, as tall as its whole form, so long
/// pages can be judged at the width the window gives them.
@MainActor
struct SettingsInspectorProbe: View {
    let page: SettingsPage

    var body: some View {
        Group {
            switch page {
            case .wallpaper: WallpaperPage()
            case .typography: LyricStylePage()
            case .covers: CoversPage()
            case .lyrics: TimingPage()
            case .readings: ReadingsPage()
            case .players: MusicPlayersPage()
            case .general: GeneralPage()
            case .extras: ExtrasInspector()
            case .share: ShareInspector()
            case .performance: PerformancePage()
            case .about: AboutPage()
            }
        }
        .fixedSize(horizontal: false, vertical: true)
        .lunalyrTint()
    }
}
