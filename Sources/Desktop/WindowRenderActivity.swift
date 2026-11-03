import AppKit
import Combine

/// Each desktop window follows its own occlusion, rather than app activation
/// (a wallpaper normally belongs to an inactive app).
@MainActor
final class WindowRenderActivity: ObservableObject {
    @Published private(set) var isVisible = true
    @Published private(set) var screensAsleep = false
    private weak var window: NSWindow?
    private var windowObserver: NSObjectProtocol?
    private var workspaceObservers: [NSObjectProtocol] = []

    func start(window: NSWindow) {
        stop()
        self.window = window
        windowObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.didChangeOcclusionStateNotification, object: window, queue: .main
        ) { [weak self] _ in
            Task { @MainActor [weak self] in self?.refresh() }
        }
        let workspace = NSWorkspace.shared.notificationCenter
        workspaceObservers.append(workspace.addObserver(forName: NSWorkspace.screensDidSleepNotification,
            object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor [weak self] in self?.screensAsleep = true }
            })
        workspaceObservers.append(workspace.addObserver(forName: NSWorkspace.screensDidWakeNotification,
            object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor [weak self] in
                    self?.screensAsleep = false
                    self?.refresh()
                }
            })
        refresh()
    }
    func refresh() {
        let visible = window?.occlusionState.contains(.visible) ?? false
        if visible != isVisible { isVisible = visible }
    }
    func shouldRender(pauseWhenCovered: Bool) -> Bool {
        !screensAsleep && (!pauseWhenCovered || isVisible)
    }
    func stop() {
        if let windowObserver { NotificationCenter.default.removeObserver(windowObserver) }
        windowObserver = nil
        let workspace = NSWorkspace.shared.notificationCenter
        workspaceObservers.forEach { workspace.removeObserver($0) }
        workspaceObservers.removeAll()
        window = nil
    }
}
