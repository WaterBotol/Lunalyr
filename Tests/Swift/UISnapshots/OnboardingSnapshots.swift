import SwiftUI
@testable import VersoCustom

/// First run: each onboarding step and the access states it explains, in the
/// window it opens in (640 x 540, see-through title bar). Owned by the
/// onboarding builder; see UISnapshotTests.swift for how to add a case.
@MainActor
extension UISnapshotCatalogue {
    static var onboarding: [UISnapshotCase] {
        let every: [UISnapshotRenderer.Look] = [.light, .dark, .lightContrast, .darkContrast]
        return [
            step("onboarding/1-welcome", "Welcome: what Lunalyr does; no Back on the first step", .welcome),
            step("onboarding/2-music", "Your music: Music open and allowed, Spotify not open", .music),
            step("onboarding/2-music-needs-consent", "Your music: macOS hasn't asked about Music; Continue will",
                 .music, player: .needsConsent),
            step("onboarding/2-music-denied", "Your music: Music refused in Automation, with its fix", .music,
                 looks: every, player: .denied),
            step("onboarding/2-music-not-running", "Your music: no player open", .music, player: .notRunning),
            step("onboarding/3-look", "Pick a look: Match the music over the live preview", .look, player: .playing),
            step("onboarding/3-look-your-wallpaper", "Pick a look: Your wallpaper (lyrics only)", .look,
                 prepare: { $0.settings.backgroundMode = .desktop }),
            step("onboarding/3-look-power-saving", "Pick a look: Visualisers, held back by Low power visuals", .look,
                 prepare: { scene in
                     scene.settings.backgroundMode = .visualizer
                     scene.settings.lowPowerVisuals = true
                 }),
            step("onboarding/3-look-lyrics", "Pick a look: the lyric layouts, the one in use first", .look,
                 prepare: { $0.settings.lyricComposition = .broadside }, lookPart: .lyrics),
            step("onboarding/4-videos", "Music videos: off, both framings, YouTube search off", .videos),
            step("onboarding/4-videos-on", "Music videos: on, library videos whole, YouTube search on", .videos,
                 looks: every, prepare: { scene in
                     scene.settings.backgroundMode = .musicVideo
                     scene.settings.libraryVideoFraming = .whole
                     scene.settings.musicVideoOnline = true
                 }),
            step("onboarding/5-lyrics", "Lyrics & readings: sources, timing, readings and translations", .lyrics),
            step("onboarding/6-extras", "Extras: the player, floating lyrics, the Discord status (off)", .extras,
                 looks: every),
            step("onboarding/6-extras-discord-on", "Extras: the Discord status switched on", .extras,
                 prepare: { $0.extras.showsDiscordPresence = true }),
            step("onboarding/7-menu-bar", "The menu bar: where Lunalyr lives, its two switches", .menuBar),
            step("onboarding/7-menu-bar-line-on", "The menu bar, with the current line switched on", .menuBar,
                 prepare: { $0.extras.showsMenuBarLine = true }),
            step("onboarding/8-done", "Done, first run: a song playing; Open Settings is the default", .done,
                 looks: every, player: .playing),
            step("onboarding/8-done-denied", "Done, first run: Music refused, its fix still offered", .done,
                 player: .denied),
            step("onboarding/8-done-again", "Done, shown again from Settings: finishing just closes", .done,
                 firstRun: false),
        ]
    }

    /// One step of onboarding in its window.
    private static func step(_ name: String, _ note: String, _ step: OnboardingFlow.Step,
                             looks: [UISnapshotRenderer.Look] = [.light, .dark], player: UISnapshotScene.Player = .idle,
                             firstRun: Bool = true,
                             prepare: @escaping @MainActor (UISnapshotScene) -> Void = { _ in },
                             lookPart: OnboardingFlow.LookPart = .wallpaper) -> UISnapshotCase {
        UISnapshotCase(name, note: note, chrome: .onboardingWindow, size: .frame(LunalyrDesign.Layout.onboarding),
                       looks: looks, player: player, prepare: prepare,
                       view: { scene in onboardingView(scene, step: step, firstRun: firstRun, lookPart: lookPart) })
    }

    /// The window onboarding draws in, configured as the app configures it.
    static var onboardingWindowChrome: UISnapshotRenderer.Chrome {
        .window(styleMask: OnboardingWindowController.styleMask, configure: OnboardingWindowController.configure)
    }

    static func onboardingView(_ scene: UISnapshotScene, step: OnboardingFlow.Step, firstRun: Bool = true,
                               lookPart: OnboardingFlow.LookPart = .wallpaper) -> some View {
        let flow = OnboardingFlow(models: scene.models, firstRun: firstRun, step: step)
        flow.lookPart = lookPart
        return OnboardingView(flow: flow, previewDisplay: SettingsPreviewDisplay(size: UISnapshotDisplay.screen))
    }
}
