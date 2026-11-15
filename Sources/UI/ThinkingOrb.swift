import SwiftUI

/// The instrumental-break cue: a dotted thought-orb from Jakub Antalik's
/// thinking-orbs (MIT; the engine is ported in ThinkingOrbEngine.swift),
/// drawn in the lyric colours, with an outer ring of dots that lights one
/// by one so the time left in the break reads at a glance.
///
/// - Through the break the orb is upstream's `listening` state (a waveform
///   rolling through latitude rings). With audio capture on, the music's
///   level deepens the wave, swells the dots and nudges the spin faster;
///   onsets pulse the dots larger and brighter. Smoothing: 60 ms attack,
///   250 ms release.
/// - The countdown ring: `ringDots` dots round the orb, lit clockwise from
///   twelve o'clock, the last as the hand-off to the next line begins.
///   Breaks shorter than `ringMinimumSpan` have none.
/// - In the last `focusWindow` seconds the orb tightens and brightens, and
///   cross-fades into upstream's `breathing` state (a face-on ring: the
///   breath before the singing), while the countdown ring draws in round
///   it. Then the existing hand-off: it shrinks to 30% and fades into the
///   next line's first word.
///
/// Everything the view draws is worked out in `pose`, a pure function of
/// the cue, so it can be tested and matched exactly by the Windows port.
enum ThinkingOrb {
    // MARK: Size

    /// Orb diameter: 0.8 x the layout's type size, 30...104 pt.
    static let sizeFactor = 0.8
    static let minDiameter = 30.0
    static let maxDiameter = 104.0
    /// The engine's frame, in diameters: the `listening` sphere fills about
    /// 0.77 of its frame, so the dotted sphere is about one diameter across.
    static let frameFactor = 1.3
    /// The drawing box, in diameters: room for the countdown ring and halo.
    static let canvasFactor = 1.75
    /// Below this diameter the tuned 20 px design is used, else the 64 px one.
    static let smallDesignBelow = 40.0

    static func diameter(baseSize: Double) -> Double {
        min(maxDiameter, max(minDiameter, baseSize * sizeFactor))
    }

    static func design(diameter: Double) -> OrbEngine.Size {
        diameter < smallDesignBelow ? .px20 : .px64
    }

    // MARK: States and clock

    /// Through the break.
    static let breakState = OrbEngine.State.listening
    /// The last seconds before the next line.
    static let concludingState = OrbEngine.State.breathing
    /// Upstream's `speed` prop: its tunings are for a busy spinner; an
    /// instrumental break wants about half that pace.
    static let speed = 0.55
    /// How far the seed spreads each break's starting pose, in seconds.
    static let seedSpread = 40.0

    // MARK: Audio

    /// The music, smoothed: `level` from loudness and bass, `onset` from
    /// the analyser's beat pulse, both 0...1. `phase` is the extra engine
    /// time the music has added (the spin nudged faster), in seconds.
    struct Audio: Equatable {
        var level: Double
        var onset: Double
        var phase: Double = 0
    }

    /// Level and onset smoothing: rise in `attack` seconds, fall in `release`.
    static let attack = 0.06
    static let release = 0.25
    /// Loud music runs the clock up to this much faster.
    static let levelSpeed = 0.35
    /// The level deepens `listening`'s waveform by up to this much.
    static let levelSwell = 0.6
    /// Dot radius: up this much at full level, and this much on an onset.
    static let levelDotGrowth = 0.12
    static let onsetDotGrowth = 0.25
    /// Ink strength (nearer to the bright, near-dot colour) on an onset.
    static let onsetBrighten = 0.25

    /// The raw targets from the analyser's levels. `level` is the same
    /// loudness the lyric layer already eases with (`liveEnergy`).
    static func targets(rms: Double, bass: Double, pulse: Double) -> (level: Double, onset: Double) {
        (min(1, max(0, rms * 2.6 + bass * 0.8)), min(1, max(0, pulse)))
    }

    /// One-pole follower with separate attack and release, and the spin's
    /// extra phase. Stepped once per frame while a break is on screen.
    struct AudioFollower {
        private(set) var audio = Audio(level: 0, onset: 0)
        private var lastWall: Double?

        static func approach(_ value: Double, _ target: Double, dt: Double) -> Double {
            let tau = target > value ? ThinkingOrb.attack : ThinkingOrb.release
            return value + (target - value) * (1 - exp(-dt / tau))
        }

        /// Steps to `wall`; the same `wall` twice returns the same values.
        mutating func step(level: Double, onset: Double, wall: Double) -> Audio {
            let dt = lastWall.map { wall - $0 } ?? 0
            if dt > 0 || dt < -1 || lastWall == nil {
                lastWall = wall
                // A long gap (the break began, or frames stalled) is one frame.
                let step = min(0.1, max(0, dt))
                if step > 0 {
                    audio.level = Self.approach(audio.level, level, dt: step)
                    audio.onset = Self.approach(audio.onset, onset, dt: step)
                    audio.phase += step * ThinkingOrb.speed * ThinkingOrb.levelSpeed * audio.level
                }
            }
            return audio
        }
    }

    // MARK: Countdown

    /// Breaks the orb is up for less than this have no countdown ring.
    static let ringMinimumSpan = 3.0
    static let ringDotsLarge = 24
    static let ringDotsSmall = 12
    /// Ring radius, in diameters; drawn in to `ringFocusRadius` at the end.
    static let ringRadius = 0.68
    static let ringFocusRadius = 0.6
    /// The 20 px design's dots are larger: its ring sits this much further out.
    static let smallRingOffset = 0.08
    /// The last seconds, before the hand-off, in which the orb focuses.
    static let focusWindow = 2.0
    /// At full focus the orb is this size.
    static let focusScale = 0.86

    static func ringDots(diameter: Double) -> Int {
        design(diameter: diameter) == .px20 ? ringDotsSmall : ringDotsLarge
    }

    /// How full the ring is, 0...1: from the break's start to the start of
    /// the hand-off (`WallpaperMotion.interludeOut` before `span`), when
    /// the last dot lights. Nil when there is no ring: the end is not known
    /// or the break is shorter than `ringMinimumSpan`.
    static func ringFill(elapsed: Double, span: Double?) -> Double? {
        guard let span, span >= ringMinimumSpan else { return nil }
        return min(1, max(0, elapsed / (span - WallpaperMotion.interludeOut)))
    }

    /// Each ring dot's light, 0...1, clockwise from twelve o'clock: each
    /// takes its own 1/count of the fill.
    static func ringLights(fill: Double, count: Int) -> [Double] {
        (0..<count).map { min(1, max(0, fill * Double(count) - Double($0))) }
    }

    /// 0...1: how far the orb has focused, over the `focusWindow` (at most
    /// half the break) that ends as the hand-off begins.
    static func focus(elapsed: Double, span: Double?) -> Double {
        guard let span else { return 0 }
        let end = span - WallpaperMotion.interludeOut
        let window = min(focusWindow, 0.5 * span)
        guard window > 0 else { return 0 }
        return Easing.smoothstep((elapsed - (end - window)) / window)
    }

    // MARK: Pose

    /// Arrival grows from this scale; leaving shrinks to `exitScale`.
    static let enterScale = 0.55
    static let exitScale = 0.30

    struct Layer: Equatable {
        var state: OrbEngine.State
        /// Engine time (already multiplied by the preset's speed).
        var t: Double
        var weight: Double
    }

    struct Pose: Equatable {
        var opacity: Double
        /// Overall scale: arrival x leaving x focus; 1 with Reduce Motion.
        var scale: Double
        /// One or two engine states, cross-faded by weight.
        var layers: [Layer]
        /// `listening`'s waveform depth, 1 = upstream's.
        var swell: Double
        /// Dot radius multiplier (the music).
        var dotScale: Double
        /// Added to every dot's ink strength (onsets and focus brighten).
        var brighten: Double
        var focus: Double
        /// Each countdown dot's light; nil with no ring.
        var ring: [Double]?
        /// In diameters.
        var ringRadius: Double
        var haloAlpha: Double
        var increaseContrast: Bool
    }

    static func pose(_ cue: InstrumentalCue, diameter: Double = 48, reduceMotion: Bool,
                     increaseContrast: Bool) -> Pose {
        let size = design(diameter: diameter)
        let audio = reduceMotion ? nil : cue.audio
        let level = audio?.level ?? 0, onset = audio?.onset ?? 0
        let focus = focus(elapsed: cue.elapsed, span: cue.span)
        // The shared clock: the break's own seconds, offset by its seed, the
        // music's extra phase on top. Reduce Motion: upstream's still frame.
        let seconds = Double(cue.seed & 0xFFFF) / 65536 * seedSpread + max(0, cue.elapsed) * speed
            + (audio?.phase ?? 0)
        func layer(_ state: OrbEngine.State, _ weight: Double) -> Layer {
            Layer(state: state, t: reduceMotion ? OrbEngine.reducedMotionTime
                  : seconds * OrbEngine.resolve(state, size).speed, weight: weight)
        }
        var layers: [Layer] = []
        if focus < 1 { layers.append(layer(breakState, 1 - focus)) }
        if focus > 0 { layers.append(layer(concludingState, focus)) }
        let arrive = enterScale + (1 - enterScale) * cue.entering
        let leave = 1 - (1 - exitScale) * cue.leaving
        let tighten = 1 - (1 - focusScale) * focus
        let fill = ringFill(elapsed: cue.elapsed, span: cue.span)
        return Pose(opacity: cue.opacity,
                    scale: reduceMotion ? 1 : arrive * leave * tighten,
                    layers: layers,
                    swell: 1 + levelSwell * level,
                    dotScale: 1 + levelDotGrowth * level + onsetDotGrowth * onset,
                    brighten: onsetBrighten * onset + 0.2 * focus,
                    focus: focus,
                    ring: fill.map { ringLights(fill: $0, count: ringDots(diameter: diameter)) },
                    ringRadius: (size == .px20 ? smallRingOffset : 0)
                        + (reduceMotion ? ringRadius : ringRadius + (ringFocusRadius - ringRadius) * focus),
                    haloAlpha: 0.10 + 0.14 * focus,
                    increaseContrast: increaseContrast)
    }

    /// The engine frames a pose draws, in the design's own units, with
    /// their weights. `listening` gets the music's swell.
    static func frames(_ pose: Pose, diameter: Double) -> [(frame: OrbEngine.Frame, weight: Double)] {
        let size = design(diameter: diameter)
        return pose.layers.map { layer in
            let resolved = OrbEngine.resolve(layer.state, size)
            var options = resolved.options
            if resolved.mode == .wave { options["swell"] = pose.swell }
            return (OrbEngine.frame(resolved.mode, size: Double(size.rawValue), t: layer.t, options: options), layer.weight)
        }
    }

    // MARK: Colour

    /// The orb's colours, in OKLab.
    struct Palette: Equatable {
        /// Primary (the colour the dots had), then its two neighbours.
        var lobes: [OKLab]
        /// Behind the orb: a paler version of the primary over light text;
        /// a light halo under dark text.
        var halo: OKLab
        /// The near-dot highlight.
        var core: OKLab
    }

    /// The two neighbours of a palette with one colour: its hue a step
    /// either way. A neutral (Static white, or its deep charcoal) has no
    /// hue, so its neighbours are `tint` (the album accent) and the accent
    /// a step round.
    static func neighbours(of colour: OKLab, tint: OKLab?) -> (OKLab, OKLab) {
        if colour.chroma <= neutralChroma, let tint {
            let chroma = max(tint.chroma, neighbourMinChroma)
            return (OKLab(l: tint.l, chroma: chroma, hue: tint.hue),
                    OKLab(l: tint.l, chroma: chroma, hue: tint.hue + neighbourHueStep))
        }
        let chroma = max(colour.chroma, neighbourMinChroma)
        return (OKLab(l: colour.l, chroma: chroma, hue: colour.hue + neighbourHueStep),
                OKLab(l: colour.l, chroma: chroma, hue: colour.hue - neighbourHueStep))
    }
    /// At or below this OKLab chroma a colour counts as neutral.
    static let neutralChroma = 0.03
    /// Hue step to the neighbouring colours when the palette has one colour.
    static let neighbourHueStep = 0.4
    static let neighbourMinChroma = 0.06

    /// `primary` as it is, the neighbours made readable and toned like it
    /// (deep on a light wallpaper). For `LyricInkDirector`.
    static func stops(primary: OKLab, neighbours: (OKLab, OKLab), ink: LyricInk) -> [OKLab] {
        func fit(_ source: OKLab) -> OKLab {
            ink.toned(Perceptual.readable(source, background: ink.backdrop, ratio: ink.contrast), source: source)
        }
        return [primary, fit(neighbours.0), fit(neighbours.1)]
    }

    static func palette(_ stops: [OKLab], dark: Double) -> Palette {
        let primary = stops.first ?? .white
        let lobes = (0..<3).map { stops.indices.contains($0) ? stops[$0] : primary }
        let dark = min(1, max(0, dark))
        let lightHalo = OKLab(l: 0.97, chroma: min(0.02, primary.chroma), hue: primary.hue)
        let halo = Perceptual.mix(Perceptual.mix(primary, .white, 0.35), lightHalo, dark)
        let core = Perceptual.mix(primary, .white, 0.6 - 0.35 * dark)
        return Palette(lobes: lobes, halo: halo, core: core)
    }

    static let black = OKLab(l: 0, a: 0, b: 0)
    /// Upstream's ink, coloured. Its `white` is the paper-theme grey (0 the
    /// strongest mark); on a dark substrate it mirrors so near dots read
    /// bright. Here strength = 1 - white in both: near dots take the strong
    /// colour (light over a dark wallpaper, deep for dark lyrics), far dots
    /// fade toward the wallpaper's side. `dark` is Lunalyr's: 1 = dark
    /// lyrics on a light wallpaper (upstream's light theme).
    static func dotColour(_ lobe: OKLab, strength: Double, dark: Double) -> OKLab {
        let dark = min(1, max(0, dark))
        let near = Perceptual.mix(Perceptual.mix(lobe, .white, 0.3), lobe, dark)
        let paper = Perceptual.mix(black, .white, dark)
        let far = Perceptual.mix(lobe, paper, 0.55)
        return Perceptual.mix(far, near, strength)
    }

    /// Ink strength levels and vertical colour bands the view caches colours in.
    static let strengthLevels = 10
    static let colourBands = 5
}

/// The thinking orb, centred where the dots were.
struct ThinkingOrbView: View {
    let cue: InstrumentalCue
    /// The album accent: the orb's colour when the ink has none of its own.
    let accent: Color
    let baseSize: CGFloat
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.lyricInk) private var ink

    var body: some View {
        let diameter = ThinkingOrb.diameter(baseSize: Double(baseSize))
        let pose = ThinkingOrb.pose(cue, diameter: diameter, reduceMotion: reduceMotion,
                                    increaseContrast: ink.contrast >= LyricInk.increasedContrast)
        let frames = ThinkingOrb.frames(pose, diameter: diameter)
        let colours = Self.colours(ink: ink, accent: accent)
        let side = diameter * ThinkingOrb.canvasFactor
        Canvas { context, size in
            Self.draw(pose, frames: frames, colours: colours, diameter: diameter, in: &context, size: size)
        }
        .frame(width: side, height: side)
        .opacity(pose.opacity)
        .accessibilityElement()
        .accessibilityLabel("Instrumental break")
        .accessibilityValue(cue.remaining.map { "\(Int($0.rounded(.up))) seconds until the next line" } ?? "")
        .allowsHitTesting(false)
    }

    /// Colours cached per frame: `colourBands` bands top to bottom (the
    /// palette's neighbour, primary, other neighbour) x `strengthLevels`.
    struct Colours {
        var dots: [[Color]]
        var halo: Color
        /// A dark-lyrics halo is stronger: it is what keeps deep dots legible.
        var dark: Double
    }

    static func colours(ink: LyricInk, accent: Color) -> Colours {
        let stops: [OKLab]
        let dark: Double
        if ink.orb.isEmpty {
            // Ink no director made: the accent, as the dots had it.
            let rgb = NSColor(accent).usingColorSpace(.sRGB)
            let lab = Perceptual.oklab(sRGB: SIMD3(Double(rgb?.redComponent ?? 1), Double(rgb?.greenComponent ?? 0.6),
                                                   Double(rgb?.blueComponent ?? 0.2)))
            stops = [lab, lab, lab]
            dark = 0
        } else {
            stops = ink.orb
            dark = ink.dark
        }
        let palette = ThinkingOrb.palette(stops, dark: dark)
        let bandStops = [palette.lobes[1], palette.lobes[0], palette.lobes[2]]
        let levels = ThinkingOrb.strengthLevels, bands = ThinkingOrb.colourBands
        let dots = (0..<bands).map { band -> [Color] in
            let f = Double(band) / Double(bands - 1) * 2
            let lobe = f <= 1 ? Perceptual.mix(bandStops[0], bandStops[1], f) : Perceptual.mix(bandStops[1], bandStops[2], f - 1)
            return (0..<levels).map { level in
                Perceptual.color(ThinkingOrb.dotColour(lobe, strength: Double(level) / Double(levels - 1), dark: dark))
            }
        }
        return Colours(dots: dots, halo: Perceptual.color(palette.halo), dark: dark)
    }

    private static func draw(_ pose: ThinkingOrb.Pose, frames: [(frame: OrbEngine.Frame, weight: Double)],
                             colours: Colours, diameter: Double, in context: inout GraphicsContext, size: CGSize) {
        let centre = CGPoint(x: size.width / 2, y: size.height / 2)
        let native = Double(ThinkingOrb.design(diameter: diameter).rawValue)
        let k = diameter * ThinkingOrb.frameFactor / native * pose.scale
        let levels = ThinkingOrb.strengthLevels, bands = ThinkingOrb.colourBands
        func colour(strength: Double, y: Double) -> Color {
            let band = min(bands - 1, max(0, Int((y / native) * Double(bands))))
            let level = min(levels - 1, max(0, Int((strength * Double(levels - 1)).rounded())))
            return colours.dots[band][level]
        }
        func alpha(_ a: Double) -> Double { pose.increaseContrast ? min(1, a * 1.25 + 0.1) : a }

        // Halo: a light one keeps deep dots legible on a light wallpaper;
        // over a dark one it is a faint glow that rises as the orb focuses.
        let haloRadius = diameter * 0.8 * pose.scale
        let haloAlpha = min(1, pose.haloAlpha + 0.45 * colours.dark)
        let gradient = Gradient(stops: [.init(color: colours.halo.opacity(haloAlpha), location: 0),
                                        .init(color: colours.halo.opacity(haloAlpha * 0.4), location: 0.55),
                                        .init(color: colours.halo.opacity(0), location: 1)])
        context.fill(Path(ellipseIn: CGRect(x: centre.x - haloRadius, y: centre.y - haloRadius,
                                            width: haloRadius * 2, height: haloRadius * 2)),
                     with: .radialGradient(gradient, center: centre, startRadius: 0, endRadius: haloRadius))

        // The countdown ring, behind the orb.
        if let ring = pose.ring {
            let radius = diameter * pose.ringRadius * pose.scale
            let unit = k * ThinkingOrb.ringDotUnit
            for (index, light) in ring.enumerated() {
                let angle = -Double.pi / 2 + Double(index) / Double(ring.count) * 2 * .pi
                let point = CGPoint(x: centre.x + radius * cos(angle), y: centre.y + radius * sin(angle))
                let r = unit * (0.75 + 0.45 * light + 0.15 * pose.focus)
                let strength = min(1, ThinkingOrb.ringRestStrength + (1 - ThinkingOrb.ringRestStrength) * light
                                   + pose.brighten * light)
                let a = alpha(ThinkingOrb.ringRestAlpha + (1 - ThinkingOrb.ringRestAlpha) * light)
                context.fill(Path(ellipseIn: CGRect(x: point.x - r, y: point.y - r, width: 2 * r, height: 2 * r)),
                             with: .color(colour(strength: strength, y: native / 2).opacity(a)))
            }
        }

        // The orb: lines first, then dots far to near.
        for (frame, weight) in frames where weight > 0.01 {
            for line in frame.lines {
                var path = Path()
                path.move(to: CGPoint(x: centre.x + (line.x1 - native / 2) * k, y: centre.y + (line.y1 - native / 2) * k))
                path.addLine(to: CGPoint(x: centre.x + (line.x2 - native / 2) * k, y: centre.y + (line.y2 - native / 2) * k))
                context.stroke(path, with: .color(colour(strength: 1 - line.white, y: native / 2)
                    .opacity(alpha(line.a) * weight)), lineWidth: line.w * k)
            }
            for dot in frame.dots {
                let r = dot.r * k * pose.dotScale
                let x = centre.x + (dot.x - native / 2) * k, y = centre.y + (dot.y - native / 2) * k
                let strength = min(1, max(0, 1 - dot.white + pose.brighten))
                context.fill(Path(ellipseIn: CGRect(x: x - r, y: y - r, width: 2 * r, height: 2 * r)),
                             with: .color(colour(strength: strength, y: dot.y).opacity(alpha(dot.a) * weight)))
            }
        }
    }
}

extension ThinkingOrb {
    /// Countdown dot radius, in the design's units (a near `listening` dot
    /// is about 0.9).
    static let ringDotUnit = 1.0
    /// An unlit countdown dot's opacity.
    static let ringRestAlpha = 0.4
    /// An unlit countdown dot's ink strength: enough to show the track.
    static let ringRestStrength = 0.45
}
