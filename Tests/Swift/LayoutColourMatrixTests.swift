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
                                             album: album, backdrop: backdrop, vocal: 0.6)
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
        let stage = CGSize(width: 720, height: 300)
        let view = LyricLineView(words: words, time: 3.6, stageSize: stage, baseSize: 52, composition: composition,
                                 seed: 4, lineEnd: 6)
            .environment(\.lyricInk, ink)
            .frame(width: stage.width, height: stage.height)
            .background(Color.black)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        guard let image = renderer.cgImage else { throw XCTSkip("no render") }
        let bitmap = NSBitmapImageRep(cgImage: image)
        var bright = 0, coloured = 0
        for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) {
            for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
                guard let p = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) else { continue }
                let lab = Perceptual.oklab(sRGB: SIMD3(Double(p.redComponent), Double(p.greenComponent), Double(p.blueComponent)))
                guard lab.l > 0.55 else { continue }
                bright += 1
                if lab.chroma > 0.05 { coloured += 1 }
            }
        }
        return (bright, coloured)
    }

    func testEveryLayoutCarriesEveryColourMode() throws {
        let modes: [LyricColourMode] = [.standard, .albumDrift, .customGradient, .sungSweep, .musicReactive]
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

    /// Not a rule, a measurement: how much colour the custom gradient keeps
    /// as the wallpaper behind the lyrics gets lighter (the 3:1 lift).
    func testCustomGradientChromaAgainstLighterBackdrops() {
        var line = "CUSTOM-GRADIENT-LIFT"
        for backdrop in [0.05, 0.2, 0.35, 0.5, 0.7] {
            let ink = Self.ink(.customGradient, backdrop: backdrop)
            let chroma = (0...10).map { slot -> Double in
                let c = NSColor(ink.sample(slot: Double(slot) / 10).rest).usingColorSpace(.sRGB)!
                return Perceptual.oklab(sRGB: SIMD3(Double(c.redComponent), Double(c.greenComponent), Double(c.blueComponent))).chroma
            }.reduce(0, +) / 11
            line += String(format: " backdrop %.2f: chroma %.3f;", backdrop, chroma)
        }
        print(line)
    }
}
