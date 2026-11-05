import Foundation

/// The newest onsets of one class, newest first, that had started by the
/// presentation time. Scenes that keep more than one flare alive (one slot
/// per recent hit) read this rather than watching the counter, which can
/// move twice between two frames.
///
/// A fixed-size value stored inline (no heap storage), so building or
/// copying a snapshot never allocates. Index it (`ring[0]` is the newest) or
/// iterate it; there are always `capacity` entries, and unused ones have time
/// −∞ and strength 0.
struct SoundOnsetRing: Sendable, Equatable, RandomAccessCollection, MutableCollection {
    static let capacity = 8
    typealias Index = Int
    typealias Indices = Range<Int>

    private var slots = (SoundOnset(), SoundOnset(), SoundOnset(), SoundOnset(),
                         SoundOnset(), SoundOnset(), SoundOnset(), SoundOnset())

    init() {}

    var startIndex: Int { 0 }
    var endIndex: Int { Self.capacity }
    var indices: Range<Int> { 0..<Self.capacity }

    subscript(index: Int) -> SoundOnset {
        get {
            switch index {
            case 0: return slots.0
            case 1: return slots.1
            case 2: return slots.2
            case 3: return slots.3
            case 4: return slots.4
            case 5: return slots.5
            case 6: return slots.6
            case 7: return slots.7
            default: preconditionFailure("SoundOnsetRing index \(index) is out of range")
            }
        }
        set {
            switch index {
            case 0: slots.0 = newValue
            case 1: slots.1 = newValue
            case 2: slots.2 = newValue
            case 3: slots.3 = newValue
            case 4: slots.4 = newValue
            case 5: slots.5 = newValue
            case 6: slots.6 = newValue
            case 7: slots.7 = newValue
            default: preconditionFailure("SoundOnsetRing index \(index) is out of range")
            }
        }
    }

    /// The same entries: `ring.onsets[i]` is `ring[i]`.
    var onsets: SoundOnsetRing {
        get { self }
        set { self = newValue }
    }

    static func == (a: SoundOnsetRing, b: SoundOnsetRing) -> Bool { a.elementsEqual(b) }
}

/// What type of sound is playing at one presentation time (see
/// SoundTypeDetector). Classes are not exclusive: a kick and a snare on the
/// same beat both fire, and a voice over a pad lights both.
struct SoundTypeFeatures: Sendable, Equatable {
    /// Transient envelopes, 0...1, with the normal shapes (attack 8-10 ms,
    /// release 0.15-0.26 s), not scaled by Punch. The uniforms re-read the
    /// rings with the calm shapes when Reduce Motion or Dim Flashing Lights
    /// is on.
    var kick: Float = 0
    var snare: Float = 0
    var hat: Float = 0
    /// Sustained envelopes, 0...1: the bass line (kick tails held out), the
    /// lead voice or melodic lead, and the harmonic bed.
    var bass: Float = 0
    var vocal: Float = 0
    var pad: Float = 0
    /// Onsets counted since the source started. They never decrease, also
    /// not through silence or when the display delay grows; only a new
    /// source resets them. They can step by more than one between two
    /// frames. A native scene's surface counts only the onsets it showed
    /// (see SoundTypeIdleGate).
    var kickOnsets: UInt32 = 0
    var snareOnsets: UInt32 = 0
    var hatOnsets: UInt32 = 0
    /// Start of the newest onset at or before `presentationTime` (capture
    /// clock), −∞ if none.
    var kickTime = -Double.infinity
    var snareTime = -Double.infinity
    var hatTime = -Double.infinity
    var kickRing = SoundOnsetRing()
    var snareRing = SoundOnsetRing()
    var hatRing = SoundOnsetRing()
    /// The display time these values were evaluated at, on the capture
    /// clock; −∞ when they were not read from audio.
    var presentationTime = -Double.infinity
}

/// strength × (1 − e^(−age/attack)) × e^(−age/release), scaled so that a
/// strength-1 onset peaks at exactly 1. A function of the age alone, so it
/// reads the same at any frame rate.
struct SoundOnsetShape: Sendable, Equatable {
    let attack: Double
    let release: Double
    private let norm: Double

    init(attack: Double, release: Double) {
        self.attack = attack
        self.release = release
        let peakAge = attack * log((attack + release) / attack)
        norm = 1 / ((1 - exp(-peakAge / attack)) * exp(-peakAge / release))
    }

    func value(at age: Double) -> Double {
        age < 0 ? 0 : (1 - exp(-age / attack)) * exp(-age / release) * norm
    }

    /// The largest flare among the ring's onsets that had started by `time`,
    /// clamped to 1. Taking the maximum rather than the sum keeps dense hats
    /// from saturating.
    func envelope(_ ring: SoundOnsetRing, at time: Double) -> Float {
        var envelope = 0.0
        var i = 0
        while i < SoundOnsetRing.capacity {
            let onset = ring[i]
            if onset.strength > 0, onset.time <= time {
                envelope = max(envelope, Double(onset.strength) * value(at: time - onset.time))
            }
            i += 1
        }
        return Float(min(1, envelope))
    }
}

/// The flare shapes the transient classes are read with.
struct SoundTypeShapes: Sendable, Equatable {
    var kick: SoundOnsetShape
    var snare: SoundOnsetShape
    var hat: SoundOnsetShape

    /// The detector's own envelopes: attack 8-10 ms, release 0.15-0.26 s.
    static let normal = SoundTypeShapes(SoundTypeDetector.Tuning())
    /// Reduce Motion and Dim Flashing Lights: slow, soft flares (attack
    /// 120 ms, release 0.45-0.6 s), which the uniforms also halve.
    static let calm = SoundTypeShapes(kick: .init(attack: 0.12, release: 0.6),
                                      snare: .init(attack: 0.12, release: 0.55),
                                      hat: .init(attack: 0.12, release: 0.45))

    init(kick: SoundOnsetShape, snare: SoundOnsetShape, hat: SoundOnsetShape) {
        self.kick = kick
        self.snare = snare
        self.hat = hat
    }

    init(_ tuning: SoundTypeDetector.Tuning) {
        kick = .init(attack: Double(tuning.kickAttack), release: Double(tuning.kickRelease))
        snare = .init(attack: Double(tuning.snareAttack), release: Double(tuning.snareRelease))
        hat = .init(attack: Double(tuning.hatAttack), release: Double(tuning.hatRelease))
    }
}

/// The detector's recent output on the capture clock, and the stateless
/// read-time evaluation the render loop uses. Every value read is a function
/// of the stored onsets, the stored passes and the display time alone, so 30,
/// 60 and 120 fps read identical values at the same instant, and a display
/// that lags capture (Bluetooth output) sees an onset only once it is heard.
///
/// Recording mutates fixed-size storage in place and never allocates, so the
/// analysis worker can do it; neither does a read.
struct SoundTypeHistory {
    static let onsetCapacity = 16
    /// About 0.35-0.7 s of passes: enough for the longest visual delay.
    static let passCapacity = 32
    /// After this long without a pass, sustained values fade with
    /// `idleFade`, as AudioAnalyzer fades its bands when capture stops.
    static let idleAfter = 0.25
    static let idleFade = 0.2

    private var kick = OnsetLog(), snare = OnsetLog(), hat = OnsetLog()
    private var passTime = [Double](repeating: -.infinity, count: SoundTypeHistory.passCapacity)
    private var passValues = [Float](repeating: 0, count: SoundTypeHistory.passCapacity * 3)
    private var passHead = 0
    private var passCount = 0

    /// Capture time of the newest recorded pass, −∞ before the first.
    var newestPassTime: Double { passCount > 0 ? passTime[passHead] : -.infinity }

    /// Records one detector pass captured at `time`.
    mutating func record(_ output: SoundTypeDetector.Output, at time: Double) {
        kick.record(count: output.kickOnsets, newest: output.newestKick)
        snare.record(count: output.snareOnsets, newest: output.newestSnare)
        hat.record(count: output.hatOnsets, newest: output.newestHat)
        // Keep pass times ordered even if the capture clock jitters.
        let stamp = passCount > 0 ? max(time, passTime[passHead]) : time
        passHead = passCount > 0 ? (passHead + 1) % Self.passCapacity : 0
        passTime[passHead] = stamp
        passValues[passHead * 3] = output.bass
        passValues[passHead * 3 + 1] = output.vocal
        passValues[passHead * 3 + 2] = output.pad
        passCount = min(passCount + 1, Self.passCapacity)
    }

    /// Every feature as shown at `time`: transients from the onsets with the
    /// normal shapes, sustained values interpolated between the passes
    /// around `time`, counters and onset times as of `time`.
    func read(at time: Double) -> SoundTypeFeatures {
        var f = SoundTypeFeatures()
        f.presentationTime = time
        let shapes = SoundTypeShapes.normal
        (f.kickOnsets, f.kickTime, f.kick) = kick.read(at: time, shape: shapes.kick, into: &f.kickRing)
        (f.snareOnsets, f.snareTime, f.snare) = snare.read(at: time, shape: shapes.snare, into: &f.snareRing)
        (f.hatOnsets, f.hatTime, f.hat) = hat.read(at: time, shape: shapes.hat, into: &f.hatRing)
        guard passCount > 0 else { return f }
        // The newest pass captured by `time`, and the one after it.
        var older = -1, newer = -1, back = 0
        while back < passCount {
            let slot = (passHead - back + Self.passCapacity) % Self.passCapacity
            if passTime[slot] <= time { older = slot; break }
            newer = slot
            back += 1
        }
        var weight: Float = 0
        if older < 0 {
            older = newer
        } else if newer >= 0, passTime[newer] > passTime[older] {
            weight = Float(min(1, max(0, (time - passTime[older]) / (passTime[newer] - passTime[older]))))
        }
        if newer < 0 { newer = older }
        func value(_ index: Int) -> Float {
            let a = passValues[older * 3 + index], b = passValues[newer * 3 + index]
            return a + (b - a) * weight
        }
        let idle = time - newestPassTime - Self.idleAfter
        let fade = idle > 0 ? Float(exp(-idle / Self.idleFade)) : 1
        f.bass = value(0) * fade
        f.vocal = value(1) * fade
        f.pad = value(2) * fade
        return f
    }
}

/// The newest onsets of one transient class, oldest overwritten first.
private struct OnsetLog {
    var times = [Double](repeating: -.infinity, count: SoundTypeHistory.onsetCapacity)
    var strengths = [Float](repeating: 0, count: SoundTypeHistory.onsetCapacity)
    /// Onsets ever recorded (wrapping), matching the detector's counter.
    var total: UInt32 = 0
    var head = 0
    var stored = 0

    mutating func record(count: UInt32, newest: SoundOnset) {
        if count != total {
            let capacity = SoundTypeHistory.onsetCapacity
            // Never step back in time, so the newest stored onset stays the
            // newest one shown.
            let time = stored > 0 ? max(newest.time, times[head]) : newest.time
            head = stored > 0 ? (head + 1) % capacity : 0
            times[head] = time
            strengths[head] = newest.strength
            stored = min(stored + 1, capacity)
            total = count
        } else if stored > 0 {
            // The detector's top-up: the newest onset may still grow.
            strengths[head] = max(strengths[head], newest.strength)
        }
    }

    /// Onsets known by `time` (the display may lag capture), the newest of
    /// them, and the envelope they make. The ring gets the newest of them in
    /// order, so its entry k is the k-th newest onset counted.
    func read(at time: Double, shape: SoundOnsetShape, into ring: inout SoundOnsetRing)
        -> (count: UInt32, time: Double, envelope: Float) {
        let capacity = SoundTypeHistory.onsetCapacity
        var count = total, newest = -Double.infinity, envelope = 0.0, filled = 0
        times.withUnsafeBufferPointer { times in
            strengths.withUnsafeBufferPointer { strengths in
                var back = 0
                while back < stored {
                    let slot = (head - back + capacity) % capacity
                    let onset = times[slot]
                    back += 1
                    if onset > time { count &-= 1; continue }
                    if filled == 0 { newest = onset }
                    if filled < SoundOnsetRing.capacity {
                        ring[filled] = SoundOnset(time: onset, strength: strengths[slot])
                        filled += 1
                    }
                    envelope = max(envelope, Double(strengths[slot]) * shape.value(at: time - onset))
                }
            }
        }
        return (count, newest, Float(min(1, envelope)))
    }
}

/// What one surface's scene is shown of the sound types. AudioVisualizerView
/// swaps in an ambient spectrum while the input is too quiet to show (its
/// idle scene), and the sound types follow that switch instead of cutting:
///
/// - The sustained classes fade out, and back in, over `ramp` seconds of
///   wall-clock time from the frame the scene switched: a function of the
///   elapsed time, so every frame rate fades alike.
/// - An onset that starts while the scene is idle is hidden: it never
///   enters the rings, its flare never shows and the counters hold. When the
///   music returns they carry on from where they held, so every step of a
///   counter is an onset this scene was shown. The onset that brought the
///   music back (up to `lead` earlier) still shows. Whether an onset is
///   hidden depends on its start, not on the frame that first saw it.
/// - Onsets shown before the scene went idle finish their flares.
///
/// The counters are therefore this surface's own: a surface that never went
/// idle may have counted more. They never decrease, and restart only with
/// the source (VisualizerAudio.reset), which this follows. Transient
/// envelopes are re-read from the filtered rings with the normal shapes.
struct SoundTypeIdleGate {
    /// How long the sustained classes take to fade out or in, in seconds.
    static let ramp = 0.25
    /// An onset this long before the music returns still shows, in seconds.
    static let lead = 0.1

    private var presence: Float = 1
    private var lastClock: Double?
    private var live = true
    /// Onsets starting in `hiddenFrom..<hiddenUntil` (display times) are
    /// hidden: from the frame the scene went idle until `lead` before the
    /// frame it came back, open-ended while it is idle.
    private var hiddenFrom = Double.infinity
    private var hiddenUntil = Double.infinity
    private var kick = Hidden(), snare = Hidden(), hat = Hidden()

    /// How much of the sustained classes shows, 0...1.
    var sustainedPresence: Float { presence }

    /// The sound types `sound` as this surface shows them at wall-clock time
    /// `clock` (seconds, any monotonic clock), `live` being whether the scene
    /// shows the live audio rather than its idle stand-in.
    mutating func frame(_ sound: SoundTypeFeatures, live: Bool, at clock: Double) -> SoundTypeFeatures {
        // The time since the last frame passed in the state that frame had;
        // a switch takes effect from this frame on.
        if let lastClock {
            let step = Float(max(0, clock - lastClock) / Self.ramp)
            presence = self.live ? min(1, presence + step) : max(0, presence - step)
        } else {
            presence = live ? 1 : 0
        }
        lastClock = clock
        let time = sound.presentationTime
        let returning = live && !self.live
        if !live, self.live {
            hiddenFrom = time
            hiddenUntil = .infinity
        } else if returning {
            hiddenUntil = time - Self.lead
        }
        self.live = live
        let window = (from: hiddenFrom, until: hiddenUntil)
        var shown = sound
        (shown.kickOnsets, shown.kickTime, shown.kickRing) =
            kick.show(sound.kickOnsets, sound.kickRing, hiding: window, live: live, returning: returning)
        (shown.snareOnsets, shown.snareTime, shown.snareRing) =
            snare.show(sound.snareOnsets, sound.snareRing, hiding: window, live: live, returning: returning)
        (shown.hatOnsets, shown.hatTime, shown.hatRing) =
            hat.show(sound.hatOnsets, sound.hatRing, hiding: window, live: live, returning: returning)
        let shapes = SoundTypeShapes.normal
        shown.kick = shapes.kick.envelope(shown.kickRing, at: time)
        shown.snare = shapes.snare.envelope(shown.snareRing, at: time)
        shown.hat = shapes.hat.envelope(shown.hatRing, at: time)
        shown.bass = sound.bass * presence
        shown.vocal = sound.vocal * presence
        shown.pad = sound.pad * presence
        return shown
    }

    /// The hidden onsets of one class. The live ring's entry k is the k-th
    /// newest onset counted (SoundTypeHistory.read), and bit k of `mask`
    /// says whether that onset is hidden.
    private struct Hidden {
        var started = false
        var mask: UInt32 = 0
        /// The live count `mask` refers to.
        var counted: UInt32 = 0
        /// Onsets hidden since the source started.
        var total: UInt32 = 0
        /// The newest onset shown.
        var newest = -Double.infinity

        mutating func show(_ count: UInt32, _ ring: SoundOnsetRing, hiding window: (from: Double, until: Double),
                           live: Bool, returning: Bool) -> (count: UInt32, time: Double, ring: SoundOnsetRing) {
            // The first frame adopts what is there; a count that went back
            // means a new source, whose counters restarted from zero.
            if !started || count &- counted > UInt32.max / 2 {
                self = Hidden()
                started = true
                counted = count
            }
            let new = count &- counted
            counted = count
            mask = new < 32 ? mask << new : 0
            // The onsets counted now are the ring's first `new` entries: each
            // is hidden if it started inside the window. Older ones beyond the
            // ring (after a long pause) are hidden while the scene is idle.
            let inRing = Int(min(new, UInt32(SoundOnsetRing.capacity)))
            var k = 0
            while k < inRing {
                let start = ring[k].time
                if start >= window.from, start < window.until {
                    mask |= 1 << k
                    total &+= 1
                }
                k += 1
            }
            if !live { total &+= new - UInt32(inRing) }
            var filled = 0
            var shown = SoundOnsetRing()
            k = 0
            while k < SoundOnsetRing.capacity, ring[k].time > -.infinity {
                let bit: UInt32 = 1 << k
                if returning, mask & bit != 0, ring[k].time >= window.until {
                    // The hit that brought the music back.
                    mask &= ~bit
                    total &-= 1
                }
                if mask & bit == 0 {
                    shown[filled] = ring[k]
                    filled += 1
                }
                k += 1
            }
            if filled > 0 { newest = max(newest, shown[0].time) }
            return (count &- total, newest, shown)
        }
    }
}
