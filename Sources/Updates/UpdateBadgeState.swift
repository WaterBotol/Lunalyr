import Foundation

/// Whether Lunalyr is telling the person about a waiting update, fed by
/// Sparkle's standard user driver callbacks (see `AppUpdates`). A value with
/// no Sparkle in it, so every path can be tested without a feed or network.
///
/// A daily background check that finds an update doesn't open a window: it
/// badges the menu-bar icon, and the panel says "Lunalyr x.y is available"
/// with Update… and Later. Settings › General says it too, until the update
/// is dealt with. A check the person starts themselves shows Sparkle's own
/// window, as before, and never badges.
struct UpdateBadgeState: Equatable {
    enum Event: Equatable {
        /// Sparkle found an update. `userInitiated` for Check for Updates…;
        /// `sparkleShowsIt` when Sparkle shows its own window for it.
        case found(version: String, userInitiated: Bool, sparkleShowsIt: Bool)
        /// Sparkle's update window came into focus (the person chose
        /// Update…, Check for Updates…, or Sparkle brought it forward).
        case userAttention
        /// Later, in the menu-bar panel: the badge goes, the update still
        /// waits in Settings, and the next daily check badges it again.
        case dismissed
        /// The update session ended: installed, skipped, or closed.
        case sessionFinished
    }

    /// False in builds without an update feed: they never badge.
    let configured: Bool
    /// The waiting update's version, from a scheduled check, until it is
    /// dealt with. Survives Later.
    private(set) var pendingVersion: String?
    /// Whether the menu-bar icon carries the badge and the panel the row.
    private(set) var showsBadge = false

    init(configured: Bool) {
        self.configured = configured
    }

    mutating func handle(_ event: Event) {
        switch event {
        case let .found(version, userInitiated, sparkleShowsIt):
            guard configured, !userInitiated, !sparkleShowsIt else {
                // Sparkle's own window is telling them now.
                clear()
                return
            }
            pendingVersion = version
            showsBadge = true
        case .dismissed:
            showsBadge = false
        case .userAttention, .sessionFinished:
            clear()
        }
    }

    private mutating func clear() {
        pendingVersion = nil
        showsBadge = false
    }

    /// "Lunalyr 2.1.0 is available", or without a version when the feed
    /// gave none.
    static func headline(for version: String?) -> String {
        let trimmed = version?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return trimmed.isEmpty ? "A new version of Lunalyr is available" : "Lunalyr \(trimmed) is available"
    }
}
