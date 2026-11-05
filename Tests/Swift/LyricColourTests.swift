import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

final class LyricColourTests: XCTestCase {
    private let vivid: [SIMD3<Float>] = [SIMD3(0.85, 0.15, 0.2), SIMD3(0.1, 0.3, 0.9), SIMD3(0.2, 0.2, 0.25),
                                         SIMD3(0.95, 0.75, 0.1), SIMD3(0.1, 0.7, 0.4)]
    private let pastel: [SIMD3<Float>] = [SIMD3(0.95, 0.9, 0.85), SIMD3(0.9, 0.95, 1), SIMD3(0.85, 0.85, 0.85),
                                          SIMD3(1, 0.92, 0.95), SIMD3(0.92, 1, 0.92)]
    private let dark: [SIMD3<Float>] = [SIMD3(0.05, 0.05, 0.08), SIMD3(0.1, 0.08, 0.12), SIMD3(0.02, 0.02, 0.03),
                                        SIMD3(0.12, 0.1, 0.1), SIMD3(0.06, 0.08, 0.06)]

    private func rgb(_ colour: Color) -> SIMD3<Double> {
        let resolved = NSColor(colour).usingColorSpace(.sRGB)!
        return SIMD3(Double(resolved.redComponent), Double(resolved.greenComponent), Double(resolved.blueComponent))
    }
    private func luminance(_ colour: Color) -> Double { Perceptual.luminance(sRGB: rgb(colour)) }

    func testOKLabRoundTripsAndAnchorsWhiteAndBlack() {
        let white = Perceptual.oklab(sRGB: SIMD3(1, 1, 1))
        XCTAssertEqual(white.l, 1, accuracy: 1e-4)
        XCTAssertEqual(white.chroma, 0, accuracy: 1e-4)
        XCTAssertEqual(Perceptual.oklab(sRGB: .zero).l, 0, accuracy: 1e-9)
        var generator = SystemRandomNumberGenerator()
        for _ in 0..<500 {
            let colour = SIMD3(Double.random(in: 0...1, using: &generator), Double.random(in: 0...1, using: &generator),
                               Double.random(in: 0...1, using: &generator))
            let back = Perceptual.sRGB(Perceptual.oklab(sRGB: colour))
            XCTAssertEqual(back.x, colour.x, accuracy: 1e-5)
            XCTAssertEqual(back.y, colour.y, accuracy: 1e-5)
            XCTAssertEqual(back.z, colour.z, accuracy: 1e-5)
        }
    }

    func testBlendsArePerceptualNotRGB() {
        // Half way from black to white is perceptual mid-grey (L 0.5); an
        // sRGB blend would land noticeably lighter.
        let middle = Perceptual.mix(.init(l: 0, a: 0, b: 0), .white, 0.5)
        XCTAssertEqual(middle.l, 0.5, accuracy: 1e-12)
        XCTAssertGreaterThan(Perceptual.oklab(sRGB: SIMD3(0.5, 0.5, 0.5)).l - middle.l, 0.05)
        // A blue-to-yellow drift keeps an even lightness ramp instead of
        // dipping: every step changes lightness by the same amount.
        let blue = Perceptual.oklab(sRGB: SIMD3(0.1, 0.2, 0.9)), yellow = Perceptual.oklab(sRGB: SIMD3(0.95, 0.85, 0.1))
        let steps = (0...10).map { Perceptual.mix(blue, yellow, Double($0) / 10).l }
        for (a, b) in zip(steps, steps.dropFirst()) {
            XCTAssertEqual(b - a, (yellow.l - blue.l) / 10, accuracy: 1e-9)
        }
    }

    func testGamutClipKeepsLightnessAndHue() {
        let neon = OKLab(l: 0.86, chroma: 0.3, hue: 2.5)
        XCTAssertFalse(Perceptual.inGamut(neon))
        let fitted = Perceptual.clipped(neon)
        XCTAssertTrue(Perceptual.inGamut(fitted))
        XCTAssertEqual(fitted.l, neon.l, accuracy: 1e-9)
        XCTAssertEqual(fitted.hue, neon.hue, accuracy: 1e-6)
        XCTAssertLessThan(fitted.chroma, neon.chroma)
    }

    @MainActor
    func testEveryModeStaysReadableOnEveryBackdrop() {
        let custom = ["#0A1A5C", "#5C0A0A", "#F2C94C", "#1F8A3A"].compactMap(LyricColourOptions.oklab(hex:))
        for mode in LyricColourMode.allCases {
            for album in [vivid, pastel, dark] {
                for backdrop in [0.0, 0.04, 0.1, 0.18, 0.3, 0.45] {
                    let director = LyricInkDirector()
                    for frame in 0..<240 {
                        let audio = VCAudioLevels(rms: frame % 20 < 10 ? 0.4 : 0.05, bass: 0.3, mid: 0.2,
                                                  treble: frame % 30 < 15 ? 0.4 : 0, pulse: 0)
                        let ink = director.ink(.init(mode: mode, custom: custom, album: album, backdrop: backdrop,
                                                     audio: audio), wall: Double(frame) / 60)
                        guard frame % 8 == 0 || frame > 230 else { continue }
                        for slot in stride(from: 0.0, through: 1.0, by: 0.25) {
                            let sample = ink.sample(slot: slot, seed: UInt64(frame))
                            for colour in [sample.rest, sample.sung] {
                                let y = luminance(colour)
                                let ratio = Perceptual.contrast(y, ink.backdrop)
                                // Where 4.5:1 is out of reach, the text is as light as it can be.
                                XCTAssertTrue(ratio >= LyricInk.minimumContrast - 0.02 || y > 0.9,
                                    "\(mode) on \(backdrop): \(ratio)")
                            }
                        }
                    }
                }
            }
        }
    }

    @MainActor
    func testColourNeverFlashesEvenOnPulsingAudio() {
        for mode in [LyricColourMode.musicReactive, .albumDrift, .sungSweep] {
            let director = LyricInkDirector()
            var lightness: [Double] = [], luminances: [Double] = []
            for frame in 0..<600 {
                let wall = Double(frame) / 60
                // An 8 Hz full-scale pulse, and the album swapping five times a second.
                let on = frame % 8 < 4
                let audio = VCAudioLevels(rms: on ? 1 : 0, bass: on ? 1 : 0, mid: 0.5, treble: on ? 0 : 1, pulse: on ? 1 : 0)
                let ink = director.ink(.init(mode: mode, album: frame % 24 < 12 ? vivid : dark, backdrop: 0.05,
                                             audio: audio), wall: wall)
                let colour = rgb(mode == .sungSweep ? ink.base.sung : ink.sample(slot: 0.3).rest)
                lightness.append(Perceptual.oklab(sRGB: colour).l)
                luminances.append(Perceptual.luminance(sRGB: colour))
            }
            // Never faster than the lightness rate limit (plus quantisation).
            for (a, b) in zip(lightness, lightness.dropFirst()) {
                XCTAssertLessThanOrEqual(abs(b - a), LyricInkDirector.lightnessRate / 60 + 0.004, "\(mode)")
            }
            // WCAG general flash: a pair of opposing changes in relative
            // luminance of 10% or more. A single fade (white to a colour at
            // the start) is not a flash; two opposing swings are.
            var extremes: [(time: Double, value: Double)] = [(0, luminances[0])], rising: Bool?
            for (index, pair) in zip(luminances, luminances.dropFirst()).enumerated() where pair.1 != pair.0 {
                let up = pair.1 > pair.0
                if let rising, rising != up { extremes.append((Double(index) / 60, pair.0)) }
                rising = up
            }
            extremes.append((Double(luminances.count) / 60, luminances.last!))
            let swings = zip(extremes, extremes.dropFirst()).filter { abs($1.value - $0.value) >= 0.1 }
            let flashes = zip(swings, swings.dropFirst()).filter { $1.1.time - $0.0.time <= 1 }
            XCTAssertEqual(flashes.count, 0, "\(mode) produced a flash")
            XCTAssertLessThanOrEqual(swings.count, 1, "\(mode): only the opening fade may be large")
        }
    }

    @MainActor
    func testThirtyAndSixtyFramesPerSecondColoursAgree() {
        let thirty = LyricInkDirector(), sixty = LyricInkDirector()
        for frame in 0...600 {
            let wall = Double(frame) / 60
            let audio = VCAudioLevels(rms: 0.3 + 0.2 * sin(wall * 2), bass: 0.3 + 0.2 * sin(wall), mid: 0.2,
                                      treble: 0.2 + 0.2 * cos(wall * 1.3), pulse: 0)
            let mode: LyricColourMode = wall < 5 ? .albumDrift : .musicReactive
            let fast = sixty.ink(.init(mode: mode, album: vivid, backdrop: 0.06, audio: audio), wall: wall)
            guard frame % 2 == 0 else { continue }
            let slow = thirty.ink(.init(mode: mode, album: vivid, backdrop: 0.06, audio: audio), wall: wall)
            let a = slow.gradient(slot: 0.3), b = fast.gradient(slot: 0.3)
            XCTAssertEqual(a.l, b.l, accuracy: 0.012, "at \(wall)")
            XCTAssertEqual(a.a, b.a, accuracy: 0.012, "at \(wall)")
            XCTAssertEqual(a.b, b.b, accuracy: 0.012, "at \(wall)")
        }
    }

    @MainActor
    func testReduceMotionHoldsColourNearlyStill() {
        func change(_ mode: LyricColourMode, calm: Bool) -> Double {
            let director = LyricInkDirector()
            var start: OKLab?, end = OKLab.white
            for frame in 0...960 {
                let wall = Double(frame) / 60
                let audio = VCAudioLevels(rms: frame % 30 < 15 ? 0.6 : 0, bass: frame % 40 < 20 ? 0.6 : 0, mid: 0,
                                          treble: frame % 40 < 20 ? 0 : 0.6, pulse: 0)
                let ink = director.ink(.init(mode: mode, album: vivid, backdrop: 0.05, audio: audio,
                                             reduceMotion: calm), wall: wall)
                // Measured once the opening fade from white has settled.
                if frame == 360 { start = ink.gradient(slot: 0.5) }
                end = ink.gradient(slot: 0.5)
            }
            let first = start ?? .white
            return ((end.l - first.l) * (end.l - first.l) + (end.a - first.a) * (end.a - first.a)
                    + (end.b - first.b) * (end.b - first.b)).squareRoot()
        }
        XCTAssertLessThan(change(.albumDrift, calm: true), 0.03)
        XCTAssertGreaterThan(change(.albumDrift, calm: false), 0.03)
        XCTAssertLessThan(change(.musicReactive, calm: true), 0.01)
    }

    @MainActor
    func testSwitchingModeFadesInFromWhite() {
        let director = LyricInkDirector()
        for frame in 0..<30 {
            XCTAssertEqual(director.ink(.init(mode: .standard, album: vivid), wall: Double(frame) / 60).mode, .standard)
        }
        var previous = 1.0
        for frame in 30..<150 {
            let ink = director.ink(.init(mode: .sungSweep, album: vivid, backdrop: 0.05), wall: Double(frame) / 60)
            let colour = Perceptual.oklab(sRGB: rgb(ink.base.sung))
            if frame == 30 { XCTAssertGreaterThan(colour.l, 0.97, "starts at white") }
            XCTAssertLessThanOrEqual(abs(colour.l - previous), LyricInkDirector.lightnessRate / 60 + 0.004)
            previous = colour.l
        }
        XCTAssertGreaterThan(Perceptual.oklab(sRGB: rgb(director.ink(.init(mode: .sungSweep, album: vivid, backdrop: 0.05),
            wall: 3).base.sung)).chroma, 0.05, "arrives at the accent")
    }

    @MainActor
    func testColourSettingsPersistInIsolatedDefaults() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let isolated = UserDefaults(suiteName: name)!
        defer { isolated.removePersistentDomain(forName: name) }
        let options = LyricColourOptions(defaults: isolated)
        XCTAssertEqual(options.mode, .albumDrift)
        XCTAssertFalse(LyricColourMode.choices.contains(.standard))
        isolated.set("static", forKey: LyricColourOptions.Keys.mode)
        XCTAssertEqual(LyricColourOptions(defaults: isolated).mode, .albumDrift)
        XCTAssertEqual(StyleMemory.Style(composition: nil, colour: "static").colourMode, .albumDrift)
        options.mode = .customGradient
        XCTAssertEqual(options.strength, 1)
        options.strength = 1.6
        options.speed = 1.5
        options.setCustom(0, to: Color(.sRGB, red: 1, green: 0, blue: 0))
        options.addCustom()
        options.addCustom()
        options.addCustom()
        XCTAssertEqual(options.customHex.count, 4, "at most four colours")
        let reloaded = LyricColourOptions(defaults: isolated)
        XCTAssertEqual(reloaded.mode, .customGradient)
        XCTAssertEqual(reloaded.speed, 1.5)
        XCTAssertEqual(reloaded.strength, 1.6)
        XCTAssertEqual(reloaded.customHex.first, "#FF0000")
        XCTAssertEqual(reloaded.customColours.count, 4)
        reloaded.removeCustom(at: 0); reloaded.removeCustom(at: 0); reloaded.removeCustom(at: 0)
        XCTAssertEqual(reloaded.customHex.count, 2, "at least two colours")
        isolated.set(["nope", "#12345"], forKey: LyricColourOptions.Keys.custom)
        isolated.set(Double.nan, forKey: LyricColourOptions.Keys.speed)
        isolated.set(-1, forKey: LyricColourOptions.Keys.strength)
        let repaired = LyricColourOptions(defaults: isolated)
        XCTAssertEqual(repaired.customHex.count, 2)
        XCTAssertEqual(repaired.speed, 1)
        XCTAssertEqual(repaired.strength, 0)
    }

    @MainActor
    func testStrengthChangesEveryPastelModeInThePreview() {
        for mode in LyricColourMode.choices {
            let preview = LyricHighlightSwatchModel()
            var inputs = LyricInkDirector.Inputs(mode: mode, strength: 0,
                custom: LyricColourOptions.defaultCustom.compactMap(LyricColourOptions.oklab(hex:)), vocal: 0.6)
            func chroma(_ ink: LyricInk) -> Double {
                [0.2, 0.5, 0.8].map { Perceptual.oklab(sRGB: rgb(ink.sample(slot: $0).sung)).chroma }.max() ?? 0
            }
            let neutral = chroma(preview.ink(inputs, now: 0))
            inputs.strength = 0.45
            let soft = chroma(preview.ink(inputs, now: 0))
            inputs.strength = 1.6
            let vivid = chroma(preview.ink(inputs, now: 0))
            XCTAssertLessThan(neutral, 0.005, mode.title)
            XCTAssertGreaterThan(soft, neutral + 0.015, mode.title)
            XCTAssertGreaterThan(vivid, soft + 0.025, mode.title)
        }
    }

    @MainActor
    func testPastelModesKeepColourOverBrightWallpaperWithAutomaticBacking() throws {
        let backdrop = LyricBackdrop.luminance(mode: .desktop, motion: false, palette: [], scrim: 1,
                                               measured: 0.90, pastelText: true)
        let neutral = [SIMD3<Float>(repeating: 0.9), SIMD3<Float>(repeating: 0.2)]
        let custom = ["#F18C97", "#8E7DE4", "#50AFC8"].compactMap(LyricColourOptions.oklab(hex:))
        for mode in LyricColourMode.choices {
            let director = LyricInkDirector()
            var ink = LyricInk.white
            for frame in 0..<160 {
                ink = director.ink(.init(mode: mode, custom: custom, album: neutral, backdrop: backdrop, vocal: 0.6),
                                   wall: Double(frame) / 30)
            }
            let sample = ink.sample(slot: 0.5)
            for colour in [sample.rest, sample.sung] {
                XCTAssertGreaterThanOrEqual(Perceptual.contrast(luminance(colour), backdrop), 2.99, "\(mode)")
            }
            let rest = Perceptual.oklab(sRGB: rgb(sample.rest))
            let sung = Perceptual.oklab(sRGB: rgb(sample.sung))
            XCTAssertGreaterThan(sung.chroma, 0.06, "\(mode) washed out on a bright wallpaper")
            if mode == .sungSweep {
                XCTAssertLessThan(rest.chroma, 0.005)
                XCTAssertGreaterThan(rest.l, 0.99)
            } else {
                XCTAssertGreaterThan(rest.chroma, 0.06)
            }
        }
    }

    @MainActor
    func testSungSweepTravelsFromWhiteAcrossAWholeLine() throws {
        let line = LyricsFetcher.makeLine(text: "Hold the light", start: 1, end: 5)
        let coral = Color(.sRGB, red: 1, green: 0.26, blue: 0.34)
        let ink = LyricInk(mode: .sungSweep,
            base: LyricInkSample(rest: .white, sung: coral, glow: coral, reading: .white, sweeps: true))
        func pixels(at time: Double) throws -> (white: Int, coloured: Int) {
            let stage = CGSize(width: 600, height: 220)
            let progress = LyricPhraseTiming.sweep(of: line, at: time)
            let renderer = ImageRenderer(content: LyricLineView(words: line.words, time: time, stageSize: stage,
                baseSize: 72, wordTimed: false, phraseSweep: progress)
                .environment(\.lyricInk, ink)
                .frame(width: stage.width, height: stage.height)
                .background(.black))
            let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
            var white = 0, coloured = 0
            for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) {
                for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
                    guard let pixel = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB), pixel.redComponent > 0.65 else {
                        continue
                    }
                    if pixel.greenComponent > 0.65, pixel.blueComponent > 0.65 { white += 1 }
                    if pixel.greenComponent < 0.45, pixel.blueComponent < 0.5 { coloured += 1 }
                }
            }
            return (white, coloured)
        }
        let start = try pixels(at: 1), middle = try pixels(at: 3), end = try pixels(at: 5)
        XCTAssertGreaterThan(start.white, 150)
        XCTAssertEqual(start.coloured, 0)
        XCTAssertGreaterThan(middle.white, 50)
        XCTAssertGreaterThan(middle.coloured, 50)
        XCTAssertEqual(end.white, 0)
        XCTAssertGreaterThan(end.coloured, middle.coloured)
    }

    @MainActor
    func testWhiteCoverUsesAnAccentAndWashedOutSweepUsesAnUnderline() throws {
        func sweep(on backdrop: Double, strength: Double = 1) -> LyricInkSample {
            let director = LyricInkDirector()
            let inputs = LyricInkDirector.Inputs(mode: .sungSweep, strength: strength,
                album: [SIMD3<Float>(repeating: 1)], backdrop: backdrop, increaseContrast: true)
            var ink = LyricInk.white
            for step in 0...30 {
                ink = director.ink(inputs, wall: Double(step) * 0.25)
            }
            return ink.base
        }

        let accent = sweep(on: 0.03)
        XCTAssertGreaterThan(Perceptual.oklab(sRGB: rgb(accent.sung)).chroma, 0.08)
        XCTAssertNil(accent.sweepUnderline)
        let washedOut = sweep(on: 0.62)
        XCTAssertGreaterThan(Perceptual.oklab(sRGB: rgb(washedOut.sung)).l, 0.98)
        let marker = try XCTUnwrap(washedOut.sweepUnderline)
        let rendered = try XCTUnwrap(NSColor(marker).usingColorSpace(.sRGB))
        XCTAssertGreaterThan(rendered.alphaComponent, 0.8)
        XCTAssertGreaterThan(Perceptual.contrast(luminance(marker), 0.62), 3)
        XCTAssertNil(sweep(on: 0.62, strength: 0).sweepUnderline)
    }

    @MainActor
    func testAlbumDriftStillMovesThroughShadesOfASingleCoverHue() {
        let director = LyricInkDirector()
        let album = [SIMD3<Float>(0.13, 0.26, 0.8)]
        let inputs = LyricInkDirector.Inputs(mode: .albumDrift, album: album)
        let samples = (0...240).compactMap { step -> OKLab? in
            let ink = director.ink(inputs, wall: Double(step) * 0.25)
            return step >= 24 ? ink.gradient(slot: 0.5) : nil
        }
        let lightness = samples.map(\.l)
        XCTAssertGreaterThan((lightness.max() ?? 0) - (lightness.min() ?? 0), 0.08)
        let hue = Perceptual.oklab(sRGB: SIMD3<Double>(album[0])).hue
        XCTAssertTrue(samples.allSatisfy { abs($0.hue - hue) < 0.03 })
    }

    func testBackdropEstimateFollowsTheWallpaperAndScrim() {
        // The wallpaper and its scrim, before the text backing.
        let light = LyricBackdrop.beforeBacking(mode: .pattern, motion: false, palette: pastel, scrim: 0)
        let darkened = LyricBackdrop.beforeBacking(mode: .pattern, motion: false, palette: pastel, scrim: 2)
        XCTAssertLessThan(darkened, light)
        XCTAssertGreaterThan(light, LyricBackdrop.beforeBacking(mode: .pattern, motion: false, palette: dark, scrim: 0))
        // With the backing turned off by hand, that is what the lyrics sit on;
        // the automatic backing only ever darkens it.
        XCTAssertEqual(LyricBackdrop.luminance(mode: .pattern, motion: false, palette: pastel, scrim: 0, backing: 0),
                       light, accuracy: 1e-12)
        XCTAssertLessThanOrEqual(LyricBackdrop.luminance(mode: .pattern, motion: false, palette: pastel, scrim: 0), light)
    }

    func testColoursReachTheFloorAtTheEndsOfTheStageToo() {
        // The wallpaper shades an ellipse 1.3 by 1.4 times the stage: the
        // full backing in the middle, 0.9 of it at 55% of its radius, 0.45
        // at 80%, none at its rim. A line reaching the side of the stage,
        // at 0.5 / 0.65 of that radius, sits on about half the backing.
        let side = 0.5 / 0.65
        let share = 0.9 + (0.45 - 0.9) * (side - 0.55) / (0.8 - 0.55)
        XCTAssertLessThanOrEqual(LyricBackdrop.edgeBacking, share)
        let cases: [(VersoCustom.Settings.BackgroundMode, [SIMD3<Float>], Double?)] = [
            (.desktop, dark, nil), (.desktop, dark, 0.2), (.custom, pastel, nil), (.custom, vivid, 0.25),
            (.pattern, pastel, nil), (.pattern, vivid, nil), (.visualizer, pastel, nil),
        ]
        for (mode, palette, measured) in cases {
            let before = LyricBackdrop.beforeBacking(mode: mode, motion: false, palette: palette, scrim: 1,
                                                     measured: measured, tinted: true)
            let backing = LyricLegibility.backing(forLuminance: before, existingDarkening: 0, target: 4.5)
            let middle = LyricLegibility.darkened(before, by: backing)
            let ends = LyricLegibility.darkened(before, by: backing * share)
            let estimate = LyricBackdrop.luminance(mode: mode, motion: false, palette: palette, scrim: 1,
                                                   measured: measured, tinted: true)
            guard !LyricBackdrop.forcesWhite(estimate) else { continue }
            // A colour lifted just to the floor against the estimate...
            for hue in stride(from: 0.0, to: 6.28, by: 0.5) {
                let colour = Perceptual.readable(OKLab(l: 0.6, chroma: 0.15, hue: hue), background: estimate)
                let lum = Perceptual.luminance(colour)
                // ...holds it at the ends of the stage as well as in the middle.
                XCTAssertGreaterThanOrEqual(Perceptual.contrast(lum, ends), LyricInk.minimumContrast - 0.01,
                                            "\(mode) \(measured ?? -1) hue \(hue)")
                XCTAssertGreaterThanOrEqual(Perceptual.contrast(lum, middle), LyricInk.minimumContrast - 0.01)
            }
        }
    }

    func testAnImportedPictureCountsTheAlbumTintLaidOverIt() {
        // The wallpaper lays the cover's colours over an imported picture at
        // 18%: a dark photo under a pale cover is not as dark as the photo.
        let bare = LyricBackdrop.beforeBacking(mode: .custom, motion: false, palette: pastel, scrim: 1, measured: 0.03)
        let tinted = LyricBackdrop.beforeBacking(mode: .custom, motion: false, palette: pastel, scrim: 1,
                                                 measured: 0.03, tinted: true)
        XCTAssertGreaterThan(tinted, bare * 2)
        // The tint as the colour field is taken elsewhere: mostly the
        // cover's average, with a margin for its brightest colour.
        let lums = pastel.map { Perceptual.luminance(sRGB: SIMD3(Double($0.x), Double($0.y), Double($0.z))) }
        let field = 0.35 * (lums.max() ?? 0) + 0.65 * lums.reduce(0, +) / Double(lums.count)
        let modelled = LyricLegibility.darkened(LyricLegibility.linear(0.82 * LyricLegibility.encoded(0.03)
            + 0.18 * LyricLegibility.encoded(field)), by: 0.12)
        XCTAssertEqual(tinted, modelled, accuracy: 1e-9)
        // A dark cover over a dark photo changes little; with no cover there is no tint.
        let darkTint = LyricBackdrop.beforeBacking(mode: .custom, motion: false, palette: dark, scrim: 1,
                                                   measured: 0.03, tinted: true)
        XCTAssertEqual(darkTint, bare, accuracy: 0.01)
        XCTAssertEqual(LyricBackdrop.beforeBacking(mode: .custom, motion: false, palette: [], scrim: 1, measured: 0.03,
                                                   tinted: true), bare, accuracy: 1e-12)
        // Your wallpaper has nothing laid over it.
        XCTAssertEqual(LyricBackdrop.beforeBacking(mode: .desktop, motion: false, palette: pastel, scrim: 1,
                                                   measured: 0.03, tinted: true), 0.03, accuracy: 1e-12)
    }

    @MainActor
    func testStaticWhiteHighlightsAreMadeReadableToo() throws {
        // A pale yellow cover accent: Cascade's held words used it raw, and
        // Cutout's labels set white type on it at 75%.
        let pale = Perceptual.oklab(sRGB: SIMD3(0.96, 0.92, 0.55))
        for backdrop in [0.04, 0.18, 0.3] {
            let director = LyricInkDirector()
            let ink = director.ink(.init(mode: .standard, backdrop: backdrop, accent: pale), wall: 0)
            XCTAssertEqual(ink.mode, .standard)
            let held = luminance(try XCTUnwrap(ink.accent))
            XCTAssertTrue(Perceptual.contrast(held, backdrop) >= LyricInk.minimumContrast - 0.02 || held > 0.9,
                          "held words on \(backdrop)")
            let tile = luminance(try XCTUnwrap(ink.tile))
            XCTAssertGreaterThanOrEqual(Perceptual.contrast(luminance(ink.onTile), tile), LyricInk.minimumContrast - 0.02)
            // Asked again with nothing changed, the same ink comes back.
            let again = director.ink(.init(mode: .standard, backdrop: backdrop, accent: pale), wall: 1)
            XCTAssertEqual(luminance(try XCTUnwrap(again.tile)), tile, accuracy: 1e-9)
        }
        // The type is still plain white without an accent or a cover; the
        // highlight has a legible colour of its own (vermilion).
        let bare = LyricInkDirector().ink(.init(mode: .standard), wall: 0)
        XCTAssertEqual(bare.mode, .standard)
        XCTAssertEqual(bare.base, LyricInkSample.white)
        let tile = luminance(try XCTUnwrap(bare.tile))
        XCTAssertGreaterThanOrEqual(Perceptual.contrast(luminance(bare.onTile), tile), LyricHighlight.textContrast - 0.02)
    }

    @MainActor
    func testColourModesShowOverYourWallpaperAndImportedPictures() throws {
        // Your wallpaper before its picture is measured: a mid tone, which
        // the backing shades for white type, half as much at the ends of the
        // stage. The old fixed 0.35 was out of every colour's reach at 3:1.
        let unknown = LyricBackdrop.luminance(mode: .desktop, motion: false, palette: dark, scrim: 1)
        XCTAssertLessThan(unknown, LyricLegibility.unknownBackdropLuminance)
        XCTAssertFalse(LyricBackdrop.forcesWhite(unknown))
        XCTAssertTrue(LyricBackdrop.forcesWhite(0.35))
        // Measured pictures are used as measured: a dark desktop needs no
        // backing, and an imported photo is not judged by the album palette.
        XCTAssertEqual(LyricBackdrop.luminance(mode: .desktop, motion: false, palette: pastel, scrim: 1, measured: 0.03),
                       0.03, accuracy: 1e-12)
        let photo = LyricBackdrop.luminance(mode: .custom, motion: false, palette: pastel, scrim: 1, measured: 0.03)
        XCTAssertLessThan(photo, 0.03)
        XCTAssertEqual(LyricBackdrop.luminance(mode: .desktop, motion: false, palette: dark, scrim: 1, measured: 0.5,
                                               backing: 0), 0.5, accuracy: 1e-12)
        // A bright picture is shaded by the backing, up to its cap.
        let bright = LyricBackdrop.luminance(mode: .desktop, motion: false, palette: dark, scrim: 1, measured: 0.9)
        XCTAssertLessThan(bright, 0.9)
        // Album drift from a red cover over that dark photo stays red rather
        // than washing out to a pale pink.
        let red: [SIMD3<Float>] = [SIMD3(0.78, 0.08, 0.10), SIMD3(0.55, 0.04, 0.06), SIMD3(0.2, 0.02, 0.03),
                                   SIMD3(0.85, 0.2, 0.15), SIMD3(0.4, 0.05, 0.05)]
        func chroma(over backdrop: Double) -> Double {
            let director = LyricInkDirector()
            var ink = LyricInk.white
            for frame in 0..<600 {
                ink = director.ink(.init(mode: .albumDrift, album: red, backdrop: backdrop), wall: Double(frame) / 60)
            }
            return Perceptual.readable(ink.gradient(slot: 0.5), background: ink.backdrop, ratio: ink.contrast).chroma
        }
        // About what an imported picture was taken to be before, whatever it showed.
        let previousEstimate = 0.24
        XCTAssertGreaterThan(chroma(over: photo), 0.12)
        XCTAssertGreaterThan(chroma(over: photo), chroma(over: previousEstimate) * 1.5)
    }

    @MainActor
    func testInkReachesEveryCompositionAndTheInterludeDots() throws {
        let director = LyricInkDirector()
        let green = [LyricColourOptions.oklab(hex: "#2BD96B")!]
        var ink = LyricInk.white
        for frame in 0..<200 {
            // Highlights in the same green, so Cutout's labels count too.
            ink = director.ink(.init(mode: .customGradient, custom: green, album: vivid, backdrop: 0.05,
                                     highlight: .custom, highlightCustom: green.first), wall: Double(frame) / 60)
        }
        let words: [LyricsFetcher.Word] = [.init(text: "Green", start: 1, end: 1.4), .init(text: "light", start: 1.4, end: 2.6),
                                           .init(text: "tonight", start: 2.6, end: 3.1)]
        for style in LyricComposition.allCases {
            let view = LyricLineView(words: words, time: 2.0, stageSize: CGSize(width: 700, height: 360), baseSize: 44,
                                     composition: style, seed: 1)
                .environment(\.lyricInk, ink)
                .frame(width: 760, height: 420)
                .background(.black)
            XCTAssertGreaterThan(try greenPixels(view), 30, style.title)
        }
        let dots = InstrumentalInterludeView(cue: InstrumentalCue(elapsed: 3, opacity: 1, progress: 0.5),
                                             accent: .red, baseSize: 60)
            .environment(\.lyricInk, ink).padding(30).background(.black)
        XCTAssertGreaterThan(try greenPixels(dots), 30, "interlude dots")
    }

    @MainActor
    private func greenPixels(_ content: some View) throws -> Int {
        let renderer = ImageRenderer(content: content)
        renderer.scale = 1
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        var count = 0
        for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) {
            for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
                guard let pixel = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) else { continue }
                // Green-dominant at any brightness: Cutout's dark label tiles count too.
                if pixel.greenComponent > 0.25, pixel.greenComponent > pixel.redComponent + 0.12,
                   pixel.greenComponent > pixel.blueComponent + 0.08 { count += 1 }
            }
        }
        return count
    }
}
