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
        // Grow the window at once so the content can animate into it; shrink
        // it only after the content has finished animating away.
        DesktopPlayerModel.shared.$isExpanded.combineLatest(DesktopPlayerModel.shared.$query.map { !$0.trimmingCharacters(in: .whitespaces).isEmpty }.removeDuplicates())
            .sink { [weak self] _, _ in
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
            contentRect: NSRect(origin: .zero, size: Self.size(drawer: 0)),
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
        hosting.sizingOptions = []
        panel.contentView = hosting
        self.panel = panel
        resize()
        panel.orderFrontRegardless()
    }

    private static func size(drawer: CGFloat) -> CGSize {
        // Room above and around the bar for its shadow.
        CGSize(width: DesktopPlayerView.width + 56, height: DesktopPlayerView.barHeight + 28 + (drawer > 0 ? drawer + 10 : 0))
    }

    private var shrinkWork: DispatchWorkItem?

    private func resize() {
        shrinkWork?.cancel()
        guard let panel else { return }
        let size = Self.size(drawer: DesktopPlayerModel.shared.drawerHeight)
        if size.height < panel.frame.height {
            let work = DispatchWorkItem { [weak self] in self?.apply(size) }
            shrinkWork = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.4, execute: work)
        } else {
            apply(size)
        }
    }

    private func apply(_ size: CGSize) {
        guard let panel, let screen = NSScreen.screens.first else { return }
        let area = screen.visibleFrame
        let frame = CGRect(x: area.midX - size.width / 2, y: area.minY + 6, width: size.width, height: size.height)
        panel.setFrame(frame, display: true)
        panel.contentView?.frame = CGRect(origin: .zero, size: size)
    }
}
