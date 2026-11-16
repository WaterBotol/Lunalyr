import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// The thinking orb that stands for an instrumental break (`ThinkingOrb`):
/// thinking-orbs' dotted `listening` state focusing
/// into `breathing` before the next line. Writes PNG stills when
/// LUNALYR_ORB_STILLS_DIR is set (pass it as TEST_RUNNER_LUNALYR_ORB_STILLS_DIR).
final class ThinkingOrbTests: XCTestCase {
    private let album: [SIMD3<Float>] = [SIMD3(0.12, 0.55, 0.85), SIMD3(0.75, 0.25, 0.6), SIMD3(0.95, 0.6, 0.2),
                                         SIMD3(0.1, 0.1, 0.2), SIMD3(0.3, 0.75, 0.55)]

    private func cue(_ gap: VCVocalGap, _ time: Double, lead: Double = 1,
                     audio: ThinkingOrb.Audio? = nil) throws -> InstrumentalCue {
        try XCTUnwrap(gap.instrumentalCue(at: time, previewLead: lead, audio: audio))
    }

    private func pose(_ cue: InstrumentalCue, reduceMotion: Bool = false) -> ThinkingOrb.Pose {
        ThinkingOrb.pose(cue, reduceMotion: reduceMotion, increaseContrast: false)
    }

    // MARK: Style

    @MainActor
    func testStyleDefaultsToComposingAndPersists() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.OrbStyle")
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertEqual(ThinkingOrb.styleKey, "custom.thinkingOrb.style")
        XCTAssertEqual(settings.thinkingOrbStyle, "composing", "the November 2026 default")
        settings.thinkingOrbStyle = "weaving"
        XCTAssertEqual(defaults.string(forKey: "custom.thinkingOrb.style"), "weaving")
        XCTAssertEqual(VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter()).thinkingOrbStyle, "weaving")
        // An unknown stored value reads as Listening.
        defaults.set("sparkles", forKey: "custom.thinkingOrb.style")
        XCTAssertEqual(VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter()).thinkingOrbStyle, "listening")
        settings.reloadLyricStyle()
        XCTAssertEqual(settings.thinkingOrbStyle, "listening")
        // Part of a lyric style, so each surface keeps its own.
        XCTAssertTrue(LyricStyleProfiles.keys.contains(ThinkingOrb.styleKey))
        XCTAssertEqual(ThinkingOrb.styles.count, 10)
        XCTAssertEqual(ThinkingOrb.title(of: "mix"), "Mix")
        XCTAssertEqual(ThinkingOrb.title(of: "nonsense"), "Listening")
    }

    func testNamedStylesDrawTheirOwnStateAndUnknownIsListening() {
        for name in ThinkingOrb.styles.dropLast() {
            XCTAssertEqual(ThinkingOrb.state(style: name, seed: 5).rawValue, name)
        }
        XCTAssertEqual(ThinkingOrb.state(style: "nonsense", seed: 5), .listening)
    }

    func testMixIsStablePerBreakAndNeverRepeatsThePreviousStyle() {
        var seen = Set<OrbEngine.State>()
        var previous: OrbEngine.State?
        for index in 0..<400 {
            let seed = LyricPhraseTiming.timeSeed(Double(index) * 37.5) ^ 0x1D07_5EED
            let state = ThinkingOrb.state(style: "mix", seed: seed, avoiding: previous)
            XCTAssertEqual(state, ThinkingOrb.state(style: "mix", seed: seed, avoiding: previous), "stable for a break")
            XCTAssertNotEqual(state, previous)
            seen.insert(state)
            previous = state
        }
        XCTAssertEqual(seen.count, 9, "every style turns up")
    }

    func testEveryStyleDrawsAtBothSizesAndWithReduceMotion() throws {
        let gap = VCVocalGap(start: 10, end: 30, nextVocal: 1)
        let loud = ThinkingOrb.Audio(level: 0.9, onset: 0.8, phase: 2)
        for state in OrbEngine.State.allCases {
            for diameter in [30.0, 60.0] {
                for reduceMotion in [false, true] {
                    for time in [11.5, 28.1] {
                        let pose = ThinkingOrb.pose(try cue(gap, time, audio: loud), diameter: diameter, state: state,
                                                    reduceMotion: reduceMotion, increaseContrast: false)
                        let frames = ThinkingOrb.frames(pose, diameter: diameter)
                        XCTAssertFalse(frames.isEmpty, "\(state)")
                        XCTAssertEqual(pose.layers.map(\.weight).reduce(0, +), 1, accuracy: 1e-9)
                        for (frame, _) in frames {
                            XCTAssertFalse(frame.dots.isEmpty, "\(state) \(diameter) \(reduceMotion)")
                            XCTAssertTrue(frame.dots.allSatisfy { $0.x.isFinite && $0.y.isFinite && $0.r.isFinite })
                            XCTAssertTrue(frame.lines.allSatisfy { $0.x1.isFinite && $0.w.isFinite })
                        }
                    }
                }
            }
        }
        // Breathing has nothing to cross-fade into.
        let late = ThinkingOrb.pose(try cue(gap, 28.6), state: .breathing, reduceMotion: false, increaseContrast: false)
        XCTAssertEqual(late.layers.map(\.state), [.breathing])
        XCTAssertEqual(late.layers.map(\.weight), [1])
        let early = ThinkingOrb.pose(try cue(gap, 12), state: .breathing, reduceMotion: false, increaseContrast: false)
        XCTAssertEqual(early.layers.map(\.state), [.breathing])
        // Every other style still focuses into breathing at the end.
        let weaving = ThinkingOrb.pose(try cue(gap, 28.6), state: .weaving, reduceMotion: false, increaseContrast: false)
        XCTAssertEqual(weaving.layers.map(\.state), [.breathing])
    }

    @MainActor
    func testWriteOrbStyleSheet() throws {
        guard let path = ProcessInfo.processInfo.environment["LUNALYR_ORB_STYLES_PNG"] else { return }
        let gap = VCVocalGap(start: 10, end: 30, nextVocal: 1)
        let cue = try cue(gap, 19.5, audio: nil)
        let ink = ink(backdrop: 0.03, mode: .standard)
        let colours = [Color(red: 0.10, green: 0.08, blue: 0.22), Color(red: 0.03, green: 0.04, blue: 0.09)]
        let sheet = VStack(spacing: 0) {
            ForEach(0..<3, id: \.self) { row in
                HStack(spacing: 0) {
                    ForEach(0..<3, id: \.self) { column in
                        let state = OrbEngine.State.allCases[row * 3 + column]
                        ZStack {
                            LinearGradient(colors: colours, startPoint: .topLeading, endPoint: .bottomTrailing)
                            ThinkingOrbView(cue: cue, accent: .orange, baseSize: 90, state: state)
                                .environment(\.lyricInk, ink)
                            Text(state.rawValue).font(.system(size: 11)).foregroundStyle(Color.white.opacity(0.6))
                                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(8)
                        }
                        .frame(width: 220, height: 200)
                    }
                }
            }
        }
        let renderer = ImageRenderer(content: sheet)
        renderer.scale = 2
        let rep = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        try XCTUnwrap(rep.representation(using: .png, properties: [:])).write(to: URL(fileURLWithPath: path))
    }

    // MARK: Focus

    func testOpenBreakNeitherFocusesNorEnds() throws {
        XCTAssertEqual(ThinkingOrb.focus(elapsed: 100, span: nil), 0)
        let open = pose(try cue(VCVocalGap(start: 10, end: -1, nextVocal: -1), 40))
        XCTAssertEqual(open.focus, 0)
        XCTAssertEqual(open.layers.map(\.state), [ThinkingOrb.breakState])
    }

    func testTheLastTwoSecondsFocusIntoBreathing() throws {
        let span = 20.0, end = span - WallpaperMotion.interludeOut
        XCTAssertEqual(ThinkingOrb.focus(elapsed: end - 2.01, span: span), 0)
        XCTAssertEqual(ThinkingOrb.focus(elapsed: end - 1, span: span), 0.5, accuracy: 1e-9)
        XCTAssertEqual(ThinkingOrb.focus(elapsed: end, span: span), 1)
        XCTAssertEqual(ThinkingOrb.focus(elapsed: span, span: span), 1)
        // A 3 s break focuses over its last 1.5 s.
        XCTAssertEqual(ThinkingOrb.focus(elapsed: 3 - 0.4 - 1.5 - 0.01, span: 3), 0)
        XCTAssertGreaterThan(ThinkingOrb.focus(elapsed: 3 - 0.4 - 1.4, span: 3), 0)

        let gap = VCVocalGap(start: 10, end: 30, nextVocal: 1)   // up until 29
        let middle = pose(try cue(gap, 18))
        XCTAssertEqual(middle.layers.map(\.state), [.listening])
        let crossing = pose(try cue(gap, 27.6))
        XCTAssertEqual(crossing.layers.map(\.state), [.listening, .breathing])
        XCTAssertEqual(crossing.layers.map(\.weight).reduce(0, +), 1, accuracy: 1e-9)
        let focused = pose(try cue(gap, 28.6))
        XCTAssertEqual(focused.layers.map(\.state), [.breathing])
        // Tighter and brighter.
        XCTAssertLessThan(focused.scale, middle.scale)
        XCTAssertGreaterThan(focused.brighten, middle.brighten)
        XCTAssertGreaterThan(focused.haloAlpha, middle.haloAlpha)
    }

    // MARK: Motion

    func testReduceMotionKeepsStillDots() throws {
        let gap = VCVocalGap(start: 10, end: 30, nextVocal: 1)
        let loud = ThinkingOrb.Audio(level: 1, onset: 1, phase: 3)
        let times = [10.6, 11.0, 14.0, 17.3, 22.9]
        let poses = try times.map { pose(try cue(gap, $0, audio: loud), reduceMotion: true) }
        let frames = poses.map { ThinkingOrb.frames($0, diameter: 48)[0].frame }
        for (pose, frame) in zip(poses, frames) {
            XCTAssertEqual(pose.layers[0].t, OrbEngine.reducedMotionTime, "upstream's still frame")
            XCTAssertEqual(frame, frames[0], "the dots do not move")
            XCTAssertEqual(pose.scale, 1, "no growing in, tightening or shrinking away")
            XCTAssertEqual(pose.swell, 1, "the music does not move it")
            XCTAssertEqual(pose.dotScale, 1)
        }
        // Arrival and leaving are opacity alone.
        XCTAssertLessThan(poses.first!.opacity, 1)
        XCTAssertEqual(poses[2].opacity, 1)
        XCTAssertLessThan(pose(try cue(gap, 28.85), reduceMotion: true).opacity, 1)
        XCTAssertEqual(pose(try cue(gap, 28.85), reduceMotion: true).scale, 1)
        // With motion it turns.
        let moving = try [14.0, 15.3].map { ThinkingOrb.frames(pose(try cue(gap, $0)), diameter: 48)[0].frame }
        XCTAssertNotEqual(moving[0], moving[1])
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
        XCTAssertEqual(try cue(gap, 12).span, 19)
        XCTAssertEqual(try XCTUnwrap(try cue(gap, 12).remaining), 17, accuracy: 1e-9)
        // Grows from 55% as it fades in.
        let start = pose(try cue(gap, 10 + delay + 0.001))
        XCTAssertEqual(start.scale, ThinkingOrb.enterScale, accuracy: 0.03)
        // Shrinks to 30% (of its focused size) and fades over the 0.4 s
        // before the next line's preview.
        let half = try cue(gap, cueEnd - WallpaperMotion.interludeOut / 2)
        XCTAssertEqual(half.leaving, 0.5, accuracy: 1e-9)
        XCTAssertEqual(half.opacity, 0.5, accuracy: 1e-9)
        let leaving = pose(try cue(gap, cueEnd - 0.01))
        XCTAssertEqual(leaving.scale, ThinkingOrb.exitScale * ThinkingOrb.focusScale, accuracy: 0.03)
        XCTAssertLessThan(leaving.opacity, 0.01)
        XCTAssertNil(gap.instrumentalCue(at: cueEnd, previewLead: lead))
        let open = try cue(VCVocalGap(start: 10, end: -1, nextVocal: -1), 40)
        XCTAssertEqual(open.leaving, 0)
        XCTAssertNil(open.remaining)
    }

    // MARK: Audio

    func testAudioSmoothingAttacksIn60msAndReleasesIn250ms() {
        XCTAssertEqual(ThinkingOrb.attack, 0.06)
        XCTAssertEqual(ThinkingOrb.release, 0.25)
        var follower = ThinkingOrb.AudioFollower()
        _ = follower.step(level: 0, onset: 0, wall: 100)
        var audio = ThinkingOrb.Audio(level: 0, onset: 0)
        for frame in 1...6 { audio = follower.step(level: 1, onset: 1, wall: 100 + Double(frame) / 100) }
        XCTAssertEqual(audio.level, 1 - exp(-1), accuracy: 0.01, "63% of a step in one attack time")
        XCTAssertEqual(audio.onset, 1 - exp(-1), accuracy: 0.01)
        for frame in 7...200 { audio = follower.step(level: 1, onset: 1, wall: 100 + Double(frame) / 100) }
        XCTAssertEqual(audio.level, 1, accuracy: 1e-6)
        let top = 102.0
        for frame in 1...25 { audio = follower.step(level: 0, onset: 0, wall: top + Double(frame) / 100) }
        XCTAssertEqual(audio.level, exp(-1), accuracy: 0.01, "falls to 37% in one release time")
        // The same frame twice changes nothing.
        let again = follower.step(level: 1, onset: 1, wall: top + 0.25)
        XCTAssertEqual(again, audio)
        // A stall is one short step, not a jump.
        let stalled = follower.step(level: 1, onset: 1, wall: top + 5)
        XCTAssertLessThan(stalled.level, 1)
        // At 24 fps (a break's frame rate) it lands in the same place.
        var slow = ThinkingOrb.AudioFollower(), fast = ThinkingOrb.AudioFollower()
        _ = slow.step(level: 0, onset: 0, wall: 0); _ = fast.step(level: 0, onset: 0, wall: 0)
        var a = slow.audio, b = fast.audio
        for frame in 1...24 { a = slow.step(level: 0.8, onset: 0, wall: Double(frame) / 24) }
        for frame in 1...120 { b = fast.step(level: 0.8, onset: 0, wall: Double(frame) / 120) }
        XCTAssertEqual(a.level, b.level, accuracy: 1e-6)
    }

    func testTheMusicNudgesTheSpinAndPulsesTheDots() throws {
        // The spin's extra phase grows only with the level.
        var follower = ThinkingOrb.AudioFollower()
        _ = follower.step(level: 0, onset: 0, wall: 0)
        for frame in 1...48 { _ = follower.step(level: 0, onset: 0, wall: Double(frame) / 24) }
        XCTAssertEqual(follower.audio.phase, 0)
        for frame in 49...96 { _ = follower.step(level: 1, onset: 0, wall: Double(frame) / 24) }
        XCTAssertEqual(follower.audio.phase, 2 * ThinkingOrb.speed * ThinkingOrb.levelSpeed, accuracy: 0.03)
        XCTAssertEqual(ThinkingOrb.targets(rms: 0.1, bass: 0.2, pulse: 0.5).level, 0.42, accuracy: 1e-9)
        XCTAssertEqual(ThinkingOrb.targets(rms: 1, bass: 1, pulse: 3).level, 1)
        XCTAssertEqual(ThinkingOrb.targets(rms: 1, bass: 1, pulse: 3).onset, 1)

        let gap = VCVocalGap(start: 10, end: 30, nextVocal: 1)
        let quiet = pose(try cue(gap, 16))
        let loud = pose(try cue(gap, 16, audio: .init(level: 1, onset: 1, phase: 0.5)))
        XCTAssertEqual(quiet.swell, 1)
        XCTAssertEqual(quiet.dotScale, 1)
        XCTAssertEqual(loud.swell, 1 + ThinkingOrb.levelSwell)
        XCTAssertEqual(loud.dotScale, 1 + ThinkingOrb.levelDotGrowth + ThinkingOrb.onsetDotGrowth)
        XCTAssertGreaterThan(loud.brighten, quiet.brighten)
        let presetSpeed = OrbEngine.resolve(.listening, .px64).speed
        XCTAssertEqual(loud.layers[0].t - quiet.layers[0].t, 0.5 * presetSpeed, accuracy: 1e-9)
        // A deeper wave: the rings swing further from the sphere.
        func spread(_ pose: ThinkingOrb.Pose) -> Double {
            let dots = ThinkingOrb.frames(pose, diameter: 48)[0].frame.dots
            return (dots.map(\.y).max() ?? 0) - (dots.map(\.y).min() ?? 0)
        }
        XCTAssertNotEqual(spread(loud), spread(quiet))
    }

    // MARK: Size

    func testTheOrbScalesWithTheTypeSize() {
        XCTAssertEqual(ThinkingOrb.diameter(baseSize: 60), 48)
        XCTAssertLessThan(ThinkingOrb.diameter(baseSize: 50), ThinkingOrb.diameter(baseSize: 90))
        XCTAssertEqual(ThinkingOrb.diameter(baseSize: 10), ThinkingOrb.minDiameter)
        XCTAssertEqual(ThinkingOrb.diameter(baseSize: 400), ThinkingOrb.maxDiameter)
        // Upstream's two tuned designs: 20 px for the smallest orbs.
        XCTAssertEqual(ThinkingOrb.design(diameter: 30), .px20)
        XCTAssertEqual(ThinkingOrb.design(diameter: 48), .px64)
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
        let moments: [(String, Double)] = [("start", 4.4), ("middle", 11.5),
                                           ("last second", 20 - settings.nextWordPreviewLead - 1), ("leaving", leaving)]
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
    /// colour mode over each wallpaper: the start, the middle (quiet and
    /// with loud music on an onset), the last second and the hand-off; a
    /// last row at the smallest size (upstream's 20 px design).
    @MainActor
    private func writeCloseUps(to directory: String, walls: [(String, [Color], Double)]) throws {
        let gap = VCVocalGap(start: 10, end: 30, nextVocal: 1)
        let loud = ThinkingOrb.Audio(level: 0.9, onset: 0.8, phase: 0)
        let columns: [(String, InstrumentalCue)] = try [("start", 11.4, nil), ("middle", 19.5, nil),
                                                        ("middle, loud", 19.5, loud), ("last second", 28.1, nil),
                                                        ("hand-off", 28.8, nil)].map {
            ($0.0, try XCTUnwrap(gap.instrumentalCue(at: $0.1, previewLead: 1, audio: $0.2)))
        }
        let rows: [(LyricColourMode, CGFloat)] = [(.standard, 90), (.albumDrift, 90), (.customGradient, 90),
                                                  (.sungSweep, 90), (.albumDrift, 38)]
        for (tone, colours, luminance) in walls {
            let inks = rows.map { ink(backdrop: luminance, mode: $0.0) }
            let sheet = VStack(spacing: 0) {
                ForEach(rows.indices, id: \.self) { row in
                    HStack(spacing: 0) {
                        ForEach(columns.indices, id: \.self) { column in
                            ZStack {
                                LinearGradient(colors: colours, startPoint: .topLeading, endPoint: .bottomTrailing)
                                ThinkingOrbView(cue: columns[column].1, accent: .orange, baseSize: rows[row].1)
                                    .environment(\.lyricInk, inks[row])
                                Text("\(rows[row].0.rawValue) · \(tone) · \(columns[column].0)").font(.system(size: 10))
                                    .foregroundStyle(tone == "dark" ? Color.white.opacity(0.6) : Color.black.opacity(0.5))
                                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(6)
                            }
                            .frame(width: 220, height: 200)
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
