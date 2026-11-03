import AppKit
import SwiftUI

/// Hosts NowPlayingView directly as the desktop background -- a borderless,
/// non-interactive window per screen, sitting just above the real desktop
/// picture and below the desktop icons. Unlike a pre-rendered video, this is
/// the live SwiftUI view itself, so the gradient animation and lyric sync
/// are the real thing, not a loop.
@MainActor
final class DesktopWallpaperController {
    static let shared = DesktopWallpaperController()

    private var windows: [NSWindow] = []
    private var renderActivities: [WindowRenderActivity] = []
    private var screenObserver: NSObjectProtocol?
    var windowIDs: Set<CGWindowID> {
        Set(windows.compactMap { $0.windowNumber > 0 ? CGWindowID($0.windowNumber) : nil })
    }
    /// Last album palette, available to desktop companion views.
    var currentPalette: [SIMD3<Double>] = ColorExtractor.fallbackHSBPalette

    func start() {
        guard screenObserver == nil else { return }
        rebuild()
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor [weak self] in self?.rebuild() }
        }
    }

    private func rebuild() {
        HoverState.shared.reset()
        renderActivities.forEach { $0.stop() }
        renderActivities.removeAll()
        windows.forEach { $0.orderOut(nil); $0.contentView = nil; $0.close() }
        windows.removeAll()

        for screen in NSScreen.screens {
            guard let displayID = screen.versoDisplayID else { continue }
            let window = NSWindow(
                contentRect: NSRect(origin: .zero, size: screen.frame.size), styleMask: .borderless,
                backing: .buffered, defer: false, screen: screen
            )
            // The initializer's origin is relative to its screen; setFrame
            // takes global coordinates. Never add a screen's origin twice.
            window.setFrame(screen.frame, display: false)
            window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopWindow)) + 1)
            window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenNone]
            window.ignoresMouseEvents = true
            window.isOpaque = true
            window.hasShadow = false
            window.backgroundColor = .black
            window.canHide = false
            window.isReleasedWhenClosed = false

            let activity = WindowRenderActivity()
            let hosting = NSHostingView(rootView: NowPlayingView(displayID: displayID, renderActivity: activity)
                .environmentObject(NowPlayingMonitor.shared))
            hosting.frame = NSRect(origin: .zero, size: screen.frame.size)
            hosting.autoresizingMask = [.width, .height]
            window.contentView = hosting

            window.orderFront(nil)
            activity.start(window: window)
            renderActivities.append(activity)
            windows.append(window)
        }
    }
}
