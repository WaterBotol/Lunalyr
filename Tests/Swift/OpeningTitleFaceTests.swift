import AppKit
import CoreText
import SwiftUI
import XCTest
@testable import VersoCustom

/// The title card holds its shape in any face: the words are measured by
/// their ink, so a system, serif, condensed or script face -- or a pixel
/// face whose line box ends at its capitals -- sets the title at much the
/// same height, lines its capitals up with the cover's top (or centres a
/// short block on the cover), and never leaves a hole between the title
/// and the details.
@MainActor
final class OpeningTitleFaceTests: XCTestCase {
    struct Measured {
        var coverTop = 0, coverBottom = 0
        /// The bands of rows with type in them, top to bottom.
        var bands: [(top: Int, bottom: Int)] = []
        var titleHeight: Int { bands.first.map { $0.bottom - $0.top + 1 } ?? 0 }
        var side: Int { coverBottom - coverTop + 1 }
    }

    /// Renders a settled card in the current lyric font, a flat magenta
    /// cover and white capitals on black, and finds the cover and the
    /// bands of type beside it.
    static func measure(title: String) throws -> Measured {
        let screen = OpeningTitlePreviewTests.screen
        let view = OpeningTitleCard(phase: OpeningTitle.Phase(age: 10, exit: 0, size: .full, moving: false),
                                    title: title, artist: "WALLOWS", album: "TELL ME THAT ITS OVER", reading: nil,
                                    cover: OpeningTitleCardLayoutPreviewTests.flatCover,
                                    baseSize: LyricViewportMetrics(size: screen).baseSize, mood: .neutral)
            .frame(width: screen.width, height: screen.height)
            .background(Color.black)
            .environment(\.lyricReduceMotion, true)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        let rep = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        var result = Measured()
        var coverRows: [Int] = [], coverRight = 0
        for y in 0..<rep.pixelsHigh {
            for x in stride(from: 0, to: rep.pixelsWide / 2, by: 2) {
                guard let c = rep.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) else { continue }
                if c.redComponent > 0.7, c.greenComponent < 0.3, c.blueComponent > 0.7 {
                    coverRows.append(y); coverRight = max(coverRight, x)
                }
            }
        }
        result.coverTop = coverRows.min() ?? 0
        result.coverBottom = coverRows.max() ?? 0
        var typeRows: [Int] = []
        for y in max(0, result.coverTop - 200)..<min(rep.pixelsHigh, result.coverBottom + 200) {
            for x in stride(from: coverRight + 4, to: rep.pixelsWide, by: 1) {
                guard let c = rep.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) else { continue }
                if c.redComponent > 0.75, c.greenComponent > 0.75, c.blueComponent > 0.75 { typeRows.append(y); break }
            }
        }
        for row in typeRows {
            if let last = result.bands.last, row <= last.bottom + 2 {
                result.bands[result.bands.count - 1].bottom = row
            } else {
                result.bands.append((row, row))
            }
        }
        return result
    }

    /// Sets the lyric font for the shared settings the card reads; nil
    /// when the family is not on this Mac.
    static func use(_ family: String?, _ settings: VersoCustom.Settings) -> Bool {
        guard let family else {
            settings.fontChoice = .appleMusic
            return true
        }
        guard InstalledFontFamily.resolve(family) != nil else { return false }
        settings.fontChoice = .installed
        settings.installedFontFamily = family
        settings.resolveInstalledFont()
        return settings.activeCustomFaces.regular != nil
    }

    func testTheCardKeepsItsShapeInAnyFace() throws {
        let settings = Settings.shared
        let saved = (settings.fontChoice, settings.installedFontFamily)
        defer {
            settings.fontChoice = saved.0
            settings.installedFontFamily = saved.1
            settings.resolveInstalledFont()
        }
        // System, serif, condensed, script, and the owner's pixel face
        // where this Mac has it.
        let faces: [String?] = [nil, "Charter", "Impact", "Snell Roundhand", "minecrafter"]
        var heights: [String: Double] = [:]
        for face in faces {
            guard Self.use(face, settings) else { continue }
            let name = face ?? "System"
            let card = try Self.measure(title: "MARVELOUS")
            let side = Double(card.side)
            XCTAssertGreaterThan(side, 60, "\(name): the cover was drawn")
            XCTAssertEqual(card.bands.count, 3, "\(name): title, artist and album: \(card.bands)")
            guard card.bands.count == 3, let first = card.bands.first, let last = card.bands.last else { continue }
            heights[name] = Double(card.titleHeight) / side
            // A one-line title's block is shorter than the cover: centred
            // on it, within a few per cent, rather than top-aligned with a
            // hole under the title.
            let above = Double(first.top - card.coverTop), below = Double(card.coverBottom - last.bottom)
            XCTAssertGreaterThanOrEqual(above, -0.03 * side, "\(name): the title stays within the cover's top")
            XCTAssertGreaterThanOrEqual(below, -0.03 * side, "\(name): the details stay within the cover's foot")
            XCTAssertEqual(above, below, accuracy: 0.06 * side, "\(name): the block is centred on the cover")
            // No hole: the title and the artist a set step apart.
            let gap = Double(card.bands[1].top - first.bottom) / side
            XCTAssertEqual(gap, OpeningTitle.blockGap, accuracy: 0.07, "\(name): the step under the title (a script's swashes reach into it)")
        }
        XCTAssertGreaterThanOrEqual(heights.count, 4)
        // The title's capitals stand at much the same height in every face.
        let mean = heights.values.reduce(0, +) / Double(max(1, heights.count))
        for (name, height) in heights {
            XCTAssertEqual(height, mean, accuracy: mean * 0.15, "\(name): title cap height \(height) vs \(mean)")
        }
    }

    /// A block about as tall as the cover runs from its top to its foot:
    /// the title's capitals on the cover's top, the last baseline on its foot.
    func testATwoLineTitleSpansTheCover() throws {
        let settings = Settings.shared
        let saved = settings.fontChoice
        defer { settings.fontChoice = saved }
        settings.fontChoice = .appleMusic
        let card = try Self.measure(title: "EVERYTHING YOU SAID BEFORE THE SUMMER")
        let side = Double(card.side)
        let first = try XCTUnwrap(card.bands.first), last = try XCTUnwrap(card.bands.last)
        XCTAssertEqual(Double(first.top - card.coverTop), 0, accuracy: 0.04 * side, "capitals on the cover's top")
        XCTAssertEqual(Double(card.coverBottom - last.bottom), 0, accuracy: 0.04 * side, "last baseline on its foot")
    }

    /// A wide face steps the title down to fit two lines before it would
    /// break a word or narrow it.
    func testATitleFitsByWholeWords() {
        let font = CTFontCreateWithName("Impact" as CFString, 60, nil)
        XCTAssertTrue(OpeningTitle.titleFits("MARVELOUS", font: font, width: 600, lines: 2))
        XCTAssertFalse(OpeningTitle.titleFits("MARVELOUS", font: font, width: 150, lines: 2), "a word would break")
        XCTAssertFalse(OpeningTitle.titleFits("Everything You Said Before the Summer Ended", font: font, width: 300, lines: 2))
    }

    /// The measures themselves: a face whose line box ends at its capitals
    /// gets room above them, and a lower-case-heavy script is set larger.
    func testTypeMetricsEvenOutFaces() {
        let charter = OpeningTitle.TypeMetrics("MARVELOUS", font: CTFontCreateWithName("Charter-Black" as CFString, 100, nil))
        XCTAssertEqual(charter.scale, 1.0, accuracy: 0.1)
        XCTAssertEqual(charter.padTop, 0, accuracy: 0.001, "Charter's line box has room above its capitals")
        let script = OpeningTitle.TypeMetrics("wave to earth", font: CTFontCreateWithName("SnellRoundhand" as CFString, 100, nil))
        XCTAssertGreaterThan(script.scale, 1.1, "small lower case is set larger")
        let kana = OpeningTitle.TypeMetrics("夜に駆ける", font: CTFontCreateWithName("Charter-Black" as CFString, 100, nil))
        XCTAssertEqual(kana.scale, 1.0, accuracy: 0.05, "kana in a fallback face keeps its size")
        if let pixel = CTFontManagerCopyAvailableFontFamilyNames() as? [String], pixel.contains("minecrafter") {
            let metrics = OpeningTitle.TypeMetrics("MARVELOUS", font: CTFontCreateWithName("minecrafter" as CFString, 100, nil))
            XCTAssertGreaterThan(metrics.padTop, 0.2, "the pixel face's tight line box gets room above")
        }
    }
}
