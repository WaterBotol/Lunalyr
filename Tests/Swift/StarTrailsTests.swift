import Metal
import simd
import XCTest
@testable import VersoCustom

/// Star Trails' CPU simulation, its state buffer and the stars each sound type lights.
final class StarTrailsTests: XCTestCase {
    private typealias L = StarTrailsLayout

    // MARK: Simulation

    func testStateLayoutMatchesTheShader() throws {
        XCTAssertEqual(L.float4Count, 206)
        XCTAssertEqual(L.byteCount, 3296)
        XCTAssertLessThanOrEqual(L.byteCount, 4096, "Small enough for setFragmentBytes on the static path")
        let shader = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("Sources/Visualizers/StarTrails.metal")
        let source = try String(contentsOf: shader, encoding: .utf8)
        func constant(_ name: String) throws -> Int {
            let pattern = try NSRegularExpression(pattern: "constant int \(name) = ([0-9]+);")
            let match = try XCTUnwrap(pattern.firstMatch(in: source, range: NSRange(source.startIndex..., in: source)), name)
            return Int(source[Range(match.range(at: 1), in: source)!])!
        }
        let expected = ["HDR0": L.hdr0, "HDR1": L.hdr1, "HDR2": L.hdr2, "ENV0": L.env0, "ENV1": L.env1,
                        "STAGE": L.stage, "CLK": L.clock, "CRAFT": L.craft, "ONSET": L.onset, "LIVE": L.live,
                        "AST": L.asteroid, "TRAIL": L.trail, "kTrailN": L.trailSamples, "kAst": L.asteroids,
                        "SKY": L.sky, "SKYTINT": L.skyTints, "TRAILCOLOUR": L.trailColours]
        for (name, value) in expected { XCTAssertEqual(try constant(name), value, name) }
        XCTAssertTrue(source.contains("[[buffer(\(L.bufferIndex))]]"), "The shader reads the state at buffer \(L.bufferIndex)")
    }

    /// A seeded, musical input: kicks at 2 Hz, snares on the off-beat, hats at 8 Hz, with exact
    /// onset times on the wall clock, and slowly moving sustained classes.
    private func music(at t: Double) -> StarTrailsSound {
        var s = StarTrailsSound()
        func onsets(_ rate: Double, _ offset: Double) -> (UInt32, Double?) {
            let n = ((t - offset) * rate).rounded(.down)
            return n < 0 ? (0, nil) : (UInt32(n + 1), offset + n / rate)
        }
        (s.kickOnsets, s.kickTime) = onsets(2, 0.1)
        (s.snareOnsets, s.snareTime) = onsets(1, 0.35)
        (s.hatOnsets, s.hatTime) = onsets(8, 0.02)
        func decay(_ time: Double?, _ tau: Double) -> Double { time.map { exp(-max(0, t - $0) / tau) } ?? 0 }
        s.kick = decay(s.kickTime, 0.26); s.snare = decay(s.snareTime, 0.22); s.hat = decay(s.hatTime, 0.15)
        s.bass = 0.5 + 0.3 * sin(t * 1.3); s.vocal = max(0, sin(t * 0.7)); s.pad = 0.4 + 0.2 * sin(t * 0.3)
        s.level = 0.45
        return s
    }

    private func run(fps: Double, seconds: Double, calm: Bool = false) -> StarTrailsSimulation {
        let simulation = StarTrailsSimulation(calm: calm)
        let frames = Int((seconds * fps).rounded())
        for i in 1...frames {
            simulation.advance(wallDelta: 1 / fps, motionDelta: 1 / fps, sound: music(at: Double(i) / fps))
        }
        return simulation
    }

    func testDeterministicAndRepeatedFramesChangeNothing() {
        let a = run(fps: 30, seconds: 4), b = run(fps: 30, seconds: 4)
        let packed = a.pack(aspect: 16.0 / 9)
        XCTAssertEqual(packed, b.pack(aspect: 16.0 / 9))
        XCTAssertEqual(packed, a.pack(aspect: 16.0 / 9), "Packing is pure")
        a.advance(wallDelta: 0, motionDelta: 0, sound: music(at: 4))
        XCTAssertEqual(packed, a.pack(aspect: 16.0 / 9), "A repeated frame changes nothing")
        XCTAssertFalse(packed.contains { !($0.x.isFinite && $0.y.isFinite && $0.z.isFinite && $0.w.isFinite) })
    }

    func testFrameRateIndependence() {
        let reference = run(fps: 120, seconds: 10).pack(aspect: 16.0 / 9)
        for fps in [24.0, 30, 60] {
            let state = run(fps: fps, seconds: 10).pack(aspect: 16.0 / 9)
            for c in 0..<2 {
                let a = state[L.craft + 2 * c], b = reference[L.craft + 2 * c]
                XCTAssertLessThan(simd_distance(SIMD2(a.x, a.y), SIMD2(b.x, b.y)), 0.0012, "craft \(c) at \(fps) fps")
            }
            let trail = (L.trail..<L.float4Count).map { abs(state[$0].x - reference[$0].x) }.max() ?? 0
            XCTAssertLessThan(trail, 0.0012, "trail at \(fps) fps")
            XCTAssertEqual(state[L.hdr1].z, reference[L.hdr1].z, accuracy: 0.0004, "scroll at \(fps) fps")
            // Onset ages are exact: the newest kick (at 9.6 s) is 0.4 s old at every rate.
            let newest = (0..<6).map { state[L.onset + 2 * $0].y }.filter { $0 >= 0 }.min() ?? -1
            XCTAssertEqual(Double(newest), 0.4, accuracy: 1e-4, "\(fps) fps")
        }
    }

    func testRingsNeverEvictALitSlot() {
        let simulation = StarTrailsSimulation()
        var sound = StarTrailsSound()
        for i in 1...300 {                                   // fast rolls of every drum, calm toggled midway
            if i % 3 == 0 || i % 7 == 0 {
                sound.kickOnsets &+= 1; sound.snareOnsets &+= 1; sound.hatOnsets &+= 1
            }
            sound.kick = 0.9; sound.snare = 0.9; sound.hat = 0.9
            if i == 150 { simulation.calmTarget = 1 }
            simulation.advance(wallDelta: 1.0 / 60, motionDelta: 1.0 / 60, sound: sound)
        }
        for (kind, ring) in StarTrailsSimulation.rings.enumerated() {
            XCTAssertGreaterThanOrEqual(simulation.youngestEviction[kind], ring.life - 1e-9, "kind \(kind)")
            XCTAssertLessThan(simulation.youngestEviction[kind], .infinity, "the roll filled ring \(kind)")
        }
        let state = simulation.pack(aspect: 16.0 / 9)
        for slot in 0..<20 where state[L.onset + 2 * slot].y >= 0 {
            XCTAssert((0.3...1).contains(state[L.onset + 2 * slot].z), "strength of slot \(slot)")
        }
    }

    func testCalmRampsOverHalfASecond() {
        let simulation = run(fps: 60, seconds: 1)
        simulation.calmTarget = 1
        for _ in 0..<15 { simulation.advance(wallDelta: 1.0 / 60, motionDelta: 1.0 / 60, sound: music(at: 1)) }
        XCTAssertEqual(simulation.calm, 0.5, accuracy: 0.01)
        for _ in 0..<15 { simulation.advance(wallDelta: 1.0 / 60, motionDelta: 1.0 / 60, sound: music(at: 1)) }
        XCTAssertEqual(simulation.calm, 1, accuracy: 1e-9)
    }

    func testKickEpicentresAvoidTheLyricStage() {
        let simulation = StarTrailsSimulation()
        for serial in 1...2000 {
            var rng = StarTrailsRandom.seeded(serial, 11)
            let e = simulation.epicentres(&rng)
            for (u, y) in [(e.x, e.y), (e.z, e.w)] {
                let d = SIMD2((u - 0.5) / 0.32, (y - 0.5) / 0.24)
                XCTAssertGreaterThanOrEqual(simd_length(d), 1.15 - 1e-9, "serial \(serial)")
            }
        }
    }

    /// Hits are light, never motion: at the same level, drums change nothing about where the craft
    /// fly, where they point or the paths their trails take; they only light them.
    func testHitsLightTheCraftButNeverMoveThem() {
        let plain = StarTrailsSimulation(), drums = StarTrailsSimulation()
        func path(_ s: [SIMD4<Float>]) -> [SIMD4<Float>] {
            [s[L.craft], s[L.craft + 2]] + (0..<(2 * L.trailSamples)).map { SIMD4<Float>(s[L.trail + $0].x, 0, 0, 0) }
        }
        var bloom: Float = 0, sparkle: Float = 0, packet: Float = 0
        for i in 1...240 {
            let hits = music(at: Double(i) / 60)
            var still = hits
            still.kick = 0; still.snare = 0; still.hat = 0
            still.kickOnsets = 0; still.snareOnsets = 0; still.hatOnsets = 0
            still.kickTime = nil; still.snareTime = nil; still.hatTime = nil
            plain.advance(wallDelta: 1.0 / 60, motionDelta: 1.0 / 60, sound: still)
            drums.advance(wallDelta: 1.0 / 60, motionDelta: 1.0 / 60, sound: hits)
            let a = plain.pack(aspect: 16.0 / 9), b = drums.pack(aspect: 16.0 / 9)
            guard path(a) == path(b) else { return XCTFail("Frame \(i): a hit moved a craft or bent its trail") }
            for c in 0..<2 {
                bloom = max(bloom, b[L.craft + 2 * c + 1].w)
                sparkle = max(sparkle, b[L.craft + 2 * c + 1].z)
                packet = max(packet, (0..<L.trailSamples).map { b[L.trail + c * L.trailSamples + $0].w }.max() ?? 0)
            }
        }
        XCTAssertGreaterThan(bloom, 0.3, "Kicks bloom the engines")
        XCTAssertGreaterThan(sparkle, 0.3, "Snares sparkle on the craft")
        XCTAssertGreaterThan(packet, 0.3, "Kicks lay bright packets into the trails")
    }

    /// The craft glide: little jerk and turn per frame, and a kick-only song whose level pumps with
    /// every kick flies as smoothly as a steady one at the same mean level.
    func testCraftGlideSmoothly() {
        func flight(_ sound: (Double) -> StarTrailsSound) -> (jerk: Double, turn: Double) {
            let simulation = StarTrailsSimulation(), fps = 30.0
            var points: [[SIMD2<Double>]] = [[], []], headings: [[Double]] = [[], []]
            for i in 1...Int(10 * fps) {
                simulation.advance(wallDelta: 1 / fps, motionDelta: 1 / fps, sound: sound(Double(i) / fps))
                let state = simulation.pack(aspect: 16.0 / 9)
                for c in 0..<2 {
                    let craft = state[L.craft + 2 * c]
                    points[c].append(SIMD2(Double(craft.x), Double(craft.y)))
                    headings[c].append(Double(craft.z))
                }
            }
            var jerk = 0.0, turn = 0.0
            for c in 0..<2 {
                let p = points[c], h = headings[c]
                for i in 1..<(p.count - 2) {
                    jerk = max(jerk, simd_length(p[i + 2] - 3 * p[i + 1] + 3 * p[i] - p[i - 1]) * fps * fps * fps)
                }
                for i in 1..<h.count {
                    let d = h[i] - h[i - 1]
                    turn = max(turn, abs(d - 2 * .pi * (d / (2 * .pi)).rounded()))
                }
            }
            return (jerk, turn * 180 / .pi)
        }
        let kicks = flight { t in
            var s = StarTrailsSound()
            let n = (t * 2).rounded(.down)
            s.kickOnsets = UInt32(n + 1); s.kickTime = n / 2
            s.kick = exp(-(t - n / 2) / 0.2)
            s.level = 0.2 + 0.6 * s.kick                              // the level pumps with every kick
            return s
        }
        let steady = flight { _ in var s = StarTrailsSound(); s.level = 0.42; return s }
        let report = String(format: "kick-only: jerk %.3f H/s³, turn %.3f°/frame; steady: %.3f, %.3f",
                            kicks.jerk, kicks.turn, steady.jerk, steady.turn)
        XCTAssertLessThan(kicks.jerk, 1.5, report)
        XCTAssertLessThan(kicks.turn, 1.0, report)
        XCTAssertLessThan(kicks.jerk, 1.15 * steady.jerk + 0.05, "Kicks do not roughen the flight: \(report)")
        XCTAssertLessThan(kicks.turn, 1.15 * steady.turn + 0.02, "Kicks do not swing the heading: \(report)")
    }

    @MainActor
    func testSharedSimulationAdvancesOncePerSceneTime() {
        let shared = StarTrailsSimulations()
        let response = VisualizerResponse()
        let simulation = shared.advance(sceneTime: 1, clock: 10, frame: AudioSnapshot(), response: response, sensitivity: 1)
        shared.advance(sceneTime: 1 + 0.7 / 30, clock: 10 + 1.0 / 30, frame: AudioSnapshot(), response: response, sensitivity: 1)
        let wall = simulation.wall, motion = simulation.motion
        XCTAssertEqual(wall, 1.0 / 30, accuracy: 1e-9)
        XCTAssertEqual(motion - StarTrailsSimulation.substep * 264, 1.0 / 30, accuracy: 1e-9, "Motion is scene time / 0.7")
        // A second display drawing the same frame does not advance it again.
        shared.advance(sceneTime: 1 + 0.7 / 30, clock: 10 + 1.0 / 30 + 0.001, frame: AudioSnapshot(), response: response, sensitivity: 1)
        XCTAssertEqual(simulation.wall, wall)
        // A rebase of the scene clock is not motion.
        shared.advance(sceneTime: 0, clock: 10 + 2.0 / 30, frame: AudioSnapshot(), response: response, sensitivity: 1)
        XCTAssertEqual(simulation.motion, motion)
        XCTAssertEqual(simulation.wall, wall + 1.0 / 30, accuracy: 1e-9)
    }

    // MARK: GPU

    private struct Renderer {
        let device: MTLDevice, queue: MTLCommandQueue, pipeline: MTLRenderPipelineState
        let width: Int, height: Int
        init(width: Int, height: Int) throws {
            device = try GPUTestSupport.device()
            queue = try XCTUnwrap(device.makeCommandQueue())
            let library = try device.makeDefaultLibrary(bundle: .main)
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.vertexFunction = library.makeFunction(name: "fullscreenVertex")
            descriptor.fragmentFunction = library.makeFunction(name: "starTrailsFragment")
            descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
            pipeline = try device.makeRenderPipelineState(descriptor: descriptor)
            self.width = width; self.height = height
        }
        @MainActor
        func render(_ binding: AudioVisualizerView.StarTrailsBinding) throws -> [UInt8] {
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
            let uniforms = AudioVisualizerView.Coordinator.uniforms(style: .starTrails,
                size: CGSize(width: width, height: height), time: 1, frame: AudioSnapshot(), sensitivity: 1, speed: 1, reduced: false)
            var inputs = AudioVisualizerView.SceneInputs(uniforms: uniforms, field: nil, bands: AudioSnapshot().bands,
                waveform: AudioSnapshot().waveform, colours: [SIMD4<Float>](repeating: .zero, count: 5))
            inputs.uniforms.intro = 1
            inputs.starTrails = binding
            encoder.setRenderPipelineState(pipeline)
            AudioVisualizerView.Coordinator.bind(inputs, to: encoder)
            AudioVisualizerView.Coordinator.bindExposure(nil, to: encoder)
            encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
            encoder.endEncoding(); command.commit(); command.waitUntilCompleted()
            XCTAssertEqual(command.status, .completed, command.error?.localizedDescription ?? "")
            var pixels = [UInt8](repeating: 0, count: width * height * 4)
            pixels.withUnsafeMutableBytes {
                texture.getBytes($0.baseAddress!, bytesPerRow: width * 4, from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
            }
            return pixels
        }
    }

    @MainActor
    func testStateBindsFromARingBufferOrBytesAlike() throws {
        let renderer = try Renderer(width: 320, height: 180)
        let state = run(fps: 30, seconds: 2).pack(aspect: 16.0 / 9)
        let buffer = try XCTUnwrap(state.withUnsafeBytes {
            renderer.device.makeBuffer(bytes: $0.baseAddress!, length: $0.count, options: .storageModeShared)
        })
        let fromBytes = try renderer.render(.bytes(state))
        XCTAssertEqual(fromBytes, try renderer.render(.buffer(buffer)))
        XCTAssertGreaterThan(fromBytes.enumerated().filter { $0.offset % 4 != 3 && $0.element > 40 }.count, 200, "Stars and trails show")
        // Only Star Trails gets a state; its static frames get a snapshot.
        let u = AudioVisualizerView.Coordinator.uniforms(style: .starTrails, size: CGSize(width: 160, height: 90), time: 3,
            frame: AudioSnapshot(), sensitivity: 1, speed: 1, reduced: false)
        var modes = WavefieldModes()
        let inputs = AudioVisualizerView.Coordinator.sceneInputs(AudioSnapshot(), style: .starTrails, uniforms: u,
            wavefield: &modes, at: 0, response: VisualizerResponse(), palette: nil)
        guard case .bytes(let snapshot)? = inputs.starTrails else { return XCTFail("Static Star Trails frames bind a snapshot") }
        XCTAssertEqual(snapshot.count, L.float4Count)
        let other = AudioVisualizerView.Coordinator.sceneInputs(AudioSnapshot(), style: .colourStrata, uniforms: u,
            wavefield: &modes, at: 0, response: VisualizerResponse(), palette: nil)
        XCTAssertNil(other.starTrails)
    }

    /// Each sound type lights its own stars, in its own colour, on the same frozen sky.
    @MainActor
    func testSoundTypesLightDifferentStars() throws {
        let renderer = try Renderer(width: 1280, height: 720)
        func state(_ configure: (inout StarTrailsSound, Int) -> Void) -> [SIMD4<Float>] {
            let simulation = StarTrailsSimulation()
            simulation.freezeSky = 5
            var sound = StarTrailsSound()
            for i in 0..<45 {
                configure(&sound, i)
                simulation.advance(wallDelta: 1.0 / 30, motionDelta: 1.0 / 30, sound: sound)
            }
            return simulation.pack(aspect: 16.0 / 9)
        }
        let silence = state { _, _ in }
        // The craft, trails and asteroids of silence, so only the stars (and clouds) differ.
        func isolated(_ s: [SIMD4<Float>]) -> [SIMD4<Float>] {
            var s = s
            for i in Array(L.craft..<L.onset) + Array(L.asteroid..<L.float4Count) { s[i] = silence[i] }
            return s
        }
        func hit(_ count: KeyPath<StarTrailsSound, UInt32>, _ write: WritableKeyPath<StarTrailsSound, UInt32>,
                 _ envelope: WritableKeyPath<StarTrailsSound, Double>, at frames: [Int]) -> [SIMD4<Float>] {
            isolated(state { sound, i in
                if frames.contains(i) { sound[keyPath: write] = sound[keyPath: count] + 1; sound[keyPath: envelope] = 0.9 }
                else { sound[keyPath: envelope] *= 0.8 }
            })
        }
        let cases: [(name: String, state: [SIMD4<Float>])] = [
            ("kick", hit(\.kickOnsets, \.kickOnsets, \.kick, at: [38])),
            ("snare", hit(\.snareOnsets, \.snareOnsets, \.snare, at: [38])),
            ("hat", hit(\.hatOnsets, \.hatOnsets, \.hat, at: [36, 39, 42])),
            ("bass", isolated(state { s, _ in s.bass = 0.85 })),
            ("vocal", isolated(state { s, _ in s.vocal = 0.85 })),
            ("pad", isolated(state { s, _ in s.pad = 0.85 })),
        ]
        let quiet = try renderer.render(.bytes(silence))
        var lit: [String: Set<Int>] = [:]
        var hues: [String: (hue: Double, saturation: Double)] = [:]
        for (name, state) in cases {
            let image = try renderer.render(.bytes(state))
            var added = SIMD3<Double>(0, 0, 0)
            var pixels = Set<Int>()
            for p in stride(from: 0, to: image.count, by: 4) {
                let d = SIMD3(Double(image[p + 2]) - Double(quiet[p + 2]), Double(image[p + 1]) - Double(quiet[p + 1]),
                              Double(image[p]) - Double(quiet[p]))
                added += simd_max(d, .zero)
                if d.max() > 60 { pixels.insert(p / 4) }
            }
            lit[name] = pixels
            let high = added.max(), low = added.min()
            var hue = 0.0
            if high > low {
                if high == added.x { hue = 60 * ((added.y - added.z) / (high - low)) }
                else if high == added.y { hue = 60 * ((added.z - added.x) / (high - low) + 2) }
                else { hue = 60 * ((added.x - added.y) / (high - low) + 4) }
            }
            hues[name] = ((hue + 360).truncatingRemainder(dividingBy: 360), high > 0 ? (high - low) / high : 0)
        }
        let report = cases.map { "\($0.name): \(lit[$0.name]!.count) px, hue \(Int(hues[$0.name]!.hue)), sat \(String(format: "%.2f", hues[$0.name]!.saturation))" }
            .joined(separator: "; ")
        for name in ["kick", "snare", "hat", "bass", "vocal", "pad"] {
            XCTAssertGreaterThan(lit[name]!.count, 20, "\(name) lights stars: \(report)")
        }
        // Pad light includes the pink the pad adds to the clouds, so it sits a little towards magenta.
        let ranges: [String: ClosedRange<Double>] = ["kick": 15...55, "snare": 170...210, "bass": 210...250,
                                                     "vocal": 310...350, "pad": 255...300]
        for (name, range) in ranges { XCTAssert(range.contains(hues[name]!.hue), "\(name) colour: \(report)") }
        XCTAssertLessThan(hues["hat"]!.saturation, 0.3, "hats glitter silver: \(report)")
        let names = cases.map(\.name)
        for i in names.indices {
            for j in names.indices where j > i {
                let a = lit[names[i]]!, b = lit[names[j]]!
                let jaccard = Double(a.intersection(b).count) / Double(max(1, a.union(b).count))
                XCTAssertLessThan(jaccard, 0.05, "\(names[i]) and \(names[j]) light different stars: \(report)")
            }
        }
        // Every kick lights a fresh set of stars.
        let next = isolated(state { sound, i in
            if i == 20 || i == 38 { sound.kickOnsets += 1; sound.kick = 0.9 } else { sound.kick *= 0.8 }
        })
        let image = try renderer.render(.bytes(next))
        var second = Set<Int>()
        for p in stride(from: 0, to: image.count, by: 4) where (0..<3).map({ Int(image[p + $0]) - Int(quiet[p + $0]) }).max()! > 60 {
            second.insert(p / 4)
        }
        let overlap = Double(second.intersection(lit["kick"]!).count) / Double(max(1, second.union(lit["kick"]!).count))
        XCTAssertLessThan(overlap, 0.3, "consecutive kicks light different stars: \(report)")
    }

    /// The background alone: craft, trails and asteroids out of the frame, no stars lit by hits.
    private func backgroundOnly(_ state: [SIMD4<Float>]) -> [SIMD4<Float>] {
        var s = state
        s[L.hdr0].x = -100                                   // the trails are drawn round the formation centre
        for c in 0..<2 { s[L.craft + 2 * c].x = -100 }
        s[L.live] = .zero                                    // no live onsets, no asteroids
        return s
    }
    /// Rec. 709 luma (0…255) of a BGRA image.
    private func luma(_ image: [UInt8]) -> [Double] {
        stride(from: 0, to: image.count, by: 4).map {
            0.2126 * Double(image[$0 + 2]) + 0.7152 * Double(image[$0 + 1]) + 0.0722 * Double(image[$0])
        }
    }
    /// A 5 x 5 minimum: removes every star point (a star's core is at most a few pixels across).
    private func erode(_ v: [Double], _ w: Int, _ h: Int) -> [Double] {
        var rows = v, out = v
        for y in 0..<h {
            for x in 2..<(w - 2) {
                let i = y * w + x
                rows[i] = min(min(min(v[i - 2], v[i - 1]), min(v[i], v[i + 1])), v[i + 2])
            }
        }
        for y in 2..<(h - 2) {
            for x in 0..<w {
                let i = y * w + x
                out[i] = min(min(min(rows[i - 2 * w], rows[i - w]), min(rows[i], rows[i + w])), rows[i + 2 * w])
            }
        }
        return out
    }

    /// A kick lights the trail being laid and blooms the engines: the craft region brightens.
    @MainActor
    func testKickBrightensTheTrailAndCraft() throws {
        let (w, h) = (640, 360)
        let renderer = try Renderer(width: w, height: h)
        func state(kickAt frame: Int?) -> [SIMD4<Float>] {
            let simulation = StarTrailsSimulation()
            simulation.freezeSky = 5
            var sound = StarTrailsSound()
            sound.level = 0.4
            for i in 0..<45 {
                if i == frame { sound.kickOnsets += 1; sound.kick = 0.9 } else { sound.kick *= 0.8 }
                simulation.advance(wallDelta: 1.0 / 30, motionDelta: 1.0 / 30, sound: sound)
            }
            return simulation.pack(aspect: 16.0 / 9)
        }
        let quiet = state(kickAt: nil)
        var kick = state(kickAt: 41)
        for i in L.onset..<L.trail { kick[i] = quiet[i] }  // no star flares or asteroid rims: only the craft and trails differ
        for c in 0..<2 { XCTAssertEqual(kick[L.craft + 2 * c], quiet[L.craft + 2 * c], "A kick does not move craft \(c)") }
        let a = luma(try renderer.render(.bytes(quiet))), b = luma(try renderer.render(.bytes(kick)))
        var base = 0.0, added = 0.0
        for c in 0..<2 {                                      // within 0.1 H of each craft: engines and the newest trail
            let craft = quiet[L.craft + 2 * c]
            let cx = Double(craft.x) * Double(h), cy = (1 - Double(craft.y)) * Double(h), r = 0.1 * Double(h)
            for y in max(0, Int(cy - r))..<min(h, Int(cy + r) + 1) {
                for x in max(0, Int(cx - r))..<min(w, Int(cx + r) + 1) where hypot(Double(x) + 0.5 - cx, Double(y) + 0.5 - cy) < r {
                    base += a[y * w + x]; added += b[y * w + x] - a[y * w + x]
                }
            }
        }
        XCTAssertGreaterThan(added / max(base, 1), 0.15, "A kick brightens the craft and their trails by \(Int(100 * added / max(base, 1))) %")
    }

    /// The clouds are out of focus: once the star points are removed, almost no pixel has a sharp
    /// luminance kink (a crisp edge, a terrace or fine noise). The lyric stage's veil keeps the
    /// clouds behind the words darker than the same clouds without a stage.
    @MainActor
    func testNebulaIsSoftAndTheStageStaysDark() throws {
        let (w, h) = (768, 432)
        let renderer = try Renderer(width: w, height: h)
        var kinks = 0, pixels = 0, veiled = 0.0, unveiled = 0.0
        for sky in [5.0, 77, 160] {
            let simulation = StarTrailsSimulation()
            simulation.freezeSky = sky
            simulation.advance(wallDelta: 1.0 / 30, motionDelta: 1.0 / 30, sound: StarTrailsSound())
            let state = backgroundOnly(simulation.pack(aspect: 16.0 / 9))
            var bare = state
            bare[L.stage] = SIMD4(-10, -10, 0.32, 0.24)                  // the same sky without a lyric stage
            let o = erode(luma(try renderer.render(.bytes(state))), w, h)
            let clear = erode(luma(try renderer.render(.bytes(bare))), w, h)
            for y in 3..<(h - 3) {
                for x in 3..<(w - 3) {
                    let i = y * w + x
                    if abs(4 * clear[i] - clear[i - 1] - clear[i + 1] - clear[i - w] - clear[i + w]) > 5 { kinks += 1 }
                    pixels += 1
                    let e = SIMD2((Double(x) + 0.5) / Double(w) - 0.5, 0.5 - (Double(y) + 0.5) / Double(h)) / SIMD2(0.32, 0.24)
                    if simd_length(e) < 0.7 { veiled += o[i]; unveiled += clear[i] }
                }
            }
        }
        // 8-bit rounding alone moves this Laplacian by at most 4 levels.
        XCTAssertLessThan(Double(kinks) / Double(pixels), 0.002, "\(kinks) of \(pixels) pixels have a sharp kink")
        XCTAssertLessThan(veiled, 0.9 * unveiled, "The stage veil darkens the clouds behind the lyrics")
    }

    /// The camera follows the craft: the scenery drifts steadily against the craft's mean heading,
    /// with parallax (clouds slowest, then dust stars, bright stars, asteroids), measured both in
    /// the state and on screen.
    @MainActor
    func testSceneryDriftsAgainstTheCraftHeading() throws {
        let axis = StarTrailsSimulation.axis
        let simulation = run(fps: 30, seconds: 2)
        var mean = SIMD2<Double>(0, 0)
        var previous = simulation.pack(aspect: 16.0 / 9)
        for i in 1...300 {                                    // 10 s: about three orbits
            simulation.advance(wallDelta: 1.0 / 30, motionDelta: 1.0 / 30, sound: music(at: 2 + Double(i) / 30))
            let s = simulation.pack(aspect: 16.0 / 9)
            for c in 0..<2 { mean += SIMD2(cos(Double(s[L.craft + 2 * c].z)), sin(Double(s[L.craft + 2 * c].z))) }
            // Each layer scrolls at its own constant speed; far layers slower.
            func speed(_ now: Float, _ before: Float) -> Double {
                let d = Double(now) - Double(before)
                return 30 * (d - 256 * (d / 256).rounded())
            }
            let cloud = speed(s[L.clock].z, previous[L.clock].z), dust = speed(s[L.hdr1].w, previous[L.hdr1].w)
            let star = speed(s[L.hdr1].z, previous[L.hdr1].z)
            XCTAssertEqual(cloud, StarTrailsSimulation.cloudDrift, accuracy: 2e-3, "clouds, frame \(i)")
            XCTAssertEqual(dust, StarTrailsSimulation.dustDrift, accuracy: 2e-3, "dust, frame \(i)")
            XCTAssertEqual(star, StarTrailsSimulation.starDrift, accuracy: 2e-3, "stars, frame \(i)")
            // Asteroids drift the same way, faster than every star.
            for k in 0..<L.asteroids {
                let d = SIMD2(Double(s[L.asteroid + 2 * k].x - previous[L.asteroid + 2 * k].x),
                              Double(s[L.asteroid + 2 * k].y - previous[L.asteroid + 2 * k].y))
                guard simd_length(d) < 0.1, s[L.asteroid + 2 * k + 1].x == previous[L.asteroid + 2 * k + 1].x else { continue }   // respawned
                var off = atan2(d.y, d.x) - (axis + .pi)
                off -= 2 * .pi * (off / (2 * .pi)).rounded()
                XCTAssertLessThan(abs(off), 0.01, "asteroid \(k), frame \(i)")
                XCTAssertGreaterThan(30 * simd_length(d), StarTrailsSimulation.starDrift, "asteroid \(k), frame \(i)")
            }
            previous = s
        }
        XCTAssertLessThan(StarTrailsSimulation.cloudDrift, StarTrailsSimulation.dustDrift)
        XCTAssertLessThan(StarTrailsSimulation.dustDrift, StarTrailsSimulation.starDrift)
        XCTAssertEqual(atan2(mean.y, mean.x), axis, accuracy: 1.5 * .pi / 180, "The flight axis is the craft's mean heading")

        // On screen: the shift that best carries the brightest stars of one frame onto a frame
        // 22/30 s later points against the flight.
        let (w, h) = (960, 540)
        let renderer = try Renderer(width: w, height: h)
        let quiet = StarTrailsSimulation()
        var frames: [[Double]] = []
        for i in 1...23 {
            quiet.advance(wallDelta: 1.0 / 30, motionDelta: 1.0 / 30, sound: StarTrailsSound())
            if i == 1 || i == 23 { frames.append(luma(try renderer.render(.bytes(backgroundOnly(quiet.pack(aspect: 16.0 / 9)))))) }
        }
        let a = frames[0], b = frames[1]
        var peaks: [(x: Int, y: Int, v: Double)] = []
        for y in 70..<(h - 70) {
            for x in 70..<(w - 70) {
                let v = a[y * w + x]
                if v > 40, v > a[y * w + x - 1], v >= a[y * w + x + 1], v > a[(y - 1) * w + x], v >= a[(y + 1) * w + x] {
                    peaks.append((x, y, v))
                }
            }
        }
        peaks = Array(peaks.sorted { $0.v > $1.v }.prefix(300))
        XCTAssertGreaterThan(peaks.count, 100, "Stars to follow")
        var best = (dx: 0, dy: 0, score: -1.0)
        for dy in -64...64 {
            for dx in -64...64 {
                var score = 0.0
                for p in peaks { score += b[(p.y + dy) * w + p.x + dx] }
                if score > best.score { best = (dx, dy, score) }
            }
        }
        let shift = SIMD2(Double(best.dx), -Double(best.dy))  // rows grow downwards
        var off = atan2(shift.y, shift.x) - (axis + .pi)
        off -= 2 * .pi * (off / (2 * .pi)).rounded()
        let expected = StarTrailsSimulation.starDrift * 22 / 30 * Double(h)
        XCTAssertLessThan(abs(off), 4 * .pi / 180, "Stars drift at \(Int(atan2(shift.y, shift.x) * 180 / .pi))°, the craft fly at \(Int(axis * 180 / .pi))°")
        XCTAssertEqual(simd_length(shift), expected, accuracy: 4, "Bright stars drift \(StarTrailsSimulation.starDrift) H/s")
    }

    // MARK: Sky colours

    private func sky(_ palette: RenderPalette) -> StarTrailsSky { StarTrailsSky.album(palette.palette!) }

    func testAlbumSkyUsesAllNineCoverSamplesWithoutMovingTheirHues() {
        let grid = (0..<9).map { SIMD3(Double($0) / 9, 0.84, 0.8) }
        let cover = AlbumWallpaperPalette(grid: grid)
        let album = StarTrailsSky.album(cover)
        XCTAssertEqual(album.tints.count, 9)
        for (sample, tint) in zip(cover.artworkStops, album.tints) {
            let linear = SIMD3(Perceptual.linear(Double(sample.x)), Perceptual.linear(Double(sample.y)),
                               Perceptual.linear(Double(sample.z)))
            let tinted = SIMD3(tint.x, tint.y, tint.z)
            XCTAssertLessThan(simd_distance(simd_normalize(linear), simd_normalize(tinted)), 1e-6)
            XCTAssertLessThanOrEqual(simd_dot(tinted, SIMD3(0.2126, 0.7152, 0.0722)), 1)
            XCTAssertEqual(tint.w, 1)
        }
        for neutral in sky(.bw).tints {
            XCTAssertEqual(neutral.x, neutral.y, accuracy: 1e-7)
            XCTAssertEqual(neutral.x, neutral.z, accuracy: 1e-7)
        }
    }

    func testAlbumTrailsStayDistinctAndBrightAgainstTheSky() throws {
        let singleHue = stride(from: 0.0, to: 1.0, by: 1.0 / 12).map { hue in
            AlbumWallpaperPalette(grid: Array(repeating: SIMD3(hue, 0.9, 0.85), count: 9))
        }
        let varied = [RenderPalette.warm, .blue, .vivid, .earth].compactMap(\.palette)
        for cover in singleHue + varied {
            let sky = try XCTUnwrap(AlbumColourHarmony(cover.artworkStops.map {
                Perceptual.oklab(sRGB: SIMD3<Double>($0))
            }).backgroundHue)
            let trails = StarTrailsSky.album(cover).trails.map { core in
                let rgb = SIMD3(core.x, core.y, core.z)
                XCTAssertEqual(core.w, 1)
                let contrast = Perceptual.contrast(Perceptual.luminance(sRGB: rgb), StarTrailsSky.brightNebulaLuminance)
                XCTAssertGreaterThanOrEqual(contrast, 2.99)
                XCTAssertGreaterThan(max(rgb.x, rgb.y, rgb.z) - min(rgb.x, rgb.y, rgb.z), 0.38)
                return Perceptual.oklab(sRGB: rgb)
            }
            XCTAssertGreaterThan(LyricHighlight.hueDistance(trails[0].hue, trails[1].hue), .pi * 0.55)
            for trail in trails {
                XCTAssertGreaterThan(LyricHighlight.hueDistance(trail.hue, sky), .pi * 0.6)
            }
        }
        XCTAssertEqual(sky(.bw).trails, StarTrailsSky.original.trails)
    }

    @MainActor
    func testEachRenderedAlbumTrailHasVividPixelsAgainstTheNebula() throws {
        let renderer = try Renderer(width: 640, height: 360)
        for palette in [RenderPalette.warm, .blue] {
            let simulation = StarTrailsSimulation(sky: sky(palette))
            var state = simulation.pack(aspect: 16.0 / 9)
            state[L.live] = .zero
            for k in 0..<2 { state[L.craft + 2 * k].x = -100 }
            let nebula = try renderer.render(.bytes(backgroundOnly(state)))
            func rgb(_ frame: [UInt8], at pixel: Int) -> SIMD3<Double> {
                SIMD3(Double(frame[pixel + 2]), Double(frame[pixel + 1]), Double(frame[pixel])) / 255
            }
            for k in 0..<2 {
                var isolated = state
                isolated[L.craft + 2 * (1 - k) + 1].y = 100
                let frame = try renderer.render(.bytes(isolated))
                let vivid = stride(from: 0, to: frame.count, by: 4).filter { pixel in
                    let colour = rgb(frame, at: pixel)
                    let span = max(colour.x, colour.y, colour.z) - min(colour.x, colour.y, colour.z)
                    guard span > 0.40 else { return false }
                    let background = rgb(nebula, at: pixel)
                    return Perceptual.contrast(Perceptual.luminance(sRGB: colour),
                                               Perceptual.luminance(sRGB: background)) > 3 && colour != background
                }.count
                XCTAssertGreaterThan(vivid, 30, "\(palette.rawValue), trail \(k)")
            }
        }
    }

    @MainActor
    func testAlbumColoursReachDifferentRegionsOfTheRenderedSky() throws {
        let (w, h) = (384, 216)
        let renderer = try Renderer(width: w, height: h)
        let grid = (0..<9).map { index in SIMD3(index % 3 == 0 ? 0.0 : 0.60, 0.94, 0.86) }
        let simulation = StarTrailsSimulation(sky: .album(AlbumWallpaperPalette(grid: grid)))
        let frame = try renderer.render(.bytes(backgroundOnly(simulation.pack(aspect: 16.0 / 9))))
        func hueBias(_ xRange: Range<Int>) -> Double {
            var bias = 0.0
            for y in 25..<(h - 25) {
                for x in xRange {
                    let pixel = (y * w + x) * 4
                    bias += Double(frame[pixel + 2]) - Double(frame[pixel])
                }
            }
            return bias / Double(xRange.count * (h - 50))
        }
        XCTAssertGreaterThan(hueBias(20..<80), 12, "red from the left of the cover")
        XCTAssertLessThan(hueBias((w - 80)..<(w - 20)), -12, "blue from the right of the cover")
    }

    @MainActor
    func testAlbumCloudsHaveLessFineDetailThanTheOriginalSky() throws {
        let (w, h) = (384, 216)
        let renderer = try Renderer(width: w, height: h)
        let neutral = AlbumWallpaperPalette(grid: Array(repeating: SIMD3(0, 0, 0.7), count: 9))
        func detail(_ sky: StarTrailsSky) throws -> Double {
            var energy = 0.0, brightness = 0.0
            for scroll in [5.0, 77, 160] {
                let simulation = StarTrailsSimulation(sky: sky)
                simulation.freezeSky = scroll
                simulation.advance(wallDelta: 1.0 / 30, motionDelta: 1.0 / 30, sound: StarTrailsSound())
                let frame = try renderer.render(.bytes(backgroundOnly(simulation.pack(aspect: 16.0 / 9))))
                let skyOnly = erode(luma(frame), w, h)
                let stride = 5
                for y in 10..<(h - 10) {
                    for x in 10..<(w - 10) {
                        let i = y * w + x
                        let change = 4 * skyOnly[i] - skyOnly[i - stride] - skyOnly[i + stride]
                            - skyOnly[i - stride * w] - skyOnly[i + stride * w]
                        energy += abs(change)
                        brightness += skyOnly[i]
                    }
                }
            }
            return energy / max(1, brightness)
        }
        let original = try detail(.original)
        let softened = try detail(.album(neutral))
        XCTAssertLessThan(softened, original * 0.80)
    }

    /// The original violet sky is exactly the reference's, settled or after a fade back.
    func testOriginalSkyIsUnchanged() {
        let reference: [SIMD3<Double>] = [
            [0.031, 0.008, 0.075], [0.055, 0.035, 0.112], [0.082, 0.043, 0.145], [0.118, 0.059, 0.188],
            [0.180, 0.078, 0.259], [0.235, 0.110, 0.310], [0.298, 0.141, 0.353], [0.360, 0.165, 0.392],
            [0.647, 0.282, 0.565], [0.831, 0.369, 0.706], [1.0, 0.97, 1.0]]
        XCTAssertEqual(StarTrailsSky.originalColours, reference)
        let expected = reference.map { SIMD4<Float>(Float($0.x), Float($0.y), Float($0.z), 0) }
        let simulation = StarTrailsSimulation()
        XCTAssertEqual(Array(simulation.pack(aspect: 16.0 / 9)[L.sky..<L.skyTints]), expected)
        simulation.skyTarget = sky(.vivid)
        for _ in 0..<60 { simulation.advance(wallDelta: 1.0 / 30, motionDelta: 1.0 / 30, sound: StarTrailsSound()) }
        simulation.skyTarget = .original
        for _ in 0..<360 { simulation.advance(wallDelta: 1.0 / 30, motionDelta: 1.0 / 30, sound: StarTrailsSound()) }
        let restored = simulation.pack(aspect: 16.0 / 9)
        XCTAssertEqual(Array(restored[L.sky..<L.skyTints]), expected, "settled back exactly")
        XCTAssertEqual(Array(restored[L.skyTints..<L.float4Count]), StarTrailsSky.original.colours.map { SIMD4<Float>($0) })
    }

    /// A new cover's sky crossfades over about 2.5 s: no pop at the start, small steps, settled
    /// by 3 s, the same at any frame rate.
    func testSkyCrossfadesSmoothly() {
        let from = sky(.blue), to = sky(.vivid)
        func fade(fps: Double, seconds: Double) -> [[SIMD4<Float>]] {
            let simulation = StarTrailsSimulation(sky: from)
            simulation.skyTarget = to
            return (0..<Int(seconds * fps)).map { _ in
                simulation.advance(wallDelta: 1 / fps, motionDelta: 1 / fps, sound: StarTrailsSound())
                return Array(simulation.pack(aspect: 16.0 / 9)[L.skyTints..<L.float4Count])
            }
        }
        func distance(_ a: [SIMD4<Float>], _ b: [SIMD4<Float>]) -> Float { zip(a, b).map { simd_length($0 - $1) }.max() ?? 0 }
        let start = from.colours.map { SIMD4<Float>($0) }
        let end = to.colours.map { SIMD4<Float>($0) }
        let total = distance(start, end)
        XCTAssertGreaterThan(total, 0.2)
        let frames = fade(fps: 60, seconds: 4)
        XCTAssertLessThan(distance(frames[0], start), 0.001 * total, "starts without a pop")
        var largest: Float = 0
        for i in 1..<frames.count { largest = max(largest, distance(frames[i], frames[i - 1])) }
        XCTAssertLessThan(largest, 0.015 * total, "small steps every frame")
        let progress = 1 - distance(frames[59], end) / total
        XCTAssert((0.2...0.8).contains(progress), "about half way after 1 s: \(progress)")
        XCTAssertLessThan(distance(frames[179], end), 0.05 * total, "nearly there after 3 s")
        let slow = fade(fps: 30, seconds: 1.5), fast = fade(fps: 120, seconds: 1.5)
        XCTAssertLessThan(distance(slow[44], fast[179]), 0.01 * total, "frame-rate independent")
    }

    /// The album skies keep the lyric stage as dark as the violet sky (the clouds behind the
    /// lyrics, stars removed, at three scrolls).
    @MainActor
    func testAlbumSkiesKeepTheLyricStageDark() throws {
        let (w, h) = (384, 216)
        let renderer = try Renderer(width: w, height: h)
        func stage(_ sky: StarTrailsSky) throws -> Double {
            var total = 0.0
            for scroll in [5.0, 77, 160] {
                let simulation = StarTrailsSimulation(sky: sky)
                simulation.freezeSky = scroll
                simulation.advance(wallDelta: 1.0 / 30, motionDelta: 1.0 / 30, sound: StarTrailsSound())
                let image = try renderer.render(.bytes(backgroundOnly(simulation.pack(aspect: 16.0 / 9))))
                let light = stride(from: 0, to: image.count, by: 4).map {
                    Perceptual.luminance(sRGB: SIMD3(Double(image[$0 + 2]), Double(image[$0 + 1]), Double(image[$0])) / 255)
                }
                let o = erode(light, w, h)
                for y in 2..<(h - 2) {
                    for x in 2..<(w - 2) {
                        let e = SIMD2((Double(x) + 0.5) / Double(w) - 0.5, 0.5 - (Double(y) + 0.5) / Double(h)) / SIMD2(0.32, 0.24)
                        if simd_length(e) <= 1 { total += o[y * w + x] }
                    }
                }
            }
            return total
        }
        let violet = try stage(.original)
        for palette in RenderPalette.allCases where palette != .none {
            let ratio = try stage(sky(palette)) / violet
            XCTAssertLessThanOrEqual(ratio, 1.02, "\(palette.rawValue): the lyric stage is \(Int((ratio - 1) * 100)) % brighter")
        }
    }

    /// "Colours" defaults to the album cover; the shared simulation follows the cover it is given.
    @MainActor
    func testStarTrailsColoursDefaultToTheAlbum() {
        let suite = "StarTrailsTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        XCTAssertEqual(VisualizerPreferences(defaults: defaults).starTrailsColours, .album)
        VisualizerPreferences(defaults: defaults).starTrailsColours = .original
        XCTAssertEqual(VisualizerPreferences(defaults: defaults).starTrailsColours, .original)
        let shared = StarTrailsSimulations()
        let simulation = shared.advance(sceneTime: 1, clock: 1, frame: AudioSnapshot(), response: VisualizerResponse(),
                                        sensitivity: 1, palette: RenderPalette.earth.palette)
        XCTAssertEqual(simulation.skyTarget, sky(.earth), "starts on the cover's sky")
        XCTAssertEqual(Array(simulation.pack(aspect: 16.0 / 9)[L.skyTints..<L.float4Count]),
                       sky(.earth).colours.map { SIMD4<Float>($0) }, "no fade in from violet")
        shared.advance(sceneTime: 2, clock: 2, frame: AudioSnapshot(), response: VisualizerResponse(), sensitivity: 1, palette: nil)
        XCTAssertEqual(simulation.skyTarget, .original, "no cover: the violet sky")
    }
}
