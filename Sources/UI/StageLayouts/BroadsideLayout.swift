import SwiftUI

// Broadside: one of the six stage layouts drawn by `StageLyricView`. Shared pieces
// (StageRun, StageMotion, StageLayout, the helpers) are in StageLyricStyles.swift.

extension StageLyricView {
    /// Broadside's entry point, called by the dispatcher (`StageLyricView.layout`):
    /// `runs` is the whole line. The whole line is printed from the start
    /// and each word is inked on its own onset, so `shown` is not needed.
    func broadsideBody(runs: [StageRun], shown: [StageRun]) -> some View {
        broadside(runs)
    }

    /// Broadside draws Reduce Motion itself: the camera steps phrase by
    /// phrase with a cross-fade, and ink fades in rather than sweeping.
    static let broadsideOwnsReduceMotion = true

    // MARK: Broadside
    //
    // A letterpress broadside: the whole line printed as one strip of type,
    // faint, the moment it starts. The big words come in two sizes (each
    // phrase's focal word the largest); the little words (`StagePhrases`
    // function words) are labels: tiny type stacked up to two lines high
    // against the cap height of the big word they belong to, as a printer
    // filled the space beside a big word. A phrase ends in an extra em of
    // space. The type never moves on the strip: a camera pans along it at
    // the singing pace and ink fills each word as it is sung. The focal word
    // fills across its whole sung time (a held word fills slowly); other big
    // words fill in a quick sweep; labels ink flat; a quick run is one sweep
    // across its words. The line leaves still travelling in the reading
    // direction, its phrases fading oldest first, and the next line's strip
    // arrives already moving the same way.

    private static let broadsideSpace = "broadside.strip"

    /// One thing on the strip: a big word, or a label block of little words
    /// riding a big word (`host`, an index into the items).
    struct BroadsideItem: Equatable {
        /// The runs it shows (`StageRun.id`s, in reading order).
        var runs: [Int]
        /// For a label, its big word (an index into the items); nil for a big word.
        var host: Int?
        /// A label set before its big word (else after it).
        var prefix: Bool
        var phrase: Int
        /// The type size: a big word's, or a label's.
        var size: CGFloat
        /// A label's lines (stacked), each a list of run ids.
        var lines: [[Int]]

        var isLabel: Bool { host != nil }
    }

    /// The big words' sizes, in type units: a phrase's focal word (larger
    /// when held), and the other big words.
    static let broadsideFocalSize: CGFloat = 2.3
    static let broadsideHeldSize: CGFloat = 2.6
    static let broadsideBigSize: CGFloat = 1.35

    /// The strip's items in reading order. A function word that is not its
    /// phrase's focal word becomes a label of the next big word in its phrase
    /// (of the one before, at the phrase's end); a label of several words is
    /// stacked on two lines and set to fill the big word's cap height.
    static func broadsideItems(_ phrasing: StagePhrasing, unit u: CGFloat, scale: CGFloat = 1) -> [BroadsideItem] {
        var items: [BroadsideItem] = []
        for phrase in phrasing.phrases {
            var pending: [Int] = []
            var lastBig: Int?
            func label(_ ids: [Int], host: Int, hostSize: CGFloat, prefix: Bool) -> BroadsideItem {
                let split = (ids.count + 1) / 2
                let lines = ids.count > 1 ? [Array(ids.prefix(split)), Array(ids.dropFirst(split))] : [ids]
                return BroadsideItem(runs: ids, host: host, prefix: prefix, phrase: phrase.index,
                                     size: hostSize * (lines.count > 1 ? 0.3 : 0.42), lines: lines)
            }
            for id in phrase.runs {
                let info = phrasing.words.indices.contains(id) ? phrasing.words[id] : nil
                let little = (info?.isFunction ?? false) && !(info?.isFocal ?? false)
                if little { pending.append(id); continue }
                let held = info?.isHeld ?? false
                let units = (info?.isFocal ?? false) ? (held ? broadsideHeldSize : broadsideFocalSize) : broadsideBigSize
                let size = u * units * scale
                if !pending.isEmpty {
                    // Set before its word, which is the next item.
                    items.append(label(pending, host: items.count + 1, hostSize: size, prefix: true))
                    pending = []
                }
                items.append(BroadsideItem(runs: [id], host: nil, prefix: false, phrase: phrase.index, size: size,
                                           lines: [[id]]))
                lastBig = items.count - 1
            }
            if !pending.isEmpty, let host = lastBig {
                items.append(label(pending, host: host, hostSize: items[host].size, prefix: false))
            }
        }
        return items
    }

    /// How far the strip reaches: each item's left edge (in strip
    /// coordinates, from 0) and the whole width. Big words are a word space
    /// apart; a label tucks close to its word; a phrase ends in an extra em.
    static func broadsideStrip(_ widths: [CGFloat], items: [BroadsideItem], unit u: CGFloat) -> (lefts: [CGFloat], width: CGFloat) {
        var lefts = [CGFloat](repeating: 0, count: items.count)
        var cursor: CGFloat = 0
        for index in items.indices {
            let width = widths.indices.contains(index) ? widths[index] : 0
            if index > 0 {
                let previous = items[index - 1]
                let item = items[index]
                if item.phrase != previous.phrase {
                    // A phrase ends in an extra space, about an em of its big type.
                    cursor += max(item.size, previous.isLabel ? item.size : previous.size) * 0.7
                } else if previous.isLabel && previous.prefix && previous.host == index {
                    cursor += item.size * 0.1
                } else if item.isLabel && !item.prefix {
                    cursor += (items[item.host ?? index].size) * 0.1
                } else {
                    cursor += max(item.size, previous.isLabel ? item.size : previous.size) * 0.26
                }
            }
            lefts[index] = cursor
            cursor += width
        }
        return (lefts, cursor)
    }

    /// One camera beat: from `start`, over `span`, the camera brings `item`
    /// (a big word) to its reading spot.
    struct BroadsideBeat: Equatable {
        var start: Double
        var span: Double
        var item: Int
    }

    /// The camera's beats: one per big word, one per quick run (moving to
    /// its last word over the whole run). A line-timed line has none of its
    /// own timing: it is read along at a steady pace from its start.
    static func broadsideBeats(_ phrasing: StagePhrasing, items: [BroadsideItem], runs: [StageRun]) -> [BroadsideBeat] {
        var beats: [BroadsideBeat] = []
        var lastGroup: Int?
        let bigs = items.indices.filter { !items[$0].isLabel }
        if phrasing.lineTimed {
            let first = runs.map(\.start).min() ?? 0
            return bigs.enumerated().map { number, item in
                BroadsideBeat(start: first + 0.6 + Double(number) * 0.4, span: 0.6, item: item)
            }
        }
        for item in bigs {
            guard let id = items[item].runs.first, runs.indices.contains(id) else { continue }
            let info = phrasing.words.indices.contains(id) ? phrasing.words[id] : nil
            if let group = info?.runGroup, group == lastGroup, var beat = beats.popLast() {
                beat.span = max(beat.span, runs[id].end - beat.start, runs[id].start - beat.start + 0.3)
                beat.item = item
                beats.append(beat)
                continue
            }
            lastGroup = info?.runGroup
            let sung = info?.duration ?? 0
            beats.append(BroadsideBeat(start: runs[id].start, span: max(0.45, min(sung, 1.4)), item: item))
        }
        return beats
    }

    /// Where the camera is: the strip x that sits at the stage's centre.
    /// Each beat glides it (eased in and out) so its word sits a little
    /// before the centre with the unsung print ahead of it; it never shows
    /// space beyond the strip's ends past the margins, and a strip that fits
    /// is centred and still. `step` (Reduce Motion) jumps to each beat at
    /// its start instead of gliding.
    /// `rest` is where the line comes to rest: the centre of its last
    /// phrase, so the camera ends with that phrase centred rather than the
    /// strip's end held against the margin.
    static func broadsideCamera(lefts: [CGFloat], widths: [CGFloat], stripWidth: CGFloat, beats: [BroadsideBeat],
                                stageWidth: CGFloat, unit u: CGFloat, at time: Double, step: Bool = false,
                                rest: CGFloat? = nil) -> CGFloat {
        let margin = stageWidth * broadsideMargin + u * broadsideFadeSpan
        let reach = stageWidth / 2 - margin
        guard stripWidth > 2 * reach else { return stripWidth / 2 }
        let low = reach, high = max(stripWidth - reach, rest ?? 0)
        func spot(_ item: Int) -> CGFloat {
            guard lefts.indices.contains(item) else { return low }
            let centre = lefts[item] + (widths.indices.contains(item) ? widths[item] : 0) / 2
            return min(high, max(low, centre + stageWidth * broadsideLeadRoom))
        }
        var camera = low
        var previous = low
        for beat in beats {
            let target = spot(beat.item)
            let begun = time - (beat.start - StageMotion.lead)
            let progress = step ? (begun >= 0 ? 1 : 0) : glide(begun / max(beat.span, 0.05))
            camera += CGFloat(progress) * (target - previous)
            previous = target
        }
        return camera
    }

    /// How far a word is inked, 0...1, at `time`: a sweep from its onset,
    /// reaching each glued syllable at the syllable's own onset. The focal
    /// word's sweep lasts its sung time (at least `broadsideFocalSweep`);
    /// other words sweep in `broadsideSweep`, a quick run's member in the
    /// time to the next member. Reduce Motion: 0 or 1 (it fades instead).
    static func broadsideInk(_ run: StageRun, info: StageWordInfo?, next: Double?, at time: Double,
                             reduceMotion: Bool = false) -> Double {
        let lead = StageMotion.lead
        if reduceMotion { return time >= run.start - lead ? 1 : 0 }
        var span = broadsideSweep
        if let info {
            if info.isFocal {
                span = max(broadsideFocalSweep, info.duration)
            } else if info.runGroup != nil, let next {
                span = min(max(next - run.start, 0.08), broadsideSweep)
            }
        }
        let parts = run.parts
        guard parts.count > 1 else { return sweep((time - (run.start - lead)) / span) }
        // Each syllable fills its share of the word from its own onset.
        let total = CGFloat(max(1, run.text.count))
        var reached = 0.0
        var offset = 0
        for (index, part) in parts.enumerated() {
            let share = Double(CGFloat(part.text.count) / total)
            let after = index + 1 < parts.count ? parts[index + 1].start : nil
            let partSpan = after.map { min(max($0 - part.start, 0.06), span) } ?? span
            let filled = sweep((time - (part.start - lead)) / partSpan)
            if filled <= 0 { break }
            reached = Double(offset) / Double(total) + share * filled
            offset += part.text.count
            if filled < 1 { break }
        }
        return min(1, reached)
    }

    private static func sweep(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        return t * t * (3 - 2 * t)
    }

    /// How much smaller the strip is set on a stage narrower than about 21
    /// type units: about three big words to a screen on every stage.
    static func broadsideStageScale(width: CGFloat, unit u: CGFloat) -> CGFloat {
        min(1, max(0.5, width / (u * 21)))
    }

    /// Broadside fades a word out, whole, as its box nears an edge of the
    /// stage this far in (a share of the stage's width): no word is ever
    /// cut through a letter by the edge.
    static let broadsideMargin: CGFloat = 0.05
    /// The distance (in type units) over which a word fades as it nears the margin.
    static let broadsideFadeSpan: CGFloat = 0.7
    /// How far ahead of the word being inked the camera looks (share of the stage).
    static let broadsideLeadRoom: CGFloat = 0.1
    /// The quick sweep of an ordinary big word, and the shortest focal sweep.
    static let broadsideSweep = 0.16
    static let broadsideFocalSweep = 0.3

    /// How visible a Broadside word is from where its box sits: 1 well inside
    /// the stage, fading to 0 as either end reaches the margin.
    static func broadsideEdgeFade(_ frame: CGRect, stageWidth: CGFloat, unit: CGFloat) -> Double {
        let margin = stageWidth * broadsideMargin
        let span = max(unit * broadsideFadeSpan, 1)
        let left = (frame.minX - margin) / span
        let right = (stageWidth - margin - frame.maxX) / span
        return Easing.smoothstep(Double(left)) * Easing.smoothstep(Double(right))
    }

    /// How present phrase `phrase` is: printed (faint) once the phrase
    /// before it is being sung, so only the next phrase is ever printed
    /// ahead; gone once two phrases after it have started (at most two
    /// inked phrases on screen); and, as the line leaves, faded out oldest first.
    static func broadsidePhrasePresence(_ phrase: Int, phrases: [StagePhrase], exitStart: Double, at time: Double) -> Double {
        let lead = StageMotion.lead
        var presence = 1.0
        if phrase > 0, phrases.indices.contains(phrase - 1) {
            presence *= Easing.smoothstep((time - (phrases[phrase - 1].start - lead)) / 0.3)
        }
        if phrases.indices.contains(phrase + 2) {
            // Gone by the time the phrase two on is sung.
            presence *= 1 - Easing.smoothstep((time - (phrases[phrase + 2].start - lead - 0.3)) / 0.3)
        }
        if time > exitStart {
            let first = phrases.indices.first { broadsidePhrasePresenceBeforeExit($0, phrases: phrases, at: exitStart) > 0.01 } ?? 0
            let order = Double(max(0, phrase - first))
            let gone = (time - exitStart - StageMotion.broadsideExitStagger * order) / StageMotion.broadsideExitFade
            presence *= 1 - Easing.smoothstep(gone)
        }
        return presence
    }

    private static func broadsidePhrasePresenceBeforeExit(_ phrase: Int, phrases: [StagePhrase], at time: Double) -> Double {
        guard phrases.indices.contains(phrase + 2) else { return 1 }
        return 1 - Easing.smoothstep((time - (phrases[phrase + 2].start - StageMotion.lead - 0.3)) / 0.3)
    }

    /// The camera's extra travel as the line arrives (from ahead, slowing)
    /// and leaves (on ahead, speeding up), as a share of the stage's width:
    /// both in the reading direction, so one line hands over to the next.
    static func broadsideTravel(firstStart: Double, exitStart: Double, at time: Double, reduceMotion: Bool) -> CGFloat {
        guard !reduceMotion else { return 0 }
        let arriving = 1 - glideOut((time - (firstStart - StageMotion.lead)) / StageMotion.broadsideArrive)
        let leaving = time > exitStart ? glideIn((time - exitStart) / StageMotion.broadsideLeave) : 0
        return StageMotion.broadsideTravel * CGFloat(leaving - arriving)
    }

    private static func glideOut(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        return 1 - pow(1 - t, 3)
    }

    private static func glideIn(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        return t * t * t
    }

    func broadside(_ runs: [StageRun]) -> some View {
        let phrasing = phrasing(runs)
        let u = unit
        let width = stageSize.width
        // A narrow stage (the floating window) sets the strip smaller, so a
        // phrase still reads as a phrase rather than one word at a time.
        let stageScale = Self.broadsideStageScale(width: width, unit: u)
        // A line-timed line cannot be read along: it is set to fit if it can.
        let fitScale: CGFloat = {
            guard phrasing.lineTimed else { return 1 }
            let size = u * Self.broadsideBigSize * stageScale
            let estimate = runs.map { CGFloat($0.text.count) * 0.55 * size + size * 0.3 }.reduce(0, +)
            return min(1, max(0.6, width * (1 - 2 * Self.broadsideMargin) / max(estimate, 1)))
        }()
        let items = Self.broadsideItems(phrasing, unit: u, scale: fitScale * stageScale)
        let beats = Self.broadsideBeats(phrasing, items: items, runs: runs)
        let firstStart = runs.map(\.start).min() ?? 0
        let travel = Self.broadsideTravel(firstStart: firstStart, exitStart: exitStart, at: time, reduceMotion: reduceMotion)
        // Reduce Motion: the camera steps at each phrase, cross-faded.
        let current = phrasing.phraseIndex(at: time) ?? 0
        let stepAge = phrasing.phrases.indices.contains(current) ? time - (phrasing.phrases[current].start - StageMotion.lead) : 1
        let mix = reduceMotion && current > 0 ? Easing.smoothstep(stepAge / WallpaperMotion.reducedDissolve) : 1
        let strip = { (cameraTime: Double) in
            broadsideStrip(runs, phrasing: phrasing, items: items, beats: beats, travel: travel, cameraTime: cameraTime)
        }
        return ZStack {
            if mix < 1 {
                strip(phrasing.phrases[current].start - StageMotion.lead - 0.001).opacity(1 - mix)
                strip(time).opacity(mix)
            } else {
                strip(time)
            }
        }
        .frame(width: stageSize.width, height: stageSize.height)
    }

    /// The strip with the camera where it is at `cameraTime` (the time,
    /// except while Reduce Motion cross-fades from the step before).
    private func broadsideStrip(_ runs: [StageRun], phrasing: StagePhrasing, items: [BroadsideItem],
                                beats: [BroadsideBeat], travel: CGFloat, cameraTime: Double) -> some View {
        let u = unit
        let width = stageSize.width
        let rtl = phrasing.rightToLeft
        let step = reduceMotion
        let slots: [CGFloat] = items.map { item in
            guard !item.isLabel, let id = item.runs.first, runs.indices.contains(id),
                  Self.reading(of: runs[id], in: readings) != nil else { return 0 }
            return Self.readingSlot(item.size)
        }
        let anyReading = slots.contains { $0 > 0 }
        let bigSize = items.filter { !$0.isLabel }.map(\.size).min() ?? u
        let time = self.time
        return StageLayout(place: { measured, bounds in
            let widths = measured.map(\.width)
            let strip = Self.broadsideStrip(widths, items: items, unit: u)
            let last = items.indices.filter { items[$0].phrase == items.last?.phrase }
            let lastStart = last.map { strip.lefts[$0] }.min() ?? 0
            let lastEnd = last.map { strip.lefts[$0] + widths[$0] }.max() ?? strip.width
            let camera = Self.broadsideCamera(lefts: strip.lefts, widths: widths, stripWidth: strip.width, beats: beats,
                                              stageWidth: bounds.width, unit: u, at: cameraTime, step: step,
                                              rest: (lastStart + lastEnd) / 2)
                + travel * bounds.width
            // The words stand on one baseline a little below the centre (their
            // capitals rise above it); readings hang below.
            let baseline = bounds.midY + bigSize * 0.4 - (anyReading ? Self.readingSlot(bigSize) * 0.5 : 0)
            return items.indices.map { index in
                let size = measured.indices.contains(index) ? measured[index] : .zero
                let item = items[index]
                var x = bounds.midX + strip.lefts[index] + size.width / 2 - camera
                if rtl { x = bounds.minX + bounds.maxX - x }
                let y: CGFloat
                if let host = item.host {
                    // Tucked against the big word's cap height, top-aligned.
                    let capTop = baseline - items[host].size * 0.72
                    y = capTop - item.size * 0.22 + size.height / 2
                } else {
                    let word = size.height - slots[index]
                    y = baseline - word * 0.79 + size.height / 2
                }
                return CGPoint(x: x, y: y)
            }
        }) {
            ForEach(items.indices, id: \.self) { index in
                broadsideItem(items[index], runs: runs, phrasing: phrasing, time: time)
                    .opacity(Self.broadsidePhrasePresence(items[index].phrase, phrases: phrasing.phrases,
                                                          exitStart: exitStart, at: time))
                    .visualEffect { item, box in
                        item.opacity(Self.broadsideEdgeFade(box.frame(in: .named(Self.broadsideSpace)),
                                                            stageWidth: width, unit: u))
                    }
            }
        }
        .coordinateSpace(.named(Self.broadsideSpace))
    }

    @ViewBuilder
    private func broadsideItem(_ item: BroadsideItem, runs: [StageRun], phrasing: StagePhrasing, time: Double) -> some View {
        if item.isLabel {
            VStack(alignment: item.prefix ? .trailing : .leading, spacing: -item.size * 0.12) {
                ForEach(item.lines.indices, id: \.self) { line in
                    HStack(spacing: item.size * 0.3) {
                        ForEach(item.lines[line], id: \.self) { id in
                            broadsideWord(runs[id], runs: runs, phrasing: phrasing, size: item.size, label: true)
                        }
                    }
                }
            }
        } else if let id = item.runs.first, runs.indices.contains(id) {
            broadsideWord(runs[id], runs: runs, phrasing: phrasing, size: item.size, label: false)
        }
    }

    /// A word on the strip: its faint print, and its ink over it, swept in
    /// from the reading side (a label's ink fades in flat).
    private func broadsideWord(_ run: StageRun, runs: [StageRun], phrasing: StagePhrasing, size: CGFloat,
                               label: Bool) -> some View {
        let info = phrasing.words.indices.contains(run.id) ? phrasing.words[run.id] : nil
        let next = runs.indices.contains(run.id + 1) ? runs[run.id + 1].start : nil
        let lineInk: Double? = phrasing.lineTimed
            ? Self.sweep((time - (run.start - StageMotion.lead) - 0.14 * Double(info?.phrase ?? 0)) / 0.3) : nil
        let flat = label || reduceMotion
        let filled = lineInk ?? Self.broadsideInk(run, info: info, next: next, at: time)
        let inkFade = flat ? Easing.smoothstep((time - (run.start - StageMotion.lead))
                                                    / (reduceMotion ? WallpaperMotion.reducedDissolve : 0.12)) : 1
        let fill = flat ? (lineInk ?? 1) : filled
        let sample = sample(run, of: runs.count)
        let tile = tiled(run)
        let hard = size * 0.04
        let rtl = phrasing.rightToLeft
        let edge = 0.08
        let reach = fill * (1 + 2 * edge) - edge
        let mask = LinearGradient(stops: [.init(color: .black, location: reach - edge),
                                          .init(color: .clear, location: reach + edge)],
                                  startPoint: rtl ? .trailing : .leading, endPoint: rtl ? .leading : .trailing)
        let print = Text(run.text)
        return withReading(run, size: size, colour: sample.reading, maxWidth: stageSize.width * 0.7, shadow: !label) {
            ZStack {
                print.foregroundStyle(sample.rest.opacity(sample.dim))
                if fill > 0 && inkFade > 0 {
                    ZStack {
                        print.foregroundStyle(Color.black.opacity(tile ? 0 : 0.34)).offset(x: hard, y: hard * 1.15)
                        print.foregroundStyle(tile ? ink.tileText : sample.sung)
                    }
                    .background { tileBackground(tile, size: size) }
                    .mask { if fill < 1 { mask } else { Color.black } }
                    .opacity(inkFade)
                }
            }
            .font(font(size, hero: !label, id: run.id))
            .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7)
        }
    }
}

extension StageMotion {
    /// Broadside: the camera's extra travel as a line arrives and leaves (a
    /// share of the stage's width, about the 12% partial travel of a cut),
    /// and how long each takes.
    static let broadsideTravel: CGFloat = 0.12
    static let broadsideArrive = 0.55
    static let broadsideLeave = 0.45
    /// Broadside: phrases fade out oldest first as the line leaves.
    static let broadsideExitStagger = 0.08
    static let broadsideExitFade = 0.32
}
