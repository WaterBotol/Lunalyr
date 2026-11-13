import SwiftUI
import XCTest
@testable import VersoCustom

/// Torque's phrase choreography (the turning column of 14 Nov 2026), without
/// rendering: faces by phrase, rows by grammar, roles, the column's turn, and
/// the poses words take as they arrive.
@MainActor
final class TorqueLayoutTests: XCTestCase {
    typealias Word = LyricsFetcher.Word

    private func plan(_ words: [Word], glue: [Bool]? = nil) -> (StageLyricView.TorquePlan, [StageRun]) {
        let runs = StageRun.make(words, glue: glue ?? [Bool](repeating: false, count: words.count))
        return (StageLyricView.makeTorquePlan(runs, phrasing: StagePhrases.analyse(runs)), runs)
    }

    private func text(_ rows: [[Int]], _ runs: [StageRun]) -> [String] {
        rows.map { row in row.map { runs[$0].text }.joined(separator: " ") }
    }

    /// One face per phrase; the focal word's group gets its own row, so the
    /// grammar decides whether the big word is at the top or the foot.
    func testFacesAndRowsFollowThePhrases() {
        let (plan, runs) = plan(StagePhrasesTests.mixed)
        XCTAssertEqual(plan.faces.count, 3)
        XCTAssertEqual(text(plan.faces[0].rows, runs), ["and we keep", "running"])
        XCTAssertEqual(text(plan.faces[1].rows, runs), ["through the city", "lights"])
        XCTAssertEqual(text(plan.faces[2].rows, runs), ["until the morning", "comes"])
        XCTAssertEqual(plan.roles[3], .focal)
        XCTAssertEqual(plan.roles[5], .function)
        XCTAssertEqual(plan.roles[6], .content)
        XCTAssertEqual(plan.roles.filter { $0 == .focal }.count, 3, "One focal word a face")
    }

    func testRowsPutTheFocalGroupOnItsOwnRow() {
        let function: Set<Int> = [0, 3]
        // "the long road home": the focal word in the middle takes the middle row.
        XCTAssertEqual(StageLyricView.torqueRows([0, 1, 2, 3, 4], focal: 2, isFunction: { function.contains($0) }),
                       [[0, 1], [2], [3, 4]])
        // A trailing function word joins the word before it.
        XCTAssertEqual(StageLyricView.torqueRows([0, 1, 2, 3], focal: 1, isFunction: { $0 == 3 || $0 == 0 }),
                       [[0, 1], [2, 3]])
        XCTAssertEqual(StageLyricView.torqueRows([7], focal: 7, isFunction: { _ in true }), [[7]])
    }

    /// Neighbouring faces are never composed the same: they alternate sides.
    func testNeighbouringFacesAlternate() {
        let (plan, _) = plan(StagePhrasesTests.mixed)
        for (a, b) in zip(plan.faces, plan.faces.dropFirst()) {
            XCTAssertNotEqual(a.flushTrailing, b.flushTrailing)
        }
    }

    /// A quick run rises as one group: its members share the first onset.
    /// A held word keeps being wrung tighter while it is sung.
    func testRunsShareAClockAndHeldWordsTighten() {
        let (plan, runs) = plan(StagePhrasesTests.mixed)
        XCTAssertEqual(plan.clocks[0], runs[0].start)
        XCTAssertEqual(plan.clocks[1], runs[0].start)
        XCTAssertEqual(plan.clocks[2], runs[0].start, "keep rises with and/we")
        XCTAssertEqual(plan.clocks[4], runs[4].start)
        XCTAssertGreaterThan(plan.held[3], 1, "running is held")
        let running = runs[3]
        func depth(_ t: Double) -> Double {
            StageLyricView.torquePose(running, role: .focal, clock: running.start, held: plan.held[3], lineTimed: false,
                                      at: t, arrived: true, reduceMotion: false).depth
        }
        XCTAssertLessThan(depth(running.start + 0.5), depth(running.start + 1.2))
        XCTAssertEqual(depth(running.start + 2), depth(running.start + 4), "Holds still once sung")
    }

    /// Only the focal word is wrung; content words rise, function words only appear.
    func testSignatureMoveBelongsToTheFocalWord() {
        let (plan, runs) = plan(StagePhrasesTests.mixed)
        func pose(_ id: Int, _ dt: Double) -> StageLyricView.TorquePose {
            StageLyricView.torquePose(runs[id], role: plan.roles[id], clock: plan.clocks[id], held: plan.held[id],
                                      lineTimed: false, at: runs[id].start + dt, arrived: true, reduceMotion: false)
        }
        XCTAssertGreaterThan(pose(3, 0).wring, 30, "running arrives wrung")
        XCTAssertEqual(pose(6, 0).wring, 0, "city is not wrung")
        XCTAssertGreaterThan(pose(6, 0).rise, 0, "city rises")
        XCTAssertEqual(pose(5, 0).wring, 0)
        XCTAssertEqual(pose(5, 0).rise, 0, "the only appears")
        for id in [3, 5, 6] {
            XCTAssertEqual(pose(id, 1.5).wring, 0, accuracy: 1e-9, "\(runs[id].text) has landed")
            XCTAssertEqual(pose(id, 1.5).rise, 0, accuracy: 1e-9)
        }
        // Not before its beat; at once under Reduce Motion.
        XCTAssertEqual(pose(6, -0.1).opacity, 0)
        let reduced = StageLyricView.torquePose(runs[3], role: .focal, clock: runs[3].start, held: 0, lineTimed: false,
                                                at: runs[3].start, arrived: true, reduceMotion: true)
        XCTAssertEqual(reduced, StageLyricView.TorquePose())
    }

    /// The column's turn never jumps, ends facing each phrase as it is sung,
    /// and no more than two faces are ever on screen.
    func testColumnTurnsSmoothlyAndShowsAtMostTwoFaces() {
        let words = FableRenderTests.cases[1].words
        let (plan, runs) = plan(words)
        XCTAssertGreaterThan(plan.faces.count, 3)
        var previous: Double?
        var t = runs[0].start - 0.2
        while t < (runs.last?.start ?? 0) + 1 {
            let rho = StageLyricView.torqueRotation(plan, at: t, shownCount: 0, reduceMotion: false)
            if let previous { XCTAssertLessThan(abs(rho - previous), 0.06, "The column jumped at \(t)") }
            previous = rho
            let visible = plan.faces.indices.filter { abs(Double($0) - rho) < 0.999 }
            XCTAssertLessThanOrEqual(visible.count, 2, "Too many faces at \(t)")
            t += 1.0 / 240
        }
        for (index, face) in plan.faces.enumerated() {
            // Facing the phrase (within the overshoot) a moment after its first word.
            let rho = StageLyricView.torqueRotation(plan, at: face.start + 0.25, shownCount: 0, reduceMotion: false)
            XCTAssertEqual(rho, Double(index), accuracy: 0.12, "face \(index)")
            if index > 0 {
                XCTAssertLessThanOrEqual(face.turnStart, face.start - StageMotion.lead)
                XCTAssertGreaterThan(face.turnStart, plan.faces[index - 1].runs.map { runs[$0].start }.max() ?? 0)
            }
        }
        // Reduce Motion steps to the newest word's face.
        XCTAssertEqual(StageLyricView.torqueRotation(plan, at: 99, shownCount: runs.count, reduceMotion: true),
                       Double(plan.faces.count - 1))
    }

    /// The line enters mid-turn and leaves turning on the same way.
    func testLineEntersAndLeavesTurningTheSameWay() {
        let (plan, runs) = plan(StagePhrasesTests.mixed)
        let first = runs[0].start - StageMotion.lead
        XCTAssertEqual(StageLyricView.torqueSwing(plan, at: first, exitStart: .infinity, reduceMotion: false), 45,
                       accuracy: 1e-9, "Enters already turning")
        XCTAssertEqual(StageLyricView.torqueSwing(plan, at: first + 1, exitStart: .infinity, reduceMotion: false), 0,
                       accuracy: 1e-9)
        let exit = 8.0
        let leaving = StageLyricView.torqueSwing(plan, at: exit + 0.2, exitStart: exit, reduceMotion: false)
        XCTAssertLessThan(leaving, 0, "Leaves turning on to the left, the way the column turns")
        XCTAssertEqual(StageLyricView.torqueLeaving(at: exit + 1, exitStart: exit), 1)
        XCTAssertEqual(StageLyricView.torqueSwing(plan, at: exit + 0.2, exitStart: exit, reduceMotion: true), 0)
    }

    /// A line-timed line is one face, a row per phrase.
    func testLineTimedLineIsOneFace() {
        let words = "rain over the city can fly away tonight".split(separator: " ").map {
            Word(text: String($0), start: 2, end: 6)
        }
        let (plan, _) = plan(words)
        XCTAssertTrue(plan.lineTimed)
        XCTAssertEqual(plan.faces.count, 1)
        XCTAssertGreaterThan(plan.faces[0].rows.count, 1)
    }

    /// Japanese glued runs: each run stays whole in a row, and every run is on a face.
    func testJapaneseRunsStayWhole() {
        let line = StageReadingTests.japanese
        let (plan, runs) = plan(line.words, glue: line.glue)
        XCTAssertEqual(plan.faces.flatMap(\.runs), Array(runs.indices))
        XCTAssertEqual(plan.faces.flatMap { $0.rows.flatMap { $0 } }, Array(runs.indices))
    }

    /// The face layout: rows share a baseline, flush to one side, mirrored
    /// for right to left; the turn squashes about the centre.
    func testFaceLayoutAndTurn() {
        let sizes = [CGSize(width: 100, height: 60), CGSize(width: 80, height: 40), CGSize(width: 300, height: 120)]
        let baselines: [CGFloat] = [48, 32, 96]
        let left = TorqueFaceLayout.arrange(sizes, baselines: baselines, rows: [[0, 1], [2]], trailing: false,
                                            rightToLeft: false, wordGap: 10, rowGap: 0)
        XCTAssertEqual(left.size.width, 300)
        XCTAssertEqual(left.frames[0].minX, 0)
        XCTAssertEqual(left.frames[0].minY + 48, left.frames[1].minY + 32, accuracy: 1e-9, "Shared baseline")
        let right = TorqueFaceLayout.arrange(sizes, baselines: baselines, rows: [[0, 1], [2]], trailing: true,
                                             rightToLeft: true, wordGap: 10, rowGap: 0)
        XCTAssertEqual(right.frames[0].maxX, 300, accuracy: 1e-9, "Right to left: the first word on the right")
        XCTAssertLessThan(right.frames[1].maxX, right.frames[0].minX)
        let size = CGSize(width: 200, height: 100)
        let flat = CGRect(origin: .zero, size: size).applying(TorqueTurn.transform(angle: 0, size: size, available: nil))
        XCTAssertEqual(flat, CGRect(origin: .zero, size: size))
        let turned = CGRect(origin: .zero, size: size).applying(TorqueTurn.transform(angle: 60, size: size, available: nil))
        XCTAssertLessThan(turned.width, 110)
        XCTAssertEqual(turned.midX, 100, accuracy: 0.5)
        let swung = CGRect(origin: .zero, size: size)
            .applying(TorqueTurn.transform(angle: 60, size: size, available: CGSize(width: 1000, height: 1000)))
        XCTAssertGreaterThan(swung.midX, 150, "A face turned to the right swings round the column")
    }
}
