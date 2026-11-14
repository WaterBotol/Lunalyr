import AppKit
import SwiftUI

// MARK: - Window

/// Presents the settings in its own window. Accessory apps have no ordinary
/// window, so this is managed by hand.
@MainActor
final class SettingsWindowController: NSObject {
    static let shared = SettingsWindowController()
    /// Low enough for a 13-inch MacBook Air at its largest text size, with
    /// the Dock showing.
    static let minimumHeight: CGFloat = 420
    static let minimumWidth: CGFloat = 860
    /// Where AppKit keeps the frame between openings.
    private static let frameName = "custom.settingsWindow"
    /// The content runs under a transparent title bar: the sidebar and the
    /// canvas reach the top edge, with the window's buttons over the sidebar.
    static let styleMask: NSWindow.StyleMask = [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView]

    /// Everything about the window except its content and frame. The UI
    /// snapshot harness draws Settings in a window configured by this too.
    static func configure(_ window: NSWindow) {
        window.title = "Lunalyr"
        window.minSize = NSSize(width: minimumWidth, height: minimumHeight)
        window.titlebarAppearsTransparent = true
        // The title stays for the Window menu and Mission Control.
        window.titleVisibility = .hidden
        // The picture's handles are dragged; only the title bar moves the window.
        window.isMovableByWindowBackground = false
        window.backgroundColor = .windowBackgroundColor
    }
    private var window: NSWindow?
    private let previewDisplay = SettingsPreviewDisplay()

    func present() {
        // The welcome comes first: it explains the permission prompt, then
        // opens Settings itself and starts following the players. Settings
        // opened around it would leave both waiting behind its window.
        let navigation = LunalyrNavigation.shared
        if navigation.onboardingIsVisible() {
            navigation.bringOnboardingForward()
            return
        }
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
            contentRect: NSRect(origin: .zero, size: LunalyrDesign.Layout.settingsMaximum),
            styleMask: Self.styleMask,
            backing: .buffered,
            defer: false
        )
        Self.configure(window)
        // Created in code, so it would otherwise be released on close while
        // this controller still points at it -- an over-release crash.
        window.isReleasedWhenClosed = false
        previewDisplay.activate()
        // The first opening of a launch shows the logo while Settings builds
        // behind it (`SettingsLoader`); later ones open straight on Settings.
        let loader: SettingsLoaderView?
        if SettingsLoader.shouldShow(alreadyShown: SettingsLoader.shown), let url = WelcomeGlass.url {
            SettingsLoader.shown = true
            let container = NSView(frame: window.contentRect(forFrameRect: window.frame))
            let view = SettingsLoaderView(frame: container.bounds, url: url)
            view.autoresizingMask = [.width, .height]
            container.addSubview(view)
            window.contentView = container
            loader = view
        } else {
            window.contentView = NSHostingView(rootView: SettingsWindowView(previewDisplay: previewDisplay))
            loader = nil
        }
        // Where it was last left; otherwise at the trailing edge of the
        // screen that was clicked, near the menu bar extra. Naming the frame
        // also restores it, and saves every later move and resize.
        let restored = window.setFrameUsingName(Self.frameName)
        _ = window.setFrameAutosaveName(Self.frameName)
        if !restored || !Self.isOnScreen(window.frame) {
            let screen = Self.presentationScreen()
            window.setFrame(Self.initialFrame(in: screen?.visibleFrame ?? window.frame), display: false)
        }
        previewDisplay.follow(window.screen)
        window.delegate = self
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = window
        if let loader { buildBehind(loader, in: window) }
    }

    /// Settings is built on the next turn of the run loop, under the playing
    /// logo (the video keeps moving on its own layer meanwhile), then the
    /// logo fades once the glass has formed.
    private func buildBehind(_ loader: SettingsLoaderView, in window: NSWindow) {
        let opened = ProcessInfo.processInfo.systemUptime
        loader.onSkip = { [weak loader] in loader?.dismissNow() }
        DispatchQueue.main.async { [weak self, weak window, weak loader] in
            guard let self, let window, let container = window.contentView else { return }
            let host = NSHostingView(rootView: SettingsWindowView(previewDisplay: self.previewDisplay))
            host.frame = container.bounds
            host.autoresizingMask = [.width, .height]
            if let loader, loader.superview === container {
                container.addSubview(host, positioned: .below, relativeTo: loader)
            } else {
                container.addSubview(host)
            }
            host.layoutSubtreeIfNeeded()
            let elapsed = ProcessInfo.processInfo.systemUptime - opened
            loader?.dismiss(after: SettingsLoader.remaining(after: elapsed)) {}
        }
    }

    /// The screen under the pointer -- the one whose menu bar was just
    /// clicked -- or the main screen.
    private static func presentationScreen() -> NSScreen? {
        let pointer = NSEvent.mouseLocation
        return NSScreen.screens.first { NSMouseInRect(pointer, $0.frame, false) } ?? NSScreen.main
    }

    private static func isOnScreen(_ frame: NSRect) -> Bool {
        NSScreen.screens.contains { screen in
            let overlap = screen.visibleFrame.intersection(frame)
            return overlap.width >= 120 && overlap.height >= 80
        }
    }

    /// The first frame: the preview inside shows the lyrics, so the window
    /// takes most of the screen it opens on, at its trailing edge, near the
    /// menu bar extra it was opened from. Three quarters of the width, at
    /// least `minimumWidth` (on a screen narrower than 1280 points, all of
    /// it less the margins), at most 1200 points; the height less the
    /// margins, at most 780. 1102 x 780 on a 13-inch Air with the Dock
    /// showing, 984 x 510 on the same Air at Larger Text.
    static func initialFrame(in visible: NSRect, margin: CGFloat = 20) -> NSRect {
        let room = NSSize(width: max(visible.width - margin * 2, 1), height: max(visible.height - margin * 2, 1))
        let maximum = LunalyrDesign.Layout.settingsMaximum
        let preferred = min(maximum.width, visible.width < 1280 ? room.width : max(minimumWidth, visible.width * 0.75))
        let width = min(room.width, preferred).rounded(.down)
        let height = min(room.height, maximum.height).rounded(.down)
        let x = max(visible.minX + margin, visible.maxX - margin - width)
        let y = max(visible.minY + margin, visible.maxY - margin - height)
        return NSRect(x: x.rounded(), y: y.rounded(), width: width, height: height)
    }
}

extension SettingsWindowController: NSWindowDelegate {
    func windowDidChangeScreen(_ notification: Notification) {
        guard let window = notification.object as? NSWindow else { return }
        previewDisplay.follow(window.screen)
    }

    /// Picks up a desktop picture changed in System Settings meanwhile.
    func windowDidBecomeKey(_ notification: Notification) {
        previewDisplay.refresh()
        // Login Items can be changed in System Settings meanwhile too.
        Settings.shared.refreshLaunchAtLogin()
    }

    /// Back to an accessory app once Settings goes away, so the Dock icon and
    /// menu bar do not linger for an app with nothing left on screen.
    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow else { return }
        closing.saveFrame(usingName: Self.frameName)
        // Drop the hosting view after the close event so preview timelines
        // and audio-meter subscriptions do not stay alive in a closed window.
        DispatchQueue.main.async { [weak self] in
            guard let self, self.window === closing, !closing.isVisible else { return }
            closing.contentView = nil
            // Free the autosave name for the next window; the frame is kept.
            _ = closing.setFrameAutosaveName("")
            self.window = nil
            self.previewDisplay.deactivate()
            NSApp.setActivationPolicy(.accessory)
        }
    }
}
