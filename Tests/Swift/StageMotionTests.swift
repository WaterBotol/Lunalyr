import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// The stage layouts' shared motion (Hinge, Broadside, Portal, Mosaic, Torque,
/// Fable): arrivals that start from rest, ranks that carry their speed, a
/// live word that hands over gradually, stacks that never jump at an onset,
/// one exit for every layout, and Reduce Motion as dissolves. Each test
/// failed on the motion the layouts had before (POLISH-AUDIT L2, L3, L5,
/// L11, L12, L14, L15, L16, L18, L19).
@MainActor
final class StageMotionTests: XCTestCase {
    private func runs(_ count: Int, step: Double = 0.5, from start: Double = 1) -> [StageRun] {
        let words = (0..<count).map { i in
            LyricsFetcher.Word(text: "word\(i)", start: start + Double(i) * step, end: start + Double(i) * step + step * 0.9)
        }
        return StageRun.make(words, glue: [Bool](repeating: false, count: count))
    }

    // MARK: L14: arrivals and ranks

    func testArrivalStartsFromRestOvershootsALittleAndLands() {
        let onset = 2.0
        let begin = onset - StageMotion.lead
        XCTAssertEqual(StageMotion.arrival(onset, at: begin - 0.01), 0)
        // easeOutBack began at 4.4 spans a second: 0.06 after 5 ms over a 0.34 s span.
        XCTAssertLessThan(StageMotion.arrival(onset, at: begin + 0.005), 0.01, "Starts from rest")
        let peak = stride(from: begin, to: begin + 1, by: 0.002).map { StageMotion.arrival(onset, at: $0) }.max() ?? 0
        XCTAssertGreaterThan(peak, 1.03)
        XCTAssertLessThan(peak, 1.12, "A little overshoot, about what the layouts had")
        XCTAssertEqual(StageMotion.arrival(onset, at: begin + 1.5), 1, "Lands exactly")
        // A fast line arrives faster.
        XCTAssertGreaterThan(StageMotion.arrival(onset, at: begin + 0.08, speed: 2.5), StageMotion.arrival(onset, at: begin + 0.08))
        // Reduce Motion: a step, which the layout dissolves.
        XCTAssertEqual(StageMotion.arrival(onset, at: begin - 0.001, reduceMotion: true), 0)
        XCTAssertEqual(StageMotion.arrival(onset, at: begin, reduceMotion: true), 1)
    }

    /// The rank of a word is the sum of the later words' shares. With
    /// easeOutBack each share started at full speed, so the stack's speed
    /// stepped at every onset; hand-off springs start from rest.
    func testRanksCarryTheirSpeedThroughEveryOnset() {
        let line = runs(6, step: 0.45)
        let dt = 0.001
        func rank0(_ t: Double) -> Double {
            StageLyricView.ranks(Array(StageLyricView.shownRuns(line, at: t)), at: t, speed: 1, seed: 3, reduceMotion: false).first ?? 0
        }
        for run in line.dropFirst() {
            let onset = run.start - StageMotion.lead
            let before = (rank0(onset) - rank0(onset - dt)) / dt
            let after = (rank0(onset + dt) - rank0(onset)) / dt
            XCTAssertLessThan(abs(after - before), 0.5, "Rank speed steps at the onset of \(run.text)")
        }
    }

    // MARK: L2: the live word hands over gradually

    func testTheLiveWordRestylesOverTheHandOverNotInOneFrame() {
        XCTAssertEqual(StageMotion.liveness(rank: 0), 1)
        XCTAssertEqual(StageMotion.liveness(rank: 0.15), 1)
        XCTAssertEqual(StageMotion.liveness(rank: 0.85), 0)
        XCTAssertEqual(StageMotion.liveness(rank: 2), 0)
        // Across the next word's arrival, frame by frame at 60 fps.
        let line = runs(3, step: 0.6)
        var previous: Double?
        var largest = 0.0
        for frame in 0..<120 {
            let t = 1.4 + Double(frame) / 60
            let shown = StageLyricView.shownRuns(line, at: t)
            let rank = StageLyricView.ranks(shown, at: t, speed: 1, seed: 3, reduceMotion: false)[0]
            let live = StageMotion.liveness(rank: rank)
            if let previous { largest = max(largest, abs(live - previous)) }
            previous = live
        }
        XCTAssertLessThan(largest, 0.3, "The old boolean flipped every live-only property at once (a step of 1)")
        XCTAssertEqual(previous ?? 1, 0, "The old word ends fully restyled")
    }

    // MARK: L3, L12: Torque
    // Torque's column (it replaced the stack these tests pinned) is tested
    // in TorqueLayoutTests: its turn never jumps and at most two faces show.

    // MARK: L3: Hinge's page

    /// Hinge sets each page's focal word as large as the stage allows: a
    /// page of more slabs, or with readings, sets it smaller, never larger
    /// than its held size, and never so large the page outgrows the stage.
    func testHingeFocalWordFitsTheStage() {
        let one = [(head: true, foot: false, focalReading: false, smallReading: false)]
        let roomy = StageLyricView.hingeFocalSize(slabs: one, held: true, unit: 40, stageHeight: 1000)
        XCTAssertEqual(roomy, 40 * 3.0, accuracy: 0.001, "A held focal word at its full size")
        XCTAssertLessThan(StageLyricView.hingeFocalSize(slabs: one, held: false, unit: 40, stageHeight: 1000), roomy)
        let short = StageLyricView.hingeFocalSize(slabs: one, held: true, unit: 40, stageHeight: 200)
        XCTAssertLessThanOrEqual(short * 1.18 + 40 * 0.62 * 1.2 + 40 * 0.12, 200 * 0.88 + 0.01)
        let two = one + [(head: true, foot: true, focalReading: false, smallReading: false)]
        XCTAssertLessThan(StageLyricView.hingeFocalSize(slabs: two, held: true, unit: 40, stageHeight: 360),
                          StageLyricView.hingeFocalSize(slabs: one, held: true, unit: 40, stageHeight: 360))
    }

    // MARK: L5, L16: one exit

    func testEveryStageLayoutLeavesOnTheSharedLineExit() {
        let exit = 10.0
        XCTAssertEqual(StageMotion.leave(at: exit, exitStart: exit), 0)
        XCTAssertEqual(StageMotion.leave(at: exit + WallpaperMotion.lineExit, exitStart: exit), 1)
        // Smoothstep over 0.5 s was only 35% gone at 0.2 s, half the speed of every other exit.
        XCTAssertGreaterThan(StageMotion.leave(at: exit + 0.2, exitStart: exit), 0.85)
        XCTAssertEqual(StageMotion.leave(at: exit + 0.2, exitStart: exit), vc_settle(0.2, WallpaperMotion.lineExit), accuracy: 1e-9)
    }

    /// Hinge leaves by closing the book, inside the shared exit span (the
    /// line is dropped 0.70 s after its exit starts): the pages fold shut
    /// accelerating, the read page is gone at once, and the closed book
    /// recedes and is gone; the next line's book arrives moving the same
    /// way, already in motion.
    func testHingeClosesTheBookWithinTheExitSpan() {
        let exit = 10.0
        let done = StageLyricView.hingeExit(exitStart: exit, at: exit + LyricPhraseTiming.exitSpan)
        XCTAssertEqual(done.opacity, 0, accuracy: 1e-9, "Nothing of the line is left when it is dropped")
        XCTAssertLessThanOrEqual(StageMotion.hingeClose + StageMotion.hingeRecede, LyricPhraseTiming.exitSpan)
        // Ease-in: the fold accelerates.
        let early = StageLyricView.hingeExit(exitStart: exit, at: exit + 0.1).fold
        let middle = StageLyricView.hingeExit(exitStart: exit, at: exit + 0.2).fold
        let late = StageLyricView.hingeExit(exitStart: exit, at: exit + 0.3).fold
        XCTAssertLessThan(middle - early, late - middle)
        XCTAssertEqual(StageLyricView.hingeExit(exitStart: exit, at: exit + StageMotion.hingeClose).fold, 1)
        // The read page goes first; the book is still there, folding.
        let start = StageLyricView.hingeExit(exitStart: exit, at: exit + StageMotion.hingeReadGone)
        XCTAssertEqual(start.readFade, 0, accuracy: 1e-9)
        XCTAssertEqual(start.opacity, 1, accuracy: 1e-9)
        let folding = StageLyricView.hingeInstances(turns: [1, 0], exit: (0.5, 0), reduceMotion: false)
        XCTAssertEqual(folding.map(\.page), [1], "The dimmed page does not linger as the book closes")
        XCTAssertEqual(folding.first?.angle ?? 0, StageMotion.hingeShutAngle * 0.5, accuracy: 1e-9)
        // Leaving moves the book on; arriving, the next book is still on its
        // way in the same direction and moving (never from rest).
        XCTAssertGreaterThan(StageLyricView.hingeExit(exitStart: exit, at: exit + 0.5).travel, 0.5)
        let arriving = StageLyricView.hingeEntry(firstStart: 11, at: 11 - StageMotion.lead)
        let next = StageLyricView.hingeEntry(firstStart: 11, at: 11 - StageMotion.lead + 1.0 / 60)
        XCTAssertEqual(arriving, 1, accuracy: 1e-9)
        XCTAssertGreaterThan(arriving - next, 0.05, "Already moving on its first frame")
        // Before the exit the last page lies flat, the one before it read.
        let starts = [1.0, 3.0, 5.0]
        let before = StageLyricView.hingeTurns(pageStarts: starts, lineTimed: false, exitStart: exit, at: exit + 0.3, speed: 1)
        XCTAssertEqual(before, [1, 1, 0], "The last page never turns: the book closes instead")
    }

    // MARK: L11: Reduce Motion

    func testReduceMotionDissolvesEachStepAndFableNeedsNone() {
        let line = runs(4, step: 0.6)
        let onset = line[2].start
        let shown = StageLyricView.shownRuns(line, at: onset + 0.05)
        let step = StageLyricView.reducedDissolve(shown, composition: .torque, at: onset + 0.05, reduceMotion: true)
        XCTAssertNotNil(step)
        XCTAssertGreaterThan(step ?? 0, 0)
        XCTAssertLessThan(step ?? 1, 1)
        XCTAssertNil(StageLyricView.reducedDissolve(shown, composition: .torque, at: onset + 0.05, reduceMotion: false))
        XCTAssertNil(StageLyricView.reducedDissolve(shown, composition: .fable, at: onset + 0.05, reduceMotion: true))
        let later = StageLyricView.shownRuns(line, at: onset + 0.3)
        XCTAssertNil(StageLyricView.reducedDissolve(later, composition: .torque, at: onset + 0.3, reduceMotion: true),
                     "Over after the reduced dissolve")
        // Ranks and arrivals are steps, so nothing travels between dissolves.
        let ranks = StageLyricView.ranks(shown, at: onset + 0.05, speed: 1, seed: 3, reduceMotion: true)
        XCTAssertEqual(ranks, [2, 1, 0])
    }

    // MARK: L15: entrances mean the same everywhere

    func testChosenEntrancesMatchTheOtherLayouts() throws {
        for style in [WordArrival.lift, .pop, .orbit, .glide, .unfold, .cutoutFold, .cutoutSlide] {
            let stage = try XCTUnwrap(StageLyricView.chosenEntrance(style, progress: 0.3, size: 40, side: -1, reduceMotion: false))
            let elsewhere = WordEntrance.transform(progress: 0.3, arrival: 0.3, style: style, size: 40, direction: -1, reduceMotion: false)
            XCTAssertEqual(stage, elsewhere, style.title)
        }
        XCTAssertEqual(StageLyricView.chosenEntrance(.lift, progress: 0, size: 40, side: 1, reduceMotion: false)?.offset.height ?? 0,
                       40 * 0.48, accuracy: 0.001, "Float up rises 0.48 of the size, not 0.7")
        XCTAssertNil(StageLyricView.chosenEntrance(.fade, progress: 0, size: 40, side: 1, reduceMotion: false), "The layout's own")
        XCTAssertNil(StageLyricView.chosenEntrance(.lift, progress: 0, size: 40, side: 1, reduceMotion: true))
    }

    // MARK: L18: glued syllables

    func testGluedSyllablesFadeInRatherThanCut() {
        let words = [LyricsFetcher.Word(text: "beau", start: 1, end: 1.2), LyricsFetcher.Word(text: "ti", start: 1.3, end: 1.5),
                     LyricsFetcher.Word(text: "ful", start: 1.6, end: 2)]
        let run = StageRun.make(words, glue: [false, true, true])[0]
        XCTAssertEqual(run.parts.count, 3)
        XCTAssertEqual(StageRun.partOpacity(0, of: run.parts, at: 0.97), 1, "The first syllable arrives with its word")
        XCTAssertEqual(StageRun.partOpacity(1, of: run.parts, at: 1.26), 0)
        let midway = StageRun.partOpacity(1, of: run.parts, at: 1.33)
        XCTAssertGreaterThan(midway, 0.05)
        XCTAssertLessThan(midway, 0.99, "Fading, not cut in")
        XCTAssertEqual(StageRun.partOpacity(1, of: run.parts, at: 1.3 - 0.03 + StageMotion.syllableFade), 1)
        // The fading syllable is drawn in the word's own colour at that opacity.
        let text = run.revealed(at: 1.33, colour: .white)
        func colour(_ text: AttributedString, _ part: String) -> Color? {
            text.range(of: part).flatMap { text[$0].foregroundColor }
        }
        XCTAssertNil(colour(text, "beau"), "Sung: the word's own style")
        XCTAssertEqual(colour(text, "ti"), Color.white.opacity(midway))
        XCTAssertEqual(colour(text, "ful"), .clear, "Not yet sung")
        // Without a colour it appears at its onset, as before.
        XCTAssertNil(colour(run.revealed(at: 1.33), "ti"))
    }

    // MARK: L19: Mosaic's morph

    func testMosaicNeverShowsBothCopiesAtDifferentSizes() {
        for ratio in [0.2, 0.26] {
            for step in 0...100 {
                let settle = Double(step) / 100
                let morph = StageMotion.mosaicMorph(settle: settle, smallRatio: ratio)
                if morph.bigOpacity > 0.01, morph.smallOpacity > 0.01 {
                    XCTAssertEqual(morph.bigScale, ratio, accuracy: 0.005, "Two sizes at once at settle \(settle)")
                }
            }
            XCTAssertEqual(StageMotion.mosaicMorph(settle: 0, smallRatio: ratio).bigScale, 1)
            XCTAssertEqual(StageMotion.mosaicMorph(settle: 0, smallRatio: ratio).smallOpacity, 0)
            XCTAssertEqual(StageMotion.mosaicMorph(settle: 1, smallRatio: ratio).bigOpacity, 0)
            XCTAssertEqual(StageMotion.mosaicMorph(settle: 1, smallRatio: ratio).smallOpacity, 1)
        }
    }

    // MARK: Drawing

    /// Every layout draws through hand-overs, exits and Reduce Motion
    /// dissolves. With LUNALYR_STAGE_SNAPSHOT_DIR set, writes a sheet of the
    /// hand-over frames (the live word restyling) for a look.
    func testEveryLayoutDrawsThroughHandOversAndReduceMotion() throws {
        let name = "LunalyrTests.\(UUID().uuidString)"
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
        let settings = Settings(defaults: try XCTUnwrap(UserDefaults(suiteName: name)), notifications: NotificationCenter())
        let texts = "Rain over the city can fly away tonight".split(separator: " ").map(String.init)
        let words = texts.enumerated().map { i, w in LyricsFetcher.Word(text: w, start: 1 + Double(i) * 0.6, end: 1.5 + Double(i) * 0.6) }
        let stage = CGSize(width: 480, height: 240)
        // From just before the fourth word to just after it, then the exit.
        let times: [Double] = [2.75, 2.8, 2.85, 2.9, 3.0, 3.15, 6.0, 6.2]
        let kinds = LyricComposition.allCases.filter(\.fillsStage)
        for reduceMotion in [false, true] {
            let sheet = VStack(spacing: 2) {
                ForEach(kinds) { kind in
                    HStack(spacing: 2) {
                        ForEach(times, id: \.self) { t in
                            ZStack {
                                LinearGradient(colors: [Color(red: 0.16, green: 0.1, blue: 0.3), Color(red: 0.05, green: 0.05, blue: 0.12)],
                                               startPoint: .topLeading, endPoint: .bottomTrailing)
                                StageLyricView(words: words, glue: [Bool](repeating: false, count: words.count), time: t,
                                               stageSize: stage, baseSize: 32, mood: .neutral, composition: kind, seed: 3,
                                               exitStart: 6, settings: settings)
                                Text(String(format: "%@ %.2fs", kind.title, t)).font(.system(size: 10)).foregroundStyle(.white.opacity(0.6))
                                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(4)
                            }
                            .frame(width: stage.width, height: stage.height)
                        }
                    }
                }
            }
            .environment(\.colorScheme, .dark)
            .environment(\.lyricInk, LyricInk())
            .environment(\.lyricReduceMotion, reduceMotion)
            let renderer = ImageRenderer(content: sheet)
            renderer.scale = 0.5
            let image = try XCTUnwrap(renderer.nsImage)
            if let directory = ProcessInfo.processInfo.environment["LUNALYR_STAGE_SNAPSHOT_DIR"] {
                try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)
                let rep = NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation))
                try rep?.representation(using: .png, properties: [:])?
                    .write(to: URL(fileURLWithPath: directory + (reduceMotion ? "/handover-reduced.png" : "/handover.png")))
            }
        }
    }
}
