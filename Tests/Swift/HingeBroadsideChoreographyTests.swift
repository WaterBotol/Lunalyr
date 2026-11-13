import SwiftUI
import XCTest
@testable import VersoCustom

/// The phrase choreography of Hinge (a book: a page per phrase, turned on
/// the spine) and Broadside (a printed strip inked as it is sung, read by a
/// panning camera). Pure functions of the line and the time; nothing drawn.
@MainActor
final class HingeBroadsideChoreographyTests: XCTestCase {
    typealias Word = LyricsFetcher.Word

    private func line(_ words: [Word]) -> (StagePhrasing, [StageRun]) {
        let runs = StageRun.make(words, glue: [Bool](repeating: false, count: words.count))
        return (StagePhrases.analyse(runs), runs)
    }

    // MARK: Hinge

    /// The focal word swings open; function words only ink; content words
    /// and members of a quick run slide (a run as one group).
    func testHingeEntranceFollowsTheRole() {
        let (p, runs) = line(StagePhrasesTests.mixed)
        let entrances = p.words.map(StageLyricView.hingeEntrance)
        for phrase in p.phrases { XCTAssertEqual(entrances[phrase.focal], .swing) }
        XCTAssertEqual(entrances.filter { $0 == .swing }.count, p.phrases.count, "One swing per phrase")
        // "and we keep": a quick run, so even "and" and "we" slide, together.
        XCTAssertEqual(Array(entrances.prefix(3)), [.slide, .slide, .slide])
        // "the" in "through the city lights" inks in place.
        let the = runs.firstIndex { $0.text == "the" && $0.id > 4 } ?? 0
        XCTAssertEqual(entrances[the], .ink)
        // A run moves as one: at any moment its members are at the same point of the slide.
        let groupStart = runs[0].start
        for time in stride(from: 1.0, through: 1.6, by: 0.05) {
            let slides = (0..<3).map {
                StageLyricView.hingePose(.slide, start: runs[$0].start, groupStart: groupStart, held: nil, at: time,
                                         speed: 1, reduceMotion: false).slide
            }
            XCTAssertEqual(slides[0], slides[1], accuracy: 1e-9)
            XCTAssertEqual(slides[1], slides[2], accuracy: 1e-9)
        }
    }

    /// Every word lands on its own onset: nothing shows before it, and it
    /// is inked a tenth of a second after (the focal word mostly open).
    func testHingeWordsLandOnTheirOnsets() {
        for entrance in [StageLyricView.HingeEntrance.swing, .slide, .ink] {
            let before = StageLyricView.hingePose(entrance, start: 5, groupStart: 5, held: nil, at: 5 - 0.05, speed: 1,
                                                  reduceMotion: false)
            XCTAssertEqual(before.ink, 0, "\(entrance) shows before its onset")
            let after = StageLyricView.hingePose(entrance, start: 5, groupStart: 5, held: nil, at: 5.1, speed: 1,
                                                 reduceMotion: false)
            XCTAssertGreaterThan(after.ink, 0.6, "\(entrance) is not inked on time")
        }
        let swing = StageLyricView.hingePose(.swing, start: 5, groupStart: 5, held: nil, at: 5.2, speed: 1, reduceMotion: false)
        XCTAssertLessThan(swing.swing, StageMotion.hingeSwingAngle * 0.25)
        // Held: it grows from the spine while held, then holds still.
        let early = StageLyricView.hingePose(.swing, start: 5, groupStart: 5, held: 1.2, at: 5.3, speed: 1, reduceMotion: false)
        let late = StageLyricView.hingePose(.swing, start: 5, groupStart: 5, held: 1.2, at: 6.2, speed: 1, reduceMotion: false)
        let later = StageLyricView.hingePose(.swing, start: 5, groupStart: 5, held: 1.2, at: 8, speed: 1, reduceMotion: false)
        XCTAssertLessThan(early.grow, late.grow)
        XCTAssertEqual(late.grow, later.grow, accuracy: 1e-9)
        // Reduce Motion: no travel, no swing, a fade.
        let reduced = StageLyricView.hingePose(.swing, start: 5, groupStart: 5, held: 1.2, at: 5.05, speed: 1, reduceMotion: true)
        XCTAssertEqual(reduced.swing, 0)
        XCTAssertEqual(reduced.slide, 0)
        XCTAssertEqual(reduced.grow, 1)
    }

    /// A page per phrase; a line-timed line opens as one spread of two pages.
    func testHingePagesByPhrase() {
        let (p, runs) = line(StagePhrasesTests.mixed)
        XCTAssertEqual(StageLyricView.hingePages(p), [[0], [1], [2]])
        let timed = StagePhrases.analyse(runs, lineTimed: true)
        XCTAssertEqual(StageLyricView.hingePages(timed), [[0, 1], [2]])
    }

    /// A page turns as the next phrase is about to be sung, never on a word
    /// inside its own phrase, and at most two pages are seen at rest.
    func testHingeTurnsAtPhrasesAndShowsAtMostTwoPages() {
        let (p, _) = line(StagePhrasesTests.mixed)
        let starts = p.phrases.map(\.start)
        func turns(_ time: Double) -> [Double] {
            StageLyricView.hingeTurns(pageStarts: starts, lineTimed: false, exitStart: 7.5, at: time, speed: 1)
        }
        // Inside the first phrase nothing turns, whatever the words do.
        for time in stride(from: 1.0, to: starts[1] - 0.2, by: 0.1) { XCTAssertEqual(turns(time), [0, 0, 0]) }
        // Just after the second phrase starts, the first page is turning over.
        let turning = turns(starts[1] + 0.1)[0]
        XCTAssertGreaterThan(turning, 0.1)
        XCTAssertLessThan(turning, 1)
        // At rest, between turns: at most two pages drawn.
        for time in [2.5, 4.4, 6.9] {
            let begun = starts.map { time >= $0 - StageMotion.lead }
            let drawn = StageLyricView.hingeInstances(turns: turns(time), begun: begun, exitPage: nil, reduceMotion: false)
                .filter { $0.opacity > 0.02 }
            XCTAssertLessThanOrEqual(drawn.count, 2, "Too many pages at \(time)s")
            XCTAssertLessThanOrEqual(drawn.filter { $0.opacity > StageMotion.hingeReadDim + 0.01 }.count, 1,
                                     "One page is the hero; the other is read")
        }
        // Edge-on a turning page is not drawn at all (never a squashed sliver).
        let edgeOn = StageLyricView.hingeInstances(turns: [0.5, 0], exitPage: nil, reduceMotion: false)
        XCTAssertFalse(edgeOn.contains { $0.page == 0 })
    }

    /// Reduce Motion: a turn is a cross-fade, never a rotation.
    func testHingeReduceMotionCrossFades() {
        let drawn = StageLyricView.hingeInstances(turns: [0.1, 0], exitPage: nil, reduceMotion: true)
        XCTAssertTrue(drawn.allSatisfy { $0.angle == 0 })
        XCTAssertTrue(drawn.contains { $0.page == 0 && !$0.back })
        XCTAssertTrue(drawn.contains { $0.page == 0 && $0.back })
    }

    // MARK: Broadside

    /// Little words ride a big word as a label block; a phrase's focal word
    /// is never a label, and is set larger than the other big words.
    func testBroadsideLabelsAndSizesByRole() {
        let (p, runs) = line(StagePhrasesTests.mixed)
        let items = StageLyricView.broadsideItems(p, unit: 40)
        let labelled = items.filter(\.isLabel).flatMap(\.runs).map { runs[$0].text }
        XCTAssertEqual(labelled, ["and", "we", "through", "the", "until", "the"])
        for phrase in p.phrases {
            let focal = items.first { !$0.isLabel && $0.runs == [phrase.focal] }
            XCTAssertNotNil(focal, "The focal word is a big word")
            let others = items.filter { !$0.isLabel && $0.phrase == phrase.index && $0.runs != [phrase.focal] }
            for other in others { XCTAssertGreaterThan(focal?.size ?? 0, other.size) }
        }
        // A two-word label stacks on two lines.
        XCTAssertEqual(items.first?.lines.count, 2)
        // A phrase ends in more space than a word does.
        let strip = StageLyricView.broadsideStrip([CGFloat](repeating: 100, count: items.count), items: items, unit: 40)
        let gaps = (1..<items.count).map { strip.lefts[$0] - strip.lefts[$0 - 1] - 100 }
        let phraseGaps = (1..<items.count).filter { items[$0].phrase != items[$0 - 1].phrase }.map { gaps[$0 - 1] }
        let wordGaps = (1..<items.count).filter { items[$0].phrase == items[$0 - 1].phrase }.map { gaps[$0 - 1] }
        XCTAssertGreaterThan(phraseGaps.min() ?? 0, wordGaps.max() ?? 0)
    }

    /// Ink lands on time: the focal word fills over its sung time (a held
    /// word slowly), other words in a quick sweep, and a glued word's
    /// syllables are reached on their own onsets.
    func testBroadsideInkByRole() {
        let (p, runs) = line(StagePhrasesTests.mixed)
        let running = runs[3]
        XCTAssertEqual(StageLyricView.broadsideInk(running, info: p.words[3], next: runs[4].start, at: running.start - 0.05), 0)
        let half = StageLyricView.broadsideInk(running, info: p.words[3], next: runs[4].start, at: running.start + 0.58)
        XCTAssertGreaterThan(half, 0.3)
        XCTAssertLessThan(half, 0.7, "A held focal word fills across its held time")
        let city = runs[6]
        XCTAssertEqual(StageLyricView.broadsideInk(city, info: p.words[6], next: runs[7].start, at: city.start + 0.15), 1,
                       accuracy: 0.02)
        // A quick run member is full before the next member starts.
        XCTAssertEqual(StageLyricView.broadsideInk(runs[0], info: p.words[0], next: runs[1].start, at: runs[1].start), 1,
                       accuracy: 0.02)
        // Syllables: "beau" "ti" "ful" glued; only "beau" is inked before "ti" starts.
        let words = [Word(text: "beau", start: 1, end: 1.3), Word(text: "ti", start: 1.3, end: 1.6), Word(text: "ful", start: 1.6, end: 2)]
        let glued = StageRun.make(words, glue: [false, true, true])[0]
        let partial = StageLyricView.broadsideInk(glued, info: nil, next: nil, at: 1.25)
        XCTAssertEqual(partial, 4.0 / 9.0, accuracy: 0.02)
        XCTAssertEqual(StageLyricView.broadsideInk(glued, info: nil, next: nil, at: 2.2), 1, accuracy: 1e-9)
    }

    /// The camera only ever travels in the reading direction, holds still
    /// in a pause, centres a strip that fits, and keeps the strip's ends
    /// from showing empty stage.
    func testBroadsideCameraFollowsTheCurrent() {
        let (p, runs) = line(StagePhrasesTests.mixed)
        let items = StageLyricView.broadsideItems(p, unit: 40)
        let widths = items.map { $0.isLabel ? CGFloat(40) : CGFloat(200) }
        let strip = StageLyricView.broadsideStrip(widths, items: items, unit: 40)
        let beats = StageLyricView.broadsideBeats(p, items: items, runs: runs)
        XCTAssertEqual(beats.count, 6, "keep (its quick run, the labels riding it), running, city, lights, morning, comes")
        var previous = -CGFloat.infinity
        for time in stride(from: 0.5, through: 8, by: 0.02) {
            let camera = StageLyricView.broadsideCamera(lefts: strip.lefts, widths: widths, stripWidth: strip.width, beats: beats,
                                                        stageWidth: 1000, unit: 40, at: time)
            XCTAssertGreaterThanOrEqual(camera, previous - 0.001, "The camera turned back at \(time)s")
            XCTAssertGreaterThanOrEqual(camera, 500 - 1000 * StageLyricView.broadsideMargin - 40 * 0.7 - 0.001)
            XCTAssertLessThanOrEqual(camera, strip.width - (500 - 1000 * StageLyricView.broadsideMargin - 40 * 0.7) + 0.001)
            previous = camera
        }
        // In the pause before "until", the camera holds still.
        let a = StageLyricView.broadsideCamera(lefts: strip.lefts, widths: widths, stripWidth: strip.width, beats: beats,
                                               stageWidth: 1000, unit: 40, at: 4.95)
        let b = StageLyricView.broadsideCamera(lefts: strip.lefts, widths: widths, stripWidth: strip.width, beats: beats,
                                               stageWidth: 1000, unit: 40, at: 5.0)
        XCTAssertEqual(a, b, accuracy: 0.001)
        // Given where it comes to rest (its last phrase's centre), the line
        // ends there, still having moved only forward.
        let rest = strip.width - 200
        let ended = StageLyricView.broadsideCamera(lefts: strip.lefts, widths: widths, stripWidth: strip.width, beats: beats,
                                                   stageWidth: 1000, unit: 40, at: 9, rest: rest)
        XCTAssertEqual(ended, rest, accuracy: 0.001)
        // A strip that fits is centred, and never moves.
        let short = StageLyricView.broadsideCamera(lefts: [0, 300], widths: [200, 200], stripWidth: 500, beats: beats,
                                                   stageWidth: 1000, unit: 40, at: 3)
        XCTAssertEqual(short, 250)
    }

    /// A line arrives and leaves moving the same way (the vector law), and
    /// no more than two phrases are on the strip at once.
    func testBroadsideHandOverAndPhrasesOnScreen() {
        let arriving = StageLyricView.broadsideTravel(firstStart: 1, exitStart: 9, at: 1, reduceMotion: false)
        let settled = StageLyricView.broadsideTravel(firstStart: 1, exitStart: 9, at: 3, reduceMotion: false)
        let leaving = StageLyricView.broadsideTravel(firstStart: 1, exitStart: 9, at: 9.3, reduceMotion: false)
        XCTAssertLessThan(arriving, settled, "It arrives from ahead...")
        XCTAssertGreaterThan(leaving, settled, "...and leaves on ahead: one direction")
        XCTAssertEqual(StageLyricView.broadsideTravel(firstStart: 1, exitStart: 9, at: 9.3, reduceMotion: true), 0)
        let (p, _) = line(StagePhrasesTests.mixed)
        for time in stride(from: 1.0, through: 7.4, by: 0.1) {
            let present = p.phrases.indices.filter {
                StageLyricView.broadsidePhrasePresence($0, phrases: p.phrases, exitStart: 7.5, at: time) > 0.05
            }
            let inked = present.filter { p.phrases[$0].start - StageMotion.lead <= time &&
                StageLyricView.broadsidePhrasePresence($0, phrases: p.phrases, exitStart: 7.5, at: time) > 0.95 }
            XCTAssertLessThanOrEqual(inked.count, 2, "Three inked phrases on the strip at \(time)s")
            let ahead = present.filter { p.phrases[$0].start - StageMotion.lead > time }
            XCTAssertLessThanOrEqual(ahead.count, 1, "More than the next phrase printed ahead at \(time)s")
        }
        // Leaving, the oldest phrase goes first.
        let first = StageLyricView.broadsidePhrasePresence(1, phrases: p.phrases, exitStart: 7.5, at: 7.7)
        let last = StageLyricView.broadsidePhrasePresence(2, phrases: p.phrases, exitStart: 7.5, at: 7.7)
        XCTAssertLessThan(first, last)
    }
}
