import Foundation

/// A surface's background crossfade, timed from a timestamp: TimelineView
/// rebuilds the wallpaper every frame and discards SwiftUI's transition
/// state, so a `.transition` would simply cut.
///
/// A GPU effect has nothing to draw until it has compiled, so the fade into
/// it holds at its start meanwhile -- but only until the layer is first
/// ready. A display can move to another GPU later (a dual-GPU MacBook Pro
/// switching, an external display), where the effect compiles again; the
/// surface keeps drawing on the GPU it has until then. Holding the fade a
/// second time hid an effect that was on screen and brought the outgoing
/// layer back in its place -- black, after leaving Your wallpaper.
struct BackgroundCrossfade<Layer: Hashable>: Equatable {
    /// The layer fading in, or on screen.
    private(set) var current: Layer
    /// The layer it replaced, drawn while the fade runs.
    private(set) var previous: Layer?
    /// When the fade into `current` started.
    private(set) var changedAt: Date
    /// `current` has had something to draw since it became current.
    private(set) var isReady: Bool

    /// A surface's first layer, shown without a fade.
    init(_ layer: Layer, ready: Bool = true) {
        current = layer
        previous = nil
        changedAt = .distantPast
        isReady = ready
    }

    /// The fade into `current` holds at its start: it has had nothing to
    /// draw yet.
    var isHeld: Bool { !isReady }

    /// Moves to `next`; `ready` says whether it has something to draw now.
    /// A layer that never had anything to draw was never seen, so the fade
    /// carries on from the one on screen.
    mutating func change(to next: Layer, ready: Bool, at date: Date) {
        if previous == nil || isReady { previous = current }
        current = next
        isReady = ready
        changedAt = date
    }

    /// `current` has something to draw: the first time, its fade starts now.
    /// Afterwards -- the effect compiling again on another GPU while the
    /// surface keeps drawing it -- nothing changes.
    mutating func becameReady(at date: Date) {
        guard !isReady else { return }
        isReady = true
        changedAt = date
    }

    /// How far `current` has faded in, from 0 to 1.
    func fade(at date: Date, duration: TimeInterval) -> Double {
        isReady ? Easing.smoothstep(date.timeIntervalSince(changedAt) / duration) : 0
    }

    /// The layer still fading out at `fade`, if any.
    func outgoing(fade: Double) -> Layer? {
        previous.flatMap { fade < 1 && $0 != current ? $0 : nil }
    }

    /// The layers drawn at `fade`, the outgoing one beneath.
    func layers(fade: Double) -> [Layer] {
        outgoing(fade: fade).map { [$0, current] } ?? [current]
    }

    /// The opacity `layer` is drawn at.
    func opacity(of layer: Layer, fade: Double) -> Double {
        layer == current ? (outgoing(fade: fade) == nil ? 1 : fade) : 1 - fade
    }
}
