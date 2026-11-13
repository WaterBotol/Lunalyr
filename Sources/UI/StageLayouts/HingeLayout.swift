import SwiftUI

// Hinge: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.

extension StageLyricView {
    /// Hinge's entry point, called by the dispatcher (`StageLyricView.layout`):
    /// `runs` is the whole line; every page is typeset from the start, and a
    /// word shows from its own onset, so `shown` is not needed.
    func hingeBody(runs: [StageRun], shown: [StageRun]) -> some View {
        hinge(runs)
    }

    /// Hinge draws Reduce Motion itself: no turns or swings, the pages and
    /// words cross-fade (see `hingeInstances`).
    static let hingeOwnsReduceMotion = true

    // MARK: Hinge
    //
    // An open book seen straight on. The spine is a vertical axis; each
    // phrase (`StagePhrases`) is one page of heavy caps: its focal word set
    // huge and flush to the spine, the words before it a small line of type
    // along the page's head, the words after it a small line at its foot.
    // The page being sung is the right-hand page (left, for a right-to-left
    // line). When the next phrase starts, the page turns over on the spine
    // and lands, dimmed, on the other side; the new phrase is set on the
    // fresh page beneath it, and the page before is covered. One axis, one
    // move at two scales: the focal word swings open on the spine as it is
    // sung, the page turns on it at each phrase, and the line leaves with a
    // last turn. Small words ink in place (content words with a short slide
    // away from the spine, function words with none); a quick run slides as
    // one group. A held focal word grows a little from the spine as it is held.

    /// Which side of the spine a page sits on.
    enum HingeSide: Equatable { case left, right }

    /// How a word arrives on its page (from its role in `StagePhrasing`).
    enum HingeEntrance: Equatable {
        /// The focal word: it swings open on the spine.
        case swing
        /// A content word or a member of a quick run: inks with a short slide away from the spine.
        case slide
        /// A function word: inks in place.
        case ink
    }

    static func hingeEntrance(_ info: StageWordInfo) -> HingeEntrance {
        if info.isFocal { return .swing }
        if info.isFunction, info.runGroup == nil { return .ink }
        return .slide
    }

    /// The phrases on each page: one phrase a page for a word-timed line; a
    /// line-timed line opens as one spread, its phrases split over two pages.
    static func hingePages(_ phrasing: StagePhrasing) -> [[Int]] {
        let count = phrasing.phrases.count
        guard count > 0 else { return [] }
        if phrasing.lineTimed, count > 1 {
            let half = (count + 1) / 2
            return [Array(0..<half), Array(half..<count)]
        }
        return (0..<count).map { [$0] }
    }

    /// The spine's x: the stage's centre for a spread, at the left third
    /// (right third, right-to-left) when the line is a single page.
    static func hingeSpine(width: CGFloat, single: Bool, rightToLeft: Bool) -> CGFloat {
        guard single else { return width / 2 }
        let margin = width * StageMotion.hingeMargin
        let x = margin + (width - 2 * margin) * 0.3
        return rightToLeft ? width - x : x
    }

    /// The room a page has between the spine's gutter and the 5% margin.
    static func hingePageWidth(width: CGFloat, spine: CGFloat, side: HingeSide, unit u: CGFloat) -> CGFloat {
        let margin = width * StageMotion.hingeMargin
        let gutter = u * StageMotion.hingeGutter
        let room = side == .right ? width - margin - spine - gutter : spine - margin - gutter
        return max(u * 2, room)
    }

    /// The small type of a page's head and foot lines.
    static func hingeSmallSize(unit u: CGFloat) -> CGFloat { u * 0.62 }

    /// The focal word's type size on a page: larger for a held focal word,
    /// and never so large that the page (its slabs, their small lines and
    /// every reading) is taller than 88% of the stage.
    /// `slabs` gives, per slab: whether it has a head line, a foot line, a
    /// reading under the focal word, and a reading under any small word.
    static func hingeFocalSize(slabs: [(head: Bool, foot: Bool, focalReading: Bool, smallReading: Bool)], held: Bool,
                               unit u: CGFloat, stageHeight: CGFloat) -> CGFloat {
        let small = hingeSmallSize(unit: u)
        let smallLine = small * 1.2
        var fixed: CGFloat = 0
        var perFocal: CGFloat = 0
        for slab in slabs {
            let lines = CGFloat((slab.head ? 1 : 0) + (slab.foot ? 1 : 0))
            fixed += lines * (smallLine + (slab.smallReading ? readingSlot(small) : 0)) + u * 0.12
            // A reading under the focal word is about 0.6 of its size (`readingSlot`).
            perFocal += 1.18 + (slab.focalReading ? readingSlot(100) / 100 : 0)
        }
        fixed += CGFloat(max(0, slabs.count - 1)) * u * 0.5
        let fits = (stageHeight * 0.88 - fixed) / max(perFocal, 0.1)
        let wanted = u * (held ? 3.0 : 2.5)
        return max(u * 0.9, min(wanted, fits))
    }

    /// How far each page has turned over, 0 (flat on its own side) to 1
    /// (flat on the other side, read). A page turns as the next page's first
    /// word is about to be sung, the last page as the line leaves; a
    /// line-timed spread opens from the spine at once, then turns its last
    /// page as it leaves. Eased in and out: a page is a big, heavy thing.
    static func hingeTurns(pageStarts: [Double], lineTimed: Bool, exitStart: Double, at time: Double,
                           speed: Double) -> [Double] {
        let count = pageStarts.count
        guard count > 0 else { return [] }
        let pace = 1 + (min(3, max(1, speed)) - 1) * 0.4
        let span = StageMotion.hingeTurn / pace
        func turning(from start: Double, span: Double) -> Double {
            hingeEaseInOut((time - start) / span)
        }
        let exit = turning(from: exitStart, span: StageMotion.hingeTurn)
        if lineTimed {
            let first = pageStarts.min() ?? 0
            return (0..<count).map { k in
                let open = hingeEaseOut((time - (first - StageMotion.lead) - StageMotion.hingeSpreadStagger * Double(k))
                                        / StageMotion.hingeSwing)
                let onOwnSide = k == count - 1
                if onOwnSide { return time >= exitStart ? exit : 0.5 - 0.5 * open }
                return 0.5 + 0.5 * open
            }
        }
        return (0..<count).map { k in
            if k + 1 < count {
                return turning(from: pageStarts[k + 1] - StageMotion.lead - StageMotion.hingeTurnLead, span: span)
            }
            return exit
        }
    }

    /// One drawn copy of a page: on its own side (`back` false) or turned over.
    struct HingeInstance: Equatable, Identifiable {
        var page: Int
        var back: Bool
        /// The page's angle about the spine, in degrees (0 flat).
        var angle: Double
        var opacity: Double
        var z: Double
        var id: Int { page * 2 + (back ? 1 : 0) }
    }

    /// The drawn copies of each page at its turn, faded edge-on so no page
    /// is seen squashed to a sliver, dimmed once read, and gone once the
    /// next page lands on it. `exitPage` is the page turned by the line's
    /// exit: it fades out as it lands rather than staying to be read.
    /// Under Reduce Motion a turn is a cross-fade between the two sides.
    /// `begun` says which pages have their first word sung (a page not yet
    /// begun is not drawn); nil takes every page as begun.
    static func hingeInstances(turns: [Double], begun: [Bool]? = nil, exitPage: Int?,
                               reduceMotion: Bool) -> [HingeInstance] {
        var result: [HingeInstance] = []
        for k in turns.indices {
            if let begun, begun.indices.contains(k), !begun[k] { continue }
            let turn = min(max(turns[k], 0), 1)
            let next = k + 1 < turns.count ? min(max(turns[k + 1], 0), 1) : 0
            let covered = Easing.smoothstep((next - 0.55) / 0.35)
            let leaving = k == exitPage
            func backOpacity(_ landed: Double) -> Double {
                leaving ? 1 - landed : 1 - (1 - StageMotion.hingeReadDim) * landed
            }
            if reduceMotion {
                // A step at the turn's start, cross-faded.
                let mix = turn <= 0 ? 0 : turn >= 1 ? 1 : Easing.smoothstep(turn / 0.3)
                let front = (1 - mix) * (1 - covered)
                let back = mix * backOpacity(mix) * (1 - covered)
                if front > 0.001 { result.append(HingeInstance(page: k, back: false, angle: 0, opacity: front, z: 1)) }
                if back > 0.001 { result.append(HingeInstance(page: k, back: true, angle: 0, opacity: back, z: 0)) }
                continue
            }
            let degrees = 180 * turn
            let edgeOn = abs(cos(degrees * .pi / 180))
            let visible = Easing.smoothstep((edgeOn - 0.1) / 0.35)
            let moving = turn > 0 && turn < 1
            if turn < 0.5 {
                let opacity = visible * (1 - covered)
                if opacity > 0.001 {
                    result.append(HingeInstance(page: k, back: false, angle: degrees, opacity: opacity, z: moving ? 2 : 1))
                }
            } else {
                let landed = Easing.smoothstep((turn - 0.5) / 0.5)
                let opacity = visible * backOpacity(landed) * (1 - covered)
                if opacity > 0.001 {
                    result.append(HingeInstance(page: k, back: true, angle: degrees - 180, opacity: opacity, z: moving ? 2 : 0))
                }
            }
        }
        return result
    }

    /// A word's pose as it arrives: its ink (opacity), its slide away from
    /// the spine (in type units), its swing (degrees, 0 flat) and its growth
    /// while held. Each lands on the word's onset; `groupStart` is when the
    /// quick run it belongs to starts to slide (its own onset otherwise).
    static func hingePose(_ entrance: HingeEntrance, start: Double, groupStart: Double, held: Double?, at time: Double,
                          speed: Double, reduceMotion: Bool)
        -> (ink: Double, slide: CGFloat, swing: Double, grow: CGFloat) {
        let age = time - (start - StageMotion.lead)
        if reduceMotion {
            return (Easing.smoothstep(age / WallpaperMotion.reducedDissolve), 0, 0, 1)
        }
        let pace = 1 + (min(3, max(1, speed)) - 1) * 0.5
        switch entrance {
        case .ink:
            return (Easing.smoothstep(age / (StageMotion.hingeInk / pace)), 0, 0, 1)
        case .slide:
            let travel = hingeEaseOut((time - (groupStart - StageMotion.lead)) / (StageMotion.hingeSlide / pace))
            return (Easing.smoothstep(age / (StageMotion.hingeInk / pace)), CGFloat(1 - travel), 0, 1)
        case .swing:
            let open = hingeEaseOut(age / (StageMotion.hingeSwing / pace))
            var grow: CGFloat = 1
            if let held, held > 0 {
                grow = 1 + StageMotion.hingeHeldGrowth * CGFloat(hingeEaseInOut((time - start) / held))
            }
            return (min(1, open * 2.5), 0, StageMotion.hingeSwingAngle * (1 - open), grow)
        }
    }

    /// Ease-out (exponential): arrivals.
    static func hingeEaseOut(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        return t >= 1 ? 1 : 1 - pow(2, -10 * t)
    }

    /// Ease-in-out (cubic): turns, and a held word's growth.
    static func hingeEaseInOut(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        return t < 0.5 ? 4 * t * t * t : 1 - pow(-2 * t + 2, 3) / 2
    }

    func hinge(_ runs: [StageRun]) -> some View {
        let phrasing = phrasing(runs)
        let pages = Self.hingePages(phrasing)
        let rtl = phrasing.rightToLeft
        let u = unit
        let width = stageSize.width, height = stageSize.height
        let spine = Self.hingeSpine(width: width, single: pages.count == 1, rightToLeft: rtl)
        // The side a page is sung on, and the side it turns over to.
        let own: HingeSide = rtl ? .left : .right
        let other: HingeSide = rtl ? .right : .left
        let speed = lineSpeed
        let starts: [Double] = pages.map { page in page.map { phrasing.phrases[$0].start }.min() ?? 0 }
        let turns = Self.hingeTurns(pageStarts: starts, lineTimed: phrasing.lineTimed, exitStart: exitStart, at: time,
                                    speed: speed)
        let exitPage: Int? = time >= exitStart && !pages.isEmpty ? pages.count - 1 : nil
        let begun = starts.map { phrasing.lineTimed || time >= $0 - StageMotion.lead }
        let instances = Self.hingeInstances(turns: turns, begun: begun, exitPage: exitPage, reduceMotion: reduceMotion)
            .sorted { $0.z < $1.z }
        // A turn swings the page's free edge up out of the stage, over the spine.
        let sign: Double = rtl ? -1 : 1
        return ZStack(alignment: .topLeading) {
            ForEach(instances) { instance in
                let side = instance.back ? other : own
                let room = Self.hingePageWidth(width: width, spine: spine, side: side, unit: u)
                let gutter = u * StageMotion.hingeGutter
                let pageWidth = room + gutter
                let minX = side == .right ? spine : spine - pageWidth
                hingePage(pages[instance.page], runs: runs, phrasing: phrasing, side: side, room: room,
                          live: !instance.back, speed: speed)
                    .padding(side == .right ? .leading : .trailing, gutter)
                    .frame(width: pageWidth, height: height, alignment: side == .right ? .leading : .trailing)
                    .rotation3DEffect(.degrees(reduceMotion ? 0 : sign * instance.angle), axis: (x: 0, y: 1, z: 0),
                                      anchor: side == .right ? .leading : .trailing, perspective: 0.45)
                    .opacity(instance.opacity)
                    .offset(x: minX)
            }
        }
        .frame(width: width, height: height, alignment: .topLeading)
    }

    /// One page: its slabs (one phrase each) stacked, flush to the spine.
    /// `live` is false on a page that has turned over: its words are drawn
    /// settled (they were all sung before it turned).
    private func hingePage(_ page: [Int], runs: [StageRun], phrasing: StagePhrasing, side: HingeSide, room: CGFloat,
                           live: Bool, speed: Double) -> some View {
        let u = unit
        let alignment: HorizontalAlignment = side == .right ? .leading : .trailing
        let slabs = page.map { phrasing.phrases[$0] }
        let shape = slabs.map { phrase -> (head: Bool, foot: Bool, focalReading: Bool, smallReading: Bool) in
            let others = phrase.runs.filter { $0 != phrase.focal }
            return (phrase.focal > phrase.runs.lowerBound, phrase.focal < phrase.runs.upperBound - 1,
                    Self.reading(of: runs[phrase.focal], in: readings) != nil,
                    others.contains { Self.reading(of: runs[$0], in: readings) != nil })
        }
        let held = slabs.count == 1 && phrasing.words.indices.contains(slabs[0].focal) && phrasing.words[slabs[0].focal].isHeld
        let focalSize = Self.hingeFocalSize(slabs: shape, held: held, unit: u, stageHeight: stageSize.height)
        return VStack(alignment: alignment, spacing: u * 0.5) {
            ForEach(slabs, id: \.index) { phrase in
                VStack(alignment: alignment, spacing: u * 0.06) {
                    if phrase.focal > phrase.runs.lowerBound {
                        hingeLine(Array(phrase.runs.lowerBound..<phrase.focal), runs: runs, phrasing: phrasing, side: side,
                                  room: room, live: live, speed: speed)
                    }
                    hingeWord(runs[phrase.focal], runs: runs, phrasing: phrasing, size: focalSize, side: side, room: room, live: live,
                              speed: speed)
                    if phrase.focal < phrase.runs.upperBound - 1 {
                        hingeLine(Array((phrase.focal + 1)..<phrase.runs.upperBound), runs: runs, phrasing: phrasing,
                                  side: side, room: room, live: live, speed: speed)
                    }
                }
            }
        }
    }

    /// A small line of type at a page's head or foot, in reading order.
    private func hingeLine(_ ids: [Int], runs: [StageRun], phrasing: StagePhrasing, side: HingeSide, room: CGFloat,
                           live: Bool, speed: Double) -> some View {
        let small = Self.hingeSmallSize(unit: unit)
        let ordered = phrasing.rightToLeft ? Array(ids.reversed()) : ids
        return HStack(alignment: .top, spacing: small * 0.34) {
            ForEach(ordered, id: \.self) { id in
                hingeWord(runs[id], runs: runs, phrasing: phrasing, size: small, side: side, room: room, live: live, speed: speed)
            }
        }
        .frame(maxWidth: room, alignment: side == .right ? .leading : .trailing)
    }

    /// One word, set in caps at `size`, arriving by its role.
    private func hingeWord(_ run: StageRun, runs: [StageRun], phrasing: StagePhrasing, size: CGFloat, side: HingeSide, room: CGFloat,
                           live: Bool, speed: Double) -> some View {
        let info = phrasing.words.indices.contains(run.id) ? phrasing.words[run.id] : nil
        let entrance = info.map(Self.hingeEntrance) ?? .ink
        let shrink: CGFloat = (info?.isFocal ?? false) ? 0.3 : 0.5
        let groupStart: Double = {
            guard let group = info?.runGroup else { return run.start }
            return phrasing.words.filter { $0.runGroup == group && runs.indices.contains($0.run) }
                .map { runs[$0.run].start }.min() ?? run.start
        }()
        let held: Double? = info.flatMap { $0.isHeld ? $0.duration : nil }
        let pose: (ink: Double, slide: CGFloat, swing: Double, grow: CGFloat) = phrasing.lineTimed || !live
            ? (time >= run.start - StageMotion.lead ? 1 : 0, 0, 0, held == nil ? 1 : 1 + StageMotion.hingeHeldGrowth)
            : Self.hingePose(entrance, start: run.start, groupStart: groupStart, held: held, at: time, speed: speed,
                             reduceMotion: reduceMotion)
        let sample = sample(run, of: phrasing.words.count)
        let tile = tiled(run)
        let towardSpine: CGFloat = side == .right ? -1 : 1
        let spineAnchor: UnitPoint = side == .right ? .leading : .trailing
        let swingSign: Double = side == .right ? 1 : -1
        return HingeCap(limit: room) {
            withReading(run, size: size, colour: sample.reading, alignment: side == .right ? .leading : .trailing,
                        maxWidth: room) {
                word(run, tile ? ink.tileText : sample.sung, uppercase: true)
                    .font(font(size, hero: true, id: run.id))
                    .lineLimit(1).minimumScaleFactor(shrink)
                    .background { tileBackground(tile, size: size) }
            }
        }
        .shadow(color: .black.opacity(tile ? 0 : 0.3), radius: max(2, size * 0.06), y: 2)
        .scaleEffect(pose.grow, anchor: spineAnchor)
        .rotation3DEffect(.degrees(swingSign * pose.swing), axis: (x: 0, y: 1, z: 0), anchor: spineAnchor,
                          perspective: 0.5)
        .offset(x: towardSpine * pose.slide * unit * StageMotion.hingeSlideDistance)
        .opacity(pose.ink)
    }

}

/// Hinge: a word at its own width, never wider than `limit` (its page):
/// a word too wide is offered the limit and shrinks to it. Its reading
/// is then measured under the word, not across the whole page.
struct HingeCap: Layout {
    var limit: CGFloat

    private func width(_ subview: LayoutSubview) -> CGFloat {
        min(subview.sizeThatFits(.unspecified).width, limit)
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let subview = subviews.first else { return .zero }
        return subview.sizeThatFits(ProposedViewSize(width: width(subview), height: nil))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard let subview = subviews.first else { return }
        subview.place(at: CGPoint(x: bounds.midX, y: bounds.midY), anchor: .center,
                      proposal: ProposedViewSize(width: width(subview), height: nil))
    }
}

extension StageMotion {
    /// Hinge: a page turning over on the spine (also the line's exit).
    static let hingeTurn = 0.62
    /// Hinge: a page starts to turn this long before the next page's first word arrives.
    static let hingeTurnLead = 0.12
    /// Hinge: the focal word swinging open on the spine.
    static let hingeSwing = 0.48
    /// Hinge: the focal word's starting angle, standing out of the page.
    static let hingeSwingAngle = 72.0
    /// Hinge: a small word inking in, and a content word's slide.
    static let hingeInk = 0.12
    static let hingeSlide = 0.24
    /// Hinge: the slide, in type units.
    static let hingeSlideDistance: CGFloat = 0.22
    /// Hinge: how much a held focal word grows over its held time.
    static let hingeHeldGrowth: CGFloat = 0.045
    /// Hinge: the two pages of a line-timed spread open this far apart.
    static let hingeSpreadStagger = 0.08
    /// Hinge: a page once read (turned over), and the 5% safe margin, and
    /// the gutter on each side of the spine (in type units).
    static let hingeReadDim = 0.38
    static let hingeMargin: CGFloat = 0.05
    static let hingeGutter: CGFloat = 0.18
}
