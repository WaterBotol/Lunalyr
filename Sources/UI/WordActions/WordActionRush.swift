import SwiftUI

// MARK: - Rush (a candidate action, `WordAction.candidateList`)
//
// The word darts forward in reading direction: a tiny pull back, a quick
// dash of ~0.36 em (stretched long and leaning forward while fast), a brake
// that lets the letters bunch up toward the front, a short hold ahead of
// its place (soft speed lines still streaming off its tail while the word
// is held), then an unhurried glide back.
// Not Echo (no copies of the word, one dash and back rather than a sway),
// not Shake (one move, no tremor), not Wave (no rocking).

extension LivelyAction {
    /// Rush's travel, 0...1-ish (times the dash distance): the pull back,
    /// the dash with one small overshoot, and the glide home, which ends at
    /// D − 0.1. Continuous for all t; 0 before the onset and from D on.
    static func rushTravel(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        let pull: Double = -0.2 * bump(t, 0.2)
        // Eased in (no jolt at the start), fastest at ~0.21 s, a little
        // past its mark as it brakes, then settled by ~0.65 s.
        let dash: Double = ss5((t - 0.06) / 0.3) + 0.08 * bump(t - 0.28, 0.38)
        let home: Double = ss5((t - (D - 0.62)) / 0.52)
        return (pull + dash - home) * rest(t, D)
    }

    /// Rush's speed, in travels per second (positive is forward).
    static func rushSpeed(_ t: Double, duration D: Double) -> Double {
        let dt: Double = 1.0 / 480
        let ahead: Double = rushTravel(t + dt, duration: D)
        let behind: Double = rushTravel(t - dt, duration: D)
        return (ahead - behind) / (2 * dt)
    }

    /// 0...1: how fast it is dashing forward (the glide home is slow and
    /// barely counts).
    static func rushFast(_ t: Double, duration D: Double) -> Double {
        let v: Double = max(0, rushSpeed(t, duration: D))
        return ss(v / 5)
    }

    /// 0...1: the brake: the letters bunch toward the front as it stops,
    /// stay a little bunched while it is held ahead, and spread back out
    /// (a hair past, then even) on the way home.
    static func rushBunch(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        let squeeze: Double = spring(t - 0.22, peak: 0.16, overshoot: 0.25)
        let release: Double = spring(t - (D - 0.66), peak: 0.3, overshoot: 0.12)
        let held: Double = 1 - 0.45 * ss((t - 0.5) / 0.4)
        return squeeze * held * (1 - release) * rest(t, D, last: 0.3)
    }

    /// 0...1: the speed lines: a burst with the dash, a softer stream while
    /// the word is held ahead, gone as it glides home.
    static func rushStreak(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        let burst: Double = bump(t - 0.06, 0.62)
        let stream: Double = 0.42 * ss((t - 0.3) / 0.3)
        let gone: Double = 1 - ss((t - (D - 0.72)) / 0.4)
        return min(1, max(burst, stream) * gone)
    }
}

extension LivelyPose {
    /// Rush's whole-word part (also all that macOS 14 draws). Identity before
    /// 0 and from `D` on; everything scaled by `A`.
    static func rush(t: Double, duration D: Double, intensity A: Double, hold: Double) -> LivelyPose {
        guard t > 0, t < D else { return .identity }
        typealias L = LivelyAction
        var pose = LivelyPose()
        let r: Double = L.rest(t, D)
        let travel: Double = L.rushTravel(t, duration: D)
        let fast: Double = L.rushFast(t, duration: D)
        // The brake: a short squash as it stops (kept small; the letters
        // bunch on top of it on macOS 15).
        let brake: Double = L.bump(t - 0.27, 0.26)
        // Held ahead: a slight forward lean, as if still pressing on.
        let pressing: Double = L.ss((t - 0.3) / 0.3) * (1 - L.ss((t - (D - 0.66)) / 0.45))
        let pullLean: Double = L.bump(t, 0.2)
        pose.dx = 0.36 * A * travel
        let stretch: Double = 0.12 * fast - 0.035 * brake + 0.025 * pullLean
        pose.sx = 1 + A * stretch * r
        let thin: Double = -0.05 * fast + 0.02 * brake
        pose.sy = 1 + A * thin * r
        pose.anchorY = 1
        let lean: Double = 3 * fast + 1.2 * pressing - 1.2 * brake - 1 * pullLean
        pose.tilt = A * lean * r
        pose.glow = 0.3 * max(fast, 0.5 * L.rushStreak(t, duration: D)) * r
        return pose
    }
}

@available(macOS 15.0, *)
extension LivelyGlyphRenderer {
    /// Rush's per-letter part: each letter slants forward with the speed
    /// (sheared, the foot swept back), and the word squeezes toward its
    /// front as it brakes, then spread out again on the way home.
    func rushGlyph(_ g: Glyph, count: Int, word: CGRect, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity
        let size: Double = Double(state.size)
        let r: Double = L.rest(t, D)
        let fast: Double = L.rushFast(t - 0.03 * (1 - g.along), duration: D)
        let pressing: Double = L.ss((t - 0.3) / 0.3) * (1 - L.ss((t - (D - 0.66)) / 0.45))
        let shear: Double = A * (0.26 * fast + 0.09 * pressing) * r
        // The bunch: the word squeezes toward its front edge, each letter
        // drawn narrower and moved toward the front by the same share, so
        // the spacing closes without letters running into each other and
        // nothing passes the trailing edge (past it the sweep draws a
        // letter as unsung). A long word squeezes by at most ~0.6 em.
        let bunch: Double = L.rushBunch(t, duration: D)
        let cap: Double = 0.6 * size / max(1, Double(word.width))
        let squeeze: Double = min(0.15 * A, cap) * bunch * r
        let toFront: Double = Double(word.maxX - g.rect.midX)
        // On launch the rear letters trail a hair behind the front one.
        let lagTravel: Double = L.rushTravel(t - 0.04 * (1 - g.along), duration: D) - L.rushTravel(t, duration: D)
        let trail: Double = L.soft(0.3 * lagTravel, 0.06) * size
        let dx: Double = squeeze * toFront + A * trail * r
        guard abs(dx) > 0.01 || shear > 0.001 || abs(squeeze) > 0.0005 else {
            context.draw(g.slice)
            return
        }
        var c = context
        let mid = g.rect.midX
        c.translateBy(x: mid + CGFloat(dx), y: 0)
        c.scaleBy(x: CGFloat(1 - squeeze), y: 1)
        c.translateBy(x: -mid, y: 0)
        // x' = x − shear·(y − top): the letter leans forward with its top
        // still and its foot swept back, so it never reaches past the
        // word's trailing edge.
        let k = CGFloat(shear)
        let top = g.rect.minY
        c.concatenate(CGAffineTransform(a: 1, b: 0, c: -k, d: 1, tx: k * top, ty: 0))
        c.draw(g.slice)
    }

    /// Soft horizontal speed lines streaming off the word's trailing side,
    /// sliding away behind it like air rushing past.
    func rushBehind(word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity, seed = state.seed
        let size: Double = Double(state.size)
        let strength: Double = L.rushStreak(t, duration: D) * A
        guard strength > 0.004 else { return }
        let fast: Double = L.rushFast(t, duration: D)
        var lines = context
        lines.addFilter(.blur(radius: CGFloat(size * 0.018)))
        let ink = state.glow
        let count = 7
        let tail: Double = Double(word.minX) + size * 0.12
        for k in 0..<count {
            let u: Double = (Double(k) + 0.5) / Double(count)
            let jitter: Double = 0.12 * (L.rnd(seed, k, 21) - 0.5)
            let y: Double = Double(word.minY) + Double(word.height) * (0.16 + 0.68 * u + jitter)
            // Each line slides back from the tail and fades, then comes again.
            let rate: Double = 1.6 + 0.9 * L.rnd(seed, k, 22)
            let cycle: Double = (t * rate + L.rnd(seed, k, 23)).truncatingRemainder(dividingBy: 1)
            let life: Double = sin(.pi * cycle)
            let longer: Double = 0.45 + 0.75 * L.rnd(seed, k, 24)
            let length: Double = size * longer * (0.55 + 0.45 * fast) * (0.5 + 0.5 * A)
            let drift: Double = size * 0.55 * cycle
            let right: Double = tail - drift
            let left: Double = max(Double(word.minX) - size * 1.7, right - length)
            guard right - left > 1 else { continue }
            let alpha: Double = strength * life * (0.6 + 0.35 * L.rnd(seed, k, 25)) * (0.75 + 0.25 * fast)
            guard alpha > 0.01 else { continue }
            let width: Double = max(1, size * (0.03 + 0.03 * L.rnd(seed, k, 26)))
            var path = Path()
            path.move(to: CGPoint(x: left, y: y))
            path.addLine(to: CGPoint(x: right, y: y))
            lines.stroke(path, with: .linearGradient(
                Gradient(colors: [ink.opacity(0), ink.opacity(0.85 * alpha)]),
                startPoint: CGPoint(x: left, y: y), endPoint: CGPoint(x: right, y: y)),
                style: StrokeStyle(lineWidth: CGFloat(width), lineCap: .round))
        }
    }

    /// Drawn in front of the letters, once per word.
    func rushFront(glyphs: [Glyph], word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {}
}
