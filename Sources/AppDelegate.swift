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
        DesktopPlayerController.shared.start()
        HoverState.shared.start()
        AudioReactiveController.shared.resumeIfEnabled()
        LunalyrExtras.shared.start()
        AppUpdates.shared.startIfConfigured()
        if !Settings.shared.hasOnboarded {
            // Onboarding explains the permission prompt before anything can
            // raise it: its music step starts following the players without
            // prompts, and Continue there lets macOS ask about the ones open
            // now. Finishing, by either button or by closing the window,
            // counts; quit with it open, and it shows again next launch.
            OnboardingWindowController.shared.present(firstRun: true) { ending in
                OnboardingWindowController.completeFirstRun(ending, models: .live)
            }
        } else {
            // Players macOS has not asked about yet wait until the user
            // plays something in them (or asks from the menu bar).
            NowPlayingMonitor.shared.start()
        }
    }

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
