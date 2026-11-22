import SwiftUI

// MARK: - Stretch (a candidate action, `WordAction.candidateList`)
//
// The letter spacing opens slowly, the letters drifting apart from the
// word's centre as if it were being pulled long; held, the word keeps a
// faint elastic tension (a slow creep and a breath of give); let go, the
// letters close back together with a soft, slightly springy settle. The
// letters keep their size (a hair wider at most): only the gaps open. Not
// Grow (the whole word scales) nor Shrink (squeezed in, then a pop out).
//
// macOS 14 (and joined scripts) draw only the whole-word part: a gentle
// horizontal widen and back. From macOS 15 the per-letter part undoes that
// widen on each letter and places it itself, so the gaps open by a set
// number of ems whatever the word's width.

extension LivelyAction {
    /// Where the letters spread from: true the word's centre, false its first letter.
    fileprivate static let stretchFromCentre = true

    /// How far the word is pulled long, 0 at rest, about 1 while held:
    /// opened with a smootherstep over 0.85 s, a slow creep (+8 %) and a faint (±6 %)
    /// ~1 Hz give while held, and let go from `D − 0.82` s on a soft
    /// spring that closes ~16 % past (a touch tighter than at rest) and settles.
    static func stretchOpen(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        let letGo: Double = D - 0.82
        let open: Double = ss5((t - 0.03) / 0.85)
        // While held: a slow creep a little further, and a faint give.
        let heldIn: Double = ss5((t - 0.6) / 0.5)
        let heldOut: Double = 1 - ss5((t - (letGo - 0.3)) / 0.3)
        let held: Double = heldIn * heldOut
        let creep: Double = 0.08 * ss5((t - 0.7) / 1.4) * heldOut
        let phase: Double = 2 * Double.pi * 0.95 * (t - 0.6)
        let give: Double = 0.06 * sin(phase) * held
        let close: Double = spring(t - letGo, peak: 0.4, overshoot: 0.16)
        let pulled: Double = open * (1 + creep + give) * (1 - close)
        return pulled * rest(t, D)
    }

    /// The whole word's horizontal widen (all that macOS 14 shows), as a share.
    static func stretchWiden(_ open: Double, A: Double) -> Double { 0.07 * A * open }

    /// Each letter's own horizontal stretch at full pull, as a share (≤ 4 %).
    static func stretchLetter(_ open: Double, A: Double) -> Double { 0.03 * A * open }

    /// How far the end letters travel out (ems, each side, at full pull):
    /// 0.15 em a gap, capped at 0.30 em for words up to 8 letters, and less
    /// for longer ones.
    static func stretchReach(count: Int, A: Double) -> Double {
        let n: Double = Double(max(1, count))
        let byGaps: Double = 0.15 * (n - 1) / 2
        let shorten: Double = min(1, (8 / n).squareRoot())
        let cap: Double = 0.30 * shorten
        return A * min(byGaps, cap)
    }
}

extension LivelyPose {
    /// Stretch's whole-word part (also all that macOS 14 draws). Identity before
    /// 0 and from `D` on; everything scaled by `A`.
    static func stretch(t: Double, duration D: Double, intensity A: Double, hold: Double) -> LivelyPose {
        guard t > 0, t < D else { return .identity }
        typealias L = LivelyAction
        let open: Double = L.stretchOpen(t, duration: D)
        var pose = LivelyPose()
        pose.sx = 1 + L.stretchWiden(open, A: A)
        pose.glow = 0.2 * A * L.clamp01(open)
        return pose
    }
}

@available(macOS 15.0, *)
extension LivelyGlyphRenderer {
    /// Stretch's per-letter part: the whole word's widen undone on each
    /// letter, which is set out by its own spread instead (outer letters a
    /// beat behind the inner ones, so it reads as elastic).
    func stretchGlyph(_ g: Glyph, count: Int, word: CGRect, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t: Double = state.t, D: Double = state.duration, A: Double = state.intensity
        let size: Double = Double(state.size)
        guard count > 1 else { context.draw(g.slice); return }
        let half: Double = Double(count - 1) / 2
        let place: Double = Double(g.index) - half
        // −1 … 1 from the centre (or 0 … 2 from the first letter).
        let from: Double = L.stretchFromCentre ? place / half : Double(g.index) / half
        let lag: Double = 0.05 * min(1, abs(place) / half)
        let open: Double = L.stretchOpen(t - lag, duration: D)
        let reach: Double = L.stretchReach(count: count, A: A)
        let target: Double = from * reach * open * size
        // The whole word is widened by `widen` about its centre: undo it.
        let widen: Double = 1 + L.stretchWiden(L.stretchOpen(t, duration: D), A: A)
        let offset: Double = Double(g.rect.midX - word.midX)
        let dx: Double = (offset + target) / widen - offset
        let sx: Double = (1 + L.stretchLetter(open, A: A)) / widen
        var c = context
        let anchor = CGPoint(x: g.rect.midX, y: g.baseline)
        c.translateBy(x: anchor.x + CGFloat(dx), y: anchor.y)
        c.scaleBy(x: CGFloat(sx), y: 1)
        c.translateBy(x: -anchor.x, y: -anchor.y)
        c.draw(g.slice)
    }

    /// Drawn behind the letters, once per word.
    func stretchBehind(word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {}

    /// Drawn in front of the letters, once per word.
    func stretchFront(glyphs: [Glyph], word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {}
}
