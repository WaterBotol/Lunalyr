import Metal
import XCTest
@testable import VersoCustom

final class VisualizerTests: XCTestCase {
    private func tone(_ frequency: Double, into analyzer: AudioAnalyzer) -> AudioSnapshot {
        let rate = 48000.0, count = 2048
        for pass in 0..<18 {
            let pcm = (0..<count).map { Float(0.25 * sin(2 * .pi * frequency * Double(pass * count + $0) / rate)) }
            pcm.withUnsafeBufferPointer { analyzer.append($0.baseAddress!, count: count, sampleRate: rate) }
        }
        return analyzer.latest()
    }
    func testImportedFFTSeparatesMusicalRegisters() {
        let peaks = [100.0, 1000, 8000].map { frequency -> Int in
            let analyzer = AudioAnalyzer()
            analyzer.setAutoGainEnabled(false)
            let frame = tone(frequency, into: analyzer)
            XCTAssertTrue(frame.bands.allSatisfy { $0.isFinite && $0 >= 0 && $0 <= 1 })
            return frame.bands.indices.max(by: { frame.bands[$0] < frame.bands[$1] })!
        }
        XCTAssertLessThan(peaks[0], peaks[1])
        XCTAssertLessThan(peaks[1], peaks[2])
        XCTAssertLessThan(peaks[0], 10)
        XCTAssertGreaterThan(peaks[2], 50)
    }
    func testStereoFFTDoesNotCancelOppositePhaseAudio() {
        let source = VisualizerAudio()
        for pass in 0..<18 {
            let left = (0..<2048).map { Float(0.25 * sin(2 * .pi * 1000 * Double(pass * 2048 + $0) / 48000)) }
            let right = left.map { -$0 }
            left.withUnsafeBufferPointer { source.append($0.baseAddress!, count: 2048, sampleRate: 48000, channel: 0) }
            right.withUnsafeBufferPointer { source.append($0.baseAddress!, count: 2048, sampleRate: 48000, channel: 1) }
            source.setChannelCount(2)
        }
        XCTAssertGreaterThan(source.latest().bands.max() ?? 0, 0.4)
        source.reset()
        XCTAssertEqual(source.latest().bands.max(), 0)
    }
    func testSmoothingIsContinuousAndIndependentOfFrameRate() {
        var target = AudioSnapshot()
        target.bands = [Float](repeating: 1, count: 64)
        func run(_ fps: Int) -> AudioSnapshot {
            var smoother = VisualizerSmoother(), result = AudioSnapshot()
            for frame in 0...(fps * 2) { result = smoother.frame(target, at: Double(frame) / Double(fps)) }
            return result
        }
        let thirty = run(30), sixty = run(60)
        XCTAssertEqual(thirty.bands[20], sixty.bands[20], accuracy: 0.00001)
        var smoother = VisualizerSmoother()
        _ = smoother.frame(target, at: 0)
        let first = smoother.frame(target, at: 1 / 60)
        XCTAssertGreaterThan(first.bands[20], 0)
        XCTAssertLessThan(first.bands[20], 0.05)
    }
    func testAudioEnvelopeKeepsNoteAttacksAndReleasesVisible() {
        var smoother = VisualizerSmoother(), sound = AudioSnapshot()
        sound.bass = 1
        _ = smoother.frame(sound, at: 0)
        var frame = sound
        for step in 1...6 { frame = smoother.frame(sound, at: Double(step) / 60) }
        XCTAssertGreaterThan(frame.bass, 0.5, "Do not smear a note attack over several hundred milliseconds")
        for step in 7...60 { _ = smoother.frame(sound, at: Double(step) / 60) }
        for step in 61...90 { frame = smoother.frame(AudioSnapshot(), at: Double(step) / 60) }
        XCTAssertLessThan(frame.bass, 0.4, "Do not hold the previous note through the next beat")
    }
    func testSilenceAndInvalidSamplesCannotDriveTheVisualiser() {
        let analyzer = AudioAnalyzer()
        let silence = [Float](repeating: 0, count: 2048)
        silence.withUnsafeBufferPointer { analyzer.append($0.baseAddress!, count: 2048, sampleRate: 48000) }
        XCTAssertEqual(analyzer.latest().level, 0)
        XCTAssertEqual(analyzer.latest().bands.max(), 0)
        var target = AudioSnapshot(), smoother = VisualizerSmoother()
        target.bands[0] = .nan
        _ = smoother.frame(target, at: 0)
        XCTAssertTrue(smoother.frame(target, at: 1).bands.allSatisfy { $0.isFinite && $0 == 0 })
    }
    func testGPUUniformLayoutMatchesImportedMetalShaders() {
        XCTAssertEqual(MemoryLayout<VizUniforms>.stride, 168)
    }
    @MainActor
    func testEveryImportedSceneRendersAndChangesWithItsSpectrum() throws {
        guard let device = MTLCreateSystemDefaultDevice(), let queue = device.makeCommandQueue() else {
            throw XCTSkip("This test host has no Metal device; GPU rendering is unverified here.")
        }
        let library = try device.makeDefaultLibrary(bundle: .main)
        let width = 160, height = 90
        for style in AudioVisualizerStyle.allCases {
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.vertexFunction = library.makeFunction(name: "fullscreenVertex")
            descriptor.fragmentFunction = library.makeFunction(name: style.fragment)
            descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
            let pipeline = try device.makeRenderPipelineState(descriptor: descriptor)
            func render(_ sound: AudioSnapshot, sensitivity: Double = 1) throws -> [UInt8] {
                let textureDescriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm,
                    width: width, height: height, mipmapped: false)
                textureDescriptor.usage = [.renderTarget]
                textureDescriptor.storageMode = .shared
                let texture = try XCTUnwrap(device.makeTexture(descriptor: textureDescriptor))
                let pass = MTLRenderPassDescriptor()
                pass.colorAttachments[0].texture = texture
                pass.colorAttachments[0].loadAction = .clear
                pass.colorAttachments[0].storeAction = .store
                let command = try XCTUnwrap(queue.makeCommandBuffer())
                let encoder = try XCTUnwrap(command.makeRenderCommandEncoder(descriptor: pass))
                var u = AudioVisualizerView.Coordinator.uniforms(style: style,
                    size: CGSize(width: width, height: height), time: 12, frame: sound,
                    sensitivity: sensitivity, speed: 1, reduced: false)
                encoder.setRenderPipelineState(pipeline)
                encoder.setFragmentBytes(&u, length: MemoryLayout<VizUniforms>.stride, index: 0)
                sound.bands.withUnsafeBytes { encoder.setFragmentBytes($0.baseAddress!, length: $0.count, index: 1) }
                sound.waveform.withUnsafeBytes { encoder.setFragmentBytes($0.baseAddress!, length: $0.count, index: 2) }
                encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
                encoder.endEncoding(); command.commit(); command.waitUntilCompleted()
                XCTAssertEqual(command.status, .completed, command.error?.localizedDescription ?? style.title)
                var pixels = [UInt8](repeating: 0, count: width * height * 4)
                pixels.withUnsafeMutableBytes {
                    texture.getBytes($0.baseAddress!, bytesPerRow: width * 4,
                        from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
                }
                return pixels
            }
            let quiet = try render(AudioSnapshot())
            var sound = AudioSnapshot()
            sound.bands = (0..<64).map { Float(0.4 + 0.35 * sin(Double($0) * 0.3)) }
            sound.components = [Float](repeating: 0.6, count: 8)
            sound.bass = 0.7; sound.mid = 0.5; sound.level = 0.7
            let playing = try render(sound)
            let difference = zip(quiet, playing).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) }
            XCTAssertGreaterThan(difference, 1000, style.title)
            if style == .curtainAurora {
                sound.bass = 0.45
                let medium = try render(sound, sensitivity: 3)
                sound.bass = 0.9
                let loud = try render(sound, sensitivity: 3)
                let change = zip(medium, loud).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) }
                XCTAssertGreaterThan(Double(change) / Double(width * height * 3), 0.75,
                    "High sensitivity must preserve the difference between medium and loud bass")
            }
        }
    }
}

extension VisualizerTests {
    @MainActor
    func testVisualiserControlsReachUniformsWithoutClippingSensitivity() {
        let u = AudioVisualizerView.Coordinator.uniforms(style: .silk, size: .init(width: 640, height: 360),
            time: 12, frame: AudioSnapshot(), sensitivity: 3, speed: 1, reduced: false, vividness: 1.7)
        XCTAssertEqual(u.sensitivity, 3)
        XCTAssertEqual(u.p15, 1.7, accuracy: 0.001)
    }
    func testLyricSizeRemainsMonotonicAfterStageFitting() {
        let content = CGSize(width: 1800, height: 900), available = CGSize(width: 1000, height: 600)
        let small = LyricStageFit.presentationScale(content: content, available: available, userScale: 0.7)
        let normal = LyricStageFit.presentationScale(content: content, available: available, userScale: 1)
        let large = LyricStageFit.presentationScale(content: content, available: available, userScale: 1.5)
        XCTAssertLessThan(small, normal)
        XCTAssertLessThan(normal, large)
        XCTAssertEqual(large / small, 1.5 / 0.7, accuracy: 0.001)
    }
}
