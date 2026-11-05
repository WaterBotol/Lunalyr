import SwiftUI

/// "Cutout · Highlight": as each word is sung, a cutout tile presses in
/// behind it -- a quick ease-out from the word's leading edge, with no pop --
/// stays while the rest of the line is sung, and fades at the line end.
/// With Reduce Motion the tile only fades
/// in and out. A pure function of playback time, so it is the same at any
/// frame rate and after a seek.
enum LyricHighlightTiming {
    /// The press.
    static let press = 0.12
    /// The fade at the end of the line.
    static let fade = 0.30

    /// Previews may supply only words, without the enclosing lyric line.
    static func lineEnd(_ end: Double?, words: [LyricsFetcher.Word]) -> Double {
        max(end ?? -.infinity, words.map(\.end).max() ?? -.infinity)
    }

    struct Pose: Equatable {
        /// 0...1.
        var opacity: Double
        /// How far the tile has pressed in from the word's leading edge, 0...1.
        var width: Double

        static let hidden = Pose(opacity: 0, width: 0)
        var isVisible: Bool { opacity > 0.001 }
    }

    /// How much of the colour set on the tile the word shows while its tile
    /// is at `opacity`: all of it above `switchAt`, none below
    /// (`LyricHighlight.textSwitch`). The change takes about a frame of the
    /// fade: blended slowly, the two inks pass through the same grey as the
    /// half-faded tile behind them.
    static func textOpacity(_ opacity: Double, switchAt: Double) -> Double {
        Easing.smoothstep((opacity - switchAt) / switchWidth + 0.5)
    }
    static let switchWidth = 0.04

    static func pose(time: Double, onset: Double, release: Double, reduceMotion: Bool) -> Pose {
        let age = time - onset
        guard age.isFinite, age >= 0, time < release + fade else { return .hidden }
        let pressed = min(1, age / press)
        // Cubic ease-out: quick, and it never passes the word's width.
        let eased = 1 - pow(1 - pressed, 3)
        let incoming = reduceMotion ? Easing.smoothstep(pressed) : min(1, eased / 0.6)
        let gone = min(1, max(0, (time - release) / fade))
        let outgoing = (1 - gone) * (1 - gone)
        let opacity = incoming * outgoing
        guard opacity > 0.001 else { return .hidden }
        return Pose(opacity: opacity, width: reduceMotion ? 1 : 0.1 + 0.9 * eased)
    }
}

/// A cutout tile: flat, opaque paper with slightly rounded corners and the
/// small tilt Cutout's labels have, pressed in from the word's leading edge.
/// Cutout's labels and the tiles "Cutout · Highlight" presses behind sung
/// words are both drawn with it.
struct CutoutTile: View {
    let colour: Color
    /// How far it has pressed in, 0...1 (`RollingUnit.tileWidth` for labels).
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

    /// The tile's rectangle within a label of `size`, set at `pointSize`.
    func rect(in size: CGSize, pointSize: CGFloat, rightToLeft: Bool) -> CGRect {
        let reach = Self.reach.width * pointSize, seam = Self.seam * pointSize
        var start = CGFloat(from) * size.width - (from <= 0.001 ? reach : seam)
        var end = CGFloat(to) * size.width + (to >= 0.999 ? reach : seam)
        if rightToLeft { (start, end) = (size.width - end, size.width - start) }
        let height = Self.reach.height * pointSize
        return CGRect(x: start, y: -height, width: max(0, end - start), height: size.height + height * 2)
    }

    /// The part of `rect` the press has reached.
    func pressed(in size: CGSize, pointSize: CGFloat, rightToLeft: Bool) -> CGRect {
        var rect = rect(in: size, pointSize: pointSize, rightToLeft: rightToLeft)
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

    var body: some View {
        GeometryReader { proxy in
            ForEach(Array(marks.enumerated()), id: \.offset) { _, mark in
                let rect = mark.rect(in: proxy.size, pointSize: pointSize, rightToLeft: rightToLeft)
                CutoutTile(colour: colour, width: CGFloat(mark.pose.width), rightToLeft: rightToLeft)
                    .frame(width: rect.width, height: rect.height)
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

    var body: some View {
        GeometryReader { proxy in
            ForEach(Array(marks.enumerated()), id: \.offset) { _, mark in
                let rect = mark.pressed(in: proxy.size, pointSize: pointSize, rightToLeft: rightToLeft)
                Rectangle()
                    .frame(width: rect.width, height: rect.height)
                    .position(x: rect.midX, y: rect.midY)
                    .opacity(LyricHighlightTiming.textOpacity(mark.pose.opacity, switchAt: switchAt))
            }
        }
    }
}
