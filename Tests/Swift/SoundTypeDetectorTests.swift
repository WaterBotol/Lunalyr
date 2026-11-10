import XCTest
@testable import VersoCustom

/// The sound types (kick, snare, hat, bass, vocal, pad) on synthetic music
/// with ground truth. The first group feeds the detector the same calibrated
/// spectra AudioAnalyzer hands it, straight from memory; the pipeline group
/// goes through VisualizerAudio.append and latest(at:), as the capture worker
/// and the render loop do. No audio devices are involved.
final class SoundTypeDetectorTests: XCTestCase {
    /// Every fixture is synthesised and analysed once per test run.
    private enum Runs {
        static func run(_ fixture: SoundFixture, hopDivisor: Int = 4) -> SoundTypeRun {
            SoundTypeRun(fixture, fftSize: AudioAnalyzer.fftSize(for: fixture.fs), hopDivisor: hopDivisor)
        }
        static let kick = run(SoundFixtures.kicks(fs: 48_000))
        static let eightOhEight = run(SoundFixtures.kicks(fs: 48_000, kind: .eightOhEight))
        static let snare = run(SoundFixtures.snares(fs: 48_000))
        static let hats = run(SoundFixtures.hats(fs: 48_000))
        static let bass = run(SoundFixtures.bassline(fs: 48_000))
        static let vocal = run(SoundFixtures.vocal(fs: 48_000, consonants: false))
        static let consonants = run(SoundFixtures.vocal(fs: 48_000, consonants: true))
        static let pad = run(SoundFixtures.pads(fs: 48_000))
        static let mixFixture = SoundFixtures.mix(fs: 48_000)
        static let mix = run(mixFixture)
        static let noise = run(SoundFixtures.pinkNoise(fs: 48_000))
        /// 3 s of the mix, from 3 s: every class plays in it.
        static let clip = Array(mixFixture.samples[144_000..<288_000])
        /// Its first 2 s.
        static let shortClip = Array(clip.prefix(96_000))
    }

    /// Capture time of the first sample in the pipeline tests.
    private static let origin = 1_000.0

    private func median(_ values: [Float]) -> Float { values.sorted()[values.count / 2] }

    // MARK: - Separation

    func testKicksAreKicksAndNothingElse() {
        let r = Runs.kick
        XCTAssertGreaterThanOrEqual(r.f1(.kick).f1, 0.95)
        XCTAssertLessThanOrEqual(max(r.count(.snare), r.count(.hat)), 1, "A beater click is not a snare or a hat")
        XCTAssertLessThanOrEqual(r.p95(.bass), 0.2, "Kick tails are held out of the bass")
        XCTAssertLessThanOrEqual(max(r.p95(.vocal), r.p95(.pad)), 0.1)
        XCTAssertGreaterThanOrEqual(median(r.hitPeaks(.kick)), 0.5, "Each hit flares")
    }

    func testAnEightOhEightIsAKickWithABassTail() {
        let r = Runs.eightOhEight
        XCTAssertGreaterThanOrEqual(r.f1(.kick).recall, 0.9)
        XCTAssertGreaterThanOrEqual(r.mean(.bass, after: .kick, from: 0.15, to: 0.45), 0.4, "A long 808 tail is bass")
        XCTAssertLessThanOrEqual(r.count(.snare) + r.count(.hat), 2)
    }

    func testSnaresAndClapsAreSnares() {
        let r = Runs.snare
        XCTAssertGreaterThanOrEqual(r.f1(.snare).f1, 0.95)
        XCTAssertLessThanOrEqual(r.count(.kick), 1)
        XCTAssertLessThanOrEqual(Double(r.count(.hat)), 0.2 * Double(r.fixture.truth.onsets[.snare]?.count ?? 0))
        XCTAssertLessThanOrEqual(max(r.p95(.bass), r.p95(.vocal), r.p95(.pad)), 0.15, "A snare's ringing body is not tonal")
    }

    func testHiHatsAreHats() {
        let r = Runs.hats
        XCTAssertGreaterThanOrEqual(r.f1(.hat).f1, 0.9)
        XCTAssertEqual(r.count(.kick), 0)
        XCTAssertLessThanOrEqual(r.count(.snare), 2)
        XCTAssertLessThanOrEqual(max(r.p95(.bass), r.p95(.vocal), r.p95(.pad)), 0.1)
    }

    func testABassLineIsBassNotKicks() {
        let r = Runs.bass
        let bass = r.spans(.bass)
        XCTAssertGreaterThanOrEqual(bass.inside, 0.6)
        XCTAssertLessThanOrEqual(bass.outside, 0.1)
        XCTAssertLessThanOrEqual(Double(r.count(.kick)), 0.25 * Double(r.fixture.truth.active[.bass]?.count ?? 0),
                                 "Only a note from silence, after a gap or an octave drop may pass for a kick")
        XCTAssertLessThanOrEqual(max(r.p95(.vocal), r.p95(.pad)), 0.3, "A bass line's harmonics belong to it")
    }

    func testASungVoiceIsVocalNotPadOrDrums() {
        for (r, consonants) in [(Runs.vocal, false), (Runs.consonants, true)] {
            let vocal = r.spans(.vocal)
            XCTAssertGreaterThanOrEqual(vocal.inside, 0.6, r.fixture.name)
            XCTAssertLessThanOrEqual(vocal.outside, 0.15, r.fixture.name)
            XCTAssertLessThanOrEqual(r.p95(.pad), 0.25, "\(r.fixture.name): a moving voice is not a pad")
            XCTAssertLessThanOrEqual(r.p95(.bass), 0.1, r.fixture.name)
            XCTAssertEqual(r.count(.kick), 0, r.fixture.name)
            XCTAssertLessThanOrEqual(r.count(.snare) + r.count(.hat), consonants ? 3 : 1, "Consonants are not drums")
        }
    }

    func testPadChordsArePad() {
        let r = Runs.pad
        XCTAssertGreaterThanOrEqual(r.spans(.pad).inside, 0.6)
        XCTAssertLessThanOrEqual(r.p95(.vocal), 0.25, "A chord's beating partials do not move like a voice")
        XCTAssertLessThanOrEqual(r.p95(.bass), 0.25, "A chord's root is not a bass line")
        XCTAssertLessThanOrEqual(max(r.count(.kick), r.count(.snare), r.count(.hat)), 1)
    }

    func testAMixSeparatesEveryClass() {
        let r = Runs.mix
        XCTAssertGreaterThanOrEqual(r.f1(.kick).f1, 0.9)
        XCTAssertGreaterThanOrEqual(r.f1(.snare).f1, 0.9)
        XCTAssertGreaterThanOrEqual(r.f1(.hat).f1, 0.65, "Dense hats, some inside bright snare tails")
        let bass = r.spans(.bass), vocal = r.spans(.vocal)
        XCTAssertGreaterThanOrEqual(bass.inside, 0.5)
        XCTAssertLessThanOrEqual(bass.outside, 0.1)
        XCTAssertGreaterThanOrEqual(vocal.inside, 0.35)
        XCTAssertLessThanOrEqual(vocal.outside, 0.1)
        XCTAssertGreaterThanOrEqual(r.spans(.pad).inside, 0.15)
        // Counters never step back, and an onset time only changes with its counter.
        for (a, b) in zip(r.reads, r.reads.dropFirst()) {
            XCTAssertGreaterThanOrEqual(b.kickOnsets, a.kickOnsets)
            XCTAssertGreaterThanOrEqual(b.hatOnsets, a.hatOnsets)
            if b.hatOnsets == a.hatOnsets { XCTAssertEqual(b.hatTime, a.hatTime) } else { XCTAssertGreaterThan(b.hatTime, a.hatTime) }
        }
    }

    func testPinkNoiseLightsNothing() {
        let r = Runs.noise
        XCTAssertLessThanOrEqual(max(r.count(.kick), r.count(.snare), r.count(.hat)), 2)
        XCTAssertLessThanOrEqual(max(r.p95(.bass), r.p95(.vocal), r.p95(.pad)), 0.25)
    }

    // MARK: - Independence

    private func assertMatchesTheMix(_ r: SoundTypeRun, _ label: String, file: StaticString = #filePath, line: UInt = #line) {
        let reference = Runs.mix
        for c in [SoundClass.kick, .snare] {
            XCTAssertEqual(r.f1(c).f1, reference.f1(c).f1, accuracy: 0.10, "\(label) \(c)", file: file, line: line)
        }
        XCTAssertEqual(r.f1(.hat).f1, reference.f1(.hat).f1, accuracy: 0.15, "\(label) hat", file: file, line: line)
        for c in [SoundClass.bass, .vocal, .pad] {
            XCTAssertEqual(r.spans(c).inside, reference.spans(c).inside, accuracy: 0.12, "\(label) \(c)", file: file, line: line)
            XCTAssertLessThanOrEqual(r.spans(c).outside, 0.1, "\(label) \(c)", file: file, line: line)
        }
    }

    func testSampleRatesAndHopsGiveTheSameResult() {
        assertMatchesTheMix(Runs.run(SoundFixtures.mix(fs: 44_100)), "44.1 kHz")
        assertMatchesTheMix(Runs.run(SoundFixtures.mix(fs: 96_000)), "96 kHz")
        assertMatchesTheMix(Runs.run(Runs.mixFixture, hopDivisor: 2), "21.3 ms hop")
        assertMatchesTheMix(Runs.run(Runs.mixFixture, hopDivisor: 8), "5.3 ms hop")
    }

    func testQuietAndLoudMastersGiveTheSameResult() {
        assertMatchesTheMix(Runs.run(Runs.mixFixture.mastered(gain: 0.063, name: "quiet")), "−24 dB master")
        assertMatchesTheMix(Runs.run(Runs.mixFixture.mastered(gain: 2, softClip: 1, name: "loud")), "+6 dB limited master")
    }

    func testInvalidSpectraAndUnusualRatesStayFinite() {
        let detector = SoundTypeDetector()
        var power = [Float](repeating: 1e-4, count: 1024)
        power[7] = .nan; power[300] = .infinity; power[500] = -3
        for pass in 0..<40 {
            power.withUnsafeBufferPointer { detector.process(power: $0, binHz: 23.4375, dt: 0.0107, time: Double(pass) * 0.0107) }
        }
        let output = detector.output
        XCTAssertTrue([output.bass, output.vocal, output.pad].allSatisfy { $0.isFinite && $0 >= 0 && $0 <= 1 })
        // Too short a spectrum is ignored; the lowest and highest rates the analyser accepts work.
        power.withUnsafeBufferPointer { detector.process(power: UnsafeBufferPointer(rebasing: $0[0..<8]), binHz: 23, dt: 0.01, time: 1) }
        for (rate, bins) in [(8_000.0, 256), (192_000.0, 4096)] {
            let spectrum = [Float](repeating: 1e-3, count: bins)
            for pass in 0..<10 {
                spectrum.withUnsafeBufferPointer {
                    detector.process(power: $0, binHz: Float(rate) / Float(2 * bins), dt: 0.0107, time: 2 + Double(pass) * 0.0107)
                }
            }
            XCTAssertTrue(detector.output.bass.isFinite, "\(rate) Hz")
        }
    }

    // MARK: - The pipeline

    /// Feeds `left` (and `right`, as a second channel) to `audio` in
    /// `chunk`-sample buffers stamped with exact capture times, and after each
    /// buffer reads every instant `index / fps` that the buffer completed.
    @discardableResult
    private func feed(_ audio: VisualizerAudio, _ left: [Float], right: [Float]? = nil, fs: Double = 48_000,
                      chunk: Int = 512, from start: Double = 0, fps: Double? = nil,
                      read: (Double, AudioSnapshot) -> Void = { _, _ in }) -> Double {
        if right != nil { audio.setChannelCount(2) }
        var i = 0, frame = 0
        while i < left.count {
            let n = min(chunk, left.count - i)
            let end = Self.origin + start + Double(i + n) / fs
            left.withUnsafeBufferPointer { audio.append($0.baseAddress! + i, count: n, sampleRate: fs, channel: 0, hostTime: end) }
            right?.withUnsafeBufferPointer { audio.append($0.baseAddress! + i, count: n, sampleRate: fs, channel: 1, hostTime: end) }
            i += n
            if let fps {
                while Self.origin + Double(frame) / fps <= end {
                    let t = Self.origin + Double(frame) / fps
                    if t >= Self.origin + start { read(t, audio.latest(at: t)) }
                    frame += 1
                }
            }
        }
        return Self.origin + start + Double(left.count) / fs
    }

    func testTheAnalyzerHandsOverCalibratedSpectraEveryQuarterWindow() throws {
        let analyzer = AudioAnalyzer()
        var passes: [(position: Int, time: Double, dt: Double, binHz: Float, power: [Float])] = []
        analyzer.observeSpectra { passes.append(($0.position, $0.time, $0.dt, $0.binHz, Array($0.power))) }
        // 0.25 s of a 1 kHz tone at amplitude 0.5, in 480-sample buffers.
        let fs = 48_000.0, amplitude: Float = 0.5
        let samples = (0..<12_000).map { amplitude * Float(sin(2 * .pi * 1000 * Double($0) / fs)) }
        var i = 0
        while i < samples.count {
            samples.withUnsafeBufferPointer {
                analyzer.append($0.baseAddress! + i, count: 480, sampleRate: fs, hostTime: Self.origin + Double(i + 480) / fs)
            }
            i += 480
        }
        XCTAssertEqual(passes.count, 12_000 / 512, "One spectrum per quarter window, full passes and halfway points alike")
        let reference = PlainSpectrum(fftSize: 2048)
        // The analyser removes any DC offset first (AudioAnalyzer.dcCutoff).
        let pole = Float(exp(-2 * Double.pi * AudioAnalyzer.dcCutoff / fs))
        var previousInput: Float = 0, previousOutput: Float = 0
        let analysed = samples.map { x -> Float in
            let y = x - previousInput + pole * previousOutput
            previousInput = x; previousOutput = y
            return y
        }
        for (k, pass) in passes.enumerated() {
            XCTAssertEqual(pass.position, (k + 1) * 512)
            XCTAssertEqual(pass.time, Self.origin + Double(pass.position) / fs, accuracy: 1e-9, "The newest sample's capture time")
            XCTAssertEqual(pass.dt, 512 / fs, accuracy: 1e-12)
            XCTAssertEqual(pass.binHz, 23.4375)
            XCTAssertEqual(pass.power.count, 1024, "The plain window's bins, not the zero-padded transform's")
            XCTAssertEqual(pass.power[0], 0)
            // Full passes reuse the padded transform; between them a plain FFT
            // runs. Both are the plain FFT of the newest window.
            let expected = analysed.withUnsafeBufferPointer { Array(reference.compute($0.baseAddress!, endingAt: pass.position)) }
            let scale = try XCTUnwrap(expected.max())
            XCTAssertLessThan(zip(pass.power, expected).map { abs($0 - $1) }.max() ?? 1, scale * 1e-4, "pass \(k)")
            if pass.position >= 2048 {
                // Calibrated: a sinusoid of amplitude A sums to A²/2 over its main lobe (1 kHz is bin 42.7).
                XCTAssertEqual(pass.power[39...46].reduce(0, +), amplitude * amplitude / 2, accuracy: 0.002)
            }
        }
        // Once the observer is removed, nothing more is handed over.
        analyzer.observeSpectra(nil)
        samples.withUnsafeBufferPointer { analyzer.append($0.baseAddress!, count: 4800, sampleRate: fs, hostTime: Self.origin + 1) }
        XCTAssertEqual(passes.count, 12_000 / 512)
    }

    func testStereoChannelsShareOneDetectorWithoutCancelling() {
        let clip = Runs.shortClip
        let mono = VisualizerAudio(), stereo = VisualizerAudio()
        var monoReads: [SoundTypeFeatures] = [], stereoReads: [SoundTypeFeatures] = []
        feed(mono, clip, fps: 50) { monoReads.append($1.sound) }
        feed(stereo, clip, right: clip.map { -$0 }, fps: 50) { stereoReads.append($1.sound) }
        XCTAssertGreaterThanOrEqual(monoReads.last?.kickOnsets ?? 0, 3)
        XCTAssertEqual(stereoReads, monoReads, "Opposite-phase channels average to the same power, never to silence")
        // Hard-panned instruments: kicks on the left only, hats on the right
        // only, a 32nd note after each kick.
        let seconds = 2.5, count = Int(seconds * 48_000), delay = 3_000
        let panned = VisualizerAudio()
        let right = [Float](repeating: 0, count: delay) + Runs.hats.fixture.samples.prefix(count - delay)
        let end = feed(panned, Array(Runs.kick.fixture.samples.prefix(count)), right: right)
        let sound = panned.latest(at: end).sound
        let kicks = Runs.kick.fixture.truth.onsets[.kick]!.filter { $0 < seconds - 0.05 }.count
        let hats = Runs.hats.fixture.truth.onsets[.hat]!.filter { $0 + Double(delay) / 48_000 < seconds - 0.05 }.count
        XCTAssertEqual(Int(sound.kickOnsets), kicks)
        XCTAssertGreaterThanOrEqual(Double(sound.hatOnsets), 0.85 * Double(hats))
        XCTAssertLessThanOrEqual(Int(sound.hatOnsets), hats)
        XCTAssertEqual(sound.snareOnsets, 0)
    }

    /// One append can carry more spectra than the stereo pairing keeps
    /// waiting: 8192 samples at 48 kHz are 16 per channel, 4096 at 16 kHz are
    /// 32. Every instant still reaches the detector exactly once, so two
    /// identical channels give exactly the mono result.
    func testLargeStereoBuffersAnalyseEachInstantOnce() {
        let low = SoundFixtures.kicks(fs: 16_000)
        for (samples, fs, chunk) in [(Array(Runs.clip.prefix(6 * 8192)), 48_000.0, 8192),
                                     (Array(low.samples.prefix(8 * 4096)), 16_000, 4096)] {
            let label = "\(Int(fs)) Hz, \(chunk)-sample buffers"
            let mono = VisualizerAudio(), stereo = VisualizerAudio()
            let end = feed(mono, samples, fs: fs, chunk: chunk)
            feed(stereo, samples, right: samples, fs: fs, chunk: chunk)
            let sound = mono.latest(at: end).sound
            XCTAssertGreaterThan(sound.kickOnsets + sound.snareOnsets + sound.hatOnsets, 3, label)
            XCTAssertEqual(stereo.soundTypePasses, mono.soundTypePasses, label)
            XCTAssertEqual(stereo.latest(at: end).sound, sound, label)
        }
    }

    /// The right channel's analyser sits idle while the input is mono. When
    /// the input turns stereo mid-source (mid-hop here), it restarts in step
    /// with the left one, so the channels still pair: kicks on the left alone
    /// keep counting.
    func testTheLeftChannelStillCountsAfterTheInputTurnsStereo() {
        let kicks = Array(Runs.kick.fixture.samples.prefix(144_000)), split = 72_300
        let audio = VisualizerAudio()
        let switched = feed(audio, Array(kicks[..<split]), chunk: 1000)
        let truth = Runs.kick.fixture.truth.onsets[.kick]!
        XCTAssertEqual(Int(audio.latest(at: switched).sound.kickOnsets), truth.filter { $0 < Double(split) / 48_000 - 0.05 }.count)
        let passes = audio.soundTypePasses
        let end = feed(audio, Array(kicks[split...]), right: [Float](repeating: 0, count: kicks.count - split), chunk: 1000,
                       from: Double(split) / 48_000)
        XCTAssertEqual(Int(audio.latest(at: end).sound.kickOnsets), truth.filter { $0 < 3 - 0.05 }.count)
        XCTAssertEqual(audio.soundTypePasses - passes, (kicks.count - split + split % 512) / 512, "One pass per quarter window")
    }

    /// Connecting headphones makes the display wait up to a quarter second
    /// longer for the sound, so the display time steps back. The sound types
    /// shown never do: they hold until the display catches up, so no counter
    /// steps back and no onset flares twice.
    func testTheSoundTypesShownNeverGoBackWhenTheDisplayWaitsLonger() {
        let audio = VisualizerAudio()   // measures no output device: no wait
        let clip = Runs.shortClip
        // The clip ends a second from now, so the display shows its middle.
        feed(audio, clip, from: AudioAnalyzer.now() + 1 - Double(clip.count) / 48_000 - Self.origin)
        let shown = audio.latest().sound
        XCTAssertGreaterThan(shown.hatOnsets, 0)
        audio.setOutputLatency(VisualizerAudio.maximumDelay + VisualizerAudio.pipelineLatency)
        XCTAssertLessThan(audio.presentationTime, shown.presentationTime, "The display time stepped back")
        XCTAssertEqual(audio.latest().sound, shown, "Held at the newest time shown")
        XCTAssertEqual(audio.latestSound(), shown, "The web engines' read holds too")
        // Once the display is past it again, the sound types move on.
        audio.setOutputLatency(0)
        XCTAssertGreaterThan(audio.latest().sound.presentationTime, shown.presentationTime)
        // A new source starts over at the display time.
        audio.setOutputLatency(VisualizerAudio.maximumDelay + VisualizerAudio.pipelineLatency)
        audio.reset()
        XCTAssertLessThan(audio.latest().sound.presentationTime, shown.presentationTime)
    }

    /// Read-time evaluation keeps no per-frame state: reading four times as
    /// often changes nothing at the instants both rates share, for the
    /// snapshot, the smoother's output and the uniforms, calm or not.
    func testReadsAreIndependentOfFrameRate() {
        func run(_ fps: Double) -> [SoundTypeFeatures] {
            let audio = VisualizerAudio()
            var smoother = VisualizerSmoother()
            var reads: [SoundTypeFeatures] = []
            feed(audio, Runs.clip, fps: fps) { t, snapshot in
                XCTAssertEqual(smoother.frame(snapshot, at: t).sound, snapshot.sound, "The smoother passes sound types through")
                reads.append(snapshot.sound)
            }
            return reads
        }
        let fast = run(120), slow = run(30)
        XCTAssertGreaterThan(slow.last?.hatOnsets ?? 0, 5)
        var compared = 0
        for (k, read) in slow.enumerated() where 4 * k < fast.count {
            XCTAssertEqual(read, fast[4 * k])
            for calm in [false, true] {
                let response = VisualizerResponse(reduceMotion: calm)
                XCTAssertEqual(SoundTypeUniforms(read, at: read.presentationTime, response: response),
                               SoundTypeUniforms(fast[4 * k], at: fast[4 * k].presentationTime, response: response))
            }
            compared += 1
        }
        XCTAssertGreaterThan(compared, 60)
        // Counters never step back; ages restart only when their counter moves.
        for (a, b) in zip(fast, fast.dropFirst()) {
            XCTAssertGreaterThanOrEqual(b.kickOnsets, a.kickOnsets)
            XCTAssertGreaterThanOrEqual(b.snareOnsets, a.snareOnsets)
            XCTAssertGreaterThanOrEqual(b.hatOnsets, a.hatOnsets)
            if b.kickOnsets == a.kickOnsets { XCTAssertEqual(b.kickTime, a.kickTime) }
        }
    }

    func testCallbackSizeDoesNotChangeTheResult() {
        func onsets(_ chunk: Int) -> (all: Set<Int>, end: SoundTypeFeatures) {
            let audio = VisualizerAudio()
            var all = Set<Int>()
            // The rings hold the newest onsets of each class: collected after
            // every buffer, they give every onset whatever the buffer size.
            let end = feed(audio, Runs.shortClip, chunk: chunk, fps: 25) { _, snapshot in
                for (ring, tag) in [(snapshot.sound.kickRing, 0), (snapshot.sound.snareRing, 1), (snapshot.sound.hatRing, 2)] {
                    for onset in ring.onsets where onset.time.isFinite { all.insert(Int((onset.time * 1e5).rounded()) * 4 + tag) }
                }
            }
            return (all, audio.latest(at: end).sound)
        }
        let reference = onsets(512)
        XCTAssertGreaterThan(reference.all.count, 8)
        for chunk in [441, 4096] {
            // Buffer boundaries move the capture timestamps by rounding only.
            let other = onsets(chunk), a = other.end, b = reference.end
            XCTAssertEqual(other.all, reference.all, "\(chunk)-sample buffers")
            XCTAssertEqual([a.kickOnsets, a.snareOnsets, a.hatOnsets], [b.kickOnsets, b.snareOnsets, b.hatOnsets])
            for (x, y) in [(a.kickTime, b.kickTime), (a.snareTime, b.snareTime), (a.hatTime, b.hatTime)] {
                XCTAssertEqual(x, y, accuracy: 1e-9)
            }
            for c in SoundClass.allCases {
                XCTAssertEqual(SoundTypeRun.value(a, c), SoundTypeRun.value(b, c), accuracy: 1e-5, "\(chunk): \(c)")
            }
        }
    }

    func testSilenceFadesEveryEnvelopeAndHoldsTheCounters() {
        let audio = VisualizerAudio()
        let music = Runs.shortClip
        var loudest: Float = 0
        let quietFrom = feed(audio, music, fps: 20) { _, snapshot in
            loudest = max(loudest, snapshot.sound.bass, snapshot.sound.vocal, snapshot.sound.pad)
        }
        let playing = audio.latest(at: quietFrom).sound
        XCTAssertGreaterThan(loudest, 0.3)
        XCTAssertGreaterThan(playing.kickOnsets, 0)
        // Two seconds of digital silence while capture continues.
        let end = feed(audio, [Float](repeating: 0, count: 96_000), from: 2)
        let silent = audio.latest(at: end).sound
        for c in SoundClass.allCases {
            XCTAssertLessThan(SoundTypeRun.value(silent, c), 0.02, "\(c) fades in silence")
        }
        XCTAssertEqual(silent.kickOnsets, playing.kickOnsets, "Silence never adds onsets")
        XCTAssertEqual(silent.hatOnsets, playing.hatOnsets)
        XCTAssertEqual(silent.kickTime, playing.kickTime, "Onset times hold")
        XCTAssertGreaterThan(end - silent.kickTime, 1.9, "Ages keep growing")
        // Capture stops: values fade with the analyser's idle fade, counters hold.
        let stopped = feed(audio, Array(music.prefix(24_000)), from: 4)
        let paused = audio.latest(at: stopped + 2).sound
        XCTAssertLessThan(max(paused.bass, paused.vocal, paused.pad), 0.001)
        XCTAssertGreaterThanOrEqual(paused.kickOnsets, playing.kickOnsets)
        // A new source starts from zero.
        audio.reset()
        XCTAssertEqual(audio.latest(at: stopped + 3).sound, {
            var empty = SoundTypeFeatures(); empty.presentationTime = stopped + 3; return empty
        }())
    }

    // MARK: - Reading and publishing

    func testTheHistoryShowsOnlyWhatWasHeardByTheDisplayTime() {
        var history = SoundTypeHistory()
        var output = SoundTypeDetector.Output()
        // A pass every 10 ms; a kick at 1.000 s (fired, then topped up) and one at 1.200 s.
        for pass in 0...40 {
            let time = 0.9 + Double(pass) * 0.01
            if pass == 11 { output.kickOnsets = 1; output.newestKick = SoundOnset(time: 1.0, strength: 0.6) }
            if pass == 12 { output.newestKick.strength = 0.8 }
            if pass == 31 { output.kickOnsets = 2; output.newestKick = SoundOnset(time: 1.2, strength: 0.5) }
            output.bass = Float(pass) / 40
            history.record(output, at: time)
        }
        let before = history.read(at: 0.999)
        XCTAssertEqual(before.kickOnsets, 0, "A display lagging capture has not heard the kick yet")
        XCTAssertEqual(before.kick, 0)
        XCTAssertEqual(before.kickTime, -.infinity)
        let peak = history.read(at: 1.0 + 0.010 * log(27))   // the normal kick shape peaks here
        XCTAssertEqual(peak.kickOnsets, 1)
        XCTAssertEqual(peak.kick, 0.8, accuracy: 1e-4, "The top-up raised the strength")
        XCTAssertEqual(peak.kickRing.onsets[0], SoundOnset(time: 1.0, strength: 0.8))
        let between = history.read(at: 1.105)
        XCTAssertEqual(between.bass, (Float(20) + 0.5) / 40, accuracy: 1e-5, "Interpolated between the passes around it")
        let both = history.read(at: 1.25)
        XCTAssertEqual(both.kickOnsets, 2)
        XCTAssertEqual(both.kickTime, 1.2)
        XCTAssertEqual(both.kickRing.onsets[0].time, 1.2)
        XCTAssertEqual(both.kickRing.onsets[1].time, 1.0)
        XCTAssertEqual(both.kickRing.onsets[2].time, -.infinity)
        XCTAssertEqual(history.read(at: 1.25), both, "A read is a pure function of the display time")
        // After the newest pass the value holds, then fades like the bands do once capture stops.
        XCTAssertEqual(history.read(at: 1.4).bass, 1)
        XCTAssertEqual(history.read(at: 1.3 + 0.25 + 0.2).bass, exp(-1), accuracy: 1e-4)
    }

    func testOnsetShapesPeakAtTheirStrength() {
        let normal = SoundTypeShapes.normal, calm = SoundTypeShapes.calm
        for shape in [normal.kick, normal.snare, normal.hat, calm.kick, calm.snare, calm.hat] {
            let peakAge = shape.attack * log((shape.attack + shape.release) / shape.attack)
            XCTAssertEqual(shape.value(at: peakAge), 1, accuracy: 1e-9)
            XCTAssertEqual(shape.value(at: 0), 0)
            XCTAssertEqual(shape.value(at: -1), 0)
            XCTAssertLessThan(shape.value(at: 4), 0.01)
        }
        var ring = SoundOnsetRing()
        ring.onsets[0] = SoundOnset(time: 1, strength: 0.9)
        ring[1] = SoundOnset(time: 0.95, strength: 0.4)
        XCTAssertEqual(ring.count, SoundOnsetRing.capacity)
        XCTAssertEqual(ring.map(\.strength), [0.9, 0.4, 0, 0, 0, 0, 0, 0])
        XCTAssertEqual(ring.onsets[1], ring[1])
        XCTAssertEqual(MemoryLayout<SoundOnsetRing>.stride, SoundOnsetRing.capacity * MemoryLayout<SoundOnset>.stride,
                       "Stored inline, so snapshots carry their rings without heap storage")
        let kick = SoundTypeShapes.normal.kick
        XCTAssertEqual(kick.envelope(ring, at: 1.033), Float(0.9 * kick.value(at: 0.033)), accuracy: 1e-6,
                       "The largest flare, not the sum, so dense hits never saturate")
        XCTAssertEqual(kick.envelope(ring, at: 0.99), Float(0.4 * kick.value(at: 0.04)), accuracy: 1e-6,
                       "An onset after the display time does not show yet")
    }

    @MainActor
    func testUniformsCarrySoundTypesWithPunchCalmAndWrap() {
        var frame = AudioSnapshot()
        frame.sound.kickRing.onsets[0] = SoundOnset(time: 99.97, strength: 1)   // 30 ms before display: near the peak
        frame.sound.bass = 0.6; frame.sound.vocal = 0.3; frame.sound.pad = 0.2
        frame.sound.kickOnsets = (1 << 24) + 7; frame.sound.kickTime = 99.97
        frame.sound.hatOnsets = 12; frame.sound.hatTime = 98.5
        frame.sound.presentationTime = 100
        func make(calm: Bool, punch: Double = 2, at time: Double? = nil) -> VizUniforms {
            AudioVisualizerView.Coordinator.uniforms(style: .starTrails, size: CGSize(width: 64, height: 36), time: 0,
                frame: frame, sensitivity: 1, speed: 1, reduced: calm, punch: punch, presentationTime: time)
        }
        let normal = make(calm: false), calm = make(calm: true)
        XCTAssertEqual(normal.soundKick, 1, "Punch scales transients, clamped to 1")
        XCTAssertEqual(make(calm: false, punch: 1).soundKick, 0.998, accuracy: 0.002)
        XCTAssertEqual(make(calm: false, punch: 0).soundKick, 0)
        XCTAssertEqual(normal.soundBass, 0.6, accuracy: 1e-6, "Punch leaves sustained classes alone")
        XCTAssertEqual(normal.soundVocal, 0.3, accuracy: 1e-6)
        XCTAssertEqual(normal.soundPad, 0.2, accuracy: 1e-6)
        XCTAssertEqual(normal.kickOnsets, 7, "Counters wrap at 2^24 so a float holds them exactly")
        XCTAssertEqual(normal.beatCount, 7, "Kicks are the beat count")
        XCTAssertEqual(normal.hatOnsets, 12)
        XCTAssertEqual(normal.snareOnsets, 0)
        XCTAssertEqual(normal.kickAge, 0.03, accuracy: 1e-5)
        XCTAssertEqual(normal.hatAge, 1.5, accuracy: 1e-5)
        XCTAssertEqual(normal.snareAge, 60, "60 means no onset yet")
        XCTAssertEqual(make(calm: false, at: 100.03).kickAge, 0.06, accuracy: 1e-5, "Ages are taken at the presentation time")
        XCTAssertLessThan(calm.soundKick, 0.25, "Calm reads the slow shape, still rising at 30 ms, at half strength")
        let calmPeakTime = 99.97 + 0.12 * log(6)
        XCTAssertEqual(make(calm: true, at: calmPeakTime).soundKick, 0.5, accuracy: 1e-4, "Calm flares peak at half strength")
        XCTAssertEqual(make(calm: true, punch: 0.5, at: calmPeakTime).soundKick, 0.25, accuracy: 1e-4,
                       "Below Punch 1 calm follows Punch: never harder than Punch alone")
        XCTAssertEqual(make(calm: true, punch: 0, at: calmPeakTime).soundKick, 0, "Beat punch Off stays off under calm")
        XCTAssertEqual(calm.soundBass, 0.6, accuracy: 1e-6, "Calm keeps the sustained classes")
        XCTAssertEqual(calm.kickOnsets, 7)
        XCTAssertEqual(calm.reduceMotion, 1)
        // Silent or synthetic input lights nothing.
        let idle = AudioVisualizerView.Coordinator.uniforms(style: .starTrails, size: CGSize(width: 64, height: 36),
            time: 0, frame: AudioVisualizerView.Coordinator.ambientSpectrum(at: 3), sensitivity: 1, speed: 1, reduced: false)
        XCTAssertEqual([idle.soundKick, idle.soundSnare, idle.soundHat, idle.soundBass, idle.soundVocal, idle.soundPad],
                       [0, 0, 0, 0, 0, 0])
        XCTAssertEqual([idle.kickAge, idle.snareAge, idle.hatAge], [60, 60, 60])
    }

    /// A pass every 10 ms with a kick every 0.25 s from 1 s (fired 13 ms
    /// before its pass, strength 0.7) and a steady pad.
    private func steadyKicks(until end: Double = 4.5) -> SoundTypeHistory {
        var history = SoundTypeHistory()
        var output = SoundTypeDetector.Output()
        output.pad = 0.8
        var pass = 0
        while Double(pass) * 0.01 <= end {
            let time = Double(pass) * 0.01
            if pass >= 100, pass % 25 == 0 {
                output.kickOnsets &+= 1
                output.newestKick = SoundOnset(time: time - 0.013, strength: 0.7)
            }
            history.record(output, at: time)
            pass += 1
        }
        return history
    }

    /// The scene in the gate tests shows its idle stand-in from 2 s until
    /// 92/30 s, an instant that is a frame at 30, 60 and 120 fps.
    private static let idleFrom = 2.0, liveFrom = 92.0 / 30

    /// While a scene shows its idle stand-in nothing new flares and its
    /// counters hold; when the music returns they carry on from there, with
    /// no jump, and the hit that brought the music back flares. Hits that
    /// started before finish their flares. The pad fades over a quarter second.
    func testAnIdleSceneShowsNoNewOnsetsAndHoldsItsCounters() {
        let history = steadyKicks(), idleFrom = Self.idleFrom, liveFrom = Self.liveFrom
        var gate = SoundTypeIdleGate()
        var last = SoundTypeFeatures(), returned = false
        for frame in 0...270 {
            let t = Double(frame) / 60, live = t < idleFrom || t >= liveFrom
            let read = history.read(at: t)
            let shown = gate.frame(read, live: live, at: 500 + t)
            XCTAssertGreaterThanOrEqual(shown.kickOnsets, last.kickOnsets, "Never decreases (\(t) s)")
            XCTAssertLessThanOrEqual(shown.kickOnsets &- last.kickOnsets, 1, "Never jumps (\(t) s)")
            if t < idleFrom {
                XCTAssertEqual(shown, read, "Live: unchanged (\(t) s)")
            } else if !live {
                XCTAssertEqual(shown.kickOnsets, 5, "Idle: the five kicks that started before, no more (\(t) s)")
                XCTAssertEqual(shown.kickTime, 2 - 0.013, accuracy: 1e-9)
                XCTAssertTrue(shown.kickRing.allSatisfy { $0.time < idleFrom }, "\(t) s")
                XCTAssertEqual(shown.pad, 0.8 * Float(max(0, 1 - (t - idleFrom) / SoundTypeIdleGate.ramp)), accuracy: 1e-4)
                var before = SoundOnsetRing(), filled = 0
                for onset in read.kickRing where onset.time < idleFrom { before[filled] = onset; filled += 1 }
                XCTAssertEqual(shown.kick, SoundTypeShapes.normal.kick.envelope(before, at: t), accuracy: 1e-6)
                if t < 2.2 { XCTAssertGreaterThan(shown.kick, 0.2, "The hit before going idle finishes its flare") }
            } else {
                XCTAssertEqual(shown.kickOnsets, read.kickOnsets &- 3, "The three kicks that started while idle never count")
                XCTAssertTrue(shown.kickRing.allSatisfy { $0.time < idleFrom || $0.time >= liveFrom - SoundTypeIdleGate.lead })
                XCTAssertEqual(shown.pad, 0.8 * Float(min(1, (t - liveFrom) / SoundTypeIdleGate.ramp)), accuracy: 1e-4)
                if !returned {
                    returned = true
                    XCTAssertEqual(shown.kickRing[0].time, 3 - 0.013, accuracy: 1e-9, "The kick that brought the music back shows")
                    XCTAssertGreaterThan(shown.kick, 0.3)
                }
            }
            last = shown
        }
        XCTAssertTrue(returned)
        // A new source restarts the counters, and the gate follows.
        XCTAssertEqual(gate.frame(SoundTypeFeatures(), live: true, at: 600).kickOnsets, 0)
    }

    /// The idle fade and the onsets it hides depend on time alone: 30 and
    /// 120 fps show the same sound types at the instants they share.
    func testTheIdleGateIsTheSameAtEveryFrameRate() {
        let history = steadyKicks()
        func run(_ fps: Double) -> [SoundTypeFeatures] {
            var gate = SoundTypeIdleGate()
            return (0...Int(4.5 * fps)).map { frame in
                let t = Double(frame) / fps
                return gate.frame(history.read(at: t), live: t < Self.idleFrom || t >= Self.liveFrom, at: 500 + t)
            }
        }
        let slow = run(30), fast = run(120)
        for (k, read) in slow.enumerated() where 4 * k < fast.count {
            let other = fast[4 * k]
            XCTAssertEqual(read.pad, other.pad, accuracy: 1e-5, "\(Double(k) / 30) s")
            XCTAssertEqual(read.kick, other.kick, accuracy: 1e-6, "\(Double(k) / 30) s")
            XCTAssertEqual(read.kickOnsets, other.kickOnsets, "\(Double(k) / 30) s")
            XCTAssertEqual(read.kickRing, other.kickRing, "\(Double(k) / 30) s")
        }
    }

    /// Every web engine reads `frame.sound` by name, with the transients
    /// unscaled: each multiplies them by its own transient factor (0 when
    /// calm, otherwise Punch), so Punch applies once and calm removes them.
    @MainActor
    func testEveryWebEngineReadsTheSoundTypesByName() throws {
        var sound = SoundTypeFeatures()
        sound.kickRing[0] = SoundOnset(time: 99.97, strength: 1)
        sound.kick = 0.9; sound.snare = 0.2; sound.hat = 0.1
        sound.bass = 0.3; sound.vocal = 0.5; sound.pad = 0.4
        sound.kickOnsets = (1 << 24) + 3; sound.kickTime = 99.97
        sound.hatOnsets = 2; sound.hatTime = 98.5
        sound.presentationTime = 100
        for (calm, punch) in [(false, 1.0), (false, 2), (true, 0), (true, 2)] {
            var request = WebVisualizerView.Request(width: 10, height: 10, preset: "", projection: .preset, sensitivity: 1,
                                                    motion: 1, reduced: calm, paused: false)
            request.calm = calm; request.punch = punch
            let frame = try XCTUnwrap(request.arguments(clock: 0, pcm: nil, sound: sound)["frame"] as? [String: Any])
            let named = try XCTUnwrap(frame["sound"] as? [String: Any])
            XCTAssertEqual(Set(named.keys), ["kick", "snare", "hat", "bass", "vocal", "pad", "kickOnsets", "snareOnsets",
                                             "hatOnsets", "kickAge", "snareAge", "hatAge"])
            for (key, value) in [("kick", 0.9), ("snare", 0.2), ("hat", 0.1), ("bass", 0.3), ("vocal", 0.5), ("pad", 0.4)] {
                XCTAssertEqual((named[key] as? NSNumber)?.doubleValue ?? -1, value, accuracy: 1e-6, "\(key), calm \(calm)")
            }
            XCTAssertEqual((named["kickOnsets"] as? NSNumber)?.doubleValue, Double((1 << 24) + 3), "Exact counts")
            XCTAssertEqual((named["snareOnsets"] as? NSNumber)?.doubleValue, 0)
            XCTAssertEqual((named["hatOnsets"] as? NSNumber)?.doubleValue, 2)
            XCTAssertEqual((named["kickAge"] as? NSNumber)?.doubleValue ?? -1, 0.03, accuracy: 1e-5)
            XCTAssertEqual((named["snareAge"] as? NSNumber)?.doubleValue, 60, "60 means none yet")
            XCTAssertEqual((named["hatAge"] as? NSNumber)?.doubleValue ?? -1, 1.5, accuracy: 1e-5)
            XCTAssertNil(frame["audio"])
        }
        let request = WebVisualizerView.Request(width: 10, height: 10, preset: "", projection: .preset, sensitivity: 1,
                                                motion: 1, reduced: false, paused: false)
        let light = try XCTUnwrap(request.arguments(clock: 0, pcm: nil, audio: AudioSnapshot(), sound: sound)["frame"]
            as? [String: Any])
        XCTAssertNil((light["audio"] as? [String: Any])?["sound"], "Light 3D's snapshot does not carry a second copy")
        XCTAssertNotNil(light["sound"])
        XCTAssertNil((request.arguments(clock: 0, pcm: nil)["frame"] as? [String: Any])?["sound"],
                     "No sound types, no frame.sound: the engines keep their own behaviour")
        // Every engine is sent them; only Light 3D also gets the spectrum snapshot.
        let source = VisualizerAudio()
        for engine in WebVisualizerEngine.allCases {
            let inputs = WebVisualizerCoordinator.audioInputs(for: engine, paused: false, from: source)
            XCTAssertNotNil(inputs.sound, "\(engine)")
            XCTAssertEqual(inputs.audio != nil, engine == .light3D, "\(engine)")
            let paused = WebVisualizerCoordinator.audioInputs(for: engine, paused: true, from: source)
            XCTAssertNil(paused.sound)
            XCTAssertNil(paused.audio)
        }
    }
}
