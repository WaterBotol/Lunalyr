import AppKit
import SwiftUI

/// One pointer, one active display. Hover is allowed only on exposed desktop;
/// it cannot dim a second screen or activate through a browser/app window.
@MainActor
final class HoverState: ObservableObject {
    static let shared = HoverState()
    @Published private(set) var activeDisplayID: CGDirectDisplayID?
    private var timer: Timer?
    private var session = VCHoverSession()
    private let lingerDuration: TimeInterval = 0.7

    func isActive(on displayID: CGDirectDisplayID) -> Bool { activeDisplayID == displayID }

    func start() {
        guard timer == nil else { return }
        let tick = Timer(timeInterval: 0.09, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in self?.poll() }
        }
        timer = tick
        RunLoop.main.add(tick, forMode: .common)
    }

    func reset() {
        session = VCHoverSession()
        if activeDisplayID != nil { activeDisplayID = nil }
    }

    private func poll() {
        let screens = NSScreen.screens
        let displays = screens.compactMap { screen -> VCDisplayFrame? in
            guard let id = screen.versoDisplayID else { return nil }
            return VCDisplayFrame(displayID: id, frame: screen.versoFrame)
        }
        let location = NSEvent.mouseLocation
        let point = VCPoint(x: Double(location.x), y: Double(location.y))
        let index = displays.withUnsafeBufferPointer { vc_display_at_point($0.baseAddress, Int32($0.count), point) }
        guard index >= 0 else { reset(); return }
        let screen = displays[Int(index)]
        let potentialHover = vc_hover_hot_zone(screen.frame).rect.contains(location) || session.activeDisplayID == screen.displayID
        let ignored = DesktopWallpaperController.shared.windowIDs.union(ControlPanelController.shared.windowIDs)
        let blocked = potentialHover && !DesktopVisibility.isExposed(at: location, ignoring: ignored)
        let next = displays.withUnsafeBufferPointer {
            vc_hover_update(&session, $0.baseAddress, Int32($0.count), point,
                            blocked ? 1 : 0, ProcessInfo.processInfo.systemUptime, lingerDuration)
        }
        let active: CGDirectDisplayID? = next == 0 ? nil : next
        guard active != activeDisplayID else { return }
        withAnimation(.easeOut(duration: 0.3)) { activeDisplayID = active }
    }
}
