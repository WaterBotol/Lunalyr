import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Constellation uses the stage at 16:9, 21:9 and in a small floating
/// window, instead of one compact block, and stays on screen. With
/// LUNALYR_CONSTELLATION_FILL_DIR set (pass it as
/// TEST_RUNNER_LUNALYR_CONSTELLATION_FILL_DIR) it writes before and after
/// stills there.
@MainActor
final class ConstellationFillTests: XCTestCase {
    struct Case { let name: String; let display: CGSize; let textSize: CGFloat? }
    static let cases = [
        Case(name: "16x9-1920x1080", display: CGSize(width: 1920, height: 1080), textSize: nil),
        Case(name: "21x9-3440x1440", display: CGSize(width: 3440, height: 1440), textSize: nil),
        Case(name: "floating-480x220", display: CGSize(width: 480, height: 220), textSize: 28),
    ]
    static let words: [LyricsFetcher.Word] = {
        let texts = "I keep running back to the lights of this town tonight".split(separator: " ").map(String.init)
        return texts.enumerated().map { index, text in
            LyricsFetcher.Word(text: text, start: Double(index) * 0.2, end: Double(index) * 0.2 + 0.6)
        }
    }()

    override func tearDown() {
        ConstellationFill.isEnabled = true
        super.tearDown()
    }

    private func render(_ test: Case, fill: Bool) throws -> (ink: CGRect, image: NSBitmapImageRep) {
        ConstellationFill.isEnabled = fill
        defer { ConstellationFill.isEnabled = true }
        let metrics = LyricViewportMetrics(size: test.display, textSize: test.textSize)
        let stage = LyricLineView(words: Self.words, time: 4, stageSize: metrics.stageSize,
                                  baseSize: metrics.baseSize, mood: .neutral, composition: .constellation, seed: 2)
            .modifier(FittedLyricStage(available: metrics.stageSize, userScale: 1))
            .frame(maxWidth: metrics.stageSize.width)
        let canvas = WallpaperViewport(size: test.display) { Color.black } lyrics: { stage }
        let renderer = ImageRenderer(content: canvas)
        renderer.scale = 1
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        var ink = CGRect.null
        for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) {
            for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
                guard let pixel = bitmap.colorAt(x: x, y: y), pixel.numberOfComponents >= 3,
                      max(pixel.redComponent, pixel.greenComponent, pixel.blueComponent) > 0.45 else { continue }
                ink = ink.union(CGRect(x: CGFloat(x) / CGFloat(bitmap.pixelsWide),
                                       y: CGFloat(y) / CGFloat(bitmap.pixelsHigh), width: 0, height: 0))
            }
        }
        XCTAssertFalse(ink.isNull, test.name)
        return (ink, bitmap)
    }

    func testTheFieldSpreadsAcrossTheStageAndStaysOnScreen() throws {
        let folder = ProcessInfo.processInfo.environment["LUNALYR_CONSTELLATION_FILL_DIR"]
            ?? ProcessInfo.processInfo.environment["TEST_RUNNER_LUNALYR_CONSTELLATION_FILL_DIR"]
        for test in Self.cases {
            let before = try render(test, fill: false)
            let after = try render(test, fill: true)
            // On screen, clear of the menu bar, the Dock and the window's edges.
            XCTAssertGreaterThan(after.ink.minX, 0.04, test.name)
            XCTAssertLessThan(after.ink.maxX, 0.96, test.name)
            XCTAssertGreaterThan(after.ink.minY, 0.06, test.name)
            XCTAssertLessThan(after.ink.maxY, 0.94, test.name)
            // Uses more of the stage than the old compact block.
            XCTAssertGreaterThanOrEqual(after.ink.width, before.ink.width - 0.01, test.name)
            if test.textSize == nil {
                XCTAssertGreaterThan(after.ink.width, 0.5, "\(test.name) spreads across the screen")
            }
            if let folder, !folder.isEmpty {
                let url = URL(fileURLWithPath: folder)
                try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
                for (label, shot) in [("before", before.image), ("after", after.image)] {
                    try shot.representation(using: .png, properties: [:])?
                        .write(to: url.appendingPathComponent("constellation-\(test.name)-\(label).png"))
                }
                print("constellation-fill \(test.name): ink width \(before.ink.width) -> \(after.ink.width), height \(before.ink.height) -> \(after.ink.height)")
            }
        }
    }
}
