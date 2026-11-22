import SwiftUI

// MARK: - Sunlight and Moonlight (candidate actions, `WordAction.candidateList`)
//
// Sunlight: a warm sheen sweeps across the letters from the leading side,
// the ink warms and brightens behind it, and a few soft rays fan up from
// behind the word as the sheen passes their side, then everything fades.
// Not the dropped "glow" (a flat bloom with a ring of thin lines all round
// the word): the moving sheen and the warm, wide, upward rays are the look.
// Moonlight: the same sweep and wash in cool silver, with no rays.

extension LivelyAction {
    /// Where the sheen's centre is across the word: 0 the leading edge, 1
    /// the far one; it crosses once, in about a second.
    static func sunSweep(_ t: Double) -> Double { -0.35 + 1.75 * ss5((t - 0.06) / 1.05) }

    /// How lit the word is: up over half a second, held, down over the
    /// last 0.75 s.
    static func sunLit(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        return ss5(t / 0.5) * (1 - ss5((t - (D - 0.8)) / 0.75))
    }

    /// The tint the letters warm (or cool) to, and the sheen's brighter core.
    static func sunInk(cool: Bool) -> (tint: (r: Float, g: Float, b: Float), sheen: (r: Float, g: Float, b: Float)) {
        cool ? ((0.80, 0.88, 1.0), (0.95, 0.98, 1.0)) : ((1.0, 0.84, 0.5), (1.0, 0.97, 0.86))
    }
}

extension LivelyPose {
    /// The whole word: it warms (cools) and lifts a hair toward the light.
    static func sunlight(t: Double, duration D: Double, intensity A: Double, cool: Bool) -> LivelyPose {
        typealias L = LivelyAction
        var pose = LivelyPose()
        let lit = L.sunLit(t, duration: D)
        if cool { pose.cool = 0.3 * A * lit } else { pose.warm = 0.3 * A * lit }
        pose.glow = 0.3 * lit * A
        pose.dy = -0.03 * A * lit
        let s = 0.025 * A * lit
        pose.sx = 1 + s
        pose.sy = 1 + s
        pose.anchorY = 1
        return pose
    }
}

@available(macOS 15.0, *)
extension LivelyGlyphRenderer {
    private var cool: Bool { state.action == .moonlight }

    /// A letter: drawn, then washed in the warm (cool) tint where the sheen
    /// has passed, and lit by the sheen's band itself, a slanted soft stripe
    /// across the whole word, so it crosses each letter smoothly.
    func sunlightGlyph(_ g: Glyph, word: CGRect, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity, size = state.size
        context.draw(g.slice)
        let lit = L.sunLit(t, duration: D)
        guard lit > 0.002 else { return }
        let sweep = L.sunSweep(t)
        let ink = L.sunInk(cool: cool)
        // Behind the sheen: the letter keeps a warm wash until the light fades.
        let passed = L.ss((sweep - g.along) / 0.3)
        let wash = Float(min(0.65, 0.62 * passed * lit * A))
        if wash > 0.004 {
            var tinted = context
            tinted.addFilter(.colorMatrix(Self.flat(ink.tint, alpha: wash)))
            tinted.draw(g.slice)
        }
        // The sheen: a soft slanted band, brightest at its centre.
        let x = word.minX + word.width * CGFloat(sweep)
        let band = max(size * 0.45, word.width * 0.16)
        guard x + band > g.rect.minX, x - band < g.rect.maxX else { return }
        var sheen = context
        let slant = band * 0.45
        let core = Color.white
        sheen.clipToLayer { layer in
            layer.fill(Path(g.rect.insetBy(dx: -size * 0.2, dy: -size * 0.3)), with: .linearGradient(
                Gradient(stops: [
                    .init(color: core.opacity(0), location: 0),
                    .init(color: core.opacity(0.55), location: 0.35),
                    .init(color: core, location: 0.5),
                    .init(color: core.opacity(0.55), location: 0.65),
                    .init(color: core.opacity(0), location: 1),
                ]),
                startPoint: CGPoint(x: x - band, y: word.midY + slant), endPoint: CGPoint(x: x + band, y: word.midY - slant)))
        }
        sheen.addFilter(.colorMatrix(Self.flat(ink.sheen, alpha: Float(min(0.97, 0.97 * A * L.ss(lit / 0.4))))))
        sheen.draw(g.slice)
    }

    /// Sunlight: soft warm rays fanning up from behind the word, each
    /// growing as the sheen passes its side and fading with the light.
    /// Moonlight: none (a halo round the word read as the dropped glow).
    func sunlightBehind(word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity, size = state.size, seed = state.seed
        let lit = L.sunLit(t, duration: D)
        guard lit > 0.004 else { return }
        guard !cool else { return }
        let centre = CGPoint(x: word.midX, y: word.midY + word.height * 0.2)
        let sweep = L.sunSweep(t)
        let gold = Color(red: 1, green: 0.8, blue: 0.42)
        var rays = context
        rays.addFilter(.blur(radius: size * 0.06))
        let count = 7
        let halfWidth = Double(word.width) * 0.5, halfHeight = Double(word.height) * 0.5
        for k in 0..<count {
            let u = (Double(k) + 0.5) / Double(count)
            let grown = L.ss((sweep - u + 0.15) / 0.35)
            let strength = grown * lit * A * (0.75 + 0.25 * L.rnd(seed, k, 1))
            guard strength > 0.01 else { continue }
            // Up from the left to up from the right; swaying a touch.
            let angle = -.pi * (0.9 - 0.8 * u) + 0.035 * sin(1.3 * t + Double(k) * 1.7)
            let reach = 0.35 + 0.65 * L.ss5(grown)
            let outX = (halfWidth + Double(size) * (1.1 + 0.55 * L.rnd(seed, k, 2))) * reach
            let outY = (halfHeight + Double(size) * (1.0 + 0.45 * L.rnd(seed, k, 3))) * reach
            let inX = halfWidth * 0.3, inY = halfHeight * 0.3
            func point(_ a: Double, _ rx: Double, _ ry: Double) -> CGPoint {
                CGPoint(x: centre.x + CGFloat(rx * cos(a)), y: centre.y + CGFloat(ry * sin(a)))
            }
            let base = 0.03, tip = 0.085 + 0.03 * L.rnd(seed, k, 4)
            var beam = Path()
            beam.move(to: point(angle - base, inX, inY))
            beam.addLine(to: point(angle - tip, outX, outY))
            beam.addLine(to: point(angle + tip, outX, outY))
            beam.addLine(to: point(angle + base, inX, inY))
            beam.closeSubpath()
            let end = CGFloat(max(outX, outY))
            rays.fill(beam, with: .radialGradient(
                Gradient(colors: [gold.opacity(0.75 * strength), gold.opacity(0.32 * strength), gold.opacity(0)]),
                center: centre, startRadius: CGFloat(min(inX, inY)), endRadius: end))
        }
    }

    /// Sunlight: a few warm motes drifting up through the light.
    func sunlightFront(glyphs: [Glyph], word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {
        typealias L = LivelyAction
        guard !cool else { return }
        let t = state.t, D = state.duration, A = state.intensity, size = state.size, seed = state.seed
        let lit = L.sunLit(t, duration: D)
        guard lit > 0.01 else { return }
        for k in 0..<8 {
            let born = 0.25 + L.rnd(seed, k, 5) * max(0.2, D - 1.4)
            let a = (t - born) / 1.1
            guard a > 0, a < 1 else { continue }
            let x = word.minX + word.width * CGFloat(L.rnd(seed, k, 6))
                + size * CGFloat(0.08 * sin(a * 4 + Double(k)))
            let y = word.minY + word.height * CGFloat(0.3 * L.rnd(seed, k, 7)) - size * CGFloat(0.5 * a)
            let r = size * CGFloat(0.018 + 0.016 * L.rnd(seed, k, 8))
            let alpha = sin(.pi * a) * lit * A
            context.fill(Path(ellipseIn: CGRect(x: x - r * 2.5, y: y - r * 2.5, width: r * 5, height: r * 5)),
                         with: .color(Color(red: 1, green: 0.85, blue: 0.5).opacity(0.18 * alpha)))
            context.fill(Path(ellipseIn: CGRect(x: x - r, y: y - r, width: 2 * r, height: 2 * r)),
                         with: .color(Color(red: 1, green: 0.95, blue: 0.8).opacity(0.85 * alpha)))
        }
    }

    /// A colour matrix that paints a glyph one colour at `alpha` of its coverage.
    static func flat(_ rgb: (r: Float, g: Float, b: Float), alpha: Float) -> ColorMatrix {
        var matrix = ColorMatrix()
        matrix.r1 = 0; matrix.r5 = rgb.r
        matrix.g2 = 0; matrix.g5 = rgb.g
        matrix.b3 = 0; matrix.b5 = rgb.b
        matrix.a4 = alpha
        return matrix
    }
}
