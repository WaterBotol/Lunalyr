import Metal
import XCTest
@testable import VersoCustom

/// The native scenes' clock, their shared GPU context and colour richness.
final class VisualizerClockAndDeviceTests: XCTestCase {
    func testSceneClockKeepsFrameStepsAfterDaysOfRendering() {
        let step = 1.0 / 60
        // Precondition: 55 hours in, a Float32 can no longer hold a 60 fps step.
        let wallpaperClock = 55.0 * 3600
        XCTAssertGreaterThan(abs(Double(Float(wallpaperClock + step) - Float(wallpaperClock)) - step), 0.0005)

        // A scene built that late restarts its own clock instead of copying it.
        var timeline = VisualizerTimeline()
        XCTAssertEqual(timeline.advance(to: wallpaperClock, rate: 1, wallClock: 0), 0)
        var previous = timeline.shaderTime
        for frame in 1...600 {
            timeline.advance(to: wallpaperClock + Double(frame) * step, rate: 1, wallClock: Double(frame) * step)
            XCTAssertEqual(Double(timeline.shaderTime - previous), step, accuracy: 0.00005)
            previous = timeline.shaderTime
        }

        // A scene that has run for over an hour keeps running through
        // continuous playback, then restarts at the next natural break.
        var running = VisualizerTimeline()
        running.advance(to: 0, rate: 1, wallClock: 0)
        var source = 0.0
        while running.time <= VisualizerTimeline.rebaseThreshold {
            source += 0.25
            running.advance(to: source, rate: 1, wallClock: source)
        }
        let before = running.time
        XCTAssertEqual(running.advance(to: source + step, rate: 1, wallClock: source + step), before + step, accuracy: 1e-9,
                       "Continuous playback never jumps")
        XCTAssertEqual(running.advance(to: source + 2 * step, rate: 1, wallClock: source + 2 * step, atBreak: true), 0,
                       "A new cover or music returning after a rest restarts the clock")
    }

    func testBreaksOnlyRebaseALargeClockAndPausesCount() {
        var timeline = VisualizerTimeline()
        timeline.advance(to: 10, rate: 1, wallClock: 0)
        XCTAssertEqual(timeline.advance(to: 10.2, rate: 1, wallClock: 0.2, atBreak: true), 10.2, accuracy: 1e-9,
                       "A young scene keeps its clock through a break")
        XCTAssertEqual(timeline.advance(to: 10.2, rate: 1, wallClock: 0.21), 10.2, accuracy: 1e-9,
                       "Another display drawing the same frame changes nothing")
        XCTAssertEqual(timeline.advance(to: 3_610.2, rate: 1, wallClock: 0.3), 10.45, accuracy: 1e-9,
                       "A scene that was off screen resumes where it was")
        var aged = VisualizerTimeline()
        aged.advance(to: 0, rate: 100, wallClock: 0)
        var clock = 0.0
        while aged.time <= VisualizerTimeline.rebaseThreshold {
            clock += 0.25
            aged.advance(to: clock, rate: 100, wallClock: clock)
        }
        XCTAssertEqual(aged.advance(to: clock + 0.1, rate: 100, wallClock: clock + 9), 0, "A wake or long pause is a break")
        var endless = VisualizerTimeline()
        endless.advance(to: 0, rate: 1, wallClock: 0)
        var source = 0.0
        while endless.time > 0 || source == 0 {
            source += 0.25
            endless.advance(to: source, rate: 10_000, wallClock: source)
            if source > 100 { break }
        }
        XCTAssertLessThan(endless.time, VisualizerTimeline.forcedRebase,
                          "A stream with no break at all is rebased once Float32 would fail")
    }

    func testReduceMotionChangesSpeedWithoutJumpingTheScene() {
        var timeline = VisualizerTimeline()
        timeline.advance(to: 100, rate: 1, wallClock: 0)
        let before = timeline.advance(to: 100.1, rate: 1, wallClock: 0.1)
        let slowed = timeline.advance(to: 100.2, rate: 0.15, wallClock: 0.2)
        XCTAssertEqual(slowed - before, 0.015, accuracy: 1e-9, "The old clock × 0.15 jumped to a different scene")
        XCTAssertEqual(timeline.advance(to: 100.15, rate: 1, wallClock: 0.3), slowed, "Never run backwards")
    }

    @MainActor
    func testEverySurfaceSharesOneDeviceQueueAndPipelineCache() throws {
        _ = try GPUTestSupport.device()
        let a = try XCTUnwrap(VisualizerGPU.shared.context(for: nil))
        let b = try XCTUnwrap(VisualizerGPU.shared.context(for: nil))
        XCTAssertTrue(a === b)
        let first = AudioVisualizerView.Coordinator(), preview = AudioVisualizerView.Coordinator(preview: true)
        XCTAssertTrue(first.device === preview.device, "Each display and the preview used to create a device")
        XCTAssertTrue(first.device === a.device)
        for display in NSScreen.screens.compactMap(\.versoDisplayID) {
            let context = try XCTUnwrap(VisualizerGPU.shared.context(for: display))
            XCTAssertEqual(context.device.registryID, CGDirectDisplayCopyCurrentMetalDevice(display)?.registryID,
                           "A surface renders on the GPU that drives its display")
        }
    }

    /// Automatic graphics switching can move a display to the other GPU
    /// without changing its ID, so surfaces re-check every couple of seconds.
    func testSurfacesRecheckTheirDisplaysGPU() {
        typealias Coordinator = AudioVisualizerView.Coordinator
        XCTAssertTrue(Coordinator.shouldCheckDevice(display: 1, current: nil, now: 10, checkedAt: -.infinity))
        XCTAssertFalse(Coordinator.shouldCheckDevice(display: 1, current: 1, now: 10.5, checkedAt: 10), "Not every frame")
        XCTAssertTrue(Coordinator.shouldCheckDevice(display: 1, current: 1, now: 10 + Coordinator.deviceCheckInterval,
                                                    checkedAt: 10), "The same display, checked again")
        XCTAssertTrue(Coordinator.shouldCheckDevice(display: 2, current: 1, now: 10.1, checkedAt: 10), "Another display")
        XCTAssertTrue(Coordinator.shouldCheckDevice(display: 1, current: 1, now: 5, checkedAt: 10), "A clock that went back")
    }

    /// A stand-in inside WebVisualizerView leaves Colour richness to
    /// NowPlayingView, which applies it to the whole web layer.
    @MainActor
    func testStandInsLeaveColourRichnessToTheWebLayer() {
        let standIn = AudioVisualizerView.Coordinator()
        standIn.prepare(style: .starTrails, time: 1, response: VisualizerResponse(), palette: nil, vividness: 1)
        XCTAssertEqual(standIn.shaderVividness, 1)
        let uniforms = AudioVisualizerView.Coordinator.uniforms(style: .starTrails, size: CGSize(width: 8, height: 8),
            time: 1, frame: AudioSnapshot(), sensitivity: 1, speed: 1, reduced: false, vividness: standIn.shaderVividness)
        XCTAssertEqual(uniforms.p15, 1)
    }

    /// xcodebuild forwards only TEST_RUNNER_-prefixed variables to the test
    /// process (without the prefix), so CI sets TEST_RUNNER_LUNALYR_REQUIRE_GPU.
    func testGPURequirementSwitchReadsTheForwardedVariable() {
        XCTAssertTrue(GPUTestSupport.required(in: ["LUNALYR_REQUIRE_GPU": "1"]), "As xcodebuild delivers it")
        XCTAssertTrue(GPUTestSupport.required(in: ["TEST_RUNNER_LUNALYR_REQUIRE_GPU": "1"]), "If a runner keeps the prefix")
        XCTAssertFalse(GPUTestSupport.required(in: [:]))
        XCTAssertFalse(GPUTestSupport.required(in: ["LUNALYR_REQUIRE_GPU": "0"]))
    }

    func testDeviceChoicePrefersTheIntegratedGPU() throws {
        let devices = MTLCopyAllDevices()
        if devices.isEmpty { try GPUTestSupport.unavailable("This test host has no Metal devices.") }
        let chosen = try XCTUnwrap(VisualizerGPU.choose(from: devices, driving: []))
        if let integrated = devices.first(where: { $0.isLowPower && !$0.isRemovable }) {
            XCTAssertEqual(chosen.registryID, integrated.registryID, "With no display on another GPU, stay integrated")
        }
        if let other = devices.first(where: { !$0.isLowPower }) {
            let driven = try XCTUnwrap(VisualizerGPU.choose(from: devices, driving: [other]))
            XCTAssertEqual(driven.registryID, other.registryID, "A display already on the discrete GPU keeps it")
        }
    }

    /// Star Trails, Colour Strata and Overprint applied colour richness only
    /// through the album tint, which does nothing without artwork.
    @MainActor
    func testColourRichnessChangesTheNewerScenesWithoutArtwork() throws {
        let device = try GPUTestSupport.device()
        let library = try device.makeDefaultLibrary(bundle: .main)
        let queue = try XCTUnwrap(device.makeCommandQueue())
        let width = 96, height = 54
        for style in [AudioVisualizerStyle.starTrails, .colourStrata, .overprint] {
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.vertexFunction = library.makeFunction(name: "fullscreenVertex")
            descriptor.fragmentFunction = library.makeFunction(name: try XCTUnwrap(style.fragment))
            descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
            let pipeline = try device.makeRenderPipelineState(descriptor: descriptor)
            func chroma(vividness: Double) throws -> Double {
                let target = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: width, height: height, mipmapped: false)
                target.usage = [.renderTarget]
                target.storageMode = .shared
                let texture = try XCTUnwrap(device.makeTexture(descriptor: target))
                let pass = MTLRenderPassDescriptor()
                pass.colorAttachments[0].texture = texture
                pass.colorAttachments[0].loadAction = .clear
                pass.colorAttachments[0].storeAction = .store
                let command = try XCTUnwrap(queue.makeCommandBuffer())
                let encoder = try XCTUnwrap(command.makeRenderCommandEncoder(descriptor: pass))
                var sound = AudioSnapshot()
                sound.bass = 0.6; sound.mid = 0.5; sound.treble = 0.5; sound.level = 0.5
                var uniforms = AudioVisualizerView.Coordinator.uniforms(style: style, size: CGSize(width: width, height: height),
                    time: 8, frame: sound, sensitivity: 1, speed: 1, reduced: false, vividness: vividness)
                var modes = WavefieldModes()
                encoder.setRenderPipelineState(pipeline)
                AudioVisualizerView.Coordinator.bind(sound, to: encoder, style: style, uniforms: &uniforms,
                    wavefield: &modes, at: 0, reduced: false, palette: nil)
                encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
                encoder.endEncoding(); command.commit(); command.waitUntilCompleted()
                var pixels = [UInt8](repeating: 0, count: width * height * 4)
                pixels.withUnsafeMutableBytes {
                    texture.getBytes($0.baseAddress!, bytesPerRow: width * 4, from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
                }
                return stride(from: 0, to: pixels.count, by: 4).reduce(0.0) { sum, offset in
                    let channels = pixels[offset..<offset + 3].map(Double.init)
                    return sum + channels.max()! - channels.min()!
                } / Double(width * height)
            }
            let muted = try chroma(vividness: 0.4), rich = try chroma(vividness: 1.8)
            XCTAssertGreaterThan(rich, muted * 1.3 + 0.5, "\(style.title) responds to colour richness without artwork")
        }
    }

    /// Fosfora's shader draws its bass and mid ribbons from the narrow
    /// registers the host puts in its option slots, not from the broad bass
    /// and mid registers that overlap the neighbouring ribbons.
    @MainActor
    func testFosforaShaderReadsItsOwnBassAndMidRegisters() throws {
        let device = try GPUTestSupport.device()
        let library = try device.makeDefaultLibrary(bundle: .main)
        let queue = try XCTUnwrap(device.makeCommandQueue())
        let descriptor = MTLRenderPipelineDescriptor()
        descriptor.vertexFunction = library.makeFunction(name: "fullscreenVertex")
        descriptor.fragmentFunction = library.makeFunction(name: try XCTUnwrap(AudioVisualizerStyle.fosforaAurora.fragment))
        descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
        let pipeline = try device.makeRenderPipelineState(descriptor: descriptor)
        let width = 96, height = 96
        func luminance(_ adjust: (inout VizUniforms) -> Void) throws -> Float {
            var uniforms = AudioVisualizerView.Coordinator.uniforms(style: .fosforaAurora,
                size: CGSize(width: width, height: height), time: 4, frame: AudioSnapshot(),
                sensitivity: 1, speed: 1, reduced: false)
            adjust(&uniforms)
            let target = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: width, height: height, mipmapped: false)
            target.usage = [.renderTarget]
            target.storageMode = .shared
            let texture = try XCTUnwrap(device.makeTexture(descriptor: target))
            let pass = MTLRenderPassDescriptor()
            pass.colorAttachments[0].texture = texture
            pass.colorAttachments[0].loadAction = .clear
            pass.colorAttachments[0].storeAction = .store
            let command = try XCTUnwrap(queue.makeCommandBuffer())
            let encoder = try XCTUnwrap(command.makeRenderCommandEncoder(descriptor: pass))
            var modes = WavefieldModes()
            encoder.setRenderPipelineState(pipeline)
            AudioVisualizerView.Coordinator.bind(AudioSnapshot(), to: encoder, style: .fosforaAurora,
                uniforms: &uniforms, wavefield: &modes, at: 0, reduced: false, palette: nil)
            encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
            encoder.endEncoding(); command.commit(); command.waitUntilCompleted()
            var pixels = [UInt8](repeating: 0, count: width * height * 4)
            pixels.withUnsafeMutableBytes {
                texture.getBytes($0.baseAddress!, bytesPerRow: width * 4, from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
            }
            return GPUTestSupport.meanLuminance(bgra: pixels)
        }
        let quiet = try luminance { _ in }
        let broadBass = try luminance { $0.bass = 1 }
        let broadMid = try luminance { $0.mid = 1 }
        let bassRibbon = try luminance { $0[parameter: 3] = 1 }
        let midRibbon = try luminance { $0[parameter: 4] = 1 }
        XCTAssertEqual(broadBass, quiet, accuracy: 0.0005, "The 30–250 Hz register no longer drives a ribbon")
        XCTAssertEqual(broadMid, quiet, accuracy: 0.0005, "Nor does the 250 Hz–2 kHz one")
        // One thin ribbon on a dark frame: a several-fold rise in the mean.
        XCTAssertGreaterThan(bassRibbon, quiet * 3 + 0.001, "60–150 Hz lights the bass ribbon")
        XCTAssertGreaterThan(midRibbon, quiet * 3 + 0.001, "400–1200 Hz lights the mid ribbon")
    }
}
