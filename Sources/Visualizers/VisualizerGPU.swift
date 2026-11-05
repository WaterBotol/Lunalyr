import AppKit
import Metal

/// One Metal device, command queue, library and pipeline cache per GPU,
/// shared by every visualiser surface: each display's wallpaper and the
/// settings preview used to create their own.
@MainActor
final class VisualizerGPU {
    static let shared = VisualizerGPU()
    private var contexts: [UInt64: Context] = [:]

    /// The context for a surface on `displayID` (nil before it has a window).
    func context(for displayID: CGDirectDisplayID?) -> Context? {
        guard let device = Self.preferredDevice(for: displayID) else { return nil }
        if let existing = contexts[device.registryID] { return existing }
        guard let context = Context(device: device) else { return nil }
        contexts[device.registryID] = context
        return context
    }

    /// Forgets the context of every GPU whose registry ID is not in `live`,
    /// so a GPU no display uses any more -- a MacBook Pro's discrete GPU,
    /// once the external display wired to it is unplugged -- is not held for
    /// the rest of the session. A surface still holding one keeps it until
    /// it follows its display to another GPU.
    func releaseContexts(keeping live: Set<UInt64>) {
        contexts = contexts.filter { live.contains($0.key) }
    }

    /// The registry IDs of the GPUs a surface may draw with now: the one
    /// driving each display, and the one preferred for a surface that has no
    /// display yet.
    static func devicesInUse() -> Set<UInt64> {
        var ids = Set(NSScreen.screens.compactMap(\.versoDisplayID)
            .compactMap { CGDirectDisplayCopyCurrentMetalDevice($0)?.registryID })
        if let preferred = preferredDevice(for: nil) { ids.insert(preferred.registryID) }
        return ids
    }

    /// Uses the GPU currently driving the surface's display, so a frame never
    /// crosses GPUs. Before a surface has a display, the integrated GPU is
    /// preferred unless a display is already driven by another GPU: on dual-GPU
    /// MacBook Pros `MTLCreateSystemDefaultDevice` returns, and powers up, the
    /// discrete GPU even when every display runs on the integrated one.
    static func preferredDevice(for displayID: CGDirectDisplayID?) -> MTLDevice? {
        if let displayID, let driving = CGDirectDisplayCopyCurrentMetalDevice(displayID) { return driving }
        let devices = MTLCopyAllDevices()
        let driving = NSScreen.screens.compactMap(\.versoDisplayID).compactMap(CGDirectDisplayCopyCurrentMetalDevice)
        return choose(from: devices, driving: driving) ?? MTLCreateSystemDefaultDevice()
    }

    /// The device choice without system state, for tests.
    nonisolated static func choose(from devices: [MTLDevice], driving: [MTLDevice]) -> MTLDevice? {
        if let lowPower = devices.first(where: { $0.isLowPower && !$0.isRemovable }),
           driving.allSatisfy({ $0.registryID == lowPower.registryID }) {
            return lowPower
        }
        return driving.first ?? devices.first(where: { !$0.isRemovable }) ?? devices.first
    }

    @MainActor
    final class Context {
        let device: MTLDevice
        let queue: MTLCommandQueue
        let library: MTLLibrary
        private var pipelines: [String: MTLRenderPipelineState] = [:]
        private var requested = Set<String>()
        private var limiter: MTLComputePipelineState?
        private var limiterRequested = false

        init?(device: MTLDevice) {
            guard let queue = device.makeCommandQueue(), let library = device.makeDefaultLibrary() else { return nil }
            self.device = device
            self.queue = queue
            self.library = library
        }

        /// The scene's pipeline, or nil while its first asynchronous build runs.
        func pipeline(_ fragment: String) -> MTLRenderPipelineState? {
            if let cached = pipelines[fragment] { return cached }
            guard requested.insert(fragment).inserted else { return nil }
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.vertexFunction = library.makeFunction(name: "fullscreenVertex")
            descriptor.fragmentFunction = library.makeFunction(name: fragment)
            descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
            device.makeRenderPipelineState(descriptor: descriptor) { [weak self] state, error in
                Task { @MainActor in
                    self?.pipelines[fragment] = state
                    if let error { NSLog("Visualizer pipeline %@: %@", fragment, error.localizedDescription) }
                }
            }
            return nil
        }

        /// The photosensitivity limiter kernel, or nil while it builds.
        var exposureLimiter: MTLComputePipelineState? {
            if let limiter { return limiter }
            guard !limiterRequested, let function = library.makeFunction(name: "exposureLimiter") else { return nil }
            limiterRequested = true
            device.makeComputePipelineState(function: function) { [weak self] state, error in
                Task { @MainActor in
                    self?.limiter = state
                    if let error { NSLog("Visualizer exposure limiter: %@", error.localizedDescription) }
                }
            }
            return nil
        }
    }
}

/// One surface's photosensitivity limiter: a small probe target and the gain
/// state the kernel in ExposureLimiter.metal keeps between frames.
@MainActor
final class ExposureProbe {
    static let width = 64, height = 36

    let device: MTLDevice
    let texture: MTLTexture
    let state: MTLBuffer
    private var lastTime: Double?
    private var adoptNext = false

    /// `shown` is the mean luminance already on screen: 0 fades a new surface
    /// in from black; a surface taking over a visible scene passes the level
    /// that scene is shown at (see `ExposureMemory`).
    init?(device: MTLDevice, shown: Float = 0) {
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm,
            width: Self.width, height: Self.height, mipmapped: false)
        descriptor.usage = [.renderTarget, .shaderRead]
        descriptor.storageMode = .private
        guard let texture = device.makeTexture(descriptor: descriptor),
              let state = device.makeBuffer(length: 4 * MemoryLayout<Float>.stride, options: .storageModeShared) else { return nil }
        memset(state.contents(), 0, state.length)
        state.contents().storeBytes(of: shown.isFinite ? max(0, shown) : 0,
                                    toByteOffset: MemoryLayout<Float>.stride, as: Float.self)
        self.device = device
        self.texture = texture
        self.state = state
    }

    /// The limiter just turned on: keep what is already on screen.
    func adoptCurrentBrightness() { adoptNext = true }

    /// The gain applied to the last encoded frame, read back for tests.
    var lastGain: Float { state.contents().load(as: Float.self) }
    /// The displayed mean luminance the kernel last allowed.
    var shownLuminance: Float { state.contents().load(fromByteOffset: MemoryLayout<Float>.stride, as: Float.self) }

    /// Renders the frame at probe size, then measures and updates the gain.
    /// `bind` binds exactly what the full-size pass will, except exposure.
    func encode(into command: MTLCommandBuffer, pipeline: MTLRenderPipelineState,
                limiter: MTLComputePipelineState, at time: Double,
                bind: (MTLRenderCommandEncoder) -> Void) -> Bool {
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = texture
        pass.colorAttachments[0].loadAction = .dontCare
        pass.colorAttachments[0].storeAction = .store
        guard let render = command.makeRenderCommandEncoder(descriptor: pass) else { return false }
        render.setRenderPipelineState(pipeline)
        bind(render)
        var unity: Float = 1
        render.setFragmentBytes(&unity, length: MemoryLayout<Float>.stride, index: 5)
        render.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
        render.endEncoding()

        guard let compute = command.makeComputeCommandEncoder() else { return false }
        let elapsed: Double = lastTime.map { max(0, time - $0) } ?? 0
        let dt = min(ExposureLimiter.maxStep, Float(elapsed))
        lastTime = time
        var params = SIMD4<Float>(ExposureLimiter.maxRisePerSecond, dt, adoptNext ? 1 : 0, 0)
        adoptNext = false
        compute.setComputePipelineState(limiter)
        compute.setTexture(texture, index: 0)
        compute.setBuffer(state, offset: 0, index: 0)
        compute.setBytes(&params, length: MemoryLayout<SIMD4<Float>>.stride, index: 1)
        // The reduction needs a power of two no larger than its 256 slots.
        var threads = 256
        while threads > limiter.maxTotalThreadsPerThreadgroup { threads /= 2 }
        compute.dispatchThreadgroups(MTLSize(width: 1, height: 1, depth: 1),
                                     threadsPerThreadgroup: MTLSize(width: threads, height: 1, depth: 1))
        compute.endEncoding()
        return true
    }
}

/// The brightness each native scene was last shown at through the limiter,
/// per display.
///
/// NowPlayingView crossfades a scene change by building a new copy of the
/// outgoing scene and fading it out. That copy's limiter used to start from
/// black, so with Reduce Motion or Dim Flashing Lights on every crossfade
/// dipped to black. A new surface for a scene that is on screen now starts
/// at the level the scene is shown at; a scene coming on screen still fades in.
@MainActor
final class ExposureMemory {
    static let shared = ExposureMemory()
    /// A level older than this belongs to a scene that has left the screen.
    static let freshness = 0.5

    struct Key: Hashable {
        let style: AudioVisualizerStyle
        let display: CGDirectDisplayID?
        let preview: Bool
    }

    private var levels: [Key: (shown: Float, at: Double)] = [:]

    func record(_ shown: Float, for key: Key, at time: Double) {
        guard shown.isFinite, time.isFinite else { return }
        levels[key] = (max(0, shown), time)
    }

    /// The level a new surface for `key` starts at, while the scene is on screen.
    func shown(for key: Key, at time: Double) -> Float? {
        guard let level = levels[key], time - level.at >= 0, time - level.at < Self.freshness else { return nil }
        return level.shown
    }
}
