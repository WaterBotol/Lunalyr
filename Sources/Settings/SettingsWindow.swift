import AppKit
import SwiftUI

// MARK: - Window

/// Presents the settings in its own window. Accessory apps have no ordinary
/// window, so this is managed by hand.
@MainActor
final class SettingsWindowController: NSObject {
    static let shared = SettingsWindowController()
    private var window: NSWindow?

    func present() {
        // Verso is an accessory app -- no Dock icon, since the wallpaper is
        // the interface. But an accessory app's window cannot become properly
        // key, so Settings would open without a working menu bar and could be
        // lost behind other windows with no way to bring it back. Becoming a
        // regular app for as long as Settings is open fixes both, and the Dock
        // icon gives somewhere to click back to.
        NSApp.setActivationPolicy(.regular)

        if let window {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1060, height: 720),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Lunalyr"
        window.minSize = NSSize(width: 880, height: 620)
        window.titlebarAppearsTransparent = false
        window.titleVisibility = .visible
        window.isMovableByWindowBackground = true
        window.backgroundColor = .windowBackgroundColor
        // Created in code, so it would otherwise be released on close while
        // this controller still points at it -- an over-release crash.
        window.isReleasedWhenClosed = false
        window.center()
        window.contentView = NSHostingView(rootView: SettingsWindowView())
        window.delegate = self
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = window
    }
}

extension SettingsWindowController: NSWindowDelegate {
    /// Back to an accessory app once Settings goes away, so the Dock icon and
    /// menu bar do not linger for an app with nothing left on screen.
    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow else { return }
        // Drop the hosting view after the close event so preview timelines
        // and audio-meter subscriptions do not stay alive in a closed window.
        DispatchQueue.main.async { [weak self] in
            guard let self, self.window === closing, !closing.isVisible else { return }
            closing.contentView = nil
            self.window = nil
            NSApp.setActivationPolicy(.accessory)
        }
    }
}
