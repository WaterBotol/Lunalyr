import XCTest
@testable import VersoCustom

/// The analyser's frequency axis, time constants and transient timing.
final class VisualizerSpectrumTests: XCTestCase {
    /// Streams `seconds` of a sine (nil: silence) in 10 ms capture buffers,
    /// each stamped with its last sample's capture time. Returns that time.
    @discardableResult
    private func feed(_ analyzer: AudioAnalyzer, _ frequency: Double?, amplitude: Double = 0.25,
                      rate: Double, seconds: Double, from start: Double, sample: inout Int) -> Double {
        let chunk = Int(rate / 100)
        var buffer = [Float](repeating: 0, count: chunk)
        var time = start
        for _ in 0..<Int((seconds * 100).rounded()) {
            for i in 0..<chunk {
                buffer[i] = frequency.map { Float(amplitude * sin(2 * .pi * $0 * Double(sample + i) / rate)) } ?? 0
            }
            sample += chunk
            time = start + Double(sample) / rate
            buffer.withUnsafeBufferPointer {
                analyzer.append($0.baseAddress!, count: chunk, sampleRate: rate, hostTime: time)
            }
        }
        return time
    }

    private func spectrum(of frequency: Double, rate: Double) -> AudioSnapshot {
        let analyzer = AudioAnalyzer()
        analyzer.setAutoGainEnabled(false)
        var sample = 0
        let end = feed(analyzer, frequency, rate: rate, seconds: 0.5, from: 1_000, sample: &sample)
        return analyzer.latest(at: end)
    }

    private func peak(_ values: [Float]) -> Int { values.indices.max { values[$0] < values[$1] }! }

    func testBandsAreLogSpacedInHzAndMatchAcrossSampleRates() {
        XCTAssertEqual(AudioAnalyzer.bandCentre(0, sampleRate: 48_000), 31.5, accuracy: 0.5)
        XCTAssertEqual(AudioAnalyzer.bandCentre(63, sampleRate: 48_000), 15_220, accuracy: 30)
        XCTAssertEqual(AudioAnalyzer.fftSize(for: 44_100), 2048)
        XCTAssertEqual(AudioAnalyzer.fftSize(for: 96_000), 4096, "The window lasts ~43 ms at every rate")
        XCTAssertEqual(AudioAnalyzer.fftSize(for: 192_000), 8192)
        // An octave is ~7 bands everywhere, not 18 at 440 Hz and 2 at 55 Hz.
        for octave in [55.0, 220, 880, 3520] {
            let low = peak(spectrum(of: octave, rate: 48_000).bands)
            let high = peak(spectrum(of: octave * 2, rate: 48_000).bands)
            XCTAssertEqual(Double(high - low), 64 / log2(16_000.0 / 30), accuracy: 2.1, "\(octave) Hz octave")
        }
        for frequency in [55.0, 110, 440, 1760, 7040] {
            let peaks = [44_100.0, 48_000, 96_000].map { peak(spectrum(of: frequency, rate: $0).bands) }
            for (rate, band) in zip([44_100.0, 48_000, 96_000], peaks) {
                let centre = AudioAnalyzer.bandCentre(band, sampleRate: rate)
                XCTAssertLessThan(abs(log2(centre / frequency)), frequency < 100 ? 0.4 : 0.25,
                    "\(frequency) Hz at \(rate) Hz peaks at \(centre) Hz")
            }
            XCTAssertLessThanOrEqual(peaks.max()! - peaks.min()!, 1, "\(frequency) Hz moves with the sample rate")
        }
    }

    func testNamedRangesFollowTheirFrequencies() {
        // Each tone sits in the middle (in octaves) of one named range.
        let tones: [(Double, Int)] = [(42, 0), (95, 1), (245, 2), (700, 3), (1900, 4), (4250, 5), (7750, 6), (12_600, 7)]
        for (frequency, component) in tones {
            let frame = spectrum(of: frequency, rate: 48_000)
            XCTAssertEqual(peak(frame.components), component, "\(frequency) Hz")
        }
        let sub = spectrum(of: 42, rate: 48_000), bass = spectrum(of: 110, rate: 48_000)
        XCTAssertGreaterThan(sub.components[0], sub.components[1] + 0.05)
        XCTAssertGreaterThan(bass.components[1], bass.components[0] + 0.05)
        let registers: [(Double, KeyPath<AudioSnapshot, Float>)] = [(100, \.bass), (800, \.mid), (6000, \.treble)]
        for (frequency, register) in registers {
            let frame = spectrum(of: frequency, rate: 48_000)
            let values = [frame.bass, frame.mid, frame.treble]
            XCTAssertEqual(frame[keyPath: register], values.max()!, "\(frequency) Hz")
        }
    }

    /// The values fosforaAuroraFragment draws its seven ribbons from, bottom
    /// to top. Its bass ribbon used to read the broad 30–250 Hz register, which
    /// contains the sub-bass ribbon's range, so the two moved together (and
    /// the mid ribbon overlapped both of its neighbours). A tone below 60 Hz
    /// still reaches the bass ribbon at about half strength: the 43 ms
    /// window resolves ~23 Hz, so a 42 Hz tone's main lobe spans 0–89 Hz.
    @MainActor
    private func fosforaRibbons(_ frequency: Double) -> [Float] {
        let u = AudioVisualizerView.Coordinator.uniforms(style: .fosforaAurora, size: CGSize(width: 64, height: 36),
            time: 0, frame: spectrum(of: frequency, rate: 48_000), sensitivity: 1, speed: 1, reduced: false)
        return [u.subBass, u[parameter: 3], u.lowMid, u[parameter: 4], u.highMid, u.presence, u.air]
    }

    @MainActor
    func testFosforaRibbonsEachFollowTheirOwnRegister() {
        let tones: [(Double, Int)] = [(42, 0), (95, 1), (245, 2), (700, 3), (1900, 4), (4250, 5), (12_600, 6)]
        for (frequency, ribbon) in tones {
            let ribbons = fosforaRibbons(frequency)
            XCTAssertEqual(peak(ribbons), ribbon, "\(frequency) Hz lights ribbon \(ribbon): \(ribbons)")
        }
        let sub = fosforaRibbons(42), bass = fosforaRibbons(110)
        XCTAssertGreaterThan(sub[0], sub[1] + 0.3, "A 42 Hz tone stays off the bass ribbon: \(sub)")
        XCTAssertGreaterThan(bass[1], bass[0] + 0.3, "A 110 Hz tone stays off the sub-bass ribbon: \(bass)")
        let lowMid = fosforaRibbons(245), mid = fosforaRibbons(700)
        XCTAssertGreaterThan(lowMid[2], lowMid[3] + 0.3, "\(lowMid)")
        XCTAssertGreaterThan(mid[3], mid[2] + 0.1, "\(mid)")
    }

    func testEnvelopesUseSecondsAtEverySampleRate() {
        func afterRelease(_ rate: Double) -> (band: Float, centroid: Float, level: Float) {
            let analyzer = AudioAnalyzer()
            analyzer.setAutoGainEnabled(false)
            var sample = 0
            let toneEnd = feed(analyzer, 440, rate: rate, seconds: 0.6, from: 50, sample: &sample)
            let end = feed(analyzer, nil, rate: rate, seconds: 0.12, from: 50, sample: &sample)
            XCTAssertEqual(end - toneEnd, 0.12, accuracy: 0.001)
            let frame = analyzer.latest(at: end)
            return (frame.bands.max()!, frame.centroid, frame.level)
        }
        let reference = afterRelease(48_000)
        XCTAssertGreaterThan(reference.band, 0.1, "Still releasing 120 ms after the note")
        for rate in [44_100.0, 96_000] {
            let other = afterRelease(rate)
            XCTAssertEqual(other.band, reference.band, accuracy: 0.04, "Band release at \(rate) Hz")
            XCTAssertEqual(other.centroid, reference.centroid, accuracy: 0.05, "Centroid at \(rate) Hz")
        }
    }

    func testKicksAreTimestampedWithinMillisecondsAndSustainedBassFiresOnce() {
        let rate = 48_000.0
        let analyzer = AudioAnalyzer()
        var sample = 0
        let quietEnd = feed(analyzer, nil, rate: rate, seconds: 0.5, from: 10, sample: &sample)
        // Four kicks: 60 ms bursts of 60 Hz every half second.
        var hits: [Double] = []
        var time = quietEnd
        for _ in 0..<4 {
            hits.append(time)
            time = feed(analyzer, 60, amplitude: 0.6, rate: rate, seconds: 0.06, from: 10, sample: &sample)
            time = feed(analyzer, nil, rate: rate, seconds: 0.44, from: 10, sample: &sample)
        }
        for hit in hits {
            XCTAssertLessThan(analyzer.latest(at: hit - 0.003).beat, 0.3, "No beat before the kick")
            XCTAssertGreaterThan(analyzer.latest(at: hit + 0.015).beat, 0.85,
                "The kick's onset is stamped within ~10 ms of the hit, not a whole FFT hop later")
        }
        // A held bass note fires one pulse and then settles.
        let noteStart = time
        let noteEnd = feed(analyzer, 55, amplitude: 0.6, rate: rate, seconds: 2, from: 10, sample: &sample)
        XCTAssertGreaterThan(analyzer.latest(at: noteStart + 0.015).beat, 0.85)
        XCTAssertLessThan(analyzer.latest(at: noteEnd).beat, 0.01, "A sustained note must not keep re-firing")
    }

    func testVisualsWaitOnlyForHighLatencyOutputs() {
        XCTAssertEqual(VisualizerAudio.delay(forOutputLatency: 0.012), 0, "Speakers: no added delay")
        XCTAssertEqual(VisualizerAudio.delay(forOutputLatency: 0.2), 0.2 - VisualizerAudio.pipelineLatency, accuracy: 0.0001)
        XCTAssertEqual(VisualizerAudio.delay(forOutputLatency: 2), VisualizerAudio.maximumDelay, "Bounded")
        XCTAssertEqual(VisualizerAudio.delay(forOutputLatency: .nan), 0)
        XCTAssertEqual(OutputLatencyMonitor.totalLatency(device: 4800, safetyOffset: 480, buffer: 512, stream: 208,
            sampleRate: 48_000), 0.125, accuracy: 0.0001)
        XCTAssertEqual(OutputLatencyMonitor.totalLatency(device: 10, safetyOffset: 0, buffer: 0, stream: 0, sampleRate: 0), 0)

        // A note that began 100 ms ago is not yet audible on AirPods-like
        // output, and is on screen at once on speakers.
        let source = VisualizerAudio()
        let rate = 48_000.0
        let now = AudioAnalyzer.now()
        var buffer = [Float](repeating: 0, count: 480)
        // One second of capture ending now: silence, then a note for the last 100 ms.
        for step in 0..<100 {
            let end = now - 1 + Double(step + 1) * 0.01
            let note = step >= 90
            for i in buffer.indices { buffer[i] = note ? Float(0.3 * sin(2 * .pi * 440 * Double(step * 480 + i) / rate)) : 0 }
            buffer.withUnsafeBufferPointer {
                source.append($0.baseAddress!, count: 480, sampleRate: rate, channel: 0, hostTime: end)
            }
        }
        source.setOutputLatency(0.2)
        XCTAssertEqual(source.visualDelay, 0.155, accuracy: 0.001)
        XCTAssertLessThan(source.latest().bands.max()!, 0.05, "Bluetooth output: the note has not been heard yet")
        let delayed = source.pcm(count: 2048)
        source.setOutputLatency(0.01)
        XCTAssertGreaterThan(source.latest().bands.max()!, 0.4, "Speakers: the note shows immediately")
        let immediate = source.pcm(count: 2048)
        // The reads happen a few milliseconds after `now`, so allow for that.
        let lead = Double((immediate?.end ?? 0) - (delayed?.end ?? 0))
        XCTAssertGreaterThan(lead, 0.1 * rate, "Web scenes receive the same delay-aligned window")
        XCTAssertLessThanOrEqual(lead, 0.155 * rate + 480)
    }

    func testTransientsRiseWithinAFrameWhileBandsKeepTheirSoftAttack() {
        var smoother = VisualizerSmoother(), hit = AudioSnapshot()
        hit.beat = 1; hit.bass = 1
        _ = smoother.frame(AudioSnapshot(), at: 0)
        let first = smoother.frame(hit, at: 1.0 / 60)
        XCTAssertGreaterThan(first.beat, 0.7, "A kick lands on the next frame")
        XCTAssertLessThan(first.bass, 0.2, "Bands still start with zero velocity")
        var released = first
        for frame in 2...30 { released = smoother.frame(AudioSnapshot(), at: Double(frame) / 60) }
        XCTAssertLessThan(released.beat, 0.4)
        XCTAssertGreaterThan(released.beat, 0, "The release stays gentle")
    }

    func testReadersGetIndependentCopiesWhileCaptureContinues() {
        let analyzer = AudioAnalyzer()
        var sample = 0
        let end = feed(analyzer, 880, rate: 48_000, seconds: 0.3, from: 5, sample: &sample)
        let snapshot = analyzer.latest(at: end)
        let window = analyzer.wavefieldPCM(at: end)
        let bands = snapshot.bands, samples = window?.samples
        // Concurrent capture on another thread while this one reads.
        let done = expectation(description: "capture finished")
        DispatchQueue.global(qos: .userInitiated).async {
            var local = sample
            _ = self.feed(analyzer, nil, rate: 48_000, seconds: 0.5, from: 5, sample: &local)
            done.fulfill()
        }
        for _ in 0..<200 {
            let frame = analyzer.latest()
            XCTAssertTrue(frame.bands.allSatisfy(\.isFinite))
        }
        wait(for: [done], timeout: 10)
        XCTAssertEqual(snapshot.bands, bands)
        XCTAssertEqual(window?.samples, samples, "A window handed out never changes underneath its reader")
        XCTAssertEqual(window?.samples.count, 2048)
    }
}
