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

    /// Glued syllables join into one run ("beau" "ti" "ful"), but never into a
    /// run too long to read as a word: an unspaced Chinese, Japanese or Korean
    /// line, or a romanised one, is cut into short runs that arrive one by one.
    static func make(_ words: [LyricsFetcher.Word], glue: [Bool]) -> [StageRun] {
        var runs: [StageRun] = []
        for (index, word) in words.enumerated() {
            let text = word.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            let limit = LyricScript.keepsSyllables(text) ? 3 : 12
            if index > 0, glue.indices.contains(index), glue[index], let previous = runs.last,
               previous.text.count + text.count <= limit {
                var last = runs.removeLast()
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
extension CGRect { fileprivate var center: CGPoint { CGPoint(x: midX, y: midY) } }

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

    /// Eases 0...1 with no overshoot: the camera and the soft arrivals use this.
    private static func glide(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        return t * t * t * (t * (t * 6 - 15) + 10)
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
    // Picture the corner of a cube seen from a slant. Text sits on its two
    // adjacent faces: the first two words as a tight pair, then the third as a
    // big bar on the left face, and from the fourth on a tall column down the
    // right face, each word a little bigger than the one above. Both faces
    // meet at the vertical edge in the middle and fall away from it in
    // perspective. When the line ends the faces swing shut edge-on.

    private func hinge(_ runs: [StageRun], total: Int) -> some View {
        let arrivals = runs.map { Self.arrive($0, at: time, over: 0.4) }
        let barStart = runs.count > 2 ? runs[2].start : .infinity
        // How far the bar has settled from huge to its place, and how far the
        // pair has been pushed to the corner.
        let settle = Easing.smoothstep((time - barStart - 0.25) / 0.5)
        let pushed = runs.count > 2 ? Easing.smoothstep((time - barStart + 0.05) / 0.4) : 0
        let columnCount = max(0, runs.count - 3)
        let u = unit
        func columnSize(_ j: Int) -> CGFloat { u * min(2.8, 1.2 + 0.24 * CGFloat(j)) * (columnCount > 6 ? 0.85 : 1) }
        let heightSum = (0..<columnCount).map { columnSize($0) * 0.95 }.reduce(0, +)
        // The whole corner grows to fill the stage once it has a bar to hang from.
        let barWidth = runs.count > 2 ? CGFloat(runs[2].text.count) * u * 1.7 * 0.66 : 0
        let columnWidth = (0..<columnCount).map { CGFloat(runs[$0 + 3].text.count) * columnSize($0) * 0.66 * 0.84 }.max() ?? 0
        let structureWidth = max(2 * max(barWidth, columnWidth) + u * 0.3, u * 3)
        let structureHeight = max(heightSum, u * 2.4) + u * 0.8
        let fill = min(2.0, stageSize.width * 0.8 / structureWidth, stageSize.height * 0.78 / structureHeight)
        let fit = min(1, stageSize.height * 0.74 / max(heightSum + u * 0.8, 1)) * (1 + (max(fill, 1) - 1) * settle)
        let leaving = leave
        let faceAngle = reduceMotion ? 0.0 : 52 + (88 - 52) * leaving
        let leftRuns = runs.filter { $0.id < 3 }
        let rightRuns = runs.filter { $0.id >= 3 }
        let stage = stageSize
        let face: (Bool) -> AnyView = { isLeft in
            let items = isLeft ? leftRuns : rightRuns
            return AnyView(StageLayout(place: { measured, bounds in
                let edge = bounds.midX
                let gap = u * 0.12
                if isLeft {
                    let barSize = measured.count > 2 ? measured[2] : .zero
                    let totalColumn = rightRuns.indices.map { columnSize($0) * 0.95 }.reduce(0, +)
                    let top = bounds.midY - (rightRuns.isEmpty ? u : totalColumn / 2)
                    var centres = [CGPoint](repeating: CGPoint(x: edge / 2, y: bounds.midY), count: measured.count)
                    if measured.count > 2 {
                        let settledX = rightRuns.isEmpty ? edge - gap - barSize.width / 2 : edge - gap - barSize.width / 2
                        let settledY = rightRuns.isEmpty ? bounds.midY : top + barSize.height * 0.45
                        centres[2] = CGPoint(x: edge / 2 + (settledX - edge / 2) * settle, y: bounds.midY + (settledY - bounds.midY) * settle)
                    }
                    let pairHeight = (measured.first?.height ?? 0) * 0.78
                    let home = CGPoint(x: edge / 2, y: bounds.midY)
                    let corner = measured.count > 2
                        ? CGPoint(x: centres[2].x + barSize.width * 0.28, y: centres[2].y - barSize.height * 0.62) : home
                    let spot = CGPoint(x: home.x + (corner.x - home.x) * pushed, y: home.y + (corner.y - home.y) * pushed)
                    if !measured.isEmpty { centres[0] = CGPoint(x: spot.x, y: spot.y - (measured.count > 1 ? pairHeight * 0.5 : 0)) }
                    if measured.count > 1 { centres[1] = CGPoint(x: spot.x, y: spot.y + pairHeight * 0.5 * (1 - 0.25 * pushed)) }
                    return centres
                }
                let heights = measured.map { $0.height * 0.9 }
                var cursor = bounds.midY - heights.reduce(0, +) / 2
                return measured.indices.map { index in
                    defer { cursor += heights[index] }
                    return CGPoint(x: edge + gap + measured[index].width / 2, y: cursor + heights[index] / 2)
                }
            }) {
                ForEach(items) { run in
                    let sample = sample(run, of: total)
                    let a = arrivals[run.id]
                    let isPair = run.id < 2, isBar = run.id == 2
                    let size: CGFloat = isPair ? u * (runs.count > 1 ? 0.95 : 1.3) * (1 - 0.45 * pushed)
                        : isBar ? u * (3.4 - 1.8 * settle)
                        : columnSize(run.id - 3) * (1 + 0.3 * (1 - a))
                    let tile = tiled(run)
                    Text(run.text.uppercased())
                        .font(font(size, hero: true, id: run.id))
                        .foregroundStyle(tile ? ink.tileText : isPair ? sample.rest.opacity(0.95) : sample.sung)
                        .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stage.width * 0.4)
                        .background { tileBackground(tile, size: size) }
                        .shadow(color: .black.opacity(tile ? 0 : 0.3), radius: 7, y: 2)
                        .scaleEffect(x: isBar || isPair ? 1 : 0.86, y: isBar || isPair ? 1 : 1.1)
                        // Words glide in along their face, from further out.
                        .offset(x: reduceMotion ? 0 : (isLeft ? -1 : 1) * (1 - min(a, 1)) * u * 1.2)
                        .opacity(min(1, a * 1.6))
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
        .opacity(1 - Easing.smoothstep((leaving - 0.7) / 0.3))
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
        // Snappy: each word springs in and the strip jumps along with it.
        let arrivals = runs.map { Self.arrive($0, at: time, over: 0.4) }
        let moves = arrivals
        let little = runs.map { isLittle($0.text) }
        let bigSize = unit * 1.7, smallSize = unit * 0.52
        let hard = unit * 0.05
        let leaving = leave
        return StageLayout(place: { measured, bounds in
            // Big words in a row; a little word lies across the one before.
            var xs = [CGFloat](repeating: 0, count: measured.count)
            var ys = [CGFloat](repeating: bounds.midY, count: measured.count)
            var cursor: CGFloat = 0
            var previousBig: Int?
            var bigOrder: [Int] = []
            var littlesSinceBig = 0
            for index in measured.indices {
                // The first little word after a big one lies across it; any
                // more follow it in the row rather than piling on top.
                if little[index], let prior = previousBig, littlesSinceBig == 0 {
                    xs[index] = xs[prior] + measured[prior].width * 0.2
                    ys[index] = bounds.midY + measured[prior].height * 0.2
                    littlesSinceBig += 1
                    continue
                }
                let width = measured[index].width
                xs[index] = cursor + width / 2
                cursor += width + (little[index] ? unit * 0.2 : unit * 0.45)
                if little[index] { littlesSinceBig += 1 } else { previousBig = index; bigOrder.append(index); littlesSinceBig = 0 }
            }
            // The strip glides so the newest big word reaches the centre.
            var camera = bigOrder.first.map { xs[$0] } ?? 0
            for (n, index) in bigOrder.enumerated() where n > 0 {
                camera += moves[index] * (xs[index] - xs[bigOrder[n - 1]])
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
                        .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7)
                        .padding(.horizontal, labelSize * 0.34).padding(.vertical, labelSize * 0.16)
                        .background(ink.tileColour, in: RoundedRectangle(cornerRadius: 3))
                        .rotationEffect(.degrees(run.id.isMultiple(of: 2) ? -3 : 2.5))
                        .scaleEffect(reduceMotion ? 1 : 0.8 + 0.2 * a)
                        .opacity(min(1, a * 1.6))
                } else {
                    let tile = tiled(run)
                    ZStack {
                        Text(run.text).foregroundStyle(.black.opacity(tile ? 0 : 0.34)).offset(x: hard, y: hard * 1.15)
                        Text(run.text).foregroundStyle(tile ? ink.tileText : sample.sung)
                    }
                    .font(font(bigSize, hero: true, id: run.id))
                    .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7)
                    .background { tileBackground(tile, size: bigSize) }
                    .scaleEffect(reduceMotion ? 1 : 0.86 + 0.14 * a)
                    .opacity(min(1, a * 1.6))
                }
            }
        }
        .mask(LinearGradient(stops: [.init(color: .clear, location: 0), .init(color: .black, location: 0.1),
                                     .init(color: .black, location: 0.9), .init(color: .clear, location: 1)],
                             startPoint: .leading, endPoint: .trailing))
        .opacity(1 - leaving)
    }

    // MARK: Portal
    //
    // The word being sung sits in a solid pill with a small badge, tilted as it
    // arrives and settling flat. The words before it drift away as smaller,
    // paler pills. Nothing repeats: every line leans a different way, and every
    // word comes from its own direction with its own tilt, shape and badge.

    /// A steady 0...1 for a line (`word` nil) or one of its words and a salt.
    private func chance(_ word: Int?, _ salt: Int) -> Double {
        var value = UInt64(truncatingIfNeeded: abs(seed) &* 2654435761 &+ (word ?? 977) &* 40503 &+ salt &* 9973)
        value ^= value >> 13; value = value &* 0x2545F4914F6CDD1D; value ^= value >> 29
        return Double(value % 10_000) / 10_000
    }

    private static let badges = ["arrow.up.right", "arrow.right", "sparkle", "play.fill", "plus", "bolt.fill"]

    private func portal(_ runs: [StageRun], total: Int) -> some View {
        let arrivals = runs.map { Self.arrive($0, at: time, over: 0.5) }
        let settled = runs.map { Self.glide((time - ($0.start - 0.05)) / 0.6) }
        let ranks = runs.indices.map { i in settled[(i + 1)...].reduce(0, +) }
        // Which way this line's trail runs, and where the pills sit.
        let flipX: CGFloat = chance(nil, 1) < 0.5 ? 1 : -1
        let flipY: CGFloat = chance(nil, 2) < 0.7 ? 1 : -1
        let reach = 0.8 + 0.5 * chance(nil, 3)
        let shiftX = (chance(nil, 4) - 0.5) * 0.24 * stageSize.width
        let lift = stageSize.height * (flipY > 0 ? 0.1 + 0.1 * chance(nil, 5) : -0.1 - 0.08 * chance(nil, 5))
        let badge = Self.badges[Int(chance(nil, 6) * Double(Self.badges.count)) % Self.badges.count]
        let trail: [CGPoint] = [
            CGPoint(x: 0, y: 0),
            CGPoint(x: -0.1 * stageSize.width * flipX * reach, y: -0.2 * stageSize.height * flipY * reach),
            CGPoint(x: 0.11 * stageSize.width * flipX * reach, y: -0.3 * stageSize.height * flipY * reach),
            CGPoint(x: -0.04 * stageSize.width * flipX * reach, y: -0.38 * stageSize.height * flipY * reach),
        ]
        func along(_ rank: Double) -> CGPoint {
            let capped = min(max(rank, 0), Double(trail.count - 1))
            let low = Int(capped.rounded(.down)), high = min(low + 1, trail.count - 1)
            let f = CGFloat(Self.glide(capped - Double(low)))
            return CGPoint(x: trail[low].x + (trail[high].x - trail[low].x) * f, y: trail[low].y + (trail[high].y - trail[low].y) * f)
        }
        let leaving = leave
        let drift = reduceMotion ? 0.0 : 1.0
        return StageLayout(place: { measured, bounds in
            measured.indices.map { index in
                let p = along(ranks[index])
                let bob = CGFloat(sin(time * (1.2 + 0.6 * chance(index, 7)) + Double(index) * 1.3)) * unit * 0.07 * drift
                let sway = CGFloat(cos(time * (0.9 + 0.5 * chance(index, 8)) + Double(index) * 0.9)) * unit * 0.05 * drift
                // Each pill comes in from its own direction.
                let heading = chance(index, 9) * 2 * .pi
                let rise = CGFloat(1 - settled[index])
                return CGPoint(x: bounds.midX + shiftX + p.x + sway + rise * CGFloat(cos(heading)) * bounds.width * 0.07 * drift,
                               y: bounds.midY + lift + p.y + bob + rise * CGFloat(sin(heading)) * bounds.height * 0.1 * drift)
            }
        }) {
            ForEach(runs) { run in
                let a = arrivals[run.id]
                let eased = settled[run.id]
                let rank = ranks[run.id]
                let sample = sample(run, of: total)
                let live = rank < 0.5
                let tile = tiled(run)
                let size = unit * (1.2 + 0.3 * chance(run.id, 10)) * max(0.4, 1 - 0.4 * min(rank, 1.5))
                // Shapes run from nearly square to a full capsule; some pills are only outlined.
                let corner = size * (0.24 + 0.26 * chance(run.id, 11))
                let outlined = live && chance(run.id, 12) < 0.25 && !tile
                let badgeSide = chance(run.id, 13)
                let tiltAmount = 4 + 10 * chance(run.id, 14)
                let tiltSign: Double = chance(run.id, 15) < 0.5 ? 1 : -1
                let tilt = reduceMotion ? 0 : (1 - eased) * tiltAmount * tiltSign + sin(time * 1.1 + Double(run.id)) * 1.6
                let badgeView = Image(systemName: badge)
                    .font(.system(size: size * 0.34, weight: .bold))
                    .rotationEffect(.degrees(reduceMotion ? 0 : (1 - eased) * -90 * tiltSign))
                    .foregroundStyle(outlined ? ink.tileColour : ink.tileText)
                    .frame(width: size * 0.62, height: size * 0.62)
                    .background(Circle().fill(outlined ? ink.tileColour.opacity(0.2) : ink.tileColour.opacity(live ? 1 : 0.35)))
                    .opacity(live ? 1 : 0)
                    .frame(width: live ? size * 0.62 : 0)
                HStack(spacing: size * 0.3) {
                    if badgeSide < 0.6 { badgeView }
                    Text(run.text)
                        .font(font(size, hero: true, id: run.id))
                        .foregroundStyle(outlined ? ink.tileColour : live || tile ? ink.tileText : sample.rest.opacity(0.85))
                        .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7)
                    if badgeSide >= 0.6 && badgeSide < 0.85 { badgeView }
                }
                .padding(.horizontal, size * 0.5).padding(.vertical, size * 0.26)
                .background {
                    ZStack {
                        // A soft ripple swells off the pill as it lands.
                        if live, !reduceMotion {
                            RoundedRectangle(cornerRadius: corner, style: .continuous)
                                .fill(ink.tileColour.opacity(0.5 * (1 - eased)))
                                .blur(radius: size * 0.16)
                                .scaleEffect(1 + 0.3 * eased)
                        }
                        if outlined {
                            RoundedRectangle(cornerRadius: corner, style: .continuous)
                                .strokeBorder(ink.tileColour, lineWidth: max(2, size * 0.07))
                        } else {
                            RoundedRectangle(cornerRadius: corner, style: .continuous)
                                .fill(live ? ink.tileColour : tile ? ink.tileColour : sample.rest.opacity(0.16))
                        }
                    }
                }
                .shadow(color: .black.opacity(live && !outlined ? 0.2 : 0), radius: 9, y: 4)
                // It squashes as it lands, then settles.
                .scaleEffect(x: reduceMotion ? 1 : 1 + 0.07 * (1 - min(a, 1)), y: reduceMotion ? 1 : 1 - 0.07 * (1 - min(a, 1)))
                .scaleEffect(reduceMotion ? 1 : 0.9 + 0.1 * min(a, 1.05))
                .rotationEffect(.degrees(live ? tilt : tilt * 0.5))
                .opacity(eased * max(0, 1 - max(0, rank - 1.6) * 0.7) * (live ? 1 : 0.85))
            }
        }
        .offset(y: -stageSize.height * 0.1 * leaving)
        .opacity(1 - leaving)
    }
}
