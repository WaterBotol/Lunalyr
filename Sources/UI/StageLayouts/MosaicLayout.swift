import SwiftUI

// Mosaic: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.

extension StageLyricView {
    /// Mosaic's entry point, called by the dispatcher (`StageLyricView.layout`):
    /// `runs` is the whole line, `shown` the runs that have arrived.
    func mosaicBody(runs: [StageRun], shown: [StageRun]) -> some View {
        mosaic(shown, total: runs.count)
    }

    /// Mosaic leaves Reduce Motion to the shared dissolve between steps
    /// when false (`StageLyricView.reducedDissolve`).
    static let mosaicOwnsReduceMotion = false

    // MARK: Mosaic
    //
    // A collage: each word is set a different way as it arrives -- huge and cut
    // off by the edge of the stage with a star beside it, white on a colour bar
    // across a pale panel, or letter by letter on loosely set baselines -- and
    // then shrinks into a small, flush-left stack above, read top to bottom,
    // the oldest words fading out at its top.

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
    /// how far the setting reaches past its own box (the colour bar's panel);
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

    /// Mosaic's accent (the bar and the star) a little less saturated than
    /// the ink's tile colour: on a dark stage a full-chroma bar shouts.
    static let mosaicAccentSaturation: Double = 0.72

    /// How much of a stacked word still shows: the newest two clearly, the
    /// third faint, gone by the fourth row.
    static func mosaicStackFade(rank: Double) -> Double { max(0, min(1, 1 - (rank - 1.6) * 0.45)) }

    func mosaic(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let arrivals = runs.map { arrive($0, speed: speed) }
        let ranks = ranks(runs, speed: speed)
        let leaving = leave
        // A word on a tile (Cutout · Highlight) is always the huge kind, which can carry one.
        let kinds = runs.map { tiled($0) ? 0 : Int(chance($0.id, 20) * 3) % 3 }
        let stage = stageSize
        let u = unit
        let rightToLeft = LyricScript.lineIsRightToLeft(runs.map(\.text))
        let edge: Alignment = rightToLeft ? .trailing : .leading
        let flush: HorizontalAlignment = rightToLeft ? .trailing : .leading
        // The stack's rows make room for readings when the line has any.
        let step = Self.mosaicStackStep(unit: u, readings: runs.contains { Self.reading(of: $0, in: readings) != nil })
        return StageLayout(place: { measured, bounds in
            measured.indices.map { index in
                // One continuous path: the word sits where it landed, then glides into the stack.
                let rank = ranks[index]
                let settle = CGFloat(Self.glide(min(rank, 1.2) / 1.2))
                let sway = CGFloat(chance(index, 21) - 0.5) * 0.06
                let size = measured[index]
                // The colour bar's panel reaches past the bar (see below).
                let overhang = kinds.indices.contains(index) && kinds[index] == 1 ? u * 2.3 * 0.45 : 0
                let home = Self.mosaicHome(size, sway: sway, step: step, overhang: overhang,
                                           rightToLeft: rightToLeft, in: bounds)
                // The stack is flush to one column: each word's own edge (the
                // big setting shrinks towards it) sits on it.
                let column = rightToLeft ? bounds.maxX - bounds.width * 0.14 : bounds.minX + bounds.width * 0.14
                let stack = CGPoint(x: column + (rightToLeft ? -1 : 1) * size.width / 2,
                                    y: Self.mosaicRowY(rank: rank, step: step, in: bounds))
                return CGPoint(x: home.x + (stack.x - home.x) * settle, y: home.y + (stack.y - home.y) * settle)
            }
        }) {
            ForEach(runs) { run in
                let a = arrivals[run.id]
                let rank = ranks[run.id]
                let settle = Self.glide(min(rank, 1.2) / 1.2)
                let sample = sample(run, of: total)
                let tile = tiled(run)
                // Words come in the reading direction: from the right, or the left
                // for a right-to-left line.
                let side: Double = rightToLeft ? -1 : 1
                let bigSize: CGFloat = kinds[run.id] == 0 ? 3.0 : kinds[run.id] == 1 ? 2.3 : 2.4
                let morph = StageMotion.mosaicMorph(settle: settle, smallRatio: 0.6 / bigSize)
                ZStack(alignment: edge) {
                    // The big setting, shrinking and dimming as the next word takes over.
                    Group {
                        if kinds[run.id] == 0 {
                            // Huge, nearly the stage's width (inside its safe edge), with a star beside it.
                            let size = unit * 3.0
                            ZStack(alignment: .topTrailing) {
                                withReading(run, size: size, colour: sample.reading, maxWidth: stage.width * 0.9) {
                                    word(run, tile ? ink.tileText : sample.sung)
                                        .font(font(size, hero: true, id: run.id))
                                        .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: stage.width * 0.9)
                                        .background { tileBackground(tile, size: size) }
                                        .shadow(color: .black.opacity(0.28), radius: 8, y: 3)
                                }
                                Star4().fill(ink.tileColour).frame(width: size * 0.32, height: size * 0.32)
                                    .saturation(Self.mosaicAccentSaturation)
                                    .rotationEffect(.degrees(reduceMotion ? 0 : time * 40))
                                    .offset(x: size * 0.1, y: -size * 0.12)
                            }
                            .offset(x: reduceMotion ? 0 : (1 - min(a, 1)) * stage.width * 0.35 * side)
                        } else if kinds[run.id] == 1 {
                            // White on a colour bar, laid across a pale panel.
                            let size = unit * 2.3
                            let wipe = reduceMotion ? 1 : Self.glide((time - (run.start - 0.04)) / (0.32 / lineSpeed))
                            let plain = Text(run.revealed(at: time))
                                .font(font(size, hero: true, id: run.id))
                                .lineLimit(1).minimumScaleFactor(0.45).frame(maxWidth: stage.width * 0.7)
                            let tilt = (chance(run.id, 28) - 0.5) * 5
                            let readingInk = ink.tileText.opacity(LyricHighlight.readingOpacity)
                            ZStack {
                                // The pale panel behind, a little off-square, arriving a beat after the bar.
                                RoundedRectangle(cornerRadius: 4).fill(Color.white.opacity(0.9))
                                    .padding(.horizontal, -size * 0.55).padding(.vertical, -size * 0.34)
                                    .rotationEffect(.degrees(-tilt * 0.8))
                                    .offset(x: size * 0.12, y: size * 0.1)
                                    .opacity(Self.glide((time - (run.start + 0.05)) / 0.3))
                                    .scaleEffect(x: reduceMotion ? 1 : 0.6 + 0.4 * Self.glide((time - (run.start + 0.05)) / 0.3), y: 1)
                                // The bar and its word are printed together: one wipe
                                // lays them both down, so the bar is never seen
                                // apart from its word. A reading is printed on the
                                // bar under its word.
                                ZStack {
                                    withReading(run, size: size, colour: readingInk, maxWidth: stage.width * 0.7, shadow: false) { plain }
                                        .hidden()
                                        .padding(.horizontal, size * 0.28).padding(.vertical, size * 0.06)
                                        .background { ink.tileColour.saturation(Self.mosaicAccentSaturation) }
                                    withReading(run, size: size, colour: readingInk, maxWidth: stage.width * 0.7, shadow: false) {
                                        word(run, ink.tileText)
                                            .font(font(size, hero: true, id: run.id))
                                            .lineLimit(1).minimumScaleFactor(0.45).frame(maxWidth: stage.width * 0.7)
                                    }
                                }
                                .mask {
                                    GeometryReader { box in
                                        Rectangle().frame(width: box.size.width * wipe)
                                            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: side > 0 ? .leading : .trailing)
                                    }
                                }
                                .rotationEffect(.degrees(tilt))
                                Star4().fill(Color.white).frame(width: size * 0.3, height: size * 0.3)
                                    .shadow(color: .black.opacity(0.2), radius: 3)
                                    .rotationEffect(.degrees(reduceMotion ? 0 : time * 50))
                                    // Above the word, which a reading under it lifts.
                                    .offset(x: side * size * 0.1, y: -size * 0.62
                                            - (Self.reading(of: run, in: readings) == nil ? 0 : Self.readingSlot(size) / 2))
                                    .scaleEffect(wipe)
                            }
                            .shadow(color: .black.opacity(0.25), radius: 8, y: 3)
                        } else {
                            // Letter by letter, each rising onto its own baseline:
                            // one size, a small and steady rise and fall, so the
                            // word reads as set loosely, not as broken.
                            let size = unit * 2.4
                            withReading(run, size: size, colour: sample.reading, maxWidth: stage.width * 0.8) {
                            HStack(spacing: size * 0.03) {
                                ForEach(Array(run.text.enumerated()), id: \.offset) { index, character in
                                    let reveal = Self.glide((time - (run.start + Double(index) * 0.05)) / 0.25)
                                    let lift = (index.isMultiple(of: 2) ? -1.0 : 1.0) * (0.5 + 0.5 * chance(run.id * 31 + index, 25))
                                    Text(String(character))
                                        .font(font(size, hero: true, id: run.id))
                                        .foregroundStyle(sample.sung)
                                        .offset(y: CGFloat(lift) * size * 0.06
                                                + (reduceMotion ? 0 : CGFloat(1 - reveal) * size * 0.3))
                                        .rotationEffect(.degrees(lift * 5))
                                        .opacity(reveal)
                                }
                            }
                            .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: stage.width * 0.8)
                            }
                            .shadow(color: .black.opacity(0.28), radius: 8, y: 3)
                        }
                    }
                    .scaleEffect(morph.bigScale, anchor: UnitPoint(x: rightToLeft ? 1 : 0, y: 0.5))
                    .opacity(min(1, a * 1.6) * morph.bigOpacity)
                    // The small version it becomes, taking over once the big one has
                    // shrunk to its size.
                    let fade = Self.mosaicStackFade(rank: rank)
                    let small = Self.mosaicSmallSize(unit: unit)
                    withReading(run, size: small, colour: sample.reading.opacity(fade), alignment: flush, maxWidth: stage.width * 0.4) {
                        word(run, sample.rest.opacity(0.8 * fade))
                            .font(font(small, hero: true, id: run.id))
                            .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: stage.width * 0.4, alignment: edge)
                    }
                        // Its own width, so its reading sits flush with it too.
                        .fixedSize(horizontal: true, vertical: false)
                        .opacity(morph.smallOpacity)
                }
            }
        }
        .opacity(1 - leaving)
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
