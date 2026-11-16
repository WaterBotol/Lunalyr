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

/// One floating panel and where it stands: a display's lyrics, or its
/// song title card.
@MainActor
final class FloatingPanelSlot {
    enum Kind { case lyrics, title }
    let kind: Kind
    /// What its content needs: whether it is on screen, editing, its margin.
    let state = FloatingLyricsState()
    var panel: FloatingLyricsPanel?
    var hosting: FirstClickHostingView<AnyView>?
    /// The box on screen: the window less its margin.
    var box: CGRect?
    var drag: (start: CGRect, mouse: CGPoint, resizing: Bool)?
    var shown = false
    /// Where the window's fade is heading.
    var targetAlpha: CGFloat = 0

    init(kind: Kind) { self.kind = kind }

    /// The smallest box a resize leaves.
    var minimumSize: CGSize { kind == .lyrics ? FloatingFrame.minimumSize : FloatingTitleFrame.minimumSize }
}

/// One display's floating windows: the lyrics, and the song title card.
@MainActor
final class FloatingLyricsWindow {
    let key: String
    let lyrics = FloatingPanelSlot(kind: .lyrics)
    let title = FloatingPanelSlot(kind: .title)
    /// The display's frame and the area the windows may use.
    var placedOn: (screenFrame: CGRect, area: CGRect)?

    var slots: [FloatingPanelSlot] { [lyrics, title] }

    init(key: String) { self.key = key }
}

/// The floating lyrics windows: on each display set to Floating or Both
/// (`DisplayLyrics`), a panel for the lyrics and, unless it is turned off,
/// one for the song title card, which can sit anywhere on that display.
/// They float above the apps (full-screen ones too) and on every Space, out
/// of the Dock, ⌘-Tab and Mission Control. Clicks pass through them except
/// on their box in edit mode. Each window is its box (the frame the
/// placement, a drag and the saved frames give for that display) with a
/// see-through margin round it, so the lyrics' effects and the card's glow
/// can spill past the box; the conversion happens only here, at the window.
/// They are shown only while a song with lyrics is playing (a short pause
/// keeps them up) and drawn by the wallpaper's own lyric stage in the
/// Floating style (`LyricStyles`). The lyrics, their timing and the
/// cover's colours are shared; each window only draws. Nothing is made for
/// a display that does not float.
@MainActor
final class FloatingLyricsController {
    static let shared = FloatingLyricsController()
    static let windowTitle = "Lunalyr floating lyrics"
    static let titleWindowTitle = "Lunalyr floating song title"
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

    private var allSlots: [FloatingPanelSlot] { windows.values.flatMap(\.slots) }
    var windowIDs: Set<CGWindowID> { Set(allSlots.compactMap { $0.panel?.windowServerID }) }
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
        if !editing { for slot in allSlots { slot.drag = nil } }
        update()
    }

    func toggleEditing() { setEditing(!state.editing) }

    /// Follows the pointer from where the drag began; on release, keeps the
    /// frame for this display: the lyrics' as Custom, the card's as its own.
    func dragged(_ slot: FloatingPanelSlot, of window: FloatingLyricsWindow, resizing: Bool, ended: Bool) {
        guard state.editing, let panel = slot.panel, let placed = window.placedOn, let box = slot.box else { return }
        let mouse = NSEvent.mouseLocation
        if slot.drag == nil || slot.drag?.resizing != resizing { slot.drag = (box, mouse, resizing) }
        guard let drag = slot.drag else { return }
        let delta = CGSize(width: mouse.x - drag.mouse.x, height: mouse.y - drag.mouse.y)
        let frame = FloatingFrame.dragged(drag.start, by: delta, resizing: resizing, in: placed.area,
                                          minimum: slot.minimumSize)
        setBox(frame, of: slot, on: panel)
        guard ended else { return }
        slot.drag = nil
        let relative = FloatingFrame.relative(frame, to: placed.screenFrame)
        switch slot.kind {
        case .lyrics: options.storeCustomFrame(relative, for: window.key)
        case .title: options.storeTitleFrame(relative, for: window.key)
        }
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
        if !floating.isEmpty { LyricStyles.shared.keepFloatingStyle() }
        let input = visibilityInput(floats: true)
        pauseCheck?.cancel()
        pauseCheck = nil
        if !floating.isEmpty, let after = FloatingLyricsVisibility.hidesAfter(input) {
            let work = DispatchWorkItem { [weak self] in MainActor.assumeIsolated { self?.update() } }
            pauseCheck = work
            DispatchQueue.main.asyncAfter(deadline: .now() + after + 0.05, execute: work)
        }
        let showing = FloatingLyricsVisibility.shows(input)
        let card = FloatingTitleVisibility.shows(lyrics: showing, card: options.showsTitleCard)
        var placed: Set<String> = []
        for (screen, key) in screens where floating.contains(key) && showing {
            let window = windows[key] ?? makeWindow(key)
            placeOn(window, screen: screen)
            show(window.lyrics, of: window)
            if card { show(window.title, of: window) } else { hide(window.title) }
            placed.insert(key)
        }
        for window in windows.values where !placed.contains(window.key) {
            for slot in window.slots { hide(slot) }
        }
        updateHoverTimer()
        updateEditPointer()
    }

    private func makeWindow(_ key: String) -> FloatingLyricsWindow {
        let window = FloatingLyricsWindow(key: key)
        windows[key] = window
        return window
    }

    /// Where the windows may go on the display.
    private func placeOn(_ window: FloatingLyricsWindow, screen: NSScreen) {
        let area = FloatingFrame.usableArea(screenFrame: screen.frame, visibleFrame: screen.visibleFrame,
                                            safeTopInset: screen.safeAreaInsets.top)
        window.placedOn = (screen.frame, area)
    }

    private func show(_ slot: FloatingPanelSlot, of window: FloatingLyricsWindow) {
        let panel = slot.panel ?? makePanel(for: slot, of: window)
        let editing = state.editing
        if slot.state.editing != editing { slot.state.editing = editing }
        if slot.drag == nil { place(slot, of: window, on: panel) }
        if !slot.state.active { slot.state.active = true }
        if !slot.shown {
            slot.shown = true
            SongStartLog.note(slot.kind == .lyrics ? "floating lyrics window shown" : "floating song title shown")
            panel.alphaValue = 0
            slot.targetAlpha = 0
            panel.orderFrontRegardless()
            fade(slot, to: 1)
        } else if !panel.isVisible {
            panel.orderFrontRegardless()
        }
        if editing, slot.targetAlpha != 1 { fade(slot, to: 1) }
    }

    private func hide(_ slot: FloatingPanelSlot) {
        if slot.state.editing { slot.state.editing = false }
        slot.drag = nil
        guard slot.shown, slot.panel != nil else { return }
        slot.shown = false
        SongStartLog.note(slot.kind == .lyrics ? "floating lyrics window hidden" : "floating song title hidden")
        fade(slot, to: 0) {
            guard !slot.shown else { return }
            slot.panel?.orderOut(nil)
            slot.state.active = false
        }
    }

    private func fade(_ slot: FloatingPanelSlot, to alpha: CGFloat, then done: (@MainActor () -> Void)? = nil) {
        guard let panel = slot.panel else { done?(); return }
        slot.targetAlpha = alpha
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

    /// Puts the panel where this display's placement says: the lyrics by
    /// their place, the card where it was put or just above the lyrics.
    private func place(_ slot: FloatingPanelSlot, of window: FloatingLyricsWindow, on panel: NSPanel) {
        guard let placed = window.placedOn else { return }
        let key = window.key, textSize = CGFloat(options.textSize)
        let lyrics = FloatingFrame.resolve(options.placement(for: key), custom: options.customFrame(for: key),
                                           savedSize: options.savedSize(for: key), textSize: textSize,
                                           screenFrame: placed.screenFrame, area: placed.area)
        switch slot.kind {
        case .lyrics:
            setBox(lyrics, of: slot, on: panel)
        case .title:
            let frame = FloatingTitleFrame.resolve(custom: options.titleFrame(for: key), start: options.titleStart,
                                                   box: window.lyrics.drag == nil ? lyrics : (window.lyrics.box ?? lyrics),
                                                   textSize: textSize, screenFrame: placed.screenFrame, area: placed.area)
            setBox(frame, of: slot, on: panel)
        }
    }

    /// Puts the box at `frame` (screen coordinates): the window goes round
    /// it, the margin following the text size for the lyrics.
    private func setBox(_ frame: CGRect, of slot: FloatingPanelSlot, on panel: NSPanel) {
        let margin = slot.kind == .lyrics ? FloatingFrame.spillMargin(textSize: CGFloat(options.textSize))
            : FloatingTitleFrame.margin
        slot.box = frame
        if slot.state.margin != margin { slot.state.margin = margin }
        slot.hosting?.boxInset = margin
        let outer = FloatingFrame.window(forBox: frame, margin: margin)
        if panel.frame != outer { panel.setFrame(outer, display: true) }
    }

    private func makePanel(for slot: FloatingPanelSlot, of window: FloatingLyricsWindow) -> FloatingLyricsPanel {
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
        panel.title = slot.kind == .lyrics ? Self.windowTitle : Self.titleWindowTitle
        panel.isExcludedFromWindowsMenu = true
        let onDrag: (Bool, Bool) -> Void = { [weak self, weak slot, weak window] resizing, ended in
            guard let slot, let window else { return }
            self?.dragged(slot, of: window, resizing: resizing, ended: ended)
        }
        let onDone: () -> Void = { [weak self] in self?.setEditing(false) }
        let view: AnyView
        switch slot.kind {
        case .lyrics:
            view = AnyView(FloatingLyricsView(state: slot.state, onDrag: onDrag, onDone: onDone)
                .environmentObject(NowPlayingMonitor.shared))
        case .title:
            view = AnyView(FloatingTitleCardView(state: slot.state, onDrag: onDrag, onDone: onDone)
                .environmentObject(NowPlayingMonitor.shared))
        }
        let hosting = FirstClickHostingView(rootView: view)
        hosting.autoresizingMask = [.width, .height]
        panel.contentView = hosting
        slot.hosting = hosting
        slot.panel = panel
        return panel
    }

    // MARK: Pointer over the windows

    private var shownSlots: [FloatingPanelSlot] { allSlots.filter(\.shown) }

    /// Four looks a second at the pointer while a window shows and should
    /// fade under it; nothing runs otherwise.
    private func updateHoverTimer() {
        let wanted = !shownSlots.isEmpty && options.fadesUnderPointer && !state.editing
        if !wanted {
            hoverTimer?.invalidate()
            hoverTimer = nil
            for slot in shownSlots where slot.targetAlpha != 1 { fade(slot, to: 1) }
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
        for slot in shownSlots {
            guard let panel = slot.panel else { continue }
            let over = (slot.box ?? panel.frame).contains(mouse)
            let target = over ? Self.pointerAlpha : 1
            if slot.targetAlpha != target { fade(slot, to: target) }
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
        let wanted = state.editing && !shownSlots.isEmpty
        guard wanted else {
            for monitor in pointerMonitors { NSEvent.removeMonitor(monitor) }
            pointerMonitors = []
            editPointerTimer?.invalidate()
            editPointerTimer = nil
            for slot in allSlots { slot.panel?.acceptsMouseMovedEvents = false }
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
        for slot in shownSlots { slot.panel?.acceptsMouseMovedEvents = true }
        updateMouseTaking()
    }

    /// Takes the mouse over a box in edit position, never in the margin
    /// and never outside edit position. A drag under way keeps it, though a
    /// resize may leave the pointer outside the box.
    private func updateMouseTaking() {
        let mouse = NSEvent.mouseLocation
        let pressed = NSEvent.pressedMouseButtons != 0
        for slot in allSlots {
            guard let panel = slot.panel else { continue }
            guard state.editing, slot.shown else {
                if !panel.ignoresMouseEvents { panel.ignoresMouseEvents = true }
                continue
            }
            // Never switched mid-press: a click or drag ends where it began.
            if slot.drag != nil || pressed { continue }
            let takes = slot.box?.contains(mouse) ?? false
            if panel.ignoresMouseEvents == takes { panel.ignoresMouseEvents = !takes }
        }
    }
}

/// When a display's song title card window is up.
enum FloatingTitleVisibility {
    /// With the floating lyrics, while the card is turned on. With the
    /// Floating style's song title off it draws nothing, and its stage's
    /// clock rests (`LyricTitleMode.only` paces by the card alone).
    static func shows(lyrics: Bool, card: Bool) -> Bool { lyrics && card }
}
