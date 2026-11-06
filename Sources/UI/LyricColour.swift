import SwiftUI

// MARK: - Perceptual colour

/// A colour in OKLab (Björn Ottosson, 2020): lightness `l` 0...1 and the
/// opponent axes `a` (green-red) and `b` (blue-yellow). Straight lines here
/// are perceptually even, so blends and drifts never sag through grey or
/// flare in brightness the way sRGB blends do.
struct OKLab: Equatable {
    var l: Double
    var a: Double
    var b: Double

    init(l: Double, a: Double, b: Double) { self.l = l; self.a = a; self.b = b }
    /// OKLCH: lightness, chroma and hue angle in radians.
    init(l: Double, chroma: Double, hue: Double) {
        self.l = l; a = chroma * cos(hue); b = chroma * sin(hue)
    }
    var chroma: Double { (a * a + b * b).squareRoot() }
    var hue: Double { atan2(b, a) }

    static let white = OKLab(l: 1, a: 0, b: 0)
}

enum Perceptual {
    static func linear(_ channel: Double) -> Double {
        channel <= 0.04045 ? channel / 12.92 : pow((channel + 0.055) / 1.055, 2.4)
    }
    static func gamma(_ channel: Double) -> Double {
        channel <= 0.0031308 ? 12.92 * channel : 1.055 * pow(channel, 1 / 2.4) - 0.055
    }

    static func oklab(sRGB rgb: SIMD3<Double>) -> OKLab {
        let r = linear(rgb.x), g = linear(rgb.y), b = linear(rgb.z)
        let l = cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b)
        let m = cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b)
        let s = cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b)
        return OKLab(l: 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                     a: 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                     b: 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s)
    }

    /// Linear-light sRGB; components may fall outside 0...1 out of gamut.
    static func linearRGB(_ lab: OKLab) -> SIMD3<Double> {
        let l = lab.l + 0.3963377774 * lab.a + 0.2158037573 * lab.b
        let m = lab.l - 0.1055613458 * lab.a - 0.0638541728 * lab.b
        let s = lab.l - 0.0894841775 * lab.a - 1.2914855480 * lab.b
        let l3 = l * l * l, m3 = m * m * m, s3 = s * s * s
        return SIMD3(4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3,
                     -1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3,
                     -0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3)
    }

    static func sRGB(_ lab: OKLab) -> SIMD3<Double> {
        let linear = linearRGB(lab)
        return SIMD3(gamma(min(1, max(0, linear.x))), gamma(min(1, max(0, linear.y))), gamma(min(1, max(0, linear.z))))
    }

    static func inGamut(_ lab: OKLab) -> Bool {
        let rgb = linearRGB(lab), tolerance = 1e-7
        return rgb.x >= -tolerance && rgb.y >= -tolerance && rgb.z >= -tolerance
            && rgb.x <= 1 + tolerance && rgb.y <= 1 + tolerance && rgb.z <= 1 + tolerance
    }

    /// Brings a colour into the sRGB gamut by lowering chroma only, keeping
    /// its lightness and hue -- the perceptual way to fit a vivid colour.
    static func clipped(_ lab: OKLab) -> OKLab {
        let l = min(1, max(0, lab.l))
        let fitted = OKLab(l: l, a: lab.a, b: lab.b)
        if inGamut(fitted) { return fitted }
        var low = 0.0, high = 1.0
        for _ in 0..<14 {
            let mid = (low + high) / 2
            if inGamut(OKLab(l: l, a: lab.a * mid, b: lab.b * mid)) { low = mid } else { high = mid }
        }
        return OKLab(l: l, a: lab.a * low, b: lab.b * low)
    }

    /// WCAG relative luminance of an in-gamut colour.
    static func luminance(_ lab: OKLab) -> Double {
        let rgb = linearRGB(clipped(lab))
        let value = 0.2126 * rgb.x + 0.7152 * rgb.y + 0.0722 * rgb.z
        return min(1, max(0, value))
    }
    static func luminance(sRGB rgb: SIMD3<Double>) -> Double {
        min(1, max(0, 0.2126 * linear(rgb.x) + 0.7152 * linear(rgb.y) + 0.0722 * linear(rgb.z)))
    }

    /// WCAG contrast ratio between two relative luminances.
    static func contrast(_ first: Double, _ second: Double) -> Double {
        (max(first, second) + 0.05) / (min(first, second) + 0.05)
    }

    static func mix(_ from: OKLab, _ to: OKLab, _ amount: Double) -> OKLab {
        let t = min(1, max(0, amount))
        return OKLab(l: from.l + (to.l - from.l) * t, a: from.a + (to.a - from.a) * t, b: from.b + (to.b - from.b) * t)
    }

    /// Lightens a colour, along its own hue, until it reaches `ratio`
    /// against `background`. Where even white cannot reach it, the
    /// lightest version of the colour is returned: the text keeps its
    /// dark shadow for the rest.
    static func readable(_ lab: OKLab, background: Double, ratio: Double = LyricInk.minimumContrast) -> OKLab {
        let colour = clipped(lab)
        let target = ratio * (background + 0.05) - 0.05
        if luminance(colour) >= target { return colour }
        if target >= 1 { return clipped(OKLab(l: 1, a: colour.a * 0.3, b: colour.b * 0.3)) }
        var low = colour.l, high = 1.0
        for _ in 0..<16 {
            let mid = (low + high) / 2
            if luminance(OKLab(l: mid, a: colour.a, b: colour.b)) >= target { high = mid } else { low = mid }
        }
        return clipped(OKLab(l: high, a: colour.a, b: colour.b))
    }

    static func color(_ lab: OKLab, opacity: Double = 1) -> Color {
        let rgb = sRGB(clipped(lab))
        return Color(.sRGB, red: rgb.x, green: rgb.y, blue: rgb.z, opacity: opacity)
    }
}

// MARK: - Modes

/// How lyric text is coloured. Standard is neutral renderer ink and the old saved “static” value.
enum LyricColourMode: String, CaseIterable, Identifiable {
    case standard = "static", albumDrift, sungSweep, musicReactive, customGradient
    var id: String { rawValue }
    static let defaultMode: Self = .albumDrift
    static var choices: [Self] { allCases.filter { $0 != .standard } }

    static func savedChoice(_ raw: String) -> Self? {
        guard let mode = Self(rawValue: raw) else { return nil }
        return mode == .standard ? defaultMode : mode
    }

    var title: String {
        switch self {
        case .standard: return "Static white"
        case .albumDrift: return "Album drift"
        case .sungSweep: return "Sung sweep"
        case .musicReactive: return "Music reactive"
        case .customGradient: return "Custom gradient"
        }
    }

    var detail: String {
        switch self {
        case .standard: return "White type, as before."
        case .albumDrift: return "Cover colours and coordinating accents drift slowly through the words."
        case .sungSweep: return "White lyrics fill with an accent that sets off the cover as each word or line is sung. Unsynced lyrics use Album Drift."
        case .musicReactive: return "Hue follows bass against treble; brightness and depth follow loudness. Uses live audio when a visualiser is capturing it, and the vocal timing otherwise."
        case .customGradient: return "Your own two to four colours, drifting through the line."
        }
    }

    var drifts: Bool { self == .albumDrift || self == .customGradient }
}

// MARK: - Ink

/// Colours for one word on one frame, already made readable.
struct LyricInkSample: Equatable {
    /// The word's colour before it is sung, and its whole colour when the
    /// ink does not sweep.
    var rest: Color
    /// The colour a sweep fills the word with as it is sung.
    var sung: Color
    var glow: Color
    var reading: Color
    /// Whether `sung` differs from `rest`, so a second, masked layer is needed.
    var sweeps: Bool
    /// Opacity of words not yet sung, in karaoke layouts. Higher with
    /// Increase Contrast.
    var dim: Double = LyricInk.unsungDim
    /// Visible progress when the readable fill approaches white.
    var sweepUnderline: Color?

    static let white = LyricInkSample(rest: .white, sung: .white, glow: .white,
                                      reading: .white.opacity(0.7), sweeps: false)
}

/// The lyric text palette for one frame. Read from the environment by every
/// lyric view, so all eight compositions, readings, interlude dots and the
/// settings preview are coloured from one place.
struct LyricInk {
    /// WCAG AA for large text. Lyrics are display type (the smallest word
    /// is still well above 18 pt) and keep a dark shadow under every glyph,
    /// so 3:1 against the wallpaper is the floor; colours only get lighter.
    static let minimumContrast = 3.0
    /// With Increase Contrast: WCAG AA for normal text.
    static let increasedContrast = 4.5
    static let unsungDim = 0.28
    static let increasedUnsungDim = 0.55

    var mode: LyricColourMode = .standard
    var base: LyricInkSample = .white
    /// Cascade's held words: the highlight's hue as type of its own. `nil`
    /// keeps the album accent the caller already has.
    var accent: Color?
    /// Highlight tiles -- Cutout's labels and the tile "Cutout · Highlight"
    /// presses behind each sung word -- and the text set on them. Opaque;
    /// `nil` only for ink no director made.
    var tile: Color?
    var onTile: Color = .white
    /// The same highlight in OKLab, as worked out (see `LyricHighlight`).
    var highlight: LyricHighlightTone?
    /// The tile opacity at which the word on a fading tile changes back to
    /// its own colour (`LyricHighlight.textSwitch`).
    var tileTextSwitch = 0.4
    /// The tile to draw, and the text on it: the director's, or for ink no
    /// director made, a legible tile of its own.
    var tileColour: Color { tile ?? LyricHighlight.fallbackTile }
    var tileText: Color { tile == nil ? LyricHighlight.fallbackText : onTile }
    /// Constellation hairlines.
    var link: Color = .white
    /// Interlude dots. `nil` keeps the album accent.
    var dot: Color?

    /// Gradient stops for the drifting and reactive modes.
    var stops: (OKLab, OKLab, OKLab, OKLab) = (.white, .white, .white, .white)
    var stopCount = 1
    /// 0...1 drift through the gradient.
    var phase = 0.0
    /// Estimated luminance behind the lyrics.
    var backdrop = 0.08
    /// Minimum contrast against `backdrop`; higher with Increase Contrast.
    var contrast = LyricInk.minimumContrast
    /// Opacity of words not yet sung.
    var dim = LyricInk.unsungDim
    /// False while the colours are still easing towards new ones (a new
    /// cover, mode or backdrop): the frame clock must keep running for it.
    var settled = true

    private func stop(_ index: Int) -> OKLab {
        switch index % max(1, stopCount) {
        case 0: return stops.0
        case 1: return stops.1
        case 2: return stops.2
        default: return stops.3
        }
    }

    /// The colours for a word at `slot`, its 0...1 position along the line.
    func sample(slot: Double, seed: UInt64 = 0) -> LyricInkSample {
        switch mode {
        case .standard, .sungSweep:
            return base
        case .albumDrift, .customGradient, .musicReactive:
            let colour = Perceptual.readable(gradient(slot: slot, seed: seed), background: backdrop, ratio: contrast)
            var sample = Self.sample(colour)
            sample.dim = dim
            return sample
        }
    }

    /// A point on the line's gradient. Blend around the hue wheel so
    /// different pastels keep their colour between stops.
    func gradient(slot: Double, seed: UInt64 = 0) -> OKLab {
        if mode == .musicReactive {
            return Self.turning(stops.0, stops.1, min(1, max(0, slot)))
        }
        guard stopCount > 1 else { return stops.0 }
        var t = (phase + slot * 0.42 + vc_jitter(seed, 21) * 0.02).truncatingRemainder(dividingBy: 1)
        if t < 0 { t += 1 }
        let position = t * Double(stopCount)
        let index = Int(position.rounded(.down))
        return Self.turning(stop(index), stop(index + 1), position - Double(index))
    }

    /// A blend in OKLCH: lightness and chroma straight, hue the short way
    /// round. A colour with next to no hue takes the other's.
    static func turning(_ from: OKLab, _ to: OKLab, _ amount: Double) -> OKLab {
        let t = min(1, max(0, amount))
        let l = from.l + (to.l - from.l) * t, chroma = from.chroma + (to.chroma - from.chroma) * t
        if from.chroma < 0.02 { return OKLab(l: l, chroma: chroma, hue: to.hue) }
        if to.chroma < 0.02 { return OKLab(l: l, chroma: chroma, hue: from.hue) }
        var turn = (to.hue - from.hue).truncatingRemainder(dividingBy: 2 * .pi)
        if turn > .pi { turn -= 2 * .pi } else if turn < -.pi { turn += 2 * .pi }
        return OKLab(l: l, chroma: chroma, hue: from.hue + turn * t)
    }

    /// Keep the source hue, with more colour and room for contrast lifting.
    /// An intentionally neutral custom stop stays neutral.
    static func pastel(_ colour: OKLab, fallbackHue: Double? = nil) -> OKLab {
        let lightness = min(0.86, max(0.76, colour.l))
        if colour.chroma < 0.02 {
            guard let fallbackHue else { return colour }
            return OKLab(l: lightness, chroma: 0.11, hue: fallbackHue)
        }
        return OKLab(l: lightness, chroma: min(0.21, max(0.105, colour.chroma * 1.4)), hue: colour.hue)
    }

    static func applyingStrength(_ strength: Double, to colour: OKLab) -> OKLab {
        // Very pale colours run out of sRGB chroma quickly. Deepen them
        // slightly above 100% to make room for richer colour. The usual
        // contrast pass still lifts the result on bright backgrounds.
        let depth = 0.13 * max(0, strength - 1) * min(1, colour.chroma / 0.09)
        return OKLab(l: colour.l - depth, a: colour.a * strength, b: colour.b * strength)
    }

    static func sample(_ colour: OKLab, sung: OKLab? = nil) -> LyricInkSample {
        let rest = Perceptual.color(colour)
        let fill = sung.map { Perceptual.color($0) } ?? rest
        let glowSource = Perceptual.clipped(OKLab(l: min(1, (sung ?? colour).l + 0.06),
                                                  a: (sung ?? colour).a * 0.8, b: (sung ?? colour).b * 0.8))
        return LyricInkSample(rest: rest, sung: fill, glow: Perceptual.color(glowSource),
                              reading: Perceptual.color(colour, opacity: 0.7), sweeps: sung != nil)
    }

    static let white = LyricInk()

    /// White ink, with the unsung level raised for Increase Contrast.
    static func white(increasedContrast: Bool) -> LyricInk {
        guard increasedContrast else { return .white }
        var ink = LyricInk()
        ink.contrast = increasedContrast ? Self.increasedContrast : Self.minimumContrast
        ink.dim = Self.increasedUnsungDim
        ink.base.dim = Self.increasedUnsungDim
        return ink
    }
}

private struct LyricInkKey: EnvironmentKey {
    static let defaultValue = LyricInk.white
}

extension EnvironmentValues {
    var lyricInk: LyricInk {
        get { self[LyricInkKey.self] }
        set { self[LyricInkKey.self] = newValue }
    }
}

// MARK: - Backdrop

/// How bright the wallpaper is behind the lyric stage, as WCAG relative
/// luminance, once everything the wallpaper lays over it is counted: its
/// scrim and the text backing it shades behind the stage for white type
/// (`LyricLegibility`). Generated wallpapers are estimated from the album
/// palette; a picture -- the desktop picture under "Your wallpaper", an
/// imported image or video -- from its measured luminance where the
/// wallpaper has measured it, and as a cautious mid tone until then.
///
/// It used to assume a bare mid tone for "Your wallpaper" (0.35) and a
/// palette guess for imported pictures, with no backing at all. At 0.35 no
/// colour reaches 3:1, so every text colour mode came out plain white over
/// the desktop, and pastel over a dark imported photo.
enum LyricBackdrop {
    /// The share of the text backing a line gets where it reaches the side
    /// of the stage. The wallpaper shades an ellipse 1.3 by 1.4 times the
    /// stage, full in the middle and fading from 55% of its radius to none
    /// at its rim (`NowPlayingView.textBacking`), which leaves about half
    /// at the stage's ends. Colours lifted against the darker middle came
    /// out at about 2.3:1 at the ends of long lines and scattered words.
    static let edgeBacking = 0.5

    /// The backdrop at the brightest place a line reaches: the ends of the
    /// stage, which get `edgeBacking` of the text backing.
    /// - Parameters:
    ///   - measured: the luminance behind the stage measured from the
    ///     picture shown, when the wallpaper is one and has measured it.
    ///   - backing: the backing the user set by hand, or nil for the
    ///     automatic amount: just enough for white type to reach `target`
    ///     in the middle of the stage.
    ///   - tinted: whether the wallpaper lays the album's colours over an
    ///     imported picture, as it does whenever the song has a cover.
    static func luminance(mode: Settings.BackgroundMode, motion: Bool, palette: [SIMD3<Float>], scrim: Double,
                          measured: Double? = nil, backing: Double? = nil, target: Double = 4.5,
                          tinted: Bool = false, pastelText: Bool = false, increaseContrast: Bool = false) -> Double {
        let behind = beforeBacking(mode: mode, motion: motion, palette: palette, scrim: scrim, measured: measured,
                                   tinted: tinted)
        if backing == nil, pastelText {
            let target = LyricLegibility.pastelBackingTarget(increaseContrast: increaseContrast)
            let shade = LyricLegibility.stageBacking(forLuminance: behind, existingDarkening: 0, target: target)
            // Automatic backing holds at full strength across the lyric stage.
            return LyricLegibility.darkened(behind, by: shade)
        }
        let amount = backing ?? LyricLegibility.backing(forLuminance: behind, existingDarkening: 0, target: target)
        return LyricLegibility.darkened(behind, by: min(1, max(0, amount)) * edgeBacking)
    }

    /// Whether no colour can reach `ratio` against `backdrop`, so every
    /// text colour mode is drawn white there.
    static func forcesWhite(_ backdrop: Double, ratio: Double = LyricInk.minimumContrast) -> Bool {
        ratio * (min(1, max(0, backdrop)) + 0.05) - 0.05 >= 1
    }

    /// The wallpaper and its scrim, before the text backing.
    static func beforeBacking(mode: Settings.BackgroundMode, motion: Bool, palette: [SIMD3<Float>],
                              scrim: Double, measured: Double? = nil, tinted: Bool = false) -> Double {
        // Called every frame: a plain loop, no intermediate arrays.
        var brightest = 0.0, total = 0.0
        for stop in palette {
            let value = Perceptual.luminance(sRGB: SIMD3(Double(stop.x), Double(stop.y), Double(stop.z)))
            brightest = max(brightest, value)
            total += value
        }
        if palette.isEmpty { brightest = 0.1; total = 0.1 }
        let average = total / Double(max(1, palette.count))
        // The blobs are blurred into one another, so the field is mostly
        // their average -- with a margin for passing over the brightest.
        let field = 0.35 * brightest + 0.65 * average
        let strength = min(2, max(0, scrim))
        switch mode {
        case .desktop:
            // Nothing is laid over the desktop picture but the backing.
            return min(1, max(0, measured ?? LyricLegibility.unknownBackdropLuminance))
        case .custom:
            // The album's colours at 18% over the picture, as the wallpaper
            // lays them (a dark photo under a pale cover's tint is not as
            // dark as the photo); then the scrim, lightest where the lyrics
            // sit, as the wallpaper's own legibility estimate takes it.
            var picture = min(1, max(0, measured ?? LyricLegibility.unknownBackdropLuminance))
            if tinted, !palette.isEmpty {
                picture = LyricLegibility.linear(0.82 * LyricLegibility.encoded(picture)
                    + 0.18 * LyricLegibility.encoded(min(1, field)))
            }
            return LyricLegibility.darkened(picture, by: min(1, 0.12 * strength))
        case .colourOnly where measured != nil:
            return LyricLegibility.darkened(measured ?? field, by: WallpaperShade.scrimAtStage(strength: strength))
        case .visualizer:
            return min(1, field * (1 - min(0.9, 0.25 * strength)) + 0.06)
        default:
            if motion {
                return field * (1 - min(0.9, 0.4 * strength)) * 0.6
            }
            return min(1, field * (1 - min(0.9, 0.25 * strength)) + 0.03)
        }
    }
}

// MARK: - Director

/// Builds each frame's ink. Every target colour goes through critically
/// damped followers with a hard rate limit on lightness, so a change of
/// song, mode or a burst of audio is a fade -- never a flash. Flashes are
/// what photosensitive viewers need avoided: pairs of opposing luminance
/// changes faster than three a second. A 3 Hz swing is attenuated about
/// fortyfold by the followers alone, and the rate limit caps any single
/// change well below a flash.
@MainActor
final class LyricInkDirector {
    struct Inputs {
        var mode: LyricColourMode
        var speed: Double = 1
        var strength: Double = 1
        var custom: [OKLab] = []
        /// Album colours, sRGB.
        var album: [SIMD3<Float>] = []
        var backdrop: Double = 0.08
        /// Live audio, when a visualiser is capturing it.
        var audio: VCAudioLevels?
        /// Timing-derived vocal intensity, used when there is no audio.
        var vocal: Double = 0
        var reduceMotion = false
        /// The system's Increase Contrast: 4.5:1 and brighter unsung words.
        var increaseContrast = false
        /// The album accent the layouts highlighted with before the
        /// highlight had a setting of its own; it breaks ties among the
        /// cover's colours for "Album accent".
        var accent: OKLab?
        /// Where highlight tiles take their colour from (`LyricHighlightOptions`).
        var highlight: LyricHighlightMode = .complement
        /// The user's own highlight colour, for `.custom`.
        var highlightCustom: OKLab?
    }
    typealias Stops = (OKLab, OKLab, OKLab, OKLab)

    /// Lightness may change by at most this much per second (OKLab L).
    static let lightnessRate = 0.5
    static let chromaRate = 0.6
    /// Every channel starts at white, so the first colour fades in.
    private var channels: [VCFollower] = (0..<12).map { VCFollower(value: $0 % 3 == 0 ? 1 : 0, velocity: 0, valid: 1) }
    private var backdrop = VCFollower()
    private var level = VCFollower(), balance = VCFollower()
    private var phase = 0.0
    private var lastWall: Double?
    private var last: LyricInk?
    private var albumKey: [SIMD3<Float>] = []
    private static let fallbackPastels: Stops = (
        OKLab(l: 0.84, chroma: 0.14, hue: 1.25), OKLab(l: 0.84, chroma: 0.15, hue: 3.12),
        OKLab(l: 0.80, chroma: 0.16, hue: 4.85), OKLab(l: 0.82, chroma: 0.16, hue: 5.82))
    private var albumStops: Stops = LyricInkDirector.fallbackPastels
    private var albumCount = 4
    private var vivid = LyricInkDirector.fallbackPastels.2
    /// The cover's colours in OKLab, for "Album accent".
    private var albumColours: [OKLab] = []
    private var backgroundHue: Double?
    /// Static white's ink, worked out again only when what it depends on changes.
    private struct WhiteKey: Equatable {
        var tile: OKLab
        var backdrop: Double
        var increaseContrast: Bool
    }
    private var whiteKey: WhiteKey?
    private var whiteInk = LyricInk.white
    /// The highlight tile eases like every other colour. It starts unset,
    /// so the first tile is simply there.
    private var highlightChannels = [VCFollower](repeating: VCFollower(), count: 3)
    /// What the highlight goal was worked out from, quantised, and the goal.
    private struct HighlightKey: Equatable {
        var mode: LyricHighlightMode
        var basis: [Int]
        var stops: [Int]
        var album: [SIMD3<Float>]
        var accent: OKLab?
        var custom: OKLab?
        var backdrop: Int
        var ratio: Double
    }
    private var highlightKey: HighlightKey?
    /// The tile the highlight is heading for; the drawn tile eases to it.
    private(set) var goalTile = OKLab(l: 0.8, chroma: 0.14, hue: 0.4)

    func ink(_ inputs: Inputs, wall: Double) -> LyricInk {
        // The system clock stepped back: carry on from the new time rather
        // than hold the colours until the old one comes round again.
        if let previous = lastWall, wall < previous - 1 { lastWall = nil }
        let dt = lastWall.map { min(0.25, max(0, wall - $0)) } ?? 0
        if lastWall != nil, dt == 0, let last { return last }
        lastWall = wall
        refreshAlbum(inputs.album)
        let calm = inputs.reduceMotion
        let ratio = inputs.increaseContrast ? LyricInk.increasedContrast : LyricInk.minimumContrast
        let textRatio = inputs.increaseContrast ? LyricHighlight.increasedTextContrast : LyricHighlight.textContrast
        if inputs.mode == .standard, atWhite {
            // The type is white, but the highlights have colour: white has
            // no hue to complement, so they take the album accent, or the
            // user's own colour.
            let backdrop = min(1, max(0, inputs.backdrop))
            let goal = highlightGoal(inputs, basis: nil, stops: [], backdrop: backdrop, ratio: textRatio)
            let tile = followHighlight(goal, dt: dt, calm: calm)
            let key = WhiteKey(tile: tile, backdrop: inputs.backdrop, increaseContrast: inputs.increaseContrast)
            if key != whiteKey {
                var white = LyricInk.white(increasedContrast: inputs.increaseContrast)
                white.backdrop = backdrop
                Self.highlight(&white, tile: tile, backdrop: backdrop, textRatio: textRatio, ratio: white.contrast,
                               lyric: 1)
                whiteKey = key
                whiteInk = white
            }
            var ink = whiteInk
            ink.settled = highlightSettled(on: goal)
            last = ink
            return ink
        }
        // Drift: about 45 s per loop at normal speed, ten times slower with
        // Reduce Motion. Quantised below, so a settled word can reuse its
        // glyphs between steps too small to see.
        if inputs.mode.drifts {
            phase = (phase + dt * min(2, max(0.25, inputs.speed)) * 0.022 * (calm ? 0.1 : 1))
                .truncatingRemainder(dividingBy: 1)
        }
        let targets = targetStops(inputs, dt: dt)
        let strength = LyricColourOptions.clampedStrength(inputs.strength)
        let goals = (LyricInk.applyingStrength(strength, to: targets.stops.0),
                     LyricInk.applyingStrength(strength, to: targets.stops.1),
                     LyricInk.applyingStrength(strength, to: targets.stops.2),
                     LyricInk.applyingStrength(strength, to: targets.stops.3))
        let lTau = calm ? 0.7 : 0.35, abTau = calm ? 0.5 : 0.25
        func follow(_ index: Int, _ target: OKLab) -> OKLab {
            let l = vc_follow(&channels[index * 3], target.l, dt, lTau, Self.lightnessRate)
            let a = vc_follow(&channels[index * 3 + 1], target.a, dt, abTau, Self.chromaRate)
            let b = vc_follow(&channels[index * 3 + 2], target.b, dt, abTau, Self.chromaRate)
            return OKLab(l: Self.quantised(l), a: Self.quantised(a), b: Self.quantised(b))
        }
        let followed: Stops = (follow(0, goals.0), follow(1, goals.1), follow(2, goals.2), follow(3, goals.3))
        let behindTarget = min(1, max(0, inputs.backdrop))
        let behind = Self.quantised(vc_follow(&backdrop, behindTarget, dt, 0.5, 0.6))
        var ink = LyricInk()
        ink.mode = inputs.mode
        ink.settled = settled(on: goals) && Self.settled(backdrop, on: behindTarget)
        ink.backdrop = behind
        ink.stops = followed
        ink.stopCount = targets.count
        ink.phase = (phase * 720).rounded() / 720
        ink.contrast = ratio
        ink.dim = inputs.increaseContrast ? LyricInk.increasedUnsungDim : LyricInk.unsungDim
        switch inputs.mode {
        case .standard:
            // Still easing back to white after another mode.
            ink.mode = .albumDrift
            ink.stopCount = 1
            ink.base = LyricInk.sample(Perceptual.readable(followed.0, background: behind, ratio: ratio))
        case .sungSweep:
            let sung = Perceptual.readable(followed.1, background: behind, ratio: ratio)
            ink.base = LyricInk.sample(Perceptual.readable(followed.0, background: behind, ratio: ratio), sung: sung)
            let fade = Easing.smoothstep((sung.l - 0.91) / 0.07)
                * Easing.smoothstep((0.065 - sung.chroma) / 0.045)
                * Easing.smoothstep((behind - 0.14) / 0.10) * min(1, strength)
            if fade > 0.001 {
                let marker = OKLab(l: 0.33, chroma: 0.12, hue: goals.1.hue)
                ink.base.sweepUnderline = Perceptual.color(marker, opacity: fade)
            }
        default:
            ink.base = LyricInk.sample(Perceptual.readable(ink.gradient(slot: 0.5), background: behind, ratio: ratio))
        }
        ink.base.dim = ink.dim
        let highlight = Perceptual.readable(inputs.mode == .sungSweep ? followed.1 : ink.gradient(slot: 0.5),
                                            background: behind, ratio: ratio)
        ink.dot = Perceptual.color(highlight)
        ink.link = Perceptual.color(Perceptual.mix(highlight, .white, 0.5))
        // The highlight complements the lyric colour as it is now (easing
        // back to Static white, the one it will settle on): the sung colour
        // for Sung sweep, the middle of the line otherwise, its hue carried
        // round the wheel rather than through grey.
        let basis: OKLab? = switch inputs.mode {
        case .standard: nil
        case .sungSweep: followed.1
        default: ink.gradient(slot: 0.5)
        }
        let stops = [followed.0, followed.1, followed.2, followed.3].prefix(max(1, min(4, targets.count)))
        let goal = highlightGoal(inputs, basis: basis, stops: Array(stops), backdrop: behind, ratio: textRatio)
        let tile = followHighlight(goal, dt: dt, calm: calm)
        Self.highlight(&ink, tile: tile, backdrop: behind, textRatio: textRatio, ratio: ratio,
                       lyric: Perceptual.luminance(highlight))
        ink.settled = ink.settled && highlightSettled(on: goal)
        last = ink
        return ink
    }

    /// The highlight from its tile: the text on it, the held words' type,
    /// and where a fading tile's text turns back to `lyric` (a luminance).
    private static func highlight(_ ink: inout LyricInk, tile: OKLab, backdrop: Double, textRatio: Double,
                                  ratio: Double, lyric: Double) {
        let tone = LyricHighlight.tone(tile, backdrop: backdrop, textRatio: textRatio, lyricRatio: ratio)
        ink.highlight = tone
        ink.tile = Perceptual.color(tone.tile)
        ink.onTile = Perceptual.color(tone.text)
        ink.accent = Perceptual.color(tone.held)
        ink.tileTextSwitch = LyricHighlight.textSwitch(tile: tone.tile, text: tone.text, backdrop: backdrop, lyric: lyric)
    }

    /// The tile the highlight is heading for. Worked out again only when
    /// what it depends on changes, to within steps too small to see: a
    /// drifting lyric colour moves it every frame.
    private func highlightGoal(_ inputs: Inputs, basis: OKLab?, stops: [OKLab], backdrop: Double,
                               ratio: Double) -> OKLab {
        func hue(_ colour: OKLab) -> [Int] {
            colour.chroma < 0.02 ? [0, 0, Int((colour.l * 50).rounded())]
                : [Int((LyricHighlight.degrees(colour.hue) / 3).rounded()) % 120, Int((colour.chroma * 100).rounded()),
                   Int((colour.l * 50).rounded())]
        }
        let complements = inputs.highlight == .complement
        let key = HighlightKey(mode: inputs.highlight, basis: complements ? basis.map(hue) ?? [] : [],
                               stops: complements ? stops.flatMap(hue) : [],
                               album: inputs.highlight == .custom ? [] : albumKey,
                               accent: inputs.highlight == .custom ? nil : inputs.accent,
                               custom: inputs.highlight == .custom ? inputs.highlightCustom : nil,
                               backdrop: Int((backdrop * 256).rounded()), ratio: ratio)
        if key == highlightKey { return goalTile }
        // Worked out from the quantised values, so equal keys give equal tiles.
        func restored(_ values: [Int]) -> OKLab? {
            guard values.count == 3 else { return nil }
            return OKLab(l: Double(values[2]) / 50, chroma: Double(values[1]) / 100,
                         hue: Double(values[0]) * 3 * .pi / 180)
        }
        let shade = Double(key.backdrop) / 256
        let album = {
            LyricHighlight.album(self.albumColours, accent: inputs.accent, backgroundHue: self.backgroundHue,
                                 backdrop: shade, ratio: ratio)
        }
        switch inputs.highlight {
        case .custom:
            goalTile = LyricHighlight.custom(inputs.highlightCustom ?? LyricHighlight.coral, backdrop: shade, ratio: ratio)
        case .album:
            goalTile = album()
        case .complement:
            let others = stride(from: 0, to: key.stops.count, by: 3).compactMap { restored(Array(key.stops[$0..<$0 + 3])) }
            let previous = highlightKey?.mode == .complement ? goalTile : nil
            goalTile = restored(key.basis).flatMap {
                LyricHighlight.complement(of: $0, stops: others, previous: previous, backgroundHue: backgroundHue,
                                          backdrop: shade, ratio: ratio)
            } ?? album()
        }
        highlightKey = key
        return goalTile
    }

    /// Quicker than the text colours: a tile changing hue changes little
    /// in lightness, which stays rate limited, and should not linger part
    /// way between two clean colours.
    private func followHighlight(_ goal: OKLab, dt: Double, calm: Bool) -> OKLab {
        let lTau = calm ? 0.7 : 0.25, abTau = calm ? 0.5 : 0.12
        let l = vc_follow(&highlightChannels[0], goal.l, dt, lTau, Self.lightnessRate)
        let a = vc_follow(&highlightChannels[1], goal.a, dt, abTau, Self.chromaRate)
        let b = vc_follow(&highlightChannels[2], goal.b, dt, abTau, Self.chromaRate)
        return OKLab(l: Self.quantised(l), a: Self.quantised(a), b: Self.quantised(b))
    }

    private func highlightSettled(on goal: OKLab) -> Bool {
        Self.settled(highlightChannels[0], on: goal.l) && Self.settled(highlightChannels[1], on: goal.a)
            && Self.settled(highlightChannels[2], on: goal.b)
    }

    private var atWhite: Bool {
        for index in 0..<4 {
            if abs(channels[index * 3].value - 1) > 1e-4 || abs(channels[index * 3 + 1].value) > 1e-4
                || abs(channels[index * 3 + 2].value) > 1e-4 { return false }
        }
        return true
    }

    /// Whether every colour channel has arrived, to within a step too small
    /// to see (finer than the quantisation the ink is drawn at).
    private func settled(on targets: Stops) -> Bool {
        let stops = [targets.0, targets.1, targets.2, targets.3]
        for (index, stop) in stops.enumerated() {
            if !Self.settled(channels[index * 3], on: stop.l) || !Self.settled(channels[index * 3 + 1], on: stop.a)
                || !Self.settled(channels[index * 3 + 2], on: stop.b) { return false }
        }
        return true
    }

    private static func settled(_ follower: VCFollower, on target: Double) -> Bool {
        abs(follower.value - target) < 1.0 / 1024 && abs(follower.velocity) < 1e-3
    }

    private static func quantised(_ value: Double) -> Double { (value * 512).rounded() / 512 }

    /// Cover colours, prepared once per palette rather than every frame.
    private func refreshAlbum(_ album: [SIMD3<Float>]) {
        guard album != albumKey else { return }
        albumKey = album
        let colours = album.map { Perceptual.oklab(sRGB: SIMD3(Double($0.x), Double($0.y), Double($0.z))) }
        albumColours = colours
        let harmony = AlbumColourHarmony(colours)
        backgroundHue = harmony.backgroundHue
        guard let backgroundHue, let accentHue = harmony.accentHue(turn: .pi * 0.86) else {
            vivid = Self.fallbackPastels.2
            albumStops = Self.fallbackPastels
            albumCount = 4
            return
        }
        vivid = OKLab(l: 0.82, chroma: 0.16, hue: accentHue)
        // Keep distinct cover hues, including the most vivid one wherever
        // it landed in the background gradient.
        var picks: [OKLab] = []
        for colour in colours.sorted(by: { $0.chroma > $1.chroma }) where colour.chroma >= 0.035 {
            let matches = picks.contains { pick in
                let difference = abs(pick.hue - colour.hue)
                return min(difference, 2 * .pi - difference) < 0.28
            }
            if !matches { picks.append(colour) }
            if picks.count == 4 { break }
        }
        var lyric = picks.map { LyricInk.pastel($0) }
        if let tint = lyric.first, lyric.count == 1 {
            // Keep the cover's own light and deep shades, with one colour
            // that stands out against both.
            lyric = [
                OKLab(l: min(0.91, tint.l + 0.07), chroma: tint.chroma * 0.65, hue: tint.hue),
                OKLab(l: max(0.70, tint.l - 0.05), chroma: tint.chroma * 1.18, hue: tint.hue),
                vivid,
            ]
        } else if lyric.allSatisfy({ LyricHighlight.hueDistance($0.hue, backgroundHue) < .pi * 0.42 }) {
            if lyric.count == 4 { lyric.removeLast() }
            lyric.append(vivid)
        }
        lyric.sort { $0.hue < $1.hue }
        albumStops = Self.stops(lyric)
        albumCount = max(1, min(4, lyric.count))
    }

    private static func stops(_ colours: [OKLab]) -> Stops {
        func at(_ index: Int) -> OKLab { colours.isEmpty ? .white : colours[index % colours.count] }
        return (at(0), at(1), at(2), at(3))
    }

    /// The colours each mode is heading for this frame.
    private func targetStops(_ inputs: Inputs, dt: Double) -> (stops: Stops, count: Int) {
        switch inputs.mode {
        case .standard:
            return ((.white, .white, .white, .white), 1)
        case .albumDrift:
            return (albumStops, albumCount)
        case .sungSweep:
            let sung = LyricInk.applyingStrength(1.35, to: LyricInk.pastel(vivid))
            return ((.white, sung, .white, sung), 2)
        case .musicReactive:
            let calm = inputs.reduceMotion
            let loud = inputs.audio.map { min(1, $0.rms * 2.4 + $0.bass * 0.6) } ?? inputs.vocal
            let tilt = inputs.audio.map { min(1, max(-1, ($0.treble - $0.bass) * 2.5)) } ?? 0
            let intensity = vc_follow(&level, calm ? 0.3 : min(1, max(0, loud)), dt, 0.3, 1.5)
            let shift = vc_follow(&balance, calm ? 0 : tilt, dt, 0.4, 1.5)
            // Bass pulls warm and treble cool, around the cover's own hue.
            let hue = vivid.hue - shift * 1.1
            let chroma = 0.12 + 0.09 * intensity, lightness = 0.77 + 0.09 * intensity
            let first = OKLab(l: lightness, chroma: chroma, hue: hue)
            let second = OKLab(l: lightness, chroma: chroma * 0.9, hue: hue + 0.75)
            return ((first, second, first, second), 2)
        case .customGradient:
            guard !inputs.custom.isEmpty else { return (albumStops, albumCount) }
            let count = min(4, inputs.custom.count)
            func at(_ index: Int) -> OKLab { LyricInk.pastel(inputs.custom[index % count]) }
            return ((at(0), at(1), at(2), at(3)), count)
        }
    }
}
