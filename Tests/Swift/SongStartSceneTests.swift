import XCTest
@testable import VersoCustom

/// A song's start as the reporter's log has it, replayed through the lyric
/// layer's scene memory: the song changes, its lyrics are looked up, and
/// Music reports where the song really is about two seconds late -- either
/// just after the lyrics are shown (`lyrics shown … at 0.20`, then `clock
/// +2.07 s: 0.08 → 2.14`) or just before. No scene that is leaving may draw
/// a line the arriving scene draws while both are on screen: that is the
/// first line seen doubled. Lines are judged by when Hinge really draws
/// them (from their start, or their preview, until their exit has run),
/// not by the memory's own estimate.
@MainActor
final class SongStartSceneTests: XCTestCase {
    private func line(_ text: String, start: Double, end: Double) -> LyricsFetcher.Line {
        let parts = text.split(separator: " ").map(String.init)
        let step = (end - start) / Double(parts.count)
        let words = parts.enumerated().map {
            LyricsFetcher.Word(text: $0.element, start: start + Double($0.offset) * step,
                               end: start + Double($0.offset + 1) * step)
        }
        return .init(start: start, end: end, text: text, words: words, isInstrumental: false, wordTimed: true)
    }

    private func sheet(song: String, first: Double, length: Double) -> [LyricsFetcher.Line] {
        (0..<10).map { row in
            let start = first + Double(row) * (length + 0.6)
            return line("\(song) line \(row) goes here", start: start, end: start + length)
        }
    }

    /// The lines Hinge has on screen at `time`: from a line's start (a
    /// later line from its preview) until its exit has finished.
    private func visible(_ lines: [LyricsFetcher.Line], at time: Double) -> Set<String> {
        let lead = VersoCustom.Settings.shared.nextWordPreviewLead
        var texts = Set<String>()
        for row in lines.indices {
            let onset = row == 0 ? lines[row].start
                : (LyricPhraseTiming.firstWordOnset(of: lines[row]) ?? lines[row].start) - lead
            let gone = LyricLayer.renderedExitStart(row, in: lines, handOver: LyricPhraseTiming.handOver(after: row, in: lines),
                                                    rolls: false, fillsStage: true)
                + LyricPhraseTiming.exitSpan(for: .hinge)
            if time >= onset, time < gone { texts.insert(lines[row].text) }
        }
        return texts
    }

    @MainActor
    private struct Run {
        let memory: LyricSceneMemory
        var time: Double
        var wall = 1000.0
        var jump: LyricClock.Jump?
        var doubled: [String] = []

        init(time: Double) {
            memory = LyricSceneMemory()
            self.time = time
        }
    }

    private func frames(_ run: inout Run, key: LyricSceneKey, content: LyricSceneContent, seconds: Double,
                        check: (LyricSceneMemory.Transition, Double, Double) -> String?) {
        let end = run.wall + seconds
        while run.wall < end {
            run.wall += 1.0 / 60
            run.time += 1.0 / 60
            let transition = run.memory.observe(key: key, content: content, time: run.time, wall: run.wall, jump: run.jump)
            if let problem = check(transition, run.time, run.wall) { run.doubled.append(problem) }
        }
    }

    func testTheFirstLineIsNeverDrawnTwiceAsASongStarts() {
        // From the reporter's log: where Music first says the song is, and
        // where it says a moment later.
        let reports: [(from: Double, to: Double)] = [(0.08, 2.14), (0.17, 2.27), (0.34, 2.62), (0.34, 3.08), (0.12, 2.21)]
        var failures: [String] = []
        for first in [0.0, 0.1, 0.2, 0.3, 0.5, 0.8, 1.2, 1.6, 2.0, 2.4, 3.0, 4.0] {
            for length in [0.5, 1.2, 2.5] {
                let lines = sheet(song: "new", first: first, length: length)
                let content = LyricSceneContent(lines: lines)
                let previous = LyricSceneContent(lines: sheet(song: "old", first: 2, length: 3))
                for report in reports {
                    for lyricsFirst in [true, false] {
                        for gap in [0.02, 0.08, 0.25, 0.6] {
                            let old = LyricSceneKey(kind: .synced, track: 1, fingerprint: 1)
                            let loading = LyricSceneKey(kind: .loading, track: 2, fingerprint: 0)
                            let synced = LyricSceneKey(kind: .synced, track: 2, fingerprint: 2)
                            var run = Run(time: 30)
                            let check = { (t: LyricSceneMemory.Transition, time: Double, wall: Double) -> String? in
                                guard t.incomingPresence > 0.02 else { return nil }
                                let shown = self.visible(lines, at: time)
                                for leaving in t.leaving where leaving.presence > 0.02 {
                                    let drawn = self.visible(leaving.scene.content.lines, at: leaving.scene.renderTime(at: wall))
                                    if let both = drawn.intersection(shown).first {
                                        return "\(both) at \(String(format: "%.2f", time)) s"
                                    }
                                }
                                return nil
                            }
                            frames(&run, key: old, content: previous, seconds: 1, check: check)
                            // The new song: its time starts where Music first puts it.
                            run.time = report.from - 0.15
                            frames(&run, key: loading, content: LyricSceneContent(), seconds: 0.15, check: check)
                            if lyricsFirst {
                                frames(&run, key: synced, content: content, seconds: gap, check: check)
                                run.jump = LyricClock.Jump(from: run.time, to: run.time + report.to - report.from,
                                                           wall: run.wall + 1.0 / 60)
                                run.time += report.to - report.from
                                frames(&run, key: synced, content: content, seconds: 1.5, check: check)
                            } else {
                                run.jump = LyricClock.Jump(from: run.time, to: run.time + report.to - report.from,
                                                           wall: run.wall + 1.0 / 60)
                                run.time += report.to - report.from
                                frames(&run, key: loading, content: LyricSceneContent(), seconds: gap, check: check)
                                frames(&run, key: synced, content: content, seconds: 1.5, check: check)
                            }
                            if let problem = run.doubled.first {
                                failures.append("first line \(first)–\(first + length) s, Music \(report.from) → \(report.to), "
                                    + (lyricsFirst ? "lyrics first" : "clock first") + " by \(gap) s: \(problem)")
                            }
                        }
                    }
                }
            }
        }
        XCTAssertTrue(failures.isEmpty, "\(failures.count) song starts drew a line twice, e.g.\n"
                      + failures.prefix(8).joined(separator: "\n"))
    }

    /// The song-start trace only runs for a few seconds after a song change.
    func testTheSongStartTraceOnlyCoversTheStartOfASong() {
        XCTAssertTrue(SongStartLog.isOpen(changedAt: 100, now: 100))
        XCTAssertTrue(SongStartLog.isOpen(changedAt: 100, now: 109.9))
        XCTAssertFalse(SongStartLog.isOpen(changedAt: 100, now: 110))
        XCTAssertFalse(SongStartLog.isOpen(changedAt: 100, now: 99))
        XCTAssertFalse(SongStartLog.isOpen(changedAt: -.infinity, now: 5))
    }
}
