import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Mosaic restored and decluttered: no ornaments, the colour bar only ever
/// with its word, fast lines without pile-ups, words in by their onset,
/// glued syllables set as one word, and long lines varied.
@MainActor
final class MosaicLayoutTests: XCTestCase {
    typealias Word = LyricsFetcher.Word
    typealias Mosaic = StageLyricView

    static let stage = CGSize(width: 1400, height: 520)
    static let unit: CGFloat = 64 * 0.8

    static func settings() -> VersoCustom.Settings {
        Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Mosaic"), notifications: NotificationCenter())
    }

    static func runs(_ words: [Word], glue: [Bool]? = nil) -> [StageRun] {
        StageRun.make(words, glue: glue ?? [Bool](repeating: false, count: words.count))
    }

    static var lines: [(name: String, runs: [StageRun])] {
        [("fast", runs(MosaicFastVideoTests.fast.words, glue: MosaicFastVideoTests.fast.glue)),
         ("burst", runs(MosaicFastVideoTests.burst.words, glue: MosaicFastVideoTests.burst.glue)),
         ("long", runs(StagePhrasesTests.mixed)),
         ("short", runs(StagePhrasesTests.even("we keep dreaming under neon rain")))]
    }

    static func speed(_ runs: [StageRun]) -> Double {
        // As `lineSpeed` reads it: every timed piece, syllables included.
        let starts = runs.flatMap { $0.parts.map(\.start) }.sorted()
        let gaps = zip(starts, starts.dropFirst()).map { $1 - $0 }.filter { $0 > 0.02 }.sorted()
        guard !gaps.isEmpty else { return 1 }
        return min(3, max(1, 0.5 / max(gaps[gaps.count / 2], 0.1)))
    }

    // MARK: Nothing but words

    /// No star, sparkle, symbol, panel or wipe: the view's own type lists
    /// everything Mosaic can draw.
    func testDrawsNoStarOrSymbol() {
        let words = StagePhrasesTests.even("we keep dreaming under neon rain")
        let view = StageLyricView(words: words, glue: [Bool](repeating: false, count: words.count), time: 3,
                                  stageSize: Self.stage, baseSize: Self.unit, mood: .neutral, composition: .mosaic,
                                  seed: 3, settings: Self.settings())
        let runs = Self.runs(words)
        let drawn = String(reflecting: type(of: view.mosaicBody(runs: runs, shown: runs)))
        for banned in ["Star4", "Image", "Circle", "Capsule", "Divider", "_ShapeView<Rectangle", "GeometryReader"] {
            XCTAssertFalse(drawn.contains(banned), "Mosaic draws \(banned)")
        }
    }

    // MARK: The bar

    /// The bar is only ever a single word's (its width is its word's).
    func testBarIsAlwaysOneWord() {
        for line in Self.lines {
            for beat in Mosaic.mosaicBeats(line.runs) where beat.kind == .bar {
                XCTAssertEqual(beat.runs.count, 1, line.name)
            }
        }
    }

    /// Drawn: whenever any of the bar shows, its word shows on it in full
    /// proportion, through the whole entrance (the old wipe uncovered the
    /// bar's padding first, and the bar arrived before its word).
    func testBarNeverShowsWithoutItsWord() throws {
        // "oh" is the longest word held, so huge; "no" takes the bar.
        let words = [Word(text: "oh", start: 1, end: 1.5), Word(text: "no", start: 1.6, end: 2.0)]
        let runs = Self.runs(words)
        XCTAssertEqual(Mosaic.mosaicBeats(runs).map(\.kind), [.huge, .bar])
        let settings = Self.settings()
        var full: Double?
        var frames: [(bar: Int, word: Int)] = []
        for step in 0...30 {
            let time = 1.6 - 0.2 + Double(step) / 60
            let view = StageLyricView(words: words, glue: [false, false], time: time, stageSize: Self.stage,
                                      baseSize: Self.unit, mood: .neutral, composition: .mosaic, seed: 3, settings: settings)
                .environment(\.lyricInk, LyricInk())
                .environment(\.lyricReduceMotion, false)
                .environment(\.colorScheme, .dark)
            let rep = try StageReadingTests.bitmap(view, size: Self.stage)
            frames.append(Self.barPixels(rep))
        }
        full = frames.last.map { Double($0.word) / Double(max(1, $0.bar)) }
        let ratio = try XCTUnwrap(full)
        XCTAssertGreaterThan(ratio, 0.05, "the finished bar should carry its word")
        for (index, frame) in frames.enumerated() where frame.bar > 150 {
            XCTAssertGreaterThan(Double(frame.word) / Double(frame.bar), ratio * 0.6,
                                 "frame \(index): the bar shows with too little of its word")
        }
    }

    /// Counts the bar's pixels (strongly coloured) and the word's on it
    /// (opaque, not the bar's colour, inside the bar's bounding box).
    static func barPixels(_ rep: NSBitmapImageRep) -> (bar: Int, word: Int) {
        var box = (minX: Int.max, minY: Int.max, maxX: -1, maxY: -1)
        var bar = 0
        func colour(_ x: Int, _ y: Int) -> NSColor? { rep.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB) }
        for y in stride(from: 0, to: rep.pixelsHigh, by: 2) {
            for x in stride(from: 0, to: rep.pixelsWide, by: 2) {
                guard let c = colour(x, y), c.alphaComponent > 0.8, c.saturationComponent > 0.35,
                      c.brightnessComponent > 0.45 else { continue }
                bar += 1
                box = (min(box.minX, x), min(box.minY, y), max(box.maxX, x), max(box.maxY, y))
            }
        }
        guard bar > 0 else { return (0, 0) }
        var word = 0
        for y in stride(from: box.minY, through: box.maxY, by: 2) {
            for x in stride(from: box.minX, through: box.maxX, by: 2) {
                guard let c = colour(x, y), c.alphaComponent > 0.5 else { continue }
                if c.saturationComponent < 0.25 || c.brightnessComponent < 0.35 { word += 1 }
            }
        }
        return (bar, word)
    }

    // MARK: Fast songs

    struct Box {
        let beat: Int
        let rect: CGRect
        let opacity: Double
        let centre: CGPoint
        let scale: Double
        /// The big setting's and the small copy's own opacities (`opacity`
        /// is the more visible of the two).
        var big: Double = 0
        var small: Double = 0
    }

    static let seed = 3

    /// Where each beat is drawn at `time`, by Mosaic's own model, with each
    /// beat's size estimated from its text.
    static func boxes(_ runs: [StageRun], at time: Double, exitStart: Double = .infinity) -> [Box] {
        let beats = Mosaic.mosaicBeats(runs)
        let speed = speed(runs)
        let motion = Mosaic.mosaicMotion(beats, runs: runs, at: time, speed: speed, exitStart: exitStart, seed: seed,
                                         reduceMotion: false, arrived: runs.count)
        let bounds = CGRect(origin: .zero, size: stage)
        let step = Mosaic.mosaicStackStep(unit: unit, readings: false)
        var result: [Box] = []
        for (g, beat) in beats.enumerated() {
            let first = runs[beat.runs.lowerBound]
            let appear = Mosaic.mosaicAppear(first, at: time, speed: speed, reduceMotion: false, arrived: runs.count)
            let fade = Mosaic.mosaicStackFade(rank: motion.ranks[g])
            guard appear > 0.001, fade > 0.001 else { continue }
            let big = Mosaic.mosaicBigSize(beat.kind)
            let text = beat.runs.map { runs[$0].text }.joined(separator: " ")
            let size = estimate(text, big: big)
            let morph = StageMotion.mosaicMorph(settle: motion.moves[g], smallRatio: 0.6 / Double(big))
            let centre = Mosaic.mosaicCentre(size, move: motion.moves[g], rank: motion.ranks[g], sway: Mosaic.mosaicSway(g), step: step,
                                             overhang: 0, rightToLeft: false, in: bounds)
            var entrance = 1.0
            if beat.kind == .huge { entrance = 0.8 + 0.2 * min(1.05, appear) }
            if beat.kind == .bar { entrance = 0.7 + 0.3 * Mosaic.mosaicBarSpread(first, at: time, speed: speed) }
            let scale = CGFloat(morph.bigScale)
            let rect = CGRect(x: centre.x - size.width / 2, y: centre.y - size.height * scale / 2,
                              width: size.width * scale, height: size.height * scale)
            let bigOpacity = fade * min(1, appear * 1.6) * morph.bigOpacity
            let smallOpacity = fade * morph.smallOpacity
            result.append(Box(beat: g, rect: rect, opacity: max(bigOpacity, smallOpacity), centre: centre,
                              scale: morph.bigScale * entrance, big: bigOpacity, small: smallOpacity))
        }
        return result
    }

    static func estimate(_ text: String, big: CGFloat) -> CGSize {
        CGSize(width: min(stage.width * 0.9, CGFloat(text.count) * 0.55 * big * unit), height: big * unit * 1.1)
    }

    /// The ORIGINAL Mosaic (aa7f036), word by word, for calibration: each
    /// word's kind by chance, its arrival spring, the shared ranks, settle
    /// `glide(min(rank, 1.2) / 1.2)`, the huge word's 35 % slide, the morph.
    static func originalBoxes(_ runs: [StageRun], at time: Double) -> [Box] {
        let view = StageLyricView(words: [], glue: [], time: time, stageSize: stage, baseSize: unit, mood: .neutral,
                                  composition: .mosaic, seed: seed, settings: settings())
        let speed = speed(runs)
        let shown = StageLyricView.shownRuns(runs, at: time)
        let ranks = StageLyricView.ranks(shown, at: time, speed: speed, seed: seed, reduceMotion: false)
        let bounds = CGRect(origin: .zero, size: stage)
        let step = Mosaic.mosaicStackStep(unit: unit, readings: false)
        var result: [Box] = []
        for (index, run) in shown.enumerated() {
            let kind = Int(view.chance(run.id, 20) * 3) % 3
            let big: CGFloat = kind == 0 ? 3.0 : kind == 1 ? 2.3 : 2.4
            let a = StageMotion.arrival(run.start, at: time, speed: speed)
            let rank = ranks[index]
            let settle = Mosaic.glide(min(rank, 1.2) / 1.2)
            let size = estimate(run.text, big: big)
            let overhang = kind == 1 ? unit * 2.3 * 0.45 : 0
            let sway = CGFloat(view.chance(index, 21) - 0.5) * 0.06
            let home = Mosaic.mosaicHome(size, sway: sway, step: step, overhang: overhang, in: bounds)
            let stack = CGPoint(x: bounds.width * 0.14 + size.width / 2, y: Mosaic.mosaicRowY(rank: rank, step: step, in: bounds))
            var centre = CGPoint(x: home.x + (stack.x - home.x) * CGFloat(settle), y: home.y + (stack.y - home.y) * CGFloat(settle))
            if kind == 0 { centre.x += CGFloat(1 - min(a, 1)) * stage.width * 0.35 }
            let morph = StageMotion.mosaicMorph(settle: settle, smallRatio: 0.6 / Double(big))
            let fade = Mosaic.mosaicStackFade(rank: rank)
            let bigOpacity = min(1, a * 1.6) * morph.bigOpacity
            let smallOpacity = morph.smallOpacity * fade
            result.append(Box(beat: index, rect: .zero, opacity: max(bigOpacity, smallOpacity), centre: centre,
                              scale: morph.bigScale, big: bigOpacity, small: smallOpacity))
        }
        return result
    }

    /// The largest change of any item's position, scale and opacity over
    /// 1/60 s, sampled at 120 Hz; items matched by index (an item new in the
    /// later sample counts from nothing).
    static func steps(_ model: (Double) -> [Box], from start: Double, to end: Double,
                      around moments: [Double] = []) -> (position: CGFloat, scale: Double, opacity: Double) {
        var worst: (position: CGFloat, scale: Double, opacity: Double) = (0, 0, 0)
        var times: [Double] = []
        var time = start
        while time < end { times.append(time); time += 1.0 / 120 }
        times += moments.map { $0 - 1.0 / 120 }
        for t in times {
            let before = Dictionary(model(t).map { ($0.beat, $0) }, uniquingKeysWith: { a, _ in a })
            for box in model(t + 1.0 / 60) {
                guard let old = before[box.beat] else {
                    worst.opacity = max(worst.opacity, box.big, box.small)
                    continue
                }
                worst.position = max(worst.position, hypot(box.centre.x - old.centre.x, box.centre.y - old.centre.y))
                worst.scale = max(worst.scale, abs(box.scale - old.scale))
                worst.opacity = max(worst.opacity, abs(box.big - old.big), abs(box.small - old.small))
            }
        }
        return worst
    }

    /// Smooth as the original: on the short, long and fast lines, no item
    /// moves, scales or fades more in 1/60 s than the original Mosaic's worst
    /// on the same line plus 15 %, including across every arrival and onset.
    func testAsSmoothAsTheOriginal() {
        for line in Self.lines where line.name != "burst" {
            let runs = line.runs
            let start = (runs.first?.start ?? 0) - 0.3
            let end = (runs.map(\.end).max() ?? 0) + 0.2
            let original = Self.steps({ Self.originalBoxes(runs, at: $0) }, from: start, to: end)
            var moments: [Double] = []
            for run in runs { moments += [run.start, run.start - StageMotion.lead, run.start - Mosaic.mosaicLead] }
            let ours = Self.steps({ Self.boxes(runs, at: $0) }, from: start, to: end, around: moments)
            print("Mosaic smoothness \(line.name): original \(original), restored \(ours)")
            XCTAssertLessThanOrEqual(ours.position, original.position * 1.15, "\(line.name) position")
            XCTAssertLessThanOrEqual(ours.scale, original.scale * 1.15, "\(line.name) scale")
            XCTAssertLessThanOrEqual(ours.opacity, original.opacity * 1.15 + 0.001, "\(line.name) opacity")
        }
    }

    /// Nothing already on stage jumps on the frame a word arrives: across
    /// ±1/120 s of every arrival and onset, every other beat changes about as
    /// much as over the 1/60 s just before and just after (its own glide),
    /// never by a step.
    func testNothingJumpsWhenAWordArrives() {
        func change(_ runs: [StageRun], beat: Int, from a: Double, to b: Double) -> (CGFloat, Double, Double)? {
            guard let old = Self.boxes(runs, at: a).first(where: { $0.beat == beat }),
                  let new = Self.boxes(runs, at: b).first(where: { $0.beat == beat }) else { return nil }
            return (hypot(new.centre.x - old.centre.x, new.centre.y - old.centre.y),
                    abs(new.scale - old.scale), max(abs(new.big - old.big), abs(new.small - old.small)))
        }
        let h = 1.0 / 120
        for line in Self.lines {
            let runs = line.runs
            let beats = Mosaic.mosaicBeats(runs)
            for run in runs {
                let mine = beats.firstIndex { $0.runs.contains(run.id) } ?? -1
                for moment in [run.start - Mosaic.mosaicLead, run.start - StageMotion.lead, run.start] {
                    for box in Self.boxes(runs, at: moment + h) where box.beat != mine {
                        guard let across = change(runs, beat: box.beat, from: moment - h, to: moment + h) else {
                            XCTFail("\(line.name) \(run.text): beat \(box.beat) appeared with another word")
                            continue
                        }
                        let before = change(runs, beat: box.beat, from: moment - 3 * h, to: moment - h) ?? (0, 0, 0)
                        let after = change(runs, beat: box.beat, from: moment + h, to: moment + 3 * h) ?? (0, 0, 0)
                        let label = "\(line.name) \(run.text): beat \(box.beat) jumped"
                        XCTAssertLessThanOrEqual(across.0, max(before.0, after.0) * 1.5 + 0.5, label)
                        XCTAssertLessThanOrEqual(across.1, max(before.1, after.1) * 1.5 + 0.002, label)
                        XCTAssertLessThanOrEqual(across.2, max(before.2, after.2) * 1.5 + 0.005, label)
                    }
                }
            }
        }
    }

    /// Ten-plus words in 2.2 s, and three words inside 0.15 s: no two beats
    /// that can be seen overlap, and none leaves the stage, at any frame.
    func testFastLinesHaveNoOverlappingWords() {
        let fast = Self.lines[0].runs
        XCTAssertGreaterThanOrEqual(fast.count, 10)
        XCTAssertLessThanOrEqual((fast.last?.start ?? 0) - (fast.first?.start ?? 0), 2.2)
        for line in Self.lines {
            let runs = line.runs
            let exit = (runs.map(\.end).max() ?? 0) + 0.3
            var time = (runs.first?.start ?? 0) - 0.3
            while time < exit + 0.6 {
                let boxes = Self.boxes(runs, at: time, exitStart: exit).filter { $0.opacity > 0.3 }
                for a in boxes.indices {
                    let rect = boxes[a].rect
                    XCTAssertGreaterThanOrEqual(rect.minX, -1, "\(line.name) \(time): off stage")
                    XCTAssertLessThanOrEqual(rect.maxX, Self.stage.width + 1, "\(line.name) \(time): off stage")
                    for b in boxes.indices where b > a {
                        let overlap = rect.insetBy(dx: 2, dy: 2).intersection(boxes[b].rect.insetBy(dx: 2, dy: 2))
                        XCTAssertTrue(overlap.isNull || overlap.width * overlap.height < 1,
                                      "\(line.name) \(String(format: "%.3f", time)): beats \(boxes[a].beat) and \(boxes[b].beat) overlap")
                    }
                }
                time += 1.0 / 60
            }
        }
    }

    /// A quick run shares a beat; a fast line is cut into beats of up to
    /// three words, so it never repeats one treatment word after word.
    func testQuickRunsShareABeat() {
        let burst = Self.lines[1].runs
        let beats = Mosaic.mosaicBeats(burst)
        let oh = try? XCTUnwrap(burst.firstIndex { $0.text == "oh" })
        let beat = beats.first { $0.runs.contains(oh ?? -1) }
        XCTAssertEqual(beat?.runs.map { burst[$0].text }, ["oh", "my", "god"])
        let fast = Mosaic.mosaicBeats(Self.lines[0].runs)
        XCTAssertTrue(fast.allSatisfy { $0.runs.count <= Mosaic.mosaicBeatRuns })
        XCTAssertGreaterThanOrEqual(fast.count, 4)
    }

    // MARK: On time

    /// Every word is in by its onset: its beat is drawn and still big, and
    /// it is fully opaque (or, set letter by letter, its first letters are
    /// well in, and all of them before the next syllable).
    func testEveryWordAppearsBySungTime() {
        for line in Self.lines {
            let runs = line.runs
            let beats = Mosaic.mosaicBeats(runs)
            let speed = Self.speed(runs)
            for (g, beat) in beats.enumerated() {
                for index in beat.runs {
                    let run = runs[index]
                    let time = run.start
                    let moves = Mosaic.mosaicMotion(beats, runs: runs, at: time, speed: speed, exitStart: .infinity,
                                                    seed: Self.seed, reduceMotion: false, arrived: runs.count).moves
                    XCTAssertLessThan(moves[g], 0.6, "\(line.name) \(run.text): already leaving when sung")
                    if beat.kind == .letters {
                        let next: Double? = index + 1 < runs.count ? runs[index + 1].start : nil
                        for (part, piece) in run.parts.enumerated() {
                            XCTAssertGreaterThanOrEqual(Mosaic.mosaicLetterReveal(part: part, letter: 0, of: run, next: next,
                                                                                  at: piece.start), 0.4, "\(line.name) \(run.text)")
                            // All in within the original's own time (0.25 s rise, letters 0.05 s apart).
                            let last = max(0, piece.text.count - 1)
                            let done = piece.start - Mosaic.mosaicLead + 0.25 + Double(last) * 0.05
                            XCTAssertGreaterThanOrEqual(Mosaic.mosaicLetterReveal(part: part, letter: last, of: run, next: next,
                                                                                  at: done), 0.99,
                                                        "\(line.name) \(run.text): letters slower than the original")
                        }
                    } else {
                        let appear = Mosaic.mosaicAppear(run, at: time, speed: speed, reduceMotion: false, arrived: runs.count)
                        XCTAssertGreaterThanOrEqual(min(1, appear * 1.6), 0.95, "\(line.name) \(run.text)")
                    }
                }
            }
        }
    }

    // MARK: Glued syllables

    /// A two- and a three-syllable glued word are one run, one beat, one
    /// treatment; their letters follow their syllables.
    func testGluedSyllablesAreOneWord() {
        let burst = Self.lines[1].runs
        XCTAssertEqual(burst.map(\.text), ["and", "then", "oh", "my", "god", "you", "came", "back", "beautiful", "tonight"])
        let beats = Mosaic.mosaicBeats(burst)
        for word in ["beautiful", "tonight"] {
            let index = burst.firstIndex { $0.text == word } ?? -1
            XCTAssertEqual(beats.filter { $0.runs.contains(index) }.count, 1, word)
        }
        let fast = Self.lines[0].runs
        XCTAssertEqual(fast.count, 11)
        XCTAssertTrue(fast.contains { $0.text == "running" } && fast.contains { $0.text == "goodbye" })
        // "ful" rises at its own onset, not with "beau".
        let beautiful = burst[8]
        XCTAssertEqual(beautiful.parts.count, 3)
        let ful = beautiful.parts[2].start
        XCTAssertLessThan(Mosaic.mosaicLetterReveal(part: 2, letter: 0, of: beautiful, next: burst[9].start,
                                                  at: ful - Mosaic.mosaicLead - 0.01), 0.01)
        XCTAssertGreaterThan(Mosaic.mosaicLetterReveal(part: 2, letter: 0, of: beautiful, next: burst[9].start, at: ful), 0.4)
    }

    // MARK: Variety

    /// Never the same treatment twice in a row on the long line or an
    /// eleven-word line; the longest-held word is huge.
    func testLongLinesVaryTreatments() {
        let eleven = Self.runs(StagePhrasesTests.even("you were the light that kept me going through the darkest night", step: 0.5))
        for runs in [Self.lines[2].runs, eleven, Self.lines[3].runs] {
            let kinds = Mosaic.mosaicBeats(runs).map(\.kind)
            for (a, b) in zip(kinds, kinds.dropFirst()) {
                XCTAssertNotEqual(a, b, "\(runs.map(\.text)): \(kinds)")
            }
        }
        let long = Self.lines[2].runs
        let running = long.firstIndex { $0.text == "running" } ?? -1
        XCTAssertEqual(Mosaic.mosaicBeats(long).first { $0.runs.contains(running) }?.kind, .huge)
        XCTAssertEqual(Set(Mosaic.mosaicBeats(eleven).map(\.kind)).count, 3)
    }
}
