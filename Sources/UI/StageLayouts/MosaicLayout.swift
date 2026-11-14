import SwiftUI

// Mosaic: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.
// This is the original Mosaic (as shipped up to 2.0.2), restored on the user's
// word, 14 Nov 2026: "old mosaic was better".

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
    // across a pale panel, or letter by letter on jumping baselines -- while the
    // words before it shrink into a small stack above.

    /// The size Mosaic's small stack sets its words at.
    static func mosaicSmallSize(unit: CGFloat) -> CGFloat { unit * 0.6 }

    /// How far apart Mosaic's stack sets its rows: a word, and its reading
    /// when the line has readings.
    static func mosaicStackStep(unit: CGFloat, readings: Bool) -> CGFloat {
        unit * 0.85 + (readings ? readingSlot(mosaicSmallSize(unit: unit)) : 0)
    }

    fileprivate func mosaic(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let arrivals = runs.map { arrive($0, speed: speed) }
        let ranks = ranks(runs, speed: speed)
        let leaving = leave
        // A word on a tile (Cutout · Highlight) is always the huge kind, which can carry one.
        let kinds = runs.map { tiled($0) ? 0 : Int(chance($0.id, 20) * 3) % 3 }
        let stage = stageSize
        let u = unit
        // The stack's rows make room for readings when the line has any.
        let step = Self.mosaicStackStep(unit: u, readings: runs.contains { Self.reading(of: $0, in: readings) != nil })
        return StageLayout(place: { measured, bounds in
            measured.indices.map { index in
                // One continuous path: the word sits where it landed, then glides into the stack.
                let rank = ranks[index]
                let settle = CGFloat(Self.glide(min(rank, 1.2) / 1.2))
                let sway = (chance(index, 21) - 0.5) * 0.24 * bounds.width
                let home = CGPoint(x: bounds.midX + sway, y: bounds.midY + (chance(index, 22) - 0.5) * 0.16 * bounds.height)
                let slot = CGFloat(min(3, max(rank, 1)))
                let stack = CGPoint(x: bounds.midX - bounds.width * 0.2 + CGFloat(index % 2) * bounds.width * 0.04,
                                    y: bounds.minY + bounds.height * 0.12 + (slot - 1) * step)
                return CGPoint(x: home.x + (stack.x - home.x) * settle, y: home.y + (stack.y - home.y) * settle)
            }
        }) {
            ForEach(runs) { run in
                let a = arrivals[run.id]
                let rank = ranks[run.id]
                let settle = Self.glide(min(rank, 1.2) / 1.2)
                let sample = sample(run, of: total)
                let tile = tiled(run)
                let side: Double = chance(run.id, 23) < 0.5 ? 1 : -1
                let bigSize: CGFloat = kinds[run.id] == 0 ? 3.0 : kinds[run.id] == 1 ? 2.3 : 2.4
                let morph = StageMotion.mosaicMorph(settle: settle, smallRatio: 0.6 / bigSize)
                ZStack {
                    // The big setting, shrinking and dimming as the next word takes over.
                    Group {
                        if kinds[run.id] == 0 {
                            // Huge, running off the edge, with a star beside it.
                            let size = unit * 3.0
                            ZStack(alignment: .topTrailing) {
                                withReading(run, size: size, colour: sample.reading, maxWidth: stage.width * 1.1) {
                                    word(run, tile ? ink.tileText : sample.sung)
                                        .font(font(size, hero: true, id: run.id))
                                        .lineLimit(1).minimumScaleFactor(0.5).frame(maxWidth: stage.width * 1.1)
                                        .background { tileBackground(tile, size: size) }
                                        .shadow(color: .black.opacity(0.28), radius: 8, y: 3)
                                }
                                Star4().fill(ink.tileColour).frame(width: size * 0.32, height: size * 0.32)
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
                            ZStack {
                                // The pale panel behind, a little off-square, arriving a beat after the bar.
                                RoundedRectangle(cornerRadius: 4).fill(Color.white.opacity(0.9))
                                    .padding(.horizontal, -size * 0.55).padding(.vertical, -size * 0.34)
                                    .rotationEffect(.degrees(-tilt * 0.8))
                                    .offset(x: size * 0.12, y: size * 0.1)
                                    .opacity(Self.glide((time - (run.start + 0.05)) / 0.3))
                                    .scaleEffect(x: reduceMotion ? 1 : 0.6 + 0.4 * Self.glide((time - (run.start + 0.05)) / 0.3), y: 1)
                                // The bar wipes across, then the word lands on it; a
                                // reading is printed on the bar under its word.
                                let readingInk = ink.tileText.opacity(LyricHighlight.readingOpacity)
                                withReading(run, size: size, colour: readingInk, maxWidth: stage.width * 0.7, shadow: false) { plain }
                                    .hidden()
                                    .padding(.horizontal, size * 0.28).padding(.vertical, size * 0.06)
                                    .background {
                                        ink.tileColour
                                            .mask(alignment: side > 0 ? .leading : .trailing) {
                                                GeometryReader { box in Rectangle().frame(width: box.size.width * wipe) }
                                            }
                                    }
                                    .rotationEffect(.degrees(tilt))
                                withReading(run, size: size, colour: readingInk, maxWidth: stage.width * 0.7, shadow: false) {
                                    word(run, ink.tileText)
                                        .font(font(size, hero: true, id: run.id))
                                        .lineLimit(1).minimumScaleFactor(0.45).frame(maxWidth: stage.width * 0.7)
                                }
                                    .rotationEffect(.degrees(tilt))
                                    .opacity(Self.glide((time - (run.start + 0.1)) / 0.2))
                                    .offset(x: reduceMotion ? 0 : CGFloat(1 - wipe) * size * 0.5 * side)
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
                            // Letter by letter, each on its own baseline.
                            let size = unit * 2.4
                            withReading(run, size: size, colour: sample.reading, maxWidth: stage.width * 0.8) {
                            HStack(spacing: size * 0.02) {
                                ForEach(Array(run.text.enumerated()), id: \.offset) { index, character in
                                    let reveal = Self.glide((time - (run.start + Double(index) * 0.05)) / 0.25)
                                    Text(String(character))
                                        .font(font(size * (0.85 + 0.3 * CGFloat(chance(run.id * 31 + index, 24))), hero: true, id: run.id))
                                        .foregroundStyle(sample.sung)
                                        .offset(y: CGFloat(chance(run.id * 31 + index, 25) - 0.5) * size * 0.28
                                                + (reduceMotion ? 0 : CGFloat(1 - reveal) * size * 0.3))
                                        .rotationEffect(.degrees((chance(run.id * 31 + index, 26) - 0.5) * 22))
                                        .opacity(reveal)
                                }
                            }
                            .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: stage.width * 0.8)
                            }
                            .shadow(color: .black.opacity(0.28), radius: 8, y: 3)
                        }
                    }
                    .scaleEffect(morph.bigScale)
                    .opacity(min(1, a * 1.6) * morph.bigOpacity)
                    // The small version it becomes, taking over once the big one has
                    // shrunk to its size.
                    let fade = max(0, 1 - (rank - 1.5) * 0.6)
                    let small = Self.mosaicSmallSize(unit: unit)
                    withReading(run, size: small, colour: sample.reading.opacity(fade), maxWidth: stage.width * 0.4) {
                        word(run, sample.rest.opacity(0.75 * fade))
                            .font(font(small, hero: true, id: run.id))
                            .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: stage.width * 0.4)
                    }
                        .opacity(morph.smallOpacity)
                }
            }
        }
        .opacity(1 - leaving)
    }

}
