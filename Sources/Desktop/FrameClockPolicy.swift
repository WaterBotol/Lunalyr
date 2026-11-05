import Foundation

/// When a wallpaper's frame clock can stop.
///
/// The clock rebuilds the whole wallpaper every frame. With "Your
/// wallpaper", a still image, or an animated cover (which plays itself), and
/// no lyric advancing -- nothing playing, paused, plain or missing lyrics --
/// every one of those rebuilds produced the same picture. The clock now stops
/// once the last fade or notice has finished, and starts again the moment any
/// input changes, since every such change rebuilds the view anyway.
enum FrameClockPolicy {
    struct Inputs: Equatable {
        /// A visible layer redraws from the clock (colour field, effects,
        /// visualisers) rather than drawing itself or holding still.
        var backgroundAnimates: Bool
        /// Timed lyrics are being sung.
        var lyricsAdvance: Bool
        /// When each fade or notice still on screen finishes.
        var transitionsEnd: [Date]
    }

    /// nil while the clock must keep running; otherwise the moment after
    /// which it can stop, possibly already past.
    static func quietAfter(_ inputs: Inputs) -> Date? {
        guard !inputs.backgroundAnimates, !inputs.lyricsAdvance else { return nil }
        return inputs.transitionsEnd.max() ?? .distantPast
    }

    static func isIdle(quietAfter: Date?, now: Date) -> Bool {
        guard let quietAfter else { return false }
        return now >= quietAfter
    }
}

/// When to restart a GPU effect on a fresh clock. A restart is a new layer,
/// crossfaded in, so it is never seen as a jump; it is only worth doing once
/// the effect's clock has grown, and only at a natural break.
enum ShaderEpochPolicy {
    enum Trigger { case trackChange, wake, elapsed }

    /// Even with no break at all, never let one effect's clock run past this.
    static let maximumAge: TimeInterval = 8 * 3600

    static func shouldRenew(age: TimeInterval, trigger: Trigger) -> Bool {
        guard age.isFinite, age >= 0 else { return true }
        switch trigger {
        case .trackChange: return age >= 30 * 60
        case .wake: return age >= 10 * 60
        case .elapsed: return age >= maximumAge - 1
        }
    }
}

extension VisualClock {
    /// Ambient wallpaper motion. Separate from `flow`, which drives the
    /// visualisers, because Reduce Motion slows only this one: the
    /// visualisers apply their own reduction to the phase they are given.
    static let ambientFlow = VisualClock()
}
