import CoreGraphics

/// The shade NowPlayingView lays over a generated wallpaper so white lyrics
/// stay readable: a scrim down the whole screen, and a soft ellipse of extra
/// shade behind the lyric stage while it is in use (the text backing, whose
/// amount LyricLegibility works out). The render harness
/// (Tests/Swift/Support) composites the same values over its frames.
enum WallpaperShade {
    /// The scrim's black at the top, the middle and the bottom of the screen
    /// (evenly spaced stops) at Darkening 1. Each scales with
    /// `Settings.scrimStrength`.
    static let scrim: [Double] = [0.34, 0.12, 0.50]

    /// The scrim where the lyrics sit: the middle of the screen, where it is
    /// lightest.
    static func scrimAtStage(strength: Double) -> Double { min(1, scrim[1] * strength) }

    /// The text backing is drawn only above this amount.
    static let minimumBacking = 0.004
    /// The text backing's stops: how far out along the ellipse (0 at its
    /// centre, 1 at its edge), and what share of the backing amount.
    static let backingStops: [(location: Double, share: Double)] = [(0, 1), (0.55, 0.9), (0.8, 0.45), (1, 0)]
    /// The ellipse's frame as a multiple of the lyric stage
    /// (`LyricViewportMetrics.stageSize`), centred on the screen.
    static let backingSpan = CGSize(width: 1.3, height: 1.4)
    /// Where the gradient ends, as a fraction of that frame: at its edges.
    static let backingRadius: CGFloat = 0.5
}
