import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Portal: its pill and burst, no static ornaments, and fast songs.
///
/// `testWritePortalFastVideoFrames` writes review frames of two fast lines
/// only when LUNALYR_PORTAL_FAST_DIR is set (pass it as
/// TEST_RUNNER_LUNALYR_PORTAL_FAST_DIR), into
/// `<dir>/<size>/portal/<line>/frames/NNNN.png`, reusing
/// `StageLayoutVideoTests`' drawing; encode them with ffmpeg afterwards.
@MainActor
final class PortalLayoutTests: XCTestCase {
    typealias Word = LyricsFetcher.Word

    // MARK: Fast lines

    /// Twelve words in 2.16 s (one every 0.18 s).
    static var fastWords: [Word] {
        let texts = "we run it back and turn it up til the lights go".split(separator: " ").map(String.init)
        return texts.enumerated().map { i, text in
            Word(text: text, start: 1 + Double(i) * 0.18, end: 1 + Double(i) * 0.18 + 0.16)
        }
    }

    /// A steadier line with three words arriving within 0.15 s ("we go now").
    static var tripleWords: [Word] {
        let timing: [(String, Double, Double)] = [
            ("hold", 1.00, 1.40), ("on", 1.45, 1.90), ("we", 2.00, 2.06), ("go", 2.07, 2.13), ("now", 2.14, 2.60),
            ("into", 2.70, 3.00), ("the", 3.05, 3.20), ("night", 3.25, 4.00),
        ]
        return timing.map { Word(text: $0.0, start: $0.1, end: $0.2) }
    }

    /// Syllable-timed words, glued: beau·ti·ful, run·ning, un·for·get·ta·ble.
    static var syllableWords: (words: [Word], glue: [Bool]) {
        let timing: [(String, Double, Bool)] = [
            ("so", 0.80, false), ("beau", 1.00, false), ("ti", 1.20, true), ("ful", 1.40, true), ("run", 1.90, false),
            ("ning", 2.10, true), ("un", 2.60, false), ("for", 2.75, true), ("get", 2.90, true), ("ta", 3.05, true),
            ("ble", 3.20, true), ("night", 3.60, false),
        ]
        let words = timing.map { Word(text: $0.0, start: $0.1, end: $0.1 + 0.14) }
        return (words, timing.map(\.2))
    }

    /// A line with words that have a picture: call, home, love, city, lights.
    static var iconWords: [Word] {
        StagePhrasesTests.even("call me home my love the city lights are burning bright", step: 0.45)
    }

    /// Japanese glued as the app glues an unspaced line: one run of tokens.
    static var japaneseAppGlue: [Bool] {
        StageReadingTests.japanese.words.indices.map { $0 > 0 }
    }

    static var fastClips: [StageLayoutVideoTests.Clip] {
        typealias V = StageLayoutVideoTests
        let fast = V.line(fastWords)
        let fastNext = V.line(V.follower(["down", "we", "go", "again", "and", "again"], after: fast, step: 0.18))
        let triple = V.line(tripleWords)
        let tripleNext = V.line(V.follower(["hold", "me", "close", "tonight"], after: triple, step: 0.3))
        let syllables = V.line(syllableWords.words)
        let syllablesNext = V.line(V.follower(["hold", "me", "close", "tonight"], after: syllables, step: 0.3))
        let icons = V.line(iconWords)
        let iconsNext = V.line(V.follower(["ring", "the", "phone", "tonight"], after: icons, step: 0.4))
        let japanese = StageReadingTests.japanese
        let jp = V.line(japanese.words)
        let jpNext = V.line(V.follower(["\u{661F}", "\u{304C}", "\u{5149}", "\u{308B}"], after: jp, step: 0.3))
        func flat(_ line: LyricsFetcher.Line) -> [Bool] { [Bool](repeating: false, count: line.words.count) }
        return [
            V.Clip(name: "syllables", line: syllables, glue: syllableWords.glue, readings: [:], next: syllablesNext,
                   nextGlue: flat(syllablesNext), nextReadings: [:], hue: 0.12),
            V.Clip(name: "icons", line: icons, glue: flat(icons), readings: [:], next: iconsNext, nextGlue: flat(iconsNext),
                   nextReadings: [:], hue: 0.66),
            V.Clip(name: "japanese-glued", line: jp, glue: japaneseAppGlue, readings: japanese.readings, next: jpNext,
                   nextGlue: [false, true, true, true], nextReadings: [:], hue: 0.03),
            V.Clip(name: "fast", line: fast, glue: flat(fast), readings: [:], next: fastNext, nextGlue: flat(fastNext),
                   nextReadings: [:], hue: 0.9),
            V.Clip(name: "triple", line: triple, glue: flat(triple), readings: [:], next: tripleNext,
                   nextGlue: flat(tripleNext), nextReadings: [:], hue: 0.45),
        ]
    }

    func testWritePortalFastVideoFrames() throws {
        typealias V = StageLayoutVideoTests
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_PORTAL_FAST_DIR"], !dir.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_PORTAL_FAST_DIR to write Portal's fast-line frames")
        }
        let fileManager = FileManager.default
        for size in V.sizes {
            let shrink = min(1, 460 / size.stage.width)
            let cell = CGSize(width: (size.stage.width * shrink).rounded(), height: (size.stage.height * shrink).rounded())
            for clip in Self.fastClips {
                let folder = "\(dir)/\(size.name)/portal/\(clip.name)"
                let frames = folder + "/frames"
                try? fileManager.removeItem(atPath: frames)
                try fileManager.createDirectory(atPath: frames, withIntermediateDirectories: true)
                let painter = V.Painter(size: size.stage, renderer: .window)
                defer { painter.close() }
                let span = V.span(of: clip)
                let count = Int(((span.upperBound - span.lowerBound) * V.fps).rounded(.up))
                let momentFrames = V.moments(of: clip).map { Int((($0 - span.lowerBound) * V.fps).rounded()) }
                var picked: [Int: NSBitmapImageRep] = [:]
                for index in 0...count {
                    try autoreleasepool {
                        let time = span.lowerBound + Double(index) / V.fps
                        let rep = try painter.draw(V.frame(clip, composition: .portal, size: size, time: time))
                        try V.writePNG(rep, to: frames + String(format: "/%04d.png", index))
                        if momentFrames.contains(index) { picked[index] = rep }
                    }
                }
                let row = momentFrames.compactMap { picked[$0] }
                try V.writePNG(try V.sheet([row], cell: cell), to: "\(dir)/\(size.name)/portal/\(clip.name)-sheet.png")
            }
        }
    }

    // MARK: Words and syllables

    func testGluedSyllablesAreOnePill() {
        let line = Self.syllableWords
        let runs = StageLyricView.portalRuns(line.words, glue: line.glue)
        XCTAssertEqual(runs.map(\.text), ["so", "beautiful", "running", "unforgettable", "night"])
        XCTAssertEqual(runs.map(\.id), Array(0..<5))
        XCTAssertEqual(runs[1].parts.count, 3, "Each syllable is revealed in the pill on its own time")
        XCTAssertEqual(runs[3].start, 2.60, accuracy: 0.0001)
        // The shared runs cut a long glued word in two; Portal does not.
        XCTAssertGreaterThan(StageRun.make(line.words, glue: line.glue).count, runs.count)
    }

    func testKoreanGluedWordIsOnePill() {
        let words = ["\u{AE30}", "\u{C5B5}", "\u{D560}", "\u{AC8C}", "\u{B108}"].enumerated().map { i, text in
            Word(text: text, start: 1 + Double(i) * 0.2, end: 1.15 + Double(i) * 0.2)
        }
        let runs = StageLyricView.portalRuns(words, glue: [false, true, true, true, false])
        XCTAssertEqual(runs.map(\.text), ["\u{AE30}\u{C5B5}\u{D560}\u{AC8C}", "\u{B108}"])
    }

    /// An unspaced Japanese line, glued as the app glues it, is parted into
    /// its words: one pill per word, never one pill per three characters.
    func testJapaneseGluedLineIsPartedIntoWords() {
        let japanese = StageReadingTests.japanese
        let runs = StageLyricView.portalRuns(japanese.words, glue: Self.japaneseAppGlue)
        XCTAssertGreaterThan(runs.count, 2)
        XCTAssertLessThan(runs.count, japanese.words.count)
        XCTAssertEqual(runs.map(\.text).joined(), japanese.words.map(\.text).joined())
        XCTAssertTrue(runs.contains { $0.text.contains("\u{540D}\u{524D}") }, "\(runs.map(\.text)): 名前 is one word")
        // The test line's own glue (already parted) is kept as it is.
        let given = StageLyricView.portalRuns(japanese.words, glue: japanese.glue)
        XCTAssertEqual(given.map(\.text), StageRun.make(japanese.words, glue: japanese.glue).map(\.text))
    }

    // MARK: Icons, ornaments, shapes

    static let plainLine = "drifting slowly across empty fields beneath gentle grey willows whispering softly together"
        .split(separator: " ").map(String.init)

    func testOrnamentsVaryAndRest() {
        XCTAssertEqual(Self.plainLine.count, 12)
        for seed in 0..<40 {
            let icons = StageLyricView.portalIcons(Self.plainLine) { index in Self.steady(seed, index) }
            XCTAssertTrue(Self.plainLine.allSatisfy { LyricWordIcons.symbol(for: $0) == nil })
            for index in icons.indices {
                guard let icon = icons[index] else { continue }
                XCTAssertTrue(StageLyricView.portalOrnaments.contains(icon))
                for back in 1...3 where index - back >= 0 {
                    XCTAssertNotEqual(icons[index - back], icon, "seed \(seed): \(icon) repeats within three pills")
                }
            }
            XCTAssertGreaterThanOrEqual(icons.filter { $0 == nil }.count * 3, icons.count, "seed \(seed): at least a third bare")
            XCTAssertGreaterThan(icons.compactMap { $0 }.count, 0)
        }
    }

    func testMeaningfulWordsAlwaysShowTheirSymbolAndFunctionWordsNone() {
        let texts = "call me home my love the city lights are burning bright".split(separator: " ").map(String.init)
        for seed in 0..<20 {
            let icons = StageLyricView.portalIcons(texts) { Self.steady(seed, $0) }
            XCTAssertEqual(icons[0], "phone.fill")
            XCTAssertNil(icons[1])
            XCTAssertEqual(icons[2], "house.fill")
            XCTAssertNil(icons[3])
            XCTAssertEqual(icons[4], "heart.fill")
            XCTAssertNil(icons[5])
            XCTAssertEqual(icons[6], "building.2.fill")
            XCTAssertEqual(icons[9], "flame.fill")
        }
        // Deterministic: the same line and seed, the same symbols.
        XCTAssertEqual(StageLyricView.portalIcons(texts) { Self.steady(3, $0) }, StageLyricView.portalIcons(texts) { Self.steady(3, $0) })
    }

    func testShapesNeverRepeatOnConsecutivePills() {
        for seed in 0..<40 {
            let shapes = StageLyricView.portalShapes(count: 12) { Self.steady(seed, $0) }
            XCTAssertEqual(shapes.count, 12)
            for index in shapes.indices.dropFirst() {
                XCTAssertNotEqual(shapes[index], shapes[index - 1], "seed \(seed) at \(index)")
            }
        }
        // Every shape draws, inset or not.
        for kind in StageLyricView.PortalShape.allCases {
            for flip in [false, true] {
                let shape = PortalPillShape(kind: kind, size: 40, flip: flip)
                let rect = CGRect(x: 0, y: 0, width: 200, height: 70)
                XCTAssertFalse(shape.path(in: rect).isEmpty)
                XCTAssertFalse(shape.inset(by: 3).path(in: rect).isEmpty)
            }
        }
    }

    static func steady(_ seed: Int, _ index: Int) -> Double {
        var value = UInt64(truncatingIfNeeded: (seed &* 7919) &+ (index &+ 2) &* 104_729)
        value ^= value >> 13; value = value &* 0x2545F4914F6CDD1D; value ^= value >> 29
        return Double(value % 10_000) / 10_000
    }

    // MARK: Fast songs

    /// The line's pace, as `StageLyricView.lineSpeed` works it out.
    static func speed(_ runs: [StageRun]) -> Double {
        let starts = runs.map(\.start).sorted()
        let gaps = zip(starts, starts.dropFirst()).map { $1 - $0 }.filter { $0 > 0.02 }.sorted()
        guard !gaps.isEmpty else { return 1 }
        return min(3, max(1, 0.5 / max(gaps[gaps.count / 2], 0.1)))
    }

    struct Pose {
        var y: CGFloat
        var height: CGFloat
        var scale: CGFloat
        var opacity: Double
        var live: Double
    }

    /// Each shown run's pill as `portal` lays it out at one moment, on a
    /// 520-point stage with 100-point, 300-point-wide pills: its place
    /// above the live pill's (climb, and the travel in from below), shown
    /// scale (age and landing) and opacity. `original` is the Portal the
    /// user liked, before this branch (`git show aa7f036:Sources/UI/StageLayouts/PortalLayout.swift`).
    static func poses(_ runs: [StageRun], at time: Double, original: Bool = false) -> [Pose] {
        let shown = runs.filter { time >= $0.start - StageMotion.lead }
        let speed = speed(runs)
        var settle: [Double], lift: [Double]
        if original {
            // (The original's lift started 0.08 s before the onset, before
            // the word arrived: its first frames jumped the trail.)
            let settleSpan: Double = 0.6 / speed
            settle = shown.map { (run: StageRun) -> Double in
                let since: Double = time - (run.start - 0.05)
                return StageLyricView.glide(since / settleSpan)
            }
            let liftSpan: Double = 0.36 / speed
            lift = shown.map { (run: StageRun) -> Double in
                let since: Double = time - (run.start - 0.08)
                return StageLyricView.glide(since / liftSpan)
            }
        } else {
            let motion = StageLyricView.portalMotion(runs, at: time, speed: speed, reduceMotion: false)
            settle = Array(motion.settle.prefix(shown.count))
            lift = Array(motion.lift.prefix(shown.count))
        }
        var ranks = [Double](repeating: 0, count: shown.count)
        var after = 0.0
        for index in shown.indices.reversed() { ranks[index] = after; after += settle[index] }
        let lives = ranks.map { StageMotion.liveness(rank: $0) }
        let heights = shown.indices.map { i -> CGFloat in
            let upright = 100 * (1.04 + 0.1 * CGFloat(lives[i]))
            return StageLyricView.portalScale(rank: ranks[i]) * upright
        }
        let fadeFrom = speed > 1.6 ? 1.0 : 1.6
        let visible: [Double] = ranks.map { (rank: Double) -> Double in
            let fade: Double = (rank - fadeFrom) / 0.9
            return 1 - Easing.smoothstep(fade)
        }
        // The live pill rests 70 points below the middle of the stage.
        let column = StageLyricView.portalColumn(heights: heights, lifts: lift, visible: visible, appear: settle, gap: 64 * 0.16,
                                                 above: 330, below: original ? 10_000 : 190)
        return shown.indices.map { i in
            let travel = CGFloat(1 - settle[i]) * 520 * 0.1 / CGFloat(speed)
            let landing = StageMotion.arrival(shown[i].start, at: time, speed: speed)
            let scale = StageLyricView.portalScale(rank: ranks[i]) * CGFloat(0.9 + 0.1 * min(landing, 1.05))
            return Pose(y: travel + column[i], height: heights[i], scale: scale,
                        opacity: settle[i] * visible[i] * (0.85 + 0.15 * lives[i]), live: lives[i])
        }
    }

    static var fastRuns: [[StageRun]] {
        [fastWords, tripleWords, StagePhrasesTests.mixed, StagePhrasesTests.even("we keep dreaming under neon rain")].map {
            StageLyricView.portalRuns($0, glue: [Bool](repeating: false, count: $0.count))
        }
    }

    /// Two pills (live words) never sit over each other, and the pale words
    /// before them never pile up, frame by frame through fast lines.
    func testFastLinesNeverStackPills() {
        for runs in Self.fastRuns {
            let first = runs.first?.start ?? 0, last = runs.last?.start ?? 0
            var time = first - 0.2
            while time < last + 0.8 {
                let poses = Self.poses(runs, at: time)
                var overlappingPills = 0
                for i in poses.indices {
                    for j in poses.indices where j > i {
                        let a = poses[i], b = poses[j]
                        guard a.opacity > 0.3, b.opacity > 0.3 else { continue }
                        let overlap = (a.height + b.height) / 2 - abs(a.y - b.y)
                        let smaller = min(a.height, b.height)
                        if a.live > 0.3, b.live > 0.3, overlap > 0.2 * smaller { overlappingPills += 1 }
                        XCTAssertLessThan(overlap, 0.35 * smaller,
                                          String(format: "%@ and %@ pile up at %.2f s", runs[i].text, runs[j].text, time))
                    }
                }
                XCTAssertEqual(overlappingPills, 0, String(format: "pills overlap at %.2f s", time))
                time += 1.0 / 60
            }
        }
    }

    /// Every word starts on its own sung time, never queued behind the
    /// words before it: its entrance begins at its onset (less the shared
    /// 0.05 s lead) and is at least half in 0.3 s later, however quick the line.
    func testEveryWordEntersOnItsSungTime() {
        for runs in Self.fastRuns {
            let speed = Self.speed(runs)
            for run in runs {
                func appear(_ time: Double) -> Double {
                    StageLyricView.portalMotion(runs, at: time, speed: speed, reduceMotion: false).appear[run.id]
                }
                XCTAssertEqual(appear(run.start - 0.05), 0, accuracy: 0.0001, run.text)
                XCTAssertGreaterThan(appear(run.start), 0, run.text)
                XCTAssertGreaterThanOrEqual(appear(run.start + 0.3), 0.5, "\(run.text) at \(run.start)")
            }
        }
    }

    /// The three words sung within 0.15 s share one burst: it runs on,
    /// never restarting, from the first word's onset.
    func testQuickRunSharesOneBurst() {
        let runs = Self.fastRuns[1]
        var previous = -1.0
        var time = 1.95
        while time < 2.5 {
            let burst = StageLyricView.portalMotion(runs, at: time, speed: Self.speed(runs), reduceMotion: false).burst
            XCTAssertEqual(burst[2], burst[3], accuracy: 0.0001)
            XCTAssertEqual(burst[3], burst[4], accuracy: 0.0001)
            XCTAssertGreaterThanOrEqual(burst[4], previous)
            previous = burst[4]
            time += 1.0 / 60
        }
    }

    /// Only a word followed within 0.35 s moves quicker than the original,
    /// and never below the floors.
    func testOnlyQuickWordsShortenAndNeverBelowTheFloors() {
        for runs in Self.fastRuns {
            let speed = Self.speed(runs)
            for index in runs.indices {
                let times = StageLyricView.portalTimes(runs, index, speed: speed)
                let next = index + 1 < runs.count ? runs[index + 1].start - runs[index].start : 9
                if next >= 0.35 {
                    XCTAssertEqual(times.settle, 0.6 / speed, accuracy: 1e-9)
                    XCTAssertEqual(times.lift, 0.36 / speed, accuracy: 1e-9)
                } else {
                    XCTAssertGreaterThanOrEqual(times.settle, min(0.6 / speed, 0.28) - 1e-9)
                    XCTAssertGreaterThanOrEqual(times.lift, min(0.36 / speed, 0.2) - 1e-9)
                    XCTAssertLessThanOrEqual(times.settle, 0.6 / speed + 1e-9)
                }
            }
        }
    }

    // MARK: Smoothness

    /// The largest change of any pill's place, scale or opacity over one
    /// 60 Hz frame, sampled at 120 Hz through a line and its exit.
    static func largestSteps(_ runs: [StageRun], original: Bool) -> (y: CGFloat, scale: CGFloat, opacity: Double) {
        var worst: (y: CGFloat, scale: CGFloat, opacity: Double) = (0, 0, 0)
        let first = runs.first?.start ?? 0, last = runs.last?.start ?? 0
        var time = first - 0.2
        while time < last + 1.0 {
            let now = poses(runs, at: time, original: original)
            let later = poses(runs, at: time + 1.0 / 60, original: original)
            for i in now.indices {
                worst.y = max(worst.y, abs(later[i].y - now[i].y))
                worst.scale = max(worst.scale, abs(later[i].scale - now[i].scale))
                worst.opacity = max(worst.opacity, abs(later[i].opacity - now[i].opacity))
            }
            time += 1.0 / 120
        }
        return worst
    }

    /// Portal moves as smoothly as the original: no pill's place, scale or
    /// opacity steps further in a frame than the original's largest step,
    /// plus 15 %. Each line is held to the original on that same line: a
    /// line's pace sets every duration (the original divides them by it),
    /// so the long line (pace 1.43) and the fast one (2.78) move quicker
    /// than the short one in the original too.
    func testMovesAsSmoothlyAsTheOriginal() {
        let short = Self.fastRuns[3], long = Self.fastRuns[2], fast = Self.fastRuns[0], triple = Self.fastRuns[1]
        for (name, runs) in [("short", short), ("long", long), ("fast", fast), ("triple", triple)] {
            let limit = Self.largestSteps(runs, original: true)
            let steps = Self.largestSteps(runs, original: false)
            print(String(format: "PORTAL-SMOOTH %@: y %.2f/%.2f scale %.4f/%.4f opacity %.4f/%.4f", name,
                         steps.y, limit.y, steps.scale, limit.scale, steps.opacity, limit.opacity))
            XCTAssertLessThanOrEqual(steps.y, limit.y * 1.15, "\(name): place")
            XCTAssertLessThanOrEqual(steps.scale, limit.scale * 1.15, "\(name): scale")
            XCTAssertLessThanOrEqual(steps.opacity, limit.opacity * 1.15, "\(name): opacity")
        }
    }

    /// Nothing already on the stage jumps when a word arrives: across each
    /// arrival (and each onset), 1/120 s either side, every pill already
    /// there before that word moves less than the per-frame limit (the
    /// original's largest step on that line, plus 15 %).
    func testNothingJumpsWhenAWordArrives() {
        for (name, runs) in [("short", Self.fastRuns[3]), ("long", Self.fastRuns[2]), ("fast", Self.fastRuns[0]),
                             ("triple", Self.fastRuns[1])] {
            let limit = Self.largestSteps(runs, original: true)
            for run in runs {
                for moment in [run.start - StageMotion.lead, run.start] {
                    let before = Self.poses(runs, at: moment - 1.0 / 120)
                    let after = Self.poses(runs, at: moment + 1.0 / 120)
                    for i in before.indices where i < run.id {
                        let what = String(format: "%@ %@: pill %d at %.3f s", name, run.text, i, moment)
                        XCTAssertLessThan(abs(after[i].y - before[i].y), limit.y * 1.15, what + " place")
                        XCTAssertLessThan(abs(after[i].scale - before[i].scale), limit.scale * 1.15, what + " scale")
                        XCTAssertLessThan(abs(after[i].opacity - before[i].opacity), limit.opacity * 1.15, what + " opacity")
                    }
                    // For the record: the original's largest jump here.
                    let oldBefore = Self.poses(runs, at: moment - 1.0 / 120, original: true)
                    let oldAfter = Self.poses(runs, at: moment + 1.0 / 120, original: true)
                    let oldJump = oldBefore.indices.filter { $0 < run.id }.map { abs(oldAfter[$0].y - oldBefore[$0].y) }.max() ?? 0
                    let newJump = before.indices.filter { $0 < run.id }.map { abs(after[$0].y - before[$0].y) }.max() ?? 0
                    if moment < run.start {
                        print(String(format: "PORTAL-ARRIVE %@ %@: original %.2f, now %.2f", name, run.text, oldJump, newJump))
                    }
                    // A pill that joins is at rest: invisible, in place.
                    if after.count > before.count { XCTAssertLessThan(after[before.count].opacity, 0.01, name + " " + run.text) }
                }
            }
        }
    }

    // MARK: Drawing

    /// Portal draws every kind of line without trouble, its icons included.
    func testPortalDrawsIconsShapesAndGluedWords() throws {
        let settings = Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Portal"), notifications: NotificationCenter())
        let lines: [([Word], [Bool])] = [
            (Self.iconWords, [Bool](repeating: false, count: Self.iconWords.count)),
            (Self.syllableWords.words, Self.syllableWords.glue),
            (Self.fastWords, [Bool](repeating: false, count: Self.fastWords.count)),
            (StageReadingTests.japanese.words, Self.japaneseAppGlue),
        ]
        for (words, glue) in lines {
            for time in stride(from: 0.9, through: 4.5, by: 0.37) {
                let view = StageLyricView(words: words, glue: glue, time: time, stageSize: CGSize(width: 900, height: 400), baseSize: 50,
                                          mood: .neutral, composition: .portal, seed: 3, settings: settings)
                    .environment(\.lyricInk, LyricInk())
                let renderer = ImageRenderer(content: view)
                renderer.scale = 1
                XCTAssertNotNil(renderer.nsImage)
            }
        }
    }
}

