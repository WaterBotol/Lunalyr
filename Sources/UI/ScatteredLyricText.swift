import SwiftUI

/// The graph measures this fixed slot, never the animated ink inside it.
private struct WordSlotLayout: Layout {
    let degrees: Double
    let arrival: WordArrival
    let pointSize: CGFloat
    func makeCache(subviews: Subviews) -> CGSize {
        subviews.first?.sizeThatFits(.unspecified) ?? .zero
    }
    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout CGSize) -> CGSize {
        let size = cache
        let box = vc_word_envelope(Double(size.width), Double(size.height), 1.38,
                                   abs(degrees) + (arrival == .orbit ? 14 : 0))
        return CGSize(width: CGFloat(box.width) + ((arrival == .orbit || arrival == .glide) ? pointSize * 0.85 : 0),
                      height: CGFloat(box.height) + ((arrival == .lift || arrival == .glide || arrival == .orbit) ? pointSize * 0.65 : 0))
    }
    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout CGSize) {
        subviews.first?.place(at: CGPoint(x: bounds.midX, y: bounds.midY), anchor: .center, proposal: .unspecified)
    }
}

struct WordEntrance: ViewModifier {
    let progress: Double
    let style: WordArrival
    let size: CGFloat
    let direction: Double
    let visibility: Double?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    init(progress: Double, style: WordArrival, size: CGFloat, direction: Double, visibility: Double? = nil) {
        self.progress = progress; self.style = style; self.size = size
        self.direction = direction; self.visibility = visibility
    }
    func body(content: Content) -> some View {
        let eased = reduceMotion ? 1 : vc_motion_ease(progress)
        let remaining = 1 - eased
        let blur = reduceMotion ? 0 : max(0, 1 - progress) * (style == .fade ? 9 : 2.5)
        content
            .opacity(visibility ?? progress)
            .blur(radius: blur)
            .scaleEffect(style == .pop && !reduceMotion ? 0.76 + 0.24 * eased : 1)
            .rotationEffect(.degrees(style == .orbit && !reduceMotion ? remaining * direction * 12 : 0))
            .rotation3DEffect(.degrees(style == .unfold && !reduceMotion ? -48 * remaining : 0),
                              axis: (x: 1, y: 0, z: 0), anchor: .bottom, perspective: 0.25)
            .offset(x: !reduceMotion && (style == .orbit || style == .glide)
                       ? CGFloat(remaining * direction) * size * (style == .glide ? 0.72 : 0.32) : 0,
                    y: !reduceMotion && (style == .lift || style == .glide || style == .orbit)
                       ? CGFloat(remaining) * size * (style == .lift ? 0.48 : 0.20) : 0)
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

    struct Row {
        var items: [(index: Int, size: CGSize)] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    struct RowKey: Equatable {
        let width: CGFloat
        let spacing: CGFloat
    }
    struct Cache {
        var sizes: [CGSize]
        var rowKey: RowKey?
        var rows: [Row] = []
    }
    func makeCache(subviews: Subviews) -> Cache {
        Cache(sizes: measuredSizes(subviews))
    }
    func updateCache(_ cache: inout Cache, subviews: Subviews) {
        let sizes = measuredSizes(subviews)
        if sizes != cache.sizes { cache = Cache(sizes: sizes) }
    }
    private func measuredSizes(_ subviews: Subviews) -> [CGSize] {
        subviews.map { subview in
            let natural = subview.sizeThatFits(.unspecified)
            let peak = subview[PeakScaleKey.self]
            let scatter = abs(subview[ScatterOffsetKey.self])
            return CGSize(width: natural.width * peak, height: natural.height * peak + scatter * 2)
        }
    }
    private func rows(maxWidth: CGFloat, cache: inout Cache) -> [Row] {
        let key = RowKey(width: maxWidth, spacing: spacing)
        if key == cache.rowKey { return cache.rows }
        var result: [Row] = []
        var current = Row()
        for (index, size) in cache.sizes.enumerated() {
            let projected = current.items.isEmpty ? size.width : current.width + spacing + size.width
            if projected > maxWidth, !current.items.isEmpty {
                result.append(current)
                current = Row()
            }
            current.width = current.items.isEmpty ? size.width : current.width + spacing + size.width
            current.height = max(current.height, size.height)
            current.items.append((index, size))
        }
        if !current.items.isEmpty { result.append(current) }
        cache.rowKey = key; cache.rows = result
        return result
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout Cache) -> CGSize {
        if cache.sizes.count != subviews.count { cache = makeCache(subviews: subviews) }
        let maxWidth = proposal.width ?? .infinity
        let laid = rows(maxWidth: maxWidth, cache: &cache)
        let height = laid.reduce(0) { $0 + $1.height } + lineSpacing * CGFloat(max(laid.count - 1, 0))
        let width = laid.map(\.width).max() ?? 0
        // Report an oversized word honestly so the outer stage can fit it;
        // claiming the proposed width hid overflow from the fitting pass.
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout Cache) {
        if cache.sizes.count != subviews.count { cache = makeCache(subviews: subviews) }
        let laid = rows(maxWidth: proposal.width ?? bounds.width, cache: &cache)
        var y = bounds.minY
        for row in laid {
            var x: CGFloat
            switch rowAlignment {
            case .leading: x = bounds.minX
            case .center: x = bounds.midX - row.width / 2
            case .trailing: x = bounds.maxX - row.width
            }
            for item in row.items {
                // Centred inside its reserved slot, so growth expands into
                // that reserved room rather than over the next word.
                subviews[item.index].place(
                    at: CGPoint(x: x + item.size.width / 2, y: y + row.height / 2),
                    anchor: .center,
                    proposal: .unspecified
                )
                x += item.size.width + spacing
            }
            y += row.height + lineSpacing
        }
    }
}

/// A dragged-out word, set glyph by glyph. Each letter lights and swells as
/// the sweep reaches it, so a held syllable visibly grows across its own
/// length rather than inflating all at once.
private struct HeldWordText: View {
    let characters: [String]
    let font: Font
    /// Needed to reserve room for each glyph's growth.
    let pointSize: CGFloat
    let progress: Double
    let sustain: Double
    let glow: Double

    /// Peak growth of a single letter. Kept small: the clearance it needs
    /// shows up as letter-spacing, and too much of it makes a held word read
    /// as a different typeface from the rest of the line.
    private static let letterScale: CGFloat = 0.07

    var body: some View {
        HStack(spacing: 0) {
            ForEach(Array(characters.enumerated()), id: \.offset) { index, character in
                let reach = progress * Double(characters.count) - Double(index)
                let lit = Easing.smoothstep(reach)
                Text(character)
                    .font(font)
                    .foregroundStyle(.white.opacity(0.28 + 0.72 * lit))
                    // Each glyph swells about its own centre, so it needs
                    // half its growth as clearance on either side -- without
                    // this the letters of a held word ride over each other.
                    .padding(.horizontal, pointSize * 0.02)
                    .scaleEffect(1 + Self.letterScale * lit * sustain, anchor: .bottom)
                    .shadow(color: .white.opacity(0.55 * glow * lit), radius: 6 + 18 * glow * lit)
            }
        }
    }
}

enum Easing {
    /// Classic 3x²-2x³ ease, clamped. Used wherever motion is driven from a
    /// time value instead of a SwiftUI animation.
    static func smoothstep(_ x: Double) -> Double {
        let clamped = min(max(x, 0), 1)
        return clamped * clamped * (3 - 2 * clamped)
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

    @StateObject private var typography = RenderMemo<ScatteredTypography.Key, [ScatteredTypography.Word]>()

    var body: some View {
        let settings = Settings.shared
        let key = ScatteredTypography.Key(words: words, baseSize: baseSize, style: style, mood: mood,
            composition: composition, spread: settings.constellationSpread,
            fontIdentity: LyricFontIdentity(settings: settings))
        let prepared = typography.value(for: key) { ScatteredTypography.prepare(key, settings: settings) }
        let fullness = min(Double(prepared.count) / 7, 1)
        let nextIndex = prepared.firstIndex { $0.source.start > time }
        let frames = prepared.map {
            ScatteredWordFrame.make($0, nextIndex: nextIndex, time: time, composition: composition, energy: energy)
        }
        // Gaps come from a fixed inset on each word (below), not from
        // reserving a share of its width -- proportional reservation made a
        // wide word claim a wide margin and a narrow one almost none, so the
        // spacing visibly changed from word to word.
        FlowLayout(
            spacing: baseSize * (composition.hasConnections ? 0.06 : 0.02),
            // The web needs vertical room to scatter into; without it the
            // words collide with the rows above and below. Short lines get
            // less of it -- see `fullness`.
            lineSpacing: baseSize * (composition.hasConnections
                                     ? 0.34 + 0.96 * CGFloat(fullness) : 0.30),
            rowAlignment: style.rowAlignment
        ) {
            ForEach(prepared, id: \.index) { word in
                wordView(word, frame: frames[word.index])
            }
        }
        .backgroundPreferenceValue(WordAnchorKey.self) { anchors in
            if composition.hasConnections {
                ConstellationLines(
                    anchors: anchors,
                    count: prepared.count,
                    seed: seed,
                    visibility: frames.map { $0.presentation.opacity },
                    emphasis: frames.map(\.heat)
                )
            }
        }
    }

    /// One word, set and lit.
    ///
    /// Kept as its own builder rather than inline in the layout: with the
    /// composition branches folded in, the single expression exceeded what
    /// the type-checker will solve in reasonable time.
    private func wordView(_ token: ScatteredTypography.Word, frame: ScatteredWordFrame) -> some View {
        let index = token.index
        return ScatteredWordInk(token: token, frame: frame,
            arrival: Settings.shared.arrival(in: composition).resolved(for: composition))
        .equatable()
        .padding(.horizontal, baseSize * 0.10)
        .background {
            Color.clear.anchorPreference(key: WordAnchorKey.self, value: .bounds) { anchor in
                composition.hasConnections ? [index: anchor] : [:]
            }
        }
        .offset(y: token.metrics.waveOffset)
        .zIndex(frame.heat > 0.05 ? 1 : 0)
        .layoutValue(key: ScatterOffsetKey.self, value: token.metrics.waveOffset)
    }


}


struct ScatteredWordFrame: Equatable {
    let presentation: WordPresentation
    let heat: Double
    let fill: Double
    let glow: Double
    let driftX: Double
    let driftY: Double
    let driftScale: Double
    let driftAngle: Double
    @MainActor
    static func make(_ token: ScatteredTypography.Word, nextIndex: Int?, time: Double,
                     composition: LyricComposition, energy: Double = 0) -> ScatteredWordFrame {
        let word = token.source
        let presentation = WordPresentation.make(word: word, index: token.index, nextIndex: nextIndex,
                                                  time: time, composition: composition)
        let heat: Double
        if time < word.start - 0.1 { heat = 0 }
        else if time < word.start { heat = Easing.smoothstep((time - word.start + 0.1) / 0.1) }
        else if time <= word.end { heat = 1 }
        else { heat = 1 - Easing.smoothstep((time - word.end) / 0.3) }
        let progress = min(max((time - word.start) / max(word.end - word.start, 0.001), 0), 1)
        let fill = Settings.shared.revealsWords(in: composition) ? 1 : progress
        let glow = token.sustain > 0.01 && heat > 0.01
            ? heat * token.sustain * Easing.smoothstep(progress / 0.45) : 0
        let pose = vc_vocal_gesture(time - word.start, word.end - word.start, token.motionSeed, energy, 0)
        return .init(presentation: presentation, heat: heat, fill: fill, glow: glow,
                     driftX: pose.x, driftY: pose.y, driftScale: pose.scale, driftAngle: pose.angle)
    }
}

/// Settled and not-yet-arriving words have identical frame values. SwiftUI
/// can retain their glyph trees while another word animates.
private struct ScatteredWordInk: View, Equatable {
    let token: ScatteredTypography.Word
    let frame: ScatteredWordFrame
    let arrival: WordArrival
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.token == rhs.token && lhs.frame == rhs.frame && lhs.arrival == rhs.arrival
    }
    var body: some View {
        WordSlotLayout(degrees: token.metrics.rotation, arrival: arrival, pointSize: token.metrics.size) {
            Group {
                if token.sustain > 0.02 {
                    HeldWordText(characters: token.characters, font: token.font, pointSize: token.metrics.size,
                                 progress: frame.fill, sustain: token.sustain, glow: frame.glow)
                } else {
                    ZStack {
                        Text(token.label).font(token.font).foregroundStyle(.white.opacity(0.28))
                        Text(token.label).font(token.font).foregroundStyle(.white)
                            .mask(sweepMask(progress: frame.fill))
                    }
                }
            }
            .rotationEffect(.degrees(token.metrics.rotation))
            .scaleEffect(1 + 0.07 * CGFloat(frame.heat) + 0.03 * CGFloat(frame.glow))
            .scaleEffect(reduceMotion ? 1 : frame.driftScale)
            .rotationEffect(.degrees(reduceMotion ? 0 : frame.driftAngle))
            .offset(x: reduceMotion ? 0 : CGFloat(frame.driftX) * token.metrics.size,
                    y: reduceMotion ? 0 : CGFloat(frame.driftY) * token.metrics.size)
            .modifier(WordEntrance(progress: frame.presentation.progress, style: arrival,
                size: token.metrics.size, direction: token.index.isMultiple(of: 2) ? 1 : -1,
                visibility: frame.presentation.opacity))
            .shadow(color: .white.opacity(0.45 * frame.glow * frame.fill), radius: 10 + 22 * frame.glow)
            .shadow(color: .black.opacity(0.28 + 0.24 * frame.heat), radius: 7 + 12 * frame.heat, y: 2)
        }
    }
    private func sweepMask(progress: Double) -> some View {
        let clamped = min(max(progress, 0), 1)
        return LinearGradient(stops: [
            .init(color: .black, location: 0),
            .init(color: .black, location: max(clamped - 0.12, 0)),
            .init(color: .clear, location: clamped),
            .init(color: .clear, location: 1),
        ], startPoint: .leading, endPoint: .trailing)
    }
}
