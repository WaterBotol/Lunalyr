import CoreGraphics
import Foundation
import Metal
import XCTest
@testable import VersoCustom

// Renders native visualiser scenes offscreen through the app's own renderer,
// as a wallpaper draws them, with a simulated clock:
//
//   SyntheticMusic → VisualizerAudio.append(hostTime:) (both channels, in the
//   hop-sized pieces AudioPCMProcessor feeds, stamped with simulated capture
//   times; capture's own overload is this one stamped now)
//   → AudioAnalyzer → VisualizerAudio.latest(at:) → Coordinator.prepare
//   (audibility, ambient fallback, scene timeline, VisualizerSmoother)
//   → Coordinator.encodeFrame (VizUniforms, Wavefield modes, album colours,
//   the photosensitivity limiter when calm) into an offscreen texture
//   → the shade NowPlayingView lays over every visualiser (WallpaperOverlay).
//
// Nothing here copies that code: a new analyser feature, uniform or scene
// change shows up in the frames and in the log on its own. What stands in for
// the host is small and listed here: the clock (simulated, not
// CACurrentMediaTime), the audio source (a VisualizerAudio that measures no
// output device, so no extra delay), the timeline store (fresh per run), the
// visualiser clock NowPlayingView feeds (a VisualClock at
// `VisualClock.visualizerRate`, from 0 unless `start` says the wallpaper had
// been running), the settings (passed in, never read from Settings.shared),
// the window (a texture of the drawable size the wallpaper would choose; the
// resolution ladder stays at full size, as it does while the GPU keeps up,
// and the layer's magnification to the display is CoreGraphics' bilinear
// scaling) and the SwiftUI layers above the scene: the scrim and the text
// backing are composited from WallpaperShade's values as NowPlayingView
// draws them, and the lyrics themselves are not drawn.

/// One rendering run's knobs, at the shipped defaults.
struct VisualiserRenderSettings: Equatable {
    /// The display in pixels (its size in points times its backing scale),
    /// which every PNG has: a 14-inch MacBook Pro at its default "looks like
    /// 1512 × 982". The wallpaper's pixel budget, not this, sets the drawable.
    var width = 3024, height = 1964
    var seconds = 4.0
    /// The wallpaper's default rate. What a scene shows depends on it (the
    /// smoother and Wavefield's modes step once a frame), as on screen.
    var fps = 30.0
    /// Simulated playback before the first captured frame, so loudness
    /// normalisation and smoothing have settled as for someone listening.
    var preroll = 1.0
    /// Seconds the wallpaper had been running when the scene appeared. A
    /// scene being built adopts the shared visualiser clock, so its own clock
    /// starts at `start` × 0.7 × Effect speed (slower under Reduce Motion,
    /// and from 0 again past 4096).
    var start = 0.0
    /// Settings › Sensitivity (0.3–3), Punch (0–2), Colour richness
    /// (0.4–1.8) and Effect speed.
    var sensitivity = 1.0, punch = 1.0, vividness = 1.0, speed = 1.0
    var reduceMotion = false, dimFlashingLights = false
    var palette: RenderPalette = .album
    /// Draw at the display size instead of the wallpaper's pixel budget.
    var fullResolution = false
    /// Settings › Darkening (0–2): NowPlayingView's scrim over the scene.
    /// Increase Contrast draws at least `Settings.increasedContrastScrimFloor`.
    var scrim = 1.0
    /// The text backing behind the lyric stage. nil works it out as
    /// Automatic text backing (the default) does for a visualiser while
    /// lyrics show; 0 is an empty stage; otherwise the manual amount (0–1).
    var textBacking: Double?

    /// Points per pixel are taken as a Retina display's when the automatic
    /// text backing reads the lyric size. Only very large type changes it.
    static let backingScale = 2.0

    var response: VisualizerResponse {
        VisualizerResponse(reduceMotion: reduceMotion, dimFlashingLights: dimFlashingLights, punch: punch)
    }
    var prerollFrames: Int { max(0, Int((preroll * fps).rounded())) }
    var capturedFrames: Int { max(1, Int((seconds * fps).rounded())) }
    /// Audio for every simulated frame, with a little to spare.
    var musicSeconds: Double { Double(prerollFrames + capturedFrames + 2) / fps + 0.05 }

    /// The drawable the production renderer would use for this display.
    func drawableSize(for scene: AudioVisualizerStyle) -> CGSize {
        let display = CGSize(width: width, height: height)
        return fullResolution ? display
            : AudioVisualizerView.Coordinator.drawableSize(native: display, style: scene, preview: false)
    }

    /// The text backing NowPlayingView draws: the manual amount, or what its
    /// `textBackingAmount` works out for a visualiser layer (the backdrop
    /// gauged from the wallpaper palette, over the scrim at the stage).
    var textBackingAmount: Double {
        if let textBacking { return textBacking }
        // Without artwork NowPlayingView gauges its fallback palette.
        let cover = palette.palette ?? AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette)
        let points = CGSize(width: Double(width) / Self.backingScale, height: Double(height) / Self.backingScale)
        let target = LyricLegibility.targetContrast(baseSize: LyricViewportMetrics(size: points).baseSize)
        return LyricLegibility.backing(forLuminance: LyricLegibility.visualizerLuminance(cover),
            existingDarkening: WallpaperShade.scrimAtStage(strength: max(0, scrim)), target: target)
    }

    /// One line on what the frames show above the scene.
    var overlayDescription: String {
        let backing = textBackingAmount
        let drawn = backing > WallpaperShade.minimumBacking
        if scrim <= 0, !drawn {
            return "The scene alone: no scrim or text backing (on the desktop the scrim shows unless Darkening is 0)"
        }
        let backingText = drawn
            ? String(format: "the text backing at %.3f (%@)", backing, textBacking == nil ? "automatic, lyrics on stage" : "manual")
            : "no text backing (an empty lyric stage)"
        return String(format: "As on the desktop: the Darkening scrim at %g and %@ over the scene; the lyrics are not drawn",
                      scrim, backingText)
    }
}

/// Album colours for the run. NowPlayingView passes nil without artwork.
enum RenderPalette: String, CaseIterable, Sendable {
    /// A dusky cover: deep blue, violet, rose, amber and teal.
    case album
    /// No artwork: the scenes' own colours.
    case none
    case warm, cool, mono
    /// Star Trails' sky checks: one blue, warm brown and olive, saturated primaries, pastels,
    /// black and white.
    case blue, earth, vivid, pastel, bw

    var palette: AlbumWallpaperPalette? {
        switch self {
        case .none: return nil
        case .album: return Self.cover([(0.62, 0.70, 0.55), (0.78, 0.55, 0.62), (0.94, 0.58, 0.80),
                                        (0.08, 0.78, 0.90), (0.50, 0.62, 0.66)])
        case .warm: return Self.cover([(0.00, 0.78, 0.70), (0.04, 0.80, 0.92), (0.09, 0.85, 0.95),
                                       (0.13, 0.70, 0.98), (0.97, 0.65, 0.60)])
        case .cool: return Self.cover([(0.55, 0.80, 0.60), (0.50, 0.70, 0.80), (0.62, 0.65, 0.90),
                                       (0.42, 0.55, 0.75), (0.68, 0.50, 0.70)])
        case .mono: return Self.cover([(0.60, 0.05, 0.35), (0.60, 0.05, 0.55), (0.60, 0.05, 0.75),
                                       (0.60, 0.05, 0.90), (0.60, 0.05, 0.60)])
        case .blue: return Self.cover([(0.60, 0.85, 0.35), (0.60, 0.70, 0.60), (0.61, 0.55, 0.85),
                                       (0.59, 0.80, 0.50), (0.60, 0.40, 0.95)])
        case .earth: return Self.cover([(0.07, 0.60, 0.40), (0.10, 0.50, 0.55), (0.15, 0.55, 0.45),
                                        (0.18, 0.45, 0.35), (0.12, 0.35, 0.70)])
        case .vivid: return Self.cover([(0.00, 0.95, 0.95), (0.15, 0.95, 0.95), (0.33, 0.90, 0.85),
                                        (0.60, 0.95, 0.90), (0.83, 0.90, 0.90)])
        case .pastel: return Self.cover([(0.95, 0.25, 0.97), (0.40, 0.22, 0.95), (0.75, 0.25, 0.96),
                                         (0.08, 0.28, 0.98), (0.55, 0.22, 0.97)])
        case .bw: return Self.cover([(0.00, 0.00, 0.08), (0.00, 0.00, 0.35), (0.00, 0.00, 0.60),
                                     (0.00, 0.00, 0.85), (0.00, 0.00, 0.98)])
        }
    }

    /// A 3×3 artwork grid (hue, saturation, brightness) whose sampled cells are `stops`.
    private static func cover(_ stops: [(Double, Double, Double)]) -> AlbumWallpaperPalette {
        var grid = [SIMD3<Double>](repeating: SIMD3(0, 0, 0.5), count: 9)
        for (index, stop) in zip(AlbumWallpaperPalette.sampleIndices, stops) { grid[index] = SIMD3(stop.0, stop.1, stop.2) }
        return AlbumWallpaperPalette(grid: grid)
    }
}

/// What to render, from the LUNALYR_RENDER_* environment that
/// Scripts/render-visualiser.sh hands the test (through TEST_RUNNER_ names).
struct VisualiserRenderRequest {
    static let prefix = "LUNALYR_RENDER_"

    var output: URL
    var scenes: [AudioVisualizerStyle]
    var fixtures: [SyntheticMusic.Fixture]
    var settings = VisualiserRenderSettings()
    /// Writes every Nth frame as a PNG; the contact sheet and log cover all.
    var every = 1
    /// Frames on the contact sheet.
    var tiles = 16

    struct Invalid: Error, CustomStringConvertible {
        let description: String
    }

    /// Every scene the native renderer draws (the WebKit ones are separate).
    static var nativeScenes: [AudioVisualizerStyle] { AudioVisualizerStyle.allCases.filter { $0.fragment != nil } }

    /// nil when LUNALYR_RENDER_DIR is unset, which is how the normal test run skips.
    init?(environment: [String: String]) throws {
        func value(_ name: String) -> String? {
            // xcodebuild strips the TEST_RUNNER_ prefix; a runner that keeps it works too.
            let raw = environment[Self.prefix + name] ?? environment["TEST_RUNNER_" + Self.prefix + name]
            let trimmed = raw?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            return trimmed.isEmpty ? nil : trimmed
        }
        func number(_ name: String, _ range: ClosedRange<Double>) throws -> Double? {
            guard let text = value(name) else { return nil }
            guard let number = Double(text), range.contains(number) else {
                throw Invalid(description: "\(Self.prefix)\(name)=\(text) must be a number in \(range)")
            }
            return number
        }
        func flag(_ name: String) throws -> Bool? {
            guard let text = value(name)?.lowercased() else { return nil }
            if ["1", "yes", "true", "on"].contains(text) { return true }
            if ["0", "no", "false", "off"].contains(text) { return false }
            throw Invalid(description: "\(Self.prefix)\(name)=\(text) must be 1 or 0")
        }
        guard let directory = value("DIR") else { return nil }
        // The test host has its own working directory and a temporary home
        // (CFFIXED_USER_HOME) that is deleted when the tests end: a relative
        // or ~ path would land there and the renders would vanish.
        guard directory.hasPrefix("/") else {
            throw Invalid(description: "\(Self.prefix)DIR=\(directory) must be an absolute path: the test host runs "
                + "in its own directory with a temporary home folder, so relative and ~ paths would be lost "
                + "(Scripts/render-visualiser.sh passes an absolute one)")
        }
        output = URL(fileURLWithPath: directory, isDirectory: true).standardizedFileURL
        scenes = try Self.scenes(value("SCENE") ?? "all")
        fixtures = try Self.fixtures(value("FIXTURE") ?? "all")
        if let size = value("SIZE") { (settings.width, settings.height) = try Self.size(size) }
        settings.seconds = try number("SECONDS", 0.05...600) ?? settings.seconds
        settings.fps = try number("FPS", 1...240) ?? settings.fps
        settings.preroll = try number("PREROLL", 0...60) ?? settings.preroll
        settings.start = try number("START", 0...86_400) ?? settings.start
        settings.scrim = try number("SCRIM", 0...2) ?? settings.scrim
        if let text = value("TEXT_BACKING")?.lowercased() {
            switch text {
            case "auto", "automatic": settings.textBacking = nil
            case "off", "none", "no": settings.textBacking = 0
            default:
                guard let amount = Double(text), (0...1).contains(amount) else {
                    throw Invalid(description: "\(Self.prefix)TEXT_BACKING=\(text) must be auto, off or a number in 0...1")
                }
                settings.textBacking = amount
            }
        }
        settings.sensitivity = try number("SENSITIVITY", 0.3...3) ?? settings.sensitivity
        settings.punch = try number("PUNCH", 0...2) ?? settings.punch
        settings.vividness = try number("VIVIDNESS", 0.4...1.8) ?? settings.vividness
        settings.speed = try number("SPEED", 0...4) ?? settings.speed
        settings.reduceMotion = try flag("REDUCE_MOTION") ?? false
        settings.dimFlashingLights = try flag("DIM_FLASHING") ?? false
        settings.fullResolution = try flag("FULL_RESOLUTION") ?? false
        if let name = value("PALETTE") {
            guard let palette = RenderPalette(rawValue: name.lowercased()) else {
                throw Invalid(description: "\(Self.prefix)PALETTE=\(name) is not one of "
                    + RenderPalette.allCases.map(\.rawValue).joined(separator: ", "))
            }
            settings.palette = palette
        }
        every = Int(try number("EVERY", 1...10_000) ?? 1)
        tiles = Int(try number("TILES", 1...64) ?? 16)
    }

    /// "all", or a comma-separated list of scene ids (or titles, in any case).
    static func scenes(_ list: String) throws -> [AudioVisualizerStyle] {
        if list.lowercased() == "all" { return nativeScenes }
        func key(_ text: String) -> String { text.lowercased().filter { $0.isLetter || $0.isNumber } }
        return try names(list).map { name in
            guard let style = AudioVisualizerStyle.allCases.first(where: { key($0.rawValue) == key(name) || key($0.title) == key(name) }) else {
                throw Invalid(description: "Unknown scene \"\(name)\". Native scenes: "
                    + nativeScenes.map(\.rawValue).joined(separator: ", ") + ", or all")
            }
            guard style.fragment != nil else {
                throw Invalid(description: "\(style.rawValue) is a WebKit scene; this harness renders the native ones: "
                    + nativeScenes.map(\.rawValue).joined(separator: ", "))
            }
            return style
        }
    }

    /// "all", or a comma-separated list of fixture names.
    static func fixtures(_ list: String) throws -> [SyntheticMusic.Fixture] {
        if list.lowercased() == "all" { return SyntheticMusic.Fixture.allCases }
        return try names(list).map { name in
            guard let fixture = SyntheticMusic.Fixture(name: name) else {
                throw Invalid(description: "Unknown fixture \"\(name)\". Fixtures: "
                    + SyntheticMusic.Fixture.allCases.map(\.rawValue).joined(separator: ", ") + ", or all")
            }
            return fixture
        }
    }

    /// "1600x900" (or 1600×900).
    static func size(_ text: String) throws -> (Int, Int) {
        let parts = text.lowercased().replacingOccurrences(of: "×", with: "x").split(separator: "x")
        guard parts.count == 2, let width = Int(parts[0]), let height = Int(parts[1]),
              (16...8192).contains(width), (16...8192).contains(height) else {
            throw Invalid(description: "\(prefix)SIZE=\(text) must look like 1600x900 (16–8192 pixels a side)")
        }
        return (width, height)
    }

    private static func names(_ list: String) throws -> [String] {
        var seen = Set<String>()
        let names = list.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && seen.insert($0.lowercased()).inserted }
        guard !names.isEmpty else { throw Invalid(description: "Nothing to render in \"\(list)\"") }
        return names
    }
}

/// Feeds a VisualizerAudio as AudioPCMProcessor's worker does: each channel,
/// in pieces that end on the analyser's hop, stamped with the simulated
/// capture time of the piece's last sample (sample k plays at k / 48 000 s).
/// Capture calls the overload without a time, which is this one stamped now.
struct PCMFeeder {
    let music: SyntheticMusic.Stereo
    let audio: VisualizerAudio
    /// 2 as from a stereo tap; 1 feeds the left channel alone, as a mono source.
    let channels: Int
    private(set) var fed = 0

    init(music: SyntheticMusic.Stereo, into audio: VisualizerAudio, channels: Int = 2) {
        self.music = music
        self.audio = audio
        self.channels = min(2, max(1, channels))
        audio.setChannelCount(self.channels)
    }

    /// Appends every sample captured by `time`.
    mutating func feed(through time: Double) {
        let rate = SyntheticMusic.sampleRate
        let end = min(music.count, Int((time * rate).rounded(.down)) + 1)
        let hop = AudioPCMProcessor.analyserHop
        while fed < end {
            let piece = min(end - fed, hop - fed % hop)
            let captured = Double(fed + piece - 1) / rate
            music.left.withUnsafeBufferPointer {
                audio.append($0.baseAddress! + fed, count: piece, sampleRate: rate, channel: 0, hostTime: captured)
            }
            if channels > 1 {
                music.right.withUnsafeBufferPointer {
                    audio.append($0.baseAddress! + fed, count: piece, sampleRate: rate, channel: 1, hostTime: captured)
                }
            }
            fed += piece
        }
    }
}

/// Stems rendered once and shared by the fixtures that contain them.
final class StemCache {
    private var stems: [SyntheticMusic.Voice: SyntheticMusic.Stereo] = [:]

    func music(_ fixture: SyntheticMusic.Fixture, seconds: Double) -> SyntheticMusic.Stereo {
        SyntheticMusic.render(fixture, seconds: seconds) { voice, count in
            if let stem = stems[voice], stem.count == count { return stem }
            let stem = SyntheticMusic.render(voice, count: count)
            stems[voice] = stem
            return stem
        }
    }
}

/// One captured frame and everything that produced it.
struct RenderedFrame {
    /// The captured frame's number, from 0; pre-roll frames are not counted.
    let index: Int
    /// Simulated seconds since the music started.
    let time: Double
    /// What the production renderer drew, at the drawable size.
    let image: BGRAImage
    /// Uniforms, album colours and Wavefield modes as bound for the draw.
    let inputs: AudioVisualizerView.SceneInputs
    /// The smoothed sound the frame drew from (bands and waveform as bound).
    let sound: AudioSnapshot
    /// The analyser's reading before smoothing.
    let analyser: AudioSnapshot
    let gpuMilliseconds: Double
}

@MainActor
final class VisualiserRenderer {
    let gpu: VisualizerGPU.Context

    /// The context every wallpaper surface uses (the integrated GPU when there is one).
    init() throws {
        guard let gpu = VisualizerGPU.shared.context(for: nil) else {
            try GPUTestSupport.unavailable("This test host has no Metal device; visualiser frames are unverified here.")
        }
        self.gpu = gpu
    }

    /// The scene's pipeline from the shared cache, once its asynchronous build lands.
    func pipeline(for scene: AudioVisualizerStyle) async throws -> MTLRenderPipelineState {
        let fragment = try XCTUnwrap(scene.fragment, "\(scene.rawValue) has no native fragment")
        let gpu = self.gpu
        let state = try await GPUTestSupport.eventually(timeout: 60, { gpu.pipeline(fragment) }, until: { $0 != nil })
        return try XCTUnwrap(state, "The \(fragment) pipeline did not build")
    }

    /// Simulates `settings.preroll` seconds of playback and then captures
    /// `settings.seconds`, one frame every 1 / fps seconds, handing each
    /// captured frame to `body`.
    func render(_ scene: AudioVisualizerStyle, music: SyntheticMusic.Stereo, settings: VisualiserRenderSettings,
                frame body: (RenderedFrame) throws -> Void) async throws {
        let pipeline = try await pipeline(for: scene)
        if settings.response.calm {
            let gpu = self.gpu
            _ = try await GPUTestSupport.eventually(timeout: 60, { gpu.exposureLimiter }, until: { $0 != nil })
        }
        try renderFrames(scene, pipeline: pipeline, music: music, settings: settings, frame: body)
    }

    /// The frame loop: synchronous, like the wallpaper's scheduled frame.
    private func renderFrames(_ scene: AudioVisualizerStyle, pipeline: MTLRenderPipelineState,
                              music: SyntheticMusic.Stereo, settings: VisualiserRenderSettings,
                              frame body: (RenderedFrame) throws -> Void) throws {
        let response = settings.response
        // A test VisualizerAudio measures no output device: no latency, no capture.
        let audio = VisualizerAudio()
        var feeder = PCMFeeder(music: music, into: audio)
        let coordinator = AudioVisualizerView.Coordinator(preview: false)
        let timelines = VisualizerTimelines()
        let clock = VisualClock(), rate = settings.speed * VisualClock.visualizerRate
        Self.run(clock, for: settings.start, rate: rate)
        let drawable = settings.drawableSize(for: scene)
        let target = try OffscreenTarget(device: gpu.device, size: drawable)
        let palette = settings.palette.palette
        let total = settings.prerollFrames + settings.capturedFrames
        for step in 0..<total {
            let time = Double(step) / settings.fps
            feeder.feed(through: time)
            let heard = audio.latest(at: time - audio.visualDelay)
            let source = clock.advance(to: time, rate: rate)
            coordinator.prepare(style: scene, time: source, response: response, palette: palette,
                                vividness: settings.vividness, sound: heard, at: time, timelines: timelines)
            let command = try XCTUnwrap(gpu.queue.makeCommandBuffer())
            let inputs = try XCTUnwrap(coordinator.encodeFrame(pipeline, on: gpu, into: command, pass: target.pass,
                size: drawable, sensitivity: settings.sensitivity, at: time), "\(scene.rawValue) could not encode a frame")
            let capturing = step >= settings.prerollFrames
            if capturing { target.synchronize(in: command) }
            command.commit()
            command.waitUntilCompleted()
            guard command.status == .completed else {
                throw VisualiserRenderRequest.Invalid(description: "\(scene.rawValue) frame \(step) did not complete: "
                    + (command.error?.localizedDescription ?? "status \(command.status.rawValue)"))
            }
            guard capturing else { continue }
            try body(RenderedFrame(index: step - settings.prerollFrames, time: time, image: target.read(),
                inputs: inputs, sound: coordinator.preparedSound, analyser: heard,
                gpuMilliseconds: max(0, command.gpuEndTime - command.gpuStartTime) * 1000))
        }
    }

    /// Runs `clock` through the `seconds` before time 0, as the shared flow
    /// clock runs while the wallpaper shows, in steps under its 0.25 s clamp.
    static func run(_ clock: VisualClock, for seconds: Double, rate: Double) {
        guard seconds > 0 else { return }
        let steps = Int((seconds / 0.125).rounded(.up))
        for step in 0..<steps {
            _ = clock.advance(to: -seconds + Double(step) * seconds / Double(steps), rate: rate)
        }
    }
}

/// A BGRA8 texture standing in for the wallpaper's drawable.
final class OffscreenTarget {
    let texture: MTLTexture
    let pass = MTLRenderPassDescriptor()
    let width: Int, height: Int
    private let managed: Bool

    init(device: MTLDevice, size: CGSize) throws {
        width = max(1, Int(size.width)); height = max(1, Int(size.height))
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm,
            width: width, height: height, mipmapped: false)
        descriptor.usage = [.renderTarget]
        managed = !device.hasUnifiedMemory
        descriptor.storageMode = managed ? .managed : .shared
        texture = try XCTUnwrap(device.makeTexture(descriptor: descriptor))
        // As an MTKView's current pass: cleared to opaque black, then stored.
        pass.colorAttachments[0].texture = texture
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 1)
        pass.colorAttachments[0].storeAction = .store
    }

    /// Makes the frame readable by the CPU on a GPU with its own memory.
    func synchronize(in command: MTLCommandBuffer) {
        guard managed, let blit = command.makeBlitCommandEncoder() else { return }
        blit.synchronize(resource: texture)
        blit.endEncoding()
    }

    func read() -> BGRAImage {
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        pixels.withUnsafeMutableBytes {
            texture.getBytes($0.baseAddress!, bytesPerRow: width * 4,
                from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
        }
        return BGRAImage(width: width, height: height, pixels: pixels)
    }
}
