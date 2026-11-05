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
            step("onboarding/4-lyrics", "Lyrics & readings: sources, timing, readings and translations", .lyrics),
            step("onboarding/5-menu-bar", "The menu bar: where Lunalyr lives, its two switches", .menuBar),
            step("onboarding/5-menu-bar-line-on", "The menu bar, with the current line switched on", .menuBar,
                 prepare: { $0.extras.showsMenuBarLine = true }),
            step("onboarding/6-done", "Done, first run: a song playing; Open Settings is the default", .done,
                 looks: every, player: .playing),
            step("onboarding/6-done-denied", "Done, first run: Music refused, its fix still offered", .done,
                 player: .denied),
            step("onboarding/6-done-again", "Done, shown again from Settings: finishing just closes", .done,
                 firstRun: false),
        ]
    }

    /// One step of onboarding in its window.
    private static func step(_ name: String, _ note: String, _ step: OnboardingFlow.Step,
                             looks: [UISnapshotRenderer.Look] = [.light, .dark], player: UISnapshotScene.Player = .idle,
                             firstRun: Bool = true,
                             prepare: @escaping @MainActor (UISnapshotScene) -> Void = { _ in }) -> UISnapshotCase {
        UISnapshotCase(name, note: note, chrome: .onboardingWindow, size: .frame(LunalyrDesign.Layout.onboarding),
                       looks: looks, player: player, prepare: prepare,
                       view: { scene in onboardingView(scene, step: step, firstRun: firstRun) })
    }

    /// The window onboarding draws in, configured as the app configures it.
    static var onboardingWindowChrome: UISnapshotRenderer.Chrome {
        .window(styleMask: OnboardingWindowController.styleMask, configure: OnboardingWindowController.configure)
    }

    static func onboardingView(_ scene: UISnapshotScene, step: OnboardingFlow.Step, firstRun: Bool = true) -> some View {
        OnboardingView(flow: OnboardingFlow(models: scene.models, firstRun: firstRun, step: step),
                       previewDisplay: SettingsPreviewDisplay(size: UISnapshotDisplay.screen))
    }
}
