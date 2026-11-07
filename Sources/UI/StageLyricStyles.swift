import SwiftUI

// MARK: - Layouts that set the whole stage themselves
//
// Hinge, Broadside and Portal each build a line word by word on a stage of
// their own: every word is placed from its measured size, arrives as it is
// sung, and sinks into the stack, the strip or the trail as the next one
// comes. They draw one line at a time; the line before leaves as this one
// begins. Written for Lunalyr; the looks are inspired by other lyric apps'
// motion but no code or assets are shared.

extension LyricComposition {
    /// Layouts drawn by `StageLyricView`.
    var fillsStage: Bool { self == .hinge || self == .broadside || self == .portal }
}

/// One glued run of a line: "beau" "ti" "ful" is a single run.
struct StageRun: Equatable, Identifiable {
    var id: Int
    var text: String
    var start: Double
    var end: Double

    static func make(_ words: [LyricsFetcher.Word], glue: [Bool]) -> [StageRun] {
        var runs: [StageRun] = []
        for (index, word) in words.enumerated() {
            let text = word.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            if index > 0, glue.indices.contains(index), glue[index], var last = runs.popLast() {
                last.text += text
                last.end = max(last.end, word.end)
                runs.append(last)
            } else {
                runs.append(StageRun(id: runs.count, text: text, start: word.start, end: max(word.end, word.start)))
            }
        }
        return runs
    }
}

/// Places each child at a centre worked out from every child's measured size.
struct StageLayout: Layout {
    var place: ([CGSize], CGRect) -> [CGPoint]

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        CGSize(width: proposal.width ?? 400, height: proposal.height ?? 300)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let sizes = subviews.map { $0.sizeThatFits(.unspecified) }
        let centres = place(sizes, bounds)
        for (index, subview) in subviews.enumerated() where centres.indices.contains(index) {
            subview.place(at: centres[index], anchor: .center, proposal: ProposedViewSize(sizes[index]))
        }
    }
}

struct StageLyricView: View {
    let words: [LyricsFetcher.Word]
    let glue: [Bool]
    let time: Double
    let stageSize: CGSize
    let baseSize: CGFloat
    let mood: TrackMood
    let composition: LyricComposition
    let seed: Int
    var exitStart: Double = .infinity
    var firstRunOnly: Double?

    @ObservedObject private var settings = Settings.shared
    @Environment(\.lyricInk) private var ink
    @Environment(\.lyricReduceMotion) private var reduceMotion

    // MARK: Shared

    private var unit: CGFloat { max(16, baseSize) }

    /// 0...1 as a word arrives, a little overshooting.
    private static func arrive(_ run: StageRun, at time: Double, over span: Double = 0.34) -> Double {
        let x = min(max((time - (run.start - 0.04)) / span, 0), 1)
        let c = 1.4
        let t = x - 1
        return x <= 0 ? 0 : max(0, 1 + (c + 1) * t * t * t + c * t * t)
    }

    private func font(_ size: CGFloat, hero: Bool, id: Int) -> Font {
        settings.lyricFont(size: size, hero: hero, mood: mood, seed: UInt64(abs(seed &+ id)))
    }

    private func sample(_ run: StageRun, of count: Int) -> LyricInkSample {
        ink.sample(slot: count > 1 ? Double(run.id) / Double(count - 1) : 0.5, seed: UInt64(abs(seed &+ run.id)))
    }

    /// "Cutout · Highlight" chosen as the entrance: each sung word sits on a tile.
    private func tiled(_ run: StageRun) -> Bool {
        settings.arrival(in: composition, for: LyricsFetcher.Word(text: run.text, start: run.start, end: run.end)).highlightsSungWords
    }

    private func tileBackground(_ on: Bool, size: CGFloat) -> some View {
        RoundedRectangle(cornerRadius: 3).fill(ink.tileColour).opacity(on ? 1 : 0)
            .padding(.horizontal, -size * 0.1).padding(.vertical, -size * 0.02)
    }

    private var leave: Double { reduceMotion ? 0 : Easing.smoothstep((time - exitStart) / 0.5) }

    var body: some View {
        if firstRunOnly == nil {
            let runs = StageRun.make(words, glue: glue)
            let shown = runs.filter { time >= $0.start - 0.04 }
            Group {
                switch composition {
                case .hinge: hinge(shown, total: runs.count)
                case .broadside: broadside(shown, total: runs.count)
                default: portal(shown, total: runs.count)
                }
            }
            .frame(width: stageSize.width, height: stageSize.height)
        } else {
            Color.clear.frame(width: stageSize.width, height: stageSize.height)
        }
    }

    // MARK: Hinge
    //
    // Words stack up in a heavy column, the newest the biggest. Each swings in
    // on a hinge at its left edge, and as the next arrives it shrinks and tips
    // a little, like folded panels. When the line goes, the stack folds away
    // into the top left corner.

    private func hinge(_ runs: [StageRun], total: Int) -> some View {
        let arrivals = runs.map { Self.arrive($0, at: time) }
        let ranks = runs.indices.map { i in arrivals[(i + 1)...].reduce(0, +) }
        let sizes = ranks.map { unit * 2.1 * max(0.38, 1 - 0.24 * $0) }
        let heights = sizes.map { $0 * 1.1 }
        let widest = zip(runs, sizes).map { CGFloat($0.text.count) * $1 * 0.66 }.max() ?? 1
        let fit = min(1, stageSize.height * 0.8 / max(heights.reduce(0, +), 1), stageSize.width * 0.86 / max(widest, 1))
        let leaving = leave
        let salt = Double(seed % 7)
        return StageLayout(place: { measured, bounds in
            let tall = measured.map { $0.height * 0.92 }
            var cursor = bounds.midY - tall.reduce(0, +) / 2
            return measured.indices.map { index in
                let sway = sin(Double(index) * 1.7 + salt) * bounds.width * 0.035
                defer { cursor += tall[index] }
                return CGPoint(x: bounds.midX + sway, y: cursor + tall[index] / 2)
            }
        }) {
            ForEach(runs) { run in
                let sample = sample(run, of: total)
                let rank = ranks[run.id]
                let newest = rank < 0.5
                let tilt = (run.id.isMultiple(of: 2) ? -1.0 : 1.0) * 4 * min(1, rank)
                let swing = reduceMotion ? 0 : (1 - arrivals[run.id]) * -78
                let tile = tiled(run)
                Text(run.text.uppercased())
                    .font(font(sizes[run.id], hero: true, id: run.id))
                    .foregroundStyle(tile ? ink.tileText : newest ? sample.sung : sample.rest.opacity(0.88))
                    .lineLimit(1)
                    .fixedSize()
                    .background { tileBackground(tile, size: sizes[run.id]) }
                    .shadow(color: .black.opacity(tile ? 0 : 0.32), radius: 7, y: 2)
                    .rotationEffect(.degrees(reduceMotion ? 0 : tilt))
                    .rotation3DEffect(.degrees(swing), axis: (x: 0, y: 1, z: 0), anchor: .leading, perspective: 0.55)
                    .opacity(min(1, arrivals[run.id] * 1.6))
            }
        }
        .scaleEffect(fit)
        // The stack folds into the top left corner as the line leaves.
        .scaleEffect(1 - 0.62 * leaving, anchor: .topLeading)
        .offset(x: -stageSize.width * 0.14 * leaving, y: -stageSize.height * 0.12 * leaving)
        .rotationEffect(.degrees(-9 * leaving), anchor: .topLeading)
        .opacity(1 - leaving)
    }

    // MARK: Broadside
    //
    // One strip of type that slides along as words are sung: big, round, hard
    // shadowed words, with the little ones ("the", "of") as paper labels laid
    // across the word before. The strip runs off both edges of the stage.

    private static let little: Set<String> = [
        "a", "an", "the", "of", "to", "in", "on", "at", "by", "for", "and", "or", "but", "so", "as", "if", "is", "it",
        "my", "me", "we", "i", "i'm", "i'll", "you", "be", "up", "us", "no", "oh", "can", "can't", "do", "did", "don't",
        "under", "over", "with", "from", "into", "out", "off", "all", "are", "was", "im", "ill",
    ]

    private func isLittle(_ text: String) -> Bool {
        Self.little.contains(text.lowercased().trimmingCharacters(in: .punctuationCharacters)) && text.count <= 5
    }

    private func broadside(_ runs: [StageRun], total: Int) -> some View {
        let arrivals = runs.map { Self.arrive($0, at: time, over: 0.4) }
        let little = runs.map { isLittle($0.text) }
        let bigSize = unit * 2.4, smallSize = unit * 0.66
        let hard = unit * 0.07
        let leaving = leave
        return StageLayout(place: { measured, bounds in
            // Big words in a row; a little word lies across the one before.
            var xs = [CGFloat](repeating: 0, count: measured.count)
            var ys = [CGFloat](repeating: bounds.midY, count: measured.count)
            var cursor: CGFloat = 0
            var previousBig: Int?
            var bigOrder: [Int] = []
            for index in measured.indices {
                if little[index], let prior = previousBig {
                    xs[index] = xs[prior] + measured[prior].width * 0.22
                    ys[index] = bounds.midY + measured[prior].height * 0.2
                    continue
                }
                let width = measured[index].width
                xs[index] = cursor + width / 2
                cursor += width + (little[index] ? unit * 0.25 : unit * 0.55)
                if !little[index] { previousBig = index; bigOrder.append(index) }
            }
            // The strip slides so the newest big word is at the centre.
            var camera = bigOrder.first.map { xs[$0] } ?? 0
            for (n, index) in bigOrder.enumerated() where n > 0 {
                camera += arrivals[index] * (xs[index] - xs[bigOrder[n - 1]])
            }
            return measured.indices.map { CGPoint(x: xs[$0] - camera + bounds.midX, y: ys[$0]) }
        }) {
            ForEach(runs) { run in
                let sample = sample(run, of: total)
                let a = arrivals[run.id]
                if little[run.id] {
                    // With "Cutout · Highlight" the labels grow into real tiles.
                    let labelSize = tiled(run) ? smallSize * 1.4 : smallSize
                    Text(run.text)
                        .font(font(labelSize, hero: true, id: run.id))
                        .foregroundStyle(ink.tileText)
                        .lineLimit(1).fixedSize()
                        .padding(.horizontal, labelSize * 0.34).padding(.vertical, labelSize * 0.16)
                        .background(ink.tileColour, in: RoundedRectangle(cornerRadius: 3))
                        .rotationEffect(.degrees(run.id.isMultiple(of: 2) ? -4 : 3))
                        .scaleEffect(reduceMotion ? 1 : 0.7 + 0.3 * a)
                        .opacity(min(1, a * 1.6))
                } else {
                    let tile = tiled(run)
                    ZStack {
                        Text(run.text).foregroundStyle(.black.opacity(tile ? 0 : 0.38)).offset(x: hard, y: hard * 1.15)
                        Text(run.text).foregroundStyle(tile ? ink.tileText : sample.sung)
                    }
                    .font(font(bigSize, hero: true, id: run.id))
                    .lineLimit(1).fixedSize()
                    .background { tileBackground(tile, size: bigSize) }
                    .scaleEffect(reduceMotion ? 1 : 0.86 + 0.14 * a)
                    .opacity(min(1, a * 1.6))
                }
            }
        }
        .mask(LinearGradient(stops: [.init(color: .clear, location: 0), .init(color: .black, location: 0.08),
                                     .init(color: .black, location: 0.92), .init(color: .clear, location: 1)],
                             startPoint: .leading, endPoint: .trailing))
        .opacity(1 - leaving)
    }

    // MARK: Portal
    //
    // The word being sung sits in a solid pill with a small arrow badge, tilted
    // as it arrives and settling flat. The words before it drift up and away as
    // smaller, paler pills.

    private func portal(_ runs: [StageRun], total: Int) -> some View {
        let arrivals = runs.map { Self.arrive($0, at: time, over: 0.38) }
        let ranks = runs.indices.map { i in arrivals[(i + 1)...].reduce(0, +) }
        let trail: [CGPoint] = [
            CGPoint(x: 0, y: 0), CGPoint(x: -0.1 * stageSize.width, y: -0.2 * stageSize.height),
            CGPoint(x: 0.11 * stageSize.width, y: -0.3 * stageSize.height),
            CGPoint(x: -0.04 * stageSize.width, y: -0.38 * stageSize.height),
        ]
        func along(_ rank: Double) -> CGPoint {
            let capped = min(max(rank, 0), Double(trail.count - 1))
            let low = Int(capped.rounded(.down)), high = min(low + 1, trail.count - 1)
            let f = CGFloat(capped - Double(low))
            return CGPoint(x: trail[low].x + (trail[high].x - trail[low].x) * f, y: trail[low].y + (trail[high].y - trail[low].y) * f)
        }
        let leaving = leave
        let lift = stageSize.height * 0.12
        return StageLayout(place: { measured, bounds in
            measured.indices.map { index in
                let p = along(ranks[index])
                return CGPoint(x: bounds.midX + p.x, y: bounds.midY + lift + p.y)
            }
        }) {
            ForEach(runs) { run in
                let a = arrivals[run.id]
                let rank = ranks[run.id]
                let sample = sample(run, of: total)
                let size = unit * 1.5 * max(0.4, 1 - 0.4 * min(rank, 1.5))
                let live = rank < 0.5
                let tile = tiled(run)
                HStack(spacing: size * 0.3) {
                    Image(systemName: "arrow.up.right")
                        .font(.system(size: size * 0.34, weight: .bold))
                        .foregroundStyle(ink.tileText)
                        .frame(width: size * 0.62, height: size * 0.62)
                        .background(Circle().fill(ink.tileColour.opacity(live ? 1 : 0.35)))
                        .opacity(live ? 1 : 0)
                        .frame(width: live ? size * 0.62 : 0)
                    Text(run.text)
                        .font(font(size, hero: true, id: run.id))
                        .foregroundStyle(live || tile ? ink.tileText : sample.rest.opacity(0.85))
                        .lineLimit(1).fixedSize()
                }
                .padding(.horizontal, size * 0.5).padding(.vertical, size * 0.26)
                .background {
                    ZStack {
                        if live, !reduceMotion {
                            RoundedRectangle(cornerRadius: size * 0.4, style: .continuous)
                                .stroke(ink.tileColour.opacity(0.6 * (1 - a)), lineWidth: 1.5)
                                .scaleEffect(1.12)
                                .rotationEffect(.degrees(-5))
                        }
                        RoundedRectangle(cornerRadius: size * 0.4, style: .continuous)
                            .fill(live ? ink.tileColour : tile ? ink.tileColour : sample.rest.opacity(0.16))
                    }
                }
                .shadow(color: .black.opacity(live ? 0.22 : 0), radius: 8, y: 3)
                .rotationEffect(.degrees(reduceMotion || !live ? 0 : (1 - a) * 7))
                .scaleEffect(reduceMotion ? 1 : 0.92 + 0.08 * min(a, 1))
                .opacity(min(1, a * 1.6) * max(0, 1 - max(0, rank - 1.6) * 0.7) * (live ? 1 : 0.85))
            }
        }
        .offset(y: -stageSize.height * 0.1 * leaving)
        .opacity(1 - leaving)
    }
}
