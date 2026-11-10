import AppKit
import XCTest
@testable import VersoCustom

/// The wallpaper's background transitions (POLISH-AUDIT 2026-11-10, W1-W3,
/// W9, W14, W19, W20): the crossfade stack, the song-change hold, and the
/// first-frame gates.
final class WallpaperSurfaceMotionTests: XCTestCase {
    private typealias Fade = BackgroundCrossfade<String>
    private let start = Date(timeIntervalSinceReferenceDate: 10_000)
    private let duration = 0.8

    private func at(_ seconds: Double) -> Date { start.addingTimeInterval(seconds) }

    /// How much of each layer reaches the screen once the stack is
    /// composited bottom to top (each layer drawn over what is beneath at
    /// its opacity). Opaque layers: their contributions sum to 1 when
    /// nothing shows through.
    private func weights(_ drawn: [Fade.Drawn]) -> [String: Double] {
        var weights: [String: Double] = [:]
        for layer in drawn {
            for key in weights.keys { weights[key]! *= 1 - layer.opacity }
            weights[layer.layer, default: 0] += layer.opacity
        }
        return weights
    }

    // MARK: W3 -- no dip at the midpoint

    /// Both layers used to be drawn translucent over black (`f` and `1-f`),
    /// which composited to about 75% brightness halfway: every song change
    /// breathed darker. The outgoing layer now stays fully drawn.
    func testACrossfadeBetweenOpaqueLayersKeepsFullCoverage() {
        var fade = Fade("A")
        fade.change(to: "B", ready: true, at: start, duration: duration)
        for step in stride(from: 0.0, through: duration, by: 0.05) {
            let total = weights(fade.layers(at: at(step), duration: duration)).values.reduce(0, +)
            XCTAssertEqual(total, 1, accuracy: 1e-9, "Coverage at \(step) s")
        }
        let halfway = fade.layers(at: at(duration / 2), duration: duration)
        assertDrawn(halfway, [.init(layer: "A", opacity: 1), .init(layer: "B", opacity: 0.5)])
    }

    /// Your wallpaper shows the desktop, so it cannot cover what it replaces:
    /// that fades out beneath it, as before.
    func testATransparentLayerFadesOutWhatItReplaces() {
        var fade = Fade("colour")
        fade.change(to: "desktop", ready: true, at: start, duration: duration)
        let drawn = fade.layers(at: at(duration / 2), duration: duration, isOpaque: { $0 != "desktop" })
        assertDrawn(drawn, [.init(layer: "colour", opacity: 0.5), .init(layer: "desktop", opacity: 0.5)])
        // And back: the colour field covers the desktop as it fades in.
        fade.change(to: "colour", ready: true, at: at(5), duration: duration)
        let back = fade.layers(at: at(5 + duration / 2), duration: duration, isOpaque: { $0 != "desktop" })
        assertDrawn(back, [.init(layer: "desktop", opacity: 1), .init(layer: "colour", opacity: 0.5)])
    }

    // MARK: W1 -- interruptions carry on

    /// A→B interrupted by C used to drop A in one frame and jump B from
    /// its fade to full under C. Now nothing on screen changes at the
    /// moment C arrives, and B carries on fading in under it.
    func testAnInterruptedCrossfadeCarriesOn() {
        var fade = Fade("A")
        fade.change(to: "B", ready: true, at: start, duration: duration)
        let interrupt = at(0.3)
        let before = weights(fade.layers(at: interrupt, duration: duration))
        fade.change(to: "C", ready: true, at: interrupt, duration: duration)
        let after = weights(fade.layers(at: interrupt, duration: duration))
        XCTAssertEqual(after["A"] ?? 0, before["A"] ?? 0, accuracy: 1e-9)
        XCTAssertEqual(after["B"] ?? 0, before["B"] ?? 0, accuracy: 1e-9)
        XCTAssertEqual(after["C"] ?? 0, 0, accuracy: 1e-9)
        XCTAssertEqual(fade.entries.map(\.layer), ["A", "B", "C"])

        // B reaches full while C is still coming in: A is no longer drawn.
        let bDone = fade.layers(at: at(duration + 0.01), duration: duration).map(\.layer)
        XCTAssertEqual(bDone, ["B", "C"])
        // C done: only C, and the next change lets the rest go.
        XCTAssertEqual(fade.layers(at: at(0.31 + duration), duration: duration).map(\.layer), ["C"])
        fade.change(to: "D", ready: true, at: at(5), duration: duration)
        XCTAssertEqual(fade.entries.map(\.layer), ["C", "D"])
    }

    /// Two changes in the same instant -- a song change, then its effect
    /// clock renewing -- are one crossfade: the first was never seen.
    func testAChangeReplacedBeforeItShowedIsSkipped() {
        var fade = Fade("A")
        fade.change(to: "B", ready: true, at: start, duration: duration)
        fade.change(to: "C", ready: true, at: at(0.001), duration: duration)
        XCTAssertEqual(fade.entries.map(\.layer), ["A", "C"])
    }

    /// Changes in quick succession draw at most three layers; the one let
    /// go is the one showing least, so the screen barely moves.
    func testAtMostThreeLayersAreKept() {
        var fade = Fade("A")
        for (index, layer) in ["B", "C", "D", "E"].enumerated() {
            let date = at(0.1 * Double(index + 1))
            let before = weights(fade.layers(at: date, duration: duration))
            fade.change(to: layer, ready: true, at: date, duration: duration)
            let after = weights(fade.layers(at: date, duration: duration))
            for (key, weight) in before {
                XCTAssertEqual(after[key] ?? 0, weight, accuracy: 0.05, "\(key) when \(layer) arrived")
            }
        }
        XCTAssertEqual(fade.entries.count, Fade.maximumLayers)
        XCTAssertEqual(fade.current, "E")
        XCTAssertEqual(fade.entries.map(\.layer), ["A", "B", "E"])
    }

    /// The frame clock runs until the last fade ends, and not at all for a
    /// layer shown at once.
    func testTheClockRunsUntilTheLastFadeEnds() {
        var fade = Fade("A")
        XCTAssertNil(fade.fadesEnd(duration: duration))
        fade.change(to: "B", ready: true, at: start, duration: duration)
        fade.change(to: "C", ready: true, at: at(0.4), duration: duration)
        XCTAssertEqual(fade.fadesEnd(duration: duration)!.timeIntervalSince(at(0.4 + duration)), 0, accuracy: 1e-6)
        // A layer still compiling does not keep the clock running.
        var held = Fade("A")
        held.change(to: "B", ready: true, at: start, duration: duration)
        held.change(to: "D", ready: false, at: at(0.5), duration: duration)
        XCTAssertEqual(held.entries.map(\.layer), ["A", "B", "D"])
        XCTAssertEqual(held.fadesEnd(duration: duration)!.timeIntervalSince(at(duration)), 0, accuracy: 1e-6)
    }

    // MARK: W19 -- the first layer fades in

    /// A wallpaper window used to show black, then its first layer popped
    /// in. It now fades in, once it has something to draw.
    func testTheFirstLayerFadesInFromBlackOnceReady() {
        var fade = Fade("aurora", ready: false, fadingInAt: start)
        assertDrawn(fade.layers(at: at(5), duration: duration), [.init(layer: "aurora", opacity: 0)])
        fade.becameReady(at: at(5))
        assertDrawn(fade.layers(at: at(5 + duration / 2), duration: duration),
                    [.init(layer: "aurora", opacity: 0.5)])
        XCTAssertEqual(fade.layers(at: at(5 + duration), duration: duration), [.init(layer: "aurora", opacity: 1)])
        // Replaced before it ever showed: the next one fades in from black.
        var early = Fade("aurora", ready: true, fadingInAt: start)
        early.change(to: "plasma", ready: true, at: start, duration: duration)
        XCTAssertEqual(early.entries.map(\.layer), ["plasma"])
    }

    // MARK: W20 -- changes while paused apply at once

    /// A paused wallpaper cannot run a fade: NowPlayingView starts its fades
    /// at `.distantPast` then, so the still frame shows the new layer (and
    /// colours) together and resuming does not jump.
    func testAChangeWhilePausedIsShownAtOnce() {
        var fade = Fade("A")
        fade.change(to: "B", ready: true, at: .distantPast, duration: duration)
        XCTAssertEqual(fade.layers(at: start, duration: duration), [.init(layer: "B", opacity: 1)])
        var held = Fade("A")
        held.change(to: "B", ready: false, at: start, duration: duration)
        held.becameReady(at: .distantPast)
        XCTAssertEqual(held.layers(at: start, duration: duration), [.init(layer: "B", opacity: 1)])
    }

    // MARK: W2 -- one crossfade per song

    private typealias Hold = BackgroundChoiceHold<String, Int>

    /// A skip used to crossfade on the track, again on the cover's colours
    /// and again on the lyrics. The last song's choice now holds until the
    /// new song's inputs are in, then changes once.
    func testASongChangeWaitsForItsInputsThenChangesOnce() {
        var hold = Hold(choice: 1)
        XCTAssertTrue(hold.update(track: "first", choice: 1, settled: true, now: start))
        var changes = 0
        // The track arrives; the palette and the lyrics follow.
        if hold.update(track: "second", choice: 2, settled: false, now: at(1)) { changes += 1 }
        XCTAssertEqual(hold.choice, 1, "The last song's background holds meanwhile")
        if hold.update(track: "second", choice: 3, settled: false, now: at(1.4)) { changes += 1 }
        if hold.update(track: "second", choice: 4, settled: true, now: at(1.9)) { changes += 1 }
        XCTAssertEqual(changes, 1)
        XCTAssertEqual(hold.choice, 4)
        // A lyric upgrade later in the song does not move it.
        XCTAssertFalse(hold.update(track: "second", choice: 5, settled: true, now: at(60)))
        XCTAssertEqual(hold.choice, 4)
    }

    func testASongWhoseInputsNeverSettleIsTakenAtTheLimit() {
        var hold = Hold(choice: 1)
        hold.update(track: "first", choice: 1, settled: true, now: start)
        XCTAssertFalse(hold.update(track: "second", choice: 2, settled: false, now: at(1)))
        XCTAssertEqual(hold.deadline, at(1 + Hold.settleLimit))
        XCTAssertFalse(hold.update(track: "second", choice: 2, settled: false, now: at(3.4)))
        XCTAssertTrue(hold.update(track: "second", choice: 2, settled: false, now: at(3.5)))
        XCTAssertEqual(hold.choice, 2)
        XCTAssertNil(hold.deadline)
    }

    /// Nothing playing keeps the last look; the same song resuming keeps it
    /// too; skipping on before a song settled waits for the newest one.
    func testNothingPlayingKeepsTheLastChoice() {
        var hold = Hold(choice: 1)
        hold.update(track: "first", choice: 7, settled: true, now: start)
        XCTAssertFalse(hold.update(track: nil, choice: 0, settled: false, now: at(1)))
        XCTAssertEqual(hold.choice, 7)
        XCTAssertFalse(hold.update(track: "first", choice: 9, settled: true, now: at(2)))
        XCTAssertEqual(hold.choice, 7)
        hold.update(track: "second", choice: 2, settled: false, now: at(3))
        hold.update(track: "third", choice: 3, settled: false, now: at(5))
        XCTAssertEqual(hold.deadline, at(5 + Hold.settleLimit), "Each new song waits from its own start")
    }

    // MARK: W14 -- the motion backing follows the palette fade

    /// The animated cover's text backing read the raw palette, so it stepped
    /// at a song change; it now reads the colours on screen, which agree
    /// with the raw palette once the fade ends.
    func testCoverLuminanceFromTheColoursOnScreenMatchesThePalette() {
        let grids: [[SIMD3<Double>]] = [
            ColorExtractor.fallbackHSBPalette,
            (0..<9).map { SIMD3(Double($0) / 9, 0.8, 0.3 + Double($0) * 0.07) },
        ]
        for grid in grids {
            XCTAssertEqual(NowPlayingView.coverLuminance(AlbumWallpaperPalette(grid: grid)),
                           LyricLegibility.coverLuminance(hsbGrid: grid), accuracy: 1e-4)
        }
    }

    // MARK: W9 -- imported pictures decode off the main thread

    func testAnImportedPictureDecodesUprightOffTheMainThread() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let url = folder.appendingPathComponent("wallpaper.png")
        let bitmap = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 40, pixelsHigh: 20,
                                                    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                                    isPlanar: false, colorSpaceName: .deviceRGB,
                                                    bytesPerRow: 0, bitsPerPixel: 0))
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to: url)
        let image = try await XCTUnwrapAsync(await CustomWallpaperView.decode(url))
        XCTAssertEqual(image.size, CGSize(width: 40, height: 20))

        let garbage = folder.appendingPathComponent("broken.png")
        try Data("not a picture".utf8).write(to: garbage)
        let broken = await CustomWallpaperView.decode(garbage)
        XCTAssertNil(broken)
    }

    private func assertDrawn(_ drawn: [Fade.Drawn], _ expected: [Fade.Drawn],
                             file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(drawn.map(\.layer), expected.map(\.layer), file: file, line: line)
        for (a, b) in zip(drawn, expected) {
            XCTAssertEqual(a.opacity, b.opacity, accuracy: 1e-6, "\(a.layer)", file: file, line: line)
        }
    }

    private func XCTUnwrapAsync<T>(_ value: T?, file: StaticString = #filePath, line: UInt = #line) async throws -> T {
        try XCTUnwrap(value, file: file, line: line)
    }
}
