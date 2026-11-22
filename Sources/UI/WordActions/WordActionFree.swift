import SwiftUI

// MARK: - Free (a candidate action, `WordAction.candidateList`)
//
// "Fly away": the end of the word breaks loose. Its last letters (about the
// last third, at least one, at most four) lift off one after another, the
// last letter first: a quick take-off up and out past the word's end (about
// 70 % of the way), then a slow glide on up and away for as long as they are
// gone, ~1.2–1.75 em up and ~1.2–1.7 em out at the furthest, shrinking,
// turning and fading a touch, bobbing on the wind; then they glide back down
// swaying like falling leaves and settle into their places, the innermost
// first. A faint ghost keeps each one's place while it is gone. The rest of
// the word stays put, lifting and leaning a little toward its end as they go
// (all that macOS 14 draws) and dipping softly as their weight comes home.
// Not Float (the whole word rises, letters bob) nor Dissolve (dust): only
// the end flies, and it comes back.

extension LivelyAction {
    /// How many letters fly: about the last third, at least one, at most four.
    static func freeFlyers(count: Int) -> Int {
        guard count > 3 else { return 1 }
        let third: Double = (Double(count) / 3).rounded(.up)
        return min(4, Int(third))
    }

    /// The whole word's lift and lean, 0...1: up over half a second as the
    /// letters leave, down as they come home.
    static func freeLift(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        let up: Double = ss5((t - 0.04) / 0.55)
        let down: Double = ss5((t - (D - 0.95)) / 0.65)
        return up * (1 - down)
    }

    /// One flying letter's motion, in ems (x outward, past the word's end;
    /// y negative is up) and degrees, all at A = 1.
    struct FreeFlight {
        var dx = 0.0, dy = 0.0, deg = 0.0, scale = 1.0
        /// 0...1: how far from home (for the fade and the ghost).
        var away = 0.0
        /// 0...1: the wing beat squeezing the letter as it takes off.
        var flap = 0.0
    }

    /// When letter `r` (0: the last letter) of `m` flying takes off, and
    /// when it turns for home (it is home `freeHomeTime` later).
    static func freeTimes(r: Int, duration D: Double) -> (start: Double, home: Double) {
        let rd = Double(r)
        let start: Double = 0.1 + 0.13 * rd
        let home: Double = D - 1.02 - 0.11 * rd
        return (start, home)
    }
    static let freeHomeTime = 0.82

    static func freeFlight(_ t: Double, r: Int, flyers m: Int, duration D: Double, seed: Int) -> FreeFlight {
        var f = FreeFlight()
        guard t > 0, t < D else { return f }
        let rd = Double(r)
        // Off one after another, the last letter first; home the other way
        // round, the innermost first, the last letter settling last.
        let times = freeTimes(r: r, duration: D)
        let homeStart: Double = times.home
        let homeTime: Double = freeHomeTime
        let tau: Double = t - times.start
        guard tau > 0 else { return f }
        // A quick take-off (most of the way, a touch past and back), then a
        // slow glide on up and away for as long as it is gone, so it never
        // parks in the air.
        let glideTime: Double = max(0.5, homeStart - times.start)
        let glide: Double = ss(tau / glideTime)
        let climb: Double = spring(tau - 0.03, peak: 0.66, overshoot: 0.07)
        let out: Double = spring(tau, peak: 0.6, overshoot: 0.05)
        let rise: Double = 0.7 * climb + 0.3 * glide
        let drift: Double = 0.68 * out + 0.32 * glide
        let home: Double = ss5((t - homeStart) / homeTime)
        let stay: Double = 1 - home
        // The outermost climbs highest and drifts furthest. Lower and wider
        // than first drawn (1.1-1.7 em up), which crossed the line above.
        let outer: Double = m > 1 ? 1 - rd / Double(m - 1) : 1
        let height: Double = 0.8 + 0.4 * outer + 0.07 * rnd(seed, r, 41)
        let reach: Double = 1.4 + 0.45 * outer + 0.12 * rnd(seed, r, 42)
        let side: Double = r % 2 == 0 ? 1 : -1
        let turn: Double = side * (14 + 8 * rnd(seed, r, 43))
        // On the wind: a slow bob and sway, out of step letter to letter.
        let hang: Double = ss((tau - 0.35) / 0.6) * stay
        let phase: Double = 2.1 * rd + 6 * rnd(seed, r, 44)
        let bobAngle: Double = 2 * .pi * 0.8 * tau + phase
        let swayAngle: Double = 2 * .pi * 0.5 * tau + 0.7 * phase
        let bob: Double = 0.07 * sin(bobAngle) * hang
        let sway: Double = 0.08 * sin(swayAngle) * hang
        // Coming down like a leaf: a pendulum sway, the letter rocking with it.
        let fall: Double = bump(t - homeStart, homeTime)
        let leafAngle: Double = 2 * .pi * 1.15 * (t - homeStart) + phase
        let leaf: Double = 0.14 * sin(leafAngle) * fall
        let rock: Double = 13 * cos(leafAngle) * fall
        let up: Double = clamp01(rise)
        f.away = up * stay
        f.dy = -(height * rise + bob) * stay
        let across: Double = (reach * drift + sway) * stay
        f.dx = across + leaf
        let leaning: Double = turn * up * stay
        let rocking: Double = 7 * cos(swayAngle) * hang
        f.deg = leaning + rocking + rock
        f.scale = 1 - 0.2 * f.away
        f.flap = bump(tau, 0.55)
        return f
    }
}

extension LivelyPose {
    /// Free's whole-word part (also all that macOS 14 draws): the word lifts
    /// a little and leans up toward its end as the letters leave, holds,
    /// and comes down with a soft dip as their weight comes home. Identity
    /// before 0 and from `D` on; everything scaled by `A`.
    static func free(t: Double, duration D: Double, intensity A: Double, hold: Double) -> LivelyPose {
        typealias L = LivelyAction
        guard t > 0, t < D else { return .identity }
        var pose = LivelyPose()
        let lift: Double = L.freeLift(t, duration: D)
        let breatheAngle: Double = 2 * .pi * 0.6 * (t - 0.6)
        let breatheIn: Double = L.ss((t - 0.6) / 0.5) * lift
        let breathe: Double = 0.018 * sin(breatheAngle) * breatheIn
        let land: Double = 0.04 * L.bump(t - (D - 0.55), 0.45)
        let rise: Double = -0.2 * lift
        pose.dy = A * (rise + breathe + land)
        pose.dx = A * 0.05 * lift
        pose.tilt = -3.6 * A * lift
        let settle: Double = 0.03 * A * L.bump(t - (D - 0.5), 0.4)
        pose.sy = 1 - settle
        pose.sx = 1 + 0.5 * settle
        pose.anchorY = 1
        pose.glow = 0.3 * lift * A
        return pose
    }
}

@available(macOS 15.0, *)
extension LivelyGlyphRenderer {
    /// Free's per-letter part: the last letters fly off and come home (a
    /// faint ghost keeps each one's place while it is gone); the others
    /// stay where they are.
    func freeGlyph(_ g: Glyph, count: Int, word: CGRect, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity
        let size = Double(state.size)
        let m = L.freeFlyers(count: count)
        let r = count - 1 - g.index
        guard r >= 0, r < m, t > 0, t < D else { context.draw(g.slice); return }
        let f = L.freeFlight(t, r: r, flyers: m, duration: D, seed: state.seed)
        guard f.away > 0.0005 || abs(f.dx) > 0.0005 || abs(f.dy) > 0.0005 else { context.draw(g.slice); return }
        // A word of one glyph (a single CJK character) flies whole: less far.
        let span: Double = count == 1 ? 0.6 : 1
        // Out past the word's own end (the right for left-to-right text).
        let outward: Double = g.rect.midX >= word.midX ? 1 : -1
        // Its place, faintly, while it is away.
        let ghostAlpha: Double = 0.16 * f.away * A
        if ghostAlpha > 0.004 {
            var ghost = context
            ghost.opacity = ghostAlpha
            ghost.draw(g.slice)
        }
        var c = context
        let fade: Double = 0.35 * f.away * A
        c.opacity = 1 - fade
        let mid = CGPoint(x: g.rect.midX, y: g.rect.midY)
        let flapAngle: Double = 2 * .pi * 3.2 * t
        let flapSine: Double = sin(flapAngle)
        let beat: Double = 0.16 * A * f.flap * flapSine * flapSine
        let shrink: Double = 1 - (1 - f.scale) * A
        let offsetX: Double = outward * f.dx * A * span * size
        let offsetY: Double = f.dy * A * span * size
        let degrees: Double = outward * f.deg * A
        let scaleX: Double = shrink * (1 - beat)
        let scaleY: Double = shrink * (1 + 0.3 * beat)
        c.translateBy(x: mid.x + CGFloat(offsetX), y: mid.y + CGFloat(offsetY))
        c.rotate(by: .degrees(degrees))
        c.scaleBy(x: CGFloat(scaleX), y: CGFloat(scaleY))
        c.translateBy(x: -mid.x, y: -mid.y)
        c.draw(g.slice)
    }

    /// Drawn behind the letters, once per word.
    func freeBehind(word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {}

    /// Drawn in front of the letters, once per word.
    func freeFront(glyphs: [Glyph], word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {}
}
