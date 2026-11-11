import SwiftUI
import XCTest
@testable import VersoCustom

final class OpeningTitleTests: XCTestCase {
    typealias T = OpeningTitle

    func testALongIntroGetsTheFullCardGoneBeforeTheFirstLine() throws {
        let schedule = try XCTUnwrap(T.schedule(words: .timed(firstLine: 11), previewLead: 0.45))
        XCTAssertEqual(schedule.size, .full)
        XCTAssertEqual(schedule.start, T.lead, accuracy: 1e-9)
        XCTAssertEqual(schedule.settled, T.lead + T.fullEntrance, accuracy: 1e-9)
        // Gone a second before the first line can first be drawn.
        XCTAssertLessThanOrEqual(schedule.end, 11 - 0.45 - 0.6 + 1e-9)
        XCTAssertLessThanOrEqual(schedule.end, 11 - T.clearance)
        // A quicker exit than entrance.
        XCTAssertLessThan(schedule.end - schedule.leave, schedule.settled - schedule.start)
        XCTAssertNil(T.phase(schedule, at: 0.2))
        XCTAssertNotNil(T.phase(schedule, at: 3))
        XCTAssertNil(T.phase(schedule, at: schedule.end))
        XCTAssertNil(T.phase(schedule, at: 11))
    }

    func testAVeryLongIntroHoldsThenLeavesWellBeforeTheVoice() throws {
        let schedule = try XCTUnwrap(T.schedule(words: .timed(firstLine: 60)))
        XCTAssertEqual(schedule.leave - schedule.settled, T.lyricHold, accuracy: 1e-9)
        XCTAssertLessThan(schedule.end, 15)
    }

    func testAFirstLineSoonGetsTheCompactCardOrNone() throws {
        let compact = try XCTUnwrap(T.schedule(words: .timed(firstLine: 4.2), previewLead: 0.45))
        XCTAssertEqual(compact.size, .compact)
        XCTAssertLessThanOrEqual(compact.end, 4.2 - T.clearance + 1e-9)
        XCTAssertGreaterThanOrEqual(compact.leave - compact.settled, T.compactMinimumHold - 1e-9)
        // Under about three seconds: no card at all.
        XCTAssertNil(T.schedule(words: .timed(firstLine: 2.8), previewLead: 0.45))
        XCTAssertNil(T.schedule(words: .timed(firstLine: 0.4)))
        // A longer preview lead keeps it further from the line.
        let early = try XCTUnwrap(T.schedule(words: .timed(firstLine: 9), previewLead: 1.5))
        XCTAssertLessThanOrEqual(early.end, 9 - 1.5 - 0.6 + 1e-9)
    }

    func testNeverOverlapsTheFirstLineAcrossManyStarts() {
        for tenth in 0...400 {
            let first = Double(tenth) / 10
            for lead in [0.0, 0.45, 1.0, 2.0] {
                guard let schedule = T.schedule(words: .timed(firstLine: first), previewLead: lead) else { continue }
                XCTAssertLessThanOrEqual(schedule.end, first - max(T.clearance, lead + 0.6) + 1e-9, "\(first) \(lead)")
                XCTAssertLessThan(schedule.start, schedule.settled)
                XCTAssertLessThanOrEqual(schedule.settled, schedule.leave)
                XCTAssertLessThan(schedule.leave, schedule.end)
            }
        }
    }

    func testWithoutWordsTheCardHoldsLongerThenFades() throws {
        let none = try XCTUnwrap(T.schedule(words: .none))
        let instrumental = try XCTUnwrap(T.schedule(words: .timed(firstLine: nil)))
        XCTAssertEqual(none, instrumental)
        XCTAssertEqual(none.size, .full)
        XCTAssertEqual(none.leave - none.settled, T.wordlessHold, accuracy: 1e-9)
        XCTAssertGreaterThan(T.wordlessHold, T.lyricHold)
        XCTAssertEqual(T.firstSung([]), nil)
        let lines = [LyricsFetcher.Line(start: 0, end: 0.01, text: "Produced by: A", words: [], isInstrumental: true, credit: true),
                     LyricsFetcher.Line(start: 0, end: 6, text: "", words: [], isInstrumental: true),
                     LyricsFetcher.Line(start: 6, end: 9, text: "Hello", words: [], isInstrumental: false)]
        XCTAssertEqual(T.firstSung(lines), 6)
    }

    func testASongJoinedPartWayGetsABriefCompactCard() throws {
        // Mid-song, the words already going: a brief reminder from where we joined.
        let brief = try XCTUnwrap(T.schedule(words: .timed(firstLine: 10), joined: 95))
        XCTAssertEqual(brief.size, .compact)
        XCTAssertEqual(brief.start, 95 + T.joinLead, accuracy: 1e-9)
        XCTAssertEqual(brief.leave - brief.settled, T.briefHold, accuracy: 1e-9)
        // Joined during a long intro: the ordinary rules, from the join.
        let intro = try XCTUnwrap(T.schedule(words: .timed(firstLine: 30), joined: 6))
        XCTAssertEqual(intro.size, .full)
        XCTAssertEqual(intro.start, 6.3, accuracy: 1e-9)
        XCTAssertLessThanOrEqual(intro.end, 29)
        // Joined just before the first line: nothing would fit before it,
        // and the words have not started -- no card.
        XCTAssertNil(T.schedule(words: .timed(firstLine: 6), joined: 5))
        // Unsynced words are already on screen: always brief.
        XCTAssertEqual(T.schedule(words: .showing)?.size, .compact)
    }

    @MainActor
    func testTheSongIsJoinedFromTheStartUnlessFirstSeenPartWay() {
        let memory = OpeningTitleMemory()
        XCTAssertEqual(memory.joined(track: 1, at: 0.3), 0)
        XCTAssertEqual(memory.joined(track: 1, at: 50), 0, "Same song playing on")
        XCTAssertEqual(memory.joined(track: 2, at: 120), 120, "A song first seen part-way")
        XCTAssertEqual(memory.joined(track: 2, at: 140), 120)
        XCTAssertEqual(memory.joined(track: 2, at: 0.5), 0, "Back to the start: the full card again")
        XCTAssertEqual(memory.joined(track: 2, at: 140), 0)
        // The old song's time read once as the new song starts does not
        // count as joining it part-way.
        XCTAssertEqual(memory.joined(track: 3, at: 210), 210)
        XCTAssertEqual(memory.joined(track: 3, at: 0.1), 0)
    }

    func testPhaseIsAPureFunctionOfTime() throws {
        let schedule = try XCTUnwrap(T.schedule(words: .timed(firstLine: 12)))
        for time in stride(from: 0.0, through: 12, by: 0.25) {
            XCTAssertEqual(T.phase(schedule, at: time), T.phase(schedule, at: time))
        }
        let entering = try XCTUnwrap(T.phase(schedule, at: schedule.start + 0.5))
        XCTAssertTrue(entering.moving); XCTAssertEqual(entering.exit, 0)
        let held = try XCTUnwrap(T.phase(schedule, at: schedule.settled + 0.5))
        XCTAssertFalse(held.moving); XCTAssertEqual(held.exit, 0)
        let leaving = try XCTUnwrap(T.phase(schedule, at: (schedule.leave + schedule.end) / 2))
        XCTAssertTrue(leaving.moving); XCTAssertEqual(leaving.exit, 0.5, accuracy: 1e-9)
    }

    func testRevealIsStagedAndReduceMotionOnlyFades() {
        let early = T.Reveal.at(0.3, size: .full, reduceMotion: false)
        XCTAssertGreaterThan(early.rule, early.title, "The rule draws in first")
        XCTAssertEqual(early.album, 0, "The album comes last")
        let done = T.Reveal.at(T.fullEntrance, size: .full, reduceMotion: false)
        for part in [done.rule, done.title, done.wipe, done.reading, done.artist, done.album] { XCTAssertEqual(part, 1, accuracy: 1e-9) }
        let compact = T.Reveal.at(T.compactEntrance, size: .compact, reduceMotion: false)
        for part in [compact.rule, compact.title, compact.wipe, compact.artist] { XCTAssertEqual(part, 1, accuracy: 1e-9) }
        // Reduce Motion: one fade for the whole card, nothing staged.
        let reduced = T.Reveal.at(0.3, size: .full, reduceMotion: true)
        XCTAssertEqual(Set([reduced.rule, reduced.title, reduced.reading, reduced.artist, reduced.album]).count, 1)
        XCTAssertEqual(reduced.wipe, 1, "No wipe: the title fades")
        XCTAssertEqual(T.Reveal.at(0.6, size: .full, reduceMotion: true).title, 1, accuracy: 1e-9)
    }

    func testTheClockRunsWhileTheCardMovesAndNeverRestsWhileItIsUp() throws {
        let schedule = try XCTUnwrap(T.schedule(words: .none))
        let entering = T.phase(schedule, at: schedule.start + 0.2)
        let held = T.phase(schedule, at: schedule.settled + 1)
        XCTAssertEqual(T.pace(.still, phase: entering, playing: true), .active)
        XCTAssertEqual(T.pace(.resting, phase: held, playing: true), .still)
        XCTAssertEqual(T.pace(.resting, phase: held, playing: false), .waiting)
        XCTAssertEqual(T.pace(.ambient, phase: held, playing: true), .ambient)
        XCTAssertEqual(T.pace(.resting, phase: nil, playing: true), .resting)
        // Still to come: the clock is kept awake for it.
        XCTAssertEqual(T.pace(.resting, phase: T.upcoming(schedule, at: 0.1), playing: true), .active)
        XCTAssertNil(T.upcoming(schedule, at: schedule.end + 0.1))
        let later = try XCTUnwrap(T.schedule(words: .timed(firstLine: 10), joined: 95))
        XCTAssertNil(T.upcoming(later, at: 30), "Long before a card, the clock may rest")
    }

    func testTheLoadingNoticeYieldsToTheCard() throws {
        let schedule = try XCTUnwrap(T.schedule(words: .none))
        XCTAssertEqual(T.yields(to: schedule, at: 3), 0)
        XCTAssertEqual(T.yields(to: schedule, at: schedule.end + 1), 1)
        XCTAssertEqual(T.yields(to: nil, at: 3), 1)
    }

    func testAlbumLineLeavesOutASingleNamedAfterTheSong() {
        XCTAssertEqual(T.albumLine(album: "Rare Hymns", title: "Human Nature"), "Rare Hymns")
        XCTAssertNil(T.albumLine(album: "Fast Start - Single", title: "Fast Start"))
        XCTAssertNil(T.albumLine(album: "Human Nature", title: "Human Nature"))
        XCTAssertNil(T.albumLine(album: "  ", title: "x"))
    }

    @MainActor
    func testAJapaneseTitleGetsItsReadingWhenReadingsShow() {
        let memory = OpeningTitleMemory()
        let below = RomanizationOptions.Snapshot(display: .below, language: .automatic, toneMarks: false, preferSaved: true)
        let reading = memory.reading(of: "夜に駆ける", options: below)
        XCTAssertEqual(reading?.lowercased().replacingOccurrences(of: " ", with: ""), "yorunikakeru")
        XCTAssertNil(memory.reading(of: "Human Nature", options: below))
        var off = below
        off.display = .off
        XCTAssertNil(memory.reading(of: "夜に駆ける", options: off))
        XCTAssertEqual(OpeningTitleMemory.capitalised("yoru ni kakeru"), "Yoru ni Kakeru")
    }

    @MainActor
    func testTheSettingIsPartOfEachSurfacesStyle() {
        XCTAssertTrue(LyricStyleProfiles.keys.contains(OpeningTitle.settingKey))
    }
}
