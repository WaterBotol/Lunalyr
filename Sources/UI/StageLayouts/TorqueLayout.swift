import SwiftUI

// Torque: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.

extension StageLyricView {
    /// Torque's entry point, called by the dispatcher (`StageLyricView.layout`):
    /// `runs` is the whole line, `shown` the runs that have arrived.
    func torqueBody(runs: [StageRun], shown: [StageRun]) -> some View {
        torque(shown, total: runs.count)
    }

    /// Torque leaves Reduce Motion to the shared dissolve between steps
    /// when false (`StageLyricView.reducedDissolve`).
    static let torqueOwnsReduceMotion = false

    // MARK: Torque
    //
    // Words are wrung into place: each twists in about its own vertical axis,
    // streaks, and settles with a thick extruded edge, stacking upwards with
    // the older words smaller and tipped the other way.

    /// Torque's state for every shown word at one moment.
    struct TorqueState {
        var arrivals: [Double]
        var ranks: [Double]
        /// Shown size over the resting (newest word's) size.
        var scales: [CGFloat]
        /// The share of its room each word takes in the stack.
        var weights: [CGFloat]
        /// The room each word's reading takes under it, at the resting size
        /// (0 for none; `torqueReadingSlot`).
        var slots: [CGFloat] = []
    }

    /// Torque's words rest at this many units.
    static func torqueRestSize(unit: CGFloat) -> CGFloat { unit * 2.0 }

    /// The gap between a Torque word and its reading: clear of the thick edge.
    static func torqueReadingGap(_ restSize: CGFloat) -> CGFloat { restSize * 0.16 }

    /// The room a reading takes under a Torque word.
    static func torqueReadingSlot(_ restSize: CGFloat) -> CGFloat {
        readingSlot(restSize, gap: torqueReadingGap(restSize))
    }

    /// How visible a Torque word is from its rank: the newest three clearly,
    /// the fourth fading as it climbs, none after it, so the stack stays a
    /// readable few lines and the oldest never pile up at the stage's top.
    static func torqueVisibility(rank: Double) -> Double { max(0, min(1, 1 - (rank - 2.4) * 0.9)) }

    /// Torque's leading: the share of a word's measured height (without its
    /// reading) it takes in the stack. Open enough that the thick edge of one
    /// word clears the next.
    static let torqueLeading: CGFloat = 0.94

    static func torqueState(_ runs: [StageRun], at time: Double, speed: Double, seed: Int,
                            reduceMotion: Bool) -> TorqueState {
        let arrivals = runs.map { StageMotion.arrival($0.start, at: time, speed: speed, reduceMotion: reduceMotion) }
        let ranks = ranks(runs, at: time, speed: speed, seed: seed, reduceMotion: reduceMotion)
        // Set at the newest word's size; older words are scaled down, never re-set.
        let scales = ranks.map { CGFloat(max(0.55, 2.0 - 0.38 * min($0, 3.5)) / 2.0) }
        // The room each word takes: it grows as the word arrives, and is given
        // back as the word fades out at the top, so the stack neither jumps at
        // an onset nor sinks off the stage on a long line.
        let weights = runs.indices.map {
            CGFloat(StageMotion.weight(arrivals[$0]) * torqueVisibility(rank: ranks[$0]))
        }
        return TorqueState(arrivals: arrivals, ranks: ranks, scales: scales, weights: weights)
    }

    /// Where Torque puts each word's centre, from the sizes measured at the
    /// resting size.
    /// The room each word takes in Torque's stack: most of its height
    /// (`torqueLeading`); a reading takes all its room and a little more,
    /// as the stack sways and turns.
    static func torqueHeights(_ measured: [CGSize], _ state: TorqueState) -> [CGFloat] {
        func slot(_ index: Int) -> CGFloat { state.slots.indices.contains(index) ? state.slots[index] : 0 }
        return measured.indices.map {
            ((measured[$0].height - slot($0)) * torqueLeading + slot($0) * 1.2) * state.scales[$0] * state.weights[$0]
        }
    }

    /// The height Torque's stack needs: its words, and room at either end
    /// for the overlap and the turn. Taller than the stage (big type,
    /// readings), the stack is scaled down to fit.
    static func torqueHeight(_ measured: [CGSize], _ state: TorqueState, restSize: CGFloat) -> CGFloat {
        torqueHeights(measured, state).reduce(0, +) + restSize * 0.3
    }

    /// The share of the stage's height Torque keeps clear above and below its
    /// stack, so the oldest, tipped words never touch the top edge.
    static let torqueMargin: CGFloat = 0.07

    static func torqueStack(_ measured: [CGSize], _ state: TorqueState, seed: Int, in stage: CGRect) -> [CGPoint] {
        let ranks = state.ranks
        let bounds = stage.insetBy(dx: 0, dy: stage.height * torqueMargin)
        // The stack is centred whatever its height.
        let heights = torqueHeights(measured, state)
        var cursor = bounds.midY + heights.reduce(0, +) / 2
        var placed = measured.indices.reversed().map { index -> (Int, CGPoint) in
            let rank = ranks[index]
            let height = heights[index]
            let y = cursor - height / 2
            cursor -= height
            return (index, CGPoint(x: bounds.midX + CGFloat(sin(Double(index) * 2.1 + Double(seed % 5))) * bounds.width * 0.05 * CGFloat(min(rank, 1)), y: y))
        }
        // The newest word is the biggest and sits lowest, so a stack centred
        // by height still looks bottom-heavy. Centre it by weight instead,
        // as far as the stage has room to move it. A stack taller than its
        // bounds keeps its foot in them: the word being sung stays whole,
        // and the oldest words, already fading, go off the top. (The view
        // gives the stack the room it needs and scales it to the stage.)
        let total = heights.reduce(0, +)
        let weight = placed.reduce(CGFloat(0)) { $0 + heights[$1.0] * heights[$1.0] }
        if total > 0, weight > 0 {
            let centroid = placed.reduce(CGFloat(0)) { $0 + heights[$1.0] * heights[$1.0] * $1.1.y } / weight
            let room = (bounds.height - total) / 2
            let shift = room >= 0 ? max(-room, min(room, bounds.midY - centroid)) : room
            placed = placed.map { ($0.0, CGPoint(x: $0.1.x, y: $0.1.y + shift)) }
        }
        return placed.sorted { $0.0 < $1.0 }.map(\.1)
    }

    func torque(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let restSize = Self.torqueRestSize(unit: unit)
        var moment = Self.torqueState(runs, at: time, speed: speed, seed: seed, reduceMotion: reduceMotion)
        moment.slots = runs.map { Self.reading(of: $0, in: readings) != nil ? Self.torqueReadingSlot(restSize) : 0 }
        let state = moment
        let arrivals = state.arrivals, ranks = state.ranks, scales = state.scales
        let leaving = leave
        let stage = stageSize
        let seed = seed
        var stack = StageLayout(place: { measured, bounds in
            Self.torqueStack(measured, state, seed: seed, in: bounds)
        })
        stack.needs = { measured in
            CGSize(width: 0, height: Self.torqueHeight(measured, state, restSize: restSize) / (1 - 2 * Self.torqueMargin))
        }
        return stack {
            ForEach(runs) { run in
                let a = arrivals[run.id]
                let rank = ranks[run.id]
                let sample = sample(run, of: total)
                // 1 while this is the word being sung, easing to 0 as the next takes over.
                let live = StageMotion.liveness(rank: rank)
                let scale = scales[run.id]
                let size = restSize * scale
                let tile = tiled(run)
                // The word's own wringing, unless another word entrance has been chosen.
                let entrance = chosenEntrance(run, progress: a, size: size)
                let twist = reduceMotion || !entrance.native ? 0 : (1 - min(a, 1)) * 110 * (run.id.isMultiple(of: 2) ? 1 : -1)
                // Wrung and loosened again while it is being sung; once the next
                // word takes over it settles and holds still, so the words
                // being read never keep moving.
                let breathe = reduceMotion ? 0 : sin(time * 2.1 + Double(run.id) * 1.7) * 9 * live
                let wander = reduceMotion ? 0 : CGFloat(sin(time * 1.3 + Double(run.id) * 2.3)) * unit * CGFloat(0.12 * live)
                let lean = min(rank, 2) * 3 * (run.id.isMultiple(of: 2) ? 1 : -1)
                let restColour = tile ? ink.tileText : sample.rest.opacity(0.85)
                let liveColour = tile ? ink.tileText : sample.sung
                // The reading takes the sung colour while its word is the one
                // being sung, then settles back to the reading colour. It is
                // set clear of the thick edge below the word.
                withReading(run, size: restSize, colour: sample.reading,
                            liveColour: (tile ? sample.reading : sample.sung).opacity(LyricHighlight.readingOpacity),
                            live: live, gap: Self.torqueReadingGap(restSize), maxWidth: stage.width * 0.7 / scale) {
                    ZStack {
                        // The thick edge: darker copies stepping back, thinning away
                        // as the word hands over.
                        if live > 0.001 {
                            ZStack {
                                ForEach(1..<7, id: \.self) { step in
                                    word(run, sample.sung.opacity(0.55))
                                        .offset(x: CGFloat(step) * restSize * 0.012, y: CGFloat(step) * restSize * 0.022)
                                        .brightness(-0.25)
                                }
                            }
                            .opacity(live)
                        }
                        ZStack {
                            if live < 0.999 { word(run, restColour) }
                            if live > 0.001 { word(run, liveColour).opacity(live) }
                        }
                        .background { tileBackground(tile, size: restSize) }
                    }
                    .font(font(restSize, hero: true, id: run.id))
                    .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stage.width * 0.7 / scale)
                }
                .scaleEffect(scale)
                .shadow(color: .black.opacity(0.15 + 0.15 * live), radius: 6, y: 3)
                .rotationEffect(.degrees(reduceMotion ? 0 : -4 * live + lean * (1 - live)))
                .rotation3DEffect(.degrees(twist + breathe), axis: (x: 0, y: 1, z: 0), perspective: 0.6)
                .rotation3DEffect(.degrees(breathe * 0.5), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
                .rotation3DEffect(.degrees(entrance.fold), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
                .rotationEffect(.degrees(entrance.spin))
                .scaleEffect(entrance.scale)
                .offset(x: wander + entrance.offset.width, y: entrance.offset.height)
                .blur(radius: reduceMotion ? 0 : size * 0.07 * (1 - min(a, 1)))
                .opacity(min(1, a * 1.6) * Self.torqueVisibility(rank: rank))
            }
        }
        // A stack taller than the stage (`needs`) is shrunk to fit it.
        .visualEffect { stack, geometry in
            stack.scaleEffect(LyricStageFit.scale(content: geometry.size, available: stage))
        }
        // The line is wrung out as it leaves: it turns edge-on, rising the
        // way the stack grows, and fades early, so the next line's first
        // word -- landing in the middle while this one is still going --
        // never sits on a readable word (it used to sink onto it).
        .rotation3DEffect(.degrees(reduceMotion ? 0 : -10 - 70 * leaving), axis: (x: 0, y: 1, z: 0), perspective: 0.5)
        .offset(y: reduceMotion ? 0 : -stage.height * 0.22 * leaving)
        .opacity((1 - leaving) * (1 - leaving))
    }
}

extension StageMotion {
    /// Torque: how visible a word is from its rank. Old words fade out
    /// above the fourth, and give their room back as they go.
    static func torqueVisibility(rank: Double) -> Double { max(0, 1 - max(0, rank - 3) * 0.8) }
}
