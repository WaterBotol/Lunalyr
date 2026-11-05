import MetalKit
import QuartzCore
import SwiftUI

enum AudioVisualizerStyle: String, CaseIterable, Identifiable {
    case silk, aurora, tunnel, curtainAurora, fosforaAurora, wavefield, wavefieldSphere, wavefieldOriginal, wavecore, light3D, starTrails, colourStrata, overprint
    var id: String { rawValue }
    var isWavefield: Bool { self == .wavefield || self == .wavefieldSphere }
    var title: String {
        switch self {
        case .silk: return "Silk Ribbons"
        case .aurora: return "Spectral Aurora"
        case .tunnel: return "Spectrum Tunnel"
        case .curtainAurora: return "Aurora Curtains"
        case .fosforaAurora: return "Fosfora Aurora"
        case .wavefield: return "Wavefield"
        case .wavefieldSphere: return "Wavefield Sphere"
        case .wavefieldOriginal: return "Wavefield Original"
        case .wavecore: return "Wavecore"
        case .light3D: return "Light 3D (Cymatic)"
        case .starTrails: return "Star Trails"
        case .colourStrata: return "Colour Strata"
        case .overprint: return "Overprint"
        }
    }
    var detail: String {
        switch self {
        case .silk: return "Quiet folds of coloured light, shaped by the music."
        case .aurora: return "A flowing sky with a ribbon for each musical register."
        case .tunnel: return "An immersive passage carved from the live spectrum."
        case .curtainAurora: return "Bass on the left, treble on the right. Each curtain’s sections bend, widen and glow with their own frequencies."
        case .fosforaAurora: return "Seven luminous frequency ribbons, adapted from Fosfora’s open-source Aurora."
        case .wavefield: return "Live spectral peaks redraw Wavefield’s heatmap interference patterns."
        case .wavefieldSphere: return "Wavefield’s interference patterns wrap around a three-dimensional sphere."
        case .wavefieldOriginal: return "Wavefield’s original engine and presets, driven by system audio. Some presets flash with the beat; Reduce Motion or Dim Flashing Lights calms them."
        case .wavecore: return "The original Wavecore scene with a luminous core and spectral particles."
        case .light3D: return "Cymatic’s original rotating cloud of light, with bass-driven expansion and treble-driven spin."
        case .starTrails: return "Two craft weave neon trails through a nebula in the album's colours (or the original violet). Kicks, snares, hi-hats, bass, voice and pads each light their own stars."
        case .colourStrata: return "Soft album-coloured layers. Bass widens the lower field, mids bend the middle, and highs soften the upper edge."
        case .overprint: return "Overlapping matte ink in album colours. Bass swells a plate, mids shift the overlap, and highs bring out fine dots."
        }
    }
    var webEngine: WebVisualizerEngine? {
        switch self {
        case .wavefieldOriginal: return .wavefield
        case .wavecore: return .wavecore
        case .light3D: return .light3D
        default: return nil
        }
    }
    var fragment: String? {
        switch self {
        case .tunnel: return "tunnelFragment"
        case .starTrails: return "starTrailsFragment"
        case .colourStrata: return "colourStrataFragment"
        case .overprint: return "overprintFragment"
        case .wavefieldOriginal, .wavecore, .light3D: return nil
        case .curtainAurora: return "curtainAuroraFragment"
        case .fosforaAurora: return "fosforaAuroraFragment"
        case .wavefield, .wavefieldSphere: return "wavefieldFragment"
        default: return "auroraFragment"
        }
    }
}

/// The imported scenes are the entire wallpaper. They render directly into
/// one Metal surface; nothing reactive is overlaid on an ambient effect.
/// Lunalyr's existing scheduled frame drives this view, so it shares the
/// wallpaper's frame limit and sleep/occlusion pause.
@MainActor
struct AudioVisualizerView: NSViewRepresentable {
    let style: AudioVisualizerStyle
    let time: Double
    var paused = false
    var preview = false
    var palette: AlbumWallpaperPalette?
    /// Colour richness for the shader, or nil for the Colour richness
    /// setting. A stand-in inside WebVisualizerView passes 1: NowPlayingView
    /// already applies richness to the whole web layer.
    var vividness: Double?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func makeCoordinator() -> Coordinator { Coordinator(preview: preview) }
    func makeNSView(context: Context) -> MTKView {
        let view = MTKView(frame: .zero, device: context.coordinator.device)
        view.colorPixelFormat = .bgra8Unorm
        view.framebufferOnly = true
        view.autoResizeDrawable = false
        view.enableSetNeedsDisplay = false
        view.isPaused = true // Draw only from the containing scheduled frame.
        view.delegate = context.coordinator
        return view
    }
    func updateNSView(_ view: MTKView, context: Context) {
        context.coordinator.prepare(style: style, time: time,
            response: .current(reduceMotion: reduceMotion), palette: palette, vividness: vividness)
        if !paused { view.draw() }
    }
    static func dismantleNSView(_ view: MTKView, coordinator: Coordinator) {
        view.isPaused = true; view.delegate = nil
    }

    /// Everything one scene binds for a frame. It is computed once, so the
    /// photosensitivity probe and the full-size pass see the same modal state.
    struct SceneInputs {
        var uniforms: VizUniforms
        var field: WavefieldFrame?
        var bands: [Float]
        var waveform: [Float]
        var colours: [SIMD4<Float>]
        /// Star Trails' simulation state (fragment buffer 6; nil for other scenes).
        var starTrails: StarTrailsBinding?
    }
    /// Where Star Trails' state comes from: a surface's ring buffer, or bytes for a static frame.
    enum StarTrailsBinding {
        case buffer(MTLBuffer)
        case bytes([SIMD4<Float>])
    }

    @MainActor
    final class Coordinator: NSObject, MTKViewDelegate {
        /// The shared device, queue and pipelines for this surface's GPU.
        private var gpu: VisualizerGPU.Context?
        private var probe: ExposureProbe?
        private var displayID: CGDirectDisplayID?
        private let preview: Bool
        private let inFlight = DispatchSemaphore(value: 2)
        private let gpuTimer = GPUFrameTimer()
        private var ladder = RenderScaleLadder()
        private var smoothing = VisualizerSmoother()
        /// What this surface's scene is shown of the sound types (idle hides them).
        private var soundGate = SoundTypeIdleGate()
        private var wavefieldModes = WavefieldModes()
        /// Star Trails' shared simulation (from the timelines) and this surface's ring of
        /// state buffers, one per frame in flight plus one, so the CPU never writes a
        /// buffer the GPU is still reading.
        private var starTrailsSimulations: StarTrailsSimulations?
        private var starTrailsBuffers: [MTLBuffer] = []
        private var starTrailsNext = 0
        private var style: AudioVisualizerStyle = .silk
        private var hasStyle = false
        private var sceneTime = 0.0
        private var response = VisualizerResponse()
        private var frame = AudioSnapshot()
        private var palette: AlbumWallpaperPalette?
        private var vividness: Double?
        private static let uncolouredStops = [SIMD4<Float>](repeating: .zero, count: 5)
        private var lastAudibleTime = -Double.infinity
        /// Whether this surface has shown a frame, and whether the last one
        /// went through the limiter. A limiter starting on a surface that
        /// is already showing keeps its brightness. A new surface for a scene
        /// that is on screen (the copy a crossfade fades out) starts at the
        /// level the scene is shown at; one bringing a scene on screen fades in.
        private var hasDrawn = false, limiting = false
        /// The scene the limiter last measured, which `ExposureMemory` files
        /// the probe's level under (the style may have changed since).
        private var limitedStyle: AudioVisualizerStyle?
        /// When the display's GPU was last checked; automatic graphics
        /// switching can move a display between GPUs without changing its ID.
        private var deviceCheckedAt = -Double.infinity
        nonisolated static let deviceCheckInterval = 2.0

        init(preview: Bool = false) {
            self.preview = preview
            super.init()
            gpu = VisualizerGPU.shared.context(for: nil)
        }

        var device: MTLDevice? { gpu?.device }
        /// Colour richness applied in the shader: the override a stand-in
        /// passes, otherwise the setting.
        var shaderVividness: Double { vividness ?? Settings.shared.vividness }

        func prepare(style: AudioVisualizerStyle, time: Double, response: VisualizerResponse,
                     palette: AlbumWallpaperPalette?, vividness: Double? = nil) {
            let clock = CACurrentMediaTime()
            prepare(style: style, time: time, response: response, palette: palette, vividness: vividness,
                    sound: VisualizerAudio.shared.latest(), at: clock, timelines: .shared)
        }
        /// `sound` is what the listener hears at `clock` (`CACurrentMediaTime`
        /// seconds). The render harness (Tests/Swift/Support) passes its own
        /// analyser's reading, a simulated clock and its own timelines.
        func prepare(style: AudioVisualizerStyle, time: Double, response: VisualizerResponse,
                     palette: AlbumWallpaperPalette?, vividness: Double?, sound: AudioSnapshot,
                     at clock: Double, timelines: VisualizerTimelines) {
            self.vividness = vividness
            // A surface's first frame is not a break: it may be the copy of
            // the outgoing scene that fades out while another surface keeps
            // the same timeline running.
            let settled = hasStyle
            let rebuilt = settled && self.style != style
            if self.style.isWavefield, !style.isWavefield { wavefieldModes = WavefieldModes() }
            let newPalette = settled && palette != self.palette
            self.style = style; hasStyle = true
            self.response = response
            self.palette = palette
            if style == .starTrails { starTrailsSimulations = timelines.starTrails }
            var target = sound
            // The captured RMS is scaled by 2.4. Keep the live source through
            // short rests, even if capture continues delivering silence.
            let recentlyAudible = clock - lastAudibleTime < 0.6
            let threshold: Float = recentlyAudible ? 0.0006 : 0.0012
            let audible = target.wavefield.rms >= threshold * 2.4
            let returned = settled && audible && !recentlyAudible
            // Music returning after a rest, a new scene and a new cover are
            // moments where the scene clock can restart unnoticed.
            sceneTime = timelines.time(for: style, source: time, rate: response.motionScale,
                wallClock: clock, atBreak: rebuilt || newPalette || returned)
            if audible {
                lastAudibleTime = clock
            } else if !recentlyAudible {
                var idle = style.isWavefield ? AudioSnapshot() : Self.ambientSpectrum(at: sceneTime)
                idle.sound = target.sound
                target = idle
            }
            // While idle nothing new flares and the counters hold; the
            // sustained classes fade out and back in over a quarter second.
            target.sound = soundGate.frame(target.sound, live: audible || recentlyAudible, at: clock)
            frame = smoothing.frame(target, at: clock)
        }
        /// The smoothed sound the next frame draws (read by the render harness).
        var preparedSound: AudioSnapshot { frame }
        /// A quiet, slowly changing spectrum for the wallpaper renderer.
        /// Leave capture, meters, and beat detection on the real audio.
        static func ambientSpectrum(at time: Double) -> AudioSnapshot {
            var ambient = AudioSnapshot()
            for band in ambient.bands.indices {
                let x = Double(band) / Double(AudioSnapshot.bandCount - 1)
                ambient.bands[band] = Float(0.16 + 0.055 * sin(7 * x + 0.24 * time)
                    + 0.025 * cos(13 * x - 0.17 * time))
            }
            let bandsPerComponent = AudioSnapshot.bandCount / AudioSnapshot.componentCount
            for component in ambient.components.indices {
                let start = component * bandsPerComponent
                ambient.components[component] = ambient.bands[start..<(start + bandsPerComponent)]
                    .reduce(0, +) / Float(bandsPerComponent)
            }
            ambient.bass = ambient.components[1]
            ambient.mid = ambient.components[3]
            ambient.treble = ambient.components[6]
            ambient.level = 0.12
            ambient.centroid = 0.45
            return ambient
        }

        nonisolated func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {}
        nonisolated func draw(in view: MTKView) {
            // The view is permanently paused and only updateNSView calls
            // draw(), synchronously on the main actor.
            MainActor.assumeIsolated { drawFrame(in: view) }
        }
        /// Follows the surface to the GPU that drives its display, when the
        /// window moves to another display and every couple of seconds, since
        /// a dual-GPU MacBook Pro can move a display to the other GPU.
        private func followDisplay(of view: MTKView, at now: Double) {
            guard let id = view.window?.screen?.versoDisplayID,
                  Self.shouldCheckDevice(display: id, current: displayID, now: now, checkedAt: deviceCheckedAt) else { return }
            displayID = id
            deviceCheckedAt = now
            guard let context = VisualizerGPU.shared.context(for: id),
                  context.device.registryID != gpu?.device.registryID else { return }
            gpu = context
            view.device = context.device
            probe = nil
            limiting = false
        }
        nonisolated static func shouldCheckDevice(display: CGDirectDisplayID, current: CGDirectDisplayID?,
                                                  now: Double, checkedAt: Double) -> Bool {
            display != current || now - checkedAt >= deviceCheckInterval || now < checkedAt
        }
        private func drawFrame(in view: MTKView) {
            guard view.bounds.width > 0, view.bounds.height > 0 else { return }
            followDisplay(of: view, at: CACurrentMediaTime())
            guard let gpu, inFlight.wait(timeout: .now()) == .success else { return }
            guard let fragment = style.fragment, let pipeline = gpu.pipeline(fragment) else {
                inFlight.signal(); return
            }
            let fps = preview ? 15 : Double(Settings.shared.effectiveAnimationRate.rawValue)
            if let average = gpuTimer.drain(minimumSamples: 30) {
                ladder.adjust(averageGPUTime: average, budget: 1 / fps)
            }
            let backing = view.window?.backingScaleFactor ?? 2
            let native = CGSize(width: view.bounds.width * backing, height: view.bounds.height * backing)
            let wanted = Self.drawableSize(native: native, style: style, preview: preview, ladderScale: ladder.scale)
            if view.drawableSize != wanted { view.drawableSize = wanted }
            guard let pass = view.currentRenderPassDescriptor, let drawable = view.currentDrawable,
                  let command = gpu.queue.makeCommandBuffer() else {
                inFlight.signal(); return
            }
            guard encodeFrame(pipeline, on: gpu, into: command, pass: pass, size: wanted,
                              sensitivity: Settings.shared.audioSensitivity, at: CACurrentMediaTime()) != nil else {
                inFlight.signal(); return
            }
            let timer = gpuTimer, semaphore = inFlight
            command.addCompletedHandler { buffer in
                timer.record(buffer.gpuEndTime - buffer.gpuStartTime)
                semaphore.signal()
            }
            command.present(drawable); command.commit()
        }
        /// The drawable for a surface of `native` pixels. Soft wallpaper
        /// fields do not need retina-sized offscreen surfaces (text keeps
        /// native resolution in its separate view), so each scene has a pixel
        /// budget, which the ladder trims while the GPU falls behind.
        nonisolated static func drawableSize(native: CGSize, style: AudioVisualizerStyle, preview: Bool,
                                             ladderScale: CGFloat = 1) -> CGSize {
            let cap = preview ? 220_000.0 : (style.isWavefield || style == .tunnel ? 950_000.0 : 1_800_000.0)
            let budgetScale = min(1, sqrt(cap / max(1, native.width * native.height)))
            let scale = budgetScale * ladderScale
            return CGSize(width: max(1, floor(native.width * scale)), height: max(1, floor(native.height * scale)))
        }
        /// Encodes the prepared frame into `pass`, after the limiter's probe
        /// while calm, and returns what it bound (nil if it could not encode).
        /// drawFrame passes the window's drawable; the render harness
        /// (Tests/Swift/Support) passes an offscreen texture and a simulated clock.
        @discardableResult
        func encodeFrame(_ pipeline: MTLRenderPipelineState, on gpu: VisualizerGPU.Context,
                         into command: MTLCommandBuffer, pass: MTLRenderPassDescriptor, size: CGSize,
                         sensitivity: Double, at now: Double) -> SceneInputs? {
            let uniforms = Self.uniforms(style: style, size: size, time: sceneTime, frame: frame,
                sensitivity: sensitivity,
                speed: 1, reduced: response.calm, vividness: shaderVividness, // The containing clock already applies Speed.
                punch: response.punch)
            // Star Trails takes the album colours into its own sky (StarTrailsSky), not this tint.
            let inputs = Self.sceneInputs(frame, style: style, uniforms: uniforms, wavefield: &wavefieldModes,
                at: now, response: response, palette: style == .starTrails ? nil : palette,
                starTrails: style == .starTrails ? starTrailsState(on: gpu.device, size: size, sensitivity: sensitivity, at: now) : nil)
            // File the level the limiter last showed, under the scene it
            // measured, for a surface that takes the scene over.
            if limiting, let probe, let limitedStyle {
                Self.remember(probe, as: memoryKey(for: limitedStyle), in: .shared, at: now)
            }
            // Reduce Motion and Dim Flashing Lights cap how fast the mean
            // brightness may rise; the GPU measures this frame first.
            var exposure: MTLBuffer?
            if response.calm, let limiter = gpu.exposureLimiter, let probe = probe(on: gpu.device, at: now) {
                if !limiting, hasDrawn { probe.adoptCurrentBrightness() }
                if probe.encode(into: command, pipeline: pipeline, limiter: limiter, at: now,
                                bind: { Self.bind(inputs, to: $0) }) {
                    exposure = probe.state
                }
            }
            limiting = exposure != nil
            limitedStyle = limiting ? style : nil
            guard let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { return nil }
            encoder.setRenderPipelineState(pipeline)
            Self.bind(inputs, to: encoder)
            Self.bindExposure(exposure, to: encoder)
            encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
            encoder.endEncoding()
            hasDrawn = true
            return inputs
        }
        /// Advances the shared Star Trails simulation to this frame (once per scene time,
        /// however many displays draw it) and packs it into the next ring buffer.
        private func starTrailsState(on device: MTLDevice, size: CGSize, sensitivity: Double,
                                     at now: Double) -> StarTrailsBinding? {
            guard let simulations = starTrailsSimulations else { return nil }
            let simulation = simulations.advance(sceneTime: sceneTime, clock: now, frame: frame,
                                                 response: response, sensitivity: sensitivity,
                                                 palette: VisualizerPreferences.shared.starTrailsColours == .album ? palette : nil)
            if starTrailsBuffers.first?.device.registryID != device.registryID {
                starTrailsBuffers = (0..<3).compactMap {
                    _ in device.makeBuffer(length: StarTrailsLayout.byteCount, options: .storageModeShared)
                }
                starTrailsNext = 0
            }
            guard !starTrailsBuffers.isEmpty else { return nil }
            let buffer = starTrailsBuffers[starTrailsNext % starTrailsBuffers.count]
            starTrailsNext = (starTrailsNext + 1) % starTrailsBuffers.count
            let state = buffer.contents().bindMemory(to: SIMD4<Float>.self, capacity: StarTrailsLayout.float4Count)
            simulation.pack(aspect: size.width / max(1, size.height), into: state)
            return .buffer(buffer)
        }
        private func probe(on device: MTLDevice, at now: Double) -> ExposureProbe? {
            if let probe, probe.device.registryID == device.registryID { return probe }
            probe = Self.makeProbe(on: device, for: memoryKey(for: style), in: .shared, at: now)
            return probe
        }
        private func memoryKey(for style: AudioVisualizerStyle) -> ExposureMemory.Key {
            .init(style: style, display: displayID, preview: preview)
        }
        /// A probe for a surface showing `key`'s scene: at the level the scene
        /// is shown at while it is on screen, otherwise fading in from black.
        static func makeProbe(on device: MTLDevice, for key: ExposureMemory.Key,
                              in memory: ExposureMemory, at now: Double) -> ExposureProbe? {
            ExposureProbe(device: device, shown: memory.shown(for: key, at: now) ?? 0)
        }
        static func remember(_ probe: ExposureProbe, as key: ExposureMemory.Key,
                             in memory: ExposureMemory, at now: Double) {
            memory.record(probe.shownLuminance, for: key, at: now)
        }

        /// Computes the scene's per-frame inputs, advancing the Wavefield
        /// modal bank exactly once.
        static func sceneInputs(_ frame: AudioSnapshot, style: AudioVisualizerStyle, uniforms: VizUniforms,
                                wavefield: inout WavefieldModes, at clock: Double, response: VisualizerResponse,
                                palette: AlbumWallpaperPalette?, starTrails: StarTrailsBinding? = nil) -> SceneInputs {
            var uniforms = uniforms
            var field: WavefieldFrame?
            if style.isWavefield {
                let next = wavefield.frame(frame, at: clock, time: uniforms.time, sensitivity: uniforms.sensitivity,
                    reduced: response.calm, transient: response.transientScale,
                    motion: Float(response.motionScale), patternRate: response.patternRate)
                next.apply(to: &uniforms)
                field = next
            }
            let colours = palette?.stops.map { SIMD4($0.x, $0.y, $0.z, uniforms.p15) } ?? uncolouredStops
            var state = starTrails
            if style == .starTrails, case .none = state {
                // A static frame: a fresh simulation run up to the frame's time.
                state = .bytes(StarTrailsSimulation.snapshot(frame: frame, time: Double(uniforms.time),
                    sensitivity: Double(uniforms.sensitivity), calm: response.calm,
                    aspect: Double(uniforms.resolution.x / max(1, uniforms.resolution.y)), palette: palette))
            }
            return SceneInputs(uniforms: uniforms, field: field, bands: frame.bands, waveform: frame.waveform,
                               colours: colours, starTrails: state)
        }
        static func bind(_ inputs: SceneInputs, to encoder: MTLRenderCommandEncoder) {
            var uniforms = inputs.uniforms
            if let field = inputs.field {
                field.bindModes(to: encoder)
            } else {
                inputs.bands.withUnsafeBytes { encoder.setFragmentBytes($0.baseAddress!, length: $0.count, index: 1) }
                inputs.waveform.withUnsafeBytes { encoder.setFragmentBytes($0.baseAddress!, length: $0.count, index: 2) }
            }
            encoder.setFragmentBytes(&uniforms, length: MemoryLayout<VizUniforms>.stride, index: 0)
            inputs.colours.withUnsafeBytes { bytes in
                encoder.setFragmentBytes(bytes.baseAddress!, length: bytes.count, index: 4)
            }
            switch inputs.starTrails {
            case .buffer(let buffer)?:
                encoder.setFragmentBuffer(buffer, offset: 0, index: StarTrailsLayout.bufferIndex)
            case .bytes(let state)?:
                state.withUnsafeBytes {
                    encoder.setFragmentBytes($0.baseAddress!, length: $0.count, index: StarTrailsLayout.bufferIndex)
                }
            case nil:
                break
            }
        }
        /// The limiter's gain buffer, or a unit gain when it is off.
        static func bindExposure(_ buffer: MTLBuffer?, to encoder: MTLRenderCommandEncoder) {
            if let buffer {
                encoder.setFragmentBuffer(buffer, offset: 0, index: 5)
            } else {
                var unity: Float = 1
                encoder.setFragmentBytes(&unity, length: MemoryLayout<Float>.stride, index: 5)
            }
        }
        /// One unlimited pass: the scene's inputs at full exposure.
        static func bind(_ frame: AudioSnapshot, to encoder: MTLRenderCommandEncoder,
                         style: AudioVisualizerStyle, uniforms: inout VizUniforms,
                         wavefield: inout WavefieldModes, at clock: Double, reduced: Bool, palette: AlbumWallpaperPalette? = nil) {
            let inputs = sceneInputs(frame, style: style, uniforms: uniforms, wavefield: &wavefield, at: clock,
                response: VisualizerResponse(reduceMotion: reduced), palette: palette)
            uniforms = inputs.uniforms
            bind(inputs, to: encoder)
            bindExposure(nil, to: encoder)
        }
        /// `time` is the scene clock (Reduce Motion's slower rate is already
        /// applied by its timeline). `reduced` means calm: Reduce Motion or
        /// Dim Flashing Lights, which remove beat-driven transients.
        /// `presentationTime` is the displayed time on the capture clock that
        /// the sound types are evaluated at; nil uses the time `frame` was
        /// read at.
        static func uniforms(style: AudioVisualizerStyle, size: CGSize, time: Double, frame: AudioSnapshot,
                             sensitivity: Double, speed: Double, reduced: Bool, vividness: Double = 1,
                             punch: Double = 1, presentationTime: Double? = nil) -> VizUniforms {
            var u = VizUniforms()
            u.resolution = SIMD2(Float(size.width), Float(size.height))
            u.time = Float(time); u.dt = 1 / 30
            u.speed = Float(speed * (style == .tunnel ? 0.22 : 0.34))
            u.sensitivity = Float(min(3, max(0.3, sensitivity)))
            u.p15 = Float(min(1.8, max(0.4, vividness)))
            u.palette = style == .silk ? 3 : 0
            u.bass = frame.bass; u.mid = frame.mid; u.treble = frame.treble; u.level = frame.level
            u.subBass = frame.components[0]; u.lowMid = frame.components[2]
            u.highMid = frame.components[4]; u.presence = frame.components[5]; u.air = frame.components[7]
            u.centroid = frame.centroid
            // Keep musical phrasing while removing camera jolts, flashing
            // stars and beat-wide exposure jumps from desktop defaults. Punch
            // scales what remains (1 is the shipped look); calm removes it.
            let response = VisualizerResponse(reduceMotion: reduced, punch: punch)
            let transient = response.transientScale
            u.beat = frame.beat * 0.20 * transient
            u.trebleBeat = frame.trebleBeat * 0.12 * transient
            u.flux = frame.flux * 0.14 * transient
            u.reduceMotion = reduced ? 1 : 0
            // Sound types, re-read from the onsets at the displayed time so
            // calm gets its own slow flares (`frame.sound` keeps the normal
            // shapes for CPU-side scene code). Kicks are the beat count.
            SoundTypeUniforms(frame.sound, at: presentationTime ?? frame.sound.presentationTime, response: response)
                .apply(to: &u)
            u.beatCount = u.kickOnsets
            if style.isWavefield {
                u.p0 = style == .wavefieldSphere ? 1 : 0
            } else if style == .tunnel {
                u.p0 = 0.38; u.p1 = 0.78; u.p2 = 0.50
                u.p3 = 0; u.p4 = 0.42; u.p5 = 0.72
            } else {
                u.p0 = style == .silk ? 4 : 6
                u.p1 = style == .silk ? 0.16 : 0.20
                u.p2 = 0 // A calm background for lyrics, without flashing stars.
            }
            if style == .fosforaAurora {
                // Each of Fosfora's seven ribbons is its own register. The
                // broad bass (30–250 Hz) and mid (250 Hz–2 kHz) registers
                // contain their neighbours' ranges, so those ribbons moved
                // together; these two read 60–150 Hz and 400–1200 Hz instead.
                u.p3 = frame.components[1]
                u.p4 = frame.components[3]
            }
            return u
        }
    }
}

/// The twelve sound-type values a native scene receives at one displayed
/// time: the last fields of VizUniforms, in order.
struct SoundTypeUniforms: Equatable {
    var kick: Float = 0
    var snare: Float = 0
    var hat: Float = 0
    var bass: Float = 0
    var vocal: Float = 0
    var pad: Float = 0
    var kickOnsets: Float = 0
    var snareOnsets: Float = 0
    var hatOnsets: Float = 0
    var kickAge: Float = 60
    var snareAge: Float = 60
    var hatAge: Float = 60

    /// Counters wrap here, so a float holds every value exactly.
    static let counterWrap: UInt32 = 1 << 24
    /// An age longer than any scene cares about; also "no onset yet".
    static let maximumAge: Float = 60

    /// The transients are re-read from the onset rings at `time`, the way
    /// `response` asks. Punch scales them, clamped to 1. Calm (Reduce Motion
    /// or Dim Flashing Lights) reads slow shapes instead, at half strength at
    /// most: 0.5 × Punch up to Punch 1, so calm never flares harder than
    /// Punch alone would, and Punch Off stays off. The sustained classes are
    /// never scaled. Ages are real seconds.
    init(_ sound: SoundTypeFeatures, at time: Double, response: VisualizerResponse) {
        let shapes: SoundTypeShapes = response.calm ? .calm : .normal
        let gain = Self.transientGain(response)
        func flare(_ shape: SoundOnsetShape, _ ring: SoundOnsetRing) -> Float {
            min(1, max(0, shape.envelope(ring, at: time) * gain))
        }
        func unit(_ value: Float) -> Float { min(1, max(0, value)) }
        func count(_ onsets: UInt32) -> Float { Float(onsets % Self.counterWrap) }
        kick = flare(shapes.kick, sound.kickRing)
        snare = flare(shapes.snare, sound.snareRing)
        hat = flare(shapes.hat, sound.hatRing)
        bass = unit(sound.bass)
        vocal = unit(sound.vocal)
        pad = unit(sound.pad)
        kickOnsets = count(sound.kickOnsets)
        snareOnsets = count(sound.snareOnsets)
        hatOnsets = count(sound.hatOnsets)
        kickAge = Self.age(since: sound.kickTime, at: time)
        snareAge = Self.age(since: sound.snareTime, at: time)
        hatAge = Self.age(since: sound.hatTime, at: time)
    }

    /// What the transient envelopes are multiplied by (see `init`).
    static func transientGain(_ response: VisualizerResponse) -> Float {
        guard response.calm else { return response.transientScale }
        return 0.5 * Float(min(1, VisualizerPreferences.clampedPunch(response.punch)))
    }

    /// Real seconds from `onset` to `time`, 0...`maximumAge` (the maximum
    /// when there was no onset).
    static func age(since onset: Double, at time: Double) -> Float {
        guard onset.isFinite, time.isFinite else { return maximumAge }
        return Float(min(Double(maximumAge), max(0, time - onset)))
    }

    func apply(to u: inout VizUniforms) {
        u.soundKick = kick; u.soundSnare = snare; u.soundHat = hat
        u.soundBass = bass; u.soundVocal = vocal; u.soundPad = pad
        u.kickOnsets = kickOnsets; u.snareOnsets = snareOnsets; u.hatOnsets = hatOnsets
        u.kickAge = kickAge; u.snareAge = snareAge; u.hatAge = hatAge
    }
}
