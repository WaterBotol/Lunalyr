import AppKit
import Combine
import SwiftUI

/// One pointer, one active display. Hover is allowed only on exposed desktop;
/// it cannot dim a second screen or activate through a browser/app window.
///
/// The poll is cheap while the pointer rests: nothing can start a reveal
/// without movement, and the window under a resting pointer is classified
/// once. It stops altogether while displays sleep, the screen is locked,
/// every wallpaper is covered, or hover controls are switched off.
@MainActor
final class HoverState: ObservableObject {
    static let shared = HoverState()
    @Published private(set) var activeDisplayID: CGDirectDisplayID?
    private var timer: Timer?
    private var session = VCHoverSession()
    private let lingerDuration: TimeInterval = 0.7
    private static let interval: TimeInterval = 0.09
    private var started = false
    private var cancellables: Set<AnyCancellable> = []
    private var lastLocation: CGPoint?
    private var lastButtons = 0
    /// The last window found under the pointer and whether it is desktop.
    private var lastHit: (window: Int, exposed: Bool, at: TimeInterval)?

    func isActive(on displayID: CGDirectDisplayID) -> Bool { activeDisplayID == displayID }

    func start() {
        guard !started else { return }
        started = true
        let monitor = DesktopCoverageMonitor.shared
        DesktopOptions.shared.$showsControlsOnHover.removeDuplicates()
            .combineLatest(monitor.$systemHidden.removeDuplicates(), monitor.$hiddenDisplays.removeDuplicates())
            .sink { [weak self] enabled, systemHidden, hidden in
                // Published before the values change; decide once they have.
                Task { @MainActor [weak self] in
                    self?.updateSchedule(enabled: enabled, systemHidden: systemHidden, hidden: hidden)
                }
            }
            .store(in: &cancellables)
    }

    /// Whether the poll has any reason to run.
    nonisolated static func shouldPoll(enabled: Bool, systemHidden: Bool, hidden: Set<CGDirectDisplayID>,
                                       displays: Set<CGDirectDisplayID>) -> Bool {
        guard enabled, !systemHidden else { return false }
        return displays.isEmpty || !displays.isSubset(of: hidden)
    }

    private func updateSchedule(enabled: Bool, systemHidden: Bool, hidden: Set<CGDirectDisplayID>) {
        let displays = Set(NSScreen.screens.compactMap(\.versoDisplayID))
        guard Self.shouldPoll(enabled: enabled, systemHidden: systemHidden, hidden: hidden, displays: displays) else {
            timer?.invalidate()
            timer = nil
            reset()
            ControlPanelController.shared.updatePointer(buttonsHeld: false)
            return
        }
        guard timer == nil else { return }
        lastLocation = nil
        let tick = Timer(timeInterval: Self.interval, repeats: true) { _ in
            Task { @MainActor in HoverState.shared.poll() }
        }
        tick.tolerance = Self.interval / 3
        timer = tick
        RunLoop.main.add(tick, forMode: .common)
    }

    func reset() {
        session = VCHoverSession()
        lastHit = nil
        if activeDisplayID != nil { activeDisplayID = nil }
    }

    private func poll() {
        let location = NSEvent.mouseLocation
        let buttons = NSEvent.pressedMouseButtons
        let moved = location != lastLocation || buttons != lastButtons
        lastLocation = location
        lastButtons = buttons
        // A resting pointer cannot start a reveal; an active one still needs
        // its linger timed and its controls kept in reach.
        guard moved || session.activeDisplayID != 0 else { return }
        let screens = NSScreen.screens
        let displays = screens.compactMap { screen -> VCDisplayFrame? in
            guard let id = screen.versoDisplayID else { return nil }
            return VCDisplayFrame(displayID: id, frame: screen.versoFrame)
        }
        let point = VCPoint(x: Double(location.x), y: Double(location.y))
        let index = displays.withUnsafeBufferPointer { vc_display_at_point($0.baseAddress, Int32($0.count), point) }
        guard index >= 0 else { reset(); return }
        let screen = displays[Int(index)]
        let held = buttons != 0
        let potentialHover = (!held && vc_hover_hot_zone(screen.frame).rect.contains(location))
            || session.activeDisplayID == screen.displayID
        let blocked = potentialHover && !isExposed(at: location, moved: moved)
        let next = displays.withUnsafeBufferPointer {
            vc_hover_step(&session, $0.baseAddress, Int32($0.count), point,
                          blocked ? 1 : 0, held ? 1 : 0, ProcessInfo.processInfo.systemUptime, lingerDuration)
        }
        var active: CGDirectDisplayID? = next == 0 ? nil : next
        // A search in progress keeps the player up while the pointer is elsewhere.
        // Forget a display that has been unplugged since.
        if let pinned = pinnedDisplay, !displays.contains(where: { $0.displayID == pinned }) { pinnedDisplay = nil }
        if active == nil, DesktopPlayerModel.shared.isPinned, let pinned = pinnedDisplay ?? activeDisplayID,
           displays.contains(where: { $0.displayID == pinned }) { active = pinned }
        if let active, pinnedDisplay != active { pinnedDisplay = active }
        if active != activeDisplayID {
            withAnimation(.easeOut(duration: 0.3)) { activeDisplayID = active }
        }
        ControlPanelController.shared.updatePointer(buttonsHeld: held)
    }

    /// The display the player was last shown on: a search in progress stays
    /// there, and only there.
    @Published private(set) var pinnedDisplay: CGDirectDisplayID?

    /// Whether the player shows on `displayID`: where the pointer hovers, and,
    /// while a search is typed, on the one display it was typed on -- not on
    /// every screen at once.
    nonisolated static func showsPlayer(on displayID: CGDirectDisplayID, active: CGDirectDisplayID?,
                                        pinned: CGDirectDisplayID?, searching: Bool) -> Bool {
        if active == displayID { return true }
        return searching && active == nil && pinned == displayID
    }

    func showsPlayer(on displayID: CGDirectDisplayID, searching: Bool) -> Bool {
        Self.showsPlayer(on: displayID, active: activeDisplayID, pinned: pinnedDisplay, searching: searching)
    }

    /// The same window keeps the same layer, so a pointer resting over (or
    /// moving within) one window needs one window-server query, not two.
    private func isExposed(at location: CGPoint, moved: Bool) -> Bool {
        let now = ProcessInfo.processInfo.systemUptime
        if !moved, let lastHit, now - lastHit.at < 1 { return lastHit.exposed }
        let ignored = DesktopWallpaperController.shared.windowIDs.union(ControlPanelController.shared.windowIDs)
        let number = NSWindow.windowNumber(at: location, belowWindowWithWindowNumber: 0)
        if let lastHit, lastHit.window == number, now - lastHit.at < 2 { return lastHit.exposed }
        let exposed = DesktopVisibility.isExposed(windowNumber: number, ignoring: ignored)
        lastHit = (number, exposed, now)
        return exposed
    }
}
