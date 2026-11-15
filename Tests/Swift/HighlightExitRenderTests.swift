import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Renders highlighted words leaving, frame by frame (every 1/30 s), for
/// every layout with "Cutout · Highlight" and for Portal's own pills, over
/// a dark visualiser and a light picture, as contact sheets. Nothing is
/// asserted about the pictures; they are for a person to compare before
/// and after a change to how highlights leave.
///
/// Skipped unless LUNALYR_HIGHLIGHT_EXIT_DIR names a folder (pass it as
/// TEST_RUNNER_LUNALYR_HIGHLIGHT_EXIT_DIR through xcodebuild). Sheets are
/// written as `<prefix>-<layout>-<backdrop>.png`, the prefix from
/// LUNALYR_HIGHLIGHT_EXIT_PREFIX (default "sheet").
final class HighlightExitRenderTests: XCTestCase {
    static let variable = "LUNALYR_HIGHLIGHT_EXIT_DIR"

    static func value(_ name: String) -> String? {
        let environment = ProcessInfo.processInfo.environment
        let value = environment[name] ?? environment["TEST_RUNNER_" + name]
        return value?.isEmpty == false ? value : nil
    }

    /// "Holding on to every golden night" ends at 21.8 s; the next line is
    /// sung from 22.4 s. The strip runs from just before the end of the
    /// line through the next line's first words.
    static let times: [Double] = stride(from: 21.62, through: 22.92, by: 1.0 / 30).map { $0 }
    /// Mid-line: "every" hands over to "golden" at 20.0 s (Portal's pill
    /// passes on, Cutout's label lifts).
    static let fastTimes: [Double] = stride(from: 30.2, through: 31.0, by: 1.0 / 30).map { $0 }
    static let midTimes: [Double] = stride(from: 19.88, through: 20.58, by: 1.0 / 30).map { $0 }

    @MainActor
    func testRenderHighlightExits() throws {
        guard let path = Self.value(Self.variable) else {
            throw XCTSkip("Renders only when \(Self.variable) names a folder.")
        }
        let folder = URL(fileURLWithPath: path, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let prefix = Self.value("LUNALYR_HIGHLIGHT_EXIT_PREFIX") ?? "sheet"
        let session = try LyricHighlightRenderTests.Session(folder: folder)
        defer { session.restore() }
        let look = LyricHighlightRenderTests.looks.last!
        // Every layout with "Cutout · Highlight"; Portal also as it is
        // (its live pill is a highlight of its own).
        typealias Config = (name: String, layout: LyricComposition, entrance: WordArrival?, reduce: Bool,
                            times: [Double], abut: Bool)
        var configs: [Config] = LyricComposition.allCases.map { (layout: LyricComposition) -> Config in
            (layout.rawValue, layout, .cutoutHighlight, false, Self.times, false)
        }
        configs.append(("portal-pills", .portal, nil, false, Self.times, false))
        configs += [("reduce-scattered", .scattered, .cutoutHighlight, true, Self.times, false),
                    ("reduce-spoken", .spoken, .cutoutHighlight, true, Self.times, false),
                    ("reduce-portal-pills", .portal, nil, true, Self.times, false)]
        // A word handing over mid-line.
        configs += [("mid-portal-pills", .portal, nil, false, Self.midTimes, false),
                    ("mid-cutout", .cutout, .cutoutHighlight, false, Self.midTimes, false),
                    ("mid-spoken", .spoken, .cutoutHighlight, false, Self.midTimes, false)]
        // The line's end given as the next line's start (22.4 s), as many
        // sources give it: the highlight is released after the line has
        // started to leave.
        configs += [LyricComposition.scattered, .echo, .spoken, .portal].map { (layout: LyricComposition) -> Config in
            ("abut-\(layout.rawValue)", layout, .cutoutHighlight, false, Self.times, true)
        }
        // Words 0.2-0.5 s apart (君と歩いたあの道を): Cutout's labels lifting
        // away one after another.
        configs.append(("fast-cutout", .cutout, .cutoutHighlight, false, Self.fastTimes, false))
        let only = Self.value("LUNALYR_HIGHLIGHT_EXIT_ONLY").map { Set($0.split(separator: ",").map(String.init)) }
        for config in configs where only?.contains(config.name) ?? true {
            session.entrance = config.entrance
            session.reduceMotion = config.reduce
            var lines = LyricHighlightRenderTests.lines
            if config.abut { lines[1].end = lines[2].start }
            if LyricsViewModel.shared.lines != lines { LyricsViewModel.shared.lines = lines }
            for backdrop in [LyricHighlightRenderTests.Backdrop.visualiser, .picture] {
                if (config.reduce || config.abut || config.times != Self.times) && backdrop == .picture { continue }
                session.apply(look, backdrop)
                VersoCustom.Settings.shared.lyricComposition = config.layout
                let times = config.times
                let renderer = session.makeRenderer(backdrop, look, time: times[0])
                session.settle(renderer, backdrop, look, time: times[0])
                var frames: [CGImage] = []
                for time in times {
                    let image = try session.capture(renderer, backdrop, look, time: time, advance: 1.0 / 30)
                    frames.append(LyricHighlightRenderTests.crop(image, LyricHighlightRenderTests.closeUp(config.layout)))
                }
                let sheet = LyricHighlightRenderTests.Sheet(
                    title: "\(prefix) · \(config.name) · \(backdrop.title) · "
                        + (config.times == Self.times ? "line ends \(config.abut ? "22.40" : "21.80"), next line 22.40"
                           : config.times == Self.fastTimes ? "君 30.0, と 30.3, 歩いた 30.5" : "golden sung at 20.00"),
                    columns: times.map { String(format: "%.3f", $0) }, rows: [""], cells: [frames],
                    wrap: 8, cellWidth: 300)
                if let image = sheet.image() {
                    try BGRAImage.writePNG(image, to: folder.appendingPathComponent(
                        "\(prefix)-\(config.name)-\(backdrop == .visualiser ? "dark" : "light").png"))
                }
            }
        }
        session.entrance = nil
        session.reduceMotion = false
    }
}
