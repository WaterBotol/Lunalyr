import SwiftUI

/// The instrumental-break cue: a soft orb of two or three lyric colours that
/// slowly changes shape, breathes, and glows brighter as the break runs out.
///
/// Drawn as a handful of radial gradients in a small `Canvas` (no blur
/// filter, no Metal pass): each lobe's gradient already fades to clear, so
/// overlapping lobes read as one blurred, morphing blob. At the frame rate
/// an interlude asks for (`LyricPace.ambient`, 24 fps) this is a few
/// gradient fills of a box about two orb-widths square.
///
/// All of the motion is worked out in `pose`, a pure function of the cue,
/// so it can be tested and matched exactly by the Windows port; the view
/// only draws it.
enum ThinkingOrb {
    // MARK: Size

    /// Orb diameter: 0.8 x the layout's type size, 30...104 pt.
    static let sizeFactor = 0.8
    static let minDiameter = 30.0
    static let maxDiameter = 104.0
    /// The drawing box, in diameters: room for the halo at full breath.
    static let canvasFactor = 2.3

    static func diameter(baseSize: Double) -> Double {
        min(maxDiameter, max(minDiameter, baseSize * sizeFactor))
    }

    // MARK: Motion

    /// Each lobe circles the centre on its own period (6...10 s): the morph.
    static let morphPeriods: [Double] = [7.0, 8.6, 9.8]
    /// Lobe radii, in orb radii, and how far they sit from the centre.
    static let lobeRadii: [Double] = [0.80, 0.66, 0.58]
    static let lobeOrbit = 0.32
    /// How much each lobe's orbit and size swell over the morph.
    static let orbitSwell = 0.25
    static let radiusSwell = 0.10
    /// The breath: a sine of this period, this deep (fraction of size), and
    /// deeper still with the music's level.
    static let breathPeriod = 2.6
    static let breathDepth = 0.045
    static let breathEnergyDepth = 0.055
    /// Arrival grows from this scale; leaving shrinks to `exitScale`.
    static let enterScale = 0.55
    static let exitScale = 0.30
    /// The end-of-break glow starts at this fraction of the break.
    static let glowFrom = 0.5
    /// Hue step to the neighbouring colours when the palette has one colour.
    static let neighbourHueStep = 0.4
    static let neighbourMinChroma = 0.06

    struct Lobe: Equatable {
        /// Centre, in orb radii from the orb's centre.
        var dx: Double
        var dy: Double
        /// Radius, in orb radii.
        var radius: Double
    }

    struct Pose: Equatable {
        /// Overall scale (breath x arrival x leaving); 1 with Reduce Motion.
        var scale: Double
        var opacity: Double
        /// 0...1: how far the end-of-break glow has come.
        var glow: Double
        /// Lobes in palette order: primary, then the two neighbours.
        var lobes: [Lobe]
        var lobeAlpha: Double
        /// Where each lobe's gradient is still at `lobeKnee` of its alpha,
        /// 0...1 of its radius: higher is a firmer edge.
        var lobeEdge: Double
        var haloAlpha: Double
        var coreAlpha: Double
    }

    static let lobeKnee = 0.75

    static func pose(_ cue: InstrumentalCue, reduceMotion: Bool, increaseContrast: Bool) -> Pose {
        let motion = reduceMotion ? 0.0 : 1.0
        let t = cue.elapsed * motion
        let energy = min(1, max(0, cue.energy)) * motion
        let lobes = (0..<3).map { index -> Lobe in
            let phase = phaseOffset(cue.seed, index)
            let angle = phase + 2 * .pi * t / morphPeriods[index]
            let orbit = lobeOrbit * (1 + orbitSwell * sin(2 * .pi * t / (morphPeriods[index] * 1.3) + phase))
            let radius = lobeRadii[index] * (1 + radiusSwell * sin(angle * 1.5))
            return Lobe(dx: orbit * cos(angle), dy: orbit * sin(angle), radius: radius)
        }
        let breath = 1 + motion * (breathDepth + breathEnergyDepth * energy) * sin(2 * .pi * t / breathPeriod)
        let arrive = enterScale + (1 - enterScale) * cue.entering
        let leave = 1 - (1 - exitScale) * cue.leaving
        let glow = cue.progress.map { Easing.smoothstep(($0 - glowFrom) / (1 - glowFrom)) } ?? 0
        return Pose(scale: reduceMotion ? 1 : breath * arrive * leave,
                    opacity: cue.opacity,
                    glow: glow,
                    lobes: lobes,
                    lobeAlpha: increaseContrast ? 0.95 : 0.80,
                    lobeEdge: increaseContrast ? 0.65 : 0.45,
                    haloAlpha: min(1, (0.28 + 0.32 * glow + 0.12 * energy) * (increaseContrast ? 1.25 : 1)),
                    coreAlpha: min(1, 0.30 + 0.40 * glow + 0.10 * energy))
    }

    /// The lobe's starting angle: its third of the circle, nudged by the
    /// break's seed so each break begins in its own shape.
    static func phaseOffset(_ seed: UInt64, _ index: Int) -> Double {
        let bits = Double((seed >> UInt64(index * 16)) & 0xFFFF) / 65536
        return 2 * .pi * (Double(index) / 3 + bits * 0.5)
    }

    // MARK: Colour

    /// The orb's colours, in OKLab.
    struct Palette: Equatable {
        /// Primary (the colour the dots had), then its two neighbours.
        var lobes: [OKLab]
        /// Behind the orb: a paler version of the primary over light text;
        /// a light halo under dark text.
        var halo: OKLab
        /// The inner glow, near the centre.
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
        let pose = ThinkingOrb.pose(cue, reduceMotion: reduceMotion,
                                    increaseContrast: ink.contrast >= LyricInk.increasedContrast)
        let colours = Self.colours(ink: ink, accent: accent)
        let side = diameter * ThinkingOrb.canvasFactor
        Canvas { context, size in
            Self.draw(pose, colours: colours, diameter: diameter, in: &context, size: size)
        }
        .frame(width: side, height: side)
        .opacity(pose.opacity)
        .accessibilityLabel("Instrumental break")
        .allowsHitTesting(false)
    }

    struct Colours {
        var lobes: [Color]
        var halo: Color
        var core: Color
    }

    static func colours(ink: LyricInk, accent: Color) -> Colours {
        guard !ink.orb.isEmpty else {
            // Ink no director made: the accent, as the dots had it.
            return Colours(lobes: [accent, accent, accent], halo: accent, core: .white)
        }
        let palette = ThinkingOrb.palette(ink.orb, dark: ink.dark)
        return Colours(lobes: palette.lobes.map { Perceptual.color($0) }, halo: Perceptual.color(palette.halo),
                       core: Perceptual.color(palette.core))
    }

    private static func draw(_ pose: ThinkingOrb.Pose, colours: Colours, diameter: Double,
                             in context: inout GraphicsContext, size: CGSize) {
        let centre = CGPoint(x: size.width / 2, y: size.height / 2)
        let r = diameter / 2 * pose.scale
        func blob(_ colour: Color, at point: CGPoint, radius: Double, stops: [(Double, Double)]) {
            let gradient = Gradient(stops: stops.map { .init(color: colour.opacity($0.1), location: $0.0) })
            let rect = CGRect(x: point.x - radius, y: point.y - radius, width: radius * 2, height: radius * 2)
            context.fill(Path(ellipseIn: rect), with: .radialGradient(gradient, center: point, startRadius: 0,
                                                                     endRadius: radius))
        }
        // Halo: twice the orb's radius.
        blob(colours.halo, at: centre, radius: r * 2,
             stops: [(0, pose.haloAlpha), (0.45, pose.haloAlpha * 0.45), (1, 0)])
        // Neighbours first, the primary over them, so their colours show
        // round its edge as it turns.
        for index in [1, 2, 0] {
            let lobe = pose.lobes[index]
            let alpha = pose.lobeAlpha * (index == 0 ? 0.9 : 1)
            blob(colours.lobes[index], at: CGPoint(x: centre.x + lobe.dx * r, y: centre.y + lobe.dy * r),
                 radius: lobe.radius * r,
                 stops: [(0, alpha), (pose.lobeEdge, alpha * ThinkingOrb.lobeKnee), (1, 0)])
        }
        // Inner glow, a little above and left of centre.
        blob(colours.core, at: CGPoint(x: centre.x - 0.12 * r, y: centre.y - 0.16 * r), radius: 0.55 * r,
             stops: [(0, pose.coreAlpha), (1, 0)])
    }
}
