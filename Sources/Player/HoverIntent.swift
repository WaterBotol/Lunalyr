import CoreGraphics
import Foundation

/// Whether a pointer in the middle of the desktop means to be there.
///
/// The desktop is crossed all day on the way to windows and to the other
/// display, so touching the hot zone is not enough. The player comes up
/// once the pointer slows down (hoverIntent's rule: two polls in a row
/// under `slowSpeed`), or after `dwell` in the zone at any speed short of a
/// fly-through. A pointer moving faster than `flyThrough` never reveals and
/// starts the dwell again.
struct HoverIntent: Equatable {
    static let slowSpeed: Double = 120
    static let slowPolls = 2
    static let dwell: Double = 0.22
    static let flyThrough: Double = 900

    private(set) var enteredAt: Double?
    private(set) var slowCount = 0
    private(set) var speed: Double = 0
    private var lastPoint: CGPoint?
    private var lastTime: Double?

    /// Whether an intent is building (the pointer is in the zone, not yet revealed).
    var isPending: Bool { enteredAt != nil }

    mutating func reset() {
        enteredAt = nil
        slowCount = 0
    }

    /// One poll. `inHotZone` is false whenever a reveal could not start here
    /// (outside the zone, over a window, a button held). True when the
    /// player should come up now.
    mutating func sample(_ point: CGPoint, at time: Double, inHotZone: Bool) -> Bool {
        defer { lastPoint = point; lastTime = time }
        if let lastPoint, let lastTime, time - lastTime > 0.005 {
            let distance = hypot(point.x - lastPoint.x, point.y - lastPoint.y)
            speed = Double(distance) / (time - lastTime)
        } else if lastPoint == nil {
            speed = 0
        }
        guard inHotZone, point.x.isFinite, point.y.isFinite, time.isFinite else {
            reset()
            return false
        }
        if speed > Self.flyThrough {
            reset()
            return false
        }
        if enteredAt == nil { enteredAt = time }
        slowCount = speed < Self.slowSpeed ? slowCount + 1 : 0
        return slowCount >= Self.slowPolls || time - (enteredAt ?? time) >= Self.dwell
    }
}

/// What keeps a shown player up after the pointer has left it.
enum PlayerDismissGuard {
    /// After a click on the player, long enough to see what it did.
    static let afterClick: Double = 1.2

    /// Whether the player must stay up although the hover logic would let
    /// it go. A drag in flight holds it even over a window; anything else
    /// gives way to a window (the user has clearly moved on).
    static func holds(dragging: Bool, searchFocused: Bool, lastClick: Double?, now: Double, overWindow: Bool) -> Bool {
        if dragging { return true }
        if overWindow { return false }
        if searchFocused { return true }
        if let lastClick, now - lastClick >= 0, now - lastClick < afterClick { return true }
        return false
    }
}

/// What the player's views tell the hover poll: kept apart from SwiftUI
/// state so the poll reads it without redrawing anything.
@MainActor
final class PlayerInteraction {
    static let shared = PlayerInteraction()
    /// A scrub is in flight (mouse button down on the seek bar).
    var dragging = false
    /// The search field has the keyboard.
    var searchFocused = false
    /// Uptime of the last click on the player.
    var lastClick: Double?

    func noteClick() { lastClick = ProcessInfo.processInfo.systemUptime }
}
