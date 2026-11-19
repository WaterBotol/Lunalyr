import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Every layout under every colour mode, through the shared colour path
/// (`LyricInkDirector` → `\.lyricInk` → the layout). Once the line has
/// been sung, the words should carry the mode's colour: white for Static,
/// colour for the others, and Sung sweep's colour kept on words already
/// sung. Prints the table it measured.
@MainActor
final class LayoutColourMatrixTests: XCTestCase {
    static let album: [SIMD3<Float>] = [SIMD3(0.85, 0.15, 0.2), SIMD3(0.1, 0.3, 0.9), SIMD3(0.95, 0.75, 0.1),
                                        SIMD3(0.1, 0.7, 0.4)]
    static let custom = [OKLab(l: 0.8, chroma: 0.16, hue: 2.4), OKLab(l: 0.7, chroma: 0.18, hue: 5.6)]

    static func ink(_ mode: LyricColourMode, backdrop: Double = 0.05) -> LyricInk {
        let director = LyricInkDirector()
        let inputs = LyricInkDirector.Inputs(mode: mode, custom: mode == .customGradient ? custom : [],
                                             album: album, backdrop: backdrop)
        var ink = director.ink(inputs, wall: 0)
        for frame in 1...300 { ink = director.ink(inputs, wall: Double(frame) / 60) }
        return ink
    }

    static let words: [LyricsFetcher.Word] = {
        let texts = "Hold me under every light tonight".split(separator: " ").map(String.init)
        return texts.enumerated().map { index, text in
            LyricsFetcher.Word(text: text, start: 1 + Double(index) * 0.35, end: 1 + Double(index) * 0.35 + 0.3)
        }
    }()

    /// Bright text pixels, and how many of them carry colour (OKLab chroma).
    static func measure(_ composition: LyricComposition, ink: LyricInk) throws -> (bright: Int, coloured: Int) {
        try measure(composition, ink: ink, wallpaper: nil).counts
    }

    /// The grey of luminance `luminance`, in sRGB.
    static func grey(_ luminance: Double) -> Color {
        let encoded = luminance <= 0.0031308 ? 12.92 * luminance : 1.055 * pow(luminance, 1 / 2.4) - 0.055
        return Color(.sRGB, red: encoded, green: encoded, blue: encoded)
    }

    /// On a dark wallpaper (`wallpaper` nil): bright pixels, and how many
    /// carry colour. On a light one: pixels at 2:1 or more against it
    /// (the type), and how many of those carry colour.
    static func measure(_ composition: LyricComposition, ink: LyricInk, wallpaper: Double?,
                        write url: URL? = nil) throws -> (counts: (bright: Int, coloured: Int), contrast: Double) {
        let stage = CGSize(width: 720, height: 300)
        let view = LyricLineView(words: words, time: 3.6, stageSize: stage, baseSize: 52, composition: composition,
                                 seed: 4, lineEnd: 6)
            .environment(\.lyricInk, ink)
            .frame(width: stage.width, height: stage.height)
            .background(wallpaper.map(grey) ?? Color.black)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        guard let image = renderer.cgImage else { throw XCTSkip("no render") }
        let bitmap = NSBitmapImageRep(cgImage: image)
        if let url { try bitmap.representation(using: .png, properties: [:])?.write(to: url) }
        var bright = 0, coloured = 0, best = 1.0
        // The wallpaper as drawn (a corner), so colour management applies
        // to it and the type alike.
        let drawn: Double? = wallpaper.flatMap { _ in
            bitmap.colorAt(x: 2, y: 2)?.usingColorSpace(.sRGB).map {
                Perceptual.luminance(sRGB: SIMD3(Double($0.redComponent), Double($0.greenComponent), Double($0.blueComponent)))
            }
        }
        for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) {
            for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
                guard let p = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) else { continue }
                let rgb = SIMD3(Double(p.redComponent), Double(p.greenComponent), Double(p.blueComponent))
                let lab = Perceptual.oklab(sRGB: rgb)
                if let wallpaper {
                    let ratio = Perceptual.contrast(Perceptual.luminance(sRGB: rgb), drawn ?? wallpaper)
                    guard ratio >= 2 else { continue }
                    best = max(best, ratio)
                } else {
                    guard lab.l > 0.55 else { continue }
                }
                bright += 1
                if lab.chroma > 0.04 { coloured += 1 }
            }
        }
        return ((bright, coloured), best)
    }

    func testEveryLayoutCarriesEveryColourMode() throws {
        let modes: [LyricColourMode] = [.standard, .albumDrift, .customGradient, .sungSweep]
        let inks = Dictionary(uniqueKeysWithValues: modes.map { ($0, Self.ink($0)) })
        var table = "layout | " + modes.map(\.title).joined(separator: " | ") + "\n"
        for composition in LyricComposition.allCases {
            var row: [String] = []
            for mode in modes {
                let (bright, coloured) = try Self.measure(composition, ink: inks[mode]!)
                let share = bright == 0 ? 0 : Double(coloured) / Double(bright)
                row.append(String(format: "%.2f", share))
                XCTAssertGreaterThan(bright, 40, "\(composition.title) draws its words")
                // Static white is white, except where a layout sets words
                // on highlight tiles by design (Cutout's labels, Portal's
                // pills) or Cascade's held words take the highlight's hue.
                if mode == .standard, ![.cascade, .cutout, .portal].contains(composition) {
                    XCTAssertLessThan(share, 0.05, "\(composition.title) · Static white stays white")
                }
                if mode != .standard {
                    XCTAssertGreaterThan(share, 0.5, "\(composition.title) · \(mode.title): sung words carry the colour")
                }
            }
            table += composition.title + " | " + row.joined(separator: " | ") + "\n"
        }
        print("LAYOUT-COLOUR-MATRIX\n" + table)
    }

    /// The same on light wallpapers: the lyrics turn dark (deep colours,
    /// a deep neutral for Static), every layout and every mode, at 3:1 or
    /// better. With LUNALYR_DARK_LYRICS_DIR set (TEST_RUNNER_…), writes
    /// stills of four layouts there.
    func testEveryLayoutTurnsDarkOnALightWallpaper() throws {
        let folder = ProcessInfo.processInfo.environment["LUNALYR_DARK_LYRICS_DIR"]
            ?? ProcessInfo.processInfo.environment["TEST_RUNNER_LUNALYR_DARK_LYRICS_DIR"]
        if let folder, !folder.isEmpty {
            try FileManager.default.createDirectory(at: URL(fileURLWithPath: folder), withIntermediateDirectories: true)
        }
        let modes: [LyricColourMode] = [.standard, .albumDrift, .customGradient, .sungSweep]
        for wallpaper in [0.5, 0.7] {
            var table = "LIGHT-WALLPAPER \(wallpaper)\nlayout | " + modes.map(\.title).joined(separator: " | ") + "\n"
            let inks = Dictionary(uniqueKeysWithValues: modes.map { ($0, Self.ink($0, backdrop: wallpaper)) })
            for mode in modes { XCTAssertEqual(inks[mode]!.dark, 1, "dark text on \(wallpaper)") }
            for composition in LyricComposition.allCases {
                var row: [String] = []
                for mode in modes {
                    let still = [LyricComposition.scattered, .constellation, .spoken, .hinge, .cutout, .portal].contains(composition)
                        && [LyricColourMode.customGradient, .sungSweep, .standard, .albumDrift].contains(mode)
                    let url = folder.flatMap { still && !$0.isEmpty
                        ? URL(fileURLWithPath: $0).appendingPathComponent(
                            "\(composition.rawValue)-\(mode.rawValue)-wallpaper\(Int(wallpaper * 100)).png") : nil }
                    let result = try Self.measure(composition, ink: inks[mode]!, wallpaper: wallpaper, write: url)
                    let share = result.counts.bright == 0 ? 0 : Double(result.counts.coloured) / Double(result.counts.bright)
                    row.append(String(format: "%.2f (%.1f:1)", share, result.contrast))
                    XCTAssertGreaterThan(result.counts.bright, 40, "\(composition.title) · \(mode.title): dark type shows")
                    // The colours themselves reach 3:1 (see the chroma test); drawn,
                    // the rolling layouts' and the tile layouts' plain words have
                    // stepped back to part opacity by now, and their sung word
                    // sits on a tile, so less is measured there.
                    let floor = [LyricComposition.spoken, .cascade, .cutout, .portal].contains(composition) ? 2.5 : 2.95
                    XCTAssertGreaterThanOrEqual(result.contrast, floor, "\(composition.title) · \(mode.title) on \(wallpaper)")
                    if mode != .standard {
                        XCTAssertGreaterThan(share, 0.4, "\(composition.title) · \(mode.title): deep colour, not grey")
                    }
                }
                table += composition.title + " | " + row.joined(separator: " | ") + "\n"
            }
            print(table)
        }
    }

    /// Light text below the switch, dark above it, with hysteresis, easing
    /// over `toneEase`.
    func testTheSwitchToDarkTextHasHysteresisAndEases() {
        let director = LyricInkDirector()
        func step(_ backdrop: Double, at wall: Double) -> LyricInk {
            director.ink(.init(mode: .albumDrift, album: Self.album, backdrop: backdrop), wall: wall)
        }
        XCTAssertEqual(step(0.30, at: 0).dark, 0, "between the thresholds, light stays light")
        XCTAssertEqual(step(0.36, at: 0.1).dark, 0.1 / LyricInkDirector.toneEase, accuracy: 0.01, "eases, not snaps")
        var ink = step(0.36, at: 0.2)
        for frame in 3...12 { ink = step(0.36, at: Double(frame) / 10) }
        XCTAssertEqual(ink.dark, 1)
        // Hovering back between the thresholds keeps it dark.
        for frame in 13...30 { ink = step(0.28, at: Double(frame) / 10) }
        XCTAssertEqual(ink.dark, 1)
        for frame in 31...45 { ink = step(0.2, at: Double(frame) / 10) }
        XCTAssertEqual(ink.dark, 0)
    }

    /// How much colour the custom gradient's words keep as the wallpaper
    /// behind them gets lighter. Before: the plain 3:1 lift (no tint kept).
    /// Now: the same lift keeping a tint (up to chroma 0.05, at 2.5:1)
    /// where one exists, as the line's base colour and Sung sweep already
    /// did. Measured: 0.05 0.166 -> 0.166, 0.20 0.079 -> 0.079,
    /// 0.35 0.000 -> 0.007, 0.50 and 0.70 0.000 -> 0.000.
    func testCustomGradientKeepsColourOnLighterWallpapers() {
        func chroma(_ colour: Color) -> Double {
            let c = NSColor(colour).usingColorSpace(.sRGB)!
            return Perceptual.oklab(sRGB: SIMD3(Double(c.redComponent), Double(c.greenComponent), Double(c.blueComponent))).chroma
        }
        var line = "CUSTOM-GRADIENT-LIFT"
        for backdrop in [0.05, 0.2, 0.35, 0.5, 0.7] {
            let ink = Self.ink(.customGradient, backdrop: backdrop)
            let slots = (0...10).map { Double($0) / 10 }
            let before = slots.map { slot in
                chroma(Perceptual.color(Perceptual.readable(ink.gradient(slot: slot), background: ink.backdrop,
                                                            ratio: ink.contrast)))
            }.reduce(0, +) / Double(slots.count)
            let after = slots.map { chroma(ink.sample(slot: $0).rest) }.reduce(0, +) / Double(slots.count)
            let contrast = slots.map { slot -> Double in
                let c = NSColor(ink.sample(slot: slot).rest).usingColorSpace(.sRGB)!
                return Perceptual.contrast(Perceptual.luminance(sRGB: SIMD3(Double(c.redComponent), Double(c.greenComponent),
                                                                            Double(c.blueComponent))), backdrop)
            }.min() ?? 0
            line += String(format: " backdrop %.2f: chroma %.3f -> %.3f (%@, min %.1f:1);", backdrop, before, after,
                           ink.dark > 0.5 ? "dark" : "light", contrast)
            XCTAssertGreaterThanOrEqual(after, before - 0.002, "\(backdrop)")
            // Where 2.5:1 can be reached at all (2.5 * (backdrop + 0.05) - 0.05
            // < 1), every word does, keeping what tint the sRGB gamut allows
            // that close to white.
            if Perceptual.tintedContrast * (ink.backdrop + 0.05) - 0.05 < 1 {
                for slot in slots {
                    let c = NSColor(ink.sample(slot: slot).rest).usingColorSpace(.sRGB)!
                    let lum = Perceptual.luminance(sRGB: SIMD3(Double(c.redComponent), Double(c.greenComponent), Double(c.blueComponent)))
                    XCTAssertGreaterThanOrEqual(Perceptual.contrast(lum, ink.backdrop), Perceptual.tintedContrast - 0.05)
                }
            }
        }
        print(line)
    }
}
