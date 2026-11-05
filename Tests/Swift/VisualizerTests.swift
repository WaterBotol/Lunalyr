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
        let frequencies = [100.0, 1000, 8000]
        let peaks = frequencies.map { frequency -> Int in
            let analyzer = AudioAnalyzer()
            analyzer.setAutoGainEnabled(false)
            let frame = tone(frequency, into: analyzer)
            XCTAssertTrue(frame.bands.allSatisfy { $0.isFinite && $0 >= 0 && $0 <= 1 })
            return frame.bands.indices.max(by: { frame.bands[$0] < frame.bands[$1] })!
        }
        XCTAssertLessThan(peaks[0], peaks[1])
        XCTAssertLessThan(peaks[1], peaks[2])
        // The axis is log-spaced in real Hz from 30 Hz to 16 kHz: each tone
        // peaks in the band containing it, within the FFT's ~23 Hz bins.
        for (frequency, peak) in zip(frequencies, peaks) {
            let expected = Int(64 * log(frequency / 30) / log(16_000.0 / 30))
            XCTAssertLessThanOrEqual(abs(peak - expected), frequency < 200 ? 2 : 1, "\(frequency) Hz")
        }
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
        // 54 floats, as VisualizerShaderTypes.h asserts; the sound types follow `intro`.
        XCTAssertEqual(MemoryLayout<VizUniforms>.stride, 216)
        XCTAssertEqual(MemoryLayout<VizUniforms>.offset(of: \.intro), 164)
        XCTAssertEqual(MemoryLayout<VizUniforms>.offset(of: \.soundKick), 168)
        XCTAssertEqual(MemoryLayout<VizUniforms>.offset(of: \.soundPad), 188)
        XCTAssertEqual(MemoryLayout<VizUniforms>.offset(of: \.kickOnsets), 192)
        XCTAssertEqual(MemoryLayout<VizUniforms>.offset(of: \.hatAge), 212)
    }
    func testWavefieldMapsLiveFFTPeaksToDistinctResonances() throws {
        let low = tone(220, into: AudioAnalyzer())
        let high = tone(1100, into: AudioAnalyzer())
        XCTAssertEqual(try XCTUnwrap(low.wavefield.peaks.first).frequency, 220, accuracy: 6)
        XCTAssertEqual(try XCTUnwrap(high.wavefield.peaks.first).frequency, 1100, accuracy: 6)

        var bank = WavefieldModes()
        let lowMode = try XCTUnwrap(bank.frame(low, at: 0, time: 0, sensitivity: 1, reduced: false).modes.first)
        XCTAssertEqual(lowMode.shape.x, 2)
        XCTAssertEqual(lowMode.shape.y, 3)
        var highFrame = bank.frame(high, at: 1.0 / 60, time: 0, sensitivity: 1, reduced: false)
        XCTAssertTrue(highFrame.modes.contains { $0.shape.x == 2 && $0.shape.y == 3 },
            "The old resonance must stay visible while the new one attacks")
        for step in 2...60 {
            highFrame = bank.frame(high, at: Double(step) / 60, time: 0, sensitivity: 1, reduced: false)
        }
        let highMode = try XCTUnwrap(highFrame.modes.first)
        XCTAssertNotEqual(SIMD2(lowMode.shape.x, lowMode.shape.y), SIMD2(highMode.shape.x, highMode.shape.y))
    }

    @MainActor
    func testEveryImportedSceneRendersAndChangesWithItsSpectrum() throws {
        let device = try GPUTestSupport.device()
        let queue = try XCTUnwrap(device.makeCommandQueue())
        let library = try device.makeDefaultLibrary(bundle: .main)
        let width = 160, height = 90
        for style in AudioVisualizerStyle.allCases where style.webEngine == nil {
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.vertexFunction = library.makeFunction(name: "fullscreenVertex")
            descriptor.fragmentFunction = library.makeFunction(name: try XCTUnwrap(style.fragment))
            descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
            let pipeline = try device.makeRenderPipelineState(descriptor: descriptor)
            func render(_ sound: AudioSnapshot, sensitivity: Double = 1, time: Double = 12, palette: AlbumWallpaperPalette? = nil) throws -> [UInt8] {
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
                    size: CGSize(width: width, height: height), time: time, frame: sound,
                    sensitivity: sensitivity, speed: 1, reduced: false)
                encoder.setRenderPipelineState(pipeline)
                var wavefield = WavefieldModes()
                AudioVisualizerView.Coordinator.bind(sound, to: encoder, style: style, uniforms: &u,
                    wavefield: &wavefield, at: 0, reduced: false, palette: palette)
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
            let idle = try render(AudioVisualizerView.Coordinator.ambientSpectrum(at: 12))
            let later = try render(AudioVisualizerView.Coordinator.ambientSpectrum(at: 16), time: 16)
            let visiblePixels = idle.enumerated().filter { $0.offset % 4 != 3 && $0.element > 16 }.count
            XCTAssertGreaterThan(visiblePixels, width * height / 100, "\(style.title) has a visible idle scene")
            XCTAssertNotEqual(idle, later, "\(style.title) moves during silence")
            var sound = AudioSnapshot()
            sound.bands = (0..<64).map { Float(0.4 + 0.35 * sin(Double($0) * 0.3)) }
            sound.components = [Float](repeating: 0.6, count: 8)
            sound.bass = 0.7; sound.mid = 0.5; sound.level = 0.7
            if style.isWavefield { sound = tone(220, into: AudioAnalyzer()) }
            let playing = try render(sound)
            let difference = zip(quiet, playing).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) }
            XCTAssertGreaterThan(difference, 1000, style.title)
            let red = AlbumWallpaperPalette(grid: Array(repeating: SIMD3(0, 1, 0.8), count: 9))
            let blue = AlbumWallpaperPalette(grid: Array(repeating: SIMD3(2.0 / 3, 1, 0.8), count: 9))
            func warmth(_ pixels: [UInt8]) -> Int {
                stride(from: 0, to: pixels.count, by: 4).reduce(0) { $0 + Int(pixels[$1 + 2]) - Int(pixels[$1]) }
            }
            XCTAssertGreaterThan(warmth(try render(sound, palette: red)), width * height, style.title)
            XCTAssertLessThan(warmth(try render(sound, palette: blue)), -width * height, style.title)
            let gentle = try render(sound, sensitivity: 0.3)
            let strong = try render(sound, sensitivity: 3)
            let sensitivityDifference = zip(gentle, strong).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) }
            XCTAssertGreaterThan(sensitivityDifference, width * height, "\(style.title) sensitivity must visibly change the same input")
            if style == .colourStrata || style == .overprint {
                func changedPixels(_ band: WritableKeyPath<AudioSnapshot, Float>) throws -> Set<Int> {
                    var isolated = AudioSnapshot()
                    isolated[keyPath: band] = 0.85
                    let image = try render(isolated)
                    return Set(stride(from: 0, to: image.count, by: 4).filter { offset in
                        (0..<3).contains { abs(Int(image[offset + $0]) - Int(quiet[offset + $0])) > 2 }
                    })
                }
                let bass = try changedPixels(\.bass)
                let mids = try changedPixels(\.mid)
                let highs = try changedPixels(\.treble)
                for region in [bass, mids, highs] {
                    XCTAssertGreaterThan(region.count, 50, "\(style.title) responds to all three bands")
                }
                XCTAssertGreaterThan(bass.symmetricDifference(mids).count, 50, style.title)
                XCTAssertGreaterThan(bass.symmetricDifference(highs).count, 50, style.title)
                XCTAssertGreaterThan(mids.symmetricDifference(highs).count, 50, style.title)
            }
            // Star Trails lights its stars by sound type, not by band: StarTrailsTests.
            if style == .tunnel {
                var waveform = AudioSnapshot()
                waveform.waveform = (0..<AudioSnapshot.waveformCount).map { Float(0.3 + 0.2 * sin(Double($0) * 0.1)) }
                XCTAssertNotEqual(try render(waveform, sensitivity: 0.3), try render(waveform, sensitivity: 3))
            }
            if style.isWavefield {
                let high = try render(tone(1100, into: AudioAnalyzer()))
                let topologyChange = zip(playing, high).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) }
                XCTAssertGreaterThan(topologyChange, 10_000, "Changing the note must redraw the field")
                if style == .wavefieldSphere {
                    XCTAssertEqual(Array(playing.prefix(4)), [0, 0, 0, 255], "Outside the sphere stays black")
                }
            }
            if style == .curtainAurora {
                sound.bass = 0.45
                for i in 0..<16 { sound.bands[i] = 0.45 }
                let medium = try render(sound, sensitivity: 3)
                sound.bass = 0.9
                for i in 0..<16 { sound.bands[i] = 0.9 }
                let loud = try render(sound, sensitivity: 3)
                let change = zip(medium, loud).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) }
                XCTAssertGreaterThan(Double(change) / Double(width * height * 3), 0.75,
                    "High sensitivity must preserve the difference between medium and loud bass")

                // Hold time and aggregate loudness fixed. Only a narrow FFT
                // peak moves: its affected region must follow the frequency
                // axis, rather than shaking or brightening the entire scene.
                for centre in [8.0, 31.0, 54.0] {
                    var note = AudioSnapshot()
                    note.bands = (0..<64).map { Float(0.85 * exp(-pow((Double($0) - centre) / 2.0, 2))) }
                    let pixels = try render(note)
                    var change = 0.0, weightedX = 0.0
                    for y in 0..<height { for x in 0..<width {
                        let start = (y * width + x) * 4
                        let delta = (0..<3).reduce(0.0) { $0 + Double(abs(Int(pixels[start + $1]) - Int(quiet[start + $1]))) }
                        change += delta; weightedX += delta * (Double(x) + 0.5) / Double(width)
                    } }
                    XCTAssertGreaterThan(change, 3000)
                    XCTAssertEqual(weightedX / max(1, change), centre / 63.0, accuracy: 0.12,
                        "The responsive section must stay at this note's horizontal frequency position")
                }
                // Real notes through the analyser: the band axis is log-spaced
                // in Hz, so each lands at its own log-frequency position, bass
                // on the left and treble on the right.
                var positions: [Double] = []
                for frequency in [100.0, 1000, 8000] {
                    let analyzer = AudioAnalyzer()
                    analyzer.setAutoGainEnabled(false)
                    let pixels = try render(tone(frequency, into: analyzer))
                    var change = 0.0, weightedX = 0.0
                    for y in 0..<height { for x in 0..<width {
                        let start = (y * width + x) * 4
                        let delta = (0..<3).reduce(0.0) { $0 + Double(abs(Int(pixels[start + $1]) - Int(quiet[start + $1]))) }
                        change += delta; weightedX += delta * (Double(x) + 0.5) / Double(width)
                    } }
                    let position = weightedX / max(1, change)
                    let expected = (64 * log(frequency / 30) / log(16_000.0 / 30) - 0.5) / 63
                    XCTAssertEqual(position, expected, accuracy: 0.15, "\(frequency) Hz")
                    positions.append(position)
                }
                XCTAssertLessThan(positions[0], positions[1])
                XCTAssertLessThan(positions[1], positions[2])
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
        XCTAssertEqual(small / normal, 0.7, accuracy: 0.001)
        XCTAssertEqual(large * content.width, available.width, accuracy: 0.001)
    }
}
