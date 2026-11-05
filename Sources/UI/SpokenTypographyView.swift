import SwiftUI

/// Fit the actual rotated ink around its intended centre. Font estimates
/// cannot bound wide glyphs, custom typefaces or the Cutout label padding.
enum WordStageFit {
    static func scale(ink: CGSize, stage: CGSize, centre: CGPoint,
                      scale: Double, angle: Double, margin: CGFloat) -> CGFloat {
        guard ink.width.isFinite, ink.height.isFinite, stage.width.isFinite, stage.height.isFinite,
              centre.x.isFinite, centre.y.isFinite, scale.isFinite, angle.isFinite, margin.isFinite,
              ink.width > 0, ink.height > 0, stage.width > 0, stage.height > 0, scale > 0 else { return 0 }
        let radians = angle * .pi / 180
        let cosine = CGFloat(abs(cos(radians))), sine = CGFloat(abs(sin(radians)))
        let rotatedWidth = ink.width * cosine + ink.height * sine
        let rotatedHeight = ink.height * cosine + ink.width * sine
        let inset = max(0, margin)
        let clearanceX = max(0, min(centre.x, stage.width - centre.x) - inset)
        let clearanceY = max(0, min(centre.y, stage.height - centre.y) - inset)
        return min(1, clearanceX * 2 / max(1, rotatedWidth * CGFloat(scale)),
            clearanceY * 2 / max(1, rotatedHeight * CGFloat(scale)))
    }

}

/// A free lyric stage. Onsets move the composition; held notes keep their
/// focus. Every exit fades while it is still inside the stage, with no mask.
@MainActor
struct SpokenTypographyView: View {
    let words: [LyricsFetcher.Word]
    let time: Double
    let baseSize: CGFloat
    let mood: TrackMood
    let composition: LyricComposition
    let seed: Int
    let accent: Color
    let energy: Double
    var readings: [TimedWordKey: String] = [:]
    /// The lyric stage's height. Rolling words are stacked by their real
    /// size, so the stage gives them its full height to stack into.
    var stageHeight: CGFloat?
    var stageHeightInTypeSizes: CGFloat = 7.2
    /// Room kept clear at the foot of the stage (for a translation line).
    var bottomInset: CGFloat = 0
    /// The onset of the first word after this window, so the window's last
    /// word keeps the same pace when that word joins it.
    var following: Double?
    /// Each word's place in the whole song, which keys its colour.
    var ordinals: [TimedWordKey: Int] = [:]
    /// The tokens each glued run is made of.
    var parts: [TimedWordKey: [LyricsFetcher.Word]] = [:]
    var lineEnd: Double?
    var lineEnds: [TimedWordKey: Double] = [:]
    @Environment(\.lyricReduceMotion) private var reduceMotion
    @Environment(\.lyricInk) private var ink
    @ObservedObject private var settings = Settings.shared
    @StateObject private var typography = RenderMemo<SpokenTypography.Key, [SpokenTypography.Word]>()

    private var nextIndex: Int? { words.firstIndex { $0.start > time } }

    /// How far the stage has rolled: one step per onset. Each step is a
    /// seeded spring paced by the gap to the next word, so quick passages
    /// move briskly, slow ones glide, and overlapping steps add their
    /// velocities instead of restarting from rest. Reduce Motion uses the
    /// nearest settled position and draws each word once.
    static func cursor(_ prepared: [SpokenTypography.Word], time: Double, intensity: Double,
                       reduceMotion: Bool) -> Double {
        var total = 0.0
        for word in prepared {
            let age = time - word.source.start
            total += reduceMotion ? vc_settle(age, 0.32)
                : vc_rolling_handoff(age, word.cadence, word.seed, intensity)
        }
        return total
    }

    var body: some View {
        let key = SpokenTypography.Key(words: words, baseSize: baseSize, mood: mood,
            composition: composition, fontIdentity: LyricFontIdentity(settings: settings), readings: readings,
            following: following, ordinals: ordinals, parts: parts)
        let prepared = typography.value(for: key) { SpokenTypography.prepare(key, settings: settings) }
        let defaultEnd = LyricHighlightTiming.lineEnd(lineEnd, words: words)
        let intensity = max(energy, OrganicMotion.vocalIntensity(words, at: time))
        let style: Int32 = composition == .spoken ? 0 : composition == .cascade ? 1 : 2
        GeometryReader { geometry in
            let cursor = Self.cursor(prepared, time: time, intensity: intensity, reduceMotion: reduceMotion)
            let position = reduceMotion ? cursor.rounded() : cursor
            let next = nextIndex
            // The words keep clear of the band at the foot of the stage.
            let stage = CGSize(width: geometry.size.width, height: max(1, geometry.size.height - bottomInset))
            // Rank 0 -- the word in focus -- sits here; every other word is
            // stacked above or below it by measured size (`RollingStack`).
            let anchor = CGPoint(x: stage.width * 0.5,
                                 y: stage.height * 0.54 + CGFloat(vc_rolling_baseline(style, 0)) * baseSize)
            RollingStackLayout(anchor: anchor, gap: baseSize * 0.16) {
                ForEach(prepared) { word in
                    let rank = position - 1 - Double(word.index)
                    if rank >= -2 && rank <= 5.2 {
                        let chosen = settings.arrival(in: composition, for: word.source)
                        let arrival = chosen.motion(in: composition)
                        let eligible = word.index > 0 ? prepared[word.index - 1].source.start : -Double.infinity
                        let highlight = chosen.highlightsSungWords && !word.tile
                            ? Self.highlight(word, lineEnd: lineEnds[word.source.presentationID] ?? defaultEnd,
                                             time: time, reduceMotion: reduceMotion)
                            : .hidden
                        token(word, next: next, rank: rank, size: stage, arrival: arrival,
                              intensity: intensity, eligibleSince: eligible, highlight: highlight)
                    }
                }
            }
            .coordinateSpace(.named(RollingStack.space))
            .frame(width: geometry.size.width, height: geometry.size.height)
        }
        .frame(height: max(baseSize * stageHeightInTypeSizes, stageHeight ?? 0))
        .accessibilityElement(children: .combine)
    }
    /// A rolling window can show words from more than one line.
    static func highlight(_ word: SpokenTypography.Word, lineEnd: Double, time: Double,
                          reduceMotion: Bool) -> LyricHighlightTiming.Pose {
        LyricHighlightTiming.pose(time: time, onset: word.source.start, release: lineEnd, reduceMotion: reduceMotion)
    }

    private func token(_ token: SpokenTypography.Word, next: Int?, rank: Double, size: CGSize, arrival: WordArrival,
                       intensity: Double, eligibleSince: Double,
                       highlight: LyricHighlightTiming.Pose = .hidden) -> some View {
        let state = RollingTokenState.make(token, next: next, rank: rank, time: time, composition: composition,
            baseSize: baseSize, stageWidth: size.width, intensity: intensity, reduceMotion: reduceMotion,
            arrival: arrival, eligibleSince: eligibleSince,
            highlightOpacity: highlight.opacity)
        let presentation = state.presentation
        let colours = ink.sample(slot: token.slot, seed: token.seed)
        let hero = composition == .cascade && token.hero && !colours.sweeps
        let sung = RollingTokenState.sweep(token, time: time)
        let entrance = state.slot, scale = state.scale, angle = state.angle
        let stageMargin = baseSize * 0.22
        // Cutout's label: one width rule before and after the onset, a
        // sliver under the faint preview, then pressed on with the word's
        // spring, so it lands with a little give. (It used to show full
        // width while previewing and snap to 5% at the onset.) Otherwise
        // the tile "Cutout · Highlight" presses in behind a word as it is
        // sung, drawn out to a label's size around the word without moving
        // or resizing it.
        let tile: RollingUnit.Tile? = token.tile
            ? .init(width: RollingUnit.tileWidth(arrival: presentation.arrival, reduceMotion: reduceMotion), opacity: 1)
            : highlight.isVisible
            ? .init(width: CGFloat(highlight.width), opacity: highlight.opacity, reach: RollingUnit.labelReach,
                    textSwitch: ink.tileTextSwitch) : nil
        return RollingUnit(token: token,
                           colour: hero ? (ink.accent ?? accent) : colours.rest,
                           sung: colours.sung, tint: colours.sweeps && !hero ? sung : 0,
                           sweepUnderline: colours.sweepUnderline,
                           readingColour: colours.reading,
                           readingOpacity: presentation.preview ? 0 : 1,
                           readingWidth: size.width * 0.8,
                           reveal: RollingTokenState.partReveal(token, time: time, composition: composition,
                               runOpacity: presentation.opacity, intensity: intensity, reduceMotion: reduceMotion),
                           tile: tile, tileText: ink.tileText)
            .background {
                if let tile {
                    CutoutTile(colour: ink.tileColour, width: tile.width, rightToLeft: token.rightToLeft)
                        .padding(.horizontal, -tile.reach.width)
                        .padding(.vertical, -tile.reach.height)
                        .opacity(tile.opacity)
                }
            }
            .modifier(WordEntrance(progress: presentation.progress, style: arrival,
                size: token.pointSize, direction: token.hash % 2 == 0 ? 1 : -1, visibility: state.shown,
                arrival: presentation.arrival, maxTurn: state.maxTurn))
            .visualEffect { ink, geometry in
                // Fitted where the stack actually placed it, word and
                // reading together as one unit, including the travel and
                // turn its entrance still has to make.
                let placed = geometry.frame(in: .named(RollingStack.space))
                let envelope = CGSize(width: geometry.size.width + 2 * abs(entrance.entrance.width),
                                      height: geometry.size.height + 2 * abs(entrance.entrance.height))
                return ink.scaleEffect(WordStageFit.scale(ink: envelope, stage: size,
                    centre: CGPoint(x: placed.midX, y: placed.midY), scale: scale * max(1, Double(entrance.entranceScale)),
                    angle: abs(angle) + abs(entrance.turn) + entrance.tilt, margin: stageMargin))
            }
            .scaleEffect(CGFloat(scale))
            .rotationEffect(.degrees(angle))
            .blur(radius: state.pose.blur)
            .shadow(color: .black.opacity(0.25), radius: 7, y: 2)
            .layoutValue(key: RollingSlotKey.self, value: state.slot)
            .zIndex(state.focus)
            .accessibilityHidden(state.shown < 0.05)
    }
}

/// Everything one rolling word does on one frame, worked out in one place
/// so the stage and its tests agree exactly.
struct RollingTokenState {
    let presentation: WordPresentation
    let pose: VCWordPose
    /// Final opacity: reveal, preview and the rank fade together.
    let shown: Double
    let focus: Double
    let scale: Double
    /// Degrees.
    let angle: Double
    /// The entrance turn this unit may make (see `RollingStack.turnLimit`).
    let maxTurn: Double
    let slot: RollingStack.Slot

    @MainActor
    static func make(_ token: SpokenTypography.Word, next: Int?, rank: Double, time: Double,
                     composition: LyricComposition, baseSize: CGFloat, stageWidth: CGFloat, intensity: Double,
                     reduceMotion: Bool, arrival: WordArrival, eligibleSince: Double = -.infinity,
                     highlightOpacity: Double = 0) -> RollingTokenState {
        let word = token.source
        let motion = vc_word_motion(time, word.start, word.end, token.cadence, .infinity, 0, token.seed,
                                    intensity, reduceMotion ? 1 : 0)
        let presentation = WordPresentation.make(word: word, index: token.index, nextIndex: next,
                                                  time: time, composition: composition, motion: motion,
                                                  eligibleSince: eligibleSince)
        let style: Int32 = composition == .spoken ? 0 : composition == .cascade ? 1 : 2
        let pose = vc_rolling_pose(style, rank, time - word.start, word.end - word.start,
                                   token.seed, intensity, reduceMotion ? 1 : 0)
        let focus = exp(-rank * rank * 2.4)
        // Hold against the time-based fade while the tile is lit. The
        // stage still fades words as they scroll off the top.
        let visible = max(pose.opacity, highlightOpacity * vc_rolling_rank_opacity(rank))
        let shown = presentation.opacity * (presentation.preview ? 1 : visible)
        // Settled words keep a slow float; the word in focus breathes instead.
        let drift = vc_idle_drift(token.seed, time, reduceMotion ? 0 : 0.55 * (1 - focus))
        let columnShift = composition == .spoken ? 0 : -stageWidth * 0.13
        let scale = pose.scale * drift.scale
        let angle = pose.angle + drift.angle
        // The entrance exactly as WordEntrance will draw it: its travel,
        // turn and scale are reserved continuously while they unwind (a
        // preview shows the entrance's starting pose, so it needs the most).
        let maxTurn = token.turnLimit
        let travel = WordEntrance.transform(progress: presentation.progress, arrival: presentation.arrival,
            style: arrival, size: token.pointSize, direction: token.hash % 2 == 0 ? 1 : -1, reduceMotion: reduceMotion,
            maxTurn: maxTurn)
        // Where the stack puts this word comes from its measured size; the
        // breath and float ride on top as a small lift inside the gap.
        let slot = RollingStack.Slot(rank: rank, scale: scale, angle: angle,
            dx: columnShift + CGFloat(pose.x + drift.x) * baseSize,
            lift: CGFloat(pose.y - vc_rolling_baseline(style, rank) + drift.y) * baseSize,
            entrance: travel.offset, turn: travel.turn, entranceScale: travel.scale,
            tilt: token.tile && !reduceMotion ? 1.2 : 0, shown: shown)
        return RollingTokenState(presentation: presentation, pose: pose, shown: shown, focus: focus, scale: scale,
                                 angle: angle, maxTurn: maxTurn, slot: slot)
    }
}

extension RollingTokenState {
    /// A glued run's syllables or characters arriving one by one, as a
    /// scattered run's do: each token's opacity, relative to the run's own,
    /// over its share of the word (see `TokenReveal`). A run of Chinese or
    /// Japanese with no spaces rolls as one word, which appeared whole at
    /// its first character and then showed nothing of what was being sung.
    /// Nil for a single token.
    @MainActor
    static func partReveal(_ token: SpokenTypography.Word, time: Double, composition: LyricComposition,
                           runOpacity: Double, intensity: Double, reduceMotion: Bool) -> [Double]? {
        guard token.parts.count > 1, token.boundaries.count == token.parts.count + 1 else { return nil }
        let next = token.parts.firstIndex { $0.start > time }
        return token.parts.enumerated().map { offset, part in
            guard offset > 0 else { return 1 }
            let motion = vc_word_motion(time, part.start, part.end, OrganicMotion.cadence(of: token.parts, at: offset),
                                        .infinity, 0, part.presentationID.visualSeed, intensity, reduceMotion ? 1 : 0)
            let opacity = WordPresentation.make(word: part, index: offset, nextIndex: next, time: time,
                composition: composition, motion: motion, eligibleSince: token.parts[offset - 1].start).opacity
            return min(1, opacity / max(runOpacity, 0.001))
        }
    }

    /// How far a sweeping colour has run through the word: through a glued
    /// run, each token's own timing over its own letters.
    static func sweep(_ token: SpokenTypography.Word, time: Double) -> Double {
        func progress(_ word: LyricsFetcher.Word) -> Double {
            min(max((time - word.start) / max(word.end - word.start, 0.001), 0), 1)
        }
        guard token.parts.count > 1, token.boundaries.count == token.parts.count + 1 else { return progress(token.source) }
        var total = 0.0
        for (offset, part) in token.parts.enumerated() {
            total += (token.boundaries[offset + 1] - token.boundaries[offset]) * progress(part)
        }
        return min(1, total)
    }
}

/// A rolling word and its reading as one unit: exactly what the stack
/// measures and the stage fits.
struct RollingUnit: View {
    /// A Cutout label's width as a share of the word. One rule before and
    /// after the onset; with Reduce Motion the label is simply there and
    /// fades in with its word instead of being pressed on.
    static func tileWidth(arrival: Double, reduceMotion: Bool) -> CGFloat {
        reduceMotion ? 1 : CGFloat(min(1.06, max(0.05, arrival)))
    }

    /// A label's padding past an unlabelled word's, so a highlight tile
    /// behind any word is the size of a label.
    static let labelReach = CGSize(width: 9, height: 3)

    let token: SpokenTypography.Word
    var colour: Color = .white
    var sung: Color = .white
    var tint: Double = 0
    var sweepUnderline: Color?
    var readingColour: Color = .white.opacity(0.7)
    var readingOpacity = 1.0
    /// The widest a reading may run under its word (a reading of the whole
    /// line hangs under the line's first word) before it wraps.
    var readingWidth: CGFloat?
    /// A glued run's tokens arriving one by one (`RollingTokenState.partReveal`).
    var reveal: [Double]?
    /// The cutout tile behind the unit, if any, and the colour the word and
    /// its reading take where the tile has pressed in.
    var tile: Tile?
    var tileText: Color = .white

    /// Cutout's label, or the tile "Cutout · Highlight" presses in behind a
    /// sung word.
    struct Tile: Equatable {
        /// How far it has pressed in from the word's leading edge, 0...1.
        var width: CGFloat
        var opacity: Double
        /// How far it reaches past the unit on each side.
        var reach: CGSize = .zero
        /// The opacity at which the word on it changes colour (`LyricHighlight.textSwitch`).
        var textSwitch = 0.4
    }

    var body: some View {
        if let tile, tile.opacity > 0.001 {
            // On the tile the word takes the colour set on it -- only as far
            // as the tile has pressed in, so no letter is ever left in dark
            // ink past its edge.
            content(colour: colour, sung: sung, tint: tint, reading: readingColour)
                .overlay {
                    content(colour: tileText, sung: tileText, tint: 0,
                            reading: tileText.opacity(LyricHighlight.readingOpacity))
                        .mask { TileMask(tile: tile, rightToLeft: token.rightToLeft) }
                }
        } else {
            content(colour: colour, sung: sung, tint: tint, reading: readingColour)
        }
    }

    private func content(colour: Color, sung: Color, tint: Double, reading: Color) -> some View {
        VStack(spacing: token.pointSize * 0.02) {
            RollingWordText(text: token.source.text, font: token.font, colour: colour, sung: sung, tint: tint,
                            rightToLeft: token.rightToLeft)
                .modifier(LyricSweepUnderline(colour: sweepUnderline, progress: tint, rightToLeft: token.rightToLeft))
                .modifier(TokenReveal(boundaries: token.boundaries, opacities: reveal, rightToLeft: token.rightToLeft))
            if let reading = token.reading?.tinted(reading.opacity(readingOpacity)) {
                if let readingWidth { reading.wrapping(at: readingWidth) } else { reading }
            }
        }
        .padding(.horizontal, token.tile ? 11 : 2).padding(.vertical, token.tile ? 3 : 0)
    }

    /// Where a unit's tile has pressed in, at its strength.
    private struct TileMask: View {
        let tile: Tile
        let rightToLeft: Bool

        var body: some View {
            GeometryReader { proxy in
                let full = CGRect(x: -tile.reach.width, y: -tile.reach.height,
                                  width: proxy.size.width + 2 * tile.reach.width,
                                  height: proxy.size.height + 2 * tile.reach.height)
                let pressed = full.width * min(1, max(0, tile.width))
                Rectangle()
                    .frame(width: pressed, height: full.height)
                    .position(x: (rightToLeft ? full.maxX - pressed : full.minX) + pressed / 2, y: full.midY)
                    .opacity(LyricHighlightTiming.textOpacity(tile.opacity, switchAt: tile.textSwitch))
            }
        }
    }
}

/// Where each word of a rolling stage sits, stacked by the size it takes.
///
/// Places used to come from a fixed spacing per rank -- about 1.2 em
/// between neighbours. A word with its reading under it is close to twice
/// that tall, so the next-word preview and the words fading out ran into the
/// word being sung. Each word is now measured -- reading, label tile, scale
/// and tilt included -- and neighbours are spaced by their real extents plus
/// a gap, so no two can meet at any size or in any face.
enum RollingStack {
    static let space = "LunalyrRollingStage"

    struct Slot: Equatable {
        /// Continuous. 0 is the word in focus; older words (positive) stack
        /// above it, words still to come (negative) below.
        var rank: Double = 0
        var scale: Double = 1
        /// Degrees.
        var angle: Double = 0
        /// Horizontal place relative to the stage's anchor.
        var dx: CGFloat = 0
        /// Breath and float: small, and well inside the gap.
        var lift: CGFloat = 0
        /// The entrance still to unwind: travel (points), turn (degrees,
        /// signed) and scale, as `WordEntrance.transform` draws them.
        var entrance: CGSize = .zero
        var turn: Double = 0
        var entranceScale: CGFloat = 1
        /// A Cutout label's own tilt, in degrees.
        var tilt: Double = 0
        /// How visible the unit is; not used for placement.
        var shown = 1.0
    }

    /// The largest entrance turn for a unit, in degrees: the ends of the
    /// label may swing by at most about a third of its height. A unit no
    /// wider than about three times its height -- a short word -- keeps the
    /// full 12 degrees; longer words turn less (a seven-letter word about
    /// 10), and a glued phrase or a long Replace run only a few, which reads
    /// the same and needs far less room. Estimated from the text, so it is
    /// known before layout; worked out once per word (`SpokenTypography`),
    /// and the stack and the drawing use the same value.
    static func turnLimit(text: String, reading: String?, pointSize: CGFloat, tile: Bool) -> Double {
        func width(_ text: String, _ size: CGFloat) -> CGFloat {
            text.unicodeScalars.reduce(0) { total, scalar in
                total + size * (scalar.value >= 0x1100 ? 1.0 : 0.6)
            }
        }
        let readingWidth = reading.map { width($0, pointSize * 0.56) } ?? 0
        let unitWidth = max(width(text, pointSize), readingWidth) + (tile ? 22 : 4)
        let unitHeight = pointSize * 1.3 + (reading == nil ? 0 : pointSize * 0.75)
        let ratio = min(1, 0.6 * unitHeight / max(1, unitWidth))
        return min(12, asin(ratio) * 180 / .pi)
    }

    /// The height a unit can take: its scale and tilt, the entrance's turn
    /// and scale (taken at their worst), and room on both sides for the
    /// travel still to come. Every term shrinks continuously as the
    /// entrance lands, so neighbours ease back together rather than snap.
    static func extent(_ size: CGSize, _ slot: Slot) -> CGFloat {
        let radians = min(90, abs(slot.angle) + abs(slot.turn) + slot.tilt) * .pi / 180
        let rotated = size.height * CGFloat(abs(cos(radians))) + size.width * CGFloat(abs(sin(radians)))
        return rotated * CGFloat(max(0, slot.scale)) * max(1, slot.entranceScale) + 2 * abs(slot.entrance.height)
    }

    /// Centres for every unit. Units are ordered by
    /// rank and spaced by half of each neighbour's extent plus `gap`; the
    /// stack is then shifted so that rank 0 falls on `anchor`. Positions are
    /// continuous in rank, so the stage still rolls smoothly.
    static func centres(sizes: [CGSize], slots: [Slot], anchor: CGPoint, gap: CGFloat) -> [CGPoint] {
        var result = [CGPoint](repeating: anchor, count: slots.count)
        let members = slots.indices.sorted { slots[$0].rank > slots[$1].rank }
        guard let first = members.first, let last = members.last else { return result }
        var stack = [CGFloat](repeating: 0, count: members.count)
        for k in members.indices.dropFirst() {
            stack[k] = stack[k - 1] + (extent(sizes[members[k - 1]], slots[members[k - 1]])
                + extent(sizes[members[k]], slots[members[k]])) / 2 + gap
        }
        let origin: CGFloat
        if let k = members.indices.dropLast().first(where: { slots[members[$0]].rank >= 0 && slots[members[$0 + 1]].rank <= 0 }) {
            let upper = slots[members[k]].rank, lower = slots[members[k + 1]].rank
            let share = upper - lower > 1e-9 ? upper / (upper - lower) : 0
            origin = stack[k] + CGFloat(share) * (stack[k + 1] - stack[k])
        } else if slots[first].rank < 0 {
            // Nothing sung yet: the first word waits below the anchor at
            // its plain height -- there is nothing above it to clear.
            var plain = slots[first]
            plain.entrance = .zero; plain.turn = 0; plain.entranceScale = 1; plain.tilt = 0
            origin = CGFloat(slots[first].rank) * (extent(sizes[first], plain) + gap)
        } else {
            // Only older words: the anchor is below the last of them.
            origin = stack[members.count - 1] + CGFloat(slots[last].rank) * (extent(sizes[last], slots[last]) + gap)
        }
        for (k, member) in members.enumerated() {
            result[member] = CGPoint(x: anchor.x + slots[member].dx,
                                     y: anchor.y + stack[k] - origin + slots[member].lift)
        }
        return result
    }
}

struct RollingSlotKey: LayoutValueKey {
    static let defaultValue = RollingStack.Slot()
}

/// Measures every rolling word -- with its reading and label -- and places
/// it where `RollingStack` says.
struct RollingStackLayout: Layout {
    let anchor: CGPoint
    let gap: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        proposal.replacingUnspecifiedDimensions()
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let sizes = subviews.map { $0.sizeThatFits(.unspecified) }
        let slots = subviews.map { $0[RollingSlotKey.self] }
        let centres = RollingStack.centres(sizes: sizes, slots: slots, anchor: anchor, gap: gap)
        for (subview, centre) in zip(subviews, centres) {
            subview.place(at: CGPoint(x: bounds.minX + centre.x, y: bounds.minY + centre.y),
                          anchor: .center, proposal: .unspecified)
        }
        Self.recorder?(zip(zip(sizes, slots), centres).map { RollingStack.Placed(size: $0.0, slot: $0.1, centre: $1) })
    }

    /// Tests watch what the real layout placed. Never set in the app.
    nonisolated(unsafe) static var recorder: (([RollingStack.Placed]) -> Void)?
}

extension RollingStack {
    /// The fit the stage applies to a placed unit (see `WordStageFit`), with
    /// the same inputs the unit's view uses.
    static func fit(size: CGSize, slot: Slot, centre: CGPoint, stage: CGSize, margin: CGFloat) -> CGFloat {
        let envelope = CGSize(width: size.width + 2 * abs(slot.entrance.width),
                              height: size.height + 2 * abs(slot.entrance.height))
        return WordStageFit.scale(ink: envelope, stage: stage, centre: centre,
            scale: slot.scale * max(1, Double(slot.entranceScale)),
            angle: abs(slot.angle) + abs(slot.turn) + slot.tilt, margin: margin)
    }

    /// One unit as the layout placed it.
    struct Placed {
        let size: CGSize
        let slot: Slot
        let centre: CGPoint
        /// The stage fit `WordStageFit` applies on top, when known.
        var fit: CGFloat = 1

        /// The unit as drawn: rotated by its tilt and the entrance's turn,
        /// scaled by its own and the entrance's scale, and moved by the
        /// entrance's remaining travel (under the stage's scale).
        var box: CGRect {
            let radians = min(90, abs(slot.angle + slot.turn) + slot.tilt) * .pi / 180
            let cosine = CGFloat(abs(cos(radians))), sine = CGFloat(abs(sin(radians)))
            let scale = CGFloat(slot.scale) * slot.entranceScale * fit
            let width = (size.width * cosine + size.height * sine) * scale
            let height = (size.height * cosine + size.width * sine) * scale
            let x = centre.x + slot.entrance.width * CGFloat(slot.scale) * fit
            let y = centre.y + slot.entrance.height * CGFloat(slot.scale) * fit
            return CGRect(x: x - width / 2, y: y - height / 2, width: width, height: height)
        }
    }
}

/// One rolling word, with the ink's colour running through it as it is sung.
private struct RollingWordText: View {
    let text: String
    let font: Font
    let colour: Color
    let sung: Color
    let tint: Double
    let rightToLeft: Bool

    var body: some View {
        ZStack {
            if tint < 1 {
                Text(text).font(font).foregroundStyle(colour)
                    .lineLimit(1).fixedSize(horizontal: true, vertical: true)
            }
            if tint > 0 {
                Text(text).font(font).foregroundStyle(sung)
                    .lineLimit(1).fixedSize(horizontal: true, vertical: true)
                    .mask(LyricSweepMask(progress: tint, rightToLeft: rightToLeft, feather: LyricSweepMask.colourFeather))
            }
        }
    }
}
