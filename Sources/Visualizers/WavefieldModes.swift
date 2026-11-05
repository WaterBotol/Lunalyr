// Adapted from Wavefield by Niko Dellic, MIT. See ThirdParty/Wavefield/SOURCE.md.

import Foundation
import Metal
import simd

struct WavefieldSpectrum: Sendable {
    struct Peak: Sendable {
        let frequency: Float
        let magnitude: Float
        let harmonics: Float
    }

    var isReceiving = false
    var rms: Float = 0
    var energy: Float = 0
    var centroid: Float = 0
    var bands = SIMD3<Float>.zero
    var peaks: [Peak] = []

    static func band(for frequency: Float) -> Int {
        if frequency < 250 { return 0 }
        return frequency < 2_000 ? 1 : 2
    }

    /// Reuse Lunalyr's vDSP FFT before log-band reduction so the atlas sees
    /// the actual pitch, including bass bins with unequal log-band spacing.
    /// Readers build this from a stored pass (`AudioAnalyzer.latest`), so its
    /// peak search and sort never run, or allocate, on the capture thread.
    init(power: [Float], binHz: Float, fftSize: Int, rms: Float) {
        self.init()
        isReceiving = true
        self.rms = min(1, max(0, rms * 2.4))
        // vDSP's packed real forward transform has a factor of two.
        let norm = 0.5 / Float(fftSize)
        var bandPower = SIMD3<Float>.zero
        var bandCounts = SIMD3<Float>.zero
        var sum: Float = 0, weighted: Float = 0, totalPower: Float = 0, strongest: Float = 0
        // Measure energy and centroid up to 24 kHz at every sample rate, so a
        // 96 kHz device's empty top octave does not dim or darken the field.
        let usable = max(2, min(power.count, Int(24_000 / max(1, binHz)) + 1))
        for bin in 1..<usable {
            let frequency = Float(bin) * binHz
            let value = max(0, power[bin])
            let amplitude = sqrt(value) * norm
            sum += amplitude
            weighted += frequency * amplitude
            totalPower += value
            strongest = max(strongest, amplitude)
            if frequency >= 20, frequency < 8_000 {
                let band = Self.band(for: frequency)
                bandPower[band] += value
                bandCounts[band] += 1
            }
        }
        for band in 0..<3 {
            bands[band] = min(1, sqrt(bandPower[band] / max(1, bandCounts[band])) * norm * 12)
        }
        energy = min(1, sqrt(totalPower / Float(usable)) * norm * 14)
        centroid = sum > 0.000001 ? min(1, weighted / sum / (Float(usable) * binHz)) : 0

        let threshold = max(0.00008, strongest * 0.12)
        guard strongest >= threshold else { return }
        let end = min(power.count - 1, Int(8_000 / binHz) + 2)
        var candidates: [(frequency: Float, magnitude: Float)] = []
        for bin in 1..<end {
            let centre = sqrt(max(0, power[bin])) * norm
            guard centre >= threshold, power[bin] >= power[bin - 1], power[bin] > power[bin + 1] else { continue }
            let left = sqrt(max(0, power[bin - 1])) * norm
            let right = sqrt(max(0, power[bin + 1])) * norm
            let denominator = left - 2 * centre + right
            let offset = abs(denominator) > 0.000001 ? (left - right) * 0.5 / denominator : 0
            let frequency = (Float(bin) + min(0.5, max(-0.5, offset))) * binHz
            if (45...8_000).contains(frequency) { candidates.append((frequency, centre)) }
        }
        for peak in candidates.sorted(by: { $0.magnitude > $1.magnitude }) {
            if peaks.count == 6 { break }
            guard !peaks.contains(where: { abs($0.frequency - peak.frequency) < 58 }) else { continue }
            var support: Float = 0, weight: Float = 0
            for order in 2...5 {
                let bin = min(power.count - 1, Int((peak.frequency * Float(order) / binHz).rounded()))
                let share = 1 / Float(order)
                support += min(1, sqrt(max(0, power[bin])) * norm / peak.magnitude) * share
                weight += share
            }
            peaks.append(Peak(frequency: peak.frequency, magnitude: peak.magnitude, harmonics: support / weight))
        }
    }

    init() {}
}

/// The heatmap only reads topology, motion and excitation. Pitch colours are
/// used by other Wavefield palettes and aren't part of the Default template.
struct WavefieldModeUniforms {
    var shape: SIMD4<Float>   // m, n, topology, band index
    var motion: SIMD4<Float>  // phase, coherence, log frequency, layer
    var drive: SIMD4<Float>   // excitation, pulse
}

struct WavefieldFrame {
    var modes: [WavefieldModeUniforms]
    var bandsAndRMS: SIMD4<Float>
    var onsetsAndEnergy: SIMD4<Float>
    var change: Float
    var pulse: Float

    func bind(to encoder: MTLRenderCommandEncoder, uniforms: inout VizUniforms) {
        apply(to: &uniforms)
        bindModes(to: encoder)
    }

    /// The field's scalar state in the uniform option slots.
    func apply(to uniforms: inout VizUniforms) {
        uniforms.p3 = Float(modes.count)
        uniforms.p4 = change
        uniforms.p5 = pulse
        uniforms.p6 = bandsAndRMS.x
        uniforms.p7 = bandsAndRMS.y
        uniforms.p8 = bandsAndRMS.z
        uniforms.p9 = bandsAndRMS.w
        uniforms.p10 = onsetsAndEnergy.x
        uniforms.p11 = onsetsAndEnergy.y
        uniforms.p12 = onsetsAndEnergy.z
        uniforms.p13 = onsetsAndEnergy.w
    }

    func bindModes(to encoder: MTLRenderCommandEncoder) {
        if modes.isEmpty {
            // Metal still requires a bound buffer when the shader exits early.
            var empty = WavefieldModeUniforms(shape: .zero, motion: .zero, drive: .zero)
            encoder.setFragmentBytes(&empty, length: MemoryLayout<WavefieldModeUniforms>.stride, index: 3)
        } else {
            modes.withUnsafeBytes { encoder.setFragmentBytes($0.baseAddress!, length: $0.count, index: 3) }
        }
    }
}

/// Wavefield's free-plate atlas and asymmetric modal bank. A peak excites its
/// two nearest resonances, and the bank retains their tails across frames.
struct WavefieldModes {
    private struct Resonance {
        let m: Float
        let n: Float
        var frequency: Float { 220 * (m * m + n * n) / 13 }
        var frequencyNorm: Float { min(1, max(0, log2(frequency / 70) / log2(7_200.0 / 70))) }
        var band: Int { WavefieldSpectrum.band(for: frequency) }
        var seed: Float {
            let hash = sin(Double(m) * 12.9898 + Double(n) * 78.233) * 43_758.5453
            return Float(abs(hash.truncatingRemainder(dividingBy: 1)))
        }
    }

    private struct Target {
        var weight: Float = 0
        var excitation: Float = 0
        var pulse: Float = 0
    }

    private struct Mode {
        var weight: Float = 0
        var excitation: Float = 0
        var pulse: Float = 0
        var phase: Float
    }

    private static let atlas = (1..<16).flatMap { m in
        ((m + 1)...16).map { Resonance(m: Float(m), n: Float($0)) }
    }.sorted { $0.frequency < $1.frequency }
    private static let bandScales = SIMD3<Float>(1.18, 1.12, 1.08)

    private var bank: [Int: Mode] = [:]
    private var bands = SIMD3<Float>.zero
    private var rms: Float = 0
    private var lastTime: Double?

    /// `reduced` is calm (Reduce Motion or Dim Flashing Lights): onsets are
    /// removed and the sensitivity no longer amplifies energy. `transient`
    /// is the Punch control, `motion` scales phase travel (Reduce Motion) and
    /// `patternRate` slows how quickly modes morph in and out.
    mutating func frame(_ sound: AudioSnapshot, at clock: Double, time: Float, sensitivity: Float, reduced: Bool,
                        transient: Float = 1, motion: Float? = nil, patternRate: Float? = nil) -> WavefieldFrame {
        let delta = Float(min(0.1, max(0, clock - (lastTime ?? clock - 1.0 / 30))))
        lastTime = clock
        let motion = motion ?? (reduced ? 0.15 : 1)
        let patternDelta = delta * (patternRate ?? (reduced ? 0.25 : 1))
        let transient = reduced ? 0 : max(0, transient)
        let spectrum = sound.wavefield
        guard spectrum.isReceiving else {
            bank.removeAll(keepingCapacity: true)
            bands = .zero
            rms = 0
            return Self.ambient(at: time)
        }

        let gain = min(3, max(0.3, sensitivity))
        for band in 0..<3 {
            bands[band] = Self.follow(bands[band], min(1, spectrum.bands[band] * gain), delta, attack: 1 / 26, release: 1 / 9)
        }
        rms = Self.follow(rms, min(1, spectrum.rms * gain), delta, attack: 1 / 24, release: 1 / 8)
        let energy = min(1, spectrum.rms * 0.42 + (spectrum.bands.sum() / 3) * 0.34 + spectrum.energy * 0.24)
        let change = sound.flux * transient
        let onsets = SIMD3<Float>(sound.beat, sound.flux, sound.trebleBeat) * transient
        let pulse = min(1, onsets.x * 0.72 + max(onsets.x * 0.32, max(onsets.y * 0.42, onsets.z * 0.50)) + change * 0.22)
        let targets = Self.targets(for: spectrum, energy: energy, onsets: onsets, pulse: pulse, sensitivity: sensitivity)
        for index in targets.keys where bank[index] == nil {
            bank[index] = Mode(phase: Self.atlas[index].seed * 2 * .pi)
        }
        for index in Array(bank.keys) {
            guard var mode = bank[index] else { continue }
            let target = targets[index] ?? Target()
            let resonance = Self.atlas[index]
            mode.weight = Self.follow(mode.weight, target.weight, patternDelta, attack: 0.12, release: 0.27)
            mode.excitation = Self.follow(mode.excitation, min(1, target.excitation * gain), patternDelta, attack: 0.03, release: 0.18)
            mode.pulse = max(mode.pulse, min(1, target.pulse * gain)) * exp(-delta / 0.072)
            let rate = 0.06 + resonance.frequencyNorm * 0.32 + mode.weight * 0.3
                + mode.pulse * (0.5 + resonance.seed * 0.3)
            mode.phase = (mode.phase + delta * rate * motion).truncatingRemainder(dividingBy: 2 * .pi)
            bank[index] = mode.weight < 0.0001 && target.weight == 0 ? nil : mode
        }

        let ranked = bank.filter { $0.value.weight > 0.01 }.sorted {
            $0.value.weight == $1.value.weight ? $0.key < $1.key : $0.value.weight > $1.value.weight
        }
        let magnitude = spectrum.peaks.reduce(Float.zero) { $0 + $1.magnitude }
        let harmonicity = spectrum.peaks.reduce(Float.zero) { $0 + $1.magnitude * $1.harmonics }
            / max(0.000001, magnitude)
        let modes = ranked.prefix(8).enumerated().map { rank, entry in
            let (index, mode) = entry
            return Self.uniforms(Self.atlas[index], weight: mode.weight, phase: mode.phase,
                coherence: mode.weight * 0.7 + harmonicity * 0.3,
                layer: rank == 0 ? 0 : rank < 3 ? 0.3 : 0.7, excitation: mode.excitation, pulse: mode.pulse)
        }
        guard !modes.isEmpty else { return Self.ambient(at: time) }
        // Calm frames never let the sensitivity amplify energy beyond the
        // field's own level; their onsets, change and pulse are already zero.
        return WavefieldFrame(modes: modes, bandsAndRMS: SIMD4(bands, rms),
            onsetsAndEnergy: SIMD4(onsets * gain, min(1, energy * (reduced ? min(1, gain) : gain))),
            change: min(1, change * gain), pulse: min(1, pulse * gain))
    }

    private static func targets(for spectrum: WavefieldSpectrum, energy: Float, onsets: SIMD3<Float>,
                                pulse: Float, sensitivity: Float) -> [Int: Target] {
        var targets: [Int: Target] = [:]
        let strongest = spectrum.peaks.first?.magnitude ?? 0
        for (rank, peak) in spectrum.peaks.enumerated() {
            let peakEnergy = min(1, peak.magnitude * 120)
            let band = WavefieldSpectrum.band(for: peak.frequency)
            let weight = (peak.magnitude / strongest) * (0.4 + peakEnergy * 0.6)
                * (0.7 + peak.harmonics * 0.3) * bandScales[band] / (1 + Float(rank) * 0.6)
            // Log-distance is monotonic on each side of the insertion point.
            // Only the two neighbours on each side can be among the closest two.
            let upper = atlas.firstIndex { $0.frequency >= peak.frequency } ?? atlas.count
            let neighbors = (max(0, upper - 2)..<min(atlas.count, upper + 2)).sorted {
                let a = atlas[$0], b = atlas[$1]
                let distanceA = abs(log2(a.frequency / peak.frequency))
                let distanceB = abs(log2(b.frequency / peak.frequency))
                if abs(distanceA - distanceB) > 0.000001 { return distanceA < distanceB }
                if a.m + a.n != b.m + b.n { return a.m + a.n < b.m + b.n }
                return $0 < $1
            }
            for (rank, index) in neighbors.prefix(2).enumerated() {
                let share: Float = rank == 0 ? 1 : 0.42
                let onset = onsets[atlas[index].band]
                var target = targets[index, default: Target()]
                target.weight += weight * share
                target.excitation += (peakEnergy * 0.6 + energy * 0.3 + spectrum.rms * 0.2) * weight * share
                target.pulse = max(target.pulse, min(1, pulse * 0.5 + onset * 0.5 + onsets.x * 0.4) * share)
                targets[index] = target
            }
        }
        if targets.isEmpty, spectrum.rms > 0.0001 {
            let frequency = 70 * pow(7_200.0 / 70, spectrum.centroid)
            if let index = atlas.indices.min(by: {
                abs(log2(atlas[$0].frequency / frequency)) < abs(log2(atlas[$1].frequency / frequency))
            }) {
                targets[index] = Target(weight: spectrum.rms, excitation: energy, pulse: pulse)
            }
        }
        guard let strongest = targets.values.map(\.weight).max(), strongest > 0.000001 else { return [:] }
        let level = min(1, pow((spectrum.rms + energy) * 0.5, 1 / 1.5) * 1.8 * sensitivity)
        return targets.mapValues { target in
            Target(weight: pow(target.weight / strongest, 1.72) * (0.55 + 0.45 * level),
                excitation: min(1, pow(min(1, target.excitation / strongest), 1 / 1.5) * 1.2 * 1.45),
                pulse: target.pulse)
        }
    }

    private static func ambient(at time: Float) -> WavefieldFrame {
        let shimmer = 0.5 + 0.5 * sin(time * 0.42)
        let modes = atlas[4..<16].enumerated().map { index, resonance -> WavefieldModeUniforms in
            let rate: Float = 0.08 + resonance.frequencyNorm * 0.18
            let offset: Float = Float(index) * 0.73
            let wave: Float = 0.5 + 0.5 * sin(time * rate + offset)
            let phase: Float = resonance.seed * 2 * .pi
            let coherence: Float = 0.42 + resonance.frequencyNorm * 0.28
            let layer: Float = index % 3 == 0 ? 1 : 0
            return uniforms(resonance, weight: 0.16 + wave * 0.08, phase: phase,
                coherence: coherence, layer: layer, excitation: 0.18 + wave * 0.08, pulse: 0.08 + wave * 0.08)
        }
        let onset = 0.02 + shimmer * 0.01
        return WavefieldFrame(modes: modes, bandsAndRMS: SIMD4(0.18 + shimmer * 0.03,
            0.24 + shimmer * 0.04, 0.12 + shimmer * 0.02, 0.18 + shimmer * 0.04),
            onsetsAndEnergy: SIMD4(onset, onset, onset, 0.22 + shimmer * 0.04), change: 0, pulse: 0.04)
    }

    private static func uniforms(_ resonance: Resonance, weight: Float, phase: Float, coherence: Float,
                                 layer: Float, excitation: Float, pulse: Float) -> WavefieldModeUniforms {
        WavefieldModeUniforms(shape: SIMD4(resonance.m, resonance.n, weight, Float(resonance.band)),
            motion: SIMD4(phase, coherence, resonance.frequencyNorm, layer), drive: SIMD4(excitation, pulse, 0, 0))
    }

    private static func follow(_ value: Float, _ target: Float, _ dt: Float, attack: Float, release: Float) -> Float {
        value + (target - value) * (1 - exp(-dt / (target > value ? attack : release)))
    }
}
