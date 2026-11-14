import SwiftUI

// Portal: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.

extension StageLyricView {
    /// Portal's entry point, called by the dispatcher (`StageLyricView.layout`):
    /// `runs` is the whole line, `shown` the runs that have arrived. Portal
    /// sets whole words, not the shared runs (see `portalRuns`); it shows
    /// those that start by the last shown run's start, so the shared Reduce
    /// Motion dissolve still steps from one word to the next.
    func portalBody(runs: [StageRun], shown: [StageRun]) -> some View {
        let words = Self.portalRuns(self.words, glue: glue)
        return portal(Self.portalArrived(words, runs: runs, shown: shown, at: time), all: words)
    }

    /// Portal's words that have arrived: each from its own onset (less the
    /// shared lead), as every curve of its entrance starts there, so nothing
    /// on the stage jumps when it joins. (A word cut from the middle of a
    /// shared run, as an unspaced line's words are, arrived late, a few
    /// frames into its entrance, when only the shared runs' arrivals
    /// counted.) Under Reduce Motion the shared dissolve draws the step
    /// before as well: that layer leaves out the newest shared run's words.
    static func portalArrived(_ words: [StageRun], runs: [StageRun], shown: [StageRun], at time: Double) -> [StageRun] {
        let byTime = Array(words.prefix { time >= $0.start - StageMotion.lead })
        guard shown.count < shownRuns(runs, at: time).count else { return byTime }
        let cut = shown.last?.start ?? -.infinity
        return Array(byTime.prefix { $0.start <= cut + 0.0001 })
    }

    /// Portal leaves Reduce Motion to the shared dissolve between steps
    /// when false (`StageLyricView.reducedDissolve`).
    static let portalOwnsReduceMotion = false

    // MARK: Portal
    //
    // The word being sung sits in a solid pill, tilted as it arrives and
    // settling flat, with a soft burst swelling off it as it lands. The
    // words before it climb above it as a trail of smaller, paler type,
    // spaced by their own heights so none overlaps, and hold still there to
    // be read. A word with a picture of its own ("love", "home", "call")
    // carries that symbol in its pill; some other words carry a small
    // ornament, never the same one twice in a row; function words none.

    /// The ornaments a word without a picture of its own may carry.
    static let portalOrnaments = ["arrow.up.right", "arrow.right", "sparkle", "play.fill", "plus", "bolt.fill", "star.fill"]

    /// Portal's words: tokens glued into one word are one pill, however
    /// many syllables (beau·ti·ful, un·for·get·ta·ble, 기·억·할·게), their
    /// syllables revealed inside it as they are sung. A line written
    /// without spaces (Japanese, Chinese, Thai), glued as one run, is parted
    /// into its words (`LyricRuns.splittingWords`), each a pill of its own.
    static func portalRuns(_ words: [LyricsFetcher.Word], glue: [Bool]) -> [StageRun] {
        var padded = glue
        if padded.count < words.count { padded += [Bool](repeating: false, count: words.count - padded.count) }
        let parted = LyricRuns.splittingWords(words, glue: Array(padded.prefix(words.count)), into: .words)
        var runs: [StageRun] = []
        for (index, word) in words.enumerated() {
            let text = word.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            let part = StageRun.Part(text: text, start: word.start, source: word.presentationID)
            // A word longer than this is not one word: it is set as two.
            if index > 0, parted[index], var last = runs.last, last.text.count + text.count <= 24 {
                last.text += text
                last.end = max(last.end, word.end)
                last.parts.append(part)
                runs[runs.count - 1] = last
            } else {
                runs.append(StageRun(id: runs.count, text: text, start: word.start, end: max(word.end, word.start), parts: [part]))
            }
        }
        return runs
    }

    // MARK: Timing

    /// How each pill moves at one moment. Every word moves as the original
    /// Portal moved: the shared landing spring, a 0.6 s settle and a 0.36 s
    /// trail lift (divided by the line's pace), all on smooth eases. Only a
    /// word whose next word follows within `portalQuickNext` has a shorter
    /// settle and lift, never under `portalQuickSettle` / `portalQuickLift`,
    /// and a quick run (words under `portalQuickGap` apart) shares one burst.
    struct PortalMotion {
        /// 0...1: the pill fading in (the settle, as in the original).
        var appear: [Double]
        /// 0...1: the entrance (travel, tilt, outline) easing to rest.
        var settle: [Double]
        /// 0...1: how far the words before it have climbed out of its way.
        var lift: [Double]
        /// 0...1: the burst swelling off the pill (its quick run's).
        var burst: [Double]
        /// How far back each word is: the later words' settles, summed.
        var ranks: [Double]
        /// 1 while the word is the one sung, easing to 0 as the next takes over.
        var live: [Double]
    }

    /// Words closer together than this share one burst.
    static let portalQuickGap = 0.16
    /// A word followed sooner than this gets a shorter entrance...
    static let portalQuickNext = 0.35
    /// ...but its settle never shorter than this,
    static let portalQuickSettle = 0.28
    /// and its trail lift never shorter than this.
    static let portalQuickLift = 0.2

    /// The original settle and lift for a line at `speed`, shortened only
    /// for a quick word (and then never below the floors).
    static func portalTimes(_ runs: [StageRun], _ index: Int, speed: Double) -> (settle: Double, lift: Double) {
        let settle = 0.6 / speed, lift = 0.36 / speed
        guard index + 1 < runs.count else { return (settle, lift) }
        let next = runs[index + 1].start - runs[index].start
        guard next < portalQuickNext else { return (settle, lift) }
        // Never longer than the original either: a line already that quick keeps its own.
        return (min(settle, max(portalQuickSettle, 2 * next)), min(lift, max(portalQuickLift, 1.2 * next)))
    }

    static func portalMotion(_ runs: [StageRun], at time: Double, speed: Double, reduceMotion: Bool) -> PortalMotion {
        let count = runs.count
        var motion = PortalMotion(appear: [], settle: [], lift: [], burst: [], ranks: [], live: [])
        // The quick run each word belongs to: its first word's index.
        var head = [Int](repeating: 0, count: count)
        for index in runs.indices where index > 0 {
            let gap = runs[index].start - runs[index - 1].start
            head[index] = gap < portalQuickGap ? head[index - 1] : index
        }
        var runEnd = [Int](repeating: 0, count: count)
        for index in runs.indices.reversed() {
            let joinsNext = index + 1 < count && head[index + 1] == head[index]
            runEnd[index] = joinsNext ? runEnd[index + 1] : index
        }
        for index in runs.indices {
            let start = runs[index].start
            let times = portalTimes(runs, index, speed: speed)
            if reduceMotion {
                let on: Double = time >= start - StageMotion.lead ? 1 : 0
                motion.appear.append(on); motion.settle.append(on); motion.lift.append(on); motion.burst.append(1)
            } else {
                // Smootherstep starts and ends at rest, so the trail's sum of
                // them never jolts.
                // (From the word's arrival, `StageMotion.lead` before its
                // onset, so a pill joins the stage at rest.)
                let settle = glide((time - (start - StageMotion.lead)) / times.settle)
                motion.settle.append(settle)
                motion.appear.append(settle)
                // The trail climbs out of the way a little quicker than the
                // new pill settles, so the pill never lands on the one before it.
                motion.lift.append(glide((time - (start - StageMotion.lead)) / times.lift))
                // The burst swells with the settle; a quick run's words share
                // one, from its first word's onset to its last word's rest.
                let first = runs[head[index]].start
                let last = runEnd[index]
                let span = runs[last].start - first + portalTimes(runs, last, speed: speed).settle
                motion.burst.append(glide((time - (first - StageMotion.lead)) / span))
            }
        }
        var after = 0.0
        motion.ranks = [Double](repeating: 0, count: count)
        for index in runs.indices.reversed() {
            motion.ranks[index] = after
            after += motion.settle[index]
        }
        motion.live = motion.ranks.map { StageMotion.liveness(rank: $0) }
        return motion
    }

    /// Each pill's place above (negative) the live pill's resting place, on
    /// a stage with `above` points over that place and `below` under it.
    /// The trail is lowered, if it must be, so its top pill stays on the
    /// stage (a fading pill counts in full until it is mostly gone, so it is
    /// never squeezed against the top onto the one below), but never so far
    /// that a pill leaves the foot of the stage. Every term moves with the
    /// pills' own curves: a pill joining at rest moves nothing.
    static func portalColumn(heights: [CGFloat], lifts: [Double], visible: [Double], appear: [Double], gap: CGFloat,
                             above: CGFloat, below: CGFloat) -> [CGFloat] {
        let climb = portalClimb(heights: heights, lifts: lifts, gap: gap)
        let room = heights.indices.map { i in
            (climb[i] + heights[i] / 2 - (above - gap)) * CGFloat(min(1, visible[i] * 3))
        }
        let lowest = heights.indices.map { i in heights[i] * CGFloat(min(1, appear[i] * 3)) }.max() ?? 0
        let floor = max(0, below - lowest * 0.54)
        let lower = min(max(0, room.max() ?? 0), floor)
        return climb.map { lower - $0 }
    }

    /// How far above the live pill's place each pill has climbed: every
    /// later pill, as its trail lifts, raises it by half of each of the
    /// two neighbours' shown heights and a gap.
    static func portalClimb(heights: [CGFloat], lifts: [Double], gap: CGFloat) -> [CGFloat] {
        var climb = [CGFloat](repeating: 0, count: heights.count)
        var above: CGFloat = 0
        for index in heights.indices.reversed() {
            climb[index] = above
            if index > 0 {
                let step = (heights[index] + heights[index - 1]) / 2 + gap
                above += CGFloat(lifts[index]) * step
            }
        }
        return climb
    }

    // MARK: Icons and shapes

    /// The symbol in each pill, or nil. A word with a picture of its own
    /// (`LyricWordIcons`) always shows it. Other words may carry an
    /// ornament, for variety only: about every other one, never a
    /// function word, never an ornament shown on any of the three pills
    /// before. `pick` is a steady 0..<1 for a word index (-1 for the line),
    /// so a line looks the same each time it plays.
    static func portalIcons(_ texts: [String], pick: (Int) -> Double) -> [String?] {
        var icons: [String?] = []
        var gives = pick(-1) < 0.5
        for (index, text) in texts.enumerated() {
            if let symbol = LyricWordIcons.symbol(for: text) {
                icons.append(symbol)
                continue
            }
            let word = text.lowercased().trimmingCharacters(in: .punctuationCharacters)
            guard !little.contains(word) else { icons.append(nil); continue }
            defer { gives.toggle() }
            guard gives else { icons.append(nil); continue }
            let recent = Set(icons.suffix(3).compactMap { $0 })
            let choices = portalOrnaments.filter { !recent.contains($0) }
            guard !choices.isEmpty else { icons.append(nil); continue }
            let slot = Int(pick(index) * Double(choices.count)) % choices.count
            icons.append(choices[slot])
        }
        return icons
    }

    /// The pill's shapes, taken in turn so no two pills in a row match.
    enum PortalShape: Int, CaseIterable {
        case capsule, rounded, tag, speech, slant
    }

    static func portalShapes(count: Int, pick: (Int) -> Double) -> [PortalShape] {
        let all = PortalShape.allCases
        var shapes: [PortalShape] = []
        for index in 0..<count {
            guard let last = shapes.last else {
                shapes.append(all[Int(pick(index) * Double(all.count)) % all.count])
                continue
            }
            // Any shape but the last one.
            let step = 1 + Int(pick(index) * Double(all.count - 1)) % (all.count - 1)
            shapes.append(all[(last.rawValue + step) % all.count])
        }
        return shapes
    }

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

    // MARK: Drawing

    func portal(_ runs: [StageRun], all: [StageRun]) -> some View {
        let speed = lineSpeed
        let total = all.count
        // Worked out over the whole line, so a word's entrance knows when the next comes.
        let whole = Self.portalMotion(all, at: time, speed: speed, reduceMotion: reduceMotion)
        let motion = PortalMotion(appear: Array(whole.appear.prefix(runs.count)), settle: Array(whole.settle.prefix(runs.count)),
                                  lift: Array(whole.lift.prefix(runs.count)), burst: Array(whole.burst.prefix(runs.count)),
                                  ranks: [], live: [])
        // Ranks count only the words shown, so the newest is always the live one.
        var ranks = [Double](repeating: 0, count: runs.count)
        var after = 0.0
        for index in runs.indices.reversed() {
            ranks[index] = after
            after += motion.settle[index]
        }
        let lives = ranks.map { StageMotion.liveness(rank: $0) }
        // Each pill's tilt, in degrees: it leans in as it arrives and rocks
        // a little while it is sung.
        let tilts = runs.indices.map { index -> Double in
            let tiltAmount = 4 + 10 * chance(index, 14)
            let tiltSign: Double = chance(index, 15) < 0.5 ? 1 : -1
            let rock = sin(time * 1.1 + Double(index)) * 1.6 * lives[index]
            let entrance = (1 - motion.settle[index]) * tiltAmount * tiltSign
            return reduceMotion ? 0 : (entrance + rock) / speed * (0.5 + 0.5 * lives[index])
        }
        // Which way this line's trail leans, and where the pills sit. The
        // trail always climbs above the live pill, so it reads top to bottom.
        let flipX: CGFloat = chance(nil, 1) < 0.5 ? 1 : -1
        let reach = (0.8 + 0.5 * chance(nil, 3)) / speed.squareRoot()
        let shiftX = (chance(nil, 4) - 0.5) * 0.24 * stageSize.width
        let lift = stageSize.height * (0.1 + 0.08 * chance(nil, 5))
        let icons = Self.portalIcons(all.map(\.text)) { chance($0 < 0 ? nil : $0, 6) }
        let shapes = Self.portalShapes(count: total) { chance($0, 16) }
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
        // Older pills are shown smaller (the same scale as below).
        let scales = ranks.map { Self.portalScale(rank: $0) }
        let gap = unit * 0.16
        // A pill the trail would push past the top of the stage fades out
        // as it nears it (judged from the type size, before measuring),
        // rather than being held at the top, where the pills below would
        // pile onto it, or pushing the whole trail, live pill and all, down.
        let roomAbove = stageSize.height * 0.48 + lift
        let estimated = runs.indices.map { i -> CGFloat in
            let size = unit * (1.2 + 0.3 * chance(i, 10))
            let reading: CGFloat = Self.reading(of: runs[i], in: readings) == nil ? 0 : Self.readingSlot(size)
            return scales[i] * (size * 1.5 + reading) * (1.04 + 0.1 * CGFloat(lives[i]))
        }
        let estimatedClimb = Self.portalClimb(heights: estimated, lifts: motion.lift, gap: gap)
        let visible = runs.indices.map { i -> Double in
            let aged = 1 - Easing.smoothstep((ranks[i] - fadeFrom) / 0.9)
            let top = estimatedClimb[i] + estimated[i] / 2
            let crowded = Easing.smoothstep(Double((top - roomAbove) / max(1, estimated[i] * 0.6)) + 1)
            // The live pill never fades for room; the one it took over from
            // starts to as it hands over (by its liveness, so nothing pops).
            return aged * (1 - crowded * (1 - lives[i]))
        }
        let leaving = leave
        // Quick songs get less float, so the eye can follow.
        let float: CGFloat = reduceMotion ? 0 : CGFloat(1 / speed)
        return StageLayout(place: { measured, bounds in
            // The live pill's hand-drawn outline reaches a little beyond it.
            let shown = measured.indices.map { i in
                measured[i].height * scales[i] * (1.04 + 0.1 * CGFloat(lives[i]))
            }
            let base = bounds.midY + lift
            let column = Self.portalColumn(heights: shown, lifts: motion.lift, visible: visible, appear: motion.appear, gap: gap,
                                           above: base - bounds.minY, below: bounds.maxY - base)
            return measured.indices.map { index -> CGPoint in
                let p = CGPoint(x: alongX(ranks[index]), y: column[index])
                let bobRate: Double = 1.2 + 0.6 * chance(index, 7)
                let bobPhase: Double = time * bobRate + Double(index) * 1.3
                // Only the live pill floats; the trail holds still to be read.
                let floating = CGFloat(lives[index]) * float
                let bob: CGFloat = CGFloat(sin(bobPhase)) * unit * 0.07 * floating
                let swayRate: Double = 0.9 + 0.5 * chance(index, 8)
                let swayPhase: Double = time * swayRate + Double(index) * 0.9
                let sway: CGFloat = CGFloat(cos(swayPhase)) * unit * 0.05 * floating
                // Each pill comes in from its own direction, between the right
                // and below, as the line reads; never down through the trail.
                let heading = chance(index, 9) * 0.5 * .pi
                let travel = CGFloat(1 - motion.settle[index]) * float
                let comeX: CGFloat = travel * CGFloat(cos(heading)) * bounds.width * 0.07
                let comeY: CGFloat = travel * CGFloat(sin(heading)) * bounds.height * 0.1
                let x: CGFloat = bounds.midX + shiftX + p.x + sway + comeX
                let y: CGFloat = bounds.midY + lift + p.y + bob + comeY
                // A pill with its reading is taller; none is pushed off the stage.
                return Self.keepOnStage(CGPoint(x: x, y: y), size: measured[index], scale: scales[index], in: bounds)
            }
        }) {
            ForEach(runs) { run in
                portalPill(run, total: total, motion: motion, rank: ranks[run.id], live: lives[run.id],
                           visible: visible[run.id], icon: icons[run.id], shape: shapes[run.id], tilt: tilts[run.id])
            }
        }
        .offset(y: reduceMotion ? 0 : -stageSize.height * 0.1 * leaving)
        .opacity(1 - leaving)
    }

    /// One word: its pill while it is sung, pale type once it is not.
    @ViewBuilder
    private func portalPill(_ run: StageRun, total: Int, motion: PortalMotion, rank: Double, live: Double,
                            visible: Double, icon: String?, shape kind: PortalShape, tilt: Double) -> some View {
        let index = run.id
        let a = arrive(run, speed: lineSpeed)
        let eased = motion.settle[index]
        let burst = motion.burst[index]
        let sample = sample(run, of: total)
        let tile = tiled(run)
        // Set at its resting size; older pills are scaled down, not re-set.
        let size = unit * (1.2 + 0.3 * chance(index, 10))
        let scale = Self.portalScale(rank: rank)
        let pill = PortalPillShape(kind: kind, size: size, flip: chance(index, 17) < 0.5)
        // Some pills are only outlined.
        let outlined = chance(index, 12) < 0.25 && !tile
        let iconSide = chance(index, 13)
        let tiltSign: Double = chance(index, 15) < 0.5 ? 1 : -1
        let restColour = tile ? ink.tileText : sample.rest.opacity(0.85)
        let liveColour = outlined ? ink.tileColour : ink.tileText
        HStack(spacing: size * 0.3) {
            if let icon, iconSide < 0.6 { portalIcon(icon, size: size, live: live, eased: eased, tiltSign: tiltSign, outlined: outlined) }
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
                .font(font(size, hero: true, id: index))
                .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7 / scale)
            }
            if let icon, iconSide >= 0.6 { portalIcon(icon, size: size, live: live, eased: eased, tiltSign: tiltSign, outlined: outlined) }
        }
        .padding(.horizontal, size * 0.5).padding(.vertical, size * 0.26)
        .background { portalBackground(pill, size: size, live: live, eased: eased, burst: burst, tile: tile, outlined: outlined, salt: index) }
        .scaleEffect(scale)
        .shadow(color: .black.opacity(outlined ? 0 : 0.2 * live), radius: 9, y: 4)
        // It squashes as it lands, then settles.
        .scaleEffect(x: reduceMotion ? 1 : 1 + 0.07 * (1 - min(a, 1)), y: reduceMotion ? 1 : 1 - 0.07 * (1 - min(a, 1)))
        .scaleEffect(reduceMotion ? 1 : 0.9 + 0.1 * min(a, 1.05))
        .rotationEffect(.degrees(tilt))
        .opacity(motion.appear[index] * visible * (0.85 + 0.15 * live))
    }

    /// The symbol in a pill: it spins in with the pill's entrance and goes
    /// with the pill when the next word takes over; it never idles alone.
    private func portalIcon(_ name: String, size: CGFloat, live: Double, eased: Double, tiltSign: Double,
                            outlined: Bool) -> some View {
        let disc = outlined ? ink.tileColour.opacity(0.2) : ink.tileColour.opacity(0.35 + 0.65 * live)
        return Image(systemName: name)
            .font(.system(size: size * 0.34, weight: .bold))
            .rotationEffect(.degrees(reduceMotion ? 0 : (1 - eased) * -90 * tiltSign))
            .scaleEffect(reduceMotion ? 1 : 0.6 + 0.4 * eased)
            .foregroundStyle(outlined ? ink.tileColour : ink.tileText)
            .frame(width: size * 0.62, height: size * 0.62)
            .background(Circle().fill(disc))
            .opacity(live)
            .frame(width: size * 0.62 * live)
    }

    @ViewBuilder
    private func portalBackground(_ pill: PortalPillShape, size: CGFloat, live: Double, eased: Double, burst: Double,
                                  tile: Bool, outlined: Bool, salt: Int) -> some View {
        ZStack {
            // The burst: a soft swell off the pill as it lands (a quick run's
            // words share one, passed from pill to pill).
            if live > 0.001, !reduceMotion, burst < 1 {
                pill.fill(ink.tileColour.opacity(0.5 * (1 - burst) * live))
                    .blur(radius: size * 0.16)
                    .scaleEffect(1 + 0.3 * burst)
            }
            if live > 0.001, pill.kind != .speech, pill.kind != .slant {
                // One loose, hand-drawn outline around the pill, redrawn a few
                // times a second so the line seems to boil. (Not around the
                // speech and slanted shapes, which a rounded loop does not fit.)
                let boil = reduceMotion ? 0 : Double(Int(time * 8)) * 0.9
                SketchyOutline(corner: pill.radius * 1.15, amount: size * 0.035, phase: boil, salt: Double(salt * 3))
                    .stroke(ink.tileColour.opacity(0.6),
                            style: StrokeStyle(lineWidth: max(1.2, size * 0.026), lineCap: .round, lineJoin: .round))
                    .scaleEffect(1.08)
                    .rotationEffect(.degrees(-1.6 * (reduceMotion ? 0 : 1)))
                    .opacity(eased * live)
            }
            // Only the word being sung sits in a pill: the trail is plain
            // pale type, so the pill marks the one focal word. (A word on
            // a tile, "Cutout · Highlight", keeps its tile.)
            if live < 0.999, tile {
                pill.fill(ink.tileColour).opacity(outlined ? 1 - live : 1)
            }
            if live > 0.001 {
                if outlined {
                    pill.strokeBorder(ink.tileColour, lineWidth: max(2, size * 0.07)).opacity(live)
                } else {
                    // A little less saturated than the ink's tile colour,
                    // so the large live pill is calmer on a dark stage.
                    pill.fill(ink.tileColour).saturation(0.78).opacity(live)
                }
            }
        }
    }
}

/// A Portal pill: a capsule, a rounded rectangle, a squarer tag, a speech
/// shape with one sharp corner, or a capsule slanted like a parallelogram.
struct PortalPillShape: InsettableShape {
    var kind: StageLyricView.PortalShape
    /// The word's type size, which sets the radii.
    var size: CGFloat
    /// Which way the speech corner and the slant face.
    var flip: Bool
    var inset: CGFloat = 0

    /// The usual corner radius (a capsule's is clamped to half its height).
    var radius: CGFloat {
        switch kind {
        case .capsule, .slant: return size * 2
        case .rounded, .speech: return size * 0.45
        case .tag: return size * 0.18
        }
    }

    func path(in rect: CGRect) -> Path {
        let box = rect.insetBy(dx: inset, dy: inset)
        guard box.width > 0, box.height > 0 else { return Path() }
        let r = max(0, min(radius - inset, box.height / 2, box.width / 2))
        switch kind {
        case .speech:
            let sharp = min(r, size * 0.05)
            let radii = RectangleCornerRadii(topLeading: r, bottomLeading: flip ? r : sharp,
                                             bottomTrailing: flip ? sharp : r, topTrailing: r)
            return UnevenRoundedRectangle(cornerRadii: radii, style: .continuous).path(in: box)
        case .slant:
            // A capsule leaned by a sixth of its height, about its centre.
            let lean = box.height * (flip ? -0.18 : 0.18)
            let shape = RoundedRectangle(cornerRadius: r, style: .continuous).path(in: box)
            let shear = CGAffineTransform(a: 1, b: 0, c: -lean / box.height, d: 1, tx: lean / box.height * box.midY, ty: 0)
            return shape.applying(shear)
        default:
            return RoundedRectangle(cornerRadius: r, style: .continuous).path(in: box)
        }
    }

    func inset(by amount: CGFloat) -> PortalPillShape {
        var copy = self
        copy.inset += amount
        return copy
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
