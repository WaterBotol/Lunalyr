import SwiftUI

/// "Cutout · Highlight": as each word is sung, a cutout tile presses in
/// behind it -- a quick ease-out from the word's leading edge, with no pop --
/// stays while the rest of the line is sung, and leaves at the line end.
/// With Reduce Motion the tile only fades in and out. A pure function of
/// playback time, so it is the same at any frame rate and after a seek.
///
/// The exit. A highlight is one object, tile and text together, and it
/// leaves as one: over `exitDuration`, eased in and out (`exitProgress`),
/// the tile fades, shrinks a little about its centre (`exitScale`) and
/// softens (`exitBlur`), and the word on it eases back from the colour set
/// on the tile to its own over the same curve (`textOpacity` with
/// `exitTextBand`), never in one frame. Where the word itself leaves (the
/// flowing layouts, Portal), its tile is carried by the word's own exit
/// instead of fading on a clock of its own, so neither a bare box nor bare
/// text in the tile's colour is ever left behind. Reduce Motion keeps the
/// same curve as an opacity fade only: no shrink, no blur.
enum LyricHighlightTiming {
    /// The press.
    static let press = 0.12
    /// How long a highlight takes to leave.
    static let exitDuration = 0.42
    /// The hold windows count the whole exit (`LyricLayerCache.rollingWindow`).
    static var fade: Double { exitDuration }
    /// How much smaller the tile is when it has gone, about its centre.
    static let exitScale = 0.06
    /// How soft it is when it has gone, in ems of the word's type size.
    static let exitBlur = 0.06
    /// The span of tile opacity over which a leaving word's colour eases
    /// back to its own, centred on the ink's switch (`LyricHighlight
    /// .textSwitch`). The press keeps the narrow `switchWidth`.
    static let exitTextBand = 0.5

    /// Previews may supply only words, without the enclosing lyric line.
    static func lineEnd(_ end: Double?, words: [LyricsFetcher.Word]) -> Double {
        max(end ?? -.infinity, words.map(\.end).max() ?? -.infinity)
    }

    /// 0 until `age` reaches 0, 1 from `exitDuration`: smootherstep, so it
    /// leaves rest and arrives at gone with no step in speed or in pace.
    static func exitProgress(_ age: Double) -> Double {
        guard age.isFinite else { return age > 0 ? 1 : 0 }
        let x: Double = min(1, max(0, age / exitDuration))
        let inner: Double = x * (6 * x - 15) + 10
        let eased: Double = x * x * x * inner
        return min(1, max(0, eased))
    }

    struct Pose: Equatable {
        /// 0...1.
        var opacity: Double
        /// How far the tile has pressed in from the word's leading edge, 0...1.
        var width: Double
        /// 0 while held (or pressing in), 1 when gone (`exitProgress`).
        var leaving = 0.0
        /// The tile's scale about its centre: 1 but while leaving.
        var scale = 1.0
        /// The tile's blur, in ems: 0 but while leaving.
        var blur = 0.0

        static let hidden = Pose(opacity: 0, width: 0)
        var isVisible: Bool { opacity > 0.001 }

        /// How much of the colour set on the tile the word shows, with the
        /// ink's switch at `switchAt`: the press switches in about a frame;
        /// leaving, it eases back over `exitTextBand` of the same curve.
        func textOpacity(switchAt: Double) -> Double {
            LyricHighlightTiming.textOpacity(opacity, switchAt: switchAt,
                                             band: leaving > 0 ? exitTextBand : switchWidth)
        }

        /// A leaving pose for `progress` (0...1 of the exit) of a tile at
        /// full strength.
        static func leaving(_ progress: Double, reduceMotion: Bool) -> Pose {
            let e = min(1, max(0, progress))
            guard e > 0 else { return Pose(opacity: 1, width: 1) }
            return Pose(opacity: 1 - e, width: 1, leaving: e,
                        scale: reduceMotion ? 1 : 1 - exitScale * e,
                        blur: reduceMotion ? 0 : exitBlur * e)
        }
    }

    /// How much of the colour set on the tile the word shows while its tile
    /// is at `opacity`: all of it above `switchAt`, none below
    /// (`LyricHighlight.textSwitch`), blended over `band` either side. The
    /// press blends over about a frame (`switchWidth`): blended slowly, the
    /// two inks pass through the same grey as the half-faded tile behind
    /// them. Leaving, a wider band (`exitTextBand`) eases the colour back.
    /// The band is kept inside 0...1, so a whole tile always carries its
    /// own text and a gone one none, wherever the switch falls.
    static func textOpacity(_ opacity: Double, switchAt: Double, band: Double = switchWidth) -> Double {
        let band = min(1, max(band, 1e-6))
        let centre = min(max(switchAt, band / 2), 1 - band / 2)
        return Easing.smoothstep((opacity - centre) / band + 0.5)
    }
    static let switchWidth = 0.04

    /// `carried`: the word leaves on its own exit and takes its tile with
    /// it, so the tile holds until then (the flowing layouts).
    static func pose(time: Double, onset: Double, release: Double, reduceMotion: Bool,
                     carried: Bool = false) -> Pose {
        let age = time - onset
        let release = carried ? .infinity : release
        guard age.isFinite, age >= 0, time < release + exitDuration else { return .hidden }
        let pressed = min(1, age / press)
        // Cubic ease-out: quick, and it never passes the word's width.
        let eased = 1 - pow(1 - pressed, 3)
        let incoming = reduceMotion ? Easing.smoothstep(pressed) : min(1, eased / 0.6)
        let width = reduceMotion ? 1 : 0.1 + 0.9 * eased
        let gone = exitProgress(time - release)
        guard gone > 0 else { return Pose(opacity: incoming, width: width) }
        var pose = Pose.leaving(gone, reduceMotion: reduceMotion)
        pose.opacity *= incoming
        pose.width = width
        guard pose.opacity > 0.001 else { return .hidden }
        return pose
    }
}

/// A cutout tile: flat, opaque paper with slightly rounded corners and the
/// small tilt Cutout's labels have, pressed in from the word's leading edge.
/// Cutout's labels and the tiles "Cutout · Highlight" presses behind sung
/// words are both drawn with it.
struct CutoutTile: View {
    let colour: Color
    /// How far it has pressed in, 0...1 (a label is always whole: `RollingUnit.label`).
    var width: CGFloat = 1
    var rightToLeft = false
    @Environment(\.lyricReduceMotion) private var reduceMotion

    static let tilt = -1.2
    static let cornerRadius: CGFloat = 3

    var body: some View {
        RoundedRectangle(cornerRadius: Self.cornerRadius)
            .fill(colour)
            .scaleEffect(x: width, y: 1, anchor: rightToLeft ? .trailing : .leading)
            .rotationEffect(.degrees(reduceMotion ? 0 : Self.tilt))
    }
}

/// Where "Cutout · Highlight" tiles fall on a word or a run of glued words
/// in the layouts that set a whole line (Scattered, Constellation, Editorial,
/// Echo): each sung word of the run is a stretch of its label, by character.
struct LyricHighlightMark: Equatable {
    /// Along the label, 0...1 by character.
    var from: Double
    var to: Double
    var pose: LyricHighlightTiming.Pose

    /// Tiles reach this far past the ink of their word, in ems: within the
    /// clearance the layout keeps around every word (`ScatteredTypography
    /// .Run.inset`), so a tile never covers a neighbour. Where two words of
    /// one run meet, their tiles just overlap.
    static let reach = CGSize(width: 0.1, height: 0.05)
    static let seam: CGFloat = 0.04

    /// The tile's rectangle within a label of `size`, set at `pointSize`,
    /// taller by however far the letters reach past the line box (`ink`).
    func rect(in size: CGSize, pointSize: CGFloat, rightToLeft: Bool, ink: LyricInkReach = .none) -> CGRect {
        let reach = Self.reach.width * pointSize, seam = Self.seam * pointSize
        var start = CGFloat(from) * size.width - (from <= 0.001 ? reach : seam)
        var end = CGFloat(to) * size.width + (to >= 0.999 ? reach : seam)
        if rightToLeft { (start, end) = (size.width - end, size.width - start) }
        let height = Self.reach.height * pointSize
        let top = height + ink.top, bottom = height + ink.bottom
        return CGRect(x: start, y: -top, width: max(0, end - start), height: size.height + top + bottom)
    }

    /// The part of `rect` the press has reached.
    func pressed(in size: CGSize, pointSize: CGFloat, rightToLeft: Bool, ink: LyricInkReach = .none) -> CGRect {
        var rect = rect(in: size, pointSize: pointSize, rightToLeft: rightToLeft, ink: ink)
        let width = rect.width * CGFloat(pose.width)
        if rightToLeft { rect.origin.x += rect.width - width }
        rect.size.width = width
        return rect
    }
}

/// The tiles behind a label, from its marks.
struct LyricHighlightTiles: View {
    let marks: [LyricHighlightMark]
    let colour: Color
    let pointSize: CGFloat
    var rightToLeft = false
    /// How far the letters reach past the line box.
    var ink = LyricInkReach.none

    var body: some View {
        GeometryReader { proxy in
            ForEach(Array(marks.enumerated()), id: \.offset) { _, mark in
                let rect = mark.rect(in: proxy.size, pointSize: pointSize, rightToLeft: rightToLeft, ink: ink)
                CutoutTile(colour: colour, width: CGFloat(mark.pose.width), rightToLeft: rightToLeft)
                    .frame(width: rect.width, height: rect.height)
                    .scaleEffect(CGFloat(mark.pose.scale))
                    .blur(radius: CGFloat(mark.pose.blur) * pointSize)
                    .position(x: rect.midX, y: rect.midY)
                    .opacity(mark.pose.opacity)
            }
        }
        .allowsHitTesting(false)
    }
}

/// Where the label's tiles have pressed in, as a mask: the text there is
/// drawn in the colour set on the tile while the tile is strong enough to
/// carry it (`LyricHighlightTiming.textOpacity`).
struct LyricHighlightMask: View {
    let marks: [LyricHighlightMark]
    let pointSize: CGFloat
    var rightToLeft = false
    var switchAt = 0.4
    /// How far the letters reach past the line box.
    var ink = LyricInkReach.none

    var body: some View {
        GeometryReader { proxy in
            ForEach(Array(marks.enumerated()), id: \.offset) { _, mark in
                let rect = mark.pressed(in: proxy.size, pointSize: pointSize, rightToLeft: rightToLeft, ink: ink)
                Rectangle()
                    .frame(width: rect.width, height: rect.height)
                    .position(x: rect.midX, y: rect.midY)
                    .opacity(mark.pose.textOpacity(switchAt: switchAt))
            }
        }
    }
}

extension View {
    /// A highlighted word and its tile drawn as one layer, so they fade as
    /// one object: the tile never shows through the word, nor the word
    /// through a half-faded tile. Nothing changes for a word without one.
    @ViewBuilder
    func highlightLayer(_ on: Bool) -> some View {
        if on { compositingGroup() } else { self }
    }
}
