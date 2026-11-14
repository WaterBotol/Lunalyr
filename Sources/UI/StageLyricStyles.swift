import SwiftUI

// MARK: - Layouts that set the whole stage themselves
//
// Hinge, Broadside, Portal, Mosaic, Torque and Fable each build a line on a
// stage of their own. This file holds what they share: the runs a line is
// cut into (`StageRun`), the shared timing (`StageMotion`), a measuring
// layout (`StageLayout`), and `StageLyricView` itself -- its inputs, the
// shared helpers (type, colour, readings, tiles, entrances) and the
// dispatcher that hands the line to one layout. Each layout lives in its own
// file under StageLayouts/ (`<Name>Layout.swift`) with its own entry point
// (`hingeBody(runs:shown:)` and so on), its own motion constants and its own
// Reduce Motion choice (`<name>OwnsReduceMotion`), so one layout can be
// rebuilt without touching another. Written for Lunalyr; the looks are
// inspired by other lyric apps' motion but no code or assets are shared.

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

    /// `x` held to 0...1 with its two corners rounded over `corner` either
    /// side (quadratic, so its slope never steps): a value driven by a rank
    /// that crosses a hard corner starts or stops at full speed. Within
    /// `corner / 4` of the plain clamp everywhere, and equal to it away
    /// from the corners.
    static func softUnit(_ x: Double, corner: Double = 0.12) -> Double {
        let c = max(1e-6, corner)
        if x <= -c { return 0 }
        if x < c { return (x + c) * (x + c) / (4 * c) }
        if x <= 1 - c { return x }
        if x < 1 + c { return 1 - (1 + c - x) * (1 + c - x) / (4 * c) }
        return 1
    }

    /// How much room a word takes in a stack: none until it starts to arrive,
    /// all of it once it has (the overshoot is not added).
    static func weight(_ arrival: Double) -> Double { min(1, max(0, arrival)) }

    /// Reduce Motion: how far the stage has dissolved from its previous step
    /// (before `onset`) to this one. 1 once the dissolve is over.
    static func reducedStep(since onset: Double, at time: Double) -> Double {
        Easing.smoothstep((time - (onset - lead)) / WallpaperMotion.reducedDissolve)
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
    @Environment(\.lyricInk) var ink
    @Environment(\.lyricReduceMotion) var reduceMotion

    // MARK: Shared

    var unit: CGFloat { max(16, baseSize) }

    /// 0...1 as a word arrives, a little overshooting (`StageMotion.arrival`).
    func arrive(_ run: StageRun, speed: Double) -> Double {
        StageMotion.arrival(run.start, at: time, speed: speed, reduceMotion: reduceMotion)
    }

    /// One onset's share of a rank or camera move (`StageMotion.handoff`).
    func handoff(_ run: StageRun, speed: Double) -> Double {
        Self.handoff(run, at: time, speed: speed, seed: seed, reduceMotion: reduceMotion)
    }

    static func handoff(_ run: StageRun, at time: Double, speed: Double, seed: Int, reduceMotion: Bool) -> Double {
        StageMotion.handoff(run.start, at: time, speed: speed, seed: UInt64(abs(seed &+ run.id &* 7919)),
                            reduceMotion: reduceMotion)
    }

    /// How far each word has been pushed back by the words after it: the sum
    /// of their hand-offs, so it carries its speed from one onset to the next.
    func ranks(_ runs: [StageRun], speed: Double) -> [Double] {
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
    func word(_ run: StageRun, _ colour: Color, uppercase: Bool = false) -> Text {
        Text(run.revealed(at: time, uppercase: uppercase, colour: colour)).foregroundStyle(colour)
    }

    /// Eases 0...1 with no overshoot: the camera and the soft arrivals use this.
    static func glide(_ x: Double) -> Double {
        let t = min(max(x, 0), 1)
        return t * t * t * (t * (t * 6 - 15) + 10)
    }

    func font(_ size: CGFloat, hero: Bool, id: Int) -> Font {
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
    func withReading<Word: View>(_ run: StageRun, size: CGFloat, colour: Color, liveColour: Color? = nil,
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

    func sample(_ run: StageRun, of count: Int) -> LyricInkSample {
        ink.sample(slot: count > 1 ? Double(run.id) / Double(count - 1) : 0.5, seed: UInt64(abs(seed &+ run.id)))
    }

    /// "Cutout · Highlight" chosen as the entrance: each sung word sits on a tile.
    func tiled(_ run: StageRun) -> Bool {
        settings.arrival(in: composition, for: LyricsFetcher.Word(text: run.text, start: run.start, end: run.end)).highlightsSungWords
    }

    func tileBackground(_ on: Bool, size: CGFloat) -> some View {
        RoundedRectangle(cornerRadius: 3).fill(ink.tileColour).opacity(on ? 1 : 0)
            .padding(.horizontal, -size * 0.1).padding(.vertical, -size * 0.02)
    }

    /// 1 for an ordinary pace, up to 3 when the line is sung fast: every motion
    /// is shortened and calmed by it so the layout keeps up with the voice.
    var lineSpeed: Double {
        let starts = words.map(\.start).sorted()
        let gaps = zip(starts, starts.dropFirst()).map { $1 - $0 }.filter { $0 > 0.02 }.sorted()
        guard !gaps.isEmpty else { return 1 }
        let median = gaps[gaps.count / 2]
        return min(3, max(1, 0.5 / max(median, 0.1)))
    }

    /// The whole line leaving, over the same settle as every other line exit.
    var leave: Double { StageMotion.leave(at: time, exitStart: exitStart) }

    /// The runs that have arrived: always the first ones, in reading order,
    /// as the layouts read their arrays by `run.id`. A source whose word
    /// times run backwards made a filter skip an earlier run, and the next
    /// one's id then read past the end of those arrays (a crash).
    static func shownRuns(_ runs: [StageRun], at time: Double) -> [StageRun] {
        Array(runs.prefix { time >= $0.start - StageMotion.lead })
    }

    /// Under Reduce Motion, how far the stage has dissolved from the step
    /// before the newest word to this one; nil when there is nothing to
    /// dissolve, or when the layout handles Reduce Motion itself
    /// (`ownsReduceMotion`: Fable never moves its words, so it needs none).
    static func reducedDissolve(_ shown: [StageRun], composition: LyricComposition, at time: Double,
                                reduceMotion: Bool) -> Double? {
        guard reduceMotion, !ownsReduceMotion(composition), let newest = shown.last else { return nil }
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

    /// The dispatcher: each layout's own entry point, in its own file
    /// (StageLayouts/<Name>Layout.swift). `runs` is the whole line; `shown`
    /// the runs that have arrived (under Reduce Motion, the step before as
    /// well, see `body`). A layout is free to use either.
    @ViewBuilder
    private func layout(_ runs: [StageRun], _ shown: [StageRun]) -> some View {
        switch composition {
        case .hinge: hingeBody(runs: runs, shown: shown)
        case .broadside: broadsideBody(runs: runs, shown: shown)
        case .mosaic: mosaicBody(runs: runs, shown: shown)
        case .torque: torqueBody(runs: runs, shown: shown)
        case .fable: fableBody(runs: runs, shown: shown)
        default: portalBody(runs: runs, shown: shown)
        }
    }

    /// Whether a layout draws Reduce Motion itself, rather than the shared
    /// dissolve between steps (`reducedDissolve`). Each layout says so in its own file.
    static func ownsReduceMotion(_ composition: LyricComposition) -> Bool {
        switch composition {
        case .hinge: return hingeOwnsReduceMotion
        case .broadside: return broadsideOwnsReduceMotion
        case .mosaic: return mosaicOwnsReduceMotion
        case .torque: return torqueOwnsReduceMotion
        case .fable: return fableOwnsReduceMotion
        case .portal: return portalOwnsReduceMotion
        default: return false
        }
    }
}

// MARK: - Shared by several layouts

extension StageLyricView {
    /// The little words ("the", "of", "and"...) Broadside sets as labels and
    /// Fable sets a touch smaller.
    static let little: Set<String> = [
        "a", "an", "the", "of", "to", "in", "on", "at", "by", "for", "and", "or", "but", "so", "as", "if", "is", "it",
        "my", "me", "we", "i", "i'm", "i'll", "you", "be", "up", "us", "no", "oh", "can", "can't", "do", "did", "don't",
        "under", "over", "with", "from", "into", "out", "off", "all", "are", "was", "im", "ill",
    ]

    /// Whether `text` is one of the little words, and short.
    func isLittle(_ text: String) -> Bool {
        Self.little.contains(text.lowercased().trimmingCharacters(in: .punctuationCharacters)) && text.count <= 5
    }

    /// A steady 0...1 for a line (`word` nil) or one of its words and a salt.
    func chance(_ word: Int?, _ salt: Int) -> Double {
        var value = UInt64(truncatingIfNeeded: abs(seed) &* 2654435761 &+ (word ?? 977) &* 40503 &+ salt &* 9973)
        value ^= value >> 13; value = value &* 0x2545F4914F6CDD1D; value ^= value >> 29
        return Double(value % 10_000) / 10_000
    }

    /// What a word does when the viewer chose a particular entrance rather than
    /// leaving it to the layout. `native` means the layout's own motion stays.
    /// The chosen entrances move exactly as they do in every other layout
    /// (`WordEntrance.transform`), so "Float up" means the same thing everywhere.
    func chosenEntrance(_ run: StageRun, progress a: Double, size: CGFloat)
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
