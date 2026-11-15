import SwiftUI

/// Word presence: how strongly a word arrives, holds and leaves.
///
/// One strength `p`, held to 1...2. At 1 every word moves exactly as it
/// did before; 2 (the default, as on Windows) makes the in, hold and out
/// clearly stronger. `extra` is `p - 1`. There is no setting.
///
/// Everything here is render-only: no factor reaches a slot, a stack's
/// spacing or a line's fitting, so the words sit where they always did and
/// only their drawn motion changes. Reduce Motion is untouched: each draw
/// site resolves its strength through `effective(_:reduceMotion:)`, which
/// is 1 under Reduce Motion.
///
/// Every factor is written so that `p = 1` returns the old value bit for
/// bit: a product with exactly 1, or `x + (x - 1) * 0`, never a refactored
/// formula.
enum WordPresence {
    /// The strength every layout draws at.
    static let wordPresence = 2.0

    /// `p` held to 1...2 (1 for anything not finite).
    static func clamped(_ p: Double) -> Double {
        p.isFinite ? min(2, max(1, p)) : 1
    }

    /// The strength a draw site uses: 1 under Reduce Motion.
    static func effective(_ p: Double, reduceMotion: Bool) -> Double {
        reduceMotion ? 1 : clamped(p)
    }

    static func extra(_ p: Double) -> Double { clamped(p) - 1 }

    // MARK: Factors

    /// Entrance travel, turn, pop shrink and blur; the exit's drift,
    /// shrink, tilt and blur.
    static func entrance(_ p: Double) -> Double { 1 + 0.8 * extra(p) }
    static func exit(_ p: Double) -> Double { 1 + 0.8 * extra(p) }
    /// A held word's swell (above 1) and drift; a rolling focus word's pose.
    static func hold(_ p: Double) -> Double { 1 + 0.7 * extra(p) }
    /// A held word's glow: its alpha and the radius term.
    static func glow(_ p: Double) -> Double { 1 + 0.35 * extra(p) }

    /// `value`'s distance from 1 times `factor`: `value` itself at factor 1.
    static func about1(_ value: Double, _ factor: Double) -> Double {
        value + (value - 1) * (factor - 1)
    }

    // MARK: Soft entrance

    /// A soft entrance with no travel of its own (Soft focus) also rises
    /// and grows into place: how far below its place it starts, in ems, and
    /// how much smaller, both unwinding with `eased`. Nothing at p = 1.
    static func softRise(_ p: Double, eased: Double) -> Double { 0.32 * extra(p) * (1 - eased) }
    static func softScale(_ p: Double, eased: Double) -> Double { 1 - 0.12 * extra(p) * (1 - eased) }

    // MARK: Stage layouts

    /// The layer a stage layout's word gets about its centre (word plus
    /// symbol), drawn with its action motion: it arrives from a little
    /// below, a little small and soft; swells a touch while it is sung; and
    /// lifts, shrinks and softens as the line leaves.
    struct StagePose: Equatable {
        /// Ems (positive is down).
        var dy = 0.0
        var scale = 1.0
        /// Points.
        var blur = 0.0
        static let identity = StagePose()
    }

    /// `arrival` is the stage arrival spring (`StageMotion.arrival`, which
    /// overshoots a little), `leave` the line's leave settle
    /// (`StageMotion.leave`). The identity at p = 1 and under Reduce Motion.
    static func stagePose(_ p: Double, arrival: Double, time: Double, start: Double, end: Double,
                          leave: Double, reduceMotion: Bool) -> StagePose {
        let extra = extra(effective(p, reduceMotion: reduceMotion))
        guard extra > 0 else { return .identity }
        let a = arrival.isFinite ? arrival : 1
        let l = leave.isFinite ? min(1, max(0, leave)) : 0
        let remaining = 1 - a
        let sung = Easing.smoothstep((time - start) / 0.12)
            * (1 - Easing.smoothstep((time - max(end, start + 0.15)) / 0.35))
        var pose = StagePose()
        pose.dy = 0.32 * extra * remaining - 0.26 * extra * l
        pose.scale = (1 - 0.12 * extra * remaining) * (1 + 0.06 * extra * sung) * (1 - 0.04 * extra * l)
        // The spring overshoots (a > 1): never a negative blur.
        pose.blur = max(0, 2 * extra * remaining) + 2.4 * extra * l
        return pose
    }
}

/// Draws `WordPresence.StagePose` about the word's centre: render-only, so
/// the layout's measured place never changes.
struct StageWordPresence: ViewModifier {
    let pose: WordPresence.StagePose
    let size: CGFloat

    func body(content: Content) -> some View {
        if pose == .identity {
            content
        } else {
            content
                .scaleEffect(CGFloat(pose.scale))
                .offset(y: CGFloat(pose.dy) * size)
                .blur(radius: pose.blur > 0.01 ? CGFloat(pose.blur) : 0)
        }
    }
}

private struct LyricWordPresenceKey: EnvironmentKey {
    static let defaultValue = WordPresence.wordPresence
}

extension EnvironmentValues {
    /// The word presence strength the lyric layers draw at. Always
    /// `WordPresence.wordPresence` in the app; tests set 1 to draw the old look.
    var lyricWordPresence: Double {
        get { self[LyricWordPresenceKey.self] }
        set { self[LyricWordPresenceKey.self] = newValue }
    }
}

extension StageLyricView {
    /// The presence layer for `run` at type `size`, on this line's
    /// arrival spring and leave settle. Line-timed lines and layouts that
    /// draw a word settled pass `live: false` and get none.
    func presence(_ run: StageRun, size: CGFloat, speed: Double, live: Bool = true) -> StageWordPresence {
        guard live else { return StageWordPresence(pose: .identity, size: size) }
        let pose = WordPresence.stagePose(wordPresence, arrival: arrive(run, speed: speed), time: time,
                                          start: run.start, end: run.end, leave: leave, reduceMotion: reduceMotion)
        return StageWordPresence(pose: pose, size: size)
    }
}
