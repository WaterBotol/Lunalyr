import AppKit
import Combine
import SwiftUI

/// A borderless, see-through panel that never activates Lunalyr and never
/// takes the keyboard, so typing always stays with the app in front. It
/// takes the pointer only in edit mode.
final class FloatingLyricsPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
    /// The window is the box plus a see-through margin that may run off the
    /// screen; AppKit must not pull it back on.
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
}

/// Takes the first click, so Done and the drag work without a click to
/// bring the panel forward first. Nothing in the see-through margin round
/// the box is hit, as a backstop to the controller letting the margin's
/// clicks through to what is underneath (`updateMouseTaking`).
final class FirstClickHostingView<Content: View>: NSHostingView<Content> {
    /// The margin round the box, on every side.
    var boxInset: CGFloat = 0

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func hitTest(_ point: NSPoint) -> NSView? {
        let local = superview.map { convert(point, from: $0) } ?? point
        guard bounds.insetBy(dx: boxInset, dy: boxInset).contains(local) else { return nil }
        return super.hitTest(point)
    }
}

/// One display's floating lyrics window and where it stands.
@MainActor
final class FloatingLyricsWindow {
    let key: String
    /// What its content needs: whether it is on screen, editing, its margin.
    let state = FloatingLyricsState()
    var panel: FloatingLyricsPanel?
    var hosting: FirstClickHostingView<AnyView>?
    /// The box on screen: the window less its margin.
    var box: CGRect?
    /// The display's frame and the area the window may use.
    var placedOn: (screenFrame: CGRect, area: CGRect)?
    var drag: (start: CGRect, mouse: CGPoint, resizing: Bool)?
    var shown = false
    /// Where the window's fade is heading.
    var targetAlpha: CGFloat = 0

    init(key: String) { self.key = key }
}

/// The floating lyrics windows: one panel on each display set to Floating
/// or Both (`DisplayLyrics`), above the apps (full-screen ones too) and on
/// every Space, out of the Dock, ⌘-Tab and Mission Control. Clicks pass
/// through them except on their box in edit mode. Each window is its box
/// (the frame the placement, a drag and the saved frames give for that
/// display) with a see-through margin round it, so the lyrics' effects can
/// spill past the box; the conversion happens only here, at the window.
/// A window is shown only while a song with lyrics is playing (a short
/// pause keeps it up) and drawn by the wallpaper's own lyric stage in the
/// Floating style (`LyricStyles`). The lyrics, their timing and the cover's
/// colours are shared; each window only draws. Nothing is made for a
/// display that does not float.
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

    /// Edit position, for every floating window at once.
    let state = FloatingLyricsState()
    private let options = FloatingLyricsOptions.shared
    private(set) var windows: [String: FloatingLyricsWindow] = [:]
    /// While in edit position: watches the pointer, so a window takes
    /// clicks on its box and lets those in its margin through.
    private var pointerMonitors: [Any] = []
    private var editPointerTimer: Timer?
    private var cancellables: Set<AnyCancellable> = []
    private var screenObserver: NSObjectProtocol?
    private var updatePending = false
    private var pausedSince: Date?
    private var pauseCheck: DispatchWorkItem?
    private var hoverTimer: Timer?

    var windowIDs: Set<CGWindowID> { Set(windows.values.compactMap { $0.panel?.windowServerID }) }
    var isEditing: Bool { state.editing }

    /// The connected displays' keys, the main display first.
    private var connected: [String] { FloatingFrame.connectedKeys() }

    /// Some display has floating lyrics.
    var floatsAnywhere: Bool { options.floats(among: connected) }

    /// The Floating quick toggle (menu bar, link, Shortcuts): off on every
    /// display when any floats, else on for the display whose menu bar is
    /// in use (`NSScreen.main`).
    func toggleFloating() {
        let target = NSScreen.main?.versoDisplayID.map(FloatingFrame.cachedKey)
        options.toggleFloating(connected: connected, target: target)
    }

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
            MainActor.assumeIsolated {
                FloatingFrame.forgetCachedKeys()
                guard let self else { return }
                self.options.migrateDisplaysIfNeeded(connected: self.connected)
                for window in self.windows.values { window.placedOn = nil }
                self.scheduleUpdate()
            }
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
        let editing = editing && floatsAnywhere
        guard state.editing != editing else { return }
        state.editing = editing
        if !editing { for window in windows.values { window.drag = nil } }
        update()
    }

    func toggleEditing() { setEditing(!state.editing) }

    /// Follows the pointer from where the drag began; on release, keeps the
    /// frame for this display as Custom.
    func dragged(_ window: FloatingLyricsWindow, resizing: Bool, ended: Bool) {
        guard state.editing, let panel = window.panel, let placed = window.placedOn, let box = window.box else { return }
        let mouse = NSEvent.mouseLocation
        if window.drag == nil || window.drag?.resizing != resizing { window.drag = (box, mouse, resizing) }
        guard let drag = window.drag else { return }
        let delta = CGSize(width: mouse.x - drag.mouse.x, height: mouse.y - drag.mouse.y)
        let frame = FloatingFrame.dragged(drag.start, by: delta, resizing: resizing, in: placed.area)
        setBox(frame, of: window, on: panel)
        guard ended else { return }
        window.drag = nil
        options.storeCustomFrame(FloatingFrame.relative(frame, to: placed.screenFrame), for: window.key)
        updateMouseTaking()
    }

    // MARK: Showing

    private func visibilityInput(floats: Bool) -> FloatingLyricsVisibility.Input {
        let nowPlaying = NowPlayingMonitor.shared, lyrics = LyricsViewModel.shared
        return .init(floats: floats, editing: state.editing,
                     lyricsVisible: LunalyrExtras.shared.lyricsVisible, hasTrack: nowPlaying.current != nil,
                     isPlaying: nowPlaying.isPlaying, pausedFor: pausedSince.map { Date().timeIntervalSince($0) },
                     hasTimedLines: !lyrics.lines.isEmpty, hasPlainLines: !lyrics.plainLines.isEmpty,
                     allInstrumental: !lyrics.lines.isEmpty && lyrics.lines.allSatisfy(\.isInstrumental),
                     notFound: lyrics.notFound)
    }

    private func update() {
        let nowPlaying = NowPlayingMonitor.shared
        if nowPlaying.current == nil || nowPlaying.isPlaying { pausedSince = nil } else if pausedSince == nil { pausedSince = Date() }
        let screens = NSScreen.screens.compactMap { screen in screen.versoDisplayID.map { (screen, FloatingFrame.cachedKey($0)) } }
        let floating = Set(options.floatingDisplays(among: screens.map(\.1)))
        if floating.isEmpty, state.editing { state.editing = false }
        let input = visibilityInput(floats: true)
        pauseCheck?.cancel()
        pauseCheck = nil
        if !floating.isEmpty, let after = FloatingLyricsVisibility.hidesAfter(input) {
            let work = DispatchWorkItem { [weak self] in MainActor.assumeIsolated { self?.update() } }
            pauseCheck = work
            DispatchQueue.main.asyncAfter(deadline: .now() + after + 0.05, execute: work)
        }
        let showing = FloatingLyricsVisibility.shows(input)
        for (screen, key) in screens where floating.contains(key) && showing {
            show(windows[key] ?? makeWindow(key), on: screen)
        }
        for window in windows.values where !(floating.contains(window.key) && showing) {
            hide(window)
        }
        updateHoverTimer()
        updateEditPointer()
    }

    private func makeWindow(_ key: String) -> FloatingLyricsWindow {
        let window = FloatingLyricsWindow(key: key)
        windows[key] = window
        return window
    }

    private func show(_ window: FloatingLyricsWindow, on screen: NSScreen) {
        let panel = window.panel ?? makePanel(for: window)
        let editing = state.editing
        if window.state.editing != editing { window.state.editing = editing }
        if window.drag == nil { place(window, on: panel, screen: screen) }
        if !window.state.active { window.state.active = true }
        if !window.shown {
            window.shown = true
            SongStartLog.note("floating lyrics window shown")
            panel.alphaValue = 0
            window.targetAlpha = 0
            panel.orderFrontRegardless()
            fade(window, to: 1)
        } else if !panel.isVisible {
            panel.orderFrontRegardless()
        }
        if editing, window.targetAlpha != 1 { fade(window, to: 1) }
    }

    private func hide(_ window: FloatingLyricsWindow) {
        if window.state.editing { window.state.editing = false }
        window.drag = nil
        guard window.shown, window.panel != nil else { return }
        window.shown = false
        SongStartLog.note("floating lyrics window hidden")
        fade(window, to: 0) {
            guard !window.shown else { return }
            window.panel?.orderOut(nil)
            window.state.active = false
        }
    }

    private func fade(_ window: FloatingLyricsWindow, to alpha: CGFloat, then done: (@MainActor () -> Void)? = nil) {
        guard let panel = window.panel else { done?(); return }
        window.targetAlpha = alpha
        if panel.alphaValue == alpha || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            panel.alphaValue = alpha
            done?()
            return
        }
        // Eases out arriving and in leaving, from the alpha shown now.
        LunalyrDesign.Motion.CA.fade(panel, to: alpha, duration: LunalyrDesign.Motion.CA.standard) {
            MainActor.assumeIsolated { done?() }
        }
    }

    /// Puts the panel where this display's placement says.
    private func place(_ window: FloatingLyricsWindow, on panel: NSPanel, screen: NSScreen) {
        let key = window.key
        let area = FloatingFrame.usableArea(screenFrame: screen.frame, visibleFrame: screen.visibleFrame,
                                            safeTopInset: screen.safeAreaInsets.top)
        window.placedOn = (screen.frame, area)
        let frame = FloatingFrame.resolve(options.placement(for: key), custom: options.customFrame(for: key),
                                          savedSize: options.savedSize(for: key), textSize: CGFloat(options.textSize),
                                          screenFrame: screen.frame, area: area)
        setBox(frame, of: window, on: panel)
    }

    /// Puts the box at `frame` (screen coordinates): the window goes round
    /// it, the margin following the text size.
    private func setBox(_ frame: CGRect, of window: FloatingLyricsWindow, on panel: NSPanel) {
        let margin = FloatingFrame.spillMargin(textSize: CGFloat(options.textSize))
        window.box = frame
        if window.state.margin != margin { window.state.margin = margin }
        window.hosting?.boxInset = margin
        let outer = FloatingFrame.window(forBox: frame, margin: margin)
        if panel.frame != outer { panel.setFrame(outer, display: true) }
    }

    private func makePanel(for window: FloatingLyricsWindow) -> FloatingLyricsPanel {
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
        let view = FloatingLyricsView(state: window.state, onDrag: { [weak self, weak window] resizing, ended in
            guard let window else { return }
            self?.dragged(window, resizing: resizing, ended: ended)
        }, onDone: { [weak self] in self?.setEditing(false) })
            .environmentObject(NowPlayingMonitor.shared)
        let hosting = FirstClickHostingView(rootView: AnyView(view))
        hosting.autoresizingMask = [.width, .height]
        panel.contentView = hosting
        window.hosting = hosting
        window.panel = panel
        return panel
    }

    // MARK: Pointer over the windows

    private var shownWindows: [FloatingLyricsWindow] { windows.values.filter(\.shown) }

    /// Four looks a second at the pointer while a window shows and should
    /// fade under it; nothing runs otherwise.
    private func updateHoverTimer() {
        let wanted = !shownWindows.isEmpty && options.fadesUnderPointer && !state.editing
        if !wanted {
            hoverTimer?.invalidate()
            hoverTimer = nil
            for window in shownWindows where window.targetAlpha != 1 { fade(window, to: 1) }
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
        guard !state.editing else { return }
        let mouse = NSEvent.mouseLocation
        for window in shownWindows {
            guard let panel = window.panel else { continue }
            let over = (window.box ?? panel.frame).contains(mouse)
            let target = over ? Self.pointerAlpha : 1
            if window.targetAlpha != target { fade(window, to: target) }
        }
    }

    // MARK: Clicks in edit position

    /// Outside edit position the windows take no clicks at all. In it, a
    /// box takes them (the drag, the grip, Done) and the margin round it
    /// lets them through to what is underneath: a window takes the mouse
    /// only while the pointer is over its box. A view returning nil from
    /// its hit test would only swallow a click, so it is the window that
    /// switches. The pointer is followed by a global monitor (moves over
    /// other apps: the pointer may be coming onto a box), a local one
    /// (moves and releases over these windows) and, for moves no monitor
    /// sees, a look twenty times a second, all only while editing.
    private func updateEditPointer() {
        let wanted = state.editing && !shownWindows.isEmpty
        guard wanted else {
            for monitor in pointerMonitors { NSEvent.removeMonitor(monitor) }
            pointerMonitors = []
            editPointerTimer?.invalidate()
            editPointerTimer = nil
            for window in windows.values { window.panel?.acceptsMouseMovedEvents = false }
            updateMouseTaking()
            return
        }
        if pointerMonitors.isEmpty {
            let events: NSEvent.EventTypeMask = [.mouseMoved, .leftMouseUp, .leftMouseDragged]
            if let global = NSEvent.addGlobalMonitorForEvents(matching: events, handler: { [weak self] _ in
                MainActor.assumeIsolated { self?.updateMouseTaking() }
            }) { pointerMonitors.append(global) }
            if let local = NSEvent.addLocalMonitorForEvents(matching: events, handler: { [weak self] event in
                MainActor.assumeIsolated { self?.updateMouseTaking() }
                return event
            }) { pointerMonitors.append(local) }
        }
        if editPointerTimer == nil {
            let timer = Timer(timeInterval: 0.05, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.updateMouseTaking() }
            }
            timer.tolerance = 0.02
            RunLoop.main.add(timer, forMode: .common)
            editPointerTimer = timer
        }
        for window in shownWindows { window.panel?.acceptsMouseMovedEvents = true }
        updateMouseTaking()
    }

    /// Takes the mouse over a box in edit position, never in the margin
    /// and never outside edit position. A drag under way keeps it, though a
    /// resize may leave the pointer outside the box.
    private func updateMouseTaking() {
        let mouse = NSEvent.mouseLocation
        let pressed = NSEvent.pressedMouseButtons != 0
        for window in windows.values {
            guard let panel = window.panel else { continue }
            guard state.editing, window.shown else {
                if !panel.ignoresMouseEvents { panel.ignoresMouseEvents = true }
                continue
            }
            // Never switched mid-press: a click or drag ends where it began.
            if window.drag != nil || pressed { continue }
            let takes = window.box?.contains(mouse) ?? false
            if panel.ignoresMouseEvents == takes { panel.ignoresMouseEvents = !takes }
        }
    }
}
