// Deterministic synthetic music with ground truth, for the sound-type tests.
// Every instrument is procedural (sines and seeded, filtered noise), so no
// audio files or devices are involved. The instruments and arrangements are
// the ones the detector's constants were validated on; they are synthesised
// with Accelerate because the test target builds without optimisation; for
// the same reason the few per-sample loops are `while` loops over pointers
// (an unoptimised `for i in 0..<n` iterates through generic witness calls).

import Accelerate
import Foundation
@testable import VersoCustom

enum SoundClass: String, CaseIterable {
    case kick, snare, hat, bass, vocal, pad
    var isTransient: Bool { self == .kick || self == .snare || self == .hat }
}

struct SoundTruth {
    var onsets: [SoundClass: [Double]] = [:]
    var active: [SoundClass: [ClosedRange<Double>]] = [:]
    mutating func onset(_ c: SoundClass, _ t: Double) { onsets[c, default: []].append(t) }
    mutating func span(_ c: SoundClass, _ r: ClosedRange<Double>) { active[c, default: []].append(r) }
}

struct SoundFixture {
    let name: String
    let samples: [Float]
    let fs: Double
    let truth: SoundTruth

    var seconds: Double { Double(samples.count) / fs }

    /// The same music through a different master: scaled, then optionally
    /// soft-clipped by tanh(x·drive)/tanh(drive).
    func mastered(gain: Float, softClip drive: Float? = nil, name: String) -> SoundFixture {
        let out = Vec(samples)
        out.scale(gain)
        if let drive { out.softClip(drive) }
        return SoundFixture(name: name, samples: out.array, fs: fs, truth: truth)
    }

    /// The same music followed by digital silence.
    func appendingSilence(_ seconds: Double) -> SoundFixture {
        SoundFixture(name: name + "+silence", samples: samples + [Float](repeating: 0, count: Int(seconds * fs)),
                     fs: fs, truth: truth)
    }
}

struct SplitMix {
    var state: UInt64
    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }
    mutating func uniform() -> Float { Float(next() >> 40) / Float(1 << 24) }

    /// `count` uniform values in −1...1. The same sequence as `uniform()`,
    /// with the mixing written out so an unoptimised build stays fast.
    mutating func fill(_ p: UnsafeMutablePointer<Float>, count: Int) {
        var z0 = UInt(state)
        var i = 0
        while i < count {
            z0 &+= 0x9E37_79B9_7F4A_7C15
            var z = z0
            z = (z ^ (z &>> 30)) &* 0xBF58_476D_1CE4_E5B9
            z = (z ^ (z &>> 27)) &* 0x94D0_49BB_1331_11EB
            z ^= z &>> 31
            p[i] = Float(Int(bitPattern: z &>> 40)) * (2 / 16_777_216) - 1
            i += 1
        }
        state = UInt64(z0)
    }
}

/// A heap buffer of floats for in-place vector work.
final class Vec {
    let p: UnsafeMutablePointer<Float>
    let n: Int

    init(_ n: Int, _ value: Float = 0) {
        self.n = max(1, n)
        p = .allocate(capacity: self.n)
        var fill = value
        vDSP_vfill(&fill, p, 1, vDSP_Length(self.n))
    }

    convenience init(_ values: [Float]) {
        self.init(values.count)
        values.withUnsafeBufferPointer { _ = memcpy(p, $0.baseAddress!, values.count * MemoryLayout<Float>.stride) }
    }

    deinit { p.deallocate() }

    var count: vDSP_Length { vDSP_Length(n) }
    var array: [Float] {
        [Float](unsafeUninitializedCapacity: n) { buffer, initialized in
            _ = memcpy(buffer.baseAddress!, p, n * MemoryLayout<Float>.stride)
            initialized = n
        }
    }

    /// Seconds since the start, for `n` samples at `fs`, from `offset` seconds.
    static func time(_ n: Int, fs: Double, from offset: Double = 0) -> Vec {
        let v = Vec(n)
        var a = Float(offset), b = Float(1 / fs)
        vDSP_vramp(&a, &b, v.p, 1, v.count)
        return v
    }

    /// p = exp(scale·p)
    @discardableResult func exp(_ scale: Float) -> Vec {
        self.scale(scale)
        var c = Int32(n)
        vvexpf(p, p, &c)
        return self
    }

    /// p = min(1, max(0, p·scale + offset))
    @discardableResult func clamped(_ scale: Float, _ offset: Float = 0) -> Vec {
        var s = scale, o = offset, lo: Float = 0, hi: Float = 1
        vDSP_vsmsa(p, 1, &s, &o, p, 1, count)
        vDSP_vclip(p, 1, &lo, &hi, p, 1, count)
        return self
    }

    @discardableResult func multiply(_ other: Vec) -> Vec {
        vDSP_vmul(p, 1, other.p, 1, p, 1, vDSP_Length(min(n, other.n)))
        return self
    }

    /// p += other × gain
    func add(_ other: Vec, gain: Float = 1, at offset: Int = 0) {
        let m = min(other.n, n - offset)
        guard offset >= 0, m > 0 else { return }
        var g = gain
        vDSP_vsma(other.p, 1, &g, p + offset, 1, p + offset, 1, vDSP_Length(m))
    }

    func scale(_ gain: Float) {
        var g = gain
        vDSP_vsmul(p, 1, &g, p, 1, count)
    }

    func softClip(_ drive: Float) {
        scale(drive)
        var c = Int32(n)
        vvtanhf(p, p, &c)
        scale(1 / tanh(drive))
    }
}

/// The same for doubles, where phases need the precision.
final class VecD {
    let p: UnsafeMutablePointer<Double>
    let n: Int

    /// p[i] = start + i·step
    init(_ n: Int, ramp start: Double, _ step: Double) {
        self.n = max(1, n)
        p = .allocate(capacity: self.n)
        var a = start, b = step
        vDSP_vrampD(&a, &b, p, 1, vDSP_Length(self.n))
    }

    deinit { p.deallocate() }

    /// sin and cos of p, in single precision.
    func sincos() -> (sin: Vec, cos: Vec) {
        let s = VecD(n, ramp: 0, 0), c = VecD(n, ramp: 0, 0)
        var count = Int32(n)
        vvsincos(s.p, c.p, p, &count)
        let sv = Vec(n), cv = Vec(n)
        vDSP_vdpsp(s.p, 1, sv.p, 1, vDSP_Length(n))
        vDSP_vdpsp(c.p, 1, cv.p, 1, vDSP_Length(n))
        return (sv, cv)
    }

    /// sin and cos of 2π·p for p in cycles; only the fraction matters.
    func cycles() -> (sin: Vec, cos: Vec) {
        var twoPi = 2 * Double.pi
        vDSP_vfracD(p, 1, p, 1, vDSP_Length(n))
        vDSP_vsmulD(p, 1, &twoPi, p, 1, vDSP_Length(n))
        return sincos()
    }
}

/// RBJ cookbook biquads run by vDSP; the state carries across calls.
final class Filter {
    enum Kind { case lowpass, highpass, bandpass }
    private let setup: vDSP_biquad_Setup
    private let delay: UnsafeMutablePointer<Float>

    init(_ sections: [(kind: Kind, frequency: Double, q: Double)], fs: Double) {
        var coefficients: [Double] = []
        for section in sections {
            let w = 2 * Double.pi * min(section.frequency, fs * 0.49) / fs
            let c = cos(w), alpha = sin(w) / (2 * section.q)
            var b0 = 0.0, b1 = 0.0, b2 = 0.0
            switch section.kind {
            case .lowpass: b0 = (1 - c) / 2; b1 = 1 - c; b2 = (1 - c) / 2
            case .highpass: b0 = (1 + c) / 2; b1 = -(1 + c); b2 = (1 + c) / 2
            case .bandpass: b0 = alpha; b1 = 0; b2 = -alpha
            }
            let a0 = 1 + alpha
            coefficients += [b0 / a0, b1 / a0, b2 / a0, -2 * c / a0, (1 - alpha) / a0]
        }
        setup = vDSP_biquad_CreateSetup(coefficients, vDSP_Length(sections.count))!
        delay = .allocate(capacity: 2 * sections.count + 2)
        vDSP_vclr(delay, 1, vDSP_Length(2 * sections.count + 2))
    }

    /// One pole: y = b·x + a·y[−1].
    init(pole a: Double, gain b: Double) {
        setup = vDSP_biquad_CreateSetup([b, 0, 0, -a, 0], 1)!
        delay = .allocate(capacity: 4)
        vDSP_vclr(delay, 1, 4)
    }

    deinit {
        vDSP_biquad_DestroySetup(setup)
        delay.deallocate()
    }

    func run(_ x: Vec) -> Vec {
        let y = Vec(x.n)
        vDSP_biquad(setup, delay, x.p, 1, y.p, 1, x.count)
        return y
    }
}

/// Adds instruments into a sample buffer and records what it added.
final class SoundSynth {
    let fs: Double
    private let out: Vec
    private var truth = SoundTruth()
    private var rng: SplitMix

    init(seconds: Double, fs: Double, seed: UInt64) {
        self.fs = fs
        out = Vec(Int(seconds * fs))
        rng = SplitMix(state: seed)
    }

    func fixture(_ name: String) -> SoundFixture { SoundFixture(name: name, samples: out.array, fs: fs, truth: truth) }

    private func index(_ t: Double) -> Int { Int((t * fs).rounded()) }
    private func length(at start: Int, _ seconds: Double) -> Int { max(0, min(Int(seconds * fs), out.n - start)) }
    private func time(_ n: Int, from offset: Double = 0) -> Vec { Vec.time(n, fs: fs, from: offset) }

    /// Uniform noise in −1...1.
    private func noise(_ n: Int) -> Vec {
        let v = Vec(n)
        rng.fill(v.p, count: v.n)
        return v
    }

    /// The first `m` of `n` noise values; the generator moves on by all `n`.
    private func noise(_ n: Int, keeping m: Int) -> Vec {
        let all = noise(n), v = Vec(m)
        v.p.update(from: all.p, count: m)
        return v
    }

    /// One period of Σ amplitudes[h − 1]·sin(2π·h·j/L), plus a guard sample.
    private static let tableLength = 4096
    private func wavetable(_ amplitudes: [Float]) -> Vec {
        let length = Self.tableLength
        let table = Vec(length + 1), angle = Vec.time(length + 1, fs: Double(length) / (2 * .pi)), term = Vec(length + 1)
        for (h, amplitude) in amplitudes.enumerated() {
            var scale = Float(h + 1), count = Int32(length + 1)
            vDSP_vsmul(angle.p, 1, &scale, term.p, 1, term.count)
            vvsinf(term.p, term.p, &count)
            table.add(term, gain: amplitude)
        }
        table.p[length] = table.p[0]
        return table
    }

    /// `table` played at `frequency`, from `phase` radians at sample −1.
    private func play(_ table: Vec, frequency: Double, phase: Double, count n: Int) -> Vec {
        let cycles = VecD(n, ramp: phase / (2 * .pi) + frequency / fs, frequency / fs)
        var length = Double(Self.tableLength)
        vDSP_vfracD(cycles.p, 1, cycles.p, 1, vDSP_Length(n))
        vDSP_vsmulD(cycles.p, 1, &length, cycles.p, 1, vDSP_Length(n))
        let index = Vec(n), y = Vec(n)
        vDSP_vdpsp(cycles.p, 1, index.p, 1, vDSP_Length(n))
        vDSP_vlint(table.p, index.p, 1, y.p, 1, vDSP_Length(n), vDSP_Length(Self.tableLength + 1))
        return y
    }

    /// Σ_h amplitude_h(i)·sin(h·φ_i), from sin φ and cos φ by the recurrence
    /// sin((h+1)φ) = 2cos φ·sin(hφ) − sin((h−1)φ). `amplitude` fills harmonic
    /// h's per-sample amplitude (h is 1-based).
    private func harmonics(_ base: (sin: Vec, cos: Vec), count h: Int, amplitude: (Int, Vec) -> Void) -> Vec {
        let n = base.sin.n
        let y = Vec(n), a = Vec(n), twoCos = Vec(n)
        var two: Float = 2
        vDSP_vsmul(base.cos.p, 1, &two, twoCos.p, 1, twoCos.count)
        var below = Vec(n), current = Vec(n)
        current.p.update(from: base.sin.p, count: n)
        for harmonic in 1...max(1, h) {
            if harmonic > 1 {
                vDSP_vmsb(twoCos.p, 1, current.p, 1, below.p, 1, below.p, 1, below.count)   // 2cos·current − below
                swap(&below, &current)
            }
            amplitude(harmonic, a)
            vDSP_vma(current.p, 1, a.p, 1, y.p, 1, y.p, 1, y.count)
        }
        return y
    }

    // MARK: Drums

    enum KickKind { case acoustic, electronic, eightOhEight }

    func kick(_ t0: Double, gain: Float, kind: KickKind = .electronic, click: Float = 0.25) {
        truth.onset(.kick, t0)
        let (f1, f2, sweep, decay, seconds): (Double, Double, Double, Double, Double) =
            switch kind {
            case .acoustic: (140, 58, 0.02, 0.09, 0.5)
            case .electronic: (160, 52, 0.03, 0.14, 0.6)
            case .eightOhEight: (95, 49, 0.04, 0.55, 1.4)
            }
        let start = index(t0), n = length(at: start, seconds)
        guard n > 0 else { return }
        // Phase = 2π/fs·Σ_{j≤i} f(j/fs) for f(t) = f2 + (f1 − f2)·e^(−t/sweep):
        // f2·(i + 1) + k·(1 − r^(i+1)) with r = e^(−1/(fs·sweep)), k = (f1 − f2)/(1 − r).
        let r = Foundation.exp(-1 / (fs * sweep)), k = (f1 - f2) / (1 - r)
        let steps = VecD(n, ramp: 1, 1), geometric = VecD(n, ramp: 1, 1)
        var scale = -1 / (fs * sweep), count = Int32(n), minusK = -k, plusK = k, f2v = f2, radians = 2 * Double.pi / fs
        vDSP_vsmulD(geometric.p, 1, &scale, geometric.p, 1, vDSP_Length(n))
        vvexp(geometric.p, geometric.p, &count)
        vDSP_vsmsaD(geometric.p, 1, &minusK, &plusK, geometric.p, 1, vDSP_Length(n))
        vDSP_vsmaD(steps.p, 1, &f2v, geometric.p, 1, steps.p, 1, vDSP_Length(n))
        vDSP_vsmulD(steps.p, 1, &radians, steps.p, 1, vDSP_Length(n))
        let y = steps.sincos().sin
        y.multiply(time(n).clamped(1 / 0.0015))
        y.multiply(time(n).exp(Float(-1 / decay)))
        if kind == .eightOhEight { y.softClip(1.6) }
        out.add(y, gain: gain, at: start)
        // The beater click: band-passed noise with a 1.5 ms decay, gone in 30 ms.
        // The noise is drawn for every sample of the kick, as the validated
        // realisation draws it, so every later instrument gets the same noise.
        let m = min(n, Int(0.03 * fs))
        let c = Filter([(.bandpass, 3500, 0.7)], fs: fs).run(noise(n, keeping: m))
        c.multiply(time(m).exp(-1 / 0.0015))
        out.add(c, gain: gain * click * 3, at: start)
    }

    func snare(_ t0: Double, gain: Float) {
        truth.onset(.snare, t0)
        let start = index(t0), n = length(at: start, 0.45)
        guard n > 0 else { return }
        let body = VecD(n, ramp: 190 / fs, 190 / fs).cycles().sin.multiply(time(n).exp(-1 / 0.06))
        let second = VecD(n, ramp: 335 / fs, 335 / fs).cycles().sin.multiply(time(n).exp(-1 / 0.04))
        var a: Float = 0.55, b: Float = 0.3
        vDSP_vsmsma(body.p, 1, &a, second.p, 1, &b, body.p, 1, body.count)
        let wires = Filter([(.highpass, 1500, 0.707), (.lowpass, 9000, 0.707)], fs: fs).run(noise(n))
        body.add(wires.multiply(time(n).exp(-1 / 0.11)), gain: 1.6)
        body.multiply(time(n).clamped(1 / 0.0006))
        out.add(body, gain: gain, at: start)
    }

    func clap(_ t0: Double, gain: Float) {
        truth.onset(.snare, t0)
        let start = index(t0), n = length(at: start, 0.35)
        guard n > 0 else { return }
        // Three 4 ms bursts and a 70 ms tail.
        let envelope = Vec(n)
        for (offset, tau, weight) in [(0.0, 0.004, Float(1)), (0.009, 0.004, 1), (0.019, 0.004, 1), (0.02, 0.07, 0.6)] {
            let from = min(n, Int((offset * fs).rounded(.up)))
            guard from < n else { continue }
            envelope.add(time(n - from, from: Double(from) / fs - offset).exp(Float(-1 / tau)), gain: weight, at: from)
        }
        let x = Filter([(.highpass, 900, 0.707), (.lowpass, 4500, 0.707)], fs: fs).run(noise(n))
        out.add(x.multiply(envelope), gain: gain * 1.4, at: start)
    }

    func hat(_ t0: Double, gain: Float, open: Bool = false) {
        truth.onset(.hat, t0)
        let start = index(t0), n = length(at: start, open ? 0.9 : 0.2)
        guard n > 0 else { return }
        let x = Filter([(.highpass, 7000, 0.707), (.highpass, 7000, 0.707)], fs: fs).run(noise(n))
        x.add(Filter([(.bandpass, 10_500, 1.2)], fs: fs).run(x), gain: 1.5)
        x.multiply(time(n).clamped(1 / 0.0003))
        x.multiply(time(n).exp(Float(-1 / (open ? 0.22 : 0.028))))
        out.add(x, gain: gain, at: start)
    }

    // MARK: Tonal instruments

    /// Band-limited saw through a gentle low-pass, sharp attack.
    func bassNote(_ t0: Double, duration: Double, frequency: Double, gain: Float, pluck: Bool = false) {
        truth.span(.bass, t0...(t0 + duration))
        let start = index(t0), n = length(at: start, duration + 0.03)
        let count = max(1, Int(2500 / frequency))
        let phase = Double(rng.uniform()) * 2 * .pi
        guard n > 0 else { return }
        let y: Vec
        if pluck {
            // The cutoff falls from 1550 to 450 Hz, so amplitudes change per sample.
            let inverse = time(n).exp(-1 / 0.12)
            var a: Float = 1100, b: Float = 450, c = Int32(n)
            vDSP_vsmsa(inverse.p, 1, &a, &b, inverse.p, 1, inverse.count)
            vDSP_vsq(inverse.p, 1, inverse.p, 1, inverse.count)
            vDSP_vsq(inverse.p, 1, inverse.p, 1, inverse.count)
            vvrecf(inverse.p, inverse.p, &c)                                    // cutoff^−4
            let base = VecD(n, ramp: phase / (2 * .pi) + frequency / fs, frequency / fs).cycles()
            y = harmonics(base, count: count) { h, amplitude in
                var f4 = Float(pow(Double(h) * frequency, 4)), one: Float = 1, length = Int32(n)
                vDSP_vsmsa(inverse.p, 1, &f4, &one, amplitude.p, 1, amplitude.count)
                vvrsqrtf(amplitude.p, amplitude.p, &length)
                amplitude.scale(1 / Float(h))
            }
        } else {
            let amplitudes = (1...count).map { h in Float(1 / Double(h) / sqrt(1 + pow(Double(h) * frequency / 700, 4))) }
            y = play(wavetable(amplitudes), frequency: frequency, phase: phase, count: n)
        }
        // 4 ms attack, a slow (or plucked) decay, a 25 ms release.
        let envelope = time(n).clamped(1 / 0.004).multiply(time(n).exp(pluck ? -1 / 0.4 : -1 / 3))
        let releaseFrom = min(n, Int((duration * fs).rounded(.down)) + 1)
        if releaseFrom < n {
            let tail = time(n - releaseFrom, from: Double(releaseFrom) / fs - duration).clamped(-1 / 0.025, 1)
            vDSP_vmul(envelope.p + releaseFrom, 1, tail.p, 1, envelope.p + releaseFrom, 1, tail.count)
        }
        out.add(y.multiply(envelope), gain: gain, at: start)
    }

    struct Syllable { var start: Double; var duration: Double; var midi: Double; var vowel: Int }
    static let vowels: [[(Double, Double, Double)]] = [  // (F, bandwidth, gain)
        [(800, 90, 1), (1150, 110, 0.6), (2900, 160, 0.25)],   // a
        [(300, 60, 1), (2300, 130, 0.45), (3000, 170, 0.3)],   // i
        [(350, 70, 1), (600, 90, 0.7), (2700, 160, 0.15)],     // u
        [(450, 70, 1), (2000, 120, 0.5), (2550, 150, 0.3)],    // e
        [(500, 80, 1), (850, 100, 0.65), (2800, 160, 0.2)],    // o
    ]

    /// Additive sung voice: f0 glides between notes, delayed vibrato, jitter,
    /// formant-shaped harmonics and syllabic envelopes. Optional consonants.
    func vocal(_ syllables: [Syllable], gain: Float, consonants: Bool = false, vibrato: Double = 35) {
        guard let first = syllables.first, let last = syllables.last else { return }
        let t0 = first.start, t1 = last.start + last.duration
        let start = index(t0), n = length(at: start, t1 - t0 + 0.08)
        guard n > 0 else { return }
        for s in syllables { truth.span(.vocal, s.start...(s.start + s.duration)) }
        func hz(_ midi: Double) -> Double { 440 * pow(2, (midi - 69) / 12) }
        // Syllable s sounds from sample bounds[s] (its start) to bounds[s + 1].
        let bounds = syllables.indices.map { s in s == 0 ? 0 : min(n, Int(((syllables[s].start - t0) * fs).rounded(.up))) } + [n]
        // Per syllable: the glide from the previous note, the vibrato depth
        // (delayed 0.18 s) and the envelope (35 ms attack, 50 ms release,
        // smoothstepped).
        let base = Vec(n), depth = Vec(n), envelope = Vec(n)
        for s in syllables.indices where bounds[s] < bounds[s + 1] {
            let from = bounds[s], m = bounds[s + 1] - from, syllable = syllables[s]
            let local = t0 + Double(from) / fs - syllable.start
            let target = hz(syllable.midi)
            if s == 0 {
                var f = Float(target)
                vDSP_vfill(&f, base.p + from, 1, vDSP_Length(m))
            } else {
                let previous = hz(syllables[s - 1].midi)
                let glide = time(m, from: local).clamped(1 / 0.07).exp(Float(log(target / previous)))
                glide.scale(Float(previous))
                (base.p + from).update(from: glide.p, count: m)
            }
            let vibratoDepth = time(m, from: local).clamped(1 / 0.2, -0.18 / 0.2)
            vibratoDepth.scale(Float(vibrato))
            (depth.p + from).update(from: vibratoDepth.p, count: m)
            let e = time(m, from: local).clamped(1 / 0.035)
                .multiply(time(m, from: local).clamped(-1 / 0.05, Float(1 + syllable.duration / 0.05)))
            let smooth = Vec(m)
            vDSP_vsq(e.p, 1, smooth.p, 1, smooth.count)
            var minusTwo: Float = -2, three: Float = 3
            vDSP_vsmsa(e.p, 1, &minusTwo, &three, e.p, 1, e.count)
            vDSP_vmul(smooth.p, 1, e.p, 1, envelope.p + from, 1, e.count)
        }
        // cents = depth·sin(2π·5.6·t) + 3·tanh(jitter/50), the jitter a leaky random walk.
        let cents = VecD(n, ramp: 5.6 * t0, 5.6 / fs).cycles().sin.multiply(depth)
        let jitter = Filter(pole: 0.998, gain: 0.6).run(noise(n))
        jitter.scale(1 / 50)
        var c = Int32(n)
        vvtanhf(jitter.p, jitter.p, &c)
        cents.add(jitter, gain: 3)
        let f0 = base.multiply(cents.exp(Float(log(2.0) / 1200)))
        // The phase is the running sum of 2π·f0/fs (vDSP_vrsumD skips its first
        // element, so the sum runs over [0, f0...] and the phase is its tail).
        let increments = VecD(n + 1, ramp: 0, 0), sums = VecD(n + 1, ramp: 0, 0)
        vDSP_vspdp(f0.p, 1, increments.p + 1, 1, vDSP_Length(n))
        var radians = 2 * Double.pi / fs
        vDSP_vrsumD(increments.p, 1, &radians, sums.p, 1, vDSP_Length(n + 1))
        let phase = VecD(n, ramp: 0, 0)
        phase.p.update(from: sums.p + 1, count: n)
        // Formant gains, held for 32 samples at a time as each block's first
        // sample's f0 and vowel give them.
        let harmonicCount = 39, blocks = (n + 31) / 32
        let blockF0 = Vec(blocks)
        var one: Float = 1
        vDSP_vsmul(f0.p, 32, &one, blockF0.p, 1, blockF0.count)
        let gains = (0..<harmonicCount).map { _ in Vec(blocks + 1) }
        for s in syllables.indices {
            // The blocks whose first sample lies in syllable s.
            let b = (bounds[s] + 31) / 32, e = min(blocks, (bounds[s + 1] + 31) / 32)
            guard b < e else { continue }
            let m = e - b, x = Vec(m)
            for h in 1...harmonicCount {
                let g = gains[h - 1].p + b
                var floor: Float = 0.02
                vDSP_vfill(&floor, g, 1, vDSP_Length(m))
                for (F, B, a) in Self.vowels[syllables[s].vowel] {
                    var harmonic = Float(h), minusF = Float(-F), scale = Float(2 / B), unit: Float = 1, weight = Float(a)
                    vDSP_vsmsa(blockF0.p + b, 1, &harmonic, &minusF, x.p, 1, x.count)   // h·f0 − F
                    vDSP_vsmul(x.p, 1, &scale, x.p, 1, x.count)
                    vDSP_vsq(x.p, 1, x.p, 1, x.count)
                    vDSP_vsadd(x.p, 1, &unit, x.p, 1, x.count)
                    vDSP_svdiv(&weight, x.p, 1, x.p, 1, x.count)                         // a / (1 + x²)
                    vDSP_vadd(g, 1, x.p, 1, g, 1, vDSP_Length(m))
                }
                var falloff = Float(1 / pow(Double(h), 0.7))
                vDSP_vsmul(g, 1, &falloff, g, 1, vDSP_Length(m))
                let limit = 6000 / Float(h)
                var k = 0
                while k < m { if blockF0.p[b + k] >= limit { g[k] = 0 }; k += 1 }
            }
        }
        let blockIndex = Vec.time(n, fs: 32)   // i / 32
        var c32 = Int32(n)
        vvfloorf(blockIndex.p, blockIndex.p, &c32)
        let y = harmonics(phase.sincos(), count: harmonicCount) { h, amplitude in
            vDSP_vlint(gains[h - 1].p, blockIndex.p, 1, amplitude.p, 1, amplitude.count, vDSP_Length(blocks + 1))
        }
        out.add(y.multiply(envelope), gain: gain * 0.5, at: start)
        guard consonants else { return }
        let sibilance = Filter([(.highpass, 4500, 0.707), (.bandpass, 7000, 0.9)], fs: fs)
        for (k, s) in syllables.enumerated() where k % 3 == 1 {
            // A sibilant just before the syllable ("s"), soft attack.
            let at = index(s.start - 0.11), m = Int(0.12 * fs)
            let x = sibilance.run(noise(m)).multiply(time(m).clamped(1 / 0.02))
            x.multiply(time(m).clamped(-1 / 0.04, 1 + 0.08 / 0.04))
            out.add(x, gain: gain * 0.22, at: at)
        }
        let plosive = Filter([(.bandpass, 4000, 0.8)], fs: fs)
        for (k, s) in syllables.enumerated() where k % 3 == 2 {
            // A plosive "t": a 15 ms burst.
            let at = index(s.start - 0.02), m = Int(0.015 * fs)
            out.add(plosive.run(noise(m)).multiply(time(m).exp(-1 / 0.005)), gain: gain * 0.3, at: at)
        }
    }

    /// Supersaw-style pad chord: three detuned voices per note, slow attack.
    func pad(_ t0: Double, duration: Double, midis: [Double], gain: Float, attack: Double = 0.7, release: Double = 1.0) {
        truth.span(.pad, (t0 + 0.3)...(t0 + duration))
        let start = index(t0), n = length(at: start, duration + release)
        var voices: [(f: Double, phase: Double)] = []
        for m in midis {
            let f = 440 * pow(2, (m - 69) / 12)
            for detune in [-8.0, 0, 7] { voices.append((f * pow(2, detune / 1200), Double(rng.uniform()) * 2 * .pi)) }
        }
        guard n > 0 else { return }
        let y = Vec(n)
        for voice in voices {
            let amplitudes = (1...max(1, Int(5000 / voice.f))).map { h in
                Float(1 / Double(h) / sqrt(1 + pow(Double(h) * voice.f / 2000, 2)))
            }
            y.add(play(wavetable(amplitudes), frequency: voice.f, phase: voice.phase, count: n))
        }
        // A raised-cosine attack, a linear release and a slow ±1.5 dB swell.
        let envelope = Vec(n, 1)
        let rise = min(n, Int((attack * fs).rounded(.up)))
        if rise > 0 {
            // 0.5 − 0.5·cos(π·t/attack); cos(x) = sin(x + π/2).
            let curve = VecD(rise, ramp: 0.25, 1 / (2 * attack * fs)).cycles().sin
            var minusHalf: Float = -0.5, half: Float = 0.5
            vDSP_vsmsa(curve.p, 1, &minusHalf, &half, envelope.p, 1, curve.count)
        }
        let releaseFrom = min(n, Int((duration * fs).rounded(.down)) + 1)
        if releaseFrom < n {
            let tail = time(n - releaseFrom, from: Double(releaseFrom) / fs - duration).clamped(Float(-1 / release), 1)
            vDSP_vmul(envelope.p + releaseFrom, 1, tail.p, 1, envelope.p + releaseFrom, 1, tail.count)
        }
        let swell = VecD(n, ramp: 0.15 * t0, 0.15 / fs).cycles().sin
        var depth: Float = 0.17, unit: Float = 1
        vDSP_vsmsa(swell.p, 1, &depth, &unit, swell.p, 1, swell.count)
        out.add(y.multiply(envelope.multiply(swell)), gain: gain, at: start)
    }

    func noise(_ t0: Double, duration: Double, gain: Float, pink: Bool) {
        let start = index(t0), n = length(at: start, duration)
        guard n > 0 else { return }
        let w = noise(n)
        guard pink else { out.add(w, gain: gain, at: start); return }
        // Paul Kellet's economy pink filter: three one-pole stages plus the input.
        let y = Vec(n)
        for (a, b) in [(0.99765, 0.0990460), (0.96300, 0.2965164), (0.57000, 1.0526913)] {
            y.add(Filter(pole: a, gain: b).run(w))
        }
        y.add(w, gain: 0.1848)
        out.add(y, gain: gain * 0.25, at: start)
    }
}

// MARK: - The fixture set

enum SoundFixtures {
    static let bpm = 120.0
    static var beat: Double { 60 / bpm }

    static func kicks(fs: Double, kind: SoundSynth.KickKind = .electronic) -> SoundFixture {
        let s = SoundSynth(seconds: 10, fs: fs, seed: 11)
        var t = 0.5, k = 0
        while t < 9.6 {
            s.kick(t, gain: 0.55 * Float([1.0, 0.75, 0.9, 0.65][k % 4]), kind: kind)
            t += beat; k += 1
        }
        return s.fixture(kind == .eightOhEight ? "808" : "kick")
    }

    static func hats(fs: Double) -> SoundFixture {
        let s = SoundSynth(seconds: 10, fs: fs, seed: 12)
        var t = 0.5, k = 0
        while t < 9.6 {
            s.hat(t, gain: 0.16 * Float(k % 4 == 0 ? 1 : (k % 2 == 0 ? 0.7 : 0.5)), open: k % 32 == 30)
            t += beat / 4; k += 1
        }
        return s.fixture("hats")
    }

    static func snares(fs: Double) -> SoundFixture {
        let s = SoundSynth(seconds: 10, fs: fs, seed: 13)
        var t = 0.5 + beat, k = 0
        while t < 9.6 {
            if k % 4 == 3 { s.clap(t, gain: 0.35) } else { s.snare(t, gain: 0.32) }
            t += 2 * beat; k += 1
        }
        return s.fixture("snare")
    }

    static func melody() -> [SoundSynth.Syllable] {
        // Two phrases; syllables 0.18-0.75 s with small gaps and legato runs.
        let notes: [(Double, Double, Double, Int)] = [
            (0.6, 0.30, 69, 0), (0.95, 0.25, 71, 3), (1.25, 0.55, 72, 4), (1.85, 0.20, 71, 1), (2.08, 0.75, 69, 0),
            (3.0, 0.28, 64, 2), (3.32, 0.28, 67, 0), (3.62, 0.62, 69, 3), (4.3, 0.22, 67, 4), (4.55, 0.9, 64, 0),
            (5.8, 0.25, 72, 1), (6.1, 0.25, 74, 0), (6.4, 0.6, 76, 4), (7.05, 0.3, 74, 3), (7.4, 1.1, 72, 0),
        ]
        return notes.map { .init(start: $0.0, duration: $0.1, midi: $0.2, vowel: $0.3) }
    }

    static func vocal(fs: Double, consonants: Bool) -> SoundFixture {
        let s = SoundSynth(seconds: 9.5, fs: fs, seed: 14)
        s.vocal(melody(), gain: 0.30, consonants: consonants)
        return s.fixture(consonants ? "vocal+consonants" : "vocal")
    }

    static let chords: [[Double]] = [[57, 60, 64, 67], [53, 57, 60, 64], [48, 55, 60, 64], [55, 59, 62, 67]]

    static func pads(fs: Double) -> SoundFixture {
        let s = SoundSynth(seconds: 11, fs: fs, seed: 15)
        for (i, c) in chords.enumerated() { s.pad(0.5 + Double(i) * 2.5, duration: 2.6, midis: c, gain: 0.018) }
        return s.fixture("pad")
    }

    static func bassline(fs: Double) -> SoundFixture {
        let s = SoundSynth(seconds: 10, fs: fs, seed: 16)
        let roots: [Double] = [33, 29, 36, 31]  // A1 F1 C2 G1: 55, 43.7, 65.4, 49 Hz
        var t = 0.5, k = 0
        while t < 6.4 {  // staccato-ish 8ths with rests
            let midi = roots[(k / 8) % 4] + (k % 8 == 6 ? 12 : 0)
            if k % 8 != 3 {
                s.bassNote(t, duration: beat / 2 * 0.8, frequency: 440 * pow(2, (midi - 69) / 12), gain: 0.22,
                           pluck: k % 16 >= 8)
            }
            t += beat / 2; k += 1
        }
        // Legato whole notes.
        for (i, m) in [33.0, 29, 31].enumerated() {
            s.bassNote(6.5 + Double(i) * 1.0, duration: 0.98, frequency: 440 * pow(2, (m - 69) / 12), gain: 0.22)
        }
        return s.fixture("bass")
    }

    /// A 16.5-second arrangement: four-on-the-floor kick, snare/clap on 2 and
    /// 4, 8th/16th hats with open hats, bass line, pad chords, sung phrases
    /// with consonants. Stem RMS relative to the mix: kick −4.5, bass −4.8,
    /// vocal −7, snare −12, pad −14, hats −22 dB, a typical pop balance.
    static func mix(fs: Double, seed: UInt64 = 21) -> SoundFixture {
        let s = SoundSynth(seconds: 16.5, fs: fs, seed: seed)
        let bar = 4 * beat
        for b in 0..<8 {
            let t0 = 0.25 + Double(b) * bar
            for q in 0..<4 {
                let tq = t0 + Double(q) * beat
                if !(b == 4 && q >= 2) { s.kick(tq, gain: 0.5 * Float(q % 2 == 0 ? 1 : 0.85)) }
                if q == 1 || q == 3 {
                    if b % 2 == 1 && q == 3 { s.clap(tq, gain: 0.42) } else { s.snare(tq, gain: 0.40) }
                }
            }
            let sixteenths = (b == 2 || b == 3 || b == 6)
            for e in 0..<(sixteenths ? 16 : 8) {
                let te = t0 + Double(e) * (sixteenths ? beat / 4 : beat / 2) + (sixteenths ? 0 : beat / 4)
                s.hat(te, gain: 0.07 * Float(e % 2 == 0 ? 1 : 0.75), open: !sixteenths && e == 7 && b % 2 == 1)
            }
            let root = [33.0, 29, 36, 31][b % 4]
            for e in 0..<8 where e != 3 {
                let te = t0 + Double(e) * beat / 2
                s.bassNote(te, duration: beat / 2 * 0.85, frequency: 440 * pow(2, (root + (e == 6 ? 12 : 0) - 69) / 12),
                           gain: 0.16)
            }
        }
        for (i, c) in chords.enumerated() { s.pad(0.25 + Double(i) * 2 * bar, duration: 2 * bar, midis: c, gain: 0.014) }
        let phrase = melody().map {
            SoundSynth.Syllable(start: $0.start + 3.6, duration: $0.duration, midi: $0.midi, vowel: $0.vowel)
        }
        s.vocal(phrase, gain: 0.45, consonants: true)
        return s.fixture("mix")
    }

    static func pinkNoise(fs: Double) -> SoundFixture {
        let s = SoundSynth(seconds: 8, fs: fs, seed: 17)
        s.noise(0.5, duration: 7, gain: 0.15, pink: true)
        return s.fixture("pink noise")
    }
}

// MARK: - Feeding fixtures to the detector

/// The calibrated power spectrum AudioAnalyzer hands the sound-type detector,
/// computed directly: a Hann window (vDSP_HANN_NORM) over the `fftSize`
/// samples ending at a position, zero-padded at the start, one plain FFT,
/// |X|² / (2·N·Σw²) and bin 0 cleared.
final class PlainSpectrum {
    let fftSize: Int
    private let log2n: vDSP_Length
    private let setup: FFTSetup
    private let window: Vec, windowed: Vec, real: Vec, imag: Vec
    let power: Vec
    private var scale: Float

    init(fftSize: Int) {
        self.fftSize = fftSize
        log2n = vDSP_Length(fftSize.trailingZeroBitCount)
        setup = vDSP_create_fftsetup(log2n, FFTRadix(kFFTRadix2))!
        window = Vec(fftSize); windowed = Vec(fftSize)
        real = Vec(fftSize / 2); imag = Vec(fftSize / 2); power = Vec(fftSize / 2)
        vDSP_hann_window(window.p, vDSP_Length(fftSize), Int32(vDSP_HANN_NORM))
        var sumSquares: Float = 0
        vDSP_svesq(window.p, 1, &sumSquares, vDSP_Length(fftSize))
        scale = 1 / (2 * Float(fftSize) * sumSquares)
    }

    deinit { vDSP_destroy_fftsetup(setup) }

    /// The spectrum of the window ending just before `samples[position]`.
    func compute(_ samples: UnsafePointer<Float>, endingAt position: Int) -> UnsafeBufferPointer<Float> {
        let available = min(position, fftSize), bins = fftSize / 2
        vDSP_vclr(windowed.p, 1, vDSP_Length(fftSize - available))
        vDSP_vmul(samples + (position - available), 1, window.p + (fftSize - available), 1,
                  windowed.p + (fftSize - available), 1, vDSP_Length(available))
        var split = DSPSplitComplex(realp: real.p, imagp: imag.p)
        windowed.p.withMemoryRebound(to: DSPComplex.self, capacity: bins) { vDSP_ctoz($0, 2, &split, 1, vDSP_Length(bins)) }
        vDSP_fft_zrip(setup, &split, 1, log2n, FFTDirection(FFT_FORWARD))
        vDSP_zvmags(&split, 1, power.p, 1, vDSP_Length(bins))
        vDSP_vsmul(power.p, 1, &scale, power.p, 1, vDSP_Length(bins))
        power.p[0] = 0
        return UnsafeBufferPointer(start: power.p, count: bins)
    }
}

/// A fixture fed to a SoundTypeDetector the way AudioAnalyzer feeds it (a
/// spectrum every quarter window, or every `fftSize / hopDivisor` samples,
/// stamped with the time of its newest sample), straight from memory, and the
/// history read at every pass as the render loop would read it.
struct SoundTypeRun {
    let fixture: SoundFixture
    private(set) var times: [Double] = []
    private(set) var reads: [SoundTypeFeatures] = []
    /// Published (backdated) onset times of each transient class.
    private(set) var onsets: [SoundClass: [Double]] = [.kick: [], .snare: [], .hat: []]

    init(_ fixture: SoundFixture, fftSize: Int, hopDivisor: Int = 4) {
        self.fixture = fixture
        let hop = fftSize / hopDivisor, spectrum = PlainSpectrum(fftSize: fftSize)
        let detector = SoundTypeDetector()
        var history = SoundTypeHistory()
        let binHz = Float(fixture.fs) / Float(fftSize)
        var last = SoundTypeFeatures()
        fixture.samples.withUnsafeBufferPointer { all in
            var position = hop
            while position <= all.count {
                let time = Double(position) / fixture.fs
                detector.process(power: spectrum.compute(all.baseAddress!, endingAt: position), binHz: binHz,
                                 dt: Double(hop) / fixture.fs, time: time)
                history.record(detector.output, at: time)
                let read = history.read(at: time)
                if read.kickOnsets != last.kickOnsets { onsets[.kick]!.append(read.kickTime) }
                if read.snareOnsets != last.snareOnsets { onsets[.snare]!.append(read.snareTime) }
                if read.hatOnsets != last.hatOnsets { onsets[.hat]!.append(read.hatTime) }
                last = read
                times.append(time)
                reads.append(read)
                position += hop
            }
        }
    }

    // MARK: Scoring

    /// Greedy one-to-one matching of published onsets to the truth: an
    /// estimate may lie 25 ms before to `window` after the true onset.
    func f1(_ c: SoundClass, window: Double = 0.06) -> (f1: Double, recall: Double, falsePositives: Int) {
        let truth = (fixture.truth.onsets[c] ?? []).sorted()
        var used = [Bool](repeating: false, count: truth.count), hits = 0, misses = 0
        for onset in onsets[c] ?? [] {
            if let k = truth.indices.first(where: { !used[$0] && onset - truth[$0] >= -0.025 && onset - truth[$0] <= window }) {
                used[k] = true; hits += 1
            } else {
                misses += 1
            }
        }
        let unmatched = used.filter { !$0 }.count
        let precision = hits + misses == 0 ? 1 : Double(hits) / Double(hits + misses)
        let recall = hits + unmatched == 0 ? 1 : Double(hits) / Double(hits + unmatched)
        return (precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), recall, misses)
    }

    static func value(_ f: SoundTypeFeatures, _ c: SoundClass) -> Float {
        switch c {
        case .kick: return f.kick
        case .snare: return f.snare
        case .hat: return f.hat
        case .bass: return f.bass
        case .vocal: return f.vocal
        case .pad: return f.pad
        }
    }

    func count(_ c: SoundClass) -> Int { onsets[c]?.count ?? 0 }

    /// The 95th percentile of a class after the first 0.4 s.
    func p95(_ c: SoundClass) -> Float {
        let v = zip(times, reads).filter { $0.0 > 0.4 }.map { Self.value($0.1, c) }.sorted()
        return v.isEmpty ? 0 : v[Int(Double(v.count - 1) * 0.95)]
    }

    /// Mean inside the class's spans (skipping 0.2 s of each start) and
    /// outside them (skipping a 0.6 s release after each end).
    func spans(_ c: SoundClass) -> (inside: Float, outside: Float) {
        let spans = fixture.truth.active[c] ?? []
        var inside: [Float] = [], outside: [Float] = []
        for (t, f) in zip(times, reads) {
            if spans.contains(where: { t >= $0.lowerBound + 0.2 && t <= $0.upperBound }) {
                inside.append(Self.value(f, c))
            } else if t > 0.4, !spans.contains(where: { t >= $0.lowerBound - 0.05 && t <= $0.upperBound + 0.6 }) {
                outside.append(Self.value(f, c))
            }
        }
        return (Self.mean(inside), Self.mean(outside))
    }

    /// The largest value of a class within 70 ms after each true onset.
    func hitPeaks(_ c: SoundClass) -> [Float] {
        (fixture.truth.onsets[c] ?? []).map { t in
            zip(times, reads).filter { $0.0 >= t && $0.0 <= t + 0.07 }.map { Self.value($0.1, c) }.max() ?? 0
        }
    }

    /// The mean of class `c` from `a` to `b` seconds after each true onset of `o`.
    func mean(_ c: SoundClass, after o: SoundClass, from a: Double, to b: Double) -> Float {
        var values: [Float] = []
        for t in fixture.truth.onsets[o] ?? [] {
            for (time, f) in zip(times, reads) where time >= t + a && time <= t + b { values.append(Self.value(f, c)) }
        }
        return Self.mean(values)
    }

    static func mean(_ values: [Float]) -> Float { values.isEmpty ? 0 : values.reduce(0, +) / Float(values.count) }
}
