import SwiftUI

// Hinge: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.

extension StageLyricView {
    /// Hinge's entry point, called by the dispatcher (`StageLyricView.layout`):
    /// `runs` is the whole line, `shown` the runs that have arrived.
    func hingeBody(runs: [StageRun], shown: [StageRun]) -> some View {
        hinge(shown, total: runs.count)
    }

    /// Hinge leaves Reduce Motion to the shared dissolve between steps
    /// when false (`StageLyricView.reducedDissolve`).
    static let hingeOwnsReduceMotion = false

    // MARK: Hinge
    //
    // Picture the corner of a cube seen from a slant. Text sits on its two
    // adjacent faces: the first two words as a tight pair, then the third as a
    // big bar on the left face, and from the fourth on a tall column down the
    // right face, each word a little bigger than the one above. Both faces
    // meet at the vertical edge in the middle and fall away from it in
    // perspective. When the line ends the faces swing shut edge-on.

    /// A Hinge column word's type size (the column's j-th word, at rest).
    static func hingeColumnFont(_ j: Int, unit u: CGFloat) -> CGFloat { u * min(2.8, 1.2 + 0.24 * CGFloat(j)) }

    /// How much the whole Hinge corner is scaled to fit the stage, and how
    /// much its column has shrunk. Every word counts by its weight (how far
    /// it has arrived), so neither steps as a word is added.
    /// `readings` are the words' readings ("Below lyrics"), counted in the
    /// height and width they add, so a corner with them is shrunk to fit too.
    /// How much more room than its own height a reading takes in Hinge's
    /// column and pair, whose words overlap their neighbours' leading
    /// (and the column is drawn 10% taller than measured).
    static let hingeReadingRoom: CGFloat = 1.4

    /// The size Hinge's pair sizes its readings from: larger than the pair's
    /// own type, which is shown small once it is pushed into the corner.
    static func hingePairReadingBase(unit u: CGFloat) -> CGFloat { u * 0.95 * 1.4 }

    /// How much Hinge's pair shrinks as the bar pushes it into the corner.
    static let hingePairTuck: CGFloat = 0.2

    static func hingeFit(_ texts: [String], weights: [CGFloat], settle: Double, unit u: CGFloat,
                         stage: CGSize, readings: [String?] = []) -> (fit: CGFloat, shrink: CGFloat) {
        let columnCount = max(0, min(texts.count, weights.count) - 3)
        let columnWeights = (0..<columnCount).map { weights[$0 + 3] }
        let shrink = CGFloat(StageMotion.hingeColumnShrink(Double(columnWeights.reduce(0, +))))
        func reading(_ index: Int) -> String? { readings.indices.contains(index) ? readings[index] : nil }
        /// The room a word's reading adds below it, and the reading's width.
        func readingRoom(_ index: Int, font: CGFloat) -> (height: CGFloat, width: CGFloat) {
            guard let text = reading(index), !text.isEmpty else { return (0, 0) }
            return (readingSlot(font), CGFloat(text.count) * readingSize(font) * 0.55)
        }
        let heightSum = (0..<columnCount).map { j -> CGFloat in
            let font = hingeColumnFont(j, unit: u)
            // A column word takes its line's height (about 1.2 of its size) less
            // the 10% it overlaps, drawn 10% taller: counted as 0.95 of its size
            // the corner was fitted too large, and a long column ran off the foot.
            return (font * 1.12 + readingRoom(j + 3, font: font).height * hingeReadingRoom) * shrink * columnWeights[j]
        }.reduce(0, +)
        // The whole corner grows to fill the stage once it has a bar to hang from.
        let barReading = texts.count > 2 ? readingRoom(2, font: u * 1.6) : (height: 0, width: 0)
        let barWidth: CGFloat = texts.count > 2 ? max(CGFloat(texts[2].count) * u * 1.7 * 0.66, barReading.width) : 0
        var columnWidth: CGFloat = 0
        for j in 0..<columnCount {
            let font = hingeColumnFont(j, unit: u)
            let word = CGFloat(texts[j + 3].count) * font * 0.66
            let width = max(word, readingRoom(j + 3, font: font).width) * shrink * 0.84 * columnWeights[j]
            columnWidth = max(columnWidth, width)
        }
        let widest: CGFloat = max(barWidth, columnWidth)
        let structureWidth: CGFloat = max(2 * widest + u * 0.3, u * 3)
        // The pair's readings stack above the bar, with the pair at its
        // corner size (a little over half its own).
        let pairReadings = (0..<min(2, texts.count)).map {
            readingRoom($0, font: hingePairReadingBase(unit: u)).height * hingeReadingRoom * 0.8
        }.reduce(0, +)
        // The pair stays readable in the corner (`hingePairTuck`), so it needs more room above the bar.
        let structureHeight = max(heightSum, u * 2.4 + barReading.height) + u * 1.2 + pairReadings
        let fill: CGFloat = min(2.0, stage.width * 0.8 / structureWidth, stage.height * 0.78 / structureHeight)
        let fitHeight: CGFloat = min(1, stage.height * 0.7 / max(heightSum + u * 1.2 + pairReadings, 1))
        let grow: CGFloat = (max(fill, 1) - 1) * CGFloat(settle)
        return (fitHeight * (1 + grow), shrink)
    }

    func hinge(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let arrivals = runs.map { arrive($0, speed: speed) }
        // Each word takes its room as it arrives, so nothing jumps at an onset.
        let weights = arrivals.map { CGFloat(StageMotion.weight($0)) }
        let barStart = runs.count > 2 ? runs[2].start : .infinity
        // How far the bar has settled from huge to its place, and how far the
        // pair has been pushed to the corner.
        let hasBar = runs.count > 2
        let settle = reduceMotion ? (hasBar ? 1 : 0) : Easing.smoothstep((time - barStart - 0.25) / 0.5)
        let pushed = !hasBar ? 0 : reduceMotion ? 1 : Easing.smoothstep((time - barStart + 0.05) / 0.4)
        let columnCount = max(0, runs.count - 3)
        let columnWeights = (0..<columnCount).map { weights[$0 + 3] }
        let u = unit
        let (fit, shrink) = Self.hingeFit(runs.map(\.text), weights: weights, settle: settle, unit: u, stage: stageSize,
                                          readings: runs.map { Self.reading(of: $0, in: readings) })
        // Type is set at its resting size and scaled, so no word is
        // re-shaped every frame; the layout scales what it measured to match.
        func columnFont(_ j: Int) -> CGFloat { Self.hingeColumnFont(j, unit: u) }
        func columnSize(_ j: Int) -> CGFloat { columnFont(j) * shrink }
        func columnScale(_ j: Int) -> CGFloat { shrink * (1 + 0.3 * (1 - CGFloat(arrivals[j + 3]))) }
        let pairWeight = runs.count > 1 ? weights[1] : 0
        // Pushed into the corner the pair stays about half the bar's size, big
        // enough to read, rather than shrinking to a caption.
        let pairScale = (1 + (1.3 / 0.95 - 1) * (1 - pairWeight)) * (1 - Self.hingePairTuck * CGFloat(pushed))
        let barScale = (3.4 - 1.8 * CGFloat(settle)) / 1.6
        let firstColumn = columnCount > 0 ? columnWeights[0] : 0
        // The faces swing shut as the words fold away, all within the line's exit.
        let close = reduceMotion ? 0 : vc_settle(time - exitStart, StageMotion.hingeClose)
        // Only a little: shut all the way, the faces crushed every word into a
        // sliver at the edge. The words fold themselves away.
        let faceAngle = reduceMotion ? 0.0 : 52 + (64 - 52) * close
        let leftRuns = runs.filter { $0.id < 3 }
        let rightRuns = runs.filter { $0.id >= 3 }
        let stage = stageSize
        // The room each word's reading takes under it, at the word's resting
        // size: the layout counts it apart from the word, which overlaps
        // its neighbours' leading where a reading cannot.
        let slots: [CGFloat] = runs.map { run in
            guard Self.reading(of: run, in: readings) != nil else { return 0 }
            return Self.readingSlot(run.id < 2 ? Self.hingePairReadingBase(unit: u)
                                    : run.id == 2 ? u * 1.6 : Self.hingeColumnFont(run.id - 3, unit: u))
        }
        let room = Self.hingeReadingRoom
        let face: (Bool) -> AnyView = { isLeft in
            let items = isLeft ? leftRuns : rightRuns
            return AnyView(StageLayout(place: { measured, bounds in
                let edge = bounds.midX
                let gap = u * 0.12
                if isLeft {
                    let barSize = measured.count > 2
                        ? CGSize(width: measured[2].width * barScale, height: measured[2].height * barScale) : .zero
                    let totalColumn = rightRuns.indices.map {
                        (columnSize($0) * 0.95 + slots[$0 + 3] * room * shrink) * columnWeights[$0]
                    }.reduce(0, +)
                    // The bar moves up beside the column as its first word arrives.
                    let top = bounds.midY - (u + (totalColumn / 2 - u) * firstColumn)
                    var centres = [CGPoint](repeating: CGPoint(x: edge / 2, y: bounds.midY), count: measured.count)
                    // The bar is placed by its word; its reading hangs below.
                    let barSlot = measured.count > 2 ? slots[2] * barScale : 0
                    let barWord = barSize.height - barSlot
                    if measured.count > 2 {
                        let settledX = edge - gap - barSize.width / 2
                        let settledY = bounds.midY + (top + barWord * 0.45 - bounds.midY) * firstColumn
                        centres[2] = CGPoint(x: edge / 2 + (settledX - edge / 2) * settle, y: bounds.midY + (settledY - bounds.midY) * settle)
                    }
                    // The pair's words overlap a little; their readings do not.
                    func pairRoom(_ index: Int) -> CGFloat {
                        guard measured.indices.contains(index) else { return pairRoom(0) }
                        return (measured[index].height - slots[index]) * 0.78 + slots[index] * room
                    }
                    let pairRead = slots.prefix(2).contains { $0 > 0 }
                    let pairHeight = measured.isEmpty ? 0 : (pairRead ? (pairRoom(0) + pairRoom(1)) / 2 : pairRoom(0)) * pairScale
                    let home = CGPoint(x: edge / 2, y: bounds.midY)
                    // A reading under the pair's lower word lifts the pair clear of the bar.
                    let lowerSlot = measured.count > 1 ? slots[1] * room * pairScale : 0
                    // In the corner the pair sits just above the bar's capitals,
                    // flush with the bar's end at the hinge edge, as one block of type.
                    let pairWord = measured.isEmpty ? 0 : (measured[0].height - slots[0]) * pairScale
                    let cornerY = measured.count > 2
                        ? centres[2].y - barWord * 0.29 - u * 0.1 - pairWord * 0.58 - lowerSlot : home.y
                    let spotY = home.y + (cornerY - home.y) * pushed
                    func pairX(_ index: Int) -> CGFloat {
                        guard measured.count > 2 else { return home.x }
                        return home.x + (edge - gap - measured[index].width * pairScale / 2 - home.x) * pushed
                    }
                    let split = pairHeight * 0.5 * pairWeight
                    if !measured.isEmpty { centres[0] = CGPoint(x: pairX(0), y: spotY - split) }
                    if measured.count > 1 { centres[1] = CGPoint(x: pairX(1), y: spotY + split * (1 - 0.25 * pushed)) }
                    if measured.count > 2 { centres[2].y += barSlot / 2 }
                    return centres
                }
                let heights = measured.indices.map {
                    ((measured[$0].height - slots[$0 + 3]) * 0.9 + slots[$0 + 3] * room) * columnScale($0) * columnWeights[$0]
                }
                var cursor = bounds.midY - heights.reduce(0, +) / 2
                return measured.indices.map { index in
                    defer { cursor += heights[index] }
                    return CGPoint(x: edge + gap + measured[index].width * columnScale(index) / 2, y: cursor + heights[index] / 2)
                }
            }) {
                ForEach(items) { run in
                    let sample = sample(run, of: total)
                    let a = arrivals[run.id]
                    let isPair = run.id < 2, isBar = run.id == 2
                    let fontSize: CGFloat = isPair ? u * 0.95 : isBar ? u * 1.6 : columnFont(run.id - 3)
                    let scale: CGFloat = isPair ? pairScale : isBar ? barScale : columnScale(run.id - 3)
                    let size = fontSize * scale
                    let tile = tiled(run)
                    // Words fold away oldest first.
                    // Squared, so each fold starts gently and speeds up as it goes (an exit eases in).
                    let settleOut = reduceMotion ? 0
                        : StageMotion.hingeFold(order: Double(run.id) / Double(max(total - 1, 1)), at: time, exitStart: exitStart)
                    let out = settleOut * settleOut
                    let widest = stage.width * 0.4 / max(scale, 0.05)
                    // The reading hangs from the word's hinge side, as the word does.
                    // The bar arrives huge and settles into place; its reading
                    // comes once it is nearly there, rather than swing in huge.
                    withReading(run, size: isPair ? Self.hingePairReadingBase(unit: u) : fontSize,
                                colour: isPair ? sample.reading.opacity(0.95) : sample.reading,
                                alignment: isPair ? .center : isLeft ? .trailing : .leading, maxWidth: widest,
                                shown: isBar ? Easing.smoothstep((settle - 0.5) / 0.5) : 1) {
                        word(run, tile ? ink.tileText : isPair ? sample.rest.opacity(0.95) : sample.sung, uppercase: true)
                            .font(font(fontSize, hero: true, id: run.id))
                            .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: widest)
                            .background { tileBackground(tile, size: fontSize) }
                    }
                        .shadow(color: .black.opacity(tile ? 0 : 0.3), radius: 7, y: 2)
                        .scaleEffect(x: scale * (isBar || isPair ? 1 : 0.86), y: scale * (isBar || isPair ? 1 : 1.1))
                        // Each word unfolds from the edge like a panel on a hinge, with a
                        // little spring; on the way out they fold shut one after another.
                        .blur(radius: reduceMotion ? 0 : size * 0.08 * (1 - min(a, 1)))
                        .rotation3DEffect(.degrees(reduceMotion ? 0 : (isLeft ? 1 : -1) * (84 * (1 - a) - 80 * out)),
                                          axis: (x: 0, y: 1, z: 0), anchor: isLeft ? .trailing : .leading, perspective: 0.6)
                        // Gone before it is edge-on, so no word is seen squashed thin.
                        .opacity(min(1, a * 1.6) * (1 - Easing.smoothstep(out / 0.7)))
                }
            })
        }
        return ZStack {
            face(true)
                .rotation3DEffect(.degrees(faceAngle), axis: (x: 0, y: 1, z: 0), anchor: .center, perspective: 0.8)
            face(false)
                .rotation3DEffect(.degrees(-faceAngle), axis: (x: 0, y: 1, z: 0), anchor: .center, perspective: 0.8)
        }
        // Looked at from a little above.
        .rotation3DEffect(.degrees(reduceMotion ? 0 : 9), axis: (x: 1, y: 0, z: 0), perspective: 0.5)
        .scaleEffect(fit)
        // Reduce Motion: nothing folds, so the line fades out as every other line does.
        .opacity(reduceMotion ? 1 - leave : 1)
    }
}

extension StageMotion {
    /// Hinge's faces swinging shut, and the extra delay of its last word's fold.
    static let hingeClose = 0.6
    static let hingeFoldStagger = 0.24

    /// Hinge: the column shrinks by 15% as its seventh word arrives, not at once.
    static func hingeColumnShrink(_ arrived: Double) -> Double { 1 - 0.15 * Easing.smoothstep(arrived - 6) }

    /// Hinge: how far one word has folded shut. Oldest first; all of them are
    /// shut by the end of the line's exit span.
    static func hingeFold(order: Double, at time: Double, exitStart: Double) -> Double {
        vc_settle(time - exitStart - hingeFoldStagger * min(1, max(0, order)), WallpaperMotion.lineExit)
    }
}
