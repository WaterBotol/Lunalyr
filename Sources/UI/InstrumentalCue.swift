import SwiftUI

/// One frame of an instrumental break, for the thinking orb (`ThinkingOrb`).
struct InstrumentalCue {
    /// Seconds since the break began.
    let elapsed: Double
    let opacity: Double
    /// 0...1 through the break, or nil when its end is not known.
    let progress: Double?
    /// Seeds the orb's starting shape, so each break moves in its own way.
    var seed: UInt64 = 0x1D07_5EED
    /// Live or timing-derived intensity, 0...1: livelier music, a deeper breath.
    var energy: Double = 0
    /// 0...1: the arrival's ease (smoothstep over `WallpaperMotion.interludeIn`).
    var entering: Double = 1
    /// 0...1: the leave as the next line comes (over `interludeOut`).
    var leaving: Double = 0
}

extension VCVocalGap {
    func presence(at time: Double) -> Double {
        let enter = Easing.smoothstep((time - start) / 0.7)
        let leave = nextVocal >= 0 ? 1 - Easing.smoothstep((time - end + 0.65) / 0.65) : 1
        return min(enter, leave)
    }

    func instrumentalCue(at time: Double, previewLead: Double, energy: Double = 0) -> InstrumentalCue? {
        let elapsed = time - start
        let fadeIn = Easing.smoothstep((elapsed - WallpaperMotion.interludeInDelay) / WallpaperMotion.interludeIn)
        let knownEnd = end > start
        let cueEnd = end - (nextVocal >= 0 ? min(previewLead, max(0, end - start - 0.85)) : 0)
        // Leaves almost as gently as it came, not in a quarter second.
        let fadeOut = knownEnd ? Easing.smoothstep((cueEnd - time) / WallpaperMotion.interludeOut) : 1
        let opacity = min(fadeIn, fadeOut)
        let progress = knownEnd ? min(1, max(0, elapsed / max(0.01, cueEnd - start))) : nil
        let seed = LyricPhraseTiming.timeSeed(start) ^ 0x1D07_5EED
        return opacity > 0 ? InstrumentalCue(elapsed: elapsed, opacity: opacity, progress: progress,
                                             seed: seed, energy: energy, entering: fadeIn,
                                             leaving: 1 - fadeOut) : nil
    }
}
