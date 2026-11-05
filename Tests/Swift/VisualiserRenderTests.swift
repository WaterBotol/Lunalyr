import Accelerate
import Metal
import SwiftUI
import XCTest
@testable import VersoCustom

/// The render harness (Tests/Swift/Support). Scripts/render-visualiser.sh runs
/// `testRenderVisualiserFrames` on its own; without LUNALYR_RENDER_DIR it
/// skips at once. The other tests keep the harness and its fixtures honest in
/// the normal run, briefly and without writing files.
final class VisualiserRenderTests: XCTestCase {
    /// Renders LUNALYR_RENDER_SCENE × LUNALYR_RENDER_FIXTURE to PNG frames,
    /// a contact sheet and a log under LUNALYR_RENDER_DIR, plus index.json and
    /// a WAV of each fixture. See Scripts/render-visualiser.sh.
    @MainActor
    func testRenderVisualiserFrames() async throws {
        guard let request = try VisualiserRenderRequest(environment: ProcessInfo.processInfo.environment) else {
            throw XCTSkip("Renders only for Scripts/render-visualiser.sh, which sets LUNALYR_RENDER_DIR.")
        }
        let renderer = try VisualiserRenderer()
        let started = Date()
        try FileManager.default.createDirectory(at: request.output, withIntermediateDirectories: true)
        let stems = StemCache()
        var runs: [JSONValue] = []
        for fixture in request.fixtures {
            let music = stems.music(fixture, seconds: request.settings.musicSeconds)
            try RenderRun.writeWAV(music, to: request.output.appendingPathComponent("fixtures/\(fixture.rawValue).wav"))
            for scene in request.scenes {
                let result = try await RenderRun.render(scene, fixture, music: music, request: request, renderer: renderer)
                print("[render] " + result.summary)
                XCTAssertTrue(result.visible, "\(scene.rawValue)/\(fixture.rawValue) drew only black frames")
                XCTAssertTrue(result.animated || request.settings.capturedFrames < 2,
                              "\(scene.rawValue)/\(fixture.rawValue) never changed")
                runs.append(result.entry)
                // Rewritten after every run, so a failure later still leaves an index.
                let index: JSONValue = .object([
                    ("scenes", .array(request.scenes.map { .string($0.rawValue) })),
                    ("fixtures", .array(request.fixtures.map { .string($0.rawValue) })),
                    ("settings", .reflecting(request.settings)),
                    ("every", .number(Double(request.every))), ("tiles", .number(Double(request.tiles))),
                    ("device", .string(renderer.gpu.device.name)),
                    ("runs", .array(runs)),
                ])
                try Data(index.text(pretty: 2).utf8).write(to: request.output.appendingPathComponent("index.json"))
            }
        }
        print(String(format: "[render] %ld runs in %.1f s -> %@", runs.count, Date().timeIntervalSince(started),
                     request.output.appendingPathComponent("index.json").path))
    }

    // MARK: - Fixtures

    /// Every test here shares one set of stems this long.
    private static let seconds = 1.9
    private static let stems = StemCache()
    private static func music(_ fixture: SyntheticMusic.Fixture) -> SyntheticMusic.Stereo {
        stems.music(fixture, seconds: seconds)
    }

    func testFixturesAreDeterministicBoundedStemsOfOneMix() {
        let mix = Self.music(.fullMix)
        XCTAssertEqual(mix.count, Int((Self.seconds * SyntheticMusic.sampleRate).rounded(.up)))
        let again = SyntheticMusic.render(.fullMix, seconds: 0.5)
        XCTAssertEqual(again.left, Array(mix.left.prefix(again.count)), "Rendering again, for any length, gives the same samples")
        XCTAssertEqual(again.right, Array(mix.right.prefix(again.count)))
        var stemLevels: [Float] = []
        for fixture in SyntheticMusic.Fixture.allCases {
            let music = Self.music(fixture)
            for channel in [music.left, music.right] {
                XCTAssertLessThanOrEqual(vDSP.maximumMagnitude(channel), 1, fixture.rawValue)
                XCTAssertTrue(vDSP.sum(channel).isFinite, "\(fixture.rawValue) has only finite samples")
            }
            if fixture == .silence {
                XCTAssertEqual(vDSP.maximumMagnitude(music.left + music.right), 0, "Silence is digital silence")
            } else {
                XCTAssertGreaterThan(vDSP.rootMeanSquare(music.left), 0.003, "\(fixture.rawValue) is audible")
                if fixture != .fullMix { stemLevels.append(vDSP.rootMeanSquare(music.left)) }
            }
        }
        XCTAssertGreaterThan(vDSP.rootMeanSquare(mix.left), stemLevels.max() ?? 1, "The mix is louder than any one stem")
        XCTAssertLessThan(vDSP.maximumMagnitude(mix.left + mix.right), 1, "The mix is levelled below full scale")
        XCTAssertEqual(SyntheticMusic.Fixture(name: "kick"), .kickOnly)
        XCTAssertEqual(SyntheticMusic.Fixture(name: "Full-Mix"), .fullMix)
        XCTAssertNil(SyntheticMusic.Fixture(name: "drums"))
    }

    /// Each fixture, fed in as capture feeds it, lands in its own register and
    /// fires its own onsets. Means and counts are over 0.6–1.8 s, once
    /// loudness normalisation has settled.
    func testFixturesDriveTheirOwnRegistersThroughTheAnalyser() {
        func listen(_ fixture: SyntheticMusic.Fixture) -> [(time: Double, sound: AudioSnapshot)] {
            let fps = 30.0, seconds = fixture == .silence ? 0.7 : 1.8
            // One channel keeps this quick; the renderer feeds both.
            var feeder = PCMFeeder(music: Self.music(fixture), into: VisualizerAudio(), channels: 1)
            return (0..<Int(seconds * fps)).map { step in
                let time = Double(step) / fps
                feeder.feed(through: time)
                return (time, feeder.audio.latest(at: time))
            }
        }
        func onsets(_ values: [Float]) -> Int { zip(values, values.dropFirst()).filter { $1 > $0 + 0.3 }.count }
        for fixture in SyntheticMusic.Fixture.allCases {
            let heard = listen(fixture)
            let settled = heard.filter { $0.time >= 0.6 }.map(\.sound)
            func mean(_ value: (AudioSnapshot) -> Float) -> Float { settled.map(value).reduce(0, +) / Float(settled.count) }
            func component(_ index: Int) -> Float { mean { $0.components[index] } }
            let bass = mean(\.bass), mid = mean(\.mid), treble = mean(\.treble)
            let kicks = onsets(settled.map(\.beat)), ticks = onsets(settled.map(\.trebleBeat))
            let name = fixture.rawValue
            switch fixture {
            case .kickOnly:
                XCTAssertTrue((1...3).contains(kicks), "\(name): a kick at 1 s and 1.5 s, heard \(kicks)")
                XCTAssertGreaterThan(bass, treble * 4, name)
                XCTAssertGreaterThan(component(0), component(5), "\(name): sub-bass over presence")
                // A clean negative for hat and treble responses.
                XCTAssertEqual(ticks, 0, "\(name) fires no treble onsets")
                XCTAssertLessThan(heard.map(\.sound.trebleBeat).max() ?? 1, 0.05, "\(name) leaves the treble beat dark")
            case .hatOnly:
                XCTAssertTrue((4...6).contains(ticks), "\(name): a tick every 0.25 s, heard \(ticks)")
                XCTAssertLessThan(bass, 0.05, name)
                XCTAssertGreaterThan(component(7), component(3), "\(name): air over mids")
            case .snareOnly:
                XCTAssertTrue((1...2).contains(ticks), "\(name): a snare at 1.5 s, heard \(ticks)")
                XCTAssertGreaterThan(component(5), component(0), "\(name): presence over sub-bass")
            case .vocalOnly:
                let strongest = (0..<8).max { component($0) < component($1) }
                XCTAssertEqual(strongest, 3, "\(name): 400–1200 Hz leads")
                XCTAssertGreaterThan(mid, bass * 2, name)
                let singing = heard.first { $0.time >= 1.2 }!.sound.level, resting = heard.first { $0.time >= 1.75 }!.sound.level
                XCTAssertGreaterThan(singing, 0.5, "\(name): mid-phrase")
                XCTAssertLessThan(resting, 0.05, "\(name): the rest at 1.5–2 s is silent")
            case .padOnly:
                XCTAssertGreaterThan(settled.map(\.level).min() ?? 0, 0.5, "\(name) is sustained")
                XCTAssertLessThan(settled.map(\.beat).max() ?? 1, 0.2, "\(name) has no kicks")
                XCTAssertGreaterThan(component(2), component(6) * 4, name)
            case .bassOnly:
                XCTAssertGreaterThan(bass, mid * 3, name)
                XCTAssertLessThan(treble, 0.02, name)
                XCTAssertLessThan(settled.map(\.trebleBeat).max() ?? 1, 0.05, "\(name) has no treble onsets")
            case .fullMix:
                XCTAssertGreaterThan(bass, 0.4, name)
                XCTAssertGreaterThan(mid, 0.2, name)
                XCTAssertGreaterThan(treble, 0.08, name)
                XCTAssertGreaterThanOrEqual(ticks, 3, "\(name): the hats come through")
            case .silence:
                XCTAssertTrue(heard.allSatisfy { $0.sound.level == 0 && $0.sound.beat == 0 && ($0.sound.bands.max() ?? 1) == 0 })
            }
        }
    }

    // MARK: - The request

    func testRenderRequestReadsTheForwardedEnvironment() throws {
        XCTAssertNil(try VisualiserRenderRequest(environment: [:]), "No LUNALYR_RENDER_DIR: the render test skips")
        let defaults = try XCTUnwrap(VisualiserRenderRequest(environment: ["LUNALYR_RENDER_DIR": "/tmp/renders"]))
        XCTAssertEqual(defaults.scenes, VisualiserRenderRequest.nativeScenes)
        XCTAssertTrue(defaults.scenes.allSatisfy { $0.fragment != nil })
        XCTAssertEqual(defaults.fixtures, SyntheticMusic.Fixture.allCases)
        XCTAssertEqual(defaults.settings, VisualiserRenderSettings())
        XCTAssertEqual(defaults.output.path, "/tmp/renders")

        let request = try XCTUnwrap(VisualiserRenderRequest(environment: [
            "TEST_RUNNER_LUNALYR_RENDER_DIR": "/tmp/r", "LUNALYR_RENDER_SCENE": "starTrails, Colour Strata,tunnel",
            "LUNALYR_RENDER_FIXTURE": "kick,full-mix", "LUNALYR_RENDER_SIZE": "1280x720", "LUNALYR_RENDER_SECONDS": "2.5",
            "LUNALYR_RENDER_FPS": "15", "LUNALYR_RENDER_PALETTE": "none", "LUNALYR_RENDER_REDUCE_MOTION": "1",
            "LUNALYR_RENDER_EVERY": "3",
        ]))
        XCTAssertEqual(request.scenes, [.starTrails, .colourStrata, .tunnel])
        XCTAssertEqual(request.fixtures, [.kickOnly, .fullMix])
        XCTAssertEqual(request.settings.width, 1280)
        XCTAssertEqual(request.settings.height, 720)
        XCTAssertEqual(request.settings.capturedFrames, 38)
        XCTAssertEqual(request.settings.prerollFrames, 15)
        XCTAssertNil(request.settings.palette.palette)
        XCTAssertTrue(request.settings.response.calm)
        XCTAssertEqual(request.every, 3)

        for bad in [["LUNALYR_RENDER_SCENE": "wavecore"], ["LUNALYR_RENDER_SCENE": "lava"],
                    ["LUNALYR_RENDER_FIXTURE": "drums"], ["LUNALYR_RENDER_SIZE": "big"],
                    ["LUNALYR_RENDER_FPS": "0"], ["LUNALYR_RENDER_PALETTE": "sepia"],
                    ["LUNALYR_RENDER_SCRIM": "3"], ["LUNALYR_RENDER_TEXT_BACKING": "lots"],
                    ["LUNALYR_RENDER_START": "-1"]] {
            XCTAssertThrowsError(try VisualiserRenderRequest(environment: bad.merging(["LUNALYR_RENDER_DIR": "/tmp/r"]) { $1 }),
                                 "\(bad)")
        }
        // The test host's home and working directory are not the caller's.
        for relative in ["~/renders", "renders", "./renders"] {
            XCTAssertThrowsError(try VisualiserRenderRequest(environment: ["LUNALYR_RENDER_DIR": relative]), relative) {
                XCTAssertTrue("\($0)".contains("absolute"), "\($0)")
            }
        }

        // A 14-inch MacBook Pro by default, so the drawable is scaled up as on
        // a Retina display: the wallpaper's pixel budget, less for the tunnel
        // and Wavefield.
        XCTAssertEqual(defaults.settings.width, 3024)
        XCTAssertEqual(defaults.settings.height, 1964)
        XCTAssertEqual(defaults.settings.drawableSize(for: .starTrails), CGSize(width: 1664, height: 1081))
        XCTAssertEqual(defaults.settings.drawableSize(for: .tunnel), CGSize(width: 1209, height: 785))
        var full = defaults.settings
        full.fullResolution = true
        XCTAssertEqual(full.drawableSize(for: .tunnel), CGSize(width: 3024, height: 1964))
        XCTAssertEqual(request.settings.drawableSize(for: .starTrails), CGSize(width: 1280, height: 720))

        // The shade over the scene, and the clock it starts from.
        XCTAssertEqual(defaults.settings.scrim, 1, "The shipped Darkening")
        XCTAssertNil(defaults.settings.textBacking, "Automatic text backing")
        XCTAssertEqual(defaults.settings.start, 0)
        let shade = try XCTUnwrap(VisualiserRenderRequest(environment: [
            "LUNALYR_RENDER_DIR": "/tmp/r", "LUNALYR_RENDER_SCRIM": "0.5", "LUNALYR_RENDER_TEXT_BACKING": "0.3",
            "LUNALYR_RENDER_START": "600",
        ])).settings
        XCTAssertEqual(shade.scrim, 0.5)
        XCTAssertEqual(shade.textBacking, 0.3)
        XCTAssertEqual(shade.textBackingAmount, 0.3)
        XCTAssertEqual(shade.start, 600)
        for (text, amount) in [("off", 0.0), ("0", 0), ("auto", nil)] as [(String, Double?)] {
            let settings = try XCTUnwrap(VisualiserRenderRequest(environment: [
                "LUNALYR_RENDER_DIR": "/tmp/r", "LUNALYR_RENDER_TEXT_BACKING": text])).settings
            XCTAssertEqual(settings.textBacking, amount, text)
        }
    }

    /// The automatic text backing is NowPlayingView's for a visualiser: just
    /// enough for white lyrics over the palette's brightest stop, on top of
    /// the scrim at the stage.
    func testAutomaticTextBackingFollowsThePaletteAndTheScrim() {
        var settings = VisualiserRenderSettings()
        let album = settings.textBackingAmount
        XCTAssertGreaterThan(album, WallpaperShade.minimumBacking, "The album cover needs a little backing")
        XCTAssertLessThanOrEqual(album, LyricLegibility.maximumBacking)
        settings.palette = .warm
        XCTAssertGreaterThan(settings.textBackingAmount, album, "A brighter cover needs more")
        settings.scrim = 2
        let darker = settings.textBackingAmount
        settings.scrim = 0
        XCTAssertGreaterThan(settings.textBackingAmount, darker, "Less scrim leaves more for the backing")
        settings.palette = .none
        let fallback = AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette)
        XCTAssertEqual(settings.textBackingAmount, LyricLegibility.backing(
            forLuminance: LyricLegibility.visualizerLuminance(fallback), existingDarkening: 0,
            target: LyricLegibility.targetContrast(baseSize: LyricViewportMetrics(size: CGSize(width: 1512, height: 982)).baseSize)),
            "Without artwork, the fallback palette decides")
        XCTAssertTrue(VisualiserRenderSettings().overlayDescription.hasPrefix("As on the desktop"))
        settings.textBacking = 0
        XCTAssertTrue(settings.overlayDescription.hasPrefix("The scene alone"))
    }

    func testLogsReflectEveryUniformAndKeepNumbersShort() throws {
        var uniforms = VizUniforms()
        uniforms.bass = 0.25; uniforms.resolution = SIMD2(320, 180)
        let json = JSONValue.reflecting(uniforms)
        XCTAssertEqual(json["bass"]?.number, 0.25)
        guard case .object(let fields) = json else { return XCTFail("VizUniforms logs as an object") }
        XCTAssertEqual(fields.map(\.0), Mirror(reflecting: uniforms).children.compactMap(\.label),
                       "Every stored field, in declaration order, including any added later")
        XCTAssertEqual(JSONValue.reflecting(Float(0.1)).text(), "0.1")
        XCTAssertEqual(JSONValue.reflecting(Double.nan).text(), "null")
        XCTAssertEqual(JSONValue.object([("a", .array([.number(1), .number(2.5)]))]).text(), #"{"a": [1, 2.5]}"#)
        let sound = JSONValue.reflecting(AudioSnapshot())
        XCTAssertEqual(sound["waveform"]?["count"]?.number, Double(AudioSnapshot.waveformCount), "Long arrays are summarised")
        guard case .array(let bands)? = sound["bands"] else { return XCTFail("Bands log in full") }
        XCTAssertEqual(bands.count, AudioSnapshot.bandCount)
        // Every integer and floating-point type is a number, and SIMD vectors of any of them arrays.
        XCTAssertEqual(JSONValue.reflecting(UInt32(3)).text(), "3")
        XCTAssertEqual(JSONValue.reflecting(Int32(-2)).text(), "-2")
        XCTAssertEqual(JSONValue.reflecting(UInt8(7)).text(), "7")
        XCTAssertEqual(JSONValue.reflecting(CGFloat(0.5)).text(), "0.5")
        XCTAssertEqual(JSONValue.reflecting(-Double.infinity).text(), "null")
        XCTAssertEqual(JSONValue.reflecting(true).text(), "true")
        XCTAssertEqual(JSONValue.reflecting(SIMD4<UInt32>(1, 2, 3, 4)).text(), "[1, 2, 3, 4]")
        XCTAssertEqual(JSONValue.reflecting(SIMD2<Double>(0.25, 1)).text(), "[0.25, 1]")
        XCTAssertEqual(JSONValue.reflecting([SIMD2<Float>(1, 2)]).text(), "[[1, 2]]")
        XCTAssertEqual(JSONValue.reflecting(Float?.none as Any).text(), "null")
        XCTAssertEqual(JSONValue.reflecting(Float?.some(0.5) as Any).text(), "0.5")
        XCTAssertEqual(JSONValue.numbers(Array(repeating: 0.5, count: 256)).text().split(separator: ",").count, 256,
                       "The bound waveform logs in full")
        // A field the harness does not know about is plotted as well as logged.
        struct Later {
            struct Ring { var onsets = [Float](repeating: 0.5, count: 12) }
            struct Types {
                var hat: Float = 0.25
                var hatOnsets: UInt32 = 3
                var hatTime = -Double.infinity
                var ring = Ring()
            }
            var bass: Float = 0.5, kick: Float = 0.75
            var stars = SIMD3<Float>(1, 2, 3)
            var count: Int32 = 2
            var types = Types()
        }
        let signals = KnownFields.newSignals(in: Later(), known: ["bass"], prefix: "u.")
        XCTAssertEqual(signals.map(\.0), ["u.kick", "u.stars[0]", "u.stars[1]", "u.stars[2]", "u.count", "u.types.hat",
                                          "u.types.hatOnsets", "u.types.hatTime"],
                       "Integers of any width count; two levels in, it stops (types.ring.onsets is not plotted)")
        XCTAssertEqual(signals.map(\.1), [0.75, 1, 2, 3, 2, 0.25, 3, 0], "An onset time of -inf plots as 0")
        XCTAssertTrue(RenderRun.isClockLike([0, 1, 1, 2, 3]), "An onset counter")
        XCTAssertTrue(RenderRun.isClockLike([1.0, 1.5, 2.25, 3.5]), "A clock")
        XCTAssertFalse(RenderRun.isClockLike([0.1, 0.3, 0.6]), "A level rising over a short run")
        XCTAssertFalse(RenderRun.isClockLike([0, 1, 0, 1]), "A beat")
        XCTAssertFalse(RenderRun.isClockLike([2, 2, 2]), "A constant")
        XCTAssertEqual(RenderRun.clipped("abcdef", to: 4), "abc…")
        XCTAssertEqual(RenderRun.clipped("abc", to: 4), "abc")
        XCTAssertEqual(ContactSheet.tileIndices(frames: 120, tiles: 16).count, 16)
        XCTAssertEqual(ContactSheet.tileIndices(frames: 120, tiles: 16).last, 119)
        XCTAssertEqual(ContactSheet.tileIndices(frames: 3, tiles: 16), [0, 1, 2])
    }

    func testFrameMetricsMeasureEveryPixel() {
        // Four pixels (BGRA): black, white, pure red and a dim grey just under the lit threshold.
        let first = BGRAImage(width: 2, height: 2, pixels: [0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 16, 16, 16, 255])
        let metrics = FrameMetrics()
        let reading = metrics.measure(first)
        let grey = Double(ExposureLimiter.luminance(red: 16 / 255, green: 16 / 255, blue: 16 / 255))
        XCTAssertEqual(reading.luminance, (0 + 1 + 0.2126 + grey) / 4, accuracy: 1e-5)
        XCTAssertEqual(reading.lit, 0.5, accuracy: 1e-6, "White and red are lit; black and the dim grey are not")
        XCTAssertEqual(reading.change, 0, "The first frame has nothing to change from")
        let inverted = BGRAImage(width: 2, height: 2, pixels: first.pixels.enumerated().map { $0.offset % 4 == 3 ? 255 : 255 - $0.element })
        let next = metrics.measure(inverted)
        let expected = first.pixels.enumerated().filter { $0.offset % 4 != 3 }.map { abs(255 - 2 * Double($0.element)) }
        XCTAssertEqual(next.change, expected.reduce(0, +) / Double(expected.count) / 255, accuracy: 1e-5)
        XCTAssertTrue(first.identical(to: first))
        XCTAssertFalse(first.identical(to: inverted))

        let follows = RenderRun.follows([0, 1, 0, 1, 0], [("beat", [0, 1, 0, 1, 0]), ("level", [1, 0, 1, 0, 1]),
                                                          ("flat", [0.5, 0.5, 0.5, 0.5, 0.5])], skipFirst: false)
        XCTAssertEqual(follows.map(\.signal), ["beat", "level"], "A signal that never varies is left out")
        XCTAssertEqual(follows.map(\.r), [1, -1])
    }

    // MARK: - What is laid over the scene

    /// The scrim's stops, top to bottom, and the text backing's ellipse
    /// around the middle, multiplied into gamma-encoded values.
    func testOverlayShadesAsNowPlayingViewDoes() {
        XCTAssertEqual(WallpaperOverlay.scrimOpacity(at: 0, strength: 1), WallpaperShade.scrim[0])
        XCTAssertEqual(WallpaperOverlay.scrimOpacity(at: 0.5, strength: 1), WallpaperShade.scrim[1])
        XCTAssertEqual(WallpaperOverlay.scrimOpacity(at: 1, strength: 1), WallpaperShade.scrim[2])
        XCTAssertEqual(WallpaperOverlay.scrimOpacity(at: 0.25, strength: 2), (WallpaperShade.scrim[0] + WallpaperShade.scrim[1]),
                       accuracy: 1e-12, "Linear between stops, scaled by Darkening")
        XCTAssertEqual(WallpaperOverlay.scrimOpacity(at: 1, strength: 3), 1, "Never more than opaque")

        let width = 101, height = 61, amount = 0.4
        let overlay = WallpaperOverlay(width: width, height: height, scrim: 1, backing: amount)
        func keep(_ column: Int, _ row: Int) -> Double { Double(overlay.keep[row * width + column]) }
        let middle = (1 - WallpaperOverlay.scrimOpacity(at: 0.5, strength: 1)) * (1 - amount)
        XCTAssertEqual(keep(50, 30), middle, accuracy: 1e-4, "The full backing at the centre, over the scrim")
        XCTAssertEqual(keep(0, 30), 1 - WallpaperShade.scrim[1], accuracy: 0.05, "Little backing at the sides")
        XCTAssertLessThan(keep(0, 0), keep(0, 30), "The scrim is darker at the top than in the middle")
        XCTAssertLessThan(keep(0, 60), keep(0, 0), "and darker still at the bottom")
        XCTAssertEqual(keep(10, 30), keep(90, 30), accuracy: 1e-6, "Symmetric")
        XCTAssertTrue(WallpaperOverlay(width: 4, height: 4, scrim: 0, backing: 0).isClear)
        XCTAssertTrue(WallpaperOverlay(width: 4, height: 4, scrim: 0, backing: WallpaperShade.minimumBacking).isClear,
                      "NowPlayingView skips a backing that faint")

        // Each colour byte keeps its share; alpha stays opaque.
        let grey = BGRAImage(width: width, height: height, pixels: (0..<width * height).flatMap { _ in [200, 100, 50, 255] as [UInt8] })
        let shown = overlay.apply(to: grey)
        let centre = (30 * width + 50) * 4
        XCTAssertEqual(Array(shown.pixels[centre..<centre + 4]),
                       [UInt8((200 * middle).rounded()), UInt8((100 * middle).rounded()), UInt8((50 * middle).rounded()), 255])
        XCTAssertTrue(WallpaperOverlay(width: width, height: height, scrim: 0, backing: 0).apply(to: grey).identical(to: grey))
    }

    /// WallpaperOverlay's arithmetic against SwiftUI drawing the gradients as
    /// NowPlayingView composes them (from WallpaperShade), over white.
    @MainActor
    func testOverlayMatchesSwiftUIsOwnGradients() throws {
        let width = 160, height = 104, strength = 1.3, amount = 0.45
        let size = CGSize(width: width, height: height)
        let stage = LyricViewportMetrics(size: size).stageSize
        let view = ZStack {
            Color.white
            LinearGradient(colors: WallpaperShade.scrim.map { Color.black.opacity($0 * strength) },
                           startPoint: .top, endPoint: .bottom)
            EllipticalGradient(stops: WallpaperShade.backingStops.map { .init(color: .black.opacity(amount * $0.share), location: $0.location) },
                               center: .center, startRadiusFraction: 0, endRadiusFraction: WallpaperShade.backingRadius)
                .frame(width: stage.width * WallpaperShade.backingSpan.width, height: stage.height * WallpaperShade.backingSpan.height)
                .frame(width: size.width, height: size.height)
        }
        .frame(width: size.width, height: size.height)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        let image = try XCTUnwrap(renderer.cgImage, "SwiftUI rendered nothing")
        XCTAssertEqual(image.width, width)
        XCTAssertEqual(image.height, height)
        // Read the grey as drawn, in the image's own colour space.
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        try pixels.withUnsafeMutableBytes { buffer in
            let context = try XCTUnwrap(CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                bytesPerRow: width * 4, space: image.colorSpace ?? BGRAImage.colourSpace,
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
        let overlay = WallpaperOverlay(width: width, height: height, scrim: strength, backing: amount)
        var worst = 0.0, total = 0.0
        for row in 0..<height {
            for column in 0..<width {
                let drawn = Double(pixels[(row * width + column) * 4 + 1])
                let expected = 255 * Double(overlay.keep[row * width + column])
                worst = max(worst, abs(drawn - expected))
                total += abs(drawn - expected)
            }
        }
        XCTAssertLessThan(total / Double(width * height), 1.0, "Mean difference in 8-bit steps")
        XCTAssertLessThan(worst, 4, "Largest difference in 8-bit steps")
    }

    /// Capture appends without a time and is stamped now; the harness passes
    /// simulated times. Both must reach the analyser by the same path.
    func testCaptureAndTheHarnessFeedTheAnalyserTheSameWay() {
        let music = Self.music(.fullMix)
        let count = AudioPCMProcessor.analyserHop * 24
        let captured = VisualizerAudio(), stamped = VisualizerAudio()
        for audio in [captured, stamped] { audio.setChannelCount(2) }
        for (channel, samples) in [music.left, music.right].enumerated() {
            samples.withUnsafeBufferPointer { buffer in
                captured.append(buffer.baseAddress!, count: count, sampleRate: SyntheticMusic.sampleRate, channel: channel)
                stamped.append(buffer.baseAddress!, count: count, sampleRate: SyntheticMusic.sampleRate, channel: channel,
                               hostTime: AudioAnalyzer.now())
            }
        }
        let now = AudioAnalyzer.now()
        let a = captured.latest(at: now), b = stamped.latest(at: now)
        XCTAssertGreaterThan(a.level, 0.1, "Both heard the music")
        // Everything but the onset envelopes, which depend on the stamps alone.
        XCTAssertEqual(a.bands, b.bands)
        XCTAssertEqual(a.components, b.components)
        XCTAssertEqual(a.waveform, b.waveform)
        XCTAssertEqual([a.level, a.bass, a.mid, a.treble, a.flux, a.centroid], [b.level, b.bass, b.mid, b.treble, b.flux, b.centroid])
        XCTAssertEqual([a.wavefield.rms, a.wavefield.energy, a.wavefield.centroid], [b.wavefield.rms, b.wavefield.energy, b.wavefield.centroid])
        XCTAssertEqual(a.wavefield.bands, b.wavefield.bands)
    }

    // MARK: - The renderer

    /// A few frames through the production coordinator, kept in memory.
    @MainActor
    func testHarnessDrawsThroughTheProductionRenderer() async throws {
        let renderer = try VisualiserRenderer()
        var settings = VisualiserRenderSettings()
        settings.width = 160; settings.height = 90
        settings.preroll = 0.5; settings.seconds = 0.6; settings.fps = 10
        XCTAssertLessThan(settings.musicSeconds, Self.seconds)
        let music = Self.music(.kickOnly)
        var frames: [RenderedFrame] = []
        try await renderer.render(.starTrails, music: music, settings: settings) { frames.append($0) }
        XCTAssertEqual(frames.count, settings.capturedFrames)
        XCTAssertEqual(frames.map(\.index), Array(0..<frames.count))
        XCTAssertEqual(frames.first?.time ?? 0, 0.5, accuracy: 1e-9, "Capture starts after the pre-roll")
        for frame in frames {
            XCTAssertEqual(frame.image.width, 160)
            XCTAssertEqual(frame.image.height, 90)
            XCTAssertEqual(frame.inputs.uniforms.resolution, SIMD2(160, 90))
            XCTAssertGreaterThan(FrameMetrics().measure(frame.image).lit, 0.001, "Frame \(frame.index) is not black")
            XCTAssertGreaterThan(frame.analyser.level, 0, "The analyser hears the kicks")
        }
        XCTAssertFalse(frames[0].image.identical(to: frames[frames.count - 1].image), "The scene moves")
        XCTAssertGreaterThan(frames.map(\.sound.bass).max() ?? 0, 0.2, "Smoothed bass reaches the frame")
        XCTAssertEqual(frames.map(\.inputs.uniforms.bass), frames.map(\.sound.bass), "Uniforms come from the smoothed sound")
        XCTAssertGreaterThan(frames.map(\.inputs.uniforms.time).max() ?? 0, frames[0].inputs.uniforms.time,
                             "The scene clock advances")

        // A scene built a while into the session adopts the shared clock.
        XCTAssertEqual(Double(frames[0].inputs.uniforms.time), 0.5 * VisualClock.visualizerRate, accuracy: 1e-4,
                       "From 0, the scene clock runs at the visualiser rate")
        var later = settings
        later.start = 100
        var resumed: [RenderedFrame] = []
        try await renderer.render(.starTrails, music: music, settings: later) { resumed.append($0) }
        XCTAssertEqual(Double(resumed[0].inputs.uniforms.time), 100.5 * VisualClock.visualizerRate, accuracy: 1e-3)

        // A silent run falls back to the wallpaper's ambient spectrum.
        var quiet: [RenderedFrame] = []
        try await renderer.render(.starTrails, music: Self.music(.silence), settings: settings) { quiet.append($0) }
        XCTAssertEqual(quiet.last?.analyser.level, 0)
        XCTAssertGreaterThan(quiet.last?.sound.bands.max() ?? 0, 0.1, "Silence shows the ambient spectrum")

        let bass = ContactSheet.Series(name: "bass", colour: ContactSheet.barColours[0], values: frames.map { Double($0.sound.bass) })
        let many = (0..<30).map { ContactSheet.Series(name: "u.soundType\($0)", colour: bass.colour, values: bass.values) }
        func sheet(_ plots: [ContactSheet.Plot]) throws -> CGImage {
            try XCTUnwrap(ContactSheet(title: "test", subtitle: ["a"], tiles: try frames.prefix(2).map {
                ContactSheet.Tile(index: $0.index, time: $0.time, image: try XCTUnwrap($0.image.cgImage), bars: [0.5])
            }, tileSize: (160, 90), times: frames.map(\.time), plots: plots).image())
        }
        let one = try sheet([.init(label: "sound", series: [bass])])
        XCTAssertGreaterThanOrEqual(one.width, 960)
        let wrapped = try sheet([.init(label: "sound", series: [bass]), .init(label: "new signals", series: many)])
        XCTAssertEqual(wrapped.width, one.width)
        XCTAssertGreaterThan(wrapped.height, one.height + 150 + 15 * 3, "A long legend wraps instead of running off the sheet")
    }
}
