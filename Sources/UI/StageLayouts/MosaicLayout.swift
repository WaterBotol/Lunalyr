import SwiftUI

// Mosaic: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.

extension StageLyricView {
    /// Mosaic's entry point, called by the dispatcher (`StageLyricView.layout`):
    /// `runs` is the whole line, `shown` the runs that have arrived (Mosaic
    /// reads it only under Reduce Motion, for the shared dissolve's steps).
    func mosaicBody(runs: [StageRun], shown: [StageRun]) -> some View {
        mosaic(runs, arrived: reduceMotion ? shown.count : runs.count)
    }

    /// Mosaic leaves Reduce Motion to the shared dissolve between steps
    /// when false (`StageLyricView.reducedDissolve`).
    static let mosaicOwnsReduceMotion = false

    // MARK: Mosaic
    //
    // A collage: each word is set a different way as it arrives -- huge, near
    // the stage's width; white on a colour bar; or letter by letter, each
    // letter rising onto one baseline -- and then shrinks into a small,
    // flush-left stack above, read top to bottom, the oldest words fading out
    // at its top. Nothing else is drawn: no ornaments, no panels.
    //
    // A quick run of words (each under `mosaicQuickGap` after the one before)
    // is one "beat": set together in one treatment, each word appearing in
    // its place as it is sung, so a fast line never stacks big words on top
    // of each other. A beat leaves for the stack just before the next one
    // arrives (the times are known), so the two never share the spot.

    /// The three ways Mosaic sets a beat.
    enum MosaicKind: Equatable {
        /// Huge, nearly the stage's width.
        case huge
        /// White on a colour bar (a single word only, printed with its bar).
        case bar
        /// Letter by letter, each rising onto one baseline.
        case letters
    }

    /// One or more runs set together in one treatment.
    struct MosaicBeat: Equatable {
        var runs: Range<Int>
        var kind: MosaicKind
    }

    /// Words closer together than this (start to start) share a beat.
    static let mosaicQuickGap = 0.28
    /// A beat holds at most this many words and characters (spaces included).
    static let mosaicBeatRuns = 3
    static let mosaicBeatCharacters = 16
    /// How far ahead of its onset a word starts to appear, so it is fully in
    /// by the time it is sung (never later).
    static let mosaicLead = 0.12

    /// The size of a treatment, in units.
    static func mosaicBigSize(_ kind: MosaicKind) -> CGFloat {
        switch kind {
        case .huge: return 3.0
        case .bar: return 2.3
        case .letters: return 2.4
        }
    }

    /// The text joining two runs of a beat: a space, or nothing for a script
    /// written without spaces.
    static func mosaicJoiner(_ next: StageRun) -> String {
        LyricScript.keepsSyllables(next.text) ? "" : " "
    }

    /// The beats of a line and how each is set. Runs in `alone` (a reading
    /// under them, or a tile behind them) always make a beat of their own.
    ///
    /// The kinds vary by importance and length, never the same twice in a
    /// row: the beat with the longest-held word is huge; a short single word
    /// (up to five letters) takes the bar, a long one (seven or more) the
    /// letters; a quick run is huge or letters.
    static func mosaicBeats(_ runs: [StageRun], alone: Set<Int> = [], tiled: Set<Int> = []) -> [MosaicBeat] {
        var groups: [Range<Int>] = []
        var characters = 0
        for index in runs.indices {
            if let last = groups.last, index > 0 {
                let gap = runs[index].start - runs[index - 1].start
                let added = mosaicJoiner(runs[index]).count + runs[index].text.count
                let fits = last.count < mosaicBeatRuns && characters + added <= mosaicBeatCharacters
                let free = !alone.contains(index) && !alone.contains(last.lowerBound)
                if gap < mosaicQuickGap && fits && free {
                    groups[groups.count - 1] = last.lowerBound..<(index + 1)
                    characters += added
                    continue
                }
            }
            groups.append(index..<(index + 1))
            characters = runs[index].text.count
        }
        // The longest-held word (ties: the longer word, then the earlier).
        var held: Int?
        var best = (-1.0, -1)
        for (index, run) in runs.enumerated() {
            let key = (run.end - run.start, run.text.count)
            if key.0 > best.0 + 0.001 || (abs(key.0 - best.0) <= 0.001 && key.1 > best.1) {
                best = key
                held = index
            }
        }
        let forced: [Bool] = groups.map { group in
            group.contains { tiled.contains($0) } || (held.map { group.contains($0) } ?? false)
        }
        var kinds: [MosaicKind] = []
        for (g, group) in groups.enumerated() {
            if forced[g] { kinds.append(.huge); continue }
            let text = group.map { runs[$0].text }.joined()
            let preference: [MosaicKind]
            if group.count > 1 {
                preference = [.huge, .letters]
            } else if text.count <= 5 {
                preference = [.bar, .letters, .huge]
            } else if text.count >= 7 {
                preference = [.letters, .huge, .bar]
            } else {
                preference = [.huge, .bar, .letters]
            }
            let previous = kinds.last
            let next: MosaicKind? = g + 1 < groups.count && forced[g + 1] ? .huge : nil
            let choice = preference.first { $0 != previous && $0 != next }
                ?? preference.first { $0 != previous }
                ?? preference[0]
            kinds.append(choice)
        }
        return zip(groups, kinds).map { MosaicBeat(runs: $0, kind: $1) }
    }

    /// How far each beat has gone from where it landed into the stack, 0...1.
    /// A beat leaves just ahead of the next beat's arrival (or of the line's
    /// exit, for the last), but not before its last word has had a moment on
    /// its own; the move is quicker on a quick line. `arrived` beats count
    /// under Reduce Motion, where the moves are steps.
    static func mosaicMoves(_ beats: [MosaicBeat], runs: [StageRun], at time: Double, speed: Double,
                            exitStart: Double, reduceMotion: Bool, arrived: Int) -> [Double] {
        beats.indices.map { g in
            let followed = g + 1 < beats.count
            if reduceMotion {
                return followed && beats[g + 1].runs.lowerBound < arrived ? 1 : 0
            }
            let next: Double
            if followed {
                next = runs[beats[g + 1].runs.lowerBound].start - mosaicLead
            } else if exitStart.isFinite {
                next = exitStart
            } else {
                return 0
            }
            let last = beats[g].runs.map { runs[$0].start }.max() ?? next
            let gap = max(0, next - (last - mosaicLead))
            let duration = min(0.42 / min(3, max(1, speed)) + 0.06, max(0.14, gap * 0.9))
            let start = max(last - mosaicLead + min(0.18, gap * 0.5), next - duration * 0.6)
            return glide((time - start) / duration)
        }
    }

    /// Each beat's row in the stack: the sum of its own move and the moves
    /// of every beat after it, so the stack climbs a row as each beat joins.
    static func mosaicRanks(_ moves: [Double]) -> [Double] {
        var after = 0.0
        var result = [Double](repeating: 0, count: moves.count)
        for index in moves.indices.reversed() {
            after += moves[index]
            result[index] = after
        }
        return result
    }

    /// How much of a word is in, 0...1 (it may overshoot a little): fully in
    /// by its onset.
    static func mosaicAppear(_ run: StageRun, at time: Double, speed: Double, reduceMotion: Bool, arrived: Int) -> Double {
        if reduceMotion { return run.id < arrived ? 1 : 0 }
        return StageMotion.arrival(run.start - (mosaicLead - StageMotion.lead), at: time, speed: speed)
    }

    /// The centre of a beat measured `size`: from where it lands (towards the
    /// lower right) to its row in the stack, flush to the stack's column.
    static func mosaicCentre(_ size: CGSize, move: Double, rank: Double, sway: CGFloat, step: CGFloat,
                             overhang: CGFloat, rightToLeft: Bool, in bounds: CGRect) -> CGPoint {
        let settle = CGFloat(move)
        let home = mosaicHome(size, sway: sway, step: step, overhang: overhang, rightToLeft: rightToLeft, in: bounds)
        let column = rightToLeft ? bounds.maxX - bounds.width * 0.14 : bounds.minX + bounds.width * 0.14
        let stack = CGPoint(x: column + (rightToLeft ? -1 : 1) * size.width / 2,
                            y: mosaicRowY(rank: max(1, rank), step: step, in: bounds))
        return CGPoint(x: home.x + (stack.x - home.x) * settle, y: home.y + (stack.y - home.y) * settle)
    }

    /// When a letter of a word rises in: its syllable's own onset (less the
    /// lead), the letters of the syllable a little apart, all of them in
    /// before the next syllable or word is sung. `next` is the onset after
    /// the run, if any.
    static func mosaicLetterReveal(part: Int, letter: Int, of run: StageRun, next: Double?, at time: Double) -> Double {
        guard run.parts.indices.contains(part) else { return 1 }
        let onset = run.parts[part].start
        let following = part + 1 < run.parts.count ? run.parts[part + 1].start : (next ?? onset + 0.5)
        let window = min(0.6, max(0.08, following - onset))
        let count = max(1, run.parts[part].text.count)
        let rise = min(0.2, max(0.07, window * 0.55))
        let stagger = count > 1 ? min(0.045, window * 0.45 / Double(count - 1)) : 0
        return glide((time - (onset - mosaicLead + Double(letter) * stagger)) / rise)
    }

    /// The size Mosaic's small stack sets its words at.
    static func mosaicSmallSize(unit: CGFloat) -> CGFloat { unit * 0.6 }

    /// How far apart Mosaic's stack sets its rows: a word, and its reading
    /// when the line has readings.
    static func mosaicStackStep(unit: CGFloat, readings: Bool) -> CGFloat {
        unit * 0.85 + (readings ? readingSlot(mosaicSmallSize(unit: unit)) : 0)
    }

    /// The centre of the stack's row for a word `rank` hand-offs old: the
    /// newest small word sits lowest, nearest the big one, and older words
    /// climb a row each (rank 1 to 4).
    static func mosaicRowY(rank: Double, step: CGFloat, in bounds: CGRect) -> CGFloat {
        let top = bounds.minY + max(bounds.height * 0.1, step * 0.6)
        return top + (3 - CGFloat(min(4, max(rank, 1)))) * step
    }

    /// Where Mosaic's word lands before it joins the stack: on the lower
    /// right thirds, below the stack, and inside the 5 % title-safe margin. On a stage too short
    /// for both, it moves right of the stack's column instead. `overhang` is
    /// how far the setting reaches past its own box (the colour bar);
    /// `rightToLeft` mirrors it.
    static func mosaicHome(_ size: CGSize, sway: CGFloat, step: CGFloat, overhang: CGFloat = 0,
                           rightToLeft: Bool = false, in bounds: CGRect) -> CGPoint {
        let below = mosaicRowY(rank: 1, step: step, in: bounds) + step * 0.7 + overhang + size.height / 2
        let lowest = bounds.maxY - size.height / 2 - bounds.height * 0.05 - overhang
        let y = min(lowest, max(bounds.minY + bounds.height * 0.6, below))
        // Its centre towards the right third, never past the safe edge.
        let right = bounds.width * 0.95 - size.width / 2
        var x = bounds.minX + max(bounds.width / 2, min(right, bounds.width * (0.6 + sway)))
        if below > lowest + 1 {
            // Clear of the stack's column (a fifth of the stage from it), as
            // far as the far edge allows.
            let clear = bounds.width * 0.34 + size.width / 2
            let far = bounds.width * 0.95 - size.width / 2
            x = bounds.minX + max(x - bounds.minX, min(clear, far))
        }
        if rightToLeft { x = bounds.maxX - (x - bounds.minX) }
        return CGPoint(x: x, y: y)
    }

    /// Mosaic's colour bar a little less saturated than the ink's tile
    /// colour: on a dark stage a full-chroma bar shouts.
    static let mosaicAccentSaturation: Double = 0.72

    /// How much of a stacked word still shows: the newest two clearly, the
    /// third faint, gone by the fourth row.
    static func mosaicStackFade(rank: Double) -> Double { max(0, min(1, 1 - (rank - 1.6) * 0.45)) }

    /// The bar's reach past its word, in sizes.
    static let mosaicBarPadding: (horizontal: CGFloat, vertical: CGFloat) = (0.28, 0.06)

    func mosaic(_ runs: [StageRun], arrived: Int) -> some View {
        let speed = lineSpeed
        let alone = Set(runs.filter { tiled($0) || Self.reading(of: $0, in: readings) != nil }.map(\.id))
        let tiles = Set(runs.filter { tiled($0) }.map(\.id))
        let beats = Self.mosaicBeats(runs, alone: alone, tiled: tiles)
        let moves = Self.mosaicMoves(beats, runs: runs, at: time, speed: speed, exitStart: exitStart,
                                     reduceMotion: reduceMotion, arrived: arrived)
        let ranks = Self.mosaicRanks(moves)
        // A beat is drawn from just before its first word until it has faded off the top of the stack.
        let visible = beats.indices.filter { g in
            let first = runs[beats[g].runs.lowerBound]
            let begun = Self.mosaicAppear(first, at: time, speed: speed, reduceMotion: reduceMotion, arrived: arrived) > 0.001
            return begun && Self.mosaicStackFade(rank: ranks[g]) > 0.001
        }
        let u = unit
        let rightToLeft = LyricScript.lineIsRightToLeft(runs.map(\.text))
        // The stack's rows make room for readings when the line has any.
        let step = Self.mosaicStackStep(unit: u, readings: runs.contains { Self.reading(of: $0, in: readings) != nil })
        return StageLayout(place: { measured, bounds in
            measured.indices.map { index in
                let g = visible.indices.contains(index) ? visible[index] : 0
                let overhang = beats[g].kind == .bar ? u * Self.mosaicBigSize(.bar) * Self.mosaicBarPadding.vertical * 2 : 0
                return Self.mosaicCentre(measured[index], move: moves[g], rank: ranks[g],
                                         sway: CGFloat(chance(g, 21) - 0.5) * 0.06, step: step, overhang: overhang,
                                         rightToLeft: rightToLeft, in: bounds)
            }
        }) {
            ForEach(visible, id: \.self) { g in
                mosaicBeat(beats[g], g: g, runs: runs, move: moves[g], rank: ranks[g], speed: speed,
                           arrived: arrived, rightToLeft: rightToLeft)
            }
        }
        .opacity(1 - leave)
    }

    /// One beat: its treatment, which shrinks towards its own edge as it
    /// leaves for the stack and there turns into the plain small word.
    @ViewBuilder
    private func mosaicBeat(_ beat: MosaicBeat, g: Int, runs: [StageRun], move: Double, rank: Double, speed: Double,
                            arrived: Int, rightToLeft: Bool) -> some View {
        let first = runs[beat.runs.lowerBound]
        let size = unit * Self.mosaicBigSize(beat.kind)
        let morph = StageMotion.mosaicMorph(settle: move, smallRatio: 0.6 / Self.mosaicBigSize(beat.kind))
        let sample = sample(first, of: runs.count)
        let fade = Self.mosaicStackFade(rank: rank)
        let edge: Alignment = rightToLeft ? .trailing : .leading
        let appear = beat.runs.map { Self.mosaicAppear(runs[$0], at: time, speed: speed, reduceMotion: reduceMotion, arrived: arrived) }
        ZStack(alignment: edge) {
            Group {
                switch beat.kind {
                case .huge:
                    mosaicSetting(beat, runs: runs, size: size, colour: tiled(first) ? ink.tileText : sample.sung,
                                  reading: sample.reading, appear: appear)
                        .background { tileBackground(tiled(first), size: size) }
                        // Punches in from a little smaller, never past its own room on the stage.
                        .scaleEffect(reduceMotion ? 1 : 0.8 + 0.2 * min(1.05, appear[0]))
                case .bar:
                    // The bar and its word are one print: they come in together
                    // and the bar never shows without the whole word on it.
                    let readingInk = ink.tileText.opacity(LyricHighlight.readingOpacity)
                    let tilt = (chance(first.id, 28) - 0.5) * 5
                    let pad = Self.mosaicBarPadding
                    mosaicSetting(beat, runs: runs, size: size, colour: ink.tileText, reading: readingInk,
                                  appear: appear.map { $0 > 0.001 ? 1 : 0 }, syllables: false, shadow: false)
                        .background {
                            ink.tileColour.saturation(Self.mosaicAccentSaturation)
                                .padding(.horizontal, -size * pad.horizontal).padding(.vertical, -size * pad.vertical)
                        }
                        .rotationEffect(.degrees(tilt))
                        .scaleEffect(x: reduceMotion ? 1 : 0.7 + 0.3 * appear[0], y: 1,
                                     anchor: UnitPoint(x: rightToLeft ? 1 : 0, y: 0.5))
                        .opacity(min(1, appear[0] * 1.6))
                case .letters:
                    mosaicLetters(beat, runs: runs, size: size, colour: sample.sung, reading: sample.reading, arrived: arrived)
                }
            }
            .shadow(color: .black.opacity(0.28), radius: 8, y: 3)
            .opacity(morph.bigOpacity)
            // The plain small word it becomes, laid exactly over it.
            mosaicSetting(beat, runs: runs, size: size, colour: sample.rest.opacity(0.8),
                          reading: sample.reading, appear: appear, syllables: beat.kind != .bar, shadow: false)
                .opacity(morph.smallOpacity)
        }
        .scaleEffect(morph.bigScale, anchor: UnitPoint(x: rightToLeft ? 1 : 0, y: 0.5))
        .opacity(fade)
    }

    /// A beat's words as one line of text, each word in its place from the
    /// start (unsung ones clear), so nothing shifts as they arrive.
    /// Without `syllables`, a word is printed whole (the bar's word).
    private func mosaicText(_ beat: MosaicBeat, runs: [StageRun], colour: Color, appear: [Double],
                            syllables: Bool) -> AttributedString {
        var text = AttributedString()
        for (offset, index) in beat.runs.enumerated() {
            let run = runs[index]
            if offset > 0 { text.append(AttributedString(Self.mosaicJoiner(run))) }
            let shown = appear.indices.contains(offset) ? min(1, max(0, appear[offset] * 1.6)) : 1
            for (part, piece) in run.parts.enumerated() {
                var chunk = AttributedString(piece.text)
                let syllable = part == 0 || !syllables ? 1 : StageRun.partOpacity(part, of: run.parts, at: time)
                let opacity = reduceMotion ? (shown > 0 ? 1 : 0) : shown * syllable
                chunk.foregroundColor = opacity <= 0.001 ? .clear : colour.opacity(min(1, opacity))
                text.append(chunk)
            }
        }
        return text
    }

    /// The text of a treatment, with the reading under a single word.
    private func mosaicSetting(_ beat: MosaicBeat, runs: [StageRun], size: CGFloat, colour: Color, reading: Color,
                               appear: [Double], syllables: Bool = true, shadow: Bool = true) -> some View {
        let first = runs[beat.runs.lowerBound]
        let width = stageSize.width * (beat.kind == .huge ? 0.9 : beat.kind == .bar ? 0.7 : 0.8)
        return withReading(first, size: size, colour: reading, maxWidth: width, shadow: shadow) {
            Text(mosaicText(beat, runs: runs, colour: colour, appear: appear, syllables: syllables))
                .font(font(size, hero: true, id: first.id))
                .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: width)
        }
    }

    /// Letter by letter: each letter fades in rising onto the one baseline
    /// (macOS 15 and later; on 14 it only fades), in time with its syllable.
    private func mosaicLetters(_ beat: MosaicBeat, runs: [StageRun], size: CGFloat, colour: Color,
                               reading: Color, arrived: Int) -> some View {
        let first = runs[beat.runs.lowerBound]
        let width = stageSize.width * 0.8
        var text = Text("")
        for (offset, index) in beat.runs.enumerated() {
            let run = runs[index]
            if offset > 0 { text = text + Text(Self.mosaicJoiner(run)) }
            let next: Double? = index + 1 < runs.count ? runs[index + 1].start : nil
            for (part, piece) in run.parts.enumerated() {
                for (letter, character) in piece.text.enumerated() {
                    let reveal = reduceMotion
                        ? (run.id < arrived ? 1 : 0)
                        : Self.mosaicLetterReveal(part: part, letter: letter, of: run, next: next, at: time)
                    text = text + mosaicLetter(character, colour: colour, reveal: reveal)
                }
            }
        }
        return withReading(first, size: size, colour: reading, maxWidth: width) {
            text
                .font(font(size, hero: true, id: first.id))
                .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: width)
                .modifier(MosaicLetterRiseModifier(drop: reduceMotion ? 0 : size * 0.22))
        }
    }

    private func mosaicLetter(_ character: Character, colour: Color, reveal: Double) -> Text {
        let piece = Text(String(character)).foregroundStyle(reveal <= 0.001 ? Color.clear : colour.opacity(min(1, reveal)))
        if #available(macOS 15.0, *) {
            return piece.customAttribute(MosaicLetterRise(lowered: 1 - min(1, max(0, reveal))))
        }
        return piece
    }
}

/// How far below its baseline a letter of Mosaic's letter-by-letter
/// treatment still is, 0...1.
@available(macOS 15.0, *)
struct MosaicLetterRise: TextAttribute {
    var lowered: Double
}

/// Draws each letter lowered by its `MosaicLetterRise`, all on one baseline
/// once in. The text's layout is untouched, so the word never shifts.
@available(macOS 15.0, *)
struct MosaicLetterRenderer: TextRenderer {
    var drop: CGFloat

    var displayPadding: EdgeInsets { EdgeInsets(top: 0, leading: 0, bottom: drop, trailing: 0) }

    func draw(layout: Text.Layout, in context: inout GraphicsContext) {
        for line in layout {
            for run in line {
                let lowered = run[MosaicLetterRise.self]?.lowered ?? 0
                var glyph = context
                glyph.translateBy(x: 0, y: CGFloat(lowered) * drop)
                glyph.draw(run)
            }
        }
    }
}

private struct MosaicLetterRiseModifier: ViewModifier {
    var drop: CGFloat

    func body(content: Content) -> some View {
        if #available(macOS 15.0, *) {
            content.textRenderer(MosaicLetterRenderer(drop: drop))
        } else {
            content
        }
    }
}

extension StageMotion {
    /// Mosaic: the big setting shrinks to the small one's size by settle 0.7,
    /// then the two cross-fade between 0.7 and 0.95, so the two copies are
    /// never seen at different sizes.
    static func mosaicMorph(settle: Double, smallRatio: Double) -> (bigScale: Double, bigOpacity: Double, smallOpacity: Double) {
        let shrink = Easing.smoothstep(settle / 0.7)
        let swap = Easing.smoothstep((settle - 0.7) / 0.25)
        return (1 - (1 - smallRatio) * shrink, 1 - swap, swap)
    }
}
