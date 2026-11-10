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
    nonisolated private static let interval: TimeInterval = 0.09
    /// The poll while the pointer has rested a while and nothing is shown:
    /// the first movement is noticed a little later, then the poll is quick
    /// again. Lunalyr runs all day; most of it with the pointer still.
    nonisolated static let restingInterval: TimeInterval = 0.25
    nonisolated static let restAfter: TimeInterval = 1.5
    /// The poll while the player is up or an intent is building: quick
    /// enough to judge the pointer's speed, and for a click on a control to
    /// reach it rather than the desktop underneath.
    nonisolated static let revealingInterval: TimeInterval = 0.045
    /// Whether the pointer means to bring the player up (`HoverIntent`).
    private var intent = HoverIntent()
    /// When the pointer last moved, or the controls were last up.
    private var lastMotion: TimeInterval = 0
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
        lastMotion = ProcessInfo.processInfo.systemUptime
        schedule(Self.interval)
    }

    /// How often to poll: quickly while the pointer moves or the controls
    /// are up, and for `restAfter` seconds after.
    nonisolated static func pollInterval(stillFor: TimeInterval, active: Bool) -> TimeInterval {
        !active && stillFor >= restAfter ? restingInterval : interval
    }

    /// As above, and quicker still while the player is up or a reveal is pending.
    nonisolated static func pollInterval(stillFor: TimeInterval, shown: Bool, pending: Bool) -> TimeInterval {
        shown || pending ? revealingInterval : pollInterval(stillFor: stillFor, active: false)
    }

    /// The display the hover logic settles on this poll, after the reveal
    /// intent and the dismiss guards: a new reveal waits for intent; a shown
    /// player stays while something holds it (`PlayerDismissGuard`).
    nonisolated static func decide(proposed: CGDirectDisplayID?, previous: CGDirectDisplayID?,
                                   intentReady: Bool, holds: Bool) -> CGDirectDisplayID? {
        if let proposed, proposed != previous, !intentReady {
            return holds ? previous : nil
        }
        if proposed == nil, holds { return previous }
        return proposed
    }

    private func schedule(_ interval: TimeInterval) {
        timer?.invalidate()
        let tick = Timer(timeInterval: interval, repeats: true) { _ in
            Task { @MainActor in HoverState.shared.poll() }
        }
        tick.tolerance = interval / 3
        timer = tick
        RunLoop.main.add(tick, forMode: .common)
    }

    func reset() {
        session = VCHoverSession()
        intent.reset()
        lastHit = nil
        if activeDisplayID != nil { activeDisplayID = nil }
    }

    private func poll() {
        let location = NSEvent.mouseLocation
        let buttons = NSEvent.pressedMouseButtons
        let moved = location != lastLocation || buttons != lastButtons
        lastLocation = location
        lastButtons = buttons
        let now = ProcessInfo.processInfo.systemUptime
        let shown = session.activeDisplayID != 0 || activeDisplayID != nil
        if moved || shown || intent.isPending { lastMotion = now }
        let wanted = Self.pollInterval(stillFor: now - lastMotion, shown: shown, pending: intent.isPending)
        if let timer, timer.timeInterval != wanted { schedule(wanted) }
        // A resting pointer cannot start a reveal; an active one still needs
        // its linger timed and its controls kept in reach, and a pending one
        // its dwell.
        guard moved || session.activeDisplayID != 0 || intent.isPending else { return }
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
        let inHotZone = vc_hover_hot_zone(screen.frame).rect.contains(location)
        let potentialHover = (!held && inHotZone) || session.activeDisplayID == screen.displayID
        let blocked = potentialHover && !isExposed(at: location, moved: moved)
        let previous = session.activeDisplayID
        let stepped = displays.withUnsafeBufferPointer {
            vc_hover_step(&session, $0.baseAddress, Int32($0.count), point,
                          blocked ? 1 : 0, held ? 1 : 0, now, lingerDuration)
        }
        let proposed: CGDirectDisplayID? = stepped == 0 ? nil : stepped
        let previousDisplay: CGDirectDisplayID? = previous == 0 ? nil : previous
        // Intent is judged only while a reveal could start here.
        let intentReady = intent.sample(location, at: now,
                                        inHotZone: !held && !blocked && inHotZone && previous != screen.displayID)
        let interaction = PlayerInteraction.shared
        // A drag is only in flight while a button is down: a press whose
        // view went away (a display change) can never pin the player up.
        let dragging = interaction.dragging && held
        let holds = previousDisplay != nil && PlayerDismissGuard.holds(
            dragging: dragging, searchFocused: interaction.searchFocused, lastClick: interaction.lastClick,
            now: now, overWindow: blocked)
        var active = Self.decide(proposed: proposed, previous: previousDisplay, intentReady: intentReady, holds: holds)
        // Keep the C session in step: a reveal refused for intent is not a
        // reveal (its keep-zone must not apply), and a held one keeps its linger.
        if let active {
            if session.activeDisplayID != active { session.activeDisplayID = active }
            // After a drag the usual linger runs from the release.
            if holds && dragging { session.deadline = now + lingerDuration }
            intent.reset()
        } else if session.activeDisplayID != 0 {
            session = VCHoverSession()
        }
        // A search in progress keeps the player up while the pointer is elsewhere.
        // Forget a display that has been unplugged since.
        if let pinned = pinnedDisplay, !displays.contains(where: { $0.displayID == pinned }) { pinnedDisplay = nil }
        if active == nil, DesktopPlayerModel.shared.isPinned, let pinned = pinnedDisplay ?? activeDisplayID,
           displays.contains(where: { $0.displayID == pinned }) { active = pinned }
        if let active, pinnedDisplay != active { pinnedDisplay = active }
        if active != activeDisplayID {
            withAnimation(PlayerMotion.reveal(shown: active != nil, reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)) {
                activeDisplayID = active
            }
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
