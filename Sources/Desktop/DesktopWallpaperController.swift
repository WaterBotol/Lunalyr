import AppKit
import Combine
import SwiftUI

/// Hosts NowPlayingView directly as the desktop background -- a borderless,
/// non-interactive window per screen, sitting just above the real desktop
/// picture and below the desktop icons. Unlike a pre-rendered video, this is
/// the live SwiftUI view itself, so the gradient animation and lyric sync
/// are the real thing, not a loop.
@MainActor
final class DesktopWallpaperController {
    static let shared = DesktopWallpaperController()
    static let windowTitle = "Lunalyr wallpaper"

    private var surfaces: [CGDirectDisplayID: (window: NSWindow, activity: WindowRenderActivity)] = [:]
    private var screenObserver: NSObjectProtocol?
    private var modeCancellable: AnyCancellable?
    private var pauseCancellable: AnyCancellable?
    var windowIDs: Set<CGWindowID> {
        Set(wallpaperWindows.values)
    }
    /// Each display's wallpaper window, once the window server knows it.
    var wallpaperWindows: [CGDirectDisplayID: CGWindowID] {
        surfaces.compactMapValues { $0.window.windowServerID }
    }
    /// Last album palette, available to desktop companion views.
    var currentPalette: [SIMD3<Double>] = ColorExtractor.fallbackHSBPalette

    func start() {
        guard screenObserver == nil else { return }
        DesktopCoverageMonitor.shared.start()
        rebuild()
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor [weak self] in self?.rebuild() }
        }
        // Only "Your wallpaper" draws over the existing desktop. Every other
        // mode paints an opaque black base, so its window can be opaque too:
        // the window server then skips blending the desktop picture beneath.
        modeCancellable = Settings.shared.$backgroundMode.removeDuplicates().sink { [weak self] mode in
            Task { @MainActor [weak self] in self?.applyOpacity(for: mode) }
        }
        // A paused wallpaper cannot run its fade; resuming lets it finish.
        pauseCancellable = LunalyrExtras.shared.$wallpaperPaused.removeDuplicates().dropFirst().sink { [weak self] paused in
            guard !paused else { return }
            Task { @MainActor [weak self] in self?.wallpaperResumed() }
        }
    }

    static func needsTransparency(_ mode: Settings.BackgroundMode) -> Bool { mode == .desktop }

    private var pendingOpacity: DispatchWorkItem?
    /// The mode whose windows turn opaque once its wallpaper has faded in.
    private var opacityChange: (mode: Settings.BackgroundMode, at: TimeInterval, resumedAt: TimeInterval?)?

    private func applyOpacity(for mode: Settings.BackgroundMode) {
        pendingOpacity?.cancel()
        pendingOpacity = nil
        opacityChange = nil
        guard !Self.needsTransparency(mode) else {
            // Transparent at once, so the outgoing wallpaper can fade away
            // over the desktop instead of cutting to black.
            for surface in surfaces.values { Self.configureOpacity(surface.window, mode: mode) }
            return
        }
        // Stay transparent until the incoming wallpaper has faded in over
        // the desktop; an opaque window would show black beneath the fade.
        opacityChange = (mode, ProcessInfo.processInfo.systemUptime, nil)
        scheduleOpacityCheck(after: WallpaperOpacityPolicy.settleTime)
    }

    private func wallpaperResumed() {
        guard let change = opacityChange else { return }
        opacityChange = (change.mode, change.at, ProcessInfo.processInfo.systemUptime)
        checkOpacity()
    }

    private func scheduleOpacityCheck(after delay: TimeInterval) {
        pendingOpacity?.cancel()
        let work = DispatchWorkItem { [weak self] in
            Task { @MainActor [weak self] in self?.checkOpacity() }
        }
        pendingOpacity = work
        DispatchQueue.main.asyncAfter(deadline: .now() + max(0, delay), execute: work)
    }

    private func checkOpacity() {
        pendingOpacity = nil
        guard let change = opacityChange else { return }
        guard Settings.shared.backgroundMode == change.mode else { opacityChange = nil; return }
        let effects = ShaderPipelineCache.shared
        switch WallpaperOpacityPolicy.step(now: ProcessInfo.processInfo.systemUptime, changedAt: change.at,
                                           compiling: effects.isCompiling, settledAt: effects.lastSettledAt,
                                           paused: LunalyrExtras.shared.wallpaperPaused, resumedAt: change.resumedAt) {
        case .opaque:
            opacityChange = nil
            for surface in surfaces.values { Self.configureOpacity(surface.window, mode: change.mode) }
        case .wait(let seconds):
            scheduleOpacityCheck(after: seconds)
        case .hold:
            break
        }
    }

    private static func configureOpacity(_ window: NSWindow, mode: Settings.BackgroundMode) {
        let opaque = !needsTransparency(mode)
        guard window.isOpaque != opaque else { return }
        window.isOpaque = opaque
        window.backgroundColor = opaque ? .black : .clear
    }

    private func rebuild() {
        HoverState.shared.reset()
        let screens = NSScreen.screens
        let connected = Set(screens.compactMap(\.versoDisplayID))
        for id in Set(surfaces.keys).subtracting(connected) {
            guard let surface = surfaces.removeValue(forKey: id) else { continue }
            surface.activity.stop()
            surface.window.orderOut(nil)
            surface.window.contentView = nil
            surface.window.close()
        }

        for screen in screens {
            guard let displayID = screen.versoDisplayID else { continue }
            if let surface = surfaces[displayID] {
                if surface.window.frame != screen.frame {
                    surface.window.setFrame(screen.frame, display: true)
                }
                if !surface.window.isVisible { surface.window.orderFrontRegardless() }
                continue
            }
            let window = NSWindow(
                contentRect: NSRect(origin: .zero, size: screen.frame.size), styleMask: .borderless,
                backing: .buffered, defer: false, screen: screen
            )
            // The initializer's origin is relative to its screen; setFrame
            // takes global coordinates. Never add a screen's origin twice.
            window.setFrame(screen.frame, display: false)
            // Keep the wallpaper above other desktop-picture surfaces and just
            // below Finder icons, without repeatedly competing for front order.
            window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) - 1)
            window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenNone]
            window.ignoresMouseEvents = true
            window.isOpaque = true
            window.backgroundColor = .black
            Self.configureOpacity(window, mode: Settings.shared.backgroundMode)
            window.hasShadow = false
            window.canHide = false
            window.isReleasedWhenClosed = false
            // Named for assistive technology and window lists; borderless,
            // so nothing is drawn. Never listed in the Window menu.
            window.title = Self.windowTitle
            window.isExcludedFromWindowsMenu = true

            let activity = WindowRenderActivity()
            let shade = max(0, screen.frame.maxY - screen.visibleFrame.maxY)
            let hosting = NSHostingView(rootView: ZStack(alignment: .top) {
                NowPlayingView(displayID: displayID, renderActivity: activity)
                    .environmentObject(NowPlayingMonitor.shared)
                MenuBarShade(menuBarHeight: shade)
            })
            hosting.frame = NSRect(origin: .zero, size: screen.frame.size)
            hosting.autoresizingMask = [.width, .height]
            window.contentView = hosting

            window.orderFrontRegardless()
            activity.start(window: window, displayID: displayID)
            surfaces[displayID] = (window, activity)
        }
        DesktopCoverageMonitor.shared.setNeedsUpdate()
    }
}

/// When the wallpaper windows may turn opaque after leaving Your wallpaper.
///
/// An opaque window paints black beneath its content, so it waits until the
/// incoming wallpaper has faded in over the desktop. That fade waits in turn
/// for an effect that is still compiling (NowPlayingView holds it at its
/// start meanwhile), and does not run at all while Pause wallpaper is on:
/// switching to opaque on a fixed timer cut the desktop picture to black
/// whenever a first compile took longer than the timer.
enum WallpaperOpacityPolicy {
    enum Step: Equatable {
        case opaque
        /// Look again after this many seconds.
        case wait(TimeInterval)
        /// Look again when the wallpaper resumes.
        case hold
    }

    /// The incoming wallpaper's fade (NowPlayingView's background fade) and
    /// a margin for its last frames.
    static let settleTime: TimeInterval = 1.2
    /// How often a compile in flight is looked at.
    static let pollInterval: TimeInterval = 0.25
    /// A compile still running after this long is not waited for.
    static let compileWait: TimeInterval = 10

    /// `settledAt` is when the last effect compile finished, and `resumedAt`
    /// when Pause wallpaper last ended, on the same clock as `now`.
    static func step(now: TimeInterval, changedAt: TimeInterval, compiling: Bool, settledAt: TimeInterval?,
                     paused: Bool, resumedAt: TimeInterval?) -> Step {
        if paused { return .hold }
        if compiling && now - changedAt < compileWait { return .wait(pollInterval) }
        // The fade starts with the change, or once the effect has compiled
        // or the wallpaper resumed, whichever came last.
        let fadeStart = [changedAt, settledAt ?? changedAt, resumedAt ?? changedAt].max() ?? changedAt
        let due = fadeStart + settleTime
        return now >= due ? .opaque : .wait(due - now)
    }
}

/// A faint dark fade along the top edge, beneath the menu bar (see
/// `DesktopOptions.shadesMenuBar`). Never takes a click.
@MainActor
struct MenuBarShade: View {
    let menuBarHeight: CGFloat
    @ObservedObject private var options = DesktopOptions.shared

    var body: some View {
        if options.shadesMenuBar {
            LinearGradient(colors: [Color.black.opacity(0.42), Color.black.opacity(0.18), .clear],
                           startPoint: .top, endPoint: .bottom)
                .frame(height: max(menuBarHeight, 24) + 36)
                .frame(maxWidth: .infinity)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
    }
}
