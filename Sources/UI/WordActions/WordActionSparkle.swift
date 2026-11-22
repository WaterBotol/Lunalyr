import SwiftUI

// MARK: - Sparkle (a candidate action, `WordAction.candidateList`)
//
// Small crisp four-point twinkles pop on a few letters in turn, left to
// right with a little randomness: each flashes in, holds a beat while it
// turns a touch, and shrinks out, and its letter catches a brief bright
// glint as it fires (every other letter a fainter one, in the same sweep).
// A long-held word gets a second, smaller round of twinkles. The whole word
// only pulses its glow a little in step (all that macOS 14 draws).
// Not the dropped "glow" (a flat bloom with a ring of thin lines round the
// word), not Float's soft eight-point sparkles below the word, and not
// Sunlight's moving sheen and rays: here the light is in points, on the letters.

extension LivelyAction {
    /// One twinkle: on which letter, when it fires, how big (1 the first
    /// round), and a key for its own random place, size and turn.
    struct SparkleTwinkle {
        var letter: Int
        var at: Double
        var scale: Double
        var key: Int
    }

    /// A twinkle's life, seconds: in ~0.09 s, a beat held, out by the end.
    static let twinkleLife = 0.56

    /// The time the left-to-right sweep takes across a word of `count`
    /// letters: 0.3 s + 0.11 s a letter, but done by about the end of the
    /// hold (`D` − 0.95 s; `D` is the hold + 0.8 s).
    static func sparkleSpan(count: Int, duration D: Double) -> Double {
        let want: Double = 0.3 + 0.11 * Double(count)
        let room: Double = max(0.3, D - 0.95)
        return min(want, room)
    }

    /// Where letter `i` is in the sweep, 0...1 (by its index, so the
    /// letters and the twinkles drawn over them agree).
    static func sparkleAlong(_ i: Int, count: Int) -> Double {
        count > 1 ? min(1, max(0, Double(i) / Double(count - 1))) : 0.5
    }

    /// When letter `i` catches its glint in the first sweep.
    static func sparkleFire(_ i: Int, count: Int, seed: Int, duration D: Double) -> Double {
        let span: Double = sparkleSpan(count: count, duration: D)
        let jitter: Double = 0.09 * (rnd(seed, i, 41) - 0.5)
        return max(0.03, 0.06 + span * sparkleAlong(i, count: count) + jitter)
    }

    /// The twinkles: one on a letter of each of a few even stretches of the
    /// word (2 to 5 of them), firing with that letter's glint; for a word
    /// held long, a second round, one fewer, 0.72× the size, on letters
    /// picked afresh (they may repeat), in a sweep of 0.8× the time.
    static func sparkleTwinkles(count: Int, seed: Int, duration D: Double) -> [SparkleTwinkle] {
        guard count > 0 else { return [] }
        let n: Int = min(count, min(5, max(2, (count + 1) / 2)))
        var result: [SparkleTwinkle] = []
        func pick(_ k: Int, of parts: Int, salt: Int) -> Int {
            let lo: Int = k * count / parts
            let hi: Int = max(lo + 1, (k + 1) * count / parts)
            let r: Double = rnd(seed, k, salt)
            return min(hi - 1, lo + Int(r * Double(hi - lo)))
        }
        for k in 0..<n {
            let letter = pick(k, of: n, salt: 43)
            result.append(SparkleTwinkle(letter: letter, at: sparkleFire(letter, count: count, seed: seed, duration: D),
                                         scale: 1, key: k))
        }
        // A second round, if the word is held long enough for it.
        let span: Double = sparkleSpan(count: count, duration: D)
        let start2: Double = 0.06 + span + 0.2
        let room2: Double = D - 0.2 - twinkleLife - start2
        let span2: Double = min(0.8 * span, room2)
        guard span2 >= 0.15 else { return result }
        let n2: Int = max(1, min(count, n - 1))
        for k in 0..<n2 {
            let letter = pick(k, of: n2, salt: 47)
            let jitter: Double = 0.08 * (rnd(seed, k, 48) - 0.5)
            let at: Double = start2 + span2 * sparkleAlong(letter, count: count) + jitter
            result.append(SparkleTwinkle(letter: letter, at: min(at, D - 0.2 - twinkleLife), scale: 0.72, key: 20 + k))
        }
        return result
    }

    /// A glint's brightness `x` seconds after it fires: up in ~0.06 s,
    /// down over ~0.35 s.
    static func glintCurve(_ x: Double) -> Double {
        let up: Double = ss((x + 0.03) / 0.08)
        let down: Double = 1 - ss5((x - 0.05) / 0.36)
        return up * down
    }

    /// How bright letter `i` is lit now, 0...1: its sweep glint (0.25), or
    /// its twinkle's (1, or 0.7 in the second round).
    static func sparkleGlint(_ t: Double, letter i: Int, count: Int, seed: Int, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        var g: Double = 0.25 * glintCurve(t - sparkleFire(i, count: count, seed: seed, duration: D))
        for twinkle in sparkleTwinkles(count: count, seed: seed, duration: D) where twinkle.letter == i {
            let strength: Double = twinkle.scale < 1 ? 0.7 : 1
            g = max(g, strength * glintCurve(t - twinkle.at))
        }
        return g * rest(t, D)
    }

    /// The whole word's glow pulses: one short pulse (0.24 s) with each
    /// twinkle of a typical six-letter word (seed 11, as `LivelyActionEffect`
    /// gives), as the pose doesn't know the letter count.
    static func sparklePulses(_ t: Double, duration D: Double) -> Double {
        var total = 0.0
        for twinkle in sparkleTwinkles(count: 6, seed: 11, duration: D) {
            let amount: Double = twinkle.scale < 1 ? 0.7 : 1
            let start: Double = max(0, twinkle.at - 0.09)
            total += amount * bump(t - start, 0.24)
        }
        return min(1.2, total)
    }
}

extension LivelyPose {
    /// Sparkle's whole-word part (also all that macOS 14 draws): a little
    /// glow pulse and the slightest swell with each twinkle, nothing steady.
    /// Identity before 0 and from `D` on; everything scaled by `A`.
    static func sparkle(t: Double, duration D: Double, intensity A: Double, hold: Double) -> LivelyPose {
        typealias L = LivelyAction
        guard t > 0, t < D else { return .identity }
        var pose = LivelyPose()
        let pulses: Double = L.sparklePulses(t, duration: D) * L.rest(t, D)
        pose.glow = 0.5 * A * pulses
        pose.warm = 0.15 * A * pulses
        let s: Double = 0.02 * A * pulses
        pose.sx = 1 + s
        pose.sy = 1 + s
        pose.anchorY = 1
        return pose
    }
}

@available(macOS 15.0, *)
extension LivelyGlyphRenderer {
    /// Sparkle's per-letter part: as its glint fires, the letter pops a hair
    /// and lights up, warm white round the point where its twinkle sits
    /// (the top of the letter for a letter without one), fading across the
    /// rest of it, and is back to its ink a third of a second on.
    func sparkleGlyph(_ g: Glyph, count: Int, word: CGRect, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity, seed = state.seed
        let glint: Double = L.sparkleGlint(t, letter: g.index, count: count, seed: seed, duration: D)
        guard glint > 0.003 else {
            context.draw(g.slice)
            return
        }
        var c = context
        let pop = CGFloat(1 + 0.05 * A * glint)
        let anchor = CGPoint(x: g.rect.midX, y: g.rect.maxY)
        c.translateBy(x: anchor.x, y: anchor.y)
        c.scaleBy(x: pop, y: pop)
        c.translateBy(x: -anchor.x, y: -anchor.y)
        c.draw(g.slice)
        // The light: from the twinkle's point, if this letter has one now.
        var point = CGPoint(x: g.rect.midX, y: g.rect.minY + g.rect.height * 0.3)
        var best = 0.0
        for twinkle in L.sparkleTwinkles(count: count, seed: seed, duration: D) where twinkle.letter == g.index {
            let near: Double = L.glintCurve(t - twinkle.at)
            if near > best {
                best = near
                point = sparkleTwinklePoint(twinkle, in: g.rect)
            }
        }
        let size = state.size
        let spread: CGFloat = max(g.rect.width, g.rect.height) * 0.8 + size * 0.1
        let reach: Double = 0.35 + 0.65 * A
        let alpha = Float(min(1, 1.05 * glint * reach))
        var lit = c
        lit.clipToLayer { layer in
            layer.fill(Path(g.rect.insetBy(dx: -size * 0.2, dy: -size * 0.2)), with: .radialGradient(
                Gradient(stops: [
                    .init(color: .white, location: 0),
                    .init(color: .white.opacity(0.8), location: 0.35),
                    .init(color: .white.opacity(0.25), location: 0.75),
                    .init(color: .white.opacity(0), location: 1),
                ]),
                center: point, startRadius: 0, endRadius: spread))
        }
        lit.addFilter(.colorMatrix(Self.flat((1.0, 0.93, 0.72), alpha: alpha)))
        lit.draw(g.slice)
    }

    /// Where a twinkle sits on its letter: over the upper part, a little
    /// off its middle.
    func sparkleTwinklePoint(_ twinkle: LivelyAction.SparkleTwinkle, in rect: CGRect) -> CGPoint {
        typealias L = LivelyAction
        let fx: Double = 0.2 + 0.6 * L.rnd(state.seed, twinkle.key, 51)
        let fy: Double = 0.14 + 0.28 * L.rnd(state.seed, twinkle.key, 52)
        return CGPoint(x: rect.minX + rect.width * CGFloat(fx), y: rect.minY + rect.height * CGFloat(fy))
    }

    /// Drawn behind the letters, once per word: nothing (the light is in
    /// the twinkles and the letters).
    func sparkleBehind(word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {}

    /// Drawn in front of the letters, once per word: the twinkles, each over
    /// the upper part of its letter, with a tiny companion beside some.
    func sparkleFront(glyphs: [Glyph], word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity, seed = state.seed
        let size = Double(state.size)
        guard t > 0, t < D else { return }
        let count = slot?.count ?? glyphs.count
        guard count > 0 else { return }
        let grow: Double = 0.45 + 0.55 * A
        let reach: Double = min(1, 0.4 + 0.6 * A)
        for twinkle in L.sparkleTwinkles(count: count, seed: seed, duration: D) {
            let a: Double = (t - twinkle.at) / L.twinkleLife
            guard a > -0.2, a < 1.3 else { continue }
            let rect = sparkleLetterRect(twinkle.letter, count: count, glyphs: glyphs, word: word)
            let key = twinkle.key
            let centre = sparkleTwinklePoint(twinkle, in: rect)
            let radius: Double = size * (0.3 + 0.12 * L.rnd(seed, key, 53)) * twinkle.scale * grow
            let turn0: Double = -15 + 30 * L.rnd(seed, key, 54)
            drawTwinkle(at: centre, radius: radius, turn0: turn0, a: a, alpha: reach, in: &context)
            // A tiny companion just after it, up and to one side.
            guard L.rnd(seed, key, 55) < 0.65 else { continue }
            let side: Double = L.rnd(seed, key, 56) < 0.5 ? -1 : 1
            let ox: Double = side * size * (0.22 + 0.1 * L.rnd(seed, key, 57))
            let oy: Double = -size * (0.1 + 0.12 * L.rnd(seed, key, 58))
            let little = CGPoint(x: centre.x + CGFloat(ox), y: centre.y + CGFloat(oy))
            let b: Double = (t - twinkle.at - 0.1) / (0.8 * L.twinkleLife)
            drawTwinkle(at: little, radius: radius * 0.42, turn0: -turn0, a: b, alpha: reach * 0.9, in: &context)
        }
    }

    /// Letter `i`'s box: its own, or (a word set letter by letter) its
    /// share of the word's estimated width.
    private func sparkleLetterRect(_ i: Int, count: Int, glyphs: [Glyph], word: CGRect) -> CGRect {
        if let g = glyphs.first(where: { $0.index == i }) { return g.rect }
        let w: CGFloat = word.width / CGFloat(max(1, count))
        return CGRect(x: word.minX + w * CGFloat(i), y: word.minY, width: w, height: word.height)
    }

    /// One twinkle `a` of the way through its life (0...1): a crisp
    /// four-point glint (the upright points a little longer), a small
    /// diagonal cross and a soft core, white with a faint warm edge so it
    /// reads on a light tile or light ink too. It pops in a touch past its
    /// size, holds, turns ~25°, and shrinks out.
    private func drawTwinkle(at centre: CGPoint, radius: Double, turn0: Double, a: Double, alpha: Double,
                             in context: inout GraphicsContext) {
        typealias L = LivelyAction
        guard a > 0, a < 1, radius > 0.5 else { return }
        let pop: Double = L.easeOutBack(L.clamp01(a / 0.17), 2.4)
        let breathe: Double = 1 + 0.06 * sin(2 * .pi * 2.6 * a) * L.bump(a - 0.15, 0.45)
        let out: Double = 1 - L.ss((a - 0.45) / 0.55)
        let scale: Double = max(0, pop * breathe * out)
        let fade: Double = L.ss(a / 0.05) * (1 - L.ss((a - 0.82) / 0.18))
        let opacity: Double = alpha * fade
        let r = CGFloat(radius * scale)
        guard r > 0.3, opacity > 0.005 else { return }
        let turn: Double = turn0 + 25 * L.easeOutCubic(a)
        var c = context
        c.translateBy(x: centre.x, y: centre.y)
        c.rotate(by: .degrees(turn))
        let warmWhite = Color(red: 1, green: 0.97, blue: 0.88)
        let edge = Color(red: 0.5, green: 0.27, blue: 0.06)
        // The soft core.
        let core: CGFloat = r * 0.62
        c.fill(Path(ellipseIn: CGRect(x: -core, y: -core, width: 2 * core, height: 2 * core)),
               with: .radialGradient(Gradient(colors: [warmWhite.opacity(0.75 * opacity), warmWhite.opacity(0)]),
                                     center: .zero, startRadius: 0, endRadius: core))
        // The small diagonal cross.
        var diagonal = c
        diagonal.rotate(by: .degrees(45))
        let small = Self.glintStar(long: r * 0.4, wide: r * 0.4, waist: r * 0.06)
        diagonal.fill(small, with: .color(warmWhite.opacity(0.7 * opacity)))
        // The four-point glint, edged.
        let star = Self.glintStar(long: r, wide: r * 0.72, waist: r * 0.12)
        let line: CGFloat = max(0.6, r * 0.06)
        c.stroke(star, with: .color(edge.opacity(0.45 * opacity)), style: StrokeStyle(lineWidth: line, lineJoin: .round))
        c.fill(star, with: .color(Color.white.opacity(opacity)))
    }

    /// A four-point star round the origin: slender points `long` up and
    /// down, `wide` left and right, flaring into a small waist (`waist`
    /// out along each diagonal) at the middle.
    static func glintStar(long: CGFloat, wide: CGFloat, waist: CGFloat) -> Path {
        var path = Path()
        let k: CGFloat = waist
        let tips: [CGPoint] = [CGPoint(x: 0, y: -long), CGPoint(x: wide, y: 0), CGPoint(x: 0, y: long), CGPoint(x: -wide, y: 0)]
        let waists: [CGPoint] = [CGPoint(x: k, y: -k), CGPoint(x: k, y: k), CGPoint(x: -k, y: k), CGPoint(x: -k, y: -k)]
        path.move(to: tips[0])
        for n in 0..<4 {
            let tip = tips[n], next = tips[(n + 1) % 4], w = waists[n]
            // Along the arm, bowed in towards its axis, to the waist; then out along the next arm.
            path.addQuadCurve(to: w, control: CGPoint(x: tip.x * 0.3 + w.x * 0.35, y: tip.y * 0.3 + w.y * 0.35))
            path.addQuadCurve(to: next, control: CGPoint(x: next.x * 0.3 + w.x * 0.35, y: next.y * 0.3 + w.y * 0.35))
        }
        path.closeSubpath()
        return path
    }
}
