import AppKit
import Combine
import SwiftUI

/// One non-activating controls panel per display. Only the panel belonging
/// to the current desktop hover accepts mouse events; others are click-through.
@MainActor
final class ControlPanelController {
    static let shared = ControlPanelController()
    private var panels: [CGDirectDisplayID: NSPanel] = [:]
    private var cancellable: AnyCancellable?
    private var screenObserver: NSObjectProtocol?
    var windowIDs: Set<CGWindowID> {
        Set(panels.values.compactMap { $0.windowNumber > 0 ? CGWindowID($0.windowNumber) : nil })
    }

    func start() {
        guard screenObserver == nil else { return }
        cancellable = HoverState.shared.$activeDisplayID.removeDuplicates().sink { [weak self] displayID in
            self?.updateMouseHandling(activeDisplayID: displayID)
        }
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
        for screen in NSScreen.screens {
            guard let displayID = screen.versoDisplayID else { continue }
            let frame = vc_control_panel_frame(screen.versoFrame).rect
            let panel = NSPanel(
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
            let hosting = NSHostingView(
                rootView: MediaControlsView(displayID: displayID)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .environmentObject(NowPlayingMonitor.shared)
            )
            hosting.frame = CGRect(origin: .zero, size: frame.size)
            hosting.autoresizingMask = [.width, .height]
            panel.contentView = hosting
            panel.orderFrontRegardless()
            panels[displayID] = panel
        }
        updateMouseHandling(activeDisplayID: HoverState.shared.activeDisplayID)
    }

    private func updateMouseHandling(activeDisplayID: CGDirectDisplayID?) {
        for (displayID, panel) in panels {
            let active = displayID == activeDisplayID
            panel.ignoresMouseEvents = !active
            if active && !panel.isVisible { panel.orderFrontRegardless() }
        }
    }
}
