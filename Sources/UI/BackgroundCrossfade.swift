import Foundation

/// A surface's background crossfade, timed from timestamps: TimelineView
/// rebuilds the wallpaper every frame and discards SwiftUI's transition
/// state, so a `.transition` would simply cut.
///
/// The layers form a short stack, bottom first. Each one fades in over the
/// ones beneath it, which stay fully drawn until something covers them, so
/// a crossfade between two opaque layers never dims the screen at its
/// midpoint (drawing the outgoing layer at `1 - f` over black composited to
/// about 75% brightness halfway). A change that arrives mid-fade stacks on
/// top: the layer that was fading in carries on from where it was instead of
/// jumping to full, and the one beneath it does not vanish. A layer that
/// reaches full opacity hides everything beneath it, and those are let go.
///
/// A transparent layer (Your wallpaper, which shows the desktop) cannot
/// cover what is beneath it, so the layers under it fade out as it fades in.
///
/// A GPU effect has nothing to draw until it has compiled, so the fade into
/// it holds at its start meanwhile -- but only until the layer is first
/// ready. A display can move to another GPU later (a dual-GPU MacBook Pro
/// switching, an external display), where the effect compiles again; the
/// surface keeps drawing on the GPU it has until then. Holding the fade a
/// second time hid an effect that was on screen and brought the outgoing
/// layer back in its place -- black, after leaving Your wallpaper.
struct BackgroundCrossfade<Layer: Hashable>: Equatable {
    struct Entry: Equatable {
        let layer: Layer
        /// When its fade in started (or starts, once it is ready).
        var startedAt: Date
        /// It has had something to draw since it was added.
        var isReady: Bool
        /// False for a layer shown at once, without a fade.
        var fadesIn: Bool
    }

    /// A layer as drawn at a moment.
    struct Drawn: Equatable {
        let layer: Layer
        let opacity: Double
    }

    /// The most layers drawn at once. A fourth change in quick succession
    /// lets go of the one contributing least to the screen.
    static var maximumLayers: Int { 3 }
    /// A layer replaced before it had faded in this far was never really
    /// seen, so the next fade carries on from what is beneath it.
    static var unseenFade: Double { 0.02 }

    /// Bottom first; the last is `current`.
    private(set) var entries: [Entry]

    /// A surface's first layer, shown without a fade.
    init(_ layer: Layer, ready: Bool = true) {
        entries = [Entry(layer: layer, startedAt: .distantPast, isReady: ready, fadesIn: false)]
    }

    /// A surface's first layer, fading in from the window beneath (black)
    /// from `date`, or from when it is first ready.
    init(_ layer: Layer, ready: Bool, fadingInAt date: Date) {
        entries = [Entry(layer: layer, startedAt: date, isReady: ready, fadesIn: true)]
    }

    /// The layer fading in, or on screen.
    var current: Layer { entries[entries.count - 1].layer }
    /// The layer directly beneath `current`, while one is kept.
    var previous: Layer? { entries.count > 1 ? entries[entries.count - 2].layer : nil }
    /// When the fade into `current` started.
    var changedAt: Date { entries[entries.count - 1].startedAt }
    /// `current` has had something to draw since it became current.
    var isReady: Bool { entries[entries.count - 1].isReady }
    /// The fade into `current` holds at its start: it has had nothing to
    /// draw yet.
    var isHeld: Bool { !isReady }

    /// Moves to `next`; `ready` says whether it has something to draw now.
    /// A layer that never had anything to draw, or was replaced before it
    /// showed, was never seen, so the fade carries on from what is beneath.
    mutating func change(to next: Layer, ready: Bool, at date: Date, duration: TimeInterval) {
        prune(at: date, duration: duration)
        let top = entries[entries.count - 1]
        let unseen = top.fadesIn && Self.fade(of: top, at: date, duration: duration) < Self.unseenFade
        // A first layer shown at once stays as the base even before it is
        // ready: it is what the surface has.
        if unseen || (!top.isReady && entries.count > 1) { entries.removeLast() }
        while entries.count >= Self.maximumLayers {
            let weights = visibleWeights(at: date, duration: duration)
            guard let least = weights.indices.min(by: { weights[$0] < weights[$1] }) else { break }
            entries.remove(at: least)
        }
        entries.append(Entry(layer: next, startedAt: date, isReady: ready, fadesIn: true))
    }

    /// `current` has something to draw: the first time, its fade starts now.
    /// Afterwards -- the effect compiling again on another GPU while the
    /// surface keeps drawing it -- nothing changes.
    mutating func becameReady(at date: Date) {
        guard !entries[entries.count - 1].isReady else { return }
        entries[entries.count - 1].isReady = true
        entries[entries.count - 1].startedAt = date
    }

    /// How far `current` has faded in, from 0 to 1.
    func fade(at date: Date, duration: TimeInterval) -> Double {
        Self.fade(of: entries[entries.count - 1], at: date, duration: duration)
    }

    /// When the last fade still running ends; nil when nothing fades.
    func fadesEnd(duration: TimeInterval) -> Date? {
        entries.filter { $0.fadesIn && $0.isReady }.map { $0.startedAt.addingTimeInterval(duration) }.max()
    }

    /// The layers drawn at `date`, bottom first, with the opacity each is
    /// drawn at. `current` is always included (an effect still compiling
    /// must exist to compile); a layer something covers is not.
    func layers(at date: Date, duration: TimeInterval,
                isOpaque: (Layer) -> Bool = { _ in true }) -> [Drawn] {
        var drawn: [Drawn] = []
        // How much of what lies beneath shows through the transparent
        // layers above it.
        var through = 1.0
        for (index, entry) in entries.enumerated().reversed() {
            let own = Self.fade(of: entry, at: date, duration: duration)
            let opacity = own * through
            if opacity > 0 || index == entries.count - 1 { drawn.append(Drawn(layer: entry.layer, opacity: opacity)) }
            if isOpaque(entry.layer) {
                if own >= 1 { break }
            } else {
                through *= 1 - own
                if through <= 0 { break }
            }
        }
        return drawn.reversed()
    }

    /// How much of each entry reaches the screen at `date`, treating every
    /// layer as opaque: each is covered by the fades of those above it.
    private func visibleWeights(at date: Date, duration: TimeInterval) -> [Double] {
        var weights: [Double] = []
        var covered = 1.0
        for entry in entries.reversed() {
            let own = Self.fade(of: entry, at: date, duration: duration)
            weights.append(own * covered)
            covered *= 1 - own
        }
        return weights.reversed()
    }

    /// Lets go of the layers a fully shown layer covers.
    private mutating func prune(at date: Date, duration: TimeInterval) {
        guard let covering = entries.lastIndex(where: { Self.fade(of: $0, at: date, duration: duration) >= 1 }),
              covering > 0 else { return }
        entries.removeFirst(covering)
    }

    private static func fade(of entry: Entry, at date: Date, duration: TimeInterval) -> Double {
        guard entry.fadesIn else { return 1 }
        guard entry.isReady else { return 0 }
        return Easing.smoothstep(date.timeIntervalSince(entry.startedAt) / duration)
    }
}

/// A song's background is chosen from its mood and seed, but the mood leans
/// on things that arrive after the song does: the cover's colours, and the
/// lyrics' pace. Choosing at once meant one skip crossfaded the background
/// two or three times (track, then palette, then lyrics), and a lyric
/// upgrade mid-song could switch it again.
///
/// This holds the last song's choice across a song change until the new
/// song's inputs have settled (or `settleLimit` passes), then takes the new
/// choice once, and keeps it for the rest of that recording. While nothing
/// plays, the last song's choice stays, so a pause or a stop does not redraw
/// the wallpaper as a different one.
struct BackgroundChoiceHold<Track: Equatable, Choice: Equatable>: Equatable {
    /// The longest a song change waits for its cover and lyrics.
    static var settleLimit: TimeInterval { 2.5 }

    /// What the background is chosen from now.
    private(set) var choice: Choice
    /// The recording `choice` was taken for.
    private(set) var track: Track?
    /// The song waiting for its inputs, and since when.
    private(set) var waiting: Track?
    private(set) var waitingSince: Date?

    init(choice: Choice) {
        self.choice = choice
    }

    /// When the song waiting is taken whatever its inputs.
    var deadline: Date? { waitingSince?.addingTimeInterval(Self.settleLimit) }

    /// Follows the playing `track`, whose choice would be `next` now;
    /// `settled` says its cover and lyrics are in. True when the choice
    /// was taken for a new recording (the moment for its single crossfade).
    @discardableResult
    mutating func update(track: Track?, choice next: Choice, settled: Bool, now: Date) -> Bool {
        guard let track, track != self.track else {
            waiting = nil
            waitingSince = nil
            return false
        }
        if waiting != track || waitingSince == nil {
            waiting = track
            waitingSince = now
        }
        guard settled || now >= deadline ?? now else { return false }
        choice = next
        self.track = track
        waiting = nil
        waitingSince = nil
        return true
    }
}
