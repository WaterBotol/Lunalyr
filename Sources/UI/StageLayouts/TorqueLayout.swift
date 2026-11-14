import SwiftUI

// Torque: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.
//
// A square column of heavy, extruded type turning about a vertical axis at the
// stage's centre. Each phrase (`StagePhrases`) is one face of the column: while
// it is sung its face looks straight out and its words are wrung into place on
// it; when the next phrase starts, the whole column torques a quarter turn to
// the left and the next face swings in from the right (mirrored for a
// right-to-left line). Within a face the focal word gets the wring (it arrives
// edge-on and unwinds, its thick edge growing in after it), content words rise
// a little, function words cut in beside the word they ride with, and a quick
// run rises as one group. A held word is wrung tighter for as long as it is
// sung. Nothing moves once it has landed.
//
// Every turn is drawn with 2D affine transforms (a horizontal squash, a slight
// shear for the keystone): `rotation3DEffect` draws at the stage's corner in a
// window's cached display and is dropped by `ImageRenderer`.

extension StageLyricView {
    /// Torque's entry point, called by the dispatcher (`StageLyricView.layout`).
    /// Under Reduce Motion `shown` decides the face (the shared dissolve then
    /// cross-fades from one step to the next); otherwise the time does.
    func torqueBody(runs: [StageRun], shown: [StageRun]) -> some View {
        torque(runs, shown: shown)
    }

    /// Torque leaves Reduce Motion to the shared dissolve between steps
    /// (`StageLyricView.reducedDissolve`): it never travels under it.
    static let torqueOwnsReduceMotion = false

    // MARK: Sizes

    /// A Torque word's size in units, by its role: size carries the hierarchy.
    static func torqueSize(_ role: TorqueRole, unit: CGFloat) -> CGFloat {
        switch role {
        case .focal: return unit * 2.4
        case .content: return unit * 1.7
        case .function: return unit * 1.1
        }
    }

    /// The depth of a word's thick edge (as a share of its size) once it has landed.
    static func torqueDepth(_ role: TorqueRole) -> CGFloat {
        switch role {
        case .focal: return 0.10
        case .content: return 0.075
        case .function: return 0.05
        }
    }

    /// The copies drawn for a word's thick edge.
    static func torqueSteps(_ role: TorqueRole) -> Int {
        switch role {
        case .focal: return 7
        case .content: return 5
        case .function: return 3
        }
    }

    // MARK: The view

    func torque(_ runs: [StageRun], shown: [StageRun]) -> some View {
        let plan = Self.torquePlan(runs)
        let rho = Self.torqueRotation(plan, at: time, shownCount: shown.count, reduceMotion: reduceMotion)
        let swing = Self.torqueSwing(plan, at: time, exitStart: exitStart, reduceMotion: reduceMotion)
        let leaving = Self.torqueLeaving(at: time, exitStart: exitStart)
        let stage = stageSize
        // Inside the 5 % title-safe margins.
        let available = CGSize(width: stage.width * 0.9, height: stage.height * 0.9)
        let direction: Double = plan.rightToLeft ? -1 : 1
        let visible = plan.faces.indices.filter { abs(Double($0) - rho) < 0.999 }
        return ZStack {
            ForEach(visible, id: \.self) { index in
                let angle = (Double(index) - rho) * 90 * direction + swing * direction
                torqueFace(plan.faces[index], plan: plan, runs: runs, shown: shown.count, stage: stage)
                    // Each face is set a little tipped, the other way from its neighbours.
                    .rotationEffect(.degrees(reduceMotion ? 0 : (plan.faces[index].flushTrailing ? 4 : -4)))
                    .compositingGroup()
                    .shadow(color: .black.opacity(0.28), radius: max(3, unit * 0.1), y: unit * 0.05)
                    .modifier(TorqueTurn(angle: angle, available: available))
                    .brightness(-0.45 * (1 - cos(min(abs(angle), 90) * .pi / 180)))
                    .opacity(pow(max(0, cos(min(abs(angle), 90) * .pi / 180)), 1.5))
            }
        }
        .opacity(1 - leaving)
        .frame(width: stage.width, height: stage.height)
    }

    /// One face: its words in rows, those not yet sung invisible but keeping
    /// their room, so the face never reflows as it fills.
    private func torqueFace(_ face: TorquePlan.Face, plan: TorquePlan, runs: [StageRun], shown: Int,
                            stage: CGSize) -> some View {
        let trailing = face.flushTrailing != plan.rightToLeft
        return TorqueFaceLayout(rows: face.rowSlots, trailing: trailing, rightToLeft: plan.rightToLeft,
                                wordGap: unit * 0.24, rowGap: -unit * 0.06) {
            // The plan is made from these runs, so every id is one of them.
            ForEach(face.runs, id: \.self) { id in
                torqueWord(runs[id], plan: plan, runs: runs, shown: shown, stage: stage)
            }
        }
    }

    @ViewBuilder
    private func torqueWord(_ run: StageRun, plan: TorquePlan, runs: [StageRun], shown: Int, stage: CGSize) -> some View {
        let role = plan.roles[run.id]
        let size = Self.torqueSize(role, unit: unit)
        let pose = Self.torquePose(run, role: role, clock: plan.clocks[run.id], held: plan.held[run.id],
                                   lineTimed: plan.lineTimed, at: time, arrived: run.id < shown,
                                   reduceMotion: reduceMotion)
        let entrance = chosenEntrance(run, progress: StageMotion.arrival(run.start, at: time), size: size)
        let native = entrance.native || plan.lineTimed
        let live = Self.torqueLive(run, runs: runs, at: time)
        let sample = sample(run, of: runs.count)
        let tile = tiled(run)
        let restColour = tile ? ink.tileText : sample.rest.opacity(0.86)
        let liveColour = tile ? ink.tileText : sample.sung
        let steps = Self.torqueSteps(role)
        let depth = size * Self.torqueDepth(role) * CGFloat(pose.depth)
        let side: CGFloat = plan.rightToLeft ? -1 : 1
        withReading(run, size: size, colour: sample.reading,
                    liveColour: (tile ? sample.reading : sample.sung).opacity(LyricHighlight.readingOpacity),
                    live: live, gap: size * 0.04, maxWidth: stage.width * 0.8) {
            ZStack {
                if steps > 0, depth > 0.2 {
                    // The thick edge: the word's own colour in shade, darker
                    // the further back it steps, so it reads as a solid edge.
                    ZStack {
                        ForEach((1...steps).reversed(), id: \.self) { step in
                            let share = CGFloat(step) / CGFloat(steps)
                            word(run, liveColour)
                                .colorMultiply(Color(white: 0.5 - 0.22 * Double(share)))
                                .offset(x: side * depth * share * 0.55, y: depth * share)
                        }
                    }
                }
                ZStack {
                    if live < 0.999 { word(run, restColour) }
                    if live > 0.001 { word(run, liveColour).opacity(live) }
                }
                .background { tileBackground(tile, size: size) }
            }
            .font(font(size, hero: true, id: run.id))
            .lineLimit(1).minimumScaleFactor(0.35)
            .frame(maxWidth: stage.width * 0.8)
            // Room for the thick edge inside the word's own box.
            .padding(.trailing, plan.rightToLeft ? 0 : size * Self.torqueDepth(role) * 0.9)
            .padding(.leading, plan.rightToLeft ? size * Self.torqueDepth(role) * 0.9 : 0)
            .padding(.bottom, size * Self.torqueDepth(role) * 1.4)
        }
        .modifier(TorqueTurn(angle: native ? pose.wring * Double(side) : 0, available: nil))
        .scaleEffect(native ? CGFloat(pose.scale) : 1)
        .offset(y: native ? size * CGFloat(pose.rise) : 0)
        .rotation3DEffect(.degrees(native ? 0 : entrance.fold), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
        .rotationEffect(.degrees(native ? 0 : entrance.spin))
        .scaleEffect(native ? 1 : entrance.scale)
        .offset(native ? .zero : entrance.offset)
        .opacity(pose.opacity)
    }

    // MARK: Motion (pure functions of time)

    /// A word's pose: how far it is still wrung (degrees), how far below its
    /// place it is (a share of its size), how deep its thick edge is (0...1
    /// of its resting depth, more for a held word) and how visible it is.
    struct TorquePose: Equatable {
        var wring: Double = 0
        var rise: Double = 0
        var depth: Double = 1
        var opacity: Double = 1
        /// The punch: a focal word lands a little large and settles.
        var scale: Double = 1
    }

    static func torquePose(_ run: StageRun, role: TorqueRole, clock: Double, held: Double, lineTimed: Bool,
                           at time: Double, arrived: Bool, reduceMotion: Bool) -> TorquePose {
        let onset = run.start - StageMotion.lead
        guard time >= onset else { return TorquePose(wring: 0, rise: 0, depth: 0, opacity: 0) }
        // Line-timed lines, and Reduce Motion: the face move is the only move.
        if reduceMotion || lineTimed { return TorquePose() }
        var pose = TorquePose()
        let unit = StageMotion.torqueUnit
        switch role {
        case .focal:
            // The biggest word takes the longest: 4 units.
            let p = (time - onset) / (4 * unit)
            pose.wring = StageMotion.torqueWringAngle * (1 - StageMotion.torqueOut(p))
            pose.scale = 1 + 0.22 * (1 - StageMotion.torqueOut(p))
            pose.opacity = min(1, (time - onset) / unit)
            // The thick edge follows the word in, and keeps deepening while it is held.
            let edge = StageMotion.torqueOut((time - onset - unit) / (2 * unit))
            let tighter = held > 0 ? min(1, max(0, (time - run.start) / held)) : 0
            pose.depth = min(1, max(0, edge)) * (1 + 0.6 * tighter)
        case .content:
            // A quick run rises as one group, on its first member's clock.
            let start = min(clock, run.start) - StageMotion.lead
            let p = (time - start) / (2 * unit)
            pose.rise = 0.3 * (1 - StageMotion.torqueOut(p))
            // The same twist, smaller: content words are wrung half as far.
            pose.wring = StageMotion.torqueWringAngle * 0.45 * (1 - StageMotion.torqueOut(p))
            pose.opacity = min(1, (time - onset) / (0.5 * unit))
            pose.depth = min(1, max(0, StageMotion.torqueOut((time - onset - 0.5 * unit) / (2 * unit))))
        case .function:
            pose.opacity = min(1, (time - onset) / unit)
            pose.depth = min(1, max(0, StageMotion.torqueOut((time - onset) / (2 * unit))))
        }
        return pose
    }

    /// 1 while `run` is the word being sung, easing to 0 over a short
    /// hand-over once the next word starts. The last word stays lit.
    static func torqueLive(_ run: StageRun, runs: [StageRun], at time: Double) -> Double {
        guard time >= run.start - StageMotion.lead else { return 0 }
        guard runs.indices.contains(run.id + 1) else { return 1 }
        let next = runs[run.id + 1].start - StageMotion.lead
        return 1 - Easing.smoothstep((time - next) / 0.2)
    }

    /// How far the column has turned, in faces: 0 facing the first phrase, 1
    /// the second, and so on (a little past during the overshoot). Under
    /// Reduce Motion it steps to the face of the newest shown word.
    static func torqueRotation(_ plan: TorquePlan, at time: Double, shownCount: Int, reduceMotion: Bool) -> Double {
        if reduceMotion {
            guard shownCount > 0, plan.faceOfRun.indices.contains(shownCount - 1) else { return 0 }
            return Double(plan.faceOfRun[shownCount - 1])
        }
        var rho = 0.0
        for face in plan.faces.dropFirst() {
            let p = (time - face.turnStart) / (3 * StageMotion.torqueUnit)
            if p <= 0 { break }
            rho += StageMotion.torqueTurn(p)
        }
        return rho
    }

    /// The whole column's extra turn, in degrees: a new line enters already
    /// turning (+45 degrees to 0, picking up the turn the last line left
    /// with), and leaves by turning on to the left (0 to -60).
    static func torqueSwing(_ plan: TorquePlan, at time: Double, exitStart: Double, reduceMotion: Bool) -> Double {
        guard !reduceMotion, let first = plan.faces.first else { return 0 }
        let enter = (time - (first.start - StageMotion.lead)) / (2 * StageMotion.torqueUnit)
        let entering = plan.lineTimed || enter < 1 ? 45 * (1 - StageMotion.torqueOut(enter)) : 0
        return entering - 60 * torqueLeaving(at: time, exitStart: exitStart)
    }

    /// The line leaving, 0...1, on the in-ease.
    static func torqueLeaving(at time: Double, exitStart: Double) -> Double {
        guard exitStart.isFinite else { return 0 }
        return StageMotion.torqueIn((time - exitStart) / StageMotion.torqueExit)
    }

    // MARK: The plan (per line, cached)

    /// A word's part in Torque: the phrase's focal word, a content word, or a
    /// function word riding along.
    enum TorqueRole: Equatable {
        case focal, content, function
    }

    struct TorquePlan: Equatable {
        struct Face: Equatable {
            /// Its runs (`StageRun.id`s) in reading order.
            var runs: [Int]
            /// The rows, as run ids.
            var rows: [[Int]]
            var focal: Int
            /// Set flush right (flush left otherwise); neighbouring faces alternate.
            var flushTrailing: Bool
            /// Its first onset, and when the column starts turning to it.
            var start: Double
            var turnStart: Double

            /// The rows as positions in `runs`, for the layout.
            var rowSlots: [[Int]] {
                rows.map { row in row.compactMap { runs.firstIndex(of: $0) } }
            }
        }

        var faces: [Face]
        /// The face each run is on, by run id.
        var faceOfRun: [Int]
        var roles: [TorqueRole]
        /// When each run starts its entrance: its own onset, or the first
        /// onset of the quick run it belongs to.
        var clocks: [Double]
        /// How long each held word is sung (0 for words that are not held).
        var held: [Double]
        var lineTimed: Bool
        var rightToLeft: Bool

        static let empty = TorquePlan(faces: [], faceOfRun: [], roles: [], clocks: [], held: [], lineTimed: false,
                                      rightToLeft: false)
    }

    private struct TorqueKey: Hashable {
        var texts: [String]
        var starts: [Double]
        var ends: [Double]
    }

    private final class TorqueCache: @unchecked Sendable {
        let lock = NSLock()
        var plans: [TorqueKey: TorquePlan] = [:]
    }

    private static let torqueCache = TorqueCache()

    /// The line's plan: faces, rows, roles and clocks. Cached per line, so
    /// asking on every frame costs a lookup.
    static func torquePlan(_ runs: [StageRun]) -> TorquePlan {
        let key = TorqueKey(texts: runs.map(\.text), starts: runs.map(\.start), ends: runs.map(\.end))
        torqueCache.lock.lock()
        if let hit = torqueCache.plans[key] { torqueCache.lock.unlock(); return hit }
        torqueCache.lock.unlock()
        let plan = makeTorquePlan(runs, phrasing: StagePhrases.phrasing(runs))
        torqueCache.lock.lock()
        if torqueCache.plans.count >= 64 { torqueCache.plans.removeAll(keepingCapacity: true) }
        torqueCache.plans[key] = plan
        torqueCache.lock.unlock()
        return plan
    }

    static func makeTorquePlan(_ runs: [StageRun], phrasing: StagePhrasing) -> TorquePlan {
        guard !runs.isEmpty, phrasing.words.count == runs.count else { return .empty }
        let infos = phrasing.words
        let roles: [TorqueRole] = infos.map { info in
            if info.isFocal { return .focal }
            return info.isFunction ? .function : .content
        }
        // A quick run's members share its first onset.
        var clocks = runs.map(\.start)
        for (index, info) in infos.enumerated() {
            guard let group = info.runGroup else { continue }
            let members = infos.indices.filter { infos[$0].runGroup == group && infos[$0].phrase == info.phrase }
            clocks[index] = members.map { runs[$0].start }.min() ?? runs[index].start
        }
        let held = infos.map { $0.isHeld ? $0.duration : 0 }
        let isFunction = { (id: Int) -> Bool in infos.indices.contains(id) && infos[id].isFunction }

        var faces: [TorquePlan.Face] = []
        var faceOfRun = [Int](repeating: 0, count: runs.count)
        if phrasing.lineTimed {
            // No word timing: one face, a row per phrase, arriving with the face move.
            let rows = phrasing.phrases.map { Array($0.runs) }
            let start = runs.map(\.start).min() ?? 0
            let focal = phrasing.phrases.max { lhs, rhs in runs[lhs.focal].text.count < runs[rhs.focal].text.count }?.focal ?? 0
            faces = [TorquePlan.Face(runs: Array(runs.indices), rows: rows, focal: focal, flushTrailing: false,
                                     start: start, turnStart: -.infinity)]
        } else {
            for phrase in phrasing.phrases {
                let ids = Array(phrase.runs)
                let rows = torqueRows(ids, focal: phrase.focal, isFunction: isFunction)
                let start = ids.map { runs[$0].start }.min() ?? phrase.start
                var turnStart = -Double.infinity
                if let previous = faces.last {
                    // Facing the new phrase as its first word is sung, but never
                    // before the last word of the old face has landed.
                    let lastLanded = previous.runs.map { runs[$0].start }.max() ?? start
                    let early = start - StageMotion.lead - StageMotion.torqueTurnLead
                    turnStart = min(start - StageMotion.lead, max(early, lastLanded + StageMotion.torqueUnit))
                }
                faces.append(TorquePlan.Face(runs: ids, rows: rows, focal: phrase.focal,
                                             flushTrailing: !faces.count.isMultiple(of: 2), start: start,
                                             turnStart: turnStart))
                for id in ids where faceOfRun.indices.contains(id) { faceOfRun[id] = faces.count - 1 }
            }
        }
        return TorquePlan(faces: faces, faceOfRun: faceOfRun, roles: roles, clocks: clocks, held: held,
                          lineTimed: phrasing.lineTimed, rightToLeft: phrasing.rightToLeft)
    }

    /// A face's rows: each content word takes the function words in front of
    /// it (a function word left at the end joins the word before); the focal
    /// word's group gets a row of its own, the groups before it share a row
    /// and the groups after it share a row. So the grammar decides where the
    /// big word sits: top, middle or foot of the face.
    static func torqueRows(_ ids: [Int], focal: Int, isFunction: (Int) -> Bool) -> [[Int]] {
        guard !ids.isEmpty else { return [] }
        var groups: [[Int]] = []
        var pending: [Int] = []
        for id in ids {
            pending.append(id)
            if !isFunction(id) {
                groups.append(pending)
                pending = []
            }
        }
        if !pending.isEmpty {
            if groups.isEmpty { groups.append(pending) } else { groups[groups.count - 1] += pending }
        }
        guard let focalGroup = groups.firstIndex(where: { $0.contains(focal) }) else { return [ids] }
        var rows: [[Int]] = []
        let before = groups[..<focalGroup].flatMap { $0 }
        let after = groups[(focalGroup + 1)...].flatMap { $0 }
        if !before.isEmpty { rows.append(before) }
        rows.append(groups[focalGroup])
        if !after.isEmpty { rows.append(after) }
        return rows
    }
}

// MARK: - Torque's parts

extension StageMotion {
    /// Torque's base unit (s): arrivals take 2, the column's turn 3, the wring 4.
    static let torqueUnit = 0.12
    /// How far a focal word is turned as it starts to arrive, in degrees.
    static let torqueWringAngle = 84.0
    /// The column starts turning this long before a phrase's first word.
    static let torqueTurnLead = 0.08
    /// The line's exit (about three quarters of the wring and turn).
    static let torqueExit = 0.27

    /// Torque's one out-ease: easeOutBack with an overshoot of about 8 %,
    /// for every arrival and the column's turn. Clamped to 0...1 input.
    static func torqueOut(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        let c1 = 1.2, c3 = c1 + 1
        let u = t - 1
        return 1 + c3 * u * u * u + c1 * u * u
    }

    /// The column's turn, `x` (0...1 of three units) since it started: the
    /// out-ease, its overshoot and its landing as before, but run up to
    /// speed over the first `torqueLaunch` of the turn instead of starting
    /// at full speed. The out-ease alone set off at about 15 degrees in the
    /// first frame from a standstill, so the face being sung snapped away
    /// as the next phrase began. The run-up adds half of it to the turn
    /// (about 0.03 s).
    static func torqueTurn(_ x: Double) -> Double {
        let ramp = torqueLaunch
        guard x > 0 else { return 0 }
        let eased = x < ramp ? x * x / (2 * ramp) : x - ramp / 2
        return torqueOut(eased)
    }

    /// The share of the turn's out-ease over which it runs up to speed.
    static let torqueLaunch = 0.15

    /// Torque's one in-ease: easeInCubic, for the line leaving.
    static func torqueIn(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        return t * t * t
    }
}

/// A turn about a vertical axis, drawn in 2D: the view is squashed by the
/// cosine of `angle`, sheared a little for the keystone and, given the space
/// it is fitted into (`available`), scaled to fit it and carried round the
/// column (a face's centre swings by half its width times the sine).
struct TorqueTurn: GeometryEffect {
    var angle: Double
    var available: CGSize?

    var animatableData: Double {
        get { angle }
        set { angle = newValue }
    }

    func effectValue(size: CGSize) -> ProjectionTransform {
        ProjectionTransform(Self.transform(angle: angle, size: size, available: available))
    }

    static func transform(angle: Double, size: CGSize, available: CGSize?) -> CGAffineTransform {
        let fit = available.map { LyricStageFit.scale(content: size, available: $0) } ?? 1
        let radians = min(max(angle, -90), 90) * .pi / 180
        let c = CGFloat(max(cos(radians), 0.001))
        let s = CGFloat(sin(radians))
        let swing = available == nil ? 0 : size.width * fit / 2 * s
        let centre = CGAffineTransform(translationX: -size.width / 2, y: -size.height / 2)
        let scaled = centre.concatenating(CGAffineTransform(scaleX: fit, y: fit))
        let turned = scaled.concatenating(CGAffineTransform(a: c, b: -s * 0.1, c: 0, d: 1 - 0.08 * abs(s),
                                                            tx: swing, ty: 0))
        return turned.concatenating(CGAffineTransform(translationX: size.width / 2, y: size.height / 2))
    }
}

/// Sets a Torque face: rows of words sharing a baseline, stacked, flush to
/// one side. Reports the face's own size; `TorqueTurn` fits it to the stage.
struct TorqueFaceLayout: Layout {
    /// Rows, as indices into the subviews.
    var rows: [[Int]]
    var trailing: Bool
    var rightToLeft: Bool
    var wordGap: CGFloat
    var rowGap: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let measured = Self.measure(subviews)
        return Self.arrange(measured.sizes, baselines: measured.baselines, rows: rows, trailing: trailing,
                            rightToLeft: rightToLeft, wordGap: wordGap, rowGap: rowGap).size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let measured = Self.measure(subviews)
        let laid = Self.arrange(measured.sizes, baselines: measured.baselines, rows: rows, trailing: trailing,
                                rightToLeft: rightToLeft, wordGap: wordGap, rowGap: rowGap)
        let origin = CGPoint(x: bounds.midX - laid.size.width / 2, y: bounds.midY - laid.size.height / 2)
        for (index, subview) in subviews.enumerated() where laid.frames.indices.contains(index) {
            let frame = laid.frames[index]
            subview.place(at: CGPoint(x: origin.x + frame.midX, y: origin.y + frame.midY), anchor: .center,
                          proposal: ProposedViewSize(measured.sizes[index]))
        }
    }

    private static func measure(_ subviews: Subviews) -> (sizes: [CGSize], baselines: [CGFloat]) {
        let sizes = subviews.map { $0.sizeThatFits(.unspecified) }
        let baselines = subviews.map { $0.dimensions(in: .unspecified)[VerticalAlignment.firstTextBaseline] }
        return (sizes, baselines)
    }

    /// Each word's box from the face's top-left corner, and the face's size.
    /// Words in a row share a baseline; a word in no row is placed nowhere
    /// (a zero box at the origin).
    static func arrange(_ sizes: [CGSize], baselines: [CGFloat], rows: [[Int]], trailing: Bool, rightToLeft: Bool,
                        wordGap: CGFloat, rowGap: CGFloat) -> (size: CGSize, frames: [CGRect]) {
        var frames = [CGRect](repeating: .zero, count: sizes.count)
        let valid = rows.map { row in row.filter { sizes.indices.contains($0) } }.filter { !$0.isEmpty }
        guard !valid.isEmpty else { return (.zero, frames) }
        func baseline(_ index: Int) -> CGFloat {
            let measured = baselines.indices.contains(index) ? baselines[index] : sizes[index].height * 0.8
            return min(sizes[index].height, max(0, measured))
        }
        let widths: [CGFloat] = valid.map { row in
            row.map { sizes[$0].width }.reduce(0, +) + wordGap * CGFloat(row.count - 1)
        }
        let widest = widths.max() ?? 0
        var top: CGFloat = 0
        for (r, row) in valid.enumerated() {
            let ascent = row.map { baseline($0) }.max() ?? 0
            let descent = row.map { sizes[$0].height - baseline($0) }.max() ?? 0
            var x = trailing ? widest - widths[r] : 0
            let ordered = rightToLeft ? Array(row.reversed()) : row
            for index in ordered {
                let size = sizes[index]
                frames[index] = CGRect(x: x, y: top + ascent - baseline(index), width: size.width, height: size.height)
                x += size.width + wordGap
            }
            top += ascent + descent + (r < valid.count - 1 ? rowGap : 0)
        }
        return (CGSize(width: widest, height: max(0, top)), frames)
    }
}
