import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// The thinking orb that stands for an instrumental break (`ThinkingOrb`).
/// Writes PNG stills when LUNALYR_ORB_STILLS_DIR is set (pass it as
/// TEST_RUNNER_LUNALYR_ORB_STILLS_DIR).
final class ThinkingOrbTests: XCTestCase {
    private let album: [SIMD3<Float>] = [SIMD3(0.12, 0.55, 0.85), SIMD3(0.75, 0.25, 0.6), SIMD3(0.95, 0.6, 0.2),
                                         SIMD3(0.1, 0.1, 0.2), SIMD3(0.3, 0.75, 0.55)]

    private func cue(_ gap: VCVocalGap, _ time: Double, lead: Double = 1) throws -> InstrumentalCue {
        try XCTUnwrap(gap.instrumentalCue(at: time, previewLead: lead))
    }

    // MARK: Motion

    func testReduceMotionHoldsTheOrbStillAndOnlyFades() throws {
        let gap = VCVocalGap(start: 10, end: 30, nextVocal: 1)
        let times = [10.6, 11.0, 14.0, 17.3, 22.9, 28.85]
        let poses = try times.map { ThinkingOrb.pose(try cue(gap, $0), reduceMotion: true, increaseContrast: false) }
        for pose in poses {
            XCTAssertEqual(pose.lobes, poses[0].lobes, "no morph")
            XCTAssertEqual(pose.scale, 1, "no breath, no growing in or shrinking away")
        }
        // Arrival and leaving are opacity alone.
        XCTAssertLessThan(poses.first!.opacity, 1)
        XCTAssertEqual(poses[2].opacity, 1)
        XCTAssertLessThan(poses.last!.opacity, 1)
        // The music's level does not move it either.
        var loud = try cue(gap, 14); loud.energy = 1
        XCTAssertEqual(ThinkingOrb.pose(loud, reduceMotion: true, increaseContrast: false).scale, 1)
        // With motion it morphs and breathes.
        let moving = try [14.0, 15.3].map { ThinkingOrb.pose(try cue(gap, $0), reduceMotion: false, increaseContrast: false) }
        XCTAssertNotEqual(moving[0].lobes, moving[1].lobes)
        XCTAssertNotEqual(moving[0].scale, moving[1].scale)
    }

    func testEaseInAndShrinkAwayTiming() throws {
        let gap = VCVocalGap(start: 10, end: 30, nextVocal: 1)
        let lead = 1.0, cueEnd = 30 - lead
        let delay = WallpaperMotion.interludeInDelay, ease = WallpaperMotion.interludeIn
        XCTAssertEqual(ease, 0.5)
        XCTAssertEqual(WallpaperMotion.interludeOut, 0.4)
        XCTAssertNil(gap.instrumentalCue(at: 10 + delay, previewLead: lead), "nothing before the delay")
        XCTAssertEqual(try cue(gap, 10 + delay + ease / 2).entering, 0.5, accuracy: 1e-9)
        XCTAssertEqual(try cue(gap, 10 + delay + ease).entering, 1, accuracy: 1e-9)
        XCTAssertEqual(try cue(gap, 10 + delay + ease).leaving, 0)
        // Grows from 55% as it fades in.
        let start = ThinkingOrb.pose(try cue(gap, 10 + delay + 0.001), reduceMotion: false, increaseContrast: false)
        XCTAssertEqual(start.scale, ThinkingOrb.enterScale, accuracy: 0.03)
        // Shrinks to 30% and fades over the 0.4 s before the next line's preview.
        let half = try cue(gap, cueEnd - WallpaperMotion.interludeOut / 2)
        XCTAssertEqual(half.leaving, 0.5, accuracy: 1e-9)
        XCTAssertEqual(half.opacity, 0.5, accuracy: 1e-9)
        let leaving = ThinkingOrb.pose(try cue(gap, cueEnd - 0.01), reduceMotion: false, increaseContrast: false)
        XCTAssertEqual(leaving.scale, ThinkingOrb.exitScale, accuracy: 0.03)
        XCTAssertLessThan(leaving.opacity, 0.01)
        XCTAssertNil(gap.instrumentalCue(at: cueEnd, previewLead: lead))
        // A break with no known end never leaves on its own, nor glows.
        let open = try cue(VCVocalGap(start: 10, end: -1, nextVocal: -1), 40)
        XCTAssertEqual(open.leaving, 0)
        XCTAssertEqual(ThinkingOrb.pose(open, reduceMotion: false, increaseContrast: false).glow, 0)
    }

    func testPeriodsBreathAndAudio() throws {
        XCTAssertTrue(ThinkingOrb.morphPeriods.allSatisfy { (6...10).contains($0) })
        XCTAssertTrue((2...3).contains(ThinkingOrb.breathPeriod))
        func widest(energy: Double) -> Double {
            (0..<260).map { step -> Double in
                let cue = InstrumentalCue(elapsed: 5 + Double(step) / 100, opacity: 1, progress: 0.3, energy: energy)
                return ThinkingOrb.pose(cue, reduceMotion: false, increaseContrast: false).scale
            }.max()!
        }
        XCTAssertEqual(widest(energy: 0), 1 + ThinkingOrb.breathDepth, accuracy: 0.002)
        XCTAssertGreaterThan(widest(energy: 1), widest(energy: 0) + 0.04, "a light response to the music")
    }

    func testTheOrbScalesWithTheTypeSize() {
        XCTAssertEqual(ThinkingOrb.diameter(baseSize: 60), 48)
        XCTAssertLessThan(ThinkingOrb.diameter(baseSize: 50), ThinkingOrb.diameter(baseSize: 90))
        XCTAssertEqual(ThinkingOrb.diameter(baseSize: 10), ThinkingOrb.minDiameter)
        XCTAssertEqual(ThinkingOrb.diameter(baseSize: 400), ThinkingOrb.maxDiameter)
    }

    func testIncreaseContrastFirmsTheOrb() {
        let cue = InstrumentalCue(elapsed: 4, opacity: 1, progress: 0.7)
        let normal = ThinkingOrb.pose(cue, reduceMotion: false, increaseContrast: false)
        let strong = ThinkingOrb.pose(cue, reduceMotion: false, increaseContrast: true)
        XCTAssertGreaterThan(strong.lobeAlpha, normal.lobeAlpha)
        XCTAssertGreaterThan(strong.lobeEdge, normal.lobeEdge)
        XCTAssertGreaterThan(strong.haloAlpha, normal.haloAlpha)
    }

    // MARK: Colour

    @MainActor
    private func ink(backdrop: Double, mode: LyricColourMode = .albumDrift, increaseContrast: Bool = false) -> LyricInk {
        let director = LyricInkDirector()
        var ink = LyricInk.white
        for frame in 0..<300 {
            ink = director.ink(.init(mode: mode, album: album, backdrop: backdrop, increaseContrast: increaseContrast,
                                     accent: Perceptual.oklab(sRGB: SIMD3(0.95, 0.6, 0.2))),
                               wall: Double(frame) / 60)
        }
        return ink
    }

    @MainActor
    func testDarkLyricsGiveADeepOrbWithALightHalo() {
        let light = ink(backdrop: 0.85)
        XCTAssertEqual(light.dark, 1)
        XCTAssertEqual(light.orb.count, 3)
        let deep = ThinkingOrb.palette(light.orb, dark: light.dark)
        for lobe in deep.lobes {
            XCTAssertLessThan(lobe.l, 0.6, "a deep orb")
            XCTAssertGreaterThanOrEqual(Perceptual.contrast(Perceptual.luminance(lobe), 0.85), 2.95)
        }
        XCTAssertGreaterThan(deep.halo.l, 0.95, "a light halo")
        XCTAssertGreaterThan(deep.core.l, deep.lobes[0].l, "the inner glow is lighter than the body")
        // Its colours are the palette's, not one hue three times.
        XCTAssertNotEqual(deep.lobes[1], deep.lobes[2])

        let dark = ink(backdrop: 0.05)
        XCTAssertEqual(dark.dark, 0)
        let bright = ThinkingOrb.palette(dark.orb, dark: dark.dark)
        for lobe in bright.lobes {
            XCTAssertGreaterThanOrEqual(Perceptual.contrast(Perceptual.luminance(lobe), 0.05), 2.95)
        }
        XCTAssertGreaterThan(bright.core.l, bright.lobes[0].l)
        // Every colour mode gives it colours, Static white and its deep neutral included.
        for mode in LyricColourMode.allCases {
            XCTAssertEqual(ink(backdrop: 0.05, mode: mode).orb.count, 3, mode.rawValue)
            XCTAssertEqual(ink(backdrop: 0.85, mode: mode).orb.count, 3, mode.rawValue)
        }
    }

    @MainActor
    func testIncreaseContrastHoldsTheOrbColoursToFourAndAHalf() {
        let ink = ink(backdrop: 0.05, increaseContrast: true)
        for lobe in ink.orb {
            XCTAssertGreaterThanOrEqual(Perceptual.contrast(Perceptual.luminance(lobe), ink.backdrop), 4.45)
        }
    }

    // MARK: Stills

    @MainActor
    func testWriteOrbStills() throws {
        guard let directory = ProcessInfo.processInfo.environment["LUNALYR_ORB_STILLS_DIR"] else { return }
        try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)
        let music = FakeMusicApp()
        music.queue = [.init(id: "00000000000000A1", name: "First Light", duration: 30)]
        music.position = 10
        let harness = PlayerHarness(music: music)
        harness.start()
        let model = LyricsViewModel.shared, settings = Settings.shared, extras = LunalyrExtras.shared
        let saved = (lines: model.lines, plain: model.plainLines, notFound: model.notFound, loading: model.isLoading,
                     mode: settings.backgroundMode, dots: settings.showInterludes, hidden: extras.lyricsHidden,
                     layout: settings.lyricComposition, colour: LyricColourOptions.shared.mode)
        defer {
            LyricColourOptions.shared.mode = saved.colour
            model.lines = saved.lines
            model.plainLines = saved.plain
            model.notFound = saved.notFound
            model.isLoading = saved.loading
            settings.backgroundMode = saved.mode
            settings.showInterludes = saved.dots
            extras.lyricsHidden = saved.hidden
            settings.lyricComposition = saved.layout
        }
        model.lines = [
            LyricsFetcher.makeLine(text: "Before the rain", start: 1, end: 3),
            LyricsFetcher.makeLine(text: "After the light", start: 20, end: 22.5),
        ]
        model.plainLines = []
        model.notFound = false
        model.isLoading = false
        extras.lyricsHidden = false
        settings.backgroundMode = .desktop
        settings.showInterludes = true
        let leaving = 20 - settings.nextWordPreviewLead - WallpaperMotion.interludeOut * 0.45
        let moments: [(String, Double)] = [("early", 6.5), ("late, glowing", 17.5), ("leaving", leaving)]
        let walls: [(String, [Color], Double)] = [
            ("dark", [Color(red: 0.10, green: 0.08, blue: 0.22), Color(red: 0.03, green: 0.04, blue: 0.09)], 0.03),
            ("light", [Color(red: 0.97, green: 0.94, blue: 0.88), Color(red: 0.86, green: 0.90, blue: 0.95)], 0.80),
        ]
        try writeCloseUps(to: directory, walls: walls)
        for layout in [LyricComposition.scattered, .hinge, .constellation] {
            settings.lyricComposition = layout
            for (tone, colours, luminance) in walls {
                // The layer's colour director starts afresh for each
                // offscreen render, before a palette has eased in, so the
                // full stage is drawn in Static white (the accent); the
                // palette modes are in the close-up sheets below.
                for mode in [LyricColourMode.standard] {
                    LyricColourOptions.shared.mode = mode
                    let sheet = HStack(spacing: 0) {
                        ForEach(moments, id: \.0) { moment in
                            self.still(layout: layout, tone: tone, colours: colours, luminance: luminance,
                                       mode: mode, moment: moment, harness: harness)
                        }
                    }
                    let renderer = ImageRenderer(content: sheet)
                    renderer.scale = 2
                    let rep = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
                    try XCTUnwrap(rep.representation(using: .png, properties: [:])).write(to: URL(
                        fileURLWithPath: "\(directory)/orb-\(tone)-\(layout.rawValue)-\(mode.rawValue).png"))
                }
            }
        }
    }

    /// The orb alone, with ink from a director that has settled, in each
    /// colour mode over each wallpaper: early, late and glowing, leaving.
    @MainActor
    private func writeCloseUps(to directory: String, walls: [(String, [Color], Double)]) throws {
        let gap = VCVocalGap(start: 10, end: 30, nextVocal: 1)
        let cues = [12.0, 26.0, 28.85].compactMap { gap.instrumentalCue(at: $0, previewLead: 1) }
        let modes: [LyricColourMode] = [.standard, .albumDrift, .customGradient, .sungSweep]
        for (tone, colours, luminance) in walls {
            let inks = modes.map { ink(backdrop: luminance, mode: $0) }
            let sheet = VStack(spacing: 0) {
                ForEach(modes.indices, id: \.self) { row in
                    HStack(spacing: 0) {
                        ForEach(cues.indices, id: \.self) { column in
                            ZStack {
                                LinearGradient(colors: colours, startPoint: .topLeading, endPoint: .bottomTrailing)
                                ThinkingOrbView(cue: cues[column], accent: .orange, baseSize: 90)
                                    .environment(\.lyricInk, inks[row])
                                Text("\(modes[row].rawValue) · \(tone)").font(.system(size: 10))
                                    .foregroundStyle(tone == "dark" ? Color.white.opacity(0.6) : Color.black.opacity(0.5))
                                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(6)
                            }
                            .frame(width: 240, height: 200)
                        }
                    }
                }
            }
            let renderer = ImageRenderer(content: sheet)
            renderer.scale = 2
            let rep = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
            try XCTUnwrap(rep.representation(using: .png, properties: [:]))
                .write(to: URL(fileURLWithPath: "\(directory)/orb-closeup-\(tone).png"))
        }
    }

    @MainActor
    private func still(layout: LyricComposition, tone: String, colours: [Color], luminance: Double,
                       mode: LyricColourMode, moment: (String, Double), harness: PlayerHarness) -> some View {
        ZStack {
            LinearGradient(colors: colours, startPoint: .topLeading, endPoint: .bottomTrailing)
            LyricLayer(time: moment.1, baseSize: 64, maxWidth: 420, maxHeight: 220, mood: .neutral,
                       accent: .orange, trackSeed: 0, palette: album, measuredBackdrop: luminance, animating: false)
                .environmentObject(harness.monitor)
            Text("\(layout.title) · \(mode.rawValue) · \(tone) · \(moment.0)").font(.system(size: 11))
                .foregroundStyle(tone == "dark" ? Color.white.opacity(0.6) : Color.black.opacity(0.5))
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(8)
        }
        .frame(width: 460, height: 260)
    }
}
