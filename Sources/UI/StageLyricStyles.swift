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
        /// The lyric word this part was cut from, for looking up its reading.
        var source: TimedWordKey? = nil
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
            if joins(text, at: index, glue: glue, after: runs.last?.text) {
                var last = runs.removeLast()
                last.text += text
                last.end = max(last.end, word.end)
                last.parts.append(Part(text: text, start: word.start, source: word.presentationID))
                runs.append(last)
            } else {
                runs.append(StageRun(id: runs.count, text: text, start: word.start, end: max(word.end, word.start),
                                     parts: [Part(text: text, start: word.start, source: word.presentationID)]))
            }
        }
        return runs
    }

    /// Whether the word `text` at `index` joins the run before it: glued to
    /// it, and the two together still short enough to read as one word.
    private static func joins(_ text: String, at index: Int, glue: [Bool], after previous: String?) -> Bool {
        guard index > 0, glue.indices.contains(index), glue[index], let previous else { return false }
        return previous.count + text.count <= (LyricScript.keepsSyllables(text) ? 3 : 12)
    }

    /// The glue of the runs `make` builds: true where a word joins the run
    /// before it. "Below lyrics" pairs readings by this in the stage layouts,
    /// so each run carries its own reading and it arrives with the run. Paired
    /// by the line's longer runs instead, an unspaced line's whole reading sat
    /// on its first run and showed as soon as the line began.
    static func glue(_ words: [LyricsFetcher.Word], glue: [Bool]) -> [Bool] {
        var result = [Bool](repeating: false, count: words.count)
        var last: String?
        for (index, word) in words.enumerated() {
            let text = word.text.trimmingCharacters(in: .whitespacesAndNewlines)
            // A blank word belongs to no run of its own.
            guard !text.isEmpty else { result[index] = index > 0; continue }
            if joins(text, at: index, glue: glue, after: last) {
                result[index] = true
                last = (last ?? "") + text
            } else {
                last = text
            }
        }
        return result
    }

    /// How visible one part is: the first arrives with its word; each later
    /// syllable fades in over a short settle as it is sung, never cut in.
    static func partOpacity(_ index: Int, of parts: [Part], at time: Double) -> Double {
        guard parts.indices.contains(index) else { return 0 }
        let age = time - (parts[index].start - 0.03)
        if index == 0 { return age >= 0 ? 1 : 0 }
        return vc_settle(age, StageMotion.syllableFade)
    }

    /// The word with its not-yet-sung syllables invisible (they still take up
    /// their room, so the word does not jump as it fills in). Given the
    /// colour the text is drawn in, a syllable being sung fades in over
    /// `StageMotion.syllableFade`; without it, it appears at its onset.
    func revealed(at time: Double, uppercase: Bool = false, colour: Color? = nil) -> AttributedString {
        var result = AttributedString()
        for (index, part) in parts.enumerated() {
            var piece = AttributedString(uppercase ? part.text.uppercased() : part.text)
            let shown = Self.partOpacity(index, of: parts, at: time)
            if shown <= 0 {
                piece.foregroundColor = .clear
            } else if shown < 1, let colour {
                piece.foregroundColor = colour.opacity(shown)
            }
            result.append(piece)
        }
        return result
    }
}

/// The timing every stage layout shares (POLISH-AUDIT L2, L3, L5, L11, L12,
/// L14, L16). Each function is a pure function of playback time, so a
/// seek, a pause or a dropped frame never changes what is drawn.
enum StageMotion {
    /// A word starts to arrive this far ahead of its onset.
    static let lead = 0.04
    /// A sung syllable of a glued word fading in (curve: `vc_settle`).
    static let syllableFade = 0.16
    /// Rise of the arrival spring at an ordinary pace; a fast line divides it
    /// by `lineSpeed`. Damping 0.62 overshoots about 8%, close to the
    /// easeOutBack the layouts used before, but it starts from rest, so a sum
    /// of arrivals never steps in speed at an onset.
    static let arrivalRise = 0.18
    static let arrivalDamping = 0.62
    /// The cadence a rank or camera hand-off is tuned to at an ordinary pace.
    static let handoffCadence = 0.5
    /// Hinge's faces swinging shut, and the extra delay of its last word's fold.
    static let hingeClose = 0.6
    static let hingeFoldStagger = 0.24

    /// A word's pose arrival, 0...1 with a little overshoot. Under Reduce
    /// Motion it is a step at the onset; the layout dissolves between steps.
    static func arrival(_ start: Double, at time: Double, speed: Double = 1, reduceMotion: Bool = false) -> Double {
        let age = time - (start - lead)
        if reduceMotion { return age >= 0 ? 1 : 0 }
        let rise = arrivalRise / min(3, max(1, speed))
        return vc_spring_step(age, VCSpring(omega: 3.36 / rise, zeta: arrivalDamping))
    }

    /// One onset's share of a rank or a camera move: the rolling layouts'
    /// hand-off spring, so velocity carries from one word to the next.
    static func handoff(_ start: Double, at time: Double, speed: Double = 1, seed: UInt64,
                        reduceMotion: Bool = false) -> Double {
        let age = time - (start - lead)
        if reduceMotion { return age >= 0 ? 1 : 0 }
        return vc_rolling_handoff(age, handoffCadence / min(3, max(1, speed)), seed, 0)
    }

    /// The whole line leaving: the same settle as every other line exit.
    static func leave(at time: Double, exitStart: Double) -> Double {
        vc_settle(time - exitStart, WallpaperMotion.lineExit)
    }

    /// How much a word is still the one being sung: 1 until the next word
    /// starts to take over, 0 once it has. Every live-only property is
    /// blended by this, so nothing restyles in a single frame.
    static func liveness(rank: Double) -> Double {
        1 - Easing.smoothstep((rank - 0.15) / 0.7)
    }

    /// How much room a word takes in a stack: none until it starts to arrive,
    /// all of it once it has (the overshoot is not added).
    static func weight(_ arrival: Double) -> Double { min(1, max(0, arrival)) }

    /// Torque: how visible a word is from its rank. Old words fade out
    /// above the fourth, and give their room back as they go.
    static func torqueVisibility(rank: Double) -> Double { max(0, 1 - max(0, rank - 3) * 0.8) }

    /// Hinge: the column shrinks by 15% as its seventh word arrives, not at once.
    static func hingeColumnShrink(_ arrived: Double) -> Double { 1 - 0.15 * Easing.smoothstep(arrived - 6) }

    /// Hinge: how far one word has folded shut. Oldest first; all of them are
    /// shut by the end of the line's exit span.
    static func hingeFold(order: Double, at time: Double, exitStart: Double) -> Double {
        vc_settle(time - exitStart - hingeFoldStagger * min(1, max(0, order)), WallpaperMotion.lineExit)
    }

    /// Reduce Motion: how far the stage has dissolved from its previous step
    /// (before `onset`) to this one. 1 once the dissolve is over.
    static func reducedStep(since onset: Double, at time: Double) -> Double {
        Easing.smoothstep((time - (onset - lead)) / WallpaperMotion.reducedDissolve)
    }

    /// Mosaic: the big setting shrinks to the small one's size by settle 0.7,
    /// then the two cross-fade between 0.7 and 0.95, so the two copies are
    /// never seen at different sizes.
    static func mosaicMorph(settle: Double, smallRatio: Double) -> (bigScale: Double, bigOpacity: Double, smallOpacity: Double) {
        let shrink = Easing.smoothstep(settle / 0.7)
        let swap = Easing.smoothstep((settle - 0.7) / 0.25)
        return (1 - (1 - smallRatio) * shrink, 1 - swap, swap)
    }
}

extension CGRect { fileprivate var center: CGPoint { CGPoint(x: midX, y: midY) } }

struct StageLayout: Layout {
    var place: ([CGSize], CGRect) -> [CGPoint]
    /// The room the words need, from their measured sizes, when it may be
    /// more than the stage: the layout then reports it, for the caller to
    /// scale down to fit. Nil takes the stage as offered.
    var needs: (([CGSize]) -> CGSize)? = nil

    init(place: @escaping ([CGSize], CGRect) -> [CGPoint]) { self.place = place }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let offered = CGSize(width: proposal.width ?? 400, height: proposal.height ?? 300)
        guard let needs else { return offered }
        let need = needs(subviews.map { $0.sizeThatFits(.unspecified) })
        return CGSize(width: max(offered.width, need.width), height: max(offered.height, need.height))
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
    /// "Below lyrics" readings by word (see `LyricReadingPairs`).
    var readings: [TimedWordKey: String] = [:]

    /// The shared settings in the app; tests pass an isolated instance.
    @ObservedObject var settings: Settings = .shared
    @Environment(\.lyricInk) private var ink
    @Environment(\.lyricReduceMotion) private var reduceMotion

    // MARK: Shared

    private var unit: CGFloat { max(16, baseSize) }

    /// 0...1 as a word arrives, a little overshooting (`StageMotion.arrival`).
    fileprivate func arrive(_ run: StageRun, speed: Double) -> Double {
        StageMotion.arrival(run.start, at: time, speed: speed, reduceMotion: reduceMotion)
    }

    /// One onset's share of a rank or camera move (`StageMotion.handoff`).
    fileprivate func handoff(_ run: StageRun, speed: Double) -> Double {
        Self.handoff(run, at: time, speed: speed, seed: seed, reduceMotion: reduceMotion)
    }

    static func handoff(_ run: StageRun, at time: Double, speed: Double, seed: Int, reduceMotion: Bool) -> Double {
        StageMotion.handoff(run.start, at: time, speed: speed, seed: UInt64(abs(seed &+ run.id &* 7919)),
                            reduceMotion: reduceMotion)
    }

    /// How far each word has been pushed back by the words after it: the sum
    /// of their hand-offs, so it carries its speed from one onset to the next.
    fileprivate func ranks(_ runs: [StageRun], speed: Double) -> [Double] {
        Self.ranks(runs, at: time, speed: speed, seed: seed, reduceMotion: reduceMotion)
    }

    static func ranks(_ runs: [StageRun], at time: Double, speed: Double, seed: Int, reduceMotion: Bool) -> [Double] {
        var after = 0.0
        var result = [Double](repeating: 0, count: runs.count)
        for index in runs.indices.reversed() {
            result[index] = after
            after += handoff(runs[index], at: time, speed: speed, seed: seed, reduceMotion: reduceMotion)
        }
        return result
    }

    /// The text of a word in `colour`, its sung syllables fading in.
    fileprivate func word(_ run: StageRun, _ colour: Color, uppercase: Bool = false) -> Text {
        Text(run.revealed(at: time, uppercase: uppercase, colour: colour)).foregroundStyle(colour)
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

    // MARK: Readings
    //
    // "Below lyrics": a word's reading sits under it, part of the word's own
    // view, so it arrives, scales, tilts and leaves with the word, and the
    // layouts measure and fit it with the word. Set smaller, in the same
    // face, in the reading colour; where a word sits on a bar or a pill the
    // reading is in the bar's text colour, as on the rolling layouts' tiles.

    /// A reading's size against its word's: between Fable's (0.36) and the
    /// rolling layouts' (0.56), as these layouts set their words larger.
    static let readingRatio: CGFloat = 0.45

    /// The type size of the reading under a word set at `size`: never below
    /// 12 pt, unless the word itself is set about that small.
    static func readingSize(_ size: CGFloat) -> CGFloat { max(size * readingRatio, min(12, size * 0.9)) }

    /// The gap between a word and its reading.
    static func readingGap(_ size: CGFloat) -> CGFloat { size * 0.04 }

    /// The room a reading takes under a word set at `size`: the gap and one
    /// line, held to this height so the layouts can count on it.
    static func readingSlot(_ size: CGFloat, gap: CGFloat? = nil) -> CGFloat {
        (gap ?? readingGap(size)) + readingSize(size) * 1.25
    }

    /// The reading drawn under `run`: its parts' shares written together
    /// (each carries the space before it where a phrase starts, see
    /// `LyricReadingPairs.parts`), or nil when it has none (or "Below
    /// lyrics" is off, so there are none).
    static func reading(of run: StageRun, in readings: [TimedWordKey: String]) -> String? {
        guard !readings.isEmpty else { return nil }
        let text = run.parts.compactMap { $0.source.flatMap { readings[$0] } }.joined()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? nil : text
    }

    /// The reading of `run` with each part's share showing as that part is
    /// sung, fading in with it (`StageRun.partOpacity`); a share not yet sung
    /// is invisible but keeps its room, so the reading never shifts as it
    /// fills in. Nil when the run has no reading.
    static func revealedReading(of run: StageRun, in readings: [TimedWordKey: String], at time: Double,
                                colour: Color) -> AttributedString? {
        guard reading(of: run, in: readings) != nil else { return nil }
        var result = AttributedString()
        var started = false
        for (index, part) in run.parts.enumerated() {
            guard var share = part.source.flatMap({ readings[$0] }), !share.isEmpty else { continue }
            if !started { share = String(share.drop(while: \.isWhitespace)); started = true }
            var piece = AttributedString(share)
            let shown = StageRun.partOpacity(index, of: run.parts, at: time)
            piece.foregroundColor = shown <= 0 ? .clear : colour.opacity(min(1, shown))
            result.append(piece)
        }
        return result
    }

    /// `word` with its reading under it, or `word` alone when it has none.
    /// The reading is `colour`, or, given `live`, blends from `colour` to
    /// `liveColour` as the word being sung does. `maxWidth` is the word's
    /// own limit; a long reading shrinks to it rather than spill. `shown`
    /// fades the reading alone, keeping its room.
    @ViewBuilder
    fileprivate func withReading<Word: View>(_ run: StageRun, size: CGFloat, colour: Color, liveColour: Color? = nil,
                                             live: Double = 0, alignment: HorizontalAlignment = .center,
                                             gap: CGFloat? = nil, maxWidth: CGFloat, shadow: Bool = true,
                                             shown: Double = 1, @ViewBuilder word: () -> Word) -> some View {
        if let text = Self.reading(of: run, in: readings) {
            let face = readingFont(size, id: run.id)
            let line = { (colour: Color) in
                Text(Self.revealedReading(of: run, in: readings, at: time, colour: colour) ?? AttributedString(text))
                    .font(face).foregroundStyle(colour)
                    .lineLimit(1).minimumScaleFactor(0.5)
            }
            VStack(alignment: alignment, spacing: gap ?? Self.readingGap(size)) {
                word()
                ZStack {
                    if let liveColour {
                        if live < 0.999 { line(colour) }
                        if live > 0.001 { line(liveColour).opacity(live) }
                    } else {
                        line(colour)
                    }
                }
                .frame(maxWidth: maxWidth)
                .frame(height: Self.readingSize(size) * 1.25)
                .shadow(color: .black.opacity(shadow ? 0.35 : 0), radius: max(2, Self.readingSize(size) * 0.18), y: 1)
                .opacity(shown)
            }
        } else {
            word()
        }
    }

    /// The reading's face: the word's own (editorial or not), not bold.
    private func readingFont(_ size: CGFloat, id: Int) -> Font {
        let alternate = settings.editorialTypography && chance(id, 60) < 0.34 && chance(id, 61) < settings.editorialAmount
        return settings.lyricFont(size: Self.readingSize(size), hero: false, mood: mood, seed: UInt64(abs(seed &+ id)),
                                  alternate: alternate)
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

    /// The whole line leaving, over the same settle as every other line exit.
    private var leave: Double { StageMotion.leave(at: time, exitStart: exitStart) }

    /// The runs that have arrived: always the first ones, in reading order,
    /// as the layouts read their arrays by `run.id`. A source whose word
    /// times run backwards made a filter skip an earlier run, and the next
    /// one's id then read past the end of those arrays (a crash).
    static func shownRuns(_ runs: [StageRun], at time: Double) -> [StageRun] {
        Array(runs.prefix { time >= $0.start - StageMotion.lead })
    }

    /// Under Reduce Motion, how far the stage has dissolved from the step
    /// before the newest word to this one; nil when there is nothing to
    /// dissolve. Fable never moves its words, so it needs none.
    static func reducedDissolve(_ shown: [StageRun], composition: LyricComposition, at time: Double,
                                reduceMotion: Bool) -> Double? {
        guard reduceMotion, composition != .fable, let newest = shown.last else { return nil }
        let step = StageMotion.reducedStep(since: newest.start, at: time)
        return step < 1 ? step : nil
    }

    var body: some View {
        if firstRunOnly == nil {
            let runs = StageRun.make(words, glue: glue)
            let shown = Self.shownRuns(runs, at: time)
            Group {
                // Reduce Motion: every layout step is a short dissolve between
                // where the words were and where they now are, not a journey.
                // The curves keep the sum of the two near 1 where nothing moved.
                if let step = Self.reducedDissolve(shown, composition: composition, at: time, reduceMotion: reduceMotion) {
                    ZStack {
                        layout(runs, Array(shown.dropLast())).opacity(1 - step * step)
                        layout(runs, shown).opacity(1 - (1 - step) * (1 - step))
                    }
                } else {
                    layout(runs, shown)
                }
            }
            .frame(width: stageSize.width, height: stageSize.height)
        } else {
            Color.clear.frame(width: stageSize.width, height: stageSize.height)
        }
    }

    @ViewBuilder
    private func layout(_ runs: [StageRun], _ shown: [StageRun]) -> some View {
        switch composition {
        case .hinge: hinge(shown, total: runs.count)
        case .broadside: broadside(shown, total: runs.count)
        case .mosaic: mosaic(shown, total: runs.count)
        case .torque: torque(shown, total: runs.count)
        case .fable: fable(runs, total: runs.count)
        default: portal(shown, total: runs.count)
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

    /// A Hinge column word's type size (the column's j-th word, at rest).
    static func hingeColumnFont(_ j: Int, unit u: CGFloat) -> CGFloat { u * min(2.8, 1.2 + 0.24 * CGFloat(j)) }

    /// How much the whole Hinge corner is scaled to fit the stage, and how
    /// much its column has shrunk. Every word counts by its weight (how far
    /// it has arrived), so neither steps as a word is added.
    /// `readings` are the words' readings ("Below lyrics"), counted in the
    /// height and width they add, so a corner with them is shrunk to fit too.
    /// How much more room than its own height a reading takes in Hinge's
    /// column and pair, whose words overlap their neighbours' leading
    /// (and the column is drawn 10% taller than measured).
    static let hingeReadingRoom: CGFloat = 1.4

    /// The size Hinge's pair sizes its readings from: larger than the pair's
    /// own type, which is shown small once it is pushed into the corner.
    static func hingePairReadingBase(unit u: CGFloat) -> CGFloat { u * 0.95 * 1.4 }

    static func hingeFit(_ texts: [String], weights: [CGFloat], settle: Double, unit u: CGFloat,
                         stage: CGSize, readings: [String?] = []) -> (fit: CGFloat, shrink: CGFloat) {
        let columnCount = max(0, min(texts.count, weights.count) - 3)
        let columnWeights = (0..<columnCount).map { weights[$0 + 3] }
        let shrink = CGFloat(StageMotion.hingeColumnShrink(Double(columnWeights.reduce(0, +))))
        func reading(_ index: Int) -> String? { readings.indices.contains(index) ? readings[index] : nil }
        /// The room a word's reading adds below it, and the reading's width.
        func readingRoom(_ index: Int, font: CGFloat) -> (height: CGFloat, width: CGFloat) {
            guard let text = reading(index), !text.isEmpty else { return (0, 0) }
            return (readingSlot(font), CGFloat(text.count) * readingSize(font) * 0.55)
        }
        let heightSum = (0..<columnCount).map { j -> CGFloat in
            let font = hingeColumnFont(j, unit: u)
            return (font * 0.95 + readingRoom(j + 3, font: font).height * hingeReadingRoom) * shrink * columnWeights[j]
        }.reduce(0, +)
        // The whole corner grows to fill the stage once it has a bar to hang from.
        let barReading = texts.count > 2 ? readingRoom(2, font: u * 1.6) : (height: 0, width: 0)
        let barWidth: CGFloat = texts.count > 2 ? max(CGFloat(texts[2].count) * u * 1.7 * 0.66, barReading.width) : 0
        var columnWidth: CGFloat = 0
        for j in 0..<columnCount {
            let font = hingeColumnFont(j, unit: u)
            let word = CGFloat(texts[j + 3].count) * font * 0.66
            let width = max(word, readingRoom(j + 3, font: font).width) * shrink * 0.84 * columnWeights[j]
            columnWidth = max(columnWidth, width)
        }
        let widest: CGFloat = max(barWidth, columnWidth)
        let structureWidth: CGFloat = max(2 * widest + u * 0.3, u * 3)
        // The pair's readings stack above the bar, with the pair at its
        // corner size (a little over half its own).
        let pairReadings = (0..<min(2, texts.count)).map {
            readingRoom($0, font: hingePairReadingBase(unit: u)).height * hingeReadingRoom * 0.6
        }.reduce(0, +)
        let structureHeight = max(heightSum, u * 2.4 + barReading.height) + u * 0.8 + pairReadings
        let fill: CGFloat = min(2.0, stage.width * 0.8 / structureWidth, stage.height * 0.78 / structureHeight)
        let fitHeight: CGFloat = min(1, stage.height * 0.74 / max(heightSum + u * 0.8 + pairReadings, 1))
        let grow: CGFloat = (max(fill, 1) - 1) * CGFloat(settle)
        return (fitHeight * (1 + grow), shrink)
    }

    private func hinge(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let arrivals = runs.map { arrive($0, speed: speed) }
        // Each word takes its room as it arrives, so nothing jumps at an onset.
        let weights = arrivals.map { CGFloat(StageMotion.weight($0)) }
        let barStart = runs.count > 2 ? runs[2].start : .infinity
        // How far the bar has settled from huge to its place, and how far the
        // pair has been pushed to the corner.
        let hasBar = runs.count > 2
        let settle = reduceMotion ? (hasBar ? 1 : 0) : Easing.smoothstep((time - barStart - 0.25) / 0.5)
        let pushed = !hasBar ? 0 : reduceMotion ? 1 : Easing.smoothstep((time - barStart + 0.05) / 0.4)
        let columnCount = max(0, runs.count - 3)
        let columnWeights = (0..<columnCount).map { weights[$0 + 3] }
        let u = unit
        let (fit, shrink) = Self.hingeFit(runs.map(\.text), weights: weights, settle: settle, unit: u, stage: stageSize,
                                          readings: runs.map { Self.reading(of: $0, in: readings) })
        // Type is set at its resting size and scaled, so no word is
        // re-shaped every frame; the layout scales what it measured to match.
        func columnFont(_ j: Int) -> CGFloat { Self.hingeColumnFont(j, unit: u) }
        func columnSize(_ j: Int) -> CGFloat { columnFont(j) * shrink }
        func columnScale(_ j: Int) -> CGFloat { shrink * (1 + 0.3 * (1 - CGFloat(arrivals[j + 3]))) }
        let pairWeight = runs.count > 1 ? weights[1] : 0
        let pairScale = (1 + (1.3 / 0.95 - 1) * (1 - pairWeight)) * (1 - 0.45 * CGFloat(pushed))
        let barScale = (3.4 - 1.8 * CGFloat(settle)) / 1.6
        let firstColumn = columnCount > 0 ? columnWeights[0] : 0
        // The faces swing shut as the words fold away, all within the line's exit.
        let close = reduceMotion ? 0 : vc_settle(time - exitStart, StageMotion.hingeClose)
        let faceAngle = reduceMotion ? 0.0 : 52 + (88 - 52) * close
        let leftRuns = runs.filter { $0.id < 3 }
        let rightRuns = runs.filter { $0.id >= 3 }
        let stage = stageSize
        // The room each word's reading takes under it, at the word's resting
        // size: the layout counts it apart from the word, which overlaps
        // its neighbours' leading where a reading cannot.
        let slots: [CGFloat] = runs.map { run in
            guard Self.reading(of: run, in: readings) != nil else { return 0 }
            return Self.readingSlot(run.id < 2 ? Self.hingePairReadingBase(unit: u)
                                    : run.id == 2 ? u * 1.6 : Self.hingeColumnFont(run.id - 3, unit: u))
        }
        let room = Self.hingeReadingRoom
        let face: (Bool) -> AnyView = { isLeft in
            let items = isLeft ? leftRuns : rightRuns
            return AnyView(StageLayout(place: { measured, bounds in
                let edge = bounds.midX
                let gap = u * 0.12
                if isLeft {
                    let barSize = measured.count > 2
                        ? CGSize(width: measured[2].width * barScale, height: measured[2].height * barScale) : .zero
                    let totalColumn = rightRuns.indices.map {
                        (columnSize($0) * 0.95 + slots[$0 + 3] * room * shrink) * columnWeights[$0]
                    }.reduce(0, +)
                    // The bar moves up beside the column as its first word arrives.
                    let top = bounds.midY - (u + (totalColumn / 2 - u) * firstColumn)
                    var centres = [CGPoint](repeating: CGPoint(x: edge / 2, y: bounds.midY), count: measured.count)
                    // The bar is placed by its word; its reading hangs below.
                    let barSlot = measured.count > 2 ? slots[2] * barScale : 0
                    let barWord = barSize.height - barSlot
                    if measured.count > 2 {
                        let settledX = edge - gap - barSize.width / 2
                        let settledY = bounds.midY + (top + barWord * 0.45 - bounds.midY) * firstColumn
                        centres[2] = CGPoint(x: edge / 2 + (settledX - edge / 2) * settle, y: bounds.midY + (settledY - bounds.midY) * settle)
                    }
                    // The pair's words overlap a little; their readings do not.
                    func pairRoom(_ index: Int) -> CGFloat {
                        guard measured.indices.contains(index) else { return pairRoom(0) }
                        return (measured[index].height - slots[index]) * 0.78 + slots[index] * room
                    }
                    let pairRead = slots.prefix(2).contains { $0 > 0 }
                    let pairHeight = measured.isEmpty ? 0 : (pairRead ? (pairRoom(0) + pairRoom(1)) / 2 : pairRoom(0)) * pairScale
                    let home = CGPoint(x: edge / 2, y: bounds.midY)
                    // A reading under the pair's lower word lifts the pair clear of the bar.
                    let lowerSlot = measured.count > 1 ? slots[1] * room * pairScale : 0
                    let corner = measured.count > 2
                        ? CGPoint(x: centres[2].x + barSize.width * 0.28, y: centres[2].y - barWord * 0.62 - lowerSlot) : home
                    let spot = CGPoint(x: home.x + (corner.x - home.x) * pushed, y: home.y + (corner.y - home.y) * pushed)
                    let split = pairHeight * 0.5 * pairWeight
                    if !measured.isEmpty { centres[0] = CGPoint(x: spot.x, y: spot.y - split) }
                    if measured.count > 1 { centres[1] = CGPoint(x: spot.x, y: spot.y + split * (1 - 0.25 * pushed)) }
                    if measured.count > 2 { centres[2].y += barSlot / 2 }
                    return centres
                }
                let heights = measured.indices.map {
                    ((measured[$0].height - slots[$0 + 3]) * 0.9 + slots[$0 + 3] * room) * columnScale($0) * columnWeights[$0]
                }
                var cursor = bounds.midY - heights.reduce(0, +) / 2
                return measured.indices.map { index in
                    defer { cursor += heights[index] }
                    return CGPoint(x: edge + gap + measured[index].width * columnScale(index) / 2, y: cursor + heights[index] / 2)
                }
            }) {
                ForEach(items) { run in
                    let sample = sample(run, of: total)
                    let a = arrivals[run.id]
                    let isPair = run.id < 2, isBar = run.id == 2
                    let fontSize: CGFloat = isPair ? u * 0.95 : isBar ? u * 1.6 : columnFont(run.id - 3)
                    let scale: CGFloat = isPair ? pairScale : isBar ? barScale : columnScale(run.id - 3)
                    let size = fontSize * scale
                    let tile = tiled(run)
                    // Words fold away oldest first.
                    let out = reduceMotion ? 0
                        : StageMotion.hingeFold(order: Double(run.id) / Double(max(total - 1, 1)), at: time, exitStart: exitStart)
                    let widest = stage.width * 0.4 / max(scale, 0.05)
                    // The reading hangs from the word's hinge side, as the word does.
                    // The bar arrives huge and settles into place; its reading
                    // comes once it is nearly there, rather than swing in huge.
                    withReading(run, size: isPair ? Self.hingePairReadingBase(unit: u) : fontSize,
                                colour: isPair ? sample.reading.opacity(0.95) : sample.reading,
                                alignment: isPair ? .center : isLeft ? .trailing : .leading, maxWidth: widest,
                                shown: isBar ? Easing.smoothstep((settle - 0.5) / 0.5) : 1) {
                        word(run, tile ? ink.tileText : isPair ? sample.rest.opacity(0.95) : sample.sung, uppercase: true)
                            .font(font(fontSize, hero: true, id: run.id))
                            .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: widest)
                            .background { tileBackground(tile, size: fontSize) }
                    }
                        .shadow(color: .black.opacity(tile ? 0 : 0.3), radius: 7, y: 2)
                        .scaleEffect(x: scale * (isBar || isPair ? 1 : 0.86), y: scale * (isBar || isPair ? 1 : 1.1))
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
        // Reduce Motion: nothing folds, so the line fades out as every other line does.
        .opacity(reduceMotion ? 1 - leave : 1)
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

    /// Where Broadside puts each word's centre: big words in a row, the
    /// strip glided so the newest big word reaches the centre; a little
    /// word lies across the big one before it. `slots` is the room each
    /// big word's reading takes under it (0 for none): the word itself is
    /// what sits on the strip's line, its reading below.
    static func broadsidePlaces(_ measured: [CGSize], little: [Bool], slots: [CGFloat], moves: [Double], unit: CGFloat,
                                in bounds: CGRect) -> [CGPoint] {
        func slot(_ index: Int) -> CGFloat { slots.indices.contains(index) ? slots[index] : 0 }
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
                ys[index] = bounds.midY + (measured[prior].height - slot(prior)) * 0.2
                littlesSinceBig += 1
                continue
            }
            let width = measured[index].width
            xs[index] = cursor + width / 2
            ys[index] = bounds.midY + slot(index) / 2
            cursor += width + (little[index] ? unit * 0.2 : unit * 0.45)
            if little[index] { littlesSinceBig += 1 } else { previousBig = index; bigOrder.append(index); littlesSinceBig = 0 }
        }
        // The strip glides so the newest big word reaches the centre.
        var camera = bigOrder.first.map { xs[$0] } ?? 0
        for (n, index) in bigOrder.enumerated() where n > 0 {
            camera += moves[index] * (xs[index] - xs[bigOrder[n - 1]])
        }
        return measured.indices.map { CGPoint(x: xs[$0] - camera + bounds.midX, y: ys[$0]) }
    }

    private func broadside(_ runs: [StageRun], total: Int) -> some View {
        // Snappy: each word springs in, and the strip glides along with it;
        // the glide hands over from word to word without a jolt.
        let speed = lineSpeed
        let arrivals = runs.map { arrive($0, speed: speed) }
        let moves = runs.map { handoff($0, speed: speed) }
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
            ((anchor[index] + 1)..<max(runs.count, anchor[index] + 1)).filter { !little[$0] }.map { moves[$0] }.reduce(0, +)
        }
        let keep: [Double] = bigAfter.map { max(0, min(1, 1 - ($0 - 1.6) * 0.9)) }
        // A big word's reading hangs under it, in a slot of known height,
        // so the words themselves stay on one line across the strip.
        let slots: [CGFloat] = runs.map {
            !little[$0.id] && Self.reading(of: $0, in: readings) != nil ? Self.readingSlot(bigSize) : 0
        }
        let u = unit
        return StageLayout(place: { measured, bounds in
            Self.broadsidePlaces(measured, little: little, slots: slots, moves: moves, unit: u, in: bounds)
        }) {
            ForEach(runs) { run in
                let sample = sample(run, of: total)
                let a = arrivals[run.id]
                if little[run.id] {
                    // With "Cutout · Highlight" the labels grow into real tiles.
                    let labelSize = tiled(run) ? smallSize * 1.4 : smallSize
                    // A label's reading is printed on the label with it.
                    withReading(run, size: labelSize, colour: ink.tileText.opacity(LyricHighlight.readingOpacity),
                                maxWidth: stageSize.width * 0.7, shadow: false) {
                        word(run, ink.tileText)
                            .font(font(labelSize, hero: true, id: run.id))
                            .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7)
                    }
                        .padding(.horizontal, labelSize * 0.34).padding(.vertical, labelSize * 0.16)
                        .background(ink.tileColour, in: RoundedRectangle(cornerRadius: 3))
                        .rotationEffect(.degrees(run.id.isMultiple(of: 2) ? -3 : 2.5))
                        .scaleEffect(reduceMotion ? 1 : 0.8 + 0.2 * a)
                        .opacity(min(1, a * 1.6) * keep[run.id])
                } else {
                    let tile = tiled(run)
                    withReading(run, size: bigSize, colour: sample.reading, maxWidth: stageSize.width * 0.7) {
                        ZStack {
                            word(run, .black.opacity(tile ? 0 : 0.34)).offset(x: hard, y: hard * 1.15)
                            word(run, tile ? ink.tileText : sample.sung)
                        }
                        .font(font(bigSize, hero: true, id: run.id))
                        .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stageSize.width * 0.7)
                        .background { tileBackground(tile, size: bigSize) }
                    }
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

    private func portal(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let arrivals = runs.map { arrive($0, speed: speed) }
        // Smootherstep starts and ends at rest, so the trail's sum of them
        // never jolts; under Reduce Motion each step is a dissolve instead.
        let settled = runs.map { reduceMotion ? 1 : Self.glide((time - ($0.start - 0.05)) / (0.6 / speed)) }
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
        let drift: CGFloat = reduceMotion ? 0 : CGFloat(1 / speed)
        // Older pills are shown smaller (the same scale as below).
        let scales = ranks.map { Self.portalScale(rank: $0) }
        return StageLayout(place: { measured, bounds in
            measured.indices.map { index -> CGPoint in
                let p = along(ranks[index])
                let bobRate: Double = 1.2 + 0.6 * chance(index, 7)
                let bobPhase: Double = time * bobRate + Double(index) * 1.3
                let bob: CGFloat = CGFloat(sin(bobPhase)) * unit * 0.07 * drift
                let swayRate: Double = 0.9 + 0.5 * chance(index, 8)
                let swayPhase: Double = time * swayRate + Double(index) * 0.9
                let sway: CGFloat = CGFloat(cos(swayPhase)) * unit * 0.05 * drift
                // Each pill comes in from its own direction.
                let heading = chance(index, 9) * 2 * .pi
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
                let tilt = reduceMotion ? 0 : ((1 - eased) * tiltAmount * tiltSign + sin(time * 1.1 + Double(run.id)) * 1.6) / speed
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
                            ForEach(0..<2, id: \.self) { pass in
                                SketchyOutline(corner: corner * 1.15, amount: size * 0.035, phase: boil + Double(pass) * 2.3,
                                               salt: Double(run.id * 3 + pass))
                                    .stroke(ink.tileColour.opacity(pass == 0 ? 0.75 : 0.4),
                                            style: StrokeStyle(lineWidth: max(1.2, size * 0.026), lineCap: .round, lineJoin: .round))
                                    .scaleEffect(1.08 + 0.05 * Double(pass))
                                    .rotationEffect(.degrees((pass == 0 ? -1.6 : 1.4) * (reduceMotion ? 0 : 1)))
                                    .opacity(eased * live)
                            }
                        }
                        // The resting pill, and the live one fading in over it.
                        if live < 0.999 {
                            RoundedRectangle(cornerRadius: corner, style: .continuous)
                                .fill(tile ? ink.tileColour : sample.rest.opacity(0.16))
                                .opacity(outlined ? 1 - live : 1)
                        }
                        if live > 0.001 {
                            if outlined {
                                RoundedRectangle(cornerRadius: corner, style: .continuous)
                                    .strokeBorder(ink.tileColour, lineWidth: max(2, size * 0.07))
                                    .opacity(live)
                            } else {
                                RoundedRectangle(cornerRadius: corner, style: .continuous)
                                    .fill(ink.tileColour)
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
                .opacity(eased * max(0, 1 - max(0, rank - (speed > 1.6 ? 1.1 : 1.6)) * 0.7) * (0.85 + 0.15 * live))
            }
        }
        .offset(y: reduceMotion ? 0 : -stageSize.height * 0.1 * leaving)
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

    /// The size Mosaic's small stack sets its words at.
    static func mosaicSmallSize(unit: CGFloat) -> CGFloat { unit * 0.6 }

    /// How far apart Mosaic's stack sets its rows: a word, and its reading
    /// when the line has readings.
    static func mosaicStackStep(unit: CGFloat, readings: Bool) -> CGFloat {
        unit * 0.85 + (readings ? readingSlot(mosaicSmallSize(unit: unit)) : 0)
    }

    fileprivate func mosaic(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let arrivals = runs.map { arrive($0, speed: speed) }
        let ranks = ranks(runs, speed: speed)
        let leaving = leave
        // A word on a tile (Cutout · Highlight) is always the huge kind, which can carry one.
        let kinds = runs.map { tiled($0) ? 0 : Int(chance($0.id, 20) * 3) % 3 }
        let stage = stageSize
        let u = unit
        // The stack's rows make room for readings when the line has any.
        let step = Self.mosaicStackStep(unit: u, readings: runs.contains { Self.reading(of: $0, in: readings) != nil })
        return StageLayout(place: { measured, bounds in
            measured.indices.map { index in
                // One continuous path: the word sits where it landed, then glides into the stack.
                let rank = ranks[index]
                let settle = CGFloat(Self.glide(min(rank, 1.2) / 1.2))
                let sway = (chance(index, 21) - 0.5) * 0.24 * bounds.width
                let home = CGPoint(x: bounds.midX + sway, y: bounds.midY + (chance(index, 22) - 0.5) * 0.16 * bounds.height)
                let slot = CGFloat(min(3, max(rank, 1)))
                let stack = CGPoint(x: bounds.midX - bounds.width * 0.2 + CGFloat(index % 2) * bounds.width * 0.04,
                                    y: bounds.minY + bounds.height * 0.12 + (slot - 1) * step)
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
                let bigSize: CGFloat = kinds[run.id] == 0 ? 3.0 : kinds[run.id] == 1 ? 2.3 : 2.4
                let morph = StageMotion.mosaicMorph(settle: settle, smallRatio: 0.6 / bigSize)
                ZStack {
                    // The big setting, shrinking and dimming as the next word takes over.
                    Group {
                        if kinds[run.id] == 0 {
                            // Huge, running off the edge, with a star beside it.
                            let size = unit * 3.0
                            ZStack(alignment: .topTrailing) {
                                withReading(run, size: size, colour: sample.reading, maxWidth: stage.width * 1.1) {
                                    word(run, tile ? ink.tileText : sample.sung)
                                        .font(font(size, hero: true, id: run.id))
                                        .lineLimit(1).minimumScaleFactor(0.5).frame(maxWidth: stage.width * 1.1)
                                        .background { tileBackground(tile, size: size) }
                                        .shadow(color: .black.opacity(0.28), radius: 8, y: 3)
                                }
                                Star4().fill(ink.tileColour).frame(width: size * 0.32, height: size * 0.32)
                                    .rotationEffect(.degrees(reduceMotion ? 0 : time * 40))
                                    .offset(x: size * 0.1, y: -size * 0.12)
                            }
                            .offset(x: reduceMotion ? 0 : (1 - min(a, 1)) * stage.width * 0.35 * side)
                        } else if kinds[run.id] == 1 {
                            // White on a colour bar, laid across a pale panel.
                            let size = unit * 2.3
                            let wipe = reduceMotion ? 1 : Self.glide((time - (run.start - 0.04)) / (0.32 / lineSpeed))
                            let plain = Text(run.revealed(at: time))
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
                                    .scaleEffect(x: reduceMotion ? 1 : 0.6 + 0.4 * Self.glide((time - (run.start + 0.05)) / 0.3), y: 1)
                                // The bar wipes across, then the word lands on it; a
                                // reading is printed on the bar under its word.
                                let readingInk = ink.tileText.opacity(LyricHighlight.readingOpacity)
                                withReading(run, size: size, colour: readingInk, maxWidth: stage.width * 0.7, shadow: false) { plain }
                                    .hidden()
                                    .padding(.horizontal, size * 0.28).padding(.vertical, size * 0.06)
                                    .background {
                                        ink.tileColour
                                            .mask(alignment: side > 0 ? .leading : .trailing) {
                                                GeometryReader { box in Rectangle().frame(width: box.size.width * wipe) }
                                            }
                                    }
                                    .rotationEffect(.degrees(tilt))
                                withReading(run, size: size, colour: readingInk, maxWidth: stage.width * 0.7, shadow: false) {
                                    word(run, ink.tileText)
                                        .font(font(size, hero: true, id: run.id))
                                        .lineLimit(1).minimumScaleFactor(0.45).frame(maxWidth: stage.width * 0.7)
                                }
                                    .rotationEffect(.degrees(tilt))
                                    .opacity(Self.glide((time - (run.start + 0.1)) / 0.2))
                                    .offset(x: reduceMotion ? 0 : CGFloat(1 - wipe) * size * 0.5 * side)
                                Star4().fill(Color.white).frame(width: size * 0.3, height: size * 0.3)
                                    .shadow(color: .black.opacity(0.2), radius: 3)
                                    .rotationEffect(.degrees(reduceMotion ? 0 : time * 50))
                                    // Above the word, which a reading under it lifts.
                                    .offset(x: side * size * 0.1, y: -size * 0.62
                                            - (Self.reading(of: run, in: readings) == nil ? 0 : Self.readingSlot(size) / 2))
                                    .scaleEffect(wipe)
                            }
                            .shadow(color: .black.opacity(0.25), radius: 8, y: 3)
                        } else {
                            // Letter by letter, each on its own baseline.
                            let size = unit * 2.4
                            withReading(run, size: size, colour: sample.reading, maxWidth: stage.width * 0.8) {
                            HStack(spacing: size * 0.02) {
                                ForEach(Array(run.text.enumerated()), id: \.offset) { index, character in
                                    let reveal = Self.glide((time - (run.start + Double(index) * 0.05)) / 0.25)
                                    Text(String(character))
                                        .font(font(size * (0.85 + 0.3 * CGFloat(chance(run.id * 31 + index, 24))), hero: true, id: run.id))
                                        .foregroundStyle(sample.sung)
                                        .offset(y: CGFloat(chance(run.id * 31 + index, 25) - 0.5) * size * 0.28
                                                + (reduceMotion ? 0 : CGFloat(1 - reveal) * size * 0.3))
                                        .rotationEffect(.degrees((chance(run.id * 31 + index, 26) - 0.5) * 22))
                                        .opacity(reveal)
                                }
                            }
                            .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: stage.width * 0.8)
                            }
                            .shadow(color: .black.opacity(0.28), radius: 8, y: 3)
                        }
                    }
                    .scaleEffect(morph.bigScale)
                    .opacity(min(1, a * 1.6) * morph.bigOpacity)
                    // The small version it becomes, taking over once the big one has
                    // shrunk to its size.
                    let fade = max(0, 1 - (rank - 1.5) * 0.6)
                    let small = Self.mosaicSmallSize(unit: unit)
                    withReading(run, size: small, colour: sample.reading.opacity(fade), maxWidth: stage.width * 0.4) {
                        word(run, sample.rest.opacity(0.75 * fade))
                            .font(font(small, hero: true, id: run.id))
                            .lineLimit(1).minimumScaleFactor(0.4).frame(maxWidth: stage.width * 0.4)
                    }
                        .opacity(morph.smallOpacity)
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

    /// Torque's state for every shown word at one moment.
    struct TorqueState {
        var arrivals: [Double]
        var ranks: [Double]
        /// Shown size over the resting (newest word's) size.
        var scales: [CGFloat]
        /// The share of its room each word takes in the stack.
        var weights: [CGFloat]
        /// The room each word's reading takes under it, at the resting size
        /// (0 for none; `torqueReadingSlot`).
        var slots: [CGFloat] = []
    }

    /// Torque's words rest at this many units.
    static func torqueRestSize(unit: CGFloat) -> CGFloat { unit * 2.0 }

    /// The gap between a Torque word and its reading: clear of the thick edge.
    static func torqueReadingGap(_ restSize: CGFloat) -> CGFloat { restSize * 0.16 }

    /// The room a reading takes under a Torque word.
    static func torqueReadingSlot(_ restSize: CGFloat) -> CGFloat {
        readingSlot(restSize, gap: torqueReadingGap(restSize))
    }

    static func torqueState(_ runs: [StageRun], at time: Double, speed: Double, seed: Int,
                            reduceMotion: Bool) -> TorqueState {
        let arrivals = runs.map { StageMotion.arrival($0.start, at: time, speed: speed, reduceMotion: reduceMotion) }
        let ranks = ranks(runs, at: time, speed: speed, seed: seed, reduceMotion: reduceMotion)
        // Set at the newest word's size; older words are scaled down, never re-set.
        let scales = ranks.map { CGFloat(max(0.55, 2.0 - 0.38 * min($0, 3.5)) / 2.0) }
        // The room each word takes: it grows as the word arrives, and is given
        // back as the word fades out at the top, so the stack neither jumps at
        // an onset nor sinks off the stage on a long line.
        let weights = runs.indices.map {
            CGFloat(StageMotion.weight(arrivals[$0]) * StageMotion.torqueVisibility(rank: ranks[$0]))
        }
        return TorqueState(arrivals: arrivals, ranks: ranks, scales: scales, weights: weights)
    }

    /// Where Torque puts each word's centre, from the sizes measured at the
    /// resting size.
    /// The room each word takes in Torque's stack. Words overlap their
    /// neighbours' leading; a reading takes all its room and a little more,
    /// as the stack sways and turns.
    static func torqueHeights(_ measured: [CGSize], _ state: TorqueState) -> [CGFloat] {
        func slot(_ index: Int) -> CGFloat { state.slots.indices.contains(index) ? state.slots[index] : 0 }
        return measured.indices.map {
            ((measured[$0].height - slot($0)) * 0.82 + slot($0) * 1.2) * state.scales[$0] * state.weights[$0]
        }
    }

    /// The height Torque's stack needs: its words, and room at either end
    /// for the overlap and the turn. Taller than the stage (big type,
    /// readings), the stack is scaled down to fit.
    static func torqueHeight(_ measured: [CGSize], _ state: TorqueState, restSize: CGFloat) -> CGFloat {
        torqueHeights(measured, state).reduce(0, +) + restSize * 0.3
    }

    static func torqueStack(_ measured: [CGSize], _ state: TorqueState, seed: Int, in bounds: CGRect) -> [CGPoint] {
        let ranks = state.ranks
        // The stack is centred whatever its height.
        let heights = torqueHeights(measured, state)
        var cursor = bounds.midY + heights.reduce(0, +) / 2
        var placed = measured.indices.reversed().map { index -> (Int, CGPoint) in
            let rank = ranks[index]
            let height = heights[index]
            let y = cursor - height / 2
            cursor -= height
            return (index, CGPoint(x: bounds.midX + CGFloat(sin(Double(index) * 2.1 + Double(seed % 5))) * bounds.width * 0.05 * CGFloat(min(rank, 1)), y: y))
        }
        // The newest word is the biggest and sits lowest, so a stack centred
        // by height still looks bottom-heavy. Centre it by weight instead,
        // as far as the stage has room to move it. A stack taller than its
        // bounds keeps its foot in them: the word being sung stays whole,
        // and the oldest words, already fading, go off the top. (The view
        // gives the stack the room it needs and scales it to the stage.)
        let total = heights.reduce(0, +)
        let weight = placed.reduce(CGFloat(0)) { $0 + heights[$1.0] * heights[$1.0] }
        if total > 0, weight > 0 {
            let centroid = placed.reduce(CGFloat(0)) { $0 + heights[$1.0] * heights[$1.0] * $1.1.y } / weight
            let room = (bounds.height - total) / 2
            let shift = room >= 0 ? max(-room, min(room, bounds.midY - centroid)) : room
            placed = placed.map { ($0.0, CGPoint(x: $0.1.x, y: $0.1.y + shift)) }
        }
        return placed.sorted { $0.0 < $1.0 }.map(\.1)
    }

    fileprivate func torque(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let restSize = Self.torqueRestSize(unit: unit)
        var moment = Self.torqueState(runs, at: time, speed: speed, seed: seed, reduceMotion: reduceMotion)
        moment.slots = runs.map { Self.reading(of: $0, in: readings) != nil ? Self.torqueReadingSlot(restSize) : 0 }
        let state = moment
        let arrivals = state.arrivals, ranks = state.ranks, scales = state.scales
        let leaving = leave
        let stage = stageSize
        let seed = seed
        var stack = StageLayout(place: { measured, bounds in
            Self.torqueStack(measured, state, seed: seed, in: bounds)
        })
        stack.needs = { measured in CGSize(width: 0, height: Self.torqueHeight(measured, state, restSize: restSize)) }
        return stack {
            ForEach(runs) { run in
                let a = arrivals[run.id]
                let rank = ranks[run.id]
                let sample = sample(run, of: total)
                // 1 while this is the word being sung, easing to 0 as the next takes over.
                let live = StageMotion.liveness(rank: rank)
                let scale = scales[run.id]
                let size = restSize * scale
                let tile = tiled(run)
                // The word's own wringing, unless another word entrance has been chosen.
                let entrance = chosenEntrance(run, progress: a, size: size)
                let twist = reduceMotion || !entrance.native ? 0 : (1 - min(a, 1)) * 110 * (run.id.isMultiple(of: 2) ? 1 : -1)
                // Wrung and loosened again, all the while it is held.
                let breathe = reduceMotion ? 0 : sin(time * 2.1 + Double(run.id) * 1.7) * (5 + 6 * live)
                let wander = reduceMotion ? 0 : CGFloat(sin(time * 1.3 + Double(run.id) * 2.3)) * unit * CGFloat(0.08 + 0.06 * live)
                let lean = min(rank, 2) * 3 * (run.id.isMultiple(of: 2) ? 1 : -1)
                let restColour = tile ? ink.tileText : sample.rest.opacity(0.85)
                let liveColour = tile ? ink.tileText : sample.sung
                // The reading takes the sung colour while its word is the one
                // being sung, then settles back to the reading colour. It is
                // set clear of the thick edge below the word.
                withReading(run, size: restSize, colour: sample.reading,
                            liveColour: (tile ? sample.reading : sample.sung).opacity(LyricHighlight.readingOpacity),
                            live: live, gap: Self.torqueReadingGap(restSize), maxWidth: stage.width * 0.7 / scale) {
                    ZStack {
                        // The thick edge: darker copies stepping back, thinning away
                        // as the word hands over.
                        if live > 0.001 {
                            ZStack {
                                ForEach(1..<7, id: \.self) { step in
                                    word(run, sample.sung.opacity(0.55))
                                        .offset(x: CGFloat(step) * restSize * 0.012, y: CGFloat(step) * restSize * 0.022)
                                        .brightness(-0.25)
                                }
                            }
                            .opacity(live)
                        }
                        ZStack {
                            if live < 0.999 { word(run, restColour) }
                            if live > 0.001 { word(run, liveColour).opacity(live) }
                        }
                        .background { tileBackground(tile, size: restSize) }
                    }
                    .font(font(restSize, hero: true, id: run.id))
                    .lineLimit(1).minimumScaleFactor(0.35).frame(maxWidth: stage.width * 0.7 / scale)
                }
                .scaleEffect(scale)
                .shadow(color: .black.opacity(0.15 + 0.15 * live), radius: 6, y: 3)
                .rotationEffect(.degrees(reduceMotion ? 0 : -4 * live + lean * (1 - live)))
                .rotation3DEffect(.degrees(twist + breathe), axis: (x: 0, y: 1, z: 0), perspective: 0.6)
                .rotation3DEffect(.degrees(breathe * 0.5), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
                .rotation3DEffect(.degrees(entrance.fold), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
                .rotationEffect(.degrees(entrance.spin))
                .scaleEffect(entrance.scale)
                .offset(x: wander + entrance.offset.width, y: entrance.offset.height)
                .blur(radius: reduceMotion ? 0 : size * 0.07 * (1 - min(a, 1)))
                .opacity(min(1, a * 1.6) * StageMotion.torqueVisibility(rank: rank))
            }
        }
        // A stack taller than the stage (`needs`) is shrunk to fit it.
        .visualEffect { stack, geometry in
            stack.scaleEffect(LyricStageFit.scale(content: geometry.size, available: stage))
        }
        .rotation3DEffect(.degrees(reduceMotion ? 0 : -10), axis: (x: 0, y: 1, z: 0), perspective: 0.5)
        .offset(y: reduceMotion ? 0 : stage.height * 0.12 * leaving)
        .opacity(1 - leaving)
    }

    // MARK: Fable
    //
    // Every line is set in its own shape -- a staircase, an arch, a zigzag, a
    // wave or a ramp -- and marks the word being sung with its own accent: an
    // underline, a ring, a tile, a star or corner brackets. Neighbouring lines
    // never share a shape or an accent, and a line always looks the same
    // (both come from the line, see `FableStyle`). The whole line is laid out
    // from its first word, so words land in their final places and nothing
    // reflows; each word makes a short entrance of its own and then holds
    // still. The block is fitted inside the stage, readings and all.

    fileprivate func fable(_ runs: [StageRun], total: Int) -> some View {
        let speed = lineSpeed
        let style = FableStyle.forLine(seed)
        let rightToLeft = LyricScript.lineIsRightToLeft(runs.map(\.text))
        let leaving = leave
        let stage = stageSize
        // Room kept round the block for shadows, accents and entrances.
        let margin = max(unit * 0.5, min(stage.width, stage.height) * 0.05)
        let available = CGSize(width: max(1, stage.width - 2 * margin), height: max(1, stage.height - 2 * margin))
        let hero = FableStyle.heroIndex(runs.map(\.text))
        // The word being sung: from its onset until the next word's.
        let current = runs.lastIndex { time >= $0.start - 0.04 }
        return FableLayout(shape: style.shape, rightToLeft: rightToLeft, available: available,
                           gap: unit * 0.08, rowGap: unit * 0.12) {
            ForEach(runs) { run in
                let index = run.id
                let size = fableSize(run, hero: hero == index, count: runs.count)
                // Fable's words never travel under Reduce Motion, so its arrival
                // only fades them in and stays smooth there.
                let a = StageMotion.arrival(run.start, at: time, speed: speed)
                let sungAt = run.start - 0.04
                let shown = time >= sungAt
                // How much of the accent this word wears: it comes with the word and
                // passes to the next one as that is sung. The last word keeps it.
                let next = runs.indices.contains(index + 1) ? runs[index + 1].start : .infinity
                let rise = Self.glide((time - sungAt) / 0.18)
                let fall = next.isFinite ? 1 - Self.glide((time - (next - 0.04)) / 0.3) : 1
                let wear = shown ? rise * fall : 0
                let entrance = chosenEntrance(run, progress: a, size: size)
                let kind = Int(chance(index, 73) * 4) % 4
                let motion: (offset: CGSize, scale: CGFloat, spin: Double) = reduceMotion || !entrance.native
                    ? (.zero, 1, 0) : FableStyle.entrance(kind, progress: a, size: size, side: chance(index, 74) < 0.5 ? -1 : 1)
                fableWord(run, size: size, style: style, wear: wear, rightToLeft: rightToLeft,
                          sample: sample(run, of: total))
                    .opacity(shown ? min(1, a * 1.8) * (0.8 + 0.2 * (index == current ? 1 : wear)) : 0)
                    .rotationEffect(.degrees(reduceMotion ? 0 : (chance(index, 72) - 0.5) * style.shape.tilt))
                    .rotation3DEffect(.degrees(entrance.fold), axis: (x: 1, y: 0, z: 0), perspective: 0.6)
                    .rotationEffect(.degrees(motion.spin + entrance.spin), anchor: .bottom)
                    .scaleEffect(motion.scale * entrance.scale)
                    .offset(x: motion.offset.width + entrance.offset.width, y: motion.offset.height + entrance.offset.height)
            }
        }
        // One shadow for the block, not one per word.
        .compositingGroup()
        .shadow(color: .black.opacity(0.3), radius: max(3, unit * 0.08), y: 2)
        // Long lines and big readings are shrunk to fit, never cut off.
        .visualEffect { block, geometry in
            block.scaleEffect(LyricStageFit.scale(content: geometry.size, available: available))
        }
        .offset(y: reduceMotion ? 0 : -stage.height * 0.04 * leaving)
        .opacity(1 - leaving)
        .frame(width: stage.width, height: stage.height)
    }

    /// A word's size, fixed for the whole line so nothing reflows: the
    /// longest word is set larger, the little ones a touch smaller.
    private func fableSize(_ run: StageRun, hero: Bool, count: Int) -> CGFloat {
        if count <= 2 || hero { return unit * 1.75 }
        if isLittle(run.text) { return unit * 1.05 }
        return unit * (1.28 + 0.14 * CGFloat(chance(run.id, 71)))
    }

    @ViewBuilder
    private func fableWord(_ run: StageRun, size: CGFloat, style: FableStyle, wear: Double, rightToLeft: Bool,
                           sample: LyricInkSample) -> some View {
        let tile = tiled(run)
        let onTile = style.accent == .tile && !tile
        let tight = LyricScript.keepsSyllables(run.text)
        // The parts' shares written together (joining them with spaces
        // read 思い出 as "omo i da"), filling in as the parts are sung.
        let reading = Self.revealedReading(of: run, in: readings, at: time, colour: sample.rest.opacity(0.9))
        let face = font(size, hero: true, id: run.id)
        let word = ZStack {
            self.word(run, tile ? ink.tileText : sample.sung).font(face)
                .opacity(onTile ? 1 - wear : 1)
            if onTile, wear > 0.001 {
                self.word(run, ink.tileText).font(face).opacity(wear)
            }
        }
        .lineLimit(1).fixedSize()
        .background { tileBackground(tile, size: size) }
        .background { if wear > 0.001 { fableAccent(style.accent, size: size, wear: wear, rightToLeft: rightToLeft) } }
        .overlay(alignment: rightToLeft ? .topLeading : .topTrailing) {
            if style.accent == .star, wear > 0.001 {
                Star4().fill(ink.tileColour)
                    .frame(width: size * 0.42, height: size * 0.42)
                    .rotationEffect(.degrees(reduceMotion ? 0 : (1 - wear) * 90))
                    .scaleEffect(wear)
                    // Within the word's own padding.
                    .offset(x: (rightToLeft ? -1 : 1) * size * 0.12, y: -size * 0.08)
            }
        }
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
        .padding(.horizontal, size * (tight ? 0.16 : 0.24))
        .padding(.vertical, size * 0.16)
    }

    @ViewBuilder
    private func fableAccent(_ accent: FableStyle.Accent, size: CGFloat, wear: Double, rightToLeft: Bool) -> some View {
        let colour = ink.tileColour
        let line = max(2, size * 0.05)
        switch accent {
        case .underline:
            Capsule().fill(colour)
                .frame(height: line * 1.4)
                .scaleEffect(x: reduceMotion ? 1 : wear, anchor: rightToLeft ? .trailing : .leading)
                .frame(maxHeight: .infinity, alignment: .bottom)
                .offset(y: size * 0.1)
                .opacity(wear)
        case .ring:
            Capsule().stroke(colour, lineWidth: line)
                .padding(.horizontal, -size * 0.18).padding(.vertical, -size * 0.1)
                .rotationEffect(.degrees(-3))
                .scaleEffect(reduceMotion ? 1 : 0.9 + 0.1 * wear)
                .opacity(wear * 0.95)
        case .tile:
            RoundedRectangle(cornerRadius: size * 0.14, style: .continuous).fill(colour)
                .padding(.horizontal, -size * 0.14).padding(.vertical, -size * 0.04)
                .opacity(wear)
        case .star:
            EmptyView()
        case .brackets:
            FableBrackets(arm: size * 0.22)
                .stroke(colour, style: StrokeStyle(lineWidth: line, lineCap: .round, lineJoin: .round))
                .padding(.horizontal, -size * 0.16).padding(.vertical, -size * 0.06)
                .scaleEffect(reduceMotion ? 1 : 1.15 - 0.15 * wear)
                .opacity(wear)
        }
    }

    /// What a word does when the viewer chose a particular entrance rather than
    /// leaving it to the layout. `native` means the layout's own motion stays.
    /// The chosen entrances move exactly as they do in every other layout
    /// (`WordEntrance.transform`), so "Float up" means the same thing everywhere.
    fileprivate func chosenEntrance(_ run: StageRun, progress a: Double, size: CGFloat)
        -> (native: Bool, offset: CGSize, scale: CGFloat, spin: Double, fold: Double) {
        let arrival = settings.arrival(in: composition, for: LyricsFetcher.Word(text: run.text, start: run.start, end: run.end))
        let moved = Self.chosenEntrance(arrival, progress: a, size: size, side: run.id.isMultiple(of: 2) ? 1 : -1,
                                        reduceMotion: reduceMotion)
        return moved.map { (false, $0.offset, $0.scale, $0.turn, $0.fold) }
            ?? (native: true, offset: .zero, scale: 1, spin: 0, fold: 0)
    }

    /// The pose a chosen entrance gives a word, or nil when the layout's own
    /// motion stays: the layout's default (Soft focus), the swipes, and
    /// everything under Reduce Motion.
    static func chosenEntrance(_ arrival: WordArrival, progress a: Double, size: CGFloat, side: Double,
                               reduceMotion: Bool) -> WordEntrance.Transform? {
        guard !reduceMotion else { return nil }
        switch arrival {
        case .lift, .pop, .orbit, .glide, .unfold, .cutoutFold, .cutoutSlide:
            return WordEntrance.transform(progress: min(max(a, 0), 1), arrival: a, style: arrival, size: size,
                                          direction: side, reduceMotion: false)
        default:
            return nil
        }
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

// MARK: - Fable's parts

/// One Fable line's look: the shape its words are set in and the accent that
/// marks the word being sung. Chosen by the line, so the same line looks the
/// same on every render, and arranged so neighbouring lines never share a
/// shape or an accent; all 25 pairs come round before any repeats.
struct FableStyle: Equatable {
    enum Shape: Int, CaseIterable {
        case staircase, arch, zigzag, wave, ramp

        /// How much of the stage's width a row may take before it wraps.
        var share: CGFloat {
            switch self {
            case .staircase: return 0.7
            case .arch: return 0.9
            case .zigzag: return 0.75
            case .wave: return 0.95
            case .ramp: return 0.85
            }
        }

        /// Most words in a row, so the shape still reads as one.
        var wordsPerRow: Int {
            switch self {
            case .staircase, .zigzag: return 3
            case .ramp: return 4
            case .arch: return 5
            case .wave: return 6
            }
        }

        /// The spread of each word's tilt, in degrees.
        var tilt: Double {
            switch self {
            case .arch: return 0
            case .ramp: return 2
            default: return 5
            }
        }
    }

    enum Accent: Int, CaseIterable {
        case underline, ring, tile, star, brackets
    }

    var shape: Shape
    var accent: Accent

    static func forLine(_ seed: Int) -> FableStyle {
        let n = Shape.allCases.count
        // Floor division, so negative seeds keep the pattern too.
        let lap = seed >= 0 ? seed / n : -((-seed + n - 1) / n)
        func wrap(_ value: Int) -> Int { ((value % n) + n) % n }
        // The accent moves on one more than the shape each lap, so the
        // pairs shift against each other instead of repeating every five lines.
        return FableStyle(shape: Shape(rawValue: wrap(seed)) ?? .staircase,
                          accent: Accent(rawValue: wrap(seed + lap) % Accent.allCases.count) ?? .underline)
    }

    /// The word set larger: the longest, the first of equals. None for a line
    /// of one or two words, which are all set large.
    static func heroIndex(_ texts: [String]) -> Int? {
        guard texts.count >= 3 else { return nil }
        var best = 0
        for (index, text) in texts.enumerated() where text.count > texts[best].count { best = index }
        return best
    }

    /// A word's own entrance (one of four), as `progress` runs 0...1 with a
    /// little overshoot: it drops in, pops up, swings up from its foot, or
    /// slides in from `side`.
    static func entrance(_ kind: Int, progress a: Double, size: CGFloat, side: CGFloat)
        -> (offset: CGSize, scale: CGFloat, spin: Double) {
        let left = CGFloat(1 - a)
        switch kind {
        case 0: return (CGSize(width: 0, height: -left * size * 0.5), 1, 0)
        case 1: return (.zero, 0.55 + 0.45 * CGFloat(a), 0)
        case 2: return (.zero, 1, Double(left) * -16 * Double(side))
        default: return (CGSize(width: side * left * size * 0.6, height: 0), 1, 0)
        }
    }
}

/// Lays a Fable line out in its shape: words in reading order, wrapped into
/// rows, each row bent into the shape, every word in a box of its own that
/// nothing else enters. Reports the block's own size; the caller scales it
/// to fit the stage.
struct FableLayout: Layout {
    var shape: FableStyle.Shape
    var rightToLeft: Bool
    var available: CGSize
    var gap: CGFloat
    var rowGap: CGFloat

    struct Arrangement: Equatable {
        var size: CGSize
        /// Each word's box, from the block's top-left corner.
        var frames: [CGRect]
        /// The scale the block will be shown at to fit `available`.
        var scale: CGFloat
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        arrangement(subviews).size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let sizes = subviews.map { $0.sizeThatFits(.unspecified) }
        let laid = Self.arrange(sizes, shape: shape, rightToLeft: rightToLeft, available: available, gap: gap, rowGap: rowGap)
        let origin = CGPoint(x: bounds.midX - laid.size.width / 2, y: bounds.midY - laid.size.height / 2)
        for (index, subview) in subviews.enumerated() where laid.frames.indices.contains(index) {
            let frame = laid.frames[index]
            subview.place(at: CGPoint(x: origin.x + frame.midX, y: origin.y + frame.midY), anchor: .center,
                          proposal: ProposedViewSize(sizes[index]))
        }
    }

    private func arrangement(_ subviews: Subviews) -> Arrangement {
        Self.arrange(subviews.map { $0.sizeThatFits(.unspecified) }, shape: shape, rightToLeft: rightToLeft,
                     available: available, gap: gap, rowGap: rowGap)
    }

    /// Tries a few row widths and keeps the one that can be shown largest.
    static func arrange(_ sizes: [CGSize], shape: FableStyle.Shape, rightToLeft: Bool, available: CGSize,
                        gap: CGFloat, rowGap: CGFloat) -> Arrangement {
        guard !sizes.isEmpty, available.width > 0, available.height > 0 else {
            return Arrangement(size: .zero, frames: sizes.map { _ in .zero }, scale: 1)
        }
        var best: Arrangement?
        for guess: CGFloat in [1, 0.9, 0.8, 0.7, 0.6, 0.5, 0.42, 0.35, 0.28] {
            var laid = build(sizes, shape: shape, wrap: available.width * shape.share / guess, gap: gap, rowGap: rowGap)
            laid.scale = LyricStageFit.scale(content: laid.size, available: available)
            if best == nil || laid.scale > best!.scale + 0.001 { best = laid }
            if laid.scale >= guess - 0.001 { break }
        }
        guard var result = best else { return Arrangement(size: .zero, frames: [], scale: 1) }
        if rightToLeft {
            // Right to left: the same shape seen in a mirror, read from the right.
            result.frames = result.frames.map { CGRect(x: result.size.width - $0.maxX, y: $0.minY, width: $0.width, height: $0.height) }
        }
        return result
    }

    private static func build(_ sizes: [CGSize], shape: FableStyle.Shape, wrap: CGFloat, gap: CGFloat,
                              rowGap: CGFloat) -> Arrangement {
        // Rows, by width and by count; a very long line allows more words a row
        // rather than more than about four rows.
        let cap = max(shape.wordsPerRow, Int((Double(sizes.count) / 4).rounded(.up)))
        var rows: [[Int]] = [[]]
        var width: CGFloat = 0
        for index in sizes.indices {
            let row = rows[rows.count - 1]
            let add = (row.isEmpty ? 0 : gap) + sizes[index].width
            if !row.isEmpty, width + add > wrap || row.count >= cap {
                rows.append([index]); width = sizes[index].width
            } else {
                rows[rows.count - 1].append(index); width += add
            }
        }
        // No lone word left at the end when the row above can spare one and
        // the two still fit the row width (a long last word may not).
        if rows.count > 1, rows[rows.count - 1].count == 1, rows[rows.count - 2].count >= 3,
           let spare = rows[rows.count - 2].last,
           sizes[spare].width + gap + sizes[rows[rows.count - 1][0]].width <= wrap {
            let moved = rows[rows.count - 2].removeLast()
            rows[rows.count - 1].insert(moved, at: 0)
        }
        let rowWidths = rows.map { row in row.map { sizes[$0].width }.reduce(0, +) + gap * CGFloat(max(0, row.count - 1)) }
        let widest = rowWidths.max() ?? 0
        var frames = [CGRect](repeating: .zero, count: sizes.count)
        var top: CGFloat = 0
        var order = 0
        for (r, row) in rows.enumerated() {
            let rowHeight = row.map { sizes[$0].height }.max() ?? 0
            let start: CGFloat
            switch shape {
            case .staircase: start = CGFloat(r) * rowHeight * 0.9
            case .zigzag: start = r.isMultiple(of: 2) ? 0 : max(widest - rowWidths[r], rowHeight * 0.9)
            default: start = (widest - rowWidths[r]) / 2
            }
            let middle = start + rowWidths[r] / 2
            let half = max(rowWidths[r] / 2, 1)
            var x = start
            var placed: [(Int, CGRect)] = []
            for (j, index) in row.enumerated() {
                let size = sizes[index]
                let centre = x + size.width / 2
                let u = min(1, max(-1, (centre - middle) / half))
                let dy: CGFloat
                switch shape {
                case .staircase: dy = CGFloat(j) * rowHeight * 0.24
                case .arch: dy = -rowHeight * 0.45 * (1 - u * u)
                case .zigzag: dy = row.count > 1 ? (j.isMultiple(of: 2) ? -1 : 1) * rowHeight * 0.16 : 0
                case .wave: dy = CGFloat(sin(Double(order) * 1.25 + 0.4)) * rowHeight * 0.2
                case .ramp: dy = -(u + 1) / 2 * rowHeight * 0.8
                }
                placed.append((index, CGRect(x: x, y: dy - size.height / 2, width: size.width, height: size.height)))
                x += size.width + gap
                order += 1
            }
            // Each row starts below the lowest point of the one before.
            let rowTop = placed.map(\.1.minY).min() ?? 0
            let shift = top - rowTop
            for (index, rect) in placed { frames[index] = rect.offsetBy(dx: 0, dy: shift) }
            top = (placed.map(\.1.maxY).max() ?? 0) + shift + rowGap
        }
        let box = frames.dropFirst().reduce(frames[0]) { $0.union($1) }
        return Arrangement(size: box.size, frames: frames.map { $0.offsetBy(dx: -box.minX, dy: -box.minY) }, scale: 1)
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
