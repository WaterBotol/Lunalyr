import SwiftUI

// MARK: - PREVIEW: lively action words (next/action-words-preview)
//
// A bolder, more playful take on the action words, drawn only where the
// environment asks for it (`lyricWordActionStyle = .lively`); the app's
// default stays `.today`, so nothing ships by this file alone.
//
// Two parts:
// - a whole-word pose (`LivelyPose`): turn, scale, tilt, travel, tint, glow,
//   applied around the word like today's `WordActionEffect`;
// - a per-letter pass (`LivelyGlyphRenderer`, a `TextRenderer`, macOS 15+)
//   that the layout's own word `Text` picks up from the environment
//   (`lyricLivelyGlyphs`): letter-by-letter hops, shivers, dust, shards,
//   drips, crystals, bubbles and sparkles, using the real glyph bounds.
//
// Every effect is identity before the onset and from `duration` on. The
// duration grows with how long the word is held, and so may outlast the
// word's end by up to ~0.8 s; the amount (`Subtle ↔ Wild`, 0...1) and the
// hold scale the size of every motion (`LivelyAction.intensity`).

/// Which action-word motion is drawn.
enum WordActionStyle: Equatable {
    /// Today's (`WordActionPose`).
    case today
    /// The lively preview, at an amount 0 (subtle) ... 1 (wild).
    case lively(amount: Double)
}

private struct WordActionStyleKey: EnvironmentKey { static let defaultValue = WordActionStyle.today }
private struct LivelyGlyphsKey: EnvironmentKey { static let defaultValue: LivelyGlyphs? = nil }
private struct LivelyParticlesKey: EnvironmentKey { static let defaultValue = true }

extension EnvironmentValues {
    var lyricWordActionStyle: WordActionStyle {
        get { self[WordActionStyleKey.self] }
        set { self[WordActionStyleKey.self] = newValue }
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
        case .float: return max(1.4, h + 0.8)
        case .dip: return 1.7
        case .shake: return max(0.9, min(h, 1.4) + 0.5)
        case .hop: return h >= 0.9 ? 1.55 : 1.15
        case .grow, .shrink: return max(1.2, h + 0.6)
        case .dissolve: return max(1.6, h + 0.75)
        case .flicker: return max(1.2, h + 0.5)
        case .frost: return max(1.6, h + 0.8)
        case .bleed: return max(1.6, h + 0.8)
        case .dark: return max(1.5, h + 0.6)
        case .shatter: return 1.35
        case .melt: return max(1.7, h + 0.8)
        case .echo: return max(1.2, h + 0.5)
        case .pulse: return max(1.0, h + 0.5)
        case .wave: return max(1.3, h + 0.6)
        case .sink: return max(1.9, h + 1.0)
        case .explode: return 1.5
        case .scream: return max(1.0, h + 0.4)
        case .whisper: return max(1.3, h + 0.6)
        case .ghost: return max(1.5, h + 0.6)
        case .glow: return max(1.2, h + 0.5)
        case .electric: return max(1.0, h + 0.4)
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
            let lift = L.spring(t - 0.02, peak: 0.5, overshoot: 0.08) - L.spring(t - (D - 0.78), peak: 0.5, overshoot: 0.05)
            let drift = L.ss(t / 0.5) * L.rest(t, D, last: 0.6)
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
            let fade = t < D - 0.6 ? 1 : exp(-(t - (D - 0.6)) / 0.16)
            let e = L.ss(t / 0.08) * fade * (0.8 + 0.2 * sin(2 * .pi * 2.3 * t)) * rest
            pose.dx = 0.11 * A * e * (0.72 * sin(2 * .pi * 11 * t) + 0.28 * sin(2 * .pi * 17.3 * t + 1))
            pose.dy = 0.025 * A * e * sin(2 * .pi * 9.4 * t + 2)
            pose.tilt = 5 * A * e * (0.7 * sin(2 * .pi * 8 * t + 1.1) + 0.3 * sin(2 * .pi * 13.1 * t + 0.4))
        case .hop:
            let h = L.hop(t, A: A, hold: hold, duration: D)
            pose.dy = h.dy
            pose.sy = h.sy
            pose.sx = 1 - 0.55 * (h.sy - 1)
            pose.tilt = h.tilt
            pose.anchorY = 1
        case .grow:
            let up = L.spring(t - 0.1, peak: 0.3, overshoot: 0.18)
            let down = L.spring(t - (D - 0.6), peak: 0.3, overshoot: 0.12)
            let held = up - down
            let breathe = L.ss((t - 0.4) / 0.3) * L.rest(t, D, last: 0.7)
            let s = (0.52 * A * held - 0.06 * A * L.bump(t, 0.18) + 0.025 * A * sin(2 * .pi * 1.8 * (t - 0.4)) * breathe) * rest
            pose.sx = 1 + s
            pose.sy = 1 + s
            pose.glow = 0.9 * L.clamp01(held) * rest
        case .shrink:
            let s = L.shrink(t, A: A, duration: D)
            pose.sx = 1 - s
            pose.sy = 1 - 0.72 * s
        case .dissolve:
            pose.glow = 0.3 * L.envelope(t, attack: 0.3, release: 0.5, duration: D)
        case .flicker:
            let e = L.envelope(t, attack: 0.15, release: 0.45, duration: D)
            let n = sin(2 * .pi * 7.3 * t) * sin(2 * .pi * 3.1 * t + 1)
            pose.warm = 0.9 * A * e
            pose.glow = e * (0.8 + 0.6 * abs(n))
            pose.opacity = 1 - 0.1 * A * e * (0.5 + 0.5 * sin(2 * .pi * 9.7 * t))
            pose.sx = 1 + 0.04 * A * e * n
            pose.sy = 1 + 0.04 * A * e * n
            pose.dy = -0.02 * A * e * sin(2 * .pi * 2.2 * t)
        case .frost:
            let f = L.ss(t / 0.5), r = L.ss((t - (D - 0.55)) / 0.55)
            let frozen = f * (1 - r)
            pose.cool = 0.95 * A * frozen
            // A shiver as it sets (then still, stiff), and a shake-off as it thaws.
            let set = exp(-pow((t - 0.42) / 0.1, 2))
            let thaw = exp(-pow((t - (D - 0.3)) / 0.09, 2))
            pose.dx = 0.03 * A * sin(2 * .pi * 14 * t) * (set + 0.6 * thaw)
            pose.sy = 1 + 0.03 * A * frozen
            pose.sx = 1 - 0.015 * A * frozen
            pose.anchorY = 1
            pose.glow = 0.3 * frozen
        case .bleed:
            let e = L.envelope(t, attack: 0.3, release: 0.5, duration: D)
            pose.red = 0.9 * A * e
            // Heavy: it sags a hair as the drips run.
            pose.dy = 0.04 * A * e
            pose.sy = 1 - 0.02 * A * e
            pose.anchorY = 1
        case .dark:
            let e = L.envelope(t, attack: 0.45, release: 0.55, duration: D)
            pose.sx = 1 - 0.04 * A * e
            pose.sy = pose.sx
            pose.dy = 0.03 * A * e
            pose.dim = 0.5 * A * e
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
            pose.sy = 1 + 0.1 * A * m
            pose.sx = 1 - 0.03 * A * m
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
            pose.glow = min(1.4, 3.5 * s)
            pose.warm = min(0.5, 1.5 * s)
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
        case .explode:
            pose.glow = 1.2 * exp(-pow((t - 0.08) / 0.08, 2))
        case .scream:
            let k = L.ss(t / 0.07)
            let r = L.ss((t - (D - 0.4)) / 0.4)
            let s = k * (1 - r) * (0.85 + 0.15 * exp(-t / 0.15))
            pose.sx = 1 + 0.7 * A * s
            pose.sy = pose.sx
            pose.dx = 0.03 * A * sin(2 * .pi * 17 * t) * s
            pose.glow = 1.1 * s
        case .whisper:
            let w = L.envelope(t, attack: 0.35, release: 0.5, duration: D)
            pose.sx = 1 - 0.35 * A * w
            pose.sy = pose.sx
            pose.opacity = 1 - 0.6 * A * w
            pose.dy = -0.05 * A * w
        case .ghost:
            let g = L.envelope(t, attack: 0.35, release: 0.5, duration: D)
            pose.opacity = 1 - 0.55 * A * g
            pose.dx = 0.35 * A * g * sin(.pi * L.clamp01(t / D))
            pose.dy = -0.1 * A * g
            pose.cool = 0.4 * g
        case .glow:
            let e = L.envelope(t, attack: 0.25, release: 0.45, duration: D)
            pose.glow = 1.6 * e
            pose.bloom = A * e
            pose.sx = 1 + 0.06 * A * e
            pose.sy = pose.sx
        case .electric:
            let e = L.envelope(t, attack: 0.05, release: 0.3, duration: D)
            let frame = Int(t * 30)
            pose.dx = 0.05 * A * e * (L.rnd(7, frame, 1) * 2 - 1)
            pose.dy = 0.04 * A * e * (L.rnd(7, frame, 2) * 2 - 1)
            let flash = L.rnd(7, Int(t * 15), 3) > 0.7 ? 1.0 : 0.0
            pose.glow = e * (0.3 + 1.3 * flash)
            pose.cool = e * 0.35 * flash
        }
        return pose
    }
}

extension LivelyAction {
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

    /// Sink: 0 → 1 as a letter goes under (slow to start, heavy, a bob at
    /// the bottom), back up quickly past the line (a little below 0) and
    /// settling.
    static func sink(_ t: Double, delay: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        let under = spring(t - 0.05 - delay, peak: 0.55, overshoot: 0.1)
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

    func body(content: Content) -> some View {
        let duration = LivelyAction.duration(action, hold: hold)
        let active = elapsed > 0 && elapsed < duration
        if !active {
            content
        } else if reduceMotion {
            // Reduce Motion: today's cue (a glow in the word's own ink,
            // warmer or cooler for the colour actions), stretched over the
            // lively duration; nothing moves, scales, turns or fades.
            let cue = WordActionPose.bump(elapsed / duration, rise: 0.2, fall: 0.5)
            content
                .colorMultiply(LivelyPose(warm: action == .flicker ? 0.5 * cue : 0,
                                          cool: action == .frost ? 0.5 * cue : 0,
                                          red: action == .bleed ? 0.4 * cue : 0).multiply)
                .shadow(color: glow.opacity(0.75 * 0.6 * cue), radius: max(1, size * 0.13 * CGFloat(cue)))
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

/// A real turn about the upright axis, in perspective. Drawn flattened
/// (with room around it for the near edge to grow), so it reads the same
/// wherever the word is drawn, including a snapshot of the view.
struct LivelyTurn: ViewModifier {
    /// Nil: no turn (and no flattening).
    let degrees: Double?
    let room: CGFloat

    func body(content: Content) -> some View {
        if let degrees {
            content
                .rotation3DEffect(.degrees(degrees), axis: (x: 0, y: 1, z: 0), perspective: 0.5)
                .padding(room)
                .drawingGroup()
                .padding(-room)
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

    func body(content: Content) -> some View {
        if #available(macOS 15.0, *), let glyphs {
            content.textRenderer(LivelyGlyphRenderer(state: glyphs, particles: particles && particlesAllowed))
        } else {
            content
        }
    }
}

extension View {
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
        for line in layout {
            for run in line {
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
            let ti = 0.08 + g.along * 0.5 * min(D - 0.4, 1.0)
            let w = exp(-pow((t - ti) / 0.14, 2)) * L.rest(t, D)
            let dy = 0.05 * A * sin(2 * .pi * 19 * t + i * 1.7) * w
            let deg = 10 * A * sin(2 * .pi * 13 * t + i) * w
            transform(dy: dy * size, deg: deg, anchor: mid, into: &c)
            plain()
        case .float:
            // The label carries the lift; each letter bobs on its own a
            // little out of step, like things on water.
            let drift = L.ss((t - 0.1 - g.along * 0.15) / 0.45) * L.rest(t, D, last: 0.6 + 0.1 * g.along)
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
        case .whisper:
            let w = L.envelope(t, attack: 0.35, release: 0.5, duration: D)
            transform(dx: Double(g.rect.midX - word.midX) * 0.3 * A * w, anchor: mid, into: &c)
            plain()
        case .dissolve:
            dust(g, size: size, in: c)
        case .flicker:
            let e = L.envelope(t, attack: 0.15, release: 0.45, duration: D)
            c.opacity = 1 - 0.45 * A * e * max(0, sin(2 * .pi * (5.3 + i * 0.7) * t + i * 2))
            transform(dy: -0.03 * A * e * sin(2 * .pi * 3 * t + i) * size, anchor: mid, into: &c)
            plain()
        case .frost:
            let fr = frozen(g, word: word)
            if fr > 0.01 {
                c.addFilter(.colorMultiply(Color(red: 1 - 0.4 * fr, green: 1 - 0.14 * fr, blue: 1)))
            }
            // Stiff: a hair taller while frozen.
            transform(sy: 1 + 0.03 * A * fr, anchor: foot, into: &c)
            plain()
            if fr > 0.01 {
                // A pale frosted sheen over the letter.
                var sheen = c
                var matrix = ColorMatrix()
                matrix.r1 = 0; matrix.r5 = 0.86
                matrix.g2 = 0; matrix.g5 = 0.95
                matrix.b3 = 0; matrix.b5 = 1
                matrix.a4 = 0.4 * Float(fr * A)
                sheen.addFilter(.colorMatrix(matrix))
                sheen.draw(g.slice)
            }
        case .dark:
            let a = -0.3 + 1.6 * L.ss(t / 0.55)
            let b = -0.3 + 1.6 * L.ss((t - (D - 0.6)) / 0.6)
            let darkness = L.ss((a - g.along) / 0.3) * (1 - L.ss((b - g.along) / 0.3))
            // In the dark, but still legible.
            c.opacity = 1 - 0.55 * A * darkness
            if darkness > 0.01 {
                let grey = 1 - 0.5 * darkness
                c.addFilter(.colorMultiply(Color(red: grey, green: grey, blue: grey * 1.02)))
            }
            plain()
        case .shatter:
            shards(g, size: size, in: c)
        case .melt:
            melt(g, size: size, in: c)
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
        case .explode:
            let e = L.easeOutCubic(t / 0.3) * (1 - L.ss((t - (D - 0.65)) / 0.55))
            var dir = CGVector(dx: g.rect.midX - word.midX, dy: g.rect.midY - word.midY - CGFloat(size) * 0.3)
            let len = max(0.001, sqrt(dir.dx * dir.dx + dir.dy * dir.dy))
            dir = CGVector(dx: dir.dx / len, dy: dir.dy / len)
            let reach = size * (0.6 + 0.5 * L.rnd(state.seed, g.index, 1)) * A * e
            c.opacity = 1 - 0.3 * e
            transform(dx: Double(dir.dx) * reach, dy: Double(dir.dy) * reach + 0.1 * size * e * e,
                      sx: 1 - 0.2 * e, sy: 1 - 0.2 * e, deg: (L.rnd(state.seed, g.index, 2) - 0.5) * 120 * A * e,
                      anchor: mid, into: &c)
            plain()
        case .ghost:
            let gh = L.envelope(t, attack: 0.35, release: 0.5, duration: D)
            var copy = c
            copy.opacity = 0.3 * gh
            transform(dx: -0.32 * A * gh * size, dy: 0.04 * size * gh, anchor: mid, into: &copy)
            copy.draw(g.slice)
            plain()
        case .electric:
            let e = L.envelope(t, attack: 0.05, release: 0.3, duration: D)
            let frame = Int(t * 30)
            transform(dx: 0.05 * A * e * (L.rnd(state.seed, frame, g.index * 2) * 2 - 1) * size,
                      dy: 0.05 * A * e * (L.rnd(state.seed, frame, g.index * 2 + 1) * 2 - 1) * size,
                      anchor: mid, into: &c)
            plain()
        default:
            plain()
        }
    }

    /// How frozen a letter is: the frost front creeps across the word, then thaws.
    private func frozen(_ g: Glyph, word: CGRect) -> Double {
        let t = state.t, D = state.duration
        let front = -0.15 + 1.3 * LivelyAction.ss(t / 0.5)
        let thaw = LivelyAction.ss((t - (D - 0.5)) / 0.5)
        return LivelyAction.ss((front - g.along) / 0.25) * (1 - thaw)
    }

    /// Dissolve: the letter breaks into drifting dust (cells of its own ink) and re-forms.
    private func dust(_ g: Glyph, size: Double, in context: GraphicsContext) {
        typealias L = LivelyAction
        let t = state.t, D = state.duration, A = state.intensity
        let start = 0.05 + g.along * 0.35, back = D - 0.8 + g.along * 0.25
        let d = L.ss((t - start) / 0.4) * (1 - L.ss((t - back) / 0.45))
        guard d > 0.005 else { context.draw(g.slice); return }
        // A ghost of the letter stays, so the word still reads while most
        // of its ink drifts off as dust.
        var base = context
        base.opacity = 1 - 0.62 * A * L.ss(d / 0.6)
        base.draw(g.slice)
        let cell = max(3, CGFloat(size) * 0.1)
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
                let swirl = t * 2.2 + r3 * 6.3
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
        let wobbly = L.melt(t, delay: g.along * 0.25, duration: D)
        let m = max(0, wobbly)
        var c = context
        let topY = g.rect.minY + g.rect.height * 0.12
        let sy = 1 + 0.26 * A * wobbly
        c.translateBy(x: g.rect.midX, y: topY + CGFloat(0.03 * A * m * size))
        c.scaleBy(x: CGFloat(1 - 0.06 * A * wobbly), y: CGFloat(sy))
        c.translateBy(x: -g.rect.midX, y: -topY)
        c.draw(g.slice)
        guard m > 0.01 else { return }
        let drips = L.rnd(state.seed, g.index, 4) > 0.45 ? 2 : 1
        for k in 0..<drips {
            let x0 = g.rect.minX + g.rect.width * CGFloat(0.2 + 0.6 * L.rnd(state.seed, g.index, 5 + k))
            let w = CGFloat(size * 0.09)
            let stripH = CGFloat(size * 0.2)
            let stripTop = g.baseline - stripH
            let lag = L.ss((m - 0.25 * L.rnd(state.seed, g.index, 9 + k)) / 0.75)
            let length = CGFloat(size * (0.3 + 0.6 * L.rnd(state.seed, g.index, 7 + k)) * A * pow(lag, 1.5))
            guard length > 0.5 else { continue }
            var drip = c
            drip.clip(to: Path(roundedRect: CGRect(x: x0 - w / 2, y: stripTop, width: w, height: stripH + length),
                               cornerRadius: w / 2))
            drip.translateBy(x: 0, y: stripTop)
            drip.scaleBy(x: 1, y: (stripH + length) / stripH)
            drip.translateBy(x: 0, y: -stripTop)
            drip.draw(g.slice)
        }
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
            // Smoke at the front as the shadow crosses, and again as it lifts.
            let a = -0.3 + 1.6 * L.ss(t / 0.55)
            let e = L.envelope(t, attack: 0.3, release: 0.5, duration: D)
            let x = word.minX + word.width * CGFloat(min(1.1, a))
            let r = size * 1.1
            let rect = CGRect(x: x - r, y: word.midY - r * 0.8, width: 2 * r, height: 1.6 * r)
            context.fill(Path(ellipseIn: rect), with: .radialGradient(
                Gradient(colors: [Color.black.opacity(0.55 * A * e), .clear]),
                center: CGPoint(x: rect.midX, y: rect.midY), startRadius: 0, endRadius: r))
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
            // Embers rising off the word.
            let e = L.envelope(t, attack: 0.12, release: 0.4, duration: D)
            for k in 0..<14 {
                let born = L.rnd(seed, k, 2) * max(0.2, D - 0.7)
                let a = (t - born) / 0.7
                guard a > 0, a < 1 else { continue }
                let x = word.minX + word.width * CGFloat(L.rnd(seed, k, 1)) + size * 0.08 * CGFloat(sin(a * 7 + Double(k)))
                let y = word.minY + word.height * 0.35 - size * 1.3 * CGFloat(a) * CGFloat(0.6 + 0.4 * A)
                let r = size * 0.045 * CGFloat(1 - a * 0.5)
                let colour = Color(red: 1, green: 0.55 + 0.35 * L.rnd(seed, k, 3), blue: 0.15)
                context.fill(Path(ellipseIn: CGRect(x: x - r, y: y - r, width: 2 * r, height: 2 * r)),
                             with: .color(colour.opacity(0.95 * sin(.pi * a) * e)))
            }
        case .frost:
            // A frost sheet creeping over the word and its label, then crystals.
            let ice = Color(red: 0.86, green: 0.95, blue: 1)
            let thaw = L.ss((t - (D - 0.5)) / 0.5)
            let reach = word.minX - size * 0.3 + (word.width + size * 0.6) * CGFloat(L.ss(t / 0.5))
            let sheet = word.insetBy(dx: -size * 0.3, dy: -size * 0.12)
            var frosted = context
            frosted.clip(to: Path(CGRect(x: sheet.minX, y: sheet.minY, width: max(0, reach - sheet.minX), height: sheet.height)))
            frosted.fill(Path(roundedRect: sheet, cornerRadius: size * 0.06), with: .linearGradient(
                Gradient(colors: [ice.opacity(0.5 * A * (1 - thaw)), ice.opacity(0.18 * A * (1 - thaw))]),
                startPoint: CGPoint(x: sheet.minX, y: sheet.minY), endPoint: CGPoint(x: sheet.maxX, y: sheet.maxY)))
            for g in glyphs {
                let fr = frozen(g, word: word)
                guard fr > 0.02 else { continue }
                for k in 0..<6 {
                    let p = CGPoint(x: g.rect.minX + g.rect.width * CGFloat(0.05 + 0.9 * L.rnd(seed, g.index, 30 + k)),
                                    y: g.baseline - size * CGFloat(0.0 + 0.7 * L.rnd(seed, g.index, 40 + k)))
                    let grow = L.ss((fr - 0.2 * L.rnd(seed, g.index, 50 + k)) / 0.8)
                    let r = size * CGFloat(0.08 + 0.1 * L.rnd(seed, g.index, 60 + k)) * CGFloat(grow) * CGFloat(0.5 + 0.5 * A)
                    guard r > 0.5 else { continue }
                    context.stroke(crystal(p, r, angle: L.rnd(seed, g.index, 70 + k) * .pi),
                                   with: .color(ice.opacity(0.95 * fr)), lineWidth: max(1.2, size * 0.024))
                }
            }
        case .bleed:
            let e = L.envelope(t, attack: 0.2, release: 0.5, duration: D)
            let blood = Color(red: 0.66, green: 0.03, blue: 0.08)
            let fall = L.clamp01((t - (D - 0.5)) / 0.5)
            for g in glyphs {
                let pick = L.rnd(seed, g.index, 1)
                guard pick < 0.55 || g.index == 1 || g.index == glyphs.count - 2 else { continue }
                let x = g.rect.minX + g.rect.width * CGFloat(0.3 + 0.4 * L.rnd(seed, g.index, 2))
                let born = 0.15 + 0.45 * L.rnd(seed, g.index, 3)
                let grow = L.ss((t - born) / 0.8)
                guard grow > 0 else { continue }
                let w = size * 0.07
                let length = size * CGFloat((0.35 + 0.65 * L.rnd(seed, g.index, 4)) * A * grow)
                let topY = g.baseline - size * 0.05
                let drip = Path(roundedRect: CGRect(x: x - w / 2, y: topY, width: w, height: length), cornerRadius: w / 2)
                context.fill(drip, with: .color(blood.opacity(0.92 * e)))
                // The drop at the tip, falling away at the end.
                let bulb = w * 0.9
                let dropY = topY + length + size * CGFloat(0.6 * fall * fall)
                context.fill(Path(ellipseIn: CGRect(x: x - bulb, y: dropY - bulb, width: 2 * bulb, height: 2.3 * bulb)),
                             with: .color(blood.opacity(0.92 * (1 - fall) * min(1, grow * 3))))
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
            // A faint ring with each thump.
            var beat = 0.0
            let last = max(0.01, state.hold - 0.3)
            while beat <= last {
                let a = (t - beat) / 0.55
                if a > 0, a < 1 {
                    let grow = CGFloat(0.15 + 0.6 * a)
                    let rect = word.insetBy(dx: -word.width * grow * 0.5, dy: -word.height * grow)
                    context.stroke(Path(ellipseIn: rect), with: .color(state.glow.opacity(0.5 * (1 - a) * A)),
                                   lineWidth: max(1, size * 0.03 * CGFloat(1 - a)))
                }
                beat += 0.8
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
        case .scream:
            for k in 0..<2 {
                let a = (t - Double(k) * 0.12) / 0.6
                guard a > 0, a < 1 else { continue }
                let grow = CGFloat(0.1 + 1.0 * a)
                let rect = word.insetBy(dx: -word.width * grow * 0.5, dy: -word.height * grow * 0.6)
                context.stroke(Path(ellipseIn: rect), with: .color(state.glow.opacity(0.8 * (1 - a) * A)),
                               lineWidth: max(1, size * 0.05 * CGFloat(1 - a)))
            }
        case .glow:
            let e = L.envelope(t, attack: 0.25, release: 0.45, duration: D)
            var rays = Path()
            let centre = CGPoint(x: word.midX, y: word.midY)
            for k in 0..<12 {
                let a = Double(k) / 12 * 2 * .pi + t * 0.6
                let inner = max(word.width, word.height) * 0.55
                let outer = inner + size * CGFloat(0.4 + 0.5 * L.rnd(seed, k, 1)) * CGFloat(e * A)
                rays.move(to: CGPoint(x: centre.x + inner * CGFloat(cos(a)), y: centre.y + inner * 0.45 * CGFloat(sin(a))))
                rays.addLine(to: CGPoint(x: centre.x + outer * CGFloat(cos(a)), y: centre.y + outer * 0.45 * CGFloat(sin(a))))
            }
            context.stroke(rays, with: .color(state.glow.opacity(0.4 * e)), lineWidth: max(1, size * 0.03))
        case .electric:
            let e = L.envelope(t, attack: 0.05, release: 0.3, duration: D)
            let tick = Int(t * 15)
            guard L.rnd(seed, tick, 5) > 0.35 else { break }
            var sparks = Path()
            for k in 0..<3 {
                var p = CGPoint(x: word.minX + word.width * CGFloat(L.rnd(seed, tick, 10 + k)),
                                y: word.minY + word.height * CGFloat(L.rnd(seed, tick, 20 + k)))
                sparks.move(to: p)
                for s in 0..<5 {
                    p.x += size * CGFloat(0.18 * (L.rnd(seed, tick * 7 + k, 30 + s) - 0.3))
                    p.y += size * CGFloat(0.22 * (L.rnd(seed, tick * 7 + k, 40 + s) - 0.5))
                    sparks.addLine(to: p)
                }
            }
            context.stroke(sparks, with: .color(Color(red: 0.75, green: 0.95, blue: 1).opacity(0.95 * e)),
                           lineWidth: max(1, size * 0.022))
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
