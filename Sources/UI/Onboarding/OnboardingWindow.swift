import AppKit
import SwiftUI

/// Onboarding's window: six steps that explain Lunalyr, ask for what it
/// needs only when the person continues, and let them pick a look while
/// seeing it. On first run the app delegate shows it before anything can
/// raise macOS's permission prompt; Settings › General shows it again
/// (`LunalyrNavigation.openOnboarding()`), when finishing just closes.
///
/// While it is up, Lunalyr is a regular app (Dock icon, working menu bar),
/// and opening Settings or Lunalyr again brings it forward instead
/// (`LunalyrNavigation.onboardingIsVisible()`).
@MainActor
final class OnboardingWindowController: NSObject, NSWindowDelegate {
    static let shared = OnboardingWindowController()

    static let styleMask: NSWindow.StyleMask = [.titled, .closable, .fullSizeContentView]
    static let title = OnboardingCopy.welcomeTitle

    /// Everything about the window except its content and frame; the UI
    /// snapshot harness draws onboarding in a window configured by this too.
    static func configure(_ window: NSWindow) {
        window.title = title
        // The steps carry their own titles: the bar stays, unlabelled and
        // see-through, with only the close button.
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isMovableByWindowBackground = true
        window.backgroundColor = .windowBackgroundColor
        window.standardWindowButton(.miniaturizeButton)?.isHidden = true
        window.standardWindowButton(.zoomButton)?.isHidden = true
        window.setContentSize(LunalyrDesign.Layout.onboarding)
    }

    private var window: NSWindow?
    private var flow: OnboardingFlow?
    private var previewDisplay: SettingsPreviewDisplay?
    private var onFinish: ((OnboardingEnding) -> Void)?
    /// How the person ended it, once they have; nil means the close button.
    private var ending: OnboardingEnding?
    private var terminating = false
    private var observers: [NSObjectProtocol] = []

    var isVisible: Bool { window?.isVisible == true }

    /// Shows onboarding from its first step. `onFinish` runs once, when the
    /// window closes, with how it ended -- never when Lunalyr quits with it
    /// open, so a first run that wasn't finished shows again next launch.
    /// `models` defaults to the app's own (`LunalyrModels.live`).
    func present(firstRun: Bool, models: LunalyrModels? = nil,
                 onFinish: @escaping (OnboardingEnding) -> Void = { _ in }) {
        // A regular app while it is up, like Settings: key, and in the Dock.
        NSApp.setActivationPolicy(.regular)
        if let window {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let models = models ?? .live
        self.onFinish = onFinish
        ending = nil
        let flow = OnboardingFlow(models: models, firstRun: firstRun) { [weak self] ending in
            self?.end(ending)
        }
        let display = SettingsPreviewDisplay()
        display.activate()
        let window = OnboardingWindow(contentRect: NSRect(origin: .zero, size: LunalyrDesign.Layout.onboarding),
                                      styleMask: Self.styleMask, backing: .buffered, defer: false)
        Self.configure(window)
        // Created in code: released on close otherwise, while still referenced.
        window.isReleasedWhenClosed = false
        let hosting = NSHostingView(rootView: OnboardingView(flow: flow, previewDisplay: display).lunalyrModels(models))
        // The window is the size; the view fills it, title bar included.
        hosting.sizingOptions = []
        window.contentView = hosting
        window.setContentSize(LunalyrDesign.Layout.onboarding)
        window.delegate = self
        window.center()
        display.follow(window.screen)
        self.flow = flow
        self.previewDisplay = display
        self.window = window
        observeOtherWindows()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// From Settings › General: the same steps, finishing just closes.
    func presentAgain() {
        present(firstRun: false)
    }

    func bringToFront() {
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// The person finished: record how, then close.
    private func end(_ ending: OnboardingEnding) {
        self.ending = ending
        window?.close()
    }

    /// The first run's last word, however it ended: it counts as done, the
    /// players are followed (silently, if the music step was never reached)
    /// and Open Settings opens Settings.
    static func completeFirstRun(_ ending: OnboardingEnding, models: LunalyrModels) {
        models.settings.hasOnboarded = true
        models.nowPlaying.start()
        if ending == .openSettings { models.navigation.openSettings() }
    }

    // MARK: NSWindowDelegate

    func windowDidChangeScreen(_ notification: Notification) {
        previewDisplay?.follow(window?.screen)
    }

    /// Picks up a desktop picture changed in System Settings meanwhile.
    func windowDidBecomeKey(_ notification: Notification) {
        previewDisplay?.refresh()
    }

    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow, closing === window else { return }
        let finish = terminating ? nil : onFinish
        let ending = self.ending ?? .closed
        onFinish = nil
        // After the close event, as Settings does: drop the hosted view.
        DispatchQueue.main.async { [weak self] in
            closing.contentView = nil
            guard let self else { return }
            self.previewDisplay?.deactivate()
            self.previewDisplay = nil
            self.flow = nil
            self.window = nil
            self.observers.forEach(NotificationCenter.default.removeObserver)
            self.observers = []
            finish?(ending)
            // Back to an accessory app unless another window (Settings,
            // perhaps just opened by Open Settings) still needs the Dock.
            if !Self.anotherWindowIsOpen(besides: closing) { NSApp.setActivationPolicy(.accessory) }
        }
    }

    // MARK: Other windows

    /// Settings opened, or closed, beside onboarding (Show Welcome Again…)
    /// leaves Lunalyr an accessory app as it closes; while onboarding is up
    /// it stays a regular one. Quitting with the window open isn't finishing.
    private func observeOtherWindows() {
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: NSWindow.willCloseNotification, object: nil,
                                            queue: .main) { [weak self] note in
            let other = note.object as? NSWindow
            MainActor.assumeIsolated {
                guard let self, other !== self.window else { return }
                // Behind the closing window's own clean-up, which runs on the next turn.
                DispatchQueue.main.async { [weak self] in
                    DispatchQueue.main.async { [weak self] in
                        if self?.isVisible == true { NSApp.setActivationPolicy(.regular) }
                    }
                }
            }
        })
        observers.append(center.addObserver(forName: NSApplication.willTerminateNotification, object: nil,
                                            queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.terminating = true }
        })
    }

    /// A titled window other than `window` is on screen: Settings, say. The
    /// wallpaper's borderless windows and the menu-bar panel don't count.
    static func anotherWindowIsOpen(besides window: NSWindow) -> Bool {
        NSApp.windows.contains { other in
            other !== window && other.isVisible && other.styleMask.contains(.titled) && !(other is NSPanel)
        }
    }
}

/// Onboarding's window: closes with ⌘W, as a titled window should, whether
/// or not the app's menu offers Close while it is up.
final class OnboardingWindow: NSWindow {
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if modifiers == .command, event.charactersIgnoringModifiers?.lowercased() == "w" {
            performClose(nil)
            return true
        }
        return super.performKeyEquivalent(with: event)
    }
}
