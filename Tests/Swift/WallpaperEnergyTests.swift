import AppKit
import Metal
import SwiftUI
import XCTest
@testable import VersoCustom

final class WallpaperEnergyTests: XCTestCase {
    // MARK: Frame clock

    func testFrameClockRunsWhileAnythingCanChange() {
        let now = Date()
        XCTAssertNil(FrameClockPolicy.quietAfter(.init(backgroundAnimates: true, lyricsAdvance: false, transitionsEnd: [])))
        XCTAssertNil(FrameClockPolicy.quietAfter(.init(backgroundAnimates: false, lyricsAdvance: true, transitionsEnd: [])))
        XCTAssertFalse(FrameClockPolicy.isIdle(quietAfter: nil, now: now))
    }

    func testFrameClockStopsAfterTheLastFadeAndResumesWithNewInput() throws {
        let now = Date()
        let notice = now.addingTimeInterval(6), fade = now.addingTimeInterval(0.75)
        // "Nothing playing" fading on a still backdrop: wait for the notice.
        let quiet = try XCTUnwrap(FrameClockPolicy.quietAfter(.init(backgroundAnimates: false, lyricsAdvance: false,
                                                                    transitionsEnd: [fade, notice])))
        XCTAssertEqual(quiet, notice)
        XCTAssertFalse(FrameClockPolicy.isIdle(quietAfter: quiet, now: now.addingTimeInterval(5.9)))
        XCTAssertTrue(FrameClockPolicy.isIdle(quietAfter: quiet, now: now.addingTimeInterval(6)))
        // Nothing pending at all: idle at once.
        let settled = FrameClockPolicy.quietAfter(.init(backgroundAnimates: false, lyricsAdvance: false, transitionsEnd: []))
        XCTAssertTrue(FrameClockPolicy.isIdle(quietAfter: settled, now: now))
        // A song starting is new input and the clock runs again immediately.
        XCTAssertNil(FrameClockPolicy.quietAfter(.init(backgroundAnimates: false, lyricsAdvance: true, transitionsEnd: [])))
    }

    @MainActor
    func testPausedTimelineStillRedrawsWhenItsInputsChange() throws {
        // The idle clock relies on this: a paused TimelineView must still
        // rebuild its content when the view around it changes.
        final class Counter { var builds = 0; var last = "" }
        struct Probe: View {
            let text: String
            let counter: Counter
            var body: some View {
                TimelineView(.animation(minimumInterval: 1.0 / 30, paused: true)) { _ in
                    let _ = { counter.builds += 1; counter.last = text }()
                    Text(text)
                }
            }
        }
        let counter = Counter()
        let hosting = NSHostingView(rootView: Probe(text: "first", counter: counter))
        hosting.frame = CGRect(x: 0, y: 0, width: 200, height: 60)
        let window = NSWindow(contentRect: hosting.frame, styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = hosting
        defer { window.close() }
        hosting.layoutSubtreeIfNeeded()
        let initial = counter.builds
        hosting.rootView = Probe(text: "second", counter: counter)
        hosting.layoutSubtreeIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.1))
        XCTAssertGreaterThan(counter.builds, initial)
        XCTAssertEqual(counter.last, "second")
    }

    // MARK: Shader clock precision

    func testShaderTimeStaysPreciseAfterDaysOfRendering() {
        let start = 60 * 3600.0 // 60 hours of accumulated phase
        let step = 1.0 / 30
        var base = ShaderTimeBase()
        var previous = base.uniforms(time: start, flow: start * 0.7)
        XCTAssertLessThan(previous.x, Float(ShaderTimeBase.period))
        var worst: Double = 0, naiveWorst: Double = 0
        var naivePrevious = Float(start)
        for frame in 1...600 {
            let time = start + Double(frame) * step
            let next = base.uniforms(time: time, flow: time * 0.7)
            worst = max(worst, abs(Double(next.x - previous.x) - step))
            previous = next
            // What passing the raw clock did.
            let naive = Float(time)
            naiveWorst = max(naiveWorst, abs(Double(naive - naivePrevious) - step))
            naivePrevious = naive
        }
        XCTAssertLessThan(worst, 0.0005, "steps stay even")
        XCTAssertGreaterThan(naiveWorst, 0.005, "the raw clock visibly stuttered")
    }

    func testShaderTimeContinuesWithoutJumpingAndRestartsSmall() {
        var base = ShaderTimeBase()
        let first = base.uniforms(time: 50_000, flow: 35_000)
        let later = base.uniforms(time: 50_010, flow: 35_007)
        XCTAssertEqual(Double(later.x - first.x), 10, accuracy: 0.001)
        XCTAssertEqual(Double(later.y - first.y), 7, accuracy: 0.001)
        // A fresh base (a new layer) starts inside one period again.
        var renewed = ShaderTimeBase()
        let restart = renewed.uniforms(time: 500_000, flow: 400_000)
        XCTAssertLessThan(restart.x, Float(ShaderTimeBase.period))
        XCTAssertLessThan(restart.y, Float(ShaderTimeBase.period))
    }

    func testShaderClockRenewsOnlyAtNaturalBreaks() {
        XCTAssertFalse(ShaderEpochPolicy.shouldRenew(age: 120, trigger: .trackChange))
        XCTAssertTrue(ShaderEpochPolicy.shouldRenew(age: 3600, trigger: .trackChange))
        XCTAssertFalse(ShaderEpochPolicy.shouldRenew(age: 60, trigger: .wake))
        XCTAssertTrue(ShaderEpochPolicy.shouldRenew(age: 900, trigger: .wake))
        XCTAssertFalse(ShaderEpochPolicy.shouldRenew(age: 3600, trigger: .elapsed))
        XCTAssertTrue(ShaderEpochPolicy.shouldRenew(age: ShaderEpochPolicy.maximumAge, trigger: .elapsed))
    }

    // MARK: Pixel budget

    func testShaderDrawableIsCappedAndKeepsItsAspect() {
        let budget = ShaderRenderBudget.standard
        // A 14-inch MacBook Pro at 2x: 5.9 MP native.
        let laptop = ShaderRenderBudget.drawableSize(points: CGSize(width: 1512, height: 982), backingScale: 2, budget: budget)
        XCTAssertLessThanOrEqual(laptop.width * laptop.height, budget)
        XCTAssertEqual(laptop.width / laptop.height, 1512.0 / 982.0, accuracy: 0.01)
        // A 6K display: 20 MP native, still under the budget.
        let xdr = ShaderRenderBudget.drawableSize(points: CGSize(width: 3008, height: 1692), backingScale: 2, budget: budget)
        XCTAssertLessThanOrEqual(xdr.width * xdr.height, budget)
        // Small surfaces render natively.
        let small = ShaderRenderBudget.drawableSize(points: CGSize(width: 400, height: 300), backingScale: 2, budget: budget)
        XCTAssertEqual(small, CGSize(width: 800, height: 600))
        XCTAssertEqual(ShaderRenderBudget.drawableSize(points: .zero, backingScale: 2, budget: budget), CGSize(width: 1, height: 1))
        XCTAssertLessThan(ShaderRenderBudget.pixels(for: .fractal, preview: false, economical: false), budget)
        XCTAssertLessThan(ShaderRenderBudget.pixels(for: .plasma, preview: false, economical: true), budget)
        XCTAssertLessThan(ShaderRenderBudget.pixels(for: .plasma, preview: true, economical: false), 300_000)
    }

    // MARK: Colour field

    private static let colourfulPalette = AlbumWallpaperPalette(grid: [
        SIMD3(0.02, 0.85, 0.95), SIMD3(0.1, 0.5, 0.5), SIMD3(0.58, 0.75, 0.85), SIMD3(0.3, 0.4, 0.6),
        SIMD3(0.8, 0.6, 0.7), SIMD3(0.5, 0.5, 0.5), SIMD3(0.12, 0.9, 0.9), SIMD3(0.4, 0.3, 0.3), SIMD3(0.7, 0.7, 0.75),
    ])

    /// The colour field under every pattern, colour-only and power-saving
    /// wallpaper drew its blur at native pixels on every frame.
    func testColourFieldStaysWithinItsPixelBudgetOnLargeDisplays() {
        let budget = ColourFieldBudget.standard
        // A 14-inch MacBook Pro (5.9 MP), a 5K (14.7 MP) and a 6K (20.4 MP) display.
        for display in [CGSize(width: 1512, height: 982), CGSize(width: 2560, height: 1440), CGSize(width: 3008, height: 1692)] {
            let canvas = ColourFieldBudget.canvas(points: display, scale: 2, budget: budget)
            XCTAssertLessThanOrEqual(canvas.pixels(scale: 2), budget, "\(display)")
            XCTAssertGreaterThan(canvas.pixels(scale: 2), budget * 0.99, "\(display): spends what it may")
            XCTAssertEqual((canvas.size.width * 2).truncatingRemainder(dividingBy: 1), 0, "whole pixels")
            XCTAssertEqual((canvas.size.height * 2).truncatingRemainder(dividingBy: 1), 0, "whole pixels")
            // Stretched to cover the display, one canvas pixel past each edge.
            XCTAssertEqual(canvas.size.width * canvas.stretch.width, display.width + 2 * display.width / (canvas.size.width * 2),
                           accuracy: 0.001)
            XCTAssertGreaterThan(canvas.size.height * canvas.stretch.height, display.height)
            XCTAssertEqual(canvas.stretch.width / canvas.stretch.height, 1, accuracy: 0.01, "the aspect is kept")
            let saving = ColourFieldBudget.canvas(points: display, scale: 2, budget: ColourFieldBudget.pixels(economical: true))
            XCTAssertLessThanOrEqual(saving.pixels(scale: 2), budget * 0.6)
        }
        // A small surface (the Settings preview) and an unlimited budget
        // draw natively, unstretched.
        let native = ColourFieldBudget.Canvas(size: CGSize(width: 256, height: 144), stretch: CGSize(width: 1, height: 1))
        XCTAssertEqual(ColourFieldBudget.canvas(points: CGSize(width: 256, height: 144), scale: 2, budget: budget), native)
        XCTAssertEqual(ColourFieldBudget.canvas(points: CGSize(width: 1512, height: 982), scale: 2, budget: .infinity).stretch,
                       CGSize(width: 1, height: 1))
        XCTAssertEqual(ColourFieldBudget.factor(points: .zero, scale: 2, budget: budget), 1)
    }

    /// Bounded and native renders of the same field look the same -- it is
    /// heavily blurred, so the compositor's upscale loses nothing visible --
    /// and the stretched canvas leaves no seam along any edge.
    @MainActor
    func testBoundedColourFieldLooksLikeTheNativeOne() throws {
        // A 14-inch MacBook Pro's share of the standard budget, and sizes
        // whose canvas SwiftUI once rounded short of the screen edge.
        let cases: [(size: CGSize, budget: Double)] = [
            (CGSize(width: 640, height: 360), ColourFieldBudget.standard * (640 * 360) / (3024 * 1964)),
            (CGSize(width: 640, height: 360), 26_000), (CGSize(width: 1000, height: 777), 26_000),
            (CGSize(width: 333, height: 777), 5_000),
        ]
        for (size, budget) in cases {
            func render(budget: Double) throws -> [UInt8] {
                let view = AnimatedGradientView(palette: Self.colourfulPalette, time: 40, vividness: 1.2, pixelBudget: budget)
                    .frame(width: size.width, height: size.height)
                let renderer = ImageRenderer(content: view)
                renderer.scale = 1
                return try rgba(try XCTUnwrap(renderer.cgImage), size: size)
            }
            let native = try render(budget: .infinity)
            let bounded = try render(budget: budget)
            XCTAssertEqual(native.count, bounded.count)
            var difference = 0.0, level = 0.0, worst = 0, seams = 0
            for index in stride(from: 0, to: native.count, by: 4) {
                for channel in 0..<3 {
                    let delta = abs(Int(native[index + channel]) - Int(bounded[index + channel]))
                    difference += Double(delta) / 255
                    worst = max(worst, delta)
                    level += Double(native[index + channel]) / 255
                }
                if bounded[index + 3] < 250 { seams += 1 }
            }
            let samples = Double(native.count / 4 * 3)
            let label = "\(Int(size.width))×\(Int(size.height)) at \(Int(budget)) px"
            XCTAssertGreaterThan(level / samples, 0.05, "\(label): the field rendered something")
            // Measured at about 0.003; one second of the field's own motion
            // differs by 0.016.
            XCTAssertLessThan(difference / samples, 0.008, "\(label): mean channel difference")
            XCTAssertLessThanOrEqual(worst, 16, "\(label): largest channel difference")
            XCTAssertEqual(seams, 0, "\(label): see-through pixels along an edge")
        }
    }

    /// What the compositor is handed on screen: the field's layer is the
    /// small canvas, scaled up, not a native-sized offscreen pass.
    @MainActor
    func testTheColourFieldLayerIsDrawnSmallAndScaledUp() throws {
        let size = CGSize(width: 1512, height: 982)
        let hosting = NSHostingView(rootView: AnimatedGradientView(palette: Self.colourfulPalette, time: 12)
            .environment(\.displayScale, 2))
        hosting.frame = CGRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: hosting.frame, styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        defer { window.close() }
        window.contentView = hosting
        hosting.layoutSubtreeIfNeeded()
        window.displayIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.2))
        func layers(_ layer: CALayer) -> [CALayer] { [layer] + (layer.sublayers ?? []).flatMap(layers) }
        let scaled = try XCTUnwrap(layers(try XCTUnwrap(hosting.layer)).first { $0.affineTransform().a > 1.5 },
                                   "the field's drawing layer is scaled up")
        let pixels = Double(scaled.bounds.width * scaled.contentsScale) * Double(scaled.bounds.height * scaled.contentsScale)
        XCTAssertLessThanOrEqual(pixels, ColourFieldBudget.standard * 1.02)
        XCTAssertEqual(scaled.bounds.width * scaled.affineTransform().a, size.width, accuracy: size.width * 0.01)
    }

    // MARK: Metal path

    /// The GPU the effects use, shared with the visualisers.
    @MainActor
    private func shaderGPU() throws -> ShaderGPU {
        _ = try GPUTestSupport.device()
        return try XCTUnwrap(VisualizerGPU.shared.context(for: nil).map(ShaderGPU.init))
    }

    @MainActor
    private func plasmaPipeline(on gpu: ShaderGPU) throws -> MTLRenderPipelineState {
        try gpu.device.makeRenderPipelineState(descriptor:
            try XCTUnwrap(ShaderBackgroundRenderer.descriptor(for: .plasma, library: gpu.library)))
    }

    @MainActor
    func testEveryEffectLinksIntoABackgroundPipeline() throws {
        let gpu = try shaderGPU()
        let group = DispatchGroup()
        var failures: [String] = []
        let lock = NSLock()
        for style in ShaderStyle.allCases {
            let descriptor = try XCTUnwrap(ShaderBackgroundRenderer.descriptor(for: style, library: gpu.library), style.title)
            group.enter()
            gpu.device.makeRenderPipelineState(descriptor: descriptor) { state, error in
                if state == nil {
                    lock.lock(); failures.append("\(style.title): \(error?.localizedDescription ?? "?")"); lock.unlock()
                }
                group.leave()
            }
        }
        XCTAssertEqual(group.wait(timeout: .now() + 120), .success)
        XCTAssertEqual(failures, [])
    }

    @MainActor
    func testAFailedEffectDrawsTheFallbackAndIsTriedAgainLater() throws {
        let gpu = try shaderGPU()
        let plasma = try plasmaPipeline(on: gpu)
        var now: TimeInterval = 100
        var failing: Set<ShaderStyle> = [.helix]
        var requests: [ShaderStyle] = []
        let cache = ShaderPipelineCache(compiler: { style, _, done in
            requests.append(style)
            done(failing.contains(style) ? nil : plasma)
            return true
        }, gpus: { _ in gpu }, clock: { now })

        XCTAssertFalse(cache.isSettled(.helix, on: gpu))
        // The failure settles the effect, so a crossfade into it can go
        // ahead, and the fallback is drawn instead of nothing.
        let first = cache.pipeline(for: .helix, on: gpu)
        XCTAssertTrue(cache.isSettled(.helix, on: gpu))
        XCTAssertTrue(first === plasma)
        XCTAssertEqual(requests, [.helix, ShaderPipelineCache.fallback])
        // Not retried on every frame...
        now += 10
        XCTAssertTrue(cache.pipeline(for: .helix, on: gpu) === plasma)
        XCTAssertEqual(requests.count, 2)
        // ...but again once the retry interval has passed.
        now += ShaderPipelineCache.retryInterval
        failing = []
        XCTAssertNotNil(cache.pipeline(for: .helix, on: gpu))
        XCTAssertEqual(requests, [.helix, ShaderPipelineCache.fallback, .helix])
        XCTAssertEqual(cache.pipeline(for: .helix, on: gpu).map { ObjectIdentifier($0) }, ObjectIdentifier(plasma))
        XCTAssertEqual(requests.count, 3, "compiled effects are kept")
    }

    @MainActor
    func testAnEffectStillCompilingIsNotSettled() throws {
        let gpu = try shaderGPU()
        let plasma = try plasmaPipeline(on: gpu)
        var pending: [@MainActor (MTLRenderPipelineState?) -> Void] = []
        var now: TimeInterval = 50
        let cache = ShaderPipelineCache(compiler: { _, _, done in pending.append(done); return true }, gpus: { _ in gpu },
                                        clock: { now })
        XCTAssertFalse(cache.isCompiling)
        cache.prepare(.aurora, display: nil)
        cache.prepare(.aurora, display: nil)
        XCTAssertEqual(pending.count, 1, "one compile per effect")
        XCTAssertNil(cache.pipeline(for: .aurora, on: gpu))
        XCTAssertFalse(cache.isSettled(.aurora, display: nil), "a crossfade into it waits")
        XCTAssertTrue(cache.isCompiling, "so do the windows leaving Your wallpaper")
        XCTAssertNil(cache.lastSettledAt)
        now = 52.5
        pending[0](plasma)
        XCTAssertTrue(cache.isSettled(.aurora, display: nil))
        XCTAssertNotNil(cache.pipeline(for: .aurora, on: gpu))
        XCTAssertFalse(cache.isCompiling)
        XCTAssertEqual(cache.lastSettledAt, 52.5)
    }

    // MARK: GPU choice

    /// The effects never ask for the system default device, which on a
    /// dual-GPU MacBook Pro is the discrete GPU and keeps it powered: each
    /// surface takes the GPU driving its display, with the visualisers' queue.
    @MainActor
    func testEffectsDrawOnTheGPUDrivingTheirDisplay() throws {
        _ = try GPUTestSupport.device()
        let cache = ShaderPipelineCache()
        let preferred = try XCTUnwrap(cache.gpu(for: nil))
        XCTAssertEqual(preferred.id, VisualizerGPU.preferredDevice(for: nil)?.registryID)
        let shared = try XCTUnwrap(VisualizerGPU.shared.context(for: nil))
        XCTAssertTrue(preferred.queue === shared.queue, "One queue per GPU, shared with the visualisers")
        XCTAssertTrue(preferred.library === shared.library)
        for display in NSScreen.screens.compactMap(\.versoDisplayID) {
            XCTAssertEqual(cache.gpu(for: display)?.id, CGDirectDisplayCopyCurrentMetalDevice(display)?.registryID,
                           "A wallpaper's effect renders on the GPU that drives its display")
        }
        let renderer = ShaderBackgroundRenderer(cache: cache)
        XCTAssertEqual(renderer.adoptDisplayGPU()?.registryID, preferred.id)
        XCTAssertEqual(renderer.gpu?.id, preferred.id)
    }

    @MainActor
    func testEachGPUCompilesItsOwnPipelines() throws {
        let real = try shaderGPU()
        let plasma = try plasmaPipeline(on: real)
        let integrated = ShaderGPU(id: 1, device: real.device, queue: real.queue, library: real.library)
        let discrete = ShaderGPU(id: 2, device: real.device, queue: real.queue, library: real.library)
        var requests: [UInt64] = []
        var pending: [@MainActor (MTLRenderPipelineState?) -> Void] = []
        let cache = ShaderPipelineCache(compiler: { _, gpu, done in
            requests.append(gpu.id)
            pending.append(done)
            return true
        }, gpus: { $0 == 7 ? discrete : integrated })
        cache.prepare(.aurora, display: nil)
        XCTAssertEqual(requests, [1])
        pending[0](plasma)
        XCTAssertTrue(cache.isSettled(.aurora, display: nil))
        XCTAssertFalse(cache.isSettled(.aurora, display: 7), "A display on the other GPU still waits")
        XCTAssertNil(cache.pipeline(for: .aurora, on: discrete))
        XCTAssertEqual(requests, [1, 2])
        XCTAssertNotNil(cache.pipeline(for: .aurora, on: integrated))
        XCTAssertEqual(requests, [1, 2], "Compiled once per GPU")
    }

    /// Automatic graphics switching can move a display to the other GPU
    /// without changing its ID, so its GPU is looked up again -- but not on
    /// every frame.
    @MainActor
    func testADisplaysGPUIsLookedUpAgainEveryCoupleOfSeconds() throws {
        let real = try shaderGPU()
        let integrated = ShaderGPU(id: 1, device: real.device, queue: real.queue, library: real.library)
        let discrete = ShaderGPU(id: 2, device: real.device, queue: real.queue, library: real.library)
        var now = 100.0, lookups = 0
        var driving = integrated
        let cache = ShaderPipelineCache(compiler: { _, _, _ in true }, gpus: { _ in lookups += 1; return driving },
                                        clock: { now })
        XCTAssertEqual(cache.gpu(for: 3)?.id, 1)
        now += 1
        XCTAssertEqual(cache.gpu(for: 3)?.id, 1)
        XCTAssertEqual(lookups, 1, "Not every frame")
        driving = discrete
        now += ShaderPipelineCache.deviceCheckInterval
        XCTAssertEqual(cache.gpu(for: 3)?.id, 2)
        XCTAssertEqual(lookups, 2)
        now -= 30
        XCTAssertEqual(cache.gpu(for: 3)?.id, 2)
        XCTAssertEqual(lookups, 3, "A clock that went back")
    }

    /// A display moving to another GPU -- a dual-GPU MacBook Pro switching,
    /// an external display plugged in -- must not hide the effect on screen
    /// while it compiles there, since the surface keeps drawing it on the GPU
    /// it has. Only a layer that has never had anything to draw holds its
    /// fade; holding it again brought the outgoing layer back, which after
    /// leaving Your wallpaper is black.
    @MainActor
    func testAGPUChangeLeavesTheEffectOnScreenAtFullOpacity() throws {
        let real = try shaderGPU()
        let plasma = try plasmaPipeline(on: real)
        let integrated = ShaderGPU(id: 1, device: real.device, queue: real.queue, library: real.library)
        let discrete = ShaderGPU(id: 2, device: real.device, queue: real.queue, library: real.library)
        var now = 100.0
        var driving = integrated
        var pending: [@MainActor (MTLRenderPipelineState?) -> Void] = []
        let cache = ShaderPipelineCache(compiler: { _, _, done in pending.append(done); return true },
                                        gpus: { _ in driving }, clock: { now })
        typealias Layer = NowPlayingView.BackgroundLayer
        let effect = Layer.shader(.aurora, epoch: 0)
        let duration = 0.75
        // What NowPlayingView asks the cache about the wallpaper on display 7.
        func canDraw() -> Bool { cache.isSettled(.aurora, display: 7) }

        // Leaving Your wallpaper for an effect still compiling: the desktop
        // stays on screen and the fade holds.
        let start = Date(timeIntervalSinceReferenceDate: 1_000)
        var crossfade = BackgroundCrossfade<Layer>(.desktop)
        cache.prepare(.aurora, display: 7)
        crossfade.change(to: effect, ready: canDraw(), at: start)
        XCTAssertTrue(crossfade.isHeld)
        XCTAssertEqual(crossfade.fade(at: start.addingTimeInterval(5), duration: duration), 0)
        XCTAssertEqual(crossfade.layers(fade: 0), [.desktop, effect])
        XCTAssertEqual(crossfade.opacity(of: effect, fade: 0), 0)
        XCTAssertEqual(crossfade.opacity(of: .desktop, fade: 0), 1)

        // Compiled: the fade runs from then, to the end.
        pending.removeFirst()(plasma)
        XCTAssertTrue(canDraw())
        let ready = start.addingTimeInterval(6)
        crossfade.becameReady(at: ready)
        XCTAssertEqual(crossfade.fade(at: ready, duration: duration), 0)
        let shown = ready.addingTimeInterval(1)
        XCTAssertEqual(crossfade.fade(at: shown, duration: duration), 1)
        XCTAssertEqual(crossfade.layers(fade: 1), [effect])

        // The display moves to the other GPU, where the effect has not
        // compiled: the effect stays on screen at full opacity.
        driving = discrete
        now += ShaderPipelineCache.deviceCheckInterval
        XCTAssertFalse(canDraw(), "A surface new to that GPU would have nothing to draw")
        XCTAssertFalse(crossfade.isHeld)
        let moved = shown.addingTimeInterval(1)
        let fade = crossfade.fade(at: moved, duration: duration)
        XCTAssertEqual(fade, 1)
        XCTAssertEqual(crossfade.layers(fade: fade), [effect], "The outgoing layer never comes back")
        XCTAssertEqual(crossfade.opacity(of: effect, fade: fade), 1)
        // Compiled there as well: nothing fades in again.
        cache.prepare(.aurora, display: 7)
        pending.removeFirst()(plasma)
        XCTAssertTrue(canDraw())
        crossfade.becameReady(at: moved)
        XCTAssertEqual(crossfade.fade(at: moved, duration: duration), 1)

        // Leaving it fades the effect out, not the layer before it.
        let colour = Layer.generated(pattern: nil, seed: 0)
        crossfade.change(to: colour, ready: true, at: moved)
        XCTAssertEqual(crossfade.layers(fade: crossfade.fade(at: moved, duration: duration)), [effect, colour])
    }

    /// Unplugging an external display wired to a MacBook Pro's discrete GPU
    /// left that GPU's pipelines (and the device behind them) cached for the
    /// rest of the session. A GPU is let go once no display uses it and no
    /// surface still draws with it.
    @MainActor
    func testAGPUNoDisplayOrSurfaceUsesIsLetGo() throws {
        let real = try shaderGPU()
        let plasma = try plasmaPipeline(on: real)
        let integrated = ShaderGPU(id: 1, device: real.device, queue: real.queue, library: real.library)
        let discrete = ShaderGPU(id: 2, device: real.device, queue: real.queue, library: real.library)
        var inUse: Set<UInt64> = [1, 2]
        var compiles: [UInt64] = []
        var pending: [@MainActor (MTLRenderPipelineState?) -> Void] = []
        let cache = ShaderPipelineCache(compiler: { _, gpu, done in
            compiles.append(gpu.id)
            pending.append(done)
            return true
        }, gpus: { $0 == 7 ? discrete : integrated }, gpusInUse: { inUse })
        cache.prepare(.aurora, display: 7)
        cache.prepare(.aurora, display: nil)
        pending.forEach { $0(plasma) }
        pending.removeAll()
        XCTAssertTrue(cache.isSettled(.aurora, on: discrete))

        // The external display is unplugged, but a surface still draws with
        // that GPU until its new one is ready: it is kept.
        var renderer: ShaderBackgroundRenderer? = ShaderBackgroundRenderer(cache: cache)
        renderer?.displayID = 7
        _ = renderer?.adoptDisplayGPU()
        XCTAssertEqual(renderer?.gpu?.id, 2)
        inUse = [1]
        cache.releaseUnusedGPUs()
        XCTAssertNotNil(cache.compiledPipeline(for: .aurora, on: discrete), "Still drawn with")

        // Once nothing draws with it, it is let go; the GPU in use keeps its
        // effects, and nothing is compiled again.
        renderer = nil
        cache.releaseUnusedGPUs()
        XCTAssertNil(cache.compiledPipeline(for: .aurora, on: discrete))
        XCTAssertFalse(cache.isSettled(.aurora, on: discrete))
        XCTAssertTrue(cache.isSettled(.aurora, on: integrated))
        XCTAssertNotNil(cache.compiledPipeline(for: .aurora, on: integrated))
        XCTAssertEqual(compiles, [2, 1])

        // A compile for it that was still running is not kept either.
        cache.prepare(.helix, display: 7)
        XCTAssertTrue(cache.isCompiling)
        cache.releaseUnusedGPUs()
        XCTAssertFalse(cache.isCompiling, "Nothing waits for it")
        pending.removeFirst()(plasma)
        XCTAssertFalse(cache.isSettled(.helix, on: discrete))
        XCTAssertNil(cache.compiledPipeline(for: .helix, on: discrete))
    }

    /// A surface whose display moved keeps drawing with what its old GPU
    /// already has, but never compiles anything there again: that would put
    /// the GPU it is leaving back to work.
    @MainActor
    func testASurfaceNeverCompilesOnTheGPUItIsLeaving() async throws {
        let real = try shaderGPU()
        let plasma = try plasmaPipeline(on: real)
        let integrated = ShaderGPU(id: 1, device: real.device, queue: real.queue, library: real.library)
        let discrete = ShaderGPU(id: 2, device: real.device, queue: real.queue, library: real.library)
        var now = 100.0
        var driving = discrete
        var compiles: [String] = []
        var pending: [@MainActor (MTLRenderPipelineState?) -> Void] = []
        let cache = ShaderPipelineCache(compiler: { style, gpu, done in
            compiles.append("\(style.rawValue)@\(gpu.id)")
            pending.append(done)
            return true
        }, gpus: { _ in driving }, gpusInUse: { [driving.id] }, clock: { now })
        let renderer = ShaderBackgroundRenderer(cache: cache)
        renderer.displayID = 7
        let view = ShaderSurfaceView.make(drawnBy: renderer)
        renderer.frame = .init(style: .aurora, time: 3, flow: 2,
                               palette: AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette),
                               vividness: 1, budget: ShaderRenderBudget.preview)
        let window = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 96, height: 54), styleMask: .borderless,
                              backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        defer { window.close() }
        window.contentView = view
        /// Draws until a frame is presented: at most two are in flight.
        func present() async throws {
            let before = renderer.framesPresented
            let after = try await GPUTestSupport.eventually({ () -> Int in
                view.draw()
                return renderer.framesPresented
            }, until: { $0 > before })
            XCTAssertGreaterThan(after, before)
        }

        view.draw()
        XCTAssertEqual(compiles, ["aurora@2"])
        pending.removeFirst()(plasma)
        try await present()
        XCTAssertEqual(renderer.gpu?.id, 2)

        // The display moves to the other GPU: the surface draws on with what
        // it has while the effect compiles there...
        driving = integrated
        now += ShaderPipelineCache.deviceCheckInterval
        try await present()
        XCTAssertEqual(compiles, ["aurora@2", "aurora@1"])
        XCTAssertEqual(renderer.gpu?.id, 2)
        cache.releaseUnusedGPUs()
        XCTAssertNotNil(cache.compiledPipeline(for: .aurora, on: discrete), "Still drawn with")

        // ...and a new effect meanwhile is compiled only where it is going.
        renderer.frame?.style = .silk
        let requested = try await GPUTestSupport.eventually({ () -> [String] in
            view.draw()
            return compiles
        }, until: { $0.count > 2 })
        XCTAssertEqual(requested, ["aurora@2", "aurora@1", "silk@1"])
        XCTAssertEqual(renderer.gpu?.id, 2)

        // Ready there: it moves, and the GPU it left is let go.
        pending.forEach { $0(plasma) }
        pending.removeAll()
        try await present()
        XCTAssertEqual(renderer.gpu?.id, 1)
        cache.releaseUnusedGPUs()
        XCTAssertNil(cache.compiledPipeline(for: .aurora, on: discrete))
        XCTAssertNotNil(cache.compiledPipeline(for: .silk, on: integrated))
    }

    /// The visualisers' contexts for GPUs nothing uses are let go too, while
    /// the GPU in use keeps its own -- one queue and library per GPU.
    @MainActor
    func testVisualizerContextsOfGPUsNothingUsesAreLetGo() throws {
        _ = try GPUTestSupport.device()
        let gpus = VisualizerGPU()
        let first = try XCTUnwrap(gpus.context(for: nil))
        XCTAssertTrue(VisualizerGPU.devicesInUse().contains(first.device.registryID),
                      "The GPU preferred for the Settings preview is always in use")
        gpus.releaseContexts(keeping: [first.device.registryID])
        XCTAssertTrue(gpus.context(for: nil) === first)
        gpus.releaseContexts(keeping: [])
        XCTAssertFalse(try XCTUnwrap(gpus.context(for: nil)) === first)
    }

    /// A layer replaced before it ever had anything to draw was never seen,
    /// so the fade carries on from the one on screen.
    func testALayerThatNeverDrewIsSkippedByTheNextFade() {
        let start = Date(timeIntervalSinceReferenceDate: 500)
        var crossfade = BackgroundCrossfade<String>("desktop")
        crossfade.change(to: "aurora", ready: false, at: start)
        crossfade.change(to: "plasma", ready: false, at: start.addingTimeInterval(1))
        XCTAssertEqual(crossfade.previous, "desktop")
        crossfade.becameReady(at: start.addingTimeInterval(2))
        XCTAssertEqual(crossfade.layers(fade: 0.5), ["desktop", "plasma"])
        XCTAssertEqual(crossfade.opacity(of: "plasma", fade: 0.5), 0.5)
        XCTAssertEqual(crossfade.opacity(of: "desktop", fade: 0.5), 0.5)
        // A surface's first layer is shown at once.
        let first = BackgroundCrossfade<String>("aurora", ready: false)
        XCTAssertEqual(first.layers(fade: 0), ["aurora"])
        XCTAssertEqual(first.opacity(of: "aurora", fade: 0), 1)
    }

    // MARK: Paused surfaces

    /// A wallpaper built while it is paused -- launched with Pause wallpaper
    /// on, a display added while covered, a mode change while paused --
    /// showed nothing at all until it resumed. It now shows one still frame,
    /// once it has a size and a window and the effect has compiled.
    @MainActor
    func testAnEffectMadeWhilePausedShowsOneStillFrame() async throws {
        let gpu = try shaderGPU()
        let renderer = ShaderBackgroundRenderer(cache: ShaderPipelineCache(gpus: { _ in gpu }))
        let view = ShaderSurfaceView.make(drawnBy: renderer)
        renderer.frame = .init(style: .aurora, time: 3, flow: 2,
                               palette: AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette),
                               vividness: 1, budget: ShaderRenderBudget.preview)
        renderer.paused = true
        renderer.showStillIfNeeded(in: view)
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(renderer.framesPresented, 0, "Nothing to draw into yet")

        // Laid out in its window (never put on screen): the still follows
        // as soon as the effect has compiled.
        let window = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 96, height: 54), styleMask: .borderless,
                              backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        defer { window.close() }
        window.contentView = view
        let shown = try await GPUTestSupport.eventually({ renderer.framesPresented }, until: { $0 > 0 })
        XCTAssertEqual(shown, 1)

        // Paused with a frame on screen, nothing more is drawn...
        renderer.showStillIfNeeded(in: view)
        window.setContentSize(CGSize(width: 100, height: 56))
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(renderer.framesPresented, 1)
        // ...until the frame clock runs again.
        renderer.paused = false
        view.draw()
        XCTAssertEqual(renderer.framesPresented, 2)
    }

    /// A surface whose display moved to another GPU keeps drawing where it is
    /// until the effect has compiled there, rather than going blank.
    func testASurfaceFollowsItsDisplaysGPUOnceTheEffectIsReadyThere() {
        typealias Renderer = ShaderBackgroundRenderer
        XCTAssertEqual(Renderer.choose(current: nil, display: 1, readyOnCurrent: false, readyOnDisplay: true), .display)
        XCTAssertEqual(Renderer.choose(current: nil, display: 1, readyOnCurrent: false, readyOnDisplay: false), .none)
        XCTAssertEqual(Renderer.choose(current: 1, display: 1, readyOnCurrent: true, readyOnDisplay: true), .current)
        XCTAssertEqual(Renderer.choose(current: 1, display: 2, readyOnCurrent: true, readyOnDisplay: false), .current)
        XCTAssertEqual(Renderer.choose(current: 1, display: 2, readyOnCurrent: true, readyOnDisplay: true), .display)
        XCTAssertEqual(Renderer.choose(current: 1, display: 2, readyOnCurrent: false, readyOnDisplay: false), .none)
        // No GPU found for the display: whatever already draws carries on.
        XCTAssertEqual(Renderer.choose(current: 1, display: nil, readyOnCurrent: true, readyOnDisplay: false), .current)
        XCTAssertEqual(Renderer.choose(current: nil, display: nil, readyOnCurrent: false, readyOnDisplay: false), .none)
    }

    /// The direct renderer must look like the SwiftUI pipeline it replaced:
    /// the effect, the album gradient (interpolated perceptually) blended in
    /// `.color` mode, then saturation.
    @MainActor
    func testDirectRenderMatchesTheSwiftUIEffect() throws {
        let size = CGSize(width: 192, height: 108)
        let palette = AlbumWallpaperPalette(grid: [
            SIMD3(0.02, 0.85, 0.95), SIMD3(0.1, 0.5, 0.5), SIMD3(0.58, 0.75, 0.85), SIMD3(0.3, 0.4, 0.6),
            SIMD3(0.8, 0.6, 0.7), SIMD3(0.5, 0.5, 0.5), SIMD3(0.12, 0.9, 0.9), SIMD3(0.4, 0.3, 0.3), SIMD3(0.7, 0.7, 0.75),
        ])
        for style in [ShaderStyle.plasma, .aurora, .silk, .tidal] {
            let reference = try swiftUIRender(style, palette: palette, size: size, vividness: 1.25)
            let direct = try metalRender(style, palette: palette, size: size, vividness: 1.25)
            XCTAssertEqual(reference.count, direct.count)
            var difference = 0.0, referenceLevel = 0.0
            for index in stride(from: 0, to: reference.count, by: 4) {
                for channel in 0..<3 {
                    difference += abs(Double(reference[index + channel]) - Double(direct[index + channel])) / 255
                    referenceLevel += Double(reference[index + channel]) / 255
                }
            }
            let samples = Double(reference.count / 4 * 3)
            XCTAssertGreaterThan(referenceLevel / samples, 0.02, "\(style.title): reference rendered something")
            // Measured at about 0.002 (half an 8-bit step) on macOS 27.
            XCTAssertLessThan(difference / samples, 0.01, "\(style.title): mean channel difference")
        }
    }

    @MainActor
    private func swiftUIRender(_ style: ShaderStyle, palette: AlbumWallpaperPalette, size: CGSize,
                               vividness: Double) throws -> [UInt8] {
        var arguments: [Shader.Argument] = [.float2(size.width, size.height), .float(12.5), .float(8.75),
            .color(AlbumWallpaperPalette.color(palette.stops[0])), .color(AlbumWallpaperPalette.color(palette.stops[2]))]
        if style == .aurora { arguments.append(.float3(0, 0, 0)) }
        let shader = Shader(function: ShaderFunction(library: .default, name: style.functionName), arguments: arguments)
        let view = ZStack {
            Rectangle().colorEffect(shader)
            palette.gradient.blendMode(.color)
        }
        .compositingGroup()
        .saturation(vividness)
        .frame(width: size.width, height: size.height)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        let image = try XCTUnwrap(renderer.cgImage)
        return try rgba(image, size: size)
    }

    @MainActor
    private func metalRender(_ style: ShaderStyle, palette: AlbumWallpaperPalette, size: CGSize,
                             vividness: Double) throws -> [UInt8] {
        let gpu = try shaderGPU()
        let device = gpu.device
        let pipeline = try device.makeRenderPipelineState(descriptor:
            try XCTUnwrap(ShaderBackgroundRenderer.descriptor(for: style, library: gpu.library)))
        let width = Int(size.width), height = Int(size.height)
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: width, height: height, mipmapped: false)
        descriptor.usage = [.renderTarget]
        descriptor.storageMode = .managed
        let texture = try XCTUnwrap(device.makeTexture(descriptor: descriptor))
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = texture
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].storeAction = .store
        let queue = try XCTUnwrap(device.makeCommandQueue())
        let command = try XCTUnwrap(queue.makeCommandBuffer())
        let frame = ShaderBackgroundRenderer.Frame(style: style, time: 12.5, flow: 8.75, palette: palette,
                                                   vividness: vividness, budget: ShaderRenderBudget.standard)
        XCTAssertTrue(ShaderBackgroundRenderer.encode(pipeline, frame: frame, clock: SIMD2(12.5, 8.75),
                                                      points: size, pixels: size, pass: pass, command: command))
        let blit = try XCTUnwrap(command.makeBlitCommandEncoder())
        blit.synchronize(resource: texture)
        blit.endEncoding()
        command.commit()
        command.waitUntilCompleted()
        var bgra = [UInt8](repeating: 0, count: width * height * 4)
        texture.getBytes(&bgra, bytesPerRow: width * 4, from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
        var rgba = bgra
        for index in stride(from: 0, to: bgra.count, by: 4) {
            rgba[index] = bgra[index + 2]
            rgba[index + 2] = bgra[index]
        }
        return rgba
    }

    private func rgba(_ image: CGImage, size: CGSize) throws -> [UInt8] {
        let width = Int(size.width), height = Int(size.height)
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        try pixels.withUnsafeMutableBytes { buffer in
            let context = try XCTUnwrap(CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                bytesPerRow: width * 4, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
        return pixels
    }
}
