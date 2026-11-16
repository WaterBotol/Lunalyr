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
private final class FirstClickHostingView<Content: View>: NSHostingView<Content> {
    /// The margin round the box, on every side.
    var boxInset: CGFloat = 0

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func hitTest(_ point: NSPoint) -> NSView? {
        let local = superview.map { convert(point, from: $0) } ?? point
        guard bounds.insetBy(dx: boxInset, dy: boxInset).contains(local) else { return nil }
        return super.hitTest(point)
    }
}

/// The floating lyrics window: one panel, on the chosen display, above the
/// apps (full-screen ones too) and on every Space, out of the Dock, ⌘-Tab and
/// Mission Control. Clicks pass through it except on its box in edit mode.
/// The window is the box (the frame the placement, a drag and the saved
/// frames give) with a see-through margin round it, so the lyrics' effects
/// can spill past the box; the conversion happens only here, at the
/// window. It is shown
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
    private var hosting: FirstClickHostingView<AnyView>?
    /// The box on screen: the window less its margin.
    private var box: CGRect?
    /// While in edit position: watches the pointer, so the window takes
    /// clicks on its box and lets those in its margin through.
    private var pointerMonitors: [Any] = []
    private var editPointerTimer: Timer?
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
        guard state.editing, let panel, let placed = placedOn, let box else { return }
        let mouse = NSEvent.mouseLocation
        if drag == nil || drag?.resizing != resizing { drag = (box, mouse, resizing) }
        guard let drag else { return }
        let delta = CGSize(width: mouse.x - drag.mouse.x, height: mouse.y - drag.mouse.y)
        let frame = FloatingFrame.dragged(drag.start, by: delta, resizing: resizing, in: placed.area)
        setBox(frame, on: panel)
        guard ended else { return }
        self.drag = nil
        options.storeCustomFrame(FloatingFrame.relative(frame, to: placed.screenFrame), for: placed.key)
        updateMouseTaking()
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
        if drag == nil { place(panel) }
        if !state.active { state.active = true }
        if !shown {
            shown = true
            SongStartLog.note("floating lyrics window shown")
            panel.alphaValue = 0
            targetAlpha = 0
            panel.orderFrontRegardless()
            fade(panel, to: 1)
        } else if !panel.isVisible {
            panel.orderFrontRegardless()
        }
        if editing, targetAlpha != 1 { fade(panel, to: 1) }
        updateHoverTimer()
        updateEditPointer()
    }

    private func hide() {
        hoverTimer?.invalidate()
        hoverTimer = nil
        guard shown, let panel else { updateEditPointer(); return }
        shown = false
        updateEditPointer()
        SongStartLog.note("floating lyrics window hidden")
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
        // Eases out arriving and in leaving, from the alpha shown now.
        LunalyrDesign.Motion.CA.fade(panel, to: alpha, duration: LunalyrDesign.Motion.CA.standard) {
            MainActor.assumeIsolated { done?() }
        }
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
        setBox(frame, on: panel)
    }

    /// Puts the box at `frame` (screen coordinates): the window goes round
    /// it, the margin following the text size.
    private func setBox(_ frame: CGRect, on panel: NSPanel) {
        let margin = FloatingFrame.spillMargin(textSize: CGFloat(options.textSize))
        box = frame
        if state.margin != margin { state.margin = margin }
        hosting?.boxInset = margin
        let window = FloatingFrame.window(forBox: frame, margin: margin)
        if panel.frame != window { panel.setFrame(window, display: true) }
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
        self.hosting = hosting
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
        let over = (box ?? panel.frame).contains(NSEvent.mouseLocation)
        let target = over ? Self.pointerAlpha : 1
        if targetAlpha != target { fade(panel, to: target) }
    }

    // MARK: Clicks in edit position

    /// Outside edit position the window takes no clicks at all. In it, the
    /// box takes them (the drag, the grip, Done) and the margin round it
    /// lets them through to what is underneath: the window takes the mouse
    /// only while the pointer is over the box. A view returning nil from
    /// its hit test would only swallow a click, so it is the window that
    /// switches. The pointer is followed by a global monitor (moves over
    /// other apps: the pointer may be coming onto the box), a local one
    /// (moves and releases over this window) and, for moves no monitor
    /// sees, a look twenty times a second, all only while editing.
    private func updateEditPointer() {
        let wanted = shown && state.editing
        guard wanted else {
            for monitor in pointerMonitors { NSEvent.removeMonitor(monitor) }
            pointerMonitors = []
            editPointerTimer?.invalidate()
            editPointerTimer = nil
            panel?.acceptsMouseMovedEvents = false
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
        panel?.acceptsMouseMovedEvents = true
        updateMouseTaking()
    }

    /// Takes the mouse over the box in edit position, never in the margin
    /// and never outside edit position. A drag under way keeps it, though a
    /// resize may leave the pointer outside the box.
    private func updateMouseTaking() {
        guard let panel else { return }
        guard state.editing, shown else {
            if !panel.ignoresMouseEvents { panel.ignoresMouseEvents = true }
            return
        }
        // Never switched mid-press: a click or drag ends where it began.
        if drag != nil || NSEvent.pressedMouseButtons != 0 { return }
        let takes = box?.contains(NSEvent.mouseLocation) ?? false
        if panel.ignoresMouseEvents == takes { panel.ignoresMouseEvents = !takes }
    }
}
