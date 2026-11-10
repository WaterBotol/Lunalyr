import SwiftUI

struct InstrumentalCue {
    let elapsed: Double
    let opacity: Double
    let progress: Double?
    /// Seeds the dots' wander, so each break moves in its own way.
    var seed: UInt64 = 0x1D07_5EED
    /// Live or timing-derived intensity, 0...1: livelier music, livelier dots.
    var energy: Double = 0

    /// Vertical wander of one dot, -1...1. Each dot follows its own seeded
    /// noise over a shared slow swell, so the three drift like things
    /// floating rather than tracing a sine wave in lockstep.
    func bob(at index: Int) -> Double {
        let pace = 0.38 + 0.22 * min(1, max(0, energy))
        let own = vc_noise2(seed &+ UInt64(index) &* 0x9E37_79B9, elapsed * pace)
        let shared = vc_noise(seed ^ 0xA5A5, elapsed * 0.21)
        return min(1, max(-1, own * 0.8 + shared * 0.45))
    }

    /// A gentle swell in size, -1...1, out of phase with the bob.
    func breath(at index: Int) -> Double {
        vc_noise(seed ^ (0xB0 &+ UInt64(index)), elapsed * 0.27)
    }

    func highlight(at index: Int) -> Double {
        guard let progress else { return 0 }
        return Easing.smoothstep(progress * 3 - Double(index))
    }
}

extension VCVocalGap {
    func presence(at time: Double) -> Double {
        let enter = Easing.smoothstep((time - start) / 0.7)
        let leave = nextVocal >= 0 ? 1 - Easing.smoothstep((time - end + 0.65) / 0.65) : 1
        return min(enter, leave)
    }

    func instrumentalCue(at time: Double, previewLead: Double, energy: Double = 0) -> InstrumentalCue? {
        let elapsed = time - start
        let fadeIn = Easing.smoothstep((elapsed - 0.42) / 0.5)
        let knownEnd = end > start
        let cueEnd = end - (nextVocal >= 0 ? min(previewLead, max(0, end - start - 0.85)) : 0)
        let fadeOut = knownEnd ? Easing.smoothstep((cueEnd - time) / 0.24) : 1
        let opacity = min(fadeIn, fadeOut)
        let progress = knownEnd ? min(1, max(0, elapsed / max(0.01, cueEnd - start))) : nil
        let seed = LyricPhraseTiming.timeSeed(start) ^ 0x1D07_5EED
        return opacity > 0 ? InstrumentalCue(elapsed: elapsed, opacity: opacity, progress: progress,
                                             seed: seed, energy: energy) : nil
    }
}

/// Three centred dots show the length of the break without covering the scene.
struct InstrumentalInterludeView: View {
    let cue: InstrumentalCue
    let accent: Color
    let baseSize: CGFloat
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.lyricInk) private var ink

    var body: some View {
        // Scales with the lyric size, so the cue is not lost on a large display.
        let diameter = min(40, max(15, baseSize * 0.3))
        let colour = ink.dot ?? accent
        // The wander grows in with the cue, so the dots arrive still.
        let float = reduceMotion ? 0 : cue.opacity
        HStack(spacing: diameter * 0.65) {
            ForEach(0..<3) { index in
                let highlight = cue.highlight(at: index)
                Circle()
                    .fill(colour.opacity(0.32 + highlight * 0.62))
                    .overlay { Circle().strokeBorder(.white.opacity(highlight * 0.22), lineWidth: 1) }
                    .frame(width: diameter, height: diameter)
                    .scaleEffect(1 + CGFloat(float * cue.breath(at: index)) * 0.09)
                    .offset(y: CGFloat(float * cue.bob(at: index)) * diameter * 0.42)
            }
        }
        .opacity(cue.opacity)
        .accessibilityLabel("Instrumental break")
        .allowsHitTesting(false)
    }
}
