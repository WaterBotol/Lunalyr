import SwiftUI

// MARK: - Action words: the motion
//
// Two parts:
// - a whole-word pose (`LivelyPose`): turn, scale, tilt, travel, tint, glow,
//   applied around the word (`LivelyActionEffect`);
// - a per-letter pass (`LivelyGlyphRenderer`, a `TextRenderer`, macOS 15+)
//   that the layout's own word `Text` picks up from the environment
//   (`lyricLivelyGlyphs`, through `LivelyGlyphHook`): letter-by-letter hops,
//   shivers, dust, shards, drips, crystals, bubbles and sparkles, using the
//   real glyph bounds. macOS 14 draws the whole-word part only.
//
// Every effect is identity before the onset and from `duration` on. The
// duration grows with how long the word is held, and so may outlast the
// word's end by up to ~1 s; the strength ("Action word strength": Subtle,
// Middle, Wild) and the hold scale the size of every motion
// (`LivelyAction.intensity`). Under Reduce Motion a word only glows
// (`LivelyAction.reduceMotionCue`).

/// "Action word strength".
enum ActionWordStrength: String, CaseIterable, Identifiable {
    case subtle, middle, wild
    var id: String { rawValue }
    var title: String {
        switch self {
        case .subtle: return "Subtle"
        case .middle: return "Middle"
        case .wild: return "Wild"
        }
    }
    /// The motion's amount, 0...1.
    var amount: Double {
        switch self {
        case .subtle: return LivelyAction.subtle
        case .middle: return LivelyAction.middle
        case .wild: return LivelyAction.wild
        }
    }
}

private struct ActionStrengthKey: EnvironmentKey { static let defaultValue = LivelyAction.defaultAmount }
private struct LivelyGlyphsKey: EnvironmentKey { static let defaultValue: LivelyGlyphs? = nil }
private struct LivelyParticlesKey: EnvironmentKey { static let defaultValue = true }

extension EnvironmentValues {
    /// "Action word strength" as an amount, 0...1 (set once at the lyric
    /// layer's root; Wild by default).
    var lyricActionStrength: Double {
        get { self[ActionStrengthKey.self] }
        set { self[ActionStrengthKey.self] = newValue }
    }
    /// The per-letter part of an action in progress, for the word's `Text`.
    var lyricLivelyGlyphs: LivelyGlyphs? {
        get { self[LivelyGlyphsKey.self] }
        set { self[LivelyGlyphsKey.self] = newValue }
    }
    /// False on a copy of the word (a tile's overlay), so particles are drawn once.
    var lyricLivelyParticles: Bool {
        get { self[LivelyParticlesKey.self] }
        set { self[LivelyParticlesKey.self] = newValue }
    }
}

enum LivelyAction {
    /// Wild.
    static let defaultAmount = 1.0
    static let subtle = 0.2, middle = 0.5, wild = 1.0
    /// A chorus repeat moves at this share of the strength (`WordAccent.gentle`).
    static let gentleShare = 0.35

    /// 0...1: the amount, scaled from 0.6× for a quickly sung word up to 1×
    /// for one held 1.2 s or longer.
    static func intensity(amount: Double, hold: Double) -> Double {
        let held = 0.6 + 0.4 * Easing.smoothstep((hold - 0.25) / 0.95)
        return min(1, max(0, amount)) * held
    }

    /// Seconds from the onset; longer for a longer-held word. Every curve
    /// has come to rest (to well under a pixel) a little before this.
    static func duration(_ action: WordAction, hold rawHold: Double) -> Double {
        let h = min(2.5, max(0.2, rawHold))
        switch action {
        case .spin: return h >= 0.9 ? 2.1 : 1.75
        case .float: return max(1.7, h + 0.9)
        case .dip: return 1.7
        case .shake: return max(0.9, min(h, 1.4) + 0.5)
        case .hop: return h >= 0.9 ? 1.55 : 1.15
        case .grow: return max(1.8, h + 1.0)
        case .shrink: return max(1.2, h + 0.6)
        case .dissolve: return max(2.3, h + 1.3)
        case .flicker: return max(1.5, h + 0.7)
        case .frost: return max(2.0, h + 1.0)
        case .bleed: return max(2.4, h + 1.2)
        case .dark: return max(1.9, h + 0.8)
        case .shatter: return 1.35
        case .melt: return max(1.7, h + 0.8)
        case .echo: return max(1.2, h + 0.5)
        case .pulse: return max(1.0, h + 0.5)
        case .wave: return max(1.3, h + 0.6)
        case .sink: return max(2.2, h + 1.1)
        }
    }

    // MARK: Curves
    //
    // The motion is built from damped springs (zero speed at the start, an
    // overshoot, a settle), sin² bumps for anticipation and squash, and
    // smoothsteps: every curve is continuous in value and speed, so nothing
    // pops, snaps or freezes at its start or its end.

    static func ss(_ x: Double) -> Double { Easing.smoothstep(x) }
    static func clamp01(_ x: Double) -> Double { min(1, max(0, x)) }
    /// In over `attack`, out over the last `release` of `duration`.
    static func envelope(_ t: Double, attack: Double, release: Double, duration: Double) -> Double {
        ss(t / attack) * (1 - ss((t - (duration - release)) / release))
    }
    static func easeOutBack(_ x: Double, _ s: Double) -> Double {
        let u = x - 1
        return 1 + (s + 1) * u * u * u + s * u * u
    }
    static func easeOutCubic(_ x: Double) -> Double { let u = 1 - clamp01(x); return 1 - u * u * u }

    /// A damped spring let go at t = 0, from 0 towards 1, starting at rest.
    /// `peak`: seconds to its first (overshooting) peak; `overshoot`: how
    /// far past 1 that peak goes (0.1 = 10 %).
    static func spring(_ t: Double, peak: Double, overshoot: Double) -> Double {
        guard t > 0 else { return 0 }
        let l = log(max(1e-4, overshoot))
        let zeta = -l / sqrt(.pi * .pi + l * l)
        let wd = .pi / peak
        let w = wd / sqrt(1 - zeta * zeta)
        return 1 - exp(-zeta * w * t) * (cos(wd * t) + zeta * w / wd * sin(wd * t))
    }
    /// A sin² hump over [0, width]: starts and ends at rest.
    static func bump(_ t: Double, _ width: Double) -> Double {
        guard t > 0, t < width else { return 0 }
        let s = sin(.pi * t / width)
        return s * s
    }
    /// 1, falling smoothly to 0 over the last `last` seconds before `D`
    /// (by then whatever it multiplies has all but come to rest).
    static func rest(_ t: Double, _ D: Double, last: Double = 0.22) -> Double {
        1 - ss((t - (D - last)) / last)
    }
    /// Smootherstep: at rest at both ends, with no jolt (no jump in
    /// acceleration) either.
    static func ss5(_ x: Double) -> Double {
        let c = clamp01(x)
        return c * c * c * (c * (6 * c - 15) + 10)
    }
    /// A soft limit to ±`limit`.
    static func soft(_ x: Double, _ limit: Double) -> Double { limit * tanh(x / limit) }

    /// A seeded number in 0..<1.
    static func rnd(_ seed: Int, _ a: Int, _ b: Int) -> Double {
        var h = UInt64(bitPattern: Int64(seed &* 73_856_093 ^ a &* 19_349_663 ^ b &* 83_492_791))
        h ^= h >> 33; h &*= 0xff51afd7ed558ccd; h ^= h >> 33; h &*= 0xc4ceb9fe1a85ec53; h ^= h >> 33
        return Double(h % 10_000) / 10_000
    }
    /// A heartbeat's thump: a smooth swell to 1 at 70 ms and a fall
    /// (zero speed at the start, no corner at the peak).
    static func thump(_ u: Double) -> Double {
        guard u > 0 else { return 0 }
        let x = u / 0.07
        return x * x * exp(2 * (1 - x))
    }

    // MARK: Shared motions (whole word and letters agree)

    /// Spin: the turn in degrees. A wind-up the other way, then one or two
    /// whole turns (eased in and out, at most ~1200°/s, so the word reads
    /// as it goes round), swinging ~18° past, back a little, and settling.
    /// Continuous for all t: 0 before the onset, the whole turns (which
    /// draw as rest) from `D` on, so a difference across either end is the
    /// real motion, never a jump of 360° or 720°.
    static func spinAngle(_ t: Double, A: Double, hold: Double, duration D: Double) -> Double {
        let turns: Double = hold >= 0.9 && A > 0.35 ? 2 : 1
        let target = 360 * turns
        guard t > 0 else { return 0 }
        guard t < D else { return target }
        let start = 0.16, time = turns == 2 ? 1.15 : 0.85
        var x = clamp01((t - start) / time)
        x = x * x * x * (x * (6 * x - 15) + 10)
        let tau = t - (start + 0.72 * time)
        let swing = tau > 0 ? 40 * A * ss(tau / 0.2) * exp(-3.2 * tau) * sin(2 * .pi * 1.15 * tau) : 0
        let angle = -26 * A * bump(t, 0.34) + target * x + swing
        // Whatever is left of the settle (a degree or two) eases out to rest.
        return target + (angle - target) * rest(t, D, last: 0.3)
    }
    /// Spin: how fast it is turning, 0...1.
    static func spinSpeed(_ t: Double, A: Double, hold: Double, duration D: Double) -> Double {
        let dt = 1.0 / 240
        let v = (spinAngle(t + dt, A: A, hold: hold, duration: D) - spinAngle(t - dt, A: A, hold: hold, duration: D)) / (2 * dt)
        return min(1, abs(v) / 1100)
    }

    /// Hop: the whole word's jump, in ems (negative is up), with its squash
    /// and stretch (`sy`; `sx` keeps the volume) and lean, from the foot.
    static func hop(_ t: Double, A: Double, hold: Double, duration D: Double) -> (dy: Double, sy: Double, tilt: Double) {
        guard t > 0, t < D else { return (0, 1, 0) }
        var dy = 0.0, sy = 1.0, tilt = 0.0
        // Crouch, push off.
        sy -= 0.17 * A * bump(t, 0.24)
        // A ballistic arc: stretched while fast, round at the top.
        func flight(_ start: Double, _ time: Double, _ height: Double, _ lean: Double) {
            let v = (t - start) / time
            guard v > 0, v < 1 else { return }
            dy -= height * 4 * v * (1 - v)
            let fast = abs(1 - 2 * v) * ss(v / 0.16) * ss((1 - v) / 0.16)
            sy += 0.16 * A * fast * (height / (0.82 * max(0.01, A)))
            tilt += lean * sin(2 * .pi * v) * sin(.pi * v)
        }
        let land1 = 0.13 + 0.42
        flight(0.13, 0.42, 0.82 * A, -7 * A)
        // Landing: squash, then a smaller bounce, then a wobble to rest.
        sy -= 0.2 * A * bump(t - land1 + 0.02, 0.2)
        var last = land1
        if hold >= 0.9 {
            let start2 = land1 + 0.12
            flight(start2, 0.3, 0.34 * A, 5 * A)
            last = start2 + 0.3
            sy -= 0.11 * A * bump(t - last + 0.02, 0.16)
        } else {
            flight(land1 + 0.1, 0.2, 0.12 * A, 0)
            last = land1 + 0.3
        }
        let u = t - last - 0.12
        if u > 0 { sy += 0.045 * A * exp(-7 * u) * sin(2 * .pi * 4.2 * u) }
        let r = rest(t, D)
        return (dy * r, 1 + (sy - 1) * r, tilt * r)
    }

    /// Dip: the whole word's drop, in ems (positive is down): a lift, a
    /// fast fall, a spring back up past the line, a settle.
    static func dip(_ t: Double, A: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        // Late enough that the label has pressed in and the line settled.
        let down = spring(t - 0.3, peak: 0.22, overshoot: 0.08)
        let up = spring(t - 0.62, peak: 0.32, overshoot: 0.22)
        return (0.75 * A * (down - up) - 0.1 * A * bump(t - 0.08, 0.3)) * rest(t, D)
    }

    /// Melt: 0 → 1 as it softens, back through a jelly wobble (a little
    /// below 0) as it firms up.
    static func melt(_ t: Double, delay: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        let soften = spring(t - 0.05 - delay, peak: 0.6, overshoot: 0.02)
        let firm = spring(t - (D - 0.72) - 0.5 * delay, peak: 0.2, overshoot: 0.3)
        return (soften - firm) * rest(t, D)
    }
}

/// The whole-word part of one frame. Travel in ems, angles in degrees.
struct LivelyPose: Equatable {
    var dx = 0.0, dy = 0.0
    /// About the upright axis, drawn as a real turn in perspective
    /// (the back of the word reads the right way round).
    var turn = 0.0
    /// In the plane.
    var tilt = 0.0
    var sx = 1.0, sy = 1.0
    /// Scale anchor: 0 the top, 0.5 centre, 1 the foot.
    var anchorY = 0.5
    var opacity = 1.0
    var glow = 0.0
    /// A wide soft bloom, 0...1.
    var bloom = 0.0
    var warm = 0.0, cool = 0.0, red = 0.0
    /// 0...1: the whole word (and its label) darkened.
    var dim = 0.0
    /// 0...1: the whirlwind round a spinning word.
    var twister = 0.0
    /// 0...1: the dark closing in round the word.
    var vignette = 0.0

    static let identity = LivelyPose()

    var multiply: Color {
        Color(red: (1 - 0.35 * cool) * (1 - dim), green: max(0, 1 - 0.25 * warm - 0.12 * cool - 0.6 * red) * (1 - dim),
              blue: max(0, 1 - 0.55 * warm - 0.55 * red) * (1 - dim))
    }

    /// The turn as drawn: past edge-on the word shows its back, which reads
    /// the right way round (the flip happens while it is a sliver).
    var shownTurn: Double {
        let a = turn.truncatingRemainder(dividingBy: 360)
        return cos(a * .pi / 180) < 0 ? a + 180 : a
    }

    // swiftlint:disable:next cyclomatic_complexity function_body_length
    static func make(_ action: WordAction, t: Double, duration D: Double, intensity A: Double, hold: Double) -> LivelyPose {
        guard t > 0, t < D else { return .identity }
        typealias L = LivelyAction
        var pose = LivelyPose()
        let rest = L.rest(t, D)
        switch action {
        case .spin:
            pose.turn = L.spinAngle(t, A: A, hold: hold, duration: D)
            let speed = L.spinSpeed(t, A: A, hold: hold, duration: D)
            let wind = L.bump(t, 0.34)
            // A twister: it crouches as it winds up, then lifts, stretches
            // tall and leans into the turn while it whirls.
            let whirl = L.ss(speed / 0.8)
            pose.dy = (-0.24 * A * whirl + 0.05 * A * wind) * rest
            pose.sy = 1 + (0.1 * A * whirl - 0.08 * A * wind) * rest
            pose.sx = 1 + (0.06 * A * wind - 0.03 * A * whirl) * rest
            pose.anchorY = 1
            pose.tilt = -8 * A * whirl * sin(.pi * L.clamp01(t / (D * 0.7))) * rest
            pose.glow = (0.25 + 0.45 * whirl) * L.envelope(t, attack: 0.15, release: 0.4, duration: D)
            pose.twister = whirl * A
        case .float:
            // Lift-off eases in slowly (no snap into motion), drifts up past
            // its height a touch and settles; it comes down as gently.
            let lift = L.ss5(t / 0.95) + 0.07 * L.bump(t - 0.55, 0.9) - L.ss5((t - (D - 0.85)) / 0.8)
            let drift = L.ss5((t - 0.1) / 0.8) * L.rest(t, D, last: 0.7)
            pose.dy = (-0.42 * A * lift - 0.05 * A * sin(2 * .pi * 1.1 * t) * drift) * rest
            pose.dx = 0.08 * A * sin(2 * .pi * 0.6 * t) * drift
            pose.tilt = 3.5 * A * sin(2 * .pi * 0.8 * t + 0.5) * drift
            pose.glow = 0.45 * L.clamp01(lift) * rest
        case .dip:
            let dy = L.dip(t, A: A, duration: D)
            pose.dy = dy
            // Squashed at the bottom, a little stretched coming back up.
            let low = max(0, dy / max(0.01, 0.75 * A))
            pose.sy = 1 - 0.16 * A * low * low + 0.06 * A * max(0, -dy / max(0.01, 0.1 * A)) * 0.5
            pose.sx = 1 + 0.11 * A * low * low
            pose.anchorY = 1
        case .shake:
            // A quick onset, a tremor that swells and fades in waves, and a
            // decay like a struck thing coming to rest.
            // A nervous tremble, not a rattle: small, a little slower, uneven.
            let fade = t < D - 0.6 ? 1 : exp(-(t - (D - 0.6)) / 0.18)
            let e = L.ss(t / 0.15) * fade * (0.75 + 0.25 * sin(2 * .pi * 1.7 * t)) * rest
            pose.dx = 0.045 * A * e * (0.7 * sin(2 * .pi * 6.5 * t) + 0.3 * sin(2 * .pi * 9.7 * t + 1))
            pose.dy = 0.012 * A * e * sin(2 * .pi * 5.9 * t + 2)
            pose.tilt = 2 * A * e * (0.7 * sin(2 * .pi * 5.3 * t + 1.1) + 0.3 * sin(2 * .pi * 7.9 * t + 0.4))
        case .hop:
            let h = L.hop(t, A: A, hold: hold, duration: D)
            pose.dy = h.dy
            pose.sy = h.sy
            pose.sx = 1 - 0.55 * (h.sy - 1)
            pose.tilt = h.tilt
            pose.anchorY = 1
        case .grow:
            // A smooth swell that drifts a little past its size and eases
            // back, a slow breath while held, and an unhurried return.
            // Like a breath in: a slow start, a gentle swell over ~0.85 s to
            // ~1.36× at Wild with a barely-there overshoot, a soft breath
            // while held, and an equally unhurried return.
            let held = L.ss5(t / 0.85) + 0.03 * L.bump(t - 0.6, 0.8) - L.ss5((t - (D - 0.9)) / 0.85)
            let breathe = L.ss5((t - 0.9) / 0.6) * L.rest(t, D, last: 0.95)
            let s = (0.36 * A * held + 0.012 * A * sin(2 * .pi * 0.8 * (t - 0.9)) * breathe) * rest
            pose.sx = 1 + s
            pose.sy = 1 + s
            pose.glow = 0.9 * L.clamp01(held) * rest
        case .shrink:
            let s = L.shrink(t, A: A, duration: D)
            pose.sx = 1 - s
            pose.sy = 1 - 0.72 * s
        case .dissolve:
            pose.glow = 0.3 * L.envelope(t, attack: 0.6, release: 0.8, duration: D)
        case .flicker:
            // A burn: the warmth kindles, glows and breathes slowly (no
            // strobing), then cools.
            let e = L.burn(t, duration: D)
            pose.warm = 0.75 * A * e
            pose.glow = e * (0.75 + 0.12 * sin(2 * .pi * 1.3 * t) + 0.08 * sin(2 * .pi * 2.1 * t + 1))
        case .frost:
            let frozen = L.frostCover(t, duration: D)
            pose.cool = 0.9 * A * frozen
            // A crisp settle as the frost closes over it: a small tick in
            // and back, then still and stiff until a gentle thaw.
            let set = L.bump(t - 0.95, 0.22)
            pose.sy = 1 + (0.025 * frozen - 0.02 * set) * A
            pose.sx = 1 + (-0.012 * frozen + 0.015 * set) * A
            pose.anchorY = 1
            pose.glow = 0.35 * frozen
        case .bleed:
            let e = L.envelope(t, attack: 0.3, release: 0.5, duration: D)
            pose.red = 0.3 * A * e
            // Heavy: it sags a hair as the drips run.
            pose.dy = 0.04 * A * e
            pose.sy = 1 - 0.02 * A * e
            pose.anchorY = 1
        case .dark:
            // The light goes: the word draws in a little as the shadow takes
            // it (the ink and the closing vignette are drawn with it).
            let e = L.darkness(t, duration: D)
            pose.sx = 1 - 0.03 * A * e
            pose.sy = pose.sx
            pose.dim = 0.2 * A * e
            pose.vignette = A * e
        case .shatter:
            // A jolt on the break and a smaller one on the snap back; the
            // snap back flashes.
            func jolt(_ u: Double) -> Double { u > 0 ? L.ss(u / 0.015) * exp(-u / 0.09) * sin(2 * .pi * 16 * u) : 0 }
            pose.dx = (0.05 * A * jolt(t - 0.05) + 0.03 * A * jolt(t - 0.66)) * rest
            pose.tilt = (2.5 * A * jolt(t - 0.06) - 1.5 * A * jolt(t - 0.67)) * rest
            pose.glow = 1.1 * exp(-pow((t - 0.7) / 0.07, 2)) + 0.6 * exp(-pow((t - 0.06) / 0.05, 2))
        case .melt:
            // The whole label sags from its top, and firms up with a wobble.
            let m = L.melt(t, delay: 0.08, duration: D)
            pose.warm = 0.15 * A * L.clamp01(m)
            pose.sy = 1 + 0.06 * A * m
            pose.sx = 1 + 0.02 * A * m
            pose.anchorY = 0
        case .echo:
            let e = L.envelope(t, attack: 0.2, release: 0.45, duration: D)
            pose.glow = 0.3 * e
            pose.dx = L.echoSway(t, A: A) * e
        case .pulse:
            var s = 0.0
            var beat = 0.0
            let last = max(0.01, hold - 0.3)
            while beat <= last {
                s += 0.2 * A * L.thump(t - beat) + 0.13 * A * L.thump(t - beat - 0.24)
                beat += 0.8
            }
            s *= rest
            pose.sx = 1 + s
            pose.sy = 1 + s
            pose.glow = min(0.9, 2 * s)
            pose.warm = min(0.4, 1.2 * s)
        case .wave:
            // The label rides the swell, rocking like a boat.
            let e = L.envelope(t, attack: 0.35, release: 0.5, duration: D)
            let phase = 2 * .pi * (0.55 - 1.5 * t)
            pose.dy = -0.07 * A * sin(phase) * e
            pose.tilt = 4 * A * cos(phase) * e
        case .sink:
            let d = L.sink(t, delay: 0.15, duration: D)
            pose.dy = 0.62 * A * d
            pose.tilt = 2.5 * A * sin(2 * .pi * 0.7 * t) * L.clamp01(d)
            pose.cool = 0.3 * L.clamp01(d)
        }
        return pose
    }
}

extension LivelyAction {
    /// Reduce Motion's cue for an action: only a glow and an ink tint, rising
    /// over the first fifth and fading over the last half of `duration`.
    static func reduceMotionCue(_ action: WordAction, elapsed: Double, duration: Double,
                                gentle: Bool = false) -> LivelyPose {
        guard elapsed > 0, elapsed < duration else { return .identity }
        let p = elapsed / duration
        let cue = ss(p / 0.2) * (1 - ss((p - 0.5) / 0.5)) * (gentle ? 0.5 : 1)
        var pose = LivelyPose()
        pose.glow = 0.6 * cue
        switch action {
        case .flicker: pose.warm = 0.5 * cue
        case .frost: pose.cool = 0.5 * cue
        case .bleed: pose.red = 0.4 * cue
        case .dark: pose.dim = 0.25 * cue
        default: break
        }
        return pose
    }

    /// Shrink: how far it has pulled in (0 at rest): a little swell first,
    /// a spring in, a tremble while squeezed, a pop back out past full size.
    static func shrink(_ t: Double, A: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        let squeeze = spring(t - 0.12, peak: 0.24, overshoot: 0.12) - spring(t - (D - 0.55), peak: 0.26, overshoot: 0.28)
        let tremble = 0.012 * A * sin(2 * .pi * 9 * t) * ss((t - 0.3) / 0.2) * rest(t, D, last: 0.65)
        return (0.46 * A * squeeze - 0.06 * A * bump(t, 0.2) + tremble) * rest(t, D)
    }

    /// Echo: the word's sway, in ems.
    static func echoSway(_ t: Double, A: Double) -> Double { 0.22 * A * sin(2 * .pi * 0.9 * max(0, t)) }

    /// One drip's state, in ems from the lip where it forms.
    struct Drip {
        /// The trail from the lip to the bead.
        var length = 0.0
        /// The trail's width at its end, against its width at the lip.
        var neck = 1.0
        /// The bead's radius at the trail's end.
        var bead = 0.0
        /// The released drop: how far below the lip, its radius, how much is left of it.
        var dropY = 0.0, dropR = 0.0, dropAlpha = 0.0
    }
    /// A drip with weight: it swells slowly at the lip, stretches (faster
    /// and faster, its neck thinning), lets go of a drop that falls with
    /// gravity and fades, and the trail springs back up to a bead.
    static func drip(_ t: Double, born: Double, size s: Double, swell: Double, stretch: Double) -> Drip? {
        let u = t - born
        guard u > 0, s > 0.01 else { return nil }
        var d = Drip()
        let full = 0.075 * s
        let a = ss5(u / swell)
        d.bead = full * a
        d.length = 0.05 * s * a
        let v = (u - swell) / stretch
        guard v > 0 else { return d }
        if v < 1 {
            let p = v * v
            d.length = 0.05 * s + 0.5 * s * p
            d.neck = 1 - 0.6 * p
            d.bead = full * (1 + 0.15 * p)
            return d
        }
        // Released.
        let w = (v - 1) * stretch
        let end = 0.55 * s
        let speed = 1.0 * s / stretch
        d.dropY = end + speed * w + 0.5 * 7 * w * w
        d.dropR = full * 1.15 * (1 - 0.15 * ss(w / 0.3))
        d.dropAlpha = 1 - ss5(w / 0.55)
        d.length = end * (0.25 + 0.75 * (1 - spring(w, peak: 0.16, overshoot: 0.3)))
        d.neck = 0.4 + 0.5 * ss(w / 0.25)
        d.bead = full * (0.35 + 0.25 * ss(w / 0.3))
        return d
    }

    /// Burn: kindles over ~0.6 s, holds, cools over the last 0.7 s.
    static func burn(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        return ss5(t / 0.65) * (1 - ss5((t - (D - 0.75)) / 0.7))
    }

    /// Freeze: how far the frost has spread, from the leading edge (0 → 1.2,
    /// over ~1 s, slowing as it closes), and how much of it is left as it thaws.
    static func frostFront(_ t: Double) -> Double { -0.25 + 1.85 * (1 - pow(1 - clamp01(t / 1.1), 2.2)) }
    static func frostThaw(_ t: Double, duration D: Double) -> Double { ss5((t - (D - 0.8)) / 0.75) }
    static func frostCover(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        return ss5(t / 0.9) * (1 - frostThaw(t, duration: D))
    }

    /// Dark: how far the shadow has come in, 0...1.
    static func darkness(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        return ss5(t / 0.75) * (1 - ss5((t - (D - 0.8)) / 0.75))
    }
    /// Dark: where the ink's edge is across the word (0 its leading side,
    /// 1 the far side), creeping in, then drawing back the way it came.
    static func inkFront(_ t: Double, duration D: Double) -> Double {
        -0.35 + 2.15 * ss5(t / 1.0) - 2.15 * ss5((t - (D - 0.85)) / 0.8)
    }

    /// Sink: 0 → 1 as a letter goes under (slow to start, heavy, a bob at
    /// the bottom), back up quickly past the line (a little below 0) and
    /// settling.
    static func sink(_ t: Double, delay: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        // Eases in (no sudden start), goes a little deeper, bobs back.
        let under = ss5((t - 0.05 - delay) / 0.95) + 0.09 * bump(t - 0.6 - delay, 0.8)
        let up = spring(t - (D - 0.85) - 0.35 * delay, peak: 0.3, overshoot: 0.2)
        return (under - up) * rest(t, D)
    }
}

/// What the word's `Text` needs to draw an action's letters and particles.
struct LivelyGlyphs: Equatable {
    var action: WordAction
    /// Seconds since the onset.
    var t: Double
    var duration: Double
    var intensity: Double
    var hold: Double
    /// The word's type size.
    var size: CGFloat
    var glow: Color
    var seed: Int
    /// The whole-word travel (ems) the particles that stay on the line undo.
    var lift: Double
}

/// The lively style around a word: the whole-word pose, and the per-letter
/// part handed to the word's `Text` through the environment.
struct LivelyActionEffect: ViewModifier {
    let action: WordAction
    let elapsed: Double
    let hold: Double
    let amount: Double
    let size: CGFloat
    var glow: Color = .white
    let reduceMotion: Bool
    /// A chorus repeat: under Reduce Motion its glow is fainter too.
    var gentle = false

    func body(content: Content) -> some View {
        let duration = LivelyAction.duration(action, hold: hold)
        let active = elapsed > 0 && elapsed < duration
        if !active {
            content
        } else if reduceMotion {
            // Reduce Motion: a glow in the word's own ink (warmer, cooler
            // or redder for the colour actions); nothing moves, scales,
            // turns or fades.
            let cue = LivelyAction.reduceMotionCue(action, elapsed: elapsed, duration: duration, gentle: gentle)
            content
                .colorMultiply(cue.multiply)
                .shadow(color: glow.opacity(min(0.9, 0.75 * cue.glow)), radius: max(1, size * 0.22 * CGFloat(cue.glow)))
        } else {
            let intensity = LivelyAction.intensity(amount: amount, hold: hold)
            let pose = LivelyPose.make(action, t: elapsed, duration: duration, intensity: intensity, hold: hold)
            let glyphs = LivelyGlyphs(action: action, t: elapsed, duration: duration, intensity: intensity, hold: hold,
                                      size: size, glow: glow, seed: 11, lift: pose.dy)
            let twister = pose.twister > 0.005
                ? LivelyTwister(t: elapsed, turn: pose.turn, strength: pose.twister, size: size, glow: glow) : nil
            content
                .environment(\.lyricLivelyGlyphs, glyphs)
                .colorMultiply(pose.multiply)
                .shadow(color: glow.opacity(min(0.9, 0.75 * pose.glow)), radius: max(1, size * 0.22 * CGFloat(min(2, pose.glow))))
                .shadow(color: glow.opacity(0.55 * pose.bloom), radius: max(1, size * 0.8 * CGFloat(pose.bloom)))
                .scaleEffect(x: CGFloat(pose.sx), y: CGFloat(pose.sy), anchor: UnitPoint(x: 0.5, y: pose.anchorY))
                .modifier(LivelyTurn(degrees: action == .spin ? pose.shownTurn : nil, room: size))
                // Spin's whirlwind: the far side of the funnel behind the
                // word, the near side in front.
                .background { if let twister { twister.side(front: false).padding(-size * 1.3) } }
                // Dark: the light round the word dims and closes in on it.
                .background { if pose.vignette > 0.005 { LivelyVignette(amount: pose.vignette, size: size).padding(-size * 3) } }
                .overlay { if let twister { twister.side(front: true).padding(-size * 1.3) } }
                .rotationEffect(.degrees(pose.tilt))
                .offset(x: CGFloat(pose.dx) * size, y: CGFloat(pose.dy) * size)
                // Sink: under the water line (fixed where the word stands)
                // the label shows faintly.
                .modifier(LivelyWaterline(depth: action == .sink ? LivelyAction.clamp01(pose.dy / max(0.01, 0.62 * intensity)) : nil,
                                          size: size))
                .opacity(pose.opacity)
        }
    }
}

/// A real turn about the upright axis, in perspective.
struct LivelyTurn: ViewModifier {
    /// Nil: no turn.
    let degrees: Double?
    let room: CGFloat

    func body(content: Content) -> some View {
        if let degrees {
            content.rotation3DEffect(.degrees(degrees), axis: (x: 0, y: 1, z: 0), perspective: 0.5)
        } else {
            content
        }
    }
}

/// Sink's water line: at the foot of the word's place, everything below it
/// fades as the word goes under.
struct LivelyWaterline: ViewModifier {
    /// Nil: none.
    let depth: Double?
    let size: CGFloat

    func body(content: Content) -> some View {
        if let depth {
            content.mask {
                Canvas { context, canvas in
                    let line = canvas.height - size * 2.1
                    context.fill(Path(CGRect(x: 0, y: 0, width: canvas.width, height: line)), with: .color(.black))
                    let under = CGRect(x: 0, y: line, width: canvas.width, height: canvas.height - line)
                    context.fill(Path(under), with: .linearGradient(
                        Gradient(colors: [.black.opacity(1 - 0.35 * depth), .black.opacity(1 - 0.7 * depth)]),
                        startPoint: CGPoint(x: 0, y: line), endPoint: CGPoint(x: 0, y: line + size * 0.7)))
                }
                .padding(-size * 2)
            }
        } else {
            content
        }
    }
}

/// Dark's closing vignette: a soft shadow round the word, its clear
/// middle shrinking towards the word as `amount` grows.
struct LivelyVignette: View {
    let amount: Double
    let size: CGFloat

    var body: some View {
        Canvas { context, canvas in
            let centre = CGPoint(x: canvas.width / 2, y: canvas.height / 2)
            let word = CGSize(width: canvas.width - size * 6, height: canvas.height - size * 6)
            let inner = max(word.width, word.height) * 0.5 + size * CGFloat(1.6 - 1.1 * amount)
            let outer = inner + size * 1.8
            let shade = Color(red: 0.02, green: 0.01, blue: 0.05)
            // Squashed to the word's shape: wider than tall.
            context.translateBy(x: centre.x, y: centre.y)
            context.scaleBy(x: 1, y: max(0.35, (word.height + size * 2.2) / (word.width + size * 2.2)))
            context.translateBy(x: -centre.x, y: -centre.y)
            context.fill(Path(ellipseIn: CGRect(x: centre.x - outer, y: centre.y - outer, width: outer * 2, height: outer * 2)),
                         with: .radialGradient(Gradient(stops: [
                             .init(color: shade.opacity(0), location: 0),
                             .init(color: shade.opacity(0), location: inner / outer * 0.75),
                             .init(color: shade.opacity(0.6 * amount), location: inner / outer),
                             .init(color: shade.opacity(0), location: 1),
                         ]), center: centre, startRadius: 0, endRadius: outer))
        }
        .allowsHitTesting(false)
    }
}

/// Spin's whirlwind: a funnel of streaks and dust curling round the word,
/// narrow at its foot, wide above, faster near the bottom. Drawn in two
/// halves, the far one behind the word and the near one in front.
struct LivelyTwister {
    let t: Double
    /// The word's turn, degrees: the streaks wind with it.
    let turn: Double
    /// 0...1.
    let strength: Double
    let size: CGFloat
    let glow: Color

    func side(front: Bool) -> some View {
        Canvas { context, canvasSize in
            draw(front: front, in: &context, rect: CGRect(origin: .zero, size: canvasSize))
        }
        .allowsHitTesting(false)
    }

    private func draw(front: Bool, in context: inout GraphicsContext, rect: CGRect) {
        typealias L = LivelyAction
        let word = rect.insetBy(dx: size * 1.3, dy: size * 1.3)
        guard word.width > 0, word.height > 0 else { return }
        let k = strength
        let bottom = word.maxY + size * 0.18, top = word.minY - size * 0.6
        let spin = turn * .pi / 180
        func place(_ h: Double, _ a: Double) -> CGPoint {
            let rx = (word.width * 0.5 * (0.5 + 0.62 * h) + size * 0.14) * (0.72 + 0.28 * k)
            let ry = rx * 0.16 + size * 0.04
            return CGPoint(x: word.midX + rx * CGFloat(cos(a)), y: bottom + (top - bottom) * CGFloat(h) + ry * CGFloat(sin(a)))
        }
        let rings = 6, segments = 9, span = 1.25
        for ring in 0..<rings {
            let h = Double(ring) / Double(rings - 1)
            for j in 0..<2 {
                let a0 = spin * (1.25 - 0.5 * h) + t * (5.5 - 2.5 * h) + Double(ring) * 1.3 + Double(j) * .pi
                for s in 0..<segments {
                    let f0 = Double(s) / Double(segments), f1 = Double(s + 1) / Double(segments)
                    let a = a0 + span * f0, b = a0 + span * f1
                    // The near half is the lower half of each ring.
                    guard (sin((a + b) / 2) > 0) == front else { continue }
                    var path = Path()
                    path.move(to: place(h, a)); path.addLine(to: place(h, b))
                    // Tapered: bright at the head, fading along the tail.
                    let alpha = k * (front ? 0.6 : 0.28) * f1 * (0.55 + 0.45 * h)
                    context.stroke(path, with: .color(glow.opacity(alpha)),
                                   style: StrokeStyle(lineWidth: max(1, size * CGFloat(0.022 + 0.018 * h * f1)), lineCap: .round))
                }
            }
        }
        // Dust spiralling up the funnel.
        for n in 0..<16 {
            let h = (t * 0.85 + L.rnd(5, n, 1)).truncatingRemainder(dividingBy: 1)
            let a = L.rnd(5, n, 2) * 2 * .pi + t * (7 - 3 * h) + spin * 0.6
            guard (sin(a) > 0) == front else { continue }
            let p = place(h, a)
            let r = size * CGFloat(0.022 + 0.022 * L.rnd(5, n, 3))
            context.fill(Path(ellipseIn: CGRect(x: p.x - r, y: p.y - r, width: 2 * r, height: 2 * r)),
                         with: .color(glow.opacity(k * (front ? 0.85 : 0.4) * sin(.pi * h))))
        }
    }
}

/// Applies the per-letter pass to a word's `Text`, when an action is in progress.
struct LivelyGlyphHook: ViewModifier {
    /// Whether this layer draws the particles (the first layer of the word).
    var particles = true
    @Environment(\.lyricLivelyGlyphs) private var glyphs
    @Environment(\.lyricLivelyParticles) private var particlesAllowed
    /// For the opt-in renders only: draw as macOS 14 does (whole word only).
    nonisolated(unsafe) static var disabledForPreview = false

    func body(content: Content) -> some View {
        if #available(macOS 15.0, *), let glyphs, !Self.disabledForPreview {
            content.textRenderer(LivelyGlyphRenderer(state: glyphs, particles: particles && particlesAllowed))
        } else {
            content
        }
    }
}

/// Marks text an action word's letters leave alone (a symbol set inline
/// after the word): drawn in place, and not counted as a letter.
@available(macOS 15.0, *)
struct LivelyStill: TextAttribute {}

extension Text {
    /// This text stays still under an action word's per-letter pass.
    func livelyStill() -> Text {
        if #available(macOS 15.0, *) { return customAttribute(LivelyStill()) }
        return self
    }
}

extension View {
    /// Lets an action in progress move this word's letters (macOS 15+);
    /// `particles` on only one layer of a word drawn in several.
    func livelyLetters(particles: Bool = true) -> some View {
        modifier(LivelyGlyphHook(particles: particles))
    }

    /// A sweep mask that, while an action moves the letters, reaches past
    /// the word on every side (sung on the leading side, unsung beyond).
    @ViewBuilder
    func livelySweepMask(progress: Double, rightToLeft: Bool, feather: Double, active: Bool, pad: CGFloat) -> some View {
        if active {
            mask {
                LyricSweepMask(progress: progress, rightToLeft: rightToLeft, feather: feather)
                    .padding(.vertical, -pad)
                    .overlay(alignment: rightToLeft ? .trailing : .leading) {
                        Color.black.frame(width: pad).padding(.vertical, -pad).offset(x: rightToLeft ? pad : -pad)
                    }
            }
        } else {
            mask(LyricSweepMask(progress: progress, rightToLeft: rightToLeft, feather: feather))
        }
    }
}

// MARK: - Letters

@available(macOS 15.0, *)
struct LivelyGlyphRenderer: TextRenderer {
    var state: LivelyGlyphs
    var particles: Bool

    var displayPadding: EdgeInsets {
        let room = state.size * 2.2 + 20
        return EdgeInsets(top: room, leading: room, bottom: room, trailing: room)
    }

    private struct Glyph {
        let slice: Text.Layout.RunSlice
        let rect: CGRect
        let baseline: CGFloat
        let index: Int
        let along: Double
    }

    func draw(layout: Text.Layout, in context: inout GraphicsContext) {
        var glyphs: [Glyph] = []
        var minX = CGFloat.infinity, maxX = -CGFloat.infinity
        var index = 0
        var still: [Text.Layout.Run] = []
        for line in layout {
            for run in line {
                if run[LivelyStill.self] != nil { still.append(run); continue }
                for slice in run {
                    let bounds = slice.typographicBounds
                    let rect = bounds.rect
                    minX = min(minX, rect.minX); maxX = max(maxX, rect.maxX)
                    glyphs.append(Glyph(slice: slice, rect: rect, baseline: bounds.origin.y, index: index, along: 0))
                    index += 1
                }
            }
        }
        guard !glyphs.isEmpty, maxX > minX else {
            for line in layout { context.draw(line) }
            return
        }
        for run in still { context.draw(run) }
        let width = maxX - minX
        glyphs = glyphs.map {
            Glyph(slice: $0.slice, rect: $0.rect, baseline: $0.baseline, index: $0.index,
                  along: Double(($0.rect.midX - minX) / width))
        }
        let top = glyphs.map(\.rect.minY).min() ?? 0
        let bottom = glyphs.map(\.rect.maxY).max() ?? 0
        let word = CGRect(x: minX, y: top, width: width, height: bottom - top)
        let baseline = glyphs[0].baseline
        if particles { behind(word: word, baseline: baseline, in: &context) }
        for glyph in glyphs {
            draw(glyph, count: glyphs.count, word: word, in: context)
        }
        if particles { front(glyphs: glyphs, word: word, baseline: baseline, in: &context) }
    }

    // MARK: Per letter

    // swiftlint:disable:next cyclomatic_complexity function_body_length
    private func draw(_ g: Glyph, count: Int, word: CGRect, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity, size = Double(state.size)
        let i = Double(g.index)
        let mid = CGPoint(x: g.rect.midX, y: g.rect.midY)
        var c = context
        func plain() { c.draw(g.slice) }
        func transform(dx: Double = 0, dy: Double = 0, sx: Double = 1, sy: Double = 1, deg: Double = 0,
                       anchor: CGPoint, into target: inout GraphicsContext) {
            target.translateBy(x: anchor.x + CGFloat(dx), y: anchor.y + CGFloat(dy))
            if deg != 0 { target.rotate(by: .degrees(deg)) }
            target.scaleBy(x: CGFloat(sx), y: CGFloat(sy))
            target.translateBy(x: -anchor.x, y: -anchor.y)
        }
        let foot = CGPoint(x: g.rect.midX, y: g.baseline)
        switch state.action {
        case .spin:
            // A twist through the word: the inner letters lead the turn, the
            // outer ones trail it, each turned about its own upright (never
            // past 45°, so none reads backwards), lifted and fanned a little.
            let D = state.duration, hold = state.hold
            let from = abs(g.along - 0.5) * 2
            let lag = 0.01 + 0.04 * from
            let turn = L.spinAngle(t, A: A, hold: hold, duration: D)
            let delta = L.soft(L.spinAngle(t - lag, A: A, hold: hold, duration: D) - turn, 45) * .pi / 180
            let speed = L.spinSpeed(t - lag, A: A, hold: hold, duration: D)
            transform(dy: -0.07 * A * L.ss(speed) * (1 - from) * size, sx: cos(delta),
                      deg: -6 * A * L.ss(speed) * (g.along - 0.5) * 2, anchor: mid, into: &c)
            plain()
        case .hop:
            // The word hops as one, label and all; the letters trail it a
            // touch, one after another.
            let lag = 0.035 * g.along
            let now = L.hop(t, A: A, hold: state.hold, duration: D)
            let late = L.hop(t - lag, A: A, hold: state.hold, duration: D)
            let dy = L.soft(0.7 * (late.dy - now.dy), 0.09)
            transform(dy: dy * size, sx: 1 - 0.3 * (late.sy - now.sy), sy: 1 + 0.6 * (late.sy - now.sy),
                      anchor: foot, into: &c)
            plain()
        case .shake:
            // A nervous tremor that runs along the word, letter by letter.
            let ti = 0.12 + g.along * 0.5 * min(D - 0.4, 1.0)
            let w = exp(-pow((t - ti) / 0.2, 2)) * L.rest(t, D)
            let dy = 0.022 * A * sin(2 * .pi * 8.5 * t + i * 1.7) * w
            let deg = 4 * A * sin(2 * .pi * 6.1 * t + i) * w
            transform(dy: dy * size, deg: deg, anchor: mid, into: &c)
            plain()
        case .float:
            // The label carries the lift; each letter bobs on its own a
            // little out of step, like things on water.
            let drift = L.ss5((t - 0.2 - g.along * 0.15) / 0.75) * L.rest(t, D, last: 0.7 + 0.1 * g.along)
            let dy = -0.06 * A * sin(2 * .pi * 1.2 * t - g.along * 2.4) * drift
            let deg = 5 * A * sin(2 * .pi * 0.9 * t - i * 0.9) * drift
            transform(dy: dy * size, deg: deg, anchor: mid, into: &c)
            plain()
        case .dip:
            // The letters follow the word down and up a beat late, in a ripple.
            let lag = 0.07 * g.along
            let dy = L.soft(0.6 * (L.dip(t - lag, A: A, duration: D) - L.dip(t, A: A, duration: D)), 0.09)
            transform(dy: dy * size, anchor: mid, into: &c)
            plain()
        case .shrink:
            // Squeezed together, then out again a touch after the word.
            let s = L.shrink(t - 0.03 * abs(g.along - 0.5), A: A, duration: D) / 0.46
            transform(dx: Double(word.midX - g.rect.midX) * 0.14 * s, anchor: mid, into: &c)
            plain()
        case .dissolve:
            dust(g, size: size, in: c)
        case .flicker:
            burnGlyph(g, size: size, in: c)
        case .frost:
            let fr = frozen(g, word: word)
            if fr > 0.01 {
                c.addFilter(.colorMultiply(Color(red: 1 - 0.4 * fr, green: 1 - 0.14 * fr, blue: 1)))
            }
            plain()
            if fr > 0.01 {
                // A pale frosted coat, and a cold glint that sweeps across
                // the word once as the frost sets.
                let sweep = -0.3 + 1.6 * L.ss5((t - 0.8) / 0.65)
                let glint = exp(-pow((g.along - sweep) / 0.14, 2)) * (1 - L.frostThaw(t, duration: D))
                var sheen = c
                var matrix = ColorMatrix()
                matrix.r1 = 0; matrix.r5 = 0.88
                matrix.g2 = 0; matrix.g5 = 0.96
                matrix.b3 = 0; matrix.b5 = 1
                matrix.a4 = Float(min(0.85, (0.32 * fr + 0.5 * glint) * A))
                sheen.addFilter(.colorMatrix(matrix))
                sheen.draw(g.slice)
            }
        case .dark:
            // Where the ink has reached, the letter shows pale against it,
            // like a word just made out in the dark.
            let inked = L.ss((L.inkFront(t, duration: D) - g.along) / 0.18) * L.darkness(t, duration: D)
            if inked > 0.01 {
                let k = Float(0.85 * inked * A)
                var matrix = ColorMatrix()
                matrix.r1 = 1 - k; matrix.r5 = k * 0.8
                matrix.g2 = 1 - k; matrix.g5 = k * 0.82
                matrix.b3 = 1 - k; matrix.b5 = k * 0.92
                c.addFilter(.colorMatrix(matrix))
            }
            plain()
        case .shatter:
            shards(g, size: size, in: c)
        case .melt:
            melt(g, size: size, in: c)
        case .bleed:
            seep(g, size: size, in: c)
        case .echo:
            let e = L.envelope(t, attack: 0.2, release: 0.45, duration: D)
            // The word itself sways (`LivelyPose`); the copies trail its
            // sway, each a beat later and further out, fading.
            for k in stride(from: 3, through: 1, by: -1) {
                let kd = Double(k)
                var copy = c
                copy.opacity = [0, 0.6, 0.38, 0.2][k] * e
                let dx = (L.echoSway(t - kd * 0.12, A: A) - L.echoSway(t, A: A) - kd * 0.18 * A) * e
                transform(dx: dx * size, dy: -kd * 0.035 * A * e * size, sx: 1 + 0.07 * kd * e, sy: 1 + 0.07 * kd * e,
                          anchor: CGPoint(x: word.midX, y: word.midY), into: &copy)
                copy.draw(g.slice)
            }
            plain()
        case .wave:
            // The swell runs in from the leading letter and out the same way.
            let e = L.ss((t - 0.05 - g.along * 0.2) / 0.35) * (1 - L.ss((t - (D - 0.55) - g.along * 0.12) / 0.45))
            let phase = 2 * .pi * (g.along * 1.1 - 1.5 * t)
            let lift = -0.17 * A * sin(phase) * e
            transform(dy: lift * size, sy: 1 + 0.06 * A * sin(phase) * e,
                      deg: -10 * A * cos(phase) * e, anchor: mid, into: &c)
            plain()
        case .sink:
            sinkGlyph(g, size: size, in: c)
        default:
            plain()
        }
    }

    /// Burn: the letter, its edge glowing orange and the heat climbing
    /// from its foot (soft, in layers), shimmering a little on the heat.
    private func burnGlyph(_ g: Glyph, size: Double, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity
        let e = L.burn(t - 0.08 * g.along, duration: D)
        guard e > 0.005 else { context.draw(g.slice); return }
        let i = Double(g.index)
        var c = context
        let mid = CGPoint(x: g.rect.midX, y: g.rect.midY)
        c.translateBy(x: mid.x + CGFloat(0.008 * A * e * sin(2 * .pi * 3.1 * t + i * 1.9) * size),
                      y: mid.y - CGFloat(0.01 * A * e * sin(2 * .pi * 2.6 * t + i) * size))
        c.translateBy(x: -mid.x, y: -mid.y)
        var halo = c
        halo.addFilter(.shadow(color: Color(red: 1, green: 0.45, blue: 0.08).opacity(0.85 * e * A), radius: CGFloat(size * 0.09)))
        halo.draw(g.slice)
        // Heat from the foot: four overlapping layers, so it fades upward.
        let lit = size * (0.3 + 0.35 * e + 0.05 * sin(2 * .pi * 0.9 * t + i)) * e
        let k = Float(0.8 * A)
        var matrix = ColorMatrix()
        matrix.r1 = 1 - k; matrix.r5 = k * 1.0
        matrix.g2 = 1 - k; matrix.g5 = k * 0.42
        matrix.b3 = 1 - k; matrix.b5 = k * 0.08
        for layer in 1...4 {
            let h = CGFloat(lit) * CGFloat(layer) / 4
            var ember = c
            ember.opacity = 0.24 * e
            ember.clip(to: Path(CGRect(x: g.rect.minX - 4, y: g.baseline + CGFloat(size * 0.2) - h - CGFloat(size * 0.2),
                                      width: g.rect.width + 8, height: h + CGFloat(size * 0.2))))
            ember.addFilter(.colorMatrix(matrix))
            ember.draw(g.slice)
        }
    }

    /// Bleed: red soaks into the letter from the top down, like ink
    /// through paper: a soft front, darker at the letter's edges.
    private func seep(_ g: Glyph, size: Double, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity
        context.draw(g.slice)
        let soak = L.ss5((t - 0.05 - g.along * 0.2) / 0.8) * (1 - L.ss5((t - (D - 0.7)) / 0.65))
        guard soak > 0.005 else { return }
        let k = Float(0.9 * A)
        var matrix = ColorMatrix()
        matrix.r1 = 1 - k; matrix.r5 = k * 0.5
        matrix.g2 = 1 - k; matrix.g5 = 0
        matrix.b3 = 1 - k; matrix.b5 = k * 0.05
        let top = g.rect.minY, height = g.rect.height
        // Four overlapping layers from the top, so the front is soft.
        for layer in 1...4 {
            let h = height * CGFloat(soak) * (0.55 + 0.15 * CGFloat(layer))
            var stain = context
            stain.opacity = 0.3
            stain.clip(to: Path(CGRect(x: g.rect.minX - 4, y: top - 4, width: g.rect.width + 8, height: h + 4)))
            stain.addFilter(.shadow(color: Color(red: 0.25, green: 0, blue: 0.03).opacity(0.7), radius: CGFloat(size * 0.025)))
            stain.addFilter(.colorMatrix(matrix))
            stain.draw(g.slice)
        }
    }

    /// How frozen a letter is: the frost front creeps across the word, then thaws.
    private func frozen(_ g: Glyph, word: CGRect) -> Double {
        let t = state.t, D = state.duration
        let front = LivelyAction.frostFront(t)
        let thaw = LivelyAction.frostThaw(t, duration: D)
        return LivelyAction.ss((front - g.along) / 0.22) * (1 - thaw)
    }

    /// Dissolve: the letter breaks into drifting dust (cells of its own ink) and re-forms.
    private func dust(_ g: Glyph, size: Double, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity
        // Slow: it drifts away over most of a second and re-forms as slowly.
        let start = 0.1 + g.along * 0.45, back = D - 1.15 + g.along * 0.3
        let d = L.ss5((t - start) / 0.8) * (1 - L.ss5((t - back) / 0.75))
        guard d > 0.005 else { context.draw(g.slice); return }
        // A ghost of the letter stays, so the word still reads while most
        // of its ink drifts off as dust.
        var base = context
        base.opacity = 1 - 0.62 * A * L.ss(d / 0.6)
        base.draw(g.slice)
        // The dust is drawn once, by the word's first layer: a copy of the
        // word (a label's overlay, a sweep) keeps only its faint ghost.
        guard particles else { return }
        let cell = max(3, CGFloat(size) * 0.12)
        let ink = state.glow.resolve(in: context.environment)
        let rect = g.rect.insetBy(dx: -1, dy: -1)
        let cols = Int((rect.width / cell).rounded(.up)), rows = Int((rect.height / cell).rounded(.up))
        for row in 0..<rows {
            for col in 0..<cols {
                let id = g.index * 10_000 + row * 100 + col
                let r1 = L.rnd(state.seed, id, 1), r2 = L.rnd(state.seed, id, 2), r3 = L.rnd(state.seed, id, 3)
                guard L.rnd(state.seed, id, 4) < 0.7 else { continue }
                let dd = L.ss((d - 0.35 * r1) / 0.65)
                let cellRect = CGRect(x: rect.minX + CGFloat(col) * cell, y: rect.minY + CGFloat(row) * cell,
                                      width: cell, height: cell)
                var c = context
                let swirl = t * 1.3 + r3 * 6.3
                let ox = ((0.3 + 1.0 * r2) * A + 0.12 * sin(swirl)) * size * dd
                let oy = (-(0.2 + 0.9 * r3) * A + 0.1 * cos(swirl)) * size * dd
                let s = 1 - 0.35 * dd
                c.opacity = 1 - 0.45 * dd
                if dd > 0.02 {
                    // The dust takes the word's glow ink, so it shows on a label and off it.
                    let k = Float(0.75 * dd)
                    var matrix = ColorMatrix()
                    matrix.r1 = 1 - k; matrix.r5 = k * ink.red
                    matrix.g2 = 1 - k; matrix.g5 = k * ink.green
                    matrix.b3 = 1 - k; matrix.b5 = k * ink.blue
                    c.addFilter(.colorMatrix(matrix))
                }
                c.translateBy(x: cellRect.midX + CGFloat(ox), y: cellRect.midY + CGFloat(oy))
                c.scaleBy(x: CGFloat(s), y: CGFloat(s))
                c.translateBy(x: -cellRect.midX, y: -cellRect.midY)
                c.clip(to: Path(cellRect))
                c.draw(g.slice)
            }
        }
    }

    /// Shatter: four wedges about a crack point fly apart and snap back.
    private func shards(_ g: Glyph, size: Double, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity
        let u = t - g.along * 0.12
        // Burst out fast, keep drifting apart while it hangs, then pulled
        // back together, overshooting into each other a hair, and settled.
        let split = L.spring(u - 0.05, peak: 0.13, overshoot: 0.06)
        let drift = 1 + 0.18 * L.ss((u - 0.12) / 0.4)
        let back = L.spring(t - 0.52 - g.along * 0.06, peak: 0.15, overshoot: 0.25)
        let s = max(-0.12, drift * (split - back)) * L.rest(t, D)
        guard abs(s) > 0.003 else { context.draw(g.slice); return }
        let seed = state.seed
        let cp = CGPoint(x: g.rect.midX + CGFloat((L.rnd(seed, g.index, 1) - 0.5) * 0.3) * g.rect.width,
                         y: g.baseline - CGFloat(size * (0.25 + 0.2 * L.rnd(seed, g.index, 2))))
        let theta0 = L.rnd(seed, g.index, 3) * 2 * .pi
        var angles: [Double] = (0..<4).map { k in theta0 + Double(k) * .pi / 2 + (L.rnd(seed, g.index, 10 + k) - 0.5) * 0.6 }
        angles.append(angles[0] + 2 * .pi)
        let far = CGFloat(size * 2)
        func point(_ a: Double) -> CGPoint { CGPoint(x: cp.x + far * CGFloat(cos(a)), y: cp.y + far * CGFloat(sin(a))) }
        for k in 0..<4 {
            let a0 = angles[k], a1 = angles[k + 1], am = (a0 + a1) / 2
            var path = Path()
            path.move(to: cp); path.addLine(to: point(a0)); path.addLine(to: point(am)); path.addLine(to: point(a1))
            path.closeSubpath()
            var c = context
            let push = size * 0.2 * A * s
            c.translateBy(x: CGFloat(cos(am) * push), y: CGFloat(sin(am) * push + size * 0.06 * A * s))
            c.translateBy(x: cp.x, y: cp.y)
            c.rotate(by: .degrees((L.rnd(seed, g.index, 20 + k) - 0.5) * 18 * A * s))
            c.translateBy(x: -cp.x, y: -cp.y)
            c.clip(to: path)
            c.draw(g.slice)
        }
    }

    /// Melt: the letter droops from its top and drips its own ink, then firms up.
    private func melt(_ g: Glyph, size: Double, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity
        // Warm wax: the letter slumps in bands. The foot softens first and
        // spreads; the upper part follows a beat later and sinks into it,
        // each band swaying a little on its own. It firms up with a wobble.
        let bands = 10
        let rect = g.rect
        let bandH = rect.height / CGFloat(bands)
        let melts: [Double] = (0..<bands).map { k in
            L.melt(t, delay: g.along * 0.25 + (1 - (Double(k) + 0.5) / Double(bands)) * 0.16, duration: D)
        }
        guard melts.contains(where: { abs($0) > 0.002 }) else { context.draw(g.slice); return }
        for k in 0..<bands {
            let q = (Double(k) + 0.5) / Double(bands)
            let m = melts[k]
            let dy = 0.24 * size * A * m * pow(1 - q, 1.3)
            let sx = 1 + 0.25 * A * m * q * q
            let dx = 0.014 * size * A * max(0, m) * sin(2 * .pi * 0.6 * t + Double(k) * 0.55 + Double(g.index))
            var band = context
            let y0 = rect.minY + CGFloat(k) * bandH
            band.clip(to: Path(CGRect(x: rect.minX - rect.width, y: y0 - 0.6, width: rect.width * 3, height: bandH + 1.2)))
            band.translateBy(x: rect.midX + CGFloat(dx), y: CGFloat(dy))
            band.scaleBy(x: CGFloat(sx), y: 1)
            band.translateBy(x: -rect.midX, y: 0)
            band.draw(g.slice)
        }
        // Drips of its own ink, each with weight: a slow swell at the foot,
        // a stretching neck, a release, a fall.
        let fade = 1 - L.ss((t - (D - 0.5)) / 0.45)
        let count = L.rnd(state.seed, g.index, 4) > 0.5 ? 2 : (L.rnd(state.seed, g.index, 3) > 0.25 ? 1 : 0)
        for k in 0..<count {
            let r = { (n: Int) in L.rnd(self.state.seed, g.index * 7 + k, n) }
            guard let drip = L.drip(t, born: 0.15 + 0.4 * r(1) + Double(k) * 0.3, size: (0.7 + 0.6 * r(2)) * A,
                                    swell: 0.4 + 0.25 * r(3), stretch: 0.35 + 0.25 * r(4)) else { continue }
            let x0 = rect.minX + rect.width * CGFloat(0.25 + 0.5 * r(5))
            let lip = g.baseline - CGFloat(size * 0.05)
            var shapes = dripPaths(drip, x: x0, lip: lip, width: CGFloat(size * 0.075 * (0.7 + 0.6 * r(2))), size: CGFloat(size))
            if let drop = dropPath(drip, x: x0, lip: lip, size: CGFloat(size)) { shapes.append(drop) }
            // The ink is the letter's own: a strip of its foot, stretched down.
            let stripH = CGFloat(size * 0.2), stripTop = g.baseline - stripH
            let reach = CGFloat(size * 2.5)
            for (n, shape) in shapes.enumerated() {
                var ink = context
                ink.opacity = fade * (n == shapes.count - 1 && drip.dropAlpha > 0.01 ? drip.dropAlpha : 1)
                ink.clip(to: shape)
                ink.translateBy(x: x0, y: stripTop)
                ink.scaleBy(x: 3, y: (stripH + reach) / stripH)
                ink.translateBy(x: -x0, y: -stripTop)
                ink.draw(g.slice)
            }
        }
    }

    /// One drip's shapes, in points: the lip pooled at the foot, the trail
    /// narrowing to its neck, the bead at its end, and the falling drop.
    /// (Separate shapes, filled one by one, so overlaps never cancel.)
    private func dripPaths(_ d: LivelyAction.Drip, x: CGFloat, lip: CGFloat, width w: CGFloat, size: CGFloat) -> [Path] {
        var paths = [Path(ellipseIn: CGRect(x: x - w * 0.95, y: lip - w * 0.35, width: w * 1.9, height: w * 0.7))]
        let length = CGFloat(d.length) * size, bead = CGFloat(d.bead) * size
        let neck = w * CGFloat(d.neck)
        if length > 0.3 {
            var path = Path()
            path.move(to: CGPoint(x: x - w / 2, y: lip))
            path.addQuadCurve(to: CGPoint(x: x - neck / 2, y: lip + length), control: CGPoint(x: x - w / 2, y: lip + length * 0.55))
            path.addLine(to: CGPoint(x: x + neck / 2, y: lip + length))
            path.addQuadCurve(to: CGPoint(x: x + w / 2, y: lip), control: CGPoint(x: x + w / 2, y: lip + length * 0.55))
            path.closeSubpath()
            paths.append(path)
        }
        if bead > 0.3 {
            paths.append(Path(ellipseIn: CGRect(x: x - bead, y: lip + length - bead * 0.8, width: bead * 2, height: bead * 2.1)))
        }
        return paths
    }

    /// The released drop, if any, in points.
    private func dropPath(_ d: LivelyAction.Drip, x: CGFloat, lip: CGFloat, size: CGFloat) -> Path? {
        guard d.dropAlpha > 0.01 else { return nil }
        let r = CGFloat(d.dropR) * size, y = lip + CGFloat(d.dropY) * size
        return Path(ellipseIn: CGRect(x: x - r * 0.88, y: y - r * 1.25, width: r * 1.76, height: r * 2.4))
    }

    /// The water line: just under the word, where its label ends.
    private func waterline(baseline: CGFloat) -> CGFloat {
        baseline + state.size * 0.12 - CGFloat(state.lift) * state.size
    }

    private func sinkDepth() -> Double {
        LivelyAction.clamp01(LivelyAction.sink(state.t, delay: 0.1, duration: state.duration) * 1.2)
    }

    /// How far one letter has sunk, 0...1: one after another, back up together.
    private func sinkDepth(_ g: Glyph) -> Double {
        LivelyAction.sink(state.t, delay: g.along * 0.3, duration: state.duration)
    }

    /// Sink: the letters drop through the water line one by one; under it
    /// they are fainter and bluer.
    private func sinkGlyph(_ g: Glyph, size: Double, in context: GraphicsContext) {
        let d = sinkDepth(g)
        guard abs(d) > 0.005 else { context.draw(g.slice); return }
        let A = state.intensity
        // The label carries the sinking (`LivelyPose`); each letter goes
        // down a little ahead of or behind it, and bobs.
        let own = 0.3 * A * (d - LivelyAction.sink(state.t, delay: 0.15, duration: state.duration))
        let line = waterline(baseline: g.baseline)
        let huge: CGFloat = 10_000
        let wobble = 5 * A * sin(2 * .pi * 0.9 * state.t + Double(g.index)) * max(0, d)
        var above = context
        above.clip(to: Path(CGRect(x: -huge, y: -huge, width: 2 * huge, height: huge + line)))
        var aboveMoved = above
        aboveMoved.translateBy(x: g.rect.midX, y: g.rect.midY + CGFloat(own * size))
        aboveMoved.rotate(by: .degrees(wobble))
        aboveMoved.translateBy(x: -g.rect.midX, y: -g.rect.midY)
        aboveMoved.draw(g.slice)
        var below = context
        below.clip(to: Path(CGRect(x: -huge, y: line, width: 2 * huge, height: huge)))
        below.opacity = 1 - 0.3 * LivelyAction.clamp01(d)
        below.addFilter(.colorMultiply(Color(red: 0.5, green: 0.78, blue: 1)))
        below.translateBy(x: g.rect.midX, y: g.rect.midY + CGFloat(own * size))
        below.rotate(by: .degrees(wobble))
        below.translateBy(x: -g.rect.midX, y: -g.rect.midY)
        below.draw(g.slice)
    }

    // MARK: Particles

    private func behind(word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity, size = state.size
        switch state.action {
        case .dark:
            // Ink creeping over the word and its label from the leading
            // side, its edge soft and uneven, then drawing back.
            let dark = L.darkness(t, duration: D)
            guard dark > 0.005 else { break }
            let front = L.inkFront(t, duration: D)
            let edge = word.minX + word.width * CGFloat(front)
            let top = word.minY - size * 0.35, bottom = word.maxY + size * 0.3
            let left = word.minX - size * 0.9
            guard edge > left else { break }
            var path = Path()
            let steps = 14
            path.move(to: CGPoint(x: left, y: top))
            // The top edge sags a little toward the front.
            for k in 1...steps {
                let f = CGFloat(k) / CGFloat(steps)
                let x = left + (edge - left) * f
                let y = top + size * 0.08 * CGFloat(sin(Double(f) * 3 + t * 1.4)) * f
                path.addLine(to: CGPoint(x: x, y: y))
            }
            // The leading edge, wavering.
            for k in 1...steps {
                let f = Double(k) / Double(steps)
                let y = top + (bottom - top) * CGFloat(f)
                let wobble = 0.16 * sin(f * 5.5 + t * 2.2) + 0.08 * sin(f * 11 - t * 3.1)
                path.addLine(to: CGPoint(x: edge + size * CGFloat(wobble), y: y))
            }
            path.addLine(to: CGPoint(x: left, y: bottom))
            path.closeSubpath()
            var ink = context
            ink.addFilter(.blur(radius: size * 0.1))
            ink.fill(path, with: .color(Color(red: 0.03, green: 0.02, blue: 0.07).opacity(0.82 * A * dark)))
        default:
            break
        }
    }

    // swiftlint:disable:next cyclomatic_complexity function_body_length
    private func front(glyphs: [Glyph], word: CGRect, baseline: CGFloat, in context: inout GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity
        let size = state.size, seed = state.seed
        let liftPx = CGFloat(state.lift) * size
        switch state.action {
        case .hop:
            // A puff of dust kicked up on the ground at each landing.
            let ground = baseline + size * 0.1 - liftPx
            let landings = state.hold >= 0.9 ? [(0.55, 1.0), (0.97, 0.6)] : [(0.55, 1.0)]
            for (at, strength) in landings {
                let a = (t - at + 0.02) / 0.45
                guard a > 0, a < 1 else { continue }
                let out = CGFloat(L.easeOutCubic(a))
                for k in 0..<8 {
                    let side: CGFloat = k < 4 ? -1 : 1
                    let n = CGFloat(k % 4)
                    let edge = side < 0 ? word.minX + word.width * 0.15 : word.maxX - word.width * 0.15
                    let x = edge + side * size * (0.12 + 0.16 * n) * out * CGFloat(0.6 + 0.4 * A)
                    let y = ground - size * CGFloat(0.06 + 0.05 * L.rnd(seed, k, 1)) * out * (1 + n * 0.3)
                    let r = size * CGFloat(0.035 + 0.03 * L.rnd(seed, k, 2)) * (0.5 + out)
                    context.fill(Path(ellipseIn: CGRect(x: x - r, y: y - r, width: 2 * r, height: 2 * r)),
                                 with: .color(state.glow.opacity(0.5 * strength * A * (1 - a) * (1 - a) * L.ss(a / 0.08))))
                }
            }
        case .float:
            // Soft sparkles left on the line below the lifted word.
            let lift = L.ss(t / 0.42) * (1 - L.ss((t - (D - 0.5)) / 0.5))
            for k in 0..<12 {
                let born = 0.12 + L.rnd(seed, k, 2) * max(0.2, D - 0.75)
                let a = (t - born) / 0.65
                guard a > 0, a < 1 else { continue }
                let x = word.minX + word.width * CGFloat(L.rnd(seed, k, 1))
                let y = baseline + size * CGFloat(0.1 + 0.4 * L.rnd(seed, k, 3)) + size * 0.12 * CGFloat(a) - liftPx
                let r = size * CGFloat(0.06 + 0.07 * L.rnd(seed, k, 4)) * CGFloat(sin(.pi * a)) * CGFloat(0.5 + 0.5 * A)
                context.fill(star(CGPoint(x: x, y: y), r), with: .color(state.glow.opacity(0.9 * sin(.pi * a) * max(0.3, lift))))
            }
        case .flicker:
            // Embers lifting off the word: each rises, slowing, drifts on
            // the heat, shrinks, cools from yellow to red and fades.
            let e = L.burn(t, duration: D)
            guard e > 0.005 else { break }
            for k in 0..<18 {
                let life = 0.9 + 0.6 * L.rnd(seed, k, 5)
                let born = 0.2 + L.rnd(seed, k, 2) * max(0.2, D - 0.5 - life * 0.6)
                let a = (t - born) / life
                guard a > 0, a < 1 else { continue }
                let rise = 1 - pow(1 - a, 1.7)
                let x0 = word.minX + word.width * CGFloat(L.rnd(seed, k, 1))
                let x = x0 + size * CGFloat(0.3 * (L.rnd(seed, k, 6) - 0.5) * a + 0.07 * sin(a * 5 + Double(k)) * a)
                let y = word.minY + word.height * CGFloat(0.25 + 0.3 * L.rnd(seed, k, 7))
                    - size * CGFloat((0.8 + 0.8 * L.rnd(seed, k, 8)) * rise * (0.6 + 0.4 * A))
                let r = size * CGFloat((0.018 + 0.022 * L.rnd(seed, k, 3)) * (1 - 0.55 * a))
                let colour = Color(red: 1, green: 0.88 - 0.5 * a, blue: 0.35 - 0.3 * a)
                let alpha = pow(sin(.pi * a), 0.8) * e * (0.8 + 0.2 * sin(t * 7 + Double(k)))
                context.fill(Path(ellipseIn: CGRect(x: x - r * 2.6, y: y - r * 2.6, width: r * 5.2, height: r * 5.2)),
                             with: .color(colour.opacity(0.18 * alpha)))
                context.fill(Path(ellipseIn: CGRect(x: x - r, y: y - r, width: 2 * r, height: 2 * r)),
                             with: .color(colour.opacity(0.95 * alpha)))
            }
        case .frost:
            // Frost spreading from the leading edge: a pale sheet with a soft
            // edge, feathery rime along the top and foot of the word, and
            // crystals branching out on the letters as the front reaches
            // them. It thaws gently, the crystals shrinking one by one.
            let ice = Color(red: 0.86, green: 0.95, blue: 1)
            let thaw = L.frostThaw(t, duration: D)
            let front = L.frostFront(t)
            guard front > -0.2, thaw < 1 else { break }
            // Close round the letters, with soft edges, so on a layout with
            // no label it reads as frost on the word rather than a box.
            let sheet = word.insetBy(dx: -size * 0.16, dy: -size * 0.06)
            let reach = word.minX + word.width * CGFloat(front)
            let feather = size * 0.5
            if reach + feather > sheet.minX {
                let stopAt = min(1, max(0.001, (reach + feather - sheet.minX) / sheet.width))
                let solid = max(0, (reach - sheet.minX) / sheet.width)
                var frosted = context
                frosted.addFilter(.blur(radius: size * 0.05))
                frosted.clip(to: Path(roundedRect: sheet.insetBy(dx: -size * 0.1, dy: -size * 0.1), cornerRadius: size * 0.1))
                frosted.fill(Path(roundedRect: sheet, cornerRadius: size * 0.12), with: .linearGradient(
                    Gradient(stops: [.init(color: ice.opacity(0.42 * A * (1 - thaw)), location: 0),
                                     .init(color: ice.opacity(0.22 * A * (1 - thaw)), location: min(solid, stopAt * 0.99)),
                                     .init(color: ice.opacity(0), location: stopAt)]),
                    startPoint: CGPoint(x: sheet.minX, y: sheet.midY), endPoint: CGPoint(x: sheet.maxX, y: sheet.midY)))
            }
            // Rime: short spikes along the top and the foot, up to the front.
            var rime = Path()
            let step = size * 0.07
            var x = sheet.minX
            var n = 0
            while x < min(sheet.maxX, reach) {
                let grow = CGFloat(L.ss5(Double((reach - x) / (size * 0.4)))) * CGFloat(1 - L.ss((thaw - 0.4 * L.rnd(seed, n, 90)) / 0.6))
                let len = size * CGFloat(0.04 + 0.08 * L.rnd(seed, n, 91)) * grow
                let lean = size * CGFloat(0.03 * (L.rnd(seed, n, 92) - 0.5))
                rime.move(to: CGPoint(x: x, y: sheet.minY + size * 0.04))
                rime.addLine(to: CGPoint(x: x + lean, y: sheet.minY + size * 0.04 + len))
                rime.move(to: CGPoint(x: x, y: sheet.maxY - size * 0.04))
                rime.addLine(to: CGPoint(x: x - lean, y: sheet.maxY - size * 0.04 - len))
                x += step * CGFloat(0.7 + 0.6 * L.rnd(seed, n, 93))
                n += 1
            }
            context.stroke(rime, with: .color(ice.opacity(0.7 * A)), lineWidth: max(1, size * 0.014))
            for g in glyphs {
                for k in 0..<6 {
                    let px = g.rect.minX + g.rect.width * CGFloat(0.05 + 0.9 * L.rnd(seed, g.index, 30 + k))
                    let p = CGPoint(x: px, y: g.baseline - size * CGFloat(0.7 * L.rnd(seed, g.index, 40 + k)))
                    let along = Double((px - word.minX) / max(1, word.width))
                    let grow = L.ss5((front - along - 0.1 * L.rnd(seed, g.index, 50 + k)) / 0.3)
                        * (1 - L.ss5((thaw - 0.4 * L.rnd(seed, g.index, 55 + k)) / 0.6))
                    let r = size * CGFloat(0.07 + 0.12 * L.rnd(seed, g.index, 60 + k)) * CGFloat(grow) * CGFloat(0.5 + 0.5 * A)
                    guard r > 0.5 else { continue }
                    context.stroke(crystal(p, r, angle: L.rnd(seed, g.index, 70 + k) * .pi),
                                   with: .color(ice.opacity(0.95 * min(1, grow * 1.5))), lineWidth: max(1, size * 0.02))
                }
            }
        case .bleed:
            // A hint of blood: a few glossy runs (2 or 3) from the letters' feet, of
            // uneven length and pace, slow and heavy, each with a round bead
            // at its tip; the longest lets its bead go, which falls and
            // splats.
            let e = L.envelope(t, attack: 0.3, release: 0.65, duration: D)
            guard e > 0.005, !glyphs.isEmpty else { break }
            let crimson = Color(red: 0.42, green: 0.0, blue: 0.05)
            let gloss = Color(red: 1, green: 0.8, blue: 0.82)
            let runs = min(3, max(2, glyphs.count / 3 + 1))
            for k in 0..<runs {
                let r = { (n: Int) in L.rnd(seed, 300 + k, n) }
                // Spread along the word, a little uneven.
                let slot = (Double(k) + 0.5 + 0.5 * (r(1) - 0.5)) / Double(runs)
                let g = glyphs[min(glyphs.count - 1, max(0, Int(slot * Double(glyphs.count))))]
                let x = g.rect.minX + g.rect.width * CGFloat(0.3 + 0.4 * r(2))
                let lip = g.baseline - size * 0.06
                let start = 0.35 + 0.35 * r(3) + 0.15 * Double(k % 2)
                let pace = 1.0 + 0.7 * r(4)
                let reach = (0.22 + 0.3 * r(5)) * A
                let flow = L.ss5((t - start) / pace)
                guard flow > 0.003 else { continue }
                let w = size * CGFloat(0.06 + 0.025 * r(6))
                let releases = k == 0
                let release = start + pace * 0.85
                var length = size * CGFloat(reach * flow)
                var bead = w * CGFloat(0.45 + 0.25 * L.ss5((t - start) / (pace * 0.7)))
                var fallen = 0.0
                if releases, t > release {
                    fallen = t - release
                    // The bead has gone; a smaller one gathers again.
                    bead = w * CGFloat(0.35 + 0.25 * L.ss5(fallen / 0.6))
                    length *= CGFloat(1 - 0.12 * L.spring(fallen, peak: 0.15, overshoot: 0.3))
                }
                // The run: wide at the lip, a little narrower down its length.
                var run = Path()
                run.move(to: CGPoint(x: x - w * 0.7, y: lip))
                run.addQuadCurve(to: CGPoint(x: x - w * 0.42, y: lip + length), control: CGPoint(x: x - w * 0.5, y: lip + length * 0.3))
                run.addLine(to: CGPoint(x: x + w * 0.42, y: lip + length))
                run.addQuadCurve(to: CGPoint(x: x + w * 0.7, y: lip), control: CGPoint(x: x + w * 0.5, y: lip + length * 0.3))
                run.closeSubpath()
                context.fill(run, with: .color(crimson.opacity(0.95 * e)))
                context.fill(Path(ellipseIn: CGRect(x: x - w * 0.9, y: lip - w * 0.3, width: w * 1.8, height: w * 0.6)),
                             with: .color(crimson.opacity(0.95 * e)))
                let tip = CGPoint(x: x, y: lip + length)
                context.fill(Path(ellipseIn: CGRect(x: tip.x - bead, y: tip.y - bead * 0.85, width: bead * 2, height: bead * 2.05)),
                             with: .color(crimson.opacity(0.95 * e)))
                // Gloss: a soft streak down the run and a glint on the bead.
                var streak = Path()
                streak.move(to: CGPoint(x: x - w * 0.22, y: lip + w * 0.2))
                streak.addLine(to: CGPoint(x: x - w * 0.18, y: lip + length - bead * 0.2))
                context.stroke(streak, with: .color(gloss.opacity(0.22 * e)), style: StrokeStyle(lineWidth: max(1, w * 0.16), lineCap: .round))
                context.fill(Path(ellipseIn: CGRect(x: tip.x - bead * 0.5, y: tip.y - bead * 0.45, width: bead * 0.45, height: bead * 0.5)),
                             with: .color(gloss.opacity(0.4 * e)))
                // The released bead: falls with gravity, then splats.
                if releases, fallen > 0 {
                    let big = w * 0.7
                    let drop = size * CGFloat(0.4 * fallen + 0.5 * 6 * fallen * fallen)
                    let floor = size * CGFloat(reach + 0.35)
                    if lip + length + drop < lip + floor {
                        let y = tip.y + drop
                        context.fill(Path(ellipseIn: CGRect(x: x - big * 0.85, y: y - big * 1.1, width: big * 1.7, height: big * 2.3)),
                                     with: .color(crimson.opacity(0.95 * e)))
                    } else {
                        // The splat: a flattened pool and two small flecks, fading.
                        let tLand = (-0.4 + sqrt(0.16 + 12 * Double((floor - length) / size))) / 6
                        let since = max(0, fallen - tLand)
                        let spread = CGFloat(L.easeOutCubic(since / 0.18))
                        let fade = 1 - L.ss5((since - 0.25) / 0.6)
                        let y = lip + floor
                        let pw = big * (1.4 + 0.9 * spread), ph = big * (0.8 - 0.3 * spread)
                        context.fill(Path(ellipseIn: CGRect(x: x - pw / 2, y: y - ph / 2, width: pw, height: ph)),
                                     with: .color(crimson.opacity(0.7 * e * fade)))
                        for side in [-1.0, 1.0] {
                            let fx = x + CGFloat(side) * big * (0.8 + 0.9 * spread)
                            let fy = y - big * CGFloat(0.5 * sin(.pi * min(1, since / 0.3)))
                            let fr = big * 0.16
                            context.fill(Path(ellipseIn: CGRect(x: fx - fr, y: fy - fr, width: fr * 2, height: fr * 2)),
                                         with: .color(crimson.opacity(0.85 * e * fade)))
                        }
                    }
                }
            }
        case .shatter:
            // Crack lines flash as each letter breaks.
            for g in glyphs {
                let u = t - g.along * 0.12
                guard u > 0, u < 0.3 else { continue }
                let cp = CGPoint(x: g.rect.midX + CGFloat((L.rnd(seed, g.index, 1) - 0.5) * 0.3) * g.rect.width,
                                 y: g.baseline - size * CGFloat(0.25 + 0.2 * L.rnd(seed, g.index, 2)))
                let theta0 = L.rnd(seed, g.index, 3) * 2 * .pi
                var path = Path()
                for k in 0..<4 {
                    let a = theta0 + Double(k) * .pi / 2 + (L.rnd(seed, g.index, 10 + k) - 0.5) * 0.6
                    path.move(to: cp)
                    path.addLine(to: CGPoint(x: cp.x + size * 0.45 * CGFloat(cos(a)), y: cp.y + size * 0.45 * CGFloat(sin(a))))
                }
                context.stroke(path, with: .color(.white.opacity(0.9 * (1 - u / 0.3))), lineWidth: max(1, size * 0.02))
            }
        case .pulse:
            // Each beat sends out a soft shockwave: it bursts out fast and
            // slows, thins and fades, its outline a little uneven; the
            // second beat's wave is smaller.
            var beat = 0.0
            let last = max(0.01, state.hold - 0.3)
            var n = 0
            while beat <= last {
                for (k, delay, strength) in [(0, 0.0, 1.0), (1, 0.24, 0.55)] {
                    let a = (t - beat - delay) / 0.95
                    guard a > 0, a < 1 else { continue }
                    let grow = CGFloat(L.easeOutCubic(a)) * size * CGFloat(0.12 + 1.05 * A)
                    let alpha = pow(1 - a, 1.6) * strength * A * L.ss(a / 0.06)
                    let lineWidth = size * CGFloat(0.07 * (1 - a) + 0.008)
                    let p1 = L.rnd(seed, n * 2 + k, 1) * 6.3, p2 = L.rnd(seed, n * 2 + k, 2) * 6.3
                    var ring = Path()
                    for step in 0...64 {
                        let th = Double(step) / 64 * 2 * .pi
                        let wobble = 1 + 0.035 * sin(3 * th + p1 + a * 2) + 0.025 * sin(5 * th + p2 - a * 3)
                        let rx = (word.width / 2 + grow) * CGFloat(wobble)
                        let ry = (word.height / 2 + grow * 0.75) * CGFloat(wobble)
                        let pt = CGPoint(x: word.midX + rx * CGFloat(cos(th)), y: word.midY + ry * CGFloat(sin(th)))
                        if step == 0 { ring.move(to: pt) } else { ring.addLine(to: pt) }
                    }
                    ring.closeSubpath()
                    context.stroke(ring, with: .color(state.glow.opacity(0.16 * alpha)), lineWidth: lineWidth * 3.5)
                    context.stroke(ring, with: .color(state.glow.opacity(0.6 * alpha)), lineWidth: max(0.8, lineWidth))
                }
                beat += 0.8
                n += 1
            }
        case .sink:
            let d = sinkDepth()
            guard d > 0.02 else { break }
            let line = waterline(baseline: baseline)
            var ripple = Path()
            let from = word.minX - size * 0.4, to = word.maxX + size * 0.4
            var x = from
            ripple.move(to: CGPoint(x: x, y: line))
            while x < to {
                x += 3
                ripple.addLine(to: CGPoint(x: x, y: line + size * 0.02 * CGFloat(sin(Double(x) * 0.08 - t * 6))))
            }
            context.stroke(ripple, with: .color(.white.opacity(0.35 * d)), lineWidth: max(1, size * 0.015))
            for k in 0..<10 {
                let born = 0.3 + L.rnd(seed, k, 2) * max(0.2, D - 1.0)
                let a = (t - born) / 0.7
                guard a > 0, a < 1 else { continue }
                let x0 = word.minX + word.width * CGFloat(L.rnd(seed, k, 1))
                let startY = baseline + size * CGFloat(0.4 + 0.3 * A)
                let y = startY + (line - size * 0.04 - startY) * CGFloat(a)
                let r = size * CGFloat(0.03 + 0.04 * L.rnd(seed, k, 3)) * CGFloat(a > 0.85 ? 1 + (a - 0.85) * 4 : 1)
                let xx = x0 + size * 0.06 * CGFloat(sin(a * 9 + Double(k)))
                context.stroke(Path(ellipseIn: CGRect(x: xx - r, y: y - r, width: 2 * r, height: 2 * r)),
                               with: .color(.white.opacity(0.8 * (a > 0.85 ? (1 - a) / 0.15 : 1) * d)),
                               lineWidth: max(1, size * 0.016))
            }
        default:
            break
        }
    }

    private func star(_ c: CGPoint, _ r: CGFloat) -> Path {
        var path = Path()
        let inner = r * 0.28
        for k in 0..<8 {
            let a = Double(k) * .pi / 4 - .pi / 2
            let rr = k.isMultiple(of: 2) ? r : inner
            let p = CGPoint(x: c.x + rr * CGFloat(cos(a)), y: c.y + rr * CGFloat(sin(a)))
            if k == 0 { path.move(to: p) } else { path.addLine(to: p) }
        }
        path.closeSubpath()
        return path
    }

    private func crystal(_ c: CGPoint, _ r: CGFloat, angle: Double) -> Path {
        var path = Path()
        for k in 0..<6 {
            let a = angle + Double(k) * .pi / 3
            let tip = CGPoint(x: c.x + r * CGFloat(cos(a)), y: c.y + r * CGFloat(sin(a)))
            path.move(to: c)
            path.addLine(to: tip)
            // A small branch on each arm.
            let m = CGPoint(x: c.x + r * 0.55 * CGFloat(cos(a)), y: c.y + r * 0.55 * CGFloat(sin(a)))
            for side in [-1.0, 1.0] {
                let b = a + side * .pi / 4
                path.move(to: m)
                path.addLine(to: CGPoint(x: m.x + r * 0.3 * CGFloat(cos(b)), y: m.y + r * 0.3 * CGFloat(sin(b))))
            }
        }
        return path
    }
}

// MARK: - Symbol entrance

/// A word's symbol pops in rather than only fading: a springy scale-up with a little overshoot, a wobble and a rise,
/// settling still. An action word's symbol echoes its action (spinning in,
/// dripping in, frosting in, ...). Reduce Motion keeps today's fade.
struct SymbolEntrancePose: Equatable {
    var dx = 0.0, dy = 0.0
    var sx = 1.0, sy = 1.0
    var rotation = 0.0
    /// Scale anchor: 0.5 the centre, 1 the foot.
    var anchorY = 0.5
    var cool = 0.0

    static let rest = SymbolEntrancePose()
    /// Seconds after which it is at rest.
    static let duration = 1.5

    /// `elapsed` seconds after the word's onset; travel in ems of the symbol.
    static func make(_ action: WordAction?, elapsed e: Double) -> SymbolEntrancePose {
        typealias L = LivelyAction
        guard e > 0 else { return from(action) }
        guard e < duration else { return .rest }
        var p = SymbolEntrancePose()
        let pop = L.spring(e, peak: 0.26, overshoot: 0.2)
        let wobble = L.spring(e, peak: 0.3, overshoot: 0.3)
        switch action {
        case .spin:
            let turn = L.spring(e, peak: 0.45, overshoot: 0.06)
            p.rotation = -360 * (1 - turn)
            p.sx = 0.4 + 0.6 * pop; p.sy = p.sx
        case .bleed, .melt:
            // A drop falling into place: stretched as it falls, squashed as it lands.
            let fall = L.spring(e, peak: 0.3, overshoot: 0.28)
            p.dy = -0.8 * (1 - fall)
            p.sy = 1 + 0.35 * (1 - fall)
            p.sx = 1 - 0.15 * (1 - fall)
            p.anchorY = 1
        case .frost:
            // Crisp: in from a little large, settling with hardly a bounce,
            // cold as it lands.
            let set = L.spring(e, peak: 0.35, overshoot: 0.05)
            p.sx = 1.35 - 0.35 * set; p.sy = p.sx
            p.cool = 1 - L.ss5((e - 0.3) / 0.9)
        case .float, .dissolve:
            let rise = L.ss5(e / 0.8) + 0.08 * L.bump(e - 0.5, 0.8)
            p.dy = 0.7 * (1 - rise)
            p.sx = 0.75 + 0.25 * L.ss5(e / 0.6); p.sy = p.sx
        case .sink:
            let drop = L.ss5(e / 0.8) + 0.08 * L.bump(e - 0.5, 0.8)
            p.dy = -0.7 * (1 - drop)
        case .hop, .dip:
            let land = L.spring(e, peak: 0.22, overshoot: 0.35)
            p.dy = -0.9 * (1 - land)
            let squash = L.bump(e - 0.16, 0.2)
            p.sy = 1 - 0.25 * squash; p.sx = 1 + 0.18 * squash
            p.anchorY = 1
        case .shake:
            p.sx = 0.3 + 0.7 * pop; p.sy = p.sx
            p.dx = 0.12 * L.ss(e / 0.05) * exp(-5 * e) * sin(2 * .pi * 7 * e)
        case .grow:
            let big = L.spring(e, peak: 0.32, overshoot: 0.3)
            p.sx = big; p.sy = big
        case .shrink:
            let small = L.spring(e, peak: 0.3, overshoot: 0.2)
            p.sx = 1.8 - 0.8 * small; p.sy = p.sx
        case .pulse:
            let beat = 0.22 * L.thump(e - 0.35) + 0.14 * L.thump(e - 0.59)
            p.sx = 0.3 + 0.7 * pop + beat; p.sy = p.sx
        default:
            p.sx = 0.3 + 0.7 * pop; p.sy = p.sx
            p.rotation = -20 * (1 - wobble)
            p.dy = 0.35 * (1 - pop)
        }
        // Whatever is left of a settle eases out by the end.
        let r = L.rest(e, duration, last: 0.3)
        p.dx *= r; p.dy *= r; p.rotation *= r; p.cool *= r
        p.sx = 1 + (p.sx - 1) * r; p.sy = 1 + (p.sy - 1) * r
        return p
    }

    /// Where it starts (it is unseen there: its opacity is still 0).
    static func from(_ action: WordAction?) -> SymbolEntrancePose {
        make(action, elapsed: 1e-9)
    }
}

/// Draws a symbol's lively entrance.
struct SymbolEntranceEffect: ViewModifier {
    let action: WordAction?
    let elapsed: Double
    let size: CGFloat

    func body(content: Content) -> some View {
        let p = SymbolEntrancePose.make(action, elapsed: elapsed)
        let em = size * WordSymbolMark.scale
        content
            .colorMultiply(LivelyPose(cool: 0.9 * p.cool).multiply)
            .scaleEffect(x: CGFloat(p.sx), y: CGFloat(p.sy), anchor: UnitPoint(x: 0.5, y: p.anchorY))
            .rotationEffect(.degrees(p.rotation))
            .offset(x: CGFloat(p.dx) * em, y: CGFloat(p.dy) * em)
    }
}
