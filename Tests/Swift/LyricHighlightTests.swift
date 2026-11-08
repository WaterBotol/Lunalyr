import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// The highlight colour (Cutout's labels, "Cutout · Highlight" tiles and
/// Cascade's held words) and the "Cutout · Highlight" word transition.
final class LyricHighlightTests: XCTestCase {
    // MARK: Covers

    private static func srgb(_ hex: String) -> SIMD3<Float> {
        let value = UInt32(hex.dropFirst(), radix: 16)!
        return SIMD3(Float((value >> 16) & 0xFF), Float((value >> 8) & 0xFF), Float(value & 0xFF)) / 255
    }

    /// Covers as the wallpaper passes them to the stage: five sRGB stops.
    private static let covers: [(name: String, stops: [SIMD3<Float>])] = [
        ("olive and khaki", ["#5C4A2A", "#9E9457", "#75663A", "#383019", "#8F7A4D"].map(srgb)),
        ("brown leather", ["#3B2414", "#6E4426", "#8B5A2B", "#A0522D", "#2A1A10"].map(srgb)),
        ("mustard", ["#B8860B", "#C9A227", "#8B7500", "#DAA520", "#6B5A12"].map(srgb)),
        ("monochrome blue", ["#1A2E6B", "#4D7AD1", "#3861B8", "#0F1A40", "#203A80"].map(srgb)),
        ("saturated", ["#E61F33", "#FAC714", "#D933BF", "#FA730F", "#262629"].map(srgb)),
        ("pastel", ["#FAD9E0", "#CCF2E5", "#E6DBFA", "#F5E0F0", "#FCF2D9"].map(srgb)),
        ("black and white", ["#0D0D0D", "#808080", "#1F1F1F", "#4D4D4D", "#F2F2F2"].map(srgb)),
        ("all grey", ["#777777", "#777777", "#777777", "#777777", "#777777"].map(srgb)),
        ("forest", ["#1E3B1E", "#2E5A2E", "#556B2F", "#3C5A14", "#6B8E23"].map(srgb)),
    ]

    /// Many more covers, deterministic: hues all round the wheel at every
    /// saturation and brightness, olive and brown among them.
    private static func randomCovers(count: Int) -> [[SIMD3<Float>]] {
        var state: UInt64 = 0x9E37_79B9_7F4A_7C15
        func next() -> Float {
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return Float(state >> 40) / Float(1 << 24)
        }
        return (0..<count).map { _ in
            let hue = next(), spread = next() * 0.3, saturation = next(), brightness = 0.15 + next() * 0.85
            return (0..<5).map { index in
                let colour = NSColor(hue: CGFloat((hue + spread * Float(index) / 4).truncatingRemainder(dividingBy: 1)),
                                     saturation: CGFloat(min(1, saturation * (0.6 + next() * 0.5))),
                                     brightness: CGFloat(min(1, brightness * (0.5 + next() * 0.7))), alpha: 1)
                return SIMD3(Float(colour.redComponent), Float(colour.greenComponent), Float(colour.blueComponent))
            }
        }
    }

    private static func oklab(_ stops: [SIMD3<Float>]) -> [OKLab] {
        stops.map { Perceptual.oklab(sRGB: SIMD3(Double($0.x), Double($0.y), Double($0.z))) }
    }

    /// The backdrops the stage is given: a black visualiser to a light
    /// picture only part shaded.
    private static let backdrops = [0.005, 0.03, 0.08, 0.155, 0.26, 0.36, 0.55]

    // MARK: What a tile must be

    /// Colours people call muddy, worked out apart from `LyricHighlight`.
    private static let muddyNames = ["#808000", "#6B8E23", "#556B2F", "#BDB76B", "#C3B091", "#8B4513", "#A0522D",
                                     "#B8860B", "#8B7500", "#7B6B3A", "#9E9457", "#6E4426", "#967117", "#A89A6B"]
    private static let muddy = muddyNames
        .map { Perceptual.oklab(sRGB: SIMD3(Double(srgb($0).x), Double(srgb($0).y), Double(srgb($0).z))) }

    private static func describe(_ colour: OKLab) -> String {
        let rgb = Perceptual.sRGB(colour)
        return String(format: "#%02X%02X%02X (L %.2f C %.3f h %.0f)", Int((rgb.x * 255).rounded()), Int((rgb.y * 255).rounded()),
                      Int((rgb.z * 255).rounded()), colour.l, colour.chroma, LyricHighlight.degrees(colour.hue))
    }

    private func assertClean(_ tile: OKLab, backdrop: Double, ratio: Double = LyricHighlight.textContrast,
                             _ context: @autoclosure () -> String, file: StaticString = #filePath, line: UInt = #line) {
        let hue = LyricHighlight.degrees(tile.hue)
        // Not greyish, and no yellow-green, gold or orange darker than a
        // clear yellow or orange: olive, khaki, mustard and brown.
        XCTAssertGreaterThanOrEqual(tile.chroma, 0.06, "greyish tile \(context())", file: file, line: line)
        if (70...125).contains(hue) {
            XCTAssertGreaterThanOrEqual(tile.l, 0.74, "olive or mustard tile \(context())", file: file, line: line)
        }
        if (125...140).contains(hue) {
            XCTAssertGreaterThanOrEqual(tile.l, 0.68, "olive-green tile \(context())", file: file, line: line)
        }
        if (30..<70).contains(hue) {
            XCTAssertGreaterThanOrEqual(tile.l, 0.6, "brown or rust tile \(context())", file: file, line: line)
        }
        if (95..<165).contains(hue), tile.l < 0.6 {
            XCTAssertGreaterThanOrEqual(tile.chroma, 0.14, "olive-green tile \(context())", file: file, line: line)
        }
        for (index, colour) in Self.muddy.enumerated() {
            let distance = ((tile.l - colour.l) * (tile.l - colour.l) + (tile.a - colour.a) * (tile.a - colour.a)
                            + (tile.b - colour.b) * (tile.b - colour.b)).squareRoot()
            XCTAssertGreaterThan(distance, 0.05, "tile \(Self.describe(tile)) close to muddy \(Self.muddyNames[index]) "
                                 + "\(context())", file: file, line: line)
        }
        assertReadable(tile, backdrop: backdrop, ratio: ratio, context(), file: file, line: line)
    }

    private func assertReadable(_ tile: OKLab, backdrop: Double, ratio: Double = LyricHighlight.textContrast,
                                _ context: @autoclosure () -> String, file: StaticString = #filePath, line: UInt = #line) {
        let text = LyricHighlight.text(on: tile, ratio: ratio)
        let contrast = Perceptual.contrast(Perceptual.luminance(text), Perceptual.luminance(tile))
        XCTAssertGreaterThanOrEqual(contrast, ratio - 0.01, "text on tile \(context())", file: file, line: line)
        // Readings on a tile are the text at 85%, over the tile.
        let reading = LyricHighlight.blend(text, over: tile, opacity: LyricHighlight.readingOpacity)
        XCTAssertGreaterThanOrEqual(Perceptual.contrast(Perceptual.luminance(reading), Perceptual.luminance(tile)),
                                    3, "reading on tile \(context())", file: file, line: line)
        XCTAssertGreaterThanOrEqual(LyricHighlight.clearance(tile.l, backdrop: backdrop), LyricHighlight.minimumSeparation,
                                    "tile lost in the backdrop \(context())", file: file, line: line)
    }

    func testAlbumTilesAreVividCleanAndReadableOnEveryCover() {
        let covers = Self.covers.map(\.stops) + Self.randomCovers(count: 160)
        for (index, cover) in covers.enumerated() {
            let colours = Self.oklab(cover)
            for backdrop in Self.backdrops {
                for ratio in [LyricHighlight.textContrast, LyricHighlight.increasedTextContrast] {
                    let tile = LyricHighlight.album(colours, backdrop: backdrop, ratio: ratio)
                    assertClean(tile, backdrop: backdrop, ratio: ratio, "cover \(index) over \(backdrop)")
                    // Lighter than any backdrop up to the brightest it can be
                    // where the lyrics sit, by a clear step.
                    if backdrop <= 0.36 {
                        XCTAssertGreaterThanOrEqual(LyricHighlight.clearance(tile.l, backdrop: backdrop), 0.1,
                                                    "cover \(index) over \(backdrop)")
                    }
                }
            }
        }
    }

    func testAMuddyCoverTurnsToACleanHueOfItsOwn() {
        for name in ["olive and khaki", "brown leather", "mustard", "forest"] {
            let cover = Self.oklab(Self.covers.first { $0.name == name }!.stops)
            for backdrop in Self.backdrops {
                let tile = LyricHighlight.album(cover, backdrop: backdrop)
                // Still a warm or earthy hue of the cover's family -- turned,
                // not swapped for an unrelated colour -- and lively.
                let nearest = cover.filter { $0.chroma >= 0.04 }
                    .map { LyricHighlight.degrees(LyricHighlight.hueDistance($0.hue, tile.hue)) }.min() ?? 0
                XCTAssertLessThanOrEqual(nearest, 75, "\(name) over \(backdrop)")
                XCTAssertGreaterThanOrEqual(tile.chroma, 0.1, "\(name) over \(backdrop)")
            }
        }
        // A cover with no colour at all takes vermilion.
        let grey = LyricHighlight.album(Self.oklab(Self.covers.first { $0.name == "all grey" }!.stops), backdrop: 0.05)
        XCTAssertLessThan(LyricHighlight.degrees(LyricHighlight.hueDistance(grey.hue, LyricHighlight.monochromeHue)), 25)
        XCTAssertGreaterThan(grey.chroma, 0.12)
    }

    func testTilesAreDeterministicPerCover() {
        for cover in Self.covers.map(\.stops) {
            let colours = Self.oklab(cover)
            XCTAssertEqual(LyricHighlight.album(colours, backdrop: 0.1), LyricHighlight.album(colours, backdrop: 0.1))
        }
    }

    // MARK: Complement lyrics

    func testComplementAvoidsAnAlbumBackgroundThatMatchesTheOppositeHue() throws {
        let background = 2 * Double.pi / 3
        let lyric = OKLab(l: 0.84, chroma: 0.15, hue: background + .pi)
        let tile = try XCTUnwrap(LyricHighlight.complement(of: lyric, backgroundHue: background, backdrop: 0.13))
        XCTAssertGreaterThan(LyricHighlight.hueDistance(tile.hue, background), .pi / 6)
        XCTAssertGreaterThanOrEqual(LyricHighlight.hueDistance(tile.hue, lyric.hue), .pi * 0.74)
        assertReadable(tile, backdrop: 0.13, "tile beside the album and lyric colours")
    }

    func testComplementOpposesTheLyricColourAndStaysClean() {
        for hue in stride(from: 0.0, to: 360, by: 7.5) {
            for (l, c) in [(0.78, 0.08), (0.85, 0.14), (0.7, 0.18)] {
                let lyric = OKLab(l: l, chroma: c, hue: hue * .pi / 180)
                for backdrop in Self.backdrops {
                    guard let tile = LyricHighlight.complement(of: lyric, backdrop: backdrop) else {
                        return XCTFail("no complement for hue \(hue)")
                    }
                    // Complementary, or split-complementary: 135-180 degrees round.
                    XCTAssertGreaterThanOrEqual(LyricHighlight.degrees(LyricHighlight.hueDistance(tile.hue, lyric.hue)),
                                                134.9, "hue \(hue) over \(backdrop)")
                    assertClean(tile, backdrop: backdrop, "complement of hue \(hue) over \(backdrop)")
                }
            }
        }
        // White and grey have no hue to oppose.
        XCTAssertNil(LyricHighlight.complement(of: .white, backdrop: 0.05))
        XCTAssertNil(LyricHighlight.complement(of: OKLab(l: 0.7, a: 0.01, b: -0.01), backdrop: 0.05))
    }

    @MainActor
    func testComplementFollowsTheLyricColourAsItDrifts() {
        // The default custom gradient drifting at speed 2, as the user has it.
        let custom = LyricColourOptions.defaultCustom.compactMap(LyricColourOptions.oklab(hex:))
        let director = LyricInkDirector()
        var hues: [Double] = [], misses = 0, frames = 0
        for frame in 0..<(60 * 30) {
            let ink = director.ink(.init(mode: .customGradient, speed: 2, custom: custom, album: [], backdrop: 0.1),
                                   wall: Double(frame) / 30)
            guard frame >= 90, let tone = ink.highlight else { continue }
            frames += 1
            let lyric = ink.gradient(slot: 0.5)
            // The tile eases behind the drifting colour; it stays opposite it.
            if LyricHighlight.degrees(LyricHighlight.hueDistance(tone.tile.hue, lyric.hue)) < 120 { misses += 1 }
            hues.append(LyricHighlight.degrees(tone.tile.hue))
            assertReadable(tone.tile, backdrop: ink.backdrop, "frame \(frame)")
        }
        XCTAssertLessThan(Double(misses) / Double(frames), 0.03, "the tile kept opposite the lyric colour")
        // It went round with the colour, not stayed put.
        let buckets = Set(hues.map { Int($0 / 30) })
        XCTAssertGreaterThanOrEqual(buckets.count, 6, "tile hues \(buckets.sorted())")
        // Between two stops of opposite hue the lyric colour passes through
        // grey; the basis turns round the wheel instead, never losing its hue.
        let ink = LyricInk(mode: .customGradient, stops: (OKLab(l: 0.85, chroma: 0.1, hue: 1.3),
            OKLab(l: 0.85, chroma: 0.1, hue: 1.3 + .pi), .white, .white), stopCount: 2)
        for step in 0...20 {
            var drifting = ink
            drifting.phase = Double(step) / 20
            XCTAssertGreaterThan(drifting.gradient(slot: 0.5).chroma, 0.09)
        }
    }

    @MainActor
    func testWhiteLyricsTakeTheAlbumAccent() throws {
        let cover = Self.covers.first { $0.name == "saturated" }!.stops
        let director = LyricInkDirector()
        let white = director.ink(.init(mode: .standard, album: cover, backdrop: 0.05), wall: 0)
        let album = LyricInkDirector().ink(.init(mode: .standard, album: cover, backdrop: 0.05, highlight: .album), wall: 0)
        XCTAssertEqual(white.highlight, album.highlight)
        // Worked out for the backdrop as the director steps it (1/256).
        XCTAssertEqual(director.goalTile, LyricHighlight.album(Self.oklab(cover), backdrop: (0.05 * 256).rounded() / 256))
        let drawn = try XCTUnwrap(white.highlight?.tile)
        XCTAssertEqual(drawn.l, director.goalTile.l, accuracy: 1.0 / 512)
        XCTAssertEqual(drawn.a, director.goalTile.a, accuracy: 1.0 / 512)
        XCTAssertEqual(drawn.b, director.goalTile.b, accuracy: 1.0 / 512)
    }

    // MARK: Each mode

    @MainActor
    func testEachModeTakesItsOwnColour() throws {
        let cover = Self.covers.first { $0.name == "monochrome blue" }!.stops
        let teal = OKLab(l: 0.75, chroma: 0.12, hue: 190 * .pi / 180)
        let coral = try XCTUnwrap(LyricHighlightOptions.oklab(hex: LyricHighlightOptions.defaultCustom))
        func tile(_ mode: LyricHighlightMode, lyric: OKLab) -> OKLab? {
            let director = LyricInkDirector()
            var ink = LyricInk.white
            for frame in 0..<120 {
                ink = director.ink(.init(mode: .customGradient, custom: [lyric], album: cover, backdrop: 0.05,
                                         highlight: mode, highlightCustom: coral), wall: Double(frame) / 30)
            }
            return ink.highlight?.tile
        }
        let pink = OKLab(l: 0.8, chroma: 0.12, hue: 0)
        // Album accent: the cover's own colour, whatever the lyrics.
        XCTAssertEqual(try XCTUnwrap(tile(.album, lyric: teal)).hue, try XCTUnwrap(tile(.album, lyric: pink)).hue, accuracy: 0.02)
        // Custom: the user's own hue.
        XCTAssertLessThan(LyricHighlight.hueDistance(try XCTUnwrap(tile(.custom, lyric: teal)).hue, coral.hue), 0.03)
        // Complement: opposite the lyrics, so it changes with them.
        let opposite = try XCTUnwrap(tile(.complement, lyric: teal))
        XCTAssertGreaterThan(LyricHighlight.degrees(LyricHighlight.hueDistance(opposite.hue, teal.hue)), 134)
        XCTAssertGreaterThan(LyricHighlight.degrees(LyricHighlight.hueDistance(opposite.hue,
            try XCTUnwrap(tile(.complement, lyric: pink)).hue)), 90)
    }

    @MainActor
    func testCustomColoursKeepTheirHueAndStayReadable() {
        for hex in ["#FF6F61", "#FFFF00", "#000080", "#FFFFFF", "#000000", "#808080", "#808000", "#00FFAA", "#6A0DAD"] {
            let colour = LyricHighlightOptions.oklab(hex: hex)!
            for backdrop in Self.backdrops {
                let tile = LyricHighlight.custom(colour, backdrop: backdrop)
                assertReadable(tile, backdrop: backdrop, "\(hex) over \(backdrop)")
                if colour.chroma > 0.05 {
                    XCTAssertLessThan(LyricHighlight.degrees(LyricHighlight.hueDistance(tile.hue, colour.hue)), 2,
                                      "\(hex) over \(backdrop)")
                }
            }
        }
    }

    @MainActor
    func testHighlightSettingsPersistInIsolatedDefaults() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let isolated = UserDefaults(suiteName: name)!
        defer { isolated.removePersistentDomain(forName: name) }
        let options = LyricHighlightOptions(defaults: isolated)
        XCTAssertEqual(options.mode, .complement, "complements the lyrics until chosen otherwise")
        XCTAssertEqual(options.customHex, LyricHighlightOptions.defaultCustom)
        for mode in LyricHighlightMode.allCases {
            options.mode = mode
            XCTAssertEqual(LyricHighlightOptions(defaults: isolated).mode, mode)
        }
        options.setCustom(Color(.sRGB, red: 0, green: 0.5, blue: 1))
        let reloaded = LyricHighlightOptions(defaults: isolated)
        XCTAssertEqual(reloaded.customHex, "#0080FF")
        XCTAssertEqual(reloaded.customColour, LyricHighlightOptions.oklab(hex: "#0080FF"))
        XCTAssertTrue(isolated.dictionaryRepresentation().keys.contains(LyricHighlightOptions.Keys.mode))
        XCTAssertTrue(LyricHighlightOptions.Keys.mode.hasPrefix("custom.lyricHighlight"))
        XCTAssertTrue(LyricHighlightOptions.Keys.custom.hasPrefix("custom.lyricHighlight"))
        isolated.set("sepia", forKey: LyricHighlightOptions.Keys.mode)
        isolated.set("#12", forKey: LyricHighlightOptions.Keys.custom)
        let repaired = LyricHighlightOptions(defaults: isolated)
        XCTAssertEqual(repaired.mode, .complement)
        XCTAssertEqual(repaired.customHex, LyricHighlightOptions.defaultCustom)
    }

    @MainActor
    func testTheStageInkCarriesTheHighlightInEveryTextMode() {
        let custom = LyricColourOptions.defaultCustom.compactMap(LyricColourOptions.oklab(hex:))
        let covers = Self.covers.filter { ["olive and khaki", "brown leather", "monochrome blue", "saturated",
                                           "black and white"].contains($0.name) }.map(\.stops)
        for mode in LyricColourMode.allCases {
            for cover in covers {
                for backdrop in [0.02, 0.3] {
                    for increased in [false, true] {
                        let director = LyricInkDirector()
                        let ratio = increased ? LyricHighlight.increasedTextContrast : LyricHighlight.textContrast
                        for frame in 0..<45 {
                            let audio = VCAudioLevels(rms: frame % 20 < 10 ? 0.4 : 0.05, bass: 0.3, mid: 0.2,
                                                      treble: frame % 30 < 15 ? 0.4 : 0, pulse: 0)
                            let ink = director.ink(.init(mode: mode, custom: custom, album: cover, backdrop: backdrop,
                                                         audio: audio, increaseContrast: increased), wall: Double(frame) / 20)
                            guard let tone = ink.highlight else { return XCTFail("\(mode): no highlight") }
                            // Where the tile is heading is always clean and readable.
                            assertClean(director.goalTile, backdrop: ink.backdrop, ratio: ratio, "\(mode) over \(backdrop)")
                            // The tile drawn eases there; whatever it is on the
                            // way, the text on it reaches AA.
                            XCTAssertGreaterThanOrEqual(Perceptual.contrast(Perceptual.luminance(tone.text),
                                Perceptual.luminance(tone.tile)), LyricHighlight.textContrast - 0.01, "\(mode) frame \(frame)")
                            if ink.settled {
                                XCTAssertGreaterThanOrEqual(Perceptual.contrast(Perceptual.luminance(tone.text),
                                    Perceptual.luminance(tone.tile)), ratio - 0.01, "\(mode) frame \(frame)")
                            }
                            // Held words are type on the wallpaper: readable as the lyrics are.
                            let held = Perceptual.luminance(tone.held)
                            XCTAssertTrue(Perceptual.contrast(held, ink.backdrop) >= ink.contrast - 0.02 || held > 0.9,
                                          "\(mode) frame \(frame)")
                        }
                    }
                }
            }
        }
    }

    // MARK: The transition

    private typealias Timing = LyricHighlightTiming

    func testTheTilePressesInAtTheOnsetAndFadesAtTheLineEnd() {
        let onset = 10.0, release = 13.6
        func pose(_ time: Double, reduce: Bool = false) -> Timing.Pose {
            Timing.pose(time: time, onset: onset, release: release, reduceMotion: reduce)
        }
        XCTAssertFalse(pose(onset - 0.001).isVisible, "nothing before the word is sung")
        XCTAssertFalse(pose(onset).isVisible)
        // A quick ease-out from the leading edge, about 120 ms, with no pop:
        // it never passes the word's width and never shrinks back.
        var width = 0.0
        for step in 1...24 {
            let current = pose(onset + Timing.press * Double(step) / 24)
            XCTAssertTrue(current.isVisible)
            XCTAssertGreaterThanOrEqual(current.width, width)
            XCTAssertLessThanOrEqual(current.width, 1)
            width = current.width
        }
        XCTAssertLessThan(pose(onset + 0.02).width, 0.6, "it presses in rather than appearing whole")
        XCTAssertGreaterThan(pose(onset + 0.06).width, 0.7, "most of the way in half way through")
        XCTAssertEqual(pose(onset + Timing.press).width, 1, accuracy: 1e-9)
        XCTAssertEqual(pose(onset + Timing.press).opacity, 1, accuracy: 1e-9)
        // Held for the rest of the line.
        for time in stride(from: onset + 0.13, to: release, by: 0.05) {
            XCTAssertEqual(pose(time), Timing.Pose(opacity: 1, width: 1))
        }
        // Fades over about 300 ms after the line ends.
        var opacity = 1.0
        for step in 1...30 {
            let current = pose(release + Timing.fade * Double(step) / 30)
            XCTAssertLessThan(current.opacity, opacity)
            if current.isVisible { XCTAssertEqual(current.width, 1, "it fades; it does not shrink") }
            opacity = current.opacity
        }
        XCTAssertFalse(pose(release + Timing.fade).isVisible)
        XCTAssertEqual(Timing.fade, 0.3, accuracy: 0.05)
        XCTAssertEqual(Timing.press, 0.12, accuracy: 0.03)
    }

    @MainActor
    func testRollingHighlightsReleaseWithTheirOwnLines() throws {
        let first = LyricsFetcher.Word(text: "Hold", start: 10, end: 10.3)
        let second = LyricsFetcher.Word(text: "on", start: 10.35, end: 10.6)
        let third = LyricsFetcher.Word(text: "Again", start: 13.9, end: 14.5)
        let lines = [
            LyricsFetcher.Line(start: 10, end: 13.8, text: "Hold on", words: [first, second],
                               isInstrumental: false, wordTimed: true),
            LyricsFetcher.Line(start: 13.9, end: 15, text: "Again", words: [third],
                               isInstrumental: false, wordTimed: true),
        ]
        let window = LyricLayerCache.rollingWindow(around: 0, lines: lines, romanized: [:],
                                                   time: 13.7, lead: 1.2, display: .off)
        XCTAssertEqual(window.words, [first, second, third])
        XCTAssertEqual(window.lineEnds[first.presentationID], 13.8)
        XCTAssertEqual(window.lineEnds[second.presentationID], 13.8)
        XCTAssertEqual(window.lineEnds[third.presentationID], 15)

        let settings = VersoCustom.Settings.shared
        let words = SpokenTypography.prepare(.init(words: window.words, baseSize: 44, mood: .neutral,
            composition: .spoken, fontIdentity: LyricFontIdentity(settings: settings)), settings: settings)
        let poses = try words.map { word in
            SpokenTypographyView.highlight(word, lineEnd: try XCTUnwrap(window.lineEnds[word.source.presentationID]),
                                          time: 13.7, reduceMotion: false)
        }
        XCTAssertEqual(poses, [.init(opacity: 1, width: 1), .init(opacity: 1, width: 1), .hidden])
        let oldest = try XCTUnwrap(words.first)
        let shown = RollingTokenState.make(oldest, next: 2, rank: 1, time: 13.7, composition: .spoken,
            baseSize: 44, stageWidth: 600, intensity: 0, reduceMotion: false, arrival: .glide,
            highlightOpacity: poses[0].opacity).shown
        XCTAssertEqual(shown, 1, accuracy: 0.001, "the normal timed fade cannot hide a held tile")
    }

    func testTheWordStaysReadableWhileItsTileFades() {
        // Light tiles with deep ink over dark and mid backdrops, under white
        // and pastel lyrics. Behind the word the wallpaper is anything from
        // black to the brightest it gets: through the whole fade the word
        // changes to its own colour where its worst cases meet, so it never
        // sinks into the half-faded tile. At worst -- pastel lyrics already
        // at their 3:1 floor on the brightest backdrop -- it dips to about
        // 2:1 for a moment mid-fade; whole or gone, it is back to its floor.
        for brightest in [0.01, 0.05, 0.12, 0.2] {
            for hue in stride(from: 0.0, to: 360, by: 30) {
                guard let tile = LyricHighlight.tile(hue: hue * .pi / 180, chroma: 0.16, backdrop: brightest)?.tile
                else { continue }
                let ink = LyricHighlight.text(on: tile)
                for pastel in [OKLab.white, OKLab(l: 0.86, chroma: 0.08, hue: 1)] {
                    // Lyric colours are lifted to 3:1 against the backdrop, as the stage lifts them.
                    let lyric = Perceptual.readable(pastel, background: brightest)
                    let at = LyricHighlight.textSwitch(tile: tile, text: ink, backdrop: brightest,
                                                       lyric: Perceptual.luminance(lyric))
                    for backdrop in [0, brightest / 2, brightest] {
                        for step in 0...200 {
                            let opacity = Double(step) / 200
                            guard abs(opacity - at) > Timing.switchWidth / 2 else { continue }
                            let shown = Timing.textOpacity(opacity, switchAt: at)
                            let behind = LyricLegibility.linear(opacity * LyricLegibility.encoded(Perceptual.luminance(tile))
                                + (1 - opacity) * LyricLegibility.encoded(backdrop))
                            let text = Perceptual.luminance(LyricHighlight.blend(ink, over: lyric, opacity: shown))
                            let floor = opacity <= 0.001 || opacity >= 0.98 ? 2.99 : 1.9
                            XCTAssertGreaterThanOrEqual(Perceptual.contrast(text, behind), floor,
                                "hue \(hue), \(backdrop) of \(brightest), at \(opacity), switching at \(at)")
                        }
                    }
                }
            }
        }
        // A whole tile carries its own text; none, the lyric colour.
        XCTAssertEqual(Timing.textOpacity(1, switchAt: 0.9), 1)
        XCTAssertEqual(Timing.textOpacity(0, switchAt: 0.1), 0)
    }

    func testReduceMotionOnlyFadesTheTile() {
        let release = 5.6
        var opacity = 0.0
        for step in 1...12 {
            let pose = Timing.pose(time: 5 + Timing.press * Double(step) / 12, onset: 5, release: release, reduceMotion: true)
            XCTAssertEqual(pose.width, 1, "no press, only a fade")
            XCTAssertGreaterThanOrEqual(pose.opacity, opacity)
            opacity = pose.opacity
        }
        XCTAssertEqual(opacity, 1, accuracy: 1e-9)
        XCTAssertEqual(Timing.pose(time: 5.7, onset: 5, release: release, reduceMotion: true).width, 1)
        XCTAssertLessThan(Timing.pose(time: 5.7, onset: 5, release: release, reduceMotion: true).opacity, 1)
    }

    func testTheNewEntranceIsOfferedEverywhere() {
        XCTAssertTrue(WordArrival.allCases.contains(.cutoutHighlight))
        XCTAssertTrue(WordArrival.mixChoices.contains(.cutoutHighlight))
        XCTAssertEqual(WordArrival.cutoutHighlight.title, "Cutout · Highlight")
        XCTAssertFalse(WordArrival.cutoutHighlight.requiresReveal)
        XCTAssertFalse(WordArrival.cutoutHighlight.isCutout)
        for composition in LyricComposition.allCases {
            // Words enter as the layout has them enter; the tile is added.
            XCTAssertEqual(WordArrival.cutoutHighlight.motion(in: composition), WordArrival.automatic.resolved(for: composition))
            XCTAssertEqual(WordArrival.cutoutHighlight.resolved(for: composition), .cutoutHighlight)
            XCTAssertTrue(WordArrival.cutoutHighlight.highlightsSungWords)
        }
        for arrival in WordArrival.allCases where arrival != .cutoutHighlight {
            XCTAssertFalse(arrival.highlightsSungWords)
            XCTAssertEqual(arrival.motion(in: .scattered), arrival)
        }
    }

    // MARK: On the stage

    /// Ink with a known highlight colour, settled.
    @MainActor
    private func greenHighlightInk() -> LyricInk {
        let director = LyricInkDirector()
        var ink = LyricInk.white
        let green = LyricHighlightOptions.oklab(hex: "#2BD96B")!
        for frame in 0..<60 {
            ink = director.ink(.init(mode: .standard, album: [], backdrop: 0.02, highlight: .custom, highlightCustom: green),
                               wall: Double(frame) / 30)
        }
        return ink
    }

    @MainActor
    private func tilePixels(_ view: some View, colour: Color) throws -> Int {
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        let target = try XCTUnwrap(NSColor(colour).usingColorSpace(.sRGB))
        var count = 0
        for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) {
            for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
                guard let pixel = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) else { continue }
                // A little loose: layers drawn offscreen (masks, shadows, 3D) come out slightly shifted.
                if abs(pixel.redComponent - target.redComponent) < 0.1, abs(pixel.greenComponent - target.greenComponent) < 0.1,
                   abs(pixel.blueComponent - target.blueComponent) < 0.1 { count += 1 }
            }
        }
        return count
    }

    /// The entrance chosen for every layout, as Settings stores it, put back afterwards.
    @MainActor
    private func withEntrance<T>(_ arrival: WordArrival?, _ body: () throws -> T) rethrows -> T {
        let settings = VersoCustom.Settings.shared
        let saved = (settings.lyricComposition, settings.wordArrivalByStyle, settings.wordRevealByStyle)
        defer {
            settings.lyricComposition = saved.0; settings.wordArrivalByStyle = saved.1; settings.wordRevealByStyle = saved.2
        }
        settings.lyricComposition = nil
        settings.wordArrivalByStyle = arrival.map { [settings.wordControlsKey: $0.rawValue] } ?? [:]
        return try body()
    }

    @MainActor
    func testEveryLayoutHighlightsTheSungWord() throws {
        let ink = greenHighlightInk()
        let tile = try XCTUnwrap(ink.tile)
        let words: [LyricsFetcher.Word] = [.init(text: "a", start: 1, end: 1.4), .init(text: "the", start: 1.4, end: 2.6),
                                           .init(text: "in", start: 2.6, end: 3.1)]
        func pixels(_ composition: LyricComposition) throws -> Int {
            try tilePixels(LyricLineView(words: words, time: 2.0, stageSize: CGSize(width: 700, height: 360), baseSize: 44,
                                         composition: composition, seed: 1)
                .environment(\.lyricInk, ink)
                .frame(width: 760, height: 420)
                .background(.black), colour: tile)
        }
        for composition in LyricComposition.allCases {
            let plain = try withEntrance(nil) { try pixels(composition) }
            let highlighted = try withEntrance(.cutoutHighlight) { try pixels(composition) }
            XCTAssertGreaterThan(highlighted, plain + 60, "\(composition.title): no tile behind the sung word")
        }
        // In a mix, words that draw "Cutout · Highlight" get it.
        let settings = VersoCustom.Settings.shared
        let mix = settings.wordEntranceMix
        defer { settings.wordEntranceMix = mix }
        settings.wordEntranceMix = [WordArrival.cutoutHighlight.rawValue]
        try withEntrance(.mix) {
            XCTAssertEqual(settings.arrival(in: .scattered, for: words[1]), .cutoutHighlight)
            XCTAssertGreaterThan(try pixels(.scattered), 60)
        }
    }

    @MainActor
    func testTheTileNeverMovesOrResizesWords() throws {
        let ink = greenHighlightInk()
        let words = (0..<6).map { LyricsFetcher.Word(text: ["Hold", "on", "to", "every", "golden", "night"][$0],
                                                     start: 10 + Double($0) * 0.5, end: 10.42 + Double($0) * 0.5) }
        // Rolling stages: every unit sized and placed the same with or without tiles.
        var placed: [RollingStack.Placed] = []
        RollingStackLayout.recorder = { placed = $0 }
        defer { RollingStackLayout.recorder = nil }
        for composition in [LyricComposition.spoken, .cascade, .cutout] {
            for time in [11.03, 11.2, 11.56, 12.3] {
                func place(_ arrival: WordArrival?) -> [CGRect] {
                    withEntrance(arrival) {
                        placed = []
                        let renderer = ImageRenderer(content: SpokenTypographyView(words: words, time: time, baseSize: 44,
                            mood: .neutral, composition: composition, seed: 3, accent: .mint, energy: 0, stageHeight: 500)
                            .environment(\.lyricInk, ink).frame(width: 900, height: 500))
                        renderer.scale = 0.25
                        _ = renderer.cgImage
                        return placed.map { CGRect(origin: $0.centre, size: $0.size) }
                    }
                }
                let plain = place(nil), highlighted = place(.cutoutHighlight)
                XCTAssertFalse(plain.isEmpty)
                XCTAssertEqual(plain, highlighted, "\(composition.title) at \(time)")
            }
        }
        // A constellation's words: the slots its links are drawn between.
        for composition in [LyricComposition.constellation, .constellationEditorial] {
            func slots(_ arrival: WordArrival?) -> [Int: CGRect] {
                withEntrance(arrival) {
                    var found: [Int: CGRect] = [:]
                    let view = LyricLineView(words: words, time: 11.56, stageSize: CGSize(width: 800, height: 400),
                                             baseSize: 44, composition: composition, seed: 2)
                        .environment(\.lyricInk, ink)
                        .backgroundPreferenceValue(WordAnchorKey.self) { anchors in
                            GeometryReader { proxy in
                                let _ = { found = anchors.mapValues { proxy[$0] } }()
                                Color.clear
                            }
                        }
                        .frame(width: 860, height: 460)
                    let renderer = ImageRenderer(content: view)
                    renderer.scale = 0.25
                    _ = renderer.cgImage
                    return found
                }
            }
            let plain = slots(nil)
            XCTAssertEqual(plain.count, words.count)
            XCTAssertEqual(plain, slots(.cutoutHighlight), composition.title)
        }
    }

    @MainActor
    func testARunOfJapaneseIsHighlightedWordByWord() {
        let tokens: [(String, Double, Double)] = [("君", 30.0, 30.3), ("と", 30.3, 30.5), ("歩いた", 30.5, 31.0),
            ("あの", 31.0, 31.4), ("道", 31.4, 31.7), ("を", 31.7, 31.9), ("今も", 31.9, 32.4), ("覚えてる", 32.4, 33.6)]
        let words = tokens.map { LyricsFetcher.Word(text: $0.0, start: $0.1, end: $0.2) }
        let line = LyricsFetcher.Line(start: 30, end: 33.6, text: "君と歩いたあの道を今も覚えてる", words: words,
                                      isInstrumental: false, wordTimed: true)
        let glue = LyricRuns.glue(line: line, words: words)
        let layout = ScatteredTypography.layout(.init(words: words, baseSize: 44, style: .forLine(0), mood: .neutral,
            composition: .scattered, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared),
            lineEnd: 34, glue: glue),
            settings: .shared)
        XCTAssertEqual(layout.runs.count, 1, "set as one run, as written")
        let segments = layout.runs[0].segments
        // Each word a reader takes in has its own tile, along its own letters.
        XCTAssertGreaterThanOrEqual(segments.count, 5, "\(segments)")
        XCTAssertEqual(segments.first?.from, 0)
        XCTAssertEqual(segments.last?.to ?? 0, 1, accuracy: 1e-9)
        for (first, second) in zip(segments, segments.dropFirst()) {
            XCTAssertEqual(first.to, second.from, accuracy: 1e-9)
            XCTAssertLessThan(first.onset, second.onset)
        }
        XCTAssertTrue(segments.allSatisfy { $0.release == 34 })
        let highlight = ScatteredRunFrame.highlight(layout.runs[0], ink: greenHighlightInk(), time: 33.9,
                                                    reduceMotion: false)
        XCTAssertEqual(highlight?.marks.count, segments.count)
        XCTAssertNil(ScatteredRunFrame.highlight(layout.runs[0], ink: greenHighlightInk(), time: 34.4,
                                                 reduceMotion: false))
        // Syllables of one Latin word stay one word.
        let syllables = ["beau", "ti", "ful"].enumerated().map {
            LyricsFetcher.Word(text: $0.element, start: 5 + Double($0.offset) * 0.2, end: 5.2 + Double($0.offset) * 0.2)
        }
        let spelled = ScatteredTypography.layout(.init(words: syllables, baseSize: 44, style: .forLine(0), mood: .neutral,
            composition: .scattered, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared), glue: [false, true, true]),
            settings: .shared)
        XCTAssertEqual(spelled.runs.map(\.segments.count), [1])
    }
}
