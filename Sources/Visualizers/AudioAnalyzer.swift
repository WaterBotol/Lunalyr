// Adapted from Synesthia by Jon Jaques, MIT licence.
// Pinned source and local changes: ThirdParty/Synesthia/SOURCE.md.
// Copyright (c) 2026 Jon Jaques. See Resources/Synesthia-LICENSE.txt.

import AVFoundation
import Accelerate
import Foundation
import QuartzCore

/// One frame of analyzed audio, safe to hand to the render thread.
///
/// This is a plain value type on purpose: the render loop takes a *copy* once
/// per frame via `AudioAnalyzer.latest()`, so it can read every field for the
/// rest of the frame with no locking and no chance of the audio thread
/// mutating it mid-read. Its arrays never share storage with the analyzer.
///
/// All values are pre-normalized to roughly 0...1 (waveform: -1...1) so
/// visualizers can use them directly as intensities without knowing anything
/// about decibels or FFT bin math.
struct AudioSnapshot: Sendable {
    static let bandCount = 64
    static let waveformCount = 256
    static let componentCount = 8

    /// The frequency spectrum, reduced to 64 bands covering 30 Hz–16 kHz
    /// (or 0.45 × the sample rate, if lower). Bands are *log-spaced*: band
    /// edges are geometric in Hz, so every octave spans about seven bands,
    /// which matches how pitch is perceived. Each band integrates the power
    /// spectrum between its edges, interpolating between FFT bins, so the
    /// narrow low bands follow the real bass frequencies instead of each
    /// taking a whole 23 Hz bin. Smoothed and normalized to roughly 0...1.
    var bands = [Float](repeating: 0, count: AudioSnapshot.bandCount)
    /// Recent time-domain samples, -1...1 — the raw wiggle of the speaker
    /// cone, downsampled to 256 points (about 21 ms). Good for drawing
    /// oscilloscope-style shapes; useless for "how loud is the bass"
    /// questions (use `bands`).
    var waveform = [Float](repeating: 0, count: AudioSnapshot.waveformCount)
    /// Overall loudness (RMS mapped through dB), 0...1. When loudness
    /// normalization is on (the default) the mapping slowly adapts to the
    /// source's program level, so "how loud the music feels" rather than
    /// "how hot the signal is".
    var level: Float = 0
    /// Average energy of the low (30–250 Hz), middle (250 Hz–2 kHz) and high
    /// (2–16 kHz) registers, the log-spaced thirds of the band axis.
    var bass: Float = 0
    var mid: Float = 0
    var treble: Float = 0
    /// Eight named sub-band energies, 0...1 each, over these ranges in Hz:
    /// sub-bass 30–60, bass 60–150, low-mid 150–400, mid 400–1200,
    /// high-mid 1.2–3k, presence 3–6k, treble 6–10k, air 10–16k.
    /// A finer-grained alternative to bass/mid/treble when a visualizer wants,
    /// say, hi-hats ("air") separately from vocals ("presence").
    var components = [Float](repeating: 0, count: AudioSnapshot.componentCount)
    /// Beat envelope: jumps to 1 on a detected bass transient (a kick drum
    /// hit), then decays exponentially. Multiply things by this for "pulse on
    /// the beat" effects — the decay gives a natural-looking falloff for free.
    var beat: Float = 0
    /// Treble transient envelope (hi-hats, snares' snap); same idea as `beat`
    /// but triggered from the top of the spectrum and decaying faster.
    var trebleBeat: Float = 0
    /// Spectral flux: how much *new* energy arrived this frame, 0...1.
    /// Spikes on any onset (a note starting, a chord change), not just bass
    /// hits, so it catches events `beat` misses.
    var flux: Float = 0
    /// Spectral centroid mapped to 0...1: the "center of mass" of the
    /// spectrum. Low = dark/bassy sound, high = bright/airy. Useful for
    /// steering color temperature.
    var centroid: Float = 0
    var wavefield = WavefieldSpectrum()
    /// What type of sound is playing (kick, snare, hat, bass, vocal, pad),
    /// evaluated at the presentation time. Filled by VisualizerAudio, which
    /// runs one detector over both channels; AudioAnalyzer leaves it empty.
    var sound = SoundTypeFeatures()
}

/// In-memory sample window for Wavefield's own analyser.
struct WavefieldPCMWindow: Sendable {
    let samples: [Float]
    let sampleRate: Double
    let level: Float
    /// Samples received up to and including the last one in `samples`.
    var end = 0
}

/// Streams mono samples in from a capture thread, analyses them, and keeps a
/// short timestamped history that readers sample under a lock.
///
/// ### The pipeline
/// 1. Capture code (any one thread at a time) calls `append` with PCM as it
///    arrives. Samples land in a ring buffer.
/// 2. Every hop (half a window) an analysis pass runs over the newest
///    window: Hann window → FFT → 64 log-spaced bands → auto-gain dB mapping
///    → smoothing → derived features (flux, centroid, registers, …). The
///    window lasts about 43 ms at every sample rate (2048 samples at 48 kHz,
///    4096 at 96 kHz), so bins stay ~23 Hz apart and levels match across
///    devices; all smoothing uses time constants in seconds.
/// 3. A fast time-domain path filters each sample into a bass and a treble
///    band and detects kicks and hi-hats on ~5 ms blocks. Its onsets carry
///    their own timestamps, so `beat` no longer waits for the next FFT hop.
/// 4. Each pass is stored with the time of its newest sample. The render loop
///    calls `latest(at:)` with the time it wants to show — usually now,
///    or a little earlier when the output device adds latency — and gets a
///    value-type copy built from the matching pass.
/// 5. While a spectrum observer is set (`observeSpectra`), every quarter
///    window it receives a calibrated power spectrum of the plain window:
///    the full pass's own bins, and one extra plain FFT halfway between
///    passes. VisualizerAudio's sound-type detector runs on these.
///
/// ### Threading model
/// Not on the main actor and shared between the capture/worker thread and the
/// render thread. One `NSLock` guards all mutable state. The writer path
/// (`append`) never allocates: every buffer is preallocated for the largest
/// supported window and reused, and readers copy values out instead of
/// sharing array storage, so nothing triggers copy-on-write on the writer.
///
/// `@unchecked Sendable` because the compiler can't verify hand-rolled lock
/// discipline; the invariant is that every access to mutable state happens
/// with `lock` held.
final class AudioAnalyzer: @unchecked Sendable {
    static let shared = AudioAnalyzer()

    /// The analysis window lasts about this long at every sample rate.
    static let windowSeconds = 0.0427
    static let maximumFFTSize = 8192
    /// The window is zero-padded to this many times its length before the
    /// FFT. That samples the same 43 ms spectrum every ~6 Hz instead of every
    /// ~23 Hz, so a bass note's band follows its real frequency rather than
    /// snapping to the nearest coarse bin (which differs between 44.1 and
    /// 48 kHz). It adds no latency: the window itself is unchanged.
    static let oversampling = 4
    static let maximumTransformSize = maximumFFTSize * oversampling
    /// Ring buffer length: enough for a delayed 8192-sample window at 192 kHz.
    static let historyCapacity = 65_536
    /// Analysis passes kept for delayed reads (~0.35 s of history).
    static let passCapacity = 16
    /// An unusually large buffer analyses only its newest hops.
    static let maximumPassesPerAppend = 32
    static let minimumFrequency = 30.0
    static let maximumFrequency = 16_000.0
    /// Edges of the eight named components, in Hz.
    static let componentEdges: [Double] = [30, 60, 150, 400, 1200, 3000, 6000, 10_000, 16_000]
    /// Edges of bass, mid and treble, in Hz.
    static let registerEdges: [Double] = [30, 250, 2000, 16_000]

    /// Time constants in seconds. At 48 kHz they reproduce the per-pass
    /// factors this analyser used to apply every 1024 samples, so the look is
    /// unchanged there and no longer speeds up at 96 kHz or slows at 44.1 kHz.
    enum TimeConstant {
        static let bandAttack = 0.0203       // was 65% of the way per pass
        static let bandRelease = 0.167       // was 12% per pass
        static let flux = 0.1415             // was × 0.86 per pass
        static let centroid = 0.2025         // was × 0.9 per pass
        static let kickAverage = 0.8425      // was × 0.975 per pass
        static let kickEnvelope = 0.2025     // was × 0.9 per pass
        static let trebleAverage = 0.5226    // was × 0.96 per pass
        static let trebleEnvelope = 0.0956   // was × 0.8 per pass
        static let agcDown = 0.1313          // was 15% per pass
        static let agcUp = 1.056             // was 2% per pass
        static let idleSlow = 0.20, idleMid = 0.16, idleFast = 0.10
    }

    private let lock = NSLock()

    // MARK: Configuration, rebuilt when the sample rate changes

    private var sampleRate: Double = 48_000
    private var fftSize = 2048
    private var log2n = vDSP_Length(11)
    private var hop = 1024
    private var hopSeconds = 1024.0 / 48_000
    /// Bins of the zero-padded transform: `oversampling × fftSize / 2`.
    private var binCount = 4096
    private let fftSetup: FFTSetup
    /// Hann window: a raised-cosine fade applied to the input so the chunk's
    /// hard edges don't smear energy across the whole spectrum ("spectral
    /// leakage"). Without it, a pure sine tone shows up in every band.
    private var window = [Float](repeating: 0, count: AudioAnalyzer.maximumFFTSize)
    /// Band b's mean power is the sum of `weightValues[i] × power[weightBins[i]]`
    /// for i in `weightStart[b]..<weightStart[b + 1]` (see `rebuildBands`).
    private var weightStart = [Int](repeating: 0, count: AudioSnapshot.bandCount + 1)
    /// Each band covers at most its width in bins plus two partial bins, and
    /// each bin segment contributes two weights.
    private static let weightCapacity = 2 * (AudioAnalyzer.maximumTransformSize / 2 + 2 * AudioSnapshot.bandCount)
    private var weightBins = [Int](repeating: 0, count: AudioAnalyzer.weightCapacity)
    private var weightValues = [Float](repeating: 0, count: AudioAnalyzer.weightCapacity)
    /// Which bands make up each named component and each register.
    private var componentRanges = [Range<Int>](repeating: 0..<1, count: AudioSnapshot.componentCount)
    private var registerRanges = [Range<Int>](repeating: 0..<1, count: 3)

    // MARK: Sample history

    private var history = [Float](repeating: 0, count: AudioAnalyzer.historyCapacity)
    /// Samples received since the last reset; the ring index is `written % capacity`.
    private var written = 0
    /// Samples received since the last analysis pass.
    private var pending = 0
    /// When the newest sample was captured, on the `CACurrentMediaTime` clock.
    private var newestSampleTime = -Double.infinity
    /// The history holds the input minus its DC offset (a one-pole high-pass
    /// at `dcCutoff`). An inaudible offset of 0.002 used to read as a bass
    /// glow at half height and kept a silent source "audible" for ever.
    static let dcCutoff = 5.0
    private var dcPole: Float = 1
    private var dcInput: Float = 0, dcOutput: Float = 0

    // MARK: Analysis scratch

    /// The windowed samples followed by zero padding (see `oversampling`).
    private var windowed = [Float](repeating: 0, count: AudioAnalyzer.maximumTransformSize)
    private var real = [Float](repeating: 0, count: AudioAnalyzer.maximumTransformSize / 2)
    private var imag = [Float](repeating: 0, count: AudioAnalyzer.maximumTransformSize / 2)
    private var power = [Float](repeating: 0, count: AudioAnalyzer.maximumTransformSize / 2)
    /// This pass's unsmoothed band values, swapped with `previousRaw` at the
    /// end of each pass (instead of copied) so neither ever reallocates.
    private var raw = [Float](repeating: 0, count: AudioSnapshot.bandCount)
    private var previousRaw = [Float](repeating: 0, count: AudioSnapshot.bandCount)
    /// Per-band values after asymmetric attack/release smoothing.
    private var smoothed = [Float](repeating: 0, count: AudioSnapshot.bandCount)
    private var fluxEnvelope: Float = 0
    private var centroidSmoothed: Float = 0.4

    // MARK: Stored passes (flat, preallocated)

    private var passTime = [Double](repeating: -Double.infinity, count: AudioAnalyzer.passCapacity)
    private var passBands = [Float](repeating: 0, count: AudioAnalyzer.passCapacity * AudioSnapshot.bandCount)
    private var passComponents = [Float](repeating: 0, count: AudioAnalyzer.passCapacity * AudioSnapshot.componentCount)
    private var passWaveform = [Float](repeating: 0, count: AudioAnalyzer.passCapacity * AudioSnapshot.waveformCount)
    private var passPower = [Float](repeating: 0, count: AudioAnalyzer.passCapacity * AudioAnalyzer.maximumFFTSize / 2)
    /// level, bass, mid, treble, flux, centroid, rms per pass.
    private var passScalars = [Float](repeating: 0, count: AudioAnalyzer.passCapacity * 7)
    private var passBinCount = [Int](repeating: 0, count: AudioAnalyzer.passCapacity)
    private var passBinHz = [Float](repeating: 0, count: AudioAnalyzer.passCapacity)
    private var passFFTSize = [Int](repeating: 0, count: AudioAnalyzer.passCapacity)
    private var passCount = 0

    // MARK: Fast transient path

    private var lowFilter = Biquad(), highFilter = Biquad()
    private var blockLength = 256
    private var blockFill = 0
    private var blockLow: Float = 0, blockHigh: Float = 0, previousBlockLow: Float = 0
    private var kickAverage: Float = 0, trebleAverage: Float = 0
    /// One-shot latches with hysteresis: set on the block that fires an
    /// onset, cleared only once the energy falls back below the release
    /// threshold. While set, the detector cannot re-fire — without this a
    /// *held* note hovering at the trigger threshold re-fires every few
    /// blocks and the envelope sawtooths, which the visualizers faithfully
    /// render as constant shaking.
    private var kickLatched = false, trebleLatched = false
    private var kickOnsets = [Double](repeating: -Double.infinity, count: 4)
    private var trebleOnsets = [Double](repeating: -Double.infinity, count: 4)
    private var kickOnsetCount = 0, trebleOnsetCount = 0

    // MARK: Spectra for the sound-type detector

    /// One spectrum handed to a spectrum observer.
    struct SpectrumPass {
        /// Calibrated one-sided power of the plain (not zero-padded) window:
        /// bin k sits at k × `binHz`, and a sinusoid of amplitude A sums to
        /// A²/2 over its main lobe. Bin 0 (DC and the packed Nyquist term)
        /// is zero. Valid only during the call.
        let power: UnsafeBufferPointer<Float>
        let binHz: Float
        /// Capture time of the window's newest sample (`CACurrentMediaTime`).
        let time: Double
        /// Audio seconds since the previous spectrum.
        let dt: Double
        /// Stream position of the window's newest sample: samples received
        /// since this analyser was created, which a reset or a new sample
        /// rate does not rewind (`align(with:)` takes another's). The two
        /// channels' spectra of the same capture buffers share it.
        let position: Int
    }

    private var spectrumObserver: ((SpectrumPass) -> Void)?
    private var spectrum = [Float](repeating: 0, count: AudioAnalyzer.maximumFFTSize / 2)
    /// |X|² of vDSP's packed real FFT (2× the DFT) to calibrated power:
    /// 1 / (2 · N · Σw²).
    private var spectrumScale: Float = 1
    /// `written` at the previous spectrum.
    private var spectrumPosition = 0
    /// Samples received in all (see `SpectrumPass.position`).
    private var streamPosition = 0

    // MARK: Loudness normalization (a slow AGC)

    // The fixed dB mappings assume mastered-music levels; when enabled, these
    // adapt them to the actual program level so a quiet mic and a loud master
    // both land in the useful visual range without retuning Sensitivity.
    private var agcEnabled = true
    /// Slow-moving estimate of the program's peak short-term level, in dB.
    private var agcPeakDB: Float = AudioAnalyzer.agcReferenceDB
    /// Set on the tracker's first audible pass; until then the peak *snaps*
    /// to the observed level instead of decaying toward it, so a freshly
    /// reset analyzer adapts to a new source in about a second, not ten.
    private var agcPrimed = false
    /// The gain currently added to both dB mappings, in dB. Smoothed toward
    /// its target every pass so the visuals drift rather than pump.
    private var agcGainDB: Float = 0

    /// Where the tracker steers program peaks: with the tracked peak at
    /// −8 dB the applied gain is zero and the mappings match the fixed
    /// historical ones, so loud mastered music looks the same as ever.
    private static let agcReferenceDB: Float = -8
    /// The tracker only updates above this; silence must *hold* the gain,
    /// not wind it up to maximum and then blast the next loud thing.
    private static let agcGateDB: Float = -55
    /// Gain clamp. The boost cap keeps a whisper-quiet source from having
    /// its noise floor amplified into the visible range.
    private static let agcMaxBoostDB: Float = 24
    private static let agcMaxCutDB: Float = 12
    /// Peak decay: quiet passages within a song brighten over seconds, so
    /// musical dynamics survive.
    private static let agcPeakDecayDBPerSecond: Float = 2.484

    init() {
        fftSetup = vDSP_create_fftsetup(vDSP_Length(AudioAnalyzer.maximumTransformSize.trailingZeroBitCount),
                                        FFTRadix(kFFTRadix2))!
        configure(sampleRate: 48_000)
    }

    deinit { vDSP_destroy_fftsetup(fftSetup) }

    static func now() -> Double { CACurrentMediaTime() }

    /// The FFT length whose window is closest to `windowSeconds` (log scale).
    static func fftSize(for sampleRate: Double) -> Int {
        let exponent = (log2(sampleRate * windowSeconds)).rounded()
        return 1 << Int(min(13, max(9, exponent)))
    }

    /// Centre frequency of band `index`, in Hz, for a sample rate.
    static func bandCentre(_ index: Int, sampleRate: Double) -> Double {
        let top = min(maximumFrequency, sampleRate * 0.45)
        return minimumFrequency * pow(top / minimumFrequency, (Double(index) + 0.5) / Double(AudioSnapshot.bandCount))
    }

    // MARK: - Reading

    /// The newest analysis. Called once per rendered frame.
    func latest() -> AudioSnapshot { latest(at: Self.now()) }

    /// The analysis as of `time` (`CACurrentMediaTime` clock): the newest pass
    /// whose samples had all arrived by then, with transient envelopes and the
    /// idle fade evaluated at exactly that time.
    func latest(at time: Double) -> AudioSnapshot {
        var snapshot = AudioSnapshot()
        var spectrum: (power: [Float], binHz: Float, fftSize: Int, rms: Float)?
        lock.lock()
        if let slot = passSlot(at: time) {
            snapshot.bands = copy(passBands, slot * AudioSnapshot.bandCount, AudioSnapshot.bandCount)
            snapshot.components = copy(passComponents, slot * AudioSnapshot.componentCount, AudioSnapshot.componentCount)
            snapshot.waveform = copy(passWaveform, slot * AudioSnapshot.waveformCount, AudioSnapshot.waveformCount)
            let scalars = slot * 7
            snapshot.level = passScalars[scalars]
            snapshot.bass = passScalars[scalars + 1]
            snapshot.mid = passScalars[scalars + 2]
            snapshot.treble = passScalars[scalars + 3]
            snapshot.flux = passScalars[scalars + 4]
            snapshot.centroid = passScalars[scalars + 5]
            spectrum = (copy(passPower, slot * Self.maximumFFTSize / 2, passBinCount[slot]),
                        passBinHz[slot], passFFTSize[slot], passScalars[scalars + 6])
        }
        snapshot.beat = Self.envelope(kickOnsets, at: time, decay: TimeConstant.kickEnvelope)
        snapshot.trebleBeat = Self.envelope(trebleOnsets, at: time, decay: TimeConstant.trebleEnvelope)
        // Fade toward silence if the source stopped feeding us: without this
        // the last frame of audio would freeze on screen when capture stops.
        let idle = time - newestSampleTime - 0.25
        lock.unlock()

        if idle > 0 {
            let slow = Float(exp(-idle / TimeConstant.idleSlow))
            let mid = Float(exp(-idle / TimeConstant.idleMid))
            let fast = Float(exp(-idle / TimeConstant.idleFast))
            for i in snapshot.bands.indices { snapshot.bands[i] *= slow }
            for i in snapshot.components.indices { snapshot.components[i] *= slow }
            for i in snapshot.waveform.indices { snapshot.waveform[i] *= mid }
            snapshot.level *= slow; snapshot.bass *= slow; snapshot.mid *= slow; snapshot.treble *= slow
            snapshot.flux *= fast
        } else if let spectrum {
            snapshot.wavefield = WavefieldSpectrum(power: spectrum.power, binHz: spectrum.binHz,
                fftSize: spectrum.fftSize, rms: spectrum.rms)
        }
        snapshot.flux = min(snapshot.flux, 1)
        return snapshot
    }

    func wavefieldPCM() -> WavefieldPCMWindow? { wavefieldPCM(at: Self.now()) }

    /// The newest 2048 samples Wavefield's own analyser expects, as of `time`.
    func wavefieldPCM(at time: Double) -> WavefieldPCMWindow? {
        pcm(at: time, count: 2048)
    }

    /// `count` samples (at most `historyCapacity / 2`) ending with the newest
    /// sample captured by `time`, or nil if capture has stopped or not begun.
    func pcm(at time: Double, count: Int) -> WavefieldPCMWindow? {
        lock.lock()
        defer { lock.unlock() }
        guard passCount > 0, time - newestSampleTime <= 0.25 else { return nil }
        let count = min(Self.historyCapacity / 2, max(1, count))
        let late = max(0, newestSampleTime - time)
        let end = max(0, written - Int((late * sampleRate).rounded()))
        var samples = [Float](repeating: 0, count: count)
        // Never read samples the ring has already overwritten.
        let available = max(0, min(count, end, Self.historyCapacity - (written - end)))
        samples.withUnsafeMutableBufferPointer { target in
            readHistory(endingAt: end, count: available, into: target.baseAddress! + (count - available))
        }
        let slot = passSlot(at: time)
        let rms = slot.map { passScalars[$0 * 7 + 6] } ?? 0
        return WavefieldPCMWindow(samples: samples, sampleRate: sampleRate, level: min(1, max(0, rms * 2.4)), end: end)
    }

    /// Samples received so far, for readers that want everything since their
    /// last frame.
    var sampleCount: Int {
        lock.lock(); defer { lock.unlock() }
        return written
    }

    /// Turns loudness normalization on/off (the "Normalize Loudness"
    /// setting). Callable from any thread; takes effect on the next analysis
    /// pass, and the applied gain ramps back rather than jumping.
    func setAutoGainEnabled(_ enabled: Bool) {
        lock.lock()
        defer { lock.unlock() }
        agcEnabled = enabled
    }

    /// Clears all analysis state; called when the user switches audio sources
    /// so the old source's tail doesn't bleed into the new one.
    func reset() {
        lock.lock()
        defer { lock.unlock() }
        resetLocked()
    }

    /// Hands `observer` a spectrum every quarter window (~10.7 ms): each
    /// full pass's own bins, and one extra plain FFT halfway between passes.
    /// It is called on the appending thread with this analyser's lock held,
    /// so it must not block, allocate or call back into this analyser. nil
    /// stops it, and the extra FFTs with it.
    func observeSpectra(_ observer: ((SpectrumPass) -> Void)?) {
        lock.lock()
        defer { lock.unlock() }
        spectrumObserver = observer
    }

    /// Restarts this analyser, with an empty history, at `other`'s sample
    /// rate, stream position and hop phase. Fed the same buffers from then
    /// on, the two analyse every instant at the same position: VisualizerAudio
    /// aligns the right channel's analyser when the input turns stereo, as it
    /// sat idle while the input was mono. Call on the appending thread.
    func align(with other: AudioAnalyzer) {
        guard other !== self else { return }
        other.lock.lock()
        let rate = other.sampleRate, position = other.streamPosition, phase = other.pending
        let written = other.written, sinceSpectrum = other.written - other.spectrumPosition
        other.lock.unlock()
        lock.lock()
        defer { lock.unlock() }
        if rate != sampleRate { configure(sampleRate: rate) } else { resetLocked() }
        streamPosition = position
        pending = phase
        // The history stays silent; counting on from `written` keeps the
        // window and the spacing of spectra (`dt`) identical to the other's.
        self.written = written
        spectrumPosition = written - sinceSpectrum
    }

    // MARK: - Writing

    /// Appends mono samples captured just now.
    func append(_ samples: UnsafePointer<Float>, count: Int, sampleRate: Double) {
        append(samples, count: count, sampleRate: sampleRate, hostTime: Self.now())
    }

    /// Appends mono samples whose last sample was captured at `hostTime`
    /// (`CACurrentMediaTime` clock, which is the IO timestamp's host time in
    /// seconds) and runs every analysis pass and onset block they complete.
    func append(_ samples: UnsafePointer<Float>, count: Int, sampleRate: Double, hostTime: Double) {
        guard count > 0, count <= 192_000, sampleRate.isFinite, (8000...192_000).contains(sampleRate),
              hostTime.isFinite else { return }
        lock.lock()
        defer { lock.unlock() }
        if sampleRate != self.sampleRate { configure(sampleRate: sampleRate) }
        settleAfterGap(until: hostTime - Double(count) / sampleRate)
        let firstIndex = written
        let firstAnalysed = count - Self.maximumPassesPerAppend * hop
        // Process in hop-sized chunks so every completed hop is analysed at
        // its own position and time, without a scratch copy of the input.
        // A spectrum observer also gets the halfway point of each hop.
        let half = hop / 2
        var offset = 0
        while offset < count {
            let boundary = spectrumObserver != nil && pending < half ? half : hop
            let chunk = min(count - offset, boundary - pending)
            writeHistory(samples + offset, count: chunk)
            detectTransients(count: chunk, firstIndex: firstIndex + offset,
                             lastIndex: firstIndex + count - 1, hostTime: hostTime)
            offset += chunk
            pending += chunk
            streamPosition += chunk
            let time = hostTime - Double(count - offset) / sampleRate
            if pending >= hop {
                pending = 0
                if offset >= firstAnalysed {
                    processLocked(at: time)
                    observeSpectrum(at: time, ofFullPass: true)
                }
            } else if boundary == half, pending == half, offset >= firstAnalysed {
                observeSpectrum(at: time, ofFullPass: false)
            }
        }
        newestSampleTime = hostTime
    }

    // MARK: - Configuration (call with lock held)

    private func configure(sampleRate: Double) {
        self.sampleRate = sampleRate
        fftSize = Self.fftSize(for: sampleRate)
        let transformSize = fftSize * Self.oversampling
        log2n = vDSP_Length(transformSize.trailingZeroBitCount)
        hop = fftSize / 2
        hopSeconds = Double(hop) / sampleRate
        binCount = transformSize / 2
        dcPole = Float(exp(-2 * Double.pi * Self.dcCutoff / sampleRate))
        window.withUnsafeMutableBufferPointer {
            vDSP_hann_window($0.baseAddress!, vDSP_Length(fftSize), Int32(vDSP_HANN_NORM))
        }
        var sumSquares: Float = 0
        window.withUnsafeBufferPointer { vDSP_svesq($0.baseAddress!, 1, &sumSquares, vDSP_Length(fftSize)) }
        spectrumScale = 1 / (2 * Float(fftSize) * sumSquares)
        // The padding stays zero: each pass only rewrites the window itself.
        for i in windowed.indices { windowed[i] = 0 }
        rebuildBands()
        lowFilter = Biquad(lowPass: 150, q: 0.7071, sampleRate: sampleRate)
        highFilter = Biquad(highPass: min(6000, sampleRate * 0.4), q: 0.7071, sampleRate: sampleRate)
        blockLength = max(32, Int((sampleRate * 0.00533).rounded()))
        resetLocked()
    }

    private func resetLocked() {
        for i in history.indices { history[i] = 0 }
        written = 0
        pending = 0
        spectrumPosition = 0
        dcInput = 0; dcOutput = 0
        newestSampleTime = -Double.infinity
        for i in smoothed.indices { smoothed[i] = 0; raw[i] = 0; previousRaw[i] = 0 }
        fluxEnvelope = 0
        centroidSmoothed = 0.4
        for i in passTime.indices { passTime[i] = -Double.infinity }
        passCount = 0
        lowFilter.clear(); highFilter.clear()
        blockFill = 0; blockLow = 0; blockHigh = 0; previousBlockLow = 0
        kickAverage = 0; trebleAverage = 0
        kickLatched = false; trebleLatched = false
        for i in kickOnsets.indices { kickOnsets[i] = -Double.infinity; trebleOnsets[i] = -Double.infinity }
        agcPeakDB = Self.agcReferenceDB
        agcPrimed = false
        agcGainDB = 0
    }

    /// Precomputes how the FFT's linear bins integrate into each band.
    ///
    /// Band edges are geometric between 30 Hz and 16 kHz, so each band spans
    /// the same *ratio* of frequencies. Each band's value is the mean of the
    /// power spectrum over its edges, with the spectrum treated as linear
    /// between bins: a band narrower than a bin takes the interpolated value
    /// at its own frequency rather than a whole bin, and a wide band averages
    /// every bin it covers, weighting the two partial bins at its ends.
    /// `fMax` is capped below the Nyquist limit (half the sample rate).
    private func rebuildBands() {
        let binHz = sampleRate / Double(fftSize * Self.oversampling)
        let fMin = Self.minimumFrequency, fMax = min(Self.maximumFrequency, sampleRate * 0.45)
        var next = 0
        for band in 0..<AudioSnapshot.bandCount {
            weightStart[band] = next
            let lowHz = fMin * pow(fMax / fMin, Double(band) / Double(AudioSnapshot.bandCount))
            let highHz = fMin * pow(fMax / fMin, Double(band + 1) / Double(AudioSnapshot.bandCount))
            // In bin units; bin 0 holds DC and the packed Nyquist term, and
            // the main lobe of DC spans the first few padded bins.
            let low = max(Double(Self.oversampling), lowHz / binHz)
            let high = min(Double(binCount - 1), max(low + 0.0001, highHz / binHz))
            let width = high - low
            var bin = Int(low.rounded(.down))
            while Double(bin) < high, bin < binCount - 1 {
                let t0 = max(low, Double(bin)) - Double(bin), t1 = min(high, Double(bin + 1)) - Double(bin)
                // ∫ P(k)(1 - t) + P(k+1) t over [t0, t1], divided by the width.
                let square = (t1 * t1 - t0 * t0) / 2
                addWeight(bin, Float(((t1 - t0) - square) / width), at: &next)
                addWeight(bin + 1, Float(square / width), at: &next)
                bin += 1
            }
        }
        weightStart[AudioSnapshot.bandCount] = next

        func bandRange(_ lowHz: Double, _ highHz: Double) -> Range<Int> {
            let centres = 0..<AudioSnapshot.bandCount
            let lower = centres.first { Self.bandCentre($0, sampleRate: sampleRate) >= lowHz } ?? AudioSnapshot.bandCount - 1
            let upper = centres.last { Self.bandCentre($0, sampleRate: sampleRate) < highHz } ?? lower
            return lower..<max(lower + 1, upper + 1)
        }
        for c in 0..<AudioSnapshot.componentCount {
            componentRanges[c] = bandRange(Self.componentEdges[c], Self.componentEdges[c + 1])
        }
        for r in 0..<3 {
            registerRanges[r] = bandRange(Self.registerEdges[r], Self.registerEdges[r + 1])
        }
    }

    private func addWeight(_ bin: Int, _ weight: Float, at next: inout Int) {
        guard weight > 0, next < weightBins.count else { return }
        weightBins[next] = bin
        weightValues[next] = weight
        next += 1
    }

    // MARK: - Writing helpers (call with lock held)

    /// Stores `samples` with the DC offset removed and non-finite samples
    /// replaced by silence, so neither reaches any analysis or reader.
    private func writeHistory(_ samples: UnsafePointer<Float>, count: Int) {
        let capacity = Self.historyCapacity, pole = dcPole
        var previousInput = dcInput, previousOutput = dcOutput
        history.withUnsafeMutableBufferPointer { ring in
            var index = written % capacity
            for i in 0..<count {
                let x = samples[i].isFinite ? samples[i] : 0
                let y = x - previousInput + pole * previousOutput
                previousInput = x
                previousOutput = y
                ring[index] = y
                index += 1
                if index == capacity { index = 0 }
            }
        }
        dcInput = previousInput
        // Silence decays the filter towards subnormals; stop there.
        dcOutput = abs(previousOutput) < 1e-20 ? 0 : previousOutput
        written += count
    }

    /// Copies `count` samples ending at sample index `end` (exclusive).
    private func readHistory(endingAt end: Int, count: Int, into target: UnsafeMutablePointer<Float>) {
        guard count > 0 else { return }
        let capacity = Self.historyCapacity
        history.withUnsafeBufferPointer { ring in
            var copied = 0
            while copied < count {
                let start = (end - count + copied) % capacity
                let run = min(count - copied, capacity - start)
                (target + copied).update(from: ring.baseAddress! + start, count: run)
                copied += run
            }
        }
    }

    /// After a capture gap, decays the smoothed state the way the visuals
    /// faded while nothing arrived, so the next note attacks from there.
    private func settleAfterGap(until firstSampleTime: Double) {
        let idle = firstSampleTime - newestSampleTime - 0.25
        guard idle > 0, idle.isFinite else { return }
        let slow = Float(exp(-idle / TimeConstant.idleSlow))
        for i in smoothed.indices { smoothed[i] *= slow }
        fluxEnvelope *= Float(exp(-idle / TimeConstant.idleFast))
    }

    // MARK: - Fast onset path (call with lock held)

    /// Filters samples into a bass band (150 Hz low-pass, kick drums) and a
    /// treble band (6 kHz high-pass, hi-hats) and evaluates both on blocks of
    /// about 5 ms. Each onset is stamped with the capture time of the block
    /// that fired it, so a kick reaches the screen tens of milliseconds
    /// sooner than an FFT hop allows and can be aligned to the output.
    /// It reads the `count` samples just written to the history at `firstIndex`.
    private func detectTransients(count: Int, firstIndex: Int, lastIndex: Int, hostTime: Double) {
        let blockSeconds = Double(blockLength) / sampleRate
        let kickFollow = Float(1 - exp(-blockSeconds / TimeConstant.kickAverage))
        let trebleFollow = Float(1 - exp(-blockSeconds / TimeConstant.trebleAverage))
        let capacity = Self.historyCapacity
        for i in 0..<count {
            let x = history[(firstIndex + i) % capacity]
            let low = lowFilter.process(x), high = highFilter.process(x)
            blockLow += low * low
            blockHigh += high * high
            blockFill += 1
            guard blockFill == blockLength else { continue }
            let length = Float(blockLength)
            // Two blocks for the bass: a kick's fundamental needs ~10 ms.
            let lowEnergy = (blockLow + previousBlockLow) / (2 * length)
            let lowLevel = normalized(energy: lowEnergy)
            let highLevel = normalized(energy: blockHigh / length)
            previousBlockLow = blockLow
            blockLow = 0; blockHigh = 0; blockFill = 0
            let time = hostTime - Double(lastIndex - (firstIndex + i)) / sampleRate

            // Kick: 30% above its own slow average (and above an absolute
            // floor, so silence can't trigger) fires once per onset; the
            // latch re-arms only below 1.1× the average.
            kickAverage += (lowLevel - kickAverage) * kickFollow
            if !kickLatched, lowLevel > kickAverage * 1.3, lowLevel > 0.15 {
                kickLatched = true
                kickOnsets[kickOnsetCount % kickOnsets.count] = time
                kickOnsetCount += 1
            } else if lowLevel < kickAverage * 1.1 {
                kickLatched = false
            }
            // Hi-hats: the same scheme over the top of the spectrum.
            trebleAverage += (highLevel - trebleAverage) * trebleFollow
            if !trebleLatched, highLevel > trebleAverage * 1.35, highLevel > 0.10 {
                trebleLatched = true
                trebleOnsets[trebleOnsetCount % trebleOnsets.count] = time
                trebleOnsetCount += 1
            } else if highLevel < trebleAverage * 1.1 {
                trebleLatched = false
            }
        }
    }

    /// The band dB mapping applied to a mean-square energy.
    private func normalized(energy: Float) -> Float {
        let db = 10 * log10(max(energy, 1e-18))
        return max(0, min(1, (db + agcGainDB + 72) / 66))
    }

    /// 1 at the newest onset no later than `time`, decaying exponentially.
    private static func envelope(_ onsets: [Double], at time: Double, decay: Double) -> Float {
        var newest = -Double.infinity
        for onset in onsets where onset <= time && onset > newest { newest = onset }
        guard newest.isFinite else { return 0 }
        return Float(exp(-(time - newest) / decay))
    }

    // MARK: - Analysis (call with lock held)

    /// One full analysis pass over the window ending at the newest sample,
    /// stored as captured at `time`.
    private func processLocked(at time: Double) {
        let available = min(written, fftSize)
        // 1. Window: the newest `fftSize` samples (zero-padded at the start
        //    until that many have arrived), times the Hann curve.
        windowed.withUnsafeMutableBufferPointer { target in
            let base = target.baseAddress!
            if available < fftSize { base.update(repeating: 0, count: fftSize - available) }
            readHistory(endingAt: written, count: available, into: base + (fftSize - available))
            window.withUnsafeBufferPointer { vDSP_vmul(base, 1, $0.baseAddress!, 1, base, 1, vDSP_Length(fftSize)) }
        }

        // 2. FFT. vDSP's real-input FFT wants the real samples packed as
        //    complex pairs (even-indexed samples → real parts, odd-indexed →
        //    imaginary): that's what vDSP_ctoz does. zrip then transforms in
        //    place, and zvmags yields squared magnitudes per bin — "how much
        //    of this frequency is present", phase discarded.
        real.withUnsafeMutableBufferPointer { rp in
            imag.withUnsafeMutableBufferPointer { ip in
                var split = DSPSplitComplex(realp: rp.baseAddress!, imagp: ip.baseAddress!)
                windowed.withUnsafeBytes { bytes in
                    vDSP_ctoz(bytes.bindMemory(to: DSPComplex.self).baseAddress!, 2,
                        &split, 1, vDSP_Length(binCount))
                }
                vDSP_fft_zrip(fftSetup, &split, 1, log2n, FFTDirection(FFT_FORWARD))
                power.withUnsafeMutableBufferPointer {
                    vDSP_zvmags(&split, 1, $0.baseAddress!, 1, vDSP_Length(binCount))
                }
            }
        }

        // 3. Overall loudness: RMS (root-mean-square, the standard "average
        //    level" measure) in dB, and the auto-gain update it feeds —
        //    computed before the bands because they apply this pass's gain.
        var rms: Float = 0
        rmsOfNewest(fftSize, into: &rms)
        let levelDB = 20 * log10(max(rms, 1e-9))
        updateAutoGain(levelDB: levelDB)
        let level = max(0, min(1, (levelDB + agcGainDB + 60) / 60))

        // 4. Reduce the bins to 64 bands, then map through decibels.
        //    Loudness perception is logarithmic, so raw magnitudes would make
        //    everything but the loudest peak invisible; converting to dB and
        //    normalizing -72 dB → 0, -6 dB → 1 spreads the useful range out.
        //    The auto-gain shifts that mapping to follow the source's level.
        let norm = 1.0 / Float(fftSize)
        for band in 0..<AudioSnapshot.bandCount {
            var mean: Float = 0
            for i in weightStart[band]..<weightStart[band + 1] { mean += weightValues[i] * power[weightBins[i]] }
            let db = 20 * log10(max(sqrt(max(mean, 0)) * norm, 1e-9))
            raw[band] = max(0, min(1, (db + agcGainDB + 72) / 66))
        }
        // 5. Asymmetric smoothing (an "attack/release" envelope, borrowed
        //    from audio compressors): rise fast when a band jumps but fall
        //    slowly. Fast attack keeps hits punchy; slow release stops the
        //    visuals flickering.
        let attack = Float(1 - exp(-hopSeconds / TimeConstant.bandAttack))
        let release = Float(1 - exp(-hopSeconds / TimeConstant.bandRelease))
        for band in 0..<AudioSnapshot.bandCount {
            let v = raw[band]
            smoothed[band] += (v - smoothed[band]) * (v > smoothed[band] ? attack : release)
        }

        // Spectral flux: sum of per-band energy *increases* since the last
        // pass (decreases don't count). Any onset — new note, chord change,
        // drum hit — shows up as a spike; steady tones contribute nothing.
        var fluxNow: Float = 0
        for band in 0..<AudioSnapshot.bandCount { fluxNow += max(0, raw[band] - previousRaw[band]) }
        fluxEnvelope = max(min(fluxNow * 0.9, 1), fluxEnvelope * Float(exp(-hopSeconds / TimeConstant.flux)))

        // Spectral centroid: energy-weighted mean band index, i.e. where the
        // energy lives on the log axis — 0 (dark) ... 1 (bright). Only
        // updated when there's enough total energy for the ratio to mean
        // anything.
        var weighted: Float = 0, total: Float = 0
        for band in 0..<AudioSnapshot.bandCount {
            weighted += raw[band] * Float(band)
            total += raw[band]
        }
        if total > 0.02 {
            let instant = weighted / (total * Float(AudioSnapshot.bandCount - 1))
            centroidSmoothed += (instant - centroidSmoothed) * Float(1 - exp(-hopSeconds / TimeConstant.centroid))
        }

        // Keep this pass's raw bands for the next flux computation. A swap,
        // not an assignment: assignment would share storage and force a
        // copy-on-write allocation when `raw` is refilled next pass.
        swap(&raw, &previousRaw)

        store(at: time, level: level, rms: rms)
    }

    /// Hands the spectrum observer the calibrated power of the newest
    /// window. After a full pass that is the pass's own transform: every
    /// `oversampling`-th bin of the padded FFT is exactly a bin of the plain
    /// one. Halfway between passes it is one plain FFT of the window.
    private func observeSpectrum(at time: Double, ofFullPass fullPass: Bool) {
        guard let observer = spectrumObserver else { return }
        let bins = fftSize / 2
        var scale = spectrumScale
        if !fullPass {
            let available = min(written, fftSize)
            windowed.withUnsafeMutableBufferPointer { target in
                let base = target.baseAddress!
                if available < fftSize { base.update(repeating: 0, count: fftSize - available) }
                readHistory(endingAt: written, count: available, into: base + (fftSize - available))
                window.withUnsafeBufferPointer { vDSP_vmul(base, 1, $0.baseAddress!, 1, base, 1, vDSP_Length(fftSize)) }
            }
        }
        spectrum.withUnsafeMutableBufferPointer { target in
            let out = target.baseAddress!
            if fullPass {
                power.withUnsafeBufferPointer {
                    vDSP_vsmul($0.baseAddress!, vDSP_Stride(Self.oversampling), &scale, out, 1, vDSP_Length(bins))
                }
            } else {
                // The padding beyond the window is untouched: the next full
                // pass still sees zeros there.
                real.withUnsafeMutableBufferPointer { rp in
                    imag.withUnsafeMutableBufferPointer { ip in
                        var split = DSPSplitComplex(realp: rp.baseAddress!, imagp: ip.baseAddress!)
                        windowed.withUnsafeBytes { bytes in
                            vDSP_ctoz(bytes.bindMemory(to: DSPComplex.self).baseAddress!, 2, &split, 1, vDSP_Length(bins))
                        }
                        vDSP_fft_zrip(fftSetup, &split, 1, vDSP_Length(fftSize.trailingZeroBitCount), FFTDirection(FFT_FORWARD))
                        vDSP_zvmags(&split, 1, out, 1, vDSP_Length(bins))
                    }
                }
                vDSP_vsmul(out, 1, &scale, out, 1, vDSP_Length(bins))
            }
            out[0] = 0
        }
        let dt = Double(written - spectrumPosition) / sampleRate
        spectrumPosition = written
        let binHz = Float(sampleRate) / Float(fftSize)
        spectrum.withUnsafeBufferPointer {
            observer(SpectrumPass(power: UnsafeBufferPointer(start: $0.baseAddress, count: bins), binHz: binHz,
                                  time: time, dt: dt, position: streamPosition))
        }
    }

    private func rmsOfNewest(_ count: Int, into rms: inout Float) {
        // The window's raw samples (before the Hann curve), read in place.
        let capacity = Self.historyCapacity
        let count = min(count, written)
        guard count > 0 else { rms = 0; return }
        var sum: Float = 0
        history.withUnsafeBufferPointer { ring in
            var done = 0
            while done < count {
                let start = (written - count + done) % capacity
                let run = min(count - done, capacity - start)
                var part: Float = 0
                vDSP_svesq(ring.baseAddress! + start, 1, &part, vDSP_Length(run))
                sum += part
                done += run
            }
        }
        // Zero padding counts toward the mean, as the zero-filled window did.
        rms = sqrt(sum / Float(fftSize))
    }

    /// Stores this pass in the next history slot without allocating.
    private func store(at time: Double, level: Float, rms: Float) {
        let slot = passCount % Self.passCapacity
        passCount += 1
        passTime[slot] = time
        func average(_ range: Range<Int>) -> Float {
            var sum: Float = 0
            for i in range { sum += smoothed[i] }
            return sum / Float(max(1, range.count))
        }
        let bands = slot * AudioSnapshot.bandCount
        for band in 0..<AudioSnapshot.bandCount { passBands[bands + band] = smoothed[band] }
        let components = slot * AudioSnapshot.componentCount
        for c in 0..<AudioSnapshot.componentCount { passComponents[components + c] = average(componentRanges[c]) }
        let scalars = slot * 7
        passScalars[scalars] = level
        passScalars[scalars + 1] = average(registerRanges[0])
        passScalars[scalars + 2] = average(registerRanges[1])
        passScalars[scalars + 3] = average(registerRanges[2])
        passScalars[scalars + 4] = min(fluxEnvelope, 1)
        passScalars[scalars + 5] = centroidSmoothed
        passScalars[scalars + 6] = rms
        // Downsample the newest hop (~21 ms) to 256 points for the waveform.
        let stride = max(1, hop / AudioSnapshot.waveformCount)
        let span = stride * AudioSnapshot.waveformCount
        let waveform = slot * AudioSnapshot.waveformCount
        let capacity = Self.historyCapacity
        for i in 0..<AudioSnapshot.waveformCount {
            let index = written - span + i * stride
            passWaveform[waveform + i] = index >= 0 ? history[index % capacity] : 0
        }
        // Wavefield's peak analysis keeps its tuned resolution: every
        // `oversampling`-th padded bin is exactly a bin of the plain FFT.
        let spectrum = slot * Self.maximumFFTSize / 2
        let plainBins = fftSize / 2
        for bin in 0..<plainBins { passPower[spectrum + bin] = power[bin * Self.oversampling] }
        passBinCount[slot] = plainBins
        passBinHz[slot] = Float(sampleRate) / Float(fftSize)
        passFFTSize[slot] = fftSize
    }

    /// The newest stored pass captured no later than `time`; the oldest one
    /// when every stored pass is newer.
    private func passSlot(at time: Double) -> Int? {
        guard passCount > 0 else { return nil }
        var best: Int?, oldest: Int?
        for slot in 0..<min(passCount, Self.passCapacity) {
            let t = passTime[slot]
            if t <= time, best.map({ t > passTime[$0] }) ?? true { best = slot }
            if oldest.map({ t < passTime[$0] }) ?? true { oldest = slot }
        }
        return best ?? oldest
    }

    private func copy(_ source: [Float], _ start: Int, _ count: Int) -> [Float] {
        source.withUnsafeBufferPointer { Array(UnsafeBufferPointer(start: $0.baseAddress! + start, count: count)) }
    }

    /// Advances the loudness-normalization tracker by one analysis pass.
    ///
    /// A deliberately slow AGC: track the program's recent peak short-term
    /// level, aim the gain at `reference − peak`, and smooth the applied
    /// value asymmetrically — down fast when a loud passage arrives (so the
    /// visuals don't pin at 1), up over ~1 s (so a kick drum's gaps aren't
    /// pumped). Below the gate nothing updates: silence holds the gain.
    /// When disabled the target is simply 0, so toggling ramps the mapping
    /// back to the fixed one instead of jump-cutting.
    private func updateAutoGain(levelDB: Float) {
        if agcEnabled, levelDB > Self.agcGateDB {
            if agcPrimed {
                agcPeakDB = max(levelDB, agcPeakDB - Self.agcPeakDecayDBPerSecond * Float(hopSeconds))
            } else {
                agcPeakDB = levelDB
                agcPrimed = true
            }
        }
        let target =
            agcEnabled
            ? max(-Self.agcMaxCutDB, min(Self.agcMaxBoostDB, Self.agcReferenceDB - agcPeakDB))
            : 0
        let tau = target < agcGainDB ? TimeConstant.agcDown : TimeConstant.agcUp
        agcGainDB += (target - agcGainDB) * Float(1 - exp(-hopSeconds / tau))
    }
}

/// A second-order filter section (RBJ cookbook), transposed direct form II.
struct Biquad: Sendable {
    private var b0: Float = 1, b1: Float = 0, b2: Float = 0, a1: Float = 0, a2: Float = 0
    private var z1: Float = 0, z2: Float = 0

    init() {}

    init(lowPass frequency: Double, q: Double, sampleRate: Double) {
        let w = 2 * Double.pi * frequency / sampleRate, alpha = sin(w) / (2 * q), c = cos(w)
        let a0 = 1 + alpha
        b0 = Float((1 - c) / 2 / a0); b1 = Float((1 - c) / a0); b2 = b0
        a1 = Float(-2 * c / a0); a2 = Float((1 - alpha) / a0)
    }

    init(highPass frequency: Double, q: Double, sampleRate: Double) {
        let w = 2 * Double.pi * frequency / sampleRate, alpha = sin(w) / (2 * q), c = cos(w)
        let a0 = 1 + alpha
        b0 = Float((1 + c) / 2 / a0); b1 = Float(-(1 + c) / a0); b2 = b0
        a1 = Float(-2 * c / a0); a2 = Float((1 - alpha) / a0)
    }

    mutating func process(_ x: Float) -> Float {
        let y = b0 * x + z1
        z1 = b1 * x - a1 * y + z2
        z2 = b2 * x - a2 * y
        if !y.isFinite { clear(); return 0 }
        return y
    }

    mutating func clear() { z1 = 0; z2 = 0 }
}
