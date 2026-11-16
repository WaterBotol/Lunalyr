import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Line continuity: lines sung close together form a run (`LyricPhraseRuns`)
/// and the carrying layouts' stage motion runs on through it
/// (`StageCarry`), settling and starting again only after a real break.
@MainActor
final class LyricPhraseRunsTests: XCTestCase {
    typealias Word = LyricsFetcher.Word
    typealias Line = LyricsFetcher.Line

    /// A word-timed line of `texts`, a word every `step` seconds from `start`.
    private func line(_ texts: [String], start: Double, step: Double = 0.4, wordTimed: Bool = true) -> Line {
        let words = texts.enumerated().map { i, text in
            Word(text: text, start: start + Double(i) * step, end: start + Double(i) * step + step * 0.9)
        }
        return Line(start: start, end: words.last.map(\.end) ?? start, text: texts.joined(separator: " "),
                    words: words, isInstrumental: false, wordTimed: wordTimed)
    }

    /// Lines with the given gaps between one line's last word end and the next line's first word.
    private func lines(gaps: [Double]) -> [Line] {
        var result = [line(["we", "keep", "running"], start: 10)]
        for gap in gaps {
            let end = result.last!.words.map(\.end).max()!
            result.append(line(["through", "the", "night"], start: end + gap))
        }
        return result
    }

    private func carry(_ row: Int, in lines: [Line]) -> StageCarry? {
        StageCarry.make(row, runs: LyricPhraseRuns(lines: lines), lines: lines) { _ in true }
    }

    // MARK: Runs

    func testLinesSungCloseTogetherShareARun() {
        let lines = lines(gaps: [0.3, 2.9])
        let runs = LyricPhraseRuns(lines: lines)
        XCTAssertEqual(runs.lines.map { $0?.run }, [0, 0, 0])
        XCTAssertEqual(runs.lines.map { $0?.ordinal }, [0, 1, 2])
        XCTAssertEqual(runs.lines.map { $0?.wordsBefore }, [0, 3, 6])
        XCTAssertEqual(runs.lines.map { $0?.runStart }, [10, 10, 10])
        XCTAssertNil(runs[0]?.incoming)
        XCTAssertEqual(runs[0]?.outgoing, .init(lastOnset: 10.8, nextOnset: lines[1].words[0].start))
        XCTAssertEqual(runs[1]?.incoming, runs[0]?.outgoing)
        XCTAssertNotNil(runs[1]?.outgoing)
        XCTAssertNil(runs[2]?.outgoing, "the song's last line hands on to nothing")
        XCTAssertEqual(Set(runs.lines.map { $0?.seed }).count, 1, "one seed for the whole run")
    }

    func testTheThresholdIsOneNamedGap() {
        XCTAssertEqual(LyricPhraseRuns.breakGap, 3.5)
        let under = LyricPhraseRuns(lines: lines(gaps: [LyricPhraseRuns.breakGap - 0.01]))
        let over = LyricPhraseRuns(lines: lines(gaps: [LyricPhraseRuns.breakGap + 0.01]))
        XCTAssertEqual(under[1]?.run, 0)
        XCTAssertNotNil(under[1]?.incoming)
        XCTAssertEqual(over[1]?.run, 1, "a pause over the threshold starts a new run")
        XCTAssertNil(over[1]?.incoming)
        XCTAssertNil(over[0]?.outgoing)
        XCTAssertEqual(over[1]?.ordinal, 0)
        XCTAssertEqual(over[1]?.wordsBefore, 0)
        XCTAssertNotEqual(over[0]?.seed, over[1]?.seed)
    }

    func testAnInstrumentalBreaksTheRunEvenWhenItIsShort() {
        var song = lines(gaps: [0.5, 0.5])
        song.insert(Line(start: song[1].start - 0.2, end: song[1].start - 0.1, text: "", words: [],
                         isInstrumental: true), at: 1)
        let runs = LyricPhraseRuns(lines: song)
        XCTAssertNil(runs[1], "an instrumental is in no run")
        XCTAssertNil(runs[0]?.outgoing)
        XCTAssertNil(runs[2]?.incoming)
        XCTAssertEqual(runs[2]?.run, 1)
        XCTAssertEqual(runs[3]?.run, 1, "and the next lines carry on a new run")
        XCTAssertNotNil(runs[3]?.incoming)
    }

    func testASwitchBetweenWordAndLineTimingBreaksTheRun() {
        var song = lines(gaps: [0.5])
        song[1].wordTimed = false
        let runs = LyricPhraseRuns(lines: song)
        XCTAssertNil(runs[0]?.outgoing)
        XCTAssertEqual(runs[1]?.run, 1)
    }

    /// The gap counts from when the singing so far stopped: an earlier,
    /// longer note sung over a short line keeps the run going.
    func testTheGapCountsFromTheLatestSinging() {
        let long = Line(start: 10, end: 16, text: "oh", words: [Word(text: "oh", start: 10, end: 16)],
                        isInstrumental: false, wordTimed: true)
        let short = line(["yeah"], start: 10.2)
        let after = line(["again"], start: 19)
        let runs = LyricPhraseRuns(lines: [long, short, after])
        XCTAssertEqual(runs[2]?.run, 0, "19 is 3 s after the note ends at 16, not 8 s after 'yeah'")
        XCTAssertNil(runs[0]?.outgoing, "a line still sung over the next is held, not carried")
    }

    /// A line-timed line's end that only marks the next line's start is not
    /// singing: the gap counts from its estimated words' end.
    func testALineTimedLineEndThatIsOnlyTheNextStartIsNotSinging() {
        let first = Line(start: 10, end: 30, endFromNextLine: true, text: "a short line",
                         words: LyricsFetcher.distributeWords(in: "a short line", from: 10, to: 12),
                         isInstrumental: false, wordTimed: false)
        let second = Line(start: 30, end: 32, text: "after a break",
                          words: LyricsFetcher.distributeWords(in: "after a break", from: 30, to: 32),
                          isInstrumental: false, wordTimed: false)
        let runs = LyricPhraseRuns(lines: [first, second])
        XCTAssertEqual(runs[1]?.run, 1)
    }

    /// Worked out from the lyrics alone: built again (after a seek, a new
    /// scene) it is the same, line for line.
    func testRunsAreTheSameHoweverTheyAreReached() {
        let song = lines(gaps: [0.4, 5, 1, 0.2, 9, 0.6])
        XCTAssertEqual(LyricPhraseRuns(lines: song), LyricPhraseRuns(lines: song))
        let runs = LyricPhraseRuns(lines: song)
        XCTAssertEqual(runs.lines.map { $0?.run }, [0, 0, 1, 1, 1, 2, 2])
        let cache = LyricLayerCache()
        XCTAssertEqual(cache.phraseRuns(for: song, fingerprint: 7), runs)
        _ = cache.phraseRuns(for: song, fingerprint: 7)
        _ = cache.phraseRuns(for: song, fingerprint: 7)
        XCTAssertEqual(cache.phraseRunBuilds, 1, "built once per song, never per frame")
    }

    // MARK: Holding the stage until the hand-over

    func testACarryingLineStaysOnStageUntilItsHandOver() {
        let song = lines(gaps: [2])
        let rolls = { (_: Int) in false }
        let held = { (row: Int) in StageCarry.heldUntil(self.carry(row, in: song), composition: .torque) }
        let handOver = StageMotion.torqueHandover(try! XCTUnwrap(carry(0, in: song)?.outgoing))
        XCTAssertGreaterThanOrEqual(handOver, song[1].start)
        let during = handOver + 0.2
        XCTAssertFalse(LyricLayer.isLeaving(0, current: 1, lines: song, time: during, rolls: rolls),
                       "on its own the line left soon after its last word")
        XCTAssertTrue(LyricLayer.isLeaving(0, current: 1, lines: song, time: during, heldUntil: held, rolls: rolls))
        // The exit the renderer draws, and the one the frame rate follows, agree.
        let hold = try! XCTUnwrap(held(0))
        let pose = LyricPhraseTiming(line: song[0], nextStart: song[1].start, time: during, heldUntil: hold)
        XCTAssertEqual(LyricLayer.stageExitStart(song[0], pose: pose, heldUntil: hold), hold)
        XCTAssertEqual(LyricLayer.renderedExitStart(0, in: song, handOver: song[1].start, rolls: false, fillsStage: true,
                                                    heldUntil: hold), hold)
        // Layouts that do not carry keep their own exits.
        XCTAssertNil(StageCarry.heldUntil(carry(0, in: song), composition: .mosaic))
        XCTAssertNil(StageCarry.heldUntil(carry(0, in: song), composition: .fable))
        // After a long gap nothing is held.
        let apart = lines(gaps: [6])
        XCTAssertNil(StageCarry.heldUntil(carry(0, in: apart), composition: .torque))
    }

    // MARK: Torque

    private func torque(_ line: Line) -> (StageLyricView.TorquePlan, [StageRun]) {
        let runs = StageRun.make(line.words, glue: [Bool](repeating: false, count: line.words.count))
        return (StageLyricView.makeTorquePlan(runs, phrasing: StagePhrases.analyse(runs)), runs)
    }

    /// The angle a face is drawn at (`torque(_:shown:)`), from the column's
    /// rotation and swing.
    private func torqueAngle(face: Int, plan: StageLyricView.TorquePlan, carry: StageCarry?, exitStart: Double,
                             at time: Double) -> Double {
        let rho = StageLyricView.torqueRotation(plan, at: time, shownCount: 0, reduceMotion: false, carry: carry)
        let swing = StageLyricView.torqueSwing(plan, at: time, exitStart: exitStart, reduceMotion: false, carry: carry)
        return (Double(face) - rho) * 90 + swing
    }

    /// Inside a run the column turns on from one line's last face into the
    /// next line's first as one column: the two faces stay a quarter turn
    /// apart all through the hand-over, the turn's speed has no step, and
    /// neither line fades or swings on its own.
    func testTorqueTurnsOnIntoTheNextLine() throws {
        let song = lines(gaps: [1.5])
        let (outPlan, _) = torque(song[0])
        let (inPlan, _) = torque(song[1])
        let outCarry = try XCTUnwrap(carry(0, in: song))
        let inCarry = try XCTUnwrap(carry(1, in: song))
        let hold = try XCTUnwrap(StageCarry.heldUntil(outCarry, composition: .torque))
        let start = StageMotion.torqueHandover(try XCTUnwrap(outCarry.outgoing))
        let last = outPlan.faces.count - 1
        var previous: Double?
        var previousStep: Double?
        for step in -30...60 {
            let t = start + Double(step) / 120
            let outgoing = torqueAngle(face: last, plan: outPlan, carry: outCarry, exitStart: hold, at: t)
            let incoming = torqueAngle(face: 0, plan: inPlan, carry: inCarry, exitStart: .infinity, at: t)
            XCTAssertEqual(incoming - outgoing, 90, accuracy: 1e-9, "one column at \(t)")
            if let previous {
                let stepSize = outgoing - previous
                XCTAssertLessThan(abs(stepSize), 12, "the column jumped at \(t)")
                if let previousStep { XCTAssertLessThan(abs(stepSize - previousStep), 2.5, "the turn lurched at \(t)") }
                previousStep = stepSize
            }
            previous = outgoing
        }
        XCTAssertEqual(torqueAngle(face: last, plan: outPlan, carry: outCarry, exitStart: hold, at: start - 0.01), 0,
                       accuracy: 1e-9, "faces the viewer until the hand-over")
        XCTAssertEqual(torqueAngle(face: 0, plan: inPlan, carry: inCarry, exitStart: .infinity, at: start + 1), 0,
                       accuracy: 1e-6, "and the next line faces it after")
        XCTAssertEqual(StageLyricView.torqueFade(at: hold - 0.01, exitStart: hold), 0)
    }

    /// After a long gap Torque starts again as before: the new line swings
    /// in from 45 degrees, the old one turns away and fades on its own.
    func testTorqueStartsAgainAfterALongGap() {
        let song = lines(gaps: [5])
        XCTAssertNil(carry(0, in: song)?.outgoing)
        XCTAssertNil(carry(1, in: song)?.incoming)
        let (plan, runs) = torque(song[1])
        let first = runs[0].start - StageMotion.lead
        XCTAssertEqual(torqueAngle(face: 0, plan: plan, carry: carry(1, in: song), exitStart: .infinity, at: first), 45,
                       accuracy: 1e-9)
    }

    // MARK: Hinge

    /// Inside a run Hinge's last page turns over onto the next line's first
    /// page, as a page turns within a line, on the one spine; the book does
    /// not close and the next book does not slide in.
    func testHingeTurnsItsLastPageOntoTheNextLine() throws {
        let song = lines(gaps: [1.2])
        let outCarry = try XCTUnwrap(carry(0, in: song))
        let inCarry = try XCTUnwrap(carry(1, in: song))
        let start = StageMotion.hingeHandover(try XCTUnwrap(outCarry.outgoing))
        let pageStarts = [song[0].words[0].start]
        func turn(_ t: Double) -> Double {
            StageLyricView.hingeTurns(pageStarts: pageStarts, lineTimed: false, exitStart: .infinity, at: t, speed: 1,
                                      carry: outCarry).last!
        }
        XCTAssertEqual(turn(start - 0.01), 0)
        XCTAssertEqual(turn(start + StageMotion.hingeTurn), 1, accuracy: 1e-9)
        var previous = turn(start - 0.05)
        for step in 1...120 {
            let now = turn(start - 0.05 + Double(step) / 120)
            XCTAssertLessThan(abs(now - previous), 0.06, "the page jumped")
            previous = now
        }
        // On its own the last page never turns: the book closes instead.
        XCTAssertEqual(StageLyricView.hingeTurns(pageStarts: pageStarts, lineTimed: false, exitStart: .infinity,
                                                 at: start + 1, speed: 1).last, 0)
        let hold = try XCTUnwrap(StageCarry.heldUntil(outCarry, composition: .hinge))
        let exit = StageLyricView.hingeExit(exitStart: hold, at: hold + 0.2, carried: true)
        XCTAssertEqual(exit.fold, 0, "a carried line does not close the book")
        XCTAssertEqual(exit.travel, 0)
        XCTAssertTrue(StageLyricView.hingeCarries(outCarry) && StageLyricView.hingeCarries(inCarry),
                      "both lines open on the middle spine")
        XCTAssertFalse(StageLyricView.hingeCarries(carry(1, in: lines(gaps: [6]))))
    }

    // MARK: Portal

    /// Inside a run every line stands in the same column; a new run may stand elsewhere.
    func testPortalKeepsItsColumnThroughARun() throws {
        let song = lines(gaps: [0.6, 0.6, 8])
        func placement(_ row: Int) -> (flipX: CGFloat, reach: Double, shiftX: CGFloat, lift: CGFloat) {
            // A different value for every line's own seed, as `chance` gives.
            StageLyricView.portalPlacement(carry: carry(row, in: song)) { salt in Double((row * 37 + salt * 11) % 100) / 100 }
        }
        for row in 1...2 {
            XCTAssertEqual(placement(row).flipX, placement(0).flipX)
            XCTAssertEqual(placement(row).reach, placement(0).reach)
            XCTAssertEqual(placement(row).shiftX, placement(0).shiftX)
            XCTAssertEqual(placement(row).lift, placement(0).lift)
        }
        XCTAssertEqual(carry(2, in: song)?.wordsBefore, 6, "the float's phase runs on word after word")
        let runs = (0..<40).map { StageLyricView.portalPlacement(carry: StageCarry(seed: $0 * 7919, wordsBefore: 0)) { _ in 0 } }
        XCTAssertGreaterThan(Set(runs.map { $0.shiftX }).count, 10, "runs stand in different columns")
        XCTAssertNil(carry(3, in: song)?.incoming)
    }

    // MARK: Echo

    /// Echo's repeats drift on the run's path, so two lines of a run cross-
    /// fade on the same drift instead of jumping to another.
    func testEchoDriftsOnThePathOfItsRun() throws {
        let song = lines(gaps: [0.4])
        let a = try XCTUnwrap(carry(0, in: song)), b = try XCTUnwrap(carry(1, in: song))
        XCTAssertEqual(a.seed, b.seed)
        for slot in 0..<5 {
            let wander = EchoBackdrop.wanderSeed(a.seed, slot: slot)
            XCTAssertEqual(vc_noise2(wander, 12.0 * 0.09), vc_noise2(EchoBackdrop.wanderSeed(b.seed, slot: slot), 12.0 * 0.09))
        }
    }

    // MARK: Drawn

    /// A carrying layout's hand-over as drawn: both lines' stages
    /// composited, as the wallpaper draws an outgoing and an incoming line,
    /// 1/60 s apart. The ink's centre never jumps, and the stage never
    /// empties -- where on its own the old line had already left.
    func testTheHandOverIsDrawnWithoutAJump() throws {
        let name = "LunalyrTests.\(UUID().uuidString)"
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
        let settings = Settings(defaults: try XCTUnwrap(UserDefaults(suiteName: name)), notifications: NotificationCenter())
        let song = lines(gaps: [1.5])
        let outCarry = try XCTUnwrap(carry(0, in: song))
        let inCarry = try XCTUnwrap(carry(1, in: song))
        let stage = CGSize(width: 320, height: 180)
        for composition in [LyricComposition.torque, .hinge, .portal] {
            let hold = try XCTUnwrap(StageCarry.heldUntil(outCarry, composition: composition))
            let hand = try XCTUnwrap(outCarry.outgoing)
            let start: Double = composition == .torque ? StageMotion.torqueHandover(hand)
                : composition == .hinge ? StageMotion.hingeHandover(hand) : StageMotion.portalHandover(hand)
            func frame(_ t: Double, carried: Bool = true) throws -> (ink: Double, x: Double, y: Double) {
                let own = LyricLayer.stageExitStart(song[0],
                    pose: LyricPhraseTiming(line: song[0], nextStart: song[1].start, time: t))
                let exit = carried ? hold : own
                let view = ZStack {
                    Color.black
                    if t < exit + LyricPhraseTiming.exitSpan(for: composition) {
                        StageLyricView(words: song[0].words, glue: [false, false, false], time: t, stageSize: stage,
                                       baseSize: 22, mood: .neutral, composition: composition, seed: 0,
                                       exitStart: exit, carry: carried ? outCarry : nil, settings: settings)
                    }
                    if t >= song[1].start {
                        StageLyricView(words: song[1].words, glue: [false, false, false], time: t, stageSize: stage,
                                       baseSize: 22, mood: .neutral, composition: composition, seed: 1,
                                       carry: carried ? inCarry : nil, settings: settings)
                    }
                }
                .frame(width: stage.width, height: stage.height)
                .environment(\.colorScheme, .dark)
                .environment(\.lyricInk, LyricInk())
                let renderer = ImageRenderer(content: view)
                renderer.scale = 1
                let image = try XCTUnwrap(renderer.cgImage)
                // For a look: LUNALYR_CONTINUITY_DIR (as TEST_RUNNER_LUNALYR_CONTINUITY_DIR).
                if carried, let directory = ProcessInfo.processInfo.environment["LUNALYR_CONTINUITY_DIR"] {
                    try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)
                    let file = String(format: "%@/%@-%06.3f.png", directory, composition.rawValue, t)
                    try NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])?
                        .write(to: URL(fileURLWithPath: file))
                }
                return Self.inkStats(image)
            }
            XCTAssertLessThan(try frame(start - 0.1, carried: false).ink, 1,
                              "\(composition): on its own the line had left and the stage stood empty")
            var previous = try frame(start - 0.1)
            XCTAssertGreaterThan(previous.ink, 50, "\(composition)")
            let full = previous.ink
            // Through the hand-over itself; a later word's own entrance is its own.
            let frames = Int((max(hold - start, 0.5) + 0.1) * 60)
            for step in 1...frames {
                let t = start - 0.1 + Double(step) / 60
                let now = try frame(t)
                XCTAssertGreaterThan(now.ink, 20, "\(composition): the stage emptied at \(t)")
                // A Hinge page passing edge-on is a sliver for a frame or two,
                // as it is turning within a line: its centre is not weighed then.
                if min(now.ink, previous.ink) > 0.25 * full {
                    XCTAssertLessThan(hypot(now.x - previous.x, now.y - previous.y), 24,
                                      "\(composition): the ink jumped at \(t)")
                }
                previous = now
            }
        }
    }

    /// The amount of light ink in an image and its centre.
    static func inkStats(_ image: CGImage) -> (ink: Double, x: Double, y: Double) {
        let width = image.width, height = image.height
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        let context = CGContext(data: &pixels, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                space: CGColorSpaceCreateDeviceRGB(),
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        var mass = 0.0, sx = 0.0, sy = 0.0
        for y in 0..<height {
            for x in 0..<width {
                let i = (y * width + x) * 4
                let light = (Double(pixels[i]) + Double(pixels[i + 1]) + Double(pixels[i + 2])) / (3 * 255)
                guard light > 0.15 else { continue }
                mass += light; sx += light * Double(x); sy += light * Double(y)
            }
        }
        return mass > 0 ? (mass, sx / mass, sy / mass) : (0, 0, 0)
    }
}
