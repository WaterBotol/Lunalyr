import AppKit
import Combine
import SwiftUI

/// A panel that can take typing without making Lunalyr the frontmost app,
/// so the player's search box works while another app keeps the focus.
private final class PlayerPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

/// One non-activating controls panel per display. Panels are click-through
/// everywhere except over the controls actually drawn on the display being
/// hovered, so desktop icons, rubber-band selections, context menus and
/// drops in the middle of the screen still reach Finder.
@MainActor
final class ControlPanelController {
    static let shared = ControlPanelController()
    static let windowTitle = "Lunalyr controls"

    private var panels: [CGDirectDisplayID: NSPanel] = [:]
    private var masks: [CGDirectDisplayID: (mask: ControlHitMask?, renderedAt: TimeInterval)] = [:]
    private var masksStale = true
    private var cancellables: Set<AnyCancellable> = []
    private var screenObserver: NSObjectProtocol?
    var windowIDs: Set<CGWindowID> {
        Set(panels.values.compactMap(\.windowServerID))
    }

    func start() {
        guard screenObserver == nil else { return }
        HoverState.shared.$activeDisplayID.removeDuplicates().sink { [weak self] _ in
            // Published before the value changes; act once it has.
            Task { @MainActor [weak self] in
                self?.masksStale = true
                self?.updatePointer(buttonsHeld: NSEvent.pressedMouseButtons != 0)
            }
        }.store(in: &cancellables)
        // What the panel draws changes with the track, artwork, play state
        // and lyric provenance; redraw the hit mask lazily after any of them.
        NowPlayingMonitor.shared.objectWillChange.sink { [weak self] _ in self?.masksStale = true }
            .store(in: &cancellables)
        DesktopPlayerModel.shared.objectWillChange.sink { [weak self] _ in self?.masksStale = true }
            .store(in: &cancellables)
        LyricsViewModel.shared.$provenance.sink { [weak self] _ in self?.masksStale = true }
            .store(in: &cancellables)
        DesktopOptions.shared.$showsControlsOnHover.removeDuplicates().dropFirst().sink { [weak self] _ in
            Task { @MainActor [weak self] in self?.rebuild() }
        }.store(in: &cancellables)
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
        for panel in panels.values { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        panels.removeAll()
        masks.removeAll()
        // With hover controls switched off there is nothing to show, so no
        // panel sits over the desktop at all.
        guard DesktopOptions.shared.showsControlsOnHover else { return }
        for screen in NSScreen.screens {
            guard let displayID = screen.versoDisplayID else { continue }
            let frame = vc_control_panel_frame(screen.versoFrame).rect
            let panel = PlayerPanel(
                contentRect: NSRect(origin: .zero, size: frame.size),
                styleMask: [.borderless, .nonactivatingPanel],
                backing: .buffered, defer: false, screen: screen
            )
            panel.setFrame(frame, display: false)
            panel.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 1)
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.hasShadow = false
            panel.isMovable = false
            // This is a background utility: Finder will normally be active
            // when the pointer is over desktop. NSPanel otherwise hides itself.
            panel.hidesOnDeactivate = false
            panel.canHide = false
            panel.isReleasedWhenClosed = false
            panel.ignoresMouseEvents = true
            panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenNone]
            panel.title = Self.windowTitle
            panel.isExcludedFromWindowsMenu = true
            let hosting = NSHostingView(rootView: Self.content(displayID: displayID))
            hosting.frame = CGRect(origin: .zero, size: frame.size)
            hosting.autoresizingMask = [.width, .height]
            panel.contentView = hosting
            panel.orderFrontRegardless()
            panels[displayID] = panel
        }
        masksStale = true
        updatePointer(buttonsHeld: NSEvent.pressedMouseButtons != 0)
    }

    private static func content(displayID: CGDirectDisplayID, forMask: Bool = false) -> some View {
        DesktopPlayerView(model: DesktopPlayerModel.shared, displayID: displayID)
            .environment(\.playerMaskRender, forMask)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .environmentObject(NowPlayingMonitor.shared)
    }

    /// Called from the hover poll: only the hovered display's panel may take
    /// the pointer, and only over drawn controls. A held button never changes
    /// which window owns the pointer.
    func updatePointer(buttonsHeld: Bool) {
        let location = NSEvent.mouseLocation
        let active = HoverState.shared.activeDisplayID
        for (displayID, panel) in panels {
            let isActive = displayID == active
            let accepts = ControlHitMask.acceptsMouse(
                active: isActive,
                overContent: isActive && !buttonsHeld && isOverControls(location, panel: panel, displayID: displayID),
                buttonsHeld: buttonsHeld, previously: !panel.ignoresMouseEvents)
            if panel.ignoresMouseEvents == accepts { panel.ignoresMouseEvents = !accepts }
            if isActive && !panel.isVisible { panel.orderFrontRegardless() }
        }
    }

    private func isOverControls(_ location: CGPoint, panel: NSPanel, displayID: CGDirectDisplayID) -> Bool {
        let local = CGPoint(x: location.x - panel.frame.minX, y: location.y - panel.frame.minY)
        guard CGRect(origin: .zero, size: panel.frame.size).contains(local) else { return false }
        let now = ProcessInfo.processInfo.systemUptime
        let entry = masks[displayID]
        if ControlHitMask.needsRender(usable: entry.map { $0.mask.map { !$0.isEmpty } ?? false }, stale: masksStale,
                                      age: now - (entry?.renderedAt ?? now)) {
            masksStale = false
            masks[displayID] = (ControlHitMask.render(Self.content(displayID: displayID, forMask: true), size: panel.frame.size), now)
        }
        // If the panel could not be rendered (or rendered before its content
        // appeared), keep the controls usable; the mask is retried shortly.
        guard let mask = masks[displayID]?.mask, !mask.isEmpty else { return true }
        return mask.contains(local)
    }
}
