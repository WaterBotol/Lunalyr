import Foundation

/// One press on the seek bar, from mouse-down to release, as plain values:
/// where the knob is, how fast it follows the hand, whether it is held by a
/// lyric line, and what the release would seek to. Nothing here talks to a
/// player; `ScrubController` sends the one seek a release commits.
///
/// Coordinates are bar-local points: x from the bar's leading edge, y from
/// its centre line (either sign).
struct ScrubSession: Equatable {
    enum Event: Equatable {
        /// The knob was pulled onto a line start (one alignment haptic).
        case snapped
        /// The knob reached the start or the end (one alignment haptic).
        case reachedEnd
    }

    /// A press this close to the knob keeps its grab offset: the knob does not jump.
    static let grabRadius: Double = 8
    /// Haptics for snapping no closer together than this.
    static let snapHapticInterval: Double = 0.09
    /// Less travel than this, released this soon, is a click.
    static let clickTravel: Double = 3
    static let clickTime: Double = 0.3

    let width: Double
    let duration: Double
    /// The song the press began on: a change of song cancels the press.
    let trackKey: String
    let targets: [Double]
    let startedAt: Double
    /// Pointer x minus knob x, kept for the whole press.
    let grabOffset: Double
    /// Whether the press landed on the knob.
    let grabbedKnob: Bool

    /// The knob before clamping and snapping; past an end only at full speed.
    private(set) var knob: Double
    private(set) var gain: Double = 1
    private(set) var snapped: Int?
    /// Smoothed horizontal pointer speed, pt/s.
    private(set) var speed: Double = 0
    private(set) var travel: Double = 0
    /// Set when the knob caught up with the pointer on returning to full
    /// speed: the view animates that one move.
    private(set) var caughtUp = false
    private var atEnd: Bool
    private var lastSnapHaptic = -Double.infinity
    private var lastX: Double
    private var lastY: Double
    private var lastTime: Double
    private var lastCommand = false

    /// A press at `x` with the knob at `knobX` (the play position).
    init(x: Double, y: Double, knobX: Double, width: Double = ScrubMath.barWidth, duration: Double,
         trackKey: String, targets: [Double], now: Double, command: Bool = false) {
        self.width = max(1, width)
        self.duration = duration
        self.trackKey = trackKey
        self.targets = targets
        startedAt = now
        let onKnob = abs(x - knobX) <= Self.grabRadius
        grabbedKnob = onKnob
        grabOffset = onKnob ? x - knobX : 0
        knob = onKnob ? knobX : x
        atEnd = false
        lastX = x
        lastY = y
        lastTime = now
        lastCommand = command
        gain = ScrubMath.gain(verticalDistance: y)
        atEnd = clamped <= 0 || clamped >= self.width
        // A press on the bar snaps as a slow hover would; one on the knob
        // stays exactly where the song is.
        if !onKnob { updateSnap(now: now, command: command, announce: false) }
    }

    // MARK: Reading

    var pointsPerSecond: Double { duration > 0 ? width / duration : 0 }
    var clamped: Double { min(max(knob, 0), width) }
    var snappedTime: Double? { snapped.flatMap { targets.indices.contains($0) ? targets[$0] : nil } }
    /// Where the knob is drawn.
    var displayX: Double { snappedTime.map { ScrubMath.x(time: $0, width: width, duration: duration) } ?? clamped }
    /// The time the capsule shows and the wallpaper previews.
    var targetTime: Double { snappedTime ?? ScrubMath.time(x: clamped, width: width, duration: duration) }
    /// Visual stretch past an end (signed), rubber-banded.
    var overshoot: Double {
        if knob > width { return ScrubMath.rubberBand(overshoot: knob - width) }
        if knob < 0 { return ScrubMath.rubberBand(overshoot: knob) }
        return 0
    }
    var rateLabel: String? { ScrubMath.rateLabel(gain: gain) }

    func isClick(at now: Double) -> Bool { travel < Self.clickTravel && now - startedAt < Self.clickTime }

    /// What a release at `now` seeks to, or nil for nothing: a click on the
    /// knob itself is not a seek.
    func commit(at now: Double) -> Double? {
        if grabbedKnob, isClick(at: now) { return nil }
        guard duration > 0 else { return nil }
        return ScrubMath.commitTarget(time: targetTime, snappedTo: snappedTime, duration: duration)
    }

    // MARK: Moving

    /// The pointer moved to (`x`, `y`). Returns the haptic-worthy events.
    /// At full speed the knob follows the pointer 1:1 (keeping the grab
    /// offset) and may stretch past the ends; slower, it moves by the gained
    /// relative delta and stops at the ends, so changing speed never jumps.
    @discardableResult
    mutating func move(x: Double, y: Double, now: Double, option: Bool = false, command: Bool = false) -> [Event] {
        guard x.isFinite, y.isFinite else { return [] }
        let dx = x - lastX
        let dt = now - lastTime
        // Two events in one instant keep the old estimate: it cannot be judged.
        if dt > 0.001 { speed = speed * 0.5 + abs(dx) / dt * 0.5 }
        travel += abs(dx) + abs(y - lastY)
        let newGain = ScrubMath.gain(verticalDistance: y, fine: option)
        caughtUp = false
        if newGain >= 1 {
            let follow = x - grabOffset
            caughtUp = gain < 1 && abs(follow - knob) > 0.5
            knob = follow
        } else {
            knob = min(max(knob, 0), width) + dx * newGain
            knob = min(max(knob, 0), width)
        }
        gain = newGain
        lastX = x
        lastY = y
        lastTime = now
        var events: [Event] = []
        let end = knob <= 0 || knob >= width
        if end && !atEnd { events.append(.reachedEnd) }
        atEnd = end
        if updateSnap(now: now, command: command, announce: true) { events.append(.snapped) }
        return events
    }

    /// ⌥ or ⌘ went down or up without the pointer moving.
    @discardableResult
    mutating func modifiersChanged(option: Bool, command: Bool, now: Double) -> [Event] {
        let newGain = ScrubMath.gain(verticalDistance: lastY, fine: option)
        if newGain != gain || command != lastCommand {
            if newGain >= 1, gain < 1 {
                let follow = lastX - grabOffset
                caughtUp = abs(follow - knob) > 0.5
                knob = follow
            }
            gain = newGain
        }
        return updateSnap(now: now, command: command, announce: true) ? [.snapped] : []
    }

    /// Snaps or lets go; true when a new snap should be felt.
    @discardableResult
    private mutating func updateSnap(now: Double, command: Bool, announce: Bool) -> Bool {
        lastCommand = command
        let time = ScrubMath.time(x: clamped, width: width, duration: duration)
        let next = ScrubMath.snap(time: time, targets: targets, pointsPerSecond: pointsPerSecond,
                                  gain: gain, speed: speed, disabled: command)
        defer { snapped = next }
        guard announce, let next, next != snapped, now - lastSnapHaptic >= Self.snapHapticInterval else { return false }
        lastSnapHaptic = now
        return true
    }
}
