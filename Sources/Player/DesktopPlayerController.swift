import AppKit
import Combine
import SwiftUI

/// A panel that can take typing without making Lunalyr the frontmost app,
/// so the search box works while another app keeps the focus.
private final class PlayerPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

/// Puts the desktop player on the main display, just above the Dock. The
/// panel is only as large as what is drawn, so the rest of the desktop
/// stays clickable.
@MainActor
final class DesktopPlayerController {
    static let shared = DesktopPlayerController()
    static let windowTitle = "Lunalyr player"

    private var panel: NSPanel?
    private var cancellables: Set<AnyCancellable> = []
    private var screenObserver: NSObjectProtocol?

    var windowIDs: Set<CGWindowID> {
        Set([panel?.windowServerID].compactMap { $0 })
    }

    func start() {
        guard screenObserver == nil else { return }
        DesktopOptions.shared.$showsDesktopPlayer.removeDuplicates().sink { [weak self] _ in
            Task { @MainActor [weak self] in self?.rebuild() }
        }.store(in: &cancellables)
        DesktopPlayerModel.shared.$isExpanded.removeDuplicates().sink { [weak self] _ in
            Task { @MainActor [weak self] in self?.resize() }
        }.store(in: &cancellables)
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor [weak self] in self?.rebuild() }
        }
        rebuild()
    }

    private func rebuild() {
        panel?.orderOut(nil)
        panel?.contentView = nil
        panel?.close()
        panel = nil
        guard DesktopOptions.shared.showsDesktopPlayer, let screen = NSScreen.screens.first else { return }
        let panel = PlayerPanel(
            contentRect: NSRect(origin: .zero, size: Self.size(expanded: false)),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false, screen: screen)
        panel.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 1)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isMovable = false
        panel.hidesOnDeactivate = false
        panel.canHide = false
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenNone]
        panel.title = Self.windowTitle
        panel.isExcludedFromWindowsMenu = true
        let hosting = NSHostingView(rootView:
            DesktopPlayerView(model: DesktopPlayerModel.shared).environmentObject(NowPlayingMonitor.shared))
        hosting.autoresizingMask = [.width, .height]
        panel.contentView = hosting
        self.panel = panel
        resize()
        panel.orderFrontRegardless()
    }

    private static func size(expanded: Bool) -> CGSize {
        // Room above and around the bar for its shadow.
        let height = DesktopPlayerView.barHeight + 28 + (expanded ? DesktopPlayerView.drawerHeight + 8 : 0)
        return CGSize(width: DesktopPlayerView.width + 56, height: height)
    }

    private func resize() {
        guard let panel, let screen = NSScreen.screens.first else { return }
        let size = Self.size(expanded: DesktopPlayerModel.shared.isExpanded)
        let area = screen.visibleFrame
        let frame = CGRect(x: area.midX - size.width / 2, y: area.minY + 6, width: size.width, height: size.height)
        panel.setFrame(frame, display: true)
        panel.contentView?.frame = CGRect(origin: .zero, size: size)
    }
}
