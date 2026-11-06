import Foundation
import simd

// StarTrailsSimulation is the CPU half of Star Trails (StarTrails.metal). It owns
// everything with memory and packs the 195-float4 StarTrailsState (3,120 bytes) that
// starTrailsFragment reads at fragment buffer 6:
//   * the two craft: a formation orbit (180° apart, clockwise, 3.55 s, 0.095 × 0.075 H)
//     with seeded wander. They glide: the path, speed and heading are smooth functions of
//     the motion clock, and music shapes them only through `cruise`, a critically damped
//     (three-pole) average of the level over several seconds that sets the cruising speed
//     and the weave's width. Hits never move a craft; they light it instead (below). The
//     heading is the path's analytic tangent, so it never swings on a hit either;
//   * light from hits, each rising from zero when the simulation learns of the onset, never
//     mid-flare: a kick lays a bright packet into the trail being laid and blooms the craft's
//     engines, a snare puts a brief sparkle and rim light on each craft;
//   * the trail history: space-frame craft positions per substep, drifting to the lower
//     left at 0.97 H/s, resampled to 56 uniform axis samples per trail. Each sample keeps
//     the width and glow it was laid with, so kick packets, sung phrases (pink, warmer
//     while the voice sings) and bass notes (lime) ride away;
//   * the scenery's drift: the camera follows the craft, so the clouds, both star layers and
//     the asteroids drift steadily along −f, against the craft's mean heading f (their path
//     is f × drift × t plus a bounded orbit and wander, so f is their mean direction of
//     travel, and the shader builds its sky frame from the same axis). Parallax: clouds
//     0.05, dust stars 0.08, bright stars 0.12, asteroids 0.14–0.34 H/s, each constant in
//     motion time, so the drift never jerks; each scroll wraps at 256 H;
//   * onset rings per drum type (kick 6, snare 6, hat 8 slots). Onsets closer than the
//     minimum gap merge; slots × gap = flare life, so a lit slot is never reused (no pops);
//   * two seeded epicentres per kick (outside the lyric stage, low in the sky), riding with
//     the bright stars;
//   * envelopes, the 0.5 s calm ramp, the three scenery scrolls, morph angles (2π),
//     twinkle clock (64 s), vocal phase (4π) and ten asteroids;
//   * the sky's colours (StarTrailsSky: the violet original or the album cover's), which
//     crossfade in OKLab over about 2.5 s when the cover changes.
// One simulation serves every display (StarTrailsSimulations): it is aspect-free, and
// `pack(aspect:)` places it on each surface. Nothing allocates per frame.

/// The sound types one frame shows, with each onset's start on the simulation's wall clock
/// (nil = unknown: now). Envelopes are 0…1; counters only grow (a decrease is a reset).
struct StarTrailsSound: Equatable {
    var kick = 0.0, snare = 0.0, hat = 0.0
    var bass = 0.0, vocal = 0.0, pad = 0.0
    var kickOnsets: UInt32 = 0, snareOnsets: UInt32 = 0, hatOnsets: UInt32 = 0
    var kickTime: Double?, snareTime: Double?, hatTime: Double?
    var level = 0.0

    /// Sensitivity shaping: identity at 1, `1 − (1 − x)^s` otherwise.
    static func shaped(_ x: Float, sensitivity: Double) -> Double {
        let s = min(3, max(0.3, sensitivity.isFinite ? sensitivity : 1))
        return 1 - pow(1 - min(1, max(0, Double(x))), s)
    }

    /// What `frame` shows. The detector's types when it has read them; otherwise the
    /// analyser's bands stand in (kick = beat, hat = trebleBeat, bass = 60–150 Hz,
    /// vocal = 1.2–3 kHz less flux, pad = mid). `wallNow` is the simulation's wall time of
    /// this frame.
    static func from(_ frame: AudioSnapshot, sensitivity: Double, wallNow: Double) -> StarTrailsSound {
        var s = StarTrailsSound()
        let f = frame.sound
        s.level = Double(min(1, max(0, frame.level)))
        if f.presentationTime.isFinite {
            s.kick = shaped(f.kick, sensitivity: sensitivity); s.snare = shaped(f.snare, sensitivity: sensitivity)
            s.hat = shaped(f.hat, sensitivity: sensitivity); s.bass = shaped(f.bass, sensitivity: sensitivity)
            s.vocal = shaped(f.vocal, sensitivity: sensitivity); s.pad = shaped(f.pad, sensitivity: sensitivity)
            s.kickOnsets = f.kickOnsets; s.snareOnsets = f.snareOnsets; s.hatOnsets = f.hatOnsets
            func onWall(_ t: Double) -> Double? {
                t.isFinite ? wallNow - max(0, f.presentationTime - t) : nil
            }
            s.kickTime = onWall(f.kickTime); s.snareTime = onWall(f.snareTime); s.hatTime = onWall(f.hatTime)
        } else {
            let components = frame.components
            func component(_ i: Int) -> Float { i < components.count ? components[i] : 0 }
            s.kick = shaped(frame.beat, sensitivity: sensitivity)
            s.hat = shaped(frame.trebleBeat, sensitivity: sensitivity)
            s.bass = shaped(component(1), sensitivity: sensitivity)
            s.vocal = shaped(component(4) * (1 - min(1, max(0, frame.flux))), sensitivity: sensitivity)
            s.pad = shaped(frame.mid, sensitivity: sensitivity)
        }
        return s
    }
}

/// splitmix64 → [0, 1): the simulation's only randomness.
struct StarTrailsRandom {
    private var state: UInt64
    init(seed: UInt64) { state = seed }
    mutating func next() -> Double {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        z ^= z >> 31
        return Double(z >> 11) / Double(UInt64(1) << 53)
    }
    mutating func uniform(_ a: Double, _ b: Double) -> Double { a + (b - a) * next() }
    static func seeded(_ index: Int, _ salt: Int) -> StarTrailsRandom {
        StarTrailsRandom(seed: UInt64(truncatingIfNeeded: index &* 0x9E37_79B1 &+ salt &* 0x85EB_CA77) & 0xFFFF_FFFF)
    }
}

/// Slot indices of StarTrailsState (float4 units), mirrored by the `constant int`s at the
/// top of StarTrails.metal (StarTrailsTests parses them).
enum StarTrailsLayout {
    static let hdr0 = 0, hdr1 = 1, hdr2 = 2, env0 = 3, env1 = 4, stage = 5, clock = 6
    static let craft = 7, onset = 11, live = 51, asteroid = 52, trail = 72
    static let trailSamples = 56, asteroids = 10
    static let sky = trail + 2 * trailSamples              // 184: StarTrailsSky.count colours
    static let skyTints = sky + StarTrailsSky.count        // 195: nine album tints
    static let float4Count = skyTints + StarTrailsSky.tintCount
    static let byteCount = float4Count * 16
    /// The fragment buffer index the state is bound at.
    static let bufferIndex = 6
}

final class StarTrailsSimulation {
    enum Flight {
        case centre, beside
        var uv: SIMD2<Double> { self == .centre ? [0.488, 0.491] : [0.70, 0.64] }
    }
    // Motion (on-screen seconds at the default Speed), measured from the reference.
    static let axis = 29.5 * Double.pi / 180
    static let orbitPeriod = 3.55, orbitAlong = 0.095, orbitCross = 0.075
    static let drift = 0.97
    /// The scenery's drift against the flight, H per motion second (far layers slower).
    static let cloudDrift = 0.05, dustDrift = 0.08, starDrift = 0.12
    static let trailS0 = -0.16, trailS1 = 1.75
    static let substep = 1.0 / 120.0
    static let world = 256.0
    /// History keeps a sample at most every 1/240 s of motion, so slow motion (Reduce
    /// Motion, a low Speed) does not need a longer ring.
    static let historySpacing = 1.0 / 240.0
    static let historyCapacity = 768
    // Onset rings: slots, life, minimum gap, first slot; slots × gap ≥ life.
    struct Ring { let slots: Int, life: Double, gap: Double, first: Int }
    static let rings = [Ring(slots: 6, life: 0.90, gap: 0.15, first: 0), Ring(slots: 6, life: 0.78, gap: 0.13, first: 6),
                        Ring(slots: 8, life: 0.44, gap: 0.055, first: 12)]

    struct Follower {
        var attack: Double, release: Double, v: Double = 0
        mutating func step(_ target: Double, _ dt: Double) {
            let tau = target > v ? attack : release
            v += (target - v) * (1 - exp(-dt / max(1e-4, tau)))
        }
    }
    /// Three equal first-order lags in series: a critically damped smoother whose output and
    /// its first two derivatives stay continuous whatever the input does, so a quantity it
    /// shapes (the craft's cruising speed and weave) can change only slowly and smoothly.
    struct Smooth3 {
        let tau: Double
        private(set) var a: Double, b: Double, c: Double
        init(tau: Double, value: Double) { self.tau = tau; a = value; b = value; c = value }
        var value: Double { c }
        /// d value / dt (per wall second).
        var rate: Double { (b - c) / tau }
        mutating func step(_ target: Double, _ dt: Double) {
            let k = 1 - exp(-dt / tau)
            a += (target - a) * k; b += (a - b) * k; c += (b - c) * k
        }
    }
    struct Wander {
        var f = SIMD4<Double>(), p = SIMD4<Double>(), a = SIMD4<Double>()
        init(seed: UInt64) {
            var r = StarTrailsRandom(seed: seed)
            for i in 0..<4 { f[i] = r.uniform(0.11, 0.37); p[i] = r.uniform(0, 6.283); a[i] = r.uniform(0.4, 1.0) }
            a /= a.sum()
        }
        func callAsFunction(_ t: Double) -> Double {
            var v = 0.0
            for i in 0..<4 { v += a[i] * sin(2 * .pi * f[i] * t + p[i]) }
            return v
        }
        /// d/dt of the wander.
        func rate(_ t: Double) -> Double {
            var v = 0.0
            for i in 0..<4 { v += a[i] * 2 * .pi * f[i] * cos(2 * .pi * f[i] * t + p[i]) }
            return v
        }
    }
    /// `t`: the onset's time on the wall clock (stars ripple from it). `lit`: when its light
    /// on the craft and trails starts, the substep the simulation learned of it, so that
    /// light always rises from zero. `sky`: the bright stars' scroll then (a kick's epicentres
    /// ride with them).
    struct Onset {
        var serial: Int; var t: Double; var lit: Double; var strength: Double; var pan: Double; var extra: SIMD4<Double>
        var sky: Double
    }
    struct Sample { var t = 0.0, x = 0.0, y = 0.0, glow = 0.0, width = 0.0, hot = 0.0 }
    /// A fixed ring of trail samples, oldest to newest.
    struct History {
        private var buffer = [Sample](repeating: Sample(), count: StarTrailsSimulation.historyCapacity)
        private var start = 0
        private(set) var count = 0
        /// `back` 0 is the newest sample.
        subscript(back back: Int) -> Sample {
            buffer[(start + count - 1 - back) % buffer.count]
        }
        var oldest: Sample { buffer[start] }
        mutating func append(_ s: Sample) {
            if count == buffer.count { start = (start + 1) % buffer.count; count -= 1 }
            buffer[(start + count) % buffer.count] = s
            count += 1
        }
        mutating func replaceNewest(_ s: Sample) { buffer[(start + count - 1) % buffer.count] = s }
        mutating func dropOldest() { start = (start + 1) % buffer.count; count -= 1 }
        mutating func shift(t dt: Double, by d: SIMD2<Double>) {
            for i in 0..<count {
                let j = (start + i) % buffer.count
                buffer[j].t += dt; buffer[j].x += d.x; buffer[j].y += d.y
            }
        }
    }
    struct Asteroid {
        var x = 0.0, y = 0.0, vx = 0.0, vy = 0.0, radius = 0.0, rot = 0.0, spin = 0.0, rimGain = 0.0
        var seed = 0.0, blur = 0.0, life = 0.0, age = 0.0, lit = 0.0
    }

    // Options and safety state.
    var punch = 1.0                  // VisualizerResponse.punch, 0…2
    var starLight = 1.0              // gain of every sound-type star
    var calmTarget = 0.0             // 1 when VisualizerResponse.calm (Reduce Motion or Dim Flashing Lights)
    var stage = SIMD4<Double>(0.5, 0.5, 0.32, 0.24)   // lyric stage ellipse (uv centre, uv radii)
    var freezeSky: Double?           // tests only: the same sky for every case

    private(set) var wall = 0.0, motion = 0.0
    private(set) var calm = 0.0
    /// Scenery scrolls along the flight axis, [0, 256) H: bright stars (`sky`), dust stars, clouds.
    private(set) var sky = 0.0, dustSky = 0.0, cloudSky = 0.0
    private var thetaA = 1.3, thetaB = 4.1, twinkle = 0.0, vocalPhase = 0.0
    private var phase = 2 * Double.pi * -0.3 / StarTrailsSimulation.orbitPeriod
    /// The only musical input to motion: the level, critically damped over several seconds.
    private(set) var cruise = Smooth3(tau: 0.8, value: 0.3)
    private var display = [Follower(attack: 0.03, release: 0.12), Follower(attack: 0.03, release: 0.12),
                           Follower(attack: 0.03, release: 0.12), Follower(attack: 0.12, release: 0.5),
                           Follower(attack: 0.10, release: 0.6), Follower(attack: 0.8, release: 1.6)]
    private var level = Follower(attack: 0.3, release: 0.8, v: 0.1)
    private var breath = Follower(attack: 0.6, release: 1.4)
    /// Kick bloom on the engines (light only), following the kick packets.
    private var engine = Follower(attack: 0.04, release: 0.30)
    /// The snare's sparkle on the craft (light only), 0…1.
    private var sparkle = 0.0
    /// Each craft's heading: the analytic tangent of its path in the space frame.
    private var headings = SIMD2<Double>(0, 0)
    /// The sky's colours, crossfading to the latest target.
    private var skyColours: StarTrailsSkyFade
    private let wander: [(Wander, Wander)]
    private var history = [History(), History()]
    private var onsets: [[Onset]] = [[], [], []]
    private var serial = [0, 0, 0]
    private var lastCount: [UInt32?] = [nil, nil, nil]
    private var pending: [[Double]] = [[], [], []]
    private static let pendingCapacity = 16
    private var asteroidRandom: StarTrailsRandom
    private var asteroids: [Asteroid] = []
    private var sound = StarTrailsSound()
    /// The youngest age at which a slot of each kind was evicted (tests: ≥ its life).
    private(set) var youngestEviction = [Double.infinity, .infinity, .infinity]

    private let f = SIMD2(cos(StarTrailsSimulation.axis), sin(StarTrailsSimulation.axis))
    private let n = SIMD2(-sin(StarTrailsSimulation.axis), cos(StarTrailsSimulation.axis))
    /// The sky frame's x axis (across the flight); its y axis is f. Mirrors the shader's.
    private let m = SIMD2(sin(StarTrailsSimulation.axis), -cos(StarTrailsSimulation.axis))

    init(seed: UInt64 = 3, calm: Bool = false, sky: StarTrailsSky = .original) {
        skyColours = StarTrailsSkyFade(sky)
        calmTarget = calm ? 1 : 0
        self.calm = calmTarget
        wander = [(Wander(seed: seed * 11 + 1), Wander(seed: seed * 11 + 2)), (Wander(seed: seed * 11 + 3), Wander(seed: seed * 11 + 4))]
        asteroidRandom = StarTrailsRandom(seed: seed + 101)
        for k in 0..<3 {
            onsets[k].reserveCapacity(Self.rings[k].slots + 1)
            pending[k].reserveCapacity(Self.pendingCapacity)
        }
        asteroids.reserveCapacity(StarTrailsLayout.asteroids)
        for _ in 0..<StarTrailsLayout.asteroids { asteroids.append(spawn(prewarm: true)) }
        // Prewarm 2.2 s of near-silence so the trails reach past the screen corner before intro fades in.
        sound.level = 0.04
        for _ in 0..<Int((2.2 / Self.substep).rounded()) { substep(motionDt: Self.substep, wallDt: Self.substep) }
        wall = 0
    }

    // MARK: Advancing

    /// The sky to show; a change crossfades over about 2.5 s of wall time.
    var skyTarget: StarTrailsSky {
        get { skyColours.target }
        set { skyColours.aim(at: newValue) }
    }

    /// One rendered frame. `wallDelta`: real seconds (envelopes, onset ages, flares).
    /// `motionDelta`: the scene-time step over 0.70 (Speed and Reduce Motion's ×0.15
    /// already applied). The frame is split into equal substeps of at most 1/120 s, so the
    /// state lands exactly on the frame time. Zero for both changes nothing.
    func advance(wallDelta: Double, motionDelta: Double, sound: StarTrailsSound) {
        self.sound = sound
        collectOnsets()
        let wallDt = min(max(wallDelta.isFinite ? wallDelta : 0, 0), 0.25)
        let motionDt = min(max(motionDelta.isFinite ? motionDelta : 0, 0), 0.25)
        guard wallDt > 0 || motionDt > 0 else { return }
        let steps = max(1, Int((wallDt / Self.substep - 1e-9).rounded(.up)))
        for _ in 0..<steps { substep(motionDt: motionDt / Double(steps), wallDt: wallDt / Double(steps)) }
        if motion > 1e5 { rebase() }
    }

    /// Shifts the motion clock back to 0 after about a day, before Double precision matters.
    private func rebase() {
        let shift = f * (Self.drift * motion)
        for c in 0..<2 { history[c].shift(t: -motion, by: -shift) }
        motion = 0
    }

    private func envelope(_ kind: Int) -> Double {
        switch kind { case 0: return sound.kick; case 1: return sound.snare; default: return sound.hat }
    }

    private func collectOnsets() {
        for k in 0..<3 {
            let count: UInt32, time: Double?
            switch k {
            case 0: count = sound.kickOnsets; time = sound.kickTime
            case 1: count = sound.snareOnsets; time = sound.snareTime
            default: count = sound.hatOnsets; time = sound.hatTime
            }
            guard let last = lastCount[k], count >= last else { lastCount[k] = count; continue }   // first frame or reset
            let new = Int(min(4, count - last))
            if new > 0 {
                var onset = time ?? wall
                if onset > wall + 0.25 { onset = wall }
                for i in 0..<new where pending[k].count < Self.pendingCapacity {
                    pending[k].append(max(onset - Double(new - 1 - i) * 0.03, wall - 0.25))
                }
            }
            lastCount[k] = count
        }
    }

    /// `lit`: the start of the substep that accepts it (or the onset itself, if later).
    private func accept(_ kind: Int, at t: Double, lit: Double) {
        let ring = Self.rings[kind]
        if let newest = onsets[kind].last, t - newest.t < ring.gap { return }   // merge into the previous onset
        serial[kind] = (serial[kind] + 1) % (1 << 24)
        var rng = StarTrailsRandom.seeded(serial[kind], 11 + kind)
        var extra = SIMD4<Double>(repeating: 0)
        if kind == 0 { extra = epicentres(&rng) } else if kind == 2 { extra = [dustSky, 0, 0, 0] }   // hats glitter in the dust
        let strength = min(1, max(0.3, 0.3 + 0.9 * envelope(kind)))
        onsets[kind].append(Onset(serial: serial[kind], t: t, lit: max(t, lit), strength: strength, pan: 0, extra: extra, sky: sky))
        if onsets[kind].count > ring.slots {
            youngestEviction[kind] = min(youngestEviction[kind], wall - onsets[kind][0].t)
            onsets[kind].removeFirst()
        }
    }

    /// Two kick epicentres (uv on the screen at the onset) outside the lyric stage, low in the
    /// sky, 0.45 H apart. `pack` places them in the bright stars' sky frame.
    func epicentres(_ rng: inout StarTrailsRandom) -> SIMD4<Double> {
        var first: SIMD2<Double>?, second: SIMD2<Double>?
        var tries = 0
        while second == nil && tries < 64 {
            tries += 1
            let u = rng.uniform(0.04, 0.96)
            let y = 0.06 + 0.80 * pow(rng.next(), 1.6)
            let e = SIMD2((u - stage.x) / stage.z, (y - stage.y) / stage.w)
            if simd_length_squared(e) < 1.15 * 1.15 { continue }
            if let first {
                if simd_length(SIMD2((u - first.x) * 16 / 9, y - first.y)) < 0.45 { continue }
                second = [u, y]
            } else {
                first = [u, y]
            }
        }
        let a = first ?? [0.12, 0.2], b = second ?? (first == nil ? [0.88, 0.2] : [a.x < 0.5 ? 0.88 : 0.12, 0.2])
        return [a.x, a.y, b.x, b.y]
    }

    private func spawn(prewarm: Bool = false) -> Asteroid {
        var a = Asteroid()
        let depth = pow(asteroidRandom.next(), 1.4)
        let speed = 0.14 + 0.20 * depth                  // nearer rocks drift faster, all faster than the stars
        let angle = Self.axis + .pi                      // against the flight, as all the scenery
        let across = asteroidRandom.uniform(-0.34, 0.34)
        let ahead = prewarm ? asteroidRandom.uniform(-0.85, 0.75) : asteroidRandom.uniform(0.35, 0.85)
        a.radius = 0.007 + 0.016 * depth + asteroidRandom.uniform(0, 0.003)
        a.rot = asteroidRandom.uniform(0, 6.283)
        a.spin = (asteroidRandom.next() < 0.5 ? -1.0 : 1.0) * asteroidRandom.uniform(0.25, 0.9)
        a.rimGain = asteroidRandom.uniform(0.6, 1.0)
        a.seed = Double(Int(asteroidRandom.next() * 61))
        a.life = asteroidRandom.uniform(6, 11)
        a.vx = speed * cos(angle); a.vy = speed * sin(angle)
        a.blur = Self.smoothstep(0.62, 0.95, depth)
        a.x = f.x * ahead + n.x * across; a.y = f.y * ahead + n.y * across
        if prewarm { a.age = asteroidRandom.uniform(0.8, a.life - 0.8) }
        return a
    }

    private func substep(motionDt dtM: Double, wallDt dtW: Double) {
        motion += dtM
        wall += dtW
        let t = motion, w = wall
        calm += min(max(calmTarget - calm, -dtW / 0.5), dtW / 0.5)              // 0.5 s ramp: a toggle never pops
        skyColours.step(dtW)
        let transient = min(max(punch, 0), 2) * (1 - calm)
        for k in 0..<3 {
            // Accept the onsets now due, oldest first; their light starts with this substep.
            while true {
                var due = -1
                for i in pending[k].indices where pending[k][i] <= w && (due < 0 || pending[k][i] < pending[k][due]) { due = i }
                guard due >= 0 else { break }
                accept(k, at: pending[k].remove(at: due), lit: w - dtW)
            }
            let target = min(1, max(0.3, 0.3 + 0.9 * envelope(k)))
            for i in onsets[k].indices where w - onsets[k][i].t < 0.08 {      // strength: envelope peak, rate-limited
                onsets[k][i].strength = min(max(onsets[k][i].strength, target), onsets[k][i].strength + dtW / 0.09)
            }
        }
        display[0].step(sound.kick, dtW); display[1].step(sound.snare, dtW); display[2].step(sound.hat, dtW)
        display[3].step(sound.bass, dtW); display[4].step(sound.vocal, dtW); display[5].step(sound.pad, dtW)
        level.step(sound.level, dtW)
        cruise.step(sound.level, dtW)
        let e = cruise.value
        if calmTarget > 0.5 { breath.attack = 1.2; breath.release = 2.0 } else { breath.attack = 0.6; breath.release = 1.4 }
        breath.step(sound.pad, dtW)
        vocalPhase = (vocalPhase + dtW * 1.6 * sound.vocal).truncatingRemainder(dividingBy: 4 * .pi)
        twinkle = (twinkle + dtW).truncatingRemainder(dividingBy: 64)
        let omega = 2 * .pi / Self.orbitPeriod * (0.84 + 0.24 * e)            // orbit rate, per motion second
        phase = (phase + omega * dtM).truncatingRemainder(dividingBy: 2 * .pi)
        // The scenery drifts against the flight at constant speeds (the camera follows the craft).
        sky = (sky + Self.starDrift * dtM).truncatingRemainder(dividingBy: Self.world)
        dustSky = (dustSky + Self.dustDrift * dtM).truncatingRemainder(dividingBy: Self.world)
        cloudSky = (cloudSky + Self.cloudDrift * dtM).truncatingRemainder(dividingBy: Self.world)
        thetaA = (thetaA + (0.030 + 0.03 * e + 0.02 * breath.v) * dtM).truncatingRemainder(dividingBy: 2 * .pi)
        thetaB = (thetaB + (0.0185 + 0.02 * e) * dtM).truncatingRemainder(dividingBy: 2 * .pi)
        if let frozen = freezeSky { sky = frozen; dustSky = frozen; cloudSky = frozen; thetaA = 1.3; thetaB = 4.1 }
        // Hits are light, never motion: kick packets in the trails being laid and the engines'
        // bloom, the snare's sparkle. Each rises from zero at its `lit` time.
        var pulse = 0.0, flash = 0.0
        for o in onsets[0] { pulse = max(pulse, o.strength * Self.bump(w - o.lit, tau: Self.packetPeak)) }
        for o in onsets[1] { flash = max(flash, o.strength * Self.bump(w - o.lit, tau: Self.sparklePeak)) }
        pulse *= transient
        sparkle = min(1, flash * transient)
        engine.step(pulse, dtW)
        let size = 0.88 + 0.18 * e
        let sizeRate = dtM > 1e-9 ? 0.18 * cruise.rate * min(dtW / dtM, 50) : 0   // d size / d motion time
        for c in 0..<2 {
            let ph = phase + (c == 1 ? 0 : .pi)                                  // craft 0 = pink (voice), 1 = lime (bass)
            let sinPh = sin(ph), cosPh = cos(ph)
            let along = Self.orbitAlong * size * sinPh + 0.004 * wander[c].0(t)
            let cross = Self.orbitCross * size * cosPh + 0.004 * wander[c].1(t)
            let p = f * along + n * cross + f * (Self.drift * t)               // space frame, relative to the centre
            if dtM > 1e-9 {                                                      // the path's tangent (kept while paused)
                let dAlong = Self.orbitAlong * (sizeRate * sinPh + size * cosPh * omega) + 0.004 * wander[c].0.rate(t)
                let dCross = Self.orbitCross * (sizeRate * cosPh - size * sinPh * omega) + 0.004 * wander[c].1.rate(t)
                let v = f * (dAlong + Self.drift) + n * dCross
                headings[c] = atan2(v.y, v.x)
            }
            let lead = c == 0 ? display[4].v : display[3].v
            let soft = 1 - 0.5 * calm
            let sample = Sample(t: t, x: p.x, y: p.y, glow: 1 + 1.2 * pulse + 0.30 * soft * lead + 0.08 * (level.v - 0.4),
                                width: 1 + 0.35 * soft * lead, hot: pulse)
            if history[c].count >= 2, t - history[c][back: 1].t < Self.historySpacing {
                history[c].replaceNewest(sample)
            } else {
                history[c].append(sample)
            }
            while history[c].count > 4, (t - history[c].oldest.t) * Self.drift > Self.trailS1 + 0.6 { history[c].dropOldest() }
        }
        for i in asteroids.indices {
            var a = asteroids[i]
            a.x += a.vx * dtM; a.y += a.vy * dtM
            let kick = display[0].v * (1 - calm) * a.rimGain
            a.rot += a.spin * (0.85 + 0.5 * e) * dtM                           // a little livelier when loud, never per hit
            a.rot = a.rot.truncatingRemainder(dividingBy: 2 * .pi)
            a.age += dtM
            a.lit += (kick - a.lit) * (1 - exp(-dtW / (kick > a.lit ? 0.09 : 0.35)))   // a kick catches the rim (light)
            if a.age > a.life || abs(a.x) > 1.9 || abs(a.y) > 0.62 { a = spawn() }
            asteroids[i] = a
        }
    }

    // MARK: Shapes

    static func smoothstep(_ a: Double, _ b: Double, _ x: Double) -> Double {
        let t = min(max((x - a) / (b - a), 0), 1); return t * t * (3 - 2 * t)
    }
    /// The light of a hit: zero value and slope at age 0 (it never pops), peak 1 at age = tau,
    /// then a smooth fall (16 % at 3 tau). The 10-90 % rise takes 0.58 tau.
    static func bump(_ age: Double, tau: Double) -> Double {
        guard age > 0 else { return 0 }
        let x = age / tau; return x * x * exp(2 * (1 - x))
    }
    /// A kick's packet peaks 100 ms after it starts (58 ms rise) and has faded by 0.4 s; the
    /// snare's sparkle peaks at 70 ms and is gone by 0.3 s.
    static let packetPeak = 0.10, sparklePeak = 0.07

    // MARK: Packing

    /// 56 samples at uniform axis distance s (cross offset, width, glow, hot); returns the head's s.
    private func trailSamples(_ c: Int, into out: UnsafeMutablePointer<SIMD4<Float>>) -> Double {
        let offset = f * (Self.drift * motion)
        let h = history[c]
        func point(_ back: Int) -> (s: Double, c: Double, e: Sample) {
            let e = h[back: back]
            let sp = SIMD2(e.x, e.y) - offset
            return (-simd_dot(sp, f), simd_dot(sp, n), e)
        }
        let head = point(0), next = point(1)
        let slope = (next.c - head.c) / max(1e-6, next.s - head.s)
        let ds = (Self.trailS1 - Self.trailS0) / Double(StarTrailsLayout.trailSamples - 1)
        var j = 0
        var a = head, b = next
        for i in 0..<StarTrailsLayout.trailSamples {
            let s = Self.trailS0 + Double(i) * ds
            var v: SIMD4<Double>
            if s <= head.s {
                v = [head.c + slope * (s - head.s), head.e.width, head.e.glow, head.e.hot]
            } else {
                while j + 1 < h.count && b.s < s { j += 1; a = b; if j + 1 < h.count { b = point(j + 1) } }
                if j + 1 >= h.count {
                    v = [a.c, a.e.width, a.e.glow, a.e.hot]
                } else {
                    let u = (s - a.s) / max(1e-9, b.s - a.s)
                    v = [a.c + (b.c - a.c) * u, a.e.width + (b.e.width - a.e.width) * u,
                         a.e.glow + (b.e.glow - a.e.glow) * u, a.e.hot + (b.e.hot - a.e.hot) * u]
                }
            }
            out[i] = SIMD4<Float>(v)
        }
        return head.s
    }

    /// The shader state for one surface, written to `st` (StarTrailsLayout.float4Count float4s).
    func pack(aspect: Double, flight: Flight = .centre, into st: UnsafeMutablePointer<SIMD4<Float>>) {
        typealias L = StarTrailsLayout
        st.update(repeating: .zero, count: L.float4Count)
        let aspect = aspect.isFinite && aspect > 0 ? aspect : 16.0 / 9
        let centre = SIMD2(flight.uv.x * aspect, flight.uv.y)
        st[L.hdr0] = SIMD4<Float>(SIMD4(centre.x, centre.y, Self.axis, aspect))
        st[L.hdr1] = SIMD4<Float>(SIMD4(Self.trailS0, (Self.trailS1 - Self.trailS0) / Double(L.trailSamples - 1), sky, dustSky))
        let punchGain = 0.55 + 0.45 * min(max(punch, 0), 2)
        st[L.hdr2] = SIMD4<Float>(SIMD4(thetaA, thetaB, calm, starLight * (punchGain + (0.7 - punchGain) * calm)))
        st[L.env0] = SIMD4<Float>(SIMD4(display[0].v, display[1].v, display[2].v, display[3].v))
        st[L.env1] = SIMD4<Float>(SIMD4(display[4].v, display[5].v, level.v, breath.v))
        st[L.stage] = SIMD4<Float>(stage)
        st[L.clock] = SIMD4<Float>(SIMD4(twinkle, vocalPhase, cloudSky, 1 / Self.drift))
        let offset = f * (Self.drift * motion)
        for c in 0..<2 {
            let h = history[c][back: 0]
            let p = centre + SIMD2(h.x, h.y) - offset
            let ph = phase + (c == 1 ? 0 : .pi)
            let headS = trailSamples(c, into: st + L.trail + c * L.trailSamples)
            let lead = c == 0 ? display[4].v : display[3].v
            st[L.craft + 2 * c] = SIMD4<Float>(SIMD4(p.x, p.y, headings[c], 0.55 * sin(ph)))
            // Engine glow (the trail's own sound swells it), head s, snare sparkle, kick bloom.
            st[L.craft + 2 * c + 1] = SIMD4<Float>(SIMD4(0.55 + 0.27 * lead, headS, sparkle, engine.v))
        }
        var live = SIMD4<Float>(0, 0, 0, Float(L.asteroids))
        for k in 0..<3 {
            let ring = Self.rings[k]
            var lit = 0
            for slot in 0..<ring.slots {
                let base = L.onset + 2 * (ring.first + slot)
                if slot < onsets[k].count {
                    let o = onsets[k][slot]
                    let age = wall - o.t
                    if age >= 0 && age <= ring.life { lit += 1 }
                    st[base] = SIMD4<Float>(SIMD4(Double(o.serial), age, o.strength, o.pan))
                    if k == 0 {
                        // The epicentres in the bright stars' sky frame (x across the flight, y along
                        // it plus their scroll at the onset), for this surface's aspect.
                        func onSky(_ u: Double, _ y: Double) -> SIMD2<Double> {
                            let p = SIMD2(u * aspect, y)
                            return [simd_dot(p, m), (simd_dot(p, f) + o.sky).truncatingRemainder(dividingBy: Self.world)]
                        }
                        let a = onSky(o.extra.x, o.extra.y), b = onSky(o.extra.z, o.extra.w)
                        st[base + 1] = SIMD4<Float>(SIMD4(a.x, a.y, b.x, b.y))
                    } else {
                        st[base + 1] = SIMD4<Float>(o.extra)
                    }
                } else {
                    st[base] = SIMD4<Float>(0, -1, 0, 0)
                }
            }
            live[k] = Float(lit)
        }
        st[L.live] = live
        skyColours.write(into: st + L.sky)
        for (i, a) in asteroids.enumerated() {
            let fade = Self.smoothstep(0, 0.8, a.age) * (1 - Self.smoothstep(a.life - 0.8, a.life, a.age))
            let rot = a.rot - 2 * .pi * (a.rot / (2 * .pi)).rounded(.down)
            st[L.asteroid + 2 * i] = SIMD4<Float>(SIMD4(centre.x + a.x, centre.y + a.y, a.radius, rot))
            st[L.asteroid + 2 * i + 1] = SIMD4<Float>(SIMD4(a.seed, a.blur, fade, a.lit))
        }
    }

    /// The state as an array (tests and the static render path).
    func pack(aspect: Double, flight: Flight = .centre) -> [SIMD4<Float>] {
        [SIMD4<Float>](unsafeUninitializedCapacity: StarTrailsLayout.float4Count) { buffer, count in
            pack(aspect: aspect, flight: flight, into: buffer.baseAddress!)
            count = StarTrailsLayout.float4Count
        }
    }

    /// A state for a static frame (tests and stills): a fresh simulation advanced through
    /// `time` seconds (at most 24) at 30 fps with `frame`'s sound held constant, under the
    /// sky of `palette` (nil: the original).
    static func snapshot(frame: AudioSnapshot, time: Double, sensitivity: Double, calm: Bool, aspect: Double,
                         palette: AlbumWallpaperPalette? = nil) -> [SIMD4<Float>] {
        let simulation = StarTrailsSimulation(calm: calm, sky: palette.map(StarTrailsSky.album) ?? .original)
        let frames = Int((min(24, max(0, time.isFinite ? time : 0)) * 30).rounded())
        for i in 0..<frames {
            let sound = StarTrailsSound.from(frame, sensitivity: sensitivity, wallNow: Double(i + 1) / 30)
            simulation.advance(wallDelta: 1.0 / 30, motionDelta: 1.0 / 30, sound: sound)
        }
        return simulation.pack(aspect: aspect)
    }
}

/// One Star Trails simulation shared by every surface on a set of timelines (each display,
/// and the copy that fades out in a scene change). It advances once per new scene time;
/// another display drawing the same frame only packs it for its own aspect.
@MainActor
final class StarTrailsSimulations {
    private(set) var simulation: StarTrailsSimulation?
    private var lastSceneTime = 0.0, lastClock = 0.0
    private var palette: AlbumWallpaperPalette?, sky = StarTrailsSky.original

    /// Advances to `sceneTime` (the shared timeline's value; the wallpaper's flow clock runs at
    /// 0.70 × wall × Speed) at `clock` seconds, and returns the simulation. `palette`: the cover
    /// whose colours the sky takes (nil: the original violet sky).
    @discardableResult
    func advance(sceneTime: Double, clock: Double, frame: AudioSnapshot, response: VisualizerResponse,
                 sensitivity: Double, palette: AlbumWallpaperPalette? = nil) -> StarTrailsSimulation {
        if palette != self.palette {
            self.palette = palette
            sky = palette.map(StarTrailsSky.album) ?? .original
        }
        simulation?.skyTarget = sky
        guard let simulation else {
            let made = StarTrailsSimulation(calm: response.calm, sky: sky)
            made.punch = VisualizerPreferences.clampedPunch(response.punch)
            simulation = made
            lastSceneTime = sceneTime; lastClock = clock
            made.advance(wallDelta: 0, motionDelta: 0, sound: .from(frame, sensitivity: sensitivity, wallNow: made.wall))
            return made
        }
        guard sceneTime != lastSceneTime else { return simulation }
        let wallDelta = min(0.25, max(0, clock - lastClock))
        // A rebase of the scene clock (it went back) is not motion.
        let motionDelta = max(0, sceneTime - lastSceneTime) / VisualClock.visualizerRate
        lastSceneTime = sceneTime; lastClock = clock
        simulation.calmTarget = response.calm ? 1 : 0
        simulation.punch = VisualizerPreferences.clampedPunch(response.punch)
        let sound = StarTrailsSound.from(frame, sensitivity: sensitivity, wallNow: simulation.wall + wallDelta)
        simulation.advance(wallDelta: wallDelta, motionDelta: motionDelta, sound: sound)
        return simulation
    }
}
