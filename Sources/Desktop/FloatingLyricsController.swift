import AppKit
import Combine
import SwiftUI

/// A borderless, see-through panel that never activates Lunalyr and never
/// takes the keyboard, so typing always stays with the app in front. It
/// takes the pointer only in edit mode.
final class FloatingLyricsPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// Takes the first click, so Done and the drag work without a click to
/// bring the panel forward first.
private final class FirstClickHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

/// The floating lyrics window: one panel, on the chosen display, above the
/// apps (full-screen ones too) and on every Space, out of the Dock, ⌘-Tab and
/// Mission Control. Clicks pass through it except in edit mode. It is shown
/// only while Floating is chosen and a song with lyrics is playing (a short
/// pause keeps it up), and drawn by the wallpaper's own lyric stage, which
/// the desktop stops drawing meanwhile (`LyricsSurface`).
@MainActor
final class FloatingLyricsController {
    static let shared = FloatingLyricsController()
    static let windowTitle = "Lunalyr floating lyrics"
    /// The window's level: over app windows, under the menu bar's menus,
    /// the Dock's own level, alerts and the screen saver.
    static let level = NSWindow.Level.floating
    static let collectionBehavior: NSWindow.CollectionBehavior =
        [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
    /// How faint the window gets while the pointer is over it.
    static let pointerAlpha: CGFloat = 0.18

    let state = FloatingLyricsState()
    private let options = FloatingLyricsOptions.shared
    private var panel: FloatingLyricsPanel?
    /// The display key and frame the panel is on.
    private var placedOn: (key: String, screenFrame: CGRect, area: CGRect)?
    private var cancellables: Set<AnyCancellable> = []
    private var screenObserver: NSObjectProtocol?
    private var updatePending = false
    private var pausedSince: Date?
    private var pauseCheck: DispatchWorkItem?
    private var hoverTimer: Timer?
    private var drag: (start: CGRect, mouse: CGPoint, resizing: Bool)?
    private var shown = false
    /// Where the window's fade is heading.
    private var targetAlpha: CGFloat = 0

    var windowIDs: Set<CGWindowID> { panel?.windowServerID.map { [$0] } ?? [] }
    var isEditing: Bool { state.editing }

    func start() {
        guard screenObserver == nil else { return }
        let nowPlaying = NowPlayingMonitor.shared, lyrics = LyricsViewModel.shared
        // Each input fires before its value changes; `scheduleUpdate` looks
        // once they have, and only once for a burst.
        options.objectWillChange.sink { [weak self] _ in self?.scheduleUpdate() }.store(in: &cancellables)
        nowPlaying.$current.sink { [weak self] _ in self?.scheduleUpdate() }.store(in: &cancellables)
        nowPlaying.$isPlaying.sink { [weak self] _ in self?.scheduleUpdate() }.store(in: &cancellables)
        lyrics.$lines.sink { [weak self] _ in self?.scheduleUpdate() }.store(in: &cancellables)
        lyrics.$plainLines.sink { [weak self] _ in self?.scheduleUpdate() }.store(in: &cancellables)
        lyrics.$notFound.sink { [weak self] _ in self?.scheduleUpdate() }.store(in: &cancellables)
        LunalyrExtras.shared.$lyricsHidden.sink { [weak self] _ in self?.scheduleUpdate() }.store(in: &cancellables)
        LunalyrExtras.shared.objectWillChange.sink { [weak self] _ in self?.scheduleUpdate() }.store(in: &cancellables)
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.placedOn = nil; self?.scheduleUpdate() }
        }
        update()
    }

    private func scheduleUpdate() {
        guard !updatePending else { return }
        updatePending = true
        DispatchQueue.main.async { [weak self] in
            MainActor.assumeIsolated {
                self?.updatePending = false
                self?.update()
            }
        }
    }

    // MARK: Edit position

    func setEditing(_ editing: Bool) {
        let editing = editing && options.isFloating
        guard state.editing != editing else { return }
        state.editing = editing
        if !editing { drag = nil }
        update()
    }

    func toggleEditing() { setEditing(!state.editing) }

    /// Follows the pointer from where the drag began; on release, keeps the
    /// frame for this display as Custom.
    func dragged(resizing: Bool, ended: Bool) {
        guard state.editing, let panel, let placed = placedOn else { return }
        let mouse = NSEvent.mouseLocation
        if drag == nil || drag?.resizing != resizing { drag = (panel.frame, mouse, resizing) }
        guard let drag else { return }
        let delta = CGSize(width: mouse.x - drag.mouse.x, height: mouse.y - drag.mouse.y)
        let frame = FloatingFrame.dragged(drag.start, by: delta, resizing: resizing, in: placed.area)
        if panel.frame != frame { panel.setFrame(frame, display: true) }
        guard ended else { return }
        self.drag = nil
        options.storeCustomFrame(FloatingFrame.relative(frame, to: placed.screenFrame), for: placed.key)
    }

    // MARK: Showing

    private var visibilityInput: FloatingLyricsVisibility.Input {
        let nowPlaying = NowPlayingMonitor.shared, lyrics = LyricsViewModel.shared
        let hasTrack = nowPlaying.current != nil
        if !hasTrack || nowPlaying.isPlaying { pausedSince = nil } else if pausedSince == nil { pausedSince = Date() }
        return .init(surface: options.surface, editing: state.editing,
                     lyricsVisible: LunalyrExtras.shared.lyricsVisible, hasTrack: hasTrack,
                     isPlaying: nowPlaying.isPlaying, pausedFor: pausedSince.map { Date().timeIntervalSince($0) },
                     hasTimedLines: !lyrics.lines.isEmpty, hasPlainLines: !lyrics.plainLines.isEmpty,
                     allInstrumental: !lyrics.lines.isEmpty && lyrics.lines.allSatisfy(\.isInstrumental),
                     notFound: lyrics.notFound)
    }

    private func update() {
        if !options.isFloating, state.editing { state.editing = false }
        let input = visibilityInput
        pauseCheck?.cancel()
        pauseCheck = nil
        if let after = FloatingLyricsVisibility.hidesAfter(input) {
            let work = DispatchWorkItem { [weak self] in MainActor.assumeIsolated { self?.update() } }
            pauseCheck = work
            DispatchQueue.main.asyncAfter(deadline: .now() + after + 0.05, execute: work)
        }
        if FloatingLyricsVisibility.shows(input) { show() } else { hide() }
    }

    private func show() {
        let panel = self.panel ?? makePanel()
        let editing = state.editing
        panel.ignoresMouseEvents = !editing
        if drag == nil { place(panel) }
        if !state.active { state.active = true }
        if !shown {
            shown = true
            panel.alphaValue = 0
            targetAlpha = 0
            panel.orderFrontRegardless()
            fade(panel, to: 1)
        } else if !panel.isVisible {
            panel.orderFrontRegardless()
        }
        if editing, targetAlpha != 1 { fade(panel, to: 1) }
        updateHoverTimer()
    }

    private func hide() {
        hoverTimer?.invalidate()
        hoverTimer = nil
        guard shown, let panel else { return }
        shown = false
        fade(panel, to: 0) { [weak self] in
            guard let self, !self.shown else { return }
            panel.orderOut(nil)
            self.state.active = false
        }
    }

    private func fade(_ panel: NSPanel, to alpha: CGFloat, then done: (@MainActor () -> Void)? = nil) {
        targetAlpha = alpha
        if panel.alphaValue == alpha || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            panel.alphaValue = alpha
            done?()
            return
        }
        NSAnimationContext.runAnimationGroup({ context in
            context.duration = 0.25
            panel.animator().alphaValue = alpha
        }, completionHandler: {
            MainActor.assumeIsolated { done?() }
        })
    }

    /// Puts the panel where its placement says on the chosen display.
    private func place(_ panel: NSPanel) {
        let screens = NSScreen.screens.compactMap { screen in screen.versoDisplayID.map { (screen, $0) } }
        let keys = screens.map { FloatingFrame.displayKey($0.1) }
        guard let index = FloatingFrame.chosenIndex(options.display, among: keys) else { return }
        let screen = screens[index].0, key = keys[index]
        let area = FloatingFrame.usableArea(screenFrame: screen.frame, visibleFrame: screen.visibleFrame,
                                            safeTopInset: screen.safeAreaInsets.top)
        placedOn = (key, screen.frame, area)
        let frame = FloatingFrame.resolve(options.placement, custom: options.customFrame(for: key),
                                          savedSize: options.savedSize(for: key), textSize: CGFloat(options.textSize),
                                          screenFrame: screen.frame, area: area)
        if panel.frame != frame { panel.setFrame(frame, display: true) }
    }

    private func makePanel() -> FloatingLyricsPanel {
        let panel = FloatingLyricsPanel(contentRect: CGRect(x: 0, y: 0, width: 640, height: 200),
                                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.level = Self.level
        panel.collectionBehavior = Self.collectionBehavior
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isMovable = false
        panel.hidesOnDeactivate = false
        panel.canHide = false
        panel.isReleasedWhenClosed = false
        panel.ignoresMouseEvents = true
        panel.animationBehavior = .none
        panel.title = Self.windowTitle
        panel.isExcludedFromWindowsMenu = true
        let view = FloatingLyricsView(state: state, onDrag: { [weak self] resizing, ended in
            self?.dragged(resizing: resizing, ended: ended)
        }, onDone: { [weak self] in self?.setEditing(false) })
            .environmentObject(NowPlayingMonitor.shared)
        let hosting = FirstClickHostingView(rootView: AnyView(view))
        hosting.autoresizingMask = [.width, .height]
        panel.contentView = hosting
        self.panel = panel
        return panel
    }

    // MARK: Pointer over the window

    /// Four looks a second at the pointer while the window shows and should
    /// fade under it; nothing runs otherwise.
    private func updateHoverTimer() {
        let wanted = shown && options.fadesUnderPointer && !state.editing
        if !wanted {
            hoverTimer?.invalidate()
            hoverTimer = nil
            if let panel, shown, targetAlpha != 1 { fade(panel, to: 1) }
            return
        }
        guard hoverTimer == nil else { return }
        let timer = Timer(timeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.checkPointer() }
        }
        timer.tolerance = 0.1
        RunLoop.main.add(timer, forMode: .common)
        hoverTimer = timer
    }

    private func checkPointer() {
        guard let panel, shown, !state.editing else { return }
        let over = panel.frame.contains(NSEvent.mouseLocation)
        let target = over ? Self.pointerAlpha : 1
        if targetAlpha != target { fade(panel, to: target) }
    }
}
