import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Renders the real lyric stage with long translations under the kept song
/// title card, to PNG, for looking at whether a translation at the foot of
/// the stage keeps clear of the card (`TitleCardClearance`), and at the
/// translation slot at the screen's edge while a held line hands over to
/// the next (`EdgeTranslations.slot`). Nothing is asserted about the
/// pictures; they are for a person to look at.
///
/// Skipped unless LUNALYR_LYRIC_RENDER_DIR names a folder, as for
/// `LyricHighlightRenderTests`, whose stage and wallpaper it draws:
///
///     TEST_RUNNER_LUNALYR_LYRIC_RENDER_DIR=/path/to/folder xcodebuild test ... \
///         -only-testing:VersoCustomTests/TitleCardClearanceRenderTests
///
/// The stage reads the shared settings, as it does in the app: the Debug
/// build's own preferences domain, and every one changed here is put back.
final class TitleCardClearanceRenderTests: XCTestCase {
    /// An invented song: two lines with long translations, then a line whose
    /// last note rings on into the next one (held), each with a translation.
    static let lines: [LyricsFetcher.Line] = {
        var lines = [
            LyricHighlightRenderTests.spaced("And every single word you ever said to me", start: 20, step: 0.4),
            LyricHighlightRenderTests.spaced("Keeps on ringing in my head tonight", start: 25, step: 0.45),
            LyricHighlightRenderTests.spaced("Hold me close until the morning light", start: 30, step: 0.4,
                                             held: ["light": 2.2]),
            LyricHighlightRenderTests.spaced("Never let me go", start: 33.8, step: 0.45),
        ]
        lines[0].translation = "Y cada palabra que me dijiste sigue resonando dentro de mi cabeza esta noche, "
            + "una y otra vez, sin parar"
        lines[1].translation = "Sigue sonando en mi cabeza esta noche, una y otra vez, sin parar ni un momento"
        lines[2].translation = "Abrázame fuerte hasta la luz de la mañana"
        lines[3].translation = "Nunca me dejes ir"
        return lines
    }()

    /// Late in the first line, most of its words sung.
    static let midLine = 22.9
    static let layouts: [LyricComposition] = [.echo, .spoken, .scattered, .cutout, .fable, .hinge]
    static let placements: [TranslationPlacement] = [.below, .bottom, .top]
    /// Through the held line's hand-over: before the next line, as it
    /// arrives, both sung, and once the held note has ended.
    static let heldTimes: [Double] = [33.6, 33.95, 34.3, 34.8, 35.4]

    @MainActor
    func testRenderTranslationsUnderTheKeptTitleCard() async throws {
        guard let folder = LyricHighlightRenderTests.folder(in: ProcessInfo.processInfo.environment) else {
            throw XCTSkip("Renders only when \(LyricHighlightRenderTests.variable) "
                + "(TEST_RUNNER_\(LyricHighlightRenderTests.variable) through xcodebuild) names a folder.")
        }
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("cells"), withIntermediateDirectories: true)
        let session = try LyricHighlightRenderTests.Session(folder: folder)
        let extras = LunalyrExtras.shared, model = LyricsViewModel.shared
        let defaults = UserDefaults.standard
        let keys = [OpeningTitle.settingKey, OpeningTitle.alwaysKey, OpeningTitle.Placement.leftKey,
                    OpeningTitle.Placement.liftKey, OpeningTitle.Placement.scaleKey]
        let savedDefaults = keys.map { defaults.object(forKey: $0) }
        let saved = (shows: extras.showsTranslations, place: extras.translationPlacement,
                     scale: VersoCustom.Settings.shared.lyricScale)
        defer {
            session.restore()
            VersoCustom.Settings.shared.lyricScale = saved.scale
            extras.showsTranslations = saved.shows
            extras.translationPlacement = saved.place
            for (key, value) in zip(keys, savedDefaults) {
                if let value { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) }
            }
        }
        // The card kept, in its standard place and size.
        defaults.set(true, forKey: OpeningTitle.settingKey)
        defaults.set(true, forKey: OpeningTitle.alwaysKey)
        OpeningTitle.Placement.reset(defaults)
        extras.showsTranslations = true
        model.lines = Self.lines
        await session.harness.settle(until: { session.harness.monitor.artwork != nil }, timeout: 3)
        var log = ["artwork shown: \(session.harness.monitor.artwork != nil)"]

        let look = LyricHighlightRenderTests.looks[0]
        let backdrop = LyricHighlightRenderTests.Backdrop.visualiser
        /// A renderer that has seen the song from its start, so the kept
        /// card is up in full by the time it draws.
        func renderer(at time: Double) throws -> ImageRenderer<LyricHighlightRenderTests.Frame> {
            session.apply(look, backdrop)
            let renderer = session.makeRenderer(backdrop, look, time: 0)
            _ = try session.capture(renderer, backdrop, look, time: 0)
            session.settle(renderer, backdrop, look, time: time)
            return renderer
        }

        // Every placement in every layout, late in a long-translated line.
        var rows: [[CGImage]] = []
        for place in Self.placements {
            extras.translationPlacement = place
            var row: [CGImage] = []
            for layout in Self.layouts {
                VersoCustom.Settings.shared.lyricComposition = layout
                let renderer = try renderer(at: Self.midLine)
                let image = try session.capture(renderer, backdrop, look, time: Self.midLine)
                row.append(image)
                try BGRAImage.writePNG(image, to: folder.appendingPathComponent(
                    "cells/title-\(place.rawValue)-\(layout.rawValue).png"))
            }
            rows.append(row)
        }
        let sheet = LyricHighlightRenderTests.Sheet(
            title: "Translations with the kept title card · t = \(Self.midLine)",
            columns: Self.layouts.map(\.title), rows: Self.placements.map(\.title), cells: rows, cellWidth: 520)
        if let image = sheet.image() {
            try BGRAImage.writePNG(image, to: folder.appendingPathComponent("title-card-translations.png"))
        }

        // The edge slot while a held line hands over, with and without Reduce Motion.
        for place in [TranslationPlacement.bottom, .top] {
            extras.translationPlacement = place
            var heldRows: [[CGImage]] = []
            var labels: [String] = []
            for reduceMotion in [false, true] {
                session.reduceMotion = reduceMotion
                for layout in [LyricComposition.echo, .scattered] {
                    VersoCustom.Settings.shared.lyricComposition = layout
                    let renderer = try renderer(at: Self.heldTimes[0])
                    var previous = Self.heldTimes[0]
                    var frames: [CGImage] = []
                    for time in Self.heldTimes {
                        frames.append(try session.capture(renderer, backdrop, look, time: time,
                                                          advance: max(1.0 / 60, time - previous)))
                        previous = time
                    }
                    heldRows.append(frames)
                    labels.append(layout.title + (reduceMotion ? " · Reduce Motion" : ""))
                }
            }
            session.reduceMotion = false
            let held = LyricHighlightRenderTests.Sheet(
                title: "A held line's hand-over · translation at the \(place.rawValue) of the screen",
                columns: Self.heldTimes.map { String(format: "t = %.2f", $0) }, rows: labels, cells: heldRows,
                cellWidth: 420)
            if let image = held.image() {
                try BGRAImage.writePNG(image, to: folder.appendingPathComponent("held-handover-\(place.rawValue).png"))
            }
        }
        // The largest text, so a line with its translation under it reaches
        // the card: late in the first line, and through the held line's hand-over.
        extras.translationPlacement = .below
        VersoCustom.Settings.shared.lyricScale = 1.5
        let largeTimes = [Self.midLine] + Self.heldTimes
        var largeRows: [[CGImage]] = []
        let largeLayouts: [LyricComposition] = [.echo, .scattered, .constellation]
        for layout in largeLayouts {
            VersoCustom.Settings.shared.lyricComposition = layout
            let renderer = try renderer(at: largeTimes[0])
            var previous = largeTimes[0]
            var frames: [CGImage] = []
            for time in largeTimes {
                frames.append(try session.capture(renderer, backdrop, look, time: time,
                                                  advance: max(1.0 / 60, time - previous)))
                previous = time
            }
            largeRows.append(frames)
        }
        VersoCustom.Settings.shared.lyricScale = saved.scale
        let large = LyricHighlightRenderTests.Sheet(
            title: "Under the line, largest text · the kept title card",
            columns: largeTimes.map { String(format: "t = %.2f", $0) }, rows: largeLayouts.map(\.title),
            cells: largeRows, cellWidth: 420)
        if let image = large.image() {
            try BGRAImage.writePNG(image, to: folder.appendingPathComponent("title-card-large-text.png"))
        }
        log.append("held line 2: \(LyricPhraseTiming.isHeld(2, in: Self.lines))")
        try log.joined(separator: "\n").write(to: folder.appendingPathComponent("title-card-log.txt"),
                                              atomically: true, encoding: .utf8)
    }
}
