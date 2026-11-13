import SwiftUI

// Fable: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.
//
// A storybook page per line. The page is set in a shape chosen for the line
// (an arc, a stair, a wave or a plain centred block) and each phrase (`StagePhrases`)
// is one line of type on it, so the page breaks where the singer breathes.
// Words land playfully, by role: each phrase's focal word makes the big
// landing (it pops up from its foot with a swing, or drops in from above,
// alternating phrase by phrase), the line's hero word lands biggest of all,
// content words hop up into place, and little words are written in with a
// flick of the pen; a held word is written slowly over the time it is sung,
// and a quick run hops in as one group. A long line turns the page by
// phrase: a page holds at most two phrases, and the old page folds away about
// its spine before the next is begun. Nothing moves once landed, and nothing
// decorates a word (no stars, brackets or underlines).

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
        let seed = seed
        return ZStack {
            ForEach(pages, id: \.self) { index in
                let turn = Self.fablePageTurn(index, plan: plan, at: time, exitStart: exitStart)
                fablePage(plan.pages[index], plan: plan, style: style, runs: runs, seed: seed)
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

    private func fablePage(_ page: FablePlan.Page, plan: FablePlan, style: FableStyle, runs: [StageRun],
                           seed: Int) -> some View {
        FableLayout(shape: style.shape, rows: page.rowSlots, rightToLeft: plan.rightToLeft,
                    gap: unit * 0.1, rowGap: unit * 0.1) {
            // The plan is made from these runs, so every id is one of them.
            ForEach(page.runs, id: \.self) { id in
                fableWord(runs[id], plan: plan, runs: runs, seed: seed)
            }
        }
    }

    @ViewBuilder
    private func fableWord(_ run: StageRun, plan: FablePlan, runs: [StageRun], seed: Int) -> some View {
        let role = plan.roles[run.id]
        let size = Self.fableSize(role, wordCount: runs.count, unit: unit)
        let written = Self.fableWritten(plan.strokes[run.id], at: time)
        let entrance = chosenEntrance(run, progress: StageMotion.arrival(run.start, at: time), size: size)
        let native = entrance.native && !reduceMotion
        let shown = time >= run.start - StageMotion.lead
        // Reduce Motion, or another entrance chosen: a fade on the word's beat instead of the stroke.
        let fade = Easing.smoothstep((time - (run.start - StageMotion.lead)) / WallpaperMotion.reducedDissolve)
        let phrase = plan.phraseOfRun.indices.contains(run.id) ? plan.phraseOfRun[run.id] : 0
        let landing = FableLanding.forPhrase(phrase, seed: seed)
        let clock = plan.clocks.indices.contains(run.id) ? plan.clocks[run.id] : run.start
        let pose = native ? Self.fablePose(run, role: role, landing: landing, lineTimed: plan.lineTimed, clock: clock,
                                           at: time)
            : FablePose()
        let side: Double = plan.rightToLeft ? -1 : 1
        let live = Self.torqueLive(run, runs: runs, at: time)
        fableFace(run, size: size, live: live, sample: sample(run, of: runs.count))
            .modifier(FableStroke(progress: native && plan.writes[run.id] ? written : 1, rightToLeft: plan.rightToLeft))
            .scaleEffect(CGFloat(pose.scale), anchor: .bottom)
            .rotationEffect(.degrees(pose.spin * side), anchor: .bottom)
            .offset(y: size * CGFloat(pose.lift))
            .rotation3DEffect(.degrees(entrance.native ? 0 : entrance.fold), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
            .rotationEffect(.degrees(entrance.native ? 0 : entrance.spin), anchor: .bottom)
            .scaleEffect(entrance.native ? 1 : entrance.scale)
            .offset(entrance.native ? .zero : entrance.offset)
            .opacity(shown ? (native ? 1 : fade) : 0)
    }

    /// A word with its reading under it.
    @ViewBuilder
    private func fableFace(_ run: StageRun, size: CGFloat, live: Double, sample: LyricInkSample) -> some View {
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
        VStack(spacing: size * 0.06) {
            word
            if let reading {
                Text(reading)
                    .font(settings.lyricFont(size: max(12, size * 0.36), hero: false, mood: mood))
                    .foregroundStyle(sample.rest.opacity(0.9))
                    .lineLimit(1).fixedSize()
            }
        }
        // Room for the landings, inside the word's own measured box.
        .padding(.horizontal, size * (tight ? 0.12 : 0.18))
        .padding(.vertical, size * 0.14)
    }

    // MARK: Sizes and motion (pure functions of time)

    /// A word's size by its role: the line's hero word largest, little
    /// words smallest. A line of one or two words is all set large.
    static func fableSize(_ role: FableRole, wordCount: Int, unit: CGFloat) -> CGFloat {
        if wordCount <= 2 { return unit * 2.0 }
        switch role {
        case .hero: return unit * 2.0
        case .focal: return unit * 1.6
        case .content: return unit * 1.3
        case .function: return unit * 0.95
        }
    }

    /// How much of a word its pen stroke has written, 0...1: the pen's own
    /// speed (smoothstep), from the stroke's start over its length.
    static func fableWritten(_ stroke: FablePlan.Stroke, at time: Double) -> Double {
        guard time >= stroke.start else { return 0 }
        return Easing.smoothstep((time - stroke.start) / max(0.01, stroke.length))
    }

    /// A word's landing at `time`: how far above (negative) or below its
    /// place it is (a share of its size), its scale and its swing (degrees,
    /// about its foot). The focal words make the big landing (the line's hero
    /// biggest, `landing` deciding which), content words hop up, function
    /// words do not move: the pen writes them in. All on the out-ease.
    static func fablePose(_ run: StageRun, role: FableRole, landing: FableLanding, lineTimed: Bool,
                          clock: Double? = nil, at time: Double) -> FablePose {
        let onset = run.start - StageMotion.lead
        guard time >= onset, !lineTimed else { return FablePose() }
        let unit = StageMotion.fableUnit
        switch role {
        case .hero, .focal:
            let big = role == .hero
            let p = (time - onset) / ((big ? 4.5 : 3.5) * unit)
            let left = 1 - StageMotion.fableOut(p)
            switch landing {
            case .pop:
                return FablePose(lift: 0, scale: 1 - (big ? 0.6 : 0.45) * left, spin: -(big ? 18 : 12) * left)
            case .drop:
                return FablePose(lift: -(big ? 0.6 : 0.45) * left, scale: 1 + (big ? 0.12 : 0.08) * left, spin: 6 * left)
            }
        case .content:
            let p = (time - (min(clock ?? run.start, run.start) - StageMotion.lead)) / (2.5 * unit)
            return FablePose(lift: 0.5 * (1 - StageMotion.fableOut(p)), scale: 1, spin: 0)
        case .function:
            return FablePose()
        }
    }

    /// How far page `index` has turned away, 0...1: when the next page is
    /// begun, or when the line leaves. A cross-fade under Reduce Motion.
    static func fablePageTurn(_ index: Int, plan: FablePlan, at time: Double, exitStart: Double) -> Double {
        var start = Double.infinity
        var length = StageMotion.fableTurn
        if plan.pages.indices.contains(index + 1) {
            start = plan.pages[index + 1].turnStart
            length = plan.pages[index + 1].turnLength
        }
        if exitStart.isFinite, exitStart < start {
            start = exitStart
            length = StageMotion.fableTurn
        }
        guard start.isFinite else { return 0 }
        return StageMotion.fableIn((time - start) / length)
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
        /// The line's one important word: the biggest word and landing.
        case hero
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
            /// How long the page before takes to turn away: shorter on a fast
            /// line, so it is gone before this page's first word lands.
            var turnLength: Double = StageMotion.fableTurn

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
        /// The line's hero word.
        var hero: Int?
        /// The phrase each run belongs to, by run id.
        var phraseOfRun: [Int]
        /// Whether each run is written in by the pen (function words, held
        /// words written as they are sung, and every word of a line-timed
        /// line); the others land with their own move and no stroke.
        var writes: [Bool]
        /// When each run's landing starts: its own onset, or the first onset
        /// of the quick run it belongs to (the run hops in as one group).
        var clocks: [Double] = []
        var lineTimed: Bool
        var rightToLeft: Bool

        static let empty = FablePlan(pages: [], roles: [], strokes: [], hero: nil, phraseOfRun: [], writes: [], lineTimed: false,
                                    rightToLeft: false)
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

    /// The line's plan: pages, roles, strokes and hero. Cached per line.
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
        let hero = fableHeroRun(runs, phrasing: phrasing)
        let roles: [FableRole] = infos.map { info in
            if info.run == hero { return .hero }
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
                // Never before the old page's last word has landed, and gone
                // before this page's first word lands.
                let landed = lastLanded + StageMotion.fableUnit
                turnStart = min(start - StageMotion.lead - StageMotion.fableUnit, max(early, landed))
                let room = start - StageMotion.lead - turnStart
                let length = min(StageMotion.fableTurn, max(StageMotion.fableUnit, room))
                pages.append(FablePlan.Page(phrases: group, runs: ids, rows: rows, start: start, turnStart: turnStart,
                                            turnLength: length))
                continue
            }
            pages.append(FablePlan.Page(phrases: group, runs: ids, rows: rows, start: start, turnStart: turnStart))
        }
        var clocks = runs.map(\.start)
        for (index, info) in infos.enumerated() {
            guard let group = info.runGroup else { continue }
            let members = infos.indices.filter { infos[$0].runGroup == group && infos[$0].phrase == info.phrase }
            clocks[index] = members.map { runs[$0].start }.min() ?? runs[index].start
        }
        return FablePlan(pages: pages, roles: roles, strokes: strokes, hero: hero, phraseOfRun: infos.map(\.phrase),
                         writes: infos.map { phrasing.lineTimed || $0.isFunction || $0.isHeld },
                         clocks: clocks, lineTimed: phrasing.lineTimed, rightToLeft: phrasing.rightToLeft)
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

    /// The line's hero word: the phrase focal held longest (ties: the
    /// later one); on a line-timed line, the longest focal word.
    static func fableHeroRun(_ runs: [StageRun], phrasing: StagePhrasing) -> Int? {
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
            case .focal, .hero: length = min(4 * unit, max(2.5 * unit, Double(run.text.count) * 0.4 * unit))
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

/// One Fable line's look: the shape its phrases are set in. Chosen by the
/// line, so the same line looks the same on every render; neighbouring lines
/// never share a shape, and the order shifts each lap so the run of shapes
/// does not repeat every four lines.
struct FableStyle: Equatable {
    enum Shape: Int, CaseIterable {
        case arc, stair, wave, block
    }

    var shape: Shape

    static func forLine(_ seed: Int) -> FableStyle {
        let n = Shape.allCases.count
        // Floor division, so negative seeds keep the pattern too.
        let lap = seed >= 0 ? seed / n : -((-seed + n - 1) / n)
        let index = (((seed + lap) % n) + n) % n
        return FableStyle(shape: Shape(rawValue: index) ?? .arc)
    }
}

/// How a phrase's focal word lands: popping up from its foot with a swing,
/// or dropping in from above. Phrases alternate, and the line's seed decides
/// which comes first, so neighbouring phrases (and lines) differ.
enum FableLanding: Equatable {
    case pop, drop

    static func forPhrase(_ phrase: Int, seed: Int) -> FableLanding {
        ((phrase + seed) % 2 + 2) % 2 == 0 ? .pop : .drop
    }
}

/// A Fable word's landing pose (`StageLyricView.fablePose`).
struct FablePose: Equatable {
    /// Below (positive) or above (negative) its place, as a share of its size.
    var lift: Double = 0
    var scale: Double = 1
    /// Degrees about its foot.
    var spin: Double = 0
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
