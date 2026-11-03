import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Unit tests exercise parsing/render preparation without launching
        // desktop windows, onboarding, network font loading or capture.
        guard ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil,
              NSClassFromString("XCTestCase") == nil else { return }
        AppFont.preload()
        DesktopWallpaperController.shared.start()
        ControlPanelController.shared.start()
        HoverState.shared.start()
        AudioReactiveController.shared.resumeIfEnabled()
        AppUpdates.shared.startIfConfigured()
        if !Settings.shared.hasOnboarded {
            Settings.shared.hasOnboarded = true
            SettingsWindowController.shared.present()
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        SettingsWindowController.shared.present()
        return true
    }

    /// The wallpaper lives in borderless windows that AppKit does not count,
    /// so closing the onboarding window would otherwise look like the last
    /// window closing and terminate the app mid-song.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }
}
