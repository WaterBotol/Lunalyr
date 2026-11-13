import SwiftUI

// Fable: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.
//
// A storybook page per line. The page is set in a shape chosen for the line
// (an arc, a stair, a wave or a plain centred block) and each phrase (`StagePhrases`)
// is one line of type on it, so the page breaks where the singer breathes.
// Words are written in with a pen stroke as they are sung: a function word in
// a flick, a content word by its length, a held word slowly over the time it
// is sung, a quick run in one stroke across the group. Each phrase's focal word
// also settles onto its baseline, and the line's one important word gets its
// accent (corner brackets or a star) as its stroke ends. A long line turns the
// page by phrase: a page holds at most two phrases, and the old page folds
// away about its spine as the next is begun. Nothing moves once written.

extension StageLyricView {
    /// Fable's entry point, called by the dispatcher (`StageLyricView.layout`).
    func fableBody(runs: [StageRun], shown: [StageRun]) -> some View {
        fable(runs)
    }

    /// Fable draws Reduce Motion itself: its words and pages fade, nothing
    /// travels, so it needs no shared dissolve.
    static let fableOwnsReduceMotion = true

    func fable(_ runs: [StageRun]) -> some View {
        let plan = Self.fablePlan(runs)
        let style = FableStyle.forLine(seed)
        let stage = stageSize
        // At least the 5 % title-safe margin on every side.
        let marginX = max(unit * 0.5, stage.width * 0.05)
        let marginY = max(unit * 0.5, stage.height * 0.05)
        let available = CGSize(width: max(1, stage.width - 2 * marginX), height: max(1, stage.height - 2 * marginY))
        let pages = plan.pages.indices.filter { Self.fablePageShown($0, plan: plan, at: time, exitStart: exitStart) }
        return ZStack {
            ForEach(pages, id: \.self) { index in
                let turn = Self.fablePageTurn(index, plan: plan, at: time, exitStart: exitStart)
                fablePage(plan.pages[index], plan: plan, style: style, runs: runs)
                    // One shadow for the page, not one per word.
                    .compositingGroup()
                    .shadow(color: .black.opacity(0.3), radius: max(3, unit * 0.08), y: 2)
                    // Long pages and big readings are shrunk to fit, never cut off.
                    .visualEffect { page, geometry in
                        page.scaleEffect(LyricStageFit.scale(content: geometry.size, available: available))
                    }
                    // The page folds away about its spine (the edge reading starts from).
                    .scaleEffect(x: reduceMotion ? 1 : max(0.001, 1 - turn * 0.85), y: 1,
                                 anchor: plan.rightToLeft ? .trailing : .leading)
                    .brightness(reduceMotion ? 0 : -0.3 * turn)
                    .opacity(1 - turn)
            }
        }
        .frame(width: stage.width, height: stage.height)
    }

    private func fablePage(_ page: FablePlan.Page, plan: FablePlan, style: FableStyle, runs: [StageRun]) -> some View {
        FableLayout(shape: style.shape, rows: page.rowSlots, rightToLeft: plan.rightToLeft,
                    gap: unit * 0.1, rowGap: unit * 0.1) {
            // The plan is made from these runs, so every id is one of them.
            ForEach(page.runs, id: \.self) { id in
                fableWord(runs[id], plan: plan, style: style, runs: runs)
            }
        }
    }

    @ViewBuilder
    private func fableWord(_ run: StageRun, plan: FablePlan, style: FableStyle, runs: [StageRun]) -> some View {
        let role = plan.roles[run.id]
        let size = Self.fableSize(role, wordCount: runs.count, unit: unit)
        let stroke = plan.strokes[run.id]
        let written = Self.fableWritten(stroke, at: time)
        let entrance = chosenEntrance(run, progress: StageMotion.arrival(run.start, at: time), size: size)
        let native = entrance.native
        let shown = time >= run.start - StageMotion.lead
        // Reduce Motion, or another entrance chosen: a fade on the word's beat instead of the stroke.
        let fade = Easing.smoothstep((time - (run.start - StageMotion.lead)) / WallpaperMotion.reducedDissolve)
        let ink = reduceMotion || !native ? 1 : written
        let settle = reduceMotion || !native ? 0 : Self.fableSettle(run, role: role, at: time)
        let accent = run.id == plan.accent ? Self.fableAccent(stroke, at: time, reduceMotion: reduceMotion) : 0
        let live = Self.torqueLive(run, runs: runs, at: time)
        fableFace(run, size: size, style: style, accent: accent, live: live, rightToLeft: plan.rightToLeft,
                  sample: sample(run, of: runs.count))
            .modifier(FableStroke(progress: ink, rightToLeft: plan.rightToLeft))
            .offset(y: size * CGFloat(settle))
            .rotation3DEffect(.degrees(native ? 0 : entrance.fold), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
            .rotationEffect(.degrees(native ? 0 : entrance.spin), anchor: .bottom)
            .scaleEffect(native ? 1 : entrance.scale)
            .offset(native ? .zero : entrance.offset)
            .opacity(shown ? (reduceMotion || !native ? fade : 1) : 0)
    }

    /// A word with its reading under it and, on the line's accent word, the accent.
    @ViewBuilder
    private func fableFace(_ run: StageRun, size: CGFloat, style: FableStyle, accent: Double, live: Double,
                           rightToLeft: Bool, sample: LyricInkSample) -> some View {
        let tile = tiled(run)
        let tight = LyricScript.keepsSyllables(run.text)
        let reading = Self.revealedReading(of: run, in: readings, at: time, colour: sample.rest.opacity(0.9))
        let face = font(size, hero: true, id: run.id)
        let rest = tile ? ink.tileText : sample.rest.opacity(0.86)
        let lit = tile ? ink.tileText : sample.sung
        let word = ZStack {
            if live < 0.999 { self.word(run, rest).font(face) }
            if live > 0.001 { self.word(run, lit).font(face).opacity(live) }
        }
        .lineLimit(1).fixedSize()
        .background { tileBackground(tile, size: size) }
        .overlay { if accent > 0.001 { fableAccent(style.accent, size: size, shown: accent, rightToLeft: rightToLeft) } }
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
        .padding(.horizontal, size * (tight ? 0.18 : 0.24))
        .padding(.vertical, size * 0.16)
    }

    @ViewBuilder
    private func fableAccent(_ accent: FableStyle.Accent, size: CGFloat, shown: Double, rightToLeft: Bool) -> some View {
        let colour = ink.tileColour
        let pop = reduceMotion ? 1 : CGFloat(StageMotion.fableOut(shown))
        switch accent {
        case .brackets:
            FableBrackets(arm: size * 0.22)
                .stroke(colour, style: StrokeStyle(lineWidth: max(2, size * 0.05), lineCap: .round, lineJoin: .round))
                .padding(.horizontal, -size * 0.16).padding(.vertical, -size * 0.04)
                // The brackets close in on the word.
                .scaleEffect(1.25 - 0.25 * pop)
                .opacity(min(1, shown * 2))
        case .star:
            Star4().fill(colour)
                .frame(width: size * 0.42, height: size * 0.42)
                .scaleEffect(pop)
                .opacity(min(1, shown * 2))
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: rightToLeft ? .topLeading : .topTrailing)
                .offset(x: (rightToLeft ? -1 : 1) * size * 0.22, y: -size * 0.12)
        }
    }

    // MARK: Sizes and motion (pure functions of time)

    /// A word's size by its role: the line's accent word largest, little
    /// words smallest. A line of one or two words is all set large.
    static func fableSize(_ role: FableRole, wordCount: Int, unit: CGFloat) -> CGFloat {
        if wordCount <= 2 { return unit * 1.75 }
        switch role {
        case .accent: return unit * 1.75
        case .focal: return unit * 1.4
        case .content: return unit * 1.15
        case .function: return unit * 0.85
        }
    }

    /// How much of a word its pen stroke has written, 0...1: the pen's own
    /// speed (smoothstep), from the stroke's start over its length.
    static func fableWritten(_ stroke: FablePlan.Stroke, at time: Double) -> Double {
        guard time >= stroke.start else { return 0 }
        return Easing.smoothstep((time - stroke.start) / max(0.01, stroke.length))
    }

    /// How far a phrase's focal word still sits below its baseline (a share
    /// of its size), settling with the out-ease as it is written.
    static func fableSettle(_ run: StageRun, role: FableRole, at time: Double) -> Double {
        guard role == .focal || role == .accent else { return 0 }
        let p = (time - (run.start - StageMotion.lead)) / (2.4 * StageMotion.fableUnit)
        return 0.2 * (1 - StageMotion.fableOut(p))
    }

    /// How far the line's accent has come on, 0...1: it snaps on just after
    /// its word's stroke ends (a step under Reduce Motion, then a fade).
    static func fableAccent(_ stroke: FablePlan.Stroke, at time: Double, reduceMotion: Bool) -> Double {
        let start = stroke.start + stroke.length + 0.6 * StageMotion.fableUnit
        if reduceMotion { return Easing.smoothstep((time - start) / WallpaperMotion.reducedDissolve) }
        return min(1, max(0, (time - start) / (2 * StageMotion.fableUnit)))
    }

    /// How far page `index` has turned away, 0...1: when the next page is
    /// begun, or when the line leaves. A cross-fade under Reduce Motion.
    static func fablePageTurn(_ index: Int, plan: FablePlan, at time: Double, exitStart: Double) -> Double {
        var start = Double.infinity
        if plan.pages.indices.contains(index + 1) { start = plan.pages[index + 1].turnStart }
        if exitStart.isFinite { start = min(start, exitStart) }
        guard start.isFinite else { return 0 }
        return StageMotion.fableIn((time - start) / StageMotion.fableTurn)
    }

    /// Whether page `index` is drawn at `time`: from its first word's beat
    /// until it has turned away.
    static func fablePageShown(_ index: Int, plan: FablePlan, at time: Double, exitStart: Double) -> Bool {
        guard plan.pages.indices.contains(index) else { return false }
        guard time >= plan.pages[index].start - StageMotion.lead else { return false }
        return fablePageTurn(index, plan: plan, at: time, exitStart: exitStart) < 1
    }

    // MARK: The plan (per line, cached)

    enum FableRole: Equatable {
        /// The line's one important word: it wears the accent.
        case accent
        /// A phrase's focal word.
        case focal
        case content
        case function
    }

    struct FablePlan: Equatable {
        struct Page: Equatable {
            /// Its phrases (indices into the line's phrasing) and runs, in reading order.
            var phrases: [Int]
            var runs: [Int]
            /// One row per phrase, as run ids.
            var rows: [[Int]]
            /// Its first onset, and when the page before it starts to turn away.
            var start: Double
            var turnStart: Double

            var rowSlots: [[Int]] { rows.map { row in row.compactMap { runs.firstIndex(of: $0) } } }
        }

        /// A pen stroke: when it starts (s) and how long it takes.
        struct Stroke: Equatable {
            var start: Double
            var length: Double
        }

        var pages: [Page]
        var roles: [FableRole]
        var strokes: [Stroke]
        /// The run that wears the line's accent.
        var accent: Int?
        var lineTimed: Bool
        var rightToLeft: Bool

        static let empty = FablePlan(pages: [], roles: [], strokes: [], accent: nil, lineTimed: false, rightToLeft: false)
    }

    private struct FableKey: Hashable {
        var texts: [String]
        var starts: [Double]
        var ends: [Double]
    }

    private final class FableCache: @unchecked Sendable {
        let lock = NSLock()
        var plans: [FableKey: FablePlan] = [:]
    }

    private static let fableCache = FableCache()

    /// The line's plan: pages, roles, strokes and accent. Cached per line.
    static func fablePlan(_ runs: [StageRun]) -> FablePlan {
        let key = FableKey(texts: runs.map(\.text), starts: runs.map(\.start), ends: runs.map(\.end))
        fableCache.lock.lock()
        if let hit = fableCache.plans[key] { fableCache.lock.unlock(); return hit }
        fableCache.lock.unlock()
        let plan = makeFablePlan(runs, phrasing: StagePhrases.phrasing(runs))
        fableCache.lock.lock()
        if fableCache.plans.count >= 64 { fableCache.plans.removeAll(keepingCapacity: true) }
        fableCache.plans[key] = plan
        fableCache.lock.unlock()
        return plan
    }

    /// Most phrases and runs on one page.
    static let fablePagePhrases = 2
    static let fablePageRuns = 8

    static func makeFablePlan(_ runs: [StageRun], phrasing: StagePhrasing) -> FablePlan {
        guard !runs.isEmpty, phrasing.words.count == runs.count else { return .empty }
        let infos = phrasing.words
        let accent = fableAccentRun(runs, phrasing: phrasing)
        let roles: [FableRole] = infos.map { info in
            if info.run == accent { return .accent }
            if info.isFocal { return .focal }
            return info.isFunction ? .function : .content
        }
        let strokes = fableStrokes(runs, phrasing: phrasing, roles: roles)
        let groups = phrasing.lineTimed ? [Array(phrasing.phrases.indices)] : fablePages(phrasing.phrases)
        var pages: [FablePlan.Page] = []
        for group in groups {
            let rows = group.map { Array(phrasing.phrases[$0].runs) }
            let ids = rows.flatMap { $0 }
            let start = ids.map { runs[$0].start }.min() ?? 0
            var turnStart = -Double.infinity
            if let previous = pages.last {
                let lastLanded = previous.runs.map { runs[$0].start }.max() ?? start
                let early = start - StageMotion.lead - StageMotion.fableTurnLead
                turnStart = min(start - StageMotion.lead, max(early, lastLanded + 2 * StageMotion.fableUnit))
            }
            pages.append(FablePlan.Page(phrases: group, runs: ids, rows: rows, start: start, turnStart: turnStart))
        }
        return FablePlan(pages: pages, roles: roles, strokes: strokes, accent: accent, lineTimed: phrasing.lineTimed,
                         rightToLeft: phrasing.rightToLeft)
    }

    /// Phrases gathered into pages: at most two phrases and eight runs a
    /// page. Where a pause is followed by a phrase that could go with the
    /// one after it, the page is turned at the pause instead.
    static func fablePages(_ phrases: [StagePhrase]) -> [[Int]] {
        var pages: [[Int]] = []
        var page: [Int] = []
        var count = 0
        for (index, phrase) in phrases.enumerated() {
            var fits = page.count < fablePagePhrases && count + phrase.count <= fablePageRuns
            if fits, page.count == 1, phrase.breakBefore == .gap, phrases.indices.contains(index + 1) {
                let next = phrases[index + 1]
                // Turn at the pause when the next two make a page of their own.
                if next.breakBefore != .gap, phrase.count + next.count <= fablePageRuns { fits = false }
            }
            if !fits, !page.isEmpty {
                pages.append(page)
                page = []
                count = 0
            }
            page.append(index)
            count += phrase.count
        }
        if !page.isEmpty { pages.append(page) }
        return pages
    }

    /// The line's accent word: the phrase focal held longest (ties: the
    /// later one); on a line-timed line, the longest focal word.
    static func fableAccentRun(_ runs: [StageRun], phrasing: StagePhrasing) -> Int? {
        var best: Int?
        for phrase in phrasing.phrases {
            let id = phrase.focal
            guard runs.indices.contains(id), phrasing.words.indices.contains(id) else { continue }
            guard let current = best else { best = id; continue }
            if phrasing.lineTimed {
                if runs[id].text.count > runs[current].text.count { best = id }
            } else if phrasing.words[id].duration >= phrasing.words[current].duration - 0.05 {
                best = id
            }
        }
        return best
    }

    /// Each word's pen stroke. A quick run is one stroke across the group:
    /// each member is written from its own beat until the next member's.
    static func fableStrokes(_ runs: [StageRun], phrasing: StagePhrasing, roles: [FableRole]) -> [FablePlan.Stroke] {
        let unit = StageMotion.fableUnit
        var strokes: [FablePlan.Stroke] = []
        for (index, run) in runs.enumerated() {
            let info = phrasing.words[index]
            let start = run.start - StageMotion.lead
            if phrasing.lineTimed {
                // Written together, phrase by phrase, a little apart.
                strokes.append(FablePlan.Stroke(start: start + Double(info.phrase) * 0.8 * unit, length: 3 * unit))
                continue
            }
            var length: Double
            switch roles[index] {
            case .function: length = unit
            case .content: length = min(4 * unit, max(1.5 * unit, Double(run.text.count) * 0.3 * unit))
            case .focal, .accent: length = min(4 * unit, max(2.5 * unit, Double(run.text.count) * 0.4 * unit))
            }
            if info.isHeld { length = max(length, min(1.2, info.duration * 0.85)) }
            if let group = info.runGroup, runs.indices.contains(index + 1), phrasing.words[index + 1].runGroup == group {
                length = max(0.05, runs[index + 1].start - run.start)
            }
            strokes.append(FablePlan.Stroke(start: start, length: length))
        }
        return strokes
    }
}

// MARK: - Fable's parts

extension StageMotion {
    /// Fable's base unit (s).
    static let fableUnit = 0.1
    /// A page starts to turn away this long before the next page's first word.
    static let fableTurnLead = 0.3
    /// A page turning away.
    static let fableTurn = 0.24

    /// Fable's one out-ease: easeOutBack, overshooting about 10 %.
    static func fableOut(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        let c1 = 1.4, c3 = c1 + 1
        let u = t - 1
        return 1 + c3 * u * u * u + c1 * u * u
    }

    /// Fable's one in-ease: easeInCubic, for pages turning away.
    static func fableIn(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        return t * t * t
    }
}

/// Writes a word in: a soft-edged, slightly slanted wipe in reading direction.
struct FableStroke: ViewModifier {
    var progress: Double
    var rightToLeft: Bool

    /// The share of the word the soft edge covers.
    static let edge = 0.3

    func body(content: Content) -> some View {
        let stops = Self.stops(progress)
        content.mask {
            LinearGradient(stops: [Gradient.Stop(color: .white, location: stops.solid),
                                   Gradient.Stop(color: .white.opacity(0), location: stops.clear)],
                           startPoint: UnitPoint(x: rightToLeft ? 1 : 0, y: 0.35),
                           endPoint: UnitPoint(x: rightToLeft ? 0 : 1, y: 0.65))
                .opacity(progress <= 0 ? 0 : 1)
        }
    }

    /// Where the solid ink ends and the clear part starts, 0...1 across the word.
    static func stops(_ progress: Double) -> (solid: Double, clear: Double) {
        if progress >= 1 { return (1, 1) }
        let head = max(0, progress) * (1 + edge)
        let clear = min(1, head)
        let solid = min(max(head - edge, 0), clear)
        return (solid, max(clear, solid))
    }
}

/// One Fable line's look: the shape its phrases are set in and the accent
/// its important word wears. Chosen by the line, so the same line looks the
/// same on every render; neighbouring lines never share a shape, the accent
/// alternates, and all eight pairings come round before any repeats.
struct FableStyle: Equatable {
    enum Shape: Int, CaseIterable {
        case arc, stair, wave, block
    }

    enum Accent: Int, CaseIterable {
        case brackets, star
    }

    var shape: Shape
    var accent: Accent

    static func forLine(_ seed: Int) -> FableStyle {
        let n = Shape.allCases.count
        // Floor division, so negative seeds keep the pattern too.
        let lap = seed >= 0 ? seed / n : -((-seed + n - 1) / n)
        func wrap(_ value: Int, _ count: Int) -> Int { ((value % count) + count) % count }
        // The shape moves on one more each lap, so the pairings shift.
        return FableStyle(shape: Shape(rawValue: wrap(seed + lap, n)) ?? .arc,
                          accent: Accent(rawValue: wrap(seed, Accent.allCases.count)) ?? .brackets)
    }
}

/// Lays a Fable page out: one row per phrase, words on a shared baseline that
/// the shape bends, every word in a box of its own that nothing else enters.
/// Reports the page's own size; the caller scales it to fit the stage.
struct FableLayout: Layout {
    var shape: FableStyle.Shape
    /// Rows, as indices into the subviews.
    var rows: [[Int]]
    var rightToLeft: Bool
    var gap: CGFloat
    var rowGap: CGFloat

    struct Arrangement: Equatable {
        var size: CGSize
        /// Each word's box, from the page's top-left corner.
        var frames: [CGRect]
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        arrangement(subviews).size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let sizes = subviews.map { $0.sizeThatFits(.unspecified) }
        let laid = arrangement(subviews)
        let origin = CGPoint(x: bounds.midX - laid.size.width / 2, y: bounds.midY - laid.size.height / 2)
        for (index, subview) in subviews.enumerated() where laid.frames.indices.contains(index) {
            let frame = laid.frames[index]
            subview.place(at: CGPoint(x: origin.x + frame.midX, y: origin.y + frame.midY), anchor: .center,
                          proposal: ProposedViewSize(sizes[index]))
        }
    }

    private func arrangement(_ subviews: Subviews) -> Arrangement {
        Self.arrange(subviews.map { $0.sizeThatFits(.unspecified) }, rows: rows, shape: shape, rightToLeft: rightToLeft,
                     gap: gap, rowGap: rowGap,
                     baselines: subviews.map { $0.dimensions(in: .unspecified)[VerticalAlignment.firstTextBaseline] })
    }

    /// The page: rows in order, each bent into the shape; each row starts
    /// below the lowest point of the one before. A word in no row gets a
    /// zero box at the origin. Right to left, the page is mirrored.
    static func arrange(_ sizes: [CGSize], rows: [[Int]], shape: FableStyle.Shape, rightToLeft: Bool,
                        gap: CGFloat, rowGap: CGFloat, baselines: [CGFloat]? = nil) -> Arrangement {
        var frames = [CGRect](repeating: .zero, count: sizes.count)
        let valid = rows.map { row in row.filter { sizes.indices.contains($0) } }.filter { !$0.isEmpty }
        guard !valid.isEmpty else { return Arrangement(size: .zero, frames: frames) }
        func baseline(_ index: Int) -> CGFloat {
            guard let baselines, baselines.indices.contains(index) else { return sizes[index].height / 2 }
            return min(sizes[index].height, max(0, baselines[index]))
        }
        let rowWidths: [CGFloat] = valid.map { row in
            row.map { sizes[$0].width }.reduce(0, +) + gap * CGFloat(row.count - 1)
        }
        let widest = rowWidths.max() ?? 0
        var placedAll: [Int] = []
        var top: CGFloat = 0
        var order = 0
        for (r, row) in valid.enumerated() {
            let rowHeight = row.map { sizes[$0].height }.max() ?? 0
            let start: CGFloat
            switch shape {
            case .stair: start = CGFloat(r) * rowHeight * 0.6
            // Arc, wave and block are centred, as a picture book's title page.
            case .arc, .wave, .block: start = (widest - rowWidths[r]) / 2
            }
            let middle = start + rowWidths[r] / 2
            let half = max(rowWidths[r] / 2, 1)
            var x = start
            var placed: [(Int, CGRect)] = []
            for (j, index) in row.enumerated() {
                let size = sizes[index]
                let u = min(1, max(-1, (x + size.width / 2 - middle) / half))
                let dy: CGFloat
                switch shape {
                case .arc: dy = -rowHeight * 0.5 * (1 - u * u)
                case .stair: dy = CGFloat(j) * rowHeight * 0.16
                case .wave: dy = CGFloat(sin(Double(order) * 1.4 + Double(r) * 0.9)) * rowHeight * 0.2
                case .block: dy = 0
                }
                placed.append((index, CGRect(x: x, y: dy - baseline(index), width: size.width, height: size.height)))
                x += size.width + gap
                order += 1
            }
            let rowTop = placed.map(\.1.minY).min() ?? 0
            let shift = top - rowTop
            for (index, rect) in placed {
                frames[index] = rect.offsetBy(dx: 0, dy: shift)
                placedAll.append(index)
            }
            top = (placed.map(\.1.maxY).max() ?? 0) + shift + rowGap
        }
        let box = placedAll.dropFirst().reduce(frames[placedAll[0]]) { $0.union(frames[$1]) }
        for index in placedAll { frames[index] = frames[index].offsetBy(dx: -box.minX, dy: -box.minY) }
        if rightToLeft {
            for index in placedAll {
                let f = frames[index]
                frames[index] = CGRect(x: box.width - f.maxX, y: f.minY, width: f.width, height: f.height)
            }
        }
        return Arrangement(size: box.size, frames: frames)
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
