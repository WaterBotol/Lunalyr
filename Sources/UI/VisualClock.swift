import Foundation

/// Accumulates animation phase from a rate that changes over time.
///
/// The obvious approach -- multiplying absolute time by a rate (`t * rate`)
/// -- looks fine until the rate moves. `timeIntervalSinceReferenceDate` is
/// around 7.9e8, so nudging the multiplier by a few percent shifts the result
/// by millions of seconds and the pattern jumps to an unrelated point in its
/// cycle. That is why speeding the visuals up on a drop read as the animation
/// resetting rather than accelerating.
///
/// Integrating instead (`phase += dt * rate`) makes rate a true velocity:
/// changing it alters speed while position stays continuous.
@MainActor
final class VisualClock {
    static let pattern = VisualClock()
    static let gradient = VisualClock()
    /// Advances faster on beats, driving how hard the shader effects flow.
    /// Integrating is what makes this safe: the surge changes speed, where
    /// scaling the clock directly would jump the fluid to a new state.
    static let flow = VisualClock()
    /// NowPlayingView advances `flow` at this rate times Effect speed, and
    /// the visualiser scenes take it as their source clock. The render
    /// harness (Tests/Swift/Support) reads it too.
    static let visualizerRate = 0.70

    private var phase: Double = 0
    private var lastTime: Double?
    /// The rate actually in use, which chases the requested one rather than
    /// snapping to it.
    private var smoothedRate: Double?

    /// Seconds for the rate to close most of the way to a new target.
    ///
    /// Integrating already guaranteed the phase stays *continuous* when the
    /// rate moves -- but continuous is not the same as gradual. A beat can
    /// take `pulse` from 0 to 1 between two frames, and the motion visibly
    /// lurched even though it never jumped. Easing the velocity as well as
    /// the position is what makes a surge read as the effect gathering pace.
    private static let rateTimeConstant = 0.65

    /// Rate may be negative.
    ///
    /// Phase running backwards simply reverses the motion, which is the point:
    /// a clock that can only ever go forward turns every musical response into
    /// a fast-forward. Clamping to zero here is what made beats read as the
    /// effect skipping ahead.
    func advance(to time: Double, rate: Double) -> Double {
        let target = rate
        guard let last = lastTime else {
            lastTime = time
            smoothedRate = target
            return phase
        }
        // SwiftUI can evaluate a body more than once for the same frame;
        // only advance on a genuinely newer timestamp so repeats are no-ops.
        guard time > last else { return phase }
        lastTime = time
        // Clamp long gaps (display sleep) so the pattern does not lurch.
        let dt = min(time - last, 0.25)

        // Frame-rate independent exponential approach: the same wall-clock
        // easing whether this runs at 60Hz or 120Hz.
        let current = smoothedRate ?? target
        let blend = 1 - exp(-dt / Self.rateTimeConstant)
        let eased = current + (target - current) * blend
        smoothedRate = eased

        phase += dt * eased
        return phase
    }
}

/// Playback position between player samples.
///
/// The player is asked where it is every couple of seconds and the position
/// is extrapolated in between. Each fresh sample disagrees with the
/// extrapolation by a few tens of milliseconds -- round-trip jitter, not a
/// seek -- and jumping to it made the lyric highlight twitch, sometimes
/// backwards, on every re-sync. A small disagreement is therefore eased out
/// over a fraction of a second instead: the clock briefly runs a little
/// fast or slow, never backwards, and lands exactly on the sample. A real
/// seek, a new track or a pause still moves at once.
///
/// Times are plain seconds on a monotonic clock supplied by the caller, so
/// the result depends only on when it is read, not on how often: the same
/// at 30 Hz, 60 Hz or 120 Hz.
struct PlaybackClock {
    struct Anchor: Equatable {
        var position: TimeInterval
        var time: TimeInterval
    }

    /// How long a small correction takes to settle.
    static let slewDuration: TimeInterval = 0.4
    /// Errors at or above this are treated as a seek and applied at once.
    /// Below `slewDuration`, so the eased clock always still moves forward.
    static let slewLimit: TimeInterval = 0.25

    private(set) var anchor: Anchor?
    private(set) var isRunning = false
    private var correction: (offset: TimeInterval, start: TimeInterval)?

    /// The position to show at `now`, or nil before the first sample.
    func position(at now: TimeInterval) -> TimeInterval? {
        guard let anchor else { return nil }
        let base = isRunning ? anchor.position + max(0, now - anchor.time) : anchor.position
        return base + remainingCorrection(at: now)
    }

    /// Whether a correction is still being eased out at `now`.
    func isSlewing(at now: TimeInterval) -> Bool {
        remainingCorrection(at: now) != 0
    }

    /// Re-anchors to a sample taken at `time`, applied at `now`.
    /// `continuous` means nothing but time passed since the previous sample:
    /// same track, playing throughout.
    mutating func sync(to position: TimeInterval, sampledAt time: TimeInterval, running: Bool,
                       now: TimeInterval, continuous: Bool) {
        let target = running ? position + max(0, now - time) : position
        var next: (offset: TimeInterval, start: TimeInterval)?
        if continuous, isRunning, running, let shown = self.position(at: now) {
            let error = target - shown
            if abs(error) < Self.slewLimit, error != 0 {
                // Start from what is on screen and ease onto the sample.
                next = (offset: -error, start: now)
            }
        }
        anchor = Anchor(position: position, time: time)
        isRunning = running
        correction = next
    }

    /// Pauses or resumes from what is on screen now.
    mutating func setRunning(_ running: Bool, now: TimeInterval) {
        guard running != isRunning else { return }
        if let shown = position(at: now) { anchor = Anchor(position: shown, time: now) }
        correction = nil
        isRunning = running
    }

    mutating func reset() {
        anchor = nil
        isRunning = false
        correction = nil
    }

    private func remainingCorrection(at now: TimeInterval) -> TimeInterval {
        guard let correction else { return 0 }
        let progress = (now - correction.start) / Self.slewDuration
        guard progress < 1 else { return 0 }
        return correction.offset * (1 - max(0, progress))
    }
}

/// An expanding ring of displacement fired when vocals return after a long
/// instrumental -- the geometry gets shoved outward as the wave passes
/// through it, so a drop deforms the pattern instead of restarting it.
struct Shockwave {
    var radius: Double
    var strength: Double

    /// Outward push applied to a point as the wavefront sweeps past.
    func offset(for point: CGPoint, center: CGPoint) -> CGSize {
        guard strength > 0.001 else { return .zero }
        let dx = Double(point.x - center.x)
        let dy = Double(point.y - center.y)
        let distance = max((dx * dx + dy * dy).squareRoot(), 1)

        // Only points near the wavefront move; a gaussian keeps the band soft.
        let band = 110.0
        let delta = abs(distance - radius)
        guard delta < band * 1.8 else { return .zero }
        let falloff = exp(-(delta * delta) / (2 * (band / 2) * (band / 2)))

        let push = strength * 42 * falloff
        return CGSize(width: dx / distance * push, height: dy / distance * push)
    }
}
