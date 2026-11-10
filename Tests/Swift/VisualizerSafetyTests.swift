import AppKit
import MediaAccessibility
import Metal
import XCTest
@testable import VersoCustom

final class VisualizerSafetyTests: XCTestCase {
    func testCalmRemovesTransientsAndOnlyReduceMotionSlowsTheScene() {
        let shipped = VisualizerResponse()
        XCTAssertFalse(shipped.calm)
        XCTAssertEqual(shipped.transientScale, 1)
        XCTAssertEqual(shipped.motionScale, 1)

        let dimmed = VisualizerResponse(dimFlashingLights: true, punch: 2)
        XCTAssertTrue(dimmed.calm)
        XCTAssertEqual(dimmed.transientScale, 0, "Dim Flashing Lights overrides Punch")
        XCTAssertEqual(dimmed.motionScale, 1, "Dim Flashing Lights is about flashes, not travel")
        XCTAssertLessThan(dimmed.patternRate, 1)

        let reduced = VisualizerResponse(reduceMotion: true)
        XCTAssertTrue(reduced.calm)
        XCTAssertEqual(reduced.motionScale, 0.15, accuracy: 0.0001)
        XCTAssertEqual(VisualizerResponse(punch: 7).transientScale, 2, "Punch is clamped to its slider range")
        XCTAssertEqual(VisualizerResponse(punch: .nan).transientScale, 1)
    }

    @MainActor
    func testPunchDefaultReproducesTheShippedBeatAndCalmZeroesIt() {
        var sound = AudioSnapshot()
        sound.beat = 1; sound.trebleBeat = 1; sound.flux = 1
        func uniforms(reduced: Bool, punch: Double = 1) -> VizUniforms {
            AudioVisualizerView.Coordinator.uniforms(style: .aurora, size: CGSize(width: 64, height: 36), time: 3,
                frame: sound, sensitivity: 1, speed: 1, reduced: reduced, punch: punch)
        }
        let shipped = uniforms(reduced: false)
        XCTAssertEqual(shipped.beat, 0.20, accuracy: 0.0001)
        XCTAssertEqual(shipped.trebleBeat, 0.12, accuracy: 0.0001)
        XCTAssertEqual(shipped.flux, 0.14, accuracy: 0.0001)
        XCTAssertEqual(uniforms(reduced: false, punch: 2).beat, 0.40, accuracy: 0.0001)
        XCTAssertEqual(uniforms(reduced: false, punch: 0).beat, 0)
        let calm = uniforms(reduced: true, punch: 2)
        XCTAssertEqual([calm.beat, calm.trebleBeat, calm.flux], [0, 0, 0])
        XCTAssertEqual(calm.reduceMotion, 1)
        XCTAssertEqual(calm.time, 3, "The timeline, not the uniforms, applies Reduce Motion's rate")
    }

    func testNativeWavefieldDropsPulsesAndUnamplifiedEnergyWhenCalm() {
        var sound = AudioSnapshot()
        let rate = 48_000.0
        let analyzer = AudioAnalyzer()
        for pass in 0..<18 {
            let pcm = (0..<2048).map { Float(0.25 * sin(2 * .pi * 220 * Double(pass * 2048 + $0) / rate)) }
            pcm.withUnsafeBufferPointer { analyzer.append($0.baseAddress!, count: 2048, sampleRate: rate) }
        }
        sound = analyzer.latest()
        sound.beat = 1; sound.flux = 1; sound.trebleBeat = 1
        var lively = WavefieldModes(), calm = WavefieldModes()
        var loud = lively.frame(sound, at: 0, time: 0, sensitivity: 3, reduced: false)
        var quiet = calm.frame(sound, at: 0, time: 0, sensitivity: 3, reduced: true)
        for step in 1...30 {
            loud = lively.frame(sound, at: Double(step) / 60, time: 0, sensitivity: 3, reduced: false)
            quiet = calm.frame(sound, at: Double(step) / 60, time: 0, sensitivity: 3, reduced: true)
        }
        XCTAssertGreaterThan(loud.pulse, 0.5)
        XCTAssertEqual(quiet.pulse, 0)
        XCTAssertEqual(quiet.change, 0)
        XCTAssertEqual(SIMD3(quiet.onsetsAndEnergy.x, quiet.onsetsAndEnergy.y, quiet.onsetsAndEnergy.z), .zero)
        XCTAssertTrue(quiet.modes.allSatisfy { $0.drive.y == 0 }, "No mode may pulse while calm")
        XCTAssertLessThanOrEqual(quiet.onsetsAndEnergy.w, loud.onsetsAndEnergy.w)
    }

    func testExposureLimiterCapsRisesAndKeepsFallsImmediate() {
        var limiter = ExposureLimiter()
        let dt: Float = 1 / 60
        // A new surface fades in from black at the capped rate.
        XCTAssertEqual(limiter.gain(forMean: 0.4, dt: 0), 0, accuracy: 0.0001)
        var shown: Float = 0
        for _ in 0..<30 {
            _ = limiter.gain(forMean: 0.4, dt: dt)
            let next = limiter.shown ?? 0
            XCTAssertLessThanOrEqual(next - shown, ExposureLimiter.maxRisePerSecond * dt + 0.00001)
            shown = next
        }
        // A 10 Hz strobe between dark and bright frames.
        var previous = limiter.shown ?? 0, risen: Float = 0
        for frame in 0..<60 {
            let mean: Float = (frame / 3) % 2 == 0 ? 0.05 : 0.6
            let gain = limiter.gain(forMean: mean, dt: dt)
            let displayed = mean * pow(gain, 2.2)
            XCTAssertEqual(displayed, limiter.shown ?? -1, accuracy: 0.002)
            if displayed > previous { risen += displayed - previous }
            previous = displayed
        }
        XCTAssertLessThanOrEqual(risen, ExposureLimiter.maxRisePerSecond * 1.0 + 0.001,
            "Over one second the display may brighten by at most the capped rise in total")
        // Falls are immediate; a stall cannot release a flash.
        XCTAssertEqual(limiter.gain(forMean: 0.01, dt: dt), 1)
        _ = limiter.gain(forMean: 0.9, dt: 30)
        XCTAssertLessThanOrEqual(limiter.shown ?? 1, 0.01 + ExposureLimiter.maxRisePerSecond * ExposureLimiter.maxStep + 0.0001)
        // Turning the limiter on keeps the current brightness.
        limiter.adopt()
        XCTAssertEqual(limiter.gain(forMean: 0.7, dt: dt), 1)
        XCTAssertEqual(limiter.gain(forMean: .nan, dt: .nan), 1)
    }

    @MainActor
    func testDimFlashingLightsRefreshReadsTheInjectedSetting() {
        var enabled = false
        let safety = VisualizerSafety(read: { enabled }, observe: false)
        XCTAssertFalse(safety.dimFlashingLights)
        enabled = true
        safety.refresh()
        XCTAssertTrue(safety.dimFlashingLights)
    }

    /// The shipping reader is the system setting, and both change
    /// notifications re-read it. Posting them in-process changes nothing
    /// outside this test; the reader is injected so this Mac's setting
    /// never decides the outcome.
    @MainActor
    func testDimFlashingLightsFollowsTheSystemNotifications() async throws {
        XCTAssertEqual(VisualizerSafety(observe: false).dimFlashingLights, MADimFlashingLightsEnabled(),
                       "Without an injected reader the system setting is read")
        var enabled = false
        let safety = VisualizerSafety(read: { enabled }, observe: true)
        XCTAssertFalse(safety.dimFlashingLights)
        enabled = true
        NotificationCenter.default.post(name: VisualizerSafety.changedNotification, object: nil)
        let on = try await GPUTestSupport.eventually({ safety.dimFlashingLights }, until: { $0 })
        XCTAssertTrue(on, "MediaAccessibility's change notification")
        enabled = false
        NSWorkspace.shared.notificationCenter.post(name: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification,
                                                   object: nil)
        let off = try await GPUTestSupport.eventually({ safety.dimFlashingLights }, until: { !$0 })
        XCTAssertFalse(off, "The display-options notification")
    }

    @MainActor
    func testNotificationTokensStopObservingWithTheirOwner() async throws {
        final class Count: @unchecked Sendable { var value = 0 }
        let name = Notification.Name("LunalyrTests.\(UUID().uuidString)")
        let center = NotificationCenter()
        let calls = Count()
        var tokens: NotificationTokens? = NotificationTokens()
        tokens?.observe(name, on: center) { _ in calls.value += 1 }
        XCTAssertEqual(tokens?.count, 1)
        center.post(name: name, object: nil)
        let delivered = try await GPUTestSupport.eventually({ calls.value }, until: { $0 == 1 })
        XCTAssertEqual(delivered, 1)
        tokens = nil
        center.post(name: name, object: nil)
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(calls.value, 1, "Releasing the owner removes its observers")
    }

    @MainActor
    func testVisualiserPreferencesDefaultToTheShippedLookAndPersist() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let isolated = UserDefaults(suiteName: name)!
        defer { isolated.removePersistentDomain(forName: name) }
        let preferences = VisualizerPreferences(defaults: isolated)
        XCTAssertEqual(preferences.punch, 1)
        XCTAssertEqual(preferences.wavefieldColours, .album)
        XCTAssertEqual(preferences.wavecoreColours, .album)
        XCTAssertFalse(preferences.heavyScenesOnMainDisplayOnly)
        XCTAssertNil(preferences.colours(for: .light3D), "Light 3D keeps its own setting")
        preferences.punch = 5
        preferences.setColours(.original, for: .wavefield)
        preferences.heavyScenesOnMainDisplayOnly = true
        let reloaded = VisualizerPreferences(defaults: isolated)
        XCTAssertEqual(reloaded.punch, 2, "A stored value outside the slider is clamped")
        XCTAssertEqual(reloaded.colours(for: .wavefield), .original)
        XCTAssertEqual(reloaded.wavecoreColours, .album)
        XCTAssertTrue(reloaded.heavyScenesOnMainDisplayOnly)
        isolated.set("unknown", forKey: VisualizerPreferences.Key.wavecoreColours)
        XCTAssertEqual(VisualizerPreferences(defaults: isolated).wavecoreColours, .album)
    }

    /// Draws the tunnel at a flat audio level through one surface's limiter:
    /// the probe pass, the measuring kernel and the limited full-size pass
    /// in one command buffer, as AudioVisualizerView does.
    @MainActor
    private final class LimiterRig {
        let device: MTLDevice
        let context: VisualizerGPU.Context
        let pipeline: MTLRenderPipelineState
        let limiter: MTLComputePipelineState
        let width = 160, height = 90

        init() throws {
            device = try GPUTestSupport.device()
            context = try XCTUnwrap(VisualizerGPU.Context(device: device))
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.vertexFunction = context.library.makeFunction(name: "fullscreenVertex")
            descriptor.fragmentFunction = context.library.makeFunction(name: "tunnelFragment")
            descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
            pipeline = try device.makeRenderPipelineState(descriptor: descriptor)
            limiter = try device.makeComputePipelineState(
                function: XCTUnwrap(context.library.makeFunction(name: "exposureLimiter")))
        }

        func draw(_ probe: ExposureProbe, level: Float, at time: Double) throws -> (shown: Float, displayed: Float) {
            let response = VisualizerResponse(dimFlashingLights: true)
            var sound = AudioSnapshot()
            sound.level = level; sound.bass = level; sound.mid = level; sound.treble = level
            sound.bands = [Float](repeating: level, count: AudioSnapshot.bandCount)
            sound.components = [Float](repeating: level, count: AudioSnapshot.componentCount)
            let uniforms = AudioVisualizerView.Coordinator.uniforms(style: .tunnel,
                size: CGSize(width: width, height: height), time: 12, frame: sound,
                sensitivity: 3, speed: 1, reduced: response.calm)
            var modes = WavefieldModes()
            let inputs = AudioVisualizerView.Coordinator.sceneInputs(sound, style: .tunnel, uniforms: uniforms,
                wavefield: &modes, at: time, response: response, palette: nil)
            let command = try XCTUnwrap(context.queue.makeCommandBuffer())
            XCTAssertTrue(probe.encode(into: command, pipeline: pipeline, limiter: limiter, at: time) {
                AudioVisualizerView.Coordinator.bind(inputs, to: $0)
            })
            let target = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm,
                width: width, height: height, mipmapped: false)
            target.usage = [.renderTarget]
            target.storageMode = .shared
            let texture = try XCTUnwrap(device.makeTexture(descriptor: target))
            let pass = MTLRenderPassDescriptor()
            pass.colorAttachments[0].texture = texture
            pass.colorAttachments[0].loadAction = .clear
            pass.colorAttachments[0].storeAction = .store
            let encoder = try XCTUnwrap(command.makeRenderCommandEncoder(descriptor: pass))
            encoder.setRenderPipelineState(pipeline)
            AudioVisualizerView.Coordinator.bind(inputs, to: encoder)
            AudioVisualizerView.Coordinator.bindExposure(probe.state, to: encoder)
            encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
            encoder.endEncoding()
            command.commit()
            command.waitUntilCompleted()
            XCTAssertEqual(command.status, .completed, command.error?.localizedDescription ?? "")
            var pixels = [UInt8](repeating: 0, count: width * height * 4)
            pixels.withUnsafeMutableBytes {
                texture.getBytes($0.baseAddress!, bytesPerRow: width * 4,
                    from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
            }
            return (probe.shownLuminance, GPUTestSupport.meanLuminance(bgra: pixels))
        }
    }

    /// The probe pass, the measuring kernel and the limited full-size pass
    /// share one command buffer. A sudden jump to a much brighter frame is
    /// held back to the capped rise, on screen and in the kernel's state.
    @MainActor
    func testNativeLimiterHoldsBackASuddenBrightFrameOnTheGPU() throws {
        let rig = try LimiterRig()
        let probe = try XCTUnwrap(ExposureProbe(device: rig.device))
        probe.adoptCurrentBrightness()

        let dark = try rig.draw(probe, level: 0, at: 0)
        XCTAssertEqual(probe.lastGain, 1, accuracy: 0.001, "Turning the limiter on keeps the current frame")
        let jump = try rig.draw(probe, level: 1, at: 1.0 / 60)
        XCTAssertLessThan(probe.lastGain, 0.9, "The sudden bright frame is dimmed")
        XCTAssertLessThanOrEqual(jump.shown - dark.shown, ExposureLimiter.maxRisePerSecond / 60 + 0.0005)
        XCTAssertLessThan(jump.displayed - dark.displayed, 0.02, "The limited frame on screen barely brightens")
        var later = jump
        for frame in 2...90 { later = try rig.draw(probe, level: 1, at: Double(frame) / 60) }
        XCTAssertGreaterThan(later.displayed, jump.displayed + 0.01, "Brightness still arrives, gradually")
        let fall = try rig.draw(probe, level: 0, at: 91.0 / 60)
        XCTAssertEqual(probe.lastGain, 1, accuracy: 0.001, "Falls are not delayed")
        XCTAssertLessThan(fall.displayed, later.displayed)
    }

    /// NowPlayingView crossfades a scene change by building a new copy of the
    /// outgoing scene. Under Reduce Motion or Dim Flashing Lights that copy's
    /// limiter used to start from black, so every crossfade dipped to black.
    @MainActor
    func testANewSurfaceForAVisibleSceneKeepsItsBrightness() throws {
        let rig = try LimiterRig()
        let memory = ExposureMemory()
        let key = ExposureMemory.Key(style: .tunnel, display: 1, preview: false)

        // The surface showing the scene, settled at its level.
        let showing = try XCTUnwrap(AudioVisualizerView.Coordinator.makeProbe(on: rig.device, for: key, in: memory, at: 0))
        var settled = try rig.draw(showing, level: 0.8, at: 0)
        XCTAssertEqual(showing.lastGain, 0, accuracy: 0.001, "A scene coming on screen fades in")
        for frame in 1...300 {
            settled = try rig.draw(showing, level: 0.8, at: Double(frame) / 60)
            AudioVisualizerView.Coordinator.remember(showing, as: key, in: memory, at: Double(frame) / 60)
        }
        XCTAssertEqual(showing.lastGain, 1, accuracy: 0.001)
        XCTAssertGreaterThan(settled.displayed, 0.02)

        // The crossfade's copy of the same scene, one frame later.
        let now = 301.0 / 60
        let copy = try XCTUnwrap(AudioVisualizerView.Coordinator.makeProbe(on: rig.device, for: key, in: memory, at: now))
        let first = try rig.draw(copy, level: 0.8, at: now)
        XCTAssertEqual(copy.lastGain, 1, accuracy: 0.01, "The copy continues at the brightness on screen")
        XCTAssertEqual(first.displayed, settled.displayed, accuracy: 0.005)

        // Another display, the preview, another scene or a stale level fade in.
        for other in [ExposureMemory.Key(style: .tunnel, display: 2, preview: false),
                      ExposureMemory.Key(style: .tunnel, display: 1, preview: true),
                      ExposureMemory.Key(style: .aurora, display: 1, preview: false)] {
            let fresh = try XCTUnwrap(AudioVisualizerView.Coordinator.makeProbe(on: rig.device, for: other, in: memory, at: now))
            _ = try rig.draw(fresh, level: 0.8, at: now)
            XCTAssertEqual(fresh.lastGain, 0, accuracy: 0.001, "\(other) is not on screen")
        }
        let later = now + ExposureMemory.freshness + 0.1
        XCTAssertNil(memory.shown(for: key, at: later), "The scene left the screen")
        XCTAssertNil(memory.shown(for: key, at: now - 1), "A level from the future is ignored")
        let stale = try XCTUnwrap(AudioVisualizerView.Coordinator.makeProbe(on: rig.device, for: key, in: memory, at: later))
        _ = try rig.draw(stale, level: 0.8, at: later)
        XCTAssertEqual(stale.lastGain, 0, accuracy: 0.001)
    }

    /// Draws one frame of `coordinator`'s prepared scene through
    /// `encodeFrame` into a small offscreen target; nil when it declined.
    @MainActor
    private func drawCoordinator(_ coordinator: AudioVisualizerView.Coordinator, on gpu: VisualizerGPU.Context,
                                 pipeline: MTLRenderPipelineState, at time: Double) throws -> Float? {
        let width = 160, height = 90
        let target = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm,
            width: width, height: height, mipmapped: false)
        target.usage = [.renderTarget]
        target.storageMode = .shared
        let texture = try XCTUnwrap(gpu.device.makeTexture(descriptor: target))
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = texture
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].storeAction = .store
        let command = try XCTUnwrap(gpu.queue.makeCommandBuffer())
        guard coordinator.encodeFrame(pipeline, on: gpu, into: command, pass: pass,
                                      size: CGSize(width: width, height: height), sensitivity: 3, at: time) != nil else {
            return nil
        }
        command.commit()
        command.waitUntilCompleted()
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        pixels.withUnsafeMutableBytes {
            texture.getBytes($0.baseAddress!, bytesPerRow: width * 4,
                from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
        }
        return GPUTestSupport.meanLuminance(bgra: pixels)
    }

    private static func loud() -> AudioSnapshot {
        var sound = AudioSnapshot()
        sound.level = 1; sound.bass = 1; sound.mid = 1; sound.treble = 1
        sound.bands = [Float](repeating: 1, count: AudioSnapshot.bandCount)
        sound.components = [Float](repeating: 1, count: AudioSnapshot.componentCount)
        sound.wavefield.rms = 1
        return sound
    }

    /// While the limiter's kernel is still building, a calm surface used to
    /// draw its first frames unlimited, at the scene's full brightness.
    @MainActor
    func testACalmSurfaceWaitsForItsLimiter() async throws {
        let device = try GPUTestSupport.device()
        let gpu = try XCTUnwrap(VisualizerGPU.Context(device: device))
        let built = try await GPUTestSupport.eventually(timeout: 60, { gpu.pipeline("tunnelFragment") },
                                                        until: { $0 != nil })
        let pipeline = try XCTUnwrap(built)
        let coordinator = AudioVisualizerView.Coordinator()
        let timelines = VisualizerTimelines()
        let response = VisualizerResponse(dimFlashingLights: true)
        coordinator.prepare(style: .tunnel, time: 0, response: response, palette: nil, vividness: nil,
                            sound: Self.loud(), at: 70_000, timelines: timelines)
        XCTAssertNil(try drawCoordinator(coordinator, on: gpu, pipeline: pipeline, at: 70_000),
                     "No calm frame before the limiter is ready")
        _ = try await GPUTestSupport.eventually(timeout: 60, { gpu.exposureLimiter }, until: { $0 != nil })
        let first = try XCTUnwrap(drawCoordinator(coordinator, on: gpu, pipeline: pipeline, at: 70_000.02))
        XCTAssertLessThan(first, 0.002, "The scene then fades in from black")
    }

    /// A display moving to another GPU (automatic graphics switching, or a
    /// window moved to a display on another GPU) used to drop the limiter's
    /// state, and the next frame adopted the scene's full brightness at once.
    @MainActor
    func testMovingToAnotherGPUKeepsTheLimitedBrightness() async throws {
        let device = try GPUTestSupport.device()
        let first = try XCTUnwrap(VisualizerGPU.Context(device: device))
        // A second context stands in for the other GPU: the move always
        // starts a new probe, which is what crossing GPUs needs.
        let second = try XCTUnwrap(VisualizerGPU.Context(device: device))
        var pipelines: [MTLRenderPipelineState] = []
        for gpu in [first, second] {
            let built = try await GPUTestSupport.eventually(timeout: 60, { gpu.pipeline("tunnelFragment") },
                                                            until: { $0 != nil })
            pipelines.append(try XCTUnwrap(built))
            _ = try await GPUTestSupport.eventually(timeout: 60, { gpu.exposureLimiter }, until: { $0 != nil })
        }
        let coordinator = AudioVisualizerView.Coordinator()
        let timelines = VisualizerTimelines()
        let response = VisualizerResponse(dimFlashingLights: true)
        let start = 80_000.0
        var shown: Float = 0
        for frame in 0..<12 {
            let time = start + Double(frame) / 60
            coordinator.prepare(style: .tunnel, time: Double(frame) / 60, response: response, palette: nil,
                                vividness: nil, sound: Self.loud(), at: time, timelines: timelines)
            shown = try XCTUnwrap(drawCoordinator(coordinator, on: first, pipeline: pipelines[0], at: time))
        }
        let time = start + 12.0 / 60
        coordinator.move(to: second, at: time)
        coordinator.prepare(style: .tunnel, time: 12.0 / 60, response: response, palette: nil,
                            vividness: nil, sound: Self.loud(), at: time, timelines: timelines)
        let moved = try XCTUnwrap(drawCoordinator(coordinator, on: second, pipeline: pipelines[1], at: time))
        // Without the hand-over the moved frame shows the unlimited mean.
        coordinator.prepare(style: .tunnel, time: 12.0 / 60, response: VisualizerResponse(), palette: nil,
                            vividness: nil, sound: Self.loud(), at: time + 0.001, timelines: timelines)
        let unlimited = try XCTUnwrap(drawCoordinator(coordinator, on: second, pipeline: pipelines[1], at: time + 0.001))
        XCTAssertGreaterThan(unlimited, shown + 0.02, "The scene is well above what the limiter allowed")
        XCTAssertLessThan(moved - shown, 0.005, "The move does not release the limited brightness")
    }

    /// The scale ladder trims resolution while a scene is too heavy. A
    /// switch to another scene on the same surface kept the old scene's
    /// rung, so a light scene after the Tunnel stayed at half resolution.
    @MainActor
    func testANewSceneStartsAtFullResolution() throws {
        let coordinator = AudioVisualizerView.Coordinator()
        let timelines = VisualizerTimelines()
        coordinator.prepare(style: .tunnel, time: 0, response: VisualizerResponse(), palette: nil, vividness: nil,
                            sound: AudioSnapshot(), at: 90_000, timelines: timelines)
        for _ in 0..<4 { coordinator.ladder.adjust(averageGPUTime: 0.05, budget: 1.0 / 30) }
        XCTAssertEqual(coordinator.ladder.scale, RenderScaleLadder.scales[0])
        coordinator.prepare(style: .tunnel, time: 0.1, response: VisualizerResponse(), palette: nil, vividness: nil,
                            sound: AudioSnapshot(), at: 90_000.1, timelines: timelines)
        XCTAssertEqual(coordinator.ladder.scale, RenderScaleLadder.scales[0], "The same scene keeps its rung")
        coordinator.prepare(style: .silk, time: 0.2, response: VisualizerResponse(), palette: nil, vividness: nil,
                            sound: AudioSnapshot(), at: 90_000.2, timelines: timelines)
        XCTAssertEqual(coordinator.ladder.scale, 1)
        for _ in 0..<4 { coordinator.ladder.adjust(averageGPUTime: 0.05, budget: 1.0 / 30) }
        let device = try GPUTestSupport.device()
        coordinator.move(to: try XCTUnwrap(VisualizerGPU.Context(device: device)), at: 90_000.3)
        XCTAssertEqual(coordinator.ladder.scale, 1, "Another GPU is measured afresh")
    }
}
