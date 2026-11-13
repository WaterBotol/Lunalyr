import SwiftUI

// Portal: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.

extension StageLyricView {
    /// Portal's entry point, called by the dispatcher (`StageLyricView.layout`):
    /// `runs` is the whole line, `shown` the runs that have arrived.
    func portalBody(runs: [StageRun], shown: [StageRun]) -> some View {
        portal(shown, total: runs.count)
    }

    /// Portal leaves Reduce Motion to the shared dissolve between steps
    /// when false (`StageLyricView.reducedDissolve`).
    static let portalOwnsReduceMotion = false

    // MARK: Portal
    //
    // The word being sung sits in a solid pill with a small badge, tilted as it
    // arrives and settling flat. The words before it climb above it as a
    // trail of smaller, paler type, spaced by their own heights so none
    // overlaps, and hold still there to be read. Nothing repeats: every line leans a different way, and every
    // word comes from its own direction with its own tilt, shape and badge.

    private static let badges = ["arrow.up.right", "arrow.right", "sparkle", "play.fill", "plus", "bolt.fill"]

    /// A Portal pill's shown size against its resting size: older pills are smaller.
    static func portalScale(rank: Double) -> CGFloat { CGFloat(max(0.4, 1 - 0.4 * min(rank, 1.5))) }

    /// `centre` moved, if it must be, so a pill measured at `size` and shown
    /// at `scale` stays inside `bounds`, with a little room for its landing
    /// squash and tilt. A pill too big for the stage is centred on it.
    static func keepOnStage(_ centre: CGPoint, size: CGSize, scale: CGFloat, in bounds: CGRect) -> CGPoint {
        let halfWidth = size.width * scale * 0.54, halfHeight = size.height * scale * 0.54
        func clamp(_ value: CGFloat, _ low: CGFloat, _ high: CGFloat) -> CGFloat {
            low > high ? (low + high) / 2 : min(max(value, low), high)
        }
        return CGPoint(x: clamp(centre.x, bounds.minX + halfWidth, bounds.maxX - halfWidth),
                       y: clamp(centre.y, bounds.minY + halfHeight, bounds.maxY - halfHeight))
    }

    func portal(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let arrivals = runs.map { arrive($0, speed: speed) }
        // Smootherstep starts and ends at rest, so the trail's sum of them
        // never jolts; under Reduce Motion each step is a dissolve instead.
        let settled = runs.map { reduceMotion ? 1 : Self.glide((time - ($0.start - 0.05)) / (0.6 / speed)) }
        let ranks = runs.indices.map { i in settled[(i + 1)...].reduce(0, +) }
        // The trail climbs out of the way a little quicker than the new pill
        // settles, so the pill never lands on the one before it.
        let lifts = runs.map { reduceMotion ? 1 : Self.glide((time - ($0.start - 0.08)) / (0.36 / speed)) }
        // Which way this line's trail leans, and where the pills sit. The
        // trail always climbs above the live pill, so it reads top to bottom.
        let flipX: CGFloat = chance(nil, 1) < 0.5 ? 1 : -1
        let reach = (0.8 + 0.5 * chance(nil, 3)) / speed.squareRoot()
        let shiftX = (chance(nil, 4) - 0.5) * 0.24 * stageSize.width
        let lift = stageSize.height * (0.1 + 0.08 * chance(nil, 5))
        let badge = Self.badges[Int(chance(nil, 6) * Double(Self.badges.count)) % Self.badges.count]
        // The trail zig-zags a little either side; the pills' heights, not
        // the stage's, set how far apart they are, so no two overlap.
        let zigzag: [CGFloat] = [0, -0.07, 0.05, -0.02].map { $0 * stageSize.width * flipX * CGFloat(reach) }
        func alongX(_ rank: Double) -> CGFloat {
            let capped = min(max(rank, 0), Double(zigzag.count - 1))
            let low = Int(capped.rounded(.down)), high = min(low + 1, zigzag.count - 1)
            return zigzag[low] + (zigzag[high] - zigzag[low]) * CGFloat(Self.glide(capped - Double(low)))
        }
        // Pills further back than this fade out: the live one and two before it.
        let fadeFrom = speed > 1.6 ? 1.0 : 1.6
        let visible = ranks.map { 1 - Easing.smoothstep(($0 - fadeFrom) / 0.9) }
        let leaving = leave
        // Quick songs get less float and a shorter run-in, so the eye can follow.
        let drift: CGFloat = reduceMotion ? 0 : CGFloat(1 / speed)
        // Older pills are shown smaller (the same scale as below).
        let scales = ranks.map { Self.portalScale(rank: $0) }
        let gap = unit * 0.16
        return StageLayout(place: { measured, bounds in
            // How far above the live pill's place each pill has climbed: every
            // later pill, as it settles, lifts it by half of each of the two
            // neighbours' shown heights and a gap.
            // The live pill's hand-drawn outlines reach a little beyond it.
            let shown = measured.indices.map { i in
                measured[i].height * scales[i] * (1.04 + 0.1 * CGFloat(StageMotion.liveness(rank: ranks[i])))
            }
            var climb = [CGFloat](repeating: 0, count: measured.count)
            var above: CGFloat = 0
            for index in measured.indices.reversed() {
                climb[index] = above
                if index > 0 { above += CGFloat(lifts[index]) * ((shown[index] + shown[index - 1]) / 2 + gap) }
            }
            // The trail is lowered, if it must be, so its top pill stays on the stage.
            let base = bounds.midY + lift
            // (A fading pill counts in full until it is mostly gone, so it is
            // never squeezed against the top of the stage onto the one below.)
            let room = measured.indices.map { i in
                (climb[i] + shown[i] / 2 - (base - bounds.minY - gap)) * CGFloat(min(1, visible[i] * 3))
            }
            let lower = max(0, room.max() ?? 0)
            return measured.indices.map { index -> CGPoint in
                let p = CGPoint(x: alongX(ranks[index]), y: lower - climb[index])
                let bobRate: Double = 1.2 + 0.6 * chance(index, 7)
                let bobPhase: Double = time * bobRate + Double(index) * 1.3
                // Only the live pill floats; the trail holds still to be read.
                let float = CGFloat(StageMotion.liveness(rank: ranks[index])) * drift
                let bob: CGFloat = CGFloat(sin(bobPhase)) * unit * 0.07 * float
                let swayRate: Double = 0.9 + 0.5 * chance(index, 8)
                let swayPhase: Double = time * swayRate + Double(index) * 0.9
                let sway: CGFloat = CGFloat(cos(swayPhase)) * unit * 0.05 * float
                // Each pill comes in from its own direction, between the right
                // and below, as the line reads; never down through the trail.
                let heading = chance(index, 9) * 0.5 * .pi
                let rise = CGFloat(1 - settled[index])
                let comeX: CGFloat = rise * CGFloat(cos(heading)) * bounds.width * 0.07 * drift
                let comeY: CGFloat = rise * CGFloat(sin(heading)) * bounds.height * 0.1 * drift
                let x: CGFloat = bounds.midX + shiftX + p.x + sway + comeX
                let y: CGFloat = bounds.midY + lift + p.y + bob + comeY
                // A pill with its reading is taller; none is pushed off the stage.
                return Self.keepOnStage(CGPoint(x: x, y: y), size: measured[index], scale: scales[index], in: bounds)
            }
        }) {
            ForEach(runs) { run in
                let a = arrivals[run.id]
                let eased = settled[run.id]
                let rank = ranks[run.id]
                let sample = sample(run, of: total)
                // 1 while this is the word being sung, easing to 0 as the next
                // takes over: the pill restyles over that hand-over, not at once.
                let live = StageMotion.liveness(rank: rank)
                let tile = tiled(run)
                // Set at its resting size; older pills are scaled down, not re-set.
                let size = unit * (1.2 + 0.3 * chance(run.id, 10))
                let scale = Self.portalScale(rank: rank)
                // Shapes run from nearly square to a full capsule; some pills are only outlined.
                let corner = size * (0.24 + 0.26 * chance(run.id, 11))
                let outlined = chance(run.id, 12) < 0.25 && !tile
                let badgeSide = chance(run.id, 13)
                let tiltAmount = 4 + 10 * chance(run.id, 14)
                let tiltSign: Double = chance(run.id, 15) < 0.5 ? 1 : -1
                let rock = sin(time * 1.1 + Double(run.id)) * 1.6 * live
                let tilt = reduceMotion ? 0 : ((1 - eased) * tiltAmount * tiltSign + rock) / speed
                let badgeView = Image(systemName: badge)
                    .font(.system(size: size * 0.34, weight: .bold))
                    .rotationEffect(.degrees(reduceMotion ? 0 : (1 - eased) * -90 * tiltSign))
                    .foregroundStyle(outlined ? ink.tileColour : ink.tileText)
                    .frame(width: size * 0.62, height: size * 0.62)
                    .background(Circle().fill(outlined ? ink.tileColour.opacity(0.2) : ink.tileColour.opacity(0.35 + 0.65 * live)))
                    .opacity(live)
                    .frame(width: size * 0.62 * live)
                let restColour = tile ? ink.tileText : sample.rest.opacity(0.85)
                let liveColour = outlined ? ink.tileColour : ink.tileText
                HStack(spacing: size * 0.3) {
                    if badgeSide < 0.6 { badgeView }
                    // The reading sits in the pill under its word and takes the
                    // live pill's text colour with it.
                    withReading(run, size: size,
                                colour: tile ? ink.tileText.opacity(LyricHighlight.readingOpacity) : sample.reading,
                                liveColour: liveColour.opacity(LyricHighlight.readingOpacity), live: live,
                                maxWidth: stageSize.width * 0.7 / scale, shadow: false) {
                        ZStack {
                            if live < 0.999 { word(run, restColour) }
                            if live > 0.001 { word(run, liveColour).opacity(live) }
                        }
                        .font(font(size, hero: true, id: run.id))
                        .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7 / scale)
                    }
                    if badgeSide >= 0.6 && badgeSide < 0.85 { badgeView }
                }
                .padding(.horizontal, size * 0.5).padding(.vertical, size * 0.26)
                .background {
                    ZStack {
                        // A soft ripple swells off the pill as it lands.
                        if live > 0.001, !reduceMotion {
                            RoundedRectangle(cornerRadius: corner, style: .continuous)
                                .fill(ink.tileColour.opacity(0.5 * (1 - eased) * live))
                                .blur(radius: size * 0.16)
                                .scaleEffect(1 + 0.3 * eased)
                        }
                        if live > 0.001 {
                            // Two loose, hand-drawn outlines around the pill, redrawn a few
                            // times a second so the lines seem to boil.
                            let boil = reduceMotion ? 0 : Double(Int(time * 8)) * 0.9
                            // One loose outline: a second one doubled it into a frame.
                            ForEach(0..<1, id: \.self) { pass in
                                SketchyOutline(corner: corner * 1.15, amount: size * 0.035, phase: boil + Double(pass) * 2.3,
                                               salt: Double(run.id * 3 + pass))
                                    .stroke(ink.tileColour.opacity(pass == 0 ? 0.75 : 0.4),
                                            style: StrokeStyle(lineWidth: max(1.2, size * 0.026), lineCap: .round, lineJoin: .round))
                                    .scaleEffect(1.08 + 0.05 * Double(pass))
                                    .rotationEffect(.degrees((pass == 0 ? -1.6 : 1.4) * (reduceMotion ? 0 : 1)))
                                    .opacity(eased * live)
                            }
                        }
                        // Only the word being sung sits in a pill: the trail is plain
                        // pale type, so the pill marks the one focal word. (A word on
                        // a tile, "Cutout · Highlight", keeps its tile.)
                        if live < 0.999, tile {
                            RoundedRectangle(cornerRadius: corner, style: .continuous)
                                .fill(ink.tileColour)
                                .opacity(outlined ? 1 - live : 1)
                        }
                        if live > 0.001 {
                            if outlined {
                                RoundedRectangle(cornerRadius: corner, style: .continuous)
                                    .strokeBorder(ink.tileColour, lineWidth: max(2, size * 0.07))
                                    .opacity(live)
                            } else {
                                // A little less saturated than the ink's tile colour,
                                // so the large live pill is calmer on a dark stage.
                                RoundedRectangle(cornerRadius: corner, style: .continuous)
                                    .fill(ink.tileColour)
                                    .saturation(0.78)
                                    .opacity(live)
                            }
                        }
                    }
                }
                .scaleEffect(scale)
                .shadow(color: .black.opacity(outlined ? 0 : 0.2 * live), radius: 9, y: 4)
                // It squashes as it lands, then settles.
                .scaleEffect(x: reduceMotion ? 1 : 1 + 0.07 * (1 - min(a, 1)), y: reduceMotion ? 1 : 1 - 0.07 * (1 - min(a, 1)))
                .scaleEffect(reduceMotion ? 1 : 0.9 + 0.1 * min(a, 1.05))
                .rotationEffect(.degrees(tilt * (0.5 + 0.5 * live)))
                .opacity(eased * visible[run.id] * (0.85 + 0.15 * live))
            }
        }
        .offset(y: reduceMotion ? 0 : -stageSize.height * 0.1 * leaving)
        .opacity(1 - leaving)
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
