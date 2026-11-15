import XCTest
@testable import VersoCustom

/// The lyric layer never cross-fades a scene into another one showing the
/// same lines: two copies of a line, each at its own point of its motion,
/// read as the lyrics doubled. A time jump or a re-timed sheet that keeps
/// those lines on screen takes over in place instead.
@MainActor
final class SameLyricsSceneTests: XCTestCase {
    private func line(_ text: String, start: Double, end: Double) -> LyricsFetcher.Line {
        let parts = text.split(separator: " ").map(String.init)
        let step = (end - start) / Double(parts.count)
        let words = parts.enumerated().map {
            LyricsFetcher.Word(text: $0.element, start: start + Double($0.offset) * step,
                               end: start + Double($0.offset + 1) * step)
        }
        return .init(start: start, end: end, text: text, words: words, isInstrumental: false, wordTimed: true)
    }

    private func sheet(shift: Double = 0) -> [LyricsFetcher.Line] {
        (0..<12).map { row in
            line("line number \(row) of the song", start: 3 + Double(row) * 4 + shift, end: 6 + Double(row) * 4 + shift)
        }
    }

    /// Frames from `wall` for `seconds`, returning the most of any leaving
    /// scene seen at once with the incoming one.
    private func run(_ memory: LyricSceneMemory, key: LyricSceneKey, content: LyricSceneContent,
                     time: inout Double, wall: inout Double, seconds: Double,
                     jump: LyricClock.Jump? = nil) -> Double {
        var overlap = 0.0
        let end = wall + seconds
        while wall < end {
            wall += 1.0 / 60
            time += 1.0 / 60
            let t = memory.observe(key: key, content: content, time: time, wall: wall, jump: jump)
            for leaving in t.leaving { overlap = max(overlap, min(leaving.presence, t.incomingPresence)) }
        }
        return overlap
    }

    /// Music's report of where a new song really is (about +2 s on the
    /// reporter's Mac), or a song-offset change, landing once the lyrics
    /// are already up: the same lines were overlaid at the old time.
    func testAClockJumpThatKeepsTheSameLinesIsNotOverlaid() {
        for (from, by) in [(0.6, 2.07), (3.4, 1.2), (4.8, -0.5), (5.9, 0.4), (33.0, 1.01), (34.0, -1.01)] {
            let memory = LyricSceneMemory()
            let key = LyricSceneKey(kind: .synced, track: 1, fingerprint: 7)
            let content = LyricSceneContent(lines: sheet())
            var time = from - 1, wall = 1000.0
            _ = run(memory, key: key, content: content, time: &time, wall: &wall, seconds: 1)
            let jump = LyricClock.Jump(from: time, to: time + by, wall: wall + 1.0 / 60)
            time += by
            let overlap = run(memory, key: key, content: content, time: &time, wall: &wall, seconds: 1, jump: jump)
            XCTAssertEqual(overlap, 0, "a copy of the same lines overlaid after a \(by) s jump at \(from) s")
        }
    }

    /// A seek to somewhere else in the song still cross-fades.
    func testASeekToOtherLinesStillCrossFades() {
        let memory = LyricSceneMemory()
        let key = LyricSceneKey(kind: .synced, track: 1, fingerprint: 7)
        let content = LyricSceneContent(lines: sheet())
        var time = 10.0, wall = 1000.0
        _ = run(memory, key: key, content: content, time: &time, wall: &wall, seconds: 1)
        let jump = LyricClock.Jump(from: time, to: 40, wall: wall + 1.0 / 60)
        time = 40
        let overlap = run(memory, key: key, content: content, time: &time, wall: &wall, seconds: 1, jump: jump)
        XCTAssertGreaterThan(overlap, 0.3)
    }

    /// A better source landing early in a song (Kugou or Apple Music's own
    /// lyrics replacing the first source a few seconds in) times the same
    /// lines a little differently: it takes over in place rather than
    /// cross-fading two copies of the line, at a boundary or mid-line.
    func testAReTimedSheetOfTheSameLinesTakesOverInPlace() {
        for (at, shift) in [(7.2, 0.5), (7.1, -0.4), (11.05, 0.45), (5.0, 1.5)] {
            let memory = LyricSceneMemory()
            let old = LyricSceneKey(kind: .synced, track: 1, fingerprint: 7)
            let new = LyricSceneKey(kind: .synced, track: 1, fingerprint: 8)
            var time = at - 1, wall = 1000.0
            _ = run(memory, key: old, content: LyricSceneContent(lines: sheet()), time: &time, wall: &wall, seconds: 1)
            let overlap = run(memory, key: new, content: LyricSceneContent(lines: sheet(shift: shift)),
                              time: &time, wall: &wall, seconds: 1)
            XCTAssertEqual(overlap, 0, "re-timed by \(shift) s at \(at) s")
        }
    }

    /// The same song's lookup running again (its album or length reported
    /// a moment late) clears the lines and brings the same ones back: the
    /// sheet that briefly left comes back instead of a second copy fading
    /// in over it, and never blinks.
    func testTheSameLyricsComingBackAreNotStacked() {
        let memory = LyricSceneMemory()
        let synced = LyricSceneKey(kind: .synced, track: 1, fingerprint: 7)
        let loading = LyricSceneKey(kind: .loading, track: 1, fingerprint: 0)
        let content = LyricSceneContent(lines: sheet())
        var time = 8.0, wall = 1000.0
        _ = run(memory, key: synced, content: content, time: &time, wall: &wall, seconds: 1)
        _ = run(memory, key: loading, content: LyricSceneContent(), time: &time, wall: &wall, seconds: 0.2)
        var previous = 0.0
        var first = true
        let end = wall + 1
        while wall < end {
            wall += 1.0 / 60
            time += 1.0 / 60
            let t = memory.observe(key: synced, content: content, time: time, wall: wall, jump: nil)
            for leaving in t.leaving where leaving.scene.key == synced {
                XCTFail("the same lyrics are stacked: \(leaving.presence) under \(t.incomingPresence)")
            }
            // What was on screen of them, at least, carries straight on.
            let shown = t.incomingPresence + t.leaving.filter { $0.scene.key == synced }.map(\.presence).reduce(0, +)
            if !first { XCTAssertGreaterThanOrEqual(shown, previous - 0.2) }
            previous = shown
            first = false
        }
        XCTAssertEqual(previous, 1)
    }
}
