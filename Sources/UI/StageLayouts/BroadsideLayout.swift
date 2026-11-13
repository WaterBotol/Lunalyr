import SwiftUI

// Broadside: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.

extension StageLyricView {
    /// Broadside's entry point, called by the dispatcher (`StageLyricView.layout`):
    /// `runs` is the whole line, `shown` the runs that have arrived.
    func broadsideBody(runs: [StageRun], shown: [StageRun]) -> some View {
        broadside(shown, total: runs.count)
    }

    /// Broadside leaves Reduce Motion to the shared dissolve between steps
    /// when false (`StageLyricView.reducedDissolve`).
    static let broadsideOwnsReduceMotion = false

    // MARK: Broadside
    //
    // One strip of type that slides along as words are sung: big, round, hard
    // shadowed words, with the little ones ("the", "of") as small paper
    // labels riding on the shoulder of the word before. Words that slide toward an edge of the
    // stage fade out whole rather than being cut off mid-letter.

    private static let broadsideSpace = "broadside.strip"

    /// Where Broadside puts each word's centre: the big words in one row, a
    /// little word as a small paper label riding on the shoulder of the big
    /// word before it (above its letters, flush with its end, never on top of
    /// them), and the strip glided so the line builds up centred until it is
    /// wider than the stage; after that the newest big word stays near the
    /// right, the line read back to its left. A label does not move the strip,
    /// so the word it rides on stays in view with it: in the row, a label took
    /// room that pushed that word off a small stage, leaving the label alone.
    /// `slots` is the room each big word's reading takes under it (0 for
    /// none): the word itself is what sits on the strip's line, its reading below.
    static func broadsidePlaces(_ measured: [CGSize], little: [Bool], slots: [CGFloat], moves: [Double], unit: CGFloat,
                                in bounds: CGRect) -> [CGPoint] {
        func slot(_ index: Int) -> CGFloat { slots.indices.contains(index) ? slots[index] : 0 }
        func isLittle(_ index: Int) -> Bool { little.indices.contains(index) && little[index] }
        var xs = [CGFloat](repeating: 0, count: measured.count)
        var ys = [CGFloat](repeating: bounds.midY, count: measured.count)
        var cursor: CGFloat = 0
        var previousBig: Int?
        for index in measured.indices {
            let width = measured[index].width
            if isLittle(index), let prior = previousBig {
                // On the word's shoulder: just clear of the top of its letters.
                let wordHeight = measured[prior].height - slot(prior)
                xs[index] = xs[prior] + measured[prior].width / 2 - width / 2
                ys[index] = bounds.midY - wordHeight * 0.36 - unit * 0.06 - measured[index].height / 2
                continue
            }
            xs[index] = cursor + width / 2
            ys[index] = bounds.midY + slot(index) / 2
            cursor += width + unit * 0.45
            previousBig = index
        }
        // Where the strip stands once each big word has arrived: the words so
        // far centred while they fit, then the newest word's end held clear of
        // the right edge, where the edge fade starts. A label keeps the stop
        // of the word it rides on.
        let margin = bounds.width * broadsideMargin + unit * broadsideFadeSpan + unit * 0.1
        let firstLeft = (xs.first ?? 0) - (measured.first?.width ?? 0) / 2
        var stops = [CGFloat](repeating: 0, count: measured.count)
        for index in measured.indices {
            if isLittle(index), index > 0 { stops[index] = stops[index - 1]; continue }
            let half = measured[index].width / 2
            let row = xs[index] + half - firstLeft
            let shift = min(max(0, row / 2 - half), max(0, bounds.width / 2 - margin - half))
            stops[index] = xs[index] - shift
        }
        var camera = stops.first ?? 0
        for index in stops.indices where index > 0 {
            camera += (moves.indices.contains(index) ? moves[index] : 1) * (stops[index] - stops[index - 1])
        }
        return measured.indices.map { CGPoint(x: xs[$0] - camera + bounds.midX, y: ys[$0]) }
    }

    /// Broadside fades a word out, whole, as its box nears an edge of the
    /// stage this far in (a share of the stage's width): no word is ever
    /// cut through a letter by the edge.
    static let broadsideMargin: CGFloat = 0.05
    /// The distance (in type units) over which a word fades as it nears the margin.
    static let broadsideFadeSpan: CGFloat = 0.7

    /// How visible a Broadside word is from where its box sits: 1 well inside
    /// the stage, fading to 0 as either end reaches the margin.
    /// `lead` widens the box to the left: a label passes it the width of the
    /// word it rides on (estimated), so it leaves with that word, not after it.
    static func broadsideEdgeFade(_ frame: CGRect, stageWidth: CGFloat, unit: CGFloat, lead: CGFloat = 0) -> Double {
        let frame = CGRect(x: frame.minX - lead, y: frame.minY, width: frame.width + lead, height: frame.height)
        let margin = stageWidth * broadsideMargin
        let span = max(unit * broadsideFadeSpan, 1)
        let left = (frame.minX - margin) / span
        let right = (stageWidth - margin - frame.maxX) / span
        return Easing.smoothstep(Double(left)) * Easing.smoothstep(Double(right))
    }

    func broadside(_ runs: [StageRun], total: Int) -> some View {
        // Snappy: each word springs in, and the strip glides along with it;
        // the glide hands over from word to word without a jolt.
        let speed = lineSpeed
        let arrivals = runs.map { arrive($0, speed: speed) }
        let moves = runs.map { handoff($0, speed: speed) }
        // Few labels, well apart: one only after a big word, and not within
        // three words of the last.
        let candidates = runs.map { isLittle($0.text) }
        var little = [Bool](repeating: false, count: runs.count)
        var sinceLabel = 99
        for index in runs.indices {
            if candidates[index], index > 0, !candidates[index - 1], sinceLabel >= 3 {
                little[index] = true
                sinceLabel = 0
            } else {
                sinceLabel += 1
            }
        }
        let bigSize = unit * 1.7, smallSize = unit * 0.52
        let hard = unit * 0.05
        let leaving = leave
        // Words more than a couple of big words behind drop out of the strip.
        var anchor = [Int](repeating: 0, count: runs.count)
        var lastBig = 0
        for index in runs.indices { if !little[index] { lastBig = index }; anchor[index] = lastBig }
        let bigAfter: [Double] = runs.indices.map { index in
            ((anchor[index] + 1)..<max(runs.count, anchor[index] + 1)).filter { !little[$0] }.map { moves[$0] }.reduce(0, +)
        }
        let keep: [Double] = bigAfter.map { max(0, min(1, 1 - ($0 - 1.6) * 0.9)) }
        // A big word's reading hangs under it, in a slot of known height,
        // so the words themselves stay on one line across the strip.
        let slots: [CGFloat] = runs.map {
            !little[$0.id] && Self.reading(of: $0, in: readings) != nil ? Self.readingSlot(bigSize) : 0
        }
        let u = unit
        let stageWidth = stageSize.width
        // How far a label's word reaches left of the label (about half an em a
        // character), so the label fades out with the word it rides on.
        let rides: [CGFloat] = runs.indices.map { index in
            guard little[index], index > 0 else { return 0 }
            return max(0, CGFloat(runs[anchor[index]].text.count) * bigSize * 0.5 - smallSize * 2)
        }
        return StageLayout(place: { measured, bounds in
            Self.broadsidePlaces(measured, little: little, slots: slots, moves: moves, unit: u, in: bounds)
        }) {
            ForEach(runs) { run in
                let sample = sample(run, of: total)
                let a = arrivals[run.id]
                if little[run.id] {
                    // With "Cutout · Highlight" the labels grow into real tiles.
                    let labelSize = tiled(run) ? smallSize * 1.4 : smallSize
                    // A label's reading is printed on the label with it.
                    withReading(run, size: labelSize, colour: ink.tileText.opacity(LyricHighlight.readingOpacity),
                                maxWidth: stageSize.width * 0.7, shadow: false) {
                        word(run, ink.tileText)
                            .font(font(labelSize, hero: true, id: run.id))
                            .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7)
                    }
                        .padding(.horizontal, labelSize * 0.34).padding(.vertical, labelSize * 0.16)
                        .background(ink.tileColour, in: RoundedRectangle(cornerRadius: 3))
                        .rotationEffect(.degrees(run.id.isMultiple(of: 2) ? -3 : 2.5))
                        .scaleEffect(reduceMotion ? 1 : 0.8 + 0.2 * a)
                        .opacity(min(1, a * 1.6) * keep[run.id])
                        .visualEffect { label, box in
                            label.opacity(Self.broadsideEdgeFade(box.frame(in: .named(Self.broadsideSpace)),
                                                                 stageWidth: stageWidth, unit: u, lead: rides[run.id]))
                        }
                } else {
                    let tile = tiled(run)
                    withReading(run, size: bigSize, colour: sample.reading, maxWidth: stageSize.width * 0.7) {
                        ZStack {
                            word(run, .black.opacity(tile ? 0 : 0.34)).offset(x: hard, y: hard * 1.15)
                            word(run, tile ? ink.tileText : sample.sung)
                        }
                        .font(font(bigSize, hero: true, id: run.id))
                        .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7)
                        .background { tileBackground(tile, size: bigSize) }
                    }
                    .scaleEffect(reduceMotion ? 1 : 0.86 + 0.14 * a)
                    .opacity(min(1, a * 1.6) * keep[run.id])
                    .visualEffect { big, box in
                        big.opacity(Self.broadsideEdgeFade(box.frame(in: .named(Self.broadsideSpace)),
                                                           stageWidth: stageWidth, unit: u))
                    }
                }
            }
        }
        .coordinateSpace(.named(Self.broadsideSpace))
        .opacity(1 - leaving)
    }
}
