import SwiftUI

// MARK: - Stretch (a candidate action, `WordAction.candidateList`)
//
// The word is pulled long: on an elastic pull (a spring that goes a touch
// past and back) it widens by about 30 % and thins a little, like taffy;
// the letters spread apart from its centre, the outer ones a beat behind;
// held, it keeps a faint elastic tension (a slow creep and a breath of
// give); let go, it snaps back with a springy settle a touch past rest.
// The letters themselves widen only 10 %: mostly the gaps open. Not Grow
// (the whole word scales) nor Shrink (squeezed in, then a pop out).
//
// macOS 14 (and joined scripts) draw only the whole-word part: the word
// itself widened and thinned. From macOS 15 the per-letter part undoes that
// widen on each letter and places it itself, at its share of the widened
// word (so a Cutout card, widened with the word, still holds every letter).

extension LivelyAction {
    /// How far the word is pulled long, 0 at rest, about 1 while held:
    /// opened on a spring (first peak at 0.45 s, 8 % past), a slow creep (+8 %) and a faint (±6 %)
    /// ~1 Hz give while held, and let go from `D − 0.82` s on a soft
    /// spring that closes ~16 % past (a touch tighter than at rest) and settles.
    static func stretchOpen(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        let letGo: Double = D - 0.82
        let open: Double = spring(t - 0.03, peak: 0.45, overshoot: 0.08)
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
    static func stretchWiden(_ open: Double, A: Double) -> Double { 0.3 * A * open }

    /// Each letter's own horizontal stretch at full pull, as a share.
    static func stretchLetter(_ open: Double, A: Double) -> Double { 0.1 * A * open }
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
        // Thinned a little as it is pulled long.
        pose.sy = 1 - 0.07 * A * open
        pose.glow = 0.2 * A * L.clamp01(open)
        return pose
    }
}

@available(macOS 15.0, *)
extension LivelyGlyphRenderer {
    /// Stretch's per-letter part: the whole word's widen undone on each
    /// letter, which is set at its own share of the widened word instead
    /// (outer letters a beat behind the inner ones, so it reads as elastic).
    func stretchGlyph(_ g: Glyph, count: Int, word: CGRect, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t: Double = state.t, D: Double = state.duration, A: Double = state.intensity
        guard count > 1 else { context.draw(g.slice); return }
        let half: Double = Double(count - 1) / 2
        let place: Double = Double(g.index) - half
        let lag: Double = 0.06 * min(1, abs(place) / half)
        let open: Double = L.stretchOpen(t - lag, duration: D)
        // The whole word is widened by `widen` about its centre: undo it,
        // and set the letter where its own (lagging) widen puts it.
        let widen: Double = 1 + L.stretchWiden(L.stretchOpen(t, duration: D), A: A)
        let own: Double = 1 + L.stretchWiden(open, A: A)
        let offset: Double = Double(g.rect.midX - word.midX)
        let dx: Double = offset * own / widen - offset
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
