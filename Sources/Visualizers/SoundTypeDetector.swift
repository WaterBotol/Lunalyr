// SoundTypeDetector — what TYPE of sound is playing, for the visualisers.
//
// One calibrated power spectrum per detector pass (the FFT AudioAnalyzer
// already computes, every quarter window) goes in; three transient classes
// with onset counters and three sustained envelopes come out:
//
//   kick   35-120 Hz onset carried by the low band, confirmed by a beater
//          click (flat 1-6 kHz increment) or by the kick's downward pitch
//          sweep (35-300 Hz centroid falling by 18% within 35 ms)
//   snare  broadband, noise-like mid transient (1-6 kHz, flat increment)
//   hat    very high, short transient (7-16 kHz, top-heavy increment)
//   bass   steady tonal partials of a bass series (f0 35-110 Hz, or an
//          octave jump to 160 Hz) or a lone tone under 110 Hz; kick tails
//          are held out
//   vocal  the harmonic lead line whose partials move together in pitch
//          (vibrato, glides): a sung voice, or a sax, violin or synth lead
//   pad    the remaining steady, long-held harmonic bed (chords, strings)
//
// Independence rules:
// - Frequencies are in Hz and mapped through `binHz`, so 44.1, 48 and 96 kHz
//   and any FFT size land on the same regions.
// - Every time constant is in seconds and applied as exp(-dt/tau) with the
//   hop's real audio duration, so neither the hop nor the capture callback
//   size changes the result. Internal timing runs on an audio clock (the sum
//   of `dt`), so a capture clock that bunches buffers together cannot reorder
//   frames; only the published onset times are on the capture clock.
// - Levels are relative: per-band adaptive whitening for onset novelty and a
//   gated program-level tracker for the sustained classes, so quiet and loud
//   masters behave alike.
//
// The algorithm and every constant come from a prototype validated on
// synthetic fixtures with ground truth (Tests/Swift/SoundTypeFixtures.swift
// carries the same fixtures). Storage is allocated once, in init, for the
// largest spectrum AudioAnalyzer produces, so `process` never allocates and
// can run on the analysis worker. Not thread-safe: one caller at a time.
//
// The per-pass loops are `while` loops over raw pointers on purpose. In an
// unoptimised build (the test suite's), `for i in 0..<n` iterates through
// generic witness calls and runs several times slower; optimised, both
// compile to the same code.

import Accelerate
import Foundation

/// One sound-type onset: when it started (capture clock, backdated to the
/// estimated start of the sound) and how hard, 0...1.
struct SoundOnset: Sendable, Equatable {
    var time = -Double.infinity
    var strength: Float = 0
}

final class SoundTypeDetector {
    /// What each pass publishes. Transient envelopes are not here: the render
    /// side evaluates them from the onsets at its own display time.
    struct Output: Sendable, Equatable {
        /// Sustained envelopes, 0...1.
        var bass: Float = 0
        var vocal: Float = 0
        var pad: Float = 0
        /// Onsets detected since the last reset (wrapping).
        var kickOnsets: UInt32 = 0
        var snareOnsets: UInt32 = 0
        var hatOnsets: UInt32 = 0
        /// The newest onset of each class. Its strength may still rise during
        /// the top-up that follows the pass it fired on.
        var newestKick = SoundOnset()
        var newestSnare = SoundOnset()
        var newestHat = SoundOnset()
    }

    /// Every constant, in Hz, seconds and dB. None is per genre: each was set
    /// from measured distributions of the synthetic fixtures.
    struct Tuning: Sendable {
        var kickBand: ClosedRange<Float> = 35...120
        var lowMidBand: ClosedRange<Float> = 160...800
        var snareBand: ClosedRange<Float> = 1000...6000
        var hatBand: ClosedRange<Float> = 7000...16000
        var bassBand: ClosedRange<Float> = 35...220
        var bassTaperFrom: Float = 110
        var flatBand: ClosedRange<Float> = 1000...8000
        var whitenTau: Float = 3.0          // band peak-hold memory, s
        var compression: Float = 100        // log(1 + λ·W) / log(1 + λ)
        var fluxLag: Float = 0.021          // compare with the frame ~21 ms earlier
        var statsTau: Float = 1.0           // adaptive-threshold statistics, s
        var programRise: Float = 0.25       // program level follower, s
        var programFall: Float = 4.0
        // Onset thresholds: θ = max(floor, mean + k·dev)
        var kickK: Float = 2.2, kickFloor: Float = 0.12
        var snareK: Float = 2.4, snareFloor: Float = 0.09
        var hatK: Float = 1.8, hatFloor: Float = 0.08
        var kickRefractory: Float = 0.085, snareRefractory: Float = 0.08, hatRefractory: Float = 0.06
        var rearm: Float = 0.55             // re-arm below this fraction of θ
        var topUp: Float = 0.03             // onset strength may still rise this long, s
        // Transient envelopes (the render side's normal shapes), s
        var kickAttack: Float = 0.010, kickRelease: Float = 0.26
        var snareAttack: Float = 0.010, snareRelease: Float = 0.22
        var hatAttack: Float = 0.008, hatRelease: Float = 0.15
        // Sustained envelopes, s
        var bassAttack: Float = 0.06, bassRelease: Float = 0.30
        var vocalAttack: Float = 0.08, vocalRelease: Float = 0.35
        var padAttack: Float = 0.30, padRelease: Float = 1.0
        // A prominent instance sits `offset` dB below the program level; the
        // envelope spans `window` dB below that.
        var bassOffset: Float = 6, bassWindow: Float = 24
        var vocalOffset: Float = 10, vocalWindow: Float = 22
        var padOffset: Float = 16, padWindow: Float = 24
        /// An onset is usually detected about this fraction of a window after
        /// it starts; published onset times are backdated by it.
        var latencyFraction: Float = 0.30
    }

    /// The most bins a spectrum may have: AudioAnalyzer's 8192-point window.
    static let maximumBinCount = 4096
    private static let depth = 8
    private static let bandCapacity = 128
    private static let peakCapacity = 64
    private static let carryCapacity = 128
    private static let seedCapacity = 8
    /// Bounds of a ±40 cent match around a harmonic: 2^(∓40/1200).
    private static let matchLow: Float = 0.977_159_9
    private static let matchHigh: Float = 1.023_374_2

    let tuning: Tuning
    private(set) var output = Output()

    // MARK: Layout, rebuilt by configure

    private var binHz: Float = 0
    private var binCount = 0
    private var bandCount = 0
    private var kickBands = 0..<0, snareBands = 0..<0, hatBands = 0..<0
    private var analysedLo = 1, analysedHi = 1
    private var kickRegion = Region(), lowMidRegion = Region(), snareRegion = Region()
    private var hatRegion = Region(), bassRegion = Region()
    private var flatLo = 1, flatHi = 1
    private var highLo = 1, highHi = 1
    private var lowLo = 1, lowHi = 0
    private var peakLo = 2, peakHi = 1

    // MARK: Storage, allocated once

    private let binCapacity: Int
    /// History ring of sanitised spectra and whitened band levels; slot
    /// `ringHead` holds the current pass while it is processed.
    private let ringP: UnsafeMutablePointer<Float>
    private let ringL: UnsafeMutablePointer<Float>
    private let ringLow: UnsafeMutablePointer<Float>
    private let ringTime: UnsafeMutablePointer<Double>
    private var ringHead = 0
    private let increment: UnsafeMutablePointer<Float>
    private let work: UnsafeMutablePointer<Float>
    private let db: UnsafeMutablePointer<Float>
    private let prefix: UnsafeMutablePointer<Float>
    private let binFrequency: UnsafeMutablePointer<Float>
    private let tilt: UnsafeMutablePointer<Float>
    private let regionWeights: UnsafeMutablePointer<Float>
    private let bandStart: UnsafeMutablePointer<Int>
    private let bandEnd: UnsafeMutablePointer<Int>
    private let bandShare: UnsafeMutablePointer<Float>
    private let bandHold: UnsafeMutablePointer<Float>
    private let typical: UnsafeMutablePointer<Float>
    private let peaks = PeakTable(capacity: SoundTypeDetector.peakCapacity)
    private let eligible = PeakTable(capacity: SoundTypeDetector.peakCapacity)
    private let previous = CarryTable(capacity: SoundTypeDetector.carryCapacity)
    private let next = CarryTable(capacity: SoundTypeDetector.carryCapacity)
    private let seeds: UnsafeMutablePointer<Int>

    // MARK: State

    private var clock = 0.0
    private var programPower: Float = 0
    private var primed = false
    private var kick: OnsetTracker
    private var snare: OnsetTracker
    private var hat: OnsetTracker
    private var previousOdfSnare: Float = 0
    private var previousFlatness: Float = 0
    private var lastBassInput: Float = 0
    private var bassEnv: Float = 0, vocalEnv: Float = 0, padEnv: Float = 0
    private var hold = KickHold()
    private var tonal = TonalState()
    private var pendingKick: PendingKick?

    init(tuning: Tuning = Tuning()) {
        self.tuning = tuning
        let bins = Self.maximumBinCount
        binCapacity = bins
        ringP = .allocate(capacity: Self.depth * bins)
        ringL = .allocate(capacity: Self.depth * Self.bandCapacity)
        ringLow = .allocate(capacity: Self.depth)
        ringTime = .allocate(capacity: Self.depth)
        increment = .allocate(capacity: bins)
        work = .allocate(capacity: bins)
        db = .allocate(capacity: bins)
        prefix = .allocate(capacity: bins + 1)
        binFrequency = .allocate(capacity: bins)
        tilt = .allocate(capacity: bins)
        regionWeights = .allocate(capacity: 5 * (bins + 4))
        bandStart = .allocate(capacity: Self.bandCapacity + 1)
        bandEnd = .allocate(capacity: Self.bandCapacity + 1)
        bandShare = .allocate(capacity: Self.bandCapacity)
        bandHold = .allocate(capacity: Self.bandCapacity)
        typical = .allocate(capacity: 3 * OnsetTracker.history)
        seeds = .allocate(capacity: Self.seedCapacity)
        seeds.initialize(repeating: 0, count: Self.seedCapacity)
        for buffer in [increment, work, db, binFrequency, tilt] { buffer.initialize(repeating: 0, count: bins) }
        ringP.initialize(repeating: 0, count: Self.depth * bins)
        ringL.initialize(repeating: 0, count: Self.depth * Self.bandCapacity)
        ringLow.initialize(repeating: 0, count: Self.depth)
        ringTime.initialize(repeating: -.infinity, count: Self.depth)
        prefix.initialize(repeating: 0, count: bins + 1)
        regionWeights.initialize(repeating: 0, count: 5 * (bins + 4))
        bandStart.initialize(repeating: 0, count: Self.bandCapacity + 1)
        bandEnd.initialize(repeating: 0, count: Self.bandCapacity + 1)
        bandShare.initialize(repeating: 0, count: Self.bandCapacity)
        bandHold.initialize(repeating: 0, count: Self.bandCapacity)
        typical.initialize(repeating: 0, count: 3 * OnsetTracker.history)
        kick = OnsetTracker(typical: typical)
        snare = OnsetTracker(typical: typical + OnsetTracker.history)
        hat = OnsetTracker(typical: typical + 2 * OnsetTracker.history)
    }

    deinit {
        for buffer in [ringP, ringL, ringLow, increment, work, db, prefix, binFrequency, tilt, regionWeights,
                       bandShare, bandHold, typical] { buffer.deallocate() }
        ringTime.deallocate()
        bandStart.deallocate()
        bandEnd.deallocate()
        seeds.deallocate()
        for table in [peaks, eligible] { table.deallocate() }
        for table in [previous, next] { table.deallocate() }
    }

    /// Forgets everything, counters included: a new source starts from zero.
    func reset() {
        output = Output()
        binCount = 0
        binHz = 0
        clock = 0
        programPower = 0
        primed = false
        kick = OnsetTracker(typical: typical)
        snare = OnsetTracker(typical: typical + OnsetTracker.history)
        hat = OnsetTracker(typical: typical + 2 * OnsetTracker.history)
        previousOdfSnare = 0
        previousFlatness = 0
        lastBassInput = 0
        bassEnv = 0; vocalEnv = 0; padEnv = 0
        hold = KickHold()
        tonal = TonalState()
        pendingKick = nil
    }

    /// Capture paused for `idle` seconds beyond the usual quarter second: the
    /// sustained classes fade as the render side showed them fading
    /// (AudioAnalyzer's idle fade), so the next pass attacks from there.
    func settle(idle: Double) {
        guard idle > 0, idle.isFinite else { return }
        let fade = Float(exp(-idle / 0.2))
        bassEnv *= fade; vocalEnv *= fade; padEnv *= fade
        output.bass = bassEnv; output.vocal = vocalEnv; output.pad = padEnv
    }

    // MARK: - Configuration

    private func configure(binHz: Float, binCount: Int) {
        self.binHz = binHz
        self.binCount = binCount
        let top = min(16_000, binHz * Float(binCount) * 0.9)  // 0.45 fs
        // Semi-log bands, 12 per octave from 30 Hz, never narrower than a bin.
        // The edges go into bandStart; band b spans edges b..<b+1.
        var edges = 0
        var step = 0
        while edges <= Self.bandCapacity {
            let f = 30 * pow(2, Float(step) / 12)
            var bin = max(1, Int((f / binHz).rounded()))
            if edges > 0 { bin = max(bin, bandStart[edges - 1] + 1) }
            if Float(bin) * binHz > top || bin >= binCount { break }
            bandStart[edges] = bin
            edges += 1
            step += 1
        }
        bandCount = max(0, edges - 1)
        analysedLo = edges > 0 ? bandStart[0] : 1
        analysedHi = edges > 0 ? bandStart[edges - 1] : 1
        for b in 0..<bandCount { bandEnd[b] = bandStart[b + 1] }
        let analysed = Float(max(1, analysedHi - analysedLo))
        for b in 0..<bandCount { bandShare[b] = Float(bandEnd[b] - bandStart[b]) / analysed }
        kickBands = bands(tuning.kickBand, top: top)
        snareBands = bands(tuning.snareBand, top: top)
        hatBands = bands(tuning.hatBand, top: top)

        for k in 0..<binCount {
            binFrequency[k] = Float(k) * binHz
            tilt[k] = Float(k) * binHz * 0.001
        }
        let stride = binCapacity + 4
        kickRegion = Region(tuning.kickBand, binHz: binHz, binCount: binCount, top: top, weights: regionWeights)
        lowMidRegion = Region(tuning.lowMidBand, binHz: binHz, binCount: binCount, top: top, weights: regionWeights + stride)
        snareRegion = Region(tuning.snareBand, binHz: binHz, binCount: binCount, top: top, weights: regionWeights + 2 * stride)
        hatRegion = Region(tuning.hatBand, binHz: binHz, binCount: binCount, top: top, weights: regionWeights + 3 * stride)
        bassRegion = Region(tuning.bassBand, binHz: binHz, binCount: binCount, top: top, weights: regionWeights + 4 * stride,
                            taperFrom: tuning.bassTaperFrom)
        flatLo = max(1, Int(tuning.flatBand.lowerBound / binHz))
        flatHi = max(flatLo, min(binCount, Int(min(tuning.flatBand.upperBound, top) / binHz)))
        highLo = max(1, Int(3000 / binHz))
        highHi = max(highLo, min(binCount, Int(top / binHz)))
        lowLo = max(1, Int(35 / binHz))
        lowHi = min(binCount - 1, Int(300 / binHz))
        peakLo = max(2, Int(35 / binHz))
        peakHi = min(binCount - 4, Int(5000 / binHz))

        for slot in 0..<Self.depth { ringTime[slot] = -.infinity; ringLow[slot] = 0 }
        vDSP_vclr(ringP, 1, vDSP_Length(Self.depth * binCapacity))
        vDSP_vclr(ringL, 1, vDSP_Length(Self.depth * Self.bandCapacity))
        vDSP_vclr(bandHold, 1, vDSP_Length(Self.bandCapacity))
        ringHead = 0
        tonal = TonalState()
        hold = KickHold()
    }

    private func centre(_ b: Int) -> Float { 0.5 * Float(bandStart[b] + bandEnd[b] - 1) * binHz }

    private func bands(_ r: ClosedRange<Float>, top: Float) -> Range<Int> {
        var lo = bandCount, hi = -1
        for b in 0..<bandCount where centre(b) >= r.lowerBound { lo = b; break }
        for b in stride(from: bandCount - 1, through: 0, by: -1) where centre(b) <= min(r.upperBound, top) { hi = b; break }
        return lo <= hi ? lo..<(hi + 1) : lo..<lo
    }

    /// The newest stored frame at least `lag` seconds older than `time`.
    private func reference(_ time: Double, lag: Float, dt: Float) -> Int {
        var ref = -1
        var back = 1
        while back < Self.depth {
            let i = (ringHead - back + Self.depth) % Self.depth
            guard ringTime[i].isFinite else { break }
            ref = i
            if time - ringTime[i] >= Double(lag) - 0.25 * Double(dt) { break }
            back += 1
        }
        return ref
    }

    // MARK: - Per-pass processing

    /// `power`: one-sided power spectrum, bin k at k·binHz, normalised so a
    /// sinusoid of amplitude A sums to A²/2 over its main lobe. NaN, infinite
    /// and negative bins count as 0. `dt`: audio seconds since the previous
    /// pass. `time`: capture time of the window's newest sample, on the
    /// clock the published onset times use.
    func process(power input: UnsafeBufferPointer<Float>, binHz: Float, dt rawDt: Double, time: Double) {
        guard let source = input.baseAddress, input.count >= 16, binHz.isFinite, binHz > 0, time.isFinite else { return }
        let n = min(input.count, binCapacity)
        if binHz != self.binHz || n != binCount { configure(binHz: binHz, binCount: n) }
        let elapsed = rawDt.isFinite ? max(0, rawDt) : 0.01
        let dt = Float(min(0.1, max(0.0005, elapsed)))
        clock += elapsed
        let now = clock
        let t = tuning
        let head = ringHead
        let p = ringP + head * binCapacity
        let level = ringL + head * Self.bandCapacity
        var zero: Float = 0
        vDSP_vthres(source, 1, &zero, p, 1, vDSP_Length(n))  // NaN and negatives → 0
        p[0] = 0
        var sum: Float = 0
        vDSP_sve(p, 1, &sum, vDSP_Length(n))
        if !sum.isFinite {
            var k = 0
            while k < n { if !p[k].isFinite { p[k] = 0 }; k += 1 }
        }

        // 0. Program level: a power follower that rises in ~0.25 s and falls
        //    over ~4 s, gated so silence holds it.
        var total: Float = 0
        vDSP_sve(p + analysedLo, 1, &total, vDSP_Length(analysedHi - analysedLo))
        let audible = 10 * log10(total + 1e-12) > -65
        if audible {
            if !primed { programPower = total; primed = true }
            let tau = total > programPower ? t.programRise : t.programFall
            programPower += (total - programPower) * (1 - exp(-dt / tau))
        }
        let G = 10 * log10(maxf(programPower, 1e-7))

        // 1. Bands, adaptive whitening, compression.
        let decayW = exp(-dt / t.whitenTau)
        let floorBase = pow(10, (G - 70) / 10)
        let logNorm = 1 / log1p(t.compression), compression = t.compression
        var b = 0
        while b < bandCount {
            var s: Float = 0
            vDSP_sve(p + bandStart[b], 1, &s, vDSP_Length(bandEnd[b] - bandStart[b]))
            let held = maxf(s, bandHold[b] * decayW)
            bandHold[b] = held
            let w = s / maxf(maxf(held, floorBase * bandShare[b]), 1e-20)
            level[b] = log1p(compression * w) * logNorm
            b += 1
        }

        // 2. Novelty against the frame ~21 ms earlier, whatever the hop.
        let ref = reference(now, lag: t.fluxLag, dt: dt)
        var odfK: Float = 0, odfS: Float = 0, odfH: Float = 0
        var dLow: Float = 0, dLowMid: Float = 0, dMid: Float = 0, dHigh: Float = 0, eLowRef: Float = 0
        var flat: Float = 0
        var highCentroid: Float = 0
        if ref >= 0 {
            let refL = ringL + ref * Self.bandCapacity, refP = ringP + ref * binCapacity
            var b = kickBands.lowerBound
            while b < kickBands.upperBound { odfK += maxf(0, level[b] - refL[b]); b += 1 }
            odfK /= Float(max(1, kickBands.count))
            odfS = superflux(level, refL, snareBands)
            odfH = superflux(level, refL, hatBands)
            vDSP_vsub(refP, 1, p, 1, increment, 1, vDSP_Length(n))
            vDSP_vthres(increment, 1, &zero, increment, 1, vDSP_Length(n))
            dLow = kickRegion.sum(increment)
            dLowMid = lowMidRegion.sum(increment)
            eLowRef = kickRegion.sum(refP)
            dMid = snareRegion.sum(increment)
            dHigh = hatRegion.sum(increment)
            flat = incrementFlatness()
            // Centroid of the positive increment above 3 kHz: an "s" centres
            // around 5-8 kHz, a hi-hat around 8-12 kHz.
            if highHi > highLo {
                var num: Float = 0, den: Float = 0
                vDSP_dotpr(increment + highLo, 1, binFrequency + highLo, 1, &num, vDSP_Length(highHi - highLo))
                vDSP_sve(increment + highLo, 1, &den, vDSP_Length(highHi - highLo))
                highCentroid = den > 0 ? num / den : 0
            }
        }

        // 3. Region levels and increment shapes.
        let eKick = kickRegion.sum(p), eSnare = snareRegion.sum(p), eHat = hatRegion.sum(p)
        let dbKick = 10 * log10(eKick + 1e-14), dbSnare = 10 * log10(eSnare + 1e-14), dbHat = 10 * log10(eHat + 1e-14)
        let shapeHM = 10 * log10((dHigh + 1e-14) / (dMid + 1e-14))         // > 0: hat-shaped
        let shapeML = 10 * log10((dMid + 1e-14) / (dLow + 1e-14))          // < 0: the low band dominates
        let harmonicRatio = 10 * log10((dLowMid + 1e-14) / (dLow + 1e-14))  // bass harmonics vs thump
        let riseLow = 10 * log10((eKick + 1e-14) / (eLowRef + 1e-14))
        // Power centroid of 35-300 Hz: a kick's sweep pulls it down by a third
        // within ~30 ms; a bass note's harmonic pattern keeps it put.
        var lowCentroid: Float = 0
        if lowHi >= lowLo {
            var num: Float = 0, den: Float = 0
            vDSP_dotpr(p + lowLo, 1, binFrequency + lowLo, 1, &num, vDSP_Length(lowHi - lowLo + 1))
            vDSP_sve(p + lowLo, 1, &den, vDSP_Length(lowHi - lowLo + 1))
            lowCentroid = den > 0 ? num / den : 0
        }

        // 4. Transients.
        let latency = Double(t.latencyFraction / binHz)  // a fraction of the window's length
        let thrK = kick.threshold(t.kickK, t.kickFloor)
        let thrS = snare.threshold(t.snareK, t.snareFloor)
        let thrH = hat.threshold(t.hatK, t.hatFloor)

        // Kick: novelty in 35-120 Hz, a real level rise and the low band
        // carrying the increment. It fires at once with a beater click (flat
        // mid novelty this pass or the last) or a very sharp non-harmonic
        // rise; otherwise it waits up to 35 ms for the kick's pitch sweep (the
        // 35-300 Hz centroid starting above 105 Hz and falling steadily by
        // 18%), which a bass note, starting on its fixed pitch, does not show.
        let click = maxf(odfS, previousOdfSnare) >= 0.07 && maxf(flat, previousFlatness) >= 0.3
        let sharp = riseLow >= 14 && harmonicRatio <= -10
        var kickFired = false
        let basic = audible && kick.ready(now, t.kickRefractory) && odfK > thrK && riseLow > 5
            && dbKick > G - 18 && shapeML <= 6
        let energyBefore = ref >= 0 ? ringLow[ref] : 0
        if let pending = pendingKick {
            let monotonic = lowCentroid <= pending.lastCentroid * 1.03
            if !monotonic || now - pending.start > 0.035 {
                pendingKick = nil
                hold.cancel()
            } else if lowCentroid <= pending.centroid * 0.82 {
                pendingKick = nil
                fireKick(now: now, onset: time - (now - pending.start) - latency, odf: maxf(pending.odf, odfK),
                         db: dbKick, G: G)
                kickFired = true
            } else {
                pendingKick?.lastCentroid = lowCentroid
                pendingKick?.odf = maxf(pending.odf, odfK)
            }
        } else if basic {
            if shapeML <= 0, click || sharp {
                fireKick(now: now, onset: time - latency, odf: odfK, db: dbKick, G: G)
                kickFired = true
                hold.start(time: now, before: lastBassInput, energyBefore: energyBefore)
            } else if lowCentroid >= 105, riseLow >= 8, odfK >= 1.3 * thrK {
                // A kick's sweep starts at 100-200 Hz; a bass note starts on its pitch.
                pendingKick = PendingKick(start: now, centroid: lowCentroid, lastCentroid: lowCentroid, odf: odfK)
                hold.start(time: now, before: lastBassInput, energyBefore: energyBefore)
            }
        }
        let kickNear = kickFired || pendingKick != nil || Float(now - kick.last) < 0.035

        // While a lead voice is evidently singing, its consonants ("t", "s")
        // are noise bursts too: demand a louder, flatter snare, and a hat
        // whose increment centres above 9.5 kHz ("s" centres at 5-9 kHz).
        let singing = tonal.evidence
        // Snare: broadband, noise-like, mid-weighted increment. Near a kick it
        // must also beat the kick's own beater click.
        var snareFired = false
        if audible, snare.ready(now, t.snareRefractory), odfS > thrS * (kickNear ? 1.4 : 1),
           flat > 0.2 + 0.1 * singing, shapeHM < 4, dbSnare > G - 26 + 12 * singing, !kickNear || shapeML > -8 {
            snareFired = true
            snare.fire(now, odf: odfS, weight: clampf((dbSnare - (G - 28)) / 16, 0.25, 1), topUp: t.topUp)
            output.snareOnsets &+= 1
            output.newestSnare = SoundOnset(time: time - latency, strength: snare.drive)
        }
        let snareNear = snareFired || Float(now - snare.last) < 0.035

        // Hat: novelty above 7 kHz with a hat-shaped (high-dominated)
        // increment. A coincident snare suppresses it unless the high band
        // clearly dominates.
        let hatLike = singing < 0.5 || highCentroid >= 9500 || shapeHM >= 9
        if audible, hat.ready(now, t.hatRefractory), odfH > thrH * (kickNear ? 1.15 : 1),
           shapeHM > (kickNear ? 6 : 3), hatLike, dbHat > G - 50 + 10 * singing, !snareNear || odfH > 1.6 * odfS {
            hat.fire(now, odf: odfH, weight: clampf((dbHat - (G - 40)) / 18, 0.25, 1), topUp: t.topUp)
            output.hatOnsets &+= 1
            output.newestHat = SoundOnset(time: time - latency, strength: hat.drive)
        }
        previousOdfSnare = odfS
        previousFlatness = flat

        kick.advance(now, odf: odfK, threshold: thrK, rearm: t.rearm, dt: dt, statsTau: t.statsTau)
        snare.advance(now, odf: odfS, threshold: thrS, rearm: t.rearm, dt: dt, statsTau: t.statsTau)
        hat.advance(now, odf: odfH, threshold: thrH, rearm: t.rearm, dt: dt, statsTau: t.statsTau)
        // The strength top-up reaches the newest published onset.
        if now <= kick.topUpUntil { output.newestKick.strength = maxf(output.newestKick.strength, kick.amplitude()) }
        if now <= snare.topUpUntil { output.newestSnare.strength = maxf(output.newestSnare.strength, snare.amplitude()) }
        if now <= hat.topUpUntil { output.newestHat.strength = maxf(output.newestHat.strength, hat.amplitude()) }
        kick.decay(dt: dt, release: t.kickRelease)
        snare.decay(dt: dt, release: t.snareRelease)
        hat.decay(dt: dt, release: t.hatRelease)

        // 5. Tonal analysis: stable peaks, bass series, lead series.
        tonalAnalysis(p, G: G, dt: dt, now: now)

        // 6. Sustained classes.
        let eLow = bassRegion.sum(p)
        let ref40 = reference(now, lag: 0.04, dt: dt)
        let lowSlope: Float = ref40 >= 0
            ? 10 * log10((eLow + 1e-14) / (ringLow[ref40] + 1e-14)) / maxf(0.02, Float(now - ringTime[ref40])) : 0
        let bassInput = hold.apply(time: now, slope: lowSlope, energy: eLow, input: tonal.bassPower)
        lastBassInput = bassInput
        let bassDb = 10 * log10(bassInput + 1e-14)
        let vocalDb = 10 * log10(tonal.vocalPower + 1e-14)
        let padDb = 10 * log10(tonal.padPower + 1e-14)
        let gate: Float = audible ? 1 : 0
        let nBass = gate * clampf((bassDb - (G - t.bassOffset - t.bassWindow)) / t.bassWindow, 0, 1)
        let nVocal = gate * clampf((vocalDb - (G - t.vocalOffset - t.vocalWindow)) / t.vocalWindow, 0, 1)
        let nPad = gate * clampf((padDb - (G - t.padOffset - t.padWindow)) / t.padWindow, 0, 1)
        // When the music stops (frames under -65 dBFS) the sustained classes
        // fade within ~1 s instead of riding their musical releases.
        let quiet: Float = audible ? .infinity : 0.3
        bassEnv = follow(bassEnv, nBass, dt, t.bassAttack, minf(t.bassRelease, quiet))
        vocalEnv = follow(vocalEnv, nVocal, dt, t.vocalAttack, minf(t.vocalRelease, quiet))
        padEnv = follow(padEnv, nPad, dt, t.padAttack, minf(t.padRelease, quiet))
        output.bass = bassEnv
        output.vocal = vocalEnv
        output.pad = padEnv

        // 7. Keep this frame (its spectrum and levels are already in the slot).
        ringLow[head] = eLow
        ringTime[head] = now
        ringHead = (head + 1) % Self.depth
    }

    private func fireKick(now: Double, onset: Double, odf: Float, db: Float, G: Float) {
        kick.fire(now, odf: odf, weight: clampf((db - (G - 20)) / 14, 0.25, 1), topUp: tuning.topUp)
        output.kickOnsets &+= 1
        output.newestKick = SoundOnset(time: onset, strength: kick.drive)
    }

    /// SuperFlux: the mean rise of each band over the ±1-band maximum of the
    /// reference, so vibrato does not count as an onset.
    private func superflux(_ level: UnsafeMutablePointer<Float>, _ refL: UnsafeMutablePointer<Float>, _ r: Range<Int>) -> Float {
        var s: Float = 0
        let last = bandCount - 1
        var b = r.lowerBound
        while b < r.upperBound {
            let m = maxf(maxf(refL[b > 0 ? b - 1 : 0], refL[b]), refL[b < last ? b + 1 : last])
            s += maxf(0, level[b] - m)
            b += 1
        }
        return s / Float(max(1, r.count))
    }

    /// Spectral flatness (geometric / arithmetic mean) of the positive power
    /// increment over 1-8 kHz, tilted by f/1 kHz so pink noise counts as flat:
    /// ~0.3-0.6 for a noise burst or a beater click, < 0.1 when a comb of
    /// harmonic partials rose. Raw power, not whitened: whitening would blow
    /// up the empty bins of a sparse spectrum and make any onset look flat.
    private func incrementFlatness() -> Float {
        let count = flatHi - flatLo
        guard count > 0 else { return 0 }
        let length = vDSP_Length(count)
        vDSP_vmul(increment + flatLo, 1, tilt + flatLo, 1, work, 1, length)
        var sum: Float = 0
        vDSP_sve(work, 1, &sum, length)
        guard sum > 1e-20 else { return 0 }
        let mean = sum / Float(count)
        var eps = mean * 1e-3
        vDSP_vsadd(work, 1, &eps, work, 1, length)
        var elements = Int32(count)
        vvlogf(work, work, &elements)
        var logSum: Float = 0
        vDSP_sve(work, 1, &logSum, length)
        return exp(logSum / Float(count)) / (mean + eps)
    }

    // MARK: - Tonal analysis

    private func tonalAnalysis(_ p: UnsafeMutablePointer<Float>, G: Float, dt: Float, now: Double) {
        let pk = peaks, prev = previous
        var count = 0
        if peakHi > peakLo + 2 {
            let lo = max(0, peakLo - 10), hi = min(binCount - 1, peakHi + 10)
            let span = vDSP_Length(hi - lo + 1)
            var tiny: Float = 1e-20, ten: Float = 10
            var elements = Int32(span)
            vDSP_vthr(p + lo, 1, &tiny, db + lo, 1, span)
            vvlog10f(db + lo, db + lo, &elements)
            vDSP_vsmul(db + lo, 1, &ten, db + lo, 1, span)
            prefix[lo] = 0
            var running: Float = 0
            var bin = lo
            while bin <= hi { running += db[bin]; prefix[bin + 1] = running; bin += 1 }
            // Peaks: local maxima ≥ 8 dB above the local mean, refined by a
            // parabola, matched to the last pass's partials for age and slope.
            var strongest: Float = -200
            vDSP_maxv(db + peakLo, 1, &strongest, vDSP_Length(peakHi - peakLo + 1))
            strongest = maxf(strongest, -200)
            let floorDb = maxf(G - 75, strongest - 70)
            // Matching tolerance as a pitch slew rate (~3600 cents/s: a 3-
            // semitone glide in 70 ms), so a glide tracks the same at any hop.
            let slewRatio = pow(2, clampf(3600 * dt, 30, 150) / 1200) - 1
            let minimumTolerance = binHz * 1.01
            let previousCount = tonal.previousCount
            let pf = prev.frequency, pm = prev.matched
            var j = 0
            var k = peakLo - 1
            while k < peakHi {
                k += 1
                let c = db[k]
                guard c > db[k - 1], c >= db[k + 1], c >= db[k + 2], k < 3 || c >= db[k - 2], c > floorDb,
                      count < Self.peakCapacity else { continue }
                let a = k - 8 > lo + 1 ? k - 8 : lo + 1, b = k + 8 < hi ? k + 8 : hi
                guard c - (prefix[b + 1] - prefix[a]) / Float(b - a + 1) >= 8 else { continue }
                let l = db[k - 1], r = db[k + 1]
                let den = l - 2 * c + r
                let offset = den.magnitude > 1e-6 ? clampf(0.5 * (l - r) / den, -0.5, 0.5) : 0
                let f = (Float(k) + offset) * binHz
                let tol = maxf(minimumTolerance, f * slewRatio)
                while j < previousCount, pf[j] < f - tol { j += 1 }
                // The nearest unclaimed previous partial within the tolerance.
                var match = -1
                var m = j
                while m < previousCount, pf[m] <= f + tol {
                    if !pm[m], match < 0 || (pf[m] - f).magnitude < (pf[match] - f).magnitude { match = m }
                    m += 1
                }
                var age: Float = 0, slope: Float = 0
                var maxDb = c, sinceMax: Float = 0
                let cents = 1200 * log2(f)
                var centsMean = cents, centsVar: Float = 0
                if match >= 0 {
                    pm[match] = true
                    let gap = prev.missing[match] + dt
                    age = prev.age[match] + gap
                    // Decay rate (dB/s) since this partial's running maximum,
                    // which itself leaks at 6 dB/s so old peaks are forgotten.
                    let leaked = prev.maxDb[match] - 6 * gap
                    if c >= leaked { maxDb = c; sinceMax = 0 } else { maxDb = leaked; sinceMax = prev.sinceMax[match] + gap }
                    slope = -(maxDb - c) / maxf(0.08, sinceMax)
                    // Pitch wobble of this partial: cents rms around a 0.2 s mean.
                    let follow = 1 - exp(-gap / 0.2)
                    centsMean = prev.centsMean[match] + (cents - prev.centsMean[match]) * follow
                    let dev = clampf(cents - centsMean, -100, 100)
                    centsVar = prev.centsVar[match] + (dev * dev - prev.centsVar[match]) * follow
                }
                pk.frequency[count] = f
                pk.cents[count] = cents
                pk.power[count] = p[k - 1] + p[k] + p[k + 1]
                pk.centsMean[count] = centsMean
                pk.centsVar[count] = centsVar
                pk.maxDb[count] = maxDb
                pk.sinceMax[count] = sinceMax
                pk.age[count] = age
                pk.slope[count] = slope
                pk.owner[count] = 0
                count += 1
            }
        }
        tonal.count = count
        let stableAge: Float = 0.03
        // How "lead-like" each partial is: a sung syllable's partials are
        // young (< 0.4 s) or wobble in pitch (vibrato, glides); a pad's are
        // long-lived and still. This only steers the lead search; the
        // vocal/pad split is decided by the series' coherent pitch motion.
        var i = 0
        while i < count {
            let rms = pk.centsVar[i].squareRoot()
            let f = pk.frequency[i]
            // Vibrato is only measurable in cents where a bin is narrow enough.
            let wobble = f >= 200 ? smooth01((rms - 5) / 10) : 0
            let young = 1 - smooth01((pk.age[i] - 0.4) / 0.6)
            pk.leadWeight[i] = maxf(wobble, young)
            // Held tone rather than a noise peak: it has lasted a little, and
            // its frequency wanders less than ~0.4 bin (rms, in bins).
            let rmsBins = rms * f * 0.000578 / binHz
            pk.pitchStable[i] = smooth01((pk.age[i] - 0.03) / 0.1) * (1 - smooth01((rmsBins - 0.25) / 0.35))
            i += 1
        }

        // Bass series (f0 35-160 Hz, fundamental present, energy in its first
        // few partials) claims its partials first, so a bass line's harmonics
        // and octave notes never read as voice or pad. The 110-160 Hz
        // extension only follows a bass line that was playing below 110 Hz
        // within the last second (octave jumps).
        let bassTop: Float = now < tonal.bassLineUntil ? 160 : 110
        let bassSeries = bestSeries(lo: 35, hi: bassTop, requireFundamental: true, minHits: 2, stableAge: stableAge,
                                    owner: 1, previous: tonal.bassF0, weighted: false, bassShaped: true)
        tonal.bassF0 = bassSeries.f0
        if bassSeries.f0 > 0, bassSeries.f0 <= 110 { tonal.bassLineUntil = now + 1.0 }
        // Lead series (f0 100-1000 Hz, at least three partials) on the rest.
        let lead = bestSeries(lo: 100, hi: 1000, requireFundamental: false, minHits: 3, stableAge: stableAge,
                              owner: 2, previous: tonal.leadF0, weighted: true, bassShaped: false)

        var tonalTotal: Float = 0, leadPower: Float = 0, bedPower: Float = 0, bassPower: Float = 0
        var leadSteady: Float = 0, leadMature: Float = 0
        i = -1
        while i + 1 < count {
            i += 1
            guard pk.age[i] >= stableAge else { continue }
            let f = pk.frequency[i], power = pk.power[i], owner = pk.owner[i]
            // Steady: decaying slower than 30 dB/s (pads, voices, held notes,
            // 808 tails); a kick tail, tom or snare body decays at 60-150 dB/s.
            let steady = smooth01((pk.slope[i] + 60) / 30)
            let mature = smooth01((pk.age[i] - 0.05) / 0.25)
            // Bass: partials of a bass series, or a lone tone below 110 Hz.
            if owner == 1 || (owner == 0 && f < 110) {
                bassPower += power * bassWeight(f) * steady * pk.pitchStable[i]
            }
            guard owner != 1, f >= 150 else { continue }
            tonalTotal += power
            if owner == 2 {
                leadPower += power
                leadSteady += power * steady
                leadMature += power * steady * mature * pk.pitchStable[i]
            } else {
                bedPower += power * steady * mature * pk.pitchStable[i]
            }
        }
        let h1 = tonalTotal > 0 ? leadPower / tonalTotal : 0
        let steadyShare = leadPower > 0 ? leadSteady / leadPower : 0

        // Lead track: continuous while the f0 stays within 80 cents (octave
        // slips of the estimate tolerated); it survives 80 ms of masking.
        if lead.f0 > 0 {
            var cents = 1200 * log2(lead.f0 / 100)
            let raw: Float = tonal.leadF0 > 0 ? 1200 * log2(lead.f0 / tonal.leadF0) : 10_000
            let octave = (raw / 1200).rounded()
            let jump = (raw - 1200 * octave).magnitude
            if jump < 80, octave.magnitude <= 1 {
                cents -= 1200 * octave
                tonal.trackAge += dt + tonal.leadMissing
            } else {
                tonal.trackAge = 0
            }
            tonal.leadF0 = 100 * pow(2, cents / 1200)
            tonal.leadMissing = 0
        } else {
            tonal.leadMissing += dt
            if tonal.leadMissing > 0.08 { tonal.leadF0 = 0; tonal.trackAge = 0 }
        }
        // Coherent pitch motion: each lead partial's deviation (cents) from
        // its own 0.2 s mean, power-weighted across partials above 300 Hz.
        // Vibrato and glides move every partial of one voice together; a
        // pad's beating partials wobble independently and average out.
        var devSum: Float = 0, devWeight: Float = 0
        i = 0
        while i < count {
            if pk.owner[i] == 2 && pk.frequency[i] >= 300 && pk.age[i] >= 0.05 {
                let dev = clampf(pk.cents[i] - pk.centsMean[i], -100, 100)
                devSum += dev * pk.power[i]
                devWeight += pk.power[i]
            }
            i += 1
        }
        if devWeight > 0 {
            let common = devSum / devWeight
            tonal.coherentVar += (common * common - tonal.coherentVar) * (1 - exp(-dt / 0.25))
        } else {
            tonal.coherentVar *= exp(-dt / 0.5)
        }
        let motion = maxf(0, tonal.coherentVar).squareRoot()

        let harmonic = smooth01((lead.coverage - 0.2) / 0.3) * smooth01((h1 - 0.15) / 0.25)
        let continuity = smooth01((tonal.trackAge - 0.04) / 0.08)
        let moving = smooth01((motion - 10) / 12)
        let instant = harmonic * continuity * moving
        // Evidence rises within ~0.15 s of vibrato or syllables and is kept
        // through the phrase: it falls over 1 s while a lead line persists
        // and 2 s through gaps.
        let target = leadPower > 0 ? instant : 0
        let tau: Float = target > tonal.evidence ? 0.15 : (leadPower > 0 ? 1.0 : 2.0)
        tonal.evidence += (target - tonal.evidence) * (1 - exp(-dt / tau))
        let v = tonal.evidence
        let toVocal = smooth01((v - 0.25) / 0.45)
        let toPad = 1 - smooth01((v - 0.2) / 0.4)
        tonal.vocalPower = leadPower * toVocal * steadyShare
        // The lead's partials join the pad only once they are mature, steady,
        // not moving and held for 0.4-0.8 s: an unclassified new syllable
        // (a first sung note before its vibrato) lights neither class.
        let held = smooth01((tonal.trackAge - 0.4) / 0.4)
        tonal.padPower = bedPower + leadMature * toPad * (1 - moving) * held
        tonal.bassPower = bassPower

        carryPeaks(count: count, dt: dt)
    }

    /// The next pass matches against this pass's peaks plus peaks that went
    /// missing less than 45 ms ago (masked by a drum hit), merged by frequency.
    private func carryPeaks(count: Int, dt: Float) {
        let pk = peaks, prev = previous, nx = next
        let previousCount = tonal.previousCount
        var merged = 0, a = 0, b = 0
        while a < count || b < previousCount {
            let takeCurrent: Bool
            if b >= previousCount { takeCurrent = true }
            else if a >= count { takeCurrent = false }
            else { takeCurrent = pk.frequency[a] <= prev.frequency[b] }
            if takeCurrent {
                nx.frequency[merged] = pk.frequency[a]; nx.age[merged] = pk.age[a]; nx.missing[merged] = 0
                nx.centsMean[merged] = pk.centsMean[a]; nx.centsVar[merged] = pk.centsVar[a]
                nx.maxDb[merged] = pk.maxDb[a]; nx.sinceMax[merged] = pk.sinceMax[a]
                merged += 1; a += 1
            } else {
                let missing = prev.missing[b] + dt
                if !prev.matched[b], missing <= 0.045 {
                    nx.frequency[merged] = prev.frequency[b]; nx.age[merged] = prev.age[b]
                    nx.missing[merged] = missing
                    nx.centsMean[merged] = prev.centsMean[b]; nx.centsVar[merged] = prev.centsVar[b]
                    nx.maxDb[merged] = prev.maxDb[b]; nx.sinceMax[merged] = prev.sinceMax[b] + dt
                    merged += 1
                }
                b += 1
            }
            if merged >= Self.carryCapacity { break }
        }
        prev.copy(from: nx, count: merged)
        tonal.previousCount = merged
    }

    private func bassWeight(_ f: Float) -> Float {
        let lo = tuning.bassBand.lowerBound, hi = tuning.bassBand.upperBound, from = tuning.bassTaperFrom
        if f < lo || f > hi { return 0 }
        return f <= from ? 1 : maxf(0.25, 1 - 0.75 * (f - from) / (hi - from))
    }

    private struct Series {
        var f0: Float = 0
        var score: Float = 0
        var coverage: Float = 0
        var fundamental = false
        var odd = false
    }

    /// Harmonic-sum search over candidates derived from the strongest free
    /// stable peaks; the winner claims its partials (`owner`). `weighted`
    /// scales each partial by its lead weight (young or wobbling partials).
    private func bestSeries(lo: Float, hi: Float, requireFundamental: Bool, minHits: Int, stableAge: Float,
                            owner: UInt8, previous: Float, weighted: Bool, bassShaped: Bool) -> Series {
        // The stable, unclaimed peaks, packed: frequency, power, √power × weight,
        // weight, age, and the index to claim through.
        let pk = peaks, el = eligible
        let total = tonal.count
        var m = 0, i = 0
        while i < total {
            if pk.age[i] >= stableAge && pk.owner[i] == 0 {
                let w = weighted ? pk.leadWeight[i] : 1
                el.frequency[m] = pk.frequency[i]
                el.power[m] = pk.power[i]
                el.cents[m] = pk.power[i].squareRoot() * w
                el.leadWeight[m] = w
                el.age[m] = pk.age[i]
                el.index[m] = i
                m += 1
            }
            i += 1
        }
        var seedCount = 0, e = 0
        while e < m {
            defer { e += 1 }
            guard el.leadWeight[e] > 0.2 else { continue }
            if seedCount < Self.seedCapacity { seeds[seedCount] = e; seedCount += 1; continue }
            var weakest = 0, s = 1
            while s < Self.seedCapacity {
                if el.power[seeds[s]] * el.leadWeight[seeds[s]] < el.power[seeds[weakest]] * el.leadWeight[seeds[weakest]] {
                    weakest = s
                }
                s += 1
            }
            if el.power[e] * el.leadWeight[e] > el.power[seeds[weakest]] * el.leadWeight[seeds[weakest]] { seeds[weakest] = e }
        }
        guard seedCount > 0 else { return Series() }
        var best = Series()
        var s = 0
        while s < seedCount {
            let f = el.frequency[seeds[s]]
            var h = 1
            while h <= 6 {
                if let candidate = score(f / Float(h), count: m, lo: lo, hi: hi, requireFundamental: requireFundamental,
                                         minHits: minHits, previous: previous, bassShaped: bassShaped),
                   candidate.score > best.score { best = candidate }
                h += 1
            }
            s += 1
        }
        if previous > 0, let candidate = score(previous, count: m, lo: lo, hi: hi, requireFundamental: requireFundamental,
                                                 minHits: minHits, previous: previous, bassShaped: bassShaped),
           candidate.score > best.score { best = candidate }
        guard best.score > 0 else { return Series() }
        // Octave check: an octave-up winner that only explains the even
        // partials loses to the true f0 if that has its own fundamental.
        if let half = score(best.f0 / 2, count: m, lo: lo, hi: hi, requireFundamental: requireFundamental,
                            minHits: minHits, previous: previous, bassShaped: bassShaped),
           half.fundamental, half.odd, half.score >= 0.75 * best.score { best = half }
        let chosen = best.f0
        var num: Float = 0, den: Float = 0
        e = 0
        while e < m {
            defer { e += 1 }
            let r = el.frequency[e] / chosen
            let h = roundf(r)
            guard h >= 1, h <= 16, r >= h * Self.matchLow, r <= h * Self.matchHigh else { continue }
            pk.owner[el.index[e]] = owner
            num += el.power[e] * el.frequency[e] / h
            den += el.power[e]
        }
        if den > 0 { best.f0 = num / den }
        return best
    }

    /// One candidate f0 against the packed peaks.
    private func score(_ c: Float, count m: Int, lo: Float, hi: Float, requireFundamental: Bool, minHits: Int,
                       previous: Float, bassShaped: Bool) -> Series? {
        guard c >= lo, c <= hi else { return nil }
        let frequency = eligible.frequency, root = eligible.cents, powers = eligible.power
        let weight = eligible.leadWeight, age = eligible.age
        let low = Self.matchLow, high = Self.matchHigh
        var sum: Float = 0, hits: UInt32 = 0, power: Float = 0, weightedOrder: Float = 0, fundamentalAge: Float = 0
        var e = 0
        while e < m {
            let r = frequency[e] / c
            let h = roundf(r)
            if h >= 1, h <= 16, r >= h * low, r <= h * high {
                sum += root[e]
                power += powers[e]
                weightedOrder += powers[e] * h
                if h == 1, age[e] > fundamentalAge { fundamentalAge = age[e] }
                if weight[e] >= 0.3 { hits |= 1 << UInt32(Int(h) - 1) }
            }
            e += 1
        }
        // A bass note's energy sits in its first partials; a low male voice's
        // sits in formants around harmonics 3-8.
        if bassShaped, c > 100, power > 0, weightedOrder / power > 3.5 { return nil }
        // Above 110 Hz only a rhythmic (recently started) note is a bass note;
        // a held low chord tone belongs to the pad.
        if bassShaped, c > 110, fundamentalAge >= 0.6 { return nil }
        let hmax = max(1, min(6, Int(5000 / c)))
        let mask: UInt32 = (1 << UInt32(hmax)) - 1
        let coverage = Float((hits & mask).nonzeroBitCount) / Float(hmax)
        let fundamental = hits & 1 != 0
        guard hits.nonzeroBitCount >= minHits else { return nil }
        if requireFundamental && (!fundamental || coverage < 0.5) { return nil }
        var value = sum * (0.35 + 0.65 * coverage) * (fundamental ? 1 : 0.6)
        // A subharmonic candidate explains only even partials: no 3rd or 5th.
        let odd = (hmax >= 3 && hits & 0b100 != 0) || (hmax >= 5 && hits & 0b1_0000 != 0)
        if hmax >= 3, !odd { value *= 0.5 }
        if previous > 0, (1200 * log2(c / previous)).magnitude < 60 { value *= 1.3 }
        return Series(f0: c, score: value, coverage: coverage, fundamental: fundamental, odd: odd)
    }
}

// MARK: - Building blocks

// Non-generic, so Debug builds call them directly instead of through
// Comparable's witness table. Same semantics as Swift.max/min.
@inline(__always) private func maxf(_ a: Float, _ b: Float) -> Float { b >= a ? b : a }
@inline(__always) private func minf(_ a: Float, _ b: Float) -> Float { b < a ? b : a }
@inline(__always) private func clampf(_ x: Float, _ lo: Float, _ hi: Float) -> Float { minf(hi, maxf(lo, x)) }
@inline(__always) private func smooth01(_ x: Float) -> Float { let c = clampf(x, 0, 1); return c * c * (3 - 2 * c) }
@inline(__always) private func follow(_ y: Float, _ x: Float, _ dt: Float, _ attack: Float, _ release: Float) -> Float {
    y + (x - y) * (1 - exp(-dt / (x > y ? attack : release)))
}

/// Fractional-bin weights of a frequency region, optionally tapering to 0.25
/// above `taperFrom`. The weights live in the detector's storage.
private struct Region {
    var start = 0
    var count = 0
    var weights: UnsafeMutablePointer<Float>?

    init() {}

    init(_ r: ClosedRange<Float>, binHz: Float, binCount: Int, top: Float, weights: UnsafeMutablePointer<Float>,
         taperFrom: Float? = nil) {
        let lo = r.lowerBound, hi = min(r.upperBound, top)
        let first = max(1, Int(floor(lo / binHz)) - 1), last = min(binCount - 1, Int(ceil(hi / binHz)) + 1)
        guard hi > lo, first <= last else { return }
        start = first
        count = last - first + 1
        self.weights = weights
        for k in first...last {
            let f0 = (Float(k) - 0.5) * binHz, f1 = (Float(k) + 0.5) * binHz
            var w = max(0, min(f1, hi) - max(f0, lo)) / binHz
            let f = Float(k) * binHz
            if let taperFrom, f > taperFrom { w *= max(0.25, 1 - 0.75 * (f - taperFrom) / (hi - taperFrom)) }
            weights[k - first] = w
        }
    }

    /// Σ weight × v over the region.
    func sum(_ v: UnsafeMutablePointer<Float>) -> Float {
        guard count > 0, let weights else { return 0 }
        var s: Float = 0
        vDSP_dotpr(weights, 1, v + start, 1, &s, vDSP_Length(count))
        return s
    }
}

/// Adaptive threshold, hysteresis, refractory period and strength
/// normalisation for one transient class.
private struct OnsetTracker {
    static let history = 16
    var mean: Float = 0
    var dev: Float = 0.02
    var armed = true
    var last = -Double.infinity
    var topUpUntil = -Double.infinity
    var peak: Float = 0
    var weight: Float = 1
    /// Recent onset peaks; their 75th percentile is "a typical strong hit".
    let typical: UnsafeMutablePointer<Float>
    var typicalIndex = 0
    var pendingTypical = false
    /// The strength handed to a new onset; decays with the class's release.
    var drive: Float = 0

    init(typical: UnsafeMutablePointer<Float>) {
        self.typical = typical
        typical.update(repeating: 0.3, count: Self.history)
    }

    func threshold(_ k: Float, _ floor: Float) -> Float { maxf(floor, mean + k * dev) }
    func ready(_ time: Double, _ refractory: Float) -> Bool { armed && time - last >= Double(refractory) }

    mutating func fire(_ time: Double, odf: Float, weight: Float, topUp: Float) {
        armed = false
        last = time
        topUpUntil = time + Double(topUp)
        peak = odf
        self.weight = weight
        pendingTypical = true
        drive = maxf(drive, amplitude())
    }

    /// 0.3 + 0.7 × (peak / 75th percentile of recent peaks), times the
    /// loudness weight. Selection by counting, so nothing allocates.
    func amplitude() -> Float {
        var p75: Float = 0
        var i = 0
        while i < Self.history {
            let candidate = typical[i]
            var below = 0, j = 0
            while j < Self.history { if typical[j] < candidate { below += 1 }; j += 1 }
            if below <= 11, candidate > p75 { p75 = candidate }
            i += 1
        }
        return minf(1, (0.3 + 0.7 * minf(1.2, peak / maxf(0.12, p75))) * weight)
    }

    mutating func advance(_ time: Double, odf: Float, threshold: Float, rearm: Float, dt: Float, statsTau: Float) {
        if time <= topUpUntil {
            if odf > peak { peak = odf; drive = maxf(drive, amplitude()) }
        } else if pendingTypical {
            typical[typicalIndex] = peak
            typicalIndex = (typicalIndex + 1) % Self.history
            pendingTypical = false
        }
        if !armed, odf < rearm * threshold || time - last > 0.25 { armed = true }
        let a = 1 - exp(-dt / statsTau)
        mean += (odf - mean) * a
        dev += ((odf - mean).magnitude - dev) * a
    }

    mutating func decay(dt: Float, release: Float) { drive *= exp(-dt / release) }
}

/// Holds the bass level at its pre-kick value while a kick's low tail rings,
/// so a kick never reads as bass; lets go as soon as the low band behaves like
/// a sustained note (an 808, or a bass note under the kick).
private struct KickHold {
    var active = false
    var onset: Double = 0
    var before: Float = 0
    var energyBefore: Float = 0

    mutating func start(time: Double, before: Float, energyBefore: Float) {
        active = true; onset = time; self.before = before; self.energyBefore = energyBefore
    }

    /// A kick candidate turned out to be a bass note.
    mutating func cancel() { active = false }

    /// `input`: this pass's tonal bass power; `energy`/`slope`: the 35-220 Hz
    /// band energy and its dB/s over the last ~40 ms.
    mutating func apply(time: Double, slope: Float, energy: Float, input: Float) -> Float {
        guard active else { return input }
        let age = time - onset
        let sustained = age >= 0.06 && slope > -30                 // an 808 or a held bass note
        let merged = age >= 0.04 && energy <= 2 * energyBefore      // the tail fell to the pre-kick level
        if sustained || merged || age > 0.5 { active = false; return input }
        return minf(input, before)
    }
}

private struct PendingKick {
    var start: Double
    var centroid: Float
    var lastCentroid: Float
    var odf: Float
}

private struct TonalState {
    var count = 0
    var previousCount = 0
    var leadMissing: Float = 0
    var bassLineUntil = -Double.infinity
    var bassF0: Float = 0
    var leadF0: Float = 0
    var trackAge: Float = 0
    var coherentVar: Float = 0
    var evidence: Float = 0
    var vocalPower: Float = 0
    var padPower: Float = 0
    var bassPower: Float = 0
}

/// This pass's spectral peaks, one column per property. The series search
/// reuses the layout for its packed candidates (`cents` then holds
/// √power × weight, `leadWeight` the weight and `index` the peak).
private struct PeakTable {
    let frequency, cents, power, centsMean, centsVar, maxDb, sinceMax, age, slope, leadWeight, pitchStable:
        UnsafeMutablePointer<Float>
    let owner: UnsafeMutablePointer<UInt8>   // 0 bed, 1 bass series, 2 lead series
    let index: UnsafeMutablePointer<Int>

    init(capacity: Int) {
        func column() -> UnsafeMutablePointer<Float> {
            let column = UnsafeMutablePointer<Float>.allocate(capacity: capacity)
            column.initialize(repeating: 0, count: capacity)
            return column
        }
        frequency = column(); cents = column(); power = column(); centsMean = column(); centsVar = column()
        maxDb = column(); sinceMax = column(); age = column(); slope = column(); leadWeight = column()
        pitchStable = column()
        owner = .allocate(capacity: capacity)
        owner.initialize(repeating: 0, count: capacity)
        index = .allocate(capacity: capacity)
        index.initialize(repeating: 0, count: capacity)
    }

    func deallocate() {
        for column in [frequency, cents, power, centsMean, centsVar, maxDb, sinceMax, age, slope, leadWeight, pitchStable] {
            column.deallocate()
        }
        owner.deallocate()
        index.deallocate()
    }
}

/// Partials carried from one pass to the next.
private struct CarryTable {
    let frequency, age, missing, centsMean, centsVar, maxDb, sinceMax: UnsafeMutablePointer<Float>
    let matched: UnsafeMutablePointer<Bool>

    init(capacity: Int) {
        func column() -> UnsafeMutablePointer<Float> {
            let column = UnsafeMutablePointer<Float>.allocate(capacity: capacity)
            column.initialize(repeating: 0, count: capacity)
            return column
        }
        frequency = column(); age = column(); missing = column(); centsMean = column(); centsVar = column()
        maxDb = column(); sinceMax = column()
        matched = .allocate(capacity: capacity)
        matched.initialize(repeating: false, count: capacity)
    }

    /// Takes `other`'s first `count` partials, all unmatched.
    func copy(from other: CarryTable, count: Int) {
        guard count > 0 else { return }
        frequency.update(from: other.frequency, count: count)
        age.update(from: other.age, count: count)
        missing.update(from: other.missing, count: count)
        centsMean.update(from: other.centsMean, count: count)
        centsVar.update(from: other.centsVar, count: count)
        maxDb.update(from: other.maxDb, count: count)
        sinceMax.update(from: other.sinceMax, count: count)
        matched.update(repeating: false, count: count)
    }

    func deallocate() {
        for column in [frequency, age, missing, centsMean, centsVar, maxDb, sinceMax] { column.deallocate() }
        matched.deallocate()
    }
}
