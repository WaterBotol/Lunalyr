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
    var fillsStage: Bool { [.hinge, .broadside, .portal, .mosaic, .torque, .fable].contains(self) }
}

/// One glued run of a line: "beau" "ti" "ful" is a single run, shown
/// syllable by syllable as each is sung.
struct StageRun: Equatable, Identifiable {
    struct Part: Equatable {
        var text: String
        var start: Double
    }

    var id: Int
    var text: String
    var start: Double
    var end: Double
    var parts: [Part]

    /// Glued syllables join into one run, but never into a run too long to read
    /// as a word: an unspaced Chinese, Japanese or Korean line, or a romanised
    /// one, is cut into short runs that arrive one by one.
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
                last.parts.append(Part(text: text, start: word.start))
                runs.append(last)
            } else {
                runs.append(StageRun(id: runs.count, text: text, start: word.start, end: max(word.end, word.start),
                                     parts: [Part(text: text, start: word.start)]))
            }
        }
        return runs
    }

    /// The word with its not-yet-sung syllables invisible (they still take up
    /// their room, so the word does not jump as it fills in).
    func revealed(at time: Double, uppercase: Bool = false) -> AttributedString {
        var result = AttributedString()
        for part in parts {
            var piece = AttributedString(uppercase ? part.text.uppercased() : part.text)
            if time < part.start - 0.03 { piece.foregroundColor = .clear }
            result.append(piece)
        }
        return result
    }
}

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
        // "Add Editorial typography": about a third of the words (by the amount set) take the
        // contrasting face, as in the other layouts. The same words every time for a line.
        let alternate = settings.editorialTypography && chance(id, 60) < 0.34 && chance(id, 61) < settings.editorialAmount
        return settings.lyricFont(size: size, hero: hero, mood: mood, seed: UInt64(abs(seed &+ id)), alternate: alternate)
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

    /// 1 for an ordinary pace, up to 3 when the line is sung fast: every motion
    /// is shortened and calmed by it so the layout keeps up with the voice.
    private var lineSpeed: Double {
        let starts = words.map(\.start).sorted()
        let gaps = zip(starts, starts.dropFirst()).map { $1 - $0 }.filter { $0 > 0.02 }.sorted()
        guard !gaps.isEmpty else { return 1 }
        let median = gaps[gaps.count / 2]
        return min(3, max(1, 0.5 / max(median, 0.1)))
    }

    private var leave: Double { Easing.smoothstep((time - exitStart) / 0.5) }

    var body: some View {
        if firstRunOnly == nil {
            let runs = StageRun.make(words, glue: glue)
            let shown = runs.filter { time >= $0.start - 0.04 }
            Group {
                switch composition {
                case .hinge: hinge(shown, total: runs.count)
                case .broadside: broadside(shown, total: runs.count)
                case .mosaic: mosaic(shown, total: runs.count)
                case .torque: torque(shown, total: runs.count)
                case .fable: fable(shown, total: runs.count)
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
        // The fold shuts over most of a second, word by word.
        let leaving = reduceMotion ? 0 : min(max((time - exitStart) / 0.9, 0), 1)
        let faceAngle = reduceMotion ? 0.0 : 52 + (88 - 52) * Easing.smoothstep(leaving)
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
                    // Words fold away oldest first.
                    let out = reduceMotion ? 0 : Easing.smoothstep((leaving - Double(run.id) / Double(max(total, 1)) * 0.5) / 0.5)
                    Text(run.revealed(at: time, uppercase: true))
                        .font(font(size, hero: true, id: run.id))
                        .foregroundStyle(tile ? ink.tileText : isPair ? sample.rest.opacity(0.95) : sample.sung)
                        .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stage.width * 0.4)
                        .background { tileBackground(tile, size: size) }
                        .shadow(color: .black.opacity(tile ? 0 : 0.3), radius: 7, y: 2)
                        .scaleEffect(x: isBar || isPair ? 1 : 0.86, y: isBar || isPair ? 1 : 1.1)
                        // Each word unfolds from the edge like a panel on a hinge, with a
                        // little spring; on the way out they fold shut one after another.
                        .blur(radius: reduceMotion ? 0 : size * 0.08 * (1 - min(a, 1)))
                        .rotation3DEffect(.degrees(reduceMotion ? 0 : (isLeft ? 1 : -1) * (84 * (1 - a) - 80 * out)),
                                          axis: (x: 0, y: 1, z: 0), anchor: isLeft ? .trailing : .leading, perspective: 0.6)
                        .offset(y: reduceMotion ? 0 : u * 0.25 * out)
                        .opacity(min(1, a * 1.6) * (1 - out))
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
            ((anchor[index] + 1)..<max(runs.count, anchor[index] + 1)).filter { !little[$0] }.map { arrivals[$0] }.reduce(0, +)
        }
        let keep: [Double] = bigAfter.map { max(0, min(1, 1 - ($0 - 1.6) * 0.9)) }
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
                    Text(run.revealed(at: time))
                        .font(font(labelSize, hero: true, id: run.id))
                        .foregroundStyle(ink.tileText)
                        .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7)
                        .padding(.horizontal, labelSize * 0.34).padding(.vertical, labelSize * 0.16)
                        .background(ink.tileColour, in: RoundedRectangle(cornerRadius: 3))
                        .rotationEffect(.degrees(run.id.isMultiple(of: 2) ? -3 : 2.5))
                        .scaleEffect(reduceMotion ? 1 : 0.8 + 0.2 * a)
                        .opacity(min(1, a * 1.6) * keep[run.id])
                } else {
                    let tile = tiled(run)
                    ZStack {
                        Text(run.revealed(at: time)).foregroundStyle(.black.opacity(tile ? 0 : 0.34)).offset(x: hard, y: hard * 1.15)
                        Text(run.revealed(at: time)).foregroundStyle(tile ? ink.tileText : sample.sung)
                    }
                    .font(font(bigSize, hero: true, id: run.id))
                    .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7)
                    .background { tileBackground(tile, size: bigSize) }
                    .scaleEffect(reduceMotion ? 1 : 0.86 + 0.14 * a)
                    .opacity(min(1, a * 1.6) * keep[run.id])
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
        let speed = lineSpeed
        let arrivals = runs.map { Self.arrive($0, at: time, over: 0.5 / speed) }
        let settled = runs.map { Self.glide((time - ($0.start - 0.05)) / (0.6 / speed)) }
        let ranks = runs.indices.map { i in settled[(i + 1)...].reduce(0, +) }
        // Which way this line's trail runs, and where the pills sit.
        let flipX: CGFloat = chance(nil, 1) < 0.5 ? 1 : -1
        let flipY: CGFloat = chance(nil, 2) < 0.7 ? 1 : -1
        let reach = (0.8 + 0.5 * chance(nil, 3)) / speed.squareRoot()
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
        // Quick songs get less float and a shorter run-in, so the eye can follow.
        let drift = reduceMotion ? 0.0 : 1.0 / speed
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
                let tilt = reduceMotion ? 0 : ((1 - eased) * tiltAmount * tiltSign + sin(time * 1.1 + Double(run.id)) * 1.6) / speed
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
                    Text(run.revealed(at: time))
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
                        if live {
                            // Two loose, hand-drawn outlines around the pill, redrawn a few
                            // times a second so the lines seem to boil.
                            let boil = reduceMotion ? 0 : Double(Int(time * 8)) * 0.9
                            ForEach(0..<2, id: \.self) { pass in
                                SketchyOutline(corner: corner * 1.15, amount: size * 0.035, phase: boil + Double(pass) * 2.3,
                                               salt: Double(run.id * 3 + pass))
                                    .stroke(ink.tileColour.opacity(pass == 0 ? 0.75 : 0.4),
                                            style: StrokeStyle(lineWidth: max(1.2, size * 0.026), lineCap: .round, lineJoin: .round))
                                    .scaleEffect(1.08 + 0.05 * Double(pass))
                                    .rotationEffect(.degrees((pass == 0 ? -1.6 : 1.4) * (reduceMotion ? 0 : 1)))
                                    .opacity(eased)
                            }
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
                .opacity(eased * max(0, 1 - max(0, rank - (speed > 1.6 ? 1.1 : 1.6)) * 0.7) * (live ? 1 : 0.85))
            }
        }
        .offset(y: -stageSize.height * 0.1 * leaving)
        .opacity(1 - leaving)
    }
}

// MARK: - Mosaic, Torque and Fable

extension StageLyricView {
    // MARK: Mosaic
    //
    // A collage: each word is set a different way as it arrives -- huge and cut
    // off by the edge of the stage with a star beside it, white on a colour bar
    // across a pale panel, or letter by letter on jumping baselines -- while the
    // words before it shrink into a small stack above.

    fileprivate func mosaic(_ runs: [StageRun], total: Int) -> some View {
        let arrivals = runs.map { Self.arriveStage($0, at: time, over: 0.45 / lineSpeed) }
        let ranks = runs.indices.map { i in arrivals[(i + 1)...].reduce(0, +) }
        let leaving = leave
        // A word on a tile (Cutout · Highlight) is always the huge kind, which can carry one.
        let kinds = runs.map { tiled($0) ? 0 : Int(chance($0.id, 20) * 3) % 3 }
        let stage = stageSize
        return StageLayout(place: { measured, bounds in
            measured.indices.map { index in
                // One continuous path: the word sits where it landed, then glides into the stack.
                let rank = ranks[index]
                let settle = CGFloat(Self.glide(min(rank, 1.2) / 1.2))
                let sway = (chance(index, 21) - 0.5) * 0.24 * bounds.width
                let home = CGPoint(x: bounds.midX + sway, y: bounds.midY + (chance(index, 22) - 0.5) * 0.16 * bounds.height)
                let slot = CGFloat(min(3, max(rank, 1)))
                let stack = CGPoint(x: bounds.midX - bounds.width * 0.2 + CGFloat(index % 2) * bounds.width * 0.04,
                                    y: bounds.minY + bounds.height * 0.12 + (slot - 1) * unit * 0.85)
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
                ZStack {
                    // The big setting, shrinking and dimming as the next word takes over.
                    Group {
                        if kinds[run.id] == 0 {
                            // Huge, running off the edge, with a star beside it.
                            let size = unit * 3.0
                            ZStack(alignment: .topTrailing) {
                                Text(run.revealed(at: time))
                                    .font(font(size, hero: true, id: run.id))
                                    .foregroundStyle(tile ? ink.tileText : sample.sung)
                                    .lineLimit(1).minimumScaleFactor(0.5).frame(maxWidth: stage.width * 1.1)
                                    .background { tileBackground(tile, size: size) }
                                    .shadow(color: .black.opacity(0.28), radius: 8, y: 3)
                                Star4().fill(ink.tileColour).frame(width: size * 0.32, height: size * 0.32)
                                    .rotationEffect(.degrees(reduceMotion ? 0 : time * 40))
                                    .offset(x: size * 0.1, y: -size * 0.12)
                            }
                            .offset(x: reduceMotion ? 0 : (1 - min(a, 1)) * stage.width * 0.35 * side)
                        } else if kinds[run.id] == 1 {
                            // White on a colour bar, laid across a pale panel.
                            let size = unit * 2.3
                            let wipe = reduceMotion ? 1 : Self.glide((time - (run.start - 0.04)) / (0.32 / lineSpeed))
                            let word = Text(run.revealed(at: time))
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
                                    .scaleEffect(x: 0.6 + 0.4 * Self.glide((time - (run.start + 0.05)) / 0.3), y: 1)
                                // The bar wipes across, then the word lands on it.
                                word.hidden()
                                    .padding(.horizontal, size * 0.28).padding(.vertical, size * 0.06)
                                    .background {
                                        ink.tileColour
                                            .mask(alignment: side > 0 ? .leading : .trailing) {
                                                GeometryReader { box in Rectangle().frame(width: box.size.width * wipe) }
                                            }
                                    }
                                    .rotationEffect(.degrees(tilt))
                                word.foregroundStyle(ink.tileText)
                                    .rotationEffect(.degrees(tilt))
                                    .opacity(Self.glide((time - (run.start + 0.1)) / 0.2))
                                    .offset(x: reduceMotion ? 0 : CGFloat(1 - wipe) * size * 0.5 * side)
                                Star4().fill(Color.white).frame(width: size * 0.3, height: size * 0.3)
                                    .shadow(color: .black.opacity(0.2), radius: 3)
                                    .rotationEffect(.degrees(reduceMotion ? 0 : time * 50))
                                    .offset(x: side * size * 0.1, y: -size * 0.62)
                                    .scaleEffect(wipe)
                            }
                            .shadow(color: .black.opacity(0.25), radius: 8, y: 3)
                        } else {
                            // Letter by letter, each on its own baseline.
                            let size = unit * 2.4
                            HStack(spacing: size * 0.02) {
                                ForEach(Array(run.text.enumerated()), id: \.offset) { index, character in
                                    let reveal = Self.glide((time - (run.start + Double(index) * 0.05)) / 0.25)
                                    Text(String(character))
                                        .font(font(size * (0.85 + 0.3 * CGFloat(chance(run.id * 31 + index, 24))), hero: true, id: run.id))
                                        .foregroundStyle(sample.sung)
                                        .offset(y: CGFloat(chance(run.id * 31 + index, 25) - 0.5) * size * 0.28 + CGFloat(1 - reveal) * size * 0.3)
                                        .rotationEffect(.degrees((chance(run.id * 31 + index, 26) - 0.5) * 22))
                                        .opacity(reveal)
                                }
                            }
                            .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: stage.width * 0.8)
                            .shadow(color: .black.opacity(0.28), radius: 8, y: 3)
                        }
                    }
                    .scaleEffect(1 - 0.72 * settle)
                    .opacity(min(1, a * 1.6) * (1 - Easing.smoothstep(settle * 1.6 - 0.2)))
                    // The small version it becomes, fading in as the big one fades out.
                    Text(run.revealed(at: time))
                        .font(font(unit * 0.6, hero: true, id: run.id))
                        .foregroundStyle(sample.rest.opacity(0.75 * max(0, 1 - (rank - 1.5) * 0.6)))
                        .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: stage.width * 0.4)
                        .opacity(Easing.smoothstep(settle * 1.6 - 0.5))
                }
            }
        }
        .opacity(1 - leaving)
    }

    // MARK: Torque
    //
    // Words are wrung into place: each twists in about its own vertical axis,
    // streaks, and settles with a thick extruded edge, stacking upwards with
    // the older words smaller and tipped the other way.

    fileprivate func torque(_ runs: [StageRun], total: Int) -> some View {
        let arrivals = runs.map { Self.arriveStage($0, at: time, over: 0.5 / lineSpeed) }
        let ranks = runs.indices.map { i in arrivals[(i + 1)...].reduce(0, +) }
        let leaving = leave
        let stage = stageSize
        return StageLayout(place: { measured, bounds in
            // The stack is centred whatever its height.
            var cursor = bounds.midY + measured.map { $0.height * 0.82 }.reduce(0, +) / 2
            return measured.indices.reversed().map { index -> (Int, CGPoint) in
                let rank = ranks[index]
                let height = measured[index].height * 0.82
                let y = cursor - height / 2
                cursor -= height
                return (index, CGPoint(x: bounds.midX + CGFloat(sin(Double(index) * 2.1 + Double(seed % 5))) * bounds.width * 0.05 * CGFloat(min(rank, 1)), y: y))
            }.sorted { $0.0 < $1.0 }.map(\.1)
        }) {
            ForEach(runs) { run in
                let a = arrivals[run.id]
                let rank = ranks[run.id]
                let sample = sample(run, of: total)
                let live = rank < 0.5
                let size = unit * max(0.55, 2.0 - 0.38 * min(rank, 3.5))
                let tile = tiled(run)
                // The word's own wringing, unless another word entrance has been chosen.
                let entrance = chosenEntrance(run, progress: a, size: size)
                let twist = reduceMotion || !entrance.native ? 0 : (1 - min(a, 1)) * 110 * (run.id.isMultiple(of: 2) ? 1 : -1)
                // Wrung and loosened again, all the while it is held.
                let breathe = reduceMotion ? 0 : sin(time * 2.1 + Double(run.id) * 1.7) * (live ? 11 : 5)
                let wander = reduceMotion ? 0 : CGFloat(sin(time * 1.3 + Double(run.id) * 2.3)) * unit * (live ? 0.14 : 0.08)
                ZStack {
                    // The thick edge: darker copies stepping back.
                    if live {
                        ForEach(1..<7, id: \.self) { step in
                            Text(run.revealed(at: time, uppercase: false))
                                .foregroundStyle(sample.sung.opacity(0.55))
                                .offset(x: CGFloat(step) * size * 0.012, y: CGFloat(step) * size * 0.022)
                                .brightness(-0.25)
                        }
                    }
                    Text(run.revealed(at: time))
                        .foregroundStyle(tile ? ink.tileText : live ? sample.sung : sample.rest.opacity(0.85))
                        .background { tileBackground(tile, size: size) }
                }
                .font(font(size, hero: true, id: run.id))
                .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stage.width * 0.7)
                .shadow(color: .black.opacity(live ? 0.3 : 0.15), radius: 6, y: 3)
                .rotationEffect(.degrees(reduceMotion ? 0 : (live ? -4 : min(rank, 2) * 3 * (run.id.isMultiple(of: 2) ? 1 : -1))))
                .rotation3DEffect(.degrees(twist + breathe), axis: (x: 0, y: 1, z: 0), perspective: 0.6)
                .rotation3DEffect(.degrees(breathe * 0.5), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
                .rotation3DEffect(.degrees(entrance.fold), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
                .rotationEffect(.degrees(entrance.spin))
                .scaleEffect(entrance.scale)
                .offset(x: wander + entrance.offset.width, y: entrance.offset.height)
                .blur(radius: reduceMotion ? 0 : size * 0.07 * (1 - min(a, 1)))
                .opacity(min(1, a * 1.6) * max(0, 1 - max(0, rank - 3) * 0.8))
            }
        }
        .rotation3DEffect(.degrees(reduceMotion ? 0 : -10), axis: (x: 0, y: 1, z: 0), perspective: 0.5)
        .offset(y: stage.height * 0.12 * leaving)
        .opacity(1 - leaving)
    }

    // MARK: Fable
    //
    // A staircase of words that never stops moving. The newest sits low and to
    // the right in the accent colour; each word before it steps up and to the
    // left, smaller and quieter. Words arrive three ways in turn -- a column of
    // echoes folding into the word, letters spread along an arc that tighten
    // and straighten, or a blur that pulls into focus -- and everything floats
    // a little, on its own slow loop, while it is held.

    fileprivate func fable(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let arrivals = runs.map { Self.glide((time - ($0.start - 0.05)) / (0.62 / speed)) }
        let ranks = runs.indices.map { i in arrivals[(i + 1)...].reduce(0, +) }
        let stage = stageSize
        let leaving = leave
        let drift = reduceMotion ? 0.0 : 1.0
        // Every line gets its own shape, accent, favourite arrival and lean.
        let shape = Int(chance(nil, 50) * 4) % 4
        let accent = Int(chance(nil, 51) * 4) % 4
        let favourite = Int(chance(nil, 52) * 3) % 3
        let lean = (chance(nil, 53) - 0.5) * 7
        let scale = 0.9 + 0.3 * chance(nil, 54)
        // Where the newest word sits, and the step to each older one.
        let origin: (x: CGFloat, y: CGFloat) = [(0.14, 0.3), (-0.14, 0.3), (0.0, 0.3), (0.2, 0.18)][shape]
        let step: (x: CGFloat, y: CGFloat) = [(-0.13, -0.2), (0.13, -0.2), (0.0, -0.18), (-0.2, -0.09)][shape]
        return StageLayout(place: { measured, bounds in
            measured.indices.map { index in
                // The steps close up a little further back, so a long line stays on the stage.
                let raw = CGFloat(ranks[index])
                let r = raw <= 3 ? raw : 3 + (raw - 3) * 0.35
                var x = bounds.midX + bounds.width * (origin.x + step.x * r) + sin(r * 0.8) * bounds.width * 0.025
                // The zigzag column swings from side to side as it climbs.
                if shape == 2 { x += CGFloat(sin(Double(r) * 1.5 + 0.6)) * bounds.width * 0.15 }
                var y = bounds.midY + bounds.height * (origin.y + step.y * r)
                x += CGFloat(sin(time * 0.9 + Double(index) * 1.9)) * unit * 0.2 * drift
                y += CGFloat(cos(time * 0.7 + Double(index) * 1.3)) * unit * 0.15 * drift
                // New words swing in along an arc from up and to the left.
                let enter = CGFloat(1 - arrivals[index])
                x -= enter * bounds.width * 0.1
                y -= enter * bounds.height * 0.16 + CGFloat(sin(Double(enter) * .pi)) * bounds.height * 0.06
                return CGPoint(x: x, y: y)
            }
        }) {
            ForEach(runs) { run in
                let a = arrivals[run.id]
                let rank = ranks[run.id]
                let sample = sample(run, of: total)
                let newest = rank < 0.5
                let size = unit * scale * max(0.55, 1.4 - 0.24 * min(rank, 3.6))
                let tile = tiled(run)
                let kind = chance(run.id, 42) < 0.55 ? favourite : Int(chance(run.id, 40) * 3) % 3
                let colour: Color = tile ? ink.tileText : newest ? sample.sung : sample.rest.opacity(max(0.45, 0.85 - 0.1 * rank))
                let word = Text(run.revealed(at: time))
                    .font(font(size, hero: true, id: run.id))
                    .foregroundStyle(colour)
                    .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: stage.width * 0.5)
                    .background { tileBackground(tile, size: size) }
                HStack(spacing: size * 0.2) {
                    Group {
                        if kind == 0 {
                            // A column of echoes folds into the word.
                            ZStack {
                                ForEach([-2, -1, 1, 2], id: \.self) { k in
                                    word.opacity(0.3 * (1 - a))
                                        .offset(y: CGFloat(k) * size * 1.0 * CGFloat(1 - a))
                                }
                                word
                            }
                        } else if kind == 1 {
                            // Letters along an arc, tightening and straightening as it lands.
                            let letters = Array(run.text)
                            HStack(spacing: size * (0.7 * (1 - a) + 0.02)) {
                                ForEach(Array(letters.enumerated()), id: \.offset) { index, letter in
                                    let from = CGFloat(index) - CGFloat(letters.count - 1) / 2
                                    let shown = Self.glide((time - (run.start - 0.05 + Double(index) * 0.03)) / 0.2)
                                    Text(String(letter))
                                        .font(font(size, hero: true, id: run.id))
                                        .foregroundStyle(newest && a < 1 ? ink.tileColour : colour)
                                        .rotationEffect(.degrees(Double(from) * 16 * (1 - a)))
                                        .offset(y: from * from * size * 0.07 * CGFloat(1 - a))
                                        .opacity(shown)
                                }
                            }
                            .lineLimit(1)
                        } else {
                            // A blur pulls into focus.
                            word.blur(radius: reduceMotion ? 0 : size * 0.22 * (1 - a))
                                .scaleEffect(reduceMotion ? 1 : 1 + 0.25 * (1 - a))
                        }
                    }
                    if accent == 0 && chance(run.id, 41) < 0.3 {
                        Star4().fill(ink.tileColour).frame(width: size * 0.5, height: size * 0.5)
                            .rotationEffect(.degrees(reduceMotion ? 0 : (1 - a) * 160 + time * 22))
                            .scaleEffect(a)
                    }
                }
                .background {
                    // A soft glow of the accent colour behind the newest word.
                    if newest {
                        Ellipse().fill(ink.tileColour.opacity(0.28)).frame(width: size * 3.2, height: size * 1.5)
                            .blur(radius: size * 0.45).opacity(a)
                    }
                }
                .overlay(alignment: .bottom) {
                    if newest && accent == 1 {
                        // A brush stroke underneath, drawn left to right.
                        Capsule().fill(ink.tileColour)
                            .frame(width: size * 2.4 * CGFloat(Self.glide((time - run.start) / 0.35)), height: size * 0.07)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .offset(y: size * 0.16)
                    } else if newest && accent == 2 {
                        // A loose ring that swells round it.
                        Capsule().stroke(ink.tileColour, lineWidth: max(2, size * 0.04))
                            .frame(width: size * 3.0, height: size * 1.1)
                            .rotationEffect(.degrees(-4))
                            .scaleEffect(0.8 + 0.2 * a).opacity(a * 0.9)
                            .offset(y: size * 0.1)
                    }
                }
                .shadow(color: .black.opacity(0.25), radius: 5, y: 2)
                .opacity(min(1, a * 1.7) * max(0, 1 - max(0, rank - 4.2) * 0.8))
            }
        }
        .rotationEffect(.degrees(reduceMotion ? 0 : lean))
        .opacity(1 - leaving)
    }

    /// What a word does when the viewer chose a particular entrance rather than
    /// leaving it to the layout. `native` means the layout's own motion stays.
    fileprivate func chosenEntrance(_ run: StageRun, progress a: Double, size: CGFloat)
        -> (native: Bool, offset: CGSize, scale: CGFloat, spin: Double, fold: Double) {
        let left = 1 - min(a, 1)
        let side: CGFloat = run.id.isMultiple(of: 2) ? 1 : -1
        let none = (native: true, offset: CGSize.zero, scale: CGFloat(1), spin: 0.0, fold: 0.0)
        if reduceMotion { return none }
        switch settings.arrival(in: composition, for: LyricsFetcher.Word(text: run.text, start: run.start, end: run.end)) {
        case .lift:
            return (false, CGSize(width: 0, height: CGFloat(left) * size * 0.7), 1, 0, 0)
        case .pop:
            return (false, .zero, CGFloat(0.55 + 0.45 * min(a, 1.25)), 0, 0)
        case .orbit:
            return (false, CGSize(width: side * CGFloat(left) * size * 0.9, height: -CGFloat(left) * size * 0.35), 1, 12 * left * Double(side), 0)
        case .glide:
            return (false, CGSize(width: side * CGFloat(left) * size * 1.5, height: 0), 1, 0, 0)
        case .unfold:
            return (false, .zero, 1, 0, -85 * left)
        default:
            return none
        }
    }

    /// Arrival for the stage layouts that live in extensions.
    fileprivate static func arriveStage(_ run: StageRun, at time: Double, over span: Double) -> Double {
        let x = min(max((time - (run.start - 0.04)) / span, 0), 1)
        let c = 1.4, t = x - 1
        return x <= 0 ? 0 : max(0, 1 + (c + 1) * t * t * t + c * t * t)
    }
}

/// A four-pointed star.
struct Star4: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        let c = CGPoint(x: rect.midX, y: rect.midY)
        let outer = min(rect.width, rect.height) / 2, inner = outer * 0.28
        for i in 0..<8 {
            let angle = Double(i) * .pi / 4 - .pi / 2
            let r = i.isMultiple(of: 2) ? outer : inner
            let p = CGPoint(x: c.x + CGFloat(cos(angle)) * r, y: c.y + CGFloat(sin(angle)) * r)
            if i == 0 { path.move(to: p) } else { path.addLine(to: p) }
        }
        path.closeSubpath()
        return path
    }
}

/// A rounded rectangle drawn the way a hand draws one: the line wanders a
/// little off true, differently each time `phase` changes.
struct SketchyOutline: Shape {
    var corner: CGFloat
    var amount: CGFloat
    var phase: Double
    var salt: Double

    func path(in rect: CGRect) -> Path {
        let radius = min(corner, rect.width / 2, rect.height / 2)
        let across = max(0, rect.width - 2 * radius), down = max(0, rect.height - 2 * radius)
        let arc = .pi / 2 * radius
        let lengths = [across, arc, down, arc, across, arc, down, arc]
        let perimeter = lengths.reduce(0, +)
        guard perimeter > 1 else { return Path() }
        let count = 72
        var points: [CGPoint] = []
        for i in 0..<count {
            var along = perimeter * CGFloat(i) / CGFloat(count)
            var side = 0
            while side < 7, along > lengths[side] { along -= lengths[side]; side += 1 }
            var point = CGPoint.zero, normal = CGPoint.zero
            func onArc(_ centre: CGPoint, from start: Double) {
                let angle = start + Double(along / max(radius, 0.001))
                normal = CGPoint(x: cos(angle), y: sin(angle))
                point = CGPoint(x: centre.x + radius * normal.x, y: centre.y + radius * normal.y)
            }
            switch side {
            case 0: point = CGPoint(x: rect.minX + radius + along, y: rect.minY); normal = CGPoint(x: 0, y: -1)
            case 1: onArc(CGPoint(x: rect.maxX - radius, y: rect.minY + radius), from: -.pi / 2)
            case 2: point = CGPoint(x: rect.maxX, y: rect.minY + radius + along); normal = CGPoint(x: 1, y: 0)
            case 3: onArc(CGPoint(x: rect.maxX - radius, y: rect.maxY - radius), from: 0)
            case 4: point = CGPoint(x: rect.maxX - radius - along, y: rect.maxY); normal = CGPoint(x: 0, y: 1)
            case 5: onArc(CGPoint(x: rect.minX + radius, y: rect.maxY - radius), from: .pi / 2)
            case 6: point = CGPoint(x: rect.minX, y: rect.maxY - radius - along); normal = CGPoint(x: -1, y: 0)
            default: onArc(CGPoint(x: rect.minX + radius, y: rect.minY + radius), from: .pi)
            }
            // Low whole-number frequencies, so the loop closes without a seam.
            let t = 2 * Double.pi * Double(i) / Double(count)
            let wobble = 0.55 * sin(3 * t + phase * 1.1 + salt) + 0.3 * sin(7 * t - phase * 0.8 + salt * 2.1)
                + 0.15 * sin(11 * t + phase * 1.7)
            let push = amount * CGFloat(wobble)
            points.append(CGPoint(x: point.x + normal.x * push, y: point.y + normal.y * push))
        }
        var path = Path()
        func middle(_ a: CGPoint, _ b: CGPoint) -> CGPoint { CGPoint(x: (a.x + b.x) / 2, y: (a.y + b.y) / 2) }
        path.move(to: middle(points[count - 1], points[0]))
        for i in 0..<count {
            path.addQuadCurve(to: middle(points[i], points[(i + 1) % count]), control: points[i])
        }
        path.closeSubpath()
        return path
    }
}
