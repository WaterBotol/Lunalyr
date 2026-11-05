import Combine
import MetalKit
import SwiftUI

/// GPU background effects, evaluated per pixel in Metal. These do what the
/// Canvas styles cannot -- raymarched geometry, escape-time fractals,
/// metaball fields -- and cost roughly the same because the work happens on
/// the GPU rather than in a per-frame draw list.
enum ShaderStyle: String, CaseIterable, Identifiable {
    // Flat 2D fields.
    case plasma, kaleido, aurora, fractal, liquid, warp
    case nebula, voronoi, orbs, silk
    // Raymarched: a signed-distance field walked step by step per pixel.
    case waves, helix
    // Closed-form 3D: a plane or sphere solved analytically, so the depth is
    // real but nothing is marched.
    case ripple, mirror
    case wormhole, rings, vortex
    case orbit, bubbles
    case galaxy
    case strata, clouds, curtains, ribbons, prism
    case tidal, lightTrails

    var id: String { rawValue }

    /// Rough per-pixel expense, used to keep the wallpaper cheap when the
    /// machine cannot afford it. `heavy` means the effect marches a ray or
    /// runs a deep escape-time loop for every pixel of every frame; on an
    /// integrated GPU driving a Retina display that is the difference
    /// between idling and running the fans.
    enum Cost { case light, medium, heavy }

    var cost: Cost {
        switch self {
        case .plasma, .aurora, .silk,
             .ripple, .mirror,
             .wormhole, .rings, .vortex,
             .orbit, .strata, .curtains, .ribbons, .prism:
            return .light
        case .kaleido, .liquid, .warp, .voronoi, .orbs,
             .bubbles, .galaxy, .clouds, .tidal, .lightTrails:
            return .medium
        case .fractal, .nebula, .waves, .helix:
            return .heavy
        }
    }

    var functionName: String {
        switch self {
        case .plasma: return "wlPlasma"
        case .kaleido: return "wlKaleido"
        case .aurora: return "wlAurora"
        case .fractal: return "wlFractal"
        case .liquid: return "wlLiquid"
        case .warp: return "wlWarp"
        case .nebula: return "wlNebula"
        case .voronoi: return "wlVoronoi"
        case .orbs: return "wlOrbs"
        case .silk: return "wlSilk"
        case .waves: return "wlWaves"
        case .helix: return "wlHelix"
        case .ripple: return "wlRipple"
        case .mirror: return "wlMirror"
        case .wormhole: return "wlWormhole"
        case .rings: return "wlRings"
        case .vortex: return "wlVortex"
        case .orbit: return "wlOrbit"
        case .bubbles: return "wlBubbles"
        case .galaxy: return "wlGalaxy"
        case .strata: return "wlStrata"
        case .clouds: return "wlClouds"
        case .curtains: return "wlCurtains"
        case .ribbons: return "wlRibbons"
        case .prism: return "wlPrism"
        case .tidal: return "wlTidal"
        case .lightTrails: return "wlLightTrails"
        }
    }

    var title: String {
        switch self {
        case .plasma: return "Plasma"
        case .kaleido: return "Kaleidoscope"
        case .aurora: return "Aurora"
        case .fractal: return "Fractal"
        case .liquid: return "Liquid"
        case .warp: return "Marble"
        case .nebula: return "Nebula"
        case .voronoi: return "Voronoi"
        case .orbs: return "Orbs"
        case .silk: return "Silk"
        case .waves: return "Waves"
        case .helix: return "Helix"
        case .ripple: return "Ripple"
        case .mirror: return "Mirror"
        case .wormhole: return "Wormhole"
        case .rings: return "Rings"
        case .vortex: return "Vortex"
        case .orbit: return "Orbit"
        case .bubbles: return "Bubbles"
        case .galaxy: return "Galaxy"
        case .strata: return "Strata"
        case .clouds: return "Clouds"
        case .curtains: return "Curtains"
        case .ribbons: return "Ribbons"
        case .prism: return "Prism"
        case .tidal: return "Tidal"
        case .lightTrails: return "Light Trails"
        }
    }

    /// Effects cheap enough to leave running on battery or a low-power Mac.
    static var economical: [ShaderStyle] { allCases.filter { $0.cost == .light } }

    /// Where an effect sits, so a track can be *matched* to one instead of
    /// drawn out of a bucket.
    struct Character {
        /// 0 = still, 1 = frantic.
        var energy: Double
        /// 0 = open and sparse, 1 = busy. Busy effects fight the lyrics, so
        /// this is the axis that protects readability.
        var density: Double
        /// 0 = geometric and hard-edged, 1 = organic and flowing. This is
        /// what separates a techno track from a folk one when both happen to
        /// sit at the same energy.
        var organic: Double
    }

    var character: Character {
        switch self {
        case .plasma:   return .init(energy: 0.30, density: 0.35, organic: 0.85)
        case .kaleido:  return .init(energy: 0.75, density: 0.80, organic: 0.30)
        case .aurora:   return .init(energy: 0.25, density: 0.30, organic: 0.95)
        case .fractal:  return .init(energy: 0.85, density: 0.95, organic: 0.20)
        case .liquid:   return .init(energy: 0.45, density: 0.50, organic: 0.90)
        case .warp:     return .init(energy: 0.35, density: 0.55, organic: 0.80)
        case .nebula:   return .init(energy: 0.42, density: 0.70, organic: 0.80)
        case .voronoi:  return .init(energy: 0.65, density: 0.70, organic: 0.25)
        case .orbs:     return .init(energy: 0.40, density: 0.40, organic: 0.75)
        case .silk:     return .init(energy: 0.20, density: 0.30, organic: 0.90)
        case .waves:    return .init(energy: 0.45, density: 0.45, organic: 0.85)
        case .helix:    return .init(energy: 0.70, density: 0.50, organic: 0.35)
        case .ripple:   return .init(energy: 0.35, density: 0.45, organic: 0.95)
        case .mirror:   return .init(energy: 0.30, density: 0.35, organic: 0.70)
        case .wormhole: return .init(energy: 0.80, density: 0.70, organic: 0.30)
        case .rings:    return .init(energy: 0.55, density: 0.35, organic: 0.45)
        case .vortex:   return .init(energy: 0.70, density: 0.55, organic: 0.55)
        case .orbit:    return .init(energy: 0.50, density: 0.30, organic: 0.50)
        case .bubbles:  return .init(energy: 0.35, density: 0.40, organic: 0.80)
        case .galaxy:   return .init(energy: 0.40, density: 0.60, organic: 0.75)
        case .strata:   return .init(energy: 0.20, density: 0.45, organic: 0.55)
        case .clouds:   return .init(energy: 0.25, density: 0.55, organic: 0.90)
        case .curtains: return .init(energy: 0.45, density: 0.50, organic: 0.85)
        case .ribbons:  return .init(energy: 0.40, density: 0.40, organic: 0.70)
        case .prism:    return .init(energy: 0.60, density: 0.50, organic: 0.40)
        case .tidal:    return .init(energy: 0.45, density: 0.28, organic: 0.95)
        case .lightTrails: return .init(energy: 0.72, density: 0.40, organic: 0.60)
        }
    }

    /// Squared distance from a track's character to this effect's, lower
    /// being a better fit. Energy leads, because it is the axis a listener
    /// notices first; organic follows, because it is what separates two
    /// effects that happen to move at the same speed.
    func fit(to target: Character) -> Double {
        let c = character
        let dEnergy = (c.energy - target.energy) * (c.energy - target.energy) * 1.00
        let dOrganic = (c.organic - target.organic) * (c.organic - target.organic) * 0.75
        let dDensity = (c.density - target.density) * (c.density - target.density) * 0.45
        return dEnergy + dOrganic + dDensity
    }

    /// How many of the best-fitting effects a track chooses between.
    ///
    /// Sweeping the plausible mood space showed why this cannot be 1: taking
    /// only the nearest effect meant 20 of 25 could never be selected at all,
    /// because a handful of them sit nearest to every point real music
    /// occupies. Four is where coverage reaches 24 of 25 while every
    /// candidate is still a genuinely close match.
    private static let candidatePoolSize = 4

    /// The effect that best suits a track, chosen from the closest few.
    static func matching(_ target: Character, seed: Int, from pool: [ShaderStyle]) -> ShaderStyle {
        guard !pool.isEmpty else { return .plasma }
        let ranked = pool.sorted { $0.fit(to: target) < $1.fit(to: target) }
        let candidates = Array(ranked.prefix(candidatePoolSize))
        let index = Int(stableNoise(seed) * Double(candidates.count)) % candidates.count
        return candidates[index]
    }

    /// A stable [0,1) from a track seed.
    ///
    /// Deliberately not `Hasher`: Swift seeds that randomly per process, so
    /// it would deal every track a new effect on each launch. The seed is
    /// folded in *first* and the result avalanched at the end -- FNV mixes
    /// forward only, so a seed folded in last barely reaches the high bits
    /// sampled here. Measured: without the avalanche, seeds 1 and 1337
    /// differed by 0.0001 and the seed was effectively ignored.
    private static func stableNoise(_ seed: Int) -> Double {
        var h: UInt64 = 1469598103934665603
        var s = UInt64(bitPattern: Int64(seed))
        for _ in 0..<8 {
            h = (h ^ (s & 0xFF)) &* 1099511628211
            s >>= 8
        }
        for byte in "verso".utf8 { h = (h ^ UInt64(byte)) &* 1099511628211 }
        // splitmix64 finalizer.
        h ^= h >> 30; h = h &* 0xBF58476D1CE4E5B9
        h ^= h >> 27; h = h &* 0x94D049BB133111EB
        h ^= h >> 31
        return Double(h >> 40) / Double(1 << 24)
    }

    /// Calm enough to sit under a full line of lyrics.
    static let calm: [ShaderStyle] = [.plasma, .aurora, .liquid, .warp, .nebula, .orbs, .silk, .waves, .ripple, .mirror, .rings, .orbit, .bubbles, .galaxy, .strata, .clouds, .curtains, .ribbons, .tidal]
    static let intense: [ShaderStyle] = [.kaleido, .fractal, .voronoi, .helix, .wormhole, .vortex, .prism, .lightTrails]
}

/// Draws a GPU effect with Lunalyr's own Metal view.
///
/// SwiftUI's `colorEffect` shaded every Retina pixel of every display, then
/// flattened the result again to tint it with the album gradient and again to
/// adjust saturation. This renders the same effect once, at a bounded pixel
/// budget (soft fields survive upscaling far better than they survive dropped
/// frames), with the tint and saturation folded into that one pass.
///
/// Each surface draws on the GPU driving its display, with the device, queue
/// and library the visualisers share (`VisualizerGPU`). The system default
/// device is never asked for: on a dual-GPU MacBook Pro it is the discrete
/// GPU, which it powers up for as long as it is held.
@MainActor
struct ShaderBackgroundView: NSViewRepresentable {
    let style: ShaderStyle
    /// Steady accumulated phase from `VisualClock`.
    let time: Double
    /// The effects advect along this flow phase.
    let flow: Double
    let palette: AlbumWallpaperPalette
    var vividness: Double = 1
    var paused = false
    var preview = false
    /// Spend fewer pixels while power is being saved.
    var economical = false
    /// The display this surface is on, whose GPU draws it; nil for the
    /// Settings preview, which takes the GPU the visualisers prefer.
    var displayID: CGDirectDisplayID?

    func makeCoordinator() -> ShaderBackgroundRenderer { ShaderBackgroundRenderer() }

    func makeNSView(context: Context) -> ShaderSurfaceView {
        context.coordinator.displayID = displayID
        return ShaderSurfaceView.make(drawnBy: context.coordinator)
    }

    func updateNSView(_ view: ShaderSurfaceView, context: Context) {
        let renderer = context.coordinator
        renderer.displayID = displayID
        renderer.frame = .init(style: style, time: time, flow: flow, palette: palette,
            vividness: vividness, budget: ShaderRenderBudget.pixels(for: style, preview: preview, economical: economical))
        renderer.paused = paused
        if paused { renderer.showStillIfNeeded(in: view) } else { view.draw() }
    }

    static func dismantleNSView(_ view: ShaderSurfaceView, coordinator: ShaderBackgroundRenderer) {
        view.isPaused = true
        view.onLayout = nil
        view.delegate = nil
        coordinator.surfaceRemoved()
    }
}

/// An effect's Metal view. It is drawn only from the wallpaper's own frame
/// clock, and tells its renderer when it gains a size or a window, so a
/// surface made while its wallpaper is paused can show its still frame.
final class ShaderSurfaceView: MTKView {
    var onLayout: (@MainActor () -> Void)?

    @MainActor static func make(drawnBy renderer: ShaderBackgroundRenderer) -> ShaderSurfaceView {
        let view = ShaderSurfaceView(frame: .zero, device: renderer.adoptDisplayGPU())
        view.colorPixelFormat = .bgra8Unorm
        view.colorspace = CGColorSpace(name: CGColorSpace.sRGB)
        view.framebufferOnly = true
        view.autoResizeDrawable = false
        view.enableSetNeedsDisplay = false
        view.isPaused = true // Drawn only from the wallpaper's own frame clock.
        view.layer?.isOpaque = true
        view.layer?.magnificationFilter = .linear
        view.delegate = renderer
        view.onLayout = { [weak renderer, weak view] in
            guard let view else { return }
            renderer?.showStillIfNeeded(in: view)
        }
        return view
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        onLayout?()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        onLayout?()
    }
}

/// How many pixels a GPU effect may shade per frame.
enum ShaderRenderBudget {
    /// Matches the native visualisers' ceiling for soft wallpaper fields.
    static let standard = 1_800_000.0
    static let heavy = 1_200_000.0
    static let preview = 220_000.0

    static func pixels(for style: ShaderStyle, preview: Bool, economical: Bool) -> Double {
        if preview { return Self.preview }
        let budget = style.cost == .heavy ? heavy : standard
        return economical ? budget * 0.6 : budget
    }

    /// The drawable for a view of `points` at `backingScale`: native pixels
    /// when they fit the budget, otherwise the same aspect scaled down.
    static func drawableSize(points: CGSize, backingScale: CGFloat, budget: Double) -> CGSize {
        guard points.width > 0, points.height > 0, points.width.isFinite, points.height.isFinite else {
            return CGSize(width: 1, height: 1)
        }
        let scale = backingScale.isFinite && backingScale > 0 ? backingScale : 2
        let native = CGSize(width: points.width * scale, height: points.height * scale)
        let fit = min(1, sqrt(max(1, budget) / Double(native.width * native.height)))
        return CGSize(width: max(1, (native.width * CGFloat(fit)).rounded(.down)),
                      height: max(1, (native.height * CGFloat(fit)).rounded(.down)))
    }
}

/// Keeps the shader's 32-bit time small. The wallpaper clocks run for as
/// long as the app does; once they pass about 2^16 seconds a Float can no
/// longer represent a frame step evenly and motion visibly stutters. Each
/// renderer starts from the clock's position within a fixed period, so a new
/// effect (a crossfade, a mode switch) never begins at the same state, then
/// advances continuously -- it never jumps while it is on screen.
struct ShaderTimeBase: Equatable {
    static let period = 4096.0
    private(set) var origin: SIMD2<Double>?

    mutating func uniforms(time: Double, flow: Double) -> SIMD2<Float> {
        let base = origin ?? SIMD2((time / Self.period).rounded(.down) * Self.period,
                                   (flow / Self.period).rounded(.down) * Self.period)
        origin = base
        return SIMD2(Float(time - base.x), Float(flow - base.y))
    }
}

@MainActor
final class ShaderBackgroundRenderer: NSObject, MTKViewDelegate {
    struct Frame {
        var style: ShaderStyle
        var time: Double
        var flow: Double
        var palette: AlbumWallpaperPalette
        var vividness: Double
        var budget: Double
    }

    /// Mirrors `WLBackgroundUniforms` in Shaders.metal.
    struct Uniforms {
        var size: SIMD2<Float>
        var pixelScale: SIMD2<Float>
        var time: Float
        var flow: Float
        var vividness: Float
        var padding: Float = 0
        var tintA: SIMD4<Float>
        var tintB: SIMD4<Float>
    }

    var frame: Frame?
    /// The display this surface is on; nil for the Settings preview.
    var displayID: CGDirectDisplayID?
    /// The wallpaper's frame clock is paused: covered, Pause wallpaper, or
    /// the screens asleep.
    var paused = false
    /// The GPU this surface draws with.
    private(set) var gpu: ShaderGPU?
    /// Frames this surface has presented.
    private(set) var framesPresented = 0
    private let cache: ShaderPipelineCache
    private var timeBase = ShaderTimeBase()
    private let inFlight = DispatchSemaphore(value: 2)
    private var stillScheduled = false
    private var compileWatch: AnyCancellable?

    init(cache: ShaderPipelineCache? = nil) {
        self.cache = cache ?? .shared
        super.init()
        // The GPU this surface draws with stays in use for as long as it does.
        self.cache.register(self)
    }

    /// Takes the display's GPU, for a surface that has none yet.
    func adoptDisplayGPU() -> MTLDevice? {
        if gpu == nil { gpu = cache.gpu(for: displayID) }
        return gpu?.device
    }

    /// A surface made while its wallpaper is paused shows one still frame
    /// instead of nothing -- a black desktop, under the scrim -- until the
    /// wallpaper resumes. It is drawn once the view has a size and a window
    /// and the effect has compiled, whichever comes last. A surface that has
    /// shown a frame keeps it while paused, as before.
    func showStillIfNeeded(in view: MTKView) {
        guard paused, framesPresented == 0, !stillScheduled else { return }
        stillScheduled = true
        // Not from inside SwiftUI's update or AppKit's layout.
        DispatchQueue.main.async { [weak self, weak view] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.stillScheduled = false
                guard let view, self.paused, self.framesPresented == 0 else { return }
                view.draw()
                if self.framesPresented == 0 { self.waitForCompile(view) }
            }
        }
    }

    /// The surface is gone: its GPU may be one nothing uses any more.
    func surfaceRemoved() {
        compileWatch = nil
        cache.scheduleRelease()
    }

    /// Tries the still again whenever an effect finishes compiling.
    private func waitForCompile(_ view: MTKView) {
        guard compileWatch == nil else { return }
        compileWatch = cache.$settled.dropFirst().sink { [weak self, weak view] _ in
            guard let self, let view else { return }
            self.showStillIfNeeded(in: view)
        }
    }

    nonisolated func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {}
    nonisolated func draw(in view: MTKView) {
        // The view is permanently paused; only updateNSView calls draw(),
        // synchronously on the main actor.
        MainActor.assumeIsolated { render(in: view) }
    }

    /// Which GPU draws a frame. Automatic graphics switching can move a
    /// display to the other GPU, and a window can move to a display another
    /// GPU drives: the surface follows once the effect is compiled there,
    /// and meanwhile keeps drawing where it is rather than going blank --
    /// though only with what that GPU already has: nothing is compiled on a
    /// GPU the surface is leaving, which would put it back to work.
    enum GPUChoice: Equatable { case current, display, none }

    nonisolated static func choose(current: UInt64?, display: UInt64?, readyOnCurrent: Bool,
                                   readyOnDisplay: Bool) -> GPUChoice {
        if let display, display != current, readyOnDisplay { return .display }
        if current != nil, readyOnCurrent { return .current }
        return .none
    }

    private func render(in view: MTKView) {
        guard let frame, view.bounds.width > 0, view.bounds.height > 0,
              inFlight.wait(timeout: .now()) == .success else { return }
        let wanted = cache.gpu(for: displayID)
        let onWanted = wanted.flatMap { cache.pipeline(for: frame.style, on: $0) }
        // Without a GPU for the display, the one in hand is all there is.
        let onCurrent = wanted?.id == gpu?.id ? onWanted : gpu.flatMap {
            wanted == nil ? cache.pipeline(for: frame.style, on: $0) : cache.compiledPipeline(for: frame.style, on: $0)
        }
        let pipeline: MTLRenderPipelineState
        switch Self.choose(current: gpu?.id, display: wanted?.id, readyOnCurrent: onCurrent != nil,
                           readyOnDisplay: onWanted != nil) {
        case .display:
            guard let wanted, let onWanted else { inFlight.signal(); return }
            if gpu?.id != wanted.id {
                let left = gpu != nil
                gpu = wanted
                view.device = wanted.device
                // The GPU left behind may be one nothing uses any more.
                if left { cache.scheduleRelease() }
            }
            pipeline = onWanted
        case .current:
            guard let onCurrent else { inFlight.signal(); return }
            pipeline = onCurrent
        case .none:
            inFlight.signal(); return
        }
        guard let gpu else { inFlight.signal(); return }
        let points = view.bounds.size
        let drawable = ShaderRenderBudget.drawableSize(points: points,
            backingScale: view.window?.backingScaleFactor ?? 2, budget: frame.budget)
        if view.drawableSize != drawable { view.drawableSize = drawable }
        guard let pass = view.currentRenderPassDescriptor, let target = view.currentDrawable,
              let command = gpu.queue.makeCommandBuffer() else { inFlight.signal(); return }
        let clock = timeBase.uniforms(time: frame.time, flow: frame.flow)
        guard Self.encode(pipeline, frame: frame, clock: clock, points: points, pixels: drawable,
                          pass: pass, command: command) else { inFlight.signal(); return }
        let semaphore = inFlight
        command.addCompletedHandler { _ in semaphore.signal() }
        command.present(target)
        command.commit()
        framesPresented &+= 1
        compileWatch = nil
    }

    static func uniforms(for frame: Frame, clock: SIMD2<Float>, points: CGSize, pixels: CGSize) -> Uniforms {
        func tint(_ index: Int) -> SIMD4<Float> {
            let stop = frame.palette.stops.indices.contains(index) ? frame.palette.stops[index] : .zero
            return SIMD4(stop.x, stop.y, stop.z, 1)
        }
        return Uniforms(
            size: SIMD2(Float(points.width), Float(points.height)),
            pixelScale: SIMD2(Float(pixels.width / points.width), Float(pixels.height / points.height)),
            time: clock.x, flow: clock.y, vividness: Float(min(1.8, max(0, frame.vividness))),
            tintA: tint(0), tintB: tint(2))
    }

    @discardableResult
    static func encode(_ pipeline: MTLRenderPipelineState, frame: Frame, clock: SIMD2<Float>, points: CGSize,
                       pixels: CGSize, pass: MTLRenderPassDescriptor, command: MTLCommandBuffer) -> Bool {
        guard let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { return false }
        var uniforms = uniforms(for: frame, clock: clock, points: points, pixels: pixels)
        var stops = frame.palette.stops.map(oklab)
        while stops.count < 5 { stops.append(stops.last ?? .zero) }
        encoder.setRenderPipelineState(pipeline)
        encoder.setFragmentBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 0)
        stops.withUnsafeBytes { encoder.setFragmentBytes($0.baseAddress!, length: $0.count, index: 1) }
        encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
        encoder.endEncoding()
        return true
    }

    /// Gamma-encoded sRGB to OKLab, the space SwiftUI interpolates
    /// gradient stops in.
    nonisolated static func oklab(_ rgb: SIMD3<Float>) -> SIMD4<Float> {
        func linear(_ c: Float) -> Float {
            let c = min(1, max(0, c))
            return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4)
        }
        let r = linear(rgb.x), g = linear(rgb.y), b = linear(rgb.z)
        let l = cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b)
        let m = cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b)
        let s = cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b)
        return SIMD4(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                     1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                     0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s, 0)
    }

    static func descriptor(for style: ShaderStyle, library: MTLLibrary) -> MTLRenderPipelineDescriptor? {
        guard let effect = library.makeFunction(name: style.functionName),
              let fragment = library.makeFunction(name: style.backgroundFragment),
              let vertex = library.makeFunction(name: "wlBackgroundVertex") else { return nil }
        let descriptor = MTLRenderPipelineDescriptor()
        descriptor.vertexFunction = vertex
        descriptor.fragmentFunction = fragment
        let linked = MTLLinkedFunctions()
        linked.privateFunctions = [effect]
        descriptor.fragmentLinkedFunctions = linked
        descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
        return descriptor
    }
}

extension ShaderStyle {
    /// The direct-rendering entry point in Shaders.metal.
    var backgroundFragment: String { functionName + "Background" }
}

/// One GPU as the effects use it: the device, command queue and shader
/// library the visualisers share for it (`VisualizerGPU.Context`).
struct ShaderGPU {
    /// The device's registry ID, which every cache keys by.
    let id: UInt64
    let device: MTLDevice
    let queue: MTLCommandQueue
    let library: MTLLibrary

    init(id: UInt64, device: MTLDevice, queue: MTLCommandQueue, library: MTLLibrary) {
        self.id = id
        self.device = device
        self.queue = queue
        self.library = library
    }

    @MainActor init(_ context: VisualizerGPU.Context) {
        self.init(id: context.device.registryID, device: context.device, queue: context.queue, library: context.library)
    }
}

/// Compiled effects per GPU, shared by every display on that GPU and by both
/// sides of a crossfade. Each is compiled once per GPU, off the main thread.
///
/// Until an effect has *settled* on a display's GPU -- compiled, or failed --
/// its view there has nothing to draw, so a crossfade into it waits rather
/// than fading towards an empty view. An effect that fails to compile draws
/// the fallback effect meanwhile and is tried again later instead of staying
/// black for the rest of the session.
///
/// A GPU that no display uses and no surface draws with any more is let go,
/// with the visualisers' context for it (see `releaseUnusedGPUs`).
@MainActor
final class ShaderPipelineCache: ObservableObject {
    /// Starts compiling `style` on a GPU and reports the result on the main
    /// actor; false when it cannot even start (the effect is missing).
    typealias Compiler = @MainActor (ShaderStyle, ShaderGPU, @escaping @MainActor (MTLRenderPipelineState?) -> Void) -> Bool
    /// The GPU driving a display, or the one preferred when there is none.
    typealias GPUFinder = @MainActor (CGDirectDisplayID?) -> ShaderGPU?
    /// The registry IDs of the GPUs a surface may draw with now (see
    /// `VisualizerGPU.devicesInUse`).
    typealias GPUsInUse = @MainActor () -> Set<UInt64>

    static let shared: ShaderPipelineCache = {
        let cache = ShaderPipelineCache()
        cache.followDisplayChanges()
        return cache
    }()
    /// Drawn while another effect is unavailable: light, and the plainest.
    static let fallback: ShaderStyle = .plasma
    static let retryInterval: TimeInterval = 30
    /// How long a display's GPU is trusted before it is looked up again, as
    /// for the visualisers: automatic graphics switching can move a display
    /// between GPUs without changing its ID.
    static let deviceCheckInterval: TimeInterval = 2
    /// How long after a display or surface change the GPUs nothing uses are
    /// let go: long enough for every surface to look its display's GPU up
    /// again.
    static let releaseDelay: TimeInterval = 3

    struct Key: Hashable {
        let gpu: UInt64
        let style: ShaderStyle
    }

    /// Effects that have compiled, or failed and use the fallback, per GPU.
    @Published private(set) var settled: Set<Key> = []
    /// When the last compile finished, on the cache's clock (system uptime).
    private(set) var lastSettledAt: TimeInterval?
    private var pipelines: [Key: MTLRenderPipelineState] = [:]
    private var compiling: Set<Key> = []

    /// A compile is in flight: some surface may be holding its crossfade.
    var isCompiling: Bool { !compiling.isEmpty }
    private var failedAt: [Key: TimeInterval] = [:]
    private var displayGPUs: [CGDirectDisplayID?: (gpu: ShaderGPU, checkedAt: TimeInterval)] = [:]
    /// The surfaces drawing with this cache; each one's GPU stays in use.
    private struct Surface { weak var renderer: ShaderBackgroundRenderer? }
    private var surfaces: [ObjectIdentifier: Surface] = [:]
    private let compiler: Compiler
    private let findGPU: GPUFinder
    private let gpusInUse: GPUsInUse
    private let releaseContexts: @MainActor (Set<UInt64>) -> Void
    private let clock: () -> TimeInterval
    private var schedulesReleases = false
    private var releaseScheduled = false
    private var displayObserver: NSObjectProtocol?

    /// `gpusInUse` defaults to the visualisers' GPUs when they are the ones
    /// `gpus` finds (the default), and their contexts are then let go with
    /// this cache's; a cache given its own GPUs keeps to them.
    init(compiler: @escaping Compiler = ShaderPipelineCache.compileWithMetal,
         gpus: GPUFinder? = nil,
         gpusInUse: GPUsInUse? = nil,
         clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.compiler = compiler
        self.findGPU = gpus ?? { VisualizerGPU.shared.context(for: $0).map(ShaderGPU.init) }
        self.gpusInUse = gpusInUse ?? (gpus == nil ? { VisualizerGPU.devicesInUse() } : { [] })
        self.releaseContexts = gpus == nil ? { VisualizerGPU.shared.releaseContexts(keeping: $0) } : { _ in }
        self.clock = clock
    }

    /// The GPU that draws a surface on `display` (nil: the Settings preview),
    /// looked up again every couple of seconds.
    func gpu(for display: CGDirectDisplayID?) -> ShaderGPU? {
        let now = clock()
        let known = displayGPUs[display]
        if let known, now - known.checkedAt >= 0, now - known.checkedAt < Self.deviceCheckInterval {
            return known.gpu
        }
        guard let gpu = findGPU(display) else {
            displayGPUs[display] = nil
            return nil
        }
        // The display moved: the GPU it left may be one nothing uses now.
        if let known, known.gpu.id != gpu.id { scheduleRelease() }
        displayGPUs[display] = (gpu, now)
        return gpu
    }

    /// A surface drawing with this cache. The GPU it draws with stays in use
    /// for as long as it does.
    func register(_ renderer: ShaderBackgroundRenderer) {
        surfaces = surfaces.filter { $0.value.renderer != nil }
        surfaces[ObjectIdentifier(renderer)] = Surface(renderer: renderer)
    }

    /// Lets go of every GPU that no display uses and no surface draws with
    /// any more: its pipelines and settled effects here, and the visualisers'
    /// device, queue and library for it. After an external display wired to
    /// a MacBook Pro's discrete GPU was unplugged, they were held for the
    /// rest of the session, which may keep macOS from switching that GPU off.
    func releaseUnusedGPUs() {
        var live = gpusInUse()
        surfaces = surfaces.filter { $0.value.renderer != nil }
        for surface in surfaces.values {
            if let id = surface.renderer?.gpu?.id { live.insert(id) }
        }
        let kept: (Key) -> Bool = { live.contains($0.gpu) }
        pipelines = pipelines.filter { kept($0.key) }
        failedAt = failedAt.filter { kept($0.key) }
        // A compile still running for a GPU let go is dropped when it ends.
        compiling = compiling.filter(kept)
        let stillSettled = settled.filter(kept)
        if stillSettled != settled { settled = stillSettled }
        displayGPUs = displayGPUs.filter { live.contains($0.value.gpu.id) }
        releaseContexts(live)
    }

    /// Looks for GPUs nothing uses any more a little after something may
    /// have stopped using one: the displays changed, or a surface moved to
    /// another GPU or went away. Only the shared cache does this.
    func scheduleRelease() {
        guard schedulesReleases, !releaseScheduled else { return }
        releaseScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.releaseDelay) { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.releaseScheduled = false
                self.releaseUnusedGPUs()
            }
        }
    }

    private func followDisplayChanges() {
        schedulesReleases = true
        displayObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor [weak self] in self?.scheduleRelease() }
        }
    }

    /// Whether a surface on `display` has something to draw `style` with.
    /// With no GPU at all nothing will ever compile, so nothing waits.
    func isSettled(_ style: ShaderStyle, display: CGDirectDisplayID?) -> Bool {
        guard let gpu = gpu(for: display) else { return true }
        return isSettled(style, on: gpu)
    }

    func isSettled(_ style: ShaderStyle, on gpu: ShaderGPU) -> Bool {
        settled.contains(Key(gpu: gpu.id, style: style))
    }

    /// Starts compiling an effect that is about to be shown on `display`.
    func prepare(_ style: ShaderStyle, display: CGDirectDisplayID?) {
        guard let gpu = gpu(for: display) else { return }
        prepare(style, on: gpu)
    }

    func prepare(_ style: ShaderStyle, on gpu: ShaderGPU) {
        if pipelines[Key(gpu: gpu.id, style: style)] == nil { request(style, on: gpu) }
    }

    /// What `style` is drawn with on `gpu` without compiling anything there:
    /// its own pipeline, the fallback's while it has failed, or nil.
    func compiledPipeline(for style: ShaderStyle, on gpu: ShaderGPU) -> MTLRenderPipelineState? {
        let key = Key(gpu: gpu.id, style: style)
        if let own = pipelines[key] { return own }
        guard failedAt[key] != nil, style != Self.fallback else { return nil }
        return pipelines[Key(gpu: gpu.id, style: Self.fallback)]
    }

    /// What to draw `style` with on `gpu`: its own pipeline, the fallback's
    /// while it has failed, or nil while it is still compiling.
    func pipeline(for style: ShaderStyle, on gpu: ShaderGPU) -> MTLRenderPipelineState? {
        let key = Key(gpu: gpu.id, style: style)
        if pipelines[key] == nil { request(style, on: gpu) }
        if let own = pipelines[key] { return own }
        guard failedAt[key] != nil, style != Self.fallback else { return nil }
        let fallback = Key(gpu: gpu.id, style: Self.fallback)
        if pipelines[fallback] == nil { request(Self.fallback, on: gpu) }
        return pipelines[fallback]
    }

    private func request(_ style: ShaderStyle, on gpu: ShaderGPU) {
        let key = Key(gpu: gpu.id, style: style)
        guard !compiling.contains(key) else { return }
        if let failed = failedAt[key], clock() - failed < Self.retryInterval { return }
        compiling.insert(key)
        let started = compiler(style, gpu) { [weak self] state in self?.finish(key, state) }
        if !started {
            // Reported later: this can run while SwiftUI is drawing.
            Task { @MainActor [weak self] in self?.finish(key, nil) }
        }
    }

    private func finish(_ key: Key, _ state: MTLRenderPipelineState?) {
        // Not kept for a GPU let go while it compiled.
        guard compiling.remove(key) != nil else { return }
        lastSettledAt = clock()
        if let state {
            pipelines[key] = state
            failedAt[key] = nil
        } else {
            failedAt[key] = clock()
        }
        if !settled.contains(key) { settled.insert(key) }
    }

    static func compileWithMetal(_ style: ShaderStyle, on gpu: ShaderGPU,
                                 _ done: @escaping @MainActor (MTLRenderPipelineState?) -> Void) -> Bool {
        guard let descriptor = ShaderBackgroundRenderer.descriptor(for: style, library: gpu.library) else { return false }
        gpu.device.makeRenderPipelineState(descriptor: descriptor) { state, error in
            if let error { NSLog("Wallpaper effect %@: %@", style.functionName, error.localizedDescription) }
            Task { @MainActor in done(state) }
        }
        return true
    }
}
