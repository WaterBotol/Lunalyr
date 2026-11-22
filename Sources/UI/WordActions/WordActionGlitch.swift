import SwiftUI

// MARK: - Glitch (a candidate action, `WordAction.candidateList`)
//
// For a few frames the word breaks into horizontal slices, bands that run
// right across the word, and some bands jump sideways by different amounts
// with a red/cyan channel split, in two or three short bursts; between
// them, and after, it is clean and still. Not the dropped "electric" (the
// whole word and each letter jittering every frame, white flashes, a cool
// tint, sparks): the word itself stays put, only slices of it jump, and
// there is no flash, tint or spark.

extension LivelyAction {
    /// Glitch's time steps: 24 a second, so a burst reads as a few hard
    /// cuts, like a stuttering signal, not as motion.
    static let glitchSteps: Double = 24

    /// The bursts, in steps of `glitchSteps`: (first step, last step,
    /// strength). Two for a short word, three when there is room for the
    /// third to end 0.2 s before `D`.
    static func glitchBursts(duration D: Double) -> [(first: Int, last: Int, strength: Double)] {
        // The first waits for the word to have landed (most layouts bring
        // a word in over about 0.2 s).
        var bursts: [(first: Int, last: Int, strength: Double)] = [(5, 8, 1.0), (12, 14, 0.8)]
        let thirdEnd: Double = 21 / glitchSteps
        if thirdEnd <= D - 0.2 { bursts.append((19, 20, 0.6)) }
        return bursts
    }

    /// Whether `step` is the last of its burst (that one only splits the
    /// colour, as the signal comes back).
    static func glitchLastStep(_ step: Int, duration D: Double) -> Bool {
        glitchBursts(duration: D).contains { $0.last == step }
    }

    /// The step `t` falls in.
    static func glitchStep(_ t: Double) -> Int { Int((t * glitchSteps).rounded(.down)) }

    /// The burst `t` is in, if any: its index and strength.
    static func glitchBurst(_ t: Double, duration D: Double) -> (index: Int, strength: Double)? {
        guard t > 0, t < D else { return nil }
        let step = glitchStep(t)
        for (k, burst) in glitchBursts(duration: D).enumerated() where step >= burst.first && step <= burst.last {
            return (k, burst.strength)
        }
        return nil
    }
}

extension LivelyPose {
    /// Glitch's whole-word part (also all that macOS 14 draws): on each
    /// burst a smooth sideways kick (alternating sides) and a slight
    /// horizontal smear (wider, a hair flatter). Identity before 0 and from
    /// `D` on; everything scaled by `A`.
    static func glitch(t: Double, duration D: Double, intensity A: Double, hold: Double) -> LivelyPose {
        typealias L = LivelyAction
        var pose = LivelyPose()
        guard t > 0, t < D else { return pose }
        var kick: Double = 0
        var smear: Double = 0
        for (k, burst) in L.glitchBursts(duration: D).enumerated() {
            let start: Double = Double(burst.first) / L.glitchSteps - 0.02
            let width: Double = Double(burst.last + 1 - burst.first) / L.glitchSteps + 0.06
            let b: Double = L.bump(t - start, width) * burst.strength
            let side: Double = k % 2 == 0 ? 1 : -1
            kick += side * b
            smear += b
        }
        let rest: Double = L.rest(t, D)
        let dx: Double = 0.04 * A * kick * rest
        let wide: Double = 0.05 * A * smear * rest
        let flat: Double = 0.02 * A * smear * rest
        pose.dx = dx
        pose.sx = 1 + wide
        pose.sy = 1 - flat
        pose.anchorY = 0.5
        return pose
    }
}

@available(macOS 15.0, *)
extension LivelyGlyphRenderer {
    /// Bands across the word in a burst step.
    private static let glitchBandCount = 5

    /// One band of a burst step: where it is (y, across the whole word, so
    /// every letter is cut the same) and how far it jumps (points).
    private struct GlitchBand {
        var top: CGFloat
        var bottom: CGFloat
        var shift: CGFloat
    }

    /// A burst step's bands: five, with jittered edges; one pushed well
    /// sideways, maybe one or two more nudged, the rest left in place (on a
    /// burst's last step all stay). A pure function of the step.
    private func glitchBands(step: Int, strength: Double, word: CGRect, settling: Bool) -> [GlitchBand] {
        typealias L = LivelyAction
        let seed = state.seed
        let A: Double = state.intensity
        let size: Double = Double(state.size)
        let n = Self.glitchBandCount
        let far: CGFloat = word.height * 4 + state.size * 4
        var edges: [CGFloat] = [word.minY - far]
        for j in 1..<n {
            let jitter: Double = 0.8 * (L.rnd(seed, step, 10 + j) - 0.5)
            let u: Double = (Double(j) + jitter) / Double(n)
            edges.append(word.minY + word.height * CGFloat(u))
        }
        edges.append(word.maxY + far)
        let forced = Int(L.rnd(seed, step, 30) * Double(n)) % n
        var bands: [GlitchBand] = []
        for j in 0..<n {
            let big = j == forced
            let moved = !settling && (big || L.rnd(seed, step, 20 + j) < 0.3)
            var shift: CGFloat = 0
            if moved {
                let r: Double = L.rnd(seed, step, 50 + j)
                let side: Double = L.rnd(seed, step, 40 + j) < 0.5 ? -1 : 1
                let bigEms: Double = 0.12 + 0.16 * r
                let smallEms: Double = 0.03 + 0.07 * r
                let ems: Double = big ? bigEms : smallEms
                shift = CGFloat(side * ems * A * strength * size)
            }
            bands.append(GlitchBand(top: edges[j], bottom: edges[j + 1], shift: shift))
        }
        return bands
    }

    /// Draws the part of a letter inside `clip`, moved `shift` sideways,
    /// with a red ghost `split` to one side and a cyan one to the other.
    private func glitchPiece(_ g: Glyph, clip: Path, shift: CGFloat, split: CGFloat, ghost: Float,
                             in context: GraphicsContext) {
        var c = context
        // Hard (not antialiased) edges: neighbouring bands then share their
        // edge pixel for pixel, with no seam or doubled line where they meet.
        c.clip(to: clip, style: FillStyle(antialiased: false))
        if ghost > 0.004, split != 0 {
            var red = c
            red.translateBy(x: shift - split, y: 0)
            red.addFilter(.colorMatrix(Self.flat((1.0, 0.16, 0.3), alpha: ghost)))
            red.draw(g.slice)
            var cyan = c
            cyan.translateBy(x: shift + split, y: 0)
            cyan.addFilter(.colorMatrix(Self.flat((0.12, 0.9, 1.0), alpha: ghost)))
            cyan.draw(g.slice)
        }
        c.translateBy(x: shift, y: 0)
        c.draw(g.slice)
    }

    /// Glitch's per-letter part: outside a burst the letter as it is; in a
    /// burst step, cut into the step's bands, the moved ones drawn shifted
    /// with a strong colour split, the still ones with a faint one.
    func glitchGlyph(_ g: Glyph, count: Int, word: CGRect, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity
        guard A > 0.001, let burst = L.glitchBurst(t, duration: D) else {
            context.draw(g.slice)
            return
        }
        let step = L.glitchStep(t)
        let seed = state.seed
        let size: Double = Double(state.size)
        let s: Double = burst.strength
        let settling = L.glitchLastStep(step, duration: D)
        let bands = glitchBands(step: step, strength: s, word: word, settling: settling)
        let side: Double = L.rnd(seed, step, 60) < 0.5 ? -1 : 1
        let strongSplit = CGFloat(side * 0.045 * A * s * size)
        let faintEms: Double = settling ? 0.035 : 0.02
        let faintSplit = CGFloat(side * faintEms * A * s * size)
        let strongGhost = Float(min(0.85, 0.85 * A * s))
        let faintAlpha: Double = settling ? 0.6 : 0.45
        let faintGhost = Float(min(0.6, faintAlpha * A * s))
        // Wide enough for the furthest jump and any overhang.
        let margin: CGFloat = state.size * 1.5
        let left: CGFloat = g.rect.minX - margin
        let wide: CGFloat = g.rect.width + 2 * margin
        // The bands left in place, as one region (no seams between them).
        var still = Path()
        for band in bands where band.shift == 0 {
            still.addRect(CGRect(x: left, y: band.top, width: wide, height: band.bottom - band.top))
        }
        if !still.isEmpty {
            glitchPiece(g, clip: still, shift: 0, split: faintSplit, ghost: faintGhost, in: context)
        }
        for band in bands where band.shift != 0 {
            let rect = CGRect(x: left, y: band.top, width: wide, height: band.bottom - band.top)
            glitchPiece(g, clip: Path(rect), shift: band.shift, split: strongSplit, ghost: strongGhost, in: context)
        }
    }

    /// Drawn behind the letters, once per word: nothing (no sparks).
    func glitchBehind(word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {}

    /// Drawn in front of the letters, once per word: nothing (no flashes).
    func glitchFront(glyphs: [Glyph], word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {}
}
