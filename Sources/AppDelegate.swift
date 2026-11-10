import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Unit tests exercise parsing/render preparation without launching
        // desktop windows, onboarding, network font loading, capture or
        // Apple events to the user's music players.
        guard !AppRuntime.isUnitTesting else { return }
        AppFont.preload()
        DesktopWallpaperController.shared.start()
        ControlPanelController.shared.start()
        AudioReactiveController.shared.resumeIfEnabled()
        LunalyrExtras.shared.start()
        MusicVideoController.shared.start()
        AppUpdates.shared.startIfConfigured()
        if !Settings.shared.hasOnboarded {
            // Onboarding explains the permission prompt before anything can
            // raise it: its music step starts following the players without
            // prompts, and Continue there lets macOS ask about the ones open
            // now. Finishing, by either button or by closing the window,
            // counts; quit with it open, and it shows again next launch.
            OnboardingWindowController.shared.present(firstRun: true) { ending in
                OnboardingWindowController.completeFirstRun(ending, models: .live)
                HoverState.shared.start()
            }
        } else {
            // Players macOS has not asked about yet wait until the user
            // plays something in them (or asks from the menu bar).
            NowPlayingMonitor.shared.start()
        }
        if Self.startsHoverAtLaunch(hasOnboarded: Settings.shared.hasOnboarded) {
            HoverState.shared.start()
        }
        // Links that launched the app run once everything above has started.
        LunalyrLinkRouter.shared.becameReady()
    }

    /// The desktop player reads the music player's queue as soon as it
    /// shows on hover. Before the first run's welcome has explained macOS's
    /// Automation prompt -- and before the player monitor has silently
    /// checked what was decided -- that read is the first Apple event to an
    /// open Music, and raises the prompt out of the blue. So on a first run
    /// the hover controls start once the welcome has finished.
    static func startsHoverAtLaunch(hasOnboarded: Bool) -> Bool { hasOnboarded }

    /// `lunalyr://` links, from a browser, Shortcuts or `open`. Unknown or
    /// malformed ones are ignored; a join is asked about before following.
    func application(_ application: NSApplication, open urls: [URL]) {
        guard !AppRuntime.isUnitTesting else { Self.openedWhileTesting.append(contentsOf: urls); return }
        LunalyrLinkRouter.shared.open(urls)
    }

    /// Links that reached this delegate in the unit tests, which check that
    /// SwiftUI's own app delegate passes `application(_:open:)` on to it.
    @MainActor static var openedWhileTesting: [URL] = []

    /// Opening Lunalyr again (from Applications, Spotlight or the Dock)
    /// is the way back when the menu-bar icon is hidden behind the notch.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        let navigation = LunalyrNavigation.shared
        if navigation.onboardingIsVisible() {
            navigation.bringOnboardingForward()
        } else {
            navigation.openSettings()
        }
        return true
    }

    /// The wallpaper lives in borderless windows that AppKit does not count,
    /// so closing the onboarding window would otherwise look like the last
    /// window closing and terminate the app mid-song.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }
}
