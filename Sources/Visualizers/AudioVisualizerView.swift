import MetalKit
import QuartzCore
import SwiftUI

enum AudioVisualizerStyle: String, CaseIterable, Identifiable {
    case silk, aurora, tunnel, curtainAurora, fosforaAurora
    var id: String { rawValue }
    var title: String {
        switch self {
        case .silk: return "Silk Ribbons"
        case .aurora: return "Spectral Aurora"
        case .tunnel: return "Spectrum Tunnel"
        case .curtainAurora: return "Aurora Curtains"
        case .fosforaAurora: return "Fosfora Aurora"
        }
    }
    var detail: String {
        switch self {
        case .silk: return "Quiet folds of coloured light, shaped by the music."
        case .aurora: return "A flowing sky with a ribbon for each musical register."
        case .tunnel: return "An immersive passage carved from the live spectrum."
        case .curtainAurora: return "Bass on the left, treble on the right. Each curtain’s sections bend, widen and glow with their own frequencies."
        case .fosforaAurora: return "Seven luminous frequency ribbons, adapted from Fosfora’s open-source Aurora."
        }
    }
    var fragment: String {
        switch self {
        case .tunnel: return "tunnelFragment"
        case .curtainAurora: return "curtainAuroraFragment"
        case .fosforaAurora: return "fosforaAuroraFragment"
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
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func makeCoordinator() -> Coordinator { Coordinator() }
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
        context.coordinator.prepare(style: style, time: time, reduced: reduceMotion)
        if !paused { view.draw() }
    }
    static func dismantleNSView(_ view: MTKView, coordinator: Coordinator) {
        view.isPaused = true; view.delegate = nil
    }

    @MainActor
    final class Coordinator: NSObject, MTKViewDelegate {
        let device = MTLCreateSystemDefaultDevice()
        private var queue: MTLCommandQueue?
        private var library: MTLLibrary?
        private var pipelines: [String: MTLRenderPipelineState] = [:]
        private let inFlight = DispatchSemaphore(value: 2)
        private let gpuTimer = GPUFrameTimer()
        private var ladder = RenderScaleLadder()
        private var smoothing = VisualizerSmoother()
        private var style: AudioVisualizerStyle = .silk
        private var time = 0.0, reduced = false
        private var frame = AudioSnapshot()

        override init() {
            super.init()
            queue = device?.makeCommandQueue()
            library = device?.makeDefaultLibrary()
        }
        func prepare(style: AudioVisualizerStyle, time: Double, reduced: Bool) {
            self.style = style; self.time = time; self.reduced = reduced
            frame = smoothing.frame(VisualizerAudio.shared.latest(), at: CACurrentMediaTime())
        }
        nonisolated func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {}
        nonisolated func draw(in view: MTKView) {
            // The view is permanently paused and only updateNSView calls
            // draw(), synchronously on the main actor.
            MainActor.assumeIsolated { drawFrame(in: view) }
        }
        private func drawFrame(in view: MTKView) {
            guard let device, let queue, let library, view.bounds.width > 0,
                  view.bounds.height > 0, inFlight.wait(timeout: .now()) == .success else { return }
            guard let pipeline = pipeline(style.fragment, device: device, library: library) else {
                inFlight.signal(); return
            }
            let fps = Double(Settings.shared.effectiveAnimationRate.rawValue)
            if let average = gpuTimer.drain(minimumSamples: 30) {
                ladder.adjust(averageGPUTime: average, budget: 1 / fps)
            }
            // Soft wallpaper fields do not need retina-sized offscreen
            // surfaces. Keep text native-resolution in its separate view.
            let backing = view.window?.backingScaleFactor ?? 2
            let native = CGSize(width: view.bounds.width * backing, height: view.bounds.height * backing)
            let cap = style == .tunnel ? 950_000.0 : 1_800_000.0
            let budgetScale = min(1, sqrt(cap / max(1, native.width * native.height)))
            let scale = budgetScale * ladder.scale
            let wanted = CGSize(width: max(1, floor(native.width * scale)), height: max(1, floor(native.height * scale)))
            if view.drawableSize != wanted { view.drawableSize = wanted }
            guard let pass = view.currentRenderPassDescriptor, let drawable = view.currentDrawable,
                  let command = queue.makeCommandBuffer(), let encoder = command.makeRenderCommandEncoder(descriptor: pass) else {
                inFlight.signal(); return
            }
            var uniforms = Self.uniforms(style: style, size: wanted, time: time, frame: frame,
                sensitivity: Settings.shared.audioSensitivity,
                speed: 1, reduced: reduced, vividness: Settings.shared.vividness) // The containing clock already applies Speed.
            encoder.setRenderPipelineState(pipeline)
            encoder.setFragmentBytes(&uniforms, length: MemoryLayout<VizUniforms>.stride, index: 0)
            frame.bands.withUnsafeBytes { encoder.setFragmentBytes($0.baseAddress!, length: $0.count, index: 1) }
            frame.waveform.withUnsafeBytes { encoder.setFragmentBytes($0.baseAddress!, length: $0.count, index: 2) }
            encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
            encoder.endEncoding()
            let timer = gpuTimer, semaphore = inFlight
            command.addCompletedHandler { buffer in
                timer.record(buffer.gpuEndTime - buffer.gpuStartTime)
                semaphore.signal()
            }
            command.present(drawable); command.commit()
        }
        private func pipeline(_ name: String, device: MTLDevice, library: MTLLibrary) -> MTLRenderPipelineState? {
            if let cached = pipelines[name] { return cached }
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.vertexFunction = library.makeFunction(name: "fullscreenVertex")
            descriptor.fragmentFunction = library.makeFunction(name: name)
            descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
            guard let result = try? device.makeRenderPipelineState(descriptor: descriptor) else { return nil }
            pipelines[name] = result
            return result
        }
        static func uniforms(style: AudioVisualizerStyle, size: CGSize, time: Double, frame: AudioSnapshot,
                             sensitivity: Double, speed: Double, reduced: Bool, vividness: Double = 1) -> VizUniforms {
            var u = VizUniforms()
            u.resolution = SIMD2(Float(size.width), Float(size.height))
            u.time = Float(time * (reduced ? 0.15 : 1)); u.dt = 1 / 30
            u.speed = Float(speed * (style == .tunnel ? 0.22 : 0.34))
            u.sensitivity = Float(min(3, max(0.3, sensitivity)))
            u.p15 = Float(min(1.8, max(0.4, vividness)))
            u.palette = style == .silk ? 3 : 0
            u.bass = frame.bass; u.mid = frame.mid; u.treble = frame.treble; u.level = frame.level
            u.subBass = frame.components[0]; u.lowMid = frame.components[2]
            u.highMid = frame.components[4]; u.presence = frame.components[5]; u.air = frame.components[7]
            u.centroid = frame.centroid
            // Keep musical phrasing while removing camera jolts, flashing
            // stars and beat-wide exposure jumps from desktop defaults.
            u.beat = reduced ? 0 : frame.beat * 0.20
            u.trebleBeat = reduced ? 0 : frame.trebleBeat * 0.12
            u.flux = reduced ? 0 : frame.flux * 0.14
            if style == .tunnel {
                u.p0 = 0.38; u.p1 = 0.78; u.p2 = 0.50
                u.p3 = 0; u.p4 = 0.42; u.p5 = 0.72
            } else {
                u.p0 = style == .silk ? 4 : 6
                u.p1 = style == .silk ? 0.16 : 0.20
                u.p2 = 0 // A calm background for lyrics, without flashing stars.
            }
            return u
        }
    }
}
