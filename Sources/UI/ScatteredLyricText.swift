import SwiftUI

/// The graph measures this fixed slot, never the animated ink inside it.
///
/// The slot is the ink's own rotated box plus a fixed inset. It used to
/// reserve 38% for the word's swell and the full travel of its entrance,
/// which spread words 0.7-1.6 em apart and left constellation links ending
/// well short of the letters. Growth is now capped to the inset instead
/// (`CappedGrowth`), and an entrance may pass briefly through the gap.
private struct WordSlotLayout: Layout {
    let degrees: Double
    let inset: CGSize
    func makeCache(subviews: Subviews) -> CGSize {
        subviews.first?.sizeThatFits(.unspecified) ?? .zero
    }
    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout CGSize) -> CGSize {
        let size = cache
        let box = vc_word_envelope(Double(size.width), Double(size.height), 1, abs(degrees) + 0.5)
        return CGSize(width: CGFloat(box.width) + inset.width * 2, height: CGFloat(box.height) + inset.height * 2)
    }
    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout CGSize) {
        subviews.first?.place(at: CGPoint(x: bounds.midX, y: bounds.midY), anchor: .center, proposal: .unspecified)
    }
}

/// Lets a word swell only as far as its inset allows, so emphasis never
/// pushes it into its neighbours. Short words keep their full swell; long
/// ones still get a little.
private struct CappedGrowth: ViewModifier {
    let scale: CGFloat
    let inset: CGFloat
    func body(content: Content) -> some View {
        content.visualEffect { ink, geometry in
            let cap = 1 + max(0.06, 1.8 * inset / max(1, geometry.size.width))
            return ink.scaleEffect(min(scale, cap))
        }
    }
}

/// A glow only where there is one: a clear shadow still costs a pass.
private struct OptionalGlow: ViewModifier {
    let colour: Color
    let strength: Double
    let radius: CGFloat
    func body(content: Content) -> some View {
        if strength > 0.002 {
            content.shadow(color: colour.opacity(strength), radius: radius)
        } else {
            content
        }
    }
}

/// Inside a glued run, each token appears at its own onset: its share of
/// the label (where its letters fall, `LyricRuns.boundaries`) takes the
/// token's own opacity.
struct TokenReveal: ViewModifier {
    let boundaries: [Double]
    let opacities: [Double]?
    let rightToLeft: Bool
    func body(content: Content) -> some View {
        if let opacities, opacities.count + 1 == boundaries.count {
            // The mask reaches well past the label on every side, carrying
            // its end parts' opacity: a face whose letters stand taller than
            // its line box (kana under a pixel face) or a letter that moves
            // must not lose its top or edge to the label's frame.
            let reach = LyricSweepMask.overreach
            let first = Color.black.opacity(min(1, max(0, opacities.first ?? 1)))
            let last = Color.black.opacity(min(1, max(0, opacities.last ?? 1)))
            content.mask {
                HStack(spacing: 0) {
                    (rightToLeft ? last : first).frame(width: reach)
                    GeometryReader { frame in
                        LinearGradient(stops: Self.stops(boundaries, opacities,
                                                         feather: Self.feather(width: frame.size.width)),
                                       startPoint: rightToLeft ? .trailing : .leading,
                                       endPoint: rightToLeft ? .leading : .trailing)
                    }
                    (rightToLeft ? first : last).frame(width: reach)
                }
                .padding(.horizontal, -reach)
                .padding(.vertical, -reach)
            }
        } else {
            content
        }
    }
    /// The soft edge between two parts, as a share of the label: one point
    /// either side of the boundary. It used to be 1.2 % of the
    /// label, which on a long unspaced line (a whole Japanese phrase) is
    /// 15-20 points and reached past the gap between two characters, so a
    /// slice of the next, still hidden character showed early.
    static let featherPoints: CGFloat = 1
    static func feather(width: CGFloat) -> Double {
        width > 1 ? min(0.012, Double(featherPoints / width)) : 0.012
    }

    static func stops(_ boundaries: [Double], _ opacities: [Double], feather: Double = 0.012) -> [Gradient.Stop] {
        var stops: [Gradient.Stop] = []
        for (index, opacity) in opacities.enumerated() {
            let colour = Color.black.opacity(min(1, max(0, opacity)))
            let from = index == 0 ? 0 : min(1, boundaries[index] + feather)
            let to = index == opacities.count - 1 ? 1 : max(from, boundaries[index + 1] - feather)
            stops.append(.init(color: colour, location: from))
            stops.append(.init(color: colour, location: to))
        }
        return stops
    }
}

struct WordEntrance: ViewModifier {
    let progress: Double
    let style: WordArrival
    let size: CGFloat
    let direction: Double
    let visibility: Double?
    /// The word's own spring. Travel, turn and scale follow it -- it can
    /// overshoot a little and settle, and its pace comes from the word's
    /// timing. Masks, blur and swipes keep the monotone `progress`.
    let arrival: Double?
    /// The largest entrance turn this word may make, in degrees. Long
    /// phrases turn less: 12 degrees at the ends of a wide label is a swing
    /// of several lines.
    let maxTurn: Double
    /// Word presence (`WordPresence`): 1 draws the entrance as it always
    /// was. Only the word-timed flow and rolling layouts pass more.
    let presence: Double
    /// A soft entrance (Soft focus) also rises and grows into place at a
    /// presence above 1; a rolling stage's faint preview does not.
    let softRise: Bool
    @Environment(\.lyricReduceMotion) private var reduceMotion
    @Environment(\.lyricSwipePalette) private var swipePalette
    init(progress: Double, style: WordArrival, size: CGFloat, direction: Double,
         visibility: Double? = nil, arrival: Double? = nil, maxTurn: Double = 12,
         presence: Double = 1, softRise: Bool = true) {
        self.progress = progress; self.style = style; self.size = size
        self.direction = direction; self.visibility = visibility; self.arrival = arrival
        self.maxTurn = maxTurn; self.presence = presence; self.softRise = softRise
    }
    /// Where an entrance has the word on this frame. Shared with layouts
    /// that must leave room for it (`RollingStack`), so the room reserved and
    /// the motion drawn can never disagree.
    struct Transform: Equatable {
        /// Travel still to cover, in points.
        var offset: CGSize = .zero
        /// Turn still to unwind, in degrees (signed).
        var turn: Double = 0
        var scale: CGFloat = 1
        /// Fold about the bottom edge, in degrees.
        var fold: Double = 0
        /// The 0...1 value the transforms follow.
        var eased: Double = 1
    }

    /// `presence` is the word presence strength (`WordPresence`): travel,
    /// turn and the pop's shrink grow by its entrance factor, and Soft
    /// focus, which has no travel of its own, rises and grows into place
    /// (unless `softRise` is false). At 1 this is the entrance as it was.
    static func transform(progress: Double, arrival: Double?, style: WordArrival, size: CGFloat,
                          direction: Double, reduceMotion: Bool, maxTurn: Double = 12,
                          presence: Double = 1, softRise: Bool = true) -> Transform {
        guard !reduceMotion else { return Transform() }
        let paper = style.isCutout
        let eased = paper || style.isSwipe ? progress : (arrival ?? vc_motion_ease(progress))
        let remaining = 1 - eased
        let strength = WordPresence.clamped(presence)
        let k = CGFloat(WordPresence.entrance(strength))
        var transform = Transform(
            offset: CGSize(width: style == .orbit || style == .glide || style == .cutoutSlide
                               ? CGFloat(remaining * direction) * size * (style == .orbit ? 0.32 : 0.72) * k : 0,
                           height: style == .lift || style == .glide || style == .orbit
                               ? CGFloat(remaining) * size * (style == .lift ? 0.48 : 0.20) * k : 0),
            turn: style == .orbit || style == .cutoutSlide
                ? remaining * direction * min(12, max(0, maxTurn)) * Double(k) : 0,
            // The pop's shrink, 0.24 of the word, grows by the entrance factor.
            scale: style == .pop || paper ? 0.76 + 0.24 * CGFloat(eased) - 0.24 * CGFloat(remaining) * (k - 1) : 1,
            fold: style == .unfold || style == .cutoutFold ? (paper ? -30 : -48) * remaining : 0,
            eased: eased)
        if style == .fade && softRise && strength > 1 {
            transform.offset.height += CGFloat(WordPresence.softRise(strength, eased: eased)) * size
            transform.scale *= CGFloat(WordPresence.softScale(strength, eased: eased))
        }
        return transform
    }

    @Environment(\.lyricLivelyGlyphs) private var lively

    func body(content: Content) -> some View {
        let paper = style.isCutout
        let motion = Self.transform(progress: progress, arrival: arrival, style: style, size: size,
                                    direction: direction, reduceMotion: reduceMotion, maxTurn: maxTurn,
                                    presence: presence, softRise: softRise)
        let blur = reduceMotion ? 0
            : max(0, 1 - progress) * (style == .fade ? 3.5 : 2.5) * WordPresence.entrance(presence)
        Group {
            if paper && motion.eased < 1 {
                content.mask {
                    // PREVIEW: a lively action word's letters may leave the
                    // word's box (hops, drips, dust) while the label still folds in.
                    let pad = (lively?.size ?? 0) * 2.5
                    Rectangle().scaleEffect(x: 0.88 + 0.12 * motion.eased, y: 1,
                        anchor: direction >= 0 ? .leading : .trailing)
                        .padding(.vertical, -pad)
                        .padding(direction >= 0 ? .leading : .trailing, -pad)
                }
            } else {
                content
            }
        }
            .opacity(visibility ?? progress)
            .blur(radius: blur)
            .overlay {
                if style.isSwipe && !reduceMotion && progress > 0 && progress < 1 {
                    LyricSwipe(style: style, progress: progress, direction: direction, palette: swipePalette)
                }
            }
            .scaleEffect(motion.scale)
            .rotationEffect(.degrees(motion.turn))
            .rotation3DEffect(.degrees(motion.fold), axis: (x: 1, y: 0, z: 0), anchor: .bottom, perspective: 0.25)
            .offset(motion.offset)
    }
}

/// How much a child may grow beyond its natural size. The layout reserves
/// that room up front, so a word swelling never lands on its neighbours.
struct ScatterOffsetKey: LayoutValueKey {
    static let defaultValue: CGFloat = 0
}

struct PeakScaleKey: LayoutValueKey {
    static let defaultValue: CGFloat = 1
}

/// Starts a new row of a `FlowLayout` at this child.
struct FlowRowBreakKey: LayoutValueKey {
    static let defaultValue = false
}

extension View {
    func peakScale(_ value: CGFloat) -> some View {
        layoutValue(key: PeakScaleKey.self, value: value)
    }
}

/// Wraps children into rows, aligned leading / centre / trailing. Row-level
/// alignment is what lets consecutive lyric lines sit differently on the
/// canvas instead of all stacking down the middle.
struct FlowLayout: Layout {
    enum RowAlignment: Equatable { case leading, center, trailing }

    var spacing: CGFloat = 12
    var lineSpacing: CGFloat = 10
    var rowAlignment: RowAlignment = .center
    var stageWidth: CGFloat?
    /// Arabic, Hebrew and other right-to-left lines read from the right:
    /// the first word sits rightmost and rows fill leftwards.
    var rightToLeft = false
    /// Evens out the rows: a line that needs two rows is parted near its
    /// middle rather than leaving one word alone on the second row.
    var balanced = false
    /// Per row, cycling, a sideways shift from the row's aligned place, in
    /// points. Applied only when the line takes more than one row, so the
    /// rows of a line step against each other.
    var rowShifts: [CGFloat] = []
    /// The narrowest the block may be: rows align inside this width, so a
    /// flush-left layout keeps one left edge from line to line.
    var fieldWidth: CGFloat?
    /// Constellation's fill (`vc_constellation_field`): each row's gaps
    /// widen towards `justifyWidth`, by at most `maxGap` each, and the rows
    /// part towards `fillHeight`, by at most `maxRowGap`.
    var justifyWidth: CGFloat?
    var maxGap: CGFloat = 0
    var fillHeight: CGFloat?
    var maxRowGap: CGFloat = 0

    struct Row {
        var items: [(index: Int, size: CGSize)] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    struct RowKey: Equatable {
        let width: CGFloat
        let spacing: CGFloat
        let balanced: Bool
    }
    struct Cache {
        var sizes: [CGSize]
        var breaks: [Bool] = []
        var rowKey: RowKey?
        var rows: [Row] = []
    }
    func makeCache(subviews: Subviews) -> Cache {
        Cache(sizes: measuredSizes(subviews), breaks: subviews.map { $0[FlowRowBreakKey.self] })
    }
    func updateCache(_ cache: inout Cache, subviews: Subviews) {
        let sizes = measuredSizes(subviews)
        let breaks = subviews.map { $0[FlowRowBreakKey.self] }
        if sizes != cache.sizes || breaks != cache.breaks { cache = Cache(sizes: sizes, breaks: breaks) }
    }
    private func measuredSizes(_ subviews: Subviews) -> [CGSize] {
        subviews.map { subview in
            let natural = subview.sizeThatFits(.unspecified)
            let peak = subview[PeakScaleKey.self]
            let scatter = abs(subview[ScatterOffsetKey.self])
            return CGSize(width: natural.width * peak, height: natural.height * peak + scatter * 2)
        }
    }
    private func wrap(_ sizes: [CGSize], breaks: [Bool], maxWidth: CGFloat) -> [Row] {
        var result: [Row] = []
        var current = Row()
        for (index, size) in sizes.enumerated() {
            let projected = current.items.isEmpty ? size.width : current.width + spacing + size.width
            let forced = breaks.indices.contains(index) && breaks[index]
            if (projected > maxWidth || forced), !current.items.isEmpty {
                result.append(current)
                current = Row()
            }
            current.width = current.items.isEmpty ? size.width : current.width + spacing + size.width
            current.height = max(current.height, size.height)
            current.items.append((index, size))
        }
        if !current.items.isEmpty { result.append(current) }
        return result
    }
    private func rows(maxWidth: CGFloat, cache: inout Cache) -> [Row] {
        let key = RowKey(width: maxWidth, spacing: spacing, balanced: balanced)
        if key == cache.rowKey { return cache.rows }
        var result = wrap(cache.sizes, breaks: cache.breaks, maxWidth: maxWidth)
        if balanced, result.count > 1, maxWidth.isFinite {
            // The narrowest measure that still takes no more rows.
            var low = cache.sizes.map(\.width).max() ?? 0, high = maxWidth
            for _ in 0..<14 where high - low > 1 {
                let middle = (low + high) / 2
                if wrap(cache.sizes, breaks: cache.breaks, maxWidth: middle).count > result.count {
                    low = middle
                } else {
                    high = middle
                }
            }
            result = wrap(cache.sizes, breaks: cache.breaks, maxWidth: high)
        }
        cache.rowKey = key; cache.rows = result
        return result
    }

    /// The gap between words in `row`, widened for the fill.
    func gap(in row: Row) -> CGFloat {
        guard let justifyWidth else { return spacing }
        return CGFloat(vc_justified_gap(Double(row.width), Int32(row.items.count), Double(spacing),
                                        Double(justifyWidth), Double(maxGap)))
    }
    func justifiedWidth(_ row: Row) -> CGFloat {
        row.width + (gap(in: row) - spacing) * CGFloat(max(row.items.count - 1, 0))
    }
    /// The gap between rows, parted for the fill.
    func rowGap(_ rows: [Row]) -> CGFloat {
        guard let fillHeight else { return lineSpacing }
        let content = rows.reduce(0) { $0 + $1.height } + lineSpacing * CGFloat(max(rows.count - 1, 0))
        return CGFloat(vc_fill_row_gap(Double(content), Int32(rows.count), Double(lineSpacing),
                                       Double(fillHeight), Double(maxRowGap)))
    }

    private func shift(row: Int, of count: Int) -> CGFloat {
        guard count > 1, !rowShifts.isEmpty else { return 0 }
        let value = rowShifts[row % rowShifts.count]
        return rightToLeft ? -value : value
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout Cache) -> CGSize {
        if cache.sizes.count != subviews.count { cache = makeCache(subviews: subviews) }
        let maxWidth = stageWidth ?? proposal.width ?? .infinity
        let laid = rows(maxWidth: maxWidth, cache: &cache)
        let height = laid.reduce(0) { $0 + $1.height } + rowGap(laid) * CGFloat(max(laid.count - 1, 0))
        var width: CGFloat = 0
        for (number, row) in laid.enumerated() {
            width = max(width, justifiedWidth(row) + 2 * abs(shift(row: number, of: laid.count)))
        }
        if let fieldWidth { width = max(width, min(fieldWidth, maxWidth)) }
        // Report an oversized word honestly so the outer stage can fit it;
        // claiming the proposed width hid overflow from the fitting pass.
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout Cache) {
        if cache.sizes.count != subviews.count { cache = makeCache(subviews: subviews) }
        let laid = rows(maxWidth: stageWidth ?? proposal.width ?? .infinity, cache: &cache)
        var y = bounds.minY
        let between = rowGap(laid)
        for (number, row) in laid.enumerated() {
            let wordGap = gap(in: row)
            let rowWidth = justifiedWidth(row)
            var x: CGFloat
            // A right-to-left line's "leading" edge is the right one.
            let alignment: RowAlignment = !rightToLeft ? rowAlignment
                : rowAlignment == .leading ? .trailing : rowAlignment == .trailing ? .leading : .center
            switch alignment {
            case .leading: x = bounds.minX
            case .center: x = bounds.midX - rowWidth / 2
            case .trailing: x = bounds.maxX - rowWidth
            }
            x += shift(row: number, of: laid.count)
            let count = row.items.count
            for position in 0..<count {
                let item = row.items[rightToLeft ? count - 1 - position : position]
                // Centred inside its reserved slot, so growth expands into
                // that reserved room rather than over the next word.
                subviews[item.index].place(
                    at: CGPoint(x: x + item.size.width / 2, y: y + row.height / 2),
                    anchor: .center,
                    proposal: .unspecified
                )
                x += item.size.width + wordGap
            }
            y += row.height + between
        }
    }
}

/// A soft-edged reveal that runs in the word's reading direction. The
/// feather travels past both ends, so 0 shows nothing and 1 shows every
/// glyph at full strength (the old mask left a sung word's last letters dim).
struct LyricSweepMask: View {
    let progress: Double
    var rightToLeft = false
    var feather = 0.12
    static let colourFeather = 0.055

    var body: some View {
        let reach = min(max(progress, 0), 1) * (1 + feather)
        LinearGradient(stops: [
            .init(color: .black, location: 0),
            .init(color: .black, location: min(1, max(reach - feather, 0))),
            .init(color: .clear, location: min(1, max(reach, 0))),
            .init(color: .clear, location: 1),
        ], startPoint: rightToLeft ? .trailing : .leading, endPoint: rightToLeft ? .leading : .trailing)
        // The sweep runs across only, so it reaches well above and below
        // the text's frame: a face whose letters stand taller than its own
        // line box (pixel fonts, some display faces) would otherwise keep
        // their tops in the unsung ink.
        .padding(.vertical, -Self.overreach)
    }

    /// How far past the top and bottom of the frame the sweep reaches.
    static let overreach: CGFloat = 400
}

/// The same sweep in an underline when contrast has made the fill white.
struct LyricSweepUnderline: ViewModifier {
    let colour: Color?
    let progress: Double
    let rightToLeft: Bool

    func body(content: Content) -> some View {
        content.overlay(alignment: .bottom) {
            if let colour, progress > 0 {
                Capsule()
                    .fill(colour)
                    .frame(height: 3)
                    .mask(LyricSweepMask(progress: progress, rightToLeft: rightToLeft, feather: LyricSweepMask.colourFeather))
                    .offset(y: 2)
                    .accessibilityHidden(true)
            }
        }
    }
}

/// A word with a karaoke fill and, when the ink sweeps, a colour fill.
/// One shaped `Text` per layer, so every script joins and orders correctly.
private struct SungText: View {
    let label: String
    let font: Font
    let fill: Double
    let tint: Double
    let rightToLeft: Bool
    let ink: LyricInkSample
    /// An action word's letters (`LivelyGlyphHook`): every layer moves them,
    /// the first draws the particles, and a sweep's mask reaches past the
    /// word while they move.
    @Environment(\.lyricLivelyGlyphs) private var lively

    var body: some View {
        let pad = (lively?.size ?? 0) * 2.5
        ZStack {
            if fill < 1 && !ink.sweeps {
                Text(label).font(font).foregroundStyle(ink.rest.opacity(ink.dim)).livelyLetters()
                Text(label).font(font).foregroundStyle(ink.rest).livelyLetters(particles: false)
                    .livelySweepMask(progress: fill, rightToLeft: rightToLeft, feather: 0.12, active: lively != nil, pad: pad)
            } else if !(ink.sweeps && tint >= 1) {
                Text(label).font(font).foregroundStyle(ink.rest).livelyLetters()
            }
            if ink.sweeps && tint >= 1 {
                Text(label).font(font).foregroundStyle(ink.sung).livelyLetters()
            } else if ink.sweeps && tint > 0 {
                Text(label).font(font).foregroundStyle(ink.sung).livelyLetters(particles: false)
                    .livelySweepMask(progress: tint, rightToLeft: rightToLeft, feather: LyricSweepMask.colourFeather,
                                     active: lively != nil, pad: pad)
            }
        }
        .modifier(LyricSweepUnderline(colour: ink.sweepUnderline, progress: tint, rightToLeft: rightToLeft))
    }
}

/// A dragged-out word. It lights and swells across its own length as the
/// sweep reaches each part of it, rather than inflating all at once.
///
/// The word is always set as one `Text`. Drawing a `Text` per character, as
/// this used to, cannot shape Arabic (letters lose their joined forms) and
/// lays right-to-left words out backwards. Where the script allows it, a
/// text renderer swells individual glyphs of the shaped line instead.
private struct HeldWordText: View {
    let label: String
    let font: Font
    /// Needed to reserve room for each glyph's growth.
    let pointSize: CGFloat
    let progress: Double
    let tint: Double
    let sustain: Double
    let glow: Double
    let rightToLeft: Bool
    let glyphMotion: Bool
    let ink: LyricInkSample
    @Environment(\.lyricReduceMotion) private var reduceMotion

    /// Peak growth of the whole word where letters cannot swell apart.
    static let letterScale: CGFloat = 0.07
    /// Peak growth of a single letter. Smaller, and with no added tracking:
    /// tracking would switch off ligatures and make a held word read as a
    /// different typeface; a touch of overlap at the peak is not visible.
    static let glyphScale: CGFloat = 0.05
    @Environment(\.lyricLivelyGlyphs) private var lively

    var body: some View {
        if #available(macOS 15.0, *), glyphMotion {
            // While an action moves the letters, its renderer (nearer the
            // text) takes over from the held swell.
            ZStack {
                Text(label).font(font).foregroundStyle(ink.rest)
                    .livelyLetters()
                    .textRenderer(HeldGlyphRenderer(progress: progress, sustain: reduceMotion ? 0 : sustain,
                        glow: glow, rightToLeft: rightToLeft, pointSize: pointSize, glowColour: ink.glow,
                        dim: ink.sweeps ? 1 : ink.dim))
                if ink.sweeps && tint > 0 {
                    Text(label).font(font).foregroundStyle(ink.sung)
                        .livelyLetters(particles: false)
                        .textRenderer(HeldGlyphRenderer(progress: progress, sustain: reduceMotion ? 0 : sustain,
                            glow: 0, rightToLeft: rightToLeft, pointSize: pointSize, glowColour: ink.glow, dim: 1))
                        .livelySweepMask(progress: tint, rightToLeft: rightToLeft, feather: LyricSweepMask.colourFeather,
                                         active: lively != nil, pad: (lively?.size ?? 0) * 2.5)
                }
            }
            .modifier(LyricSweepUnderline(colour: ink.sweepUnderline, progress: tint, rightToLeft: rightToLeft))
        } else {
            // Joined scripts, and macOS 14: the whole shaped word swells and
            // the light sweeps through it in reading order.
            SungText(label: label, font: font, fill: progress, tint: tint, rightToLeft: rightToLeft, ink: ink)
                .scaleEffect(reduceMotion ? 1 : 1 + Self.letterScale * sustain * Easing.smoothstep(progress), anchor: .bottom)
                .shadow(color: ink.glow.opacity(0.55 * glow * progress), radius: 6 + 18 * glow * progress)
        }
    }
}

/// Lights and swells each glyph of an already shaped line by how far the
/// sweep has travelled through it in reading order.
@available(macOS 15.0, *)
private struct HeldGlyphRenderer: TextRenderer {
    var progress: Double
    var sustain: Double
    var glow: Double
    var rightToLeft: Bool
    var pointSize: CGFloat
    var glowColour: Color
    var dim: Double = 0.28

    var displayPadding: EdgeInsets {
        let room = pointSize * 0.12 + 30
        return EdgeInsets(top: room, leading: room, bottom: room, trailing: room)
    }

    func draw(layout: Text.Layout, in context: inout GraphicsContext) {
        var minX = CGFloat.infinity, maxX = -CGFloat.infinity, glyphs = 0
        for line in layout {
            for run in line {
                for slice in run {
                    let rect = slice.typographicBounds.rect
                    minX = min(minX, rect.minX); maxX = max(maxX, rect.maxX); glyphs += 1
                }
            }
        }
        guard glyphs > 0, maxX > minX else {
            for line in layout { context.draw(line) }
            return
        }
        let width = maxX - minX, count = Double(glyphs)
        for line in layout {
            for run in line {
                for slice in run {
                    let bounds = slice.typographicBounds
                    let rect = bounds.rect
                    var along = Double((rect.midX - minX) / width)
                    if rightToLeft { along = 1 - along }
                    let lit = Easing.smoothstep((progress - along) * count + 0.5)
                    var glyph = context
                    let scale = 1 + HeldWordText.glyphScale * CGFloat(lit * sustain)
                    // Each glyph grows about its own foot on the baseline.
                    glyph.translateBy(x: rect.midX, y: bounds.origin.y)
                    glyph.scaleBy(x: scale, y: scale)
                    glyph.translateBy(x: -rect.midX, y: -bounds.origin.y)
                    if glow * lit > 0.01 {
                        glyph.addFilter(.shadow(color: glowColour.opacity(0.55 * glow * lit),
                                                radius: 6 + 18 * glow * lit))
                    }
                    glyph.opacity = dim + (1 - dim) * lit
                    glyph.draw(slice)
                }
            }
        }
    }
}

/// How a single lyric line is set. Cycling these between lines gives the
/// restless, re-composing quality of the Lyric Speaker display rather than a
/// fixed centred block.
struct LyricLineStyle: Equatable {
    var rowAlignment: FlowLayout.RowAlignment
    /// Amplitude of the undulating baseline, as a fraction of base size.
    var wave: CGFloat
    var rotationSpread: Double
    /// 0 = every word the same size, 1 = strong large/small contrast.
    var sizeContrast: CGFloat
    var uppercaseHeroes: Bool

    static func forLine(_ index: Int) -> LyricLineStyle {
        switch abs(index) % 5 {
        case 0:
            return .init(rowAlignment: .center, wave: 0.00, rotationSpread: 1.3, sizeContrast: 0.9, uppercaseHeroes: true)
        case 1:
            return .init(rowAlignment: .leading, wave: 0.09, rotationSpread: 0.5, sizeContrast: 0.45, uppercaseHeroes: false)
        case 2:
            return .init(rowAlignment: .center, wave: 0.15, rotationSpread: 2.1, sizeContrast: 1.0, uppercaseHeroes: true)
        case 3:
            return .init(rowAlignment: .trailing, wave: -0.10, rotationSpread: 0.9, sizeContrast: 0.6, uppercaseHeroes: false)
        default:
            return .init(rowAlignment: .center, wave: 0.06, rotationSpread: 0.0, sizeContrast: 0.25, uppercaseHeroes: true)
        }
    }
}

/// A lyric line as a typographic collage. Static per-word variation supplies
/// the texture; the word currently being sung is always rendered decisively
/// larger and brighter than any idle word, so the eye tracks the vocal.
@MainActor
struct ScatteredLyricText: View {
    let words: [LyricsFetcher.Word]
    var stageWidth: CGFloat?
    /// The stage's height, for Constellation's fill.
    var stageHeight: CGFloat?
    /// Current playback position. Emphasis is interpolated from this every
    /// frame rather than toggled on an index, which is what keeps words
    /// swelling smoothly instead of snapping between states.
    let time: TimeInterval
    var baseSize: CGFloat = 44
    var style: LyricLineStyle = .forLine(0)
    /// Supplies the typeface voice and how animated the layout feels.
    var mood: TrackMood = .neutral
    /// How the line is arranged. Timing and emphasis are unaffected -- only
    /// the arrangement changes.
    var composition: LyricComposition = .scattered
    /// Stable per line, so a composition draws the same figure each frame.
    var seed: Int = 0
    var energy: Double = 0
    var readings: [TimedWordKey: String] = [:]
    var wordTimed = true
    var lineEnd: Double?
    var phraseSweep = 0.0
    /// When the line starts to leave. Each word then exits on its own,
    /// staggered in reading order, instead of the block sliding away.
    var exitStart: Double = .infinity
    /// When this line's first word could first be previewed.
    var previewSince: Double = -.infinity
    /// Per word: continues the previous word's run (see `LyricRuns`).
    var glue: [Bool] = []
    /// Line-timed preview: only the first word, at this opacity, in the place
    /// it will take when the line arrives. Nil draws the whole line.
    var firstRunOnly: Double?
    /// A line-timed line whose first word was previewed: `firstRunOnly`
    /// then holds that word where the preview left it while the rest of the
    /// line makes its entrance (`LyricLayer.lineTimedLyric`), so it keeps
    /// its colour sweep and leaves its reading to the line.
    var heldFirstRun = false
    /// The other half of that: the line making its entrance without its
    /// first word's label, which `heldFirstRun` draws.
    var hidesFirstRun = false

    @SurfaceStyle private var surfaceStyle
    private var settings: Settings { surfaceStyle.settings }
    @StateObject private var typography = RenderMemo<ScatteredTypography.Key, ScatteredTypography.Layout>()
    @Environment(\.lyricReduceMotion) private var reduceMotion
    @Environment(\.lyricInk) private var ink
    @Environment(\.lyricWordAccents) private var accents
    @Environment(\.lyricWordPresence) private var wordPresence

    var body: some View {
        let key = ScatteredTypography.Key(words: words, baseSize: baseSize, style: style, mood: mood,
            composition: composition, spread: settings.constellationSpread,
            fontIdentity: LyricFontIdentity(settings: settings), readings: readings, wordTimed: wordTimed,
            lineEnd: lineEnd, glue: glue)
        let layout = typography.value(for: key) { ScatteredTypography.layout(key, settings: settings) }
        let prepared = layout.words
        let fullness = min(Double(layout.runs.count) / 7, 1)
        let sweepBounds = !wordTimed && ink.mode == .sungSweep && (firstRunOnly == nil || heldFirstRun)
            ? LyricRuns.boundaries(layout.runs.map(\.source)) : []
        let nextIndex = prepared.firstIndex { $0.source.start > time }
        // Motion follows the voice: timing alone tells dense delivery and
        // held notes apart, and any live energy supplied only adds to it.
        let intensity = wordTimed ? max(energy, OrganicMotion.vocalIntensity(words, at: time)) : 0
        let arrivals = layout.runs.map { settings.arrival(in: composition, for: $0.source) }
        // A highlighted word leaves with the rest of its line, and its tile
        // goes with it (`LyricHighlightTiming`, carried). Held back to the
        // line end instead, it still stood at full strength as the next
        // line arrived over it, and was cut off part-way out when the
        // line's end was the next line's start.
        let states: [ScatteredWordState] = wordTimed ? prepared.map {
            ScatteredWordFrame.state($0, nextIndex: nextIndex, time: time, composition: composition,
                energy: intensity, exitStart: exitStart, reduceMotion: reduceMotion, ink: ink,
                eligibleSince: $0.index > 0 ? prepared[$0.index - 1].source.start : previewSince, settings: settings)
        } : []
        let reveals = settings.revealsWords(in: composition)
        let frames = wordTimed ? layout.runs.map {
            ScatteredRunFrame.make($0, states: states, reveals: reveals, ink: ink,
                                   highlights: arrivals[$0.index].highlightsSungWords, time: time, reduceMotion: reduceMotion,
                                   carried: exitStart.isFinite)
        } : []
        // Settled words hold still while they are being read: the flow
        // layouts no longer float idle words about.
        let idle = 0.0
        // Sampled twelve times a second, so a settled word's layer is left
        // alone in between (the float itself is now still).
        let floatTime = (time * 12).rounded(.down) / 12
        // Gaps come from a fixed inset on each word's slot, not from
        // reserving a share of its width -- proportional reservation made a
        // wide word claim a wide margin and a narrow one almost none, so the
        // spacing visibly changed from word to word.
        let plan = FlowPlan.make(composition, style: style, wordTimed: wordTimed, fullness: fullness)
        let fill = ConstellationFill.field(composition: composition, wordTimed: wordTimed,
                                           stage: CGSize(width: stageWidth ?? 0, height: stageHeight ?? 0), baseSize: baseSize)
        FlowLayout(
            spacing: baseSize * plan.spacing,
            lineSpacing: baseSize * plan.lineSpacing,
            rowAlignment: plan.alignment,
            stageWidth: fill.map { CGFloat($0.measure) }
                ?? plan.measure.map { min(stageWidth ?? .infinity, $0 * baseSize) } ?? stageWidth,
            rightToLeft: layout.rightToLeft,
            balanced: plan.balanced,
            rowShifts: plan.rowShifts.map { $0 * baseSize },
            fieldWidth: plan.field.map { $0 * baseSize },
            justifyWidth: fill.map { CGFloat($0.target_width) },
            maxGap: fill.map { CGFloat($0.max_gap) } ?? 0,
            fillHeight: fill.map { CGFloat($0.target_height) },
            maxRowGap: fill.map { CGFloat($0.max_row_gap) } ?? 0
        ) {
            ForEach(layout.runs, id: \.index) { run in
                if wordTimed {
                    runView(run, frame: frames[run.index], arrival: arrivals[run.index].motion(in: composition),
                            tiles: arrivals[run.index].highlightsSungWords,
                            leave: states[run.tokens.lowerBound].leave, idle: idle, floatTime: floatTime,
                            accent: accents[prepared[run.tokens.lowerBound].source.presentationID],
                            onset: prepared[run.tokens.lowerBound].source.start)
                } else {
                    let colours = ink.sample(slot: run.order, seed: run.seed)
                    VStack(spacing: run.metrics.size * 0.04) {
                        SungText(label: run.label, font: run.font, fill: 1,
                            tint: Self.runSweep(phraseSweep, index: run.index, boundaries: sweepBounds),
                            rightToLeft: run.rightToLeft, ink: colours)
                            .fixedSize()
                            .opacity(hidesFirstRun && run.index == 0 ? 0 : 1)
                        run.reading?.tinted(colours.reading).opacity(firstRunOnly == nil ? 1 : 0)
                    }
                    .padding(.horizontal, run.inset.width)
                    // Line-timed text keeps the same dark shadow as sung words,
                    // so it stays legible over a light wallpaper.
                    .lyricShadow(0.32, radius: 8, y: 2)
                    .opacity(firstRunOnly.map { run.index == 0 ? $0 : 0 } ?? 1)
                }
            }
        }
        .backgroundPreferenceValue(WordAnchorKey.self) { anchors in
            if wordTimed && composition.hasConnections {
                ConstellationLines(
                    anchors: anchors,
                    count: layout.runs.count,
                    seed: seed,
                    visibility: zip(frames, layout.runs).map { $0.visible * (1 - states[$1.tokens.lowerBound].leave) },
                    emphasis: frames.map(\.heat),
                    colour: ink.link,
                    insets: layout.runs.map(\.inset)
                )
            }
        }
    }

    /// A line-level sweep keeps reading order even when the lyric wraps.
    static func runSweep(_ progress: Double, index: Int, boundaries: [Double]) -> Double {
        guard boundaries.indices.contains(index + 1) else { return 0 }
        let start = boundaries[index], end = boundaries[index + 1]
        return min(1, max(0, (progress - start) / max(0.001, end - start)))
    }

    /// One run (usually one word), set and lit.
    ///
    /// Kept as its own builder rather than inline in the layout: with the
    /// composition branches folded in, the single expression exceeded what
    /// the type-checker will solve in reasonable time.
    private func runView(_ run: ScatteredTypography.Run, frame: ScatteredRunFrame, arrival: WordArrival,
                         tiles: Bool = false, leave: Double,
                         idle: Double, floatTime: Double, accent: WordAccent? = nil, onset: Double = 0) -> some View {
        let index = run.index
        // Word presence: Reduce Motion keeps every word as it was.
        let presence = WordPresence.effective(wordPresence, reduceMotion: reduceMotion)
        // A word symbol fades in from the onset; a preview shows none.
        let symbolOpacity = frame.presentation.preview ? 0 : WordSymbolMark.opacity(elapsed: time - onset)
        // The float and the exit sit outside the equatable ink, so a settled
        // word's glyph tree is reused and only its layer moves. They also sit
        // inside the slot, so the constellation's anchors never drift.
        return ScatteredRunInk(run: run, frame: frame, arrival: arrival, tiles: tiles, settles: composition != .echo,
                               symbol: accent?.symbol, symbolOpacity: symbolOpacity,
                               symbolElapsed: frame.presentation.preview ? nil : WordSymbolMark.entering(elapsed: time - onset),
                               symbolAction: accent?.action, presence: presence)
            .equatable()
            // An action word's motion, over its entrance and inside its slot.
            .wordAction(accent, start: onset, end: run.source.end, time: time, size: run.metrics.size, glow: frame.ink.glow)
            .modifier(WordFloat(drift: vc_idle_drift(run.seed, floatTime, idle), leave: leave,
                                size: run.metrics.size, seed: run.seed, reduceMotion: reduceMotion,
                                presence: presence))
            .background {
                Color.clear.anchorPreference(key: WordAnchorKey.self, value: .bounds) { anchor in
                    composition.hasConnections ? [index: anchor] : [:]
                }
            }
            .offset(y: run.metrics.waveOffset)
            .zIndex(frame.heat > 0.05 ? 1 : 0)
            .layoutValue(key: ScatterOffsetKey.self, value: run.metrics.waveOffset)
            .layoutValue(key: FlowRowBreakKey.self, value: run.metrics.breakBefore)
    }
}

/// How a flow layout uses the stage: the measure it wraps at, its gaps, and
/// how its rows sit. Lengths are in ems of the line's base size, so a line
/// composes the same way on a small window and a wide display.
struct FlowPlan: Equatable {
    /// The spacing unit: 8 pt at the gallery's 46 pt base size.
    static let unit: CGFloat = 8.0 / 46.0

    var spacing: CGFloat = 0.02
    var lineSpacing: CGFloat = 0.30
    var alignment: FlowLayout.RowAlignment = .center
    /// Wrap width; nil wraps only at the stage's edge.
    var measure: CGFloat?
    var balanced = false
    var rowShifts: [CGFloat] = []
    var field: CGFloat?

    static func make(_ composition: LyricComposition, style: LyricLineStyle, wordTimed: Bool,
                     fullness: Double) -> FlowPlan {
        // A line sung as one phrase is set as one level sentence.
        guard wordTimed else { return FlowPlan(spacing: composition.hasConnections ? 0.06 : 0.02) }
        switch composition {
        case .constellation:
            // A wide field, rows stepping against each other, with real
            // gaps between the stars so the hairlines can be seen.
            let step = 6 * unit
            let shifts: [CGFloat] = style.rowAlignment == .trailing ? [step, -step] : [-step, step]
            return FlowPlan(spacing: 4 * unit, lineSpacing: (fullness < 0.6 ? 2 : 1) * unit, measure: 12,
                            balanced: true, rowShifts: shifts)
        case .echo:
            return FlowPlan(spacing: 0.02, lineSpacing: 0, measure: 13, balanced: true)
        default:
            // Scattered: a compact block whose rows step left and right of
            // each other, so the line breaks loose of a single baseline.
            let shifts: [CGFloat]
            switch style.rowAlignment {
            case .leading: shifts = [-9 * unit, 7 * unit, -2 * unit]
            case .trailing: shifts = [9 * unit, -7 * unit, 2 * unit]
            case .center: shifts = [-5 * unit, 9 * unit, -9 * unit]
            }
            return FlowPlan(spacing: 0.02, lineSpacing: 0, measure: 9, balanced: true, rowShifts: shifts)
        }
    }
}

/// A word's equatable frame plus the share of the line exit it has made.
struct ScatteredWordState {
    let frame: ScatteredWordFrame
    let leave: Double
}

/// One token's frame. Tokens are lit and timed individually; `ScatteredRunFrame`
/// gathers them into what their run shows.
struct ScatteredWordFrame: Equatable {
    let presentation: WordPresentation
    let heat: Double
    /// Karaoke fill: how much of the word is lit.
    let fill: Double
    /// Colour fill: how far through the sung word the ink's colour has run.
    let tint: Double
    let glow: Double
    let driftX: Double
    let driftY: Double
    let driftScale: Double
    let driftAngle: Double
    let ink: LyricInkSample

    @MainActor
    static func make(_ token: ScatteredTypography.Word, nextIndex: Int?, time: Double,
                     composition: LyricComposition, energy: Double = 0, settings: Settings = .shared) -> ScatteredWordFrame {
        state(token, nextIndex: nextIndex, time: time, composition: composition, energy: energy, settings: settings).frame
    }

    @MainActor
    static func state(_ token: ScatteredTypography.Word, nextIndex: Int?, time: Double,
                      composition: LyricComposition, energy: Double = 0, exitStart: Double = .infinity,
                      reduceMotion: Bool = false, ink: LyricInk = .white,
                      eligibleSince: Double = -.infinity, settings: Settings = .shared) -> ScatteredWordState {
        let word = token.source
        let motion = vc_word_motion(time, word.start, word.end, token.cadence, exitStart, token.order,
                                    token.seed, energy, reduceMotion ? 1 : 0)
        let presentation = WordPresentation.make(word: word, index: token.index, nextIndex: nextIndex,
                                                  time: time, composition: composition, motion: motion,
                                                  eligibleSince: eligibleSince, settings: settings)
        let progress = min(max((time - word.start) / max(word.end - word.start, 0.001), 0), 1)
        let fill = settings.revealsWords(in: composition) ? 1 : progress
        let heat = motion.heat
        let glow = token.sustain > 0.01 && heat > 0.01
            ? min(1, heat) * token.sustain * Easing.smoothstep(progress / 0.45) : 0
        let colours = ink.sample(slot: token.slot, seed: token.seed)
        let frame = ScatteredWordFrame(presentation: presentation, heat: heat, fill: fill,
            tint: colours.sweeps ? progress : 0, glow: glow,
            driftX: motion.x, driftY: motion.y, driftScale: motion.scale, driftAngle: motion.angle,
            ink: colours)
        return ScatteredWordState(frame: frame, leave: motion.leave)
    }
}

/// What one run shows on one frame, gathered from its tokens. A single
/// word passes straight through; a glued run lights by character share --
/// each token's fill falls on its own letters -- and swells with whichever
/// token is being sung.
struct ScatteredRunFrame: Equatable {
    let presentation: WordPresentation
    let heat: Double
    let fill: Double
    let tint: Double
    let glow: Double
    let driftX: Double
    let driftY: Double
    let driftScale: Double
    let driftAngle: Double
    let ink: LyricInkSample
    /// Per-token opacity while a glued run's tokens arrive one by one.
    let reveal: [Double]?
    /// How visible the run is at all, for the constellation's links.
    let visible: Double
    /// The reveal mask does not cover a glued run's reading, which comes
    /// with the run's first token, as a single word's does.
    var readingOpacity: Double { presentation.preview ? 0 : reveal?.first ?? 1 }
    /// Per token of a glued run, how far its share of the reading shows
    /// (`LyricReadingInk.shareOpacities`), relative to `readingOpacity`:
    /// the reading fills in with the run's syllables. Nil shows it whole.
    var readingReveal: [Double]? = nil
    /// "Cutout · Highlight": the tiles on this run's sung words and their
    /// colours, while any shows.
    var highlight: Highlight? = nil

    struct Highlight: Equatable {
        var marks: [LyricHighlightMark]
        var tile: Color
        var text: Color
        var textSwitch = 0.4
        /// The strongest tile, which mutes the word's glow under it.
        var strength: Double { marks.map(\.pose.opacity).max() ?? 0 }
    }

    /// How much the word in focus grows. With Reduce Motion nothing zooms:
    /// emphasis is carried by brightness and glow alone.
    /// At a word presence above 1 (`WordPresence.hold`) the swell's growth
    /// is stronger; `CappedGrowth` still caps it to the word's inset.
    static func swell(heat: Double, glow: Double, gesture: Double, reduceMotion: Bool,
                      presence: Double = 1) -> CGFloat {
        guard !reduceMotion else { return 1 }
        let swell = (1 + 0.07 * CGFloat(heat) + 0.03 * CGFloat(glow)) * CGFloat(gesture)
        return CGFloat(WordPresence.about1(Double(swell), WordPresence.hold(presence)))
    }

    @MainActor
    static func make(_ run: ScatteredTypography.Run, states: [ScatteredWordState], reveals: Bool,
                     ink: LyricInk, highlights: Bool = false, time: Double = 0,
                     reduceMotion: Bool = false, carried: Bool = false) -> ScatteredRunFrame {
        let first = states[run.tokens.lowerBound].frame
        let colours = ink.sample(slot: run.order, seed: run.seed)
        let highlight = highlights
            ? Self.highlight(run, ink: ink, time: time, reduceMotion: reduceMotion, carried: carried) : nil
        guard run.isGlued else {
            return ScatteredRunFrame(presentation: first.presentation, heat: first.heat, fill: first.fill,
                tint: first.tint, glow: first.glow, driftX: first.driftX, driftY: first.driftY,
                driftScale: first.driftScale, driftAngle: first.driftAngle, ink: colours, reveal: nil,
                visible: first.presentation.opacity, highlight: highlight)
        }
        let readingReveal = run.reading?.shareOpacities(at: time, syllables: reveals ? tokenOpacities(run, states) : nil,
                                                        reduceMotion: reduceMotion)
        var heat = 0.0, glow = 0.0, fill = 0.0, tint = 0.0, visible = 0.0
        var x = 0.0, y = 0.0, scale = 1.0, angle = 0.0
        var opacities: [Double] = []
        opacities.reserveCapacity(run.tokens.count)
        for (offset, index) in run.tokens.enumerated() {
            let frame = states[index].frame
            let share = run.boundaries[offset + 1] - run.boundaries[offset]
            heat = max(heat, frame.heat); glow = max(glow, frame.glow)
            fill += share * frame.fill; tint += share * frame.tint
            x += frame.driftX; y += frame.driftY; scale += frame.driftScale - 1; angle += frame.driftAngle
            opacities.append(frame.presentation.opacity)
            visible = max(visible, frame.presentation.opacity)
        }
        // The run enters with its first token; its tokens then appear
        // through the reveal mask, so the run itself is fully opaque.
        var presentation = first.presentation
        if reveals { presentation.opacity = 1 }
        return ScatteredRunFrame(presentation: presentation, heat: heat, fill: fill, tint: tint, glow: glow,
            driftX: x, driftY: y, driftScale: scale, driftAngle: angle, ink: colours,
            reveal: reveals ? opacities : nil, visible: visible, readingReveal: readingReveal, highlight: highlight)
    }

    /// Each token's opacity relative to the run's first, which the reading
    /// as a whole already takes (`readingOpacity`).
    private static func tokenOpacities(_ run: ScatteredTypography.Run, _ states: [ScatteredWordState]) -> [Double] {
        let first = max(states[run.tokens.lowerBound].frame.presentation.opacity, 0.001)
        return run.tokens.map { min(1, states[$0].frame.presentation.opacity / first) }
    }

    /// The run's tiles on this frame, or nil while none shows. `carried`:
    /// the line leaves (a finite `exitStart`), so each word's own exit
    /// takes its tile with it, tile and text as one, and the tile never
    /// fades on a clock of its own. A line that never leaves (a preview,
    /// a still) lets its tiles leave from the line end instead.
    static func highlight(_ run: ScatteredTypography.Run, ink: LyricInk, time: Double,
                          reduceMotion: Bool, carried: Bool = false) -> Highlight? {
        var marks: [LyricHighlightMark] = []
        for segment in run.segments {
            let pose = LyricHighlightTiming.pose(time: time, onset: segment.onset, release: segment.release,
                                                 reduceMotion: reduceMotion, carried: carried)
            if pose.isVisible { marks.append(LyricHighlightMark(from: segment.from, to: segment.to, pose: pose)) }
        }
        guard !marks.isEmpty else { return nil }
        return Highlight(marks: marks, tile: ink.tileColour, text: ink.tileText, textSwitch: ink.tileTextSwitch)
    }
}

/// Settled and not-yet-arriving runs have identical frame values. SwiftUI
/// can retain their glyph trees while another run animates.
private struct ScatteredRunInk: View, Equatable {
    let run: ScatteredTypography.Run
    let frame: ScatteredRunFrame
    let arrival: WordArrival
    /// "Cutout · Highlight": the word and its tile are drawn as one layer,
    /// so they fade as one object (the tile never shows through the word,
    /// nor the word through a half-faded tile).
    var tiles = false
    /// The calm layouts land without overshoot: the word's spring is held
    /// at its rest instead of passing it. Echo keeps its small overshoot.
    var settles = true
    /// The word's symbol (`WordAccentPlan`), set beside it from the first
    /// frame so the slot measures it, and shown at `symbolOpacity`.
    var symbol: String? = nil
    var symbolOpacity = 0.0
    /// PREVIEW: the symbol's entrance (`WordSymbolView.elapsed`, `.action`);
    /// nil once settled, so a settled word stays equal.
    var symbolElapsed: Double? = nil
    var symbolAction: WordAction? = nil
    /// Word presence (`WordPresence`), already 1 under Reduce Motion.
    var presence = 1.0
    @Environment(\.lyricReduceMotion) private var reduceMotion
    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.run == rhs.run && lhs.frame == rhs.frame && lhs.arrival == rhs.arrival && lhs.tiles == rhs.tiles
            && lhs.settles == rhs.settles
            && lhs.symbol == rhs.symbol && lhs.symbolOpacity == rhs.symbolOpacity && lhs.presence == rhs.presence
            && lhs.symbolElapsed == rhs.symbolElapsed && lhs.symbolAction == rhs.symbolAction
    }
    var body: some View {
        let size = run.metrics.size
        let tiled = frame.highlight?.strength ?? 0
        WordSlotLayout(degrees: run.metrics.rotation, inset: run.inset) {
            VStack(spacing: size * 0.04) {
                HStack(spacing: size * WordSymbolMark.gap) {
                    word(tiled: tiled).highlightLayer(tiles)
                    if let symbol {
                        WordSymbolView(symbol: symbol, size: size, rest: frame.ink.rest, sung: frame.ink.sung,
                                       tint: frame.ink.sweeps ? frame.tint : 0, opacity: symbolOpacity,
                                       elapsed: symbolElapsed, action: symbolAction)
                    }
                }
                // Word and symbol at their own size: a slot proposed only
                // the word's width must not wrap the word to fit the symbol.
                .fixedSize(horizontal: symbol != nil, vertical: false)
                run.reading?.revealing(frame.readingReveal).tinted(frame.ink.reading).opacity(frame.readingOpacity)
            }
            .modifier(Self.Motion(run: run, frame: frame, arrival: arrival, settles: settles, reduceMotion: reduceMotion,
                                  presence: presence))
        }
    }

    /// The word, its tile and its reveal.
    @ViewBuilder
    private func word(tiled: Double) -> some View {
        let size = run.metrics.size
        label(frame.ink, glow: frame.glow * (1 - tiled))
            .overlay {
                // On a tile the word takes the colour set on it, as
                // far as the tile has pressed in.
                if let highlight = frame.highlight {
                    label(LyricInkSample(rest: highlight.text, sung: highlight.text, glow: .clear,
                                         reading: highlight.text, sweeps: false, dim: frame.ink.dim), glow: 0)
                        .environment(\.lyricLivelyParticles, false)
                        .mask {
                            LyricHighlightMask(marks: highlight.marks, pointSize: size, rightToLeft: run.rightToLeft,
                                               switchAt: highlight.textSwitch, ink: run.inkReach)
                        }
                }
            }
            .modifier(TokenReveal(boundaries: run.boundaries, opacities: frame.reveal, rightToLeft: run.rightToLeft))
            .background {
                if let highlight = frame.highlight {
                    LyricHighlightTiles(marks: highlight.marks, colour: highlight.tile, pointSize: size,
                                        rightToLeft: run.rightToLeft, ink: run.inkReach)
                }
            }
    }

    /// The run's tilt, swell, drift, entrance, glow and shadow.
    private struct Motion: ViewModifier {
        let run: ScatteredTypography.Run
        let frame: ScatteredRunFrame
        let arrival: WordArrival
        let settles: Bool
        let reduceMotion: Bool
        /// Word presence (`WordPresence`): a stronger entrance, hold and glow.
        var presence = 1.0

        func body(content: Content) -> some View {
            let size = run.metrics.size
            let heat = CGFloat(min(1, frame.heat))
            let tiled = frame.highlight?.strength ?? 0
            let hold = WordPresence.hold(presence), glow = WordPresence.glow(presence)
            content
                .rotationEffect(.degrees(run.metrics.rotation))
                .modifier(CappedGrowth(scale: ScatteredRunFrame.swell(heat: frame.heat, glow: frame.glow,
                                       gesture: frame.driftScale, reduceMotion: reduceMotion, presence: presence),
                                       inset: run.inset.width))
                .rotationEffect(.degrees(reduceMotion ? 0 : frame.driftAngle * hold))
                .offset(x: reduceMotion ? 0 : CGFloat(frame.driftX * hold) * size,
                        y: reduceMotion ? 0 : CGFloat(frame.driftY * hold) * size)
                .modifier(WordEntrance(progress: frame.presentation.progress, style: arrival,
                    // Words come in along the reading direction: from the
                    // right, or from the left in a right-to-left line.
                    size: size, direction: run.rightToLeft ? -1 : 1,
                    visibility: frame.presentation.opacity,
                    arrival: settles ? min(1, frame.presentation.arrival) : frame.presentation.arrival,
                    presence: presence))
                .modifier(OptionalGlow(colour: frame.ink.glow,
                                       strength: 0.45 * frame.glow * frame.fill * (1 - tiled) * glow,
                                       radius: 10 + 22 * frame.glow * glow))
                .lyricShadow(0.28 + 0.24 * heat, radius: 7 + 12 * heat, y: 2)
        }
    }

    /// The word itself, in `ink`: lit glyph by glyph where it is held. A
    /// tile mutes the glow, which would haze the type set on it.
    @ViewBuilder
    private func label(_ ink: LyricInkSample, glow: Double) -> some View {
        if !run.isGlued && run.sustain > 0.02 {
            HeldWordText(label: run.label, font: run.font, pointSize: run.metrics.size,
                         progress: frame.fill, tint: frame.tint, sustain: run.sustain, glow: glow,
                         rightToLeft: run.rightToLeft, glyphMotion: run.glyphMotion, ink: ink)
        } else {
            SungText(label: run.label, font: run.font, fill: frame.fill, tint: frame.tint,
                     rightToLeft: run.rightToLeft, ink: ink)
        }
    }
}

/// Constellation spreads its stars over the stage at any aspect ratio
/// (16:9, 21:9, a small floating window) instead of drawing one compact
/// block: the maths is shared with Windows in `vc_constellation_field`.
enum ConstellationFill {
    /// For before-and-after renders in the tests only.
    nonisolated(unsafe) static var isEnabled = true

    /// Nil for other layouts, line-timed lines and an unknown stage.
    static func field(composition: LyricComposition, wordTimed: Bool, stage: CGSize,
                      baseSize: CGFloat) -> VCConstellationField? {
        guard isEnabled, composition == .constellation, wordTimed, stage.width > 0, stage.height > 0 else { return nil }
        let field = vc_constellation_field(Double(stage.width), Double(stage.height), Double(baseSize))
        return field.measure > 0 ? field : nil
    }
}
