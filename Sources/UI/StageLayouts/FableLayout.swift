import SwiftUI

// Fable: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.

extension StageLyricView {
    /// Fable's entry point, called by the dispatcher (`StageLyricView.layout`):
    /// `runs` is the whole line, `shown` the runs that have arrived.
    func fableBody(runs: [StageRun], shown: [StageRun]) -> some View {
        fable(runs, total: runs.count)
    }

    /// Fable leaves Reduce Motion to the shared dissolve between steps
    /// when false (`StageLyricView.reducedDissolve`).
    static let fableOwnsReduceMotion = true

    // MARK: Fable
    //
    // Every line is set in its own shape -- a staircase, an arch, a zigzag, a
    // wave or a ramp -- and marks the word being sung with its own accent: an
    // underline, a ring, a tile, a star or corner brackets. Neighbouring lines
    // never share a shape or an accent, and a line always looks the same
    // (both come from the line, see `FableStyle`). The whole line is laid out
    // from its first word, so words land in their final places and nothing
    // reflows; each word makes a short entrance of its own and then holds
    // still. The block is fitted inside the stage, readings and all.

    func fable(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let style = FableStyle.forLine(seed)
        let rightToLeft = LyricScript.lineIsRightToLeft(runs.map(\.text))
        let leaving = leave
        let stage = stageSize
        // Room kept round the block for shadows, accents and entrances.
        // At least the 5 % title-safe margin on every side.
        let marginX = max(unit * 0.5, stage.width * 0.05)
        let marginY = max(unit * 0.5, stage.height * 0.05)
        let available = CGSize(width: max(1, stage.width - 2 * marginX), height: max(1, stage.height - 2 * marginY))
        let hero = FableStyle.heroIndex(runs.map(\.text))
        // The word being sung: from its onset until the next word's.
        let current = runs.lastIndex { time >= $0.start - 0.04 }
        return FableLayout(shape: style.shape, rightToLeft: rightToLeft, available: available,
                           gap: unit * 0.08, rowGap: unit * 0.12) {
            ForEach(runs) { run in
                let index = run.id
                let size = fableSize(run, hero: hero == index, count: runs.count)
                // Fable's words never travel under Reduce Motion, so its arrival
                // only fades them in and stays smooth there.
                let a = StageMotion.arrival(run.start, at: time, speed: speed)
                let sungAt = run.start - 0.04
                let shown = time >= sungAt
                // How much of the accent this word wears: it comes with the word and
                // passes to the next one as that is sung. The last word keeps it.
                let next = runs.indices.contains(index + 1) ? runs[index + 1].start : .infinity
                let rise = Self.glide((time - sungAt) / 0.18)
                let fall = next.isFinite ? 1 - Self.glide((time - (next - 0.04)) / 0.3) : 1
                let wear = shown ? rise * fall : 0
                let entrance = chosenEntrance(run, progress: a, size: size)
                let kind = Int(chance(index, 73) * 4) % 4
                let motion: (offset: CGSize, scale: CGFloat, spin: Double) = reduceMotion || !entrance.native
                    ? (.zero, 1, 0) : FableStyle.entrance(kind, progress: a, size: size, side: rightToLeft ? -1 : 1)
                fableWord(run, size: size, style: style, wear: wear, rightToLeft: rightToLeft,
                          sample: sample(run, of: total))
                    .opacity(shown ? min(1, a * 1.8) * (0.8 + 0.2 * (index == current ? 1 : wear)) : 0)
                    .rotationEffect(.degrees(reduceMotion ? 0 : (chance(index, 72) - 0.5) * style.shape.tilt))
                    .rotation3DEffect(.degrees(entrance.fold), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
                    .rotationEffect(.degrees(motion.spin + entrance.spin), anchor: .bottom)
                    .scaleEffect(motion.scale * entrance.scale)
                    .offset(x: motion.offset.width + entrance.offset.width, y: motion.offset.height + entrance.offset.height)
            }
        }
        // One shadow for the block, not one per word.
        .compositingGroup()
        .shadow(color: .black.opacity(0.3), radius: max(3, unit * 0.08), y: 2)
        // Long lines and big readings are shrunk to fit, never cut off.
        .visualEffect { block, geometry in
            block.scaleEffect(LyricStageFit.scale(content: geometry.size, available: available))
        }
        .offset(y: reduceMotion ? 0 : -stage.height * 0.04 * leaving)
        .opacity(1 - leaving)
        .frame(width: stage.width, height: stage.height)
    }

    /// A word's size, fixed for the whole line so nothing reflows: the
    /// longest word is set larger, the little ones a touch smaller.
    private func fableSize(_ run: StageRun, hero: Bool, count: Int) -> CGFloat {
        if count <= 2 || hero { return unit * 1.75 }
        if isLittle(run.text) { return unit * 1.05 }
        return unit * (1.28 + 0.14 * CGFloat(chance(run.id, 71)))
    }

    @ViewBuilder
    private func fableWord(_ run: StageRun, size: CGFloat, style: FableStyle, wear: Double, rightToLeft: Bool,
                           sample: LyricInkSample) -> some View {
        let tile = tiled(run)
        let onTile = style.accent == .tile && !tile
        let tight = LyricScript.keepsSyllables(run.text)
        // The parts' shares written together (joining them with spaces
        // read 思い出 as "omo i da"), filling in as the parts are sung.
        let reading = Self.revealedReading(of: run, in: readings, at: time, colour: sample.rest.opacity(0.9))
        let face = font(size, hero: true, id: run.id)
        let word = ZStack {
            self.word(run, tile ? ink.tileText : sample.sung).font(face)
                .opacity(onTile ? 1 - wear : 1)
            if onTile, wear > 0.001 {
                self.word(run, ink.tileText).font(face).opacity(wear)
            }
        }
        .lineLimit(1).fixedSize()
        .background { tileBackground(tile, size: size) }
        .background { if wear > 0.001 { fableAccent(style.accent, size: size, wear: wear, rightToLeft: rightToLeft) } }
        .overlay(alignment: rightToLeft ? .topLeading : .topTrailing) {
            if style.accent == .star, wear > 0.001 {
                Star4().fill(ink.tileColour)
                    .frame(width: size * 0.42, height: size * 0.42)
                    .rotationEffect(.degrees(reduceMotion ? 0 : (1 - wear) * 90))
                    .scaleEffect(wear)
                    // Within the word's own padding.
                    .offset(x: (rightToLeft ? -1 : 1) * size * 0.12, y: -size * 0.08)
            }
        }
        VStack(spacing: size * 0.06) {
            word
            if let reading {
                Text(reading)
                    .font(settings.lyricFont(size: max(12, size * 0.36), hero: false, mood: mood))
                    .foregroundStyle(sample.rest.opacity(0.9))
                    .lineLimit(1).fixedSize()
            }
        }
        // Room for the accent, inside the word's own measured box.
        .padding(.horizontal, size * (tight ? 0.16 : 0.24))
        .padding(.vertical, size * 0.16)
    }

    @ViewBuilder
    private func fableAccent(_ accent: FableStyle.Accent, size: CGFloat, wear: Double, rightToLeft: Bool) -> some View {
        let colour = ink.tileColour
        let line = max(2, size * 0.05)
        switch accent {
        case .underline:
            Capsule().fill(colour)
                .frame(height: line * 1.4)
                .scaleEffect(x: reduceMotion ? 1 : wear, anchor: rightToLeft ? .trailing : .leading)
                .frame(maxHeight: .infinity, alignment: .bottom)
                .offset(y: size * 0.1)
                .opacity(wear)
        case .ring:
            Capsule().stroke(colour, lineWidth: line)
                .padding(.horizontal, -size * 0.18).padding(.vertical, -size * 0.1)
                .rotationEffect(.degrees(-3))
                .scaleEffect(reduceMotion ? 1 : 0.9 + 0.1 * wear)
                .opacity(wear * 0.95)
        case .tile:
            RoundedRectangle(cornerRadius: size * 0.14, style: .continuous).fill(colour)
                .padding(.horizontal, -size * 0.14).padding(.vertical, -size * 0.04)
                .opacity(wear)
        case .star:
            EmptyView()
        case .brackets:
            FableBrackets(arm: size * 0.22)
                .stroke(colour, style: StrokeStyle(lineWidth: line, lineCap: .round, lineJoin: .round))
                .padding(.horizontal, -size * 0.16).padding(.vertical, -size * 0.06)
                .scaleEffect(reduceMotion ? 1 : 1.15 - 0.15 * wear)
                .opacity(wear)
        }
    }
}

// MARK: - Fable's parts

/// One Fable line's look: the shape its words are set in and the accent that
/// marks the word being sung. Chosen by the line, so the same line looks the
/// same on every render, and arranged so neighbouring lines never share a
/// shape or an accent; all 25 pairs come round before any repeats.
struct FableStyle: Equatable {
    enum Shape: Int, CaseIterable {
        case staircase, arch, zigzag, wave, ramp

        /// How much of the stage's width a row may take before it wraps.
        var share: CGFloat {
            switch self {
            case .staircase: return 0.7
            case .arch: return 0.9
            case .zigzag: return 0.75
            case .wave: return 0.95
            case .ramp: return 0.85
            }
        }

        /// Most words in a row, so the shape still reads as one.
        var wordsPerRow: Int {
            switch self {
            case .staircase, .zigzag: return 3
            case .ramp: return 4
            case .arch: return 5
            case .wave: return 6
            }
        }

        /// The spread of each word's tilt, in degrees.
        var tilt: Double {
            switch self {
            case .arch: return 0
            case .ramp: return 2
            default: return 5
            }
        }
    }

    enum Accent: Int, CaseIterable {
        case underline, ring, tile, star, brackets
    }

    var shape: Shape
    var accent: Accent

    static func forLine(_ seed: Int) -> FableStyle {
        let n = Shape.allCases.count
        // Floor division, so negative seeds keep the pattern too.
        let lap = seed >= 0 ? seed / n : -((-seed + n - 1) / n)
        func wrap(_ value: Int) -> Int { ((value % n) + n) % n }
        // The accent moves on one more than the shape each lap, so the
        // pairs shift against each other instead of repeating every five lines.
        return FableStyle(shape: Shape(rawValue: wrap(seed)) ?? .staircase,
                          accent: Accent(rawValue: wrap(seed + lap) % Accent.allCases.count) ?? .underline)
    }

    /// The word set larger: the longest, the first of equals. None for a line
    /// of one or two words, which are all set large.
    static func heroIndex(_ texts: [String]) -> Int? {
        guard texts.count >= 3 else { return nil }
        var best = 0
        for (index, text) in texts.enumerated() where text.count > texts[best].count { best = index }
        return best
    }

    /// A word's own entrance (one of four), as `progress` runs 0...1 with a
    /// little overshoot: it rises in, pops up, swings up from its foot, or
    /// slides in from `side` (the reading direction).
    static func entrance(_ kind: Int, progress a: Double, size: CGFloat, side: CGFloat)
        -> (offset: CGSize, scale: CGFloat, spin: Double) {
        let left = CGFloat(1 - a)
        switch kind {
        case 0: return (CGSize(width: 0, height: left * size * 0.5), 1, 0)
        case 1: return (.zero, 0.55 + 0.45 * CGFloat(a), 0)
        case 2: return (.zero, 1, Double(left) * -16 * Double(side))
        default: return (CGSize(width: side * left * size * 0.6, height: 0), 1, 0)
        }
    }
}

/// Lays a Fable line out in its shape: words in reading order, wrapped into
/// rows, each row bent into the shape, every word in a box of its own that
/// nothing else enters. Reports the block's own size; the caller scales it
/// to fit the stage.
struct FableLayout: Layout {
    var shape: FableStyle.Shape
    var rightToLeft: Bool
    var available: CGSize
    var gap: CGFloat
    var rowGap: CGFloat

    struct Arrangement: Equatable {
        var size: CGSize
        /// Each word's box, from the block's top-left corner.
        var frames: [CGRect]
        /// The scale the block will be shown at to fit `available`.
        var scale: CGFloat
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        arrangement(subviews).size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let sizes = subviews.map { $0.sizeThatFits(.unspecified) }
        let laid = Self.arrange(sizes, shape: shape, rightToLeft: rightToLeft, available: available, gap: gap, rowGap: rowGap,
                                baselines: Self.baselines(subviews))
        let origin = CGPoint(x: bounds.midX - laid.size.width / 2, y: bounds.midY - laid.size.height / 2)
        for (index, subview) in subviews.enumerated() where laid.frames.indices.contains(index) {
            let frame = laid.frames[index]
            subview.place(at: CGPoint(x: origin.x + frame.midX, y: origin.y + frame.midY), anchor: .center,
                          proposal: ProposedViewSize(sizes[index]))
        }
    }

    private func arrangement(_ subviews: Subviews) -> Arrangement {
        Self.arrange(subviews.map { $0.sizeThatFits(.unspecified) }, shape: shape, rightToLeft: rightToLeft,
                     available: available, gap: gap, rowGap: rowGap, baselines: Self.baselines(subviews))
    }

    /// Each word's baseline, from the top of its box (the word's, not its reading's).
    private static func baselines(_ subviews: Subviews) -> [CGFloat] {
        subviews.map { $0.dimensions(in: .unspecified)[VerticalAlignment.firstTextBaseline] }
    }

    /// Tries a few row widths and keeps the one that can be shown largest.
    /// Words in a row share a baseline, which the shape then bends; with no
    /// `baselines` given, they share a centre line.
    static func arrange(_ sizes: [CGSize], shape: FableStyle.Shape, rightToLeft: Bool, available: CGSize,
                        gap: CGFloat, rowGap: CGFloat, baselines: [CGFloat]? = nil) -> Arrangement {
        guard !sizes.isEmpty, available.width > 0, available.height > 0 else {
            return Arrangement(size: .zero, frames: sizes.map { _ in .zero }, scale: 1)
        }
        var best: Arrangement?
        for guess: CGFloat in [1, 0.9, 0.8, 0.7, 0.6, 0.5, 0.42, 0.35, 0.28] {
            var laid = build(sizes, shape: shape, wrap: available.width * shape.share / guess, gap: gap, rowGap: rowGap,
                             baselines: baselines)
            laid.scale = LyricStageFit.scale(content: laid.size, available: available)
            if best == nil || laid.scale > best!.scale + 0.001 { best = laid }
            if laid.scale >= guess - 0.001 { break }
        }
        guard var result = best else { return Arrangement(size: .zero, frames: [], scale: 1) }
        if rightToLeft {
            // Right to left: the same shape seen in a mirror, read from the right.
            result.frames = result.frames.map { CGRect(x: result.size.width - $0.maxX, y: $0.minY, width: $0.width, height: $0.height) }
        }
        return result
    }

    private static func build(_ sizes: [CGSize], shape: FableStyle.Shape, wrap: CGFloat, gap: CGFloat,
                              rowGap: CGFloat, baselines: [CGFloat]?) -> Arrangement {
        func baseline(_ index: Int) -> CGFloat {
            guard let baselines, baselines.indices.contains(index) else { return sizes[index].height / 2 }
            return min(sizes[index].height, max(0, baselines[index]))
        }
        // Rows, by width and by count; a very long line allows more words a row
        // rather than more than about four rows.
        let cap = max(shape.wordsPerRow, Int((Double(sizes.count) / 4).rounded(.up)))
        var rows: [[Int]] = [[]]
        var width: CGFloat = 0
        for index in sizes.indices {
            let row = rows[rows.count - 1]
            let add = (row.isEmpty ? 0 : gap) + sizes[index].width
            if !row.isEmpty, width + add > wrap || row.count >= cap {
                rows.append([index]); width = sizes[index].width
            } else {
                rows[rows.count - 1].append(index); width += add
            }
        }
        // No lone word left at the end when the row above can spare one and
        // the two still fit the row width (a long last word may not).
        if rows.count > 1, rows[rows.count - 1].count == 1, rows[rows.count - 2].count >= 3,
           let spare = rows[rows.count - 2].last,
           sizes[spare].width + gap + sizes[rows[rows.count - 1][0]].width <= wrap {
            let moved = rows[rows.count - 2].removeLast()
            rows[rows.count - 1].insert(moved, at: 0)
        }
        let rowWidths = rows.map { row in row.map { sizes[$0].width }.reduce(0, +) + gap * CGFloat(max(0, row.count - 1)) }
        let widest = rowWidths.max() ?? 0
        var frames = [CGRect](repeating: .zero, count: sizes.count)
        var top: CGFloat = 0
        var order = 0
        for (r, row) in rows.enumerated() {
            let rowHeight = row.map { sizes[$0].height }.max() ?? 0
            let start: CGFloat
            switch shape {
            case .staircase: start = CGFloat(r) * rowHeight * 0.9
            case .zigzag: start = r.isMultiple(of: 2) ? 0 : max(widest - rowWidths[r], rowHeight * 0.9)
            default: start = (widest - rowWidths[r]) / 2
            }
            let middle = start + rowWidths[r] / 2
            let half = max(rowWidths[r] / 2, 1)
            var x = start
            var placed: [(Int, CGRect)] = []
            for (j, index) in row.enumerated() {
                let size = sizes[index]
                let centre = x + size.width / 2
                let u = min(1, max(-1, (centre - middle) / half))
                let dy: CGFloat
                switch shape {
                case .staircase: dy = CGFloat(j) * rowHeight * 0.24
                case .arch: dy = -rowHeight * 0.45 * (1 - u * u)
                case .zigzag: dy = row.count > 1 ? (j.isMultiple(of: 2) ? -1 : 1) * rowHeight * 0.16 : 0
                case .wave: dy = CGFloat(sin(Double(order) * 1.25 + 0.4)) * rowHeight * 0.12
                case .ramp: dy = -(u + 1) / 2 * rowHeight * 0.8
                }
                placed.append((index, CGRect(x: x, y: dy - baseline(index), width: size.width, height: size.height)))
                x += size.width + gap
                order += 1
            }
            // Each row starts below the lowest point of the one before.
            let rowTop = placed.map(\.1.minY).min() ?? 0
            let shift = top - rowTop
            for (index, rect) in placed { frames[index] = rect.offsetBy(dx: 0, dy: shift) }
            top = (placed.map(\.1.maxY).max() ?? 0) + shift + rowGap
        }
        let box = frames.dropFirst().reduce(frames[0]) { $0.union($1) }
        return Arrangement(size: box.size, frames: frames.map { $0.offsetBy(dx: -box.minX, dy: -box.minY) }, scale: 1)
    }
}

/// Four corner marks round a word.
struct FableBrackets: Shape {
    var arm: CGFloat

    func path(in rect: CGRect) -> Path {
        let a = min(arm, rect.width / 2, rect.height / 2)
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: rect.minY + a)); path.addLine(to: CGPoint(x: rect.minX, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.minX + a, y: rect.minY))
        path.move(to: CGPoint(x: rect.maxX - a, y: rect.minY)); path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY + a))
        path.move(to: CGPoint(x: rect.maxX, y: rect.maxY - a)); path.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.maxX - a, y: rect.maxY))
        path.move(to: CGPoint(x: rect.minX + a, y: rect.maxY)); path.addLine(to: CGPoint(x: rect.minX, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.minX, y: rect.maxY - a))
        return path
    }
}
