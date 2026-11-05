import Foundation

/// Deterministic stems of one short song (120 bpm, A minor) at 48 kHz stereo,
/// for driving the visualisers without a microphone, a player or the network.
///
/// Every fixture is a stem of the same arrangement, or all of them: the kick
/// in "kick-only" is exactly the kick in "full-mix", at the same level. Each
/// voice is generated from time zero with a fixed seed, so rendering twice
/// gives identical samples, and a voice's events sit at fixed musical times:
///
/// - kick: a sine sweeping 160 → 45 Hz on every beat (four on the floor,
///   every 0.5 s), with no click, so it lights no treble onsets;
/// - hat: white noise through two 7 kHz high-pass sections, a 22 ms tick on
///   every eighth (every 0.25 s), the off-beats accented;
/// - snare: a 180 Hz body with a 330 Hz overtone plus 3 kHz band-passed noise,
///   on the backbeats (beats 2 and 4: 0.5 s, 1.5 s, …);
/// - vocal: a sung "ah" (a harmonic source shaped by four vowel formants)
///   gliding between notes, with 5.5 Hz vibrato that grows through each note
///   and some breath; four phrases over 8 s, each followed by half a bar of
///   rest (silent at 1.5–2, 3.5–4, 5.5–6 and 7.5–8 s of every 8 s);
/// - pad: a sustained Am7 chord of detuned saws behind a 1.8 kHz low-pass,
///   fading in over the first 0.8 s and breathing slowly;
/// - bass: a plucked two-bar eighth-note riff around A1 (55 Hz) with rests.
enum SyntheticMusic {
    static let sampleRate = 48_000.0
    static let tempo = 120.0
    /// Seconds per beat.
    static var beat: Double { 60 / tempo }
    /// The arrangement repeats every 8 s (the vocal's four phrases).
    static let loopSeconds = 8.0

    enum Fixture: String, CaseIterable, Sendable {
        case kickOnly = "kick-only", hatOnly = "hat-only", snareOnly = "snare-only", vocalOnly = "vocal-only"
        case padOnly = "pad-only", bassOnly = "bass-only", fullMix = "full-mix", silence

        var voices: [Voice] {
            switch self {
            case .kickOnly: return [.kick]
            case .hatOnly: return [.hat]
            case .snareOnly: return [.snare]
            case .vocalOnly: return [.vocal]
            case .padOnly: return [.pad]
            case .bassOnly: return [.bass]
            case .fullMix: return Voice.allCases
            case .silence: return []
            }
        }

        /// One line for logs and contact sheets.
        var summary: String {
            switch self {
            case .kickOnly: return "sine-sweep kicks on every beat at 120 bpm (no click: no treble onsets)"
            case .hatOnly: return "filtered-noise hat ticks on every eighth"
            case .snareOnly: return "noise + body snare on the backbeats (2 and 4)"
            case .vocalOnly: return "sung 'ah' melody with vibrato; rests at 1.5-2, 3.5-4, 5.5-6, 7.5-8 s"
            case .padOnly: return "sustained Am7 pad chord"
            case .bassOnly: return "plucked eighth-note bass riff around 55 Hz"
            case .fullMix: return "kick, hat, snare, vocal, pad and bass together"
            case .silence: return "digital silence"
            }
        }

        /// A fixture by name: its raw value, or that without "-only" ("kick").
        init?(name: String) {
            let name = name.trimmingCharacters(in: .whitespaces).lowercased()
            guard let fixture = Self(rawValue: name) ?? Self(rawValue: name + "-only") else { return nil }
            self = fixture
        }
    }

    enum Voice: String, CaseIterable, Sendable {
        case kick, hat, snare, vocal, pad, bass

        /// Level in the mix. Each voice peaks near 1 on its own.
        var gain: Float {
            switch self {
            case .kick: return 0.86
            case .hat: return 0.24
            case .snare: return 0.52
            case .vocal: return 0.44
            case .pad: return 0.30
            case .bass: return 0.48
            }
        }
    }

    /// Scales the sum of the voice gains so the full mix peaks just under
    /// 0 dBFS; every fixture uses it, so stems keep their level in the mix.
    static let masterGain: Float = 0.58

    struct Stereo: Sendable {
        var left: [Float]
        var right: [Float]
        var count: Int { left.count }
        var seconds: Double { Double(count) / SyntheticMusic.sampleRate }
    }

    /// The fixture's first `seconds` of audio. `stem` renders one voice
    /// (a cache can stand in, so a stem shared by fixtures is made once).
    static func render(_ fixture: Fixture, seconds: Double,
                       stem: (Voice, Int) -> Stereo = { render($0, count: $1) }) -> Stereo {
        let count = max(0, Int((seconds * sampleRate).rounded(.up)))
        var left = [Float](repeating: 0, count: count), right = [Float](repeating: 0, count: count)
        for voice in fixture.voices {
            let part = stem(voice, count)
            let gain = voice.gain * masterGain
            for i in 0..<count {
                left[i] += part.left[i] * gain
                right[i] += part.right[i] * gain
            }
        }
        // A guard only: the arrangement is levelled to stay inside ±1.
        for i in 0..<count {
            left[i] = min(1, max(-1, left[i]))
            right[i] = min(1, max(-1, right[i]))
        }
        return Stereo(left: left, right: right)
    }

    /// One voice at its own level (peaking near 1), before mix gains.
    static func render(_ voice: Voice, count: Int) -> Stereo {
        switch voice {
        case .kick: return centred(kick(count))
        case .hat:
            let hats = self.hats(count)
            return Stereo(left: hats.map { $0 * 0.8 }, right: hats)
        case .snare: return centred(snare(count))
        case .vocal: return centred(vocal(count))
        case .pad: return Stereo(left: pad(count, detune: [-6, 4]), right: pad(count, detune: [-4, 6]))
        case .bass: return centred(bass(count))
        }
    }

    private static func centred(_ mono: [Float]) -> Stereo { Stereo(left: mono, right: mono) }

    // MARK: - Voices

    /// Calls `body` with each event's first sample index and its local
    /// sample offsets, for events every `period` seconds from `phase`.
    private static func events(every period: Double, from phase: Double, length: Double, count: Int,
                               _ body: (_ event: Int, _ start: Int, _ samples: Range<Int>) -> Void) {
        var event = 0
        while true {
            let start = Int(((phase + Double(event) * period) * sampleRate).rounded())
            guard start < count else { return }
            let samples = 0..<min(Int(length * sampleRate), count - start)
            body(event, start, samples)
            event += 1
        }
    }

    /// No beater click, and no edges: the analyser's treble onsets (a 6 kHz
    /// high-pass of only second order, with a very low floor) fire on any
    /// transient above the bass, even a soft 1 kHz knock or the kink of a
    /// 1.5 ms linear attack. So the sweep eases in over 5 ms (a raised cosine)
    /// and each kick rings out for 1.2 s, to −80 dB, under the next instead
    /// of being cut off: kick-only leaves the hats' register dark.
    static func kick(_ count: Int) -> [Float] {
        var out = [Float](repeating: 0, count: count)
        let attack = 0.005
        out.withUnsafeMutableBufferPointer { out in
            events(every: beat, from: 0, length: 1.2, count: count) { _, start, samples in
                for i in samples {
                    let t = Double(i) / sampleRate
                    // The pitch falls from 160 Hz to 45 Hz; the phase is its integral.
                    let phase = 2 * Double.pi * (45 * t + 115 * 0.035 * (1 - exp(-t / 0.035)))
                    let onset = t < attack ? 0.5 - 0.5 * cos(Double.pi * t / attack) : 1
                    out[start + i] += Float(sin(phase) * exp(-t / 0.13) * onset * 0.88)
                }
            }
        }
        return out
    }

    static func hats(_ count: Int) -> [Float] {
        var out = [Float](repeating: 0, count: count)
        var noise = Noise(seed: 0x6861_7473)
        var first = Biquad(highPass: 7_000, q: 0.7071), second = Biquad(highPass: 7_000, q: 0.7071)
        out.withUnsafeMutableBufferPointer { out in
            events(every: beat / 2, from: 0, length: 0.09, count: count) { event, start, samples in
                let accent = event % 2 == 1 ? 1.0 : 0.72
                for i in samples {
                    let t = Double(i) / sampleRate
                    let tick = second.process(first.process(noise.next()))
                    out[start + i] = Float(tick * exp(-t / 0.022) * min(1, t / 0.0004) * accent * 0.88)
                }
            }
        }
        return out
    }

    static func snare(_ count: Int) -> [Float] {
        var out = [Float](repeating: 0, count: count)
        var noise = Noise(seed: 0x736E_6172)
        var band = Biquad(bandPass: 3_000, q: 0.8), low = Biquad(highPass: 900, q: 0.7071)
        out.withUnsafeMutableBufferPointer { out in
            events(every: 2 * beat, from: beat, length: 0.98, count: count) { _, start, samples in
                for i in samples {
                    let t = Double(i) / sampleRate
                    let pitch = 2 * Double.pi * (180 * t + 40 * 0.02 * (1 - exp(-t / 0.02)))
                    let body = (sin(pitch) * exp(-t / 0.07) + 0.4 * sin(2 * Double.pi * 330 * t) * exp(-t / 0.04))
                        * min(1, t / 0.001)
                    let rattle = band.process(low.process(noise.next())) * exp(-t / 0.12)
                    out[start + i] = Float((body * 0.5 + rattle * 1.25) * 0.68)
                }
            }
        }
        return out
    }

    /// The vocal's notes, as (start beat, MIDI note), and its phrase ends.
    /// Each phrase leaves half a bar of rest before the next.
    private static let melody: [(beat: Double, note: Double)] = [
        (0, 64), (1, 62), (2, 60),          // E4 D4 C4, rest at beat 3
        (4, 57), (5.5, 60),                 // A3 C4, rest at beat 7
        (8, 64), (9, 67), (10, 64),         // E4 G4 E4, rest at beat 11
        (12, 62), (13, 60), (14, 57),       // D4 C4 A3, rest at beat 15
    ]
    private static let phraseEnds: [Double] = [3, 7, 11, 15]

    static func vocal(_ count: Int) -> [Float] {
        var out = [Float](repeating: 0, count: count)
        var breath = Noise(seed: 0x766F_6361)
        var air = Biquad(highPass: 3_500, q: 0.7071)
        // Formants of an open "ah": centre, bandwidth and level.
        let formants: [(centre: Double, width: Double, level: Double)] = [
            (800, 90, 1.0), (1_150, 110, 0.55), (2_900, 160, 0.28), (3_900, 200, 0.12),
        ]
        func envelope(_ frequency: Double) -> Double {
            formants.reduce(0.015) { sum, formant in
                let x = (frequency - formant.centre) / (formant.width / 2)
                return sum + formant.level / (1 + x * x)
            }
        }
        let loopBeats = loopSeconds / beat
        let glideStep = 1 - exp(-1 / (0.03 * sampleRate))
        // Every note and phrase boundary falls on a multiple of this many
        // samples (a beat is 24 000), so the musical state is read per block.
        let block = 32
        var phase = 0.0, glide = -1.0
        var harmonics = 0
        var singing = false, phraseStart = 0.0, phraseEnd = 0.0, noteBeat = 0.0, syllable = false, target = 0.0
        var amplitudes = [Double](repeating: 0, count: 48)
        out.withUnsafeMutableBufferPointer { out in
            amplitudes.withUnsafeMutableBufferPointer { amplitudes in
                for i in 0..<count {
                    let t = Double(i) / sampleRate
                    let position = (t / beat).truncatingRemainder(dividingBy: loopBeats)
                    if i % block == 0 {
                        let phrase = phraseEnds.firstIndex { position < $0 } ?? phraseEnds.count
                        phraseStart = phrase == 0 ? 0 : phraseEnds[phrase - 1] + 1
                        phraseEnd = phrase < phraseEnds.count ? phraseEnds[phrase] : loopBeats
                        singing = phrase < phraseEnds.count && position >= phraseStart
                        let note = melody[melody.lastIndex { $0.beat <= position } ?? 0]
                        noteBeat = note.beat
                        syllable = note.beat > phraseStart
                        target = log2(440 * pow(2, (note.note - 69) / 12))
                        if glide < 0 { glide = target }
                    }
                    glide += (target - glide) * glideStep
                    let sinceNote = (position - noteBeat) * beat
                    let depth = 0.35 / 12 * min(1, max(0, (sinceNote - 0.12) / 0.35))
                    let frequency = pow(2, glide + depth * sin(2 * Double.pi * 5.5 * t))
                    phase += 2 * Double.pi * frequency / sampleRate
                    if phase > 2 * Double.pi { phase -= 2 * Double.pi }
                    let hiss = air.process(breath.next()) * 0.05

                    // Phrase envelope with a small dip at each new syllable.
                    guard singing else { continue }
                    var level = min(1, (position - phraseStart) * beat / 0.06) * min(1, (phraseEnd - position) * beat / 0.12)
                    if syllable { level *= 0.72 + 0.28 * min(1, sinceNote / 0.05) }
                    // The harmonic levels follow the pitch.
                    if i % block == 0 || harmonics == 0 {
                        harmonics = min(amplitudes.count, Int(9_000 / frequency))
                        for k in 1...max(1, harmonics) {
                            amplitudes[k - 1] = envelope(Double(k) * frequency) / Double(k)
                        }
                    }
                    // sin(kφ) by the Chebyshev recurrence: one sine and one cosine a sample.
                    let c = cos(phase)
                    var previous = 0.0, current = sin(phase), sum = 0.0
                    for k in 0..<harmonics {
                        sum += amplitudes[k] * current
                        let next = 2 * c * current - previous
                        previous = current
                        current = next
                    }
                    out[i] = Float((sum * 1.5 + hiss) * level)
                }
            }
        }
        return out
    }

    /// A sustained Am7 chord (A2 E3 G3 C4 E4), two saws a note detuned by
    /// `detune` cents, low-passed, fading in over 0.8 s and breathing slowly.
    static func pad(_ count: Int, detune: [Double]) -> [Float] {
        var out = [Float](repeating: 0, count: count)
        let notes: [Double] = [110.0, 164.81, 196.0, 261.63, 329.63]
        var phases: [Double] = [], steps: [Double] = []
        for (n, frequency) in notes.enumerated() {
            for (d, cents) in detune.enumerated() {
                phases.append(Double((n * 7 + d * 3) % 10) / 10)
                steps.append(frequency * pow(2, cents / 1_200) / sampleRate)
            }
        }
        var first = Biquad(lowPass: 1_800, q: 0.6), second = Biquad(lowPass: 1_800, q: 0.6)
        let scale = 0.48 / Double(phases.count).squareRoot()
        out.withUnsafeMutableBufferPointer { out in
            phases.withUnsafeMutableBufferPointer { phases in
                steps.withUnsafeBufferPointer { steps in
                    for i in 0..<count {
                        let t = Double(i) / sampleRate
                        var sum = 0.0
                        for o in 0..<phases.count {
                            let phase = phases[o], step = steps[o]
                            sum += 2 * phase - 1 - polyBLEP(phase, step)
                            phases[o] = phase + step >= 1 ? phase + step - 1 : phase + step
                        }
                        let swell = min(1, t / 0.8) * (0.88 + 0.12 * sin(2 * Double.pi * t / 4))
                        out[i] = Float(second.process(first.process(sum * scale)) * swell)
                    }
                }
            }
        }
        return out
    }

    /// A band-limited step correction for a naive sawtooth.
    private static func polyBLEP(_ phase: Double, _ step: Double) -> Double {
        if phase < step {
            let x = phase / step
            return x + x - x * x - 1
        }
        if phase > 1 - step {
            let x = (phase - 1) / step
            return x * x + x + x + 1
        }
        return 0
    }

    /// Sixteen eighths over two bars; nil is a rest. A1 = 33.
    private static let riff: [Double?] = [33, 33, nil, 33, 36, nil, 38, 40, 31, 31, nil, 31, 33, nil, 36, 40]

    static func bass(_ count: Int) -> [Float] {
        var out = [Float](repeating: 0, count: count)
        let eighth = beat / 2
        out.withUnsafeMutableBufferPointer { out in
            events(every: eighth, from: 0, length: eighth, count: count) { event, start, samples in
                guard let note = riff[event % riff.count] else { return }
                let frequency = 440 * pow(2, (note - 69) / 12)
                let held = eighth * 0.9
                for i in samples {
                    let t = Double(i) / sampleRate
                    let attack = min(1, t / 0.004)
                    let decay = 0.5 + 0.5 * exp(-t / 0.18)
                    let release = t < held ? 1 : max(0, 1 - (t - held) / 0.02)
                    let w = 2 * Double.pi * frequency * t
                    let tone = sin(w) + 0.4 * sin(2 * w) + 0.18 * sin(3 * w)
                    out[start + i] = Float(tanh(1.5 * tone * attack * decay * release) / tanh(1.5 * 1.45))
                }
            }
        }
        return out
    }

    // MARK: - Building blocks

    /// A 64-bit linear congruential generator; uniform in -1..<1.
    struct Noise {
        var state: UInt64
        init(seed: UInt64) { state = seed &* 0x9E37_79B9_7F4A_7C15 | 1 }
        mutating func next() -> Double {
            state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            return Double(UInt32(truncatingIfNeeded: state >> 32)) / 2_147_483_648 - 1
        }
    }

    /// An RBJ-cookbook biquad in transposed direct form II.
    struct Biquad {
        private var b0 = 1.0, b1 = 0.0, b2 = 0.0, a1 = 0.0, a2 = 0.0, z1 = 0.0, z2 = 0.0

        private init(_ b: (Double, Double, Double), _ a: (Double, Double, Double)) {
            b0 = b.0 / a.0; b1 = b.1 / a.0; b2 = b.2 / a.0; a1 = a.1 / a.0; a2 = a.2 / a.0
        }
        private static func terms(_ frequency: Double, _ q: Double) -> (cos: Double, alpha: Double) {
            let w = 2 * Double.pi * frequency / SyntheticMusic.sampleRate
            return (cos(w), sin(w) / (2 * q))
        }
        init(lowPass frequency: Double, q: Double) {
            let (c, alpha) = Self.terms(frequency, q)
            self.init(((1 - c) / 2, 1 - c, (1 - c) / 2), (1 + alpha, -2 * c, 1 - alpha))
        }
        init(highPass frequency: Double, q: Double) {
            let (c, alpha) = Self.terms(frequency, q)
            self.init(((1 + c) / 2, -(1 + c), (1 + c) / 2), (1 + alpha, -2 * c, 1 - alpha))
        }
        /// Constant 0 dB peak gain.
        init(bandPass frequency: Double, q: Double) {
            let (c, alpha) = Self.terms(frequency, q)
            self.init((alpha, 0, -alpha), (1 + alpha, -2 * c, 1 - alpha))
        }
        mutating func process(_ x: Double) -> Double {
            let y = b0 * x + z1
            z1 = b1 * x - a1 * y + z2
            z2 = b2 * x - a2 * y
            return y
        }
    }
}
